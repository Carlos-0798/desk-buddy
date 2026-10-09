using DeskBuddy.App;
using DeskBuddy.Core;

internal static partial class Program
{
    private static async Task CustomDisplayTimeouts()
    {
        const string displayCaps = " display_sleep=1 backlight_control=0 sleep_binding=1";
        Check(!ParseCapabilities(displayCaps).SupportsCustomDisplayTimeout, "missing custom capability is false even with display support");
        Check(!ParseCapabilities(displayCaps + " display_custom=0").SupportsCustomDisplayTimeout, "explicit legacy custom=false");
        Check(ParseCapabilities(displayCaps + " display_custom=1").SupportsCustomDisplayTimeout, "strict explicit custom capability true");
        foreach (string v in new[] { "2", "-1", "true", "01", "+1", "" })
            Throws<FormatException>(() => ParseCapabilities(displayCaps + " display_custom=" + v), "malformed custom capability rejected");
        Throws<FormatException>(() => ParseCapabilities(" display_custom=1"), "custom capability requires display support");
        Throws<FormatException>(() => ParseCapabilities(displayCaps + " display_custom=1 display_custom=0"), "duplicate capability rejected");
        for (uint minute = 1; minute <= 720; minute++)
        {
            uint seconds = minute * 60;
            Check(DisplayTimeoutInput.TryMinutes(minute.ToString(System.Globalization.CultureInfo.InvariantCulture), out uint converted) && converted == seconds,
                "whole-minute UI conversion exact");
            Check(DisplaySettings.IsAllowedTimeout(seconds), "every minute 1..720 fits wire range");
            Check(HubProtocol.BuildRequest(5, $"DISPLAY SET {seconds} 30") == $"HUB 5 DISPLAY SET {seconds} 30", "minute wire request canonical");
        }
        foreach (string invalid in new[] { "", " ", "0", "-1", "+1", "1.5", "7.0", "721", "1e2", "１２", "4294967295", "999999999999" })
            Check(!DisplayTimeoutInput.TryMinutes(invalid, out _), "invalid minutes never round/clamp: " + invalid);
        Check(DisplayTimeoutInput.TryMinutes(" 7 ", out uint seven) && seven == 420, "outer whitespace in UI accepted as exact integer");
        foreach (uint invalid in new uint[] { 1, 29, 31, 59, 61, 301, 43201, 43260, 65535, uint.MaxValue })
        {
            Check(!DisplaySettings.IsAllowedTimeout(invalid), "nonminute or out-of-range wire duration rejected");
            Throws<FormatException>(() => HubProtocol.BuildRequest(1, $"DISPLAY SET {invalid} 300"), "invalid seconds rejected by request grammar");
        }
        foreach (string malformed in new[] { "060", "+60", "60.0", "-60", "4294967296" })
            Throws<FormatException>(() => HubProtocol.BuildRequest(1, $"DISPLAY SET {malformed} 300"), "noncanonical wire token rejected");
        var peer = new DisplayPeer(new DemoTransport() { DelayMilliseconds = 0 });
        await using var client = new HubClient(peer); await client.ConnectAsync();
        string bank = BankCodec.ToHex(client.Configuration!.Bank); uint revision = client.Configuration.Revision;
        foreach (bool armed in new[] { false, true })
        {
            await client.ControlAsync(armed ? "OUTPUT 1" : "OUTPUT 0");
            foreach ((uint usb, uint battery) in new[] { (1200u, 1800u), (3600u, 420u), (43200u, 60u), (30u, 0u), (300u, 300u) })
            {
                int before = peer.Sent.Count;
                var s = await client.SetDisplayAsync(usb, battery);
                Check(s.Display is { Saved: true } && s.Display.UsbTimeoutSeconds == usb && s.Display.BatteryTimeoutSeconds == battery && s.Armed == armed,
                    "extended values save and independently read back while preserving output");
                Check(peer.Sent.Skip(before).Select(CommandText).SequenceEqual(new[] { $"DISPLAY SET {usb} {battery}", "DISPLAY GET", "STATUS" }),
                    "extended setting remains one ACK/GET/STATUS transaction, no hidden rearm");
            }
        }
        Check(BankCodec.ToHex(client.Configuration!.Bank) == bank && client.Configuration.Revision == revision, "custom settings never change bank or revision");
        peer.Demo.RejectNextDisplay = "SAVE_FAILED"; await AnimationError("SAVE_FAILED", () => client.SetDisplayAsync(420, 1200));
        Check((await client.ReadDisplayAsync()).Display is { UsbTimeoutSeconds: 300, BatteryTimeoutSeconds: 300 }, "failed custom save retains prior settings");
        await client.DisconnectAsync();
        peer.Transform = (line, cmd) => cmd == "HELLO" ? line.Replace(" display_custom=1", "", StringComparison.Ordinal) : line;
        await client.ConnectAsync(); int oldCount = peer.Sent.Count;
        Check(!client.Capabilities!.SupportsCustomDisplayTimeout, "reconnect rereads missing capability rather than keeping old support");
        foreach ((uint usb, uint battery) in new[] { (420u, 60u), (300u, 1200u), (43200u, 0u) })
            await AnimationError("UNSUPPORTED_DISPLAY_TIMEOUT", () => client.SetDisplayAsync(usb, battery));
        Check(peer.Sent.Count == oldCount && client.Status is not null, "legacy capability gates either extended operand before transport and preserves current observation");
        await client.SetDisplayAsync(30, 300); Check(client.Status!.Display is { UsbTimeoutSeconds: 30, BatteryTimeoutSeconds: 300 }, "legacy four-option settings still work");
        peer.Transform = (line, cmd) => cmd is "STATUS" or "DISPLAY GET" ? line.Replace("display_usb_s=30", "display_usb_s=420", StringComparison.Ordinal) : line;
        Check((await client.ReadDisplayAsync()).Display!.UsbTimeoutSeconds == 420, "all valid extended observations readable even when writer capability absent; no silent clamp");
        var legacyPeer = new DisplayPeer(new DemoTransport(displayCustom: false) { DelayMilliseconds = 0 });
        await using var legacy = new HubClient(legacyPeer); await legacy.ConnectAsync();
        Check(!legacy.Capabilities!.SupportsCustomDisplayTimeout, "demo old-peer capability explicit false");
        await legacy.SetDisplayAsync(300, 300);
        int noWrite = legacyPeer.Sent.Count; await AnimationError("UNSUPPORTED_DISPLAY_TIMEOUT", () => legacy.SetDisplayAsync(3600, 300));
        Check(legacyPeer.Sent.Count == noWrite, "no unsupported command emitted to actual legacy demo");
    }
}
