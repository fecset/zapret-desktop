using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Zapret.Core;
using Zapret.TelegramProxy;
namespace Zapret.Desktop.Tests;

public sealed class TelegramProxyTests
{
    [AvaloniaFact]
    public async Task FirstRunAndTrayAreOffAndExplicitStartUsesActualConfiguration()
    {
        await using var server = new TelegramProxyServer(); var store = new MemoryStore(); var proxy = new TelegramProxyViewModel(server, store);
        await proxy.InitializeAsync(); using var tray = new TelegramProxyTrayMenu(proxy, () => { }, _ => { });
        Assert.Equal(TelegramProxyState.Stopped, proxy.Snapshot.State); Assert.Null(store.Value); Assert.False(proxy.CanConnect); Assert.False(tray.Connect.IsEnabled);
        await proxy.SaveAsync(proxy.Draft with { Port = OtherPort() });
        await proxy.StartAsync(); Dispatcher.UIThread.RunJobs(); tray.Refresh();
        Assert.True(proxy.CanConnect); Assert.NotNull(store.Value); Assert.False(store.Value.AutoStart); Assert.StartsWith("dd", proxy.ConnectionSecret); Assert.True(tray.Connect.IsEnabled);
        var link = proxy.ConnectionUri; var secret = proxy.ConnectionSecret;
        await proxy.SaveAsync(proxy.Draft with { Port = OtherPort(), AutoStart = true });
        Assert.Equal(link, proxy.ConnectionUri); Assert.Equal(secret, proxy.ConnectionSecret);
        await proxy.StopAsync(); tray.Refresh(); Assert.False(tray.Connect.IsEnabled);
    }
    [AvaloniaFact]
    public async Task ZapretStartStopAndSelectionDoNotStopProxyAndProxyStopDoesNotStopZapret()
    {
        await using var server = new TelegramProxyServer(); var proxy = new TelegramProxyViewModel(server, new MemoryStore { Value = new TelegramProxyOptions { Port = OtherPort(), Secret = new string('1',32) } }); using var app = new TestApplication(telegramProxy: proxy);
        await app.ViewModel.InitializeAsync(); await proxy.StartAsync();
        await app.ViewModel.StartAsync(); await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Last()); await app.ViewModel.StopAsync();
        Assert.Equal(TelegramProxyState.Running, proxy.Snapshot.State);
        await app.ViewModel.StartAsync(); var calls = app.Process.StopCalls; await proxy.StopAsync(); Assert.Equal(calls, app.Process.StopCalls);
        await proxy.StartAsync(); Assert.True(await app.ViewModel.StopOwnedForExitAsync()); await proxy.ShutdownAsync(); Assert.Equal(TelegramProxyState.Stopped, proxy.Snapshot.State);
        Assert.DoesNotContain(app.ViewModel.Logs, x => x.Contains(proxy.Draft.Secret, StringComparison.Ordinal));
    }
    [AvaloniaFact]
    public async Task ExplicitOptInIsRequiredForAutostartAndRegenerationIsSafe()
    {
        await using var server = new TelegramProxyServer(); var store = new MemoryStore { Value = new TelegramProxyOptions { Port = OtherPort(), Secret = new string('1',32), AutoStart = true } };
        var proxy = new TelegramProxyViewModel(server, store); await proxy.InitializeAsync(); Assert.True(proxy.CanConnect);
        var secret = proxy.ConnectionSecret; await proxy.RegenerateSecretAsync(); Assert.Null(proxy.Error); Assert.NotEqual(secret, proxy.ConnectionSecret); Assert.True(proxy.CanConnect);
        Assert.Equal("dd" + store.Value.Secret, proxy.ConnectionSecret);
        await proxy.StopAsync(); await proxy.RegenerateSecretAsync(); Assert.NotEqual(new string('1',32), store.Value.Secret); Assert.False(proxy.CanConnect);
    }
    [AvaloniaTheory]
    [InlineData("Dark",1440,900)] [InlineData("Light",1440,900)] [InlineData("Dark",1200,800)] [InlineData("Light",1200,800)]
    public async Task PageUsesExistingThemeAndShowsRealStates(string theme, int width, int height)
    {
        await using var server = new TelegramProxyServer(); var proxy = new TelegramProxyViewModel(server,new MemoryStore { Value = new TelegramProxyOptions { Port = OtherPort(), Secret = new string('1',32) } }); using var app = new TestApplication(telegramProxy: proxy);
        await app.Settings.SaveAsync(new DesktopSettings { Theme = theme, CheckUpdates = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel) { Width = width, Height = height }; window.Show(); await window.InitializeAsync(); window.OpenTelegramProxy(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            TextBlock Status() => window.GetLogicalDescendants().OfType<TextBlock>().Single(x => AutomationProperties.GetAutomationId(x) == "TelegramStatus");
            Assert.Equal("TG ПРОКСИ ВЫКЛЮЧЕН", Status().Text);
            var secret = window.GetLogicalDescendants().OfType<TextBox>().Single(x => AutomationProperties.GetAutomationId(x) == "TelegramKey"); Assert.Equal('\0',secret.PasswordChar); Assert.Equal(proxy.DisplayKey, secret.Text);
            await proxy.StartAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.Equal("TG ПРОКСИ АКТИВЕН",Status().Text);
            Assert.Equal("Ожидание Telegram", proxy.TrafficStatus);
            var output = Environment.GetEnvironmentVariable("TELEGRAM_PROXY_SCREENSHOTS");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output); Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(output,$"telegram-{theme}-{width}.png"), new PngBitmapEncoderOptions());
            }
            var expander = window.GetLogicalDescendants().OfType<Expander>().Single(); expander.IsExpanded = true; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            if (!string.IsNullOrEmpty(output))
            {
                var scroll = window.GetLogicalDescendants().OfType<ScrollViewer>().Single(x => AutomationProperties.GetAutomationId(x) == "PageScroll");
                scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(Path.Combine(output,$"telegram-settings-{theme}-{width}.png"),new PngBitmapEncoderOptions());
            }
            await proxy.StopAsync(); Dispatcher.UIThread.RunJobs(); Assert.Equal("TG ПРОКСИ ВЫКЛЮЧЕН",Status().Text);
            using var occupied = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, proxy.Draft.Port); occupied.Start();
            await proxy.StartAsync(); Dispatcher.UIThread.RunJobs(); Assert.Equal("ОШИБКА TG ПРОКСИ", Status().Text); Assert.False(proxy.CanConnect); Assert.True(proxy.CanStart); Assert.NotNull(proxy.Error);
        }
        finally { window.Hide(); }
    }
    [AvaloniaFact]
    public async Task HomeScreenshotShowsCurrentVersionAndTelegramCard()
    {
        await using var server = new TelegramProxyServer();
        var proxy = new TelegramProxyViewModel(server, new MemoryStore { Value = new TelegramProxyOptions { Port = 1443, Secret = new string('1', 32) } });
        using var app = new TestApplication(telegramProxy: proxy, connection: new AvailableHealth());
        Directory.CreateDirectory(app.Distribution.BinDirectory);
        foreach (var name in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" })
            File.WriteAllText(Path.Combine(app.Distribution.BinDirectory, name), "test fixture");
        File.WriteAllText(Path.Combine(app.Distribution.Root, "service.bat"), "set LOCAL_VERSION=1.10.4");
        File.WriteAllText(Path.Combine(app.Distribution.ListsDirectory, "list-general.txt"), "example.test");
        await app.Settings.SaveAsync(new DesktopSettings { Theme = "Dark", CheckUpdates = false, CheckConnectionAfterStart = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel) { Width = 1440, Height = 1120 }; window.Show(); await window.InitializeAsync();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Telegram прокси");
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Desktop " + app.ViewModel.DesktopVersion);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "127.0.0.1:1443");
            var output = Environment.GetEnvironmentVariable("TELEGRAM_PROXY_SCREENSHOTS");
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            var path = Path.Combine(output, "README-v0.4.0.png");
            window.CaptureRenderedFrame()?.Save(path, new PngBitmapEncoderOptions());
            Assert.True(File.Exists(path));
        }
        finally { window.Hide(); }
    }
    [AvaloniaFact]
    public async Task KeyRotationKeepsActivePortAndPreservesPendingSettings()
    {
        await using var server = new TelegramProxyServer();
        var store = new MemoryStore { Value = new TelegramProxyOptions { Port = OtherPort(), Secret = new string('1',32) } };
        var proxy = new TelegramProxyViewModel(server,store); await proxy.InitializeAsync(); await proxy.StartAsync();
        var activePort = server.Snapshot.Port; var pendingPort = OtherPort(); var oldKey = proxy.ConnectionSecret;
        await proxy.SaveAsync(proxy.Draft with { Port = pendingPort });
        await proxy.RegenerateSecretAsync();
        Assert.Null(proxy.Error); Assert.True(proxy.CanConnect); Assert.Equal(activePort,server.Snapshot.Port);
        Assert.Equal(pendingPort,store.Value!.Port); Assert.Equal("dd" + store.Value.Secret,proxy.ConnectionSecret); Assert.NotEqual(oldKey,proxy.ConnectionSecret);
        store.FailSave = true; var key = proxy.ConnectionSecret; await proxy.RegenerateSecretAsync();
        Assert.NotNull(proxy.Error); Assert.Equal(key,proxy.ConnectionSecret); Assert.Equal(TelegramProxyState.Running,server.Snapshot.State);
    }
    [AvaloniaFact]
    public async Task PageRotatesVisibleKeyAndSavesOneDcRulePerLine()
    {
        await using var server = new TelegramProxyServer(); var store = new MemoryStore { Value = new TelegramProxyOptions { Port = OtherPort(), Secret = new string('1',32) } };
        var proxy = new TelegramProxyViewModel(server,store); using var app = new TestApplication(telegramProxy:proxy);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false },TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel); window.Show(); await window.InitializeAsync(); window.OpenTelegramProxy(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            var key = Find<TextBox>(window,"TelegramKey"); Assert.Equal('\0',key.PasswordChar); Assert.Equal("dd" + store.Value.Secret,key.Text);
            var old = key.Text; Find<Button>(window,"TelegramRegenerateKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForUi(() => proxy.DisplayKey != old && !proxy.Busy); Assert.Equal(proxy.DisplayKey,key.Text);
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Button>(),b => (b.Content as string)?.StartsWith("Скопировать") == true);
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<CheckBox>(),b => (b.Content as string)?.StartsWith("Показать",StringComparison.OrdinalIgnoreCase) == true || (b.Content as string)?.StartsWith("После смены",StringComparison.OrdinalIgnoreCase) == true);
            var dc = Find<TextBox>(window,"TelegramDcOverrides"); Assert.True(dc.AcceptsReturn); Assert.Contains(Environment.NewLine,dc.Text); Assert.DoesNotContain(",",dc.Text!);
            window.GetLogicalDescendants().OfType<Expander>().Single().IsExpanded = true;
            dc.Text = "2:149.154.167.220\n4:149.154.167.91";
            window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Сохранить настройки").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForUi(() => !proxy.Busy); Assert.Null(proxy.Error); Assert.Equal("149.154.167.91",store.Value.DcOverrides[4]);
        }
        finally { window.Hide(); }
    }
    [AvaloniaTheory]
    [InlineData("Dark",1440,900)] [InlineData("Light",1440,900)]
    [InlineData("Dark",1200,800)] [InlineData("Light",1200,800)]
    [InlineData("Dark",890,800)] [InlineData("Light",890,800)]
    public async Task HomePlacesTelegramBesideAvailabilityAndReflowsAtNarrowWidths(string theme,int width,int height)
    {
        await using var server = new TelegramProxyServer(); var store = new MemoryStore { Value = new TelegramProxyOptions { Port = OtherPort(), Secret = new string('1',32) } };
        var proxy = new TelegramProxyViewModel(server,store); using var app = new TestApplication(telegramProxy:proxy,connection:new AvailableHealth());
        Directory.CreateDirectory(app.Distribution.BinDirectory);
        foreach (var name in new[] { "winws.exe","WinDivert.dll","WinDivert64.sys" }) File.WriteAllText(Path.Combine(app.Distribution.BinDirectory,name),"test fixture");
        File.WriteAllText(Path.Combine(app.Distribution.Root,"service.bat"),"set LOCAL_VERSION=1.10.4"); File.WriteAllText(Path.Combine(app.Distribution.ListsDirectory,"list-general.txt"),"example.test");
        await app.Settings.SaveAsync(new DesktopSettings { Theme = theme, CheckUpdates = false, CheckConnectionAfterStart = false },TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel) { Width=width,Height=height }; window.Show(); await window.InitializeAsync();
        try
        {
            await app.ViewModel.StartAsync(); await app.ViewModel.CheckConnectionAsync(); await proxy.StartAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var services = Find<Border>(window,"ServiceAvailability"); var telegram = Find<Border>(window,"TelegramHome"); var engine = Find<Border>(window,"EngineControl");
            var first = services.TranslatePoint(default,window)!.Value; var second = telegram.TranslatePoint(default,window)!.Value;
            Assert.True(first.Y >= engine.TranslatePoint(default,window)!.Value.Y + engine.Bounds.Height);
            if (width >= 1100) { Assert.InRange(Math.Abs(first.Y-second.Y),0,0.5); Assert.True(second.X >= first.X + services.Bounds.Width + 19); Assert.True(second.X + telegram.Bounds.Width <= width); }
            else Assert.True(second.Y >= first.Y + services.Bounds.Height + 19);
            var proxyPill = Find<Border>(window,"TelegramHomeStatus");
            var enginePill = engine.GetLogicalDescendants().OfType<Border>().Single(b => b.Child is Grid row && row.Children.OfType<TextBlock>().Any(t => t.Text == "ZAPRET АКТИВЕН"));
            Assert.Equal(enginePill.Bounds.Size,proxyPill.Bounds.Size);
            Assert.Equal(((ISolidColorBrush)enginePill.Background!).Color,((ISolidColorBrush)proxyPill.Background!).Color);
            Assert.Contains(proxyPill.GetLogicalDescendants().OfType<TextBlock>(),t => t.Text == "TG ПРОКСИ АКТИВЕН" && t.FontSize == 12);
            if (width >= 1100)
            {
                Assert.InRange(telegram.Bounds.Width,359.5,360.5);
                Assert.InRange(Math.Abs(telegram.Bounds.Height-services.Bounds.Height),0,0.5);
            }
            else Assert.Equal(services.Bounds.Width,telegram.Bounds.Width);
            Assert.InRange(telegram.Bounds.Height,180,280);
            var toggle = Find<Button>(window,"TelegramHomeToggle");
            Assert.Equal(264,toggle.Bounds.Width);
            Assert.Contains("danger",toggle.Classes); Assert.Equal("Остановить прокси",toggle.Content);
            Assert.True(toggle.Bounds.Bottom <= telegram.Bounds.Height);
            foreach (var label in new[] { "Telegram прокси", proxy.Address, "TG ПРОКСИ АКТИВЕН" })
            {
                var text = telegram.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == label);
                var inset = text.TranslatePoint(default,telegram)!.Value.X;
                Assert.InRange(inset,label == "Telegram прокси" ? 54.5 : label == "TG ПРОКСИ АКТИВЕН" ? 44.5 : 20.5,label == "Telegram прокси" ? 55.5 : label == "TG ПРОКСИ АКТИВЕН" ? 45.5 : 21.5);
            }
            var sidebar = Find<Border>(window,"TelegramSidebarStatus"); Assert.Contains(sidebar.GetLogicalDescendants().OfType<TextBlock>(),t => t.Text == "TG Прокси активен");
            var output = Environment.GetEnvironmentVariable("TELEGRAM_PROXY_SCREENSHOTS");
            if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); window.CaptureRenderedFrame()?.Save(Path.Combine(output,$"home-{theme}-{width}.png"),new PngBitmapEncoderOptions()); }
            await proxy.StopAsync(); Dispatcher.UIThread.RunJobs(); Assert.Contains(sidebar.GetLogicalDescendants().OfType<TextBlock>(),t => t.Text == "TG Прокси выключен");
        }
        finally { window.Hide(); }
    }
    [AvaloniaFact]
    public async Task ConnectionWarningsStayInHistoryAndLogsWhileListenerFailuresRemainVisible()
    {
        var memory = new MemoryProxy(); var proxy = new TelegramProxyViewModel(memory,new MemoryStore { Value = new TelegramProxyOptions { Secret = new string('1',32),AutoStart = true } });
        using var app = new TestApplication(telegramProxy:proxy);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false },TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel); window.Show(); await window.InitializeAsync(); Dispatcher.UIThread.RunJobs();
        try
        {
            const string warning = "DC4 media, WebSocket, приём от Telegram: ConnectionClosedPrematurely";
            memory.Warn(warning); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("TG Прокси активен",proxy.StatusText); Assert.Contains("Соединений:",Find<TextBlock>(window,"TelegramHomeSummary").Text);
            Assert.False(Find<TextBlock>(window,"TelegramHomeError").IsVisible);
            Assert.Contains(app.ViewModel.Logs,line => line.Contains("[WARN]") && line.Contains(warning));
            Assert.DoesNotContain(app.ViewModel.Logs,line => line.Contains("[INFO]") && line.Contains(warning));
            window.OpenTelegramProxy(); Dispatcher.UIThread.RunJobs();
            Assert.False(Find<TextBlock>(window,"TelegramError").IsVisible);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(),t => t.Text?.Contains("Последняя проблема соединения") == true && t.Text.Contains(warning));
            memory.Fail("Локальный сервер остановился"); Dispatcher.UIThread.RunJobs(); Assert.True(Find<TextBlock>(window,"TelegramError").IsVisible);
        }
        finally { window.Hide(); }
    }
    [AvaloniaFact]
    public async Task ToggleShowsBusyStateAndActionResultsInFooterAndUsesZapretStopStyle()
    {
        var memory = new MemoryProxy { StartPending = new(), StopPending = new() };
        var store = new MemoryStore { Value = new TelegramProxyOptions { Secret = new string('1',32) } };
        var proxy = new TelegramProxyViewModel(memory,store); using var app = new TestApplication(telegramProxy:proxy);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false,CheckConnectionAfterStart = false },TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel); window.Show(); await window.InitializeAsync();
        try
        {
            var start = Find<ActionButton>(window,"TelegramHomeToggle");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(start.IsBusy); Assert.False(start.IsEffectivelyEnabled); Assert.Equal("TG Прокси запускается",proxy.StatusText);
            Assert.Equal("Запускаем Telegram прокси…",app.ViewModel.Message);
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1,memory.StartCalls);
            window.OpenTelegramProxy(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(Find<ActionButton>(window,"TelegramToggle").IsBusy);
            memory.StartPending.SetResult(); await WaitForUi(() => !proxy.Busy); window.UpdateLayout();
            Assert.Equal("Telegram прокси запущен — 127.0.0.1:1443",app.ViewModel.Message);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(),t => t.Text == app.ViewModel.Message && t.IsVisible);
            var stop = Find<ActionButton>(window,"TelegramToggle"); Assert.Contains("danger",stop.Classes); Assert.DoesNotContain("primary",stop.Classes);
            var output = Environment.GetEnvironmentVariable("TELEGRAM_PROXY_SCREENSHOTS");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(stop.IsBusy); Assert.False(stop.IsEffectivelyEnabled); Assert.Equal("TG Прокси останавливается",proxy.StatusText);
            Assert.Equal("Останавливаем Telegram прокси…",app.ViewModel.Message);
            if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); window.CaptureRenderedFrame()?.Save(Path.Combine(output,"telegram-stopping.png"),new PngBitmapEncoderOptions()); }
            memory.StopPending.SetResult(); await WaitForUi(() => !proxy.Busy); Assert.Equal("Telegram прокси остановлен",app.ViewModel.Message);
            await proxy.SaveAsync(proxy.Draft); Assert.Equal("Настройки Telegram прокси сохранены",app.ViewModel.Message);
            await proxy.RegenerateSecretAsync(); Assert.StartsWith("Ключ Telegram прокси обновлён",app.ViewModel.Message);
            store.FailSave = true; await proxy.SaveAsync(proxy.Draft); Dispatcher.UIThread.RunJobs();
            Assert.True(app.ViewModel.MessageIsError); Assert.Equal("Ошибка: Не удалось сохранить ключ.",app.ViewModel.Message);
        }
        finally { window.Hide(); }
    }
    [AvaloniaFact]
    public async Task ResizingAcrossBreakpointsKeepsCardsAlignedAndAuthorsVisibleAtMinimumHeight()
    {
        var memory = new MemoryProxy(); var proxy = new TelegramProxyViewModel(memory,new MemoryStore { Value = new TelegramProxyOptions { Secret = new string('1',32),AutoStart = true } });
        using var app = new TestApplication(telegramProxy:proxy,connection:new AvailableHealth());
        Directory.CreateDirectory(app.Distribution.BinDirectory);
        foreach (var name in new[] { "winws.exe","WinDivert.dll","WinDivert64.sys" }) File.WriteAllText(Path.Combine(app.Distribution.BinDirectory,name),"test fixture");
        File.WriteAllText(Path.Combine(app.Distribution.Root,"service.bat"),"set LOCAL_VERSION=1.10.4"); File.WriteAllText(Path.Combine(app.Distribution.ListsDirectory,"list-general.txt"),"example.test");
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false,CheckConnectionAfterStart = false },TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel); window.Show(); await window.InitializeAsync();
        try
        {
            await app.ViewModel.StartAsync(); await app.ViewModel.CheckConnectionAsync();
            foreach (var width in new[] {1440,1200,1100,1099,890,1440})
            {
                window.Width = width; window.Height = window.MinHeight; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var services = Find<Border>(window,"ServiceAvailability"); var card = Find<Border>(window,"TelegramHome");
                var a = services.TranslatePoint(default,window)!.Value; var b = card.TranslatePoint(default,window)!.Value;
                if (width >= 1100) { Assert.Equal(a.Y,b.Y); Assert.Equal(services.Bounds.Height,card.Bounds.Height); }
                else { Assert.Equal(a.X,b.X); Assert.Equal(services.Bounds.Width,card.Bounds.Width); Assert.True(b.Y >= a.Y+services.Bounds.Height+19); }
                var authors = window.GetLogicalDescendants().OfType<TextBlock>().Single(x => x.Name == "SidebarAuthors");
                var authorAt = authors.TranslatePoint(default,window)!.Value;
                Assert.True(authorAt.Y+authors.Bounds.Height <= window.Bounds.Height);
                var settings = window.GetLogicalDescendants().OfType<Button>().Single(x => x.Content is StackPanel p && p.Children.OfType<TextBlock>().Any(t => t.Text == "Настройки"));
                var sidebarFooter = window.GetLogicalDescendants().OfType<StackPanel>().Single(x => x.Name == "SidebarFooter");
                Assert.True(settings.TranslatePoint(default,window)!.Value.Y+settings.Bounds.Height <= sidebarFooter.TranslatePoint(default,window)!.Value.Y);
                var status = Find<Border>(window,"SidebarStatus");
                Assert.True(settings.TranslatePoint(default,window)!.Value.Y+settings.Bounds.Height+15 <= status.TranslatePoint(default,window)!.Value.Y);
                var toggle = Find<Button>(window,"TelegramHomeToggle"); var zapret = window.GetLogicalDescendants().OfType<Button>().Single(x => x.Content as string == "Остановить Zapret");
                Assert.Equal(zapret.Background,toggle.Background); Assert.Equal(zapret.Foreground,toggle.Foreground); Assert.Equal(zapret.BorderBrush,toggle.BorderBrush);
                Assert.Equal(zapret.Bounds.Height,toggle.Bounds.Height);
                var output = Environment.GetEnvironmentVariable("TELEGRAM_PROXY_SCREENSHOTS");
                if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); window.CaptureRenderedFrame()?.Save(Path.Combine(output,$"resized-{width}-min-height.png"),new PngBitmapEncoderOptions()); }
            }
            Assert.Equal(TelegramProxyState.Running,proxy.Snapshot.State);
        }
        finally { window.Hide(); }
    }
    [AvaloniaFact]
    public async Task RepeatedInitializationDoesNotAutostartTwiceOrDuplicateDiagnostics()
    {
        var memory = new MemoryProxy(); var proxy = new TelegramProxyViewModel(memory,new MemoryStore { Value = new TelegramProxyOptions { Secret = new string('1',32),AutoStart = true } });
        using var app = new TestApplication(telegramProxy:proxy);
        await app.ViewModel.InitializeAsync(); await app.ViewModel.InitializeAsync(); await proxy.InitializeAsync();
        Assert.Equal(1,memory.StartCalls);
        memory.Warn("unique warning"); Dispatcher.UIThread.RunJobs();
        Assert.Single(app.ViewModel.Logs,x => x.Contains("unique warning"));
    }
    private sealed class MemoryProxy : ITelegramProxy
    {
        public TelegramProxySnapshot Snapshot { get; private set; } = new(TelegramProxyState.Stopped);
        public event Action? Changed;
        public event Action<TelegramProxyDiagnostic>? Diagnostic;
        public TaskCompletionSource? StartPending,StopPending;
        public int StartCalls;
        public void Warn(string message)
        { Snapshot = Snapshot with { ConnectionErrors = 1,LastConnectionError = message,LastConnectionErrorAt = DateTimeOffset.UtcNow }; Diagnostic?.Invoke(new("WARN",message)); Changed?.Invoke(); }
        public void Fail(string message) { Snapshot = Snapshot with { State = TelegramProxyState.Error,LastError = message }; Changed?.Invoke(); }
        public async Task StartAsync(TelegramProxyOptions options,CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); StartCalls++;
            Snapshot = Snapshot with { State = TelegramProxyState.Starting }; Changed?.Invoke();
            if (StartPending is not null) await StartPending.Task.WaitAsync(ct);
            Snapshot = Snapshot with { State = TelegramProxyState.Running,Port = options.Port }; Changed?.Invoke();
        }
        public async Task StopAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Snapshot = Snapshot with { State = TelegramProxyState.Stopping }; Changed?.Invoke();
            if (StopPending is not null) await StopPending.Task.WaitAsync(ct);
            Snapshot = Snapshot with { State = TelegramProxyState.Stopped }; Changed?.Invoke();
        }
        public Task RestartAsync(TelegramProxyOptions options,CancellationToken ct = default) => StartAsync(options,ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static T Find<T>(Window window,string id) where T:Control => window.GetLogicalDescendants().OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == id);
    private static async Task WaitForUi(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(10,TestContext.Current.CancellationToken); }
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class AvailableHealth : IConnectionHealthService
    {
        public Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionHealthResult(DateTimeOffset.Now,
            [new("YouTube","https://youtube.com",true,200,TimeSpan.FromMilliseconds(1053),"Доступен"),new("Discord","https://discord.com",true,200,TimeSpan.FromMilliseconds(810),"Доступен")]));
    }
    internal static int OtherPort() { using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); listener.Start(); return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
    private sealed class MemoryStore : ITelegramProxySettingsStore
    {
        public TelegramProxyOptions? Value;
        public bool FailSave;
        public Task<TelegramProxyOptions?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value?.Copy());
        public Task SaveAsync(TelegramProxyOptions options,CancellationToken cancellationToken = default) { if (FailSave) throw new IOException("Не удалось сохранить ключ."); Value=options.Copy(); return Task.CompletedTask; }
    }
}
