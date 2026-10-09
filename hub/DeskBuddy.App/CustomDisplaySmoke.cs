using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    async Task RunCustomDisplaySmoke(string directory, Action<bool, string> check, List<string> images)
    {
        async Task Click(Button button)
        {
            check(button.IsEnabled, "custom timeout operation enabled for valid supported state");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            long until = Environment.TickCount64 + 3000;
            while (busy && Environment.TickCount64 < until) await Task.Delay(10);
            check(!busy, "custom timeout operation completed"); await Settle();
        }
        foreach (bool zh in new[] { true, false })
        {
            demoTransport = new DemoTransport(backlightControl: true); await Attach(demoTransport); await ReadDevice();
            chinese = zh; page = 0; BuildShell(); await Settle();
            EditProfile(p => p with { Name = "KEEP MY DRAFT" }); string bankDraft = BankCodec.ToHex(draft);
            var card = Descendants<Border>(content).Single(b => b.Name == "DisplaySettingsCard"); card.BringIntoView(); await Settle();
            var usb = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayUsbTimeout");
            var battery = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayBatteryTimeout");
            var um = Descendants<TextBox>(card).Single(c => c.Name == "DisplayUsbMinutes");
            var bm = Descendants<TextBox>(card).Single(c => c.Name == "DisplayBatteryMinutes");
            var ue = Descendants<TextBlock>(card).Single(c => c.Name == "DisplayUsbValidation");
            var be = Descendants<TextBlock>(card).Single(c => c.Name == "DisplayBatteryValidation");
            var save = Descendants<Button>(card).Single(b => b.Content?.ToString() == T("Save screen settings", "保存屏幕设置"));
            var read = Descendants<Button>(card).Single(b => b.Content?.ToString() == T("Read current", "读取当前"));
            check(usb.Items.Cast<TimeoutChoice>().Select(c => c.Seconds).SequenceEqual(new uint?[] { 0, 30, 60, 300, 1200, 1800, 3600, null }), "eight bilingual preset/custom choices use correct wire seconds");
            foreach ((uint u, uint b) in new[] { (1200u, 1800u), (3600u, 300u) })
            {
                usb.SelectedItem = usb.Items.Cast<TimeoutChoice>().Single(c => c.Seconds == u);
                battery.SelectedItem = battery.Items.Cast<TimeoutChoice>().Single(c => c.Seconds == b);
                await Refresh(); check(((TimeoutChoice)usb.SelectedItem).Seconds == u && ((TimeoutChoice)battery.SelectedItem).Seconds == b, "poll preserves both independent preset edits");
                await Click(save);
                check(client!.Status!.Display!.UsbTimeoutSeconds == u && client.Status.Display.BatteryTimeoutSeconds == b, "20/30/60-minute presets save and read back exactly");
            }
            usb.SelectedItem = usb.Items.Cast<TimeoutChoice>().Single(c => c.Seconds is null);
            battery.SelectedItem = battery.Items.Cast<TimeoutChoice>().Single(c => c.Seconds is null);
            um.Text = "7"; bm.Text = "720";
            await Refresh();
            check(um.Text == "7" && bm.Text == "720" && um.IsVisible && bm.IsVisible && save.IsEnabled, "custom 7min/12h visible, valid and retained across polling");
            um.Text = "7."; bool hadFocus = um.Focus(); um.SelectionStart = 1; um.SelectionLength = 1;
            for (int i = 0; i < 3; i++) { await Refresh(); await Task.Delay(120); UpdateLive(); }
            check(um.Text == "7." && um.IsEnabled && um.SelectionStart == 1 && um.SelectionLength == 1 && (!hadFocus || um.IsKeyboardFocusWithin),
                "poll and periodic live updates preserve partial input, caret, selection and acquired keyboard focus");
            um.Text = "7";
            foreach (string bad in new[] { "", " ", "0", "-1", "1.5", "7.0", "721", "abc", "1e2", "+7" })
            {
                int sent = demoTransport.SentLineCount; um.Text = bad; UpdateLive();
                check(!save.IsEnabled && ue.Visibility == Visibility.Visible && ue.Text.Contains("1") && ue.Text.Contains("720"), "invalid USB custom input has inline explanation and disables save");
                check(demoTransport.SentLineCount == sent, "invalid textbox edit sends no command");
                um.Text = "7"; bm.Text = bad; UpdateLive();
                check(!save.IsEnabled && be.Visibility == Visibility.Visible, "invalid battery custom input independently disables save");
                bm.Text = "720";
            }
            um.Text = "1.5"; bm.Text = ""; await Settle(); card.BringIntoView(); await Settle();
            string file = $"{(zh ? "zh" : "en")}-display-custom-invalid.png";
            CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
            um.Text = "7"; bm.Text = "720";
            demoTransport.RejectNextDisplay = "SAVE_FAILED";
            await Click(save);
            check(um.Text == "7" && bm.Text == "720" && !save.IsEnabled && client!.Status is null, "failed save keeps local edits and blocks further save until status is fresh");
            await Refresh();
            check(um.Text == "7" && bm.Text == "720" && save.IsEnabled, "fresh poll after failure keeps unsaved custom input");
            await Click(save);
            check(client!.Status!.Display is { Saved: true, UsbTimeoutSeconds: 420, BatteryTimeoutSeconds: 43200 }, "custom exact minute-to-second conversion is independently read back");
            check(dirty && BankCodec.ToHex(draft) == bankDraft && demoTransport.ConfigurationWriteRequests == 0, "custom save does not commit or overwrite profile draft");
            check(um.Text == "7" && bm.Text == "720" && !save.IsEnabled, "readback maps nonpreset values to custom editors and clears dirty timeout state");
            file = $"{(zh ? "zh" : "en")}-display-custom-saved.png";
            CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
            double originalWidth = Width, originalHeight = Height;
            Width = MinWidth; Height = MinHeight; await Settle();
            check(um.ActualWidth > 0 && bm.ActualWidth > 0 &&
                um.TransformToAncestor(card).TransformBounds(new Rect(0, 0, um.ActualWidth, um.ActualHeight)).Right <= card.ActualWidth &&
                bm.TransformToAncestor(card).TransformBounds(new Rect(0, 0, bm.ActualWidth, bm.ActualHeight)).Right <= card.ActualWidth,
                "both custom minute editors remain inside card at minimum window width");
            // 192-dpi raster is explicitly a 2x render, not a change to Windows display settings.
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * 2),
                (int)Math.Ceiling(card.ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
            // A direct Render(card) retains its scroll/layout offset and can yield a blank image.
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new System.Windows.Media.VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            bitmap.Render(drawing);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int visible = 0; for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0 && pixels[i - 1] < 220) visible++;
            check(visible > 1000, "minimum-window 2x card screenshot contains actual visible UI pixels");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            file = $"{(zh ? "zh" : "en")}-display-custom-minimum-window-2x.png";
            using (var stream = File.Create(Path.Combine(directory, file))) encoder.Save(stream);
            images.Add(file); Width = originalWidth; Height = originalHeight; await Settle();
            um.Text = "8.";
            page = 1; BuildPage(); page = 0; BuildPage(); await Settle();
            card = Descendants<Border>(content).Single(b => b.Name == "DisplaySettingsCard"); card.BringIntoView(); await Settle();
            usb = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayUsbTimeout");
            battery = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayBatteryTimeout");
            um = Descendants<TextBox>(card).Single(c => c.Name == "DisplayUsbMinutes");
            bm = Descendants<TextBox>(card).Single(c => c.Name == "DisplayBatteryMinutes");
            save = Descendants<Button>(card).Single(b => b.Content?.ToString() == T("Save screen settings", "保存屏幕设置"));
            read = Descendants<Button>(card).Single(b => b.Content?.ToString() == T("Read current", "读取当前"));
            check(um.Text == "8." && bm.Text == "720" && !save.IsEnabled, "navigation within same connection preserves even incomplete timeout draft");
            um.Text = "8"; await Click(read);
            check(um.Text == "7" && bm.Text == "720" && !save.IsEnabled, "explicit Read discards only timeout edits using actual device settings");
            usb.SelectedItem = usb.Items.Cast<TimeoutChoice>().Single(c => c.Seconds == 300);
            battery.SelectedItem = battery.Items.Cast<TimeoutChoice>().Single(c => c.Seconds == 300);
            check(!um.IsVisible && !bm.IsVisible, "selecting a preset hides custom fields without carrying stale custom values");
            await Click(save);
            check(client.Status!.Display is { UsbTimeoutSeconds: 300, BatteryTimeoutSeconds: 300 }, "five-minute settings remain five minutes");

            // Reconnect from capable peer: no stale custom choices or draft may survive the session boundary.
            demoTransport = new DemoTransport(displayCustom: false); await Attach(demoTransport); await ReadDevice();
            page = 0; BuildShell(); await Settle();
            card = Descendants<Border>(content).Single(b => b.Name == "DisplaySettingsCard"); card.BringIntoView(); await Settle();
            usb = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayUsbTimeout");
            battery = Descendants<ComboBox>(card).Single(c => c.Name == "DisplayBatteryTimeout");
            check(usb.Items.Count == 4 && battery.Items.Count == 4 && usb.Items.Cast<TimeoutChoice>().All(c => c.Seconds.HasValue), "legacy display peer shows only four compatible options after reconnect");
            check(!Descendants<TextBox>(card).Any(t => t.IsVisible), "legacy card has no active custom editor");
            check(Descendants<TextBlock>(card).Single(t => t.Name == "DisplayDraftNote").Text.Contains(T("Only", "仅支持")), "legacy duration limitation clearly explained in both languages");
            int oldSent = demoTransport.SentLineCount;
            bool blocked = false; try { await client!.SetDisplayAsync(1200, 300); } catch (HubProtocolException e) when (e.Code == "UNSUPPORTED_DISPLAY_TIMEOUT") { blocked = true; }
            check(blocked && oldSent == demoTransport.SentLineCount, "extended setting cannot reach old-peer transport");
            file = $"{(zh ? "zh" : "en")}-display-legacy-durations.png";
            CaptureShellForSmoke(Path.Combine(directory, file)); images.Add(file);
        }
        await ConnectDemo(); await ReadDevice();
    }
}
