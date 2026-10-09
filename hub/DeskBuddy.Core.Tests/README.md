# Host-only core tests

No NuGet packages, serial ports, WPF or hardware are used. Run from the repository root:

```powershell
dotnet run --project hub/DeskBuddy.Core.Tests/DeskBuddy.Core.Tests.csproj
```

The console runner exits nonzero on a failed assertion. It exercises the actual Core classes against an in-memory transport, and uses a temporary flat folder for atomic-file tests. On Windows it holds an existing target open with FileShare.None to verify that a failed replacement preserves the original bytes. No configuration is sent to a physical device.

Coverage: exact 224-byte encoding; independent canonical defaults in `default-bank.hex`; invalid schema, reserved fields, labels, HEX and bindings; CRC32 standard vector; strict public JSON import; atomic save success/failure; bounded line framing; malformed/duplicate reply fields; typed legacy STATUS; exact-ID ACK matching; unrelated and late responses; total write+reply deadlines; cancellation; disconnect of active/queued requests; explicit reconnect with no replay; stale revisions; save and readback failures; output denial; legacy fallback; unsupported future protocol; and monotonic freshness despite wall-clock rollback.

Hub 1.1 adds all 96 theme/face pairs under each of three home modes (288 combinations), independent wire-offset and JSON roundtrips, unknown IDs, original default-byte stability, strict HELLO appearance/mode counts, original 4/4/1 fallback, optional partial counts, unsupported inactive-profile guards before transmission, old-peer support for original IDs, and capability re-reading on upgrade/downgrade reconnect. Old Hub R1 evidence remains an immutable earlier checkpoint; current results are written separately under `outputs/hub-r2/core` by the release workflow.

When the four delivered preset files are available, append `-- --validate-presets examples/presets` to the run command. This invokes the actual `LocalConfigurationStore.Load`, checks each documented appearance/mode combination, verifies unchanged default mappings and checks the old-peer compatibility boundary. It never sends a preset to a device.

The canonical fixture was constructed separately from the C# encoder using the documented offsets and default values. It is an interoperability reference for firmware GET, not a hardware test result. Fake-device tests do not establish COM behavior, live firmware correctness, filesystem power-loss safety or actual media-player effects.

R3E compatibility adds strict independent `preserve_output`/`pet_input_feedback` capabilities; legacy and new ON/OFF configuration matrices; actual ACK plus GET/STATUS; same-route versus changed-route behavior; HOME/language/USB-management preservation; all seven animations while ON/OFF; physical OFF between ACK and readback; malformed capabilities/armed ACKs; BUSY without retry; disconnect after mutation; upgrade/downgrade capability refresh; and independent bounded physical-pet telemetry. No test implicitly restores output. The release-specific host run is recorded in `test-results-r3e.txt` and `test-results-r3e.json`; it does not establish live firmware or GUI acceptance.

R3F adds strict optional `preserve_page_output` and `hid_suppressed`; old R3E page requirements remain covered unchanged. New matrices cover ON/OFF navigation, capability-gated page tokens, no remote calibration, ACK-to-STATUS physical OFF, malformed ACKs, disconnect/downgrade, no hidden arm and unchanged bank bytes. The runner also compiles the actual hardware-free App DemoTransport and output-presentation helper to check both languages, paused versus switch state, and the explicit-enable predicate. These linked sources do not use WPF or hardware. Results for Hub 1.5.1 are retained in `outputs/hub-r6-1/core`; GUI rendering and real firmware acceptance remain separate.

## Packaged evidence boundary

The `test-results*.txt` files are retained historical R1/R3E checkpoints, including a historical failed attempt; they are not current-run acceptance. Machine-specific source paths and synthetic test IDs are redacted. The matching Hub 1.7.1 release ran 42 groups / 6,146 assertions; this candidate reran those plus delivered presets (43 groups / 6,180 assertions). Current results are summarized in the repository evidence ledger. References to `outputs/` and omitted JSON records above identify the private development history, not shipped files.
