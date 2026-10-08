using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class WinDivertDriverTests
{
    [Fact]
    public async Task UnloadsBothDriverVersionsBeforeRemovingTheirServices()
    {
        var system = new DriverTestSystem();
        system.States["WinDivert14"] = "Running";
        Assert.True(await system.Driver.UnloadAsync(CancellationToken.None));
        Assert.Equal(new[] { "stop WinDivert", "delete WinDivert", "stop WinDivert14", "delete WinDivert14" }, system.Commands);
        Assert.Equal("NotInstalled", await system.Driver.GetStatusAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Running", false)]
    [InlineData("StartPending", false)]
    [InlineData("StopPending", false)]
    [InlineData("NotInstalled", true)]
    public async Task LeavesDriverAloneWhileAnotherZapretInstanceUsesIt(string serviceState, bool hasWinws)
    {
        var system = new DriverTestSystem { HasWinws = hasWinws };
        system.States["zapret"] = serviceState;
        Assert.False(await system.Driver.UnloadAsync(CancellationToken.None));
        Assert.Empty(system.Commands);
    }

    [Theory]
    [InlineData("NotInstalled", new string[0])]
    [InlineData("Stopped", new[] { "delete WinDivert" })]
    public async Task CleanupIsIdempotent(string state, string[] commands)
    {
        var system = new DriverTestSystem();
        system.States["WinDivert"] = state;
        Assert.True(await system.Driver.UnloadAsync(CancellationToken.None));
        Assert.True(await system.Driver.UnloadAsync(CancellationToken.None));
        Assert.Equal(commands, system.Commands);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1052)]
    public async Task FailedUnloadIsReportedAndDoesNotDeleteALoadedDriver(int code)
    {
        var system = new DriverTestSystem { StopCode = code };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Driver.UnloadAsync(CancellationToken.None));
        Assert.Contains("WinDivert", error.Message);
        Assert.Contains($"код {code}", error.Message);
        Assert.Equal(new[] { "stop WinDivert" }, system.Commands);
        Assert.Equal("Running", system.States["WinDivert"]);
    }

    [Fact]
    public async Task StopPendingMustFinishBeforeTheDriverServiceCanBeDeleted()
    {
        var system = new DriverTestSystem { KeepRunning = true };
        using var cancellation = new CancellationTokenSource();
        system.BeforeCommand = command =>
        {
            if (command == "stop WinDivert") cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => system.Driver.UnloadAsync(cancellation.Token));
        Assert.Equal(new[] { "stop WinDivert" }, system.Commands);
        system.KeepRunning = false;
        system.States["WinDivert"] = "Stopped";
        Assert.True(await system.Driver.UnloadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedRemovalIsReportedEvenAfterDriverStops()
    {
        var system = new DriverTestSystem { DeleteCode = 5 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Driver.UnloadAsync(CancellationToken.None));
        Assert.Equal("Stopped", system.States["WinDivert"]);
        system.DeleteCode = 0;
        Assert.True(await system.Driver.UnloadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryingAnInProgressUnloadWaitsWithoutIssuingAnotherStop()
    {
        var system = new DriverTestSystem { StopCode = 1061 };
        system.States["WinDivert"] = "StopPending";
        var queries = 0;
        system.BeforeQuery = name =>
        {
            if (name == "WinDivert" && ++queries == 2) system.States[name] = "Stopped";
        };
        Assert.True(await system.Driver.UnloadAsync(CancellationToken.None));
        Assert.Equal(new[] { "delete WinDivert" }, system.Commands);
    }

    [Fact]
    public async Task ANewWinwsDuringTheStopWaitPreventsDriverServiceRemoval()
    {
        var system = new DriverTestSystem();
        system.States["WinDivert"] = "StopPending";
        var queries = 0;
        system.BeforeQuery = name =>
        {
            if (name != "WinDivert" || ++queries != 2) return;
            system.States[name] = "Stopped";
            system.HasWinws = true;
        };
        Assert.False(await system.Driver.UnloadAsync(CancellationToken.None));
        Assert.Empty(system.Commands);
    }

    [Theory]
    [InlineData("AccessDenied")]
    [InlineData("StopPending")]
    [InlineData("StartPending")]
    public async Task InactiveFirstDriverDoesNotHideTheOtherDriverState(string state)
    {
        var system = new DriverTestSystem();
        system.States["WinDivert"] = "Stopped";
        system.States["WinDivert14"] = state;
        Assert.Equal(state, await system.Driver.GetStatusAsync(CancellationToken.None));
    }
}

internal sealed class DriverTestSystem
{
    public Dictionary<string, string> States { get; } = new()
    {
        ["zapret"] = "NotInstalled", ["WinDivert"] = "Running", ["WinDivert14"] = "NotInstalled"
    };
    public List<string> Commands { get; } = [];
    public bool HasWinws { get; set; }
    public bool KeepRunning { get; set; }
    public int StopCode { get; set; }
    public int DeleteCode { get; set; }
    public Func<string>? ServiceStatus { get; set; }
    public Action<string>? BeforeCommand { get; set; }
    public Action<string>? BeforeQuery { get; set; }
    public WinDivertDriver Driver => new(QueryAsync, RunAsync, () => HasWinws);

    private Task<string> QueryAsync(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BeforeQuery?.Invoke(name);
        return Task.FromResult(name == "zapret" && ServiceStatus is not null ? ServiceStatus() : States[name]);
    }
    private Task<(int Code, string Output)> RunAsync(string[] args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var command = string.Join(" ", args);
        BeforeCommand?.Invoke(command);
        Commands.Add(command);
        var code = args[0] == "stop" ? StopCode : DeleteCode;
        if (code == 0 && args[0] == "delete") States[args[1]] = "NotInstalled";
        if (code == 0 && args[0] == "stop") States[args[1]] = KeepRunning ? "StopPending" : "Stopped";
        return Task.FromResult((code, "fixture output"));
    }
}
