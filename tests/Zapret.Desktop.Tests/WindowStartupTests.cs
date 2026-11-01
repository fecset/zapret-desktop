using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class WindowStartupTests
{
    [AvaloniaTheory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task OnlyWindowsAutostartWithBothPreferencesStartsHidden(bool autoStart, bool enabled, bool inTray, bool visible)
    {
        using var app = new TestApplication();
        var settings = new DesktopSettings { StartWithWindows = enabled, StartInTray = inTray, StartZapretOnLaunch = true,
            CheckUpdates = false, CheckConnectionAfterStart = false, MinimizeToTray = false };
        await app.Settings.SaveAsync(settings);
        var window = new MainWindow(app.ViewModel, exitApplication: () => { });
        try
        {
            await WindowStartup.StartAsync(window, settings, autoStart ? ["--autostart"] : []);
            await UntilAsync(() => app.Process.StartCalls == 1 && !app.ViewModel.Busy);
            Assert.Equal(visible, window.IsVisible);
            Assert.Equal(2, app.ViewModel.Strategies.Count);
            window.Show();
            await window.InitializeAsync();
            Assert.True(window.IsVisible);
            Assert.Equal(1, app.Process.StartCalls);
            Assert.Equal(2, app.ViewModel.Strategies.Count);
        }
        finally { await window.ExitAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task StartupCheckboxRevealsTheDependentChoiceAndSavesIt(string theme)
    {
        await using var session = await PreviewSession.OpenAsync(new(Theme: theme));
        var window = session.Window;
        window.GetLogicalDescendants().OfType<Button>().Single(b =>
            AutomationProperties.GetAutomationId(b) == "Navigation:Настройки")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        var windows = FindCheck(window, "Запускать приложение вместе с Windows");
        var startInTray = FindCheck(window, "При автозапуске сворачивать окно в трей");
        Assert.False(startInTray.IsVisible);
        windows.IsChecked = true;
        Assert.True(startInTray.IsVisible);
        startInTray.IsChecked = true;
        windows.IsChecked = false;
        Assert.False(startInTray.IsVisible);
        windows.IsChecked = true;
        Assert.True(startInTray.IsChecked);
        window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Сохранить настройки")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => session.ViewModel.Settings.StartInTray && !session.ViewModel.Busy);
        var saved = await session.Fixture.Settings.LoadAsync();
        Assert.True(saved.StartWithWindows);
        Assert.True(saved.StartInTray);
        Pump(window);
        var scroll = window.GetLogicalDescendants().OfType<ScrollViewer>().Single(c => AutomationProperties.GetAutomationId(c) == "PageScroll");
        var heading = window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Приложение");
        var point = heading.TranslatePoint(default, (Visual)scroll.Content!)!.Value;
        scroll.Offset = new Vector(0, point.Y - 24);
        Pump(window);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var directory = Environment.GetEnvironmentVariable("ZAPRET_UI_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "startup-tray");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, theme + "-startup-tray.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static CheckBox FindCheck(Window window, string text) => window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Content as string == text);
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!ready()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
}
