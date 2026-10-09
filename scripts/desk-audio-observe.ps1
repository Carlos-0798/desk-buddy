# Read-only CoreAudio evidence; never sets volume/mute, plays audio, or opens serial.
# Relative OutputPath values resolve against the project root, not the caller's CWD.
# Example: .\scripts\desk-audio-observe.ps1 -Seconds 10 -OutputPath output/evidence/audio-01.json
# Samples include t=0 and t=Seconds at a nominal 250 ms interval. Actual timing is recorded.
[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int]$Seconds = 10,

    [string]$OutputPath,

    # Compare eConsole/eMultimedia/eCommunications without changing defaults.
    [switch]$AllRoles,

    # All ACTIVE render endpoints, including virtual endpoints; implies all roles.
    [switch]$AllActiveEndpoints
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$reader = $null

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'CoreAudio observation requires Windows.'
    }
    $projectRoot = Split-Path -Parent $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = Join-Path 'output/evidence' ('audio-observe-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.json')
    }
    if (-not [IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath = Join-Path $projectRoot $OutputPath
    }
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $OutputPath) {
        throw "Output already exists; choose a new evidence filename: $OutputPath"
    }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))

    if (-not ('DeskAudioObservationV3.Reader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace DeskAudioObservationV3 {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class DeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceCollection {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters,
                                  [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    // Declarations retain the documented COM vtable order. Setter slots are
    // necessary ABI placeholders only: Reader invokes only the two getters.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEndpointVolume {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    public sealed class Reading {
        public string EndpointId { get; internal set; }
        public float? Volume { get; internal set; }
        public bool? Mute { get; internal set; }
        public string Error { get; internal set; }
    }

    public sealed class Reader : IDisposable {
        private IDeviceEnumerator enumerator;
        public Reader() {
            enumerator = (IDeviceEnumerator)(new DeviceEnumeratorCom());
        }
        public Reading Read() {
            return ReadRole(1);
        }
        public Reading ReadRole(int role) {
            if (role < 0 || role > 2) throw new ArgumentOutOfRangeException("role");
            if (enumerator == null) throw new ObjectDisposedException("Reader");
            IDevice device = null;
            try {
                // eRender=0, roles Console=0/Multimedia=1/Communications=2.
                // Re-query each sample so endpoint
                // changes are visible rather than silently observing the old one.
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, role, out device));
                return ReadDevice(device);
            } finally {
                if (device != null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            }
        }
        public string GetDefaultEndpointId(int role) {
            if (role < 0 || role > 2) throw new ArgumentOutOfRangeException("role");
            if (enumerator == null) throw new ObjectDisposedException("Reader");
            IDevice device = null;
            try {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, role, out device));
                string id;
                Marshal.ThrowExceptionForHR(device.GetId(out id));
                return id;
            } finally {
                if (device != null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            }
        }
        public Reading[] ReadActive() {
            if (enumerator == null) throw new ObjectDisposedException("Reader");
            IDeviceCollection devices = null;
            try {
                // eRender=0; DEVICE_STATE_ACTIVE=1. Re-enumerate every tick.
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out devices));
                uint count;
                Marshal.ThrowExceptionForHR(devices.GetCount(out count));
                Reading[] readings = new Reading[count];
                for (uint index = 0; index < count; index++) {
                    IDevice device = null;
                    try {
                        Marshal.ThrowExceptionForHR(devices.Item(index, out device));
                        readings[index] = ReadDevice(device);
                    } catch (Exception error) {
                        readings[index] = new Reading { Error = error.GetBaseException().Message };
                    } finally {
                        if (device != null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
                    }
                }
                return readings;
            } finally {
                if (devices != null && Marshal.IsComObject(devices)) Marshal.ReleaseComObject(devices);
            }
        }
        private Reading ReadDevice(IDevice device) {
            string id = null;
            object activated = null;
            try {
                Marshal.ThrowExceptionForHR(device.GetId(out id));
                Guid iid = typeof(IEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out activated));
                IEndpointVolume volume = (IEndpointVolume)activated;
                float scalar;
                bool muted;
                Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out scalar));
                Marshal.ThrowExceptionForHR(volume.GetMute(out muted));
                if (float.IsNaN(scalar) || scalar < 0 || scalar > 1)
                    throw new InvalidOperationException("CoreAudio returned an invalid volume scalar.");
                return new Reading { EndpointId = id, Volume = scalar, Mute = muted };
            } catch (Exception error) {
                // Preserve identity if an endpoint disconnects between enumeration
                // and activation; never turn an error into a zero-volume sample.
                return new Reading { EndpointId = id, Error = error.GetBaseException().Message };
            } finally {
                if (activated != null && Marshal.IsComObject(activated)) Marshal.ReleaseComObject(activated);
            }
        }
        public void Dispose() {
            if (enumerator != null) {
                Marshal.ReleaseComObject(enumerator);
                enumerator = null;
            }
        }
    }
}
'@
    }

    $reader = New-Object DeskAudioObservationV3.Reader
    $roles = if ($AllRoles) { @(0, 1, 2) } else { @(1) }
    $roleNames = @('eConsole', 'eMultimedia', 'eCommunications')
    $samples = New-Object 'System.Collections.Generic.List[object]'
    $roleSnapshots = New-Object 'System.Collections.Generic.List[object]'
    $inventoryErrors = New-Object 'System.Collections.Generic.List[string]'
    $endpointNames = @{}
    $services = @()
    $steelSeriesProcesses = @()
    if ($AllActiveEndpoints) {
        # PnP is only a friendly-name lookup. CoreAudio alone decides which
        # endpoints are ACTIVE; PresentOnly is not used as an activity filter.
        try {
            Get-PnpDevice -Class AudioEndpoint -PresentOnly | ForEach-Object {
                if ($_.InstanceId.StartsWith('SWD\MMDEVAPI\', [StringComparison]::OrdinalIgnoreCase)) {
                    $endpointNames[$_.InstanceId.Substring(13)] = $_.FriendlyName
                }
            }
        } catch { $inventoryErrors.Add('Endpoint names: ' + $_.Exception.GetBaseException().Message) }
        try {
            $services = @(Get-Service -Name AudioSrv, AudioEndpointBuilder, HidServ | ForEach-Object {
                [pscustomobject]@{ name=$_.Name; status=$_.Status.ToString(); startType=$_.StartType.ToString() }
            })
        } catch { $inventoryErrors.Add('Services: ' + $_.Exception.GetBaseException().Message) }
        try {
            $steelSeriesProcesses = @(Get-Process | Where-Object { $_.ProcessName -match '^(SteelSeries|Sonar)' } |
                ForEach-Object { [pscustomobject]@{ name=$_.ProcessName; pid=$_.Id } })
        } catch { $inventoryErrors.Add('SteelSeries processes: ' + $_.Exception.GetBaseException().Message) }
    }
    $startedUtc = [DateTimeOffset]::UtcNow
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $errorCount = 0
    for ($index = 0; $index -le ($Seconds * 4); $index++) {
        $scheduledMs = $index * 250
        $remainingMs = $scheduledMs - $clock.Elapsed.TotalMilliseconds
        if ($remainingMs -gt 0) {
            Start-Sleep -Milliseconds ([int][Math]::Ceiling($remainingMs))
        }
        if ($AllActiveEndpoints) {
            $bindings = @()
            foreach ($role in 0..2) {
                try {
                    $endpointId = $reader.GetDefaultEndpointId($role)
                    $bindings += [pscustomobject]@{ role=$roleNames[$role]; endpointId=$endpointId; error=$null }
                } catch {
                    $errorCount++
                    $bindings += [pscustomobject]@{ role=$roleNames[$role]; endpointId=$null; error=$_.Exception.GetBaseException().Message }
                }
            }
            $roleSnapshots.Add([pscustomobject]@{
                timestampUtc=[DateTimeOffset]::UtcNow.ToString('o')
                elapsedMs=[Math]::Round($clock.Elapsed.TotalMilliseconds, 3)
                scheduledMs=$scheduledMs
                roles=$bindings
            })
            $timestamp = [DateTimeOffset]::UtcNow.ToString('o')
            $elapsedMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 3)
            try {
                $readings = @($reader.ReadActive())
                if ($readings.Count -eq 0) { throw 'No ACTIVE render endpoints were returned by CoreAudio.' }
                foreach ($reading in $readings) {
                    if ($null -ne $reading.Error) { $errorCount++ }
                    $name = if ($null -ne $reading.EndpointId) { $endpointNames[$reading.EndpointId] } else { $null }
                    $samples.Add([pscustomobject][ordered]@{
                        timestampUtc=$timestamp
                        elapsedMs=$elapsedMs
                        scheduledMs=$scheduledMs
                        role=$null
                        defaultRoles=@($bindings | Where-Object { $null -ne $_.endpointId -and $_.endpointId -eq $reading.EndpointId } | ForEach-Object { $_.role })
                        endpointId=$reading.EndpointId
                        endpointName=$name
                        endpointState='ACTIVE'
                        volume=$reading.Volume
                        mute=$reading.Mute
                        error=$reading.Error
                    })
                }
            } catch {
                $errorCount++
                $samples.Add([pscustomobject][ordered]@{
                    timestampUtc=$timestamp; elapsedMs=$elapsedMs; scheduledMs=$scheduledMs
                    role=$null; defaultRoles=@(); endpointId=$null; endpointName=$null
                    endpointState=$null; volume=$null; mute=$null; error=$_.Exception.GetBaseException().Message
                })
            }
        } else {
        foreach ($role in $roles) {
        $timestamp = [DateTimeOffset]::UtcNow.ToString('o')
        $elapsedMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 3)
        try {
            $reading = $reader.ReadRole($role)
            if ($null -ne $reading.Error) { throw $reading.Error }
            $samples.Add([pscustomobject][ordered]@{
                timestampUtc = $timestamp
                elapsedMs = $elapsedMs
                scheduledMs = $scheduledMs
                role = $roleNames[$role]
                endpointId = $reading.EndpointId
                volume = [double]$reading.Volume
                mute = $reading.Mute
                error = $null
            })
        } catch {
            $errorCount++
            $samples.Add([pscustomobject][ordered]@{
                timestampUtc = $timestamp
                elapsedMs = $elapsedMs
                scheduledMs = $scheduledMs
                role = $roleNames[$role]
                endpointId = $null
                volume = $null
                mute = $null
                error = $_.Exception.GetBaseException().Message
            })
        }
        }
        }
    }
    $clock.Stop()
    $record = [ordered]@{
        schema = 'desk-coreaudio-observation-v1'
        readOnly = $true
        source = 'Windows CoreAudio IAudioEndpointVolume'
        getters = @('GetMasterVolumeLevelScalar', 'GetMute')
        defaultEndpointFlow = 'eRender'
        defaultEndpointRole = if ($AllActiveEndpoints -or $AllRoles) { 'all-three-render-roles' } else { 'eMultimedia' }
        observationMode = if ($AllActiveEndpoints) { 'all-active-render-endpoints' } elseif ($AllRoles) { 'three-default-render-roles' } else { 'default-multimedia-render-endpoint' }
        startedUtc = $startedUtc.ToString('o')
        completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        requestedSeconds = $Seconds
        sampleIntervalMs = 250
        actualElapsedMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 3)
        sampleCount = $samples.Count
        errorCount = $errorCount
        evidenceBoundary = 'Observes host state only; does not identify the cause of changes or prove HID input.'
        samples = $samples.ToArray()
    }
    if ($AllActiveEndpoints) {
        $record['endpointEnumeration'] = 'IMMDeviceEnumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE) each tick'
        $record['endpointNameSource'] = 'Get-PnpDevice AudioEndpoint friendly-name snapshot before sampling; names may be null for newly connected endpoints'
        $record['defaultRoleSnapshots'] = $roleSnapshots.ToArray()
        $record['servicesBeforeSampling'] = $services
        $record['steelSeriesProcessesBeforeSampling'] = $steelSeriesProcesses
        $record['inventoryErrors'] = $inventoryErrors.ToArray()
    }
    $json = $record | ConvertTo-Json -Depth 7
    # CreateNew preserves any evidence file that appeared while sampling.
    $stream = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally {
        $stream.Dispose()
    }
    Write-Host "CoreAudio observation saved: $OutputPath ($($samples.Count) samples, $errorCount errors)"
    if ($errorCount -gt 0) { exit 1 }
    exit 0
} catch {
    [Console]::Error.WriteLine("Audio observation failed: $($_.Exception.GetBaseException().Message)")
    exit 1
} finally {
    if ($null -ne $reader) { $reader.Dispose() }
}
