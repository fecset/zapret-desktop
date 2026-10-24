using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

// Regression coverage for cancellation at a completed protocol result.
public sealed class StrategyProtocolRegressionTests
{
    [Theory]
    [InlineData(2, 0, "general (ALT).bat")]
    [InlineData(1, 2, "general (ALT).bat")]
    [InlineData(1, 1, "general.bat")]
    public async Task RecommendationRanksCompletedHttpThenPingAndKeepsFirstTie(int secondHttp, int secondPing, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in new[] { "general.bat", "general (ALT).bat" })
                await File.WriteAllTextAsync(Path.Combine(root, file), "start \"test\" /min \"%BIN%winws.exe\" --wf-tcp=80");
            var script = "param([switch]$DesktopMode, [string]$DistributionRoot)\n" +
                "Write-Host '__ZD_START__|general.bat'\n[void][Console]::ReadLine()\n" +
                "Write-Host '__ZD_RESULT__|general.bat|1|0|0|1|0'\n" +
                "Write-Host '__ZD_START__|general (ALT).bat'\n[void][Console]::ReadLine()\n" +
                $"Write-Host '__ZD_RESULT__|general (ALT).bat|{secondHttp}|0|100|{secondPing}|0'\n";
            var distribution = new ZapretDistribution(root);
            var tester = new PowerShellStrategyTester(distribution, new NoService(), new NoProcess(), new Privilege(),
                new BatStrategyProvider(distribution), _ => Task.FromResult(script));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await tester.RunAsync(new Immediate<string>(_ => { }), new Immediate<StrategyCheckProgress>(_ => { }), cancellation.Token);
            Assert.Equal(expected, result.BestStrategy);
        }
        finally { await DeleteTestDirectoryAsync(root); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("late")]
    [InlineData("reported-failure")]
    public async Task ResultWithoutSuccessfulCurrentStartCannotBecomeRecommendation(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in new[] { "general.bat", "general (ALT).bat" })
                await File.WriteAllTextAsync(Path.Combine(root, file), "start \"test\" /min \"%BIN%winws.exe\" --wf-tcp=80");
            var script = "param([switch]$DesktopMode, [string]$DistributionRoot)\n";
            if (scenario != "missing") script += "Write-Host '__ZD_START__|general.bat'\n[void][Console]::ReadLine()\n";
            if (scenario == "late") script += "Write-Host '__ZD_START__|general (ALT).bat'\n[void][Console]::ReadLine()\n";
            if (scenario == "reported-failure") script += "Write-Host 'Strategy failed to start'\n";
            script += "Write-Host '__ZD_RESULT__|general.bat|3|0|0|1|0'\n";
            var distribution = new ZapretDistribution(root);
            var tester = new PowerShellStrategyTester(distribution, new NoService(), new NoProcess(scenario == "failed"),
                new Privilege(), new BatStrategyProvider(distribution), _ => Task.FromResult(script));
            var checks = new List<StrategyCheckProgress>();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await tester.RunAsync(new Immediate<string>(_ => { }), new Immediate<StrategyCheckProgress>(checks.Add), cancellation.Token);
            Assert.Null(result.BestStrategy);
            Assert.DoesNotContain(checks, check => check.State == StrategyCheckState.Completed);
        }
        finally { await DeleteTestDirectoryAsync(root); }
    }

    [Fact]
    public async Task CancellationAfterCompletedProtocolResultKeepsThatRecommendation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "general.bat"),
                "start \"test\" /min \"%BIN%winws.exe\" --wf-tcp=80");
            var distribution = new ZapretDistribution(root);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tester = new PowerShellStrategyTester(distribution, new NoService(), new NoProcess(),
                new Privilege(), new BatStrategyProvider(distribution), _ => Task.FromResult("""
                param([switch]$DesktopMode, [string]$DistributionRoot)
                Write-Host '__ZD_START__|general.bat'
                [void][Console]::ReadLine()
                Write-Host '  [1/1] general.bat'
                Write-Host 'Discord HTTP:OK TLS1.2:OK TLS1.3:OK | Ping: 1 ms'
                Write-Host '__ZD_RESULT__|general.bat|3|0|0|1|0'
                [void][Console]::ReadLine()
                """));
            var sawCompleted = false;
            var result = await tester.RunAsync(new Immediate<string>(_ => { }),
                new Immediate<StrategyCheckProgress>(check =>
                {
                    if (check.State != StrategyCheckState.Completed) return;
                    sawCompleted = true;
                    cancellation.Cancel();
                }), cancellation.Token);
            Assert.True(sawCompleted);
            Assert.True(result.Cancelled);
            Assert.Equal("general.bat", result.BestStrategy);
        }
        finally { await DeleteTestDirectoryAsync(root); }
    }

    private static async Task DeleteTestDirectoryAsync(string root)
    {
        // Process exit has already been awaited. Windows can briefly retain a
        // sharing lock on its working directory; persistent locks still fail.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32 && attempt < 40)
            { await Task.Delay(25); }
        }
    }

    private sealed class Immediate<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
    private sealed class Privilege : IPrivilegeService
    {
        public bool IsAdministrator => true;
        public void RestartElevated() => throw new NotSupportedException();
    }
    private sealed class NoService : IServiceManager
    {
        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult("NotInstalled");
        public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateStrategyAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class NoProcess(bool failStart = false) : IZapretProcessManager
    {
        public event Action<string, string>? OutputReceived { add { } remove { } }
        public Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(
            new ZapretStatus(ZapretRunState.Stopped, null, null, null, "NotInstalled", "NotInstalled"));
        public Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) =>
            failStart ? Task.FromException<ZapretStatus>(new InvalidOperationException("test start failed")) : GetStatusAsync(cancellationToken);
        public Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
