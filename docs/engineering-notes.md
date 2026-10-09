# Engineering notes

These are four points where building and using Desk Buddy revealed a problem that the initial design did not handle well. The software observations retain the revision on which I checked them. The enclosure observation describes the first physical assembly and its temporary cable arrangement.

## Opening a menu changed the controls

I could enable output on the home screen, return to the menu, and find that the device no longer behaved as I expected. Two things were coupled: entering a page could reset the output setting, and dispatching physical media actions depended on being on the home page.

I wanted navigation to leave the user's output choice alone. I split that choice from the conditions that allow a report to leave the device. Ordinary pages keep ON or OFF intact. Diagnostic and activity screens can suppress sending without rewriting the choice; pending actions are cleared and the physical inputs are resampled before normal operation resumes.

The useful checks were specific transitions: ON through Back and Menu, the same sequence while OFF, and knob/mute actions while a menu remained visible. I accepted that behavior on the R3D8 prototype with USB HID, BLE while USB-powered, and BLE on battery. Those results establish the earlier correction; they are not a complete physical regression of every later firmware change.

This separation also made a connected-but-unresponsive device easier to investigate. A BLE link, encryption, notification subscriptions and permission to send are distinct parts of the path. In earlier sessions I found missing subscriptions in one case and output OFF in another. The original reason the subscriptions disappeared was not uniquely identified.

## The screen was dark, but the backlight was still on

The first screen-rest behavior changed the image without shutting down the light behind it. I added the TFT LITE connection to Arduino D11 so the firmware could control the backlight itself.

That exposed a second problem: the input used to wake the display could also execute its assigned action. Ignoring only one button would leave simultaneous events or a held touch unresolved. I made the first sampled input batch belong to waking, cleared queued HID work, and kept input isolated until release and a short quiet interval. The requested output setting survives this transition.

On the USB-powered R3G build, I observed that the backlight went completely off and one touch restored the home screen without triggering another control. R3H then extended those separate USB and battery timeout settings with custom whole-minute values. Device checks covered saving and reading those values, retention through software reboot, and a real 30-second idle timeout despite continued status polling.

The longer intervals were checked through the state logic and stored values, not by waiting through every duration. Battery-only physical button/encoder wake and battery endurance remain open.

## Reopening the Hub looked like losing settings

After closing and reopening the Hub, the disconnected editor showed its defaults. Reconnecting restored the settings from the device. That observation pointed to the desktop's startup state; it did not show that device storage had lost data.

I gave the editor two separate records: the last configuration read from the device and an unsent local draft. The app labels the source being shown. Reconnecting performs a fresh read before editing, and a retained draft requires an explicit load. The cache does not restore connection status, permissions or the output switch.

For persistence, I used validated records, same-directory staging, atomic replacement and a recovery copy. I exercised close/reopen behavior, a different configuration after reconnect, damaged cache files and write failures in eight isolated WPF processes. The 57 checks passed using a controlled peer and separate test directories. That run covered desktop lifecycle behavior, without adding a new hardware acceptance result.

A snapshot means the last configuration returned by GET. A temporary device preview can appear in GET before it is saved to flash, so the label does not imply persistent storage. Everyday use of the new Hub version still needs a fresh connection and observation in the normal device environment.

## The connector opening was on the wrong side

I had checked the case against nominal board envelopes, mounting points and cable allowances. During the first printed assembly, the actual Feather/TFT orientation put the nRF52840 board on the right side rather than the left-side assumption behind my access opening. The components fit inside the case, but that did not mean the USB plug could reach the connector through the intended opening.

For this prototype, I rotated the display assembly 180 degrees and left a USB cable connected inside. It exits through the opening near the battery, so I now disconnect it at the computer end. I completed assembly and closed the lid. The powered front photograph shows the resulting prototype; the internal photographs predate that final cable arrangement.

The mistake was an interface-location check, not simply insufficient enclosure volume. Before a future print, I would register the fully mated stack against the lid in its actual orientation, then check the connector centerline, plug body and bend path against the opening. I would also include cable retention in that review. The current workaround has not established strain relief, pull resistance or long-term connector reliability.

Mounting the buttons revealed a second documentation issue: the installed order differs from the intended order. I recorded the front-facing physical layout as left K6/K5/K4 and right K3/K2/K1, without renaming the electrical inputs. Separating physical position, input identity and the configurable action prevents a later software change from masking a wiring or assembly assumption.

The [as-built record](as-built.md) carries that layout and the dated photographs. The [mechanical notes](mechanical-design.md) preserve the original CAD dimensions, the USB-access discrepancy and the remaining checks.

The [validation record](validation.md) collects the software, build and device evidence by method and revision.
