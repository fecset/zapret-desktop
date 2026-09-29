using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed class MainViewModel(IZapretDistribution distribution, IStrategyProvider strategies,
    IZapretProcessManager process, IServiceManager service, IFilterService filters,
    IDiagnosticService diagnostics, IListService lists, IUpdateService updates, IStrategyTester tester,
    ISettingsStore settingsStore, IStartupService startup, IFakePayloadService fakes,
    IPrivilegeService privilege,
    ILogger<MainViewModel> logger) : ObservableObject
{
    private DesktopSettings settings = new();
    private ZapretStrategy? selectedStrategy;
    private ZapretStatus? status;
    private GameFilterSettings game = new(GameFilterMode.Disabled, "1024-65535", "1024-65535");
    private IpSetMode ipset;
    private string message = "";
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private bool busy;
    private UpdateInfo? upstreamUpdate;
    private UpdateInfo? desktopUpdate;
    private bool checkingUpdates;
    private string? updateError;
    private CancellationTokenSource? testCancellation;
    private string? bestTestStrategy;
    private string? strategyTestSummary;
    private string logLevelFilter = "ALL";
    private bool serviceAutoStart;
    public ObservableCollection<ZapretStrategy> Strategies { get; } = [];
    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> FilteredLogs { get; } = [];
    public string LogLevelFilter
    {
        get => logLevelFilter;
        set
        {
            if (!SetProperty(ref logLevelFilter, value)) return;
            FilteredLogs.Clear();
            foreach (var line in Logs.Reverse().Where(x => value == "ALL" || x.Contains($"[{value}]", StringComparison.Ordinal)))
                FilteredLogs.Add(line);
        }
    }
    public ObservableCollection<string> ListLines { get; } = [];
    public IReadOnlyList<string> ListFiles => lists.AvailableFiles;
    public IReadOnlyList<string> FakePayloads => fakes.GetAvailablePayloads();
    public string UpstreamVersion => distribution.Version ?? "неизвестна";
    public string DesktopVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "неизвестна";
    public string DistributionPath => distribution.Root;
    public bool IsAdministrator => privilege.IsAdministrator;
    public DesktopSettings Settings => settings;
    public ZapretStrategy? SelectedStrategy
    {
        get => selectedStrategy;
        set { if (SetProperty(ref selectedStrategy, value)) OnPropertyChanged(nameof(SelectedStrategyName)); }
    }
    public string SelectedStrategyName => SelectedStrategy?.DisplayName ?? "Не выбрана";
    public ZapretStatus? Status { get => status; private set { if (SetProperty(ref status, value)) OnPropertyChanged(nameof(StatusText)); } }
    public string StatusText => Status?.State switch
    {
        ZapretRunState.Running => "Активен",
        ZapretRunState.External => "Запущен вне приложения",
        ZapretRunState.ServiceRunning => "Служба активна",
        _ => "Остановлен"
    };
    public GameFilterSettings Game { get => game; private set => SetProperty(ref game, value); }
    public IpSetMode IpSet { get => ipset; private set => SetProperty(ref ipset, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool Busy { get => busy; private set => SetProperty(ref busy, value); }
    public bool ServiceAutoStart { get => serviceAutoStart; private set => SetProperty(ref serviceAutoStart, value); }
    public UpdateInfo? UpstreamUpdate { get => upstreamUpdate; private set => SetProperty(ref upstreamUpdate, value); }
    public UpdateInfo? DesktopUpdate { get => desktopUpdate; private set => SetProperty(ref desktopUpdate, value); }
    public bool CheckingUpdates { get => checkingUpdates; private set => SetProperty(ref checkingUpdates, value); }
    public string? UpdateError { get => updateError; private set => SetProperty(ref updateError, value); }
    public string? BestTestStrategy { get => bestTestStrategy; private set => SetProperty(ref bestTestStrategy, value); }
    public string? StrategyTestSummary { get => strategyTestSummary; private set => SetProperty(ref strategyTestSummary, value); }

    public async Task InitializeAsync()
    {
        process.OutputReceived += (level, line) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(level, line));
        settings = await settingsStore.LoadAsync();
        settings.StartWithWindows = startup.IsEnabled();
        OnPropertyChanged(nameof(Settings));
        foreach (var strategy in await strategies.GetStrategiesAsync()) Strategies.Add(strategy);
        SelectedStrategy = Strategies.FirstOrDefault(x => x.Id == settings.SelectedStrategyId) ?? Strategies.FirstOrDefault();
        await RefreshAsync();
        if (settings.StartZapretOnLaunch && SelectedStrategy is not null && Status?.State == ZapretRunState.Stopped)
            await StartAsync();
    }
    public Task RefreshAsync() => Busy ? Task.CompletedTask : RunAsync(async () =>
    {
        Status = await process.GetStatusAsync();
        ServiceAutoStart = await service.GetAutoStartAsync();
        Game = await filters.GetGameFilterAsync();
        IpSet = await filters.GetIpSetModeAsync();
    });
    public Task StartAsync() => RunAsync(async () =>
    {
        if (SelectedStrategy is null) throw new InvalidOperationException("Выберите стратегию.");
        var fresh = (await strategies.GetStrategiesAsync()).First(x => x.Id == SelectedStrategy.Id);
        Status = await process.StartAsync(fresh);
        Log("INFO", $"Запущена стратегия {SelectedStrategy.Name}; PID {Status.ProcessId}");
        Message = "Стратегия запущена";
    });
    public Task StopAsync() => RunAsync(async () =>
    {
        Status = await process.StopAsync();
        Log("INFO", "Запрос остановки завершён");
        Message = "Остановлено";
    });
    public Task SelectAsync(ZapretStrategy? strategy) => RunAsync(async () =>
    {
        SelectedStrategy = strategy;
        settings.SelectedStrategyId = strategy?.Id;
        await settingsStore.SaveAsync(settings);
        Message = "Стратегия по умолчанию сохранена";
    });
    public Task SetGameAsync(GameFilterMode mode, string tcp, string udp) => RunAsync(async () =>
    {
        await filters.SetGameFilterAsync(new(mode, tcp, udp));
        Game = await filters.GetGameFilterAsync();
        Message = "Game Filter изменён. Перезапустите активную стратегию.";
        Log("INFO", $"Game Filter: {mode}");
    });
    public Task SetIpSetAsync(IpSetMode mode) => RunAsync(async () =>
    {
        await filters.SetIpSetModeAsync(mode);
        IpSet = await filters.GetIpSetModeAsync();
        Message = "IPSet Filter изменён. Перезапустите активную стратегию.";
        Log("INFO", $"IPSet Filter: {mode}");
    });
    public Task ServiceAsync(string action) => RunAsync(async () =>
    {
        switch (action)
        {
            case "install":
                if (SelectedStrategy is null) throw new InvalidOperationException("Выберите стратегию.");
                await service.InstallAsync((await strategies.GetStrategiesAsync()).First(x => x.Id == SelectedStrategy.Id)); break;
            case "remove": await service.RemoveAsync(); break;
            case "start": await service.StartAsync(); break;
            case "stop": await service.StopAsync(); break;
            case "restart": await service.RestartAsync(); break;
            case "auto": await service.SetAutoStartAsync(true); break;
            case "manual": await service.SetAutoStartAsync(false); break;
        }
        Status = await process.GetStatusAsync();
        ServiceAutoStart = await service.GetAutoStartAsync();
        Log("INFO", $"Служба: {action}");
        Message = action switch
        {
            "install" => "Служба установлена и запущена.", "remove" => "Служба удалена.",
            "start" => "Служба запущена.", "stop" => "Служба остановлена.",
            "restart" => "Служба перезапущена.", "auto" => "Автозапуск службы включён.",
            "manual" => "Автозапуск службы выключен.", _ => "Состояние службы обновлено."
        };
    });
    public Task DiagnoseAsync() => RunAsync(async () =>
    {
        Diagnostics.Clear();
        foreach (var item in await diagnostics.RunAsync()) Diagnostics.Add(item);
        Log("INFO", $"Диагностика: {Diagnostics.Count} проверок");
        Message = "Диагностика завершена";
    });
    public Task SaveSettingsAsync() => RunAsync(async () =>
    {
        startup.SetEnabled(settings.StartWithWindows);
        await settingsStore.SaveAsync(settings);
        Message = "Настройки сохранены";
    });
    public Task SearchListAsync(string file, string query) => RunAsync(async () =>
    {
        ListLines.Clear();
        foreach (var line in await lists.SearchAsync(file, query)) ListLines.Add(line);
    });
    public Task AddListEntryAsync(string file, string entry) => RunAsync(async () =>
    {
        await lists.AddEntryAsync(file, entry);
        Message = $"Запись добавлена в {file}";
        Log("INFO", $"Список {file} изменён");
    });
    public Task RemoveListEntryAsync(string file, string entry) => RunAsync(async () =>
    {
        await lists.RemoveEntryAsync(file, entry);
        Message = $"Запись удалена из {file}";
        Log("INFO", $"Список {file} изменён");
    });
    public void OpenListsFolder() => lists.OpenFolder();
    public async Task CheckUpdatesAsync()
    {
        if (CheckingUpdates) return;
        CheckingUpdates = true;
        UpdateError = null;
        try
        {
            async Task<UpdateInfo?> CheckAsync(Func<Task<UpdateInfo>> check, string product)
            {
                try { return await check(); }
                catch (Exception ex)
                {
                    Log("WARN", $"Не удалось проверить обновления {product}: {ex.Message}");
                    return null;
                }
            }
            var upstreamTask = CheckAsync(() => updates.CheckUpstreamAsync(), "zapret");
            var desktopTask = CheckAsync(() => updates.CheckDesktopAsync(DesktopVersion), "Zapret Desktop");
            await Task.WhenAll(upstreamTask, desktopTask);
            UpstreamUpdate = await upstreamTask;
            DesktopUpdate = await desktopTask;
            if (UpstreamUpdate is null || DesktopUpdate is null)
                UpdateError = "Не удалось проверить " + string.Join(" и ", new[]
                {
                    UpstreamUpdate is null ? "zapret" : null,
                    DesktopUpdate is null ? "Zapret Desktop" : null
                }.Where(x => x is not null)) + ". Проверьте подключение к интернету.";
            var available = new[] { UpstreamUpdate, DesktopUpdate }.Where(x => x?.Available == true)
                .Select(x => $"{x!.Product} {x.LatestVersion}").ToArray();
            Message = available.Length > 0 ? "Доступны обновления: " + string.Join(", ", available) :
                UpdateError ?? "Установлены актуальные версии zapret и Zapret Desktop";
            Log("INFO", Message);
        }
        finally { CheckingUpdates = false; }
    }
    public Task TestStrategiesAsync() => RunAsync(async () =>
    {
        testCancellation = new CancellationTokenSource();
        BestTestStrategy = null;
        StrategyTestSummary = "Идёт проверка стратегий. Результат появится здесь.";
        var testFinished = false;
        var progress = new Progress<string>(line =>
        {
            if (testFinished || testCancellation?.IsCancellationRequested != false) return;
            if (line.Contains("Starting config", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Best config", StringComparison.OrdinalIgnoreCase) || line.Contains("[ERROR]"))
                Log("INFO", line);
            Message = line;
        });
        try
        {
            var result = await tester.RunAsync(progress, testCancellation.Token);
            testFinished = true;
            BestTestStrategy = result.BestStrategy;
            StrategyTestSummary = result.Cancelled
                ? result.BestStrategy is null
                    ? "Тест остановлен. Завершённых проверок пока нет."
                    : $"Тест остановлен. Лучшая из проверенных стратегий: {result.BestStrategy}."
                : result.BestStrategy is null
                    ? "Тест завершён без рекомендации."
                    : $"Рекомендована стратегия: {result.BestStrategy}.";
            Message = StrategyTestSummary;
        }
        catch (OperationCanceledException) when (testCancellation.IsCancellationRequested)
        {
            StrategyTestSummary = "Тест остановлен до завершения первой проверки.";
            Message = StrategyTestSummary;
        }
        catch (Exception ex)
        {
            StrategyTestSummary = "Автоподбор не выполнен: " + ex.Message;
            throw;
        }
        finally { testFinished = true; testCancellation.Dispose(); testCancellation = null; }
    });
    public void CancelStrategyTest()
    {
        if (testCancellation is null) return;
        Message = "Останавливаем тест и сохраняем результат завершённых проверок...";
        testCancellation.Cancel();
    }
    public Task ReplaceFakeAsync(string target, string source) => RunAsync(async () =>
    {
        await fakes.ReplaceAsync(target, source);
        Message = "Активный fake payload заменён. Перезапустите стратегию.";
        Log("INFO", $"Заменён {target} из {source}");
    });
    public void RequestElevation() => privilege.RestartElevated();
    public async Task<bool> StopOwnedForExitAsync()
    {
        try
        {
            if (await process.StopOwnedAsync()) Log("INFO", "Запущенный приложением Zapret остановлен при выходе.");
            return true;
        }
        catch (Exception ex)
        {
            Message = "Не удалось остановить Zapret при выходе: " + ex.Message;
            Log("ERROR", ex.ToString());
            return false;
        }
    }
    public void ClearLogs() { Logs.Clear(); FilteredLogs.Clear(); }
    public string BuildDiagnosticReport() =>
        "Zapret Desktop " + DesktopVersion + " | upstream " + UpstreamVersion + Environment.NewLine +
        string.Join(Environment.NewLine, Diagnostics.Select(x => $"{(x.Passed ? "OK" : "FAIL")} {x.Name}: {x.Detail} — {x.Solution}"));
    public async Task SaveLogsAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        foreach (var line in Logs) await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
    public void Log(string level, string line)
    {
        if (Logs.Count >= 3000)
        {
            var oldest = Logs[0];
            Logs.RemoveAt(0);
            for (var i = FilteredLogs.Count - 1; i >= 0; i--)
                if (FilteredLogs[i] == oldest) { FilteredLogs.RemoveAt(i); break; }
        }
        Logs.Add($"{DateTime.Now:HH:mm:ss} [{level}] {line}");
        if (LogLevelFilter == "ALL" || LogLevelFilter == level) FilteredLogs.Insert(0, Logs[^1]);
        logger.LogInformation("{Level}: {Line}", level, line);
    }
    private async Task RunAsync(Func<Task> action)
    {
        await operationGate.WaitAsync();
        Busy = true;
        try { await action(); }
        catch (Exception ex)
        {
            Message = "Ошибка: " + (ex.Message.Contains("No process is associated with this object", StringComparison.OrdinalIgnoreCase)
                ? "Не удалось получить состояние winws.exe. Повторите действие и проверьте журнал."
                : ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
                    ? "Для этого действия нужны права администратора. Перезапустите приложение с повышенными правами."
                    : ex.Message);
            Log("ERROR", ex.ToString());
            logger.LogError(ex, "Operation failed");
        }
        finally { Busy = false; operationGate.Release(); }
    }
}
