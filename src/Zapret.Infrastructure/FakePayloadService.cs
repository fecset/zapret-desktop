using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class FakePayloadService(IZapretDistribution distribution, string? dataDirectory = null) : IFakePayloadService
{
    private readonly DataProtectionPaths paths = new(distribution, dataDirectory);
    public IReadOnlyList<string> GetAvailablePayloads() => !Directory.Exists(distribution.BinDirectory) ? [] : Directory.EnumerateFiles(distribution.BinDirectory, "*.bin")
        .Select(Path.GetFileName).OfType<string>()
        .Where(x => !x.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    public async Task ReplaceAsync(string targetName, string sourceName, CancellationToken cancellationToken = default)
    {
        targetName = DataProtectionPaths.Target(targetName);
        if (!GetAvailablePayloads().Contains(sourceName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unknown source payload.");
        await paths.Gate.WaitAsync(cancellationToken);
        try
        {
            var source = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.BinDirectory, sourceName));
            var target = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.BinDirectory, targetName));
            var bytes = await File.ReadAllBytesAsync(source, cancellationToken);
            await PayloadRecoveryService.PreserveOriginalAsync(paths, targetName, cancellationToken);
            await AtomicFiles.WriteBytesAsync(target, bytes, cancellationToken);
        }
        finally { paths.Gate.Release(); }
    }
}
