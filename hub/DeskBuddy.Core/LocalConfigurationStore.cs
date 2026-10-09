using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBuddy.Core;

/// <summary>Public configuration only; no credentials, executable actions or device identity.</summary>
public static class LocalConfigurationStore
{
    public const int MaxFileBytes = 16384;
    public static string ExportJson(HubBank bank)
    {
        byte[] encoded = BankCodec.Encode(bank);
        return JsonSerializer.Serialize(new
        {
            format = "desk-buddy-config", version = 1,
            bankHex = Convert.ToHexString(encoded), crc32 = Crc32.Compute(encoded).ToString("X8", CultureInfo.InvariantCulture)
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
    public static HubBank ImportJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new FormatException("Configuration file exceeds 16 KiB.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a configuration object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("format" or "version" or "bankHex" or "crc32"))
                    throw new FormatException("Duplicate or unsupported configuration property.");
            if (names.Count != 4 || root.GetProperty("format").GetString() != "desk-buddy-config" ||
                !root.GetProperty("version").TryGetInt32(out int version) || version != 1)
                throw new FormatException("Unsupported or incomplete local configuration format.");
            var bank = BankCodec.FromHex(root.GetProperty("bankHex").GetString() ?? "");
            string crc = root.GetProperty("crc32").GetString() ?? "";
            if (crc.Length != 8 || !uint.TryParse(crc, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint stored) ||
                Crc32.Compute(BankCodec.Encode(bank)) != stored) throw new FormatException("Configuration checksum mismatch.");
            return bank;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new FormatException("Invalid configuration JSON.", ex); }
    }
    public static HubBank Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new FormatException("Configuration file exceeds 16 KiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
        return ImportJson(reader.ReadToEnd());
    }
    /// <summary>Same-directory staging plus atomic replacement. Failure preserves the prior destination.</summary>
    public static void SaveAtomic(string path, HubBank bank)
    {
        byte[] payload = Encoding.UTF8.GetBytes(ExportJson(bank)); // Validate before touching disk.
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("A local file path is required.", nameof(path));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The configuration folder does not exist.");
        string temporary = Path.Combine(directory, ".desk-buddy-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                created = true; stream.Write(payload); stream.Flush(true);
            }
            // Validate the closed staging file before committing; no delete-then-move window.
            if (BankCodec.ToHex(Load(temporary)) != BankCodec.ToHex(bank)) throw new IOException("Staged configuration verification failed.");
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination); // Fail safely if another writer created it first.
            created = false;
        }
        finally
        {
            if (created) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
