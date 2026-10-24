using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class VisualFeedbackTests
{
    [AvaloniaTheory]
    [InlineData("Стратегии", "История автоподбора", "Dark", 1180)]
    [InlineData("Стратегии", "История автоподбора", "Light", 1180)]
    [InlineData("Стратегии", "История автоподбора", "Dark", 890)]
    [InlineData("Стратегии", "История автоподбора", "Light", 890)]
    [InlineData("Настройки", "Резервные копии и перенос данных", "Dark", 1180)]
    [InlineData("Настройки", "Резервные копии и перенос данных", "Light", 1180)]
    [InlineData("Настройки", "Резервные копии и перенос данных", "Dark", 890)]
    [InlineData("Настройки", "Резервные копии и перенос данных", "Light", 890)]
    public async Task PopulatedChooserSharesTheCardContentEdges(string page, string title, string theme, int width)
    {
        using var app = new TestApplication(featureServices: true);
        var window = await OpenAsync(app, theme, width);
        try
        {
            await app.ViewModel.CreateBackupAsync();
            app.ViewModel.TestHistory.Add(new("layout", DateTimeOffset.Now, "general (ALT2).bat", true, "", "test", []));
            Navigate(window, page);
            window.UpdateLayout();
            var heading = window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == title);
            var container = Assert.IsType<StackPanel>(heading.Parent);
            var chooser = Assert.Single(container.Children.OfType<ComboBox>());
            var origin = chooser.TranslatePoint(default, container)!.Value;
            Assert.InRange(Math.Abs(origin.X), 0, 0.5);
            Assert.InRange(Math.Abs(origin.X + chooser.Bounds.Width - container.Bounds.Width), 0, 0.5);
        }
        finally { window.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData(1180)]
    [InlineData(890)]
    public async Task ReleaseButtonsHaveAVisibleGap(int width)
    {
        using var app = new TestApplication(updateService: new PausedUpdates(paused: false));
        var window = await OpenAsync(app, width: width);
        try
        {
            Navigate(window, "Настройки");
            await app.ViewModel.CheckUpdatesAsync();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var first = Find(window, "Открыть выпуск zapret");
            var second = Find(window, "Открыть выпуск Desktop");
            var firstEdge = first.TranslatePoint(new Point(first.Bounds.Width, 0), window)!.Value;
            var secondStart = second.TranslatePoint(default, window)!.Value;
            var gap = secondStart.Y > firstEdge.Y + 1
                ? secondStart.Y - firstEdge.Y - first.Bounds.Height
                : secondStart.X - firstEdge.X;
            Assert.InRange(gap, 8, 32);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task CheckingUpdatesKeepsSpinnerAcrossNavigationAndRejectsRepeatClicks()
    {
        var updates = new PausedUpdates();
        using var app = new TestApplication(updateService: updates);
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Настройки");
            Find(window, "Проверить обновления").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await updates.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AssertLoading(Find(window, "Проверить обновления"));
            Find(window, "Проверить обновления").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, updates.UpstreamCalls);
            Navigate(window, "Главная");
            Navigate(window, "Настройки");
            AssertLoading(Find(window, "Проверить обновления"));
            updates.Release.TrySetResult();
            await WaitAsync(() => !app.ViewModel.CheckingUpdates && Spinner(Find(window, "Проверить обновления")) is null);
            Assert.True(Find(window, "Проверить обновления").IsEffectivelyEnabled);
        }
        finally { updates.Release.TrySetResult(); window.Hide(); }
    }

    [AvaloniaFact]
    public async Task DiagnosticFailureRemovesSpinnerAndAllowsRetry()
    {
        var diagnostic = new PausedDiagnostic();
        using var app = new TestApplication(diagnostic);
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Диагностика");
            Find(window, "Проверить систему").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await diagnostic.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AssertLoading(Find(window, "Проверить систему"));
            diagnostic.Release.TrySetException(new IOException("Diagnostic fixture failed"));
            await WaitAsync(() => !app.ViewModel.Busy && Spinner(Find(window, "Проверить систему")) is null);
            Assert.True(Find(window, "Проверить систему").IsEffectivelyEnabled);
            Assert.Contains("Diagnostic fixture failed", app.ViewModel.Message);
        }
        finally { diagnostic.Release.TrySetResult([]); window.Hide(); }
    }

    [AvaloniaFact]
    public async Task QuickDiagnosticKeepsLoadingOnTheDestinationPageAndDoesNotQueueASecondRun()
    {
        var diagnostic = new PausedDiagnostic();
        using var app = new TestApplication(diagnostic);
        var window = await OpenAsync(app);
        try
        {
            window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content is not string &&
                b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Проверить систему"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await diagnostic.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AssertLoading(Find(window, "Проверить систему"));
            Find(window, "Проверить систему").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            diagnostic.Release.TrySetResult([]);
            await WaitAsync(() => !app.ViewModel.Busy && Spinner(Find(window, "Проверить систему")) is null);
            Assert.Equal(1, diagnostic.Calls);
        }
        finally { diagnostic.Release.TrySetResult([]); window.Hide(); }
    }

    [AvaloniaFact]
    public async Task ManualConnectionSpinnerDoesNotDisableStop()
    {
        var health = new PausedHealth();
        using var app = new TestApplication(connection: health);
        var window = await OpenAsync(app);
        try
        {
            await app.ViewModel.StartAsync();
            Dispatcher.UIThread.RunJobs();
            Find(window, "Проверить соединение").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await health.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AssertLoading(Find(window, "Проверить соединение"));
            Assert.True(Find(window, "Остановить Zapret").IsEffectivelyEnabled);
            Find(window, "Остановить Zapret").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => app.ViewModel.Status?.State == ZapretRunState.Stopped && !app.ViewModel.Busy);
            await WaitAsync(() => Spinner(Find(window, "Проверить соединение")) is null);
        }
        finally { health.Release.TrySetResult(new(DateTimeOffset.Now, [])); window.Hide(); }
    }

    [AvaloniaFact]
    public async Task StartingFromHomeKeepsTheSpinnerOnTheMatchingStrategy()
    {
        using var app = new TestApplication();
        var window = await OpenAsync(app);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Tester.Recover = async () => { entered.TrySetResult(); await release.Task; };
        try
        {
            Find(window, "Запустить Zapret").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Navigate(window, "Стратегии");
            Assert.Single(window.GetLogicalDescendants().OfType<Button>(), b => b.Content as string == "Запустить" && Spinner(b) is not null);
            Navigate(window, "Главная");
            AssertLoading(Find(window, "Запустить Zapret"));
        }
        finally { release.TrySetResult(); await WaitAsync(() => !app.ViewModel.Busy); window.Hide(); }
    }

    [AvaloniaFact]
    public async Task RenamingAListActionUpdatesItsAccessibleName()
    {
        using var app = new TestApplication();
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Списки");
            var files = window.GetLogicalDescendants().OfType<ComboBox>().Single();
            files.SelectedItem = "ipset-all.txt";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Добавить IP", AutomationProperties.GetName(Find(window, "Добавить IP")));
            Assert.Equal("Удалить выбранное", AutomationProperties.GetName(Find(window, "Удалить выбранное")));
        }
        finally { await WaitAsync(() => !app.ViewModel.Busy); window.Hide(); }
    }

    private static void AssertLoading(Button button)
    {
        Assert.NotNull(Spinner(button));
        Assert.False(button.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task CancellingPreparationRemovesItsSpinnerAfterPageRebuild()
    {
        using var response = new PausedResponse();
        using var client = new HttpClient(response);
        using var app = new TestApplication(updateClient: client);
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Настройки");
            Find(window, "Загрузить IPSet").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AssertLoading(Find(window, "Загрузить IPSet"));
            Find(window, "Отменить подготовку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !app.ViewModel.PreparingData && Spinner(Find(window, "Загрузить IPSet")) is null);
            Assert.Null(app.ViewModel.IpSetUpdatePlan);
            Assert.True(Find(window, "Загрузить IPSet").IsEffectivelyEnabled);
        }
        finally { app.ViewModel.CancelPreparation(); window.Hide(); }
    }

    [AvaloniaFact]
    public async Task StrategyTestShowsSpinnerAndKeepsCancellationAvailable()
    {
        using var app = new TestApplication(featureServices: true);
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Стратегии");
            Find(window, "Автоподбор").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await app.Tester.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AssertLoading(Find(window, "Автоподбор"));
            Assert.True(Find(window, "Отменить тест").IsEffectivelyEnabled);
            Navigate(window, "Настройки");
            Navigate(window, "Стратегии");
            AssertLoading(Find(window, "Автоподбор"));
            Find(window, "Отменить тест").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(app.Tester.Token.IsCancellationRequested);
            app.Tester.Cleanup.TrySetResult();
            await WaitAsync(() => !app.ViewModel.TestingStrategies && Spinner(Find(window, "Автоподбор")) is null);
        }
        finally { app.Tester.Cleanup.TrySetResult(); window.Hide(); }
    }

    [AvaloniaFact]
    public async Task SpinnerRotatesOnlyWhileVisibleAndAttached()
    {
        var spinner = new LoadingSpinner();
        var timer = (DispatcherTimer)typeof(LoadingSpinner).GetField("timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(spinner)!;
        var angle = typeof(LoadingSpinner).GetField("angle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var parent = new Border { Child = spinner };
        var window = new Window { Content = parent, Width = 100, Height = 100 };
        window.Show();
        try
        {
            await WaitAsync(() => (double)angle.GetValue(spinner)! > 0);
            Assert.True(timer.IsEnabled);
            window.Hide();
            Assert.False(timer.IsEnabled);
            window.Show();
            Assert.True(timer.IsEnabled);
            parent.IsVisible = false;
            Assert.False(timer.IsEnabled);
            parent.IsVisible = true;
            Assert.True(timer.IsEnabled);
            spinner.IsVisible = false;
            Assert.False(timer.IsEnabled);
            spinner.IsVisible = true;
            Assert.True(timer.IsEnabled);
            window.Content = null;
            Assert.False(timer.IsEnabled);
        }
        finally { window.Close(); }
    }

    private sealed class PausedResponse : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }
    }
    private static Control? Spinner(Button button) => button.GetVisualDescendants().OfType<Control>()
        .SingleOrDefault(c => AutomationProperties.GetAutomationId(c) == "operation-spinner" && c.IsEffectivelyVisible);
    private static Button Find(Window window, string label) => window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == label);
    private static void Navigate(Window window, string page)
    {
        window.GetLogicalDescendants().OfType<Button>().Single(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == page))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
    private static async Task<MainWindow> OpenAsync(TestApplication app, string theme = "Dark", int width = 1180)
    {
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false, CheckConnectionAfterStart = false, Theme = theme }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel) { Width = width, Height = width == 890 ? 620 : 800 };
        window.Show();
        await WaitAsync(() => app.ViewModel.Strategies.Count == 2 && !app.ViewModel.Busy);
        return window;
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Visual feedback did not settle.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class PausedUpdates(bool paused = true) : IUpdateService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int UpstreamCalls { get; private set; }
        public async Task<UpdateInfo> CheckUpstreamAsync(CancellationToken cancellationToken = default)
        {
            UpstreamCalls++;
            Entered.TrySetResult();
            if (paused) await Release.Task.WaitAsync(cancellationToken);
            return new("zapret", "1.10.4", "1.10.4", "https://github.com/Flowseal/zapret-discord-youtube/releases/tag/1.10.4", false);
        }
        public async Task<UpdateInfo> CheckDesktopAsync(string localVersion, CancellationToken cancellationToken = default)
        {
            if (paused) await Release.Task.WaitAsync(cancellationToken);
            return new("Desktop", localVersion, localVersion, "https://github.com/fecset/zapret-desktop/releases/tag/v" + localVersion, false);
        }
    }
    private sealed class PausedDiagnostic : IDiagnosticService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<DiagnosticItem>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
        { Calls++; Entered.TrySetResult(); return Release.Task; }
    }
    private sealed class PausedHealth : IConnectionHealthService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ConnectionHealthResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); return Release.Task; }
    }
}
