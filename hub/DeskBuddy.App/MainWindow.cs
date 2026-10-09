using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBuddy.Core;
using Microsoft.Win32;

namespace DeskBuddy.App;

public sealed partial class MainWindow : Window
{
    readonly bool demo;
    readonly string? smokeDirectory;
    readonly CancellationTokenSource lifetime = new();
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer blinkTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly List<DevicePreview> previews = [];
    readonly ObservableCollection<string> logs = [];
    readonly List<Action> liveUpdates = [];
    readonly List<(Button Button, Func<bool> Enabled)> actionButtons = [];
    HubClient? client;
    ILineTransport? transport;
    DemoTransport? demoTransport;
    HubBank draft = HubDefaults.CreateBank();
    uint? draftRevision;
    bool dirty, busy, polling, chinese = true, closing;
    readonly string?[] invalidNames = new string?[3];
    int page, editProfile;
    int scaleIndex;
    PortCandidate[] candidates = [];
    PortCandidate? selectedPort;
    Grid shell = null!;
    StackPanel content = null!;
    TextBlock stateText = null!, noticeText = null!, headerSub = null!;
    Button[] navigation = [];
    TextBlock? outputPill;
    readonly Brush ink = BrushOf("#1C3540"), muted = BrushOf("#60747D"), accent = BrushOf("#147E70"), line = BrushOf("#DEE7E9");
    string notice = "";
    readonly ConnectionNotices connectionNotices = new();
    public MainWindow(bool demo, string? smokeDirectory, string? deviceSmokeDirectory = null, bool deferStartupForTest = false, string? isolatedWorkspacePath = null)
    {
        this.demo = demo; this.smokeDirectory = smokeDirectory;
        Title = demo ? "Desk Buddy Hub · Demo" : "Desk Buddy Hub";
        Width = 1280; Height = 800; MinWidth = 1000; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BrushOf("#F4F7F8"); FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        InitializeWorkspace(isolatedWorkspacePath, demo || smokeDirectory != null || deviceSmokeDirectory != null || deferStartupForTest);
        SetupStyles(); BuildShell();
        Loaded += async (_, _) =>
        {
            if (deferStartupForTest) return;
            if (deviceSmokeDirectory != null) { await RunDeviceSmoke(deviceSmokeDirectory); return; }
            if (demo) await RunOperation(async () => await ConnectDemo(), T("Demo device ready. No hardware connection.", "演示设备已就绪，未连接硬件。"));
            else await RunOperation(ScanPorts, T("Device discovery complete.", "设备发现完成。"));
            poll.Start(); blinkTimer.Start();
            if (smokeDirectory != null) await RunSmoke(smokeDirectory);
        };
        poll.Tick += async (_, _) =>
        {
            UpdateLive();
            if (polling || busy || client is null || !Connected) return;
            polling = true;
            try { await client.RefreshStatusAsync(lifetime.Token); }
            catch (Exception e) { SetNotice(T("Status unavailable: ", "状态暂不可用：") + connectionNotices.Error(e, chinese)); }
            finally { polling = false; UpdateLive(); }
        };
        blinkTimer.Tick += (_, _) => TickPreviewAnimation();
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true; poll.Stop(); blinkTimer.Stop(); FlushWorkspace(); connectionOperation?.Cancel(); await StopAnimation(); lifetime.Cancel();
            try { if (client is not null) await client.DisconnectAsync(); } catch { }
            // Even a null/disconnected client can complete cleanup synchronously.
            // Defer the second Close until WPF has returned from this Closing event.
            var deferredClose = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        };
    }
    bool Connected => client?.State is HubConnectionState.Ready or HubConnectionState.Legacy;
    bool Fresh => Connected && client?.IsStatusStale == false;
    bool CanConfigure => Fresh && client?.IsLegacy == false && client.Configuration != null && draftRevision != null && !busy && !invalidNames.Any(n => n != null);
    string T(string en, string zh) => chinese ? zh : en;
    static Brush BrushOf(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    HubProfile Editing => draft.Profiles[editProfile];

    void TickPreviewAnimation()
    {
        // Local projection only: keep the one-second transport poll unchanged.
        // Device-page feedback needs the same 120 ms clock as Appearance, even
        // when automatic idle blinking is OFF. UpdateLive reads the existing
        // snapshot and its age; it neither creates input nor refreshes freshness.
        if (page != 2 && previews.Any(p => p.PhysicalFeedback && p.PetActive == true)) UpdateLive();
        bool blink = Environment.TickCount64 % 4200 < 170;
        int frame = (int)(Environment.TickCount64 / 180 % 2);
        foreach (var p in previews)
        {
            bool idleChanged = p.Blink != blink || p.IdleFrame != frame;
            p.Blink = blink; p.IdleFrame = frame;
            if ((p.Animate && idleChanged) || (p.PhysicalFeedback && p.PetActive == true)) p.InvalidateVisual();
        }
        TickAnimation();
    }

    void SetupStyles()
    {
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.FontSizeProperty, 13.0));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty, FontWeights.SemiBold));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(15, 10, 15, 10)));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, ink));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.White));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, line));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.MinHeightProperty, 40.0));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "ButtonBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(OpacityProperty, .82)); template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .38)); template.Triggers.Add(disabled);
        button.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template)); Resources.Add(typeof(Button), button);
        ControlStyles.Register(Resources);
        var text = new Style(typeof(TextBox)); text.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(10, 8, 10, 8))); text.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, line)); text.Setters.Add(new Setter(System.Windows.Controls.Control.MinHeightProperty, 36.0)); Resources.Add(typeof(TextBox), text);
    }
    TextBlock Txt(string s, double size = 14, bool bold = false, Brush? color = null) => new()
    { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? ink, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.48 };
    TextBlock Note(string s) { var t = Txt(s, 12, false, muted); t.Margin = new Thickness(0, 7, 0, 0); return t; }
    StackPanel Stack(params UIElement[] items) { var s = new StackPanel(); foreach (var x in items) s.Children.Add(x); return s; }
    Border Card(UIElement child, Thickness? margin = null) => new() { Child = child, Background = Brushes.White, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = margin ?? new Thickness(0, 0, 0, 18) };
    Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        if (primary) { b.Background = accent; b.Foreground = Brushes.White; b.BorderBrush = accent; }
        b.Click += (_, _) => click(); return b;
    }
    Button AsyncBtn(string text, Func<Task> action, string success, Func<bool>? enabled = null, bool primary = false)
    {
        var b = Btn(text, async () => await RunOperation(action, success), primary);
        if (enabled != null) actionButtons.Add((b, enabled)); return b;
    }
    WrapPanel Row(params UIElement[] elements) { var p = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; foreach (var e in elements) p.Children.Add(e); return p; }
    void BuildShell()
    {
        liveUpdates.Clear(); actionButtons.Clear();
        shell = new Grid { Background = BrushOf("#F4F7F8") }; shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) }); shell.ColumnDefinitions.Add(new ColumnDefinition());
        Content = shell;
        var side = new Grid { Background = BrushOf("#142E38") }; side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); side.RowDefinitions.Add(new RowDefinition()); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Children.Add(side);
        var brand = Stack(Txt("◉  desk buddy", 22, true, Brushes.White), Txt("H U B  /  W I N D O W S", 10, false, BrushOf("#8EADA9")));
        brand.Margin = new Thickness(25, 30, 20, 30); side.Children.Add(brand);
        string[] titles = [T("Device", "设备"), T("Controls", "控制配置"), T("Appearance", "外观"), T("Diagnostics", "诊断")];
        string[] glyphs = ["◫", "⌘", "◉", "≋"];
        var nav = new StackPanel { Margin = new Thickness(16, 4, 16, 0) }; Grid.SetRow(nav, 1); side.Children.Add(nav);
        navigation = new Button[4];
        for (int i = 0; i < 4; i++)
        {
            int index = i; var b = Btn(glyphs[i] + "    " + titles[i], () => { if (page == 2 && index != 2) CancelAnimationForContext(); page = index; BuildPage(); });
            b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 0, 0, 9); b.Height = 48; b.BorderThickness = new Thickness(0); b.Foreground = Brushes.White;
            navigation[i] = b; nav.Children.Add(b);
        }
        var lower = Stack(Txt(demo ? T("DEMO WORKSPACE", "演示工作区") : T("DEVICE COMPANION", "设备配置助手"), 10, true, BrushOf("#A0C4BE")),
            Txt(T("Your controls. Your little companion.", "让每一次操作，都有你的风格。"), 12, false, BrushOf("#99B1B9")));
        lower.Margin = new Thickness(25, 10, 24, 30); Grid.SetRow(lower, 2); side.Children.Add(lower);
        var area = new Grid { Margin = new Thickness(32, 26, 32, 18) }; Grid.SetColumn(area, 1); shell.Children.Add(area);
        area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); area.RowDefinitions.Add(new RowDefinition()); area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 22) }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel(); var title = Txt(titles[page], 27, true); title.Name = "PageTitle"; titleStack.Children.Add(title);
        headerSub = Txt("", 12, false, muted); headerSub.Margin = new Thickness(0, 4, 0, 0); titleStack.Children.Add(headerSub); header.Children.Add(titleStack);
        var utility = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        outputPill = Txt("", 11, true, accent); outputPill.Margin = new Thickness(0, 12, 16, 0); utility.Children.Add(outputPill);
        utility.Children.Add(Btn(chinese ? "EN" : "中文", () => { chinese = !chinese; BuildShell(); }));
        var zoom = new ComboBox { Width = 104, ItemsSource = new[] { "100%", "110%", "125%" }, SelectedIndex = scaleIndex, ToolTip = T("Interface scale", "界面缩放") };
        zoom.SelectionChanged += (_, _) => { scaleIndex = zoom.SelectedIndex; double v = scaleIndex == 2 ? 1.25 : scaleIndex == 1 ? 1.1 : 1; content.LayoutTransform = new ScaleTransform(v, v); };
        utility.Children.Add(zoom); Grid.SetColumn(utility, 1); header.Children.Add(utility); area.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 0) };
        content = new StackPanel(); double scale = scaleIndex == 2 ? 1.25 : scaleIndex == 1 ? 1.1 : 1; content.LayoutTransform = new ScaleTransform(scale, scale); scroll.Content = content; Grid.SetRow(scroll, 1); area.Children.Add(scroll);
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noticeText = Txt(notice, 11, false, muted); footer.Children.Add(noticeText);
        stateText = Txt("", 11, true, accent); stateText.Margin = new Thickness(14, 0, 0, 0); Grid.SetColumn(stateText, 1); footer.Children.Add(stateText); Grid.SetRow(footer, 2); area.Children.Add(footer);
        BuildPage();
    }
    void BuildPage()
    {
        content.Children.Clear(); liveUpdates.Clear(); actionButtons.Clear(); previews.Clear();
        string[] titles = [T("Device", "设备"), T("Controls", "控制配置"), T("Appearance", "外观"), T("Diagnostics", "诊断")];
        var title = ((shell.Children[1] as Grid)!.Children[0] as Grid)!.Children[0] as StackPanel; ((TextBlock)title!.Children[0]).Text = titles[page];
        string[] subtitles = [T("Connect, check your link, and stay in control.", "连接设备，确认链路，安心开始。"), T("Three profiles. Twelve inputs. One consistent workflow.", "三套配置，十二种输入，自由安排每个动作。"), T("A tiny screen with a little personality.", "给小屏幕，添一点自己的风格。"), T("Live input edges and device telemetry, with clear freshness.", "实时输入边沿与设备数据，清楚标记新鲜度。")];
        headerSub.Text = subtitles[page];
        for (int i = 0; i < 4; i++) navigation[i].Background = BrushOf(i == page ? "#286356" : "#142E38");
        BuildWorkspaceNotice();
        switch (page) { case 0: BuildDevice(); break; case 1: BuildControls(); break; case 2: BuildAppearance(); break; case 3: BuildDiagnostics(); break; }
        UpdateLive();
    }
    void AddSection(string heading, string description)
    { content.Children.Add(Txt(heading, 17, true)); content.Children.Add(Note(description)); }

    DevicePreview Preview(double height = 245, bool appearanceDemo = false)
    {
        var p = new DevicePreview { Height = height, Width = height * 4 / 3, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
        previews.Add(p);
        liveUpdates.Add(() =>
        {
            p.Theme = (int)Editing.Theme; p.Face = (int)Editing.Face; p.Mode = (int)Editing.Mode;
            p.PetInteractions = Fresh ? client?.Status?.GetUInt32("pet_interactions") : null;
            p.PetActive = Fresh ? client?.Status?.GetBool("pet_active") : null;
            p.PetMotion = Fresh ? client?.Status?.GetInt32("pet_motion") ?? 0 : 0;
            p.PetDirection = Fresh ? client?.Status?.GetInt32("pet_direction") ?? 0 : 0;
            p.PetElapsedMilliseconds = Fresh ? client?.Status?.GetUInt32("pet_elapsed_ms") ?? 0 : 0;
            p.PetStatusAgeMilliseconds = Fresh ? client?.StatusAge?.TotalMilliseconds ?? 0 : 0;
            p.PhysicalFeedback = client?.Capabilities?.SupportsPetInputFeedback == true;
            p.PetSleeping = Fresh ? client?.Status?.GetBool("pet_sleeping") : null;
            p.PetHeart = Fresh && client?.Status?.GetInt32("pet_effect") == 1;
            p.PetTaps = Fresh ? client?.Status?.GetUInt32("pet_taps") : null;
            p.DemoPose = appearanceDemo ? animationPlayback.Pose : null;
            p.LocalDemo = appearanceDemo && animationPlayback.Running;
            p.Animate = Editing.Animation; p.Accent = Editing.AccentRgb565; p.Profile = Editing.Name;
            p.Chinese = chinese; p.Route = client?.Status?.Route ?? "USB";
            p.Connected = Fresh && client?.Status?.Connected == true; p.Armed = Fresh && client?.Status?.Armed == true;
            p.HidSuppressed = Fresh && client?.Status?.HidSuppressed == true;
            p.StatusKnown = Fresh;
            p.Battery = Fresh ? client?.Status?.GetInt32("battery_est_percent") ?? -1 : -1;
            p.InvalidateVisual();
        });
        return p;
    }
    void BuildDevice()
    {
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = Stack(Txt(T("Hello, Desk Buddy", "你好，Desk Buddy"), 20, true), Note(T("Software appearance preview", "设备外观软件预览")), Preview());
        left.Children.Add(Note(T("Preview uses the selected local profile. Device state is shown only while fresh.", "预览使用当前本地配置；设备状态仅在数据新鲜时显示。")));
        var lc = Card(left, new Thickness(0, 0, 18, 18)); columns.Children.Add(lc);
        var right = Stack(Txt(T("Hub management connection", "Hub 管理连接"), 18, true));
        BuildConnectionControls(right);
        right.Children.Add(new Separator { Margin = new Thickness(0, 17, 0, 8), Background = line });
        right.Children.Add(Txt(T("HID output & route", "HID 输出与通道"), 15, true));
        right.Children.Add(Row(AsyncBtn(T("Output OFF", "关闭输出"), () => Control("OUTPUT 0"), T("Output OFF confirmed.", "已确认输出关闭。"), () => Connected && !busy, true),
            AsyncBtn(T("Enable output", "手动开启输出"), () => Control("OUTPUT 1"), T("Output ON acknowledged. Hold USER SW to stop locally.", "已确认输出开启，可随时长按 USER SW 停止。"), () => OutputStatePresentation.CanEnable(client?.Status, client?.Capabilities, Fresh, busy))));
        var outputDetail = Note(""); liveUpdates.Add(() => outputDetail.Text = OutputStatePresentation.Detail(client?.Status, Fresh, chinese)); right.Children.Add(outputDetail);
        right.Children.Add(Row(AsyncBtn("USB", () => Control("ROUTE USB"), T("USB selected; check the output state above.", "已选择 USB，当前输出状态见顶部。"), () => Fresh && !busy),
            AsyncBtn("BLE", () => Control("ROUTE BLE"), T("BLE selected; check the output state above.", "已选择 BLE，当前输出状态见顶部。"), () => Fresh && !busy)));
        right.Children.Add(Note(T("These USB/BLE buttons select HID output, not the Hub management connection. Switching never arms output. Pairing stays in Windows Settings.", "这里的 USB/BLE 按钮只切换 HID 输出，不切换 Hub 管理连接，也不会开启输出。配对请在 Windows 设置中完成。")));
        var rc = Card(right); Grid.SetColumn(rc, 1); columns.Children.Add(rc); columns.Name = "ManagementConnection"; content.Children.Add(columns);
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -12, 0) };
        stats.Children.Add(Stat(T("HID LINK", "HID 链路"), () => Fresh ? $"{client?.Status?.Route} · {(client?.Status?.Connected == true ? T("Connected", "已连接") : T("Waiting", "等待连接"))}" : "—", T("Independent of USB/BLE Hub management.", "与 USB/BLE Hub 管理连接相互独立。")));
        stats.Children.Add(Stat(T("BATTERY ESTIMATE", "电池电压估计"), () => Fresh && client?.Status?.GetInt32("vbat_est_mV") is int mv ? $"{mv / 1000.0:F2} V" : "—", T("Voltage only; USB can raise the estimate.", "仅电压估计；USB 可能使估算偏高。")));
        stats.Children.Add(Stat(T("CALIBRATION", "触摸校准"), () => Fresh ? (client?.Status?.GetBool("stored") == true ? T("Saved", "已保存") : T("Check device", "需检查设备")) : "—", T("Recalibrate with the device USER SW flow.", "重新校准请使用设备 USER SW 流程。")));
        content.Children.Add(stats);
        BuildDisplaySettingsCard();
        content.Children.Add(Card(Stack(Txt(T("Device language & calibration", "设备语言与校准"), 15, true),
            Row(AsyncBtn("English", () => Control("LANGUAGE EN"), T("Device language saved and confirmed.", "设备语言已保存并核对。"), () => Fresh && !busy),
                AsyncBtn("中文", () => Control("LANGUAGE ZH"), T("Device language saved and confirmed.", "设备语言已保存并核对。"), () => Fresh && !busy),
                AsyncBtn(T("Device information", "设备信息页"), () => Control("PAGE INFO"), T("Information page opened. Calibration must be started on the device.", "设备信息页已打开，校准需在设备上启动。"), () => Fresh && !busy)),
            Note(T("To recalibrate, stop output, hold USER SW while restarting the device, then follow five touch targets. USB removal alone does not restart a battery-powered board. Hub cannot start calibration remotely.", "重新校准：先停止输出，重启设备时按住 USER SW，再依次触摸五个靶点。电池连接时，仅拔 USB 不等于重启。Hub 不会远程启动校准。")))));
        content.Children.Add(Card(Stack(Txt(T("Built for deliberate changes", "每次变更，都有明确状态"), 15, true), Note(T("Preview applies to RAM; Save verifies persistent readback. Reconnect reads without replaying edits. BLE Hub requires paired encryption, local permission and new firmware. A different writer can hold a 10-second lease; BUSY never triggers automatic retries.", "预览只应用到内存；保存核对持久化回读。重连只读取，不重放草稿。BLE Hub 需配对加密、本机授权和新版固件。另一管理端可持有 10 秒写入租约；遇到 BUSY 不会自动重试。")))));
    }
    Border Stat(string heading, Func<string> value, string detail)
    {
        var v = Txt("", 22, true); liveUpdates.Add(() => v.Text = value());
        return Card(Stack(Txt(heading, 10, true, accent), v, Note(detail)), new Thickness(0, 0, 12, 18));
    }
    void ProfileTabs()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        for (int i = 0; i < 3; i++) { int ix = i; var b = Btn($"0{i + 1}  {draft.Profiles[i].Name}" + (draft.ActiveProfile == i ? "  •" : ""), () => { CancelAnimationForContext(); selectedAnimation = null; editProfile = ix; BuildPage(); }, editProfile == i); row.Children.Add(b); }
        content.Children.Add(row);
        var explanation = Note(T("Three independently saved profiles. MEDIA / STUDY / WORK suggest media, study and work uses; their default mappings are identical. Names are labels, not automatic app detection.", "这是 3 套可独立保存的配置。MEDIA / STUDY / WORK 分别建议用于媒体、学习、工作；默认映射相同。名称只是用途标签，不会自动识别应用。"));
        explanation.Name = "ProfileExplanation"; explanation.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(explanation);
    }
    void ConfigActions()
    {
        var caption = Txt("", 12, true, accent); liveUpdates.Add(() => caption.Text = dirty ? T("Local edits · not applied", "本地草稿 · 尚未应用") : client?.Configuration?.Preview == true ? T("Device preview · not saved to flash", "设备预览中 · 尚未保存到闪存") : T("Local profile bank", "本地配置库"));
        var bar = Stack(caption, Row(
            AsyncBtn(T("Preview on device", "预览到设备"), () => ApplyBank(false), T("Preview applied and verified. See the current output state above.", "预览已应用并回读确认，当前输出状态见顶部。"), () => CanApplyDraft, true),
            AsyncBtn(T("Save to device", "保存到设备"), () => ApplyBank(true), T("Saved and persistent readback verified. See the current output state above.", "保存及持久化回读核对成功，当前输出状态见顶部。"), () => CanApplyDraft),
            AsyncBtn(T("Revert device", "还原设备"), Revert, T("Persisted configuration restored and read back.", "已还原设备持久化配置并回读确认。"), () => CanConfigure),
            AsyncBtn(T("Read device", "读取设备"), ReadDevice, T("Readback replaced local edits.", "设备回读已替换本地草稿。"), () => Fresh && client?.IsLegacy == false && !busy),
            Btn(T("Import JSON", "导入 JSON"), Import), Btn(T("Export JSON", "导出 JSON"), Export)),
            Note(T("Preview/Save use all three profiles and activate the one being edited. Read device replaces local edits. Imported files stay local until applied.", "预览和保存包含三套配置，并激活当前编辑的配置。读取设备会替换本地草稿；导入文件不会自动发送。")));
        var compatibility = Txt("", 12, true, BrushOf("#9B5723")); compatibility.Margin = new Thickness(0, 8, 0, 0);
        liveUpdates.Add(() => compatibility.Text = !Connected ? T("Offline: edit or export locally; nothing will be sent.", "离线状态：可本地编辑或导出，不会发送到设备。") : client?.IsLegacy == true ? T("Legacy firmware: device configuration writes are unavailable.", "旧版固件不支持设备配置写入。") : !SupportsDraft ? T("This bank includes an unsupported appearance, home mode or screen-rest binding. Preview/Save are blocked; local editing and export remain available.", "配置中含设备不支持的外观、主页模式或休屏绑定，已阻止预览/保存；仍可本地编辑和导出。") : T($"Device supports {client?.Capabilities?.ThemeCount} themes, {client?.Capabilities?.FaceCount} characters and {client?.Capabilities?.HomeModeCount} home modes.", $"设备支持 {client?.Capabilities?.ThemeCount} 套主题、{client?.Capabilities?.FaceCount} 个角色和 {client?.Capabilities?.HomeModeCount} 种主页模式。"));
        bar.Children.Add(compatibility); content.Children.Add(Card(bar));
    }
    void EditProfile(Func<HubProfile, HubProfile> change)
    {
        CancelAnimationForContext(); selectedAnimation = null;
        var p = (HubProfile[])draft.Profiles.Clone(); p[editProfile] = change(p[editProfile]); draft = draft with { Profiles = p }; dirty = true; RememberWorkspaceDraft(); UpdateLive();
    }
    void BuildControls()
    {
        ProfileTabs(); ConfigActions();
        var name = new TextBox { Text = invalidNames[editProfile] ?? Editing.Name, Width = 190, MaxLength = 15 };
        var nameError = Txt(invalidNames[editProfile] == null ? "" : T("Use 1–15 printable ASCII characters before applying.", "请使用 1–15 个可打印 ASCII 字符后再应用。"), 12, false, BrushOf("#AD4635"));
        name.TextChanged += (_, _) =>
        {
            bool valid = name.Text.Length is >= 1 and <= 15 && name.Text.All(c => c >= 32 && c <= 126);
            invalidNames[editProfile] = valid ? null : name.Text; dirty = true;
            nameError.Text = valid ? "" : T("Use 1–15 printable ASCII characters before applying.", "请使用 1–15 个可打印 ASCII 字符后再应用。");
            if (valid) EditProfile(p => p with { Name = name.Text }); else UpdateLive();
        };
        var reverse = new CheckBox { Content = T("Reverse encoder direction", "反转旋钮方向"), IsChecked = Editing.EncoderReverse, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        reverse.Click += (_, _) => EditProfile(p => p with { EncoderReverse = reverse.IsChecked == true });
        content.Children.Add(Card(Stack(Txt(T("Hardware profile label", "设备配置名称"), 15, true), Row(name, reverse), nameError, Note(T("1–15 printable ASCII characters. The filled dot marks the device's selected profile.", "使用 1–15 个可打印 ASCII 字符；圆点表示设备当前选中的配置。")),
            Row(AsyncBtn(T("Select this profile on device", "在设备上选择此配置"), SelectProfile, T("Profile selected and verified; current output shown above.", "配置选择与回读成功，当前输出状态见顶部。"), () => CanConfigure && !dirty)))));
        var panel = new StackPanel();
        panel.Children.Add(Txt(T("Input mapping", "输入映射"), 17, true));
        panel.Children.Add(Note(T("One input sends one discrete action. USER SW remains a fixed emergency output stop.", "每次输入只发送一个离散动作。USER SW 始终保留紧急关闭输出功能。")));
        panel.Children.Add(Note(T("Video presets send keyboard shortcuts to the focused player. They do not choose an app or change your other mappings.", "视频预设向当前焦点播放器发送快捷键，不自动选择应用，也不改动其它映射。")));
        panel.Children.Add(new Expander { Header = T("Video shortcuts and keyboard focus", "视频快捷键与焦点说明"), Margin = new Thickness(0, 10, 0, 0),
            Content = Stack(Note(BindingPresets.Help(chinese)), Note(T("Official YouTube shortcuts: ", "YouTube 官方快捷键：") + BindingPresets.YouTubeHelpUrl)) });
        string[] labels = ["K1", "K2", "K3", "K4", "K5", "K6", T("Clockwise", "顺时针"), T("Counterclockwise", "逆时针"), T("Knob press", "按下旋钮"), T("Touch Previous", "触摸上一首"), T("Touch Play", "触摸播放"), T("Touch Next", "触摸下一首")];
        for (int i = 0; i < 12; i++) panel.Children.Add(BindingRow(i, labels[i]));
        content.Children.Add(Card(panel));
    }
    sealed record ActionChoice(BindingPreset Preset, string Label) { public override string ToString() => Label; }
    ActionChoice[] Choices() => BindingPresets.All.Select(p => new ActionChoice(p, p.Label(chinese))).ToArray();
    static string KeyLabel(int code)
    {
        if (code is >= 4 and <= 29) return ((char)('A' + code - 4)).ToString();
        if (code is >= 30 and <= 38) return (code - 29).ToString();
        if (code == 39) return "0";
        if (code is >= 58 and <= 69) return $"F{code - 57}";
        if (code is >= 0x68 and <= 0x73) return $"F{code - 0x68 + 13}";
        return code switch { 40 => "Enter", 41 => "Escape", 42 => "Backspace", 43 => "Tab", 44 => "Space", 45 => "-", 46 => "=", 47 => "[", 48 => "]", 49 => "\\", 51 => ";", 52 => "'", 53 => "`", 54 => ",", 55 => ".", 56 => "/", 57 => "Caps Lock", 73 => "Insert", 74 => "Home", 75 => "Page Up", 76 => "Delete", 77 => "End", 78 => "Page Down", 79 => "Right", 80 => "Left", 81 => "Down", 82 => "Up", _ => $"HID 0x{code:X2}" };
    }
    UIElement BindingRow(int index, string label)
    {
        var row = new Grid { Name = $"BindingRow{index}", Margin = new Thickness(0, 14, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        var name = Txt(label, 13, true); name.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(name);
        var binding = Editing.Bindings[index]; var choices = Choices();
        var choice = new ComboBox { Name = $"BindingAction{index}", ItemsSource = choices, SelectedItem = choices.Single(c => c.Preset == BindingPresets.Resolve(binding)), Margin = new Thickness(0, 0, 12, 0) }; Grid.SetColumn(choice, 1); row.Children.Add(choice);
        var extra = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var key = new ComboBox { Name = $"BindingKey{index}", ItemsSource = Enumerable.Range(4, 0x73 - 4 + 1).Select(KeyLabel).ToArray(), SelectedIndex = binding.Kind == BindingKind.Keyboard ? Math.Clamp(binding.Value - 4, 0, 0x73 - 4) : 0, Width = 120, Margin = new Thickness(0, 0, 8, 0) }; extra.Children.Add(key);
        var mods = new CheckBox[8]; string[] modLabels = ["L Ctrl", "L Shift", "L Alt", "L Win", "R Ctrl", "R Shift", "R Alt", "R Win"]; int[] modBits = [1, 2, 4, 8, 16, 32, 64, 128];
        for (int j = 0; j < 8; j++) { mods[j] = new CheckBox { Content = modLabels[j], IsChecked = (binding.Modifiers & modBits[j]) != 0, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 7, 3) }; extra.Children.Add(mods[j]); }
        extra.Visibility = binding.Kind == BindingKind.Keyboard ? Visibility.Visible : Visibility.Collapsed; Grid.SetColumn(extra, 2); row.Children.Add(extra);
        bool syncing = false;
        HubBinding KeyboardValue()
        {
            byte mask = 0; for (int j = 0; j < 8; j++) if (mods[j].IsChecked == true) mask |= (byte)modBits[j];
            return new HubBinding(BindingKind.Keyboard, (ushort)(key.SelectedIndex + 4), mask);
        }
        void Commit(HubBinding b)
        {
            EditProfile(p => { var bs = (HubBinding[])p.Bindings.Clone(); bs[index] = b; return p with { Bindings = bs }; });
            extra.Visibility = b.Kind == BindingKind.Keyboard ? Visibility.Visible : Visibility.Collapsed;
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (syncing || choice.SelectedItem is not ActionChoice a) return;
            var b = BindingPresets.Select(a.Preset, KeyboardValue());
            // Fill every part of a preset before committing: neither the previous
            // key nor a stale modifier may leak into the selected shortcut.
            syncing = true;
            try
            {
                if (b.Kind == BindingKind.Keyboard)
                {
                    key.SelectedIndex = b.Value - 4;
                    for (int j = 0; j < 8; j++) mods[j].IsChecked = (b.Modifiers & modBits[j]) != 0;
                }
            }
            finally { syncing = false; }
            Commit(b);
        };
        void UpdateKeyboard()
        {
            if (syncing || choice.SelectedItem is not ActionChoice a || a.Preset.Binding.Kind != BindingKind.Keyboard) return;
            var b = KeyboardValue();
            syncing = true;
            try { choice.SelectedItem = choices.Single(c => c.Preset == BindingPresets.Resolve(b)); }
            finally { syncing = false; }
            Commit(b);
        }
        key.SelectionChanged += (_, _) => UpdateKeyboard(); foreach (var m in mods) m.Click += (_, _) => UpdateKeyboard();
        return row;
    }
    void BuildDiagnostics()
    {
        var heading = Stack(Txt(T("Physical input monitor", "实物输入监视"), 18, true), Note(T("Highlight = stable pressed state. P/R = press/release edges. Stale data is never shown as a live press.", "高亮表示稳定按下状态；P/R 为按下/释放边沿数。过期数据不会显示为实时按下。")));
        var keys = new UniformGrid { Columns = 6, Margin = new Thickness(0, 18, -8, 0) };
        for (int i = 0; i < 6; i++)
        {
            int ix = i; var counts = Txt("—", 12, false, muted); var state = Txt("—", 12, true);
            var b = new Border { Background = BrushOf("#F1F6F6"), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 0), Child = Stack(Txt($"K{i + 1}", 17, true), state, counts) };
            keys.Children.Add(b);
            liveUpdates.Add(() => { bool down = Fresh && client?.Status?.KeysDown is { Length: 6 } k && k[ix]; b.Background = down ? BrushOf("#AEE8D4") : BrushOf("#F1F6F6"); state.Text = !Fresh ? T("Stale", "已过期") : down ? T("PRESSED", "按下") : T("Released", "松开"); counts.Text = Fresh ? $"P {client?.Status?.KeyPresses?[ix].ToString() ?? "—"}  R {client?.Status?.KeyReleases?[ix].ToString() ?? "—"}" : "P —  R —"; });
        }
        heading.Children.Add(keys);
        heading.Children.Add(Row(AsyncBtn(T("Device DIAG", "设备诊断页"), () => Control("PAGE DIAG"), T("Device DIAG confirmed. Output switch and HID pause state are shown above.", "已确认设备进入诊断页；输出开关与 HID 暂停状态见顶部。"), () => Fresh && !busy),
            AsyncBtn(T("Device Home", "设备主页"), () => Control("PAGE HOME"), T("Device Home confirmed.", "已确认设备返回主页。"), () => Fresh && !busy),
            AsyncBtn(T("Reset counters", "清零计数"), () => Control("COUNTERS RESET"), T("Counter reset acknowledged.", "计数已确认清零。"), () => Fresh && !busy)));
        if (demo) heading.Children.Add(Row(Btn(T("Demo: press K1", "演示：按下 K1"), async () => { demoTransport?.SetKey(0, true); await Refresh(); }), Btn(T("Demo: release K1", "演示：松开 K1"), async () => { demoTransport?.SetKey(0, false); await Refresh(); })));
        content.Children.Add(Card(heading));
        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -12, 0) };
        stats.Children.Add(Stat(T("ROTATION", "旋转"), () => Fresh ? $"{Field("cw")} / {Field("ccw")}" : "—", "CW / CCW"));
        stats.Children.Add(Stat(T("HID", "HID"), () => Fresh ? $"{Field("sent")} / {Field("dropped")}" : "—", T("Sent / dropped, not host acceptance", "提交 / 丢弃，不等于主机执行")));
        stats.Children.Add(Stat(T("VBAT", "VBAT"), () => Fresh ? $"{Field("vbat_est_mV")} mV" : "—", T("Coarse estimate; not charge state", "粗略估计，不代表充电状态")));
        stats.Children.Add(Stat(T("POWER", "供电"), () => Fresh ? $"USB {Field("usb_vbus")}" : "—", T("USB presence only", "仅表示 USB 电源存在"))); content.Children.Add(stats);
        var raw = Txt("", 12, false, muted); liveUpdates.Add(() => raw.Text = Fresh ? $"{T("Firmware", "固件")} {client?.Status?.Build}\n{T("Calibration", "校准")} {Field("calibrated")} · {T("stored", "保存")} {Field("stored")} · {T("knob press", "旋钮按下")} {Field("presses")} · {T("markers", "标记")} {Field("markers")}\n{T("Status age", "状态年龄")} {client?.StatusAge?.TotalSeconds:F1}s · {T("Config revision", "配置修订")} {client?.Configuration?.Revision}" : T("No fresh status. Reconnect or wait for a successful STATUS response.", "暂无新鲜状态，请重连或等待有效 STATUS 应答。"));
        content.Children.Add(Card(raw));
        var log = new ListBox { ItemsSource = logs, Height = 160, Background = BrushOf("#102A36"), Foreground = BrushOf("#B9DDCF"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, BorderThickness = new Thickness(0), Padding = new Thickness(10) };
        content.Children.Add(Card(Stack(Txt(T("Session events", "会话事件"), 15, true), Note(T("Last 200 entries. Configuration hex and device identifiers are not logged here.", "保留最近 200 条，不记录配置十六进制内容或设备标识。")), Row(Btn(T("Export log", "导出日志"), ExportLog)), log)));
    }
    string Field(string key) => client?.Status?.Get(key) ?? "—";
    void UpdateLive()
    {
        content.IsEnabled = page is 0 or 2 || !busy;
        if (page is 0 or 2) foreach (UIElement element in content.Children) element.IsEnabled = !busy || element is FrameworkElement { Name: "AppearancePoses" or "ManagementConnection" };
        foreach (var x in liveUpdates) x(); foreach (var (b, enabled) in actionButtons) b.IsEnabled = enabled();
        stateText.Text = busy ? T("Working…", "处理中…") : !Connected ? T("DISCONNECTED", "未连接") : client!.IsStatusStale ? T("STALE", "状态过期") : demo ? T("DEMO · LIVE", "演示 · 实时") : T("CONNECTED", "已连接");
        outputPill!.Text = OutputStatePresentation.Pill(client?.Status, Fresh, chinese);
        outputPill.ToolTip = OutputStatePresentation.Detail(client?.Status, Fresh, chinese);
        outputPill.Foreground = Fresh && client?.Status?.Armed == true ? BrushOf("#A85722") : accent;
    }
    void SetNotice(string value) { notice = value; noticeText.Text = value; }
    void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; }
        logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}"); while (logs.Count > 200) logs.RemoveAt(logs.Count - 1);
    }
    async Task RunOperation(Func<Task> action, string success)
    {
        if (busy) return;
        busy = true; UpdateLive();
        try { await action(); if (dirty) success += T(" Local draft retained; not applied.", " 本地草稿已保留，尚未应用。"); SetNotice(success); Log(success); }
        catch (OperationCanceledException) { SetNotice(T("Operation cancelled; not replayed.", "操作已取消，不会自动重试。")); }
        catch (Exception e)
        {
            string msg = connectionNotices.Error(e, chinese);
            SetNotice(T("Not completed: ", "操作未完成：") + msg); Log("ERROR  " + msg);
        }
        finally { busy = false; UpdateLive(); }
    }
    async Task ScanPorts()
    {
        if (demo) return;
        await WithConnectionOperation(async token =>
        {
            if (useBleManagement)
            {
                bleCandidates = await BleDiscovery.ScanAsync(token);
                selectedBle = bleCandidates.FirstOrDefault(candidate => candidate.Id == selectedBle?.Id);
                Log($"{bleCandidates.Length} paired BLE device(s) listed; no GATT connection opened.");
            }
            else
            {
                candidates = await Task.Run(FeatherDiscovery.Scan, token).WaitAsync(token);
                selectedPort = candidates.Length == 1 ? candidates[0] : candidates.FirstOrDefault(c => c.PortName == selectedPort?.PortName);
                Log($"{candidates.Length} matching USB port(s) detected; none opened.");
            }
            BuildPage();
        });
    }
    async Task ConnectDemo() { demoTransport = new DemoTransport(); await Attach(demoTransport); }
    async Task ConnectSelected()
    {
        if (demo) { await ConnectDemo(); return; }
        await WithConnectionOperation(async token =>
        {
            if (useBleManagement)
            {
                var choice = selectedBle ?? throw new InvalidOperationException(T("Select your paired Desk Buddy first.", "请先选择已配对的 Desk Buddy。"));
                var paired = await BleDiscovery.ScanAsync(token);
                if (!paired.Any(p => p.Id == choice.Id)) throw new IOException("The selected device is no longer paired. Nothing connected.");
                await Attach(new BleLineTransport(() => new WindowsBleGattConnection(choice.Id, Dispatcher)), token);
            }
            else
            {
                if (selectedPort is null) throw new InvalidOperationException(T("Select a verified device port first.", "请先选择已识别的设备端口。"));
                var verified = await Task.Run(FeatherDiscovery.Scan, token).WaitAsync(token);
                if (!verified.Any(p => p.PortName == selectedPort.PortName)) throw new IOException("Selected VID/PID port is no longer present.");
                await Attach(new SerialLineTransport(selectedPort.PortName), token);
            }
        });
    }
    async Task Attach(ILineTransport next, CancellationToken cancellationToken = default)
    {
        if (transport is SerialLineTransport { IsClosing: true } pending) throw new WorkerClosePendingException(pending.PortName);
        await StopAnimation(); selectedAnimation = null;
        if (client != null) await client.DisposeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        connectionNotices.Reset();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        transport = next; client = new HubClient(next, next is IManagementTransport { Channel: ManagementChannel.Ble } ? TimeSpan.FromSeconds(8) : null); var session = client;
        session.Changed += () => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(client, session)) { TickAnimation(); UpdateLive(); } });
        next.LineReceived += value =>
        {
            if (new[] { "KEY ", "KEY_RELEASE ", "ENC ", "KNOB_PRESS ", "TOUCH ", "ACTION ", "BINDING ", "LOCAL_MARKER " }.Any(value.StartsWith))
                Dispatcher.BeginInvoke(() => { if (ReferenceEquals(client, session)) Log(value.Length > 180 ? value[..180] : value); });
        };
        next.Disconnected += e => Dispatcher.BeginInvoke(() => { if (!ReferenceEquals(client, session)) return; CancelAnimationForContext(); draftRevision = null; SetNotice(connectionNotices.Disconnected(e, chinese)); Log(e?.Message ?? "Transport closed."); UpdateLive(); });
        try
        {
            await session.ConnectAsync(linked.Token);
            bool keptDraft = dirty;
            await session.RefreshStatusAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (MayCacheDevice)
            {
                if (dirty) RememberWorkspaceDraft();
                ReadSnapshot(true, keepLocalDraft: true); // Real device wins; retained draft requires an explicit editor action.
            }
            else ReadSnapshot(!dirty); // Isolated demo behavior does not enter the real-device cache.
            BuildPage();
            if (keptDraft) Log(T("Local draft retained separately. Device was read; no local draft was sent.", "本地草稿已单独保留；设备已回读，没有发送本地草稿。"));
        }
        catch
        {
            // A cancelled final status read is still a cancelled connection, not a hidden live session.
            await session.DisconnectAsync(); draftRevision = null;
            throw;
        }
    }
    void ReadSnapshot(bool replace, bool keepLocalDraft = false)
    {
        if (client?.Configuration is not { } c) { draftRevision = null; return; }
        if (replace) { draft = BankCodec.Decode(BankCodec.Encode(c.Bank)); dirty = false; editProfile = draft.ActiveProfile; Array.Clear(invalidNames); }
        draftRevision = c.Revision;
        ObserveWorkspaceDevice(c, clearDraft: replace && !keepLocalDraft);
    }
    async Task Disconnect() { await StopAnimation(); if (client != null) await client.DisconnectAsync(); draftRevision = null; BuildPage(); }
    async Task Refresh() { if (client != null && Connected) { await client.RefreshStatusAsync(lifetime.Token); UpdateLive(); } }
    async Task Control(string command)
    {
        if (client == null || !Connected) throw new IOException("No connected device.");
        await client.ControlAsync(command, lifetime.Token); await client.RefreshStatusAsync(lifetime.Token); UpdateLive();
    }
    async Task ApplyBank(bool save)
    {
        if (!save && page == 2 && selectedAnimation is { } action && DeviceAnimationSupported) { await StartAnimation(action, forceDevice: true); return; }
        await StopAnimation();
        await ApplyBankOnly(save, lifetime.Token);
    }
    async Task ApplyBankOnly(bool save, CancellationToken token)
    {
        if (invalidNames.Any(n => n != null)) throw new FormatException(T("Correct invalid profile names before applying.", "请先修正无效配置名称。"));
        if (client == null || draftRevision is not uint rev || !Fresh || client.IsLegacy) throw new InvalidOperationException("Fresh R3 readback required.");
        var candidate = draft with { ActiveProfile = (byte)editProfile }; _ = BankCodec.Encode(candidate);
        client.Capabilities?.RequireSupportedBank(candidate);
        if (save) await client.SaveAsync(candidate, rev, token); else await client.PreviewAsync(candidate, rev, token);
        token.ThrowIfCancellationRequested(); ReadSnapshot(true); await client.RefreshStatusAsync(token); if (page == 2) RefreshAppearance(); else BuildPage();
    }
    async Task Revert()
    { await StopAnimation(); selectedAnimation = null; if (client == null) return; await client.RevertAsync(lifetime.Token); ReadSnapshot(true); await Refresh(); BuildPage(); }
    async Task ReadDevice()
    { await StopAnimation(); selectedAnimation = null; if (client == null) return; await client.GetAsync(lifetime.Token); ReadSnapshot(true); await Refresh(); BuildPage(); }
    async Task SelectProfile()
    { await StopAnimation(); selectedAnimation = null; if (client == null || draftRevision is not uint rev || dirty) return; await client.SelectProfileAsync((byte)editProfile, rev, lifetime.Token); ReadSnapshot(true); await Refresh(); BuildPage(); }
    void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Desk Buddy profiles (*.json)|*.json", Title = T("Import local configuration", "导入本地配置") };
        if (dialog.ShowDialog(this) != true) return;
        try { CancelAnimationForContext(); selectedAnimation = null; draft = LocalConfigurationStore.Load(dialog.FileName); editProfile = draft.ActiveProfile; Array.Clear(invalidNames); dirty = true; RememberWorkspaceDraft(); BuildPage(); SetNotice(T("Imported locally. Nothing sent to device.", "已导入本地，尚未发送到设备。")); }
        catch (Exception e) { SetNotice(T("Import rejected: ", "导入被拒绝：") + e.Message); }
    }
    void Export()
    {
        if (invalidNames.Any(n => n != null)) { SetNotice(T("Correct invalid profile names before exporting.", "请先修正无效配置名称再导出。")); return; }
        var dialog = new SaveFileDialog { Filter = "Desk Buddy profiles (*.json)|*.json", FileName = "desk-buddy-profiles.json", Title = T("Export local configuration", "导出本地配置") };
        if (dialog.ShowDialog(this) != true) return;
        try { LocalConfigurationStore.SaveAtomic(dialog.FileName, draft); SetNotice(T("Local JSON exported and verified.", "本地 JSON 已导出并核验。")); }
        catch (Exception e) { SetNotice(T("Export failed: ", "导出失败：") + e.Message); }
    }
    void ExportLog()
    {
        var dialog = new SaveFileDialog { Filter = "Session log (*.json)|*.json", FileName = "desk-buddy-session.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { format = "desk-buddy-session", demo, events = logs.ToArray() }, new JsonSerializerOptions { WriteIndented = true })); SetNotice(T("Session log exported locally.", "会话日志已导出到本地。")); }
        catch (Exception e) { SetNotice(e.Message); }
    }
}


