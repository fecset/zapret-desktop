using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Zapret.Core.Tests;

public sealed class PreviewRetentionTests
{
    [Fact]
    public async Task KeepsThreeNewestCompletedRunsAndNeverClaimsRunningLegacyOrUnknownOutputs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var completed = Enumerable.Range(1, 5).Select(ticks => fixture.CreateOutput(ticks)).ToArray();
        using var owner = Process.GetCurrentProcess();
        var running = fixture.CreateOutput(owner.StartTime.ToUniversalTime().Ticks);
        var legacy = fixture.CreateOutput(null);
        var malformed = fixture.CreateOutput(null);
        File.WriteAllText(Path.Combine(malformed, ".preview-owner.json"), "{broken");
        var unrecognized = fixture.CreateOutput(1, "manually-saved-preview");

        await fixture.PruneAsync(keepCompleted: 3);

        Assert.False(Directory.Exists(completed[0]));
        Assert.False(Directory.Exists(completed[1]));
        Assert.All(completed.Skip(2), path => Assert.True(Directory.Exists(path)));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(legacy));
        Assert.True(Directory.Exists(malformed));
        Assert.True(Directory.Exists(unrecognized));
    }

    [Fact]
    public async Task NestedJunctionKeepsTheEntireOutputAndItsExternalTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var linkedOutput = fixture.CreateOutput(1);
        var ordinaryOutput = fixture.CreateOutput(2);
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-preview")).FullName;
        var preservedFile = Path.Combine(external, "keep.txt");
        File.WriteAllText(preservedFile, "external data");

        await fixture.PruneAsync(keepCompleted: 0, junctionPath: Path.Combine(linkedOutput, "linked"), junctionTarget: external);

        Assert.True(Directory.Exists(linkedOutput));
        Assert.False(Directory.Exists(ordinaryOutput));
        Assert.Equal("external data", File.ReadAllText(preservedFile));
    }

    [Fact]
    public async Task JunctionAtPreviewRootCannotRedirectCleanupOutsideTheExpectedTree()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-preview")).FullName;
        var preservedOutput = fixture.CreateOutput(1, parent: external);
        Directory.Delete(fixture.PreviewRoot);

        await fixture.PruneAsync(keepCompleted: 0, junctionPath: fixture.PreviewRoot, junctionTarget: external);

        Assert.True(Directory.Exists(preservedOutput));
        Assert.Equal("preview fixture", File.ReadAllText(Path.Combine(preservedOutput, "keep.txt")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("Zapret preview retention ").FullName;
        public string PreviewRoot { get; }
        private string? junction;

        public Fixture() => PreviewRoot = Directory.CreateDirectory(Path.Combine(Root, "TestResults", "engineering-preview")).FullName;

        public string CreateOutput(long? started, string? name = null, string? parent = null)
        {
            var directory = Directory.CreateDirectory(Path.Combine(parent ?? PreviewRoot, name ?? Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "preview fixture");
            if (started is not null)
                File.WriteAllText(Path.Combine(directory, ".preview-owner.json"), JsonSerializer.Serialize(new
                {
                    Version = 1, ProcessId = Environment.ProcessId, ProcessStartTicks = started.Value.ToString()
                }));
            return directory;
        }

        public async Task PruneAsync(int keepCompleted, string? junctionPath = null, string? junctionTarget = null)
        {
            junction = junctionPath;
            const string command = """
                $ErrorActionPreference = 'Stop'
                function dotnet { throw 'Retention tests must not build the application.' }
                function Start-Process { throw 'Retention tests must not launch a preview.' }
                if ($env:PREVIEW_TEST_JUNCTION_PATH) {
                    New-Item -ItemType Junction -Path $env:PREVIEW_TEST_JUNCTION_PATH -Value $env:PREVIEW_TEST_JUNCTION_TARGET | Out-Null
                }
                . $env:PREVIEW_TEST_SCRIPT
                Remove-CompletedPreviewOutputs -ProjectRoot $env:PREVIEW_TEST_ROOT -KeepCompleted ([int]$env:PREVIEW_TEST_KEEP)
                """;
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
                start.ArgumentList.Add(argument);
            start.Environment["PREVIEW_TEST_SCRIPT"] = FindPreviewScript();
            start.Environment["PREVIEW_TEST_ROOT"] = Root;
            start.Environment["PREVIEW_TEST_KEEP"] = keepCompleted.ToString();
            start.Environment["PREVIEW_TEST_JUNCTION_PATH"] = junctionPath ?? "";
            start.Environment["PREVIEW_TEST_JUNCTION_TARGET"] = junctionTarget ?? "";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            Assert.True(process.ExitCode == 0, await output + await error);
        }

        private static string FindPreviewScript()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var script = Path.Combine(directory.FullName, "scripts", "Preview-EngineeringUi.ps1");
                if (File.Exists(script)) return script;
            }
            throw new FileNotFoundException("Cannot find the repository preview script.");
        }

        public void Dispose()
        {
            if (junction is not null && Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(Root, recursive: true);
        }
    }
}
