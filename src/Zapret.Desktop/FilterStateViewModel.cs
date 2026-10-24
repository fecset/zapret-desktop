using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    public const string StrategyTestRestriction = "Дождитесь завершения автоподбора или отмените его на странице «Стратегии». Диагностика и работа со списками временно недоступны.";
    private bool filtersNeedRestart;
    public bool FiltersNeedRestart { get => filtersNeedRestart; private set => SetProperty(ref filtersNeedRestart, value); }

    private async Task MarkChangedFiltersAsync()
    {
        Status = await process.GetStatusAsync();
        FiltersNeedRestart = Status.State is ZapretRunState.Running or ZapretRunState.ServiceRunning or ZapretRunState.External;
    }
}
