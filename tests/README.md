# Selected firmware host checks

These four current-source fixtures cover controller input helpers, battery-estimate policy, configuration validation/storage API fakes, and the bounded USB console. Run `python scripts/run_firmware_host_checks.py --cxx zig` to compile them. Compilation is the default; `--run` explicitly executes fixtures only where the operating system permits it.

The filesystem fake is synthetic and in-memory. It does not reproduce the real littlefs allocator, flash erase/program timing, SoftDevice, Windows drivers, I2C peripherals or power loss. The USB fixture compiles the actual `UsbConsole.cpp` against fake CDC events/FIFOs.

This is a selected public suite, not every historical project test. New native fixtures were compile-only on the original development host because of application-control restrictions. Do not report compilation as runtime assertions. The separately executed .NET Hub tests live in `hub/DeskBuddy.Core.Tests`.

## Display sleep boundaries

`python scripts/check_display_timeouts.py --cxx zig` compiles `r3h_custom_timeout_test.cpp` under C++14. Static assertions evaluate the actual state methods with synthetic timestamps, covering the supported range, all minute deadlines, wrap-around, power-source changes, polling and first-wake suppression. It does not execute a native program or wait for real long intervals. The older `LAST-RUN.json` is a historical R2 checkpoint; current candidate results are identified separately.
