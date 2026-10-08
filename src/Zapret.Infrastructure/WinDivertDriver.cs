using System.Diagnostics;

namespace Zapret.Infrastructure;

internal sealed class WinDivertDriver
{
    private static readonly string[] Names = ["WinDivert", "WinDivert14"];
    private readonly Func<string, CancellationToken, Task<string>> query;
    private readonly Func<string[], CancellationToken, Task<(int Code, string Output)>> run;
    private readonly Func<bool> hasWinws;

    public WinDivertDriver() : this(ScServiceManager.QueryStatusAsync, ScServiceManager.RunScAsync, HasWinws) { }

    internal WinDivertDriver(Func<string, CancellationToken, Task<string>> query,
        Func<string[], CancellationToken, Task<(int Code, string Output)>> run, Func<bool> hasWinws)
    {
        this.query = query;
        this.run = run;
        this.hasWinws = hasWinws;
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        var states = new List<string>();
        foreach (var name in Names) states.Add(await query(name, cancellationToken));
        return new[] { "Running", "StartPending", "StopPending", "AccessDenied", "Unknown", "Stopped", "NotInstalled" }
            .FirstOrDefault(states.Contains) ?? "Unknown";
    }

    // A separate service or winws instance must keep its driver when Desktop exits.
    public async Task<bool> UnloadAsync(CancellationToken cancellationToken)
    {
        foreach (var name in Names)
        {
            if (await IsInUseAsync(cancellationToken)) return false;
            var state = await query(name, cancellationToken);
            if (state == "NotInstalled") continue;
            if (state is "AccessDenied" or "Unknown")
                throw new InvalidOperationException($"Не удалось проверить состояние {name}. Перезапустите приложение с правами администратора.");
            if (state != "Stopped")
            {
                if (await IsInUseAsync(cancellationToken)) return false;
                var (code, output) = state == "StopPending" ? (0, "") : await run(["stop", name], cancellationToken);
                if (code is not (0 or 1060 or 1062) || !await WaitForAsync(name, removed: false, cancellationToken))
                    throw Failure(name, "выгрузить", code, output);
            }
            if (await IsInUseAsync(cancellationToken)) return false;
            // WinDivertOpen recreates the service on the next start with the current driver path.
            var (deleteCode, deleteOutput) = await run(["delete", name], cancellationToken);
            if (deleteCode is not (0 or 1060 or 1072) || !await WaitForAsync(name, removed: true, cancellationToken))
                throw Failure(name, "удалить службу драйвера", deleteCode, deleteOutput);
        }
        return true;
    }

    private async Task<bool> IsInUseAsync(CancellationToken cancellationToken) =>
        await query("zapret", cancellationToken) is not ("Stopped" or "NotInstalled") || hasWinws();

    private async Task<bool> WaitForAsync(string name, bool removed, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var state = await query(name, cancellationToken);
            if (state == "NotInstalled" || (!removed && state == "Stopped")) return true;
            await Task.Delay(200, cancellationToken);
        }
        return false;
    }

    private static InvalidOperationException Failure(string name, string action, int code, string output) =>
        new($"Zapret остановлен, но не удалось {action} {name} (код {code}). " +
            (code == 5 ? "Нужны права администратора. " : "Закройте другие приложения, использующие WinDivert, и повторите остановку. ") + output.Trim());

    private static bool HasWinws()
    {
        var processes = Process.GetProcessesByName("winws");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
