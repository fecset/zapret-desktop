using Zapret.Core;
using System.Net;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zapret.Infrastructure;

public sealed class DiagnosticService : IDiagnosticService
{
    private readonly IZapretDistribution distribution;
    private readonly IStrategyProvider strategies;
    private readonly IPrivilegeService privilege;
    private readonly IZapretProcessManager process;
    private readonly IDiagnosticSystemProbe system;

    public DiagnosticService(IZapretDistribution distribution, IStrategyProvider strategies,
        IPrivilegeService privilege, IZapretProcessManager process)
        : this(distribution, strategies, privilege, process, new WindowsDiagnosticSystemProbe()) { }

    internal DiagnosticService(IZapretDistribution distribution, IStrategyProvider strategies,
        IPrivilegeService privilege, IZapretProcessManager process, IDiagnosticSystemProbe system)
    {
        this.distribution = distribution;
        this.strategies = strategies;
        this.privilege = privilege;
        this.process = process;
        this.system = system;
    }

    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<DiagnosticItem>();
        foreach (var relative in new[] { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys",
            "lists/list-general.txt", "lists/ipset-all.txt" })
        {
            var present = File.Exists(Path.Combine(distribution.Root, relative));
            result.Add(new(relative, present, present ? "Файл найден" : "Файл отсутствует", "Обновите дистрибутив zapret из официального релиза."));
        }
        await CheckAsync("Комплект дистрибутива", "Восстановите полную поставку zapret из официального релиза.", _ =>
        {
            var missing = distribution.Validate();
            return Task.FromResult((missing.Count == 0, missing.Count == 0 ? "Обязательные файлы найдены." : "Отсутствуют: " + string.Join(", ", missing)));
        });
        result.Add(new("Права администратора", privilege.IsAdministrator,
            privilege.IsAdministrator ? "Доступны" : "Недоступны", "Для запуска winws и управления службой запустите приложение от администратора."));
        try
        {
            var count = (await strategies.GetStrategiesAsync(cancellationToken)).Count;
            result.Add(new("Стратегии", count > 0, $"Найдено: {count}", "Добавьте файлы general*.bat в корень дистрибутива."));
            foreach (var error in strategies.Errors)
                result.Add(new("Разбор стратегии", false, error, "Проверьте BAT-синтаксис и файлы, на которые ссылается стратегия."));
        }
        catch (Exception ex) when (IsCheckError(ex) || ex is FormatException)
        {
            result.Add(new("Стратегии", false, ex.Message, "Проверьте BAT-синтаксис и файлы, на которые ссылается стратегия."));
        }
        ZapretStatus? runtime = null;
        await CheckAsync("Состояние Zapret", "Повторите проверку. При ошибке доступа запустите приложение от администратора.", async ct =>
        {
            runtime = await process.GetStatusAsync(ct);
            return (true, $"{runtime.State}; PID: {runtime.ProcessId?.ToString() ?? "—"}; стратегия активного процесса: {runtime.StrategyId ?? "не определена"}.");
        });
        if (runtime is not null)
        {
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
        }
        await CheckAsync("Системная служба фильтрации Windows (BFE)",
            "Включите системную службу «Служба базовой фильтрации» в Windows.", async ct =>
        {
            var bfe = await system.GetServiceStatusAsync("BFE", ct);
            return (bfe == "Running", bfe == "Running" ? "Работает. Требуется драйверу WinDivert." : StatusText(bfe));
        });
        await CheckAsync("DNS-серверы", "Проверьте DNS-настройки активного сетевого адаптера.", async ct =>
        {
            var servers = await system.GetDnsServersAsync(ct);
            return (servers.Count > 0, servers.Count > 0 ? string.Join("; ", servers) : "У активных адаптеров не найдены DNS-серверы.");
        });
        foreach (var host in ConnectionHealthService.Hosts)
            await CheckAsync("DNS " + host, "Проверьте соединение, DNS и файл hosts. Диагностика не меняет DNS-настройки.", async ct =>
            {
                var addresses = await system.ResolveAsync(host, ct);
                var valid = addresses.Length > 0 && !addresses.Any(address => IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
                return (valid, addresses.Length == 0 ? "Ответ DNS пуст." : string.Join(", ", addresses.Select(address => address.ToString())) + (valid ? "" : "; найден локальный или нулевой адрес."));
            });
        await CheckAsync("Hosts", "Проверьте указанные переопределения. Изменение hosts выполняется отдельным явным действием.", async ct =>
        {
            var hosts = await system.ReadHostsAsync(ct);
            var overrides = FindHostsOverrides(hosts);
            return (overrides.Count == 0, overrides.Count == 0 ? "Активных переопределений YouTube/Discord не найдено." : "Переопределения: " + string.Join("; ", overrides.Take(50)));
        });
        await CheckAsync("Прокси Windows (WinINET)", "Проверьте настройки прокси Windows и сценарий автоматической настройки.", async ct =>
        {
            var proxy = await system.GetProxyAsync(ct);
            var configured = proxy.Enabled || !string.IsNullOrWhiteSpace(proxy.AutoConfigUrl);
            var detail = $"Ручной прокси: {(proxy.Enabled ? "включён" : "отключён")}.";
            if (proxy.Enabled) detail += " Сервер: " + RedactProxy(proxy.Server) + ".";
            if (!string.IsNullOrWhiteSpace(proxy.AutoConfigUrl)) detail += " PAC: " + RedactProxy(proxy.AutoConfigUrl) + ".";
            return (!configured, detail);
        });
        await CheckAsync("Прокси окружения", "Проверьте HTTP_PROXY, HTTPS_PROXY, ALL_PROXY и NO_PROXY в переменных окружения.", async ct =>
        {
            var environment = await system.GetEnvironmentProxyAsync(ct);
            var values = environment.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToArray();
            var active = values.Any(pair => !pair.Key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase));
            return (!active, values.Length == 0 ? "Переменные прокси не заданы." : string.Join("; ", values.Select(pair =>
                pair.Key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) ? "NO_PROXY задан" : pair.Key + "=" + RedactProxy(pair.Value))));
        });
        await CheckAsync("Возможные конфликты ПО", "Проверьте настройки сетевой фильтрации найденных программ. Наличие процесса не доказывает конфликт.", async ct =>
        {
            var names = await system.GetProcessNamesAsync(ct);
            var known = new[] { "goodbyedpi", "AdguardSvc", "Adguard", "BHSvc", "ciadpi", "ByeDPI", "dvtws" };
            var found = names.Where(name => known.Contains(name, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var winwsCount = names.Count(name => name.Equals("winws", StringComparison.OrdinalIgnoreCase));
            if (winwsCount > 1) found.Add($"winws: {winwsCount} процессов");
            return (found.Count == 0, found.Count == 0 ? "Из известных процессов фильтрации конфликтующие программы не обнаружены." : "Возможная дополнительная фильтрация: " + string.Join(", ", found));
        });
        foreach (var name in new[] { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" })
            await CheckAsync("Служба возможного конфликта " + name, "Проверьте назначение службы и её настройки. Диагностика не останавливает службы.", async ct =>
            {
                var state = await system.GetServiceStatusAsync(name, ct);
                return (state is "NotInstalled" or "Stopped", state == "Running" ? "Работает; возможна дополнительная фильтрация." : StatusText(state));
            });
        await CheckAsync("Фактические параметры winws", "Проверьте параметры активного процесса и соответствие выбранной стратегии. Для чтения чужого процесса могут потребоваться права администратора.", async ct =>
        {
            if (runtime is null) return (false, "Состояние процесса не удалось определить.");
            if (runtime.State == ZapretRunState.Stopped) return (true, "Zapret не запущен. Активных параметров запуска нет.");
            if (runtime.ProcessId is { } pid)
            {
                var command = await system.GetCommandLineAsync(pid, ct);
                return (!string.IsNullOrWhiteSpace(command), string.IsNullOrWhiteSpace(command) ? $"PID {pid}: параметры недоступны." : $"PID {pid}: {command}");
            }
            if (runtime.State == ZapretRunState.ServiceRunning)
            {
                var configured = await system.GetServiceCommandLineAsync(ct);
                return (false, "PID работающей службы не определён. Настроенная команда службы (активные параметры не подтверждены): " + (configured ?? "недоступна"));
            }
            return (false, "PID активного процесса не определён; параметры недоступны.");
        });
        return result;

        async Task CheckAsync(string name, string solution, Func<CancellationToken, Task<(bool Passed, string Detail)>> check)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var item = await check(deadline.Token).WaitAsync(deadline.Token);
                result.Add(new(name, item.Passed, item.Detail, solution));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.Add(new(name, false, "Превышено время ожидания проверки.", solution));
            }
            catch (Exception ex) when (IsCheckError(ex))
            {
                result.Add(new(name, false, "Не удалось проверить: " + ex.Message, solution));
            }
        }
    }

    internal static IReadOnlyList<string> FindHostsOverrides(string hosts)
    {
        var domains = new[] { "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "discord.com", "discord.gg", "discordapp.com", "discordapp.net" };
        var result = new List<string>();
        foreach (var raw in hosts.Split('\n'))
        {
            var comment = raw.IndexOf('#');
            var line = comment >= 0 ? raw[..comment] : raw;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !IPAddress.TryParse(tokens[0], out _)) continue;
            foreach (var token in tokens.Skip(1))
            {
                var name = token.TrimEnd('.');
                if (domains.Any(domain => name.Equals(domain, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
                    result.Add(tokens[0] + " " + token);
            }
        }
        return result;
    }

    private static string RedactProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "не задан";
        var redacted = Regex.Replace(value, @"[^\s/;@]+@", "***@", RegexOptions.CultureInvariant);
        redacted = Regex.Replace(redacted, @"[?#][^;\s]*", "", RegexOptions.CultureInvariant);
        return redacted.Length <= 512 ? redacted : redacted[..512];
    }

    private static bool IsCheckError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or
        ArgumentException or NotSupportedException or TimeoutException;

    private static string StatusText(string state) => state switch
    {
        "Stopped" => "Остановлена.", "NotInstalled" => "Не установлена.",
        "StartPending" => "Запускается.", "StopPending" => "Останавливается.",
        "AccessDenied" => "Не удалось проверить: нет доступа.",
        _ => "Не удалось определить состояние."
    };
}

internal sealed record DiagnosticProxyConfiguration(bool Enabled, string? Server,
    string? AutoConfigUrl, IReadOnlyDictionary<string, string?> Environment);

internal interface IDiagnosticSystemProbe
{
    Task<string> GetServiceStatusAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetDnsServersAsync(CancellationToken cancellationToken);
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
    Task<string> ReadHostsAsync(CancellationToken cancellationToken);
    Task<DiagnosticProxyConfiguration> GetProxyAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string?>> GetEnvironmentProxyAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetProcessNamesAsync(CancellationToken cancellationToken);
    Task<string?> GetCommandLineAsync(int processId, CancellationToken cancellationToken);
    Task<string?> GetServiceCommandLineAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsDiagnosticSystemProbe : IDiagnosticSystemProbe
{
    public Task<string> GetServiceStatusAsync(string name, CancellationToken cancellationToken) => ScServiceManager.QueryStatusAsync(name, cancellationToken);
    public Task<IReadOnlyList<string>> GetDnsServersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up))
        {
            var addresses = adapter.GetIPProperties().DnsAddresses;
            if (addresses.Count > 0) result.Add(adapter.Name + ": " + string.Join(", ", addresses.Select(address => address.ToString())));
        }
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    public async Task<string> ReadHostsAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        if (input.Length > 1024 * 1024) throw new IOException("Размер hosts превышает предел чтения 1 МиБ.");
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public Task<DiagnosticProxyConfiguration> GetProxyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return Task.FromResult(new DiagnosticProxyConfiguration(key?.GetValue("ProxyEnable") is int enabled && enabled != 0,
            key?.GetValue("ProxyServer") as string, key?.GetValue("AutoConfigURL") as string, new Dictionary<string, string?>()));
    }

    public Task<IReadOnlyDictionary<string, string?>> GetEnvironmentProxyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var variables = new Dictionary<string, string?>();
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
            variables[name] = Environment.GetEnvironmentVariable(name);
        return Task.FromResult<IReadOnlyDictionary<string, string?>>(variables);
    }

    public Task<IReadOnlyList<string>> GetProcessNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<string>();
        foreach (var item in Process.GetProcesses())
            using (item)
            {
                try { result.Add(item.ProcessName); }
                catch (InvalidOperationException) { } // A process may exit while enumerating.
            }
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public async Task<string?> GetCommandLineAsync(int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        var script = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding; " +
            $"Get-CimInstance -ClassName Win32_Process -Filter \"ProcessId = {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)} AND Name = 'winws.exe'\" -ErrorAction Stop | Select-Object -ExpandProperty CommandLine";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            info.ArgumentList.Add(arg);
        using var helper = Process.Start(info) ?? throw new IOException("Не удалось прочитать командную строку процесса.");
        try
        {
            var output = helper.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = helper.StandardError.ReadToEndAsync(cancellationToken);
            await helper.WaitForExitAsync(cancellationToken);
            if (helper.ExitCode != 0) throw new IOException("CIM не смог прочитать параметры процесса: " + await error);
            return (await output).Trim();
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
                await helper.WaitForExitAsync();
            }
        }
    }

    public Task<string?> GetServiceCommandLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
        return Task.FromResult(key?.GetValue("ImagePath") as string);
    }
}
