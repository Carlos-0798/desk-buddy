namespace DeskBuddy.Core;

/// <summary>A real device observation, separate from the 224-byte profile bank and pet sleep animation.</summary>
public sealed record DisplaySettings(bool Asleep, uint UsbTimeoutSeconds, uint BatteryTimeoutSeconds,
    bool Saved, bool BacklightControlled, uint WakeCount, bool Armed, uint IdleMilliseconds)
{
    internal static readonly string[] FieldNames = ["display_asleep", "display_usb_s", "display_battery_s", "display_saved", "display_backlight", "display_wake_count"];
    public const uint MaximumTimeoutSeconds = 43200;
    public static bool IsAllowedTimeout(uint seconds) => seconds is 0 or 30 || seconds >= 60 && seconds <= MaximumTimeoutSeconds && seconds % 60 == 0;
    public static bool IsLegacyTimeout(uint seconds) => seconds is 0 or 30 or 60 or 300;
    internal static DisplaySettings Parse(IReadOnlyDictionary<string, string> fields)
    {
        uint usb = HubProtocol.RequireUInt(fields, "display_usb_s"), battery = HubProtocol.RequireUInt(fields, "display_battery_s");
        if (!IsAllowedTimeout(usb) || !IsAllowedTimeout(battery)) throw new FormatException("Display timeouts must be 0, 30, or whole minutes from 60 through 43200 seconds.");
        return new(HubProtocol.RequireBool(fields, "display_asleep"), usb, battery,
            HubProtocol.RequireBool(fields, "display_saved"), HubProtocol.RequireBool(fields, "display_backlight"),
            HubProtocol.RequireUInt(fields, "display_wake_count"), HubProtocol.RequireBool(fields, "armed"), HubProtocol.RequireUInt(fields, "display_idle_ms"));
    }
}
