using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DeskBuddy.App;

// These are decoded from the same RGB565 palette/index bytes used by the device.
// Embedded resources keep the published single-file app independent of an asset folder.
public static class CompanionSprites
{
    public static readonly string[] Frames = ["idle", "blink", "happy", "walk_a", "walk_b"];
    static readonly Dictionary<(int Character, string Frame), BitmapSource> images = [];
    static readonly Dictionary<string, string> hashes = [];
    public static IReadOnlyDictionary<string, string> AssetHashes => hashes;
    public static string ManifestHash { get; }
    public static int Count => images.Count;
    static CompanionSprites()
    {
        var assembly = typeof(CompanionSprites).Assembly;
        byte[] manifest = Read(assembly, "manifest.json");
        ManifestHash = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
        using var document = JsonDocument.Parse(manifest);
        foreach (var item in document.RootElement.GetProperty("frames").EnumerateArray())
        {
            int character = item.GetProperty("character").GetInt32();
            string frame = item.GetProperty("frame").GetString()!;
            string png = item.GetProperty("png").GetString()!;
            byte[] bytes = Read(assembly, png);
            hashes[png] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hashes[png] != item.GetProperty("png_sha256").GetString())
                throw new InvalidDataException("The embedded companion asset differs from its production manifest: " + png);
            using var stream = new MemoryStream(bytes, false);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (bitmap.PixelWidth != 64 || bitmap.PixelHeight != 60) throw new InvalidDataException("Unexpected companion dimensions.");
            bitmap.Freeze(); images.Add((character, frame), bitmap);
        }
        if (images.Count != 60 || Enumerable.Range(0, 12).Any(c => Frames.Any(f => !images.ContainsKey((c, f)))))
            throw new InvalidDataException("The companion sprite pack must contain all 60 production frames.");
    }
    static byte[] Read(Assembly assembly, string file)
    {
        using var stream = assembly.GetManifestResourceStream("DeskBuddy.Companions." + file)
            ?? throw new InvalidDataException("Missing embedded companion asset: " + file);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    public static BitmapSource Get(int character, string frame)
        => images[(Math.Clamp(character, 0, 11), frame)];
}
