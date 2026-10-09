using DeskBuddy.App;
using DeskBuddy.Core;

internal static partial class Program
{
    private static void BindingPresetRegression()
    {
        var catalog = BindingPresets.All;
        Check(catalog.Select(p => p.Id).Distinct().Count() == catalog.Count, "preset IDs unique");
        Check(catalog.Count(p => p.IsCustomKeyboard) == 1, "one custom editor fallback");
        Check(catalog.All(p => !string.IsNullOrWhiteSpace(p.Label(false)) && !string.IsNullOrWhiteSpace(p.Label(true))), "all presets bilingual");
        var expected = new Dictionary<string, HubBinding>
        {
            ["youtube-back-10"] = new(BindingKind.Keyboard, 0x0D),
            ["youtube-play"] = new(BindingKind.Keyboard, 0x0E),
            ["youtube-forward-10"] = new(BindingKind.Keyboard, 0x0F),
            ["youtube-back-5"] = new(BindingKind.Keyboard, 0x50),
            ["youtube-forward-5"] = new(BindingKind.Keyboard, 0x4F),
            ["youtube-fullscreen"] = new(BindingKind.Keyboard, 0x09),
            ["youtube-mute"] = new(BindingKind.Keyboard, 0x10),
            ["key-space"] = new(BindingKind.Keyboard, 0x2C),
            ["key-escape"] = new(BindingKind.Keyboard, 0x29),
            ["edit-copy"] = new(BindingKind.Keyboard, 0x06, 1),
            ["edit-paste"] = new(BindingKind.Keyboard, 0x19, 1),
            ["media-play"] = new(BindingKind.Consumer, 0xCD),
            ["media-previous"] = new(BindingKind.Consumer, 0xB6),
            ["media-next"] = new(BindingKind.Consumer, 0xB5)
        };
        foreach (var (id, binding) in expected)
            Check(catalog.Single(p => p.Id == id).Binding == binding, "documented physical key/consumer usage: " + id);
        var defaults = HubDefaults.CreateBank(); string original = BankCodec.ToHex(defaults);
        var custom = new HubBinding(BindingKind.Keyboard, 0x68, 0xF0);
        foreach (var preset in catalog)
        {
            HubBinding selected = BindingPresets.Select(preset, custom);
            Check(selected == (preset.IsCustomKeyboard ? custom : preset.Binding), "preset replaces stale key and modifier atomically: " + preset.Id);
            for (int profile = 0; profile < 3; profile++)
            {
                var edited = BankCodec.FromHex(original);
                edited.Profiles[profile].Bindings[11] = selected;
                byte[] encoded = BankCodec.Encode(edited);
                Check(encoded.Length == 224 && encoded.Take(8).SequenceEqual(BankCodec.Encode(defaults).Take(8)), "preset leaves header/reserved bytes unchanged");
                var decoded = BankCodec.Decode(encoded);
                Check(decoded.Profiles[profile].Bindings[11] == selected, "preset survives existing codec for every profile");
                for (int other = 0; other < 3; other++)
                    Check(decoded.Profiles[other].Bindings.Take(other == profile ? 11 : 12).SequenceEqual(defaults.Profiles[other].Bindings.Take(other == profile ? 11 : 12)), "preset never rewrites other bindings/profiles");
            }
            var resolved = BindingPresets.Resolve(selected);
            Check(resolved.IsCustomKeyboard || resolved.Binding == selected, "readback never changes stored wire tuple");
        }
        for (int mask = 0; mask <= 255; mask++)
        {
            var binding = new HubBinding(BindingKind.Keyboard, 0x0D, (byte)mask);
            var resolved = BindingPresets.Resolve(binding);
            Check(mask == 0 ? resolved.Id == "youtube-back-10" : resolved.IsCustomKeyboard, "YouTube J alias requires exact modifier byte");
            Check(BindingPresets.Select(catalog.Single(p => p.IsCustomKeyboard), binding) == binding, "custom editor preserves all modifier masks");
        }
        Check(BindingPresets.Resolve(new(BindingKind.Keyboard, 0x50)).Id == "key-left", "ambiguous arrow alias reads back as neutral key");
        Check(BindingPresets.Resolve(new(BindingKind.Keyboard, 0x4F)).Id == "key-right", "forward arrow does not invent player association");
        Check(BindingPresets.Resolve(new(BindingKind.Local, 5)).Id == "local-display-sleep", "sleep binding is an explicit local action");
        Throws<ArgumentException>(() => BindingPresets.Resolve(new(BindingKind.Local, 6)), "undefined local binding still rejected");
        Check(BankCodec.ToHex(defaults) == original && original == File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "default-bank.hex")).Trim(), "catalog leaves canonical default bank byte-identical");
        Check(BindingPresets.Help(false).Contains("focused window") && BindingPresets.Help(true).Contains("焦点") && BindingPresets.Help(false).Contains("not universal"), "help states focus and player-specific limits");
    }
}
