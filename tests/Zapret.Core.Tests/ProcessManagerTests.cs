using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class ProcessManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDriverUnloadCanBeRetriedAfterProcessOwnershipIsReleased(bool explicitStop)
    {
        using var fixture = new OwnedFixture();
        fixture.Drivers.StopCode = 5;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (explicitStop) await fixture.Manager.StopAsync();
            else await fixture.Manager.StopOwnedAsync();
        });
        Assert.True(fixture.Monitor.HasExited);
        var status = await fixture.Manager.GetStatusAsync();
        Assert.Equal(ZapretRunState.Stopped, status.State);
        Assert.Equal("Running", status.DriverStatus);
        fixture.Drivers.StopCode = 0;
        Assert.False(await fixture.Manager.StopOwnedAsync());
        Assert.Equal("NotInstalled", (await fixture.Manager.GetStatusAsync()).DriverStatus);
        Assert.Equal(new[] { "stop WinDivert", "stop WinDivert", "delete WinDivert" }, fixture.Drivers.Commands);
    }

    [Fact]
    public async Task ExitKeepsTheDriverOfAnIndependentRunningService()
    {
        using var fixture = new OwnedFixture();
        fixture.Service.State = "Running";
        Assert.True(await fixture.Manager.StopOwnedAsync());
        Assert.True(fixture.Monitor.HasExited);
        Assert.Equal("Running", fixture.Service.State);
        Assert.Empty(fixture.Drivers.Commands);
        fixture.Service.State = "Stopped";
        Assert.False(await fixture.Manager.StopOwnedAsync());
        Assert.Equal(new[] { "stop WinDivert", "delete WinDivert" }, fixture.Drivers.Commands);
    }

    [Fact]
    public async Task ExplicitStopReleasesAnIdleDriverEvenWithoutAnOwnedProcess()
    {
        var drivers = new DriverTestSystem();
        var manager = new ZapretProcessManager(new ZapretDistribution(Path.GetTempPath()), new GuardService(),
            (_, _) => throw new NotSupportedException(), drivers.Driver, () => []);
        var status = await manager.StopAsync();
        Assert.Equal(ZapretRunState.Stopped, status.State);
        Assert.Equal("NotInstalled", status.DriverStatus);
    }

    [Fact]
    public async Task ExplicitStopDoesNotUnloadDriverWhileAnExternalWinwsRemains()
    {
        var drivers = new DriverTestSystem { HasWinws = true };
        var manager = new ZapretProcessManager(new ZapretDistribution(Path.GetTempPath()), new GuardService(),
            (_, _) => throw new NotSupportedException(), drivers.Driver, () => []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StopAsync());
        Assert.Empty(drivers.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTerminationPreservesOwnershipForTheNextStop(bool cancelled)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        foreach (var arg in new[] { "-NoProfile", "-Command", "[void][Console]::ReadLine()" }) info.ArgumentList.Add(arg);
        using var child = Process.Start(info)!;
        using var monitor = Process.GetProcessById(child.Id);
        var attempts = 0;
        var drivers = new DriverTestSystem();
        var manager = new ZapretProcessManager(new ZapretDistribution(Path.GetTempPath()), null!, async (live, token) =>
        {
            if (attempts++ == 0)
            {
                if (cancelled) throw new OperationCanceledException();
                throw new Win32Exception(5);
            }
            live.Kill();
            await live.WaitForExitAsync(token);
        }, drivers.Driver, () => []);
        var owned = typeof(ZapretProcessManager).GetField("owned", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var strategy = typeof(ZapretProcessManager).GetField("strategyId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        owned.SetValue(manager, child);
        strategy.SetValue(manager, "general");
        try
        {
            if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => manager.StopOwnedAsync());
            else await Assert.ThrowsAsync<Win32Exception>(() => manager.StopOwnedAsync());
            Assert.Same(child, owned.GetValue(manager));
            Assert.Equal("general", strategy.GetValue(manager));
            Assert.False(monitor.HasExited);
            Assert.Empty(drivers.Commands);
            Assert.True(await manager.StopOwnedAsync());
            await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(owned.GetValue(manager));
            Assert.Null(strategy.GetValue(manager));
            Assert.Equal(new[] { "stop WinDivert", "delete WinDivert" }, drivers.Commands);
        }
        finally
        {
            if (!monitor.HasExited) monitor.Kill();
            await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class OwnedFixture : IDisposable
    {
        public GuardService Service { get; } = new();
        public DriverTestSystem Drivers { get; } = new();
        public ZapretProcessManager Manager { get; }
        public Process Monitor { get; }
        private readonly Process child;

        public OwnedFixture()
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            };
            foreach (var arg in new[] { "-NoProfile", "-Command", "[void][Console]::ReadLine()" }) info.ArgumentList.Add(arg);
            child = Process.Start(info)!;
            Monitor = Process.GetProcessById(child.Id);
            Drivers.ServiceStatus = () => Service.State;
            Drivers.BeforeCommand = _ => Assert.True(Monitor.HasExited);
            Manager = new(new ZapretDistribution(Path.GetTempPath()), Service, async (live, token) =>
            {
                live.Kill();
                await live.WaitForExitAsync(token);
            }, Drivers.Driver, () => []);
            typeof(ZapretProcessManager).GetField("owned", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Manager, child);
        }

        public void Dispose()
        {
            if (!Monitor.HasExited) Monitor.Kill();
            Monitor.WaitForExit(5000);
            Monitor.Dispose();
            child.Dispose();
        }
    }

    private sealed class GuardService : IServiceManager
    {
        public string State { get; set; } = "NotInstalled";
        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task StopAsync(CancellationToken cancellationToken = default) { State = "Stopped"; return Task.CompletedTask; }
        public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateStrategyAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
