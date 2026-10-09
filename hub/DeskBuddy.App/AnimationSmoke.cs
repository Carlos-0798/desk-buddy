using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    async Task RunAnimationSmoke(string directory, Action<bool, string> check, List<string> images)
    {
        static string Pixels(DevicePreview view)
        {
            var bmp = RenderNativePreview(view); var pixels = new byte[320 * 240 * 4]; bmp.CopyPixels(pixels, 320 * 4, 0);
            return Convert.ToHexString(SHA256.HashData(pixels));
        }
        long time = 0; var clock = new AnimationPlayback(() => time);
        foreach (var action in Enum.GetValues<AnimationPreviewAction>())
        {
            foreach (bool animate in new[] { false, true }) foreach (int mode in new[] { 0, 1, 2 })
            {
                if (mode == 0 && action == AnimationPreviewAction.Walk) continue;
                var hashes = new HashSet<string>();
                foreach (int ms in new[] { 0, 180, 300, 600, 900, 1200, 1800, 2400, 4220, 5100 })
                {
                    var visual = new DevicePreview { Width = 320, Height = 240, Face = 2, Theme = 4, Mode = mode, Animate = animate, DemoPose = AnimationPlayback.At(action, ms) };
                    hashes.Add(Pixels(visual));
                    if (!animate && mode == 2 && ms is 0 or 600 or 1800 or 4220)
                    { string file = $"action-{action.ToString().ToLowerInvariant()}-{ms}ms-animation-off.png"; Capture(visual, Path.Combine(directory, file)); images.Add(file); }
                }
                check(hashes.Count > 1, $"{action} mode {mode} daily animation {animate}: actual rendered pixels change over time");
            }
            time = 100; clock.Start(action); time = 6099;
            check(clock.Running && clock.Remaining == 1, action + " has a bounded six-second timeline");
            time = 6100; check(!clock.Running && clock.Pose == null, action + " expires without looping or resuming");
            time = 120000; check(!clock.Running, action + " never revives after expiry");
        }
        check(AnimationPlayback.At(AnimationPreviewAction.Blink, 600).Frame == "blink" && AnimationPlayback.At(AnimationPreviewAction.Blink, 900).Frame == "idle", "blink 900ms period has open and closed eyes");
        check(AnimationPlayback.At(AnimationPreviewAction.Walk, 0).Frame == "walk_a" && AnimationPlayback.At(AnimationPreviewAction.Walk, 180).Frame == "walk_b", "walk advances both pose and bounded position");
        check(AnimationPlayback.At(AnimationPreviewAction.Tap, 180).HeartY == -3 && !AnimationPlayback.At(AnimationPreviewAction.Tap, 1200).Heart, "greeting heart phase and timeout match device timeline");
        check(AnimationPlayback.At(AnimationPreviewAction.Wake, 400).ZCount == 3 && AnimationPlayback.At(AnimationPreviewAction.Wake, 600).Frame == "happy" && AnimationPlayback.At(AnimationPreviewAction.Wake, 4200).Frame == "blink", "wake closed-happy-idle timeline matches device");
        long boundary = 0; var boundaryClock = new AnimationPlayback(() => boundary++);
        boundaryClock.Start(AnimationPreviewAction.Idle); boundary = 5999;
        var beforeExpiry = boundaryClock.Snapshot; var afterExpiry = boundaryClock.Snapshot;
        check(beforeExpiry.Action == AnimationPreviewAction.Idle && beforeExpiry.Remaining == 1 && afterExpiry.Action == null && afterExpiry.Remaining == 0, "one-clock-read snapshot is safe across the exact 6000ms UI boundary");

        await ConnectDemo(); await ReadDevice(); page = 2; BuildShell(); await Settle();
        check(client!.Capabilities?.SupportsAnimationPreview == true, "new demo peer explicitly advertises animation preview");
        check(Descendants<TextBlock>(content).Any(t => t.Name == "ProfileExplanation" && t.Text.Contains("默认映射相同")), "profile labels explicitly explain identical default mappings and no app detection");
        check(!CanStartAnimation(AnimationPreviewAction.Walk), "Face mode disables full-body walking");
        check(!Descendants<Button>(content).First(b => b.Tag as string == "animation:Walk").IsEnabled, "Face Walk button is visibly disabled");
        int lines = demoTransport!.SentLineCount;
        bool rejected = false; try { await StartAnimation(AnimationPreviewAction.Walk); } catch (HubProtocolException e) when (e.Code == "INCOMPATIBLE") { rejected = true; }
        check(rejected && demoTransport.SentLineCount == lines, "Face Walk cannot send or silently fall back to Idle");
        EditProfile(p => p with { Name = "CUSTOM DESK", Mode = HomeMode.VirtualPet, Face = Face.Cat, Animation = false }); BuildPage();
        var originalBindings = Editing.Bindings.ToArray(); int saves = demoTransport.SaveRequests;
        uint? interactions = client.Status?.GetUInt32("pet_interactions"), sent = client.Status?.GetUInt32("sent");
        await StartAnimation(AnimationPreviewAction.Walk);
        check(animationPlayback.Running && animationDevice && client.Status?.DemoAction == AnimationPreviewAction.Walk, "action click applies draft then starts confirmed device and computer playback");
        check(client.Configuration?.Bank.Profiles[0] is { Name: "CUSTOM DESK", Mode: HomeMode.VirtualPet, Animation: false } && client.Status?.Armed == false, "selected draft is applied in RAM and output remains OFF");
        check(demoTransport.SaveRequests == saves && client.Status?.GetUInt32("pet_interactions") == interactions && client.Status?.GetUInt32("sent") == sent, "preview does not save or fake interactions or send HID");
        check(Editing.Bindings.SequenceEqual(originalBindings), "preview preserves existing user bindings");
        var sequence = demoTransport.Commands.ToArray();
        int animateIx = Array.FindLastIndex(sequence, x => x == "ANIMATE WALK"), previewIx = Array.FindLastIndex(sequence, x => x == "PREVIEW");
        check(previewIx >= 0 && animateIx > previewIx && sequence.Skip(previewIx + 1).Take(animateIx - previewIx - 1).Contains("GET"), "device animation follows preview ACK and GET readback");
        await ApplyBank(false); check(client.Status?.DemoAction == AnimationPreviewAction.Walk, "Preview on device also plays the selected action");
        int animations = demoTransport.AnimationRequests; await ApplyBank(true);
        check(demoTransport.AnimationRequests == animations + 1 && client.Status?.DemoAction == null && !animationPlayback.Running, "Save stops playback and saves only configuration; never starts an animation");
        await StartAnimation(AnimationPreviewAction.Happy); await StopAnimation();
        check(client.Status?.DemoAction == null && !animationPlayback.Running && client.Status?.Armed == false, "Stop is acknowledged and does not restore output ON");
        await StartAnimation(AnimationPreviewAction.Happy); UpdateLive();
        var timerView = previews.First(); string timerBefore = Pixels(timerView);
        blinkTimer.Start(); poll.Start(); await Task.Delay(420);
        check(animationPlayback.Remaining < 5700 && Pixels(timerView) != timerBefore, "actual WPF timer advances the adjacent preview even when daily animation is OFF");
        await Task.Delay(animationPlayback.Remaining + 30); TickAnimation(); await Refresh();
        blinkTimer.Stop(); poll.Stop();
        check(!animationPlayback.Running && client.Status?.DemoAction == null && client.Status?.DemoRemainingMilliseconds == 0, "real six-second device simulator and local preview expire together");
        demoTransport.SetKey(0, true); await Refresh(); await StartAnimation(AnimationPreviewAction.Blink);
        demoTransport.SetKey(0, false); await Refresh(); TickAnimation();
        check(!animationPlayback.Running, "releasing an already held physical key also cancels the preview");
        await StartAnimation(AnimationPreviewAction.Idle); demoTransport.TapHome(); await Refresh(); TickAnimation();
        check(!animationPlayback.Running, "fresh physical-input cancellation removes the local override");
        await StartAnimation(AnimationPreviewAction.Blink); EditProfile(p => p with { Mode = HomeMode.DeskPet }); await StopAnimation();
        check(!animationPlayback.Running && selectedAnimation == null, "mode edit cancels playback and stale action selection");
        await ReadDevice(); page = 2; await StartAnimation(AnimationPreviewAction.Tap);
        navigation[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await StopAnimation();
        check(page == 0 && !animationPlayback.Running, "leaving Appearance cancels local and device animation");
        page = 2; BuildPage(); await StartAnimation(AnimationPreviewAction.Sleep);
        client.StatusStaleAfter = TimeSpan.FromMilliseconds(10); await Task.Delay(30); TickAnimation(); await StopAnimation();
        check(!animationPlayback.Running, "stale status stops synchronized playback without replay");
        client.StatusStaleAfter = TimeSpan.FromSeconds(3); await Refresh();
        demoTransport.RejectNextAnimation = true; bool error = false;
        try { await StartAnimation(AnimationPreviewAction.Tap); } catch (HubProtocolException e) when (e.Code == "BUSY") { error = true; }
        check(error && !animationPlayback.Running && animationMessage.Contains("BUSY"), "device error is visible by preview and never reports success");
        await Refresh();
        demoTransport.CancelNextAnimationBeforeStatus = true; bool readbackCancelled = false;
        try { await StartAnimation(AnimationPreviewAction.Tap); } catch (OperationCanceledException) { readbackCancelled = true; }
        check(readbackCancelled && !animationPlayback.Running, "ACK followed by cancelled STATUS never reports playback started");
        // Persist Face, preview a walking body, revert, then perform an ordinary preview.
        EditProfile(p => p with { Mode = HomeMode.Face }); await ApplyBank(true);
        EditProfile(p => p with { Mode = HomeMode.DeskPet }); await StartAnimation(AnimationPreviewAction.Walk);
        await Revert(); int beforeRevertedPreview = demoTransport.AnimationRequests;
        await ApplyBank(false);
        check(Editing.Mode == HomeMode.Face && selectedAnimation == null && demoTransport.AnimationRequests == beforeRevertedPreview, "revert to Face clears residual Walk and ordinary preview remains usable");
        EditProfile(p => p with { Mode = HomeMode.DeskPet }); await StartAnimation(AnimationPreviewAction.Walk); await ReadDevice();
        check(selectedAnimation == null, "Read device also clears the obsolete selected action");
        demoTransport.DelayMilliseconds = 90;
        var busyOperation = RunOperation(() => StartAnimation(AnimationPreviewAction.Idle), "animation test");
        await Task.Delay(20); UpdateLive();
        check(busy && Descendants<Button>(content).First(b => b.Tag as string == "animation:Stop").IsEnabled &&
            content.Children.Cast<FrameworkElement>().Where(e => e.Name != "AppearancePoses").All(e => !e.IsEnabled), "while preparing, Stop remains accessible and editors cannot overwrite the in-flight draft");
        await StopAnimation(); await busyOperation;
        await ConnectDemo(); await ReadDevice(); EditProfile(p => p with { Mode = HomeMode.VirtualPet }); page = 2; BuildPage();
        // Cancel while the PREVIEW request is in flight: no obsolete ANIMATE may follow.
        demoTransport.DelayMilliseconds = 90; int commandsBefore = demoTransport.Commands.Count;
        var pending = StartAnimation(AnimationPreviewAction.Wake); await Task.Delay(15);
        CancelAnimationForContext(); page = 0; BuildPage();
        try { await pending; } catch (OperationCanceledException) { }
        await StopAnimation();
        check(!demoTransport.Commands.Skip(commandsBefore).Any(x => x == "ANIMATE WAKE") && !animationPlayback.Running, "navigation cancellation never sends queued obsolete action");
        check(!Connected, "cancelling an incomplete write requires an explicit fresh connection");
        demoTransport.DelayMilliseconds = 12; await ConnectDemo(); await ReadDevice(); page = 2; BuildPage();
        var old = new DemoTransport(animationPreview: false); demoTransport = old; await Attach(old); await ReadDevice();
        EditProfile(p => p with { Mode = HomeMode.DeskPet }); page = 2; BuildPage(); lines = old.SentLineCount;
        UpdateLive();
        check(Descendants<CheckBox>(content).First(c => c.Name == "AnimationDeviceSync") is { IsChecked: false, IsEnabled: false }, "unsupported animation peer has unchecked disabled device sync");
        await StartAnimation(AnimationPreviewAction.Walk);
        check(animationPlayback.Running && !animationDevice && old.SentLineCount == lines, "older firmware can preview locally with zero protocol writes");
        check(Descendants<TextBlock>(content).Any(t => t.Text.Contains("升级 R3C")), "older firmware has a visible upgrade explanation");
        await StopAnimation(); await Disconnect(); page = 2; BuildPage(); lines = old.SentLineCount;
        UpdateLive();
        check(Descendants<CheckBox>(content).First(c => c.Name == "AnimationDeviceSync") is { IsChecked: false, IsEnabled: false }, "disconnected animation target has unchecked disabled device sync");
        await StartAnimation(AnimationPreviewAction.Sleep);
        check(animationPlayback.Running && !animationDevice && old.SentLineCount == lines, "disconnected preview remains strictly local");
        await StopAnimation(); await ConnectDemo(); await ReadDevice();
        EditProfile(p => p with { Mode = HomeMode.VirtualPet, Theme = Theme.Ocean, Face = Face.Cat, Animation = false }); page = 2; BuildShell();
        UpdateLive();
        check(syncAnimation && Descendants<CheckBox>(content).First(c => c.Name == "AnimationDeviceSync") is { IsChecked: true, IsEnabled: true }, "supported reconnect restores preferred device sync visibly");
        foreach (bool zh in new[] { true, false })
        {
            chinese = zh; BuildShell(); await StartAnimation(AnimationPreviewAction.Tap); await Settle();
            Descendants<Border>(content).First(b => b.Name == "AppearancePoses").BringIntoView(); await Settle();
            string file = $"{(zh ? "zh" : "en")}-continuous-preview-playing.png"; CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
            check(Descendants<Button>(content).Any(b => b.Tag as string == "animation:Stop" && b.IsEnabled), "Stop stays available during active playback " + zh);
            await StopAnimation();
        }
        Width = 1000; Height = 680; chinese = true; BuildShell(); await Settle();
        Descendants<Border>(content).First(b => b.Name == "AppearancePoses").BringIntoView(); await Settle();
        CaptureShellForSmoke(Path.Combine(directory, "zh-actions-1000x680.png")); images.Add("zh-actions-1000x680.png");
        Width = 1280; Height = 800; scaleIndex = 2; BuildShell(); await Settle();
        Descendants<Border>(content).First(b => b.Name == "AppearancePoses").BringIntoView(); await Settle();
        CaptureShellForSmoke(Path.Combine(directory, "zh-actions-125percent.png")); images.Add("zh-actions-125percent.png");
        scaleIndex = 0; Width = 1280; Height = 800; BuildShell();
        chinese = true; selectedAnimation = null;
        await Control("COUNTERS RESET");
    }
}
