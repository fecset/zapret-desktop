using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainViewModel(IZapretDistribution distribution, IStrategyProvider strategies,
    IZapretProcessManager process, IServiceManager service, IFilterService filters,
    IDiagnosticService diagnostics, IListService lists, IUpdateService updates, IStrategyTester tester,
    ISettingsStore settingsStore, IStartupService startup, IFakePayloadService fakes,
    IPrivilegeService privilege,
    ILogger<MainViewModel> logger, IUserDataBackupService? backups = null,
    IPayloadRecoveryService? payloadRecovery = null, IManagedUpdateService? managedUpdates = null,
    IConnectionHealthService? connection = null, IStrategyHistoryService? history = null) : ObservableObject
{
    private DesktopSettings settings = new();
    private ZapretStrategy? selectedStrategy;
    private ZapretStatus? status;
    private GameFilterSettings game = new(GameFilterMode.Disabled, "1024-65535", "1024-65535");
    private IpSetMode ipset;
    private string message = "";
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private bool busy;
    private bool exitRequested;
    private UpdateInfo? upstreamUpdate;
    private UpdateInfo? desktopUpdate;
    private bool checkingUpdates;
    private string? updateError;
    private CancellationTokenSource? testCancellation;
    private string? bestTestStrategy;
    private string? strategyTestSummary;
    private readonly ConcurrentDictionary<string, StrategyCheckProgress> strategyChecks = new(StringComparer.OrdinalIgnoreCase);
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
    public ObservableCollection<string> StartupWarnings { get; } = [];
    public IReadOnlyList<string> DistributionProblems => distribution.Validate();
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
    public IReadOnlyDictionary<string, StrategyCheckProgress> StrategyChecks => strategyChecks;
    public bool TestingStrategies => testCancellation is not null;

    public async Task InitializeAsync()
    {
        process.OutputReceived += (level, line) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(level, line));
        settings = await settingsStore.LoadAsync();
        if (settingsStore is ISettingsHealth { LastWarning: { } warning })
        {
            StartupWarnings.Add(warning);
            Log("WARN", warning);
        }
        foreach (var problem in distribution.Validate()) StartupWarnings.Add("Отсутствует компонент zapret: " + problem);
        var recovered = true;
        try { await tester.RecoverInterruptedTestAsync(); }
        catch (Exception ex)
        {
            recovered = false;
            Message = "Не удалось восстановить IPSet после теста: " + ex.Message;
            StartupWarnings.Add(Message);
            Log("ERROR", ex.ToString());
        }
        try
        {
            settings.StartWithWindows = startup.IsEnabled();
            if (startup.MigrateLegacyRegistration())
                Log("INFO", "Автозапуск приложения перенесён в Планировщик заданий Windows.");
        }
        catch (Exception ex)
        {
            Log("WARN", "Не удалось обновить автозапуск приложения: " + ex.Message);
            Message = "Не удалось обновить автозапуск. Повторно сохраните настройки: " + ex.Message;
        }
        OnPropertyChanged(nameof(Settings));
        foreach (var strategy in await strategies.GetStrategiesAsync()) Strategies.Add(strategy);
        SelectedStrategy = Strategies.FirstOrDefault(x => x.Id == settings.SelectedStrategyId) ?? Strategies.FirstOrDefault();
        await RefreshAsync();
        await LoadFeatureDataAsync();
        if (recovered && settings.StartZapretOnLaunch && SelectedStrategy is not null && Status?.State == ZapretRunState.Stopped)
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
        await tester.RecoverInterruptedTestAsync();
        if (SelectedStrategy is null) throw new InvalidOperationException("Выберите стратегию.");
        var fresh = (await strategies.GetStrategiesAsync()).First(x => x.Id == SelectedStrategy.Id);
        Status = await process.StartAsync(fresh);
        await CheckConnectionCoreAsync();
        Log("INFO", $"Запущена стратегия {SelectedStrategy.Name}; PID {Status.ProcessId}");
        Message = "Стратегия запущена";
    });
    public Task StopAsync() => RunAsync(async () =>
    {
        try
        {
            await process.StopAsync();
            Log("INFO", "Zapret остановлен, WinDivert выгружен");
            Message = "Zapret остановлен, драйвер выгружен. Файлы можно обновлять.";
        }
        finally
        {
            Status = await process.GetStatusAsync();
            if (Status.State == ZapretRunState.Stopped) ConnectionHealth = null;
        }
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
        await BackupBeforeChangeAsync("Перед изменением Game Filter");
        await filters.SetGameFilterAsync(new(mode, tcp, udp));
        Game = await filters.GetGameFilterAsync();
        Message = "Game Filter изменён. Перезапустите активную стратегию.";
        Log("INFO", $"Game Filter: {mode}");
    });
    public Task SetIpSetAsync(IpSetMode mode) => RunAsync(async () =>
    {
        await BackupBeforeChangeAsync("Перед изменением IPSet Filter");
        await filters.SetIpSetModeAsync(mode);
        IpSet = await filters.GetIpSetModeAsync();
        Message = "IPSet Filter изменён. Перезапустите активную стратегию.";
        Log("INFO", $"IPSet Filter: {mode}");
    });
    public Task ServiceAsync(string action) => RunAsync(async () =>
    {
        switch (action)
        {
            case "install" or "start" or "restart":
                await tester.RecoverInterruptedTestAsync();
                Status = await process.GetStatusAsync();
                if (Status.State is ZapretRunState.Running or ZapretRunState.External)
                    throw new InvalidOperationException("Перед запуском службы остановите работающий winws. Внешний экземпляр остановите в приложении, которое его запустило.");
                if (SelectedStrategy is null) throw new InvalidOperationException("Выберите стратегию.");
                var fresh = (await strategies.GetStrategiesAsync()).First(x => x.Id == SelectedStrategy.Id);
                if (action == "install") await service.InstallAsync(fresh);
                else
                {
                    await service.UpdateStrategyAsync(fresh);
                    if (action == "restart") await service.RestartAsync();
                    else await service.StartAsync();
                }
                break;
            case "remove":
                try { await service.RemoveAsync(); }
                finally { Status = await process.GetStatusAsync(); }
                break;
            case "stop":
                try { await service.StopAsync(); }
                finally { Status = await process.GetStatusAsync(); }
                break;
            case "auto": await service.SetAutoStartAsync(true); break;
            case "manual": await service.SetAutoStartAsync(false); break;
        }
        Status = await process.GetStatusAsync();
        if (action is "install" or "start" or "restart") await CheckConnectionCoreAsync();
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
        await RefreshDiagnosticsCoreAsync();
        Log("INFO", $"Диагностика: {Diagnostics.Count} проверок");
        Message = "Диагностика завершена";
    });
    private async Task RefreshDiagnosticsCoreAsync()
    {
        var results = await diagnostics.RunAsync();
        Diagnostics.Clear();
        foreach (var item in results) Diagnostics.Add(item);
        OnPropertyChanged(nameof(Diagnostics));
    }
    public Task SaveSettingsAsync() => RunAsync(async () =>
    {
        await BackupBeforeChangeAsync("Перед сохранением настроек");
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
        await BackupBeforeChangeAsync("Перед изменением " + file);
        await lists.AddEntryAsync(file, entry);
        Message = $"Запись добавлена в {file}";
        Log("INFO", $"Список {file} изменён");
    });
    public Task RemoveListEntryAsync(string file, string entry) => RunAsync(async () =>
    {
        await BackupBeforeChangeAsync("Перед изменением " + file);
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
    public Task TestStrategiesAsync() => TestingStrategies ? Task.CompletedTask : RunAsync(async () =>
    {
        await tester.RecoverInterruptedTestAsync();
        if (exitRequested) return;
        testCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(TestingStrategies));
        BestTestStrategy = null;
        strategyChecks.Clear();
        OnPropertyChanged(nameof(StrategyChecks));
        StrategyTestSummary = "Идёт проверка стратегий. Результат появится здесь.";
        var testFinished = 0;
        var progressGate = new object();
        var progress = new Progress<string>(line =>
        {
            if (Volatile.Read(ref testFinished) != 0 || testCancellation?.IsCancellationRequested != false) return;
            if (line.Contains("Starting config", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Best config", StringComparison.OrdinalIgnoreCase) || line.Contains("[ERROR]"))
                Log("INFO", line);
            Message = line;
        });
        var strategyProgress = new ImmediateProgress<StrategyCheckProgress>(result =>
        {
            // Store synchronously so the final callback is included in the history snapshot.
            // Only the view notification goes through the UI queue.
            lock (progressGate)
            {
                if (testFinished != 0) return;
                strategyChecks[result.FileName] = result;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(StrategyChecks)));
        });
        try
        {
            var result = await tester.RunAsync(progress, strategyProgress, testCancellation.Token);
            lock (progressGate) Volatile.Write(ref testFinished, 1);
            if (result.Cancelled) CancelRunningStrategyChecks();
            BestTestStrategy = result.BestStrategy;
            StrategyTestSummary = result.Cancelled
                ? result.BestStrategy is null
                    ? "Тест остановлен. Завершённых проверок пока нет."
                    : $"Тест остановлен. Лучшая из проверенных стратегий: {result.BestStrategy}."
                : result.BestStrategy is null
                    ? "Тест завершён без рекомендации."
                    : $"Рекомендована стратегия: {result.BestStrategy}.";
            Message = StrategyTestSummary;
            await SaveTestHistoryAsync(result);
        }
        catch (OperationCanceledException) when (testCancellation.IsCancellationRequested)
        {
            lock (progressGate) Volatile.Write(ref testFinished, 1);
            CancelRunningStrategyChecks();
            StrategyTestSummary = "Тест остановлен до завершения первой проверки.";
            Message = StrategyTestSummary;
            await SaveTestHistoryAsync(new(null, "", Cancelled: true));
        }
        catch (Exception ex)
        {
            CancelRunningStrategyChecks();
            StrategyTestSummary = "Автоподбор не выполнен: " + ex.Message;
            throw;
        }
        finally
        {
            lock (progressGate) Volatile.Write(ref testFinished, 1);
            testCancellation.Dispose();
            testCancellation = null;
            OnPropertyChanged(nameof(TestingStrategies));
        }
    });
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private void CancelRunningStrategyChecks()
    {
        foreach (var (key, result) in strategyChecks.ToArray())
            if (result.State == StrategyCheckState.Running)
                strategyChecks[key] = result with { State = StrategyCheckState.Cancelled };
        OnPropertyChanged(nameof(StrategyChecks));
    }
    public void CancelStrategyTest()
    {
        if (testCancellation is null) return;
        Message = "Останавливаем тест и сохраняем результат завершённых проверок...";
        testCancellation.Cancel();
    }
    public Task ReplaceFakeAsync(string target, string source) => RunAsync(async () =>
    {
        await BackupBeforeChangeAsync("Перед заменой " + target);
        await fakes.ReplaceAsync(target, source);
        await LoadPayloadStatesAsync();
        Message = "Активный fake payload заменён. Перезапустите стратегию.";
        Log("INFO", $"Заменён {target} из {source}");
    });
    public void RequestElevation() => privilege.RestartElevated();
    public void ReportError(string context, Exception exception)
    {
        Message = context + ": " + exception.Message;
        Log("ERROR", context + ": " + exception);
    }
    public async Task<bool> StopOwnedForExitAsync()
    {
        exitRequested = true;
        CancelStrategyTest();
        // The test holds this gate until its PowerShell and IPSet cleanup has finished.
        await operationGate.WaitAsync();
        try
        {
            await tester.RecoverInterruptedTestAsync();
            if (await process.StopOwnedAsync()) Log("INFO", "Запущенный приложением Zapret остановлен при выходе.");
            return true;
        }
        catch (Exception ex)
        {
            exitRequested = false;
            Message = "Не удалось безопасно завершить работу Zapret: " + ex.Message;
            Log("ERROR", ex.ToString());
            return false;
        }
        finally { operationGate.Release(); }
    }
    public void ClearLogs() { Logs.Clear(); FilteredLogs.Clear(); }
    public string BuildDiagnosticReport() =>
        "Zapret Desktop " + DesktopVersion + " | upstream " + UpstreamVersion + Environment.NewLine +
        string.Join(Environment.NewLine, Diagnostics.Select(x => $"{(x.Passed ? "OK" : "FAIL")} {x.Name}: {x.Detail}" +
            (x.Passed || string.IsNullOrWhiteSpace(x.Solution) ? "" : " — " + x.Solution)));
    public async Task SaveLogsAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var snapshot = Logs.ToArray();
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        foreach (var line in snapshot) await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
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
        try
        {
            if (exitRequested) return;
            Busy = true;
            await action();
        }
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
