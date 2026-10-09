using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Security.Cryptography;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    async Task RunSmoke(string directory)
    {
        // No COM discovery or hardware transport is reachable from this mode.
        poll.Stop(); blinkTimer.Stop();
        var checks = new List<string>(); var images = new List<string>();
        Directory.CreateDirectory(directory);
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Smoke failed: " + name); checks.Add(name); }
        try
        {
            Check(demo && transport is DemoTransport && Fresh, "demo connects via protocol peer only");
            Check(client!.Status!.Armed == false, "default output OFF");
            Check(client.Status.GetInt32("battery_est_percent") == 75, "actual battery estimate field");
            Check(client.Capabilities is { ThemeCount: 8, FaceCount: 12, HomeModeCount: 3 }, "new peer advertises 8 themes 12 characters 3 modes");
            Check(CompanionSprites.Count == 60 && CompanionSprites.AssetHashes.Count == 60, "all 60 embedded production PNGs match their manifest SHA256");
            Check(CompanionPortraits.Count == 36 && CompanionPortraits.AssetHashes.Count == 36, "all 36 dedicated portrait PNGs match their manifest SHA256");
            page = 1; BuildShell(); await Settle();
            var nameEditor = Descendants<TextBox>(content).First();
            nameEditor.Text = "中文"; Check(invalidNames[editProfile] != null && !CanConfigure, "invalid ASCII name visibly blocks apply");
            nameEditor.Text = "MEDIA"; Check(invalidNames[editProfile] == null && Editing.Name == "MEDIA", "valid name editor commits draft");
            EditProfile(p => { var b = (HubBinding[])p.Bindings.Clone(); b[0] = new HubBinding(BindingKind.Keyboard, 0x68, 0xF0); return p with { Bindings = b }; });
            BuildPage(); await Settle();
            var keyEditor = Descendants<ComboBox>(content).First(x => x.Items.Count == 112);
            Check(keyEditor.SelectedItem?.ToString() == "F13", "F13 label and imported right modifiers represented");
            keyEditor.SelectedIndex = 0x73 - 4;
            Check(Editing.Bindings[0].Modifiers == 0xF0 && Editing.Bindings[0].Value == 0x73, "editing key preserves all right-side modifier bits");
            await ReadDevice();
            await RunHotkeySmoke(directory, Check, images);
            await RunDisplaySmoke(directory, Check, images);
            await RunCustomDisplaySmoke(directory, Check, images);
            foreach (var command in new[] { "PAGE DIAG", "PAGE INFO", "PAGE ACTIVITY", "PAGE HOME", "ROUTE BLE", "ROUTE USB", "LANGUAGE EN", "LANGUAGE ZH", "COUNTERS RESET", "OUTPUT 0" })
            { await Control(command); checks.Add("confirmed " + command); }
            await Control("PAGE DIAG"); bool armDenied = false;
            try { await Control("OUTPUT 1"); } catch (HubProtocolException e) when (e.Code == "NOT_READY") { armDenied = true; }
            Check(armDenied && client.Status?.Armed != true, "diagnostic page denies output ON");
            await Control("PAGE HOME"); await Control("OUTPUT 1"); await Control("LANGUAGE EN");
            Check(client.Status?.Armed == true, "R3E language change preserves output ON");
            EditProfile(p => p with { Name = "MY DESK", Theme = Theme.Lavender, Face = Face.Cat });
            var requested = BankCodec.ToHex(draft); await ApplyBank(false);
            Check(client.Configuration?.Preview == true && BankCodec.ToHex(client.Configuration.Bank) == requested, "preview ACK and GET exact bank");
            Check(client.Status?.Armed == true, "preview preserves output ON");
            await ApplyBank(true); Check(client.Configuration?.Preview == false && client.Status?.Armed == true, "save ACK and readback persisted with output ON");
            editProfile = 2; EditProfile(p => p with { Theme = Theme.Terminal, Face = Face.Robot }); await ApplyBank(false);
            Check(client.Configuration?.Bank.ActiveProfile == 2 && client.Configuration.Bank.Profiles[2].Face == Face.Robot, "preview activates edited third profile and appearance");
            await Revert();
            editProfile = 1; await SelectProfile(); Check(client.Configuration?.Bank.ActiveProfile == 1 && client.Status!.Armed, "select profile preserves output ON");
            await Revert(); Check(client.Configuration?.Bank.ActiveProfile == 0, "revert restores saved profile");
            EditProfile(p => p with { Theme = Theme.Blueprint, Face = Face.Wink, Mode = HomeMode.VirtualPet, Animation = false });
            await ApplyBank(false);
            Check(client.Configuration?.Bank.Profiles[0] is { Theme: Theme.Blueprint, Face: Face.Wink, Mode: HomeMode.VirtualPet, Animation: false }, "new style and virtual-pet mode ACK and exact readback");
            demoTransport!.SetKey(0, true); await Refresh();
            Check(client.Status?.GetUInt32("pet_interactions") == 1 && client.Status.GetBool("pet_active") == true, "physical press reacts in virtual-pet mode with animation OFF");
            Check(client.Status?.GetInt32("pet_motion") == 1, "physical press reports happy motion");
            demoTransport.SetKey(0, false); await Refresh();
            Check(client.Status?.GetUInt32("pet_interactions") == 1, "release does not add a pet interaction");
            await Control("PAGE INFO"); Check(client.Status?.GetUInt32("pet_interactions") == 1, "Hub controls do not add pet interactions");
            demoTransport.Turn(-2); await Refresh();
            Check(client.Status?.GetUInt32("pet_interactions") == 3 && client.Status.GetInt32("pet_motion") == 2, "rotary detents report walk motion and count every detent");
            uint? sentBeforeTap = client.Status!.GetUInt32("sent"); bool armedBeforeTap = client.Status.Armed;
            demoTransport.TapHome(); await Refresh();
            Check(client.Status?.GetUInt32("pet_interactions") == 4 && client.Status.GetUInt32("pet_taps") == 1 && client.Status.GetInt32("pet_effect") == 1, "simulated character tap reports one interaction and one tap");
            Check(client.Status?.GetUInt32("sent") == sentBeforeTap && client.Status?.Armed == armedBeforeTap, "simulated character tap preserves output and HID sent count");
            demoTransport.SimulateIdleFor(TimeSpan.FromSeconds(30)); await Refresh();
            Check(client.Status?.GetBool("pet_sleeping") == true && client.Status.GetInt32("pet_motion") == 0, "simulated 30-second physical idle reports sleeping");
            await Control("COUNTERS RESET"); Check(client.Status?.GetBool("pet_sleeping") == true, "counter reset does not wake a sleeping companion");
            await Control("PAGE HOME"); Check(client.Status?.GetBool("pet_sleeping") == true, "Hub navigation does not wake companion");
            demoTransport.SetKey(5, true); await Refresh(); Check(client.Status?.GetBool("pet_sleeping") == false, "simulated physical input wakes companion");
            demoTransport.SetKey(5, false);
            await Control("COUNTERS RESET"); Check(client.Status?.GetUInt32("pet_interactions") == 0 && client.Status.GetBool("pet_active") == false, "counter reset clears session pet activity");
            await Control("PAGE HOME"); await Revert();
            string local = Path.Combine(directory, "roundtrip-profiles.json"); LocalConfigurationStore.SaveAtomic(local, draft);
            Check(BankCodec.ToHex(LocalConfigurationStore.Load(local)) == BankCodec.ToHex(draft), "local JSON disk roundtrip");
            string beforeLocal = BankCodec.ToHex(draft); EditProfile(p => p with { Name = "LOCAL DRAFT" });
            string pending = BankCodec.ToHex(draft); await Disconnect(); await ConnectDemo();
            Check(dirty && BankCodec.ToHex(draft) == pending && draftRevision != null, "dirty draft survives disconnect and new handshake");
            await ReadDevice(); Check(!dirty && BankCodec.ToHex(draft) != pending, "explicit read replaces draft");
            // A competing controller changed the device. Matching STALE must not be advertised as a save.
            uint previous = draftRevision!.Value;
            await demoTransport!.SendLineAsync($"HUB 2000000000 SELECT {previous} 1", lifetime.Token);
            bool staleRejected = false;
            try { await client.PreviewAsync(draft, previous, lifetime.Token); }
            catch (HubProtocolException e) when (e.Code == "STALE") { staleRejected = true; }
            Check(staleRejected && client.Configuration == null, "stale write rejected and cached configuration invalidated");
            await ReadDevice(); await Revert();
            // Missing capability fields describe the original R3, not the latest artwork set.
            var oldPeer = new DemoTransport(4, 4, 1, advertiseCapabilities: false);
            demoTransport = oldPeer; await Attach(oldPeer); await ReadDevice();
            Check(client!.Capabilities is { ThemeCount: 4, FaceCount: 4, HomeModeCount: 1 }, "old HELLO defaults to 4 themes 4 faces and Face mode");
            EditProfile(p => p with { Theme = Theme.Ocean });
            bool blockedTheme = false;
            try { await ApplyBank(false); } catch (HubProtocolException e) when (e.Code == "UNSUPPORTED_STYLE") { blockedTheme = true; }
            Check(blockedTheme && !CanApplyDraft && oldPeer.ConfigurationWriteRequests == 0, "new theme blocked before send on old peer");
            EditProfile(p => p with { Theme = Theme.Midnight, Face = Face.Panda });
            bool blockedFace = false;
            try { await ApplyBank(true); } catch (HubProtocolException e) when (e.Code == "UNSUPPORTED_STYLE") { blockedFace = true; }
            Check(blockedFace && !CanApplyDraft && oldPeer.ConfigurationWriteRequests == 0, "new character save blocked before send on old peer");
            EditProfile(p => p with { Face = Face.Pixel, Mode = HomeMode.DeskPet });
            bool blockedMode = false;
            try { await ApplyBank(false); } catch (HubProtocolException e) when (e.Code == "UNSUPPORTED_STYLE") { blockedMode = true; }
            Check(blockedMode && !CanApplyDraft && oldPeer.ConfigurationWriteRequests == 0, "pet mode blocked before send on old peer");
            page = 2; chinese = true; BuildShell(); await Settle();
            Check(Descendants<CheckBox>(content).First(c => c.Name == "AnimationDeviceSync") is { IsChecked: false, IsEnabled: false }, "old firmware shows device animation sync unchecked and disabled");
            SetNotice("连接已断开，需重新读取配置；不会重放命令。");
            CaptureShellForSmoke(Path.Combine(directory, "zh-appearance-old-firmware.png")); images.Add("zh-appearance-old-firmware.png");
            Check(noticeText.Text.Contains("模拟设备已连接") && !noticeText.Text.Contains("连接已断开"), "connected evidence screenshot replaces stale disconnect notice with explicit demo state");
            await ReadDevice(); await ApplyBank(false); Check(oldPeer.ConfigurationWriteRequests == 1, "old styles remain writable on old R3");
            await Revert(); await ConnectDemo(); await ReadDevice();
            EditProfile(p => p with { Theme = Theme.Ocean, Face = Face.Bunny, Mode = HomeMode.VirtualPet });
            page = 2; BuildShell(); await Settle();
            await RunAnimationSmoke(directory, Check, images);
            await RunInteractionSmoke(directory, Check, images);
            selectedAnimation = null; BuildShell();
            demoTransport.SetKey(2, true); await Refresh();
            Check(client.Status?.KeysDown?[2] == true && client.Status.KeyPresses?[2] == 1, "physical key highlight data updates");
            for (int language = 0; language < 2; language++)
            {
                chinese = language == 0;
                for (int p = 0; p < 4; p++)
                {
                    await Refresh(); page = p; BuildShell(); await Settle();
                    string file = $"{(chinese ? "zh" : "en")}-{new[] { "device", "controls", "appearance", "diagnostics" }[p]}.png";
                    CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
                    if (p == 1)
                    {
                        FindVisual<ScrollViewer>(content)?.ScrollToEnd(); await Settle();
                        file = $"{(chinese ? "zh" : "en")}-controls-lower.png"; CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
                    }
                    if (p == 2)
                    {
                        foreach (var section in new[] { ("AppearancePoses", "poses"), ("AppearanceThemes", "themes"), ("AppearanceCharacters", "characters") })
                        {
                            Descendants<Border>(content).First(b => b.Name == section.Item1).BringIntoView(); await Settle();
                            file = $"{(chinese ? "zh" : "en")}-appearance-{section.Item2}.png"; CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
                        }
                    }
                }
            }
            // Render every supported combination through the actual WPF implementation.
            HomeMode previousMode = Editing.Mode; bool previousLanguage = chinese; int previousPage = page;
            EditProfile(p => p with { Mode = HomeMode.Face }); chinese = true; page = 2; BuildShell(); await Settle();
            Descendants<Border>(content).First(b => b.Name == "AppearanceCharacters").BringIntoView(); await Settle();
            CaptureShellForSmoke(Path.Combine(directory, "zh-appearance-face-characters.png")); images.Add("zh-appearance-face-characters.png");
            EditProfile(p => p with { Mode = previousMode }); chinese = previousLanguage; page = previousPage; BuildShell();
            for (int mode = 0; mode < 3; mode++) for (int t = 0; t < 8; t++) for (int f = 0; f < 12; f++)
            {
                var visual = new DevicePreview { Width = 320, Height = 240, Theme = t, Face = f, Mode = mode, Profile = "MEDIA", Chinese = true, Connected = true, Battery = 75, PetInteractions = 0, PetActive = false };
                visual.Measure(new Size(320, 240)); visual.Arrange(new Rect(0, 0, 320, 240)); visual.UpdateLayout();
                string file = $"preview-m{mode}-t{t}-f{f}.png"; Capture(visual, Path.Combine(directory, file)); images.Add(file);
            }
            for (int f = 0; f < 12; f++)
            {
                var active = new DevicePreview { Width = 320, Height = 240, Theme = 4, Face = f, Mode = 2, Profile = "MEDIA", Chinese = true, Connected = true, Battery = 75, PetInteractions = 12, PetActive = true, PetMotion = 1, Animate = false };
                active.Measure(new Size(320, 240)); active.Arrange(new Rect(0, 0, 320, 240)); active.UpdateLayout();
                string file = $"pet-active-f{f}.png"; Capture(active, Path.Combine(directory, file)); images.Add(file);
                var hashes = Enumerable.Range(0, 3).Select(m => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, $"preview-m{m}-t4-f{f}.png"))))).Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, file))))).ToArray();
                Check(hashes.Distinct().Count() == 4, $"character {f}: face, full-body, virtual-pet and active reaction render distinctly");
            }
            foreach (int f in Enumerable.Range(0, 12))
            {
                foreach (string frame in CompanionPortraits.Frames)
                {
                    var portrait = new DevicePreview { Width = 320, Height = 240, Theme = 4, Face = f, Mode = 0, Chinese = true, Connected = true, Battery = 75, ForcedFrame = frame };
                    portrait.Measure(new Size(320, 240)); portrait.Arrange(new Rect(0, 0, 320, 240)); portrait.UpdateLayout();
                    string portraitFile = $"portrait-f{f}-{frame}.png"; Capture(portrait, Path.Combine(directory, portraitFile)); images.Add(portraitFile);
                }
                foreach (string frame in CompanionSprites.Frames)
                {
                    var pose = new DevicePreview { Width = 320, Height = 240, Theme = 4, Face = f, Mode = 2, Chinese = true, Connected = true, Battery = 75, ForcedFrame = frame };
                    pose.Measure(new Size(320, 240)); pose.Arrange(new Rect(0, 0, 320, 240)); pose.UpdateLayout();
                    string file = $"sprite-f{f}-{frame}.png"; Capture(pose, Path.Combine(directory, file)); images.Add(file);
                }
                var state = new DevicePreview { Face = f, Mode = 2, Animate = false, PetMotion = 1, PetActive = true };
                Check(state.SpriteFrame == "happy", $"character {f}: animation OFF preserves static happy feedback");
                state.PetMotion = 2; Check(state.SpriteFrame == "idle", $"character {f}: animation OFF suppresses walking");
                state.ForcedFrame = "blink"; Check(state.SpriteFrame == "idle", $"character {f}: animation OFF suppresses local blink");
                state.ForcedFrame = null; state.Animate = true; state.IdleFrame = 0;
                Check(state.SpriteFrame == "walk_a", $"character {f}: rotary frame A");
                state.IdleFrame = 1; Check(state.SpriteFrame == "walk_b", $"character {f}: rotary frame B");
                state.Mode = 0; Check(state.SpriteFrame == "idle", $"character {f}: face mode does not walk");
                foreach (int mode in Enumerable.Range(0, 3)) foreach (bool animate in new[] { false, true })
                {
                    foreach (string effect in new[] { "sleep", "tap" })
                    {
                        var effectView = new DevicePreview { Width = 320, Height = 240, Theme = 4, Face = f, Mode = mode, Animate = animate, Chinese = true, ForcedFrame = effect };
                        Check(effectView.SpriteFrame == (effect == "sleep" ? "blink" : "happy"), $"character {f} mode {mode} animation {animate}: static {effect} state");
                        effectView.Measure(new Size(320, 240)); effectView.Arrange(new Rect(0, 0, 320, 240)); effectView.UpdateLayout();
                        string effectFile = $"effect-m{mode}-f{f}-{effect}-animation{(animate ? 1 : 0)}.png"; Capture(effectView, Path.Combine(directory, effectFile)); images.Add(effectFile);
                    }
                }
            }
            demoTransport.SetKey(2, false); await Refresh(); Check(client.Status?.KeyReleases?[2] == 1, "release edge clears physical key highlight");
            // Regression: tap then turn within 1200ms must retain a heart, not a
            // Happy pose. Compare the real WPF sprite pixels against the same turn
            // without the effect; independently verify the heart pixels differ.
            foreach (int mode in Enumerable.Range(0, 3)) foreach (bool animate in new[] { false, true }) foreach (int gait in new[] { 0, 1 })
            {
                DevicePreview Mixed(bool heart) => new() { Width = 320, Height = 240, Theme = 4, Face = 2, Mode = mode, Animate = animate, PetActive = true, PetMotion = 2, PetHeart = heart, IdleFrame = gait };
                var withHeart = Mixed(true); var reference = Mixed(false);
                string expectedFrame = animate && mode > 0 ? gait == 0 ? "walk_a" : "walk_b" : "idle";
                Check(withHeart.SpriteFrame == expectedFrame, $"tap then turn: mode {mode} animation {animate} gait {gait} frame priority");
                var actualBitmap = RenderNativePreview(withHeart); var referenceBitmap = RenderNativePreview(reference);
                static byte[] Region(BitmapSource bitmap, Int32Rect rectangle)
                { var pixels = new byte[rectangle.Width * rectangle.Height * 4]; bitmap.CopyPixels(rectangle, pixels, rectangle.Width * 4, 0); return pixels; }
                var spriteRegion = new Int32Rect(mode == 0 ? 112 : 128, 88, mode == 0 ? 96 : 64, 60);
                Check(Region(actualBitmap, spriteRegion).SequenceEqual(Region(referenceBitmap, spriteRegion)), $"tap then turn: mode {mode} animation {animate} gait {gait} actual sprite pixels match no-effect turn");
                var heartRegion = new Int32Rect(220, 79, 16, 12);
                Check(!Region(actualBitmap, heartRegion).SequenceEqual(Region(referenceBitmap, heartRegion)), $"tap then turn: mode {mode} animation {animate} gait {gait} actual heart remains visible");
                string file = $"mixed-heart-turn-m{mode}-animation{(animate ? 1 : 0)}-gait{gait}.png";
                Capture(withHeart, Path.Combine(directory, file)); images.Add(file);
            }
            client.StatusStaleAfter = TimeSpan.FromMilliseconds(20); await Task.Delay(60); UpdateLive();
            Check(!Fresh && outputPill!.Text.Contains("UNKNOWN"), "stale output is unknown");
            CaptureShellForSmoke(Path.Combine(directory, "en-diagnostics-stale.png")); images.Add("en-diagnostics-stale.png");
            Check(noticeText.Text.Contains("status stale") && !noticeText.Text.Contains("Demo peer connected"), "stale screenshot states stale instead of a current connection claim");
            client.StatusStaleAfter = TimeSpan.FromSeconds(3); await Refresh();
            Width = 1000; Height = 680; chinese = true; page = 2; BuildShell(); await Settle();
            CaptureShellForSmoke(Path.Combine(directory, "zh-appearance-1000x680.png")); images.Add("zh-appearance-1000x680.png");
            page = 1; scaleIndex = 2; BuildShell(); await Settle();
            CaptureShellForSmoke(Path.Combine(directory, "zh-controls-125percent.png")); images.Add("zh-controls-125percent.png");
            checks.Add("four pages rendered EN/ZH; 288 mode-theme-character previews; 36 dedicated portraits; 60 body poses; all-mode sleep/tap with animation ON/OFF; compact and scaled layouts");
            await Disconnect(); Check(client.Status == null && !Fresh && draftRevision == null, "disconnect clears fresh data and write revision");
            await VerifyClosing(false); checks.Add("unconnected window Closing event reaches Closed without reentrancy");
            await VerifyClosing(true); checks.Add("connected demo window closes transport and reaches Closed");
            var report = new { passed = true, hardware_access = false, checks, images, production_manifest_sha256 = CompanionSprites.ManifestHash, embedded_production_png_sha256 = CompanionSprites.AssetHashes, portrait_manifest_sha256 = CompanionPortraits.ManifestHash, embedded_portrait_png_sha256 = CompanionPortraits.AssetHashes, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription };
            await File.WriteAllTextAsync(Path.Combine(directory, "smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 0;
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "smoke.json"), JsonSerializer.Serialize(new { passed = false, hardware_access = false, checks, images, error = e.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally { closing = true; if (client != null) await client.DisposeAsync(); Close(); }
    }
    static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(60); }
    static async Task VerifyClosing(bool connected)
    {
        var window = new MainWindow(true, null, deferStartupForTest: true)
        {
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Opacity = 0
        };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show(); await Settle();
        if (connected) await window.ConnectDemo();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (window.client?.State is HubConnectionState.Ready or HubConnectionState.Legacy)
            throw new InvalidOperationException("Closing left the demo transport connected.");
    }
    static T? FindVisual<T>(DependencyObject from) where T : DependencyObject
    { DependencyObject? item = from; while (item != null) { if (item is T t) return t; item = VisualTreeHelper.GetParent(item); } return null; }
    static IEnumerable<T> Descendants<T>(DependencyObject from) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++) { var child = VisualTreeHelper.GetChild(from, i); if (child is T item) yield return item; foreach (var next in Descendants<T>(child)) yield return next; } }
    void CaptureShellForSmoke(string path)
    {
        // Smoke calls operations directly instead of RunOperation; its previous
        // failure notices are test history, not the connection in this image.
        if (!demo) throw new InvalidOperationException("Demo screenshots cannot capture a hardware session.");
        SetNotice(!Connected ? T("Offline demo · not connected; no hardware accessed.", "离线演示：尚未连接模拟设备，未访问硬件。") :
            !Fresh ? T("Offline demo · status stale; no hardware accessed.", "离线演示：模拟状态已过期，未访问硬件。") :
            T("Offline demo · Demo peer connected; no hardware accessed.", "离线演示：模拟设备已连接，未访问硬件。"));
        UpdateLive(); Capture(shell, path);
    }
    static void Capture(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    static RenderTargetBitmap RenderNativePreview(DevicePreview visual)
    {
        visual.Measure(new Size(320, 240)); visual.Arrange(new Rect(0, 0, 320, 240)); visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }

    // Explicit root-only bench entrypoint. This method is never called by --demo/render smoke.
    async Task RunDeviceSmoke(string directory)
    {
        poll.Stop(); Directory.CreateDirectory(directory);
        var steps = new List<object>(); bool previewAttempted = false, verified = false;
        string? original = null, error = null;
        try
        {
            var ports = await Task.Run(FeatherDiscovery.Scan);
            if (ports.Length != 1) throw new InvalidOperationException("Device smoke requires exactly one verified VID_239A/PID_8029 port; no selection is guessed.");
            selectedPort = ports[0]; await ConnectSelected();
            if (client!.IsLegacy) throw new InvalidOperationException("Device smoke requires protocol 1; legacy is read-only here.");
            if (client.Configuration is not { Preview: false } initial) throw new InvalidOperationException("Start from a persisted bank (preview=0); smoke will not replace an existing preview.");
            original = BankCodec.ToHex(initial.Bank);
            steps.Add(new { step = "connect_get_status", build = client.Capabilities?.Build, revision = initial.Revision, armed = client.Status?.Armed });
            await Control("OUTPUT 0");
            var temporary = BankCodec.Clone(initial.Bank); int ix = temporary.ActiveProfile;
            temporary.Profiles[ix] = temporary.Profiles[ix] with { Theme = (Theme)(((int)temporary.Profiles[ix].Theme + 1) % 4),
                Mode = client.Capabilities?.SupportsAnimationPreview == true ? HomeMode.DeskPet : temporary.Profiles[ix].Mode };
            previewAttempted = true;
            var previewResult = await client.PreviewAsync(temporary, initial.Revision, lifetime.Token);
            steps.Add(new { step = "preview_ack_get_verified", revision = previewResult.Revision, preview = previewResult.Preview, armed = client.Status?.Armed });
            if (client.Capabilities?.SupportsAnimationPreview == true)
            {
                var animated = await client.StartAnimationPreviewAsync(AnimationPreviewAction.Walk, lifetime.Token);
                if (animated.DemoAction != AnimationPreviewAction.Walk || animated.DemoRemainingMilliseconds is not > 0 || animated.Armed)
                    throw new InvalidOperationException("Walk animation was not active/OFF at fresh readback.");
                uint firstRemaining = animated.DemoRemainingMilliseconds.Value;
                await Task.Delay(300, lifetime.Token);
                var advanced = await client.RefreshStatusAsync(lifetime.Token);
                if (advanced.DemoAction != AnimationPreviewAction.Walk || advanced.DemoRemainingMilliseconds is not > 0 || advanced.DemoRemainingMilliseconds >= firstRemaining || advanced.Armed)
                    throw new InvalidOperationException("Walk countdown did not advance with output OFF.");
                var stopped = await client.StopAnimationPreviewAsync(lifetime.Token);
                if (stopped.DemoAction != null || stopped.DemoRemainingMilliseconds != 0 || stopped.Armed)
                    throw new InvalidOperationException("Animation STOP was not confirmed.");
                steps.Add(new { step = "animation_walk_countdown_stop_verified", first_remaining_ms = firstRemaining, later_remaining_ms = advanced.DemoRemainingMilliseconds, stopped = true, armed = false, save_used = false });
            }
            var reverted = await client.RevertAsync(lifetime.Token);
            if (BankCodec.ToHex(reverted.Bank) != original || reverted.Preview) throw new InvalidOperationException("Revert differs from original persisted bank.");
            steps.Add(new { step = "revert_original_verified", revision = reverted.Revision });
            await Disconnect(); await ConnectSelected();
            if (BankCodec.ToHex(client!.Configuration!.Bank) != original || client.Configuration.Preview || client.Status!.Armed) throw new InvalidOperationException("Reconnect readback/output differs.");
            steps.Add(new { step = "reconnect_readonly_verified", revision = client.Configuration.Revision, armed = client.Status.Armed }); verified = true;
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            try
            {
                if (client is not null && Connected)
                {
                    await client.ControlAsync("OUTPUT 0", lifetime.Token);
                    if (previewAttempted)
                    {
                        var restored = await client.RevertAsync(lifetime.Token);
                        if (original != null && BankCodec.ToHex(restored.Bank) != original) throw new InvalidOperationException("Final cleanup differs from original bank.");
                    }
                    steps.Add(new { step = "final_output_off_and_restore", armed = client.Status?.Armed });
                }
                else if (previewAttempted) throw new IOException("Connection unavailable during cleanup; device state must be checked manually.");
            }
            catch (Exception e) { verified = false; error = (error ?? "") + "\nCLEANUP: " + e.Message; }
            if (client != null) await client.DisposeAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "device-smoke.json"), JsonSerializer.Serialize(new { passed = verified && error == null, hardware_access = true, save_used = false, output_on_used = false, steps, error }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = verified && error == null ? 0 : 1; closing = true; Close();
        }
    }
}
