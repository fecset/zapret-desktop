using System.Diagnostics;
using System.Text.RegularExpressions;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class PowerShellStrategyTester(IZapretDistribution distribution, IServiceManager service,
    IZapretProcessManager process, IPrivilegeService privilege, IStrategyProvider strategies) : IStrategyTester
{
    public async Task<StrategyTestResult> RunAsync(IProgress<string> progress,
        IProgress<StrategyCheckProgress> strategyProgress, CancellationToken cancellationToken = default)
    {
        if (!privilege.IsAdministrator) throw new UnauthorizedAccessException("Для автоподбора нужны права администратора.");
        if (await service.GetStatusAsync(cancellationToken) != "NotInstalled")
            throw new InvalidOperationException("Перед автоподбором удалите службу zapret на странице настроек.");
        if ((await process.GetStatusAsync(cancellationToken)).State != ZapretRunState.Stopped)
            throw new InvalidOperationException("Перед автоподбором остановите Zapret.");
        var available = (await strategies.GetStrategiesAsync(cancellationToken)).ToDictionary(
            item => Path.GetFileName(item.SourceFile), StringComparer.OrdinalIgnoreCase);
        var script = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.Root, "utils", "test zapret.ps1"));
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = distribution.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-DesktopMode" })
            info.ArgumentList.Add(arg);
        using var runner = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить PowerShell для теста стратегий.");
        async Task ReplyAsync(string value)
        {
            await runner.StandardInput.WriteLineAsync(value);
            await runner.StandardInput.FlushAsync();
        }
        string? best = null;
        string? results = null;
        string? currentFile = null;
        var completed = new CompletedStrategyTracker();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!runner.HasExited) runner.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        try
        {
            while (await runner.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line == "__ZD_STOP__")
                {
                    try { await process.StopOwnedAsync(); }
                    catch (Exception ex) { progress.Report("[ERROR] Не удалось остановить winws: " + ex.Message); }
                    await ReplyAsync("OK");
                    continue;
                }
                if (line.StartsWith("__ZD_START__|", StringComparison.Ordinal))
                {
                    var fileName = line["__ZD_START__|".Length..];
                    currentFile = fileName;
                    strategyProgress.Report(new(fileName, StrategyCheckState.Running));
                    try
                    {
                        if (!available.TryGetValue(fileName, out var strategy))
                            throw new InvalidOperationException($"Стратегия {fileName} не найдена или не поддерживается.");
                        await process.StartAsync(strategy, cancellationToken);
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
                    if (fields.Length == 7 && int.TryParse(fields[2], out var httpOk) &&
                        int.TryParse(fields[3], out var httpFailed) && int.TryParse(fields[4], out var unsupported) &&
                        int.TryParse(fields[5], out var pingOk) && int.TryParse(fields[6], out var pingFailed))
                        strategyProgress.Report(new(fields[1], StrategyCheckState.Completed,
                            httpOk, httpFailed, unsupported, pingOk, pingFailed));
                    continue;
                }
                if (line.Contains("Strategy failed to start", StringComparison.OrdinalIgnoreCase) && currentFile is not null)
                    strategyProgress.Report(new(currentFile, StrategyCheckState.Failed));
                progress.Report(line);
                completed.Observe(line);
                if (line.StartsWith("Best config: ", StringComparison.OrdinalIgnoreCase))
                    best = string.IsNullOrWhiteSpace(line[13..]) ? null : line[13..].Trim();
                if (line.StartsWith("Results saved to ", StringComparison.OrdinalIgnoreCase)) results = line[17..].Trim();
            }
            var error = await runner.StandardError.ReadToEndAsync(cancellationToken);
            if (error.Length > 0) progress.Report(error);
            await runner.WaitForExitAsync(cancellationToken);
            if (runner.ExitCode != 0) throw new InvalidOperationException($"Тестовый скрипт завершился с кодом {runner.ExitCode}: {error}");
            return new(best ?? completed.BestStrategy, results ?? "");
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            return new(completed.BestStrategy, results ?? "", Cancelled: true);
        }
        finally
        {
            try { await process.StopOwnedAsync(); }
            finally { await RestoreIpSetIfInterruptedAsync(); }
        }
    }
    private async Task RestoreIpSetIfInterruptedAsync()
    {
        var flag = Path.Combine(distribution.Root, "ipset_switched.flag");
        if (!File.Exists(flag)) return;
        var backup = Path.Combine(distribution.ListsDirectory, "ipset-all.test-backup.txt");
        var target = Path.Combine(distribution.ListsDirectory, "ipset-all.txt");
        if (File.Exists(backup))
            await AtomicFiles.WriteTextAsync(target, await File.ReadAllTextAsync(backup));
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
