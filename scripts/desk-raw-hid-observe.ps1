# Read-only Raw Input observation of the Feather consumer-control collection.
# Uses a hidden message-only window and RIDEV_INPUTSINK only (not NOLEGACY).
# No keyboard registration, input injection/suppression, serial, or settings writes.
# Run as a separate PowerShell process. Relative OutputPath is project-relative.
# Example: powershell.exe -NoProfile -File .\scripts\desk-raw-hid-observe.ps1 -Seconds 20 -OutputPath output/evidence/raw-hid-01.json
# API references:
# https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices
# https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getrawinputdata
[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int]$Seconds = 10,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Raw Input observation requires Windows.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path 'output/evidence' ('raw-hid-observe-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.json')
}
if (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $projectRoot $OutputPath
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    throw "Output already exists; choose a new evidence filename: $OutputPath"
}
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))

if (-not ('DeskRawHidObservationV1.Observer' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeskRawHidObservationV1 {
    public sealed class Report {
        public string timestampUtc;
        public double elapsedMilliseconds;
        public string deviceName;
        public uint reportSize;
        public uint batchCount;
        public uint batchIndex;
        public string hex;
        public int[] bytes;
    }
    public sealed class Result {
        public string schema = "desk-raw-hid-observation/1";
        public string startedUtc;
        public string endedUtc;
        public int requestedSeconds;
        public double elapsedMilliseconds;
        public ushort usagePage = 0x000C;
        public ushort usage = 0x0001;
        public string registrationFlags = "RIDEV_INPUTSINK (0x00000100)";
        public string deviceFilter = "VID_239A&PID_8029";
        public string window = "hidden HWND_MESSAGE; never activated";
        public bool registrationSucceeded;
        public bool registrationRemoved;
        public uint targetMessages;
        public bool truncated;
        public List<Report> reports = new List<Report>();
        public List<string> errors = new List<string>();
        public string interpretation = "Raw bytes received by this observer only. No reports does not prove device failure; no assertion about Windows media-action processing.";
    }

    public sealed class Observer {
        private const uint WM_INPUT = 0x00FF;
        private const uint RID_HEADER = 0x10000005;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIM_TYPEHID = 2;
        private const uint RIDEV_INPUTSINK = 0x100;
        private const uint RIDEV_REMOVE = 0x1;
        private const uint PM_REMOVE = 1;
        private const uint Failed = 0xFFFFFFFF;
        private const int MaxReports = 10000;
        private static readonly Regex Target = new Regex(@"(?:^|[#&\\])VID_239A&PID_8029(?:[&#\\]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [StructLayout(LayoutKind.Sequential)]
        private struct RawDevice { public ushort page, usage; public uint flags; public IntPtr target; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RawHeader { public uint type, size; public IntPtr device; public UIntPtr wParam; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Message { public IntPtr window; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public Point point; public uint reserved; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass {
            public uint size, style;
            public WindowProc proc;
            public int classExtra, windowExtra;
            public IntPtr instance, icon, cursor, background;
            public string menuName, className;
            public IntPtr smallIcon;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices([In] RawDevice[] devices, uint number, uint size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRegisteredRawInputDevices([Out] RawDevice[] devices, ref uint number, uint size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetRawInputDeviceName(IntPtr device, uint command, StringBuilder data, ref uint size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string className, IntPtr instance);
        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string module);

        private readonly uint headerSize = (uint)Marshal.SizeOf(typeof(RawHeader));
        private readonly uint deviceSize = (uint)Marshal.SizeOf(typeof(RawDevice));
        private Result result;
        private Stopwatch clock;
        private WindowProc callback;
        private bool callbackFailed;

        private static string NativeError(string operation) {
            return operation + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        private string TargetDeviceName(IntPtr device) {
            uint characters = 0;
            if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref characters) == Failed || characters == 0 || characters > 32768) return null;
            StringBuilder buffer = new StringBuilder((int)characters + 1);
            if (GetRawInputDeviceName(device, RIDI_DEVICENAME, buffer, ref characters) == Failed) return null;
            string name = buffer.ToString();
            // Do not retain names or read report bodies belonging to other devices.
            // Recheck each event: a removed/reconnected device may reuse a handle.
            if (!Target.IsMatch(name)) name = null;
            return name;
        }
        private void ReadReport(IntPtr rawInput) {
            IntPtr headerBuffer = Marshal.AllocHGlobal((int)headerSize);
            RawHeader header;
            try {
                uint length = headerSize;
                uint received = GetRawInputData(rawInput, RID_HEADER, headerBuffer, ref length, headerSize);
                if (received == Failed || received != headerSize) throw new InvalidOperationException(NativeError("GetRawInputData(header)"));
                header = (RawHeader)Marshal.PtrToStructure(headerBuffer, typeof(RawHeader));
            } finally { Marshal.FreeHGlobal(headerBuffer); }
            if (header.type != RIM_TYPEHID) return;
            string name = TargetDeviceName(header.device);
            if (name == null) return;
            ++result.targetMessages;
            uint size = 0;
            if (GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) == Failed) throw new InvalidOperationException(NativeError("GetRawInputData(size)"));
            if (size < headerSize + 8 || size > 1048576) throw new InvalidOperationException("Unexpected target RAWINPUT size.");
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try {
                uint received = GetRawInputData(rawInput, RID_INPUT, buffer, ref size, headerSize);
                if (received == Failed || received != size) throw new InvalidOperationException(NativeError("GetRawInputData(body)"));
                uint reportSize = unchecked((uint)Marshal.ReadInt32(buffer, (int)headerSize));
                uint count = unchecked((uint)Marshal.ReadInt32(buffer, (int)headerSize + 4));
                if (reportSize == 0 || count == 0 || (ulong)reportSize * count > size - headerSize - 8) throw new InvalidOperationException("Malformed target RAWHID bounds.");
                string timestamp = DateTimeOffset.UtcNow.ToString("o");
                double elapsed = clock.Elapsed.TotalMilliseconds;
                for (uint index = 0; index < count; ++index) {
                    if (result.reports.Count >= MaxReports) { result.truncated = true; break; }
                    byte[] bytes = new byte[(int)reportSize];
                    IntPtr reportStart = IntPtr.Add(buffer, checked((int)(headerSize + 8 + index * reportSize)));
                    Marshal.Copy(reportStart, bytes, 0, bytes.Length);
                    int[] numeric = new int[bytes.Length];
                    for (int b = 0; b < bytes.Length; ++b) numeric[b] = bytes[b];
                    result.reports.Add(new Report { timestampUtc = timestamp, elapsedMilliseconds = elapsed, deviceName = name, reportSize = reportSize, batchCount = count, batchIndex = index, hex = BitConverter.ToString(bytes).Replace('-', ' '), bytes = numeric });
                }
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        private IntPtr OnWindowMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam) {
            if (message == WM_INPUT && !callbackFailed) {
                try { ReadReport(lParam); }
                catch (Exception ex) { result.errors.Add(ex.Message); callbackFailed = true; }
            }
            // Preserve the required default processing, including WM_INPUT cleanup.
            return DefWindowProc(window, message, wParam, lParam);
        }
        private void RejectExistingConsumerRegistration() {
            uint count = 0;
            if (GetRegisteredRawInputDevices(null, ref count, deviceSize) == Failed) throw new InvalidOperationException(NativeError("GetRegisteredRawInputDevices(size)"));
            if (count == 0) return;
            RawDevice[] existing = new RawDevice[count];
            uint received = GetRegisteredRawInputDevices(existing, ref count, deviceSize);
            if (received == Failed) throw new InvalidOperationException(NativeError("GetRegisteredRawInputDevices"));
            foreach (RawDevice item in existing) {
                if (item.page == 0x000C && (item.usage == 1 || item.usage == 0)) throw new InvalidOperationException("This process already registered consumer Raw Input. Run the script in a separate PowerShell process.");
            }
        }
        public Result Capture(int seconds) {
            if (seconds < 1 || seconds > 120) throw new ArgumentOutOfRangeException("seconds");
            result = new Result { requestedSeconds = seconds };
            clock = new Stopwatch();
            callback = OnWindowMessage;
            string className = "DeskRawHid_" + Guid.NewGuid().ToString("N");
            IntPtr instance = GetModuleHandle(null), window = IntPtr.Zero;
            bool classRegistered = false;
            try {
                RejectExistingConsumerRegistration();
                WindowClass wc = new WindowClass { size = (uint)Marshal.SizeOf(typeof(WindowClass)), proc = callback, instance = instance, className = className };
                if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException(NativeError("RegisterClassEx"));
                classRegistered = true;
                window = CreateWindowEx(0, className, "Desk Raw HID observer", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
                if (window == IntPtr.Zero) throw new InvalidOperationException(NativeError("CreateWindowEx(HWND_MESSAGE)"));
                RawDevice[] devices = { new RawDevice { page = 0x000C, usage = 0x0001, flags = RIDEV_INPUTSINK, target = window } };
                if (!RegisterRawInputDevices(devices, 1, deviceSize)) throw new InvalidOperationException(NativeError("RegisterRawInputDevices"));
                result.registrationSucceeded = true;
                result.startedUtc = DateTimeOffset.UtcNow.ToString("o");
                clock.Start();
                while (clock.Elapsed.TotalSeconds < seconds && !callbackFailed && !result.truncated) {
                    Message message;
                    while (PeekMessage(out message, window, 0, 0, PM_REMOVE)) {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                        if (callbackFailed || result.truncated || clock.Elapsed.TotalSeconds >= seconds) break;
                    }
                    if (!callbackFailed && !result.truncated) Thread.Sleep(5);
                }
            } catch (Exception ex) { result.errors.Add(ex.Message); }
            finally {
                clock.Stop();
                result.endedUtc = DateTimeOffset.UtcNow.ToString("o");
                result.elapsedMilliseconds = clock.Elapsed.TotalMilliseconds;
                if (result.registrationSucceeded) {
                    RawDevice[] remove = { new RawDevice { page = 0x000C, usage = 0x0001, flags = RIDEV_REMOVE, target = IntPtr.Zero } };
                    result.registrationRemoved = RegisterRawInputDevices(remove, 1, deviceSize);
                    if (!result.registrationRemoved) result.errors.Add(NativeError("Remove Raw Input registration"));
                }
                if (window != IntPtr.Zero && !DestroyWindow(window)) result.errors.Add(NativeError("DestroyWindow"));
                if (classRegistered && !UnregisterClass(className, instance)) result.errors.Add(NativeError("UnregisterClass"));
                GC.KeepAlive(callback);
            }
            return result;
        }
    }
}
'@
}

$observer = [DeskRawHidObservationV1.Observer]::new()
$result = $observer.Capture($Seconds)
$json = $result | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
Write-Host ("Raw HID observation: {0} target reports, {1:N0} ms. Evidence: {2}" -f $result.reports.Count, $result.elapsedMilliseconds, $OutputPath)
if ($result.errors.Count -gt 0) {
    throw ("Raw HID observation reported errors; evidence retained: " + ($result.errors -join '; '))
}
