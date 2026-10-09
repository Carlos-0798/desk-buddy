# Third-party components and asset provenance

No project-wide license is assigned by this source package. The terms below apply to the respective materials, independently of a later project license choice.

## Bitmap font

`firmware/DeskController/UiFont.cpp` contains **DeskUiBitmap16**, a derivative bitmap subset of Noto Sans SC. The upstream notice is:

> Copyright 2014-2021 Adobe (http://www.adobe.com/), with Reserved Font Name 'Source'

The glyph data remain under **SIL Open Font License 1.1**. The full notice and license are preserved at the start of `UiFont.cpp` and in `firmware/DeskController/FONT-OFL.txt`. The derivative name does not use the reserved font name. The original TTF is not bundled; normal compilation uses the committed bitmap table.

Sources: [Noto Sans SC family](https://github.com/google/fonts/tree/main/ofl/notosanssc) and [its OFL notice](https://github.com/google/fonts/blob/main/ofl/notosanssc/OFL.txt).

## Companion graphics

Original atlases were generated for this project; they are not copied game sprites. Project scripts extract complete alpha-connected silhouettes, preserve a common scale/ground line, quantize to shared RGB565 palettes and verify decoded PNG/C++ data. Dedicated portraits and full-body frames are separate assets. Original active PNGs, mappings and generated manifests are included as provenance.

The generation/conversion history and general technique references are documented in `assets/companions/r2/README.txt` and `r3/README.txt`. The reference artists' assets are not included. Generated drawings should not be described as manually pixel-painted originals. This notice does not assign a license to project-created assets or guarantee copyright protection for generated imagery.

## External dependencies

The dependencies below are installed separately, except the narrow project-local `firmware/libraries/DeskWire` fork described here. Precompiled BSP libraries and toolchains are not vendored. DeskWire derives from the Adafruit nRF52 BSP 1.7.0 Wire implementation; original Arduino LLC / Sandeep Mistry copyright and LGPL 2.1-or-later notices remain in the source. Project changes bound foreground master waits and failure cleanup; the installed BSP is not modified. See its README and source notices. Consult all component-level notices before distributing binaries.

| Dependency | Upstream |
| --- | --- |
| Adafruit nRF52 BSP 1.7.0, including Bluefruit, TinyUSB, FreeRTOS, littlefs, nrfx and nRFCrypto | [Pinned BSP](https://github.com/adafruit/Adafruit_nRF52_Arduino/tree/1.7.0) |
| Adafruit GFX | [Repository](https://github.com/adafruit/Adafruit-GFX-Library) |
| Adafruit ILI9341 | [Repository](https://github.com/adafruit/Adafruit_ILI9341) |
| Adafruit TSC2007 | [Repository](https://github.com/adafruit/Adafruit_TSC2007) |
| Adafruit seesaw | [Repository](https://github.com/adafruit/Adafruit_Seesaw) |
| Adafruit BusIO | [Repository](https://github.com/adafruit/Adafruit_BusIO) |
| SH110X, STMPE610, TouchScreen, ST7735/ST7789 and SD | Versions in [build.md](build.md) |
| .NET SDK/runtime and System.IO.Ports | [dotnet/runtime](https://github.com/dotnet/runtime) |
| WPF | [dotnet/wpf](https://github.com/dotnet/wpf) |
| Windows SDK / WinRT APIs | [Microsoft documentation](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-enhance) |
| Python / Pillow | [Python](https://www.python.org/), [Pillow](https://github.com/python-pillow/Pillow) |
| Arduino CLI | [Repository](https://github.com/arduino/arduino-cli) |
| Host C++ compiler, tested with Zig 0.13.0 | [Zig](https://ziglang.org/) |

The BSP top-level license is LGPL 2.1 or later; bundled components carry their own notices. This document is not a replacement for those notices. `BatteryGauge.h` cites the BSP voltage-estimation example: it is a coarse heuristic, not a calibrated state-of-charge or charger sensor.

## Tests, scripts and mechanical source

The included firmware test doubles implement a limited in-memory filesystem API or peripheral behavior needed by the selected fixtures. They are not copies of a real filesystem image, bond database, SoftDevice or USB stack and cannot prove those components' physical behavior. The historical Adafruit_GFX host raster helper is **not** included in this selected suite.

Windows observation scripts use documented operating-system APIs; raw captures and personal device identifiers are not bundled. Any included enclosure source is project CAD; manufacturer PCB/STEP models and their artwork are not included by this source allowlist. Optional CAD Python dependencies retain their own licenses and should be installed from the mechanical requirements file.

## Physical photographs

The three `assets/prototype-*.jpg` files are unmodified original photographs supplied by the project builder for this showcase. Their SHA-256 hashes and visible-state captions are recorded in `evidence/asset-provenance.json`. They are not generated illustrations or CAD renders.
