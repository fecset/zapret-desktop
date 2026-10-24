namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private readonly HashSet<string> activeButtonActions = [];
    private readonly List<(ActionButton Button, string Key, Func<bool>? ExternalBusy)> actionButtons = [];

    private void TrackButton(ActionButton button, string key, Func<Task> action, Func<bool>? externalBusy = null)
    {
        actionButtons.Add((button, key, externalBusy));
        button.IsBusy = activeButtonActions.Contains(key) || externalBusy?.Invoke() == true;
        button.Click += async (_, _) =>
        {
            if (!button.IsEffectivelyEnabled || !activeButtonActions.Add(key)) return;
            RefreshButtonActivity();
            try { await action(); }
            catch (Exception ex) { vm.ReportError("Не удалось выполнить действие", ex); }
            finally
            {
                activeButtonActions.Remove(key);
                button.IsBusy = false;
                RefreshButtonActivity();
            }
        };
    }

    private void RefreshButtonActivity()
    {
        foreach (var (button, key, externalBusy) in actionButtons)
            button.IsBusy = activeButtonActions.Contains(key) || externalBusy?.Invoke() == true;
    }
}
