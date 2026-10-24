using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    public async Task ApplyFilterDraftAsync(bool restart = false)
    {
        if (PreparingData) { Message = "Перед применением фильтров завершите или отмените подготовку данных."; return; }
        var gameDraft = Draft.GameFilter;
        var ipsetDraft = Draft.IpSetMode;
        var saved = false;
        async Task ApplyAsync()
        {
            FilterRules.Validate(gameDraft, ipsetDraft);
            if (gameDraft == await filters.GetGameFilterAsync() && ipsetDraft == await filters.GetIpSetModeAsync())
            {
                Message = "Фильтры не изменились";
                if (restart) await ApplyAndRestartCoreAsync();
                return;
            }
            await BackupBeforeChangeAsync("Перед применением фильтров");
            await filters.SetFiltersAsync(gameDraft, ipsetDraft);
            saved = true;
            Game = await filters.GetGameFilterAsync();
            IpSet = await filters.GetIpSetModeAsync();
            await MarkChangedFiltersAsync();
            Message = "Фильтры сохранены. Изменения вступят в силу при следующем запуске Zapret.";
            if (restart) await ApplyAndRestartCoreAsync();
        }
        if (restart) await RunEngineActionAsync(ApplyAsync);
        else await RunAsync(ApplyAsync);
        if (restart && saved && Message.StartsWith("Ошибка:", StringComparison.Ordinal))
            Message = "Ошибка: фильтры сохранены, но перезапуск не выполнен. " + Message[7..];
    }
}
