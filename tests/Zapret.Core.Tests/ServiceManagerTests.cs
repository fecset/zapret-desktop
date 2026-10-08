using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class ServiceManagerTests
{
    [Fact]
    public async Task UpdatedArgumentsAndStrategyAreUsedOnTheNextServiceStart()
    {
        using var fixture = new Fixture();
        await fixture.Manager.UpdateStrategyAsync(fixture.Strategy);
        await fixture.Manager.RestartAsync();
        Assert.EndsWith("\"--wf-tcp=80,4000\" \"--filter-udp=6000\"", fixture.System.RunningImagePath);
        Assert.Equal("general (ALT)", fixture.System.StrategyName);
        Assert.False(await fixture.Manager.GetAutoStartAsync());
    }

    [Theory]
    [InlineData("install")]
    [InlineData("start")]
    [InlineData("restart")]
    public async Task ExistingWinwsBlocksServiceLaunch(string action)
    {
        using var fixture = new Fixture();
        fixture.System.State = action == "install" ? "NotInstalled" : "Stopped";
        fixture.System.HasConflictingWinwsProcesses = true;
        var previous = fixture.System.State;
        await Assert.ThrowsAsync<InvalidOperationException>(() => action switch
        {
            "install" => fixture.Manager.InstallAsync(fixture.Strategy),
            "start" => fixture.Manager.StartAsync(),
            _ => fixture.Manager.RestartAsync()
        });
        Assert.Equal(previous, fixture.System.State);
        Assert.Null(fixture.System.RunningImagePath);
        Assert.Null(fixture.System.ConfiguredImagePath);
    }

    [Fact]
    public async Task FailedConfigurationKeepsThePreviousStrategyMetadata()
    {
        using var fixture = new Fixture();
        fixture.System.ConfigError = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.UpdateStrategyAsync(fixture.Strategy));
        Assert.Equal("general", fixture.System.StrategyName);
        Assert.Null(fixture.System.ConfiguredImagePath);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("start")]
    [InlineData("restart")]
    public async Task SecondWinwsIsRejectedBeforeMutatingARunningService(string action)
    {
        using var fixture = new Fixture();
        fixture.System.HasConflictingWinwsProcesses = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => action switch
        {
            "update" => fixture.Manager.UpdateStrategyAsync(fixture.Strategy),
            "start" => fixture.Manager.StartAsync(),
            _ => fixture.Manager.RestartAsync()
        });
        Assert.Equal("Running", fixture.System.State);
        Assert.Equal("general", fixture.System.StrategyName);
        Assert.Null(fixture.System.ConfiguredImagePath);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ZapretService-tests-" + Guid.NewGuid().ToString("N"));
        public ServiceSystem System { get; } = new();
        public ScServiceManager Manager { get; }
        public ZapretStrategy Strategy { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "winws.exe"), "unused fixture");
            Manager = new(new ZapretDistribution(root), new Privilege(), System);
            Strategy = new("general (alt)", "general (ALT)", "General (ALT)", "general (ALT).bat",
                ["--wf-tcp=80,4000", "--filter-udp=6000"], "", false, false, false);
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private sealed class Privilege : IPrivilegeService
    {
        public bool IsAdministrator => true;
        public void RestartElevated() => throw new NotSupportedException();
    }

    private sealed class ServiceSystem : IZapretServiceSystem
    {
        public string State { get; set; } = "Running";
        public bool HasConflictingWinwsProcesses { get; set; }
        public bool ConfigError { get; set; }
        public string StrategyName { get; private set; } = "general";
        public string? ConfiguredImagePath { get; private set; }
        public string? RunningImagePath { get; private set; }
        public Task<string> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public void SetStrategyName(string strategyName) => StrategyName = strategyName;
        public Task<(int Code, string Output)> RunAsync(string[] args, CancellationToken cancellationToken)
        {
            var binPath = Array.IndexOf(args, "binPath=");
            if (args[0] == "config" && ConfigError) return Task.FromResult((5, "Access denied"));
            if (binPath >= 0) ConfiguredImagePath = args[binPath + 1];
            if (args[0] == "start") { State = "Running"; RunningImagePath = ConfiguredImagePath; }
            if (args[0] == "stop") State = "Stopped";
            if (args[0] == "create") State = "Stopped";
            return Task.FromResult((0, ""));
        }
    }
}
