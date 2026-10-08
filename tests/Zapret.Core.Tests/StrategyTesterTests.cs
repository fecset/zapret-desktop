using System.Diagnostics;
using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class StrategyTesterTests
{
    [Fact]
    public async Task RecoveryRestoresTheExactBytesAndRemovesTheInterruptionMarker()
    {
        using var fixture = new Fixture();
        await fixture.InterruptAsync();
        await fixture.Tester.RecoverInterruptedTestAsync();
        Assert.Equal(fixture.Original, await File.ReadAllBytesAsync(fixture.IpSet));
        Assert.False(File.Exists(fixture.Flag));
        Assert.False(File.Exists(fixture.Backup));
        await fixture.Tester.RecoverInterruptedTestAsync();
        Assert.Equal(fixture.Original, await File.ReadAllBytesAsync(fixture.IpSet));
    }

    [Fact]
    public async Task MissingBackupPreservesTheMarkerAndReportsRecoveryFailure()
    {
        using var fixture = new Fixture();
        await fixture.InterruptAsync();
        File.Delete(fixture.Backup);
        await Assert.ThrowsAsync<IOException>(() => fixture.Tester.RecoverInterruptedTestAsync());
        Assert.True(File.Exists(fixture.Flag));
        Assert.Empty(await File.ReadAllBytesAsync(fixture.IpSet));
    }

    [Fact]
    public async Task CancellationStopsPowerShellBeforeReturningWithTheIpSetRestored()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "utils", "test zapret.ps1"), """
            param([switch]$DesktopMode)
            $testRoot = Split-Path $PSScriptRoot
            $testLists = Join-Path $testRoot 'lists'
            Copy-Item -LiteralPath (Join-Path $testLists 'ipset-all.txt') -Destination (Join-Path $testLists 'ipset-all.test-backup.txt')
            [IO.File]::WriteAllText((Join-Path $testLists 'ipset-all.txt'), '')
            [IO.File]::WriteAllText((Join-Path $testRoot 'ipset_switched.flag'), '')
            [IO.File]::WriteAllText((Join-Path $testRoot 'runner.pid'), [string]$PID)
            Write-Host 'WAITING'
            [void][Console]::ReadLine()
            """);
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = fixture.Tester.RunAsync(new InlineProgress<string>(line => { if (line == "WAITING") ready.TrySetResult(); }),
            new InlineProgress<StrategyCheckProgress>(_ => { }), cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { cancellation.Cancel(); }
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Cancelled);
        Assert.Equal(fixture.Original, await File.ReadAllBytesAsync(fixture.IpSet));
        Assert.False(File.Exists(fixture.Flag));
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "runner.pid")));
        try
        {
            using var runner = Process.GetProcessById(pid);
            Assert.True(runner.HasExited);
        }
        catch (ArgumentException) { } // Windows has already removed the terminated process.
    }

    [Theory]
    [InlineData("any", "MARKED_BEFORE_CLEAR=True")]
    [InlineData("restore", "BACKUP_BEFORE_CLEAR=True")]
    public async Task BundledScriptKeepsIpSetRecoverableAtEachMutation(string mode, string expected)
    {
        using var fixture = new Fixture();
        var source = await ReadBundledScriptAsync();
        // Load only the real file helpers; the network tests and process/service operations never run.
        var helpers = source[..source.IndexOf("# Pause that tolerates", StringComparison.Ordinal)];
        var probe = """
            $flag = Join-Path $rootDir 'ipset_switched.flag'
            $backup = Join-Path $listsDir 'ipset-all.test-backup.txt'
            if ('MODE' -eq 'any') {
                function Out-File {
                    [CmdletBinding()]
                    param([Parameter(Position=0)][string]$FilePath, [string]$LiteralPath, [string]$Encoding,
                        [Parameter(ValueFromPipeline=$true)]$InputObject)
                    Write-Host ('MARKED_BEFORE_CLEAR=' + [bool](Test-Path -LiteralPath $flag))
                    throw 'Simulated interruption before clearing IPSet'
                }
                try { Set-IpsetMode -mode 'any' } catch { }
            } else {
                Set-IpsetMode -mode 'any'
                [IO.File]::WriteAllText($flag, '')
                function Remove-Item {
                    [CmdletBinding()]
                    param([string]$Path, [string]$LiteralPath, [switch]$Force)
                    Write-Host ('BACKUP_BEFORE_CLEAR=' + [bool](Test-Path -LiteralPath $backup))
                    throw 'Simulated interruption before clearing marker'
                }
                try { Set-IpsetMode -mode 'restore' } catch { }
            }
            """.Replace("MODE", mode, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "utils", "test zapret.ps1"), helpers + probe);
        var output = new List<string>();
        await fixture.Tester.RunAsync(new InlineProgress<string>(output.Add), new InlineProgress<StrategyCheckProgress>(_ => { }));
        Assert.Contains(expected, output);
        Assert.Equal(fixture.Original, await File.ReadAllBytesAsync(fixture.IpSet));
        Assert.False(File.Exists(fixture.Flag));
    }

    [Fact]
    public async Task BundledScriptPreservesTheMarkerWhenStartupRecoveryHasNoBackup()
    {
        using var fixture = new Fixture();
        await fixture.InterruptAsync();
        File.Delete(fixture.Backup);
        var source = await ReadBundledScriptAsync();
        var helpers = source[..source.IndexOf("function New-OrderedDict", StringComparison.Ordinal)];
        var startupStart = source.IndexOf("# Check for leftover ipset flag", StringComparison.Ordinal);
        var startupEnd = source.IndexOf("# Get original ipset status early", StringComparison.Ordinal);
        var script = Path.Combine(fixture.Root, "utils", "test zapret.ps1");
        await File.WriteAllTextAsync(script, helpers + source[startupStart..startupEnd]);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-DesktopMode" }) info.ArgumentList.Add(arg);
        using var runner = Process.Start(info)!;
        var output = runner.StandardOutput.ReadToEndAsync();
        var error = runner.StandardError.ReadToEndAsync();
        try { await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!runner.HasExited) { runner.Kill(); await runner.WaitForExitAsync(); } }
        await Task.WhenAll(output, error);
        Assert.True(File.Exists(fixture.Flag));
        Assert.Empty(await File.ReadAllBytesAsync(fixture.IpSet));
        await Assert.ThrowsAsync<IOException>(() => fixture.Tester.RecoverInterruptedTestAsync());
    }

    [Fact]
    public async Task BundledScriptDoesNotApplyAnUnmarkedBackup()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Backup, "stale or incomplete backup");
        var source = await ReadBundledScriptAsync();
        var helpers = source[..source.IndexOf("# Pause that tolerates", StringComparison.Ordinal)];
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "utils", "test zapret.ps1"),
            helpers + "Set-IpsetMode -mode 'restore'");
        await fixture.Tester.RunAsync(new InlineProgress<string>(_ => { }), new InlineProgress<StrategyCheckProgress>(_ => { }));
        Assert.Equal(fixture.Original, await File.ReadAllBytesAsync(fixture.IpSet));
    }

    private static Task<string> ReadBundledScriptAsync() => File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../zapret/utils/test zapret.ps1")));

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ZapretTester-tests-" + Guid.NewGuid().ToString("N"));
        public string IpSet => Path.Combine(Root, "lists", "ipset-all.txt");
        public string Backup => Path.Combine(Root, "lists", "ipset-all.test-backup.txt");
        public string Flag => Path.Combine(Root, "ipset_switched.flag");
        public byte[] Original { get; } = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("1.2.3.0/24\r\n")];
        public PowerShellStrategyTester Tester { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "lists"));
            Directory.CreateDirectory(Path.Combine(Root, "utils"));
            File.WriteAllBytes(IpSet, Original);
            File.WriteAllText(Path.Combine(Root, "general.bat"), "start \"test\" /min \"%BIN%winws.exe\" --wf-tcp=80");
            var distribution = new ZapretDistribution(Root);
            Tester = new(distribution, new NoService(), new NoProcess(), new Privilege(), new BatStrategyProvider(distribution));
        }
        public async Task InterruptAsync()
        {
            await File.WriteAllBytesAsync(Backup, Original);
            await File.WriteAllTextAsync(IpSet, "");
            await File.WriteAllTextAsync(Flag, "");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private sealed class Privilege : IPrivilegeService
    {
        public bool IsAdministrator => true;
        public void RestartElevated() => throw new NotSupportedException();
    }
    private sealed class NoService : IServiceManager
    {
        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult("NotInstalled");
        public Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateStrategyAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class NoProcess : IZapretProcessManager
    {
        public event Action<string, string>? OutputReceived { add { } remove { } }
        public Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ZapretStatus(
            ZapretRunState.Stopped, null, null, null, "Stopped", "NotInstalled"));
        public Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
