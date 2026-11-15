using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class FilterTransactionTests
{
    [Theory]
    [InlineData("", IpSetMode.Any)]
    [InlineData("\uFEFF  \r\n# 203.0.113.113/32 is a placeholder\n", IpSetMode.Any)]
    [InlineData("203.0.113.113/32\n", IpSetMode.None)]
    [InlineData("\uFEFF # comment\r\n 203.0.113.113/32 # disabled\r\n", IpSetMode.None)]
    [InlineData("203.0.113.113/32\n1.2.3.0/24\n", IpSetMode.Loaded)]
    [InlineData("# 203.0.113.113/32\n1.2.3.0/24\n", IpSetMode.Loaded)]
    public async Task IpSetModeUsesEffectiveEntriesRatherThanSentinelSubstrings(string content, IpSetMode expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret-ipset-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "lists", "ipset-all.txt"), content);
            Assert.Equal(expected, await new FilterService(new ZapretDistribution(root)).GetIpSetModeAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("203.0.113.113/32\n1.2.3.0/24\n")]
    [InlineData("# 203.0.113.113/32\n1.2.3.0/24\n")]
    public async Task SwitchingMixedIpSetToAnyAndBackPreservesAllAddresses(string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret-ipset-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        var file = Path.Combine(root, "lists", "ipset-all.txt");
        try
        {
            await File.WriteAllTextAsync(file, content);
            var filters = new FilterService(new ZapretDistribution(root));
            await filters.SetIpSetModeAsync(IpSetMode.Any);
            Assert.Equal(content, await File.ReadAllTextAsync(file + ".backup"));
            Assert.Equal(IpSetMode.Any, await filters.GetIpSetModeAsync());
            await filters.SetIpSetModeAsync(IpSetMode.Loaded);
            Assert.Equal(content, await File.ReadAllTextAsync(file));
            Assert.Equal(IpSetMode.Loaded, await filters.GetIpSetModeAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
