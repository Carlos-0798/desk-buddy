using DeskBuddy.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DeskBuddy.App;

public sealed partial class MainWindow
{
    bool useBleManagement;
    BleCandidate[] bleCandidates = [];
    BleCandidate? selectedBle;
    CancellationTokenSource? connectionOperation;
    bool ConnectionBusy => connectionOperation is not null;
    bool UsbClosing => transport is SerialLineTransport { IsClosing: true };
    string ManagementLabel => UsbClosing ? T("USB is still closing", "USB 仍在关闭") : !Connected ? T("Not connected", "未连接") : client!.ManagementChannel switch
    {
        ManagementChannel.Ble => T("BLE GATT management", "BLE GATT 管理"),
        ManagementChannel.Usb => demo ? T("USB management (simulated)", "USB 管理（模拟）") : T("USB cable management", "USB 线缆管理"),
        _ => T("Simulated management", "模拟管理通道")
    };

    void BuildConnectionControls(StackPanel right)
    {
        right.Children.Add(Note(demo ? T("Offline demo. No radio or serial device is accessed.", "离线演示，不访问无线或串口设备。") :
            T("Choose one management connection. HID output remains a separate setting below.", "选择一种管理连接；下方 HID 输出通道是独立设置。")));
        var channel = new ComboBox { ItemsSource = new[] { T("USB cable", "USB 线缆"), T("Bluetooth LE · paired device", "蓝牙 LE · 已配对设备") }, SelectedIndex = useBleManagement ? 1 : 0, Margin = new Thickness(0, 10, 0, 0) };
        channel.SelectionChanged += (_, _) => { if (Connected || ConnectionBusy || busy || UsbClosing) return; useBleManagement = channel.SelectedIndex == 1; BuildPage(); };
        liveUpdates.Add(() => channel.IsEnabled = !demo && !Connected && !busy && !ConnectionBusy && !UsbClosing); right.Children.Add(channel);
        if (useBleManagement)
        {
            var choices = new ComboBox { ItemsSource = bleCandidates, SelectedItem = selectedBle, Margin = new Thickness(0, 10, 0, 0) };
            choices.SelectionChanged += (_, _) => { selectedBle = choices.SelectedItem as BleCandidate; UpdateLive(); };
            liveUpdates.Add(() => choices.IsEnabled = !Connected && !busy && !ConnectionBusy); right.Children.Add(choices);
            right.Children.Add(Note(T("Pair in Windows first. On Desk Buddy: Settings → Device info → BLE management → Allow BLE connection, or use the USB grant below. Select your Desk Buddy; its custom service is checked on connection. Older firmware needs a USB upgrade.",
                "先在 Windows 设置中配对。本机路径：菜单 → 设备信息 → 蓝牙管理 → 允许蓝牙连接；也可先用下方 USB 授权。列表包含已配对 BLE 设备，请选自己的 Desk Buddy；连接时核对专用服务。旧固件需先通过 USB 升级。")));
        }
        else
        {
            var ports = new ComboBox { ItemsSource = candidates, SelectedItem = selectedPort, Margin = new Thickness(0, 10, 0, 0) };
            ports.SelectionChanged += (_, _) => { selectedPort = ports.SelectedItem as PortCandidate; UpdateLive(); };
            liveUpdates.Add(() => ports.IsEnabled = !demo && !Connected && !busy && !ConnectionBusy); right.Children.Add(ports);
            right.Children.Add(Note(T("Only Adafruit 239A:8029 USB ports are listed; the handshake checks firmware.", "USB 列表仅显示 Adafruit 239A:8029；连接后握手核对固件。")));
        }
        right.Children.Add(Row(AsyncBtn(T("Scan", "重新检测"), ScanPorts, T("Device list refreshed. Nothing connected automatically.", "设备列表已刷新，未自动连接。"), () => !busy && !ConnectionBusy && !demo && !Connected),
            AsyncBtn(T("Connect", "连接"), ConnectSelected, T("Management handshake and readback complete.", "管理握手与回读完成。"), () => !busy && !ConnectionBusy && !UsbClosing && !Connected && (demo || (useBleManagement ? selectedBle != null : selectedPort != null)), true),
            AsyncBtn(T("Disconnect", "断开"), Disconnect, T("Management disconnected; no pending command will replay.", "管理连接已断开，不会重放未完成命令。"), () => !busy && !ConnectionBusy && Connected)));
        var cancel = Btn(T("Cancel discovery / connection", "取消检测／连接"), () => connectionOperation?.Cancel());
        actionButtons.Add((cancel, () => ConnectionBusy)); right.Children.Add(cancel);
        var connectionText = Txt("", 12, false, muted); connectionText.Margin = new Thickness(0, 10, 0, 0); right.Children.Add(connectionText);
        liveUpdates.Add(() => connectionText.Text = UsbClosing ? T("USB is still closing. The driver has not released the port; new connections are blocked. If this persists, exit Hub and reopen before retrying.", "USB 仍在关闭：驱动尚未释放端口，暂不能建立新连接。若持续不结束，请退出 Hub 后重新打开再试。") : ConnectionBusy ? T("Working… cancellation is available above.", "正在处理，可在上方取消。") : !Connected ?
            T("Select a device explicitly. No SAVE or output command is sent on connection.", "请明确选择设备；连接不会自动保存配置或开启输出。") :
            ManagementLabel + "\n" + (client!.IsLegacy ? T("Legacy controls only; profile editing requires upgraded firmware.", "旧固件仅支持既有控制；配置编辑需升级。") : $"{client.Capabilities?.Build} · {T("revision", "修订")} {client.Configuration?.Revision}"));
        var grantState = Txt("", 12, false, muted); right.Children.Add(grantState);
        liveUpdates.Add(() => grantState.Text = !Fresh || client?.Capabilities?.SupportsBleManagement != true ? T("BLE permission status unavailable on this connection.", "此连接暂无 BLE 管理授权状态。") :
            T("BLE permission: ", "BLE 管理授权：") + (client.Status!.BleManagementState switch { 1 => T("Waiting for a bonded encrypted connection (up to 60 s)", "等待已配对加密连接（最多 60 秒）"), 2 => T("authorized; session timeout still applies", "已授权，仍受会话超时限制"), _ => T("closed", "关闭") }) +
            " · " + T("writer lease: ", "写入租约：") + (client.Status.ManagementOwner switch { 1 => "USB", 2 => "BLE", _ => T("none", "无") }));
        right.Children.Add(Note(T("Connect within 60 seconds of granting access. Supported firmware revokes management after 15 seconds without a successful Hub request once management begins. Normal BLE disconnect confirms self-revocation or notification unsubscribe; if neither is confirmed, revoke on the device or wait for that timeout. HID output is separate and is not turned off by BLE self-revocation.",
            "授权后请在 60 秒内连接。支持的新固件在开始管理后，连续 15 秒没有成功 Hub 请求会自动撤销管理。正常断开会确认自撤权或退订通知；两者均未确认时，请在本机撤销或等待此超时。HID 输出独立，BLE 自撤权不会关闭媒体输出。")));
        right.Children.Add(Row(AsyncBtn(T("Allow BLE via USB", "通过 USB 允许 BLE"), () => SetManagementPermission(true), T("Device BLE grant confirmed. Disconnect USB management, then choose BLE.", "设备 BLE 授权已确认。请断开 USB 管理，再选择 BLE 连接。"), CanGrantBle),
            AsyncBtn(T("Revoke BLE via USB", "通过 USB 撤销 BLE"), () => SetManagementPermission(false), T("BLE management grant revoked.", "已确认撤销 BLE 管理授权。"), CanGrantBle)));
    }
    bool CanGrantBle() => !busy && Fresh && client?.ManagementChannel == ManagementChannel.Usb && client.Capabilities?.SupportsBleManagement == true;
    async Task SetManagementPermission(bool enabled)
    {
        await StopAnimation();
        if (client is null) throw new IOException("No USB management connection.");
        await client.SetBleManagementAsync(enabled, lifetime.Token); UpdateLive();
    }
    async Task WithConnectionOperation(Func<CancellationToken, Task> operation)
    {
        if (connectionOperation is not null) throw new InvalidOperationException("A discovery or connection is already running.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, deadline.Token);
        connectionOperation = request; UpdateLive();
        try { await operation(request.Token); }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException(T("Management connection timed out. Reconnect explicitly; nothing was replayed.", "管理连接超时。请手动重连；没有重放命令。"), error); }
        finally { connectionOperation = null; UpdateLive(); }
    }
}
