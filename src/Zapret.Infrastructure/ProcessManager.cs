using System.Diagnostics;
using System.Security.Principal;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class WindowsPrivilegeService : IPrivilegeService
{
    public bool IsAdministrator => OperatingSystem.IsWindows() &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public void RestartElevated()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
    }
}

public sealed class ZapretProcessManager : IZapretProcessManager
{
    private readonly IZapretDistribution distribution;
    private readonly IServiceManager service;
    private readonly Func<Process, CancellationToken, Task> terminateProcess;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? owned;
    private string? strategyId;
    public event Action<string, string>? OutputReceived;

    public ZapretProcessManager(IZapretDistribution distribution, IServiceManager service)
        : this(distribution, service, TerminateProcessAsync) { }

    internal ZapretProcessManager(IZapretDistribution distribution, IServiceManager service,
        Func<Process, CancellationToken, Task> terminateProcess)
    {
        this.distribution = distribution;
        this.service = service;
        this.terminateProcess = terminateProcess;
    }

    private static async Task TerminateProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync(cancellationToken);
    }

    public async Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serviceStatus = await service.GetStatusAsync(cancellationToken);
        var winDivert = await ScServiceManager.QueryStatusAsync("WinDivert", cancellationToken);
        var winDivert14 = await ScServiceManager.QueryStatusAsync("WinDivert14", cancellationToken);
        var driverStatus = winDivert == "Running" || winDivert14 == "Running" ? "Running" :
            winDivert != "NotInstalled" ? winDivert : winDivert14;
        var processes = Process.GetProcessesByName("winws");
        try
        {
            var live = LiveOwnedProcess();
            var process = live ?? processes.FirstOrDefault();
            string? runningStrategy = live is not null ? strategyId : null;
            if (serviceStatus == "Running")
            {
                // Registry metadata describes the next service start, not the live process.
                // A failed restart can leave the old process running with different arguments.
                runningStrategy = null;
            }
            DateTimeOffset? started = null;
            try { if (process is not null) started = process.StartTime; }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            return new(serviceStatus == "Running" ? ZapretRunState.ServiceRunning :
                live is not null ? ZapretRunState.Running : process is not null ? ZapretRunState.External : ZapretRunState.Stopped,
                process?.Id, started, runningStrategy, driverStatus, serviceStatus);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public async Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await GetStatusAsync(cancellationToken);
            if (current.State != ZapretRunState.Stopped) throw new InvalidOperationException("winws.exe или служба zapret уже запущены.");
            var exe = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.BinDirectory, "winws.exe"));
            var info = new ProcessStartInfo(exe) { WorkingDirectory = distribution.BinDirectory, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in strategy.Arguments) info.ArgumentList.Add(arg);
            var candidate = new Process { StartInfo = info, EnableRaisingEvents = true };
            candidate.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke("INFO", e.Data); };
            candidate.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke("ERROR", e.Data); };
            try
            {
                if (!candidate.Start()) throw new InvalidOperationException("Не удалось запустить winws.exe.");
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
            owned?.Dispose();
            owned = candidate;
            strategyId = strategy.Id;
            candidate.BeginOutputReadLine();
            candidate.BeginErrorReadLine();
            await Task.Delay(400, cancellationToken);
            if (candidate.HasExited) throw new InvalidOperationException($"winws.exe завершился сразу после запуска (код {candidate.ExitCode}). Проверьте права администратора и журнал.");
            return await GetStatusAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }
    public async Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!await StopOwnedCoreAsync(cancellationToken))
            {
                var state = (await GetStatusAsync(cancellationToken)).State;
                if (state == ZapretRunState.ServiceRunning) await service.StopAsync(cancellationToken);
                else if (state == ZapretRunState.External)
                    throw new InvalidOperationException("winws.exe запущен другим приложением. Остановите его там, где он был запущен.");
            }
            return await GetStatusAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }
    public async Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await StopOwnedCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }
    private async Task<bool> StopOwnedCoreAsync(CancellationToken cancellationToken)
    {
        if (LiveOwnedProcess() is not { } live)
        {
            owned?.Dispose();
            owned = null;
            strategyId = null;
            return false;
        }
        try
        {
            await terminateProcess(live, cancellationToken);
        }
        catch (InvalidOperationException) when (live.HasExited) { }
        // Keep ownership after a failed Kill or cancelled wait so another stop can retry.
        if (!live.HasExited) throw new InvalidOperationException("Не удалось дождаться завершения winws.exe.");
        live.Dispose();
        owned = null;
        strategyId = null;
        return true;
    }
    public async Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        return await StartAsync(strategy, cancellationToken);
    }
    private Process? LiveOwnedProcess()
    {
        var current = owned;
        if (current is null) return null;
        try { return current.HasExited ? null : current; }
        catch (ObjectDisposedException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
