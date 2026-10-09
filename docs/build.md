# Build and host checks

This source snapshot contains R3H firmware and Hub 1.7.1. Compiled font and companion graphics are included. Commands run from the repository root; the normal build and host-check commands below do not upload firmware or open a hardware transport.

## Firmware

The recorded toolchain uses Arduino CLI 1.5.1 and Adafruit nRF52 BSP 1.7.0, with the BSP's Bluefruit 0.21.0 and TinyUSB 3.6.0. The board is Feather nRF52840 Express with S140 v6.

```text
arduino-cli core update-index --additional-urls https://adafruit.github.io/arduino-board-index/package_adafruit_index.json
arduino-cli core install adafruit:nrf52@1.7.0 --additional-urls https://adafruit.github.io/arduino-board-index/package_adafruit_index.json
arduino-cli lib update-index
arduino-cli lib install --no-deps "Adafruit BusIO@1.17.4" "Adafruit GFX Library@1.12.6" "Adafruit ILI9341@1.6.4" "Adafruit TSC2007@1.1.2" "Adafruit seesaw Library@1.7.9" "Adafruit SH110X@2.1.15" "Adafruit STMPE610@1.1.6" "Adafruit TouchScreen@1.1.6" "Adafruit ST7735 and ST7789 Library@1.11.0" "SD@1.3.0"
python scripts/build.py
```

The wrapper selects the project-local DeskWire library and accepts `--cli`, `--config` and `--build-dir`. Its equivalent default compile is:

```text
arduino-cli compile --fqbn adafruit:nrf52:feather52840:softdevice=s140v6,debug=l0 --library firmware/libraries/DeskWire --output-dir build/arduino/artifacts firmware/DeskController
```

Backlight GPIO control defaults to `-1`. The assembled prototype's TFT LITE wire is connected to Arduino D11 / P0.06; `python scripts/build.py --backlight-pin 11` selects that variant. Other wiring needs its own pin review. Pin and peripheral definitions are in [AppConfig.h](../firmware/DeskController/AppConfig.h).

The D11 variant's recorded footprint and hashes are in [validation.md](validation.md). Toolchain servicing and build paths can change output identity. Downgrading to R3G requires first saving both display timers to its supported values: 0, 30, 60 or 300 seconds.

## Windows Hub

Use Windows with .NET SDK 10.0.401; `global.json` allows servicing patches within its feature band. The app targets `net10.0-windows10.0.19041.0`, publishes for `win-x64` and restores `System.IO.Ports` 10.0.0.

```text
dotnet run --project hub/DeskBuddy.Core.Tests -c Release --artifacts-path build/dotnet -- --validate-presets examples/presets
dotnet publish hub/DeskBuddy.App/DeskBuddy.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --artifacts-path build/dotnet -o build/hub
build/hub/DeskBuddy.Hub.exe --render-smoke build/render-smoke
```

Core tests use controlled transports and workers. `--render-smoke` uses an offline demonstration peer and then exits. The separate `--device-smoke` option accesses hardware and is outside this sequence. The published app is self-contained but is not thereby signed; local application-control policy still applies.

The App project embeds both `assets/companions/r2/compiled` and `r3/compiled` by relative path. `default-bank.hex` in Core.Tests is a text configuration fixture. Four shipped presets derive from project defaults. The R3 recorded result, including those presets, is 43 groups / 6,180 assertions; [stage-validation.json](../evidence/stage-validation.json) identifies that run. [tests/LAST-RUN.json](../tests/LAST-RUN.json) is retained R2 history.

## Firmware fixtures

Python 3.12 and a C++17 compiler are used for these checks; the recorded host used Zig 0.13.0.

```text
python scripts/run_firmware_host_checks.py --cxx zig
python scripts/check_display_timeouts.py --cxx zig
```

The first command compiles four selected fixtures by default; `--run` explicitly requests native execution. `--output-dir` redirects generated files. The display-timeout command evaluates state checks at compile time with synthetic timestamps. The existing records are compile-only and do not cover physical flash timing, I²C, Windows HID behavior or elapsed long-duration trials.

## Optional asset rebuilds

Normal builds use the checked-in graphics. Rebuilding them from their active atlases requires Pillow 12.3.0 and updates firmware data and asset manifests:

```text
python -m pip install Pillow==12.3.0
python scripts/compile-companion-art.py --emit-firmware
python scripts/compile-companion-portraits.py --emit-firmware
```

Maps and source PNGs remain under `assets/companions/`. The checked-in font has its own OFL notice. The [component notices](THIRD_PARTY.md) cover those assets and dependencies.

## Optional Windows observation tools

The `desk-audio-observe.ps1`, `desk-raw-hid-observe.ps1` and `desk-hid-parse-capture.ps1` scripts under `scripts/` collect or interpret host observations. They are separate from the offline checks. The parser also reads the currently connected device's HID descriptor. Captures can contain endpoint/device identifiers, so their output belongs outside the shared source package. In the retained source verification these scripts were parsed, not executed.

[CAD build](../cad/README.md) · [Validation](validation.md)
