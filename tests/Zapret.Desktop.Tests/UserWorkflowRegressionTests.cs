using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class UserWorkflowRegressionTests
{
    [AvaloniaFact]
    public async Task IdenticalErrorsAreVisibleAgainAfterDismissal()
    {
        using var app = new TestApplication();
        var window = await OpenAsync(app);
        try
        {
            app.ViewModel.ReportError("Проверка", new IOException("Одинаковая ошибка"));
            await DrainAsync();
            var dismiss = Assert.Single(window.GetLogicalDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Скрыть сообщение");
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            app.ViewModel.ReportError("Проверка", new IOException("Одинаковая ошибка"));
            await DrainAsync();
            Assert.True(((Control)dismiss.Parent!).IsEffectivelyVisible);
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("feedbackTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.False(timer.IsEnabled);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task UnrelatedBackupActionKeepsUnsavedSettingsAndPorts()
    {
        using var app = new TestApplication(featureServices: true);
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Настройки");
            window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "TCP порты").Text = "4000";
            window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Запускать Zapret при открытии приложения").IsChecked = true;
            Click(window, "Создать копию");
            await WaitAsync(() => app.ViewModel.Backups.Count > 0 && !app.ViewModel.Busy);
            await DrainAsync();
            Assert.Equal("4000", window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "TCP порты").Text);
            Assert.True(window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Запускать Zapret при открытии приложения").IsChecked);
            Assert.False((await app.Settings.LoadAsync()).StartZapretOnLaunch);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task InvalidVisiblePortsDoNotRestartOrChangeSavedFilters()
    {
        using var app = new TestApplication();
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Настройки");
            window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "TCP порты").Text = "bad ports";
            var ipset = await File.ReadAllBytesAsync(Path.Combine(app.Distribution.ListsDirectory, "ipset-all.txt"));
            Click(window, "Применить и перезапустить Zapret");
            await DrainAsync();
            Assert.Equal(0, app.Process.StartCalls);
            Assert.StartsWith("Ошибка:", app.ViewModel.Message);
            Assert.Equal(ipset, await File.ReadAllBytesAsync(Path.Combine(app.Distribution.ListsDirectory, "ipset-all.txt")));
            Assert.False(File.Exists(Path.Combine(app.Distribution.Root, "utils", "game_filter.enabled")));
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task ImportWhileHomeIsVisibleKeepsTheRestoredStrategy()
    {
        using var app = new TestApplication(featureServices: true);
        var window = await OpenAsync(app);
        try
        {
            await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(s => s.Id == "general (alt)"));
            using var exported = new MemoryStream();
            await app.ViewModel.ExportUserDataAsync(exported);
            await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(s => s.Id == "general"));
            Dispatcher.UIThread.RunJobs();
            Navigate(window, "Настройки");
            using var source = new PausingReadStream(exported.ToArray());
            var import = app.ViewModel.ImportUserDataAsync(source);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Navigate(window, "Главная");
            source.Resume.TrySetResult();
            await import;
            await DrainAsync();
            Assert.Equal("general (alt)", (await app.Settings.LoadAsync()).SelectedStrategyId);
            Assert.Equal("general (alt)", app.ViewModel.SelectedStrategy?.Id);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task InvalidListEntryIsKeptSoTheUserCanCorrectIt()
    {
        using var app = new TestApplication();
        await File.WriteAllTextAsync(Path.Combine(app.Distribution.ListsDirectory, "list-general.txt"), "example.com\n");
        var window = await OpenAsync(app);
        try
        {
            Navigate(window, "Списки");
            await DrainAsync();
            var entry = window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Например, example.com");
            entry.Text = "not a valid domain";
            Click(window, "Добавить домен");
            await DrainAsync();
            Assert.Contains("Ошибка:", app.ViewModel.Message);
            Assert.Equal("not a valid domain", entry.Text);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task ApplyAndRestartUsesThePortValuesVisibleInTheEditor()
    {
        using var app = new TestApplication();
        var window = await OpenAsync(app);
        try
        {
            await app.ViewModel.ServiceAsync("install");
            Navigate(window, "Настройки");
            var controls = window.GetLogicalDescendants().ToArray();
            controls.OfType<ComboBox>().Single(c => c.SelectedItem is GameFilterMode).SelectedItem = GameFilterMode.All;
            controls.OfType<TextBox>().Single(t => t.PlaceholderText == "TCP порты").Text = "4000";
            controls.OfType<TextBox>().Single(t => t.PlaceholderText == "UDP порты").Text = "6000";
            Click(window, "Применить и перезапустить Zapret");
            await WaitAsync(() => app.ViewModel.Message == "Выбранная стратегия и текущие фильтры применены" && !app.ViewModel.Busy);
            Assert.Contains("--wf-tcp=80,4000", app.Service.RunningStrategy!.Arguments);
            Assert.Contains("--filter-udp=6000", app.Service.RunningStrategy.Arguments);
        }
        finally { window.Hide(); }
    }

    private static async Task<MainWindow> OpenAsync(TestApplication app)
    {
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false, SelectedStrategyId = "general" });
        var window = new MainWindow(app.ViewModel);
        window.Show();
        await WaitAsync(() => app.ViewModel.Strategies.Count == 2 && !app.ViewModel.Busy);
        await DrainAsync();
        return window;
    }
    private static void Navigate(MainWindow window, string title)
    {
        var button = window.GetLogicalDescendants().OfType<Button>().Single(b =>
            b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == title));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
    private static void Click(MainWindow window, string label) => window.GetLogicalDescendants().OfType<Button>()
        .Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Audit fixture did not settle.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task DrainAsync()
    {
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class PausingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
