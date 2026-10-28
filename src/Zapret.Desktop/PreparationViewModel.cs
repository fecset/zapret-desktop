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

    private Task PrepareAsync<T>(string label, Func<CancellationToken, Task<T>> prepare, Action<T> publish) =>
        PrepareAsync(label, (_, token) => prepare(token), publish);

    private async Task PrepareAsync<T>(string label,
        Func<IProgress<ManagedUpdateProgress>, CancellationToken, Task<T>> prepare, Action<T> publish)
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
            if (!cancellation.IsCancellationRequested && !exitRequested) publish(result);
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
