using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public class IntegrationTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../zapret"));

    [Fact]
    public async Task ReadsServiceStatusWithoutParsingLocalizedScOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("Running", await ScServiceManager.QueryStatusAsync("BFE"));
        Assert.Equal("NotInstalled", await ScServiceManager.QueryStatusAsync("zapret-test-nonexistent-service"));
    }

    [Fact]
    public async Task ScOutputUsesAnAvailableOemCodePage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var run = typeof(ScServiceManager).GetMethod("RunScAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(run);
        var command = (Task<(int Code, string Output)>)run.Invoke(null, [new[] { "query", "BFE" }, CancellationToken.None])!;
        var result = await command;
        Assert.Equal(0, result.Code);
        Assert.Contains("BFE", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrayExitDoesNotStopWindowsService()
    {
        var service = new GuardServiceManager();
        var process = new ZapretProcessManager(new ZapretDistribution(Root), service);
        Assert.False(await process.StopOwnedAsync());
        Assert.False(service.StopCalled);
    }

    [Fact]
    public async Task RemovesTheExactLineSelectedInAList()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "lists");
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "list-general.txt");
            await File.WriteAllTextAsync(file, "# header\r\n^example.com # note\r\nkeep.example\r\n");
            var lists = new ListService(new ZapretDistribution(root));
            await lists.RemoveEntryAsync("list-general.txt", "^example.com # note");
            Assert.Equal("# header\r\nkeep.example\r\n", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class GuardServiceManager : IServiceManager
    {
        public bool StopCalled { get; private set; }
        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult("Running");
        public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateStrategyAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCalled = true; return Task.CompletedTask; }
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void CancelledTestRecommendsOnlyCompletedStrategies()
    {
        var tracker = new CompletedStrategyTracker();
        tracker.Observe("  [1/3] general.bat");
        tracker.Observe("Discord HTTP:OK TLS1.2:ERROR TLS1.3:OK | Ping: 17 ms");
        tracker.Observe("  [2/3] general (ALT).bat");
        Assert.Equal("general.bat", tracker.BestStrategy);
        tracker.Observe("Discord HTTP:OK TLS1.2:OK TLS1.3:OK | Ping: 12 ms");
        Assert.Equal("general.bat", tracker.BestStrategy);
        tracker.Observe("  [3/3] general (ALT2).bat");
        Assert.Equal("general (ALT).bat", tracker.BestStrategy);
        Assert.Equal(2, tracker.CompletedCount);
        tracker.Observe("  > Strategy failed to start (winws process not found). Skipping...");
        tracker.Observe("All tests finished.");
        Assert.Equal(2, tracker.CompletedCount);
    }

    [Fact]
    public async Task DiscoversAndParsesEveryBundledStrategy()
    {
        var provider = new BatStrategyProvider(new ZapretDistribution(Root));
        var strategies = await provider.GetStrategiesAsync();
        Assert.Equal(22, strategies.Count);
        Assert.All(strategies, x => Assert.StartsWith("--wf-tcp=", x.Arguments[0]));
        Assert.All(strategies, x => Assert.True(x.Arguments.Count > 20));
    }

    [Fact]
    public void RejectsPathTraversal()
    {
        Assert.Throws<InvalidOperationException>(() => SafePaths.RequireDirectFile(Root, Path.Combine(Root, "..", "other.bat")));
    }

    [Theory]
    [InlineData("1024-65535,80", true)]
    [InlineData("0-65535", false)]
    [InlineData("1024-70000", false)]
    [InlineData("1-0", false)]
    [InlineData("1&whoami", false)]
    public void ValidatesPorts(string value, bool valid) => Assert.Equal(valid, FilterService.ValidPorts(value));

    [Fact]
    public async Task SettingsRecoverFromCorruptJson()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{broken");
            var store = new JsonSettingsStore(path);
            var settings = await store.LoadAsync();
            Assert.Equal("System", settings.Theme);
            settings.SelectedStrategyId = "general";
            await store.SaveAsync(settings);
            Assert.Equal("general", (await store.LoadAsync()).SelectedStrategyId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DetectsLocalVersion()
    {
        Assert.Equal("1.10.3", new ZapretDistribution(Root).Version);
    }

    [Fact]
    public void RejectsUnknownBatchExpansion()
    {
        var sourceFile = Path.Combine(Root, "general.bat");
        var source = "start \"test\" /min \"%BIN%winws.exe\" --filter-tcp=%UNTRUSTED%";
        Assert.Throws<FormatException>(() => BatStrategyParser.Parse(source, Root, sourceFile,
            new GameFilterSettings(GameFilterMode.Disabled, "1024-65535", "1024-65535")));
    }

    [Fact]
    public void TokenizerPreservesQuotedSpaces()
    {
        Assert.Equal(["--hostlist=C:\\A B\\list.txt", "--new"],
            BatStrategyParser.Tokenize("--hostlist=\"C:\\A B\\list.txt\" --new"));
    }

    [Fact]
    public async Task IpSetModeCyclesWithoutLosingLoadedList()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        var file = Path.Combine(root, "lists", "ipset-all.txt");
        try
        {
            await File.WriteAllTextAsync(file, "1.2.3.0/24\n");
            var filter = new FilterService(new ZapretDistribution(root));
            Assert.Equal(IpSetMode.Loaded, await filter.GetIpSetModeAsync());
            await filter.SetIpSetModeAsync(IpSetMode.None);
            Assert.Equal(IpSetMode.None, await filter.GetIpSetModeAsync());
            await filter.SetIpSetModeAsync(IpSetMode.Any);
            Assert.Equal(IpSetMode.Any, await filter.GetIpSetModeAsync());
            await filter.SetIpSetModeAsync(IpSetMode.Loaded);
            Assert.Equal("1.2.3.0/24\n", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EveryListedFileCanBeEditedWithTheRightEntryType()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "lists");
        Directory.CreateDirectory(directory);
        try
        {
            var names = new[] { "list-general.txt", "list-general-user.txt", "list-exclude.txt",
                "list-exclude-user.txt", "list-google.txt", "ipset-all.txt", "ipset-exclude.txt",
                "ipset-exclude-user.txt", "list-custom.txt", "ipset-custom.txt" };
            var lists = new ListService(new ZapretDistribution(root));
            foreach (var name in names)
            {
                var isIpSet = name.StartsWith("ipset-", StringComparison.Ordinal);
                var original = isIpSet ? "1.2.3.0/24\r\n" : "^existing.example\r\n";
                var entry = isIpSet ? "8.8.8.8/32" : "new.example";
                var path = Path.Combine(directory, name);
                await File.WriteAllTextAsync(path, original);
                await lists.AddEntryAsync(name, entry);
                Assert.Contains(entry, await lists.SearchAsync(name, entry));
                await lists.AddEntryAsync(name, entry);
                Assert.Single(await lists.SearchAsync(name, entry));
                await lists.RemoveEntryAsync(name, entry);
                Assert.Equal(original, await File.ReadAllTextAsync(path));
            }
            Assert.Equal(names.Length, lists.AvailableFiles.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsWrongListEntryType()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "lists");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "list-general.txt"), "");
            await File.WriteAllTextAsync(Path.Combine(directory, "ipset-all.txt"), "");
            var lists = new ListService(new ZapretDistribution(root));
            await Assert.ThrowsAsync<ArgumentException>(() => lists.AddEntryAsync("list-general.txt", "1.2.3.4"));
            await Assert.ThrowsAsync<ArgumentException>(() => lists.AddEntryAsync("ipset-all.txt", "example.com"));
            await Assert.ThrowsAsync<ArgumentException>(() => lists.AddEntryAsync("ipset-all.txt", "1.2.3.4/33"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
