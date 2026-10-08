using System.Security.Cryptography;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class PayloadRecoveryService : IPayloadRecoveryService
{
    private readonly IZapretDistribution distribution;
    private readonly DataProtectionPaths paths;
    public PayloadRecoveryService(IZapretDistribution distribution, string? dataDirectory = null)
    {
        this.distribution = distribution;
        paths = new(distribution, dataDirectory);
    }

    public async Task<PayloadInspection> InspectAsync(string targetName, string? sourceName = null,
        CancellationToken cancellationToken = default)
    {
        var target = DataProtectionPaths.Target(targetName);
        await paths.Gate.WaitAsync(cancellationToken);
        try
        {
            var targetHash = await HashAsync(paths.DistributionFile("bin/" + target), cancellationToken);
            var originalHash = await HashAsync(paths.OriginalFile(target), cancellationToken);
            string? sourceHash = null;
            if (sourceName is not null)
            {
                if (!new FakePayloadService(distribution).GetAvailablePayloads().Contains(sourceName, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Unknown source payload.", nameof(sourceName));
                sourceHash = await HashAsync(SafePaths.RequireDirectFile(distribution.Root,
                    Path.Combine(distribution.BinDirectory, sourceName)), cancellationToken);
            }
            return new(target, sourceName, targetHash, sourceHash, originalHash,
                targetHash is not null && targetHash == originalHash, originalHash is not null);
        }
        finally { paths.Gate.Release(); }
    }

    public async Task RestoreOriginalAsync(string targetName, CancellationToken cancellationToken = default)
    {
        var target = DataProtectionPaths.Target(targetName);
        await paths.Gate.WaitAsync(cancellationToken);
        try
        {
            var original = paths.OriginalFile(target);
            if (!File.Exists(original)) throw new InvalidOperationException("The original payload has not been saved.");
            await DataProtectionPaths.WriteAsync(paths.DistributionRoot, paths.DistributionFile("bin/" + target),
                await File.ReadAllBytesAsync(original, cancellationToken), cancellationToken);
        }
        finally { paths.Gate.Release(); }
    }

    internal static async Task PreserveOriginalAsync(DataProtectionPaths paths, string target, CancellationToken ct)
    {
        var original = paths.OriginalFile(target);
        if (File.Exists(original)) return;
        var current = paths.DistributionFile("bin/" + DataProtectionPaths.Target(target));
        if (!File.Exists(current)) return;
        await DataProtectionPaths.WriteAsync(paths.DataRoot, original, await File.ReadAllBytesAsync(current, ct), ct);
    }

    private static async Task<string?> HashAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
