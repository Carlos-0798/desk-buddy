using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace DeskBuddy.App;

// Dedicated 48x30 portraits, not a crop of a full-body character.
public static class CompanionPortraits
{
    public static readonly string[] Frames = ["idle", "blink", "happy"];
    static readonly Dictionary<(int Character, string Frame), BitmapSource> images = [];
    static readonly Dictionary<string, string> hashes = [];
    public static IReadOnlyDictionary<string, string> AssetHashes => hashes;
    public static string ManifestHash { get; }
    public static int Count => images.Count;
    static CompanionPortraits()
    {
        byte[] manifest = Read("manifest.json");
        ManifestHash = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
        using var document = JsonDocument.Parse(manifest);
        foreach (var item in document.RootElement.GetProperty("frames").EnumerateArray())
        {
            int character = item.GetProperty("character").GetInt32();
            string frame = item.GetProperty("frame").GetString()!;
            string png = item.GetProperty("png").GetString()!;
            byte[] bytes = Read(png);
            hashes[png] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hashes[png] != item.GetProperty("png_sha256").GetString())
                throw new InvalidDataException("Portrait asset differs from its production manifest: " + png);
            using var stream = new MemoryStream(bytes, false);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (bitmap.PixelWidth != 48 || bitmap.PixelHeight != 30) throw new InvalidDataException("Unexpected portrait dimensions.");
            bitmap.Freeze(); images.Add((character, frame), bitmap);
        }
        if (images.Count != 36 || Enumerable.Range(0, 12).Any(c => Frames.Any(f => !images.ContainsKey((c, f)))))
            throw new InvalidDataException("The portrait pack must contain all 36 production frames.");
    }
    static byte[] Read(string file)
    {
        using var stream = typeof(CompanionPortraits).Assembly.GetManifestResourceStream("DeskBuddy.Portraits." + file)
            ?? throw new InvalidDataException("Missing embedded portrait asset: " + file);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    public static BitmapSource Get(int character, string frame) => images[(Math.Clamp(character, 0, 11), frame)];
}
