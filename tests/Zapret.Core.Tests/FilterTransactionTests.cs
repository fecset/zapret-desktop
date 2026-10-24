using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class FilterTransactionTests
{
    [Fact]
    public async Task FailedGameFilterWriteRestoresIpSetAndDoesNotLeaveANewBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret-filter-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "utils"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        var game = Path.Combine(root, "utils", "game_filter.enabled");
        var ipset = Path.Combine(root, "lists", "ipset-all.txt");
        const string originalGame = "mode=disabled\ntcp=443\nudp=444\n";
        const string originalIpSet = "1.2.3.0/24\n";
        try
        {
            await File.WriteAllTextAsync(game, originalGame);
            await File.WriteAllTextAsync(ipset, originalIpSet);
            var filters = new FilterService(new ZapretDistribution(root));
            using (var locked = new FileStream(game, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAnyAsync<IOException>(() => filters.SetFiltersAsync(new(GameFilterMode.All, "4000", "6000"), IpSetMode.None));
            Assert.Equal(originalGame, await File.ReadAllTextAsync(game));
            Assert.Equal(originalIpSet, await File.ReadAllTextAsync(ipset));
            Assert.False(File.Exists(ipset + ".backup"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
