using DeskBuddy.Core;

internal static partial class Program
{
    private static readonly AnimationPreviewAction[] AnimationActions = Enum.GetValues<AnimationPreviewAction>();
    private static void AnimationParsing()
    {
        Check(!ParseCapabilities().SupportsAnimationPreview, "missing capability is unsupported, independent of build name");
        Check(!ParseCapabilities(" animation_preview=0").SupportsAnimationPreview, "explicit zero capability");
        Check(ParseCapabilities(" animation_preview=1").SupportsAnimationPreview, "explicit one capability");
        foreach (string value in new[] { "2", "255", "-1", "+1", "01", "true", "1.0", "" })
            Throws<FormatException>(() => ParseCapabilities(" animation_preview=" + value), "invalid animation capability");
        Throws<FormatException>(() => ParseCapabilities(" animation_preview=1 animation_preview=1"), "duplicate capability");
        Check(!new HelloInfo(1, "NEW_FUTURE_BUILD", 224, 3, 12, 1, false).SupportsAnimationPreview, "source-compatible capability defaults false");
        foreach (string token in new[] { "IDLE", "BLINK", "HAPPY", "WALK", "TAP", "SLEEP", "WAKE", "STOP" })
            Check(HubProtocol.BuildRequest(7, "ANIMATE " + token) == "HUB 7 ANIMATE " + token, "exact animation wire command");
        foreach (string command in new[] { "ANIMATE", "ANIMATE idle", "ANIMATE 1", "ANIMATE NONE", "ANIMATE STOP 0", "ANIMATE TAP 6000", "ANIMATE  WAKE", "ANIMATE WAKE\nCONTROL OUTPUT 1", "ANIMATE WAKE " })
            Throws<FormatException>(() => HubProtocol.BuildRequest(1, command), "animation command is not an arbitrary string or duration");
        string baseStatus = "STATUS " + FakeDevice.StatusFields();
        var old = LegacyStatusParser.Parse(baseStatus);
        Check(!old.HasAnimationPreviewStatus && old.DemoAction is null && old.DemoRemainingMilliseconds is null, "old status is unknown, not idle/zero");
        var none = LegacyStatusParser.Parse(baseStatus + " demo_action=0 demo_remaining_ms=0");
        Check(none.HasAnimationPreviewStatus && none.DemoAction is null && none.DemoRemainingMilliseconds == 0, "known inactive status");
        foreach (var action in AnimationActions)
            foreach (uint duration in new uint[] { 1, 6000 })
            {
                var status = LegacyStatusParser.Parse(baseStatus + $" demo_action={(byte)action} demo_remaining_ms={duration}");
                Check(status.DemoAction == action && status.DemoRemainingMilliseconds == duration, "all action IDs and duration endpoints");
            }
        foreach (string fields in new[] { "demo_action=0", "demo_remaining_ms=0", "demo_action=1 demo_remaining_ms=0", "demo_action=0 demo_remaining_ms=1", "demo_action=8 demo_remaining_ms=6000", "demo_action=255 demo_remaining_ms=6000", "demo_action=1 demo_remaining_ms=6001", "demo_action=1 demo_remaining_ms=4294967296", "demo_action=-1 demo_remaining_ms=6000", "demo_action=1 demo_remaining_ms=-1", "demo_action=1 demo_remaining_ms=+1", "demo_action=1 demo_remaining_ms=1.0" })
            Throws<FormatException>(() => LegacyStatusParser.Parse(baseStatus + " " + fields), "incoherent animation status rejected");
        Throws<FormatException>(() => LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields(armed: true) + " demo_action=1 demo_remaining_ms=100"), "active demo cannot claim output ON");
        Throws<FormatException>(() => LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields(page: 2) + " demo_action=1 demo_remaining_ms=100"), "active demo cannot claim a non-home page");
        Check(LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields(armed: true, page: 2) + " demo_action=0 demo_remaining_ms=0").Armed, "inactive demo imposes no invented output/page state");
    }

    private static async Task AnimationPeerGuard()
    {
        var device = new FakeDevice(); await using var client = new HubClient(device, TimeSpan.FromMilliseconds(100)); await client.ConnectAsync();
        foreach (bool? capability in new bool?[] { null, false })
        {
            if (capability == false) { await client.DisconnectAsync(); device.AnimationPreviewCapability = false; await client.ConnectAsync(); }
            int before = device.Sent.Count; var status = client.Status;
            foreach (var action in AnimationActions)
                await AnimationError("UNSUPPORTED", () => client.StartAnimationPreviewAsync(action));
            await AnimationError("UNSUPPORTED", () => client.StopAnimationPreviewAsync());
            Check(device.Sent.Count == before && ReferenceEquals(client.Status, status) && client.Configuration is not null, "unsupported animation never writes or invalidates valid readback");
        }
        foreach (byte invalid in new byte[] { 0, 8, 255 })
        {
            int before = device.Sent.Count;
            await ThrowsAsync<ArgumentOutOfRangeException>(() => client.StartAnimationPreviewAsync((AnimationPreviewAction)invalid), "undefined action rejected before send");
            Check(device.Sent.Count == before, "invalid enum cannot send STOP or arbitrary action");
        }
        await client.DisconnectAsync(); device.AnimationPreviewCapability = true; await client.ConnectAsync();
        Check(client.Capabilities!.SupportsAnimationPreview && client.Status!.HasAnimationPreviewStatus, "reconnect reads new animation capability");
        var stopped = await client.StopAnimationPreviewAsync(); Check(stopped.DemoRemainingMilliseconds == 0, "new peer accepts STOP");
        await client.DisconnectAsync(); device.AnimationPreviewCapability = null; await client.ConnectAsync();
        int oldCount = device.Sent.Count; await AnimationError("UNSUPPORTED", () => client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle));
        Check(!client.Capabilities!.SupportsAnimationPreview && device.Sent.Count == oldCount, "downgrade forgets capability and cannot replay");
        var legacy = new FakeDevice { Legacy = true }; await using var legacyClient = new HubClient(legacy, TimeSpan.FromMilliseconds(25)); await legacyClient.ConnectAsync();
        int legacyCount = legacy.Sent.Count;
        await AnimationError("UNSUPPORTED", () => legacyClient.StartAnimationPreviewAsync(AnimationPreviewAction.Tap));
        await AnimationError("UNSUPPORTED", () => legacyClient.StopAnimationPreviewAsync());
        Check(legacy.Sent.Count == legacyCount, "legacy uses no invented animation commands");
        foreach (string capability in new[] { "animation_preview=2", "animation_preview=true" })
        {
            var bad = new FakeDevice { Intercept = line => { } };
            bad.Intercept = line => bad.Emit($"HUB {line.Split(' ')[1]} OK {HelloFields} {capability}");
            await using var badClient = new HubClient(bad);
            await ThrowsAsync<FormatException>(() => badClient.ConnectAsync(), "malformed capability fails connection");
            Check(badClient.State == HubConnectionState.Faulted && bad.Sent.Count == 1, "malformed capability is never legacy fallback");
        }
        var missing = new FakeDevice { AnimationPreviewCapability = true };
        missing.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) missing.Emit($"HUB {line.Split(' ')[1]} OK {FakeDevice.StatusFields()}"); else missing.DefaultReply(line); };
        await using var missingClient = new HubClient(missing);
        await ThrowsAsync<FormatException>(() => missingClient.ConnectAsync(), "advertised capability requires coherent status on connect");
        Check(missingClient.State == HubConnectionState.Faulted, "missing advertised telemetry fails closed");
    }

    private static async Task AnimationLifecycle()
    {
        var device = new FakeDevice { AnimationPreviewCapability = true, HomeModeCount = 3 };
        var bank = HubDefaults.CreateBank(); bank.Profiles[0] = bank.Profiles[0] with { Mode = HomeMode.DeskPet, Animation = false }; device.SetBankForTest(bank);
        await using var client = new HubClient(device, TimeSpan.FromMilliseconds(100)); await client.ConnectAsync();
        string original = device.Hex; uint revision = device.Revision;
        foreach (var action in AnimationActions)
        {
            await client.ControlAsync("PAGE INFO"); await client.ControlAsync("OUTPUT 1");
            int before = device.Sent.Count;
            var started = await client.StartAnimationPreviewAsync(action);
            Check(started.DemoAction == action && started.DemoRemainingMilliseconds == 6000 && !started.Armed && started.GetUInt32("page") == 0, "start returns HOME/OFF and fixed six-second state");
            Check(device.Sent.Skip(before).Select(s => string.Join(' ', s.Split(' ').Skip(2))).SequenceEqual(new[] { "ANIMATE " + action.ToString().ToUpperInvariant(), "STATUS" }), "start sends exactly ANIMATE + STATUS; no bank command or output ON");
            Check(device.Hex == original && device.PersistedHex == original && device.Revision == revision && client.Configuration!.Revision == revision && !client.Configuration.Preview, "animation does not mutate bank, preview flag or revision");
            Check(started.GetUInt32("sent") == 0 && started.KeyPresses!.All(n => n == 0), "mock telemetry preserved, not synthesized from demo");
            device.AdvanceAnimation(5999);
            Check((await client.RefreshStatusAsync()).DemoRemainingMilliseconds == 1, "last millisecond reported");
            device.AdvanceAnimation(1);
            var expired = await client.RefreshStatusAsync();
            Check(expired.DemoAction is null && expired.DemoRemainingMilliseconds == 0 && !expired.Armed, "expiry does not restore output");
        }
        var run = await client.StartAnimationPreviewAsync(AnimationPreviewAction.Tap);
        int stopBefore = device.Sent.Count; var stop = await client.StopAnimationPreviewAsync();
        Check(stop.DemoAction is null && stop.DemoRemainingMilliseconds == 0 && !stop.Armed && device.Sent.Count == stopBefore + 2, "STOP requires ACK and status and sends only two lines");
        await client.ControlAsync("OUTPUT 1");
        Check((await client.StopAnimationPreviewAsync()).Armed, "STOP without active demo is no-op for explicit ON");
        await client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle); device.CancelAnimation();
        Check((await client.RefreshStatusAsync()).DemoAction is null, "external physical cancellation observed");
        foreach (string control in new[] { "PAGE DIAG", "ROUTE BLE", "OUTPUT 1" })
        {
            await client.StartAnimationPreviewAsync(AnimationPreviewAction.Happy); await client.ControlAsync(control);
            Check(client.Status!.DemoRemainingMilliseconds == 0, "cancellation through explicit control observed");
        }
        await client.StartAnimationPreviewAsync(AnimationPreviewAction.Sleep);
        await client.PreviewAsync(bank, revision);
        Check(client.Status!.DemoAction is null && client.Configuration!.Preview, "config apply ends only the temporary demo in protocol mock");
        device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.CancelAnimation(); device.DefaultReply(line); };
        var canceled = await client.StartAnimationPreviewAsync(AnimationPreviewAction.Wake);
        Check(canceled.HasAnimationPreviewStatus && canceled.DemoAction is null && canceled.DemoRemainingMilliseconds == 0, "cancel between ACK and STATUS returned as cancellation, not false success or replay");
        device.Intercept = null;
        device.SetBankForTest(HubDefaults.CreateBank()); await client.GetAsync();
        int denied = device.Sent.Count;
        await AnimationError("INCOMPATIBLE", () => client.StartAnimationPreviewAsync(AnimationPreviewAction.Walk));
        Check(device.Sent.Count == denied + 1 && client.Status is null && client.Configuration is not null, "firmware Face/WALK denial preserved without retry or assumed state");
        device.Calibrating = true;
        await AnimationError("BUSY", () => client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle));
        Check((await client.StopAnimationPreviewAsync()).DemoRemainingMilliseconds == 0, "STOP still accepted while calibration busy");
    }

    private static async Task AnimationFailures()
    {
        var device = new FakeDevice { AnimationPreviewCapability = true }; await using var client = new HubClient(device, TimeSpan.FromMilliseconds(60)); await client.ConnectAsync();
        foreach (string fields in new[] { "demo_action=3 demo_remaining_ms=6000 armed=0", "demo_action=1 demo_remaining_ms=5999 armed=0", "demo_action=1 demo_remaining_ms=6000 armed=1", "demo_action=0 demo_remaining_ms=0 armed=0", "demo_action=1 demo_remaining_ms=6001 armed=0", "demo_action=1 armed=0", "demo_action=1 demo_remaining_ms=6000", "demo_action=1 demo_remaining_ms=6000 armed=2" })
        {
            int before = device.Sent.Count; device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} OK {fields}");
            await AnimationMalformedOrUnconfirmed(() => client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle));
            Check(device.Sent.Count == before + 1 && client.Status is null && client.Configuration is not null, "bad ACK never starts a status/readback sequence or clears unchanged bank");
        }
        foreach (string status in new[] { FakeDevice.StatusFields(), FakeDevice.StatusFields() + " demo_action=2 demo_remaining_ms=5999", FakeDevice.StatusFields(armed: true) + " demo_action=1 demo_remaining_ms=5000", FakeDevice.StatusFields(page: 4) + " demo_action=1 demo_remaining_ms=5000", FakeDevice.StatusFields() + " demo_action=1 demo_remaining_ms=0" })
        {
            int before = device.Sent.Count;
            device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.Emit($"HUB {line.Split(' ')[1]} OK {status}"); else device.DefaultReply(line); };
            await AnimationMalformedOrUnconfirmed(() => client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle));
            Check(device.Sent.Count == before + 2 && client.Status is null, "bad readback leaves state unknown, no resend");
        }
        device.Intercept = line => { if (line.Contains(" ANIMATE STOP", StringComparison.Ordinal)) device.Emit($"HUB {line.Split(' ')[1]} OK demo_action=1 demo_remaining_ms=6000 armed=0"); else device.DefaultReply(line); };
        await AnimationError("ANIMATION_NOT_CONFIRMED", () => client.StopAnimationPreviewAsync());
        device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.Emit($"HUB {line.Split(' ')[1]} OK {FakeDevice.StatusFields()} demo_action=1 demo_remaining_ms=100"); else device.DefaultReply(line); };
        await AnimationError("ANIMATION_NOT_CONFIRMED", () => client.StopAnimationPreviewAsync());
        foreach (string error in new[] { "BUSY", "INCOMPATIBLE", "UNSUPPORTED", "INVALID" })
        {
            int before = device.Sent.Count; device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} ERR code={error}");
            await AnimationError(error, () => client.StartAnimationPreviewAsync(AnimationPreviewAction.Tap));
            Check(device.Sent.Count == before + 1 && client.Status is null, "device error propagated without status fabrication or resend");
        }
        device.Intercept = line => device.Emit($"HUB {int.Parse(line.Split(' ')[1]) + 1} OK demo_action=1 demo_remaining_ms=6000 armed=0");
        int timeoutBefore = device.Sent.Count;
        await ThrowsAsync<TimeoutException>(() => client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle), "wrong-ID reply cannot acknowledge animation");
        Check(device.Sent.Count == timeoutBefore + 1 && client.Status is null, "animation timeout has no retry");
        device.Intercept = _ => { };
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); int before = device.Sent.Count;
            await ThrowsAsync<OperationCanceledException>(() => client.StartAnimationPreviewAsync(AnimationPreviewAction.Idle, canceled.Token), "pre-canceled start");
            Check(device.Sent.Count == before, "pre-canceled start sends nothing");
        }
        using (var cancel = new CancellationTokenSource())
        {
            int before = device.Sent.Count; var pending = client.StartAnimationPreviewAsync(AnimationPreviewAction.Tap, cancel.Token); cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => pending, "cancel wait after send");
            Check(device.Sent.Count == before + 1 && client.Status is null, "cancel is not an implicit STOP or replay; device may have executed");
        }
        int cutBefore = device.Sent.Count;
        var active = client.StartAnimationPreviewAsync(AnimationPreviewAction.Sleep);
        var queued = client.StartAnimationPreviewAsync(AnimationPreviewAction.Wake);
        device.Cut();
        await ThrowsAsync<IOException>(() => active, "disconnect ends active animation request");
        await ThrowsAsync<IOException>(() => queued, "disconnect abandons queued animation");
        Check(device.Sent.Count == cutBefore + 1 && client.Status is null && client.Capabilities is null, "queued animation never crosses sessions");
        device.Intercept = null; await client.ConnectAsync();
        Check(device.Sent.Skip(cutBefore + 1).Select(s => s.Split(' ')[2]).SequenceEqual(new[] { "HELLO", "GET", "STATUS" }), "reconnect only reads; no animation/STOP/bank replay");
        Check(device.Sent.Select(s => s.Split(' ')[1]).Distinct().Count() == device.Sent.Count, "animation shares globally unique session IDs");
    }

    private static async Task AnimationError(string code, Func<Task> action)
    {
        try { await action(); throw new Exception("Expected animation error " + code); }
        catch (HubProtocolException ex) { Check(ex.Code == code, "exact animation error code " + code); }
    }
    private static async Task AnimationMalformedOrUnconfirmed(Func<Task> action)
    {
        try { await action(); throw new Exception("Expected malformed or unconfirmed animation"); }
        catch (FormatException) { Check(true, "malformed animation rejected"); }
        catch (HubProtocolException ex) { Check(ex.Code == "ANIMATION_NOT_CONFIRMED", "unexpected animation ACK/status rejected"); }
    }
}
