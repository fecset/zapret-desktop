using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class RemainingIssuesTests
{
    [Theory]
    [InlineData("Zapret & Desktop")]
    [InlineData("Zapret % Desktop")]
    [InlineData("Zapret ^ Desktop")]
    public async Task SubstitutedPathsPreserveSpecialCharactersAndArgumentBoundaries(string directory)
    {
        var root = Path.Combine(Path.GetTempPath(), directory + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        var bat = Path.Combine(root, "general.bat");
        var list = Path.Combine(root, "lists", "list-general.txt");
        try
        {
            await File.WriteAllTextAsync(list, "example.com");
            await File.WriteAllTextAsync(bat, "start \"test\" /min \"%BIN%winws.exe\" --wf-tcp=80 --hostlist=\"%LISTS%list-general.txt\"");
            var strategies = await new BatStrategyProvider(new ZapretDistribution(root)).GetStrategiesAsync();
            var strategy = Assert.Single(strategies);
            Assert.Equal(["--wf-tcp=80", "--hostlist=" + list], strategy.Arguments);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--wf-tcp=80 & echo bad")]
    [InlineData("--wf-tcp=%UNKNOWN%")]
    [InlineData("--wf-tcp=80 | echo bad")]
    public async Task UnsupportedBatchCommandsAreStillRejected(string arguments)
    {
        var root = Path.Combine(Path.GetTempPath(), "ZapretParser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bat = Path.Combine(root, "general.bat");
        try
        {
            await File.WriteAllTextAsync(bat, "start \"test\" /min \"%BIN%winws.exe\" " + arguments);
            Assert.Throws<FormatException>(() => BatStrategyParser.Parse(File.ReadAllText(bat), root, bat,
                new(GameFilterMode.Disabled, "1024-65535", "1024-65535")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingDistributionIsReportedWithoutThrowing()
    {
        var distribution = new ZapretDistribution(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Contains("general*.bat", distribution.Validate());
        var provider = new BatStrategyProvider(distribution);
        Assert.Empty(await provider.GetStrategiesAsync());
        Assert.NotEmpty(provider.Errors);
    }
}
