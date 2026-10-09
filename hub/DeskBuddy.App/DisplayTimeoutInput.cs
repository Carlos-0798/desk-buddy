using System.Globalization;
namespace DeskBuddy.App;
/// <summary>UI minutes are separate from wire seconds. No rounding or clamping.</summary>
public static class DisplayTimeoutInput
{
    public static bool TryMinutes(string? text, out uint seconds)
    {
        seconds = 0;
        if (!uint.TryParse(text?.Trim() ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out uint minutes) || minutes is < 1 or > 720) return false;
        seconds = minutes * 60; return true;
    }
}
