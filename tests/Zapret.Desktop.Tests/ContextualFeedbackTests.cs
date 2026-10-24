using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class ContextualFeedbackTests
{
    [AvaloniaFact]
    public async Task MessageDismissIsCompactAndHasAnAccessibleName()
    {
        using var app = new TestApplication();
        var window = await OpenAsync(app);
        try
        {
            app.ViewModel.ReportError("Проверка", new IOException("Ошибка"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var close = window.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Скрыть сообщение");
            Assert.InRange(close.Bounds.Width, 24, 32);
            Assert.InRange(close.Bounds.Height, 24, 32);
            Assert.NotNull(ToolTip.GetTip(close));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(((Control)close.Parent!).IsEffectivelyVisible);
        }
        finally { window.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData("Диагностика")]
    [InlineData("Списки")]
    public async Task TestRestrictionsAreExplainedAndActionsAreNotQueued(string page)
    {
        var diagnostic = new CountingDiagnostic();
        using var app = new TestApplication(diagnostic);
        await File.WriteAllTextAsync(Path.Combine(app.Distribution.ListsDirectory, "list-general.txt"), "example.com\n", TestContext.Current.CancellationToken);
        var window = await OpenAsync(app);
        if (page == "Списки") await app.ViewModel.SearchListAsync("ipset-all.txt", "");
        var test = app.ViewModel.TestStrategiesAsync();
        Task? blocked = null;
        try
        {
            await app.Tester.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Navigate(window, page);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Дождитесь завершения автоподбора", StringComparison.Ordinal) == true);
            Assert.False(Find(window, page == "Списки" ? "Найти" : "Проверить систему").IsEffectivelyEnabled);
            if (page == "Списки")
                Assert.DoesNotContain(window.GetLogicalDescendants().OfType<ListBox>(), l => l.IsEffectivelyVisible && l.Items.Contains("1.2.3.0/24"));
            blocked = page == "Списки" ? app.ViewModel.SearchListAsync("list-general.txt", "") : app.ViewModel.DiagnoseAsync();
            Assert.Same(blocked, await Task.WhenAny(blocked, Task.Delay(750, TestContext.Current.CancellationToken)));
            Assert.Equal(0, diagnostic.Calls);
        }
        finally
        {
            app.Tester.Cleanup.TrySetResult();
            await test;
            if (blocked is not null) await blocked;
            Dispatcher.UIThread.RunJobs();
            await WaitAsync(() => !app.ViewModel.Busy);
            window.Hide();
        }
        if (page == "Диагностика") Assert.Equal(0, diagnostic.Calls);
        else await WaitAsync(() => app.ViewModel.ListLines.Contains("example.com"));
    }

    [Fact]
    public async Task OrdinarySettingsDoNotCreateAutomaticCopies()
    {
        using var app = new TestApplication(featureServices: true);
        await app.ViewModel.InitializeAsync();
        app.ViewModel.Draft.Theme = "Light";
        await app.ViewModel.SaveSettingsAsync();
        await app.ViewModel.SaveSettingsAsync();
        Assert.Empty(app.ViewModel.Backups);
        Assert.Equal("Light", (await app.Settings.LoadAsync(TestContext.Current.CancellationToken)).Theme);
    }

    [Fact]
    public async Task SavingUnchangedFiltersDoesNotWriteOrBackup()
    {
        using var app = new TestApplication(featureServices: true);
        await app.ViewModel.InitializeAsync();
        var ipset = Path.Combine(app.Distribution.ListsDirectory, "ipset-all.txt");
        var bytes = await File.ReadAllBytesAsync(ipset, TestContext.Current.CancellationToken);
        var time = File.GetLastWriteTimeUtc(ipset);
        await app.ViewModel.ApplyFilterDraftAsync();
        Assert.Empty(app.ViewModel.Backups);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ipset, TestContext.Current.CancellationToken));
        Assert.Equal(time, File.GetLastWriteTimeUtc(ipset));
        Assert.False(File.Exists(Path.Combine(app.Distribution.Root, "utils", "game_filter.enabled")));
    }

    [Fact]
    public async Task InvalidAndDuplicateListEditsDoNotCreateAutomaticCopies()
    {
        using var app = new TestApplication(featureServices: true);
        var path = Path.Combine(app.Distribution.ListsDirectory, "list-general.txt");
        await File.WriteAllTextAsync(path, "example.com\n", TestContext.Current.CancellationToken);
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.AddListEntryAsync("list-general.txt", "not a valid domain");
        Assert.Empty(app.ViewModel.Backups);
        await app.ViewModel.AddListEntryAsync("list-general.txt", "EXAMPLE.COM");
        Assert.Empty(app.ViewModel.Backups);
        await app.ViewModel.RemoveListEntryAsync("list-general.txt", "missing.example");
        Assert.Empty(app.ViewModel.Backups);
        Assert.Equal("example.com\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        await app.ViewModel.AddListEntryAsync("list-general.txt", "new.example");
        Assert.Single(app.ViewModel.Backups);
    }

    [AvaloniaFact]
    public async Task RestartNoticeAppearsOnlyAfterSavingChangedFiltersAndDisappearsAfterRestart()
    {
        using var app = new TestApplication(featureServices: true);
        var window = await OpenAsync(app);
        try
        {
            await app.ViewModel.StartAsync();
            Navigate(window, "Настройки");
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "После изменения фильтра перезапустите работающую стратегию.");
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Control>(), c => AutomationProperties.GetAutomationId(c) == "filter-restart-notice");
            app.ViewModel.Draft.GameMode = GameFilterMode.All;
            app.ViewModel.Draft.TcpPorts = "4000";
            await app.ViewModel.ApplyFilterDraftAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetLogicalDescendants().OfType<Control>(), c => AutomationProperties.GetAutomationId(c) == "filter-restart-notice");
            await app.ViewModel.ApplyAndRestartAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Control>(), c => AutomationProperties.GetAutomationId(c) == "filter-restart-notice");
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task UnchangedHostsHaveAClearStatusAndDisabledApply()
    {
        using var client = new HttpClient(new HostsResponse());
        using var app = new TestApplication(updateClient: client, featureServices: true);
        await File.WriteAllTextAsync(app.HostsPath, "127.0.0.1 localhost\n", TestContext.Current.CancellationToken);
        var window = await OpenAsync(app);
        try
        {
            await app.ViewModel.PreviewHostsAsync();
            await app.ViewModel.ApplyHostsAsync();
            await app.ViewModel.PreviewHostsAsync();
            Navigate(window, "Настройки");
            Assert.False(Find(window, "Применить изменения hosts").IsEffectivelyEnabled);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Изменений нет", StringComparison.Ordinal) == true);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task ChangedHostsApplyBecomesAvailableWhenAutoTestEndsOnSettings()
    {
        using var client = new HttpClient(new HostsResponse());
        using var app = new TestApplication(updateClient: client);
        var window = await OpenAsync(app);
        var test = Task.CompletedTask;
        try
        {
            await app.ViewModel.PreviewHostsAsync();
            test = app.ViewModel.TestStrategiesAsync();
            await app.Tester.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Navigate(window, "Настройки");
            Assert.False(Find(window, "Применить изменения hosts").IsEffectivelyEnabled);
            app.ViewModel.CancelStrategyTest();
            app.Tester.Cleanup.TrySetResult();
            await test;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find(window, "Применить изменения hosts").IsEffectivelyEnabled);
        }
        finally { app.Tester.Cleanup.TrySetResult(); await test; window.Hide(); }
    }

    private static Button Find(Window window, string label) => window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == label);
    private static void Navigate(Window window, string title)
    {
        window.GetLogicalDescendants().OfType<Button>().Single(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == title))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
    private static async Task<MainWindow> OpenAsync(TestApplication app)
    {
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false, CheckConnectionAfterStart = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitAsync(() => app.ViewModel.Strategies.Count == 2 && !app.ViewModel.Busy);
        return window;
    }
    private static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Contextual UI did not settle.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class CountingDiagnostic : IDiagnosticService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<DiagnosticItem>>([]); }
    }
    private sealed class HostsResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("203.0.113.5 youtube.com\n") });
    }
}
