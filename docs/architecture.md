# System architecture

Desk Buddy has two host-facing paths. HID carries the selected keyboard and media actions. A separate management protocol carries settings and status between the device and Windows Hub.

## Device application

The nRF52840 samples six digital keys, a seesaw I²C encoder and a TSC2007 touch controller. A project-local SPIM2 instance drives the ILI9341 display at 8 MHz. Flash holds indexed artwork, and display work is divided into chunks so input sampling can continue between draws.

Mapped input passes through route readiness, the requested output setting and temporary suppression state. The selected route is either USB or BLE HID. Sleep and diagnostic pages suppress sending while preserving the ON/OFF choice; real route changes and explicit OFF have their own transitions. Pending actions and held-key release are handled at these boundaries.

The local DeskWire library adds bounded master waits and recovery to the peripheral path. A failed encoder read leaves the previous complete snapshot intact; loss of input readiness blocks output. The observed SDA-low startup incident motivated recovery work, but the originating slave was not identified.

## Management and the Windows app

USB CDC and encrypted, bonded BLE GATT carry line-framed management commands. BLE admission is enabled locally on the device for 60 seconds. Management has a 15-second inactivity timeout and a separate 10-second mutation-owner lease. Its lifetime is independent of Windows' HID use of the same BLE connection.

`DeskBuddy.Core` owns configuration validation, session behavior and transport lifetime. WPF presents three profiles, twelve mappings per profile, appearance previews and status. The editor distinguishes local preview, temporary device preview and a persistent save followed by readback. A serial port lease remains owned until its driver worker exits, including after a timeout.

| State | Owner and lifetime |
|---|---|
| Input bindings and appearance | Device configuration; three profiles in a 224-byte schema-1 bank |
| Display preferences | Separate 20-byte record; independent USB and battery timers |
| Output permission and management leases | Current session and explicit device transitions |
| Link readiness, power source and inputs | Current observations |
| Last-read snapshot and unsent draft | Local Hub workspace, labelled separately |

On reconnect, current device readback takes priority. The local workspace stores neither connection state nor permission to send output. It uses schema checks, CRC checks, staged writes, atomic replacement and backup recovery.

## Persistence

Device settings use inactive-slot writes with content and CRC verification. Language, mapping, display, calibration and bond records have separate purposes. A lower-priority storage worker serializes preference writes; the controlled-reboot guard waits behind pending callback work and holds the filesystem lock through flush/reset. The verified cases cover controlled saves and software reboot. Arbitrary physical power cuts have not been established as safe.

## Display and power

The assembled prototype connects TFT LITE to Arduino D11 / nRF P0.06. LOW turns the backlight off; releasing the pin to INPUT uses the module's pull-up to turn it on. The source default is `-1`, with an explicit build option for a wired D11 variant.

Each power source has its own sleep interval. A source transition starts a fresh interval, while read-only Hub polling leaves idle time alone. The first physical input batch wakes the display and is consumed; release and a quiet period separate it from the next action.

With USB connected, the BAT ADC sees a charger node whose voltage cannot establish battery presence. The screen therefore shows USB power and battery unknown. Battery-only operation uses fresh samples for a coarse voltage estimate. There is no fuel gauge or measured runtime model.

[Engineering decisions](engineering-notes.md) · [Protocol reference](../hub/PROTOCOL.md) · [Validation](validation.md)
