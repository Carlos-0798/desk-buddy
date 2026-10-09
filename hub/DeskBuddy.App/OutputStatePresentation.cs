using DeskBuddy.Core;

namespace DeskBuddy.App;

// Text-only presentation is also exercised by the managed, hardware-free runner.
internal static class OutputStatePresentation
{
    public static bool CanEnable(DeviceStatus? status, HelloInfo? capabilities, bool fresh, bool busy) =>
        fresh && !busy && status is { Connected: true, HidSuppressed: false } && status.GetBool("calibrated") == true &&
        (status.GetUInt32("page") is 0 or 1 || capabilities?.PreservesPageOutput == true && status.GetUInt32("page") is 4 or 6 or 7 or 8);

    public static string Pill(DeviceStatus? status, bool fresh, bool chinese)
    {
        if (!fresh || status is null) return chinese ? "输出未知" : "OUTPUT UNKNOWN";
        string state = status.Armed ? (chinese ? "输出开启" : "OUTPUT ON") : (chinese ? "输出关闭" : "OUTPUT OFF");
        return status.HidSuppressed ? state + (chinese ? "\nHID 暂停发送" : "\nHID PAUSED") : state;
    }

    public static string Detail(DeviceStatus? status, bool fresh, bool chinese)
    {
        if (!fresh || status is null) return chinese ? "等待设备状态；不会自动开启输出。" : "Waiting for device state; output is never enabled automatically.";
        if (status.Display?.Asleep == true)
            return chinese ? "屏幕休息中，HID 暂停发送；首次实体输入只唤醒。输出开关保持实际状态，唤醒不会自动开启关闭的输出。"
                : "Screen resting; HID sending is paused. The first physical input only wakes it. The actual output switch is retained; waking never enables an OFF switch.";
        if (status.HidSuppressed)
            return status.Armed
                ? (chinese ? "输出开关保持开启，HID 暂停发送。离开测试页或结束唤醒输入隔离后，仅在开关仍开启且链路就绪时恢复。" : "The switch stays ON; HID sending is paused. After leaving a test page or finishing wake-input isolation, it resumes only while the switch remains ON and the link is ready.")
                : (chinese ? "输出开关已关闭，HID 暂停发送；退出暂停状态不会自动开启输出。" : "The switch is OFF and HID sending is paused; ending the pause will not enable output.");
        return chinese ? "显示设备实际输出开关状态；导航不会自动重新开启输出。" : "Showing the device's actual output switch; navigation never automatically rearms it.";
    }
}
