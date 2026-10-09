using DeskBuddy.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    readonly AnimationPlayback animationPlayback = new();
    AnimationPreviewAction? selectedAnimation;
    CancellationTokenSource? animationRequest;
    HubClient? animationSession;
    bool animationDevice, syncAnimation = true, animationPending;
    int animationGeneration;
    Task animationCleanup = Task.CompletedTask;
    string animationMessage = "";
    long observedDemoTimestamp;
    bool DeviceAnimationSupported => Connected && client?.Capabilities?.SupportsAnimationPreview == true;
    static readonly string[] AnimationNamesEn = ["Idle", "Blink", "Happy", "Walk", "Say hello", "Rest", "Wake up"];
    static readonly string[] AnimationNamesZh = ["待机", "眨眼", "开心", "走路", "打招呼", "休息", "醒来"];
    string AnimationName(AnimationPreviewAction action) => (chinese ? AnimationNamesZh : AnimationNamesEn)[(int)action - 1];
    bool CanStartAnimation(AnimationPreviewAction action) => !busy && !(Editing.Mode == HomeMode.Face && action == AnimationPreviewAction.Walk)
        && (!DeviceAnimationSupported || !syncAnimation || CanApplyDraft);

    async Task StartAnimation(AnimationPreviewAction action, bool forceDevice = false)
    {
        if (Editing.Mode == HomeMode.Face && action == AnimationPreviewAction.Walk) throw new HubProtocolException("INCOMPATIBLE", T("Walking needs a full-body mode.", "走路请先选择桌面伙伴或电子宠物。"));
        await StopAnimation();
        if (page != 2) throw new OperationCanceledException();
        int generation = ++animationGeneration; selectedAnimation = action;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); animationRequest = request;
        var session = client; bool toDevice = forceDevice || DeviceAnimationSupported && syncAnimation;
        animationSession = toDevice ? session : null; animationPending = true;
        animationMessage = toDevice ? T("Applying appearance, then starting…", "正在应用草稿外观，然后开始播放…") : T("Starting computer preview…", "正在开始电脑预览…");
        UpdateLive();
        try
        {
            int remaining = 6000;
            if (toDevice)
            {
                if (session?.Capabilities?.SupportsAnimationPreview != true) throw new HubProtocolException("UNSUPPORTED", T("Device animation preview requires R3C.", "设备动作预览需要 R3C。"));
                if (!Fresh) throw new InvalidOperationException(T("Wait for fresh device status before playback.", "请先等待设备状态更新，再开始播放。"));
                await ApplyBankOnly(false, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (generation != animationGeneration || !ReferenceEquals(client, session) || page != 2) throw new OperationCanceledException();
                var status = await session.StartAnimationPreviewAsync(action, request.Token);
                request.Token.ThrowIfCancellationRequested();
                remaining = status.DemoAction == action ? (int)(status.DemoRemainingMilliseconds ?? 0) : 0;
                observedDemoTimestamp = status.ReceivedAt.UtcTicks;
                if (remaining == 0) throw new OperationCanceledException(T("Device playback was interrupted before readback.", "设备播放在回读前已被中断。"));
            }
            if (generation != animationGeneration || page != 2) throw new OperationCanceledException();
            animationDevice = toDevice && remaining > 0; animationPlayback.Start(action, remaining);
            animationMessage = remaining == 0 ? T("Device playback was cancelled before readback.", "回读时设备播放已被中断。") : toDevice ? T("Computer + device · temporary preview", "电脑＋设备 · 临时试看") : T("Computer only · no device command", "仅电脑 · 未发送设备指令");
        }
        catch (Exception error)
        {
            animationPlayback.Stop();
            animationMessage = T("Not saved. Preview stopped: ", "未保存，试看已停止：") + error.Message;
            if (toDevice && ReferenceEquals(session, client) && Connected) await StopSessionSafely(session!);
            throw;
        }
        finally
        {
            if (ReferenceEquals(animationRequest, request)) { animationRequest = null; animationPending = false; }
            request.Dispose(); UpdateLive();
        }
    }
    async Task StopSessionSafely(HubClient session)
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); if (ReferenceEquals(client, session) && Connected) await session.StopAnimationPreviewAsync(timeout.Token); }
        catch (Exception e) { animationMessage = T("Stopped on computer; device stop was not confirmed: ", "电脑已停止；设备停止未获确认：") + e.Message; }
    }
    async Task StopAnimation()
    {
        ++animationGeneration; animationRequest?.Cancel(); animationPlayback.Stop();
        var session = animationSession; bool shouldStop = animationDevice || animationPending;
        animationDevice = false; animationSession = null;
        animationMessage = T("Stopped · showing current state", "已停止 · 显示当前状态");
        UpdateLive();
        if (shouldStop && session != null) animationCleanup = StopAfterCleanup(animationCleanup, session);
        await animationCleanup;
    }
    async Task StopAfterCleanup(Task preceding, HubClient session) { await preceding; await StopSessionSafely(session); }
    void CancelAnimationForContext()
    {
        // Cancellation is immediate; only a STOP cleanup can follow. Never replay an action.
        _ = StopAnimation();
    }
    void TickAnimation()
    {
        if (animationDevice && (!Fresh || !ReferenceEquals(animationSession, client))) CancelAnimationForContext();
        if (animationDevice && Fresh && client?.Status is { } status && status.ReceivedAt.UtcTicks != observedDemoTimestamp)
        {
            observedDemoTimestamp = status.ReceivedAt.UtcTicks;
            if (status.DemoAction != animationPlayback.Action) { animationPlayback.Stop(); animationDevice = false; animationMessage = T("Device playback ended or was interrupted", "设备播放已结束或被实物操作中断"); }
        }
        if (page == 2) UpdateLive();
    }
    string PlaybackMessage => animationMessage switch
    {
        "Stopped · showing current state" or "已停止 · 显示当前状态" => T("Stopped · showing current state", "已停止 · 显示当前状态"),
        "Computer + device · temporary preview" or "电脑＋设备 · 临时试看" => T("Computer + device · temporary preview", "电脑＋设备 · 临时试看"),
        "Computer only · no device command" or "仅电脑 · 未发送设备指令" => T("Computer only · no device command", "仅电脑 · 未发送设备指令"),
        "Device playback ended or was interrupted" or "设备播放已结束或被实物操作中断" => T("Device playback ended or was interrupted", "设备播放已结束或被实物操作中断"),
        _ => animationMessage
    };
    void BuildAnimationControls()
    {
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.42, GridUnitType.Star) }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.58, GridUnitType.Star) });
        var view = Preview(232, appearanceDemo: true); var left = Stack(Txt(T("Live action preview", "连续动作预览"), 18, true), new System.Windows.Controls.Viewbox { Child = view, Stretch = System.Windows.Media.Stretch.Uniform, MaxWidth = 310, VerticalAlignment = VerticalAlignment.Top });
        left.Children.Add(Note(T("Software preview using the device artwork; not a captured screen.", "电脑示意使用设备同源像素图，并非实屏截图。")));
        left.Margin = new Thickness(0, 0, 22, 0); layout.Children.Add(left);
        var right = new StackPanel(); Grid.SetColumn(right, 1); layout.Children.Add(right);
        var target = Txt("", 14, true, accent); right.Children.Add(target);
        liveUpdates.Add(() => target.Text = DeviceAnimationSupported && syncAnimation ? T("Target: computer + device", "目标：电脑＋设备") : T("Target: computer only", "目标：仅电脑"));
        var sync = new CheckBox { Name = "AnimationDeviceSync", Content = T("Play on the device too", "同时在设备屏幕播放"), IsChecked = DeviceAnimationSupported && syncAnimation, Margin = new Thickness(0, 8, 0, 0) };
        sync.Click += (_, _) => { CancelAnimationForContext(); syncAnimation = sync.IsChecked == true; UpdateLive(); };
        liveUpdates.Add(() => { sync.IsEnabled = DeviceAnimationSupported && !busy; sync.IsChecked = DeviceAnimationSupported && syncAnimation; }); right.Children.Add(sync);
        right.Children.Add(Note(T("Each click plays for 6 seconds. It temporarily overrides daily animation OFF; the setting stays unchanged.", "点一次连续播放 6 秒。即使日常动画关闭，也能临时试看；不会更改开关设置。")));
        right.Children.Add(Note(T("Everyday interaction: turn the knob to walk left/right in full-body modes; press a key or the knob for a happy response; tap the character for a heart. Touch wakes the buddy. Automatic idle blinking can be disabled; physical reactions still animate on R3E.", "日常互动：转旋钮时全身角色向左／右迈步；按键或按旋钮会开心回应；点角色出现爱心，触摸会唤醒。R3E 关闭自动待机眨眼后，实物互动仍有动画。")));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var action in Enum.GetValues<AnimationPreviewAction>())
        {
            var button = AsyncBtn(AnimationName(action), () => StartAnimation(action), T("Action preview started.", "动作试看已开始。"), () => CanStartAnimation(action));
            button.Tag = "animation:" + action; button.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(button);
        }
        right.Children.Add(actions);
        var walkNote = Note(""); right.Children.Add(walkNote);
        liveUpdates.Add(() => walkNote.Text = Editing.Mode == HomeMode.Face ? T("Walk needs a full-body character. Choose Desk companion or Virtual pet above.", "表情面板只有头像。要看走路，请选择“桌面伙伴”或“电子宠物”。") : "");
        var status = Txt("", 13, true, accent); right.Children.Add(status);
        liveUpdates.Add(() => { var snapshot = animationPlayback.Snapshot; status.Text = animationPending ? T("Preparing…", "准备中…") : snapshot.Action is { } action ? $"{AnimationName(action)} · {T("playing", "播放中")} · {snapshot.Remaining / 1000.0:F1}s" : animationMessage.Length > 0 ? PlaybackMessage : T("Select an action to begin", "选择一个动作开始试看"); });
        var stop = Btn(T("Stop", "停止"), async () => await StopAnimation()); stop.Tag = "animation:Stop";
        actionButtons.Add((stop, () => animationPending || animationPlayback.Running)); right.Children.Add(stop);
        var compatibility = Note(""); right.Children.Add(compatibility);
        liveUpdates.Add(() => compatibility.Text = Connected && !DeviceAnimationSupported ? T("This firmware cannot play requested animations. Computer preview is available; upgrade to R3C for device playback.", "当前固件不支持指定动作播放，可先在电脑试看；升级 R3C 后可同步实屏。") : T("Device playback applies the draft in RAM without saving. R3E keeps your output ON/OFF; older firmware turns it OFF. Physical input takes over immediately.", "同步设备临时应用草稿，不自动保存。R3E 保留原输出开关，旧固件会关闭输出。实物操作立即接管动作。"));
        var card = Card(layout); card.Name = "AppearancePoses"; content.Children.Add(card);
    }
}
