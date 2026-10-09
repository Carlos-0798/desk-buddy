using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    bool SupportsDraft => client?.Capabilities?.SupportsBank(draft) == true;
    bool CanApplyDraft => CanConfigure && SupportsDraft;
    bool ThemeAvailable(int id) => !Connected || client?.Capabilities?.SupportsTheme((Theme)id) == true;
    bool FaceAvailable(int id) => !Connected || client?.Capabilities?.SupportsFace((Face)id) == true;
    bool ModeAvailable(int id) => !Connected || client?.Capabilities?.SupportsHomeMode((HomeMode)id) == true;
    string AvailabilityNote(bool available) => available ? "" : T("LOCAL PREVIEW ONLY", "仅本地预览 · 设备不支持");

    void RefreshAppearance()
    {
        var viewer = FindVisual<ScrollViewer>(content); double offset = viewer?.VerticalOffset ?? 0;
        BuildPage();
        Dispatcher.BeginInvoke(() => viewer?.ScrollToVerticalOffset(offset));
    }
    void BuildAppearance()
    {
        ProfileTabs(); ConfigActions();
        var modes = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
        string[] modeNames = [T("Face panel", "表情面板"), T("Desk companion", "桌面伙伴"), T("Virtual pet", "电子宠物")];
        string[] modeNotes = [T("Large pixel portraits. Walking needs a full-body mode.", "精细像素头像；走路需切换全身模式。"), T("A full-body companion on the device screen.", "设备小屏上的完整小伙伴。"), T("Full-body companion + this-boot interaction count.", "全身伙伴，加上本次开机互动次数。")];
        for (int i = 0; i < 3; i++)
        {
            int index = i; bool selected = (int)Editing.Mode == i;
            var label = Stack(Txt((selected ? "✓  " : "") + modeNames[i], 14, true, selected ? Brushes.White : ink),
                Txt(modeNotes[i], 11, false, selected ? BrushOf("#D9F3EB") : muted));
            if (!ModeAvailable(i)) label.Children.Add(Txt(AvailabilityNote(false), 10, true, selected ? Brushes.White : muted));
            var button = Btn("", () => { EditProfile(p => p with { Mode = (HomeMode)index }); RefreshAppearance(); }, selected);
            button.Content = label; button.Padding = new Thickness(13, 10, 13, 10); modes.Children.Add(button);
        }
        content.Children.Add(Card(Stack(Txt(T("Device-screen home mode", "设备小屏的主页模式"), 17, true), modes,
            Note(T("Not a Windows floating pet. No feeding or persistent growth. Real interaction counts remain separate from previews.", "不是 Windows 悬浮宠物，不含喂养或成长存档。试看不会增加真实互动计数。")))));
        BuildAnimationControls();

        var themePanel = Stack(Txt(T("8 themes · set the scene", "8 套主题 · 选择氛围"), 18, true),
            Note(T("Classic themes keep their original IDs. New scenes add restrained geometric details.", "经典主题保留原编号；新主题加入适量几何场景装饰。")));
        themePanel.Children.Add(ThemeGroup(0, 4, T("CLASSICS", "经典配色")));
        themePanel.Children.Add(ThemeGroup(4, 4, T("SCENES", "场景配色")));
        var themeCard = Card(themePanel); themeCard.Name = "AppearanceThemes"; content.Children.Add(themeCard);

        var faces = Stack(Txt(T("12 characters · a personality for every desk", "12 个角色 · 找到喜欢的伙伴"), 18, true),
            Note(T("Your character stays the same across face, desk-companion and virtual-pet modes.", "表情面板、桌面伙伴与电子宠物模式共用同一角色。")));
        faces.Children.Add(FaceGroup(0, 4, T("FAMILIAR FACES", "熟悉的面孔")));
        faces.Children.Add(FaceGroup(4, 4, T("SOFT & CURIOUS", "软萌与好奇")));
        faces.Children.Add(FaceGroup(8, 4, T("LITTLE PERSONALITIES", "个性小伙伴")));
        var faceCard = Card(faces); faceCard.Name = "AppearanceCharacters"; content.Children.Add(faceCard);

        var options = new StackPanel(); options.Children.Add(Txt(T("Finishing touches", "最后一点风格"), 17, true));
        var animation = new CheckBox { Content = T("Automatic idle blinking", "自动待机眨眼"), IsChecked = Editing.Animation, Margin = new Thickness(0, 14, 0, 8) };
        animation.Click += (_, _) => { EditProfile(p => p with { Animation = animation.IsChecked == true }); UpdateLive(); }; options.Children.Add(animation);
        options.Children.Add(Note(T("Turning idle motion off does not suppress brief physical-input reactions.", "关闭待机动画不会屏蔽实物输入带来的短暂反应。")));
        options.Children.Add(Note(T("Accent color (advanced): 0000 uses the theme default.", "强调色（高级）：0000 使用主题默认色。")));
        var color = new TextBox { Text = Editing.AccentRgb565.ToString("X4"), Width = 100, MaxLength = 4 };
        options.Children.Add(Row(color, Btn(T("Set color", "设置颜色"), () =>
        { if (ushort.TryParse(color.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) EditProfile(p => p with { AccentRgb565 = value }); else SetNotice(T("Enter four hexadecimal digits.", "请输入四位十六进制数。")); })));
        options.Children.Add(Row(new Button { Content = T("Image upload · unavailable", "图片上传 · 暂不支持"), IsEnabled = false }, new Button { Content = T("Brightness · unavailable", "亮度 · 暂不支持"), IsEnabled = false }));
        content.Children.Add(Card(options));
    }
    UIElement ThemeGroup(int first, int count, string title)
    {
        var group = Stack(Txt(title, 10, true, accent)); group.Margin = new Thickness(0, 20, 0, 0);
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 10, -8, 0) };
        for (int i = first; i < first + count; i++)
        {
            int index = i; bool selected = (int)Editing.Theme == i, available = ThemeAvailable(i);
            Brush foreground = BrushOf(DevicePreview.Accents[i]);
            var label = Stack(Txt((selected ? "✓  " : "") + (chinese ? DevicePreview.ThemeChinese[i] : DevicePreview.ThemeNames[i]), 12, true, foreground),
                Txt(chinese ? DevicePreview.ThemeNames[i] : DevicePreview.ThemeChinese[i], 10, false, foreground));
            if (!available) label.Children.Add(Txt(T("Local only", "仅本地预览"), 9, true, foreground));
            var button = Btn("", () => { EditProfile(p => p with { Theme = (Theme)index }); RefreshAppearance(); });
            button.Content = label; button.Background = BrushOf(DevicePreview.Backgrounds[i]); button.BorderBrush = selected ? accent : line;
            button.BorderThickness = new Thickness(selected ? 3 : 1); button.MinHeight = 88; button.Padding = new Thickness(8); button.Margin = new Thickness(0, 0, 8, 0);
            button.ToolTip = available ? T("Select this local theme", "选择本地主题") : T("Connected firmware cannot apply this theme; local preview is available.", "当前固件不支持发送此主题，但仍可本地预览。" );
            grid.Children.Add(button);
        }
        group.Children.Add(grid); return group;
    }
    UIElement FaceGroup(int first, int count, string title)
    {
        var group = Stack(Txt(title, 10, true, accent)); group.Margin = new Thickness(0, 20, 0, 0);
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 10, -8, 0) };
        for (int i = first; i < first + count; i++)
        {
            int index = i; bool selected = (int)Editing.Face == i, available = FaceAvailable(i);
            var portrait = new DevicePreview { Width = 160, Height = 92, Theme = (int)Editing.Theme, Face = i, Mode = (int)Editing.Mode, PortraitOnly = true, Animate = false };
            var portraitBox = new Viewbox { Height = 64, Stretch = Stretch.Uniform, Child = portrait, Margin = new Thickness(0, 0, 0, 8) };
            var label = Stack(portraitBox, Txt((selected ? "✓  " : "") + DevicePreview.CharacterLabel(i, (int)Editing.Mode, chinese), 12, true),
                Txt(DevicePreview.CharacterLabel(i, (int)Editing.Mode, !chinese), 10, false, muted));
            if (!available) label.Children.Add(Txt(T("Local preview only", "仅本地预览 · 设备不支持"), 9, true, BrushOf("#9B5723")));
            var button = Btn("", () => { EditProfile(p => p with { Face = (Face)index }); RefreshAppearance(); });
            button.Content = label; button.Padding = new Thickness(10); button.Margin = new Thickness(0, 0, 8, 0); button.BorderBrush = selected ? accent : line;
            button.BorderThickness = new Thickness(selected ? 3 : 1); button.Background = selected ? BrushOf("#E6F5EF") : Brushes.White;
            grid.Children.Add(button);
        }
        group.Children.Add(grid); return group;
    }
}
