using DeskBuddy.Core;
using System.IO;

namespace DeskBuddy.App;

// A deterministic protocol peer for offline UI review. It never enumerates or opens COM.
public sealed class DemoTransport(int themeCount = 8, int faceCount = 12, int homeModeCount = 3, bool advertiseCapabilities = true, bool animationPreview = true, bool bleManagement = true, bool preserveOutput = true, bool preservePageOutput = true, bool displaySleep = true, bool backlightControl = false, bool sleepBinding = true, bool displayCustom = true) : IManagementTransport
{
    public ManagementChannel Channel => ManagementChannel.Usb;
    public event Action<string>? LineReceived;
    public event Action<Exception?>? Disconnected;
    HubBank bank = HubDefaults.CreateBank(), saved = HubDefaults.CreateBank();
    uint revision = 1;
    bool open, preview, armed;
    string route = "USB", language = "EN";
    readonly uint[] presses = new uint[6], releases = new uint[6];
    int activeKey = -1;
    int devicePage, cw = 20, ccw = 20, knob = 10, markers = 2;
    uint petInteractions;
    int petMotion, petDirection;
    bool KeepOutput => advertiseCapabilities && preserveOutput;
    bool KeepPageOutput => advertiseCapabilities && preservePageOutput;
    bool HidSuppressed => displayAsleep || WakeIsolated || KeepPageOutput && devicePage is 2 or 3 or 5;
    bool DisplaySupported => advertiseCapabilities && displaySleep;
    bool displayAsleep, displaySaved;
    uint displayUsbSeconds = 300, displayBatterySeconds = 60, displayWakeCount;
    long displayActivityAt = Environment.TickCount64;
    long displayWakeQuietUntil;
    int wakeHeldKey = -1;
    bool WakeIsolated => wakeHeldKey >= 0 || Environment.TickCount64 < displayWakeQuietUntil;
    public bool UsbPowered { get; set; } = true;
    public string? RejectNextDisplay { get; set; }
    public int DisplaySaveRequests { get; private set; }
    uint DisplayIdle => (uint)Math.Clamp(Environment.TickCount64 - displayActivityAt, 0, uint.MaxValue);
    void SleepScreen() { displayAsleep = true; CancelDemo(); }
    void WakeScreen() { if (displayAsleep) { displayWakeCount++; displayWakeQuietUntil = Environment.TickCount64 + 200; } displayAsleep = false; displayActivityAt = Environment.TickCount64; }
    void TickScreen()
    {
        uint timeout = UsbPowered ? displayUsbSeconds : displayBatterySeconds;
        if (DisplaySupported && !displayAsleep && !WakeIsolated && devicePage != 3 && timeout != 0 && DisplayIdle >= timeout * 1000) SleepScreen();
    }
    public void SimulateDisplayIdleFor(TimeSpan duration) { displayActivityAt = Environment.TickCount64 - (long)duration.TotalMilliseconds; TickScreen(); }
    string DisplayFields(bool includeArmed = true) => $"display_asleep={(displayAsleep ? 1 : 0)} display_usb_s={displayUsbSeconds} display_battery_s={displayBatterySeconds} display_saved={(displaySaved ? 1 : 0)} display_backlight={(backlightControl ? 1 : 0)} display_wake_count={displayWakeCount} display_idle_ms={DisplayIdle}" + (includeArmed ? $" armed={(armed ? 1 : 0)}" : "");
    bool RestBinding(int index) => DisplaySupported && sleepBinding && bank.Profiles[bank.ActiveProfile].Bindings[index] is { Kind: BindingKind.Local, Value: 5 };
    DateTimeOffset? lastPetInteraction;
    DateTimeOffset lastPhysicalActivity = DateTimeOffset.UtcNow;
    DateTimeOffset? lastPetTap;
    uint petTaps;
    int demoAction;
    long demoDeadline;
    long managementWindowDeadline;
    public int SaveRequests { get; private set; }
    public int AnimationRequests { get; private set; }
    public int DelayMilliseconds { get; set; } = 12;
    public bool RejectNextAnimation { get; set; }
    public bool CancelNextAnimationBeforeStatus { get; set; }
    public List<string> Commands { get; } = [];
    int DemoRemaining => (int)Math.Clamp(demoDeadline - Environment.TickCount64, 0, 6000);
    int DemoAction => DemoRemaining > 0 ? demoAction : 0;
    void CancelDemo() { demoAction = 0; demoDeadline = 0; }
    bool Sleeping => DateTimeOffset.UtcNow - lastPhysicalActivity >= TimeSpan.FromSeconds(30);
    public int ConfigurationWriteRequests { get; private set; }
    public int SentLineCount { get; private set; }
    public Task OpenAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); open = true; return Task.CompletedTask; }
    public Task CloseAsync() { open = false; armed = false; Disconnected?.Invoke(null); return Task.CompletedTask; }
    public void SetKey(int key, bool down)
    {
        TickScreen();
        if (wakeHeldKey >= 0) { if (!down && key == wakeHeldKey) { wakeHeldKey = -1; displayWakeQuietUntil = Environment.TickCount64 + 200; } return; }
        if (displayAsleep && down) { WakeScreen(); wakeHeldKey = key; return; }
        if (WakeIsolated) { if (down) wakeHeldKey = key; displayWakeQuietUntil = Environment.TickCount64 + 200; return; }
        displayActivityAt = Environment.TickCount64;
        if (down && activeKey != key) { CancelDemo(); activeKey = key; presses[key]++; petInteractions++; lastPetInteraction = lastPhysicalActivity = DateTimeOffset.UtcNow; petMotion = 1; }
        if (!down && activeKey == key) { CancelDemo(); activeKey = -1; releases[key]++; }
        if (down && RestBinding(key)) SleepScreen();
    }
    public void Turn(int delta)
    {
        if (delta == 0) return; CancelDemo();
        TickScreen(); if (displayAsleep) { WakeScreen(); return; }
        if (WakeIsolated) { displayWakeQuietUntil = Environment.TickCount64 + 200; return; }
        displayActivityAt = Environment.TickCount64;
        if (delta > 0) cw += delta; else ccw -= delta;
        petInteractions += (uint)Math.Abs(delta); lastPetInteraction = lastPhysicalActivity = DateTimeOffset.UtcNow; petMotion = 2; petDirection = Math.Sign(delta);
        if (RestBinding(delta > 0 ? 6 : 7)) SleepScreen();
    }
    public void TapHome() { TickScreen(); if (displayAsleep) { WakeScreen(); return; } if (WakeIsolated) { displayWakeQuietUntil = Environment.TickCount64 + 200; return; } displayActivityAt = Environment.TickCount64; CancelDemo(); petInteractions++; petTaps++; lastPetTap = lastPetInteraction = lastPhysicalActivity = DateTimeOffset.UtcNow; petMotion = 1; }
    public void SimulateIdleFor(TimeSpan duration) { lastPhysicalActivity = DateTimeOffset.UtcNow - duration; lastPetInteraction = lastPetTap = null; }
    public async Task SendLineAsync(string line, CancellationToken token)
    {
        if (!open) throw new IOException("Demo connection closed.");
        SentLineCount++;
        await Task.Delay(DelayMilliseconds, token); token.ThrowIfCancellationRequested(); if (!open) throw new IOException("Demo closed before send.");
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3 || p[0] != "HUB") return;
        string id = p[1], result; Commands.Add(p[2] + (p[2] is "ANIMATE" or "CONTROL" or "DISPLAY" ? " " + string.Join(" ", p.Skip(3)) : ""));
        TickScreen();
        try
        {
            switch (p[2])
            {
                case "HELLO": result = $"proto=1 build=DEMO_R3H config_bytes=224 profiles=3 bindings=12 revision={revision} preview={(preview ? 1 : 0)}" + (advertiseCapabilities ? $" theme_count={themeCount} face_count={faceCount} home_mode_count={homeModeCount} animation_preview={(animationPreview ? 1 : 0)} ble_management={(bleManagement ? 1 : 0)} preserve_output={(preserveOutput ? 1 : 0)} pet_input_feedback=1 preserve_page_output={(preservePageOutput ? 1 : 0)} display_sleep={(displaySleep ? 1 : 0)} backlight_control={(displaySleep && backlightControl ? 1 : 0)} sleep_binding={(displaySleep && sleepBinding ? 1 : 0)} display_custom={(displaySleep && displayCustom ? 1 : 0)}" + (bleManagement ? " ble_manage_idle_ms=15000" : "") : ""); break;
                case "DISPLAY":
                    if (!DisplaySupported) { LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED"); return; }
                    if (RejectNextDisplay is { } displayError) { RejectNextDisplay = null; LineReceived?.Invoke($"HUB {id} ERR code={displayError}"); return; }
                    if (p.Length == 4 && p[3] == "GET") { }
                    else if (p.Length == 6 && p[3] == "SET" && uint.TryParse(p[4], out uint u) && uint.TryParse(p[5], out uint b) && DisplaySettings.IsAllowedTimeout(u) && DisplaySettings.IsAllowedTimeout(b))
                    {
                        if (!displayCustom && (!DisplaySettings.IsLegacyTimeout(u) || !DisplaySettings.IsLegacyTimeout(b))) { LineReceived?.Invoke($"HUB {id} ERR code=INVALID"); return; }
                        if (!displaySaved || u != displayUsbSeconds || b != displayBatterySeconds) DisplaySaveRequests++;
                        displayUsbSeconds = u; displayBatterySeconds = b; displaySaved = true; WakeScreen();
                    }
                    else if (p.Length == 4 && p[3] == "SLEEP") SleepScreen();
                    else if (p.Length == 4 && p[3] == "WAKE") WakeScreen();
                    else { LineReceived?.Invoke($"HUB {id} ERR code=INVALID"); return; }
                    result = DisplayFields(); break;
                case "GET": result = $"revision={revision} preview={(preview ? 1 : 0)} hex={BankCodec.ToHex(bank)}"; break;
                case "STATUS":
                    if (CancelNextAnimationBeforeStatus && DemoAction > 0) { CancelNextAnimationBeforeStatus = false; CancelDemo(); }
                    int remaining = DemoRemaining, actionNow = remaining > 0 ? demoAction : 0;
                    int managementRemaining = (int)Math.Clamp(managementWindowDeadline - Environment.TickCount64, 0, 60000);
                    result = $"build=DEMO_R3H route={route} connected=1 armed={(armed ? 1 : 0)} usb_connected=1 ble_connected=1 language={language} language_saved=1 calibrated=1 stored=1 page={devicePage} hid_suppressed={(HidSuppressed ? 1 : 0)} uptime_ms=42000 cw={cw} ccw={ccw} presses={knob} switch_down=0 sent=12 dropped=0 keys_down={new string(Enumerable.Range(0, 6).Select(i => i == activeKey ? '1' : '0').ToArray())} key_presses={string.Join(',', presses)} key_releases={string.Join(',', releases)} markers={markers} vbat_est_mV=3940 vbat_sampled=1 vbat_age_ms=200 usb_vbus=1 battery_est_percent=75 config_revision={revision} config_preview={(preview ? 1 : 0)} profile_index={bank.ActiveProfile} theme={(int)bank.Profiles[bank.ActiveProfile].Theme} face={(int)bank.Profiles[bank.ActiveProfile].Face}" + (advertiseCapabilities ? $" home_mode={(int)bank.Profiles[bank.ActiveProfile].Mode} pet_interactions={petInteractions} pet_active={(!Sleeping && lastPetInteraction is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromMilliseconds(1800) ? 1 : 0)} pet_motion={(!Sleeping && lastPetInteraction is { } motionAt && DateTimeOffset.UtcNow - motionAt < TimeSpan.FromMilliseconds(1800) ? petMotion : 0)} pet_sleeping={(Sleeping ? 1 : 0)} pet_effect={(!Sleeping && lastPetTap is { } tapAt && DateTimeOffset.UtcNow - tapAt < TimeSpan.FromMilliseconds(1200) ? 1 : 0)} pet_taps={petTaps} pet_direction={(!Sleeping && lastPetInteraction is { } dirAt && DateTimeOffset.UtcNow - dirAt < TimeSpan.FromMilliseconds(1800) && petMotion == 2 ? petDirection : 0)} pet_elapsed_ms={(!Sleeping && lastPetInteraction is { } elapsedAt && DateTimeOffset.UtcNow - elapsedAt < TimeSpan.FromMilliseconds(1800) ? (int)(DateTimeOffset.UtcNow - elapsedAt).TotalMilliseconds : 0)}" : "") + (advertiseCapabilities && animationPreview ? $" demo_action={actionNow} demo_remaining_ms={remaining}" : "") + (advertiseCapabilities && bleManagement ? $" ble_manage={(managementRemaining > 0 ? 1 : 0)} ble_manage_remaining_ms={managementRemaining} ble_link=0 ble_secured=0 ble_bonded=0 manage_owner=0 manage_lease_ms=0" : ""); break;
                case "ANIMATE":
                    if (!advertiseCapabilities || !animationPreview) { LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED"); return; }
                    AnimationRequests++;
                    if (RejectNextAnimation) { RejectNextAnimation = false; LineReceived?.Invoke($"HUB {id} ERR code=BUSY"); return; }
                    int requested = p[3] switch { "IDLE" => 1, "BLINK" => 2, "HAPPY" => 3, "WALK" => 4, "TAP" => 5, "SLEEP" => 6, "WAKE" => 7, "STOP" => 0, _ => -1 };
                    if (requested < 0) { LineReceived?.Invoke($"HUB {id} ERR code=INVALID"); return; }
                    if (requested == 4 && bank.Profiles[bank.ActiveProfile].Mode == HomeMode.Face) { LineReceived?.Invoke($"HUB {id} ERR code=INCOMPATIBLE"); return; }
                    if (requested == 0) CancelDemo();
                    else { demoAction = requested; demoDeadline = Environment.TickCount64 + 6000; devicePage = 0; if (!KeepOutput) armed = false; }
                    result = $"demo_action={requested} demo_remaining_ms={(requested == 0 ? 0 : 6000)} armed={(armed ? 1 : 0)}"; break;
                case "PREVIEW": case "SAVE":
                    ConfigurationWriteRequests++;
                    if (uint.Parse(p[3]) != revision) { LineReceived?.Invoke($"HUB {id} ERR code=STALE"); return; }
                    var incoming = BankCodec.FromHex(p[4]);
                    if ((!DisplaySupported || !sleepBinding) && incoming.Profiles.Any(profile => profile.Bindings.Any(b => b is { Kind: BindingKind.Local, Value: 5 }))) { LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED_BINDING"); return; }
                    if (incoming.Profiles.Any(profile => (int)profile.Theme >= themeCount || (int)profile.Face >= faceCount || (int)profile.Mode >= homeModeCount)) { LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED_STYLE"); return; }
                    CancelDemo(); bank = incoming; revision++; if (!KeepOutput) armed = false;
                    preview = p[2] == "PREVIEW";
                    if (!preview) { SaveRequests++; saved = BankCodec.Decode(BankCodec.Encode(bank)); }
                    result = $"revision={revision} preview={(preview ? 1 : 0)} armed={(armed ? 1 : 0)}" + (!preview ? " saved=1" : ""); break;
                case "REVERT": CancelDemo(); bank = BankCodec.Decode(BankCodec.Encode(saved)); preview = false; if (!KeepOutput) armed = false; revision++; result = $"revision={revision} preview=0 armed={(armed ? 1 : 0)}"; break;
                case "SELECT":
                    if (uint.Parse(p[3]) != revision) { LineReceived?.Invoke($"HUB {id} ERR code=STALE"); return; }
                    CancelDemo(); bank = bank with { ActiveProfile = byte.Parse(p[4]) }; preview = true; if (!KeepOutput) armed = false; revision++; result = $"revision={revision} preview=1 armed={(armed ? 1 : 0)}"; break;
                case "CONTROL":
                    if (p[3] is "OUTPUT" or "ROUTE" || p[3] == "PAGE" && p[4] != "HOME") CancelDemo();
                    switch (p[3])
                    {
                        case "MANAGE":
                            if (!bleManagement) { LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED"); return; }
                            CancelDemo(); if (!KeepOutput) armed = false; managementWindowDeadline = p[4] == "1" ? Environment.TickCount64 + 60000 : 0;
                            result = $"ble_manage={(p[4] == "1" ? 1 : 0)} ble_manage_remaining_ms={(p[4] == "1" ? 60000 : 0)} armed={(armed ? 1 : 0)}"; break;
                        case "OUTPUT":
                            WakeScreen();
                            if (p[4] == "1" && WakeIsolated) { LineReceived?.Invoke($"HUB {id} ERR code=BUSY"); return; }
                            if (p[4] == "1" && !(devicePage is 0 or 1 || KeepPageOutput && devicePage is 4 or 6 or 7 or 8)) { LineReceived?.Invoke($"HUB {id} ERR code=NOT_READY"); return; }
                            armed = p[4] == "1"; result = $"armed={(armed ? 1 : 0)}"; break;
                        case "ROUTE": if (!KeepOutput || route != p[4]) armed = false; route = p[4]; result = $"route={route} armed={(armed ? 1 : 0)}"; break;
                        case "LANGUAGE": language = p[4]; if (!KeepOutput) armed = false; result = $"language={language} saved=1 armed={(armed ? 1 : 0)}"; break;
                        case "COUNTERS": Array.Clear(presses); Array.Clear(releases); cw = ccw = knob = markers = 0; petInteractions = petTaps = 0; lastPetInteraction = lastPetTap = null; petMotion = 0; result = "reset=1"; break;
                        case "PAGE":
                            if (HubProtocol.RequiresPageOutputCapability("PAGE " + p[4]) && !KeepPageOutput) { LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED"); return; }
                            devicePage = p[4] switch { "SETTINGS" => 1, "DIAG" => 2, "INFO" => 4, "ACTIVITY" => 5, "MANAGEMENT" => 6, "LANGUAGE" => 7, "TOOLS" => 8, _ => 0 };
                            if (devicePage == 0 ? !KeepOutput : !KeepPageOutput) armed = false;
                            result = $"page={devicePage} armed={(armed ? 1 : 0)}"; break;
                        default: LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED"); return;
                    }
                    break;
                default: LineReceived?.Invoke($"HUB {id} ERR code=UNSUPPORTED"); return;
            }
            if (p[2] == "STATUS")
            {
                result = result.Replace("build=DEMO_R3H", "build=DEMO_R3H", StringComparison.Ordinal)
                    .Replace("usb_vbus=1", $"usb_vbus={(UsbPowered ? 1 : 0)}", StringComparison.Ordinal);
                if (DisplaySupported) result += " " + DisplayFields(false);
            }
            LineReceived?.Invoke($"HUB {id} OK {result}");
        }
        catch (Exception) { LineReceived?.Invoke($"HUB {id} ERR code=INVALID"); }
    }
}
