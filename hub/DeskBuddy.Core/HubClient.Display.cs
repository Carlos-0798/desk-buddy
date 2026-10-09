using System.Globalization;

namespace DeskBuddy.Core;

public sealed partial class HubClient
{
    /// <summary>Read the independent display settings and fresh status. Never wakes or saves.</summary>
    public Task<DeviceStatus> ReadDisplayAsync(CancellationToken cancellationToken = default) =>
        DisplayCommandAsync("GET", null, null, cancellationToken);

    /// <summary>Persist only the USB/battery timeouts, then verify a separate GET and STATUS. Never changes the profile bank or arms HID.</summary>
    public Task<DeviceStatus> SetDisplayAsync(uint usbSeconds, uint batterySeconds, CancellationToken cancellationToken = default)
    {
        if (!DisplaySettings.IsAllowedTimeout(usbSeconds)) throw new ArgumentOutOfRangeException(nameof(usbSeconds));
        if (!DisplaySettings.IsAllowedTimeout(batterySeconds)) throw new ArgumentOutOfRangeException(nameof(batterySeconds));
        return DisplayCommandAsync($"SET {usbSeconds.ToString(CultureInfo.InvariantCulture)} {batterySeconds.ToString(CultureInfo.InvariantCulture)}",
            usbSeconds, batterySeconds, cancellationToken);
    }
    public Task<DeviceStatus> SleepDisplayAsync(CancellationToken cancellationToken = default) =>
        DisplayCommandAsync("SLEEP", null, null, cancellationToken);
    public Task<DeviceStatus> WakeDisplayAsync(CancellationToken cancellationToken = default) =>
        DisplayCommandAsync("WAKE", null, null, cancellationToken);

    private Task<DeviceStatus> DisplayCommandAsync(string operation, uint? usb, uint? battery, CancellationToken ct) => RunAsync(async () =>
    {
        if (IsLegacy || Capabilities?.SupportsDisplaySleep != true)
            throw new HubProtocolException("UNSUPPORTED", "This device does not advertise display_sleep. No display command was sent.");
        if (usb.HasValue && Capabilities.SupportsCustomDisplayTimeout != true &&
            (!DisplaySettings.IsLegacyTimeout(usb.Value) || !DisplaySettings.IsLegacyTimeout(battery!.Value)))
            throw new HubProtocolException("UNSUPPORTED_DISPLAY_TIMEOUT", "This device does not advertise display_custom. Only 0, 30, 60 or 300 seconds can be written; no command was sent.");
        Status = null; Notify();
        var reply = await RequestAsync("DISPLAY " + operation, ct).ConfigureAwait(false);
        var ack = ParseDisplayObservation(reply);
        if (operation == "SLEEP" && !ack.Asleep || operation == "WAKE" && ack.Asleep)
            throw new HubProtocolException("DISPLAY_NOT_CONFIRMED", "Display acknowledgement did not confirm the requested state.");
        if (usb.HasValue)
        {
            if (!ack.Saved) throw new HubProtocolException("SAVE_FAILED", "Display settings were not verified as saved.");
            RequireRequestedTimeouts(ack, usb.Value, battery!.Value);
            var readback = ParseDisplayObservation(await RequestAsync("DISPLAY GET", ct).ConfigureAwait(false));
            if (!readback.Saved) throw new HubProtocolException("SAVE_FAILED", "Display readback did not confirm saved settings.");
            RequireRequestedTimeouts(readback, usb.Value, battery.Value);
        }
        var actual = await StatusCoreAsync(false, ct).ConfigureAwait(false);
        if (usb.HasValue)
        {
            if (actual.Display?.Saved != true) throw new HubProtocolException("SAVE_FAILED", "Current display status does not confirm saved settings.");
            RequireRequestedTimeouts(actual.Display, usb.Value, battery!.Value);
        }
        // Physical wake, output OFF, or a source change may occur after the ACK.
        // Return this later observation; never restore either asleep or armed.
        return actual;
    }, ct);

    private DisplaySettings ParseDisplayObservation(HubReply reply)
    {
        var observation = DisplaySettings.Parse(reply.Fields);
        if (observation.BacklightControlled != Capabilities!.BacklightControl)
            throw new FormatException("Display backlight status differs from the negotiated hardware capability.");
        return observation;
    }
    private static void RequireRequestedTimeouts(DisplaySettings state, uint usb, uint battery)
    {
        if (state.UsbTimeoutSeconds != usb || state.BatteryTimeoutSeconds != battery)
            throw new HubProtocolException("READBACK_MISMATCH", "Display settings changed or did not match the requested timeouts. Read current settings before retrying.");
    }
}
