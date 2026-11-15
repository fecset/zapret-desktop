using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? preparationCancellation;
    private TaskCompletionSource? preparationCompletion;
    private string preparationMessage = "";
    private double? preparationProgress;
    private string preparationStage = "";
    private string preparationDetail = "";
    public bool PreparingData => preparationCancellation is not null;
    public string PreparationMessage { get => preparationMessage; private set => SetProperty(ref preparationMessage, value); }
    public double? PreparationProgress => preparationProgress;
    public bool PreparationIndeterminate => preparationProgress is null;
    public string PreparationStage => preparationStage;
    public string PreparationDetail => preparationDetail;

    public void CancelPreparation() => preparationCancellation?.Cancel();

    private Task PrepareAsync<T>(string label, Func<CancellationToken, Task<T>> prepare, Action<T> publish,
        Func<string?>? previousPlanId = null) =>
        PrepareAsync(label, (_, token) => prepare(token), publish, previousPlanId);

    private async Task PrepareAsync<T>(string label,
        Func<IProgress<ManagedUpdateProgress>, CancellationToken, Task<T>> prepare, Action<T> publish,
        Func<string?>? previousPlanId = null)
    {
        if (Busy || PreparingData || exitRequested)
        {
            Message = "Дождитесь завершения текущей операции или отмените подготовку.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        preparationCancellation = cancellation;
        preparationCompletion = completion;
        PreparationMessage = label + " Можно продолжать управлять Zapret или отменить подготовку.";
        preparationProgress = null;
        preparationStage = "Подготовка";
        preparationDetail = "";
        OnPropertyChanged(nameof(PreparingData));
        OnPropertyChanged(nameof(PreparationProgress));
        OnPropertyChanged(nameof(PreparationIndeterminate));
        OnPropertyChanged(nameof(PreparationStage));
        OnPropertyChanged(nameof(PreparationDetail));
        var progress = new Progress<ManagedUpdateProgress>(value =>
        {
            if (preparationCancellation != cancellation) return;
            preparationProgress = value.Fraction;
            preparationStage = value.Stage;
            preparationDetail = FormatProgressDetail(value);
            OnPropertyChanged(nameof(PreparationProgress));
            OnPropertyChanged(nameof(PreparationIndeterminate));
            OnPropertyChanged(nameof(PreparationStage));
            OnPropertyChanged(nameof(PreparationDetail));
        });
        try
        {
            // ZIP validation and hashing must not run on the UI thread.
            var result = await Task.Run(() => prepare(progress, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || exitRequested)
            {
                await DiscardPreparedPlanAsync(PreparedPlanId(result));
                if (!exitRequested) Message = "Подготовка отменена. Установленные файлы не изменены.";
                return;
            }
            var previous = previousPlanId?.Invoke();
            publish(result);
            if (previous != PreparedPlanId(result)) await DiscardPreparedPlanAsync(previous);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!exitRequested) Message = "Подготовка отменена. Установленные файлы не изменены.";
        }
        catch (Exception ex) { if (!exitRequested) ReportError("Не удалось подготовить данные", ex); }
        finally
        {
            preparationCancellation = null;
            preparationCompletion = null;
            PreparationMessage = "";
            preparationProgress = null;
            preparationStage = "";
            preparationDetail = "";
            OnPropertyChanged(nameof(PreparingData));
            OnPropertyChanged(nameof(PreparationProgress));
            OnPropertyChanged(nameof(PreparationIndeterminate));
            OnPropertyChanged(nameof(PreparationStage));
            OnPropertyChanged(nameof(PreparationDetail));
            completion.TrySetResult();
        }
    }

    private static string? PreparedPlanId<T>(T result) => result switch
    {
        ManagedUpdatePlan update => update.Id,
        IpSetUpdatePlan ipset => ipset.Id,
        HostsUpdatePreview hosts => hosts.Id,
        _ => null
    };

    private async Task DiscardPreparedPlanAsync(string? id)
    {
        if (id is null || managedUpdates is null) return;
        try { await managedUpdates.DiscardPreparedAsync(id); }
        catch (Exception ex)
        {
            // A prepared replacement remains usable even if an older workspace is locked.
            Log("WARN", "Не удалось очистить временные данные подготовки " + id + ": " + ex.Message);
            if (!exitRequested) Message += " Не удалось удалить прежние временные данные; подробности в журнале.";
        }
    }

    private async Task DiscardPendingPreparationsAsync(bool preserveDesktopUpdate)
    {
        var ids = new[] { DistributionUpdatePlan?.Id, preserveDesktopUpdate ? null : DesktopUpdatePlan?.Id,
            IpSetUpdatePlan?.Id, HostsPreview?.Id };
        foreach (var id in ids.Where(id => id is not null).Distinct()) await DiscardPreparedPlanAsync(id);
        DistributionUpdatePlan = null;
        if (!preserveDesktopUpdate) DesktopUpdatePlan = null;
        IpSetUpdatePlan = null;
        HostsPreview = null;
    }

    private static string FormatProgressDetail(ManagedUpdateProgress progress)
    {
        if (progress.BytesReceived <= 0) return "";
        var received = FormatBytes(progress.BytesReceived);
        return progress.TotalBytes is > 0 ? $"{received} из {FormatBytes(progress.TotalBytes.Value)}" : received;
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):F1} МБ"
        : $"{Math.Max(1, bytes / 1024)} КБ";
}
