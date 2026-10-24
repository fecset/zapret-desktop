using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Desktop.Tests;

public sealed class FeatureTests
{
    [Fact]
    public async Task DistributionApplyAndRollbackPreserveBothUnsavedDraftBlocks()
    {
        using var app = new TestApplication(managedUpdates: new MemoryUpdates());
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.PrepareDistributionUpdateAsync();
        app.ViewModel.Draft.TcpPorts = "4000";
        app.ViewModel.Draft.StartZapretOnLaunch = true;
        await app.ViewModel.ApplyDistributionUpdateAsync();
        Assert.NotNull(app.ViewModel.LastDistributionUpdate);
        AssertDraft();
        await app.ViewModel.RollbackDistributionUpdateAsync();
        Assert.Null(app.ViewModel.LastDistributionUpdate);
        AssertDraft();
        void AssertDraft()
        {
            Assert.Equal("4000", app.ViewModel.Draft.TcpPorts);
            Assert.True(app.ViewModel.Draft.StartZapretOnLaunch);
            Assert.False(app.ViewModel.Settings.StartZapretOnLaunch);
        }
    }

    private sealed class MemoryUpdates : IManagedUpdateService
    {
        public Task<ManagedUpdatePlan> PrepareDistributionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagedUpdatePlan("test", ManagedUpdateProduct.Distribution, "test", "", "", 0, "", 0, "", "test"));
        public Task<ManagedUpdateResult> ApplyDistributionAsync(ManagedUpdatePlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagedUpdateResult("test", "test", "", "", "test"));
        public Task RollbackDistributionAsync(ManagedUpdateResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ManagedUpdatePlan> PrepareDesktopAsync(string localVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopUpdateSchedule> ScheduleDesktopUpdateAsync(ManagedUpdatePlan plan, int currentProcessId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IpSetUpdatePlan> PrepareIpSetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DataUpdateResult> ApplyIpSetAsync(IpSetUpdatePlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HostsUpdatePreview> PreviewHostsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DataUpdateResult> ApplyHostsAsync(HostsUpdatePreview preview, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ManualConnectionCheckCannotPublishResultsDuringAnEngineMutation()
    {
        var health = new DelayedHealth();
        using var app = new TestApplication(connection: health);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Process.BeforeStop = async () => { entered.TrySetResult(); await release.Task; };
        var stop = app.ViewModel.StopAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var check = app.ViewModel.CheckConnectionAsync();
        var checkWasSkipped = !health.Entered.Task.IsCompleted;
        release.TrySetResult();
        await stop;
        health.Completion.TrySetResult(new(DateTimeOffset.UtcNow, []));
        await check;
        Assert.True(checkWasSkipped);
        Assert.Null(app.ViewModel.ConnectionHealth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowConnectionCheckDoesNotBlockStoppingOrChangingStrategy(bool restart)
    {
        var health = new DelayedHealth();
        using var app = new TestApplication(connection: health);
        await app.ViewModel.InitializeAsync();
        var start = restart ? app.ViewModel.ApplyAndRestartAsync() : app.ViewModel.StartAsync();
        await health.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            Assert.False(app.ViewModel.Busy);
            await app.ViewModel.StopAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(item => item.Id == "general (alt)"))
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(ZapretRunState.Stopped, app.ViewModel.Status?.State);
            Assert.Equal("general (alt)", app.ViewModel.SelectedStrategy?.Id);
        }
        finally
        {
            health.Completion.TrySetResult(new(DateTimeOffset.UtcNow, []));
            await start;
        }
        Assert.Null(app.ViewModel.ConnectionHealth);
    }

    private sealed class DelayedHealth : IConnectionHealthService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ConnectionHealthResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            // Simulate a transport that finishes even after cancellation.
            return Completion.Task;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingDistributionFilesFirstUnloadsTheIdleDriver(bool rollback)
    {
        using var app = new TestApplication();
        app.Process.DriverState = "Running";
        app.Process.StopError = new InvalidOperationException("WinDivert cannot unload");
        if (rollback) await app.ViewModel.RollbackDistributionUpdateAsync();
        else await app.ViewModel.ApplyDistributionUpdateAsync();
        Assert.Equal(1, app.Process.StopCalls);
        Assert.Contains("WinDivert cannot unload", app.ViewModel.Message);
    }

    [Fact]
    public async Task FailedDriverUnloadRefreshesEngineStatusAndOffersAnError()
    {
        using var app = new TestApplication();
        app.Process.NormalState = ZapretRunState.Running;
        app.Process.DriverState = "Running";
        await app.ViewModel.InitializeAsync();
        app.Process.StopError = new InvalidOperationException("WinDivert cannot unload");
        await app.ViewModel.StopAsync();
        Assert.Equal(ZapretRunState.Stopped, app.ViewModel.Status?.State);
        Assert.Equal("Running", app.ViewModel.Status?.DriverStatus);
        Assert.Contains("WinDivert cannot unload", app.ViewModel.Message);
    }

    [Fact]
    public async Task DiagnosticReportOffersRemediationOnlyForFailedChecks()
    {
        using var app = new TestApplication(new ReportDiagnostic());
        await app.ViewModel.DiagnoseAsync();
        var report = app.ViewModel.BuildDiagnosticReport();
        Assert.Contains("OK Hosts: Alternative addresses", report);
        Assert.DoesNotContain("Remove hosts entries", report);
        Assert.Contains("FAIL DNS: No response — Check DNS", report);
    }

    private sealed class ReportDiagnostic : IDiagnosticService
    {
        public Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiagnosticItem>>([new("Hosts", true, "Alternative addresses", "Remove hosts entries"),
                new("DNS", false, "No response", "Check DNS")]);
    }

    [Fact]
    public async Task ApplyingHostsRefreshesAnExistingDiagnosticReport()
    {
        var diagnostic = new HostsDiagnostic();
        using var client = new HttpClient(new HostsResponse());
        using var app = new TestApplication(diagnostic, updateClient: client);
        diagnostic.Path = app.HostsPath;
        File.WriteAllText(app.HostsPath, "127.0.0.1 localhost\n");
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.DiagnoseAsync();
        Assert.False(Assert.Single(app.ViewModel.Diagnostics).Passed);
        await app.ViewModel.PreviewHostsAsync();
        Assert.Equal("127.0.0.1 localhost\n", File.ReadAllText(app.HostsPath));
        await app.ViewModel.ApplyHostsAsync();
        Assert.Contains("162.159.138.232 discord.com", File.ReadAllText(app.HostsPath));
        Assert.True(Assert.Single(app.ViewModel.Diagnostics).Passed);
        Assert.Null(app.ViewModel.HostsPreview);
    }

    private sealed class HostsDiagnostic : IDiagnosticService
    {
        public string Path { get; set; } = "";
        public Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiagnosticItem>>([new("Hosts", File.ReadAllText(Path).Contains("162.159.138.232 discord.com"), "Current hosts", "")]);
    }

    private sealed class HostsResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("162.159.138.232 discord.com\n") });
    }

    [Fact]
    public async Task ApplyingFiltersAndStrategyToServiceRefreshesConnectionHealth()
    {
        var health = new Health();
        using var app = new TestApplication(connection: health);
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(item => item.Id == "general"));
        await app.Service.InstallAsync(app.ViewModel.SelectedStrategy!, TestContext.Current.CancellationToken);
        await app.ViewModel.SetGameAsync(GameFilterMode.All, "4000-5000", "6000-7000");
        await app.ViewModel.ApplyAndRestartAsync();
        Assert.Contains("--filter-udp=6000-7000", app.Service.RunningStrategy!.Arguments);
        Assert.Same(health.Result, app.ViewModel.ConnectionHealth);
    }

    [Fact]
    public async Task CompletedTestIsPersistedAndRecommendationOnlyChangesNextLaunch()
    {
        using var app = new TestApplication(featureServices: true);
        await app.ViewModel.InitializeAsync();
        app.Tester.Result = new("general (ALT).bat", "", false);
        app.Tester.Cleanup.TrySetResult();
        await app.ViewModel.TestStrategiesAsync();
        var entry = Assert.Single(app.ViewModel.TestHistory);
        Assert.Equal("general (ALT).bat", entry.BestStrategy);
        await app.ViewModel.ShowHistoryAsync(entry.Id);
        Assert.NotNull(app.ViewModel.HistoryDetails);
        await app.ViewModel.ApplyRecommendationAsync(entry.BestStrategy);
        Assert.Equal("general (alt)", (await app.Settings.LoadAsync(TestContext.Current.CancellationToken)).SelectedStrategyId);
        Assert.Equal(ZapretRunState.Stopped, (await app.Process.GetStatusAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task RestoreIsBlockedWhileEngineRunsAndSettingsRemainUnchanged()
    {
        using var app = new TestApplication(featureServices: true);
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(item => item.Id == "general"));
        await app.ViewModel.CreateBackupAsync();
        var backup = Assert.Single(app.ViewModel.Backups);
        await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(item => item.Id == "general (alt)"));
        await app.ViewModel.StartAsync();
        await app.ViewModel.RestoreBackupAsync(backup.Id);
        Assert.StartsWith("Ошибка:", app.ViewModel.Message);
        Assert.Equal("general (alt)", (await app.Settings.LoadAsync(TestContext.Current.CancellationToken)).SelectedStrategyId);
        await app.ViewModel.StopAsync();
        await app.ViewModel.RestoreBackupAsync(backup.Id);
        Assert.Equal("general", app.ViewModel.SelectedStrategy?.Id);
    }

    private sealed class Health : IConnectionHealthService
    {
        public ConnectionHealthResult Result { get; } = new(DateTimeOffset.UtcNow,
            [new("probe", "https://example.com", true, 200, TimeSpan.Zero, "OK")]);
        public Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }
}
