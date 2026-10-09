using System.Windows;
using System.Windows.Controls;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    sealed record TimeoutChoice(uint? Seconds, string Label) { public override string ToString() => Label; }
    sealed class TimeoutDraft
    {
        public HelloInfo? Session;
        public bool Editing, UsbSelected, BatterySelected;
        public uint? UsbChoice, BatteryChoice;
        public string UsbMinutes = "", BatteryMinutes = "";
    }
    readonly TimeoutDraft timeoutDraft = new();
    void BuildDisplaySettingsCard()
    {
        TimeoutChoice[] legacy = [new(0, T("Never", "永不")), new(30, T("30 seconds", "30 秒")), new(60, T("1 minute", "1 分钟")), new(300, T("5 minutes", "5 分钟"))];
        TimeoutChoice[] extended = [.. legacy, new(1200, T("20 minutes", "20 分钟")), new(1800, T("30 minutes", "30 分钟")), new(3600, T("1 hour", "1 小时")), new(null, T("Custom…", "自定义…"))];
        var usb = new ComboBox { Name = "DisplayUsbTimeout", ItemsSource = legacy, Width = 210, SelectedIndex = -1 };
        var battery = new ComboBox { Name = "DisplayBatteryTimeout", ItemsSource = legacy, Width = 210, SelectedIndex = -1 };
        var usbMinutes = new TextBox { Name = "DisplayUsbMinutes", Width = 96, MaxLength = 12 };
        var batteryMinutes = new TextBox { Name = "DisplayBatteryMinutes", Width = 96, MaxLength = 12 };
        var usbCustom = Row(usbMinutes, Txt(T("minutes", "分钟"), 12));
        var batteryCustom = Row(batteryMinutes, Txt(T("minutes", "分钟"), 12));
        var usbError = Note(""); usbError.Name = "DisplayUsbValidation"; usbError.MaxWidth = 210;
        var batteryError = Note(""); batteryError.Name = "DisplayBatteryValidation"; batteryError.MaxWidth = 210;
        usbError.Foreground = batteryError.Foreground = System.Windows.Media.Brushes.Firebrick;
        var stateText = Txt("", 15, true); stateText.Name = "DisplayState";
        var hardware = Note(""); hardware.Name = "DisplayBacklightNote";
        var localNote = Note(""); localNote.Name = "DisplayDraftNote"; localNote.MaxWidth = 205;
        bool editing = false, syncing = false, hasCustom = false;
        HelloInfo? session = null;
        bool Supported() => Connected && client?.IsLegacy == false && client.Capabilities?.SupportsDisplaySleep == true;
        bool Ready() => Supported() && Fresh && client?.Status?.Display != null && !busy;
        bool Value(ComboBox combo, TextBox minutes, out uint seconds)
        {
            seconds = 0;
            if (combo.SelectedItem is not TimeoutChoice choice) return false;
            if (choice.Seconds is { } preset) { seconds = preset; return true; }
            return hasCustom && DisplayTimeoutInput.TryMinutes(minutes.Text, out seconds);
        }
        void CopyOne(ComboBox combo, TextBox minutes, uint seconds)
        {
            var options = hasCustom ? extended : legacy;
            combo.SelectedItem = options.FirstOrDefault(o => o.Seconds == seconds) ?? (hasCustom ? extended[^1] : null);
            minutes.Text = seconds >= 60 ? (seconds / 60).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        }
        void CopyObserved()
        {
            if (client?.Status?.Display is not { } actual || !Fresh) return;
            syncing = true;
            try
            {
                CopyOne(usb, usbMinutes, actual.UsbTimeoutSeconds);
                CopyOne(battery, batteryMinutes, actual.BatteryTimeoutSeconds);
            }
            finally { syncing = false; }
        }
        void RememberEdit()
        {
            if (syncing) return;
            editing = timeoutDraft.Editing = true; timeoutDraft.Session = client?.Capabilities;
            timeoutDraft.UsbSelected = usb.SelectedItem is TimeoutChoice; timeoutDraft.BatterySelected = battery.SelectedItem is TimeoutChoice;
            timeoutDraft.UsbChoice = (usb.SelectedItem as TimeoutChoice)?.Seconds; timeoutDraft.BatteryChoice = (battery.SelectedItem as TimeoutChoice)?.Seconds;
            timeoutDraft.UsbMinutes = usbMinutes.Text; timeoutDraft.BatteryMinutes = batteryMinutes.Text; UpdateLive();
        }
        void Changed(object? _, SelectionChangedEventArgs __) => RememberEdit();
        usb.SelectionChanged += Changed; battery.SelectionChanged += Changed;
        usbMinutes.TextChanged += (_, _) => RememberEdit();
        batteryMinutes.TextChanged += (_, _) => RememberEdit();
        async Task Read()
        { await client!.ReadDisplayAsync(lifetime.Token); editing = timeoutDraft.Editing = false; CopyObserved(); }
        async Task Save()
        {
            if (!Value(usb, usbMinutes, out uint u) || !Value(battery, batteryMinutes, out uint b))
                throw new InvalidOperationException(T("Enter whole minutes from 1 to 720.", "请输入 1–720 的整数分钟。"));
            await client!.SetDisplayAsync(u, b, lifetime.Token); editing = timeoutDraft.Editing = false; CopyObserved();
        }
        var buttons = Row(
            AsyncBtn(T("Read current", "读取当前"), Read, T("Display settings read; profile edits were kept.", "已读取屏幕设置，按键/外观草稿保留。"), Ready),
            AsyncBtn(T("Save screen settings", "保存屏幕设置"), Save, T("Screen timeouts saved and read back. Profile bank unchanged.", "屏幕超时已保存并回读，按键/外观配置未改。"), () => Ready() && Value(usb, usbMinutes, out _) && Value(battery, batteryMinutes, out _) && (editing || client!.Status!.Display!.Saved == false), true),
            AsyncBtn(T("Rest now", "立即休屏"), async () => { await client!.SleepDisplayAsync(lifetime.Token); }, T("Rest request acknowledged; current state is shown on this card.", "休屏请求已回读，当前状态见本卡片。"), () => Ready() && client!.Status!.Display!.Asleep == false),
            AsyncBtn(T("Wake", "唤醒"), async () => { await client!.WakeDisplayAsync(lifetime.Token); }, T("Wake request acknowledged; output was not enabled automatically.", "唤醒请求已回读，未自动开启输出。"), () => Ready() && client!.Status!.Display!.Asleep == true));
        var fields = Row(Stack(Txt(T("With USB power", "USB 供电"), 12, true), usb, usbCustom, usbError),
            Stack(Txt(T("Battery only", "仅电池供电"), 12, true), battery, batteryCustom, batteryError), localNote);
        var card = Card(Stack(Txt(T("Screen rest", "屏幕休息"), 18, true), stateText, hardware, fields, buttons,
            Note(T("The first touch, key press or knob movement only wakes the screen; release and act again. Rest/wake preserves the output switch. Hub status polling does not wake it. These settings are saved independently of profiles.",
                "首次触摸、按键或转动旋钮只唤醒；抬起后再操作。休屏/唤醒保留输出开关，Hub 状态轮询不会唤醒。这里的设置独立保存，不与三套配置草稿混合。"))));
        card.Name = "DisplaySettingsCard"; content.Children.Add(card);
        void UpdateInput(ComboBox combo, TextBox minutes, FrameworkElement custom, TextBlock error)
        {
            bool selectedCustom = combo.SelectedItem is TimeoutChoice { Seconds: null };
            custom.Visibility = selectedCustom ? Visibility.Visible : Visibility.Collapsed;
            minutes.IsEnabled = Ready() && hasCustom;
            error.Text = selectedCustom && !DisplayTimeoutInput.TryMinutes(minutes.Text, out _)
                ? T("Enter a whole number, 1–720 minutes.", "请输入 1–720 的整数分钟。") : "";
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        string Duration(uint seconds) => seconds == 0 ? T("never", "永不") : seconds == 30 ? T("30 s", "30 秒") : T($"{seconds / 60} min", $"{seconds / 60} 分钟");
        liveUpdates.Add(() =>
        {
            if (!ReferenceEquals(session, client?.Capabilities))
            {
                session = client?.Capabilities; editing = ReferenceEquals(timeoutDraft.Session, session) && timeoutDraft.Editing;
                timeoutDraft.Session = session; timeoutDraft.Editing = editing; hasCustom = session?.SupportsCustomDisplayTimeout == true;
                syncing = true;
                try
                {
                    var options = hasCustom ? extended : legacy; usb.ItemsSource = battery.ItemsSource = options;
                    usb.SelectedItem = editing && timeoutDraft.UsbSelected ? options.FirstOrDefault(o => o.Seconds == timeoutDraft.UsbChoice) : null;
                    battery.SelectedItem = editing && timeoutDraft.BatterySelected ? options.FirstOrDefault(o => o.Seconds == timeoutDraft.BatteryChoice) : null;
                    usbMinutes.Text = editing ? timeoutDraft.UsbMinutes : ""; batteryMinutes.Text = editing ? timeoutDraft.BatteryMinutes : "";
                }
                finally { syncing = false; }
            }
            if (!editing && Supported() && Fresh) CopyObserved();
            usb.IsEnabled = battery.IsEnabled = Ready();
            UpdateInput(usb, usbMinutes, usbCustom, usbError); UpdateInput(battery, batteryMinutes, batteryCustom, batteryError);
            if (!Supported())
            {
                stateText.Text = Connected ? T("Not supported by this firmware", "当前固件不支持") : T("Connect to read screen settings", "连接后读取屏幕设置");
                hardware.Text = T("Requires explicit display_sleep support. No display command or sleep binding is sent to older firmware.", "需要设备明确支持 display_sleep；不会向旧固件发送休屏命令或新休屏绑定。");
            }
            else
            {
                var actual = Fresh ? client?.Status?.Display : null;
                stateText.Text = actual == null ? T("Status unknown / stale", "状态未知 / 已过期") :
                    (actual.Asleep ? T("Resting", "休屏中") : T("Awake", "已唤醒")) + " · " +
                    (actual.Saved ? T("Settings saved", "设置已保存") : T("Settings not yet saved", "设置尚未保存")) +
                    $" · USB {Duration(actual.UsbTimeoutSeconds)} / " + T("Battery ", "电池 ") + Duration(actual.BatteryTimeoutSeconds);
                hardware.Text = client!.Capabilities!.BacklightControl
                    ? T("Firmware reports backlight control: rest also turns off the backlight. This is not whole-device power off or a measured battery-life claim.", "固件声明已启用背光控制：休屏时也关闭背光。这不等于整机关机，也不代表续航已实测提升。")
                    : T("No backlight control on this build: the picture goes black but the backlight stays on. It is not power off; a later LITE wiring change and matching firmware are required for a dark backlight.", "本版无背光控制：只隐藏画面，背光仍亮，不是关机。真正关闭背光需后续补接 LITE 并使用匹配固件。");
            }
            localNote.Text = (editing ? T("Local timeout edits · not saved", "本地超时草稿 · 未保存") : "") +
                (Supported() && !hasCustom ? T("\nOnly Never, 30 s, 1 min and 5 min. Update device firmware for longer/custom times.", "\n本固件仅支持永不、30 秒、1 分钟和 5 分钟。长时间和自定义需升级设备固件。") : "");
        });
    }
}
