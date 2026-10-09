using DeskBuddy.Core;

namespace DeskBuddy.App;

// Shared by polling, explicit operations and late disconnect notifications.
// Remember LOCKED until a new connection so a generic close event cannot erase its recovery instructions.
internal sealed class ConnectionNotices
{
    private bool permissionLost;
    public void Reset() => permissionLost = false;
    public string Error(Exception error, bool chinese)
    {
        if (error is HubProtocolException { Code: "LOCKED" }) permissionLost = true;
        return Describe(error, chinese);
    }
    public string Disconnected(Exception? error, bool chinese)
    {
        if (error is HubProtocolException { Code: "LOCKED" }) permissionLost = true;
        return permissionLost ? Describe(new HubProtocolException("LOCKED"), chinese) :
            chinese ? "连接已断开，需重新读取配置；不会重放命令。" : "Disconnected. Configuration must be read again; no automatic replay.";
    }
    private static string Describe(Exception error, bool chinese)
    {
        string T(string en, string zh) => chinese ? zh : en;
        if (error is WorkerClosePendingException pending)
            return T($"{pending.ResourceName} is still closing; the driver has not confirmed release. New connections are blocked until it finishes. If it stays stuck, exit Hub and reopen before retrying. No command was replayed.",
                $"{pending.ResourceName} 仍在关闭，驱动尚未确认释放。结束前禁止建立新连接；若持续卡住，请退出 Hub 后重新打开再试。没有重放命令。");
        return error is HubProtocolException hp ? hp.Code switch
        {
            "STALE" => T("Device changed. Re-read it before applying; your local draft was kept.", "设备配置已改变，请先重新读取；本地草稿仍保留。"),
            "NOT_READY" => T("Return to device Home and check HID connection and touch calibration before enabling output.", "请先返回设备主页，确认 HID 链路和触摸校准，再开启输出。"),
            "SAVE_FAILED" => T("Device could not verify saving. Your local draft was kept; do not treat this as saved.", "设备保存未通过核验，本地草稿仍保留；当前不能视为已保存。"),
            "LOCKED" => T("BLE Hub permission has closed. On Desk Buddy, select Settings → Device info → BLE management → Allow BLE connection, or grant access through USB; then reconnect manually. Nothing was replayed.",
                "BLE Hub 尚未授权或授权已撤销。请在设备本机选择：菜单 → 设备信息 → 蓝牙管理 → 允许蓝牙连接；也可通过 USB 授权，然后手动重连。没有重放命令。"),
            "FORBIDDEN" => T("This permission change is allowed only on the device or through USB management.", "此授权只能在设备本机或 USB 管理连接中更改。"),
            "BUSY" => T("The device is calibrating or another management channel owns the 10-second write lease. Read back and retry explicitly after it is free.", "设备正在校准，或另一管理通道持有 10 秒写入租约。请等其释放后先回读，再手动操作。"),
            "UPGRADE_REQUIRED" => T("The selected device lacks the required BLE Hub service or capability. Select Desk Buddy and upgrade firmware through USB if needed.", "所选设备缺少 BLE Hub 服务或能力。请确认选择 Desk Buddy；旧固件需通过 USB 升级。"),
            "GATT_SERVICE_MISSING" => T("Windows did not discover the Hub service. Check the selected device, renew its local BLE permission and reconnect. Older firmware may need a USB upgrade.", "Windows 尚未发现 Hub 服务。请确认所选设备、在本机重新允许 BLE 管理后手动连接；旧固件可能需要通过 USB 升级。"),
            _ => error.Message
        } : error.Message;
    }
}
