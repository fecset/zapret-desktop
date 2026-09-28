using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class FakePayloadService(IZapretDistribution distribution) : IFakePayloadService
{
    private static readonly string[] Targets = ["ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin"];
    public IReadOnlyList<string> GetAvailablePayloads() => Directory.EnumerateFiles(distribution.BinDirectory, "*.bin")
        .Select(Path.GetFileName).OfType<string>()
        .Where(x => !x.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    public async Task ReplaceAsync(string targetName, string sourceName, CancellationToken cancellationToken = default)
    {
        if (!Targets.Contains(targetName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unknown active payload.");
        if (!GetAvailablePayloads().Contains(sourceName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unknown source payload.");
        var source = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.BinDirectory, sourceName));
        var target = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.BinDirectory, targetName));
        await AtomicFiles.WriteBytesAsync(target, await File.ReadAllBytesAsync(source, cancellationToken), cancellationToken);
    }
}
