using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Core;

namespace Zapret.Desktop.Tests;

public sealed class PreviewGalleryTests
{
    public static IEnumerable<object[]> Scenarios()
    {
        foreach (var theme in new[] { "Dark", "Light" })
        {
            foreach (var engine in Enum.GetValues<PreviewEngine>())
                yield return [theme, "engine", (int)engine];
            foreach (var connection in Enum.GetValues<PreviewConnection>().Where(c => c != PreviewConnection.Never))
                yield return [theme, "connection", (int)connection];
            foreach (var updates in Enum.GetValues<PreviewUpdates>().Where(u => u != PreviewUpdates.Never))
                yield return [theme, "updates", (int)updates];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Scenarios))]
    public async Task EverySelectableScenarioOpensTheProductionWindow(string theme, string group, int value)
    {
        var config = new PreviewConfiguration(Theme: theme, Compact: true);
        config = group switch {
            "engine" => config with { Engine = (PreviewEngine)value },
            "connection" => config with { Engine = PreviewEngine.Running, Connection = (PreviewConnection)value },
            _ => config with { Updates = (PreviewUpdates)value }
        };
        await using var session = await PreviewSession.OpenAsync(config);
        Pump(session.Window);
        Assert.True(session.Window.IsVisible);
        Assert.Equal("1.10.4", session.ViewModel.UpstreamVersion);
        Assert.False(session.ViewModel.Settings.StartWithWindows);
        Assert.False(session.ViewModel.Settings.CheckUpdates);
        Assert.Equal("NotInstalled", session.Fixture.Service.State);
        Assert.Equal(0, session.Fixture.Process.StartCalls);
        Assert.StartsWith(Path.GetTempPath(), session.Fixture.Distribution.Root, StringComparison.OrdinalIgnoreCase);
        if (config.Engine == PreviewEngine.StrategyPending)
            Assert.NotEqual(session.ViewModel.Status!.StrategyId, session.ViewModel.SelectedStrategy!.Id);
        if (config.Engine == PreviewEngine.FiltersPending) Assert.True(session.ViewModel.FiltersNeedRestart);
        if (config.Engine == PreviewEngine.MissingFiles) Assert.NotEmpty(session.ViewModel.StartupWarnings);
        if (config.Connection == PreviewConnection.Checking) Assert.True(session.ViewModel.CheckingConnection);
        if (config.Connection == PreviewConnection.Error) Assert.NotNull(session.ViewModel.ConnectionError);
        if (config.Updates == PreviewUpdates.Checking) Assert.True(session.ViewModel.CheckingUpdates);
        if (config.Updates is PreviewUpdates.Downloading or PreviewUpdates.Validating) Assert.True(session.ViewModel.PreparingData);
        if (config.Updates == PreviewUpdates.Ready) Assert.NotNull(session.ViewModel.DesktopUpdatePlan);
        if (config.Updates == PreviewUpdates.PreparationError) Assert.True(session.ViewModel.MessageIsError);
        if (config.Updates == PreviewUpdates.Installed) Assert.True(session.ViewModel.DesktopUpdateOutcome!.Success);
        if (config.Updates == PreviewUpdates.InstallError) Assert.False(session.ViewModel.DesktopUpdateOutcome!.Success);
        Capture(session.Window, $"{theme}-{group}-{value}-home");
        if (group == "updates")
        {
            session.NavigateToUpdates();
            Pump(session.Window);
            Assert.Contains(session.Window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Обновления");
            Capture(session.Window, $"{theme}-{group}-{value}-settings");
        }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task PreviewButtonsStartAndStopOnlyTheIsolatedEngineAndExitKeepsTheControllerOpen(string theme)
    {
        await using var session = await PreviewSession.OpenAsync(new(Theme: theme));
        var status = session.Window.GetLogicalDescendants().OfType<Border>().Single(b =>
            AutomationProperties.GetAutomationId(b) == "SidebarStatus");
        var stoppedBackground = status.Background!.ToString();
        var start = session.Window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Запустить Zapret");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => session.ViewModel.Status?.State == ZapretRunState.Running && !session.ViewModel.Busy);
        Pump(session.Window);
        Assert.Equal(ZapretRunState.Running, session.ViewModel.Status!.State);
        Assert.Equal("general", session.ViewModel.Status.StrategyId);
        Assert.Equal(0, session.Fixture.Process.StartCalls);
        var runningBackground = status.Background!.ToString();
        Assert.NotEqual(stoppedBackground, runningBackground);
        Capture(session.Window, theme + "-sidebar-running");
        var stop = session.Window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Остановить Zapret");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => session.ViewModel.Status?.State == ZapretRunState.Stopped && !session.ViewModel.Busy);
        Pump(session.Window);
        Assert.Equal(ZapretRunState.Stopped, session.ViewModel.Status!.State);
        Assert.Equal(stoppedBackground, status.Background!.ToString());
        Capture(session.Window, theme + "-sidebar-stopped");
        var controller = new Window();
        controller.Show();
        try
        {
            await session.Window.ExitAsync();
            Assert.False(session.Window.IsVisible);
            Assert.True(controller.IsVisible);
        }
        finally { controller.Close(); }
    }

    [AvaloniaFact]
    public async Task GalleryCanReplaceAWindowWhileAnUpdateCheckIsPending()
    {
        var gallery = new PreviewGalleryWindow();
        gallery.Show();
        try
        {
            await UntilAsync(() => gallery.Session is not null);
            Pump(gallery);
            Capture(gallery, "gallery-controls");
            var picker = gallery.GetLogicalDescendants().OfType<ComboBox>().Single(c =>
                AutomationProperties.GetAutomationId(c) == "PreviewUpdates");
            picker.SelectedItem = PreviewCatalog.Updates.Single(c => c.Value == PreviewUpdates.Checking);
            await gallery.ShowSelectionAsync();
            Assert.True(gallery.Session!.ViewModel.CheckingUpdates);
            picker.SelectedItem = PreviewCatalog.Updates.Single(c => c.Value == PreviewUpdates.Desktop);
            await gallery.ShowSelectionAsync();
            Assert.False(gallery.Session!.ViewModel.CheckingUpdates);
            Assert.True(gallery.Session.ViewModel.DesktopUpdate!.Available);
        }
        finally
        {
            gallery.Close();
            await UntilAsync(() => !gallery.IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheGalleryDuringStartupAlsoClosesThePreview()
    {
        var gallery = new PreviewGalleryWindow();
        gallery.Show();
        gallery.Close();
        await UntilAsync(() => !gallery.IsVisible);
        Assert.NotNull(gallery.Session);
        Assert.False(gallery.Session.Window.IsVisible);
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("ZAPRET_UI_SCREENSHOTS");
        if (directory is null) return;
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
