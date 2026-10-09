using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBuddy.Core;

public sealed record DeviceWorkspaceSnapshot(HubBank Bank, DateTimeOffset ObservedAt);
public sealed record HubWorkspaceState(DeviceWorkspaceSnapshot? LastDevice, HubBank? Draft);
public sealed record WorkspaceLoadResult(HubWorkspaceState State, string Source, string? Warning);

/// <summary>Local configuration snapshots only: no device identity, connection, capabilities or output state.</summary>
public static class HubWorkspaceStore
{
    public const int MaxFileBytes = 65536;
    private const string Format = "desk-buddy-workspace";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static WorkspaceLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string destination = Path.GetFullPath(path);
        var primary = TryLoad(destination);
        if (primary.State is not null)
            return new(primary.State, "primary", primary.Legacy ? "Imported an older configuration file as a local draft only." : null);
        var backup = TryLoad(destination + ".bak");
        if (backup.State is not null)
            return new(backup.State, "backup", "Recovered the local workspace backup; it does not confirm current device settings." +
                (backup.Legacy ? " The older configuration was imported as a draft only." : ""));
        if (primary.Missing && backup.Missing) return new(new(null, null), "missing", null);
        return new(new(null, null), "invalid", "The local workspace and backup could not be read or validated. Both files were left unchanged.");
    }

    public static void SaveAtomic(string path, HubWorkspaceState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        // Capture and validate mutable bank arrays before any filesystem operation.
        string json = Export(state);
        byte[] bytes = Utf8.GetBytes(json);
        if (bytes.Length > MaxFileBytes) throw new FormatException("Workspace exceeds 64 KiB.");
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("A local file path is required.", nameof(path));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The workspace folder does not exist.");
        // All writers using this store serialize across processes. This empty lock
        // file holds no configuration and may remain after closing the handle.
        using var writerLock = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        bool exists = false, valid = false;
        try { _ = Read(destination); exists = valid = true; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (FormatException) { exists = true; }
        // IO/sharing/access failures deliberately propagate instead of being
        // mistaken for a corrupt destination that may be replaced.
        string staging = Path.Combine(directory, ".desk-workspace-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { created = true; stream.Write(bytes); stream.Flush(true); }
            if (Export(Read(staging).State) != json) throw new IOException("Staged workspace verification failed.");
            // Never replace a healthy backup with a corrupt primary recovered
            // by Load(). If primary is invalid, atomically replace it alone.
            if (exists) File.Replace(staging, destination, valid ? destination + ".bak" : null);
            else File.Move(staging, destination);
            created = false;
        }
        finally
        {
            if (created) try { File.Delete(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static (HubWorkspaceState? State, bool Missing, bool Legacy) TryLoad(string path)
    {
        try { var result = Read(path); return (result.State, false, result.Legacy); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return (null, true, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return (null, false, false); }
    }

    private static (HubWorkspaceState State, bool Legacy) Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new FormatException("Workspace exceeds 64 KiB.");
        byte[] bytes = new byte[MaxFileBytes + 1];
        int count = 0, read;
        while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) != 0) count += read;
        if (count > MaxFileBytes) throw new FormatException("Workspace exceeds 64 KiB.");
        try { return Import(Utf8.GetString(bytes, 0, count)); }
        catch (DecoderFallbackException ex) { throw new FormatException("Workspace is not valid UTF-8.", ex); }
    }

    private static string Export(HubWorkspaceState state)
    {
        object? last = null;
        if (state.LastDevice is { } snapshot)
        {
            var bank = BankFields(snapshot.Bank);
            last = new { observedAt = snapshot.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), bankHex = bank.Hex, crc32 = bank.Crc };
        }
        object? draft = null;
        if (state.Draft is { } draftBank)
        {
            var bank = BankFields(draftBank); draft = new { bankHex = bank.Hex, crc32 = bank.Crc };
        }
        return JsonSerializer.Serialize(new { format = Format, version = 1, lastDevice = last, draft }, JsonOptions) + "\n";
    }

    private static (string Hex, string Crc) BankFields(HubBank bank)
    {
        byte[] encoded = BankCodec.Encode(bank);
        return (Convert.ToHexString(encoded), Crc32.Compute(encoded).ToString("X8", CultureInfo.InvariantCulture));
    }

    private static (HubWorkspaceState State, bool Legacy) Import(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a workspace object.");
            // Legacy import performs its own duplicate/unknown/type/CRC checks.
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == "desk-buddy-config")
                return (new(null, LocalConfigurationStore.ImportJson(json)), true);
            Fields(root, "format", "version", "lastDevice", "draft");
            if (root.GetProperty("format").GetString() != Format || !root.GetProperty("version").TryGetInt32(out int version) || version != 1)
                throw new FormatException("Unsupported workspace format.");
            DeviceWorkspaceSnapshot? last = null;
            var snapshot = root.GetProperty("lastDevice");
            if (snapshot.ValueKind != JsonValueKind.Null)
            {
                Fields(snapshot, "observedAt", "bankHex", "crc32");
                string? date = snapshot.GetProperty("observedAt").GetString();
                if (!DateTimeOffset.TryParseExact(date, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var observed))
                    throw new FormatException("Invalid workspace observation time.");
                last = new(ReadBank(snapshot), observed);
            }
            HubBank? draft = null;
            var candidate = root.GetProperty("draft");
            if (candidate.ValueKind != JsonValueKind.Null)
            { Fields(candidate, "bankHex", "crc32"); draft = ReadBank(candidate); }
            return (new(last, draft), false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new FormatException("Invalid workspace JSON.", ex); }
    }

    private static void Fields(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw new FormatException("Duplicate or unsupported workspace property.");
        if (seen.Count != allowed.Length) throw new FormatException("Incomplete workspace object.");
    }

    private static HubBank ReadBank(JsonElement element)
    {
        var bank = BankCodec.FromHex(element.GetProperty("bankHex").GetString() ?? "");
        string crc = element.GetProperty("crc32").GetString() ?? "";
        if (crc.Length != 8 || !uint.TryParse(crc, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint stored) ||
            Crc32.Compute(BankCodec.Encode(bank)) != stored) throw new FormatException("Workspace bank checksum mismatch.");
        return bank;
    }
}
