using DeskBuddy.App;
using DeskBuddy.Core;

internal static partial class Program
{
    private const string DisplayFields = "display_asleep=0 display_usb_s=300 display_battery_s=60 display_saved=0 display_backlight=0 display_wake_count=0 display_idle_ms=0";
    private static void DisplayParsing()
    {
        var old = ParseCapabilities();
        Check(!old.SupportsDisplaySleep && !old.BacklightControl && !old.SupportsSleepBinding, "old peer gets no display capabilities from version name");
        foreach (bool backlight in new[] { false, true }) foreach (bool binding in new[] { false, true })
        {
            var cap = ParseCapabilities($" display_sleep=1 backlight_control={(backlight ? 1 : 0)} sleep_binding={(binding ? 1 : 0)}");
            Check(cap.SupportsDisplaySleep && cap.BacklightControl == backlight && cap.SupportsSleepBinding == binding, "three explicit display capabilities remain independent");
        }
        foreach (string field in new[] { "display_sleep", "backlight_control", "sleep_binding" })
            foreach (string value in new[] { "2", "-1", "01", "true", "+1", "" })
                Throws<FormatException>(() => ParseCapabilities(" " + field + "=" + value), "strict display capability Boolean");
        foreach (string invalid in new[] { " display_sleep=1", " backlight_control=1", " sleep_binding=1", " display_sleep=0 backlight_control=1 sleep_binding=0", " display_sleep=1 backlight_control=0 sleep_binding=1 display_sleep=1" })
            Throws<FormatException>(() => ParseCapabilities(invalid), "incomplete or contradictory display capability rejected");
        foreach (uint usb in new uint[] { 0, 30, 60, 300 }) foreach (uint battery in new uint[] { 0, 30, 60, 300 })
            Check(HubProtocol.BuildRequest(23, $"DISPLAY SET {usb} {battery}") == $"HUB 23 DISPLAY SET {usb} {battery}", "exact display SET grammar");
        foreach (string verb in new[] { "GET", "SLEEP", "WAKE" }) Check(HubProtocol.BuildRequest(1, "DISPLAY " + verb).EndsWith(verb), "display simple grammar");
        foreach (string command in new[] { "DISPLAY", "DISPLAY GET 1", "DISPLAY SET 60", "DISPLAY SET 60 60 1", "DISPLAY SET 01 60", "DISPLAY SET 601 60", "DISPLAY SET -1 60", "DISPLAY SET 60 1", "DISPLAY SLEEP 1", "DISPLAY OFF", "DISPLAY set 0 0" })
            Throws<FormatException>(() => HubProtocol.BuildRequest(1, command), "bad display command rejected locally");
        var valid = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields() + " " + DisplayFields);
        Check(valid.Display is { Asleep: false, Saved: false, UsbTimeoutSeconds: 300, BatteryTimeoutSeconds: 60, BacklightControlled: false, IdleMilliseconds: 0 }, "display defaults are observations, not bank fields");
        foreach (string field in DisplayFields.Split(' ').Select(s => s.Split('=')[0]))
        {
            string missing = string.Join(' ', DisplayFields.Split(' ').Where(s => !s.StartsWith(field + "=", StringComparison.Ordinal)));
            Throws<FormatException>(() => LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields() + " " + missing), "partial display status rejected: " + field);
        }
        foreach (string invalid in new[] { "display_asleep=2", "display_saved=true", "display_backlight=01", "display_usb_s=10", "display_battery_s=601", "display_wake_count=-1", "display_idle_ms=4294967296" })
        {
            string key = invalid.Split('=')[0]; string replaced = string.Join(' ', DisplayFields.Split(' ').Select(s => s.StartsWith(key + "=", StringComparison.Ordinal) ? invalid : s));
            Throws<FormatException>(() => LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields() + " " + replaced), "invalid display field rejected");
        }
        var bank = HubDefaults.CreateBank(); string baseline = BankCodec.ToHex(bank);
        bank.Profiles[2].Bindings[11] = new(BindingKind.Local, 5);
        Check(BankCodec.FromHex(BankCodec.ToHex(bank)).Profiles[2].Bindings[11] == new HubBinding(BindingKind.Local, 5), "local sleep action roundtrips in existing wire field");
        Check(BankCodec.ToHex(LocalConfigurationStore.ImportJson(LocalConfigurationStore.ExportJson(bank))) == BankCodec.ToHex(bank), "offline JSON preserves sleep binding without adding display timeout fields");
        Check(!old.SupportsBank(bank) && !ParseCapabilities(" display_sleep=1 backlight_control=0 sleep_binding=0").SupportsBank(bank), "sleep binding requires its own capability in every profile");
        Check(ParseCapabilities(" display_sleep=1 backlight_control=0 sleep_binding=1").SupportsBank(bank), "supported sleep bank accepted");
        Check(baseline == File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "default-bank.hex")).Trim(), "new defaults still byte-identical");
        var asleep = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields(armed: true) + " hid_suppressed=1 " + DisplayFields.Replace("display_asleep=0", "display_asleep=1"));
        Check(OutputStatePresentation.Detail(asleep, true, true).Contains("休息") && OutputStatePresentation.Detail(asleep, true, false).Contains("Screen resting"), "screen suppression is not incorrectly described as a test page");
        Check(!OutputStatePresentation.CanEnable(asleep, null, true, false), "suppressed wake input cannot enable output from App");
    }

    private static async Task DisplayOperations()
    {
        foreach (bool backlight in new[] { false, true })
        {
            var peer = new DisplayPeer(new DemoTransport(backlightControl: backlight) { DelayMilliseconds = 0 });
            await using var client = new HubClient(peer); await client.ConnectAsync();
            Check(peer.Sent.Select(CommandText).SequenceEqual(new[] { "HELLO", "GET", "STATUS" }), "connection remains read-only, no automatic display SET or WAKE");
            string bank = BankCodec.ToHex(client.Configuration!.Bank); uint revision = client.Configuration.Revision;
            foreach (bool armed in new[] { false, true })
            {
                await client.ControlAsync(armed ? "OUTPUT 1" : "OUTPUT 0"); int start = peer.Sent.Count;
                foreach (uint usb in new uint[] { 0, 30, 60, 300 }) foreach (uint battery in new uint[] { 0, 30, 60, 300 })
                {
                    var state = await client.SetDisplayAsync(usb, battery);
                    Check(state.Display is { Saved: true } && state.Display.UsbTimeoutSeconds == usb && state.Display.BatteryTimeoutSeconds == battery && state.Armed == armed, "all timeout pairs save/GET/status and preserve observed ON/OFF");
                }
                var sleep = await client.SleepDisplayAsync();
                Check(sleep.Display!.Asleep && sleep.Armed == armed && sleep.HidSuppressed, "rest preserves switch, pauses HID and reports actual backlight capability");
                uint wakes = sleep.Display.WakeCount;
                for (int i = 0; i < 3; i++) Check((await client.ReadDisplayAsync()).Display!.Asleep, "poll/GET never wakes resting display");
                var wake = await client.WakeDisplayAsync();
                Check(!wake.Display!.Asleep && wake.Armed == armed && wake.HidSuppressed && wake.Display.WakeCount == wakes + 1 && wake.Display.BacklightControlled == backlight, "wake preserves switch and reports actual count/capability plus input isolation");
                Check(!peer.Sent.Skip(start).Any(s => CommandText(s) is "CONTROL OUTPUT 1" or "SAVE" || CommandText(s).StartsWith("SAVE ", StringComparison.Ordinal)), "display operations never send hidden rearm or bank SAVE");
                Check(BankCodec.ToHex(client.Configuration!.Bank) == bank && client.Configuration.Revision == revision, "screen settings leave configuration revision/preview bank untouched");
                await Task.Delay(220);
            }
            await client.SetDisplayAsync(300, 30);
            peer.Demo.UsbPowered = true; peer.Demo.SimulateDisplayIdleFor(TimeSpan.FromSeconds(40));
            Check(!(await client.RefreshStatusAsync()).Display!.Asleep, "USB uses its independent 300-second timer");
            peer.Demo.UsbPowered = false;
            Check((await client.RefreshStatusAsync()).Display!.Asleep, "battery uses 30-second timer without poll refreshing activity");
            uint presses = client.Status!.KeyPresses![0]; uint interactions = client.Status.GetUInt32("pet_interactions")!.Value;
            peer.Demo.SetKey(0, true); var physicalWake = await client.RefreshStatusAsync();
            Check(!physicalWake.Display!.Asleep && physicalWake.HidSuppressed && physicalWake.KeyPresses![0] == presses && physicalWake.GetUInt32("pet_interactions") == interactions, "first physical wake input is consumed and held-key isolation stays paused");
            peer.Demo.SetKey(0, false); await Task.Delay(220); peer.Demo.SetKey(0, true);
            Check((await client.RefreshStatusAsync()).KeyPresses![0] == presses + 1, "only a later press becomes a normal input"); peer.Demo.SetKey(0, false);
            await client.SleepDisplayAsync(); uint cw = client.Status!.GetUInt32("cw")!.Value;
            peer.Demo.Turn(1); Check(!(await client.RefreshStatusAsync()).Display!.Asleep && client.Status.GetUInt32("cw") == cw, "first knob movement wakes without action");
            await client.SleepDisplayAsync(); peer.Demo.TapHome(); Check(!(await client.RefreshStatusAsync()).Display!.Asleep, "first touch wakes screen only");
            await Task.Delay(220); await client.ControlAsync("OUTPUT 1");
            peer.Transform = (line, command) => command == "STATUS" ? line.Replace("armed=1", "armed=0", StringComparison.Ordinal) : line;
            Check(!(await client.SleepDisplayAsync()).Armed, "ACK ON followed by physical OFF returns actual OFF and never restores ON"); peer.Transform = null;
        }
        var boundPeer = new DisplayPeer(new DemoTransport() { DelayMilliseconds = 0 });
        await using var boundClient = new HubClient(boundPeer); await boundClient.ConnectAsync();
        var mapped = boundClient.Configuration!.Bank; mapped.Profiles[0].Bindings[5] = new(BindingKind.Local, 5);
        await boundClient.PreviewAsync(mapped, boundClient.Configuration.Revision); boundPeer.Demo.SetKey(5, true);
        Check((await boundClient.RefreshStatusAsync()).Display!.Asleep, "actual App demo interprets local sleep binding");
        await AnimationError("BUSY", () => boundClient.ControlAsync("OUTPUT 1"));
        Check((await boundClient.ReadDisplayAsync()) is { Armed: false, HidSuppressed: true, Display.Asleep: false }, "explicit arm during sleep wakes but remains OFF during isolation");
    }

    private static async Task DisplayFailures()
    {
        foreach (bool advertise in new[] { false, true })
        {
            var old = new DisplayPeer(new DemoTransport(advertiseCapabilities: advertise, displaySleep: false) { DelayMilliseconds = 0 });
            await using var client = new HubClient(old); await client.ConnectAsync(); int before = old.Sent.Count;
            await AnimationError("UNSUPPORTED", () => client.ReadDisplayAsync()); await AnimationError("UNSUPPORTED", () => client.SetDisplayAsync(30, 60));
            await AnimationError("UNSUPPORTED", () => client.SleepDisplayAsync()); await AnimationError("UNSUPPORTED", () => client.WakeDisplayAsync());
            var bank = client.Configuration!.Bank; bank.Profiles[2].Bindings[11] = new(BindingKind.Local, 5);
            await AnimationError("UNSUPPORTED_BINDING", () => client.PreviewAsync(bank, client.Configuration!.Revision));
            await AnimationError("UNSUPPORTED_BINDING", () => client.SaveAsync(bank, client.Configuration!.Revision));
            Check(old.Sent.Count == before, "old peer rejects display commands and any-profile sleep binding before writes");
            await client.PreviewAsync(HubDefaults.CreateBank(), client.Configuration!.Revision); Check(client.Configuration!.Preview, "old bank still works on old device");
        }
        var peer = new DisplayPeer(new DemoTransport() { DelayMilliseconds = 0 });
        await using var modern = new HubClient(peer, TimeSpan.FromMilliseconds(80)); await modern.ConnectAsync();
        int initial = peer.Sent.Count;
        foreach (uint bad in new uint[] { 1, 29, 31, 59, 61, 301, uint.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => modern.SetDisplayAsync(bad, 60), "bad USB timeout rejected before await");
            Throws<ArgumentOutOfRangeException>(() => modern.SetDisplayAsync(300, bad), "bad battery timeout rejected before await");
        }
        Check(peer.Sent.Count == initial, "invalid timeout arguments never reach transport");
        foreach (string error in new[] { "BUSY", "SAVE_FAILED" })
        {
            peer.Demo.RejectNextDisplay = error; int before = peer.Sent.Count;
            await AnimationError(error, () => modern.SetDisplayAsync(30, 30));
            Check(peer.Sent.Count == before + 1 && modern.Status is null, "save failure is not silently retried or reported saved");
            Check((await modern.ReadDisplayAsync()).Display is { Saved: false, UsbTimeoutSeconds: 300, BatteryTimeoutSeconds: 60 }, "failed save leaves old settings readable");
        }
        foreach (string malformed in new[] { "display_saved=2", "display_usb_s=10", "display_backlight=1", "display_wake_count=-1" })
        {
            string key = malformed.Split('=')[0];
            peer.Transform = (line, command) => command.StartsWith("DISPLAY ", StringComparison.Ordinal) ? string.Join(' ', line.Split(' ').Select(s => s.StartsWith(key + "=", StringComparison.Ordinal) ? malformed : s)) : line;
            await ThrowsAsync<FormatException>(() => modern.ReadDisplayAsync(), "bad display reply is not accepted");
        }
        peer.Transform = (line, command) => command.StartsWith("DISPLAY SET", StringComparison.Ordinal) ? line.Replace("display_saved=1", "display_saved=0", StringComparison.Ordinal) : line;
        await AnimationError("SAVE_FAILED", () => modern.SetDisplayAsync(30, 30));
        peer.Transform = (line, command) => command == "DISPLAY GET" ? line.Replace("display_usb_s=30", "display_usb_s=60", StringComparison.Ordinal) : line;
        await AnimationError("READBACK_MISMATCH", () => modern.SetDisplayAsync(30, 30));
        peer.Transform = null;
        int serialized = peer.Sent.Count;
        await Task.WhenAll(modern.SetDisplayAsync(60, 300), modern.RefreshStatusAsync(), modern.SleepDisplayAsync());
        Check(peer.Sent.Skip(serialized).Select(CommandText).SequenceEqual(new[] { "DISPLAY SET 60 300", "DISPLAY GET", "STATUS", "STATUS", "DISPLAY SLEEP", "STATUS" }), "display ACK/readback sequence is one serialized operation against poll");
        peer.Transform = (line, command) => command == "DISPLAY WAKE" ? null : line;
        int timedOut = peer.Sent.Count;
        await ThrowsAsync<TimeoutException>(() => modern.WakeDisplayAsync(), "missing wake ACK times out without replay");
        Check(peer.Sent.Count == timedOut + 1 && modern.Status == null, "timeout clears live state without compensating wake/arm"); peer.Transform = null;
        await modern.ReadDisplayAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); int cancelledAt = peer.Sent.Count;
        await ThrowsAsync<OperationCanceledException>(() => modern.SetDisplayAsync(0, 0, cancelled.Token), "queued cancelled change never sends");
        Check(peer.Sent.Count == cancelledAt, "cancel leaves transport untouched");
        peer.BeforeSend = command => { if (command == "DISPLAY SLEEP") peer.Cut(); };
        await ThrowsAsync<IOException>(() => modern.SleepDisplayAsync(), "disconnect interrupts display operation");
        Check(modern.Capabilities == null && modern.Status == null, "disconnect clears display capability/status caches");
        peer.BeforeSend = null; int reconnect = peer.Sent.Count; await modern.ConnectAsync();
        Check(peer.Sent.Skip(reconnect).Select(CommandText).SequenceEqual(new[] { "HELLO", "GET", "STATUS" }), "reconnect rereads capabilities without replaying sleep/settings");
    }
    private static string CommandText(string line) => string.Join(' ', line.Split(' ').Skip(2));
}

internal sealed class DisplayPeer(DemoTransport demo) : ILineTransport
{
    public DemoTransport Demo { get; } = demo;
    public List<string> Sent { get; } = [];
    public Func<string, string, string?>? Transform { get; set; }
    public Action<string>? BeforeSend { get; set; }
    public event Action<string>? LineReceived;
    public event Action<Exception?>? Disconnected;
    private string command = "";
    private bool hooked;
    public Task OpenAsync(CancellationToken ct)
    {
        if (!hooked)
        {
            Demo.LineReceived += line => { string? result = Transform == null ? line : Transform(line, command); if (result != null) LineReceived?.Invoke(result); };
            Demo.Disconnected += error => Disconnected?.Invoke(error); hooked = true;
        }
        return Demo.OpenAsync(ct);
    }
    public Task SendLineAsync(string line, CancellationToken ct)
    { Sent.Add(line); command = string.Join(' ', line.Split(' ').Skip(2)); BeforeSend?.Invoke(command); return Demo.SendLineAsync(line, ct); }
    public Task CloseAsync() => Demo.CloseAsync();
    public void Cut() { _ = Demo.CloseAsync(); }
}
