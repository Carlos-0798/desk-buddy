using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace DeskBuddy.Core;

public sealed class HubProtocolException(string code, string? message = null) : IOException(message ?? $"Device returned {code}.")
{ public string Code { get; } = code; }
public sealed record HubReply(int Id, bool Success, IReadOnlyDictionary<string, string> Fields)
{
    public string? ErrorCode => Success ? null : Fields.GetValueOrDefault("code");
}
/// <summary>RAM-only visual demonstrations; zero is not a start action. Use StopAnimationPreviewAsync to stop.</summary>
public enum AnimationPreviewAction : byte { Idle = 1, Blink = 2, Happy = 3, Walk = 4, Tap = 5, Sleep = 6, Wake = 7 }
public sealed record HelloInfo(int Protocol, string Build, int ConfigBytes, int Profiles, int Bindings, uint Revision, bool Preview,
    int ThemeCount = 4, int FaceCount = 4, int HomeModeCount = 1, bool SupportsAnimationPreview = false, bool SupportsBleManagement = false,
    uint? BleManagementIdleMilliseconds = null, bool PreservesOutput = false, bool SupportsPetInputFeedback = false, bool PreservesPageOutput = false,
    bool SupportsDisplaySleep = false, bool BacklightControl = false, bool SupportsSleepBinding = false, bool SupportsCustomDisplayTimeout = false)
{
    public bool SupportsTheme(Theme theme) => ThemeCount is >= 4 and <= BankCodec.ThemeCount && (byte)theme < ThemeCount;
    public bool SupportsFace(Face face) => FaceCount is >= 4 and <= BankCodec.FaceCount && (byte)face < FaceCount;
    public bool SupportsHomeMode(HomeMode mode) => HomeModeCount is >= 1 and <= BankCodec.HomeModeCount && (byte)mode < HomeModeCount;
    public bool SupportsBank(HubBank bank)
    {
        BankCodec.Validate(bank);
        return bank.Profiles.All(p => SupportsTheme(p.Theme) && SupportsFace(p.Face) && SupportsHomeMode(p.Mode) &&
            (SupportsSleepBinding || !p.Bindings.Any(IsSleepBinding)));
    }
    public void RequireSupportedBank(HubBank bank)
    {
        BankCodec.Validate(bank);
        for (int i = 0; i < bank.Profiles.Length; i++)
        {
            var profile = bank.Profiles[i];
            if (!SupportsTheme(profile.Theme) || !SupportsFace(profile.Face) || !SupportsHomeMode(profile.Mode))
                throw new HubProtocolException("UNSUPPORTED_STYLE", $"Profile {i + 1} uses an appearance or home mode unavailable on this device (themes 0..{ThemeCount - 1}, faces 0..{FaceCount - 1}, home modes 0..{HomeModeCount - 1}). The local configuration was not changed or sent.");
            if (!SupportsSleepBinding && profile.Bindings.Any(IsSleepBinding))
                throw new HubProtocolException("UNSUPPORTED_BINDING", $"Profile {i + 1} uses the local screen-rest action, but this device does not advertise sleep_binding. The bank was not sent or changed.");
        }
    }
    private static bool IsSleepBinding(HubBinding binding) => binding.Kind == BindingKind.Local && binding.Value == 5;
}

public static class HubProtocol
{
    public const int MaxRequestLength = 511;
    public const int MaxReplyLength = 4096;
    public const uint AnimationPreviewDurationMilliseconds = 6000;
    public static HelloInfo ParseHello(HubReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Success) throw new HubProtocolException(reply.ErrorCode ?? "INVALID");
        var f = reply.Fields;
        if (RequireUInt(f, "proto") != 1 || RequireUInt(f, "config_bytes") != BankCodec.ByteCount ||
            RequireUInt(f, "profiles") != BankCodec.ProfileCount || RequireUInt(f, "bindings") != BankCodec.BindingCount)
            throw new HubProtocolException("UNSUPPORTED", "Device protocol or configuration layout is unsupported.");
        if (!f.TryGetValue("build", out string? build) || string.IsNullOrEmpty(build)) throw new FormatException("HELLO is missing its firmware build.");
        int themes = CapabilityCount(f, "theme_count", BankCodec.ThemeCount);
        int faces = CapabilityCount(f, "face_count", BankCodec.FaceCount);
        int modes = CapabilityCount(f, "home_mode_count", BankCodec.HomeModeCount, 1);
        bool ble = f.ContainsKey("ble_management") && RequireBool(f, "ble_management");
        uint? idle = f.ContainsKey("ble_manage_idle_ms") ? RequireUInt(f, "ble_manage_idle_ms") : null;
        if (idle.HasValue && (!ble || idle != 15000)) throw new FormatException("Invalid BLE management idle timeout capability.");
        bool display = f.ContainsKey("display_sleep") && RequireBool(f, "display_sleep");
        bool backlight = f.ContainsKey("backlight_control") && RequireBool(f, "backlight_control");
        bool sleepBinding = f.ContainsKey("sleep_binding") && RequireBool(f, "sleep_binding");
        bool displayCustom = f.ContainsKey("display_custom") && RequireBool(f, "display_custom");
        if ((backlight || sleepBinding || displayCustom) && !display || display && (!f.ContainsKey("backlight_control") || !f.ContainsKey("sleep_binding")))
            throw new FormatException("Display sleep capability requires explicit backlight and binding capabilities.");
        return new(1, build, BankCodec.ByteCount, BankCodec.ProfileCount, BankCodec.BindingCount,
            RequireUInt(f, "revision"), RequireBool(f, "preview"), themes, faces, modes,
            f.ContainsKey("animation_preview") && RequireBool(f, "animation_preview"),
            ble, idle,
            f.ContainsKey("preserve_output") && RequireBool(f, "preserve_output"),
            f.ContainsKey("pet_input_feedback") && RequireBool(f, "pet_input_feedback"),
            f.ContainsKey("preserve_page_output") && RequireBool(f, "preserve_page_output"), display, backlight, sleepBinding, displayCustom);
    }
    private static int CapabilityCount(IReadOnlyDictionary<string, string> fields, string key, int maximum, int minimum = 4)
    {
        if (!fields.ContainsKey(key)) return minimum; // Original protocol-1 firmware: four themes/faces, face-only home mode.
        uint value = RequireUInt(fields, key);
        if (value < minimum || value > maximum) throw new FormatException($"Invalid {key}: supported advertised range is {minimum}..{maximum}.");
        return (int)value;
    }
    public static string BuildRequest(int id, string command)
    {
        if (id < 1) throw new ArgumentOutOfRangeException(nameof(id));
        ValidateCommand(command);
        string line = $"HUB {id.ToString(CultureInfo.InvariantCulture)} {command}";
        if (line.Length > MaxRequestLength) throw new FormatException("Request exceeds 511 ASCII characters.");
        return line;
    }
    public static void ValidateCommand(string command)
    {
        if (string.IsNullOrEmpty(command) || command.Any(c => c is < ' ' or > '~') || command != command.Trim() || command.Contains("  "))
            throw new FormatException("Command must be a single canonical ASCII line.");
        string[] tokens = command.Split(' ');
        bool valid = tokens[0] switch
        {
            "HELLO" or "GET" or "STATUS" or "REVERT" => tokens.Length == 1,
            "PREVIEW" or "SAVE" => tokens.Length == 3 && IsUInt(tokens[1]) && IsBank(tokens[2]),
            "SELECT" => tokens.Length == 3 && IsUInt(tokens[1]) && tokens[2] is "0" or "1" or "2",
            "CONTROL" => IsControl(string.Join(' ', tokens.Skip(1))),
            "ANIMATE" => tokens.Length == 2 && tokens[1] is "IDLE" or "BLINK" or "HAPPY" or "WALK" or "TAP" or "SLEEP" or "WAKE" or "STOP",
            "DISPLAY" => tokens.Length == 2 && tokens[1] is "GET" or "SLEEP" or "WAKE" ||
                tokens.Length == 4 && tokens[1] == "SET" && IsDisplayTimeoutToken(tokens[2]) && IsDisplayTimeoutToken(tokens[3]),
            _ => false
        };
        if (!valid) throw new FormatException("Unsupported command or invalid arguments.");
    }
    private static bool IsDisplayTimeoutToken(string value) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint seconds) &&
        value == seconds.ToString(CultureInfo.InvariantCulture) && DisplaySettings.IsAllowedTimeout(seconds);
    public static bool IsControl(string command) => command is
        "OUTPUT 0" or "OUTPUT 1" or "ROUTE USB" or "ROUTE BLE" or "LANGUAGE EN" or "LANGUAGE ZH" or
        "PAGE HOME" or "PAGE DIAG" or "PAGE INFO" or "PAGE ACTIVITY" or
        "PAGE SETTINGS" or "PAGE LANGUAGE" or "PAGE TOOLS" or "PAGE MANAGEMENT" or
        "COUNTERS RESET" or "MANAGE 0" or "MANAGE 1";
    public static bool RequiresPageOutputCapability(string command) => command is
        "PAGE SETTINGS" or "PAGE LANGUAGE" or "PAGE TOOLS" or "PAGE MANAGEMENT";
    public static HubReply ParseReply(string line)
    {
        ValidateLine(line);
        string[] parts = line.Split(' ');
        if (parts.Length < 3 || parts[0] != "HUB" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 1 ||
            parts[2] is not ("OK" or "ERR")) throw new FormatException("Invalid HUB reply header.");
        var fields = ParseFields(parts.Skip(3));
        if (parts[2] == "ERR" && (!fields.TryGetValue("code", out string? code) || code.Length == 0 || code.Any(c => c is not (>= 'A' and <= 'Z') and not '_')))
            throw new FormatException("Invalid HUB error code.");
        return new(id, parts[2] == "OK", fields);
    }
    public static bool TryParseReply(string line, out HubReply? reply)
    {
        try { reply = ParseReply(line); return true; } catch (FormatException) { reply = null; return false; }
    }
    internal static IReadOnlyDictionary<string, string> ParseFields(IEnumerable<string> tokens)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            int at = token.IndexOf('=');
            if (at <= 0 || at == token.Length - 1 || token[..at].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
                !fields.TryAdd(token[..at], token[(at + 1)..])) throw new FormatException("Invalid or duplicate reply field.");
        }
        return new ReadOnlyDictionary<string, string>(fields);
    }
    internal static void ValidateLine(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > MaxReplyLength || line.Any(c => c is < ' ' or > '~') || line != line.Trim() || line.Contains("  "))
            throw new FormatException("Reply must be a bounded canonical ASCII line.");
    }
    internal static uint RequireUInt(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out string? value) || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
            throw new FormatException($"Missing or invalid {key}.");
        return parsed;
    }
    internal static bool RequireBool(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out string? value) || value is not ("0" or "1")) throw new FormatException($"Missing or invalid {key}.");
        return value == "1";
    }
    internal static (uint Action, uint Remaining) RequireAnimationState(IReadOnlyDictionary<string, string> fields)
    {
        uint action = RequireUInt(fields, "demo_action"), remaining = RequireUInt(fields, "demo_remaining_ms");
        if (action > (byte)AnimationPreviewAction.Wake || remaining > AnimationPreviewDurationMilliseconds ||
            (action == 0) != (remaining == 0))
            throw new FormatException("Invalid animation preview action or remaining duration.");
        return (action, remaining);
    }
    private static bool IsUInt(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static bool IsBank(string value) { try { BankCodec.FromHex(value); return true; } catch (FormatException) { return false; } }
}

public sealed class DeviceStatus
{
    public IReadOnlyDictionary<string, string> Fields { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string Build => Fields["build"];
    public string Route => Fields["route"];
    public bool Connected => GetBool("connected") == true;
    public bool Armed => GetBool("armed") == true;
    public bool HidSuppressed => GetBool("hid_suppressed") == true;
    public DisplaySettings? Display { get; }
    public uint? DisplayIdleMilliseconds => GetUInt32("display_idle_ms");
    public bool HasAnimationPreviewStatus => Fields.ContainsKey("demo_action");
    public AnimationPreviewAction? DemoAction => GetUInt32("demo_action") is > 0 and <= 7 ? (AnimationPreviewAction)GetUInt32("demo_action")!.Value : null;
    public uint? DemoRemainingMilliseconds => GetUInt32("demo_remaining_ms");
    public bool HasPetInputFeedbackStatus => Fields.ContainsKey("pet_direction");
    public int? PetDirection => GetInt32("pet_direction");
    public uint? PetElapsedMilliseconds => GetUInt32("pet_elapsed_ms");
    public bool HasBleManagementStatus => Fields.ContainsKey("ble_manage");
    public uint? BleManagementState => GetUInt32("ble_manage");
    public uint? BleManagementRemainingMilliseconds => GetUInt32("ble_manage_remaining_ms");
    public uint? ManagementOwner => GetUInt32("manage_owner");
    public uint? ManagementLeaseMilliseconds => GetUInt32("manage_lease_ms");
    public bool[]? KeysDown => Get("keys_down")?.Select(c => c == '1').ToArray();
    public uint[]? KeyPresses => ParseCounts(Get("key_presses"));
    public uint[]? KeyReleases => ParseCounts(Get("key_releases"));
    public string? Get(string key) => Fields.GetValueOrDefault(key);
    public uint? GetUInt32(string key) => uint.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ? value : null;
    public int? GetInt32(string key) => int.TryParse(Get(key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : null;
    public bool? GetBool(string key) => Get(key) switch { "1" => true, "0" => false, _ => null };
    internal DeviceStatus(IReadOnlyDictionary<string, string> fields, DateTimeOffset receivedAt, bool preservesOutput = false)
    {
        if (!fields.TryGetValue("build", out string? build) || build.Length == 0 || !fields.TryGetValue("route", out string? route) || route is not ("USB" or "BLE"))
            throw new FormatException("Status requires build and USB/BLE route.");
        HubProtocol.RequireBool(fields, "connected"); HubProtocol.RequireBool(fields, "armed");
        if (DisplaySettings.FieldNames.Any(fields.ContainsKey) || fields.ContainsKey("display_idle_ms"))
        {
            Display = DisplaySettings.Parse(fields);
            HubProtocol.RequireUInt(fields, "display_idle_ms");
        }
        foreach (string name in new[] { "encoder", "touch", "calibrated", "stored", "switch_down", "vbat_sampled", "usb_vbus", "language_saved", "usb_connected", "ble_connected", "config_preview", "hid_suppressed" })
            if (fields.ContainsKey(name)) HubProtocol.RequireBool(fields, name);
        foreach (string name in new[] { "uptime_ms", "presses", "cw", "ccw", "sent", "dropped", "page", "markers", "vbat_est_mV", "vbat_raw", "vbat_age_ms", "ui_last_us", "ui_max_us", "config_revision", "profile_index", "theme", "face", "home_mode" })
            if (fields.ContainsKey(name)) HubProtocol.RequireUInt(fields, name);
        foreach (string name in new[] { "enc", "rotation" })
            if (fields.TryGetValue(name, out string? value) && !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                throw new FormatException($"Invalid {name}.");
        if (fields.TryGetValue("keys_down", out string? keys) && (keys.Length != 6 || keys.Any(c => c is not ('0' or '1')))) throw new FormatException("Invalid six-key state.");
        if (fields.TryGetValue("key_presses", out string? presses)) ValidateCounts(presses);
        if (fields.TryGetValue("key_releases", out string? releases)) ValidateCounts(releases);
        if (fields.TryGetValue("battery_est_percent", out string? battery) &&
            (!int.TryParse(battery, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int estimate) || estimate is < -1 or > 100))
            throw new FormatException("Invalid battery estimate.");
        if (fields.TryGetValue("language", out string? language) && language is not ("ZH" or "EN")) throw new FormatException("Invalid language.");
        if (fields.ContainsKey("profile_index") && HubProtocol.RequireUInt(fields, "profile_index") > 2) throw new FormatException("Invalid profile index.");
        if (fields.ContainsKey("theme") && HubProtocol.RequireUInt(fields, "theme") >= BankCodec.ThemeCount) throw new FormatException("Invalid theme.");
        if (fields.ContainsKey("face") && HubProtocol.RequireUInt(fields, "face") >= BankCodec.FaceCount) throw new FormatException("Invalid face.");
        if (fields.ContainsKey("home_mode") && HubProtocol.RequireUInt(fields, "home_mode") >= BankCodec.HomeModeCount) throw new FormatException("Invalid home mode.");
        if (fields.ContainsKey("demo_action") || fields.ContainsKey("demo_remaining_ms"))
        {
            var demo = HubProtocol.RequireAnimationState(fields);
            if (demo.Action != 0 && ((!preservesOutput && HubProtocol.RequireBool(fields, "armed")) || HubProtocol.RequireUInt(fields, "page") != 0))
                throw new FormatException("An active animation preview requires HOME and, without the negotiated preserve-output capability, output OFF.");
        }
        if (fields.ContainsKey("pet_direction") || fields.ContainsKey("pet_elapsed_ms"))
        {
            if (!fields.TryGetValue("pet_direction", out string? direction) ||
                !int.TryParse(direction, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsedDirection) || parsedDirection is < -1 or > 1 ||
                HubProtocol.RequireUInt(fields, "pet_elapsed_ms") > 1799)
                throw new FormatException("Invalid physical pet feedback direction or elapsed time.");
        }
        string[] managementFields = ["ble_manage", "ble_manage_remaining_ms", "ble_link", "ble_secured", "ble_bonded", "manage_owner", "manage_lease_ms"];
        if (managementFields.Any(fields.ContainsKey))
        {
            uint state = HubProtocol.RequireUInt(fields, "ble_manage"), remaining = HubProtocol.RequireUInt(fields, "ble_manage_remaining_ms");
            uint owner = HubProtocol.RequireUInt(fields, "manage_owner"), lease = HubProtocol.RequireUInt(fields, "manage_lease_ms");
            bool link = HubProtocol.RequireBool(fields, "ble_link"), secured = HubProtocol.RequireBool(fields, "ble_secured"), bonded = HubProtocol.RequireBool(fields, "ble_bonded");
            if (state > 2 || remaining > 60000 || (state == 1) != (remaining > 0) || owner > 2 || lease > 10000 || (owner == 0) != (lease == 0) ||
                (state == 2 && (!link || !secured || !bonded))) throw new FormatException("Invalid BLE management status or lease.");
        }
        Fields = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(fields, StringComparer.Ordinal)); ReceivedAt = receivedAt;
    }
    private static void ValidateCounts(string value)
    {
        var tokens = value.Split(',');
        if (tokens.Length != 6 || tokens.Any(t => !uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out _))) throw new FormatException("Expected six unsigned key counters.");
    }
    private static uint[]? ParseCounts(string? value) => value?.Split(',').Select(t => uint.Parse(t, CultureInfo.InvariantCulture)).ToArray();
}

public static class LegacyStatusParser
{
    public static DeviceStatus Parse(string line, DateTimeOffset? receivedAt = null, bool preservesOutput = false)
    {
        HubProtocol.ValidateLine(line);
        if (!line.StartsWith("STATUS ", StringComparison.Ordinal)) throw new FormatException("Expected STATUS line.");
        return new(HubProtocol.ParseFields(line[7..].Split(' ')), receivedAt ?? DateTimeOffset.UtcNow, preservesOutput);
    }
    public static bool TryParse(string line, out DeviceStatus? status, DateTimeOffset? receivedAt = null, bool preservesOutput = false)
    {
        try { status = Parse(line, receivedAt, preservesOutput); return true; } catch (FormatException) { status = null; return false; }
    }
}

/// <summary>Bounded, byte-oriented LF/CRLF framing. Overflow or invalid bytes discard the entire line.</summary>
public sealed class LineFramer
{
    private readonly int _max;
    private readonly StringBuilder _buffer = new();
    private bool _discard;
    private bool _carriageReturn;
    public long DroppedLines { get; private set; }
    public LineFramer(int maxLineLength = HubProtocol.MaxReplyLength)
    {
        if (maxLineLength is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maxLineLength));
        _max = maxLineLength;
    }
    public IReadOnlyList<string> Feed(ReadOnlySpan<byte> chunk)
    {
        var lines = new List<string>();
        foreach (byte b in chunk)
        {
            if (b == 10)
            {
                if (_discard) DroppedLines++; else if (_buffer.Length > 0) lines.Add(_buffer.ToString());
                _buffer.Clear(); _discard = false; _carriageReturn = false; continue;
            }
            if (_discard) continue;
            if (b == 13 && !_carriageReturn) { _carriageReturn = true; continue; }
            if (_carriageReturn || b is < 0x20 or > 0x7E || _buffer.Length >= _max) { _discard = true; _buffer.Clear(); continue; }
            _buffer.Append((char)b);
        }
        return lines;
    }
    public void Reset() { _buffer.Clear(); _discard = false; _carriageReturn = false; DroppedLines = 0; }
}
