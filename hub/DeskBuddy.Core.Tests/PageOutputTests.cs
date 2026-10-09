using DeskBuddy.Core;
using DeskBuddy.App;

internal static partial class Program
{
    private static readonly (string Token, uint Id, bool Suppressed)[] ExtendedPages =
        [("DIAG", 2, true), ("INFO", 4, false), ("ACTIVITY", 5, true), ("SETTINGS", 1, false), ("MANAGEMENT", 6, false), ("LANGUAGE", 7, false), ("TOOLS", 8, false)];

    private static void PageOutputParsing()
    {
        Check(!ParseCapabilities().PreservesPageOutput, "missing page capability defaults false");
        Check(!ParseCapabilities(" preserve_output=1").PreservesPageOutput, "R3E preservation does not imply page preservation");
        var isolated = ParseCapabilities(" preserve_page_output=1");
        Check(isolated.PreservesPageOutput && !isolated.PreservesOutput, "new page flag does not grant unrelated output semantics");
        Check(!ParseCapabilities(" preserve_page_output=0").PreservesPageOutput, "explicit false retains old page rules");
        Check(!HubProtocol.ParseHello(HubProtocol.ParseReply("HUB 1 OK " + HelloFields.Replace("TEST_R3", "R3F"))).PreservesPageOutput, "build name cannot grant capability");
        foreach (string bad in new[] { "2", "-1", "+1", "01", "true", "", "1.0" })
            Throws<FormatException>(() => ParseCapabilities(" preserve_page_output=" + bad), "page capability must be canonical Boolean");
        Throws<FormatException>(() => ParseCapabilities(" preserve_page_output=1 preserve_page_output=1"), "duplicate capability rejected");
        foreach (bool armed in new[] { false, true })
        {
            string line = "STATUS " + FakeDevice.StatusFields(armed: armed, page: 2);
            Check(!LegacyStatusParser.Parse(line).HidSuppressed, "absent optional suppression defaults false without inferring page");
            foreach (bool suppressed in new[] { false, true })
            {
                var status = LegacyStatusParser.Parse(line + $" hid_suppressed={(suppressed ? 1 : 0)}");
                Check(status.Armed == armed && status.HidSuppressed == suppressed, "suppression does not overwrite user's armed switch");
            }
            foreach (string bad in new[] { "2", "-1", "true", "01", "+0", "" })
                Throws<FormatException>(() => LegacyStatusParser.Parse(line + " hid_suppressed=" + bad), "optional suppression remains strict when present");
        }
        foreach (var page in ExtendedPages)
            Check(HubProtocol.BuildRequest(1, "CONTROL PAGE " + page.Token).EndsWith(page.Token, StringComparison.Ordinal), "extended grammar retains exact page token");
        Check(!HubProtocol.IsControl("PAGE CAL") && !HubProtocol.IsControl("PAGE CALIBRATION") && !HubProtocol.IsControl("PAGE BACK"), "calibration and parent navigation are not invented remote commands");
    }

    private static async Task PageOutputOperations()
    {
        foreach (bool? capability in new bool?[] { null, false, true })
        {
            var device = new FakeDevice { PreserveOutputCapability = true, PreservePageOutputCapability = capability };
            await using var client = new HubClient(device); await client.ConnectAsync();
            foreach (bool armed in new[] { false, true })
                foreach (var page in ExtendedPages)
                {
                    await client.ControlAsync("PAGE HOME"); await client.ControlAsync(armed ? "OUTPUT 1" : "OUTPUT 0");
                    int before = device.Sent.Count; uint revision = device.Revision; string bank = device.Hex;
                    if (HubProtocol.RequiresPageOutputCapability("PAGE " + page.Token) && capability != true)
                    {
                        await AnimationError("UNSUPPORTED", () => client.ControlAsync("PAGE " + page.Token));
                        Check(device.Sent.Count == before && device.Armed == armed, "old-peer extended page rejected locally with zero writes");
                        continue;
                    }
                    var status = await client.ControlAsync("PAGE " + page.Token);
                    Check(status.GetUInt32("page") == page.Id && status.Armed == (armed && capability == true), "page ID and actual old/new armed behavior verified");
                    Check(status.HidSuppressed == (capability == true && page.Suppressed), "only reported test-page suppression is observed");
                    Check(CommandTokens(device, before).SequenceEqual(new[] { "CONTROL", "STATUS" }), "navigation sends one command and one status only");
                    Check(device.Revision == revision && device.Hex == bank, "page navigation leaves configuration untouched");
                    var home = await client.ControlAsync("PAGE HOME");
                    Check(home.Armed == status.Armed && !home.HidSuppressed, "HOME removes pause without restoring a stale ON");
                    Check(!device.Sent.Skip(before).Any(s => s.EndsWith("OUTPUT 1", StringComparison.Ordinal)), "navigation never sends a compensating arm command");
                }
        }
        var independent = new FakeDevice { PreservePageOutputCapability = true };
        await using var independentClient = new HubClient(independent); await independentClient.ConnectAsync(); await independentClient.ControlAsync("OUTPUT 1");
        Check((await independentClient.ControlAsync("PAGE INFO")).Armed, "page-only capability permits INFO ON");
        Check((await independentClient.PreviewAsync(HubDefaults.CreateBank(), 1)).Preview, "preview still completes old configuration semantics");
        Check(!independentClient.Status!.Armed, "page-only capability never relaxes configuration OFF behavior");
    }

    private static async Task PageOutputRaces()
    {
        var device = new FakeDevice { PreserveOutputCapability = true, PreservePageOutputCapability = true };
        await using var client = new HubClient(device); await client.ConnectAsync();
        foreach (var page in ExtendedPages)
        {
            await client.ControlAsync("PAGE HOME"); await client.ControlAsync("OUTPUT 1"); int before = device.Sent.Count;
            device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.PhysicalOutputOff(); device.DefaultReply(line); };
            Check(!(await client.ControlAsync("PAGE " + page.Token)).Armed, "physical OFF between page ACK and readback wins");
            device.Intercept = null;
            Check(!(await client.ControlAsync("PAGE HOME")).Armed, "return after physical OFF does not restore ON");
            Check(!device.Sent.Skip(before).Any(s => s.EndsWith("OUTPUT 1", StringComparison.Ordinal)), "race does not rearm output");
        }
        foreach (string fields in new[] { "page=4", "page=4 armed=2", "page=4 armed=true" })
        {
            int before = device.Sent.Count; device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} OK {fields}");
            await ThrowsAsync<FormatException>(() => client.ControlAsync("PAGE INFO"), "new page ACK requires actual Boolean armed");
            Check(device.Sent.Count == before + 1 && client.Status is null, "invalid ACK cannot be reported as confirmed navigation");
            device.Intercept = null;
        }
        device.Intercept = line => { if (line.Contains(" CONTROL PAGE INFO", StringComparison.Ordinal)) device.Emit($"HUB {line.Split(' ')[1]} OK page=5 armed=1"); else device.DefaultReply(line); };
        await AnimationError("CONTROL_NOT_CONFIRMED", () => client.ControlAsync("PAGE INFO"));
        device.Intercept = null;
        await client.ControlAsync("PAGE HOME"); await client.ControlAsync("OUTPUT 1"); int cut = device.Sent.Count;
        device.Intercept = line => { if (line.EndsWith(" STATUS", StringComparison.Ordinal)) device.Cut(); else device.DefaultReply(line); };
        await ThrowsAsync<IOException>(() => client.ControlAsync("PAGE DIAG"), "disconnect after page ACK abandons readback");
        Check(client.Capabilities is null && client.Status is null && CommandTokens(device, cut).SequenceEqual(new[] { "CONTROL", "STATUS" }), "disconnect clears capability without replay");
        device.Intercept = null; device.PhysicalOutputOff(); device.PreservePageOutputCapability = null;
        int reconnect = device.Sent.Count; await client.ConnectAsync();
        Check(client.Capabilities!.PreservesOutput && !client.Capabilities.PreservesPageOutput, "R3E reconnect keeps old output fix but removes page extension");
        Check(CommandTokens(device, reconnect).SequenceEqual(new[] { "HELLO", "GET", "STATUS" }), "reconnect is read-only");
        device.Intercept = line => { if (line.Contains(" CONTROL PAGE INFO", StringComparison.Ordinal)) device.Emit($"HUB {line.Split(' ')[1]} OK page=4 armed=1"); else device.DefaultReply(line); };
        await AnimationError("CONTROL_NOT_CONFIRMED", () => client.ControlAsync("PAGE INFO"));
        device.Intercept = null;
        var accepted = await client.RefreshStatusAsync();
        device.Emit("STATUS " + FakeDevice.StatusFields() + " hid_suppressed=2");
        Check(ReferenceEquals(client.Status, accepted), "malformed unsolicited suppression cannot refresh cached state");
    }

    private static async Task PageOutputAppDemo()
    {
        foreach (bool newPages in new[] { false, true })
        {
            var demo = new DemoTransport(preservePageOutput: newPages) { DelayMilliseconds = 0 };
            await using var client = new HubClient(demo); await client.ConnectAsync();
            Check(client.Capabilities!.PreservesPageOutput == newPages, "actual App demo advertises selected old/new contract");
            await client.ControlAsync("OUTPUT 1"); int before = demo.Commands.Count;
            foreach (string page in newPages ? new[] { "DIAG", "INFO", "ACTIVITY", "MANAGEMENT", "LANGUAGE", "TOOLS", "SETTINGS" } : new[] { "DIAG", "INFO", "ACTIVITY" })
            {
                var status = await client.ControlAsync("PAGE " + page);
                Check(status.Armed == newPages && status.HidSuppressed == (newPages && page is "DIAG" or "ACTIVITY"), "actual demo separates switch and suppression");
                foreach (bool chinese in new[] { false, true })
                {
                    string pill = OutputStatePresentation.Pill(status, true, chinese);
                    Check(pill.Contains(newPages ? (chinese ? "开启" : "ON") : (chinese ? "关闭" : "OFF")), "pill describes actual switch state");
                    Check(pill.Contains(chinese ? "暂停" : "PAUSED") == status.HidSuppressed, "pill shows pause only when device reports it");
                    Check(OutputStatePresentation.Pill(status, false, chinese) == (chinese ? "输出未知" : "OUTPUT UNKNOWN"), "stale snapshot never claims live ON or pause");
                    Check(!string.IsNullOrWhiteSpace(OutputStatePresentation.Detail(status, true, chinese)), "bilingual detail available for each state");
                }
            }
            await client.ControlAsync("PAGE HOME");
            Check(client.Status!.Armed == newPages && !client.Status.HidSuppressed, "actual demo home follows current switch only");
            Check(!demo.Commands.Skip(before).Any(s => s == "CONTROL OUTPUT 1"), "App demo navigation requires no hidden arming");
            await client.ControlAsync("OUTPUT 0"); await client.ControlAsync("PAGE DIAG");
            await AnimationError("NOT_READY", () => client.ControlAsync("OUTPUT 1"));
            Check(!(await client.RefreshStatusAsync()).Armed, "diagnostic page rejects fresh enabling while OFF");
            if (newPages)
            {
                await client.ControlAsync("PAGE INFO"); Check((await client.ControlAsync("OUTPUT 1")).Armed, "new info page permits explicit arming");
                await client.ControlAsync("PAGE DIAG"); await AnimationError("NOT_READY", () => client.ControlAsync("OUTPUT 1"));
                Check((await client.RefreshStatusAsync()).Armed, "rejected diagnostic arm request does not flip an existing ON switch");
            }
        }
        Check(OutputStatePresentation.Pill(null, true, false) == "OUTPUT UNKNOWN", "missing snapshot remains unknown even with stale freshness flag");
        foreach (bool pageCapability in new[] { false, true })
            for (int page = 0; page <= 9; page++)
            {
                var caps = ParseCapabilities(" preserve_page_output=" + (pageCapability ? "1" : "0"));
                var status = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields(page: page));
                Check(OutputStatePresentation.CanEnable(status, caps, true, false) == (page is 0 or 1 || pageCapability && page is 4 or 6 or 7 or 8), "actual App enable predicate accepts only capability-safe pages");
                Check(!OutputStatePresentation.CanEnable(status, caps, false, false) && !OutputStatePresentation.CanEnable(status, caps, true, true), "stale or busy UI cannot enable output");
                var suppressed = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields(page: page) + " hid_suppressed=1");
                Check(!OutputStatePresentation.CanEnable(suppressed, caps, true, false), "reported suppression blocks fresh enabling independently of page ID");
            }
    }
}
