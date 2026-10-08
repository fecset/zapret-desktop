using System.Collections.ObjectModel;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    private ConnectionHealthResult? connectionHealth;
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
        var backup = await backups.CreateAsync(reason);
        Backups.Insert(0, backup);
    }
    private async Task SaveTestHistoryAsync(StrategyTestResult result)
    {
        if (history is null) return;
        try { TestHistory.Insert(0, await history.SaveAsync(result, strategyChecks.Values.ToArray())); }
        catch (Exception ex) { Log("WARN", "Не удалось сохранить историю автоподбора: " + ex.Message); }
    }
    private async Task RequireStoppedForFilesAsync(bool unloadDriver = false)
    {
        await tester.RecoverInterruptedTestAsync();
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
        await BackupBeforeChangeAsync("Ручная резервная копия");
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
        await RequireStoppedForFilesAsync();
        await Required(backups).ImportAsync(source);
        await ReloadUserDataAsync();
        Message = "Данные импортированы; предыдущая версия сохранена в резервной копии";
    });
    public Task RestoreBackupAsync(string id) => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync();
        await Required(backups).RestoreAsync(id);
        await ReloadUserDataAsync();
        Message = "Резервная копия восстановлена";
    });
    private async Task ReloadUserDataAsync()
    {
        settings = await settingsStore.LoadAsync();
        // Importing a file never enables elevated startup as a side effect.
        settings.StartWithWindows = startup.IsEnabled();
        OnPropertyChanged(nameof(Settings));
        await ReloadStrategiesAsync();
        Game = await filters.GetGameFilterAsync();
        IpSet = await filters.GetIpSetModeAsync();
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
    public Task CheckConnectionAsync() => RunAsync(async () =>
    {
        await CheckConnectionCoreAsync();
        Message = "Проверка HTTP/TLS доступности завершена";
    });
    private async Task CheckConnectionCoreAsync()
    {
        if (connection is null) return;
        try { ConnectionHealth = await connection.CheckAsync(); }
        catch (Exception ex) { Log("WARN", "Не удалось проверить доступность сервисов: " + ex.Message); }
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
    public Task ApplyAndRestartAsync() => RunAsync(async () =>
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
        await CheckConnectionCoreAsync();
        Message = "Выбранная стратегия и текущие фильтры применены";
    });
    public Task PrepareDistributionUpdateAsync() => RunAsync(async () =>
    {
        DistributionUpdatePlan = await Required(managedUpdates).PrepareDistributionAsync();
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
    public Task PrepareDesktopUpdateAsync() => RunAsync(async () =>
    {
        DesktopUpdatePlan = await Required(managedUpdates).PrepareDesktopAsync(DesktopVersion);
        Message = "Обновление Desktop загружено и проверено. Установка выполняется после выхода.";
    });
    public async Task<bool> InstallDesktopUpdateForExitAsync()
    {
        if (DesktopUpdatePlan is null || Busy)
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
    public Task PrepareIpSetUpdateAsync() => RunAsync(async () =>
        IpSetUpdatePlan = await Required(managedUpdates).PrepareIpSetAsync());
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
    public Task PreviewHostsAsync() => RunAsync(async () =>
    {
        HostsPreview = await Required(managedUpdates).PreviewHostsAsync();
        HostsUpdateResult = null;
        Message = "Предпросмотр готов. Файл hosts ещё не изменён.";
    });
    public Task ApplyHostsAsync() => RunAsync(async () =>
    {
        await RequireStoppedForFilesAsync();
        var result = await Required(managedUpdates).ApplyHostsAsync(
            HostsPreview ?? throw new InvalidOperationException("Сначала просмотрите изменения hosts."));
        HostsPreview = null;
        HostsUpdateResult = result;
        Message = result.Summary + " Обновляем диагностику...";
        if (diagnostics is not null) await RefreshDiagnosticsCoreAsync();
        Message = result.Summary;
    });
    private static T Required<T>(T? instance) where T : class =>
        instance ?? throw new InvalidOperationException("Компонент недоступен в этой конфигурации приложения.");
}
