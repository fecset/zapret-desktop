using System.Collections.ObjectModel;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    private ConnectionHealthResult? connectionHealth;
    private CancellationTokenSource? connectionCancellation;
    private long connectionVersion;
    private ManagedUpdatePlan? distributionUpdatePlan;
    private ManagedUpdatePlan? desktopUpdatePlan;
    private ManagedUpdateResult? lastDistributionUpdate;
    private IpSetUpdatePlan? ipSetUpdatePlan;
    private HostsUpdatePreview? hostsPreview;
    private IReadOnlyDictionary<string, PayloadInspection> payloadStates = new Dictionary<string, PayloadInspection>();
    private DataUpdateResult? hostsUpdateResult;
    private StrategyHistoryDetails? historyDetails;
    public ObservableCollection<UserDataBackupInfo> Backups { get; } = [];
    public ObservableCollection<StrategyHistoryEntry> TestHistory { get; } = [];
    public ConnectionHealthResult? ConnectionHealth { get => connectionHealth; private set => SetProperty(ref connectionHealth, value); }
    public ManagedUpdatePlan? DistributionUpdatePlan { get => distributionUpdatePlan; private set => SetProperty(ref distributionUpdatePlan, value); }
    public ManagedUpdatePlan? DesktopUpdatePlan { get => desktopUpdatePlan; private set => SetProperty(ref desktopUpdatePlan, value); }
    public ManagedUpdateResult? LastDistributionUpdate { get => lastDistributionUpdate; private set => SetProperty(ref lastDistributionUpdate, value); }
    public IpSetUpdatePlan? IpSetUpdatePlan { get => ipSetUpdatePlan; private set => SetProperty(ref ipSetUpdatePlan, value); }
    public HostsUpdatePreview? HostsPreview { get => hostsPreview; private set => SetProperty(ref hostsPreview, value); }
    public IReadOnlyDictionary<string, PayloadInspection> PayloadStates { get => payloadStates; private set => SetProperty(ref payloadStates, value); }
    public DataUpdateResult? HostsUpdateResult { get => hostsUpdateResult; private set => SetProperty(ref hostsUpdateResult, value); }
    public StrategyHistoryDetails? HistoryDetails { get => historyDetails; private set => SetProperty(ref historyDetails, value); }
    public string RunningStrategyName => Status?.StrategyId is { } id
        ? Strategies.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id
        : "Не определена";
    public bool CheckingConnection => connectionCancellation is not null;

    private async Task LoadFeatureDataAsync()
    {
        try
        {
            if (backups is not null)
            {
                Backups.Clear();
                foreach (var item in await backups.GetBackupsAsync()) Backups.Add(item);
            }
            if (history is not null)
            {
                TestHistory.Clear();
                foreach (var item in await history.GetHistoryAsync()) TestHistory.Add(item);
            }
            await LoadPayloadStatesAsync();
        }
        catch (Exception ex) { Log("WARN", "Не удалось прочитать резервные копии или историю: " + ex.Message); }
    }
    private async Task BackupBeforeChangeAsync(string reason)
    {
        if (backups is null) return;
        await backups.CreateAutomaticAsync(reason);
        await ReloadBackupsAsync();
    }
    private async Task ReloadBackupsAsync()
    {
        if (backups is null) return;
        var items = await backups.GetBackupsAsync();
        Backups.Clear();
        foreach (var item in items) Backups.Add(item);
    }
    private async Task SaveTestHistoryAsync(StrategyTestResult result)
    {
        if (history is null) return;
        try { TestHistory.Insert(0, await history.SaveAsync(result, strategyChecks.Values.ToArray())); }
        catch (Exception ex) { Log("WARN", "Не удалось сохранить историю автоподбора: " + ex.Message); }
    }
    private async Task RequireStoppedForFilesAsync(bool unloadDriver = false, bool allowRecovery = false)
    {
        try { await tester.RecoverInterruptedTestAsync(); }
        catch (InterruptedTestRecoveryException) when (allowRecovery) { RecoveryRequired = true; }
        Status = await process.GetStatusAsync();
        var serviceState = await service.GetStatusAsync();
        if (Status.State != ZapretRunState.Stopped || serviceState is not ("Stopped" or "NotInstalled"))
            throw new InvalidOperationException("Остановите Zapret и службу перед изменением файлов или восстановлением данных.");
        if (unloadDriver) Status = await process.StopAsync();
    }
    public Task RefreshFeatureDataAsync() => RunAsync(LoadFeatureDataAsync);
    public Task CreateBackupAsync() => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync();
        await Required(backups).CreateAsync("Ручная резервная копия");
        await ReloadBackupsAsync();
        Message = "Резервная копия создана";
    });
    public Task ExportUserDataAsync(Stream destination) => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync();
        await Required(backups).ExportAsync(destination);
        Message = "Пользовательские данные экспортированы";
    });
    public Task ImportUserDataAsync(Stream source) => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync(allowRecovery: true);
        await Required(backups).ImportAsync(source);
        await ConfirmRecoveryAsync();
        await ReloadUserDataAsync(resetDraft: true);
        Message = "Данные импортированы; предыдущая версия сохранена в резервной копии";
    });
    public Task RestoreBackupAsync(string id) => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync(allowRecovery: true);
        await Required(backups).RestoreAsync(id);
        await ConfirmRecoveryAsync();
        await ReloadUserDataAsync(resetDraft: true);
        Message = "Резервная копия восстановлена";
    });
    private async Task ReloadUserDataAsync(bool resetDraft = false)
    {
        settings = await settingsStore.LoadAsync();
        // Importing a file never enables elevated startup as a side effect.
        settings.StartWithWindows = startup.IsEnabled();
        OnPropertyChanged(nameof(Settings));
        await ReloadStrategiesAsync();
        Game = await filters.GetGameFilterAsync();
        IpSet = await filters.GetIpSetModeAsync();
        if (resetDraft) Draft.Load(settings, Game, IpSet);
        FiltersNeedRestart = false;
        OnPropertyChanged(nameof(ListFiles));
        OnPropertyChanged(nameof(FakePayloads));
        await LoadFeatureDataAsync();
    }
    private async Task ReloadStrategiesAsync()
    {
        var selected = settings.SelectedStrategyId ?? SelectedStrategy?.Id;
        Strategies.Clear();
        foreach (var strategy in await strategies.GetStrategiesAsync()) Strategies.Add(strategy);
        SelectedStrategy = Strategies.FirstOrDefault(item => item.Id == selected) ?? Strategies.FirstOrDefault();
        foreach (var warning in StartupWarnings.Where(item => item.StartsWith("Отсутствует компонент zapret: ", StringComparison.Ordinal)).ToArray())
            StartupWarnings.Remove(warning);
        foreach (var problem in distribution.Validate()) StartupWarnings.Add("Отсутствует компонент zapret: " + problem);
        OnPropertyChanged(nameof(DistributionProblems));
        OnPropertyChanged(nameof(UpstreamVersion));
    }
    private async Task LoadPayloadStatesAsync()
    {
        if (payloadRecovery is null) return;
        var states = new Dictionary<string, PayloadInspection>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { "ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin" })
            states[target] = await payloadRecovery.InspectAsync(target);
        PayloadStates = states;
    }
    public Task RestorePayloadAsync(string target) => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync();
        await BackupBeforeChangeAsync("Перед восстановлением " + target);
        await Required(payloadRecovery).RestoreOriginalAsync(target);
        await LoadPayloadStatesAsync();
        Message = "Замена файла отменена. Перезапустите стратегию, чтобы применить изменение.";
    });
    public async Task CheckConnectionAsync()
    {
        if (Busy || exitRequested) return;
        if (await CheckConnectionCoreAsync()) Message = "Проверка HTTP/TLS доступности завершена";
    }
    private void CancelConnectionCheck()
    {
        connectionVersion++;
        var previous = connectionCancellation;
        connectionCancellation = null;
        OnPropertyChanged(nameof(CheckingConnection));
        previous?.Cancel();
    }
    private async Task<bool> CheckConnectionCoreAsync()
    {
        CancelConnectionCheck();
        if (connection is null || exitRequested) return false;
        using var cancellation = new CancellationTokenSource();
        connectionCancellation = cancellation;
        OnPropertyChanged(nameof(CheckingConnection));
        try
        {
            var result = await connection.CheckAsync(cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
            if (cancellation.IsCancellationRequested || exitRequested || connectionCancellation != cancellation) return false;
            ConnectionHealth = result;
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Log("WARN", "Не удалось проверить доступность сервисов: " + ex.Message); }
        finally
        {
            if (connectionCancellation == cancellation)
            {
                connectionCancellation = null;
                OnPropertyChanged(nameof(CheckingConnection));
            }
        }
        return false;
    }
    private async Task RunEngineActionAsync(Func<Task> action, bool checkConnection = true)
    {
        long? completedVersion = null;
        await RunAsync(async () =>
        {
            CancelConnectionCheck();
            ConnectionHealth = null;
            await action();
            completedVersion = connectionVersion;
        }, skipIfBusy: true, allowDuringPreparation: true);
        // Network latency must not hold the gate used by start, stop and strategy selection.
        if (checkConnection && settings.CheckConnectionAfterStart && completedVersion == connectionVersion && !exitRequested)
            await CheckConnectionCoreAsync();
    }
    public Task ShowHistoryAsync(string id) => RunAsync(async () =>
        HistoryDetails = await Required(history).GetDetailsAsync(id));
    public void CloseHistory() => HistoryDetails = null;
    public Task ApplyRecommendationAsync(string? recommended = null) => RunAsync(async () =>
    {
        var file = recommended ?? BestTestStrategy;
        var strategy = Strategies.FirstOrDefault(item => Path.GetFileName(item.SourceFile).Equals(file, StringComparison.OrdinalIgnoreCase));
        if (strategy is null) throw new InvalidOperationException("Рекомендованная стратегия отсутствует в текущей поставке.");
        SelectedStrategy = strategy;
        settings.SelectedStrategyId = strategy.Id;
        await settingsStore.SaveAsync(settings);
        Message = "Рекомендация сохранена для следующего запуска";
    });
    public Task ApplyAndRestartAsync() => RunEngineActionAsync(async () =>
        await ApplyAndRestartCoreAsync());
    private async Task ApplyAndRestartCoreAsync()
    {
        await tester.RecoverInterruptedTestAsync();
        if (SelectedStrategy is null) throw new InvalidOperationException("Выберите стратегию.");
        var fresh = (await strategies.GetStrategiesAsync()).First(item => item.Id == SelectedStrategy.Id);
        Status = await process.GetStatusAsync();
        if (Status.State == ZapretRunState.External) throw new InvalidOperationException("Остановите внешний экземпляр winws в приложении, которое его запустило.");
        if (Status.State == ZapretRunState.ServiceRunning)
        {
            await service.UpdateStrategyAsync(fresh);
            await service.RestartAsync();
            Status = await process.GetStatusAsync();
        }
        else Status = await process.RestartAsync(fresh);
        FiltersNeedRestart = false;
        Message = "Выбранная стратегия и текущие фильтры применены";
    }
    public Task PrepareDistributionUpdateAsync() => PrepareAsync("Загружаем и проверяем обновление zapret.",
        token => Required(managedUpdates).PrepareDistributionAsync(token), plan =>
    {
        DistributionUpdatePlan = plan;
        Message = "Обновление zapret загружено и проверено. Теперь его можно установить.";
    });
    public Task ApplyDistributionUpdateAsync() => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync(unloadDriver: true);
        await BackupBeforeChangeAsync("Перед обновлением zapret");
        LastDistributionUpdate = await Required(managedUpdates).ApplyDistributionAsync(
            DistributionUpdatePlan ?? throw new InvalidOperationException("Сначала загрузите обновление zapret."));
        DistributionUpdatePlan = null;
        await ReloadUserDataAsync();
        Message = LastDistributionUpdate.Summary;
    });
    public Task RollbackDistributionUpdateAsync() => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync(unloadDriver: true);
        await Required(managedUpdates).RollbackDistributionAsync(
            LastDistributionUpdate ?? throw new InvalidOperationException("Нет обновления для отката."));
        LastDistributionUpdate = null;
        await ReloadUserDataAsync();
        Message = "Предыдущая версия zapret восстановлена";
    });
    public Task PrepareDesktopUpdateAsync() => PrepareAsync("Загружаем и проверяем обновление Desktop.",
        token => Required(managedUpdates).PrepareDesktopAsync(DesktopVersion, token), plan =>
    {
        DesktopUpdatePlan = plan;
        Message = "Обновление Desktop загружено и проверено. Установка выполняется после выхода.";
    });
    public async Task<bool> InstallDesktopUpdateForExitAsync()
    {
        if (DesktopUpdatePlan is null || Busy || PreparingData)
        {
            Message = "Сначала загрузите обновление и дождитесь завершения текущей операции.";
            return false;
        }
        if (!await StopOwnedForExitAsync()) return false;
        try
        {
            var state = await process.GetStatusAsync();
            var serviceState = await service.GetStatusAsync();
            if (state.State != ZapretRunState.Stopped || serviceState is not ("Stopped" or "NotInstalled"))
                throw new InvalidOperationException("Остановите службу и внешний winws перед обновлением Desktop.");
            Status = await process.StopAsync();
            var scheduled = await Required(managedUpdates).ScheduleDesktopUpdateAsync(DesktopUpdatePlan, Environment.ProcessId);
            Log("INFO", scheduled.Summary + " Журнал: " + scheduled.LogPath);
            return true;
        }
        catch (Exception ex)
        {
            exitRequested = false;
            ReportError("Не удалось запланировать обновление", ex);
            return false;
        }
    }
    public Task PrepareIpSetUpdateAsync() => PrepareAsync("Загружаем список IP-адресов.",
        token => Required(managedUpdates).PrepareIpSetAsync(token), plan => IpSetUpdatePlan = plan);
    public Task ApplyIpSetUpdateAsync() => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync();
        await BackupBeforeChangeAsync("Перед обновлением IPSet");
        var result = await Required(managedUpdates).ApplyIpSetAsync(
            IpSetUpdatePlan ?? throw new InvalidOperationException("Сначала загрузите IPSet."));
        IpSetUpdatePlan = null;
        IpSet = await filters.GetIpSetModeAsync();
        Message = result.Summary;
    });
    public Task PreviewHostsAsync() => PrepareAsync("Готовим предпросмотр hosts.",
        token => Required(managedUpdates).PreviewHostsAsync(token), preview =>
    {
        HostsPreview = preview;
        HostsUpdateResult = null;
        Message = preview.HasChanges ? "Предпросмотр готов. Файл hosts ещё не изменён." : "Изменений нет: hosts уже содержит эти адреса.";
    });
    public Task ApplyHostsAsync() => RunAsync(async () =>
    {
        var preview = HostsPreview ?? throw new InvalidOperationException("Сначала просмотрите изменения hosts.");
        if (preview.HasChanges) await RequireStoppedForFilesAsync();
        var result = await Required(managedUpdates).ApplyHostsAsync(preview);
        HostsPreview = null;
        HostsUpdateResult = result;
        if (result.Changed && diagnostics is not null)
        {
            Message = result.Summary + " Обновляем диагностику...";
            await RefreshDiagnosticsCoreAsync();
        }
        Message = result.Summary;
    });
    private static T Required<T>(T? instance) where T : class =>
        instance ?? throw new InvalidOperationException("Компонент недоступен в этой конфигурации приложения.");
}
