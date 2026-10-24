using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class FilterService(IZapretDistribution distribution) : IFilterService
{
    private string GameFile => Path.Combine(distribution.Root, "utils", "game_filter.enabled");
    private string IpSetFile => Path.Combine(distribution.ListsDirectory, "ipset-all.txt");
    private string BackupFile => IpSetFile + ".backup";

    public async Task<GameFilterSettings> GetGameFilterAsync(CancellationToken cancellationToken = default)
    {
        var mode = GameFilterMode.Disabled;
        var tcp = "1024-65535";
        var udp = "1024-65535";
        if (!File.Exists(GameFile)) return new(mode, tcp, udp);
        foreach (var line in await File.ReadAllLinesAsync(GameFile, cancellationToken))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 1)
            {
                if (Enum.TryParse<GameFilterMode>(parts[0], true, out var legacy)) mode = legacy;
                continue;
            }
            switch (parts[0].ToLowerInvariant())
            {
                case "mode": if (Enum.TryParse<GameFilterMode>(parts[1], true, out var parsed)) mode = parsed; break;
                case "tcp": if (ValidPorts(parts[1])) tcp = parts[1]; break;
                case "udp": if (ValidPorts(parts[1])) udp = parts[1]; break;
            }
        }
        return new(mode, tcp, udp);
    }
    public Task SetGameFilterAsync(GameFilterSettings value, CancellationToken cancellationToken = default)
    {
        if (!ValidPorts(value.TcpPorts) || !ValidPorts(value.UdpPorts)) throw new ArgumentException("Invalid port range.");
        return AtomicFiles.WriteTextAsync(GameFile,
            $"mode={value.Mode.ToString().ToLowerInvariant()}\ntcp={value.TcpPorts}\nudp={value.UdpPorts}\n", cancellationToken);
    }
    public static bool ValidPorts(string value) => FilterRules.ValidPorts(value);

    public async Task SetFiltersAsync(GameFilterSettings game, IpSetMode mode, CancellationToken cancellationToken = default)
    {
        FilterRules.Validate(game, mode);
        var before = new Dictionary<string, byte[]?>();
        foreach (var file in new[] { GameFile, IpSetFile, BackupFile })
        {
            DataProtectionPaths.RequireSafe(distribution.Root, file);
            before[file] = File.Exists(file) ? await File.ReadAllBytesAsync(file, cancellationToken) : null;
        }
        try
        {
            await SetIpSetModeAsync(mode, cancellationToken);
            await SetGameFilterAsync(game, cancellationToken);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var (file, bytes) in before)
            {
                try
                {
                    DataProtectionPaths.RequireSafe(distribution.Root, file);
                    if (bytes is null) { if (File.Exists(file)) File.Delete(file); }
                    else if (!File.Exists(file) || !(await File.ReadAllBytesAsync(file)).AsSpan().SequenceEqual(bytes))
                        await AtomicFiles.WriteBytesAsync(file, bytes);
                }
                catch (Exception rollback) { failures.Add(rollback); }
            }
            if (failures.Count > 1) throw new AggregateException("Не удалось полностью вернуть прежние фильтры. Используйте резервную копию.", failures);
            throw;
        }
    }
    public async Task<IpSetMode> GetIpSetModeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(IpSetFile)) return IpSetMode.None;
        var text = await File.ReadAllTextAsync(IpSetFile, cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) return IpSetMode.Any;
        return text.Contains("203.0.113.113/32", StringComparison.Ordinal) ? IpSetMode.None : IpSetMode.Loaded;
    }
    public async Task SetIpSetModeAsync(IpSetMode mode, CancellationToken cancellationToken = default)
    {
        var current = await GetIpSetModeAsync(cancellationToken);
        if (current == mode) return;
        if (current == IpSetMode.Loaded)
        {
            var original = await File.ReadAllTextAsync(IpSetFile, cancellationToken);
            await AtomicFiles.WriteTextAsync(BackupFile, original, cancellationToken);
        }
        if (mode == IpSetMode.Loaded)
        {
            if (!File.Exists(BackupFile)) throw new InvalidOperationException("IPSet backup is missing; update the upstream list first.");
            await AtomicFiles.WriteTextAsync(IpSetFile, await File.ReadAllTextAsync(BackupFile, cancellationToken), cancellationToken);
        }
        else await AtomicFiles.WriteTextAsync(IpSetFile, mode == IpSetMode.None ? "203.0.113.113/32\n" : "", cancellationToken);
    }
}
