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
    public async Task PayloadReplacementCanBeUndoneWithoutChoosingTechnicalTargets()
    {
        using var app = new TestApplication(featureServices: true);
        Directory.CreateDirectory(app.Distribution.BinDirectory);
        File.WriteAllBytes(Path.Combine(app.Distribution.BinDirectory, "a.bin"), [1]);
        File.WriteAllBytes(Path.Combine(app.Distribution.BinDirectory, "b.bin"), [2]);
        var discord = Path.Combine(app.Distribution.BinDirectory, "ACTIVE_DISCORD_UDP.bin");
        var game = Path.Combine(app.Distribution.BinDirectory, "ACTIVE_GAME_UDP.bin");
        File.WriteAllBytes(discord, [9]);
        File.WriteAllBytes(game, [8]);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0);
        window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Настройки"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Button>(), button =>
            button.Content as string == "Отменить замену для Discord");
        window.GetLogicalDescendants().OfType<ComboBox>().Single(combo => combo.Items.Contains("b.bin")).SelectedItem = "b.bin";
        window.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Применить для Discord")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => window.GetLogicalDescendants().OfType<Button>().Any(button =>
            button.Content as string == "Отменить замену для Discord"));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(discord));
        Assert.Equal("b.bin", window.GetLogicalDescendants().OfType<ComboBox>().Single(combo => combo.Items.Contains("b.bin")).SelectedItem);
        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Button>(), button => button.Content as string == "Сравнить SHA-256");
        window.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Отменить замену для Discord")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => !app.ViewModel.Busy && File.ReadAllBytes(discord).SequenceEqual(new byte[] { 9 }));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Button>(), button =>
            button.Content as string == "Отменить замену для Discord");
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(game));
        window.Hide();
    }

    [AvaloniaFact]
    public Task CreatingBackupKeepsSettingsScrollPosition() => AssertSettingsActionKeepsScrollAsync("Создать копию");

    [AvaloniaFact]
    public Task SavingSettingsKeepsScrollPositionAfterRebuildingTheTheme() => AssertSettingsActionKeepsScrollAsync("Сохранить настройки");

    private static async Task AssertSettingsActionKeepsScrollAsync(string label)
    {
        using var app = new TestApplication(featureServices: true);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0 && !app.ViewModel.Busy);
        Navigate(window, "Настройки");
        window.UpdateLayout();
        var scroll = PageScroll(window);
        var action = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == label);
        action.Focus();
        action.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var offset = scroll.Offset.Y;
        Assert.True(offset > 0);
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => app.ViewModel.Backups.Count > 0 && !app.ViewModel.Busy);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(offset, PageScroll(window).Offset.Y);
        window.Hide();
    }

    [AvaloniaFact]
    public async Task BackgroundStatusRefreshKeepsScrollAndNavigationStartsAtTop()
    {
        using var app = new TestApplication();
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0 && !app.ViewModel.Busy);
        window.Height = 620;
        window.UpdateLayout();
        var scroll = PageScroll(window);
        scroll.Offset = new Vector(0, 150);
        window.UpdateLayout();
        Assert.Equal(150, scroll.Offset.Y);
        app.Process.NormalState = ZapretRunState.External;
        await app.ViewModel.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(150, PageScroll(window).Offset.Y);
        Navigate(window, "Настройки");
        window.UpdateLayout();
        Assert.Equal(0, PageScroll(window).Offset.Y);
        window.Hide();
    }

    private static ScrollViewer PageScroll(Window window) => window.GetLogicalDescendants().OfType<ScrollViewer>()
        .First(scroll => scroll.Content is StackPanel);

    [AvaloniaFact]
    public async Task LongHistoryResultLeavesTheWholeStrategiesPageScrollable()
    {
        using var app = new TestApplication(featureServices: true);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0 && !app.ViewModel.Busy);
        app.Tester.Result = new("general.bat", "", false);
        app.Tester.ReportChecks = progress =>
        {
            for (var i = 0; i < 30; i++) progress.Report(new($"general-{i}.bat", StrategyCheckState.Completed, 36, 0, 16, 0));
        };
        app.Tester.Cleanup.TrySetResult();
        await app.ViewModel.TestStrategiesAsync();
        Navigate(window, "Стратегии");
        var open = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Открыть результат");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => app.ViewModel.HistoryDetails is not null && !app.ViewModel.Busy);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var scroll = PageScroll(window);
        var currentOpen = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Открыть результат");
        Assert.Contains(currentOpen, scroll.GetLogicalDescendants());
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        scroll.ScrollToEnd();
        window.UpdateLayout();
        var lastStart = window.GetLogicalDescendants().OfType<Button>().Last(button => button.Content as string == "Запустить");
        var position = lastStart.TranslatePoint(new Point(0, 0), scroll);
        Assert.NotNull(position);
        Assert.InRange(position.Value.Y, 0, scroll.Bounds.Height - lastStart.Bounds.Height);
        window.Hide();
    }

    private static void Navigate(Window window, string section) => window.GetLogicalDescendants().OfType<Button>().Single(button =>
        button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == section))
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task HostsCompletionRefreshesDiagnosticsWhenTheUserOpensThatPageDuringTheUpdate()
    {
        var diagnostic = new DelayedDiagnostic();
        using var client = new HttpClient(new HostsResponse());
        using var app = new TestApplication(diagnostic, updateClient: client);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitForAsync(() => app.ViewModel.Strategies.Count > 0 && !app.ViewModel.Busy);
        await app.ViewModel.DiagnoseAsync();
        await app.ViewModel.PreviewHostsAsync();
        diagnostic.Defer = true;
        var applying = app.ViewModel.ApplyHostsAsync();
        await WaitForAsync(() => app.ViewModel.HostsPreview is null && app.ViewModel.Busy);
        Navigate(window, "Диагностика");
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Old hosts report");
        diagnostic.Completion.SetResult([new("Hosts", true, "Updated hosts report", "")]);
        await applying;
        await WaitForAsync(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Updated hosts report"));
        window.Hide();
    }

    private sealed class DelayedDiagnostic : IDiagnosticService
    {
        public bool Defer { get; set; }
        public TaskCompletionSource<IReadOnlyList<DiagnosticItem>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default) =>
            Defer ? Completion.Task : Task.FromResult<IReadOnlyList<DiagnosticItem>>([new("Hosts", false, "Old hosts report", "")]);
    }

    private sealed class HostsResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("162.159.138.232 discord.com\n") });
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

}
