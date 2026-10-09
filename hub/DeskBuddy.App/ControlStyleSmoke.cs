using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskBuddy.App;

/// <summary>An isolated native-control render harness. Never creates a transport or MainWindow.</summary>
internal static class ControlStyleSmoke
{
    internal static Window CreateWindow(string directory)
    {
        var window = new Window
        {
            Title = "Desk Buddy · offline control-style check", Background = Brush("#F4F7F8"),
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = 14,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize
        };
        ControlStyles.Register(window.Resources);
        var panel = new StackPanel { Width = 560, Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Desk Buddy · 选择控件 / Selection controls", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brush("#1C3540"), Margin = new Thickness(0, 0, 0, 16) });
        ComboBox Field(string label, string[] values, int selected = 0, bool enabled = true)
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = Brush("#60747D"), Margin = new Thickness(0, 0, 0, 5) });
            var combo = new ComboBox { ItemsSource = values, SelectedIndex = selected, IsEnabled = enabled, Margin = new Thickness(0, 0, 0, 13) };
            panel.Children.Add(combo); return combo;
        }
        var connection = Field("管理连接 · USB / Bluetooth", ["USB 线缆", "蓝牙 LE · 已配对设备"], 1);
        Field("设备 · 长名称自动省略", ["Desk Buddy · a deliberately very long device display name that should remain inside this field, never widen a popup"]);
        var mapping = Field("K1 输入映射 · 112 个选项可滚动", Enumerable.Range(1, 112).Select(i => i == 1 ? "播放 / 暂停" : $"键盘按键 / Keyboard key {i}").ToArray());
        Field("设备未连接 · Disabled", ["暂不可用 / Not connected"], enabled: false);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "界面缩放", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#1C3540"), Margin = new Thickness(0, 0, 14, 0) });
        var zoom = new ComboBox { Width = 104, ItemsSource = new[] { "100%", "125%", "150%", "200%" }, SelectedIndex = 0 };
        row.Children.Add(zoom); panel.Children.Add(row);
        var host = new Border { Background = Brush("#F4F7F8"), Child = panel };
        window.Content = host;
        window.Loaded += async (_, _) =>
        {
            var checks = new List<string>(); var images = new List<string>();
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
            try
            {
                Directory.CreateDirectory(directory);
                foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    panel.LayoutTransform = new ScaleTransform(scale, scale);
                    await Settle(window);
                    Check(connection.Template.FindName("Frame", connection) is Border { CornerRadius.TopLeft: 8 }, $"rounded native ComboBox template at {scale * 100:0}%");
                    Check(connection.ActualHeight >= 40 && connection.Focusable && connection.IsTabStop && connection.IsTextSearchEnabled, $"native keyboard/tab/text-search semantics retained at {scale * 100:0}%");
                    Check(connection.Template.FindName("DropDownToggle", connection) is ToggleButton { Focusable: false, IsTabStop: false }, $"toggle does not add a keyboard tab stop at {scale * 100:0}%");
                    string file = $"controls-{scale * 100:0}.png"; Capture(host, Path.Combine(directory, file)); images.Add(file);
                    var peer = new ComboBoxAutomationPeer(mapping);
                    var expand = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
                    expand.Expand(); await Settle(window);
                    var popup = (Popup)mapping.Template.FindName("PART_Popup", mapping);
                    var popupFrame = (Border)popup.Child;
                    Check(mapping.IsDropDownOpen && popup.IsOpen, $"UI Automation expands real native popup at {scale * 100:0}%");
                    Check(Math.Abs(popupFrame.ActualWidth - mapping.ActualWidth) < 1 && popupFrame.ActualHeight <= mapping.MaxDropDownHeight + 1, $"popup width follows field and height bounded at {scale * 100:0}%");
                    var scroll = (ScrollViewer)popupFrame.Child;
                    Check(scroll.ScrollableHeight > 0, $"long option list scrolls at {scale * 100:0}%");
                    file = $"popup-{scale * 100:0}.png"; Capture(popupFrame, Path.Combine(directory, file)); images.Add(file);
                    expand.Collapse(); await Settle(window);
                    Check(!mapping.IsDropDownOpen && mapping.SelectedIndex == 0, $"collapse does not change selected item at {scale * 100:0}%");
                }
                panel.LayoutTransform = Transform.Identity; await Settle(window);
                // Routed native key handlers are checked directly; no OS keyboard or mouse input is injected.
                void Key(ComboBox combo, System.Windows.Input.Key key) => combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
                Key(mapping, System.Windows.Input.Key.Down);
                Check(mapping.SelectedIndex == 1, "native Down key advances closed selection");
                Key(mapping, System.Windows.Input.Key.Up);
                Check(mapping.SelectedIndex == 0, "native Up key restores closed selection");
                Key(mapping, System.Windows.Input.Key.F4); await Settle(window);
                Check(mapping.IsDropDownOpen, "native F4 key opens popup");
                Key(mapping, System.Windows.Input.Key.Escape); await Settle(window);
                Check(!mapping.IsDropDownOpen, "native Escape key closes popup");
                await File.WriteAllTextAsync(Path.Combine(directory, "control-style-smoke.json"), JsonSerializer.Serialize(new { passed = true, hardware_access = false, checks, images, scale_kind = "WPF LayoutTransform DIP scaling; not physical monitor DPI", keyboard_kind = "native routed key handlers and UI Automation; no injected OS input" }, new JsonSerializerOptions { WriteIndented = true }));
                Environment.ExitCode = 0;
            }
            catch (Exception e)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "control-style-smoke.json"), JsonSerializer.Serialize(new { passed = false, hardware_access = false, checks, images, error = e.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                Environment.ExitCode = 1;
            }
            finally { window.Close(); }
        };
        return window;
    }
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static async Task Settle(Window window) { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(70); window.UpdateLayout(); }
    static void Capture(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
