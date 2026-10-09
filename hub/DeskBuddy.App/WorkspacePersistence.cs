using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    string? workspaceFile;
    HubWorkspaceState workspace = new(null, null);
    string? workspaceWarning;
    bool workspaceFixturePeer;
    bool workspaceSavePending;
    readonly DispatcherTimer workspaceSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    void InitializeWorkspace(string? isolatedPath, bool disabled)
    {
        if (isolatedPath == null && disabled) return; // Demo/render/device-smoke never touches real user AppData.
        workspaceFile = isolatedPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBuddy", "Hub", "workspace-v1.json");
        var loaded = HubWorkspaceStore.Load(workspaceFile);
        workspace = loaded.State; workspaceWarning = loaded.Warning;
        if (workspace.Draft is { } local) { draft = BankCodec.Clone(local); dirty = true; }
        else if (workspace.LastDevice is { } prior) draft = BankCodec.Clone(prior.Bank);
        editProfile = draft.ActiveProfile;
        // Never restore a connection, permission, revision or output status from disk.
        draftRevision = null;
        workspaceSaveTimer.Tick += (_, _) => FlushWorkspace();
    }
    bool MayCacheDevice => workspaceFile != null && ((!demo && transport is not DemoTransport) || workspaceFixturePeer);
    void RememberWorkspaceDraft()
    {
        if (workspaceFile == null) return;
        workspace = workspace with { Draft = BankCodec.Clone(draft) };
        QueueWorkspaceSave();
    }
    void ObserveWorkspaceDevice(BankSnapshot snapshot, bool clearDraft)
    {
        if (!MayCacheDevice) return;
        workspace = new(new(BankCodec.Clone(snapshot.Bank), DateTimeOffset.UtcNow), clearDraft ? null : workspace.Draft);
        QueueWorkspaceSave();
    }
    void QueueWorkspaceSave()
    {
        if (workspaceFile == null) return;
        workspaceSavePending = true;
        workspaceSaveTimer.Stop(); workspaceSaveTimer.Start();
    }
    void FlushWorkspace()
    {
        workspaceSaveTimer.Stop();
        if (workspaceFile == null || !workspaceSavePending) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
            HubWorkspaceStore.SaveAtomic(workspaceFile, workspace); workspaceSavePending = false; workspaceWarning = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            workspaceWarning = T("Local recovery copy could not be saved. Export your draft before exiting.", "本地恢复副本未能保存，请在退出前导出草稿。");
            Log("Local workspace save failed: " + error.GetType().Name);
        }
    }
    void LoadRetainedDraft()
    {
        if (workspace.Draft is not { } retained || busy) return;
        CancelAnimationForContext(); selectedAnimation = null;
        draft = BankCodec.Clone(retained); dirty = true; editProfile = draft.ActiveProfile; Array.Clear(invalidNames);
        BuildPage(); SetNotice(T("Local draft loaded into the editor only. Preview or Save is still your explicit choice.",
            "仅将本地草稿载入编辑器；仍需你明确选择预览或保存，尚未发送到设备。"));
    }
    void BuildWorkspaceNotice()
    {
        if (workspaceFile == null) return;
        var title = Txt("", 13, true); title.Name = "WorkspaceSource";
        var detail = Note(""); detail.Name = "WorkspaceDetail";
        var restore = Btn(T("Load retained local draft", "载入保留的本地草稿"), LoadRetainedDraft);
        restore.Name = "WorkspaceRestoreDraft";
        var warning = Note(""); warning.Name = "WorkspaceWarning";
        var card = Card(Stack(title, detail, restore, warning)); card.Name = "WorkspaceNotice";
        content.Children.Add(card);
        liveUpdates.Add(() =>
        {
            title.Text = dirty ? T("Local draft · not sent to device", "本地草稿 · 尚未发送到设备") :
                Fresh && client?.Configuration != null ? T("Current device readback", "设备当前回读配置") :
                workspace.LastDevice != null ? T("Last device snapshot · not connected/live", "上次设备快照 · 当前未连接或非实时") :
                T("Example configuration · device has not been read", "示例配置 · 尚未读取设备");
            detail.Text = Fresh && client?.Configuration != null
                ? T("The device readback is authoritative. Loading a retained draft only changes this editor; nothing is sent automatically.",
                    "设备回读为准。载入保留草稿只改变编辑器，不会自动写入设备。")
                : workspace.LastDevice is { } previous
                    ? T($"Last read {previous.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm}. Connect to verify the current device state; this is not proof of flash persistence.",
                        $"上次回读 {previous.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm}。连接后核对设备当前状态；此快照不证明已保存到设备闪存。")
                    : T("You can edit locally. Connect and read the device before applying anything.", "可先在本地编辑；连接并回读设备后，才能明确选择应用。");
            bool retainedDifferent = workspace.Draft != null && (!dirty || BankCodec.ToHex(workspace.Draft) != BankCodec.ToHex(draft));
            restore.Visibility = retainedDifferent ? Visibility.Visible : Visibility.Collapsed; restore.IsEnabled = !busy;
            warning.Text = workspaceWarning == null ? "" : T("Local recovery notice: ", "本地恢复提示：") + workspaceWarning;
            warning.Visibility = warning.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
    }
}
