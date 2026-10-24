namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? preparationCancellation;
    private TaskCompletionSource? preparationCompletion;
    private string preparationMessage = "";
    public bool PreparingData => preparationCancellation is not null;
    public string PreparationMessage { get => preparationMessage; private set => SetProperty(ref preparationMessage, value); }

    public void CancelPreparation() => preparationCancellation?.Cancel();

    private async Task PrepareAsync<T>(string label, Func<CancellationToken, Task<T>> prepare, Action<T> publish)
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
        OnPropertyChanged(nameof(PreparingData));
        try
        {
            // ZIP validation and hashing must not run on the UI thread.
            var result = await Task.Run(() => prepare(cancellation.Token), cancellation.Token);
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
            OnPropertyChanged(nameof(PreparingData));
            completion.TrySetResult();
        }
    }
}
