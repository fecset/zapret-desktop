using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core;
using Zapret.Desktop;
using Zapret.Infrastructure;

namespace Zapret.Desktop.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task ExitCancelsTheTestAndWaitsForItsCleanup()
    {
        using var app = new TestApplication();
        var test = app.ViewModel.TestStrategiesAsync();
        await app.Tester.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var exit = app.ViewModel.StopOwnedForExitAsync();
        var cancelled = app.Tester.Token.IsCancellationRequested;
        var exitedBeforeCleanup = exit.IsCompleted;
        app.Tester.Cleanup.TrySetResult();
        await test.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(await exit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(cancelled);
        Assert.False(exitedBeforeCleanup);
        Assert.False(app.ViewModel.TestingStrategies);
    }

    [Fact]
    public async Task ExitPreventsQueuedStartsFromLeavingAProcessRunning()
    {
        using var app = new TestApplication();
        var test = app.ViewModel.TestStrategiesAsync();
        await app.Tester.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var exit = app.ViewModel.StopOwnedForExitAsync();
        var start = app.ViewModel.StartAsync();
        app.Tester.Cleanup.TrySetResult();
        await Task.WhenAll(test, exit, start).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(ZapretRunState.Stopped, (await app.Process.GetStatusAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task ExitDuringRecoveryPreventsThePendingStrategyTest()
    {
        using var app = new TestApplication();
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Tester.Recover = async () => { recovering.TrySetResult(); await recovered.Task; };
        var test = app.ViewModel.TestStrategiesAsync();
        await recovering.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var exit = app.ViewModel.StopOwnedForExitAsync();
        // Release both waits so the regression fails promptly instead of hanging the test suite.
        recovered.TrySetResult();
        app.Tester.Cleanup.TrySetResult();
        await test.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(await exit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(app.Tester.Entered.Task.IsCompleted);
    }

    [Fact]
    public async Task ExitStaysOpenWhenInterruptedTestRecoveryFails()
    {
        using var app = new TestApplication();
        app.Tester.RecoveryError = new IOException("Backup unavailable");
        Assert.False(await app.ViewModel.StopOwnedForExitAsync());
        app.Tester.RecoveryError = null;
        await app.ViewModel.StartAsync();
        Assert.Equal(ZapretRunState.Running, (await app.Process.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        Assert.True(await app.ViewModel.StopOwnedForExitAsync());
    }

    [Fact]
    public async Task StartupRestoresInterruptedIpSetBeforeAutomaticLaunch()
    {
        using var app = new TestApplication();
        var ipset = Path.Combine(app.Distribution.ListsDirectory, "ipset-all.txt");
        await File.WriteAllTextAsync(ipset, "", TestContext.Current.CancellationToken);
        app.Tester.Recover = () => File.WriteAllTextAsync(ipset, "1.2.3.0/24\n");
        await app.Settings.SaveAsync(new DesktopSettings { StartZapretOnLaunch = true }, TestContext.Current.CancellationToken);
        await app.ViewModel.InitializeAsync();
        Assert.Equal("1.2.3.0/24\n", app.Process.IpSetAtStart);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("install")]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("test")]
    public async Task FailedRecoveryBlocksEverySubsequentLaunch(string action)
    {
        using var app = new TestApplication();
        if (action is "start" or "restart")
        {
            await app.Service.InstallAsync(app.ViewModel.SelectedStrategy!, TestContext.Current.CancellationToken);
            await app.Service.StopAsync(TestContext.Current.CancellationToken);
        }
        app.Tester.RecoveryError = new IOException("Backup unavailable");
        Assert.False(await app.ViewModel.StopOwnedForExitAsync());
        var previousService = app.Service.State;
        if (action == "normal") await app.ViewModel.StartAsync();
        else if (action == "test")
        {
            app.Tester.Cleanup.TrySetResult();
            await app.ViewModel.TestStrategiesAsync();
            Assert.False(app.Tester.Entered.Task.IsCompleted);
        }
        else await app.ViewModel.ServiceAsync(action);
        Assert.Equal(ZapretRunState.Stopped, (await app.Process.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(previousService, app.Service.State);
        Assert.StartsWith("Ошибка:", app.ViewModel.Message);
    }

    [Fact]
    public async Task StartupRecoveryFailureIsVisibleAndPreventsAutomaticLaunch()
    {
        using var app = new TestApplication();
        app.Tester.RecoveryError = new IOException("Backup unavailable");
        await app.Settings.SaveAsync(new DesktopSettings { StartZapretOnLaunch = true }, TestContext.Current.CancellationToken);
        await app.ViewModel.InitializeAsync();
        Assert.Equal(ZapretRunState.Stopped, (await app.Process.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        Assert.Contains("Backup unavailable", app.ViewModel.Message);
        Assert.NotEmpty(app.ViewModel.Strategies);
    }

    [Fact]
    public async Task ServiceRestartUsesCurrentGameFilterPorts()
    {
        using var app = new TestApplication();
        await app.Service.InstallAsync(app.ViewModel.SelectedStrategy!, TestContext.Current.CancellationToken);
        await app.ViewModel.SetGameAsync(GameFilterMode.All, "4000-5000", "6000-7000");
        await app.ViewModel.ServiceAsync("restart");
        Assert.Contains("--wf-tcp=80,4000-5000", app.Service.RunningStrategy!.Arguments);
        Assert.Contains("--filter-udp=6000-7000", app.Service.RunningStrategy.Arguments);
        Assert.DoesNotContain("--filter-udp=12", app.Service.RunningStrategy.Arguments);
    }

    [Fact]
    public async Task ServiceStartUsesTheNewlySelectedStrategy()
    {
        using var app = new TestApplication();
        await app.Service.InstallAsync(app.ViewModel.SelectedStrategy!, TestContext.Current.CancellationToken);
        await app.Service.StopAsync(TestContext.Current.CancellationToken);
        await app.ViewModel.SelectAsync((await app.Strategies.GetStrategiesAsync(TestContext.Current.CancellationToken)).Single(x => x.Id == "general (alt)"));
        await app.ViewModel.ServiceAsync("start");
        Assert.Equal("general (alt)", app.Service.RunningStrategy!.Id);
        Assert.Contains("--wf-tcp=443", app.Service.RunningStrategy.Arguments);
    }

    [Theory]
    [InlineData("install", ZapretRunState.Running)]
    [InlineData("install", ZapretRunState.External)]
    [InlineData("start", ZapretRunState.Running)]
    [InlineData("start", ZapretRunState.External)]
    [InlineData("restart", ZapretRunState.Running)]
    [InlineData("restart", ZapretRunState.External)]
    public async Task ServiceLaunchRejectsAnExistingNormalOrExternalProcess(string action, ZapretRunState state)
    {
        using var app = new TestApplication();
        if (action != "install")
        {
            await app.Service.InstallAsync(app.ViewModel.SelectedStrategy!, TestContext.Current.CancellationToken);
            await app.Service.StopAsync(TestContext.Current.CancellationToken);
        }
        var previous = app.Service.State;
        app.Process.NormalState = state;
        await app.ViewModel.ServiceAsync(action);
        Assert.StartsWith("Ошибка:", app.ViewModel.Message);
        Assert.Equal(previous, app.Service.State);
        Assert.Equal(state, (await app.Process.GetStatusAsync(TestContext.Current.CancellationToken)).State);
    }
}

internal sealed class TestApplication : IDisposable
{
    private readonly string workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ZapretDesktop-tests-" + Guid.NewGuid().ToString("N")));
    public ZapretDistribution Distribution { get; }
    public BatStrategyProvider Strategies { get; }
    public JsonSettingsStore Settings { get; }
    public TestServiceManager Service { get; } = new();
    public TestStrategyTester Tester { get; } = new();
    public TestProcessManager Process { get; }
    public MainViewModel ViewModel { get; }
    public string HostsPath => Path.Combine(workspace, "hosts");

    public TestApplication(IDiagnosticService? diagnostics = null, IConnectionHealthService? connection = null,
        bool featureServices = false, IPayloadRecoveryService? payloadRecovery = null, HttpClient? updateClient = null)
    {
        Distribution = new(Path.Combine(workspace, "zapret"));
        Directory.CreateDirectory(Distribution.ListsDirectory);
        File.WriteAllText(Path.Combine(Distribution.ListsDirectory, "ipset-all.txt"), "1.2.3.0/24\n");
        File.WriteAllText(Path.Combine(Distribution.Root, "general.bat"),
            "start \"test\" /min \"%BIN%winws.exe\" --wf-tcp=80,%GameFilterTCP% --filter-udp=%GameFilterUDP%");
        File.WriteAllText(Path.Combine(Distribution.Root, "general (ALT).bat"),
            "start \"test\" /min \"%BIN%winws.exe\" --wf-tcp=443");
        Strategies = new(Distribution);
        Settings = new(Path.Combine(workspace, "data", "settings.json"));
        Process = new(Service, Distribution);
        ViewModel = new(Distribution, Strategies, Process, Service, new FilterService(Distribution),
            diagnostics!, new ListService(Distribution), null!, Tester, Settings, new TestStartupService(),
            new FakePayloadService(Distribution, Path.Combine(workspace, "data")), new TestPrivilegeService(), NullLogger<MainViewModel>.Instance,
            backups: featureServices ? new UserDataBackupService(Distribution, Settings, Path.Combine(workspace, "data")) : null,
            payloadRecovery: payloadRecovery ?? (featureServices ? new PayloadRecoveryService(Distribution, Path.Combine(workspace, "data")) : null),
            managedUpdates: updateClient is null ? null : new ManagedUpdateService(Distribution, updateClient,
                Path.Combine(workspace, "application"), Path.Combine(workspace, "stage"), HostsPath),
            connection: connection,
            history: featureServices ? new StrategyHistoryService(Distribution, Path.Combine(workspace, "data")) : null);
        var initialFile = Path.Combine(Distribution.Root, "general.bat");
        ViewModel.SelectedStrategy = new("general", "general", "General", initialFile,
            BatStrategyParser.Parse(File.ReadAllText(initialFile), Distribution.Root, initialFile,
                new(GameFilterMode.Disabled, "1024-65535", "1024-65535")), "", false, false, false);
    }

    public void Dispose()
    {
        var expectedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!workspace.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture path.");
        Directory.Delete(workspace, recursive: true);
    }
}

internal sealed class TestStrategyTester : IStrategyTester
{
    public StrategyTestResult? Result { get; set; }
    public Action<IProgress<StrategyCheckProgress>>? ReportChecks { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Token { get; private set; }
    public Exception? RecoveryError { get; set; }
    public Func<Task>? Recover { get; set; }
    public async Task<StrategyTestResult> RunAsync(IProgress<string> progress, IProgress<StrategyCheckProgress> strategyProgress,
        CancellationToken cancellationToken = default)
    {
        Token = cancellationToken;
        Entered.TrySetResult();
        await Cleanup.Task;
        ReportChecks?.Invoke(strategyProgress);
        return Result ?? new(null, "", cancellationToken.IsCancellationRequested);
    }
    public Task RecoverInterruptedTestAsync(CancellationToken cancellationToken = default) =>
        RecoveryError is not null ? Task.FromException(RecoveryError) : Recover?.Invoke() ?? Task.CompletedTask;
}

internal sealed class TestServiceManager : IServiceManager
{
    public string State { get; private set; } = "NotInstalled";
    private ZapretStrategy? configured;
    public ZapretStrategy? RunningStrategy { get; private set; }
    public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
    public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    {
        configured = strategy;
        return StartAsync(cancellationToken);
    }
    public Task UpdateStrategyAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    {
        configured = strategy;
        return Task.CompletedTask;
    }
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        State = "Running";
        RunningStrategy = configured;
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken = default) { State = "Stopped"; return Task.CompletedTask; }
    public async Task RestartAsync(CancellationToken cancellationToken = default) { await StopAsync(cancellationToken); await StartAsync(cancellationToken); }
    public Task RemoveAsync(CancellationToken cancellationToken = default) { State = "NotInstalled"; return Task.CompletedTask; }
    public Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class TestProcessManager(TestServiceManager service, IZapretDistribution distribution) : IZapretProcessManager
{
    public ZapretRunState NormalState { get; set; }
    public string? IpSetAtStart { get; private set; }
    public event Action<string, string>? OutputReceived { add { } remove { } }
    public Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ZapretStatus(
        NormalState != ZapretRunState.Stopped ? NormalState : service.State == "Running" ? ZapretRunState.ServiceRunning : ZapretRunState.Stopped,
        null, null, service.RunningStrategy?.Id, "Stopped", service.State));
    public async Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    {
        NormalState = ZapretRunState.Running;
        IpSetAtStart = await File.ReadAllTextAsync(Path.Combine(distribution.ListsDirectory, "ipset-all.txt"), cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }
    public async Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default) { await StopOwnedAsync(cancellationToken); return await GetStatusAsync(cancellationToken); }
    public Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        var stopped = NormalState == ZapretRunState.Running;
        if (stopped) NormalState = ZapretRunState.Stopped;
        return Task.FromResult(stopped);
    }
    public async Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    { await StopAsync(cancellationToken); return await StartAsync(strategy, cancellationToken); }
}

internal sealed class TestPrivilegeService : IPrivilegeService
{
    public bool IsAdministrator => true;
    public void RestartElevated() => throw new NotSupportedException();
}

internal sealed class TestStartupService : IStartupService
{
    public bool IsEnabled() => false;
    public void SetEnabled(bool enabled) { }
    public bool MigrateLegacyRegistration() => false;
}
