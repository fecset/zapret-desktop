using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class LegacyScriptSafetyTests
{
    [Theory]
    [InlineData(false, "backup-failure")]
    [InlineData(true, "backup-failure")]
    [InlineData(false, "marker-order")]
    [InlineData(true, "marker-order")]
    [InlineData(false, "restore-marker-failure")]
    [InlineData(true, "restore-marker-failure")]
    public async Task IpSetHelpersKeepRecoveryDataWhenAMutationFails(bool standalone, string scenario)
    {
        using var fixture = new Fixture();
        var source = await ReadStrategyScriptAsync(standalone);
        var helpers = Between(source, "function Get-IpsetStatus", "# Pause that tolerates");
        var probe = FixtureVariables + helpers + "\n" + """
            if ($env:LEGACY_TEST_SCENARIO -eq 'backup-failure') {
                function Copy-Item {
                    [CmdletBinding()]
                    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
                    throw 'Fixture backup failure'
                }
                try { Set-IpsetMode 'any'; throw 'Expected the backup to fail' }
                catch { if ($_.Exception.Message -ne 'Fixture backup failure') { throw } }
            } elseif ($env:LEGACY_TEST_SCENARIO -eq 'marker-order') {
                function Out-File {
                    [CmdletBinding()]
                    param([string]$LiteralPath, [string]$Encoding,
                        [Parameter(ValueFromPipeline=$true)]$InputObject)
                    if (-not (Test-Path -LiteralPath $flag) -or -not (Test-Path -LiteralPath $backup)) {
                        throw 'IPSet was about to change without recovery data'
                    }
                    throw 'Fixture interruption before clearing'
                }
                try { Set-IpsetMode 'any'; throw 'Expected interruption' }
                catch { if ($_.Exception.Message -ne 'Fixture interruption before clearing') { throw } }
            } else {
                Set-IpsetMode 'any'
                function Remove-Item {
                    [CmdletBinding()]
                    param([string]$LiteralPath, [switch]$Force)
                    throw 'Fixture marker removal failure'
                }
                try { Set-IpsetMode 'restore'; throw 'Expected marker failure' }
                catch { if ($_.Exception.Message -ne 'Fixture marker removal failure') { throw } }
            }
            Write-Host 'EXPECTED_FAILURE_HANDLED'
            """;

        var result = await fixture.RunAsync(probe, scenario);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("EXPECTED_FAILURE_HANDLED", result.Output);
        Assert.Equal(fixture.OriginalIpSet, await File.ReadAllBytesAsync(fixture.IpSet));
        Assert.Equal(scenario != "backup-failure", File.Exists(fixture.Flag));
        Assert.Equal(scenario != "backup-failure", File.Exists(fixture.Backup));
        if (File.Exists(fixture.Backup)) Assert.Equal(fixture.OriginalIpSet, await File.ReadAllBytesAsync(fixture.Backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupRecoveryFailsWithoutDeletingTheMarkerWhenBackupIsMissing(bool standalone)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.IpSet, "");
        await File.WriteAllTextAsync(fixture.Flag, "");
        var source = await ReadStrategyScriptAsync(standalone);
        var helpers = Between(source, "function Get-IpsetStatus", "function New-OrderedDict");
        var startup = Between(source, "# Check for leftover ipset flag", "# Get original ipset status early");
        var result = await fixture.RunAsync(FixtureVariables + helpers + "\nfunction Wait-AnyKey {}\n" + startup);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("backup is missing", result.Output);
        Assert.True(File.Exists(fixture.Flag));
        Assert.Empty(await File.ReadAllBytesAsync(fixture.IpSet));
    }

    [Theory]
    [InlineData(0, "203.0.113.113/32\n1.2.3.0/24\n", "loaded")]
    [InlineData(1, "203.0.113.113/32\n1.2.3.0/24\n", "loaded")]
    [InlineData(2, "203.0.113.113/32\n1.2.3.0/24\n", "loaded")]
    [InlineData(0, "# 203.0.113.113/32\n", "any")]
    [InlineData(1, "# 203.0.113.113/32\n", "any")]
    [InlineData(2, "# 203.0.113.113/32\n", "any")]
    [InlineData(0, "203.0.113.113/32 # placeholder\n", "none")]
    [InlineData(1, "203.0.113.113/32 # placeholder\n", "none")]
    [InlineData(2, "203.0.113.113/32 # placeholder\n", "none")]
    public async Task AllScriptEntryPointsInferIpSetModeFromEffectiveEntries(int entryPoint, string content, string expected)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.IpSet, content);
        string probe;
        if (entryPoint == 2)
        {
            probe = (await ReadFileHelperAsync()).Split("# Execute the selected file operation.", StringSplitOptions.None)[0] +
                "\nWrite-Host ('MODE=' + (Get-IpSetMode $env:ZAPRET_LEGACY_TARGET))";
        }
        else
        {
            var source = await ReadStrategyScriptAsync(entryPoint == 1);
            probe = FixtureVariables + Between(source, "function Get-IpsetStatus", "function Wait-WinwsReady") +
                "\nWrite-Host ('MODE=' + (Get-IpsetStatus))";
        }

        var result = await fixture.RunAsync(probe);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("MODE=" + expected, result.Output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TerminalFailureRestoresFilesBeforeRestartAndNeverRestartsAfterFailedCleanup(bool standalone, bool stopFails)
    {
        using var fixture = new Fixture();
        var source = await ReadStrategyScriptAsync(standalone);
        var reporting = Between(source, "function Report-TestFailure", "trap {");
        var tail = source[source.LastIndexOf("} catch {\n    Report-TestFailure $_", StringComparison.Ordinal)..];
        var probe = FixtureVariables + reporting + "\n" + """
            $script:TestExitCode = 0
            $originalWinws = @()
            $ipsetFlagFile = $flag
            [IO.File]::WriteAllText($flag, '')
            function Stop-Zapret {
                Write-Host 'STOP_ATTEMPTED'
                if ($env:LEGACY_TEST_SCENARIO -eq 'stop-fails') { throw 'Fixture stop failure' }
            }
            function Restore-WinwsSnapshot { param($snapshot) Write-Host 'SNAPSHOT_ATTEMPTED' }
            function Set-IpsetMode { param($mode) Write-Host 'IPSET_ATTEMPTED' }
            function Wait-AnyKey {}
            while ($true) {
                try { throw 'Fixture terminal failure'
            """ + "\n" + tail;

        var result = await fixture.RunAsync(probe, stopFails ? "stop-fails" : "");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Fixture terminal failure", result.Error);
        Assert.Contains("STOP_ATTEMPTED", result.Output);
        Assert.Contains("IPSET_ATTEMPTED", result.Output);
        if (stopFails)
        {
            Assert.Contains("Fixture stop failure", result.Error);
            Assert.DoesNotContain("SNAPSHOT_ATTEMPTED", result.Output);
        }
        else
        {
            Assert.Contains("SNAPSHOT_ATTEMPTED", result.Output);
            Assert.True(result.Output.IndexOf("IPSET_ATTEMPTED", StringComparison.Ordinal) <
                result.Output.IndexOf("SNAPSHOT_ATTEMPTED", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false, "0")]
    [InlineData(true, "0")]
    [InlineData(false, "not-a-number")]
    [InlineData(true, "65")]
    public async Task InvalidParallelismIsRejectedBeforeAnyNetworkOrProcessWork(bool standalone, string value)
    {
        using var fixture = new Fixture();
        var source = await ReadStrategyScriptAsync(standalone);
        var configuration = Between(source, "# DPI checker defaults", "function Get-DpiSuite");
        var result = await fixture.RunAsync(configuration, parallelism: value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("TEST_MAX_PARALLEL must be an integer", result.Error);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("truncated")]
    [InlineData("locked-target")]
    [InlineData("success")]
    public async Task LegacyIpSetUpdateCommitsOnlyACompleteValidatedDownload(string scenario)
    {
        using var fixture = new Fixture();
        var probe = InjectOverrides(await ReadFileHelperAsync(), """
            function Receive-IpSetDownload {
                param([string]$Url, [string]$Destination)
                $body = if ($env:LEGACY_TEST_SCENARIO -eq 'invalid') { '<html>not an IPSet</html>' } else { "5.6.7.0/24`n" }
                [IO.File]::WriteAllText($Destination, $body)
                if ($env:LEGACY_TEST_SCENARIO -eq 'truncated') { throw 'Fixture interrupted download' }
            }
            """);
        using var locked = scenario == "locked-target" ? new FileStream(fixture.IpSet, FileMode.Open, FileAccess.Read, FileShare.Read) : null;

        var result = await fixture.RunAsync(probe, scenario, operation: "update-ipset");

        Assert.True(result.ExitCode == (scenario == "success" ? 0 : 1),
            $"Exit code: {result.ExitCode}\n{result.Output}\n{result.Error}");
        if (scenario == "success") Assert.Equal("5.6.7.0/24\n", await File.ReadAllTextAsync(fixture.IpSet));
        else Assert.Equal(fixture.OriginalIpSet, await File.ReadAllBytesAsync(fixture.IpSet));
        var previous = Path.Combine(fixture.Root, "lists", "ipset-all.update.backup");
        Assert.Equal(scenario is "success" or "locked-target", File.Exists(previous));
        if (File.Exists(previous)) Assert.Equal(fixture.OriginalIpSet, await File.ReadAllBytesAsync(previous));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPayloadReplacementKeepsTheActiveFileAndFirstOriginalOnFailure(bool failCommit)
    {
        using var fixture = new Fixture();
        var active = Path.Combine(fixture.Root, "bin", "ACTIVE_GAME_UDP.bin");
        var source = Path.Combine(fixture.Root, "bin", "stun.bin");
        byte[] original = [1, 2, 3, 4];
        byte[] replacement = [9, 8, 7];
        await File.WriteAllBytesAsync(active, original);
        await File.WriteAllBytesAsync(source, replacement);
        var probe = InjectOverrides(await ReadFileHelperAsync(), """
            function Get-ZapretDataRoot { return $env:LEGACY_TEST_DATA_ROOT }
            """);
        FileStream? locked = failCommit ? new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        ProcessResult result;
        try { result = await fixture.RunAsync(probe, operation: "replace-payload", target: active, source: source); }
        finally { locked?.Dispose(); }

        Assert.True(result.ExitCode == (failCommit ? 1 : 0),
            $"Exit code: {result.ExitCode}\n{result.Output}\n{result.Error}");
        Assert.Equal(failCommit ? original : replacement, await File.ReadAllBytesAsync(active));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Root.ToUpperInvariant())));
        var savedOriginal = Path.Combine(fixture.Data, "payload-originals", identity, "ACTIVE_GAME_UDP.bin");
        Assert.Equal(original, await File.ReadAllBytesAsync(savedOriginal));

        var retry = await fixture.RunAsync(probe, operation: "replace-payload", target: active, source: source);
        Assert.True(retry.ExitCode == 0, $"Exit code: {retry.ExitCode}\n{retry.Output}\n{retry.Error}");
        Assert.Equal(replacement, await File.ReadAllBytesAsync(active));
        Assert.Equal(original, await File.ReadAllBytesAsync(savedOriginal));
        Assert.Empty(Directory.EnumerateFiles(fixture.Base, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LegacyDiagnosticsContainsNoServiceStopOrDeleteCommands()
    {
        var source = await File.ReadAllTextAsync(FindRepositoryFile("zapret", "service.bat"));
        var diagnostics = Between(Normalize(source), ":service_diagnostics\n", ":: GAME SWITCH");
        Assert.DoesNotContain("sc delete", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("net stop", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    private const string FixtureVariables = """
        $ErrorActionPreference = 'Stop'
        $rootDir = $env:LEGACY_TEST_ROOT
        $listsDir = Join-Path $rootDir 'lists'
        $flag = Join-Path $rootDir 'ipset_switched.flag'
        $backup = Join-Path $listsDir 'ipset-all.test-backup.txt'

        """;

    private static async Task<string> ReadStrategyScriptAsync(bool standalone) => Normalize(standalone
        ? await File.ReadAllTextAsync(FindRepositoryFile("zapret", "utils", "test zapret.ps1"))
        : await DesktopStrategyScript.ReadSourceAsync(CancellationToken.None));

    private static async Task<string> ReadFileHelperAsync() => Between(Normalize(
        await File.ReadAllTextAsync(FindRepositoryFile("zapret", "service.bat"))),
        "# BEGIN ZAPRET SAFE FILE OPERATIONS\n", "# END ZAPRET SAFE FILE OPERATIONS");

    private static string InjectOverrides(string helper, string overrides) => helper.Replace(
        "# Execute the selected file operation.", overrides + "\n# Execute the selected file operation.", StringComparison.Ordinal);

    private static string Between(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Missing source section: " + start);
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last >= 0, "Missing source section: " + end);
        return source[first..last];
    }

    private static string Normalize(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("Cannot find repository file: " + Path.Combine(segments));
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class Fixture : IDisposable
    {
        public string Base { get; } = Path.Combine(Path.GetTempPath(), "Zapret-legacy-script-tests-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Base, "distribution");
        public string Data => Path.Combine(Base, "data");
        public string IpSet => Path.Combine(Root, "lists", "ipset-all.txt");
        public string Flag => Path.Combine(Root, "ipset_switched.flag");
        public string Backup => Path.Combine(Root, "lists", "ipset-all.test-backup.txt");
        public byte[] OriginalIpSet { get; } = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("1.2.3.0/24\r\n")];

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "lists"));
            Directory.CreateDirectory(Path.Combine(Root, "bin"));
            File.WriteAllBytes(IpSet, OriginalIpSet);
        }

        public async Task<ProcessResult> RunAsync(string probe, string scenario = "", string? operation = null,
            string? target = null, string? source = null, string? parallelism = null)
        {
            var script = Path.Combine(Base, "probe.ps1");
            await File.WriteAllTextAsync(script, probe, new UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                WorkingDirectory = Base, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "MONITOR_TIMEOUT", "MONITOR_RANGE", "MONITOR_MAX_PARALLEL", "TEST_CURL_TIMEOUT", "TEST_MAX_PARALLEL" })
                start.Environment.Remove(name);
            start.Environment["LEGACY_TEST_ROOT"] = Root;
            start.Environment["LEGACY_TEST_DATA_ROOT"] = Data;
            start.Environment["LEGACY_TEST_SCENARIO"] = scenario;
            start.Environment["ZAPRET_LEGACY_SCRIPT"] = Path.Combine(Root, "service.bat");
            start.Environment["ZAPRET_LEGACY_OPERATION"] = operation ?? "";
            start.Environment["ZAPRET_LEGACY_TARGET"] = target ?? IpSet;
            start.Environment["ZAPRET_LEGACY_SOURCE"] = source ?? "https://fixture.invalid/ipset";
            if (parallelism is not null) start.Environment["TEST_MAX_PARALLEL"] = parallelism;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            return new(process.ExitCode, await output, await error);
        }

        public void Dispose() => Directory.Delete(Base, recursive: true);
    }
}
