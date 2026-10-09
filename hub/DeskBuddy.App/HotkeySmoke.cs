using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    async Task RunHotkeySmoke(string directory, Action<bool, string> check, List<string> images)
    {
        await ConnectDemo(); await ReadDevice();
        page = 1; BuildShell(); await Settle();
        string original = BankCodec.ToHex(draft);
        int commandStart = demoTransport!.Commands.Count;
        check(BankCodec.ToHex(draft) == original && !dirty, "opening mapping editor leaves existing mappings untouched");
        var row = Descendants<Grid>(content).Single(g => g.Name == "BindingRow0");
        var action = Descendants<ComboBox>(row).Single(c => c.Name == "BindingAction0");
        var key = Descendants<ComboBox>(row).Single(c => c.Name == "BindingKey0");
        var mods = Descendants<CheckBox>(row).ToArray();
        foreach (ActionChoice choice in action.Items)
        {
            action.SelectedItem = choice;
            var actual = Editing.Bindings[0];
            check(choice.Preset.IsCustomKeyboard ? actual.Kind == BindingKind.Keyboard : actual == choice.Preset.Binding,
                "actual ComboBox selects exact preset tuple: " + choice.Preset.Id);
            if (actual.Kind == BindingKind.Keyboard)
            {
                byte mask = 0; for (int i = 0; i < 8; i++) if (mods[i].IsChecked == true) mask |= (byte)(1 << i);
                check(key.SelectedIndex == actual.Value - 4 && mask == actual.Modifiers, "key/modifier controls agree with preset: " + choice.Preset.Id);
            }
            check(Editing.Bindings.Skip(1).SequenceEqual(BankCodec.FromHex(original).Profiles[editProfile].Bindings.Skip(1)), "editing one mapping preserves the other eleven");
        }
        action.SelectedItem = action.Items.Cast<ActionChoice>().Single(c => c.Preset.Id == "edit-copy");
        check(Editing.Bindings[0] == new HubBinding(BindingKind.Keyboard, 6, 1), "Ctrl+C selection sets Ctrl and C");
        action.SelectedItem = action.Items.Cast<ActionChoice>().Single(c => c.Preset.Id == "youtube-forward-10");
        check(Editing.Bindings[0] == new HubBinding(BindingKind.Keyboard, 0x0F) && mods.All(m => m.IsChecked == false), "YouTube L clears previous Ctrl instead of sending Ctrl+L");
        mods[7].IsChecked = true; mods[7].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(Editing.Bindings[0] == new HubBinding(BindingKind.Keyboard, 0x0F, 0x80) && ((ActionChoice)action.SelectedItem).Preset.IsCustomKeyboard,
            "manual right-Win modifier becomes custom without changing key");
        key.SelectedIndex = 0x73 - 4;
        check(Editing.Bindings[0] == new HubBinding(BindingKind.Keyboard, 0x73, 0x80), "custom F24 preserves right modifier after preset editing");
        check(demoTransport.Commands.Count == commandStart, "catalog selection remains local with no preview/save/output command");
        await ApplyBank(false); await ReadDevice(); BuildPage(); await Settle();
        var reread = Descendants<ComboBox>(content).Single(c => c.Name == "BindingAction0");
        check(((ActionChoice)reread.SelectedItem).Preset.IsCustomKeyboard && Editing.Bindings[0] == new HubBinding(BindingKind.Keyboard, 0x73, 0x80), "explicit demo preview/GET preserves custom key and right modifier");
        await Revert();
        foreach (bool zh in new[] { true, false })
        {
            chinese = zh; page = 1; BuildShell(); await Settle();
            var picker = Descendants<ComboBox>(content).Single(c => c.Name == "BindingAction0");
            picker.SelectedItem = picker.Items.Cast<ActionChoice>().Single(c => c.Preset.Id == "youtube-back-10");
            picker.BringIntoView(); await Settle();
            picker.IsDropDownOpen = true; await Settle();
            check(picker.Style != null && picker.MaxDropDownHeight <= 340, "hotkey dropdown retains shared bounded rounded style");
            string popup = (zh ? "zh" : "en") + "-hotkey-presets.png";
            CaptureShellForSmoke(Path.Combine(directory, popup)); images.Add(popup);
            var popupControl = (System.Windows.Controls.Primitives.Popup)picker.Template.FindName("PART_Popup", picker);
            var popupFrame = (Border)popupControl.Child;
            check(popupFrame.CornerRadius.TopLeft == 9 && Math.Abs(popupFrame.ActualWidth - picker.ActualWidth) < 1 && ((ScrollViewer)popupFrame.Child).ScrollableHeight > 0,
                "40-item catalog uses actual rounded, field-width, scrollable popup");
            string popupOnly = (zh ? "zh" : "en") + "-hotkey-popup.png";
            Capture(popupFrame, Path.Combine(directory, popupOnly)); images.Add(popupOnly);
            picker.IsDropDownOpen = false;
            var help = Descendants<Expander>(content).Single(); help.IsExpanded = true; help.BringIntoView(); await Settle();
            string helpImage = (zh ? "zh" : "en") + "-hotkey-help.png";
            CaptureShellForSmoke(Path.Combine(directory, helpImage)); images.Add(helpImage);
            check(Descendants<TextBlock>(help).Any(t => t.Text == BindingPresets.Help(zh)), "bilingual focus/player limitation is reachable in the real editor");
        }
        await ReadDevice();
    }
}
