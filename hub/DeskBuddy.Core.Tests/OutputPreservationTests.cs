using DeskBuddy.Core;

internal static partial class Program
{
    private static void OutputPreservationParsing()
    {
        var old = ParseCapabilities();
        Check(!old.PreservesOutput && !old.SupportsPetInputFeedback, "old HELLO defaults independently to false");
        Check(!HubProtocol.ParseHello(HubProtocol.ParseReply("HUB 1 OK " + HelloFields.Replace("TEST_R3", "R3E"))).PreservesOutput, "build name never grants preservation");
        Check(ParseCapabilities(" preserve_output=1").PreservesOutput && !ParseCapabilities(" preserve_output=1").SupportsPetInputFeedback, "preserve flag is independent");
        Check(ParseCapabilities(" pet_input_feedback=1").SupportsPetInputFeedback && !ParseCapabilities(" pet_input_feedback=1").PreservesOutput, "feedback flag is independent");
        Check(!ParseCapabilities(" preserve_output=0 pet_input_feedback=0").PreservesOutput, "explicit zero is old semantics");
        Check(ParseCapabilities(" preserve_output=1 pet_input_feedback=1").SupportsPetInputFeedback, "both capabilities parsed");
        foreach (string key in new[] { "preserve_output", "pet_input_feedback" })
        {
            foreach (string value in new[] { "2", "255", "-1", "+1", "01", "true", "1.0", "" })
                Throws<FormatException>(() => ParseCapabilities($" {key}={value}"), "malformed capability is not a fallback");
            Throws<FormatException>(() => ParseCapabilities($" {key}=1 {key}=1"), "duplicate capability rejected");
        }
        string active = "STATUS " + FakeDevice.StatusFields(armed: true) + " demo_action=4 demo_remaining_ms=1";
        Throws<FormatException>(() => LegacyStatusParser.Parse(active), "legacy parser retains active-demo OFF rule");
        Check(LegacyStatusParser.Parse(active, preservesOutput: true).Armed, "negotiated parser allows active-demo ON");
        Throws<FormatException>(() => LegacyStatusParser.Parse(active.Replace("page=0", "page=2"), preservesOutput: true), "preservation does not relax HOME rule");
        foreach (int direction in new[] { -1, 0, 1 })
            foreach (uint elapsed in new uint[] { 0, 1799 })
            {
                var status = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields() + $" pet_direction={direction} pet_elapsed_ms={elapsed}");
                Check(status.HasPetInputFeedbackStatus && status.PetDirection == direction && status.PetElapsedMilliseconds == elapsed, "physical feedback endpoints are typed");
            }
        var absent = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields());
        Check(!absent.HasPetInputFeedbackStatus && absent.PetDirection is null && absent.PetElapsedMilliseconds is null, "old absent feedback remains unknown");
        foreach (string invalid in new[] { "pet_direction=0", "pet_elapsed_ms=0", "pet_direction=2 pet_elapsed_ms=0", "pet_direction=-2 pet_elapsed_ms=0", "pet_direction=one pet_elapsed_ms=0", "pet_direction=0 pet_elapsed_ms=1800", "pet_direction=0 pet_elapsed_ms=-1", "pet_direction=0 pet_elapsed_ms=4294967296" })
            Throws<FormatException>(() => LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields() + " " + invalid, preservesOutput: true), "feedback limits remain strict");
    }

    private static async Task OutputPreservationOperations()
    {
        foreach (bool? capability in new bool?[] { null, false, true })
            foreach (bool initial in new[] { false, true })
            {
                var device = new FakeDevice { PreserveOutputCapability = capability, PetInputFeedbackCapability = capability, BleManagementCapability = true };
                await using var client = new HubClient(device); await client.ConnectAsync();
                foreach (string verb in new[] { "PREVIEW", "SAVE", "REVERT", "SELECT" })
                {
                    await client.ControlAsync(initial ? "OUTPUT 1" : "OUTPUT 0");
                    int before = device.Sent.Count; var snapshot = client.Configuration!;
                    var result = await ChangeForOutputTest(client, verb, snapshot);
                    bool expected = initial && capability == true;
                    Check(client.Status!.Armed == expected && device.Armed == expected, "configuration uses advertised output policy");
                    Check(result.Revision == snapshot.Revision + 1, "preserving output does not weaken revision validation");
                    Check(CommandTokens(device, before).SequenceEqual(new[] { verb, "GET", "STATUS" }), "bank mutation sends no implicit output command");
                }
                foreach (string command in new[] { "LANGUAGE ZH", "LANGUAGE EN", "PAGE HOME", "ROUTE USB", "MANAGE 1", "MANAGE 0" })
                {
                    await client.ControlAsync(initial ? "OUTPUT 1" : "OUTPUT 0"); int before = device.Sent.Count;
                    var status = await client.ControlAsync(command);
                    bool expected = initial && (capability == true || command.StartsWith("LANGUAGE ", StringComparison.Ordinal));
                    Check(status.Armed == expected, "ordinary control reports actual output without restoration");
                    Check(device.Sent.Count == before + 2 && CommandTokens(device, before).SequenceEqual(new[] { "CONTROL", "STATUS" }), "control writes only requested command and readback");
                    Check(!device.Sent.Skip(before).Any(s => s.EndsWith("OUTPUT 1", StringComparison.Ordinal)), "no hidden re-arm");
                }
                foreach (string command in new[] { "ROUTE BLE", "PAGE DIAG", "PAGE INFO", "PAGE ACTIVITY" })
                {
                    await client.ControlAsync(initial ? "OUTPUT 1" : "OUTPUT 0");
                    Check(!(await client.ControlAsync(command)).Armed, "actual route change and diagnostic pages still disarm");
                }
            }
        // A future-looking name with no capability must still reject an armed bank readback.
        var masquerading = new FakeDevice { PreserveOutputCapability = true };
        masquerading.Intercept = line =>
        {
            if (line.EndsWith(" HELLO", StringComparison.Ordinal)) masquerading.Emit($"HUB {line.Split(' ')[1]} OK " + HelloFields.Replace("TEST_R3", "R3E"));
            else masquerading.DefaultReply(line);
        };
        await using var oldClient = new HubClient(masquerading); await oldClient.ConnectAsync(); await oldClient.ControlAsync("OUTPUT 1");
        await AnimationError("READBACK_MISMATCH", () => oldClient.PreviewAsync(HubDefaults.CreateBank(), 1));
        Check(oldClient.Configuration is null && oldClient.Status is null, "old peer ON readback remains a hard mismatch");
    }

    private static async Task OutputPreservationRaces()
    {
        var device = new FakeDevice { PreserveOutputCapability = true, PetInputFeedbackCapability = true, AnimationPreviewCapability = true, HomeModeCount = 3 };
        var bank = HubDefaults.CreateBank(); bank.Profiles[0] = bank.Profiles[0] with { Mode = HomeMode.DeskPet }; device.SetBankForTest(bank);
        await using var client = new HubClient(device); await client.ConnectAsync();
        foreach (bool initial in new[] { false, true })
            foreach (var action in AnimationActions)
            {
                await client.ControlAsync(initial ? "OUTPUT 1" : "OUTPUT 0"); int before = device.Sent.Count;
                uint revision = device.Revision; string hex = device.Hex;
                var started = await client.StartAnimationPreviewAsync(action);
                Check(started.Armed == initial && started.DemoAction == action, "all seven demos may coexist with ON or OFF on explicit peer");
                Check(CommandTokens(device, before).SequenceEqual(new[] { "ANIMATE", "STATUS" }), "demo has no save or hidden output command");
                device.AdvanceAnimation(6000); var expired = await client.RefreshStatusAsync();
                Check(expired.Armed == initial && expired.DemoAction is null, "expiry leaves actual output unchanged");
                Check((await client.StopAnimationPreviewAsync()).Armed == initial, "STOP retains actual state");
                Check(device.Revision == revision && device.Hex == hex && device.PersistedHex == hex, "animation does not mutate bank or revision");
            }
        foreach (string verb in new[] { "PREVIEW", "SAVE", "REVERT", "SELECT" })
        {
            await client.ControlAsync("OUTPUT 1"); var snapshot = client.Configuration!; int before = device.Sent.Count;
            device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.PhysicalOutputOff(); device.DefaultReply(line); };
            await ChangeForOutputTest(client, verb, snapshot);
            Check(!client.Status!.Armed && !device.Armed, "physical OFF between config ACK and STATUS wins");
            Check(CommandTokens(device, before).SequenceEqual(new[] { verb, "GET", "STATUS" }), "physical OFF never triggers compensating command");
            device.Intercept = null;
        }
        foreach (string command in new[] { "LANGUAGE ZH", "PAGE HOME", "ROUTE USB" })
        {
            await client.ControlAsync("OUTPUT 1"); int before = device.Sent.Count;
            device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.PhysicalOutputOff(); device.DefaultReply(line); };
            Check(!(await client.ControlAsync(command)).Armed, "physical OFF wins ordinary control readback");
            Check(!device.Sent.Skip(before).Any(s => s.EndsWith("OUTPUT 1", StringComparison.Ordinal)), "ordinary control cannot undo physical OFF");
            device.Intercept = null;
        }
        await client.ControlAsync("OUTPUT 1"); int animationStart = device.Sent.Count;
        device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.PhysicalOutputOff(); device.DefaultReply(line); };
        var canceled = await client.StartAnimationPreviewAsync(AnimationPreviewAction.Tap);
        Check(!canceled.Armed && canceled.DemoAction is null, "physical OFF cancels demo after an armed ACK");
        Check(CommandTokens(device, animationStart).SequenceEqual(new[] { "ANIMATE", "STATUS" }), "canceled demo is not replayed");
        device.Intercept = null;
        var last = client.Status;
        device.Emit("STATUS " + FakeDevice.StatusFields(armed: true) + " demo_action=2 demo_remaining_ms=300 pet_direction=-1 pet_elapsed_ms=100");
        Check(client.Status!.Armed && client.Status.DemoAction == AnimationPreviewAction.Blink && client.Status.PetDirection == -1, "unsolicited status uses negotiated parsing too");
        last = client.Status;
        device.Emit("STATUS " + FakeDevice.StatusFields(armed: true) + " demo_action=2 demo_remaining_ms=300 pet_direction=2 pet_elapsed_ms=100");
        Check(ReferenceEquals(client.Status, last), "malformed unsolicited feedback cannot replace accepted status");
    }

    private static async Task OutputPreservationFailures()
    {
        foreach (string bad in new[] { "preserve_output=2", "pet_input_feedback=true", "preserve_output=1 preserve_output=0" })
        {
            var badPeer = new FakeDevice(); badPeer.Intercept = line => badPeer.Emit($"HUB {line.Split(' ')[1]} OK {HelloFields} {bad}");
            await using var badClient = new HubClient(badPeer);
            await ThrowsAsync<FormatException>(() => badClient.ConnectAsync(), "malformed new capability fails handshake");
            Check(badPeer.Sent.Count == 1 && badClient.Capabilities is null, "malformed flag causes no mutation or legacy downgrade");
        }
        var missingFeedback = new FakeDevice { PreserveOutputCapability = true, PetInputFeedbackCapability = true };
        missingFeedback.Intercept = line =>
        {
            if (line.EndsWith(" STATUS", StringComparison.Ordinal)) missingFeedback.Emit($"HUB {line.Split(' ')[1]} OK " + FakeDevice.StatusFields());
            else missingFeedback.DefaultReply(line);
        };
        await using (var missingClient = new HubClient(missingFeedback))
        {
            await ThrowsAsync<FormatException>(() => missingClient.ConnectAsync(), "advertised feedback cannot silently omit telemetry");
            Check(missingClient.Capabilities is null && missingClient.Status is null, "incomplete advertised status fails handshake cleanly");
        }
        var device = new FakeDevice { PreserveOutputCapability = true, PetInputFeedbackCapability = true, AnimationPreviewCapability = true };
        await using var client = new HubClient(device, TimeSpan.FromMilliseconds(60)); await client.ConnectAsync(); await client.ControlAsync("OUTPUT 1");
        foreach (string malformed in new[] { "", " armed=2" })
        {
            int before = device.Sent.Count;
            device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} OK revision=2 preview=1{malformed}");
            await ThrowsAsync<FormatException>(() => client.PreviewAsync(HubDefaults.CreateBank(), 1), "new configuration ACK requires actual Boolean armed");
            Check(device.Sent.Count == before + 1 && client.Configuration is null && client.Status is null, "malformed ACK cannot falsely confirm or re-arm");
            device.Intercept = null; await client.GetAsync();
        }
        int denied = device.Sent.Count;
        device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} ERR code=BUSY");
        await AnimationError("BUSY", () => client.SaveAsync(HubDefaults.CreateBank(), 1));
        Check(device.Sent.Count == denied + 1 && device.Armed && client.Configuration is null, "busy storage response is not retried and does not invent OFF");
        device.Intercept = null; await client.GetAsync();
        int cut = device.Sent.Count;
        device.Intercept = line => { if (line.EndsWith(" GET", StringComparison.Ordinal)) device.Cut(); else device.DefaultReply(line); };
        await ThrowsAsync<IOException>(() => client.PreviewAsync(HubDefaults.CreateBank(), 1), "disconnect after mutation ACK abandons readback");
        Check(client.Capabilities is null && client.Configuration is null && client.Status is null, "disconnect clears preservation capability and output state");
        Check(CommandTokens(device, cut).SequenceEqual(new[] { "PREVIEW", "GET" }), "disconnect causes no auto-recovery output command");
        device.Intercept = null; device.PhysicalOutputOff(); device.PreserveOutputCapability = null; device.PetInputFeedbackCapability = null;
        int reconnect = device.Sent.Count; await client.ConnectAsync();
        Check(!client.Capabilities!.PreservesOutput && !client.Capabilities.SupportsPetInputFeedback, "reconnect re-reads and removes unsupported flags");
        Check(CommandTokens(device, reconnect).SequenceEqual(new[] { "HELLO", "GET", "STATUS" }), "reconnect only observes and never replays prior ON or mutation");
        device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.Emit($"HUB {line.Split(' ')[1]} OK " + FakeDevice.StatusFields(device.Revision, armed: true) + " demo_action=1 demo_remaining_ms=10"); else device.DefaultReply(line); };
        await ThrowsAsync<FormatException>(() => client.RefreshStatusAsync(), "downgraded session rejects active ON demo");
    }

    private static IEnumerable<string> CommandTokens(FakeDevice device, int from) => device.Sent.Skip(from).Select(line => line.Split(' ')[2]);
    private static Task<BankSnapshot> ChangeForOutputTest(HubClient client, string verb, BankSnapshot current) => verb switch
    {
        "PREVIEW" => client.PreviewAsync(current.Bank, current.Revision),
        "SAVE" => client.SaveAsync(current.Bank, current.Revision),
        "REVERT" => client.RevertAsync(),
        "SELECT" => client.SelectProfileAsync(1, current.Revision),
        _ => throw new ArgumentOutOfRangeException(nameof(verb))
    };
}
