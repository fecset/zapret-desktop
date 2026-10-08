using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Desktop.Tests;

public sealed class FeatureTests
{
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
