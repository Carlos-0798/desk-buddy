using System.Buffers.Binary;
using System.Text;

namespace DeskBuddy.Core;

public enum Theme : byte { Midnight = 0, Paper = 1, Terminal = 2, Lavender = 3, Ocean = 4, Sunset = 5, Garden = 6, Blueprint = 7 }
public enum Face : byte { Pixel = 0, Round = 1, Cat = 2, Robot = 3, Bunny = 4, Ghost = 5, Mochi = 6, Orbit = 7, Fox = 8, Panda = 9, Sleepy = 10, Wink = 11 }
public enum HomeMode : byte { Face = 0, DeskPet = 1, VirtualPet = 2 }
public enum BindingKind : byte { Disabled, Consumer, Keyboard, Local }
public sealed record HubBinding(BindingKind Kind, ushort Value = 0, byte Modifiers = 0);
public sealed record HubProfile(string Name, Theme Theme, Face Face, bool Animation,
    bool EncoderReverse, ushort AccentRgb565, HubBinding[] Bindings, HomeMode Mode = HomeMode.Face);
public sealed record HubBank(byte ActiveProfile, HubProfile[] Profiles);
public sealed record BankSnapshot(HubBank Bank, uint Revision, bool Preview);

public static class HubDefaults
{
    public static HubBank CreateBank() => new(0, new[] { "MEDIA", "STUDY", "WORK" }
        .Select(name => new HubProfile(name, Theme.Midnight, Face.Pixel, true, false, 0,
        [Consumer(0xCD), Consumer(0xB6), Consumer(0xB5), Consumer(0xE2),
         new(BindingKind.Local, 1), new(BindingKind.Local, 2), Consumer(0xE9),
         Consumer(0xEA), Consumer(0xE2), Consumer(0xB6), Consumer(0xCD), Consumer(0xB5)]))
        .ToArray());
    private static HubBinding Consumer(ushort value) => new(BindingKind.Consumer, value);
}

/// <summary>The exact protocol-1 bank. No CRC is added to the 224 wire bytes.</summary>
public static class BankCodec
{
    public const int ByteCount = 224;
    public const int ProfileCount = 3;
    public const int BindingCount = 12;
    public const int ThemeCount = 8;
    public const int FaceCount = 12;
    public const int HomeModeCount = 3;
    public static readonly IReadOnlyList<string> BindingNames = Array.AsReadOnly(new[]
    { "K1", "K2", "K3", "K4", "K5", "K6", "Clockwise", "Counterclockwise", "Knob press", "Touch previous", "Touch play", "Touch next" });

    public static void Validate(HubBank bank) => _ = Encode(bank);
    public static HubBank Clone(HubBank bank) => Decode(Encode(bank));
    public static byte[] Encode(HubBank bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (bank.ActiveProfile >= ProfileCount || bank.Profiles is null || bank.Profiles.Length != ProfileCount)
            throw new FormatException("A bank requires exactly three profiles and an active index from 0 to 2.");
        var bytes = new byte[ByteCount];
        bytes[0] = 1; bytes[1] = bank.ActiveProfile; bytes[2] = ProfileCount;
        for (int p = 0; p < ProfileCount; p++)
        {
            var profile = bank.Profiles[p] ?? throw new FormatException($"Profile {p + 1} is missing.");
            if ((byte)profile.Theme >= ThemeCount || (byte)profile.Face >= FaceCount || (byte)profile.Mode >= HomeModeCount)
                throw new FormatException($"Profile {p + 1}: unknown theme, face or home mode.");
            if (profile.Name is null || profile.Name.Length is < 1 or > 15 || profile.Name.Any(c => c is < ' ' or > '~'))
                throw new FormatException($"Profile {p + 1}: name must contain 1 to 15 printable ASCII characters.");
            if (profile.Bindings is null || profile.Bindings.Length != BindingCount)
                throw new FormatException($"Profile {p + 1}: exactly twelve bindings are required.");
            int offset = 8 + p * 72;
            bytes[offset] = (byte)profile.Theme; bytes[offset + 1] = (byte)profile.Face;
            bytes[offset + 2] = profile.Animation ? (byte)1 : (byte)0;
            bytes[offset + 3] = profile.EncoderReverse ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 4), profile.AccentRgb565);
            bytes[offset + 6] = (byte)profile.Mode;
            Encoding.ASCII.GetBytes(profile.Name, bytes.AsSpan(offset + 8, 16));
            for (int b = 0; b < BindingCount; b++)
            {
                var binding = profile.Bindings[b] ?? throw new FormatException($"Binding {b + 1} is missing.");
                ValidateBinding(binding);
                int at = offset + 24 + b * 4;
                bytes[at] = (byte)binding.Kind;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 1), binding.Value);
                bytes[at + 3] = binding.Modifiers;
            }
        }
        return bytes;
    }
    public static HubBank Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount) throw new FormatException("Configuration must be exactly 224 bytes.");
        if (bytes[0] != 1) throw new FormatException("Unsupported configuration schema.");
        if (bytes[1] >= ProfileCount || bytes[2] != ProfileCount || bytes.Slice(3, 5).ContainsAnyExcept((byte)0))
            throw new FormatException("Invalid bank header or reserved bytes.");
        var profiles = new HubProfile[ProfileCount];
        for (int p = 0; p < ProfileCount; p++)
        {
            int at = 8 + p * 72;
            if (bytes[at] >= ThemeCount || bytes[at + 1] >= FaceCount || bytes[at + 2] > 1 || bytes[at + 3] > 1 ||
                bytes[at + 6] >= HomeModeCount || bytes[at + 7] != 0)
                throw new FormatException($"Profile {p + 1}: invalid flags or reserved bytes.");
            var name = bytes.Slice(at + 8, 16);
            int end = name.IndexOf((byte)0);
            if (end is < 1 or > 15 || name[end..].ContainsAnyExcept((byte)0))
                throw new FormatException($"Profile {p + 1}: name must be NUL padded.");
            for (int i = 0; i < end; i++) if (name[i] is < 0x20 or > 0x7E) throw new FormatException("Non-ASCII profile name.");
            var bindings = new HubBinding[BindingCount];
            for (int b = 0; b < BindingCount; b++)
            {
                var item = bytes.Slice(at + 24 + b * 4, 4);
                var binding = new HubBinding((BindingKind)item[0], BinaryPrimitives.ReadUInt16LittleEndian(item[1..]), item[3]);
                ValidateBinding(binding); bindings[b] = binding;
            }
            profiles[p] = new(Encoding.ASCII.GetString(name[..end]), (Theme)bytes[at], (Face)bytes[at + 1],
                bytes[at + 2] == 1, bytes[at + 3] == 1, BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 4)..]), bindings, (HomeMode)bytes[at + 6]);
        }
        return new(bytes[1], profiles);
    }
    public static string ToHex(HubBank bank) => Convert.ToHexString(Encode(bank));
    public static HubBank FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != ByteCount * 2 || hex.Any(c => !Uri.IsHexDigit(c)))
            throw new FormatException("Expected exactly 448 hexadecimal characters without spaces.");
        return Decode(Convert.FromHexString(hex));
    }
    private static void ValidateBinding(HubBinding binding)
    {
        bool valid = binding.Kind switch
        {
            BindingKind.Disabled => binding.Value == 0 && binding.Modifiers == 0,
            BindingKind.Consumer => binding.Modifiers == 0 && binding.Value is 0xCD or 0xB6 or 0xB5 or 0xE2 or 0xE9 or 0xEA,
            BindingKind.Keyboard => binding.Value is >= 0x04 and <= 0x73,
            BindingKind.Local => binding.Modifiers == 0 && binding.Value is >= 1 and <= 5,
            _ => false
        };
        if (!valid) throw new FormatException($"Unsupported {binding.Kind} binding value/modifiers.");
    }
}

public static class Crc32
{
    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }
}
