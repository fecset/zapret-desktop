using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class NetworkPolicyTests
{
    [AvaloniaFact]
    public async Task ReleasePagesHaveAnExplicitActionAfterChecking()
    {
        using var app = new TestApplication(updateService: new Updates());
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var opened = new List<Uri>();
        var window = new MainWindow(app.ViewModel, opened.Add);
        window.Show();
        try
        {
            await WaitAsync(() => app.ViewModel.Strategies.Count > 0 && !app.ViewModel.Busy);
            window.GetLogicalDescendants().OfType<Button>().Single(b =>
                b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Настройки"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await app.ViewModel.CheckUpdatesAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(opened);
            var release = Assert.Single(window.GetLogicalDescendants().OfType<Button>(), b => b.Content as string == "Открыть выпуск Desktop");
            release.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("https://github.com/fecset/zapret-desktop/releases/tag/v0.3.0", Assert.Single(opened).AbsoluteUri);
        }
        finally { window.Hide(); }
    }

    private sealed class Updates : IUpdateService
    {
        public Task<UpdateInfo> CheckUpstreamAsync(CancellationToken cancellationToken = default) => Task.FromResult(
            new UpdateInfo("zapret", "1.10.4", "1.10.4", "https://github.com/Flowseal/zapret-discord-youtube/releases/tag/1.10.4", false));
        public Task<UpdateInfo> CheckDesktopAsync(string localVersion, CancellationToken cancellationToken = default) => Task.FromResult(
            new UpdateInfo("Zapret Desktop", localVersion, "0.3.0", "https://github.com/fecset/zapret-desktop/releases/tag/v0.3.0", true));
    }

    [AvaloniaFact]
    public async Task AutomaticConnectionCheckCanBeDisabledAndRoundTripsThroughBackup()
    {
        var health = new CountingHealth();
        using var app = new TestApplication(featureServices: true, connection: health);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel);
        window.Show();
        try
        {
            await WaitAsync(() => app.ViewModel.Strategies.Count > 0 && !app.ViewModel.Busy);
            var navigation = window.GetLogicalDescendants().OfType<Button>().Single(b =>
                b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Настройки"));
            navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var check = Assert.Single(window.GetLogicalDescendants().OfType<CheckBox>(),
                c => c.Content as string == "Проверять доступность сервисов после запуска Zapret");
            check.IsChecked = false;
            window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Сохранить настройки")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => app.ViewModel.Message == "Настройки сохранены" && !app.ViewModel.Busy);
            var saved = await app.Settings.LoadAsync(TestContext.Current.CancellationToken);
            Assert.False(saved.CheckConnectionAfterStart);
            await app.ViewModel.StartAsync();
            Assert.Equal(0, health.Calls);
            await app.ViewModel.CheckConnectionAsync();
            Assert.Equal(1, health.Calls);
            await app.ViewModel.StopAsync();
            using var archive = new MemoryStream();
            await app.ViewModel.ExportUserDataAsync(archive);
            archive.Position = 0;
            await app.ViewModel.ImportUserDataAsync(archive);
            Assert.False((await app.Settings.LoadAsync(TestContext.Current.CancellationToken)).CheckConnectionAfterStart);
        }
        finally { window.Hide(); }
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class CountingHealth : IConnectionHealthService
    {
        public int Calls { get; private set; }
        public Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ConnectionHealthResult(DateTimeOffset.UtcNow, [])); }
    }
}
