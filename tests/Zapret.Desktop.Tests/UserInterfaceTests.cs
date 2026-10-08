using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Zapret.Core;
using Zapret.Desktop;

[assembly: AvaloniaTestApplication(typeof(Zapret.Desktop.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Zapret.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestAvaloniaApp>()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class TestAvaloniaApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public sealed class UserInterfaceTests
{
    [AvaloniaFact]
    public async Task FinalProgressIsIncludedInCancelledHistoryAndLateCallbacksAreIgnored()
    {
        using var app = new TestApplication(featureServices: true);
        IProgress<StrategyCheckProgress>? reporter = null;
        app.Tester.Result = new(null, "", true);
        app.Tester.ReportChecks = progress =>
        {
            reporter = progress;
            progress.Report(new("general.bat", StrategyCheckState.Running));
        };
        await app.ViewModel.InitializeAsync();
        app.Tester.Cleanup.TrySetResult();
        await app.ViewModel.TestStrategiesAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(StrategyCheckState.Cancelled, app.ViewModel.StrategyChecks["general.bat"].State);
        Assert.Equal(StrategyCheckState.Cancelled, Assert.Single(Assert.Single(app.ViewModel.TestHistory).Checks).State);
        reporter!.Report(new("general.bat", StrategyCheckState.Running));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(StrategyCheckState.Cancelled, app.ViewModel.StrategyChecks["general.bat"].State);
    }

    [AvaloniaFact]
    public async Task ComparingPayloadKeepsSelectedGameTargetAndSource()
    {
        using var app = new TestApplication(payloadRecovery: new PayloadRecovery());
        Directory.CreateDirectory(app.Distribution.BinDirectory);
        File.WriteAllBytes(Path.Combine(app.Distribution.BinDirectory, "a.bin"), [1]);
        File.WriteAllBytes(Path.Combine(app.Distribution.BinDirectory, "b.bin"), [2]);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0);
        window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Настройки"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var combos = window.GetLogicalDescendants().OfType<ComboBox>().ToArray();
        combos.Single(combo => combo.Items.Contains("ACTIVE_GAME_UDP.bin")).SelectedItem = "ACTIVE_GAME_UDP.bin";
        combos.Single(combo => combo.Items.Contains("b.bin")).SelectedItem = "b.bin";
        window.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Сравнить SHA-256")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Проверен: ACTIVE_GAME_UDP.bin"));
        Assert.Equal("ACTIVE_GAME_UDP.bin", window.GetLogicalDescendants().OfType<ComboBox>()
            .Single(combo => combo.Items.Contains("ACTIVE_GAME_UDP.bin")).SelectedItem);
        Assert.Equal("b.bin", window.GetLogicalDescendants().OfType<ComboBox>().Single(combo => combo.Items.Contains("b.bin")).SelectedItem);
        window.Hide();
    }

    [AvaloniaFact]
    public async Task CompletedTestEnablesApplyRecommendationWithoutLeavingThePage()
    {
        using var app = new TestApplication();
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        app.Tester.Result = new("general (ALT).bat", "", false);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0);
        var strategies = window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Стратегии"));
        strategies.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var recommendation = window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.Content as string == "Применить рекомендацию");
        Assert.False(recommendation.IsEnabled);
        app.Tester.Cleanup.TrySetResult();
        await app.ViewModel.TestStrategiesAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(recommendation.IsEnabled);
        recommendation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => app.ViewModel.SelectedStrategy?.Id == "general (alt)" && !app.ViewModel.Busy);
        Assert.Equal("general (alt)", (await app.Settings.LoadAsync(TestContext.Current.CancellationToken)).SelectedStrategyId);
        window.Hide();
    }

    [AvaloniaFact]
    public async Task HomeDiagnosticActionDisplaysTheNewReport()
    {
        using var app = new TestApplication(new Diagnostic());
        var window = new MainWindow(app.ViewModel);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0 && !app.ViewModel.Busy);
        var action = window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Проверить систему"));
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Diagnostic probe passed"));
        window.Hide();
    }

    [AvaloniaFact]
    public async Task SettingsOpenWithAMissingBinDirectory()
    {
        using var app = new TestApplication();
        var window = new MainWindow(app.ViewModel);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0);
        var settings = window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Настройки"));
        settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Активные fake payloads");
        window.Hide();
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI did not show the expected result.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class Diagnostic : IDiagnosticService
    {
        public Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiagnosticItem>>([new("Diagnostic probe passed", true, "Current report", "")]);
    }

    private sealed class PayloadRecovery : IPayloadRecoveryService
    {
        public Task<PayloadInspection> InspectAsync(string target, string? source = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PayloadInspection(target, source, "target-hash", "source-hash", "original-hash", false, true));
        public Task RestoreOriginalAsync(string target, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
