using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Zapret.Core;

namespace Zapret.Infrastructure;

internal sealed class DataProtectionPaths
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly string[] PayloadTargets = ["ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin"];
    internal string DistributionRoot { get; }
    internal string DataRoot { get; }
    internal string BackupsDirectory => Path.Combine(DataRoot, "backups");
    internal string OriginalsDirectory { get; }
    internal SemaphoreSlim Gate { get; }

    internal DataProtectionPaths(IZapretDistribution distribution, string? dataDirectory)
    {
        DistributionRoot = Path.GetFullPath(distribution.Root);
        DataRoot = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretDesktop"));
        if (IsInside(DistributionRoot, DataRoot))
            throw new ArgumentException("User data must be outside the distribution.", nameof(dataDirectory));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DistributionRoot.ToUpperInvariant())));
        OriginalsDirectory = Path.Combine(DataRoot, "payload-originals", identity);
        Gate = Gates.GetOrAdd(DistributionRoot, _ => new SemaphoreSlim(1, 1));
    }

    internal string DistributionFile(string relative) => RequireSafe(DistributionRoot,
        Path.Combine(DistributionRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    internal string OriginalFile(string target) => RequireSafe(DataRoot, Path.Combine(OriginalsDirectory, Target(target)));
    internal string BackupFile(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid backup identifier.", nameof(id));
        return RequireSafe(DataRoot, Path.Combine(BackupsDirectory, id + ".zip"));
    }
    internal static string Target(string value) => PayloadTargets.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Unknown active payload.", nameof(value));

    internal static string RequireSafe(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsInside(root, full) || full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path is outside its managed directory.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked files and directories are not allowed.");
        }
        if (Directory.Exists(full)) throw new InvalidOperationException("Expected a file, found a directory.");
        return full;
    }

    internal static async Task WriteAsync(string root, string path, byte[] bytes, CancellationToken ct)
    {
        RequireSafe(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RequireSafe(root, path);
        await AtomicFiles.WriteBytesAsync(path, bytes, ct);
    }

    private static bool IsInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
