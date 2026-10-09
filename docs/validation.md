# Validation record

The device baseline is **R3H with D11 backlight control**; the desktop baseline is **Hub 1.7.1**. Software and device results below were recorded on October 5, 2026. An October 8 assembly update adds photographs and the recorded physical layout; it does not add a new firmware, Hub or closed-case functional test run.

## Recorded checks

| Revision and method | Result | Scope |
|---|---|---|
| R3H target compilation | 545,088 bytes Flash; 33,716 bytes static RAM | Compiled D11 variant; peak stack and heap use unmeasured |
| Hub 1.7.1 release host tests | 42 groups / 6,146 assertions passed | Protocol, lifecycle and model checks with controlled transports |
| Hub 1.7.1 offline rendering | 788 checks / 649 images passed | Automated layout and preview checks |
| Hub 1.7.1 workspace lifecycle | 8 isolated WPF processes / 57 checks passed | Restart, drafts, reconnect authority and cache recovery using a controlled peer |
| R3H USB management with Hub 1.7.0 Core | 42 checks passed | Settings, capability handling, readback and persistence |
| R3H BLE management with Windows transport | Separate run passed | Encrypted management path through production transport/Core code |
| R3H device timing | 30-second automatic sleep observed while polling continued | One real interval; longer configured intervals were not all observed in real time |
| R3H settings persistence | Custom values retained through software reboot | Controlled save/readback/reboot cases |
| R3G physical observation | Backlight fully off; first touch restored the screen without another action | USB-powered observation on that earlier revision |
| R6 geometry | Portable generator reproduced seven archived mesh hashes | Digital reproducibility of the original print revision; separate from physical assembly |
| R6 physical assembly, October 8 | Printed parts assembled; lid closed; powered display photographed | Assembly observation, with a documented USB-access workaround; not a new HID, power-transition or endurance test |

The [October 5 release summary](../evidence/summary.json) retains the result identities and boundaries, including the pre-print mechanical status at that date. The later [October 8 assembly record](../evidence/assembly-2026-10-08.json) supplements it. The Hub workspace run used isolated files and did not access the normal user profile or a physical device. Hub 1.7.1's local workspace change was not a new R3H hardware regression.

Earlier prototype observations also covered encoder detents/presses, key counters, USB/BLE volume and mute, battery cold start and calibration retention. R3D8 menu behavior was accepted across USB HID, USB-powered BLE and battery BLE. These observations belong to their original revisions.

## October 8 enclosure observation

The [as-built record](as-built.md) contains five original photographs. The front photograph shows the closed enclosure with the display powered and reporting its USB route and ready state. A still image does not establish that a host received an input report.

I recorded the installed button layout as left K6/K5/K4 and right K3/K2/K1, viewed from the front. I also recorded the USB opening mismatch and the final workaround: a 180-degree display rotation, an internal USB connection and a cable exit beside the battery. The four internal images show the earlier assembly before that rotation and reroute. There is no photograph of the final internal cable path and no measured cable-retention or clearance result.

## Source package verification

The [R3 source verification record](../evidence/stage-validation.json) includes a separate firmware rebuild matching the delivered R3H image and a Hub rebuild. Its Core run passed **43 groups / 6,180 assertions**, including the four shipped preset validations in addition to the release suite. Its rebuilt Hub passed its own 788-check offline render run. The eight-process workspace test was performed on the delivered executable, not repeated on that rebuild.

Four selected firmware host fixtures compiled. The display-state checks were evaluated by the compiler with synthetic timestamps, including boundaries and wraparound. There were **zero native firmware test executions** in those recorded runs and no twelve-hour physical timeout trial.

| Artifact | SHA-256 |
|---|---|
| R3H D11 firmware image | `3c5a91cf43779c7a88e8e8f9f8940d50bb3f1d9a19bde44aea19b79214c3bf7e` |
| Delivered Hub 1.7.1 executable | `50a009ddb8f05607528ef4ccddd9d77fde6cab38f11204e35c57254acfb9b0cd` |
| R3 source-package Hub rebuild | `09d6c07e21bacc422a9dd0355a222cf82afb2c43c28b8bab37006fe5838c7f5e` |

The older [tests/LAST-RUN.json](../tests/LAST-RUN.json) describes R3D9 / Hub 1.4.0. Its 29-group result is historical, separate from the R3H / Hub 1.7.1 source verification above.

## Unresolved observations

A prior R3G run briefly reported the encoder unavailable and then recovered. The exact failed transaction is unknown. During the R3H upgrade, an initial English-language readback differed from an earlier Chinese snapshot; the source of that difference was not established. Explicitly saving Chinese and two subsequent software reboots passed.

A startup trace localized one stall to I²C initialization with SDA low; recovery clocks released it. That observation does not explain every earlier white-screen incident. Older Python BLE fixtures also had cancelled or object-closed first writes, separately from the later passing Windows production-transport checks. An initial upload attempt failed before a subsequently enumerated port completed application upload.

## Work still open

The enclosure is printed and assembled; closed-case controls, power transitions, cable retention and harness clearance still need recorded checks. The original CAD USB opening needs correction before treating it as a verified direct-access design. The physical wake matrix still needs battery-powered button and encoder trials, and extended idle intervals need real-time observation. Battery endurance, charging current and precise state of charge have not been measured. The full native Hub GUI matrix, including Windows sleep/wake and concurrent management/HID, remains incomplete. Controlled storage results do not establish arbitrary power-cut safety.

[Build and host commands](build.md) · [Engineering notes](engineering-notes.md)
