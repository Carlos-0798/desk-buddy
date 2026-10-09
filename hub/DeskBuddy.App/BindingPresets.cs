using DeskBuddy.Core;

namespace DeskBuddy.App;

// These are editor aliases for existing single-key wire bindings, not macros or
// application detection. Only Kind/Value/Modifiers are stored in the 224-byte bank.
internal sealed record BindingPreset(string Id, string English, string Chinese,
    HubBinding Binding, bool IsCustomKeyboard = false)
{
    public string Label(bool chinese) => chinese ? Chinese : English;
}

internal static class BindingPresets
{
    public const string YouTubeHelpUrl = "https://support.google.com/youtube/answer/7631406?hl=en";
    public static IReadOnlyList<BindingPreset> All { get; } = Array.AsReadOnly(new BindingPreset[]
    {
        new("disabled", "Disabled", "禁用", new(BindingKind.Disabled)),
        new("media-play", "Media: Play / Pause", "媒体：播放 / 暂停", new(BindingKind.Consumer, 0xCD)),
        new("media-previous", "Media: Previous track", "媒体：上一首", new(BindingKind.Consumer, 0xB6)),
        new("media-next", "Media: Next track", "媒体：下一首", new(BindingKind.Consumer, 0xB5)),
        new("media-mute", "Media: Mute toggle", "媒体：静音切换", new(BindingKind.Consumer, 0xE2)),
        new("media-volume-up", "Media: Volume up", "媒体：音量增加", new(BindingKind.Consumer, 0xE9)),
        new("media-volume-down", "Media: Volume down", "媒体：音量减小", new(BindingKind.Consumer, 0xEA)),
        new("custom-keyboard", "Custom keyboard shortcut", "自定义键盘快捷键", new(BindingKind.Keyboard, 0x04), true),
        new("youtube-back-10", "YouTube: Back 10 s (J)", "YouTube：后退 10 秒（J）", new(BindingKind.Keyboard, 0x0D)),
        new("youtube-play", "YouTube: Play / Pause (K)", "YouTube：播放 / 暂停（K）", new(BindingKind.Keyboard, 0x0E)),
        new("youtube-forward-10", "YouTube: Forward 10 s (L)", "YouTube：前进 10 秒（L）", new(BindingKind.Keyboard, 0x0F)),
        new("youtube-back-5", "YouTube: Back 5 s (Left)", "YouTube：后退 5 秒（左）", new(BindingKind.Keyboard, 0x50)),
        new("youtube-forward-5", "YouTube: Forward 5 s (Right)", "YouTube：前进 5 秒（右）", new(BindingKind.Keyboard, 0x4F)),
        new("youtube-fullscreen", "YouTube: Fullscreen (F)", "YouTube：全屏（F）", new(BindingKind.Keyboard, 0x09)),
        new("youtube-mute", "YouTube: Mute (M)", "YouTube：播放器静音（M）", new(BindingKind.Keyboard, 0x10)),
        new("key-space", "Key: Space", "按键：空格", new(BindingKind.Keyboard, 0x2C)),
        new("key-enter", "Key: Enter", "按键：回车", new(BindingKind.Keyboard, 0x28)),
        new("key-escape", "Key: Escape", "按键：Esc", new(BindingKind.Keyboard, 0x29)),
        new("key-tab", "Key: Tab", "按键：Tab", new(BindingKind.Keyboard, 0x2B)),
        new("key-left", "Key: Left", "按键：左方向", new(BindingKind.Keyboard, 0x50)),
        new("key-right", "Key: Right", "按键：右方向", new(BindingKind.Keyboard, 0x4F)),
        new("key-up", "Key: Up", "按键：上方向", new(BindingKind.Keyboard, 0x52)),
        new("key-down", "Key: Down", "按键：下方向", new(BindingKind.Keyboard, 0x51)),
        new("key-home", "Key: Home", "按键：Home", new(BindingKind.Keyboard, 0x4A)),
        new("key-end", "Key: End", "按键：End", new(BindingKind.Keyboard, 0x4D)),
        new("key-page-up", "Key: Page Up", "按键：Page Up", new(BindingKind.Keyboard, 0x4B)),
        new("key-page-down", "Key: Page Down", "按键：Page Down", new(BindingKind.Keyboard, 0x4E)),
        new("key-backspace", "Key: Backspace", "按键：退格", new(BindingKind.Keyboard, 0x2A)),
        new("key-delete", "Key: Delete", "按键：Delete", new(BindingKind.Keyboard, 0x4C)),
        new("edit-copy", "Shortcut: Ctrl+C", "快捷键：Ctrl+C", new(BindingKind.Keyboard, 0x06, 0x01)),
        new("edit-paste", "Shortcut: Ctrl+V", "快捷键：Ctrl+V", new(BindingKind.Keyboard, 0x19, 0x01)),
        new("edit-cut", "Shortcut: Ctrl+X", "快捷键：Ctrl+X", new(BindingKind.Keyboard, 0x1B, 0x01)),
        new("edit-undo", "Shortcut: Ctrl+Z", "快捷键：Ctrl+Z", new(BindingKind.Keyboard, 0x1D, 0x01)),
        new("edit-redo", "Shortcut: Ctrl+Y", "快捷键：Ctrl+Y", new(BindingKind.Keyboard, 0x1C, 0x01)),
        new("edit-select-all", "Shortcut: Ctrl+A", "快捷键：Ctrl+A", new(BindingKind.Keyboard, 0x04, 0x01)),
        new("local-menu", "Open device menu", "打开设备菜单", new(BindingKind.Local, 1)),
        new("local-marker", "Local marker", "本地标记", new(BindingKind.Local, 2)),
        new("local-output-off", "Output OFF", "关闭输出", new(BindingKind.Local, 3)),
        new("local-next-profile", "Next profile", "下一套配置", new(BindingKind.Local, 4)),
        new("local-display-sleep", "Rest screen (supported devices)", "立即休屏（需设备支持）", new(BindingKind.Local, 5))
    });

    public static BindingPreset Resolve(HubBinding binding)
    {
        // Shared arrow aliases have identical wire bytes; on readback use the
        // neutral key label rather than inventing an application association.
        return All.FirstOrDefault(p => !p.IsCustomKeyboard && !p.Id.StartsWith("youtube-", StringComparison.Ordinal) && p.Binding == binding)
            ?? All.FirstOrDefault(p => !p.IsCustomKeyboard && p.Binding == binding)
            ?? (binding.Kind == BindingKind.Keyboard ? All.Single(p => p.IsCustomKeyboard)
                : throw new ArgumentException("Binding has no supported editor choice.", nameof(binding)));
    }

    public static HubBinding Select(BindingPreset preset, HubBinding keyboardEditor) =>
        preset.IsCustomKeyboard ? keyboardEditor : preset.Binding;

    public static string Help(bool chinese) => chinese
        ? "键盘动作发送到电脑当前焦点窗口。先点击 YouTube 播放器，退出搜索框或输入框；左右 5 秒需要进度条焦点，空格在按钮获得焦点时可能点击按钮。J / K / L 分别后退 10 秒、播放/暂停、前进 10 秒；F 全屏，M 仅切换播放器静音。其它播放器、键盘布局或快捷键设置可能不同，不保证通用跳转秒数。媒体上一首/下一首是切歌，不是视频快进。预设只填入单键与修饰键，不检测应用或读取播放进度；相同按键回读时可能显示通用键名。"
        : "Keyboard actions go to the focused window. Click the YouTube player first and leave search/text fields; Left/Right 5-second seeking needs seek-bar focus. Space may activate a focused button. J / K / L seek back 10 s, play/pause, and seek forward 10 s; F toggles fullscreen and M mutes the player. Other players, keyboard layouts and shortcut settings may differ: seek times are not universal. Media Previous/Next changes tracks, not video position. Presets fill one key and its modifiers; they do not detect apps or read playback. Identical key aliases may read back as a generic key name.";
}
