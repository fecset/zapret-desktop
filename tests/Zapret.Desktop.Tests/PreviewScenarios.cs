using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

// Only the test executable references these services. They never call SCM, start a
// process, access the network, or write outside the temporary TestApplication root.
internal enum PreviewEngine { Stopped, Running, ServiceRunning, ServiceStopped, External, Unknown, AccessDenied,
    ServiceStarting, ServiceStopping, DriverLoaded, StrategyPending, FiltersPending, MissingFiles }
internal enum PreviewConnection { Never, Available, YouTubeFailed, DiscordFailed, Unavailable, Checking, Error }
internal enum PreviewUpdates { Never, Checking, Current, Desktop, Distribution, Both, Error, Partial,
    Downloading, Validating, Ready, PreparationError, Installed, InstallError }
internal sealed record PreviewChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
internal sealed record PreviewConfiguration(PreviewEngine Engine = PreviewEngine.Stopped,
    PreviewConnection Connection = PreviewConnection.Never, PreviewUpdates Updates = PreviewUpdates.Never,
    string Theme = "Dark", bool Compact = false, bool OpenUpdates = false);

internal static class PreviewCatalog
{
    public static readonly PreviewChoice<PreviewEngine>[] Engines =
    [
        new(PreviewEngine.Stopped, "Выключен"), new(PreviewEngine.Running, "Запущен из приложения"),
        new(PreviewEngine.ServiceRunning, "Работает служба Windows"), new(PreviewEngine.ServiceStopped, "Служба установлена и остановлена"),
        new(PreviewEngine.External, "Внешний winws"), new(PreviewEngine.Unknown, "Не удалось определить состояние"),
        new(PreviewEngine.AccessDenied, "Нет доступа к службе"), new(PreviewEngine.ServiceStarting, "Служба запускается"),
        new(PreviewEngine.ServiceStopping, "Служба останавливается"), new(PreviewEngine.DriverLoaded, "Выключен, драйвер ещё загружен"),
        new(PreviewEngine.StrategyPending, "Выбрана другая стратегия — нужен перезапуск"),
        new(PreviewEngine.FiltersPending, "Изменены фильтры — нужен перезапуск"), new(PreviewEngine.MissingFiles, "Отсутствуют файлы zapret")
    ];
    public static readonly PreviewChoice<PreviewConnection>[] Connections =
    [
        new(PreviewConnection.Never, "Ещё не проверялось"), new(PreviewConnection.Available, "YouTube и Discord доступны"),
        new(PreviewConnection.YouTubeFailed, "YouTube недоступен"), new(PreviewConnection.DiscordFailed, "Discord недоступен"),
        new(PreviewConnection.Unavailable, "Оба сервиса недоступны"), new(PreviewConnection.Checking, "Идёт проверка (до 10 секунд)"),
        new(PreviewConnection.Error, "Ошибка проверки / тайм-аут")
    ];
    public static readonly PreviewChoice<PreviewUpdates>[] Updates =
    [
        new(PreviewUpdates.Never, "Ещё не проверялось"), new(PreviewUpdates.Checking, "Идёт проверка обновлений"),
        new(PreviewUpdates.Current, "Установлены актуальные версии"), new(PreviewUpdates.Desktop, "Доступно обновление Desktop"),
        new(PreviewUpdates.Distribution, "Доступно обновление zapret"), new(PreviewUpdates.Both, "Доступны оба обновления"),
        new(PreviewUpdates.Error, "Не удалось проверить обновления"), new(PreviewUpdates.Partial, "Desktop доступен, проверка zapret не удалась"),
        new(PreviewUpdates.Downloading, "Загрузка обновления — 42%"), new(PreviewUpdates.Validating, "Проверка загруженного архива"),
        new(PreviewUpdates.Ready, "Обновление загружено и готово"), new(PreviewUpdates.PreparationError, "Ошибка загрузки / проверки архива"),
        new(PreviewUpdates.Installed, "Desktop успешно обновлён"), new(PreviewUpdates.InstallError, "Ошибка установки Desktop")
    ];
}

internal sealed class PreviewSession : IAsyncDisposable
{
    private readonly List<Task> operations = [];
    private readonly PreviewHealth health;
    private readonly PreviewUpdateService updates;
    private readonly PreviewManagedUpdates managed;
    private bool disposed;
    private bool closed;
    public TestApplication Fixture { get; }
    public MainViewModel ViewModel => Fixture.ViewModel;
    public PreviewProcess Process { get; }
    public MainWindow Window { get; private set; } = null!;
    public Task? ConnectionOperation { get; private set; }

    private PreviewSession(PreviewConfiguration config)
    {
        Process = new PreviewProcess(config.Engine);
        health = new PreviewHealth(config.Connection);
        updates = new PreviewUpdateService(config.Updates);
        managed = new PreviewManagedUpdates(config.Updates);
        Fixture = new TestApplication(connection: health, updateService: updates, managedUpdates: managed,
            diagnostics: new PreviewDiagnostic(Process, config.Engine == PreviewEngine.MissingFiles), featureServices: true, processManager: Process, serviceManager: Process);
        managed.Root = Fixture.Distribution.Root;
    }

    public static async Task<PreviewSession> OpenAsync(PreviewConfiguration config, Action<string>? notice = null)
    {
        var session = new PreviewSession(config);
        try
        {
            var fixture = session.Fixture;
            Directory.CreateDirectory(fixture.Distribution.BinDirectory);
            if (config.Engine != PreviewEngine.MissingFiles)
                foreach (var file in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" })
                    File.WriteAllText(Path.Combine(fixture.Distribution.BinDirectory, file), "UI preview, never executable");
            File.WriteAllText(Path.Combine(fixture.Distribution.Root, "service.bat"), "set LOCAL_VERSION=1.10.4");
            File.WriteAllText(Path.Combine(fixture.Distribution.ListsDirectory, "list-general.txt"), "example.test\n");
            await fixture.Settings.SaveAsync(new DesktopSettings { Theme = config.Theme, SelectedStrategyId = "general",
                CheckUpdates = false, CheckConnectionAfterStart = false, MinimizeToTray = false });
            session.Window = new MainWindow(session.ViewModel, uri => notice?.Invoke("Страница релиза: " + uri),
                exitApplication: () => { session.ReleasePending(); session.Window.Close(); })
            {
                Title = "Zapret Desktop — предпросмотр состояний (тестовые данные)",
                Width = config.Compact ? 1200 : 1440, Height = config.Compact ? 800 : 900
            };
            session.Window.Show();
            session.Window.Closed += (_, _) => session.closed = true;
            await UntilAsync(() => session.ViewModel.Status is not null && session.ViewModel.Strategies.Count > 0 && !session.ViewModel.Busy);
            await session.ViewModel.DiagnoseAsync();
            if (config.Engine == PreviewEngine.StrategyPending)
                await session.ViewModel.SelectAsync(session.ViewModel.Strategies.Single(s => s.Id != "general"));
            if (config.Engine == PreviewEngine.FiltersPending)
                await session.ViewModel.SetGameAsync(GameFilterMode.All, "1024-65535", "1024-65535");
            if (config.Connection != PreviewConnection.Never)
            {
                session.ConnectionOperation = session.ViewModel.CheckConnectionAsync();
                session.operations.Add(session.ConnectionOperation);
                if (config.Connection != PreviewConnection.Checking) await session.ConnectionOperation;
            }
            if (config.Updates is not (PreviewUpdates.Never or PreviewUpdates.Installed or PreviewUpdates.InstallError))
            {
                var check = session.ViewModel.CheckUpdatesAsync();
                session.operations.Add(check);
                if (config.Updates != PreviewUpdates.Checking) await check;
            }
            if (config.Updates is PreviewUpdates.Downloading or PreviewUpdates.Validating or PreviewUpdates.Ready or PreviewUpdates.PreparationError)
            {
                var prepare = session.ViewModel.PrepareDesktopUpdateAsync();
                session.operations.Add(prepare);
                if (config.Updates is PreviewUpdates.Ready or PreviewUpdates.PreparationError) await prepare;
                else await UntilAsync(() => session.ViewModel.PreparationStage is "Загрузка" or "Проверка архива");
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (config.Updates is not (PreviewUpdates.PreparationError or PreviewUpdates.Installed or PreviewUpdates.InstallError))
                session.Window.GetLogicalDescendants().OfType<Button>().Single(b =>
                    Avalonia.Automation.AutomationProperties.GetName(b) == "Скрыть сообщение")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (config.OpenUpdates) session.NavigateToUpdates();
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public void NavigateToUpdates()
    {
        // Use the normal UI action, including its scroll positioning.
        var button = Window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Открыть обновления");
        if (button is not null) button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        else
        {
            var nav = Window.GetLogicalDescendants().OfType<Button>().Single(b =>
                Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "Navigation:Настройки");
            nav.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Window.UpdateLayout();
            var scroll = Window.GetLogicalDescendants().OfType<ScrollViewer>().First(c =>
                Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "PageScroll");
            var heading = Window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == "Обновления");
            var origin = heading.TranslatePoint(default, scroll.Content as Visual ?? throw new InvalidOperationException("Нет области обновлений."));
            if (origin is { } point) scroll.Offset = new Avalonia.Vector(0, Math.Max(0, point.Y - 32));
        }
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Не удалось открыть выбранный сценарий предпросмотра.");
            await Task.Delay(10);
        }
    }
    private void ReleasePending() { health.Release(); updates.Release(); ViewModel.CancelPreparation(); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        ReleasePending();
        await Task.WhenAll(operations);
        if (Window is not null && !closed) await Window.ExitAsync();
        Fixture.Dispose();
    }
}

internal sealed class PreviewProcess : IZapretProcessManager, IServiceManager
{
    private ZapretStatus status;
    private string? serviceStrategy = "general";
    private bool autoStart;
    public PreviewProcess(PreviewEngine engine)
    {
        var run = engine is PreviewEngine.Running or PreviewEngine.StrategyPending or PreviewEngine.FiltersPending;
        var service = engine switch { PreviewEngine.ServiceRunning => "Running", PreviewEngine.ServiceStopped => "Stopped",
            PreviewEngine.Unknown => "Unknown", PreviewEngine.AccessDenied => "AccessDenied",
            PreviewEngine.ServiceStarting => "StartPending", PreviewEngine.ServiceStopping => "StopPending", _ => "NotInstalled" };
        var state = engine == PreviewEngine.ServiceRunning ? ZapretRunState.ServiceRunning : engine == PreviewEngine.External
            ? ZapretRunState.External : run ? ZapretRunState.Running : ZapretRunState.Stopped;
        status = new(state, run || state is ZapretRunState.ServiceRunning or ZapretRunState.External ? 4242 : null,
            run || state == ZapretRunState.ServiceRunning ? DateTimeOffset.Now.AddMinutes(-12) : null,
            run ? "general" : null, run || state != ZapretRunState.Stopped || engine == PreviewEngine.DriverLoaded ? "Running" :
                engine is PreviewEngine.Unknown or PreviewEngine.AccessDenied ? "Unknown" : "Stopped", service);
    }
    public event Action<string, string>? OutputReceived;
    public Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);
    public Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    {
        status = status with { State = ZapretRunState.Running, ProcessId = 4242, StartedAt = DateTimeOffset.Now,
            StrategyId = strategy.Id, DriverStatus = "Running" };
        OutputReceived?.Invoke("INFO", "Предпросмотр: запущена " + strategy.DisplayName);
        return Task.FromResult(status);
    }
    public Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        status = status with { State = ZapretRunState.Stopped, ProcessId = null, StartedAt = null, StrategyId = null,
            DriverStatus = "Stopped", ServiceStatus = status.ServiceStatus == "NotInstalled" ? "NotInstalled" : "Stopped" };
        return Task.FromResult(status);
    }
    public Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        var owned = status.State == ZapretRunState.Running;
        if (owned) status = status with { State = ZapretRunState.Stopped, ProcessId = null, StartedAt = null, StrategyId = null };
        return Task.FromResult(owned);
    }
    public async Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    { await StopAsync(cancellationToken); return await StartAsync(strategy, cancellationToken); }
    Task<string> IServiceManager.GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(status.ServiceStatus);
    public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default) => Task.FromResult(autoStart);
    public Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) { autoStart = enabled; return Task.CompletedTask; }
    public async Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    { serviceStrategy = strategy.Id; await ((IServiceManager)this).StartAsync(cancellationToken); }
    public Task UpdateStrategyAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    { serviceStrategy = strategy.Id; return Task.CompletedTask; }
    Task IServiceManager.StartAsync(CancellationToken cancellationToken)
    { status = status with { State = ZapretRunState.ServiceRunning, ServiceStatus = "Running", DriverStatus = "Running", StrategyId = serviceStrategy }; return Task.CompletedTask; }
    async Task IServiceManager.StopAsync(CancellationToken cancellationToken) { await StopAsync(cancellationToken); }
    Task IServiceManager.RestartAsync(CancellationToken cancellationToken) => ((IServiceManager)this).StartAsync(cancellationToken);
    public Task RemoveAsync(CancellationToken cancellationToken = default)
    { status = status with { State = ZapretRunState.Stopped, ServiceStatus = "NotInstalled", StrategyId = null }; return Task.CompletedTask; }
}

internal sealed class PreviewHealth(PreviewConnection state) : IConnectionHealthService
{
    private readonly TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => hold.TrySetResult();
    public async Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (state == PreviewConnection.Checking) await hold.Task.WaitAsync(cancellationToken);
        if (state == PreviewConnection.Error) throw new TimeoutException("Тестовая ошибка: сервер не ответил за отведённое время.");
        var youtube = state is not (PreviewConnection.YouTubeFailed or PreviewConnection.Unavailable);
        var discord = state is not (PreviewConnection.DiscordFailed or PreviewConnection.Unavailable);
        return new(DateTimeOffset.Now,
        [
            new("YouTube", "https://www.youtube.com/", youtube, youtube ? 200 : null, TimeSpan.FromMilliseconds(429),
                youtube ? "Тестовый результат; сетевой запрос не выполнялся" : "Тестовая ошибка TLS: соединение прервано"),
            new("Discord", "https://discord.com/", discord, discord ? 200 : null, TimeSpan.FromMilliseconds(211),
                discord ? "Тестовый результат; сетевой запрос не выполнялся" : "Тестовая ошибка: превышено время ожидания")
        ]);
    }
}

internal sealed class PreviewUpdateService(PreviewUpdates state) : IUpdateService
{
    private readonly TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => hold.TrySetResult();
    private async Task<UpdateInfo> CheckAsync(bool desktop, string local)
    {
        if (state == PreviewUpdates.Checking) await hold.Task;
        if (state == PreviewUpdates.Error || !desktop && state == PreviewUpdates.Partial)
            throw new IOException("Тестовая ошибка соединения с сервером обновлений.");
        var available = desktop ? state is PreviewUpdates.Desktop or PreviewUpdates.Both or PreviewUpdates.Partial or
                PreviewUpdates.Downloading or PreviewUpdates.Validating or PreviewUpdates.Ready or PreviewUpdates.PreparationError
            : state is PreviewUpdates.Distribution or PreviewUpdates.Both;
        var version = available ? desktop ? "0.3.1" : "1.10.5" : local;
        return new(desktop ? "Zapret Desktop" : "zapret", local, version,
            "https://github.com/" + (desktop ? "fecset/zapret-desktop" : "Flowseal/zapret-discord-youtube") + "/releases/tag/" + version, available);
    }
    public Task<UpdateInfo> CheckUpstreamAsync(CancellationToken cancellationToken = default) => CheckAsync(false, "1.10.4");
    public Task<UpdateInfo> CheckDesktopAsync(string localVersion, CancellationToken cancellationToken = default) => CheckAsync(true, localVersion);
}

internal sealed class PreviewManagedUpdates(PreviewUpdates state) : IManagedUpdateService
{
    public string Root { get; set; } = "";
    public Task<DesktopUpdateOutcome?> ReadDesktopUpdateOutcomeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<DesktopUpdateOutcome?>(state switch {
            PreviewUpdates.Installed => new(true, "0.3.1", "Тестовый результат успешной установки."),
            PreviewUpdates.InstallError => new(false, "0.3.1", "Тестовая ошибка установки. Предыдущая версия восстановлена."), _ => null });
    private async Task<ManagedUpdatePlan> PrepareAsync(ManagedUpdateProduct product, IProgress<ManagedUpdateProgress>? progress, CancellationToken token)
    {
        if (state == PreviewUpdates.PreparationError) throw new InvalidDataException("Тестовая ошибка: контрольная сумма архива не совпадает.");
        if (state is PreviewUpdates.Downloading or PreviewUpdates.Validating)
        {
            progress?.Report(state == PreviewUpdates.Downloading ? new("Загрузка", 0.42, 4_404_019, 10_485_760) : new("Проверка архива", null));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        return new("ui-preview", product, product == ManagedUpdateProduct.Desktop ? "0.3.1" : "1.10.5", "", "", 10_485_760,
            new string('a', 64), 12, Root, "Тестовое обновление готово. Установленные файлы не изменяются.");
    }
    public Task<ManagedUpdatePlan> PrepareDesktopAsync(string localVersion, IProgress<ManagedUpdateProgress>? progress = null, CancellationToken cancellationToken = default) =>
        PrepareAsync(ManagedUpdateProduct.Desktop, progress, cancellationToken);
    public Task<ManagedUpdatePlan> PrepareDistributionAsync(IProgress<ManagedUpdateProgress>? progress = null, CancellationToken cancellationToken = default) =>
        PrepareAsync(ManagedUpdateProduct.Distribution, progress, cancellationToken);
    public Task<ManagedUpdateResult> ApplyDistributionAsync(ManagedUpdatePlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManagedUpdateResult("ui-preview", plan.Version, Root, Root, "Тестовое обновление применено."));
    public Task RollbackDistributionAsync(ManagedUpdateResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<DesktopUpdateSchedule> ScheduleDesktopUpdateAsync(ManagedUpdatePlan plan, int currentProcessId, CancellationToken cancellationToken = default) =>
        Task.FromException<DesktopUpdateSchedule>(new InvalidOperationException("Установка Desktop отключена в предпросмотре. Выберите результат установки в панели состояний."));
    public Task<IpSetUpdatePlan> PrepareIpSetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IpSetUpdatePlan("ui-preview", "", Path.Combine(Root, "lists", "ipset-all.txt"), 1, 13, "", "1.2.3.0/24\n", "Тестовый список IP-адресов."));
    public Task<DataUpdateResult> ApplyIpSetAsync(IpSetUpdatePlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DataUpdateResult(plan.TargetPath, null, "Тестовый список IP-адресов применён."));
    public Task<HostsUpdatePreview> PreviewHostsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostsUpdatePreview("ui-preview", "", Path.Combine(Root, "hosts-preview.txt"), "", "127.0.0.1 example.test\n", 1, "Тестовое изменение hosts."));
    public Task<DataUpdateResult> ApplyHostsAsync(HostsUpdatePreview preview, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DataUpdateResult(preview.TargetPath, null, "Тестовое изменение hosts применено."));
}

internal sealed class PreviewDiagnostic(PreviewProcess process, bool missingFiles) : IDiagnosticService
{
    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        var status = await process.GetStatusAsync(cancellationToken);
        return [new("Файлы zapret", !missingFiles, missingFiles ? "Отсутствуют winws.exe и WinDivert." : "Тестовый дистрибутив для просмотра интерфейса.", "Восстановите файлы zapret."),
            new("WinDivert", status.DriverStatus is "Running" or "Stopped", "Состояние: " + status.DriverStatus, "Проверьте состояние драйвера."),
            new("Служба Windows", status.ServiceStatus is not ("Unknown" or "AccessDenied"), "Состояние: " + status.ServiceStatus, "Повторите проверку с правами администратора.")];
    }
}
