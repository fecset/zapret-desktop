using Zapret.Core;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class RecoveryTests
{
    [AvaloniaFact]
    public async Task RecoveryFromTheWindowRequiresExplicitConsent()
    {
        var tester = new MissingBackupTester();
        using var app = new TestApplication(featureServices: true, strategyTester: tester);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false }, TestContext.Current.CancellationToken);
        await app.ViewModel.CreateBackupAsync();
        tester.Missing = true;
        var window = new MainWindow(app.ViewModel);
        window.Show();
        try
        {
            await WaitAsync(() => app.ViewModel.RecoveryRequired && app.ViewModel.Strategies.Count == 2 && !app.ViewModel.Busy);
            window.GetLogicalDescendants().OfType<Button>().Single(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Настройки"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var consent = window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Подтверждаю восстановление данных из выбранной копии");
            var restore = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Восстановить выбранную копию");
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(tester.Confirmed);
            Assert.True(app.ViewModel.RecoveryRequired);
            Assert.StartsWith("Ошибка: Восстановление не подтверждено", app.ViewModel.Message);
            consent.IsChecked = true;
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => tester.Confirmed && !app.ViewModel.Busy);
            Assert.False(app.ViewModel.RecoveryRequired);
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

    [Fact]
    public async Task SelectedBackupResolvesRecoveryAndLaunchRemainsBlockedUntilItSucceeds()
    {
        var tester = new MissingBackupTester();
        using var app = new TestApplication(featureServices: true, strategyTester: tester);
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.CreateBackupAsync();
        var backup = Assert.Single(app.ViewModel.Backups);
        tester.Missing = true;
        await app.ViewModel.StartAsync();
        Assert.Equal(0, app.Process.StartCalls);
        await app.ViewModel.RestoreBackupAsync(backup.Id);
        Assert.True(tester.Confirmed);
        await app.ViewModel.StartAsync();
        Assert.Equal(1, app.Process.StartCalls);
    }

    private sealed class MissingBackupTester : IStrategyTester
    {
        public bool Missing { get; set; }
        public bool Confirmed { get; private set; }
        public Task RecoverInterruptedTestAsync(CancellationToken cancellationToken = default) => Missing
            ? Task.FromException(new InterruptedTestRecoveryException("Missing test backup")) : Task.CompletedTask;
        public Task ConfirmRestoredIpSetAsync(CancellationToken cancellationToken = default)
        { Confirmed = true; Missing = false; return Task.CompletedTask; }
        public Task<StrategyTestResult> RunAsync(IProgress<string> progress, IProgress<StrategyCheckProgress> strategyProgress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
