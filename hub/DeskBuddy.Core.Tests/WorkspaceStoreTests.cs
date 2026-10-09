using System.Text;
using System.Text.Json.Nodes;
using DeskBuddy.Core;

internal static partial class Program
{
    private static void WorkspaceStoreTests()
    {
        string folder = Path.Combine(Path.GetTempPath(), "desk-workspace-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "workspace.json");
            var empty = HubWorkspaceStore.Load(path);
            Check(empty.Source == "missing" && empty.State.LastDevice is null && empty.State.Draft is null && empty.Warning is null, "missing workspace is explicitly empty");
            Check(!Directory.EnumerateFileSystemEntries(folder).Any(), "load missing does not create files");
            var observed = new DateTimeOffset(2026, 10, 5, 19, 20, 21, TimeSpan.FromHours(-4));
            var device = HubDefaults.CreateBank();
            var draft = HubDefaults.CreateBank() with { ActiveProfile = 2 };
            var state = new HubWorkspaceState(new(device, observed), draft);
            HubWorkspaceStore.SaveAtomic(path, state);
            string original = File.ReadAllText(path);
            var loaded = HubWorkspaceStore.Load(path);
            Check(loaded.Source == "primary" && loaded.Warning is null && loaded.State.LastDevice!.ObservedAt == observed, "workspace snapshot observation time roundtrip");
            Check(BankCodec.ToHex(loaded.State.LastDevice!.Bank) == BankCodec.ToHex(device) && loaded.State.Draft!.ActiveProfile == 2, "separate device snapshot and draft roundtrip");
            Check(!original.Contains("armed", StringComparison.OrdinalIgnoreCase) && !original.Contains("connected", StringComparison.OrdinalIgnoreCase) && !original.Contains("identity", StringComparison.OrdinalIgnoreCase), "no output connection or identity persisted");
            loaded.State.Draft!.Profiles[0] = loaded.State.Draft.Profiles[0] with { Name = "Changed" };
            Check(loaded.State.LastDevice.Bank.Profiles[0].Name == "MEDIA", "snapshot and draft are independent mutable arrays");

            HubWorkspaceStore.SaveAtomic(path, new(state.LastDevice, null));
            Check(File.ReadAllText(path + ".bak") == original && HubWorkspaceStore.Load(path).State.Draft is null, "atomic replace keeps previous validated primary as backup");
            File.WriteAllText(path, "partial {");
            byte[] healthyBackup = File.ReadAllBytes(path + ".bak");
            loaded = HubWorkspaceStore.Load(path);
            Check(loaded.Source == "backup" && loaded.Warning is not null && loaded.State.Draft!.ActiveProfile == 2, "partial primary falls back to healthy backup with warning");
            Check(File.ReadAllText(path) == "partial {", "load recovery does not overwrite damaged primary");
            HubWorkspaceStore.SaveAtomic(path, new(state.LastDevice, HubDefaults.CreateBank()));
            Check(HubWorkspaceStore.Load(path).Source == "primary" && File.ReadAllBytes(path + ".bak").SequenceEqual(healthyBackup), "save after recovery never backs up corrupt primary over healthy backup");

            File.WriteAllText(path, "bad primary"); File.WriteAllText(path + ".bak", "bad backup");
            loaded = HubWorkspaceStore.Load(path);
            Check(loaded.Source == "invalid" && loaded.Warning is not null && loaded.State.LastDevice is null && loaded.State.Draft is null, "two bad files are unavailable, never device defaults");
            Check(File.ReadAllText(path) == "bad primary" && File.ReadAllText(path + ".bak") == "bad backup", "two invalid files preserved by load");
            File.Delete(path + ".bak");

            var badJson = new List<string> { "[]", "{", original.Replace("desk-buddy-workspace", "other"), original.Replace("\"version\": 1", "\"version\": 2"),
                original.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"), original.Replace("\"draft\":", "\"armed\": false, \"draft\":"),
                original.Replace("\"bankHex\":", "\"bankHex\": \"00\", \"bankHex\":"), original.Replace("\"observedAt\":", "\"host\": \"secret\", \"observedAt\":"),
                original.Replace("\"observedAt\":", "\"observedAt\": false, \"otherAt\":"), new string(' ', HubWorkspaceStore.MaxFileBytes + 1) };
            foreach (string target in new[] { "lastDevice", "draft" })
            {
                var json = JsonNode.Parse(original)!.AsObject(); json[target]!["crc32"] = "00000000"; badJson.Add(json.ToJsonString());
                json = JsonNode.Parse(original)!.AsObject(); json[target]!["bankHex"] = "00"; badJson.Add(json.ToJsonString());
            }
            var badDate = JsonNode.Parse(original)!.AsObject(); badDate["lastDevice"]!["observedAt"] = "yesterday"; badJson.Add(badDate.ToJsonString());
            foreach (string bad in badJson)
            {
                File.WriteAllText(path, bad);
                Check(HubWorkspaceStore.Load(path).Source == "invalid", "strict schema, duplicate, unknown, oversized or checksum rejection");
                Check(File.ReadAllText(path) == bad, "invalid read leaves source unchanged");
            }
            File.WriteAllBytes(path, [0xC3, 0x28]);
            Check(HubWorkspaceStore.Load(path).Source == "invalid", "invalid UTF8 rejected");

            File.WriteAllText(path, LocalConfigurationStore.ExportJson(draft));
            loaded = HubWorkspaceStore.Load(path);
            Check(loaded.Source == "primary" && loaded.Warning is not null && loaded.State.LastDevice is null && loaded.State.Draft!.ActiveProfile == 2, "old export imports only as draft");
            HubWorkspaceStore.SaveAtomic(path, loaded.State);
            Check(File.ReadAllText(path).Contains("desk-buddy-workspace", StringComparison.Ordinal) && HubWorkspaceStore.Load(path).State.LastDevice is null, "legacy draft upgrade does not invent device snapshot");

            HubWorkspaceStore.SaveAtomic(path, state);
            byte[] saved = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
            var invalidBank = HubDefaults.CreateBank() with { ActiveProfile = 9 };
            Throws<FormatException>(() => HubWorkspaceStore.SaveAtomic(path, new(null, invalidBank)), "invalid bank rejected before write");
            Check(saved.SequenceEqual(File.ReadAllBytes(path)) && backup.SequenceEqual(File.ReadAllBytes(path + ".bak")), "invalid state preserves primary and backup");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Throws<IOException>(() => HubWorkspaceStore.SaveAtomic(path, new(null, null)), "locked primary cannot be mistaken for corruption");
            Check(saved.SequenceEqual(File.ReadAllBytes(path)) && backup.SequenceEqual(File.ReadAllBytes(path + ".bak")), "locked primary failed save preserves files");
            using (var locked = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Throws<IOException>(() => HubWorkspaceStore.SaveAtomic(path, new(null, null)), "concurrent writer lock fails before staging");
            Check(saved.SequenceEqual(File.ReadAllBytes(path)) && backup.SequenceEqual(File.ReadAllBytes(path + ".bak")), "writer lock preserves prior workspace");
            Check(!Directory.EnumerateFiles(folder, ".desk-workspace-*.tmp").Any(), "failed saves leave no staging files");
            Throws<DirectoryNotFoundException>(() => HubWorkspaceStore.SaveAtomic(Path.Combine(folder, "missing", "workspace.json"), state), "missing destination directory not created implicitly");
            File.Delete(path); loaded = HubWorkspaceStore.Load(path);
            Check(loaded.Source == "backup" && loaded.State.Draft is not null, "missing primary with healthy backup recovers");
        }
        finally { Directory.Delete(folder, true); }
    }
}
