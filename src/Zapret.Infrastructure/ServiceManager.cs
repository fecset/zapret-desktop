using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class ScServiceManager(IZapretDistribution distribution, IPrivilegeService privilege) : IServiceManager
{
    private const string Name = "zapret";
    public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) => QueryStatusAsync(Name, cancellationToken);
    public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
        return Task.FromResult(key?.GetValue("Start") is int start && start == 2);
    }

    public static Task<string> QueryStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть диспетчер служб Windows.");
        try
        {
            var handle = OpenService(manager, serviceName, 0x0004); // SERVICE_QUERY_STATUS
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060) return Task.FromResult("NotInstalled");
                if (error == 5) return Task.FromResult("AccessDenied");
                throw new Win32Exception(error, $"Не удалось получить состояние службы {serviceName}.");
            }
            try
            {
                if (!QueryServiceStatus(handle, out var status))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Не удалось получить состояние службы {serviceName}.");
                return Task.FromResult(status.CurrentState switch
                {
                    1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running",
                    _ => "Unknown"
                });
            }
            finally { CloseServiceHandle(handle); }
        }
        finally { CloseServiceHandle(manager); }
    }
    public async Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        if (await GetStatusAsync(cancellationToken) != "NotInstalled") throw new InvalidOperationException("zapret service is already installed.");
        var exe = SafePaths.RequireDirectFile(distribution.Root, Path.Combine(distribution.BinDirectory, "winws.exe"));
        var commandLine = Quote(exe) + " " + string.Join(" ", strategy.Arguments.Select(Quote));
        await EnsureSuccessAsync(["create", Name, "binPath=", commandLine, "DisplayName=", "zapret", "start=", "auto"], cancellationToken);
        await EnsureSuccessAsync(["description", Name, "Zapret DPI bypass software"], cancellationToken);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret", writable: true);
        key?.SetValue("zapret-discord-youtube", strategy.Name, RegistryValueKind.String);
        await StartAsync(cancellationToken);
    }
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        if (await GetStatusAsync(cancellationToken) == "NotInstalled") return;
        await StopAsync(cancellationToken);
        await EnsureSuccessAsync(["delete", Name], cancellationToken);
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        if (await GetStatusAsync(cancellationToken) == "Running") return;
        var (code, output) = await RunScAsync(["start", Name], cancellationToken);
        var running = code is 0 or 1056
            ? await WaitForStatusAsync("Running", cancellationToken)
            : await GetStatusAsync(cancellationToken) == "Running";
        if (running) return;
        throw new InvalidOperationException($"Не удалось запустить службу zapret (код {code}): {output}");
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        if (await GetStatusAsync(cancellationToken) is "Stopped" or "NotInstalled") return;
        var (code, output) = await RunScAsync(["stop", Name], cancellationToken);
        var stopped = code is 0 or 1062
            ? await WaitForStatusAsync("Stopped", cancellationToken)
            : await GetStatusAsync(cancellationToken) == "Stopped";
        if (stopped) return;
        throw new InvalidOperationException($"Не удалось остановить службу zapret (код {code}): {output}");
    }
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        for (var i = 0; i < 30 && await GetStatusAsync(cancellationToken) != "Stopped"; i++)
            await Task.Delay(200, cancellationToken);
        await StartAsync(cancellationToken);
    }
    public async Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        await EnsureSuccessAsync(["config", Name, "start=", enabled ? "auto" : "demand"], cancellationToken);
    }
    private void RequireAdmin()
    {
        if (!privilege.IsAdministrator) throw new UnauthorizedAccessException("Administrator rights are required for Windows services.");
    }
    private async Task<bool> WaitForStatusAsync(string expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await GetStatusAsync(cancellationToken) == expected) return true;
            await Task.Delay(200, cancellationToken);
        }
        return false;
    }
    private static string Quote(string value)
    {
        // Windows CreateProcess quoting rules for sc.exe's persisted ImagePath.
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }
    private static async Task EnsureSuccessAsync(string[] args, CancellationToken cancellationToken)
    {
        var (code, output) = await RunScAsync(args, cancellationToken);
        if (code != 0) throw new InvalidOperationException($"sc.exe failed ({code}): {output}");
    }
    private static async Task<(int, string)> RunScAsync(string[] args, CancellationToken cancellationToken)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to launch sc.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
