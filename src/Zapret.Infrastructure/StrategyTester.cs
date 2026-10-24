using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class PowerShellStrategyTester : IStrategyTester
{
    private readonly IZapretDistribution distribution;
    private readonly IServiceManager service;
    private readonly IZapretProcessManager process;
    private readonly IPrivilegeService privilege;
    private readonly IStrategyProvider strategies;
    private readonly Func<CancellationToken, Task<string>> scriptSource;
    private Process? activeRunner;

    public PowerShellStrategyTester(IZapretDistribution distribution, IServiceManager service,
        IZapretProcessManager process, IPrivilegeService privilege, IStrategyProvider strategies)
        : this(distribution, service, process, privilege, strategies, DesktopStrategyScript.ReadSourceAsync) { }

    internal PowerShellStrategyTester(IZapretDistribution distribution, IServiceManager service,
        IZapretProcessManager process, IPrivilegeService privilege, IStrategyProvider strategies,
        Func<CancellationToken, Task<string>> scriptSource)
    {
        this.distribution = distribution;
        this.service = service;
        this.process = process;
        this.privilege = privilege;
        this.strategies = strategies;
        this.scriptSource = scriptSource;
    }

    public async Task<StrategyTestResult> RunAsync(IProgress<string> progress,
        IProgress<StrategyCheckProgress> strategyProgress, CancellationToken cancellationToken = default)
    {
        if (activeRunner is not null)
            throw new InvalidOperationException("Предыдущий тест стратегий ещё не завершён. Дождитесь его остановки.");
        await RecoverInterruptedTestAsync(cancellationToken);
        if (!privilege.IsAdministrator) throw new UnauthorizedAccessException("Для автоподбора нужны права администратора.");
        if (await service.GetStatusAsync(cancellationToken) != "NotInstalled")
            throw new InvalidOperationException("Перед автоподбором удалите службу zapret на странице настроек.");
        if ((await process.GetStatusAsync(cancellationToken)).State != ZapretRunState.Stopped)
            throw new InvalidOperationException("Перед автоподбором остановите Zapret.");
        var available = (await strategies.GetStrategiesAsync(cancellationToken)).ToDictionary(
            item => Path.GetFileName(item.SourceFile), StringComparer.OrdinalIgnoreCase);
        using var script = await DesktopStrategyScript.CreateAsync(await scriptSource(cancellationToken), cancellationToken);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = distribution.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script.Path,
            "-DesktopMode", "-DistributionRoot", distribution.Root })
            info.ArgumentList.Add(arg);
        var runner = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить PowerShell для теста стратегий.");
        activeRunner = runner;
        async Task ReplyAsync(string value)
        {
            await runner.StandardInput.WriteLineAsync(value);
            await runner.StandardInput.FlushAsync();
        }
        string? results = null;
        string? currentFile = null;
        var expectingResult = false;
        var completed = new Dictionary<string, StrategyCheckProgress>(StringComparer.OrdinalIgnoreCase);
        string? BestCompleted() => completed.Values.OrderByDescending(check => check.HttpOk)
            .ThenByDescending(check => check.PingOk).FirstOrDefault()?.FileName;
        try
        {
            while (await runner.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line == "__ZD_STOP__")
                {
                    expectingResult = false;
                    try { await process.StopOwnedAsync(); }
                    catch (Exception ex) { progress.Report("[ERROR] Не удалось остановить winws: " + ex.Message); }
                    await ReplyAsync("OK");
                    continue;
                }
                if (line.StartsWith("__ZD_START__|", StringComparison.Ordinal))
                {
                    var fileName = line["__ZD_START__|".Length..];
                    currentFile = fileName;
                    expectingResult = false;
                    strategyProgress.Report(new(fileName, StrategyCheckState.Running));
                    try
                    {
                        if (!available.TryGetValue(fileName, out var strategy))
                            throw new InvalidOperationException($"Стратегия {fileName} не найдена или не поддерживается.");
                        await process.StartAsync(strategy, cancellationToken);
                        expectingResult = true;
                        await ReplyAsync("OK");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        strategyProgress.Report(new(fileName, StrategyCheckState.Failed));
                        progress.Report("[ERROR] " + ex.Message);
                        await ReplyAsync("FAIL");
                    }
                    continue;
                }
                if (line.StartsWith("__ZD_RESULT__|", StringComparison.Ordinal))
                {
                    var fields = line.Split('|');
                    if (fields.Length == 7 && expectingResult && string.Equals(currentFile, fields[1], StringComparison.OrdinalIgnoreCase) &&
                        available.ContainsKey(fields[1]) && int.TryParse(fields[2], out var httpOk) &&
                        int.TryParse(fields[3], out var httpFailed) && int.TryParse(fields[4], out var unsupported) &&
                        int.TryParse(fields[5], out var pingOk) && int.TryParse(fields[6], out var pingFailed) &&
                        httpOk >= 0 && httpFailed >= 0 && unsupported >= 0 && pingOk >= 0 && pingFailed >= 0)
                    {
                        var result = new StrategyCheckProgress(fields[1], StrategyCheckState.Completed,
                            httpOk, httpFailed, unsupported, pingOk, pingFailed);
                        expectingResult = false;
                        if (completed.TryAdd(fields[1], result)) strategyProgress.Report(result);
                    }
                    continue;
                }
                if (line.Contains("Strategy failed to start", StringComparison.OrdinalIgnoreCase) && currentFile is not null)
                {
                    expectingResult = false;
                    strategyProgress.Report(new(currentFile, StrategyCheckState.Failed));
                }
                progress.Report(line);
                if (line.StartsWith("Results saved to ", StringComparison.OrdinalIgnoreCase)) results = line[17..].Trim();
            }
            var error = await runner.StandardError.ReadToEndAsync(cancellationToken);
            if (error.Length > 0) progress.Report(error);
            await runner.WaitForExitAsync(cancellationToken);
            if (runner.ExitCode != 0) throw new InvalidOperationException($"Тестовый скрипт завершился с кодом {runner.ExitCode}: {error}");
            return new(BestCompleted(), results ?? "");
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            return new(BestCompleted(), results ?? "", Cancelled: true);
        }
        finally
        {
            await RecoverInterruptedTestAsync();
        }
    }
    public async Task RecoverInterruptedTestAsync(CancellationToken cancellationToken = default)
    {
        if (activeRunner is { } runner)
        {
            if (!runner.HasExited) runner.Kill(entireProcessTree: true);
            await runner.WaitForExitAsync(cancellationToken);
            await process.StopOwnedAsync(cancellationToken);
            runner.Dispose();
            activeRunner = null;
        }
        var flag = Path.Combine(distribution.Root, "ipset_switched.flag");
        if (!File.Exists(flag)) return;
        var backup = Path.Combine(distribution.ListsDirectory, "ipset-all.test-backup.txt");
        var target = Path.Combine(distribution.ListsDirectory, "ipset-all.txt");
        if (!File.Exists(backup))
            throw new InterruptedTestRecoveryException("Тестовая копия IPSet отсутствует. Откройте «Настройки → Резервные копии» и восстановите выбранную копию перед запуском Zapret.");
        SafePaths.RequireDirectFile(distribution.Root, backup);
        await AtomicFiles.WriteBytesAsync(target, await File.ReadAllBytesAsync(backup, cancellationToken), cancellationToken);
        File.Delete(flag);
        File.Delete(backup);
    }

    public async Task ConfirmRestoredIpSetAsync(CancellationToken cancellationToken = default)
    {
        if (activeRunner is not null) throw new InvalidOperationException("Сначала завершите тест стратегий.");
        var flag = Path.Combine(distribution.Root, "ipset_switched.flag");
        if (!File.Exists(flag)) return;
        SafePaths.RequireDirectFile(distribution.Root, flag);
        if (File.Exists(Path.Combine(distribution.ListsDirectory, "ipset-all.test-backup.txt")))
            throw new InvalidOperationException("Тестовая копия снова доступна. Повторите автоматическое восстановление.");
        var file = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.ListsDirectory, "ipset-all.txt"));
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Восстановленный IPSet слишком большой.");
        var text = await File.ReadAllTextAsync(file, cancellationToken);
        foreach (var line in text.Split('\n'))
        {
            var entry = line.Split('#', 2)[0].Trim().TrimStart('\uFEFF');
            if (entry.Length == 0) continue;
            var parts = entry.Split('/');
            if (parts.Length > 2 || parts[0].Any(c => !(char.IsAsciiHexDigit(c) || c is '.' or ':')) ||
                !IPAddress.TryParse(parts[0], out var address) ||
                parts.Length == 2 && (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix >
                    (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)))
                throw new InvalidDataException("В выбранной копии некорректный IPSet. Выберите другую резервную копию.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        SafePaths.RequireDirectFile(distribution.Root, flag);
        File.Delete(flag);
    }
}

public sealed class CompletedStrategyTracker
{
    private static readonly Regex Header = new(@"^\s*\[\d+/\d+\]\s+(?<name>.+\.bat)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Success = new(@"(?:HTTP|TLS1\.2|TLS1\.3):OK\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ping = new(@"Ping:\s*(?<result>\d[\d\s,.]*\s*ms)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private string? current;
    private bool sawResult;
    private int score;
    private int pingScore;
    private int bestScore = -1;
    private int bestPing = -1;
    public string? BestStrategy { get; private set; }
    public int CompletedCount { get; private set; }

    public void Observe(string line)
    {
        var header = Header.Match(line);
        if (header.Success)
        {
            Commit();
            current = header.Groups["name"].Value.Trim();
            sawResult = false;
            score = pingScore = 0;
            return;
        }
        if (line.Contains("All tests finished.", StringComparison.OrdinalIgnoreCase))
        {
            Commit();
            current = null;
            return;
        }
        if (current is null) return;
        if (line.Contains("Strategy failed to start", StringComparison.OrdinalIgnoreCase))
        {
            current = null;
            return;
        }
        if (!line.Contains("HTTP:", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("TLS1.2:", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("TLS1.3:", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("Ping:", StringComparison.OrdinalIgnoreCase)) return;
        sawResult = true;
        score += Success.Matches(line).Count;
        if (Ping.IsMatch(line)) pingScore++;
    }

    private void Commit()
    {
        if (current is null || !sawResult) return;
        CompletedCount++;
        if (score > bestScore || score == bestScore && pingScore > bestPing)
        {
            BestStrategy = current;
            bestScore = score;
            bestPing = pingScore;
        }
    }
}
