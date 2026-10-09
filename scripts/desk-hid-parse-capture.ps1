# Read-only Windows HID semantic parsing of an already saved Raw Input capture.
# No CreateFile, serial, HID output, input injection, device registration or settings.
# Gets the current Feather Consumer TLC's RIDI_PREPARSEDDATA and calls HidP_GetUsages.
# https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getusages
# https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getrawinputdeviceinfow
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CapturePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($CapturePath)) { $CapturePath = Join-Path $projectRoot $CapturePath }
if (-not [IO.Path]::IsPathRooted($OutputPath)) { $OutputPath = Join-Path $projectRoot $OutputPath }
$CapturePath = [IO.Path]::GetFullPath($CapturePath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw "Output exists: $OutputPath" }
$capture = Get-Content -Raw -LiteralPath $CapturePath | ConvertFrom-Json
if ($capture.schema -ne 'desk-raw-hid-observation/1' -or @($capture.reports).Count -eq 0) {
    throw 'Expected a nonempty desk-raw-hid-observation/1 capture.'
}
$sourcePaths = @($capture.reports.deviceName | Sort-Object -Unique)
if ($sourcePaths.Count -ne 1) { throw 'Capture must contain exactly one device path.' }

if (-not ('DeskHidParseV1.Parser' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskHidParseV1 {
    public sealed class Caps {
        public ushort usage, usagePage, inputReportByteLength, outputReportByteLength,
            featureReportByteLength, linkCollectionNodes, inputButtonCaps,
            inputValueCaps, inputDataIndices;
        public uint maxConsumerUsageListLength;
    }
    public sealed class Parsed {
        public string sourceHex, parserHex, ntStatus;
        public int rawLength, parserLength;
        public bool zeroPaddedToCapsLength, success;
        public ushort usagePage = 0x000C;
        public ushort[] usages;
        public string[] usageHex;
    }
    public sealed class Parser : IDisposable {
        private const uint Failed = 0xFFFFFFFF, Success = 0x00110000;
        private const uint NameCommand = 0x20000007, PreparsedCommand = 0x20000005;
        private IntPtr preparsed = IntPtr.Zero;
        public string deviceName, preparsedSha256;
        public uint preparsedBytes;
        public Caps caps;
        [StructLayout(LayoutKind.Sequential)]
        private struct Device { public IntPtr handle; public uint type; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList(IntPtr list, ref uint count, uint size);
        [DllImport("user32.dll", EntryPoint="GetRawInputDeviceInfoW", SetLastError=true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
        [DllImport("user32.dll", EntryPoint="GetRawInputDeviceInfoW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetRawInputDeviceName(IntPtr device, uint command, StringBuilder data, ref uint size);
        [DllImport("hid.dll")]
        private static extern uint HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);
        [DllImport("hid.dll")]
        private static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsedData);
        [DllImport("hid.dll")]
        private static extern uint HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
            [Out] ushort[] usages, ref uint usageCount, IntPtr preparsedData, [In] byte[] report, uint reportLength);

        private static void CheckWin(uint result, string operation) {
            if (result == Failed) throw new InvalidOperationException(operation + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        private static string Name(IntPtr handle) {
            uint size = 0;
            CheckWin(GetRawInputDeviceInfo(handle, NameCommand, IntPtr.Zero, ref size), "Device name size");
            if (size == 0 || size > 32768) throw new InvalidOperationException("Invalid device name size.");
            StringBuilder name = new StringBuilder((int)size + 1);
            CheckWin(GetRawInputDeviceName(handle, NameCommand, name, ref size), "Device name");
            return name.ToString();
        }
        public Parser(string expectedDevicePath) {
            uint count = 0, deviceSize = (uint)Marshal.SizeOf(typeof(Device));
            CheckWin(GetRawInputDeviceList(IntPtr.Zero, ref count, deviceSize), "Device list size");
            if (count == 0 || count > 4096) throw new InvalidOperationException("Unexpected raw-device count.");
            IntPtr list = Marshal.AllocHGlobal(checked((int)(count * deviceSize)));
            try {
                uint actual = GetRawInputDeviceList(list, ref count, deviceSize);
                CheckWin(actual, "Device list snapshot");
                IntPtr match = IntPtr.Zero;
                for (uint i = 0; i < actual; ++i) {
                    Device item = (Device)Marshal.PtrToStructure(IntPtr.Add(list, checked((int)(i * deviceSize))), typeof(Device));
                    if (item.type != 2) continue; // RIM_TYPEHID only.
                    string name = Name(item.handle);
                    if (!Regex.IsMatch(name, @"(?:^|[#&\\])VID_239A&PID_8029(?:[&#\\]|$)", RegexOptions.IgnoreCase)) continue;
                    if (!String.Equals(name, expectedDevicePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (match != IntPtr.Zero) throw new InvalidOperationException("Multiple matching Feather collections.");
                    match = item.handle; deviceName = name;
                }
                if (match == IntPtr.Zero) throw new InvalidOperationException("Captured Feather collection is not currently present; retry after enumeration.");
                uint size = 0;
                CheckWin(GetRawInputDeviceInfo(match, PreparsedCommand, IntPtr.Zero, ref size), "Preparsed size");
                if (size == 0 || size > 1048576) throw new InvalidOperationException("Invalid preparsed size.");
                preparsed = Marshal.AllocHGlobal((int)size);
                uint received = GetRawInputDeviceInfo(match, PreparsedCommand, preparsed, ref size);
                CheckWin(received, "Preparsed data");
                if (received != size) throw new InvalidOperationException("Preparsed length changed during snapshot.");
                preparsedBytes = size;
                byte[] data = new byte[size]; Marshal.Copy(preparsed, data, 0, data.Length);
                using (SHA256 sha = SHA256.Create()) { preparsedSha256 = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
                // HIDP_CAPS is 32 consecutive USHORTs (64 bytes), per hidpi.h.
                IntPtr capBuffer = Marshal.AllocHGlobal(64);
                try {
                    uint status = HidP_GetCaps(preparsed, capBuffer);
                    if (status != Success) throw new InvalidOperationException("HidP_GetCaps: 0x" + status.ToString("X8"));
                    caps = new Caps {
                        usage = unchecked((ushort)Marshal.ReadInt16(capBuffer, 0)),
                        usagePage = unchecked((ushort)Marshal.ReadInt16(capBuffer, 2)),
                        inputReportByteLength = unchecked((ushort)Marshal.ReadInt16(capBuffer, 4)),
                        outputReportByteLength = unchecked((ushort)Marshal.ReadInt16(capBuffer, 6)),
                        featureReportByteLength = unchecked((ushort)Marshal.ReadInt16(capBuffer, 8)),
                        linkCollectionNodes = unchecked((ushort)Marshal.ReadInt16(capBuffer, 44)),
                        inputButtonCaps = unchecked((ushort)Marshal.ReadInt16(capBuffer, 46)),
                        inputValueCaps = unchecked((ushort)Marshal.ReadInt16(capBuffer, 48)),
                        inputDataIndices = unchecked((ushort)Marshal.ReadInt16(capBuffer, 50))
                    };
                } finally { Marshal.FreeHGlobal(capBuffer); }
                if (caps.usagePage != 0x0C || caps.usage != 1) throw new InvalidOperationException("Expected Consumer Control top-level collection.");
                if (caps.inputReportByteLength == 0 || caps.inputReportByteLength > 4096) throw new InvalidOperationException("Unexpected input report length.");
                caps.maxConsumerUsageListLength = HidP_MaxUsageListLength(0, 0x0C, preparsed);
                if (caps.maxConsumerUsageListLength == 0 || caps.maxConsumerUsageListLength > 65536) throw new InvalidOperationException("Unexpected usage list capacity.");
            } catch { Dispose(); throw; }
            finally { Marshal.FreeHGlobal(list); }
        }
        public Parsed Parse(byte[] raw, bool padToCapsLength) {
            if (preparsed == IntPtr.Zero) throw new ObjectDisposedException("Parser");
            if (raw == null || raw.Length == 0) throw new ArgumentException("Empty report.");
            int length = padToCapsLength ? caps.inputReportByteLength : raw.Length;
            if (padToCapsLength && raw.Length > length) throw new ArgumentException("Raw report exceeds HIDP_CAPS input length.");
            byte[] report = new byte[length]; Buffer.BlockCopy(raw, 0, report, 0, Math.Min(raw.Length, length));
            uint count = caps.maxConsumerUsageListLength;
            ushort[] usages = new ushort[count];
            uint status = HidP_GetUsages(0, 0x0C, 0, usages, ref count, preparsed, report, (uint)report.Length);
            if (count > usages.Length) throw new InvalidOperationException("Parser returned more usages than capacity.");
            ushort[] parsed = status == Success ? new ushort[count] : new ushort[0];
            if (status == Success) Array.Copy(usages, parsed, parsed.Length);
            string[] hex = new string[parsed.Length];
            for (int i = 0; i < parsed.Length; ++i) hex[i] = "0x" + parsed[i].ToString("X4");
            return new Parsed {
                sourceHex = BitConverter.ToString(raw).Replace('-', ' '), parserHex = BitConverter.ToString(report).Replace('-', ' '),
                ntStatus = "0x" + status.ToString("X8"), rawLength = raw.Length, parserLength = report.Length,
                zeroPaddedToCapsLength = report.Length > raw.Length, success = status == Success, usages = parsed, usageHex = hex
            };
        }
        public void Dispose() { if (preparsed != IntPtr.Zero) { Marshal.FreeHGlobal(preparsed); preparsed = IntPtr.Zero; } }
    }
}
'@
}

$parser = $null
$result = [ordered]@{
    schema = 'desk-windows-hid-semantic-parse/1'
    timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    readOnly = $true
    sourceCapture = $CapturePath
    sourceSha256 = (Get-FileHash -LiteralPath $CapturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    currentDevicePath = $null
    preparsedSha256 = $null
    preparsedBytes = $null
    caps = $null
    records = @()
    negativeControls = @()
    allCapturedReportsMatchExpectedConsumerUsage = $false
    errors = @()
    evidenceBoundary = 'Current Windows Consumer TLC parser applied offline to previously captured Feather reports. No live inputs sent or observed. Does not prove Windows shell/app/audio execution.'
}
try {
    $parser = [DeskHidParseV1.Parser]::new([string]$sourcePaths[0])
    $result.currentDevicePath = $parser.deviceName
    $result.preparsedSha256 = $parser.preparsedSha256
    $result.preparsedBytes = $parser.preparsedBytes
    $result.caps = $parser.caps
    $allMatch = $true
    for ($i = 0; $i -lt $capture.reports.Count; ++$i) {
        $record = $capture.reports[$i]
        [byte[]]$raw = $record.bytes
        $parsed = $parser.Parse($raw, $true)
        $expected = $null
        # All consumer usages emitted by DeskController: release, play/pause,
        # previous, next, volume up/down and mute. Sequence validation against
        # the requested physical actions is a separate evidence check.
        if ($raw.Length -eq 3 -and $raw[0] -eq 2 -and $raw[2] -eq 0 -and $raw[1] -in @(0, 0xCD, 0xB6, 0xB5, 0xE9, 0xEA, 0xE2)) {
            $expected = [int]$raw[1]
        }
        $matches = $parsed.success -and $null -ne $expected
        if ($matches) {
            if ($expected -eq 0) { $matches = $parsed.usages.Length -eq 0 }
            else { $matches = $parsed.usages.Length -eq 1 -and $parsed.usages[0] -eq $expected }
        }
        if (-not $matches) { $allMatch = $false }
        $result.records += [ordered]@{ index = $i; timestampUtc = $record.timestampUtc; parsed = $parsed; expectedUsage = $expected; semanticMatch = $matches }
    }
    # Deliberately invalid, offline-only buffers verify meaningful parser rejection.
    $result.negativeControls += [ordered]@{ kind = 'wrong-report-id'; dataSource = 'synthetic-offline-only'; parsed = $parser.Parse([byte[]]@(1, 0xE9, 0), $true) }
    $result.negativeControls += [ordered]@{ kind = 'short-report'; dataSource = 'synthetic-offline-only'; parsed = $parser.Parse([byte[]]@(2, 0xE9), $false) }
    $result.allCapturedReportsMatchExpectedConsumerUsage = $allMatch
} catch { $result.errors += $_.Exception.Message }
finally { if ($null -ne $parser) { $parser.Dispose() } }
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))
[IO.File]::WriteAllText($OutputPath, ($result | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Host ("HID semantic parse: {0} captured reports; all match={1}. Evidence: {2}" -f $result.records.Count, $result.allCapturedReportsMatchExpectedConsumerUsage, $OutputPath)
if ($result.errors.Count -gt 0) { throw ($result.errors -join '; ') }
if (-not $result.allCapturedReportsMatchExpectedConsumerUsage) { throw 'One or more reports did not match the expected consumer usages; inspect evidence.' }
