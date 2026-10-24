using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class UserDataBackupService : IUserDataBackupService
{
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxArchiveBytes = 64 * 1024 * 1024;
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxAutomaticBackups = 10;
    private const int MaxAutomaticIndexBytes = 64 * 1024;
    private static readonly string[] ManagedFiles = [
        "lists/list-general.txt", "lists/list-general-user.txt", "lists/list-exclude.txt", "lists/list-exclude-user.txt",
        "lists/list-google.txt", "lists/list-custom.txt", "lists/ipset-exclude.txt", "lists/ipset-exclude-user.txt",
        "lists/ipset-custom.txt", "lists/ipset-all.txt", "lists/ipset-all.txt.backup", "utils/game_filter.enabled",
        "bin/ACTIVE_DISCORD_UDP.bin", "bin/ACTIVE_GAME_UDP.bin"];
    private static readonly string[] OriginalFiles = DataProtectionPaths.PayloadTargets.Select(x => "originals/" + x).ToArray();
    private static readonly HashSet<string> AllowedEntries = new(ManagedFiles.Concat(OriginalFiles).Append("settings.json").Append("manifest.json"), StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly ISettingsStore settingsStore;
    private readonly DataProtectionPaths paths;

    public UserDataBackupService(IZapretDistribution distribution, ISettingsStore settingsStore, string? dataDirectory = null)
    {
        this.settingsStore = settingsStore;
        paths = new(distribution, dataDirectory);
    }

    public async Task<UserDataBackupInfo> CreateAsync(string reason = "manual", CancellationToken cancellationToken = default)
    {
        ValidateReason(reason);
        await paths.Gate.WaitAsync(cancellationToken);
        try
        {
            return (await SaveBackupAsync(await CaptureAsync(cancellationToken), reason, cancellationToken)).Info;
        }
        finally { paths.Gate.Release(); }
    }

    public async Task<UserDataBackupInfo> CreateAutomaticAsync(string reason, CancellationToken cancellationToken = default)
    {
        ValidateReason(reason);
        await paths.Gate.WaitAsync(cancellationToken);
        try { return await SaveAutomaticBackupAsync(await CaptureAsync(cancellationToken), reason, cancellationToken); }
        finally { paths.Gate.Release(); }
    }

    public async Task<IReadOnlyList<UserDataBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        await paths.Gate.WaitAsync(cancellationToken);
        try
        {
            var result = new List<UserDataBackupInfo>();
            paths.BackupFile(Guid.Empty.ToString("N"));
            if (!Directory.Exists(paths.BackupsDirectory)) return result;
            foreach (var file in Directory.EnumerateFiles(paths.BackupsDirectory, "*.zip", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                try
                {
                    await using var stream = File.OpenRead(paths.BackupFile(id));
                    var snapshot = await ReadArchiveAsync(stream, cancellationToken);
                    result.Add(new(id, snapshot.Manifest.CreatedAt, snapshot.Manifest.Reason, snapshot.Files.Count, stream.Length));
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException) { }
            }
            return result.OrderByDescending(x => x.CreatedAt).ToArray();
        }
        finally { paths.Gate.Release(); }
    }

    public async Task ExportAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination is not writable.", nameof(destination));
        await paths.Gate.WaitAsync(cancellationToken);
        try { await WriteArchiveAsync(destination, await CaptureAsync(cancellationToken), "export", cancellationToken); }
        finally { paths.Gate.Release(); }
    }

    public async Task ImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = await ReadArchiveAsync(source, cancellationToken);
        await paths.Gate.WaitAsync(cancellationToken);
        try { await ApplyWithBackupAsync(snapshot.Files, "before import", cancellationToken); }
        finally { paths.Gate.Release(); }
    }

    public async Task RestoreAsync(string id, CancellationToken cancellationToken = default)
    {
        var file = paths.BackupFile(id);
        await paths.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(file);
            var snapshot = await ReadArchiveAsync(stream, cancellationToken);
            await ApplyWithBackupAsync(snapshot.Files, "before restore", cancellationToken, id);
        }
        finally { paths.Gate.Release(); }
    }

    private async Task<Dictionary<string, byte[]>> CaptureAsync(CancellationToken ct)
    {
        DataProtectionPaths.RequireSafe(paths.DataRoot, Path.Combine(paths.DataRoot, "settings.json"));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(await settingsStore.LoadAsync(ct), JsonOptions)
        };
        long total = files["settings.json"].Length;
        foreach (var name in ManagedFiles.Concat(OriginalFiles))
        {
            var file = ResolveFile(name);
            if (!File.Exists(file)) continue;
            var bytes = await ReadLimitedAsync(File.OpenRead(file), MaxFileBytes, ct, dispose: true);
            total += bytes.Length;
            if (total > MaxArchiveBytes) throw new InvalidDataException("User data exceeds the archive size limit.");
            files.Add(name, bytes);
        }
        return files;
    }

    private async Task<SavedBackup> SaveBackupAsync(Dictionary<string, byte[]> files, string reason, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var file = paths.BackupFile(id);
        using var archive = new MemoryStream();
        var manifest = await WriteArchiveAsync(archive, files, reason, ct);
        var bytes = archive.ToArray();
        await DataProtectionPaths.WriteAsync(paths.DataRoot, file, bytes, ct);
        return new(new(id, manifest.CreatedAt, reason, files.Count, archive.Length), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private async Task<UserDataBackupInfo> SaveAutomaticBackupAsync(Dictionary<string, byte[]> files, string reason,
        CancellationToken ct, string? protectedBackupId = null)
    {
        // A recovery snapshot must pass the same validation required to restore it.
        ValidateSettings(files["settings.json"]);
        if (files.TryGetValue("utils/game_filter.enabled", out var filter)) ValidateGameFilter(filter);
        var owned = new List<AutomaticBackupEntry>();
        UserDataBackupInfo? matching = null;
        foreach (var entry in await ReadAutomaticIndexAsync(ct))
        {
            try
            {
                var backup = await ReadAutomaticBackupAsync(entry, ct);
                owned.Add(entry);
                if (SnapshotsEqual(files, backup.Snapshot.Files)) matching = backup.Info;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        if (matching is null)
        {
            var saved = await SaveBackupAsync(files, reason, ct);
            matching = saved.Info;
            owned.Add(new(matching.Id, saved.ArchiveSha256));
        }
        await WriteAutomaticIndexAsync(owned, ct);
        await PruneAutomaticBackupsAsync(owned, matching.Id, protectedBackupId, ct);
        return matching;
    }

    private string AutomaticIndexFile => DataProtectionPaths.RequireSafe(paths.DataRoot,
        Path.Combine(paths.BackupsDirectory, "automatic-backups.json"));

    private async Task<IReadOnlyList<AutomaticBackupEntry>> ReadAutomaticIndexAsync(CancellationToken ct)
    {
        try
        {
            var file = AutomaticIndexFile;
            if (!File.Exists(file)) return [];
            var bytes = await ReadLimitedAsync(File.OpenRead(file), MaxAutomaticIndexBytes, ct, dispose: true);
            var index = JsonSerializer.Deserialize<AutomaticBackupIndex>(bytes, JsonOptions);
            if (index is not { Version: 1, Backups: not null }) return [];
            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in index.Backups)
            {
                if (entry is null || !Guid.TryParseExact(entry.Id, "N", out _) || !identifiers.Add(entry.Id) ||
                    entry.ArchiveSha256 is not { Length: 64 } || !entry.ArchiveSha256.All(char.IsAsciiHexDigit)) return [];
            }
            return index.Backups;
        }
        // Missing or damaged ownership metadata must never adopt legacy/manual archives.
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return []; }
    }

    private Task WriteAutomaticIndexAsync(List<AutomaticBackupEntry> backups, CancellationToken ct) =>
        DataProtectionPaths.WriteAsync(paths.DataRoot, AutomaticIndexFile,
            JsonSerializer.SerializeToUtf8Bytes(new AutomaticBackupIndex(1, backups), JsonOptions), ct);

    private async Task<AutomaticBackupSnapshot> ReadAutomaticBackupAsync(AutomaticBackupEntry entry, CancellationToken ct)
    {
        var bytes = await ReadLimitedAsync(File.OpenRead(paths.BackupFile(entry.Id)), MaxArchiveBytes, ct, dispose: true);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Automatic backup ownership checksum does not match.");
        using var stream = new MemoryStream(bytes);
        var snapshot = await ReadArchiveAsync(stream, ct);
        return new(new(entry.Id, snapshot.Manifest.CreatedAt, snapshot.Manifest.Reason, snapshot.Files.Count, bytes.Length), snapshot);
    }

    private async Task ApplyWithBackupAsync(Dictionary<string, byte[]> files, string reason, CancellationToken ct, string? protectedBackupId = null)
    {
        // Resolve every destination before any mutation, including files absent from the snapshot.
        foreach (var name in ManagedFiles.Concat(OriginalFiles)) ResolveFile(name);
        DataProtectionPaths.RequireSafe(paths.DataRoot, Path.Combine(paths.DataRoot, "settings.json"));
        var before = await CaptureAsync(ct);
        if (!HasEffectiveChanges(before, files)) return;
        await SaveAutomaticBackupAsync(before, reason, ct, protectedBackupId);
        var changed = new List<string>();
        var settingsChanged = false;
        try
        {
            foreach (var target in DataProtectionPaths.PayloadTargets)
            {
                var name = "originals/" + target;
                if (File.Exists(paths.OriginalFile(target))) continue;
                if (files.TryGetValue(name, out var original))
                    await DataProtectionPaths.WriteAsync(paths.DataRoot, paths.OriginalFile(target), original, ct);
                else await PayloadRecoveryService.PreserveOriginalAsync(paths, target, ct);
                if (File.Exists(paths.OriginalFile(target))) changed.Add(name);
            }
            foreach (var name in ManagedFiles)
            {
                before.TryGetValue(name, out var old);
                files.TryGetValue(name, out var value);
                if (old is null && value is null || old is not null && value is not null && old.AsSpan().SequenceEqual(value)) continue;
                await ApplyFileAsync(name, value, ct);
                changed.Add(name);
            }
            settingsChanged = true;
            await settingsStore.SaveAsync(ValidateSettings(files["settings.json"]), ct);
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var name in changed.AsEnumerable().Reverse())
            {
                try { await ApplyFileAsync(name, before.GetValueOrDefault(name), CancellationToken.None); }
                catch (Exception ex) { rollbackFailures.Add(ex); }
            }
            if (settingsChanged)
            {
                try { await settingsStore.SaveAsync(ValidateSettings(before["settings.json"]), CancellationToken.None); }
                catch (Exception ex) { rollbackFailures.Add(ex); }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException("Restore failed; the pre-operation backup is available for recovery.", new[] { failure }.Concat(rollbackFailures));
            throw;
        }
    }

    private static bool SnapshotsEqual(Dictionary<string, byte[]> first, Dictionary<string, byte[]> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var bytes) && pair.Value.AsSpan().SequenceEqual(bytes));

    private static bool HasEffectiveChanges(Dictionary<string, byte[]> before, Dictionary<string, byte[]> files)
    {
        foreach (var name in ManagedFiles)
        {
            before.TryGetValue(name, out var old);
            files.TryGetValue(name, out var value);
            if (old is null ? value is not null : value is null || !old.AsSpan().SequenceEqual(value)) return true;
        }
        if (OriginalFiles.Any(name => !before.ContainsKey(name) && files.ContainsKey(name))) return true;
        var settings = JsonSerializer.SerializeToUtf8Bytes(ValidateSettings(files["settings.json"]), JsonOptions);
        return !before["settings.json"].AsSpan().SequenceEqual(settings);
    }

    private async Task PruneAutomaticBackupsAsync(List<AutomaticBackupEntry> backups, string currentId,
        string? protectedId, CancellationToken ct)
    {
        try
        {
            var excess = backups.Count - MaxAutomaticBackups;
            foreach (var entry in backups.ToArray())
            {
                if (excess <= 0 || ct.IsCancellationRequested) break;
                if (entry.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase) ||
                    entry.Id.Equals(protectedId, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // Revalidate the exact indexed archive before deleting it; a replaced copy is protected.
                    await ReadAutomaticBackupAsync(entry, ct);
                    File.Delete(paths.BackupFile(entry.Id));
                    backups.Remove(entry);
                    excess--;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            await WriteAutomaticIndexAsync(backups, ct);
        }
        // Retention must not turn a completed save/restore into a reported failure.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException) { }
    }

    private async Task ApplyFileAsync(string name, byte[]? bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var file = ResolveFile(name);
        if (bytes is null) { if (File.Exists(file)) File.Delete(file); }
        else await DataProtectionPaths.WriteAsync(name.StartsWith("originals/", StringComparison.Ordinal) ? paths.DataRoot : paths.DistributionRoot, file, bytes, ct);
    }

    private string ResolveFile(string name) => name.StartsWith("originals/", StringComparison.Ordinal)
        ? paths.OriginalFile(name["originals/".Length..]) : paths.DistributionFile(name);

    private static async Task<ArchiveManifest> WriteArchiveAsync(Stream destination, Dictionary<string, byte[]> files, string reason, CancellationToken ct)
    {
        var manifest = new ArchiveManifest(1, DateTimeOffset.UtcNow, reason,
            files.ToDictionary(x => x.Key, x => new ArchiveFile(x.Value.Length, Convert.ToHexString(SHA256.HashData(x.Value))), StringComparer.Ordinal));
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var pair in files.Append(new("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions))))
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = archive.CreateEntry(pair.Key, CompressionLevel.Optimal).Open();
            await stream.WriteAsync(pair.Value, ct);
        }
        return manifest;
    }

    private static async Task<ArchiveSnapshot> ReadArchiveAsync(Stream source, CancellationToken ct)
    {
        if (!source.CanRead) throw new ArgumentException("The source is not readable.", nameof(source));
        using var buffer = new MemoryStream(await ReadLimitedAsync(source, MaxArchiveBytes, ct));
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        if (archive.Entries.Count > AllowedEntries.Count) throw new InvalidDataException("Too many archive entries.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!AllowedEntries.Contains(entry.FullName) || !names.Add(entry.FullName) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unknown, duplicated, or linked archive entry.");
            var limit = entry.FullName == "manifest.json" ? MaxManifestBytes : MaxFileBytes;
            total += entry.Length;
            if (entry.Length > limit || total > MaxArchiveBytes) throw new InvalidDataException("Archive size limit exceeded.");
            await using var stream = entry.Open();
            var bytes = await ReadLimitedAsync(stream, limit, ct);
            if (bytes.LongLength != entry.Length) throw new InvalidDataException("Archive entry length does not match its metadata.");
            files.Add(entry.FullName, bytes);
        }
        if (!files.Remove("manifest.json", out var metadata) || !files.ContainsKey("settings.json"))
            throw new InvalidDataException("The archive is missing its manifest or settings.");
        ArchiveManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ArchiveManifest>(metadata, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid backup manifest.", ex); }
        if (manifest.Version != 1 || manifest.Files is null || manifest.Files.Count != files.Count)
            throw new InvalidDataException("Unsupported or incomplete backup manifest.");
        ValidateReason(manifest.Reason, archive: true);
        foreach (var pair in files)
        {
            if (!manifest.Files.TryGetValue(pair.Key, out var info) || info is null || info.Length != pair.Value.Length ||
                !string.Equals(info.Sha256, Convert.ToHexString(SHA256.HashData(pair.Value)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive checksum does not match its manifest.");
        }
        ValidateSettings(files["settings.json"]);
        if (files.TryGetValue("utils/game_filter.enabled", out var filter)) ValidateGameFilter(filter);
        return new(manifest, files);
    }

    private static void ValidateGameFilter(byte[] bytes)
    {
        if (bytes.Length > 4096) throw new InvalidDataException("Game filter settings are too large.");
        string content;
        try { content = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid game filter encoding.", ex); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 1)
            {
                if (!names.Add("mode") || !ValidMode(parts[0])) throw new InvalidDataException("Invalid game filter mode.");
                continue;
            }
            var name = parts[0].Trim();
            var value = parts[1].Trim();
            if (!names.Add(name) || (name.Equals("mode", StringComparison.OrdinalIgnoreCase)
                ? !ValidMode(value)
                : !(name.Equals("tcp", StringComparison.OrdinalIgnoreCase) || name.Equals("udp", StringComparison.OrdinalIgnoreCase)) || !FilterService.ValidPorts(value)))
                throw new InvalidDataException("Invalid game filter settings.");
        }
    }

    private static bool ValidMode(string value) => new[] { "disabled", "all", "tcp", "udp" }.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static DesktopSettings ValidateSettings(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                switch (property.Name)
                {
                    case nameof(DesktopSettings.SelectedStrategyId):
                        if (property.Value.ValueKind != JsonValueKind.Null && (property.Value.ValueKind != JsonValueKind.String ||
                            property.Value.GetString() is not { Length: <= 128 } id || id.Any(char.IsControl))) throw new JsonException();
                        break;
                    case nameof(DesktopSettings.Theme):
                        if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not ("System" or "Dark" or "Light")) throw new JsonException();
                        break;
                    case nameof(DesktopSettings.LogLevel):
                        if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical" or "None")) throw new JsonException();
                        break;
                    case nameof(DesktopSettings.StartWithWindows):
                    case nameof(DesktopSettings.StartZapretOnLaunch):
                    case nameof(DesktopSettings.MinimizeToTray):
                    case nameof(DesktopSettings.CheckUpdates):
                    case nameof(DesktopSettings.CheckConnectionAfterStart):
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
                        break;
                    default: throw new JsonException();
                }
            }
            return JsonSerializer.Deserialize<DesktopSettings>(bytes, JsonOptions) ?? throw new JsonException();
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid desktop settings in the archive.", ex); }
    }

    private static void ValidateReason(string reason, bool archive = false)
    {
        if (!string.IsNullOrWhiteSpace(reason) && reason.Length <= 120 && !reason.Any(char.IsControl)) return;
        if (archive) throw new InvalidDataException("Invalid backup reason.");
        throw new ArgumentException("Backup reason must contain 1–120 characters without control characters.", nameof(reason));
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream source, int limit, CancellationToken ct, bool dispose = false)
    {
        try
        {
            using var result = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (result.Length + count > limit) throw new InvalidDataException("Archive size limit exceeded.");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
        finally { if (dispose) await source.DisposeAsync(); }
    }

    private sealed record ArchiveFile(long Length, string Sha256);
    private sealed record ArchiveManifest(int Version, DateTimeOffset CreatedAt, string Reason, Dictionary<string, ArchiveFile> Files);
    private sealed record ArchiveSnapshot(ArchiveManifest Manifest, Dictionary<string, byte[]> Files);
    private sealed record SavedBackup(UserDataBackupInfo Info, string ArchiveSha256);
    private sealed record AutomaticBackupEntry(string Id, string ArchiveSha256);
    private sealed record AutomaticBackupIndex(int Version, List<AutomaticBackupEntry> Backups);
    private sealed record AutomaticBackupSnapshot(UserDataBackupInfo Info, ArchiveSnapshot Snapshot);
}
