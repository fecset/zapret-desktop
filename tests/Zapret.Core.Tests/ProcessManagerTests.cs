using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class ProcessManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTerminationPreservesOwnershipForTheNextStop(bool cancelled)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        foreach (var arg in new[] { "-NoProfile", "-Command", "[void][Console]::ReadLine()" }) info.ArgumentList.Add(arg);
        using var child = Process.Start(info)!;
        using var monitor = Process.GetProcessById(child.Id);
        var attempts = 0;
        var manager = new ZapretProcessManager(new ZapretDistribution(Path.GetTempPath()), null!, async (live, token) =>
        {
            if (attempts++ == 0)
            {
                if (cancelled) throw new OperationCanceledException();
                throw new Win32Exception(5);
            }
            live.Kill();
            await live.WaitForExitAsync(token);
        });
        var owned = typeof(ZapretProcessManager).GetField("owned", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var strategy = typeof(ZapretProcessManager).GetField("strategyId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        owned.SetValue(manager, child);
        strategy.SetValue(manager, "general");
        try
        {
            if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => manager.StopOwnedAsync());
            else await Assert.ThrowsAsync<Win32Exception>(() => manager.StopOwnedAsync());
            Assert.Same(child, owned.GetValue(manager));
            Assert.Equal("general", strategy.GetValue(manager));
            Assert.False(monitor.HasExited);
            Assert.True(await manager.StopOwnedAsync());
            await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(owned.GetValue(manager));
            Assert.Null(strategy.GetValue(manager));
        }
        finally
        {
            if (!monitor.HasExited) monitor.Kill();
            await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
