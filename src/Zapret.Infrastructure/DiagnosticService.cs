using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class DiagnosticService(IZapretDistribution distribution, IStrategyProvider strategies,
    IPrivilegeService privilege, IZapretProcessManager process) : IDiagnosticService
{
    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<DiagnosticItem>();
        foreach (var relative in new[] { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys",
            "lists/list-general.txt", "lists/ipset-all.txt" })
        {
            var present = File.Exists(Path.Combine(distribution.Root, relative));
            result.Add(new(relative, present, present ? "Файл найден" : "Файл отсутствует", "Обновите дистрибутив zapret из официального релиза."));
        }
        result.Add(new("Права администратора", privilege.IsAdministrator,
            privilege.IsAdministrator ? "Доступны" : "Недоступны", "Для запуска winws и управления службой запустите приложение от администратора."));
        try
        {
            var count = (await strategies.GetStrategiesAsync(cancellationToken)).Count;
            result.Add(new("Стратегии", count > 0, $"Найдено: {count}", "Добавьте файлы general*.bat в корень дистрибутива."));
            foreach (var error in strategies.Errors)
                result.Add(new("Разбор стратегии", false, error, "Проверьте BAT-синтаксис и файлы, на которые ссылается стратегия."));
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
        {
            result.Add(new("Стратегии", false, ex.Message, "Проверьте BAT-синтаксис и файлы, на которые ссылается стратегия."));
        }
        var runtime = await process.GetStatusAsync(cancellationToken);
        var serviceState = runtime.ServiceStatus;
        result.Add(new("Служба zapret (необязательно)", serviceState is "Running" or "Stopped" or "NotInstalled",
            serviceState switch
            {
                "Running" => "Работает: Zapret запущен как служба Windows.",
                "Stopped" => "Остановлена. Запуск с главной страницы возможен без службы.",
                "NotInstalled" => "Не установлена. Запуск с главной страницы возможен без службы.",
                _ => StatusText(serviceState)
            }, "Проверьте службу zapret на странице настроек."));
        var driver = runtime.DriverStatus;
        var zapretActive = runtime.State is ZapretRunState.Running or ZapretRunState.ServiceRunning or ZapretRunState.External;
        result.Add(new("Сетевой драйвер WinDivert", driver == "Running" || !zapretActive && driver is "Stopped" or "NotInstalled",
            driver == "Running" && !zapretActive ? "Драйвер загружен, но Zapret не запущен. Сам по себе этот статус не означает работу Zapret." :
            driver == "Running" ? "Драйвер загружен." : !zapretActive && driver is "Stopped" or "NotInstalled"
                ? "Не загружен: это нормально, пока Zapret не запущен." : StatusText(driver),
            "Если Zapret запущен, проверьте права администратора и файлы драйвера."));
        var bfe = await ScServiceManager.QueryStatusAsync("BFE", cancellationToken);
        result.Add(new("Системная служба фильтрации Windows (BFE)", bfe == "Running",
            bfe == "Running" ? "Работает. Требуется драйверу WinDivert." : StatusText(bfe),
            "Включите системную службу «Служба базовой фильтрации» в Windows."));
        return result;
    }

    private static string StatusText(string state) => state switch
    {
        "Stopped" => "Остановлена.", "NotInstalled" => "Не установлена.",
        "StartPending" => "Запускается.", "StopPending" => "Останавливается.",
        "AccessDenied" => "Не удалось проверить: нет доступа.",
        _ => "Не удалось определить состояние."
    };
}
