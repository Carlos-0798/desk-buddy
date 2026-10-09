# Desk Buddy Hub protocol 1 (implementation contract)

USB CDC at 115200 or the authorized BLE management transport, ASCII LF lines.
Legacy R2C STATUS/commands remain supported over USB.
Use one serial owner at a time. DFU is a separate, explicit maintenance operation.

Request: `HUB <id> <verb> [args]`, id 1..2147483647.
Response: `HUB <id> OK <key=value ...>` or `HUB <id> ERR code=<UPPER_CODE>`.
No spaces in values. A command is complete only after its matching response.
Unsolicited legacy STATUS/events can interleave; never count them as an ACK.
Maximum request 511 ASCII characters excluding newline; overflow discards whole line.

Commands (R3H response fields; capability-based compatibility is described below):

- HELLO -> proto=1 build=... config_bytes=224 profiles=3 bindings=12 revision=<u32> preview=0/1 theme_count=8 face_count=12 home_mode_count=3 animation_preview=1 preserve_output=1 pet_input_feedback=1 preserve_page_output=1 ble_management=1 ble_manage_idle_ms=15000 display_sleep=1 backlight_control=0/1 sleep_binding=1 display_custom=1
- GET -> revision=<u32> preview=0/1 hex=<448 hexadecimal chars>
- PREVIEW <expected_revision> <hex> -> revision=<new> preview=1 armed=<actual>; validate entire bank before applying; preserve output; no flash write.
- SAVE <expected_revision> <hex> -> revision=<new> preview=0 saved=1 armed=<actual>; write/readback verified alternating record, then apply; preserve output. Unchanged persisted bytes do not write flash.
- REVERT -> revision=<new> preview=0 armed=<actual>; restore persisted/default bank; preserve output.
- SELECT <expected_revision> <index0to2> -> revision=<new> preview=1 armed=<actual>; runtime selection without flash; preserve output.
- CONTROL OUTPUT 0|1 -> armed=0/1; existing arming preconditions apply and denied arming returns ERR code=NOT_READY.
- CONTROL ROUTE USB|BLE -> route=... armed=<actual>; preserve output when reselecting the current route; a different route closes output.
- CONTROL LANGUAGE EN|ZH -> language=... saved=1 armed=<actual>; preserve output, or return ERR code=BUSY / SAVE_FAILED.
- CONTROL PAGE HOME|DIAG|INFO|ACTIVITY -> page=<numeric> armed=<actual>; HOME follows preserve_output; DIAG/INFO/ACTIVITY preserve the switch only with preserve_page_output=1. SETTINGS/LANGUAGE/TOOLS/MANAGEMENT additionally require that new capability.
- CONTROL MANAGE 0|1 -> ble_manage=<0..2> ble_manage_remaining_ms=<0..60000> armed=<actual>; USB grants/revokes BLE management without changing output in R3E. Authorized BLE may only revoke itself with MANAGE 0.
- CONTROL COUNTERS RESET -> reset=1
- DISPLAY GET|SLEEP|WAKE -> complete display observation described below; no bank change.
- DISPLAY SET <usbSeconds> <batterySeconds> -> independent verified display settings; with display_custom=1, each is 0, 30, or an integer-minute multiple of 60 from 60 through 43200 seconds. Without that capability, only 0/30/60/300 may be written.
- STATUS -> same status fields as legacy STATUS on OK line (including usb_connected, ble_connected, config_revision, config_preview, profile_index, theme, face, home_mode, pet_direction, pet_elapsed_ms, hid_suppressed).

Errors include INVALID, UNSUPPORTED, STALE, SAVE_FAILED, NOT_READY, BUSY,
INCOMPATIBLE, LOCKED and FORBIDDEN.
Expected revision compares current runtime revision, not a PC file timestamp.
Always re-read configuration after reconnect; never automatically overwrite it.
Changing routes/config never enables output. Local emergency stop remains fixed.

### Output preservation and physical feedback (R3E)

HELLO adds independent boolean capabilities `preserve_output` and
`pet_input_feedback`. A present value must be `0` or `1`; an omitted field means
false. Do not infer either capability from the build name. Read HELLO again after
every reconnect; protocol 1, schema 1, the 224-byte bank and its defaults are unchanged.

With `preserve_output=1`, PREVIEW/SAVE/REVERT/SELECT, language changes, ordinary
HOME/Settings navigation, same-route selection, ANIMATE/STOP and USB management
grant/revoke preserve the current ON/OFF state. Protocol success ACKs report the actual
`armed=0|1` at acknowledgement time; this is an observation, not a promise that output
will remain enabled. Configuration ACKs also retain revision/preview verification.
An ACK with `armed=1` followed by a fresh STATUS with `armed=0` is valid when a
physical stop or HID readiness/link loss intervenes. Never send `OUTPUT 1` to
restore a cached state, and never replay an interrupted change after reconnect.

Explicit output OFF, a real USB/BLE output-route change, diagnostic/activity/info/
management pages on R3E, calibration, emergency stop and HID readiness/link-generation
loss still close output. R3F extends page behavior as described below. Opening a management page is distinct from granting
management access over USB. None of the preserving operations implicitly arms
output; explicit arming still requires the existing readiness checks.

Without `preserve_output=1`, retain the older OFF checks for configuration changes,
route control, HOME, animation starts and USB management control. Older configuration
ACKs may omit `armed`; fresh STATUS must still confirm the old OFF behavior. STOP
reports the current state, and BLE self-revocation remains independent of HID output.
Do not enable the relaxed ON checks globally for legacy devices.

Mapping changes discard queued actions from the old mapping and request release of
an accepted HID report without rearming. Before synchronous SAVE or language storage,
an outstanding release can produce `ERR code=BUSY`; the requested configuration or
language is not committed. Output is not deliberately closed for this condition.
The host reports the failure for a manual retry, without automatic retries or arming.

With `pet_input_feedback=1`, both legacy and HUB STATUS include:

- `pet_direction=-1|0|1`: counterclockwise walk, no walking direction, or clockwise walk.
- `pet_elapsed_ms=0..1799`: the current physical feedback animation phase in milliseconds.

The fields must occur together and be range-valid; advertised support requires both
on STATUS. They describe accepted physical interaction, alongside `pet_motion`
(0 idle, 1 happy, 2 walk), and are separate from the temporary `demo_*` overlay.
Repeated turns in the same direction sustain the walk phase; a new gesture/direction
restarts it. Feedback expires 1800 ms after the latest accepted input, returning both
fields to zero. Missing fields on an older device mean unavailable, not measured zero.
In R3E the stored animation flag controls automatic idle blinking only; accepted
physical turn/press/tap feedback remains visible when that flag is off.

### Page switch preservation (R3F / Hub 1.5.1)

`preserve_page_output=1` is a separate strict optional Boolean capability; missing
or 0 retains R3E page behavior. It permits PAGE DIAG/INFO/ACTIVITY to report their
actual retained `armed=0|1`. It does not change the existing `preserve_output`
requirements for HOME, configuration, routes or temporary animation. No operation
restores an earlier ON after physical OFF, readiness loss or reconnect.

The same capability gates additional CONTROL PAGE tokens: SETTINGS=1,
MANAGEMENT=6, LANGUAGE=7 and TOOLS=8. Existing IDs remain HOME=0, DIAG=2, INFO=4,
ACTIVITY=5; calibration=3 is not remotely selectable. Back follows the device's
parent page rather than a host-side page stack. Clients must reject these new tokens
locally on older peers, with no command sent.

Optional STATUS `hid_suppressed=0|1` is strict when present and defaults false when
absent. It reports the sending pause separately from the user's armed switch.
R3F pauses HID on DIAG/ACTIVITY and calibration; calibration still explicitly sets
OFF. INFO/MANAGEMENT/LANGUAGE/TOOLS permit normal physical input with ordinary
arming/readiness checks. Returning from a test page resumes only while the current
switch is still ON; it never sends or implies OUTPUT 1. Requesting OUTPUT 1 on a
test/calibration page is denied without flipping an already-ON switch. The Hub
shows pause separately and does not offer fresh enabling on suppressed pages.

### Screen rest (R3G; extended timeouts in R3H / Hub 1.7.0)

HELLO adds strict Boolean `display_sleep`, `backlight_control` and `sleep_binding`.
Missing fields on old firmware mean false. An advertised display capability must
include the other two flags explicitly; backlight/sleep-binding support requires
display support. Build names never grant these capabilities. Initially
`backlight_control=0`: the picture is hidden but the backlight stays lit. Only a
matching wired/compiled build can report 1. Neither state means whole-device power
off, nor does it establish a measured battery-life improvement.

R3H adds strict optional Boolean display_custom. Missing means false, independent
of the version string. True requires display_sleep=1. Permitted extended seconds
are 0, 30, or 60 * minutes with integer minutes in 1..720 inclusive (12 hours).
No decimals, negative values, rounding or clamping. Hub reads the full valid range
even from a peer omitting the new flag, but refuses an extended SET before
transmission unless that flag is true. Older display-capable firmware keeps
0/30/60/300 write choices. Firmware lacking display support receives no DISPLAY.

All four DISPLAY replies are ordinary `HUB n OK` lines containing:
`display_asleep=0|1 display_usb_s=<valid seconds> display_battery_s=<valid seconds>
display_saved=0|1 display_backlight=0|1 display_wake_count=<u32>
display_idle_ms=<u32> armed=<actual 0|1>`.
An advertising peer includes all those fields in STATUS as well. Backlight status
must match HELLO. These observations are separate from pet_sleeping and the bank.

`DISPLAY GET` is read-only, takes no writer lease and does not reset physical idle.
SET/SLEEP/WAKE use the existing management writer lease and can return BUSY.
SET persists an independent alternating record and verifies it before reporting
saved=1; SAVE_FAILED is never success. Unchanged already-saved settings avoid a
flash write. Default timeouts are USB 300 seconds and battery 60 seconds, initially
unsaved; connection does not automatically save defaults. Zero disables that idle
timeout. Successful SET wakes the screen; it does not alter profile/revision or armed.

The independent 20-byte stored record and 224-byte bank remain unchanged. Existing
saved timeouts load normally, not replaced by new defaults. Before downgrading to
R3G, explicitly restore both durations to one of its four supported values;
R3G cannot decode an extended-duration record.

Hub offers independent USB/battery presets: Never, 30 seconds, 1 minute,
5 minutes, 20 minutes, 30 minutes, 1 hour, and Custom. Custom accepts integer
minutes 1..720 with inline validation. Polling does not overwrite either editor
or its selection/caret. Unsaved timeout drafts survive navigation in the same
connection; reconnect clears them. Read current explicitly replaces timeout
drafts. Saving verifies ACK, DISPLAY GET and STATUS before clearing them and
does not commit an unrelated profile draft.

SLEEP/WAKE preserve the actual output switch and current page. After an actual
sleep-to-wake transition, the first touch/key/encoder event is consumed, followed
by at least 200 ms of quiet with all inputs released. `hid_suppressed` includes
screen sleep and this wake isolation, as well as diagnostic pauses. Resting,
paused HID and output OFF are distinct states. Status polling does not wake the
device. An explicit OUTPUT 1 while asleep first wakes and returns BUSY during
isolation; a later explicit request is needed if output was OFF. OUTPUT 0 remains
available and wakes while stopping output. Nothing restores a previously cached ON.

Hub serializes DISPLAY command/ACK/readback with ordinary polling. SET verifies
ACK, a separate DISPLAY GET and STATUS. A physical wake or output OFF after ACK
is reflected in the later status instead of being undone. Failure, timeout,
cancellation or reconnect never retries a mutation automatically.

The 224-byte bank/header/reserved bytes stay unchanged. Local kind 3 value 5,
modifiers 0, means immediate screen rest and requires `sleep_binding=1`.
Import/export can retain it offline; PREVIEW/SAVE checks every profile before
transmission. Older devices reject it locally as UNSUPPORTED_BINDING, while their
existing mappings remain usable. The default bank is unchanged. Hub offers 40
editor choices; video shortcuts remain aliases for existing keyboard HID bytes,
not macros, application detection or universal seek-time commands.

### Appearance capability extension (Hub 1.1)

The wire bank remains 224 bytes, schema 1 and protocol 1. Existing appearance IDs
0..3 and the complete default bank remain byte-for-byte unchanged. HELLO advertises
contiguous supported ID prefixes: theme IDs `0..theme_count-1` and face IDs
`0..face_count-1`. If an old protocol-1 device omits either count, that field defaults
independently to 4. Present counts must be unsigned decimal values in `4..8` and
`4..12`; malformed or larger counts are rejected rather than silently clamped.

`home_mode_count` advertises `0..home_mode_count-1`; missing means 1 (Face only),
and a present value must be in `1..3`. Profile byte +6, previously reserved zero,
now stores HomeMode: 0 Face, 1 DeskPet, 2 VirtualPet. Byte +7 remains reserved zero.
Old banks and old local JSON naturally decode as Face; no migration or default
byte change occurs. Pet modes describe the local home UI, not cloud services,
autonomous desktop automation, or a guarantee of persistent pet statistics.

Local import/export accepts all eight themes and twelve faces. Before PREVIEW or
SAVE, the host checks every profile's theme, face and home mode, including inactive profiles, against the
current connection's HELLO counts. Unsupported appearance is a local
`UNSUPPORTED_STYLE` error and sends no command; there is no automatic downgrade.
Reconnect reads HELLO again. The client also rejects GET data whose appearance
exceeds the peer's declared capability. Original 4/4 devices remain usable with
their supported IDs.

## Packed configuration (224 bytes, wire as hex)

Header bytes: 0=schema 1, 1=active profile 0..2, 2=profile count 3, 3..7=0.
Three profiles at 8 + index*72:
- +0 theme: 0 Midnight, 1 Paper, 2 Terminal, 3 Lavender, 4 Ocean, 5 Sunset, 6 Garden, 7 Blueprint
- +1 face: 0 Pixel, 1 Round, 2 Cat, 3 Robot, 4 Bunny, 5 Ghost, 6 Mochi, 7 Orbit, 8 Fox, 9 Panda, 10 Sleepy, 11 Wink
- +2 animation: 0 off / 1 on (automatic idle blink in R3E; physical feedback and explicit temporary previews are independent)
- +3 encoder reverse: 0/1
- +4..5 accent RGB565 little endian; zero means theme default
- +6 home mode: 0 Face (legacy default), 1 DeskPet, 2 VirtualPet
- +7 reserved zero
- +8..23 name: printable ASCII 0x20..0x7E, 1..15 chars, NUL padded; no embedded NUL then nonzero. Display name can be separate Unicode in local catalog, hardware label ASCII.
- +24..71 twelve bindings, four bytes each: kind, value low, value high, modifiers.

Binding order K1..K6, clockwise, counterclockwise, knob press, touch previous, touch play, touch next.
kind 0 disabled: value/modifiers all zero.
kind 1 consumer: values 0xCD,0xB6,0xB5,0xE2,0xE9,0xEA; modifiers zero.
kind 2 keyboard: value 0x04..0x73, high byte zero; modifier byte is standard USB modifier mask. One key plus modifiers, discrete press/release, not held macro.
kind 3 local: value 1 menu, 2 marker, 3 output off, 4 next profile, 5 immediate screen rest (requires sleep_binding); modifiers zero.
No executable scripts, shell commands, raw flash or pairing data in profile files.
Defaults: three named MEDIA/STUDY/WORK, mappings start from R2C media defaults. K5 menu retained by default; USER SW emergency stop always independent of remapping.

## Release scope
Native Windows WPF Hub: discover only verified Feather USB ports or paired BLE devices with the management service; legacy R2C read-only plus existing explicit controls; R3 bank editing, preview/save/revert, local import/export, theme preview, input/status diagnostics, bounded logs, stale/disconnect handling, no automatic firmware update or output arming.
Runtime app-profile automation and arbitrary user sprite packs remain outside this release. No fake enabled UI controls.

## BLE management (Hub 1.4 / R3D, output preservation in R3E)

HELLO advertises `ble_management=1` and `ble_manage_idle_ms=15000`. Management
access requires a bonded, encrypted connection and a device-local or USB grant.
The initial grant waits up to 60 seconds; after the first successful authorized HUB
request, 15 seconds without another successful request revoke access. Unauthorized
BLE HUB requests return LOCKED. BLE `CONTROL MANAGE 1` is always FORBIDDEN;
authorized BLE `CONTROL MANAGE 0` revokes only its own management access/lease and
reports the current `armed`, which may be 0 or 1 even on older R3D firmware.

`ble_manage` is 0 closed, 1 waiting window, or 2 authorized. Its remaining-ms field
counts down only in state 1 and is zero in states 0/2. USB MANAGE 0/1 closed output
in R3D; R3E preserves it when `preserve_output=1`. The management connection and the
USB/BLE HID output route are separate: closing Hub management does not deliberately
disconnect the HID link or change its output state. On close, the host attempts a
self-revocation ACK and notification unsubscribe. If neither is confirmed, do not
claim immediate revocation; the device's idle timeout is the fallback.

## Temporary animation preview (Hub 1.3 / R3C)

HELLO advertises `animation_preview=1`. Missing or `0` means unsupported; do not infer support from the build name. Configuration remains 224 bytes/schema 1.

`HUB <id> ANIMATE IDLE|BLINK|HAPPY|WALK|TAP|SLEEP|WAKE|STOP`

Start ACK: `HUB <id> OK demo_action=<1..7> demo_remaining_ms=6000 armed=<current>`.
STOP ACK: `HUB <id> OK demo_action=0 demo_remaining_ms=0 armed=<current>`.
STATUS includes the same two demo fields, with a decreasing remaining duration. No active demo is exactly 0/0.
Action IDs follow the listed order. Unknown actions or extra parameters return INVALID; WALK in Face mode returns INCOMPATIBLE; starting during calibration returns BUSY. Rejected commands have no side effects.

A valid start opens HOME. With `preserve_output=1`, it preserves ON/OFF and an active demo may coexist with `armed=1`; without that capability, a start closes output and its ACK/active STATUS must have `armed=0`. STOP preserves the current state in either case. The overlay lasts six seconds, uses elapsed time safely across millis rollover, and temporarily animates even when the saved everyday animation setting is off. It does not modify the bank, Flash, or physical pet/input counters, and generates no HID reports itself. The `pet_*` fields always describe real interaction state, not the overlay.

STOP, expiration, physical input, config/profile changes, navigation away from HOME, route changes and output control cancel the overlay. Cancellation never restores a previous armed state. Serial polling is not physical interaction. Disconnect alone cannot send a command after the cable is lost; the six-second deadline still ends the device overlay.

The host applies its draft by PREVIEW before a synchronized action; this is separate from ANIMATE. Explicit SAVE saves configuration, not the current visual action. On reconnect the host reads fresh capability/state, never replays a timed-out action, and never silently overwrites the bank. MEDIA/STUDY/WORK are three profiles within one bank with identical initial bindings, not automatic foreground-application modes.

## Hub 1.7.1 local recovery (no wire change)

The Hub caches a CRC-checked last device GET snapshot and a separate unsent local
draft in a versioned local workspace file. A disconnected startup labels this as
historical/local data. Connecting always reads the device; no cached PREVIEW,
SAVE, OUTPUT or connection request is replayed. Loading a retained draft requires
an explicit editor action and still sends nothing until Preview/Save is chosen.
Device SAVE verification and the independent DISPLAY settings protocol are unchanged.
