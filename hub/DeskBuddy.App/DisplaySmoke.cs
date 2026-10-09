using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    async Task RunDisplaySmoke(string directory, Action<bool, string> check, List<string> images)
    {
        async Task Click(Button button)
        {
            check(button.IsEnabled, "display operation button is enabled only for supported fresh state");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            long until = Environment.TickCount64 + 3000;
            while (busy && Environment.TickCount64 < until) await Task.Delay(10);
            check(!busy, "display operation returns through shared bounded operation flow"); await Settle();
        }
        foreach (bool backlight in new[] { false, true })
        {
            demoTransport = new DemoTransport(backlightControl: backlight); await Attach(demoTransport); await ReadDevice();
            EditProfile(p => p with { Name = "LOCAL ONLY" }); string draftBefore = BankCodec.ToHex(draft);
            int bankWrites = demoTransport.ConfigurationWriteRequests;
            foreach (bool zh in new[] { true, false })
            {
                chinese = zh; page = 0; BuildShell(); await Settle();
                var card = Descendants<Border>(content).Single(b => b.Name == "DisplaySettingsCard"); card.BringIntoView(); await Settle();
                var usb = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayUsbTimeout");
                var battery = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayBatteryTimeout");
                check(usb.Items.Count == 8 && battery.Items.Count == 8 && usb.SelectedItem is TimeoutChoice && battery.SelectedItem is TimeoutChoice, "display card reads device values with eight choices including custom");
                uint requestedUsb = zh ? 30u : 60u, requestedBattery = zh ? 300u : 30u;
                usb.SelectedItem = usb.Items.Cast<TimeoutChoice>().Single(t => t.Seconds == requestedUsb);
                battery.SelectedItem = battery.Items.Cast<TimeoutChoice>().Single(t => t.Seconds == requestedBattery);
                await Refresh();
                check(((TimeoutChoice)usb.SelectedItem).Seconds == requestedUsb && ((TimeoutChoice)battery.SelectedItem).Seconds == requestedBattery, "status polling does not overwrite edited timeout controls");
                var save = Descendants<Button>(card).Single(b => b.Content?.ToString() == T("Save screen settings", "保存屏幕设置"));
                await Click(save);
                check(client!.Status!.Display is { Saved: true } actual && actual.UsbTimeoutSeconds == requestedUsb && actual.BatteryTimeoutSeconds == requestedBattery, "actual Save button verifies independent persisted timeouts");
                check(dirty && BankCodec.ToHex(draft) == draftBefore && demoTransport.ConfigurationWriteRequests == bankWrites, "screen save keeps pending profile edits and sends no bank write");
                var note = Descendants<TextBlock>(card).Single(t => t.Name == "DisplayBacklightNote");
                check(note.Text.Contains(backlight ? T("turns off", "关闭背光") : T("stays on", "背光仍亮")), "backlight explanation matches actual negotiated capability");
                await Task.Delay(220); await Refresh(); await Control("OUTPUT 1");
                await Click(Descendants<Button>(card).Single(b => b.Content?.ToString() == T("Rest now", "立即休屏")));
                check(client.Status.Armed && client.Status.Display!.Asleep && client.Status.HidSuppressed, "rest button retains ON and displays pause");
                check(outputPill!.Text.Contains(T("ON", "开启")) && outputPill.Text.Contains(T("PAUSED", "暂停")), "sleeping ON is not misreported as OFF");
                string file = $"{(zh ? "zh" : "en")}-display-rest-backlight{(backlight ? 1 : 0)}.png";
                CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
                int before = demoTransport.Commands.Count;
                await Click(Descendants<Button>(card).Single(b => b.Content?.ToString() == T("Wake", "唤醒")));
                check(!client.Status!.Display!.Asleep && client.Status.Armed && !demoTransport.Commands.Skip(before).Contains("CONTROL OUTPUT 1"), "wake button never performs a compensating output ON");
                await Control("OUTPUT 0");
                file = $"{(zh ? "zh" : "en")}-display-awake-backlight{(backlight ? 1 : 0)}.png";
                CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
            }
        }
        // Old firmware remains usable but the display controls are explicitly unavailable.
        demoTransport = new DemoTransport(displaySleep: false); await Attach(demoTransport); await ReadDevice();
        chinese = true; page = 0; BuildShell(); await Settle();
        var oldCard = Descendants<Border>(content).Single(b => b.Name == "DisplaySettingsCard"); oldCard.BringIntoView(); await Settle();
        check(Descendants<ComboBox>(oldCard).All(c => !c.IsEnabled) && Descendants<Button>(oldCard).All(b => !b.IsEnabled), "old firmware disables display actions and timeout selectors");
        check(Descendants<TextBlock>(oldCard).Any(t => t.Text == "当前固件不支持"), "old firmware absence is explained without claiming sleep status");
        CaptureShellForSmoke(Path.Combine(directory, "zh-display-old-firmware.png")); images.Add("zh-display-old-firmware.png");
        EditProfile(p => { var bindings = (HubBinding[])p.Bindings.Clone(); bindings[0] = new(BindingKind.Local, 5); return p with { Bindings = bindings }; });
        page = 1; BuildShell(); await Settle(); int oldWrites = demoTransport.ConfigurationWriteRequests;
        check(!CanApplyDraft, "unsupported local sleep binding blocks whole-bank Apply");
        bool rejected = false; try { await ApplyBank(false); } catch (HubProtocolException e) when (e.Code == "UNSUPPORTED_BINDING") { rejected = true; }
        check(rejected && demoTransport.ConfigurationWriteRequests == oldWrites, "old peer sleep-binding guard happens before transmission");
        await ConnectDemo(); await ReadDevice();
    }
}
