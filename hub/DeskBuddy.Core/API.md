# DeskBuddy.Core API (protocol 1)

Targets `net10.0`, with no NuGet packages, WPF, serial-port or hardware dependency. All types are in `DeskBuddy.Core`. The app owns verified-port discovery and transport lifecycle implementation. Core never auto-arms output, flashes firmware, retries a command or replays an earlier session.

## Configuration

`HubBank(byte ActiveProfile, HubProfile[] Profiles)` contains exactly three `HubProfile(string Name, Theme Theme, Face Face, bool Animation, bool EncoderReverse, ushort AccentRgb565, HubBinding[] Bindings, HomeMode Mode=HomeMode.Face)` records. `HubBinding(BindingKind Kind, ushort Value=0, byte Modifiers=0)` has kinds Disabled/Consumer/Keyboard/Local. Enum values and binding order match `../PROTOCOL.md`. Arrays permit editing; pass a `BankCodec.Clone(bank)` to editors to avoid changing a readback snapshot. Each client mutation captures a validated copy before awaiting I/O.

`HubDefaults.CreateBank()` returns independent MEDIA/STUDY/WORK arrays. The three defaults have identical mappings and appearance; their names are editable labels, not application-specific presets. All three start with K1 play/pause, K2 previous, K3 next, K4 mute, K5 local menu, K6 local marker, clockwise volume up, counterclockwise volume down, knob press mute, and touch previous/play-pause/next. `BankCodec.Encode`, `Decode`, `ToHex`, `FromHex`, `Clone`, `Validate` throw `FormatException` for invalid wire data. Encode returns exactly 224 bytes. No extra CRC is added to the protocol wire bank. `Crc32.Compute` is the standard reflected CRC32 used only for local file integrity.

`LocalConfigurationStore.ExportJson(bank)`, `ImportJson(json)`, `Load(path)` and `SaveAtomic(path, bank)` store a strict version-1 public config envelope (format/version/bankHex/crc32). Unexpected properties, duplicate keys, overlong files, unsupported schema, invalid bank or checksum are rejected. Save stages in the same existing directory, flushes, reads back, then atomically replaces/moves; it does not delete the destination first. Disk/permission errors propagate. This is not cryptographic authentication or a physical power-loss guarantee. No BLE bonding data or executable actions are stored.

### Hub 1.1 appearance and home modes

`BankCodec.ThemeCount=8`, `FaceCount=12`, `HomeModeCount=3`. Added enum values are Theme Ocean=4, Sunset=5, Garden=6, Blueprint=7; Face Bunny=4, Ghost=5, Mochi=6, Orbit=7, Fox=8, Panda=9, Sleepy=10, Wink=11. `HomeMode` is Face=0, DeskPet=1, VirtualPet=2. Profile byte +6 stores Mode; byte +7 stays zero. Original 0..3 theme/face IDs, 224-byte length, schema/protocol 1, and the complete default-bank fixture are unchanged. Old configuration JSON contains the packed bank and therefore naturally decodes its zero byte +6 as Face. Offline import/export preserves all supported local IDs; it never clamps them to the last connected device.

`HelloInfo` adds optional final parameters `int ThemeCount=4, int FaceCount=4, int HomeModeCount=1`. `HubProtocol.ParseHello(HubReply)` parses a successful HELLO, checks the base protocol/layout, and validates optional counts. Missing fields default independently to 4/4/1. Present `theme_count` must be 4..8, `face_count` 4..12 and `home_mode_count` 1..3, as unsigned decimal integers; malformed, duplicate or excessive counts fail the connection instead of silently downgrading.

Public helpers are `SupportsTheme(Theme)`, `SupportsFace(Face)`, `SupportsHomeMode(HomeMode)`, `SupportsBank(HubBank)` and `RequireSupportedBank(HubBank)`. The bank helpers validate the local bank first and check every profile, including inactive ones. Unsupported known appearance or mode yields the local `HubProtocolException.Code == "UNSUPPORTED_STYLE"`. PREVIEW/SAVE run this guard before sending any line or invalidating the known device snapshot. GET is also checked against peer capability. Reconnect clears and re-reads capability; there is no automatic rewrite of a local draft. App controls can use these helpers to label unsupported choices while allowing offline previews.

Home modes are display choices. This core does not invent pet activity readings or persistent pet statistics; absent telemetry remains null.

### Hub 1.3 temporary device animation preview

`HelloInfo` has an additional optional final `bool SupportsAnimationPreview=false`. Only explicit `animation_preview=1` advertises support; missing or `0` means unsupported. Other values and duplicate fields are invalid. Build strings are never used to guess support. Reconnect clears and re-reads this capability.

`AnimationPreviewAction : byte` is Idle=1, Blink=2, Happy=3, Walk=4, Tap=5, Sleep=6, Wake=7. There is no zero or Stop start action. Use `StartAnimationPreviewAsync(AnimationPreviewAction action, CancellationToken cancellationToken=default)` and `StopAnimationPreviewAsync(CancellationToken cancellationToken=default)`, both returning `Task<DeviceStatus>`. An undefined enum throws `ArgumentOutOfRangeException` before I/O. Both methods locally reject legacy or unadvertised support with `HubProtocolException.Code == "UNSUPPORTED"` before sending or invalidating valid readback.

Each successful operation sends exactly `HUB n ANIMATE <IDLE|BLINK|HAPPY|WALK|TAP|SLEEP|WAKE|STOP>` then `HUB m STATUS`, with different request IDs. No duration argument, bank command, automatic configuration application, SAVE, or output-ON command is sent. The app may explicitly apply its draft beforehand through the separate PREVIEW API. Core never implicitly changes the cached bank, its revision or the profile Animation flag for an animation command.

Start requires ACK `demo_action=N demo_remaining_ms=6000 armed=0|1`. Without `preserve_output=1`, armed must be 0 and firmware returns HOME/OFF. With that explicit capability, either actual output state is valid. The six-second override is RAM-only and leaves actual input/pet counters unchanged. STOP requires ACK `demo_action=0 demo_remaining_ms=0 armed=0|1`: it does not restore previous ON, and does not turn off an already-ON device when no demo is active. The firmware can reject non-STOP while calibrating with BUSY, or Walk in Face mode with INCOMPATIBLE. Core propagates these errors and does not retry them or infer a successful state.

`DeviceStatus.HasAnimationPreviewStatus` distinguishes old absent telemetry from a reported inactive demo. `DemoAction` is `AnimationPreviewAction?`: null means action zero (inactive) or absent, distinguished by the presence flag. `DemoRemainingMilliseconds` is `uint?`: null means absent, zero means inactive. Fields must appear together; action is 0..7, remaining is 0..6000, and either both are zero or both positive. An active demo requires HOME (`page=0`); OFF (`armed=0`) is additionally required unless `preserve_output=1` was negotiated. A peer advertising support must include the fields in STATUS. Malformed telemetry fails parsing; malformed unsolicited lines cannot refresh known status.

After validating ACK, Core checks fresh STATUS before accepting it. A different active action produces local ANIMATION_NOT_CONFIRMED; missing/invalid fields produce FormatException. A zero/zero STATUS is a valid observed cancellation/expiry, including a physical input between ACK and readback. The caller must use the returned status rather than assume the ACK means it is still playing. The app can derive elapsed time as `6000 - remaining`, using a monotonic clock, and reconcile with later fresh telemetry.

STOP, expiry, physical input, configuration application, leaving HOME, changing route or enabling output cancels firmware demonstration. Those are device semantics, not extra commands sent by Core. A timeout/cancel after sending may leave the device playing until cancellation or expiry; Core marks status unknown and does not send an implicit STOP or replay. Explicit RefreshStatusAsync is the way to observe the result. Disconnect cancels active and queued host operations and drops capability/status caches; reconnect sends only HELLO/GET/STATUS.

The Core tests use an in-memory protocol peer and do not establish physical animation, HID, timing or firmware acceptance. Reproduce with `tools/dotnet/dotnet.exe run --project hub/DeskBuddy.Core.Tests -c Release -- --validate-presets outputs/hub-r2/presets` from the workspace root (or omit the optional preset argument).

### R3E output preservation and physical pet feedback

`HelloInfo` appends optional `bool PreservesOutput=false` and `bool SupportsPetInputFeedback=false`. They parse `preserve_output` and `pet_input_feedback` independently: only literal `1` enables a capability; missing or `0` means false. Malformed or duplicate values fail the handshake. No firmware build name is used as a proxy. A reconnect always clears and rereads the flags.

On an explicit preserving peer, PREVIEW/SAVE/REVERT/SELECT require a Boolean `armed` ACK and still verify the exact bank, revision and preview state via GET and STATUS. Either actual ON/OFF state is valid. Language, ordinary HOME, same-route selection, ANIMATE/STOP and USB MANAGE report their real armed state. Diagnostic/info/activity pages still require OFF unless the independent R3F page capability below is present. The firmware is responsible for making an actual route change turn OFF; Core validates the requested target route and observed output without treating a potentially stale cached route as proof that a physical route change occurred.

ACK and STATUS are separate observations. A physical OFF, cancellation or disconnect may happen between them. Core returns the fresh state, never requires it to equal a cached pre-command ON state, and **never sends OUTPUT 1 to restore anything**. An explicit OUTPUT command still requires its requested state to be confirmed; a competing physical OFF can therefore make explicit OUTPUT 1 return CONTROL_NOT_CONFIRMED, with no automatic retry. A storage BUSY response propagates unchanged and invalidates configuration readback until an explicit GET; no SAVE retry is hidden inside Core.

`LegacyStatusParser.Parse(line, receivedAt=null, preservesOutput=false)` and `TryParse(line, out status, receivedAt=null, preservesOutput=false)` keep old behavior by default. Passing true permits active animation with ON, but HOME and all action/time bounds still apply. HubClient passes only its negotiated flag on command and unsolicited-status paths.

Physical feedback is independent of temporary `demo_*` telemetry. `DeviceStatus.HasPetInputFeedbackStatus`, `PetDirection` and `PetElapsedMilliseconds` expose `pet_direction` (-1..1) and `pet_elapsed_ms` (0..1799). Both fields are required together when either is present, and a peer advertising feedback must include them in STATUS. Absent old-peer readings remain null; missing or invalid unsolicited fields cannot refresh the accepted snapshot. These are reported device values, not synthesized activity or HID-delivery evidence.

### R3F page output preservation (Hub 1.5.1)

`HelloInfo` appends `bool PreservesPageOutput=false`, parsed only from strict optional `preserve_page_output=0|1`. Missing means false; it never follows from `PreservesOutput`, build names or status values. Reconnect clears and renegotiates it.

For an advertising peer, PAGE DIAG/INFO/ACTIVITY success ACK and fresh STATUS may report either actual `armed` value. PAGE HOME retains its existing `PreservesOutput` rule. Both observations still require the requested page and a valid Boolean `armed`; a physical OFF between ACK and STATUS wins without any restoration command. Older R3E peers retain their diagnostic/info/activity OFF checks. This extension does not relax configuration, route, animation or BLE permission checks.

`ControlAsync` additionally accepts `PAGE SETTINGS|MANAGEMENT|LANGUAGE|TOOLS` only when `PreservesPageOutput` is true; otherwise it returns local UNSUPPORTED with no write. The page IDs are HOME 0, SETTINGS 1, DIAG 2, INFO 4, ACTIVITY 5, MANAGEMENT 6, LANGUAGE 7, TOOLS 8. Calibration 3 has no remote PAGE command; Back/parent navigation is device-local.

`DeviceStatus.HidSuppressed` is the optional strict Boolean `hid_suppressed`, default false for an older absent field. It is independent of `Armed`: R3F reports suppression on diagnostics/activity/calibration; info/management/language/tools allow ordinary input. Calibration still explicitly turns the switch OFF. The app displays the actual switch and a separate pause indicator; it never equates suppression with a changed switch. Fresh explicit enabling remains unavailable on test/calibration pages, stale states or a reported suppression. Returning only permits output if the device's switch is still ON and its normal link/input requirements hold; Core sends no OUTPUT 1. Existing active animation still requires HOME.

## Transport and session

```csharp
interface ILineTransport {
  event Action<string>? LineReceived;
  event Action<Exception?>? Disconnected;
  Task OpenAsync(CancellationToken cancellationToken);
  Task SendLineAsync(string line, CancellationToken cancellationToken);
  Task CloseAsync();
}
```

Transport sends LF exactly once and raises complete lines without CR/LF. `LineFramer(maxLineLength=4096).Feed(ReadOnlySpan<byte>)` returns complete nonempty lines; overflow/non-ASCII invalidates the whole line until LF. Do not reconnect implicitly inside a transport. `Disconnected` terminates pending requests and invalidates state.

SendLineAsync must honor cancellation and must never continue an old write through a newly opened port instance. The request deadline covers both write and ACK. If the write itself stalls, Core closes the transport and requires an explicit reconnect; it never repeats the line.

`HubClient(transport, TimeSpan? requestTimeout=null, TimeProvider? timeProvider=null)` exposes `Changed`, `State` (Disconnected/Connecting/Ready/Legacy/Faulted), `IsLegacy`, `Capabilities`, `Status`, `Configuration`, `StatusAge`, `IsStatusStale`, `StatusStaleAfter` (default three seconds) and `LastError`. Changed runs on the producer thread; WPF must dispatch. A UI timer may reevaluate staleness without new events.

Freshness uses monotonic TimeProvider timestamps, so a backwards wall-clock adjustment cannot revive old data; ReceivedAt is for display only. The Configuration getter returns an isolated clone. Legacy firmware has no request IDs, so a fresh observed STATUS is checked after an explicit control; this is weaker evidence than a protocol-1 ACK and is never presented as one.

Async methods, each with optional final `CancellationToken`:

- `ConnectAsync()` opens transport, tries HELLO, validates protocol-1 capabilities, GETs config and STATUS. A USB/unknown-channel HELLO timeout/UNSUPPORTED can fall back to legacy STATUS; BLE never falls back. Unknown future protocol is an error, never legacy fallback.
- `DisconnectAsync()` clears device data and closes transport. For an established BLE management session it first attempts one self-revocation ACK; see below. It never replays a pending mutation.
- `GetAsync()` returns `BankSnapshot(HubBank Bank, uint Revision, bool Preview)`.
- `RefreshStatusAsync()` returns fresh `DeviceStatus`.
- `PreviewAsync(bank, uint expectedRevision)`, `SaveAsync(bank, uint expectedRevision)`, `RevertAsync()`, `SelectProfileAsync(byte index, uint expectedRevision)` await their own response then GET readback and return `BankSnapshot`.
- `ControlAsync(string command)` accepts exactly `OUTPUT 0|1`, `ROUTE USB|BLE`, `LANGUAGE EN|ZH`, `PAGE HOME|DIAG|INFO|ACTIVITY` (plus capability-gated R3F pages above), `COUNTERS RESET`; returns fresh `DeviceStatus`. Legacy maps these to existing commands and verifies observed status instead of inventing an ACK.
- `SetBleManagementAsync(bool enabled)` / `ControlAsync("MANAGE 0|1")` are capability-checked USB-only grant/revoke APIs, with ACK and fresh STATUS confirming the permission state and a valid actual output state. Older peers must report OFF; explicit preserve-output peers may report ON or OFF. A BLE session self-revokes through `DisconnectAsync()` instead, because STATUS is no longer permitted after self-revocation.
- `StartAnimationPreviewAsync(AnimationPreviewAction action)` and `StopAnimationPreviewAsync()` return freshly observed `DeviceStatus`; see the temporary animation contract above.
- `ReadDisplayAsync()`, `SetDisplayAsync(uint usbSeconds, uint batterySeconds)`, `SleepDisplayAsync()` and `WakeDisplayAsync()` return fresh `DeviceStatus`. They require explicit `SupportsDisplaySleep`; valid timeout seconds are 0/30 or whole-minute multiples 60..43200. Writes outside the legacy set 0/30/60/300 additionally require SupportsCustomDisplayTimeout; otherwise local UNSUPPORTED_DISPLAY_TIMEOUT occurs before transport activity. SET verifies ACK, DISPLAY GET and STATUS under one operation lock. All are independent of the profile bank and never issue OUTPUT 1. Physical wake/OFF between ACK and STATUS wins; failed or interrupted operations are not replayed.

Only one logical operation is active at a time. IDs monotonically increase within the client, including across reconnects; interleaved legacy events and wrong-ID replies never acknowledge requests. Timeout/cancellation of configuration changes invalidates cached config; no automatic resend. STALE invalidates config and requires an explicit GET. Reconnect always reads the device and never writes a local bank. Disconnect clears caches and ends pending waits. A lost response may mean a device change happened; show unknown state and read it back before the next edit.

`HelloInfo` properties: Protocol(int), Build(string), ConfigBytes(int), Profiles(int), Bindings(int), Revision(uint), Preview(bool), ThemeCount(int), FaceCount(int), HomeModeCount(int), SupportsAnimationPreview(bool), SupportsBleManagement(bool), BleManagementIdleMilliseconds(uint?), PreservesOutput(bool), SupportsPetInputFeedback(bool), PreservesPageOutput(bool). `DeviceStatus` exposes `Fields` read-only, `ReceivedAt`, `Get(key)`, `GetUInt32(key)`, `GetInt32(key)`, `GetBool(key)`, plus Build/Route/Connected/Armed/HidSuppressed, KeysDown(bool[]?), KeyPresses/KeyReleases(uint[]?), HasAnimationPreviewStatus(bool), DemoAction(AnimationPreviewAction?), DemoRemainingMilliseconds(uint?), HasPetInputFeedbackStatus(bool), PetDirection(int?), PetElapsedMilliseconds(uint?). `GetUInt32("home_mode")` returns null when absent, otherwise 0..2. Missing optional telemetry is null, never a fabricated zero. `HubProtocolException.Code` carries a device or explicitly documented local guard error; malformed responses use `FormatException`; timeouts use `TimeoutException`; disconnect uses `IOException`.

`HubProtocol.BuildRequest(id,command)`, `ParseReply`, `TryParseReply`; `LegacyStatusParser.Parse/TryParse` are independently testable. STATUS boolean and counter fields are validated. Battery remains an estimate; connected is HID readiness and sent is report submission, not an application ACK.

## Hub 1.7 screen-rest data

HelloInfo.SupportsCustomDisplayTimeout is the strict optional display_custom
Boolean, false when omitted. True requires SupportsDisplaySleep; never infer it
from build/version. DisplaySettings.IsAllowedTimeout validates the full
0/30/whole-minute 60..43200 range; IsLegacyTimeout recognizes 0/30/60/300.
Reads accept the full valid range without silently clamping an observation.
Writes are gated inside the serialized operation against the current session
capability before status invalidation or transport activity. Reconnect rereads it.

`HelloInfo.SupportsDisplaySleep`, `BacklightControl`, `SupportsSleepBinding` are
strict capability flags, default false. Display support requires both companion
flags to be explicitly reported. `SupportsBank` / `RequireSupportedBank` also gate
local kind 3 value 5 against `SupportsSleepBinding` in every profile; unsupported
bindings produce local `UNSUPPORTED_BINDING` before any write.

`DeviceStatus.Display` is nullable `DisplaySettings(bool Asleep,
uint UsbTimeoutSeconds, uint BatteryTimeoutSeconds, bool Saved,
bool BacklightControlled, uint WakeCount, bool Armed, uint IdleMilliseconds)`.
Null on older firmware means unavailable. `DisplayIdleMilliseconds` is also exposed
as a nullable convenience property. Once any display telemetry is present, the
whole group is required and strictly validated. Advertised support requires it on
STATUS; backlight telemetry must match HELLO. The device, rather than the host,
determines sleep state and idle time. Status/GET does not wake it.

Sleep/wake preserves output; `HidSuppressed` covers sleeping, wake-input isolation,
and the existing test pages. Do not label every suppressed state as a test page or
replace Armed with !HidSuppressed. The first physical event wakes only; it is not a
queued HID action. Timeouts use a separate saved record; no reserved bank bytes or
default profile mappings change. Readbacks and `IsStatusStale` determine displayed
state; the App does not infer a saved setting from local selector values.

## Hub 1.4 BLE management

Management transport and HID output route are independent. `ManagementChannel` is Unknown/Usb/Ble; `IManagementTransport : ILineTransport` exposes `Channel`. `HubClient.ManagementChannel` reports the transport, not `DeviceStatus.Route` (the selected HID route). The app implements `SerialLineTransport` as USB and `WindowsBleGattConnection` for Windows BLE. The portable `BleLineTransport` implements `IManagementTransport` around an injected `IGattByteConnection` factory, creating a fresh backend for every open.

`IGattByteConnection` exposes `NotificationReceived(ReadOnlyMemory<byte>)`, `Disconnected(Exception?)`, `OpenAsync(CancellationToken)`, `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)`, and `CloseAsync()`. It enables notifications before Open completes, writes with response, and closes its notification subscription and OS references. The portable transport validates 1..511 printable ASCII characters, appends one LF, serializes writes, and splits each line into at most 20 bytes per acknowledged GATT write. It accepts at most 4096 reply-content bytes plus LF, bounds partial buffering, and discards an invalid/overlong line through LF. Default connection deadline is 20 seconds; the total line-write deadline is eight seconds. A failed/partially cancelled write closes that generation; queued writes, partial lines and old callbacks are discarded. Nothing is resent after reconnect.

Windows uses service `8e7b0001-6f9a-4c4d-9f32-2f3c1d0b5a60`, RX Write-with-response `8e7b0002-6f9a-4c4d-9f32-2f3c1d0b5a60`, TX Notify `8e7b0003-6f9a-4c4d-9f32-2f3c1d0b5a60`. The UI enumerates already-paired devices, requires explicit selection and checks this service after opening. It does not pair devices or remove bonds. Both characteristic handles require encryption. FromId/access operations run on the UI dispatcher; cancelled late discovery results are disposed without entering a new session. Notify and write operation **submission** occurs under the same lifecycle lock as Close; awaits remain outside it. An OS operation already submitted cannot be promised retracted by cancellation. Close removes callbacks, requests CCCD None, verifies its result and disposes references without explicitly disconnecting the HID physical link.

The filtered service query is Uncached. Only a successful result containing zero services triggers one `GetGattServicesAsync(Cached)` full-table fallback. `GattServiceDiscovery.SelectAsync` tests this policy with injected enumeration delegates and disposable service references: strict expected UUID, exactly one matching service, disposal of all unselected or cancelled results, no retry after exceptions/failed statuses, and the original cancellation token/deadline throughout. It never queries full-table Uncached or repeats a user command. Characteristics still use Uncached discovery; encryption and protocol HELLO remain required after a cached service is selected. An empty or inconsistent list reports `GATT_SERVICE_MISSING` rather than claiming the physical firmware version is known. This compatibility path follows observed Windows enumeration behavior; it is not a cache reset, and it makes no pairing changes. Microsoft specifically recommends Cached enumeration after a [GattServicesChanged event](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.gattserviceschanged).

`HelloInfo.SupportsBleManagement` is true only for explicit `ble_management=1`; missing/0 is false, malformed/duplicate values fail parsing. Optional `BleManagementIdleMilliseconds` is null when absent; a present `ble_manage_idle_ms` must be 15000 and requires the BLE capability. Build strings never imply support. A BLE handshake without this capability fails `UPGRADE_REQUIRED`; it never sends a legacy fallback command. `LOCKED` over BLE clears state and closes the transport.

`DeviceStatus` provides `HasBleManagementStatus`, `BleManagementState`, `BleManagementRemainingMilliseconds`, `ManagementOwner`, `ManagementLeaseMilliseconds`; raw link/security/bond booleans remain available through `GetBool`. Once any management field is present, all seven fields are required and checked: state 0..2, grant-window remaining 0..60000 (positive iff state 1), link/secured/bonded booleans, writer owner 0..2, lease remaining 0..10000 (positive iff owned). Authorized state 2 requires a linked, encrypted, bonded peer. A device advertising management must include these fields in STATUS. The grant-window field is not the session idle countdown.

On the device, use Settings → Device info → BLE management → Allow BLE connection (菜单 → 设备信息 → 蓝牙管理 → 允许蓝牙连接), or explicitly grant via USB. Firmware grants up to 60 seconds for the first successful management request on a bonded encrypted link; after management begins, 15 seconds with no successful authorized request revokes management. The normal status poll keeps a working session alive. Successful changes acquire/renew a ten-second writer lease; a competing writer receives BUSY, and read-only HELLO/GET/STATUS remain possible while authorized. Errors neither renew the writer lease nor prove a mutation succeeded.

Normal BLE `DisconnectAsync`/`DisposeAsync` stops ordinary work and attempts **one** `CONTROL MANAGE 0`. Self-revoke ACK must report state/remaining 0/0 and a valid `armed=0|1`, preserving the existing HID state. It sends no STATUS after revoking its own permission. An ERR LOCKED already confirms permission is absent. The transport then attempts CCCD None independently. If ACK and unsubscribe are both unconfirmed, close reports an IOException while the local session remains closed; revoke on the device or allow firmware idle expiry. If revoke was acknowledged but Windows cleanup failed, `LastError` records the cleanup warning. Failed background unsubscribe is retained for the next explicit close rather than silently lost. Abrupt process exit cannot promise an ACK/CCCD write; the firmware idle expiry is the fallback. USB grant/revoke is separate: older peers confirm OFF, while explicit preserve-output peers retain and report the actual ON/OFF state.

Tests use injected byte backends, including fragmentation, malformed/oversized data, timeout/cancel, late opening, old callbacks, close failures, single self-revoke, armed HID preservation, LOCKED/BUSY and no SAVE replay. They do not validate Windows radio timing, consent UI, actual encryption, OS queue cancellation or physical acceptance. The Windows project targets `net10.0-windows10.0.19041.0`; its build is distinct from a GUI or real-device test. API references: [Microsoft GATT client guidance](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client) and [Windows APIs in desktop .NET apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps).

## Hub 1.7.1 local workspace recovery

`HubWorkspaceStore.Load(path)` returns `WorkspaceLoadResult(State, Source, Warning)`.
`State` is `HubWorkspaceState(DeviceWorkspaceSnapshot? LastDevice, HubBank? Draft)`;
`DeviceWorkspaceSnapshot` contains only `Bank` and `ObservedAt`. `Source` is
`primary`, `backup`, `missing`, or `invalid`. A missing file does not create one.
The separate strict JSON format `desk-buddy-workspace` version 1 stores CRC-checked
224-byte bank HEX and the observation timestamp, never a device ID, connection,
capabilities, armed state or pairing data. An older `desk-buddy-config` export
loads only as a draft, with a warning; it never becomes a confirmed device snapshot.

`SaveAtomic(path, state)` validates before writing, uses a same-directory staging
file, Flush(true), readback and atomic replace. A validated previous primary becomes
`.bak`. Recovery from a malformed primary preserves an existing healthy backup.
An empty `.lock` file serializes cooperating writers; IO/access/lock failures fail
without treating a locked destination as corrupt. Files are bounded to 64 KiB.
Load does not modify corrupt originals; simply opening/closing an invalid workspace
also does not schedule a replacement with defaults.

The normal WPF app uses `%LOCALAPPDATA%/DeskBuddy/Hub/workspace-v1.json`.
An unsent valid draft takes precedence at offline startup; otherwise the last GET
snapshot is shown with an explicit non-live label. A successful real connection
always shows device GET as authoritative, retains any separate local draft and
requires an explicit editor action to load it. That action sends no command.
Explicit device read/revert/select/verified preview/save replaces the editor and
clears the retained draft. Save failure keeps current edits and offers JSON export.
Demo/render/device-smoke modes do not read or write the real workspace.

`--workspace-smoke <stage> <isolated-file> <result-directory>` is a bounded offline
fixture using the actual MainWindow startup and Closing flow and a fake protocol
peer only. Paired `seed-*`/`read-*` invocations verify recovery across distinct
processes; backup/invalid/legacy/write-failure cases exercise startup warnings.
No firmware contract, bank layout, display-timeout persistence or output policy
changes in this revision.
