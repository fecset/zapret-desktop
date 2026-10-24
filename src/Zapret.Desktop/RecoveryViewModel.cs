using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    private bool recoveryRequired;
    public bool RecoveryRequired { get => recoveryRequired; private set => SetProperty(ref recoveryRequired, value); }

    private async Task ConfirmRecoveryAsync()
    {
        if (!RecoveryRequired) return;
        await tester.ConfirmRestoredIpSetAsync();
        RecoveryRequired = false;
        foreach (var warning in StartupWarnings.Where(w => w.StartsWith("Не удалось восстановить IPSet после теста:", StringComparison.Ordinal)).ToArray())
            StartupWarnings.Remove(warning);
    }
}
