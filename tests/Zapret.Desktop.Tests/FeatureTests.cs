using Zapret.Core;

namespace Zapret.Desktop.Tests;

public sealed class FeatureTests
{
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
