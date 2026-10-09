# DeskWire: bounded nRF52 master I2C

This project-local fork starts from Adafruit nRF52 BSP **1.7.0** `libraries/Wire`
(`Wire.h`, `Wire_nRF52.cpp`, and `keywords.txt`). The original Arduino LLC /
Sandeep Mistry copyright notices and **LGPL 2.1-or-later** licensing remain in
the copied source. Project modifications are identified in the source. The
installed Arduino BSP is not changed.

Build Desk Buddy with `arduino-cli compile --library <absolute-path-to-this-folder>`.
The application can require `DESK_WIRE_BOUNDED_MASTER == 1` to reject an accidental
build with the unbounded BSP library. Check the compiler's selected library path;
placing this directory nearby alone does not select it.

## Behavioral changes

* A single 20,000 us deadline covers all START, LAST-byte, STOP and SUSPEND waits
  of each master transmit or receive. `setTransactionTimeoutUs()` accepts 1 us
  through 1,000,000 us; zero is clamped to 1 us, never disabling the deadline.
* On a deadline expiry, a STOP attempt has a separate maximum 1,000 us budget.
  The peripheral is then disabled, task event/error state and DMA lengths are
  cleared, software buffers are discarded, and the peripheral is re-enabled
  with its existing pins and clock. No START/retry/replay is issued by cleanup.
* `endTransmission()` returns existing generic error **4** on timeout;
  `requestFrom()` returns **0** and exposes no partial bytes on bus error/timeout.
  Address/data NACK values **2/3** retain their existing meaning when STOP succeeds.
* `timeoutCount()` is a saturating cumulative count. `lastTransactionTimedOut()`
  describes only the most recent transmit/receive. Both are owned by the calling
  task; this fork does not make concurrent users of `Wire` safe.
* Receive requests are checked against the 64-byte BSP ring buffer (maximum 63
  bytes). This prevents DMA overflow and ambiguous full-ring indices. Existing
  Adafruit BusIO reads are at most 32 bytes.
* Slave interrupt handling and board pin selection remain the BSP implementation.

Deadlines bound active foreground polling, excluding higher-priority interrupt /
RTOS preemption. The fallback does **not** clock-clear a physically stuck slave.
The controller should mark failed peripherals unavailable, keep HID off when
required inputs are missing, and retain USB diagnostics. Re-initialization and
any bounded open-drain bus recovery belong to the controller, not this library.

## Verification boundary

Run `python tests/r3f_wire_check.py`. This executes source/provenance checks and
asks the ARM target compiler to evaluate deadline `static_assert` cases and build
a small sketch against this exact library. It does not execute the sketch or
access a serial port. Hardware stalled-bus recovery remains a separate test.
