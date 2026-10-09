using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    // Separate process invocations share only the explicitly supplied test file.
    // No normal startup, real AppData, port discovery, COM or GATT is involved.
    internal static MainWindow CreateWorkspaceSmoke(string stage, string file, string output)
    {
        string[] stages = ["seed-draft", "read-draft", "seed-snapshot", "read-snapshot", "legacy", "backup", "invalid", "write-failure"];
        if (!stages.Contains(stage)) throw new ArgumentException("Unknown workspace fixture stage.");
        file = Path.GetFullPath(file); output = Path.GetFullPath(output);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBuddy", "Hub");
        if (file.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Workspace fixtures cannot use the real Hub AppData folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); Directory.CreateDirectory(output);
        var saved = WorkspaceFixtureBank(false); var edited = WorkspaceFixtureBank(true);
        var snapshot = new DeviceWorkspaceSnapshot(saved, DateTimeOffset.UtcNow.AddHours(-1));
        if (stage == "legacy") File.WriteAllText(file, LocalConfigurationStore.ExportJson(edited));
        if (stage == "backup")
        {
            HubWorkspaceStore.SaveAtomic(file, new(snapshot, null));
            HubWorkspaceStore.SaveAtomic(file, new(snapshot, edited));
            File.WriteAllText(file, "interrupted {");
        }
        if (stage == "invalid") { File.WriteAllText(file, "bad primary"); File.WriteAllText(file + ".bak", "bad backup"); }
        if (stage == "write-failure") HubWorkspaceStore.SaveAtomic(file, new(snapshot, null));
        var window = new MainWindow(false, null, deferStartupForTest: true, isolatedWorkspacePath: file)
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Opacity = 0, workspaceFixturePeer = true };
        var checks = new List<string>(); var images = new List<string>();
        Exception? failure = null; FileStream? heldLock = null;
        void Check(bool ok, string note) { if (!ok) throw new InvalidOperationException(note); checks.Add(note); }
        string Hex(HubBank? bank) => bank == null ? "null" : BankCodec.ToHex(bank);
        window.Closed += (_, _) =>
        {
            try
            {
                Check(!window.Connected, "actual Closing event completes transport cleanup");
                if (stage.StartsWith("seed-"))
                {
                    var state = HubWorkspaceStore.Load(file).State;
                    Check(Hex(state.LastDevice?.Bank) == Hex(saved), "Closing writes the confirmed snapshot before process exits");
                    Check(Hex(state.Draft) == (stage == "seed-draft" ? Hex(edited) : "null"), "Closing preserves exactly the separate unsent draft");
                }
                if (stage == "invalid") Check(File.ReadAllText(file) == "bad primary" && File.ReadAllText(file + ".bak") == "bad backup", "opening and closing invalid cache never overwrites evidence with defaults");
                if (stage == "write-failure") Check(Hex(HubWorkspaceStore.Load(file).State.LastDevice?.Bank) == Hex(saved), "failed close save leaves prior snapshot intact");
            }
            catch (Exception e) { failure ??= e; }
            finally { heldLock?.Dispose(); }
            File.WriteAllText(Path.Combine(output, "workspace-" + stage + ".json"), JsonSerializer.Serialize(new
            { passed = failure == null, stage, process_id = Environment.ProcessId, hardware_access = false,
                real_appdata_access = false, checks, images, error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = failure == null ? 0 : 1;
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Settle();
                Check(window.client == null && window.draftRevision == null && !window.Fresh, "startup does not restore a device connection, revision or fresh status");
                if (stage.StartsWith("seed-"))
                {
                    Check(HubWorkspaceStore.Load(file).Source == "missing", "writer process begins without a workspace file");
                    var peer = new DemoTransport(); await window.Attach(peer);
                    await window.client!.PreviewAsync(saved, window.client.Configuration!.Revision);
                    window.ReadSnapshot(true); window.BuildPage();
                    Check(Hex(window.draft) == Hex(saved), "last device snapshot comes from actual Core GET readback");
                    if (stage == "seed-draft")
                    {
                        window.editProfile = 0;
                        window.EditProfile(_ => edited.Profiles[0]);
                        Check(window.dirty && Hex(window.workspace.Draft) == Hex(edited), "real editor stores nondefault theme character and keyboard binding separately");
                    }
                    // Close immediately: test Closing flush rather than relying on debounce.
                }
                else if (stage is "read-draft" or "read-snapshot")
                {
                    bool hasDraft = stage == "read-draft";
                    Check(Hex(window.workspace.LastDevice?.Bank) == Hex(saved), "second process restores prior device snapshot bytes");
                    Check(Hex(window.draft) == Hex(hasDraft ? edited : saved) && window.dirty == hasDraft, "second process displays retained draft or snapshot, not defaults");
                    foreach (bool zh in new[] { true, false })
                    {
                        window.chinese = zh; window.page = 2; window.BuildShell(); await Settle();
                        string title = Descendants<TextBlock>(window.content).Single(x => x.Name == "WorkspaceSource").Text;
                        Check(title.Contains(hasDraft ? window.T("Local draft", "本地草稿") : window.T("Last device snapshot", "上次设备快照")), "bilingual offline source label distinguishes draft and non-live snapshot");
                        Check(!window.CanConfigure && window.previews.All(p => p.Face == (int)window.Editing.Face), "offline preview uses recovered character and cannot write device");
                        string image = (zh ? "zh-" : "en-") + stage + ".png";
                        Capture(window.shell, Path.Combine(output, image)); images.Add(image);
                    }
                    var peer = new DemoTransport(); await window.Attach(peer); await Settle();
                    Check(!window.dirty && Hex(window.draft) == Hex(HubDefaults.CreateBank()), "connecting makes current device GET authoritative");
                    Check(peer.Commands.All(c => c.Split(' ')[0] is "HELLO" or "GET" or "STATUS"), "connect never replays draft, SAVE or OUTPUT 1");
                    if (hasDraft)
                    {
                        Check(Hex(window.workspace.Draft) == Hex(edited), "connecting retains local draft separately for explicit choice");
                        int commands = peer.Commands.Count;
                        var restore = Descendants<Button>(window.content).Single(x => x.Name == "WorkspaceRestoreDraft");
                        Check(restore.IsVisible && restore.IsEnabled, "explicit retained-draft action is available after readback");
                        restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle();
                        Check(window.dirty && Hex(window.draft) == Hex(edited) && peer.Commands.Count == commands, "restore click changes editor only without transport writes");
                        Check(window.client!.Status?.Armed == false, "draft restore does not arm output");
                        await window.ReadDevice();
                        Check(!window.dirty && window.workspace.Draft == null, "explicit Read device discards the retained draft as described");
                    }
                    Check(peer.ConfigurationWriteRequests == 0 && peer.SaveRequests == 0, "restart/reconnect/restore workflow never mutates fake device bank");
                }
                else if (stage == "legacy")
                {
                    Check(window.dirty && Hex(window.draft) == Hex(edited) && window.workspace.LastDevice == null, "older exported bank migrates only as unsent local draft");
                    Check(window.workspaceWarning != null, "legacy import explains source instead of inventing device confirmation");
                }
                else if (stage == "backup")
                {
                    Check(!window.dirty && Hex(window.draft) == Hex(saved) && window.workspaceWarning != null, "actual startup recovers healthy backup with visible warning");
                }
                else if (stage == "invalid")
                {
                    Check(window.workspace.LastDevice == null && window.workspace.Draft == null && window.workspaceWarning != null, "corrupt local files produce unavailable source warning");
                    Check(Descendants<TextBlock>(window.content).Single(x => x.Name == "WorkspaceSource").Text.Contains("示例配置"), "no valid history labels default bank as example only");
                }
                else if (stage == "write-failure")
                {
                    heldLock = new FileStream(file + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    window.EditProfile(_ => edited.Profiles[0]); await Task.Delay(500); window.UpdateLive();
                    Check(window.workspaceWarning != null && window.workspaceSavePending && window.dirty, "failed atomic save preserves dirty editor and pending retry with warning");
                    Check(Descendants<TextBlock>(window.content).Single(x => x.Name == "WorkspaceWarning").Text.Contains("导出草稿"), "failed save offers explicit draft export");
                }
            }
            catch (Exception e) { failure = e; }
            finally { window.Close(); }
        };
        return window;
    }

    static HubBank WorkspaceFixtureBank(bool edited)
    {
        var bank = HubDefaults.CreateBank();
        var bindings = (HubBinding[])bank.Profiles[0].Bindings.Clone();
        bindings[0] = new(BindingKind.Keyboard, edited ? (ushort)0x0F : (ushort)0x0D, edited ? (byte)2 : (byte)0);
        bank.Profiles[0] = bank.Profiles[0] with { Name = edited ? "LOCAL DRAFT" : "DEVICE SNAP", Theme = edited ? Theme.Ocean : Theme.Garden,
            Face = edited ? Face.Bunny : Face.Fox, Mode = HomeMode.DeskPet, Bindings = bindings };
        return bank;
    }
}
