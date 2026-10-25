using System.Diagnostics;

namespace Zapret.Core.Tests;

public sealed class ReleasePackagingTests
{
    [Theory]
    [InlineData("zapret/lists/ipset-all.txt", false)]
    [InlineData("zapret/lists/ipset-all.txt", true)]
    [InlineData("zapret/lists/list-general.txt", false)]
    [InlineData("zapret/lists/list-general.txt", true)]
    [InlineData("zapret/bin/ACTIVE_GAME_UDP.bin", false)]
    [InlineData("zapret/bin/ACTIVE_GAME_UDP.bin", true)]
    public async Task ChangedTrackedDistributionIsRejectedBeforeBuildWithoutChangingInputs(string relativePath, bool staged)
    {
        var root = Directory.CreateTempSubdirectory("Zapret-release-test-").FullName;
        try
        {
            var script = Path.Combine(root, "scripts", "Build-Release.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.Copy(FindReleaseScript(), script);
            var project = Path.Combine(root, "src", "Zapret.Desktop", "Zapret.Desktop.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            await File.WriteAllTextAsync(project, "<Project><PropertyGroup><Version>0.2.2</Version></PropertyGroup></Project>");
            var input = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            await File.WriteAllBytesAsync(input, [1, 2, 3]);
            await GitAsync(root, "-c", "init.templateDir=", "init", "--quiet");
            await GitAsync(root, "-c", "core.autocrlf=false", "add", "--", ".");
            var hooks = Directory.CreateDirectory(Path.Combine(root, "empty-hooks")).FullName;
            await GitAsync(root, "-c", "user.name=Release test", "-c", "user.email=release-test@example.invalid",
                "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + hooks, "commit", "--quiet", "-m", "Fixture");
            byte[] changed = [4, 5, 6, 7];
            await File.WriteAllBytesAsync(input, changed);
            if (staged) await GitAsync(root, "-c", "core.autocrlf=false", "add", "--", relativePath);
            var before = await GitAsync(root, "status", "--porcelain", "--untracked-files=all");
            var outputDirectory = Path.Combine(root, "release-output");
            var temporaryDirectory = Directory.CreateDirectory(Path.Combine(root, "temporary")).FullName;
            var dotnetMarker = Path.Combine(root, "dotnet-invoked.txt");
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            // Keep the regression safe even before the guard exists: never run a real build.
            const string command = "function dotnet { [IO.File]::WriteAllText($env:RELEASE_TEST_DOTNET_MARKER, 'invoked'); throw 'Unexpected dotnet invocation.' }; " +
                "try { & $env:RELEASE_TEST_SCRIPT -OutputDirectory $env:RELEASE_TEST_OUTPUT -SkipTests; exit 0 } " +
                "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command })
                start.ArgumentList.Add(argument);
            start.Environment["RELEASE_TEST_SCRIPT"] = script;
            start.Environment["RELEASE_TEST_OUTPUT"] = outputDirectory;
            start.Environment["RELEASE_TEST_DOTNET_MARKER"] = dotnetMarker;
            start.Environment["TEMP"] = temporaryDirectory;
            start.Environment["TMP"] = temporaryDirectory;
            RemoveGitOverrides(start);

            var result = await RunAsync(start);

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(dotnetMarker), result.Output);
            Assert.Contains("Tracked zapret files differ from HEAD", result.Output);
            Assert.False(Directory.Exists(outputDirectory), result.Output);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
            Assert.Equal(changed, await File.ReadAllBytesAsync(input));
            Assert.Equal(before, await GitAsync(root, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindReleaseScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var script = Path.Combine(directory.FullName, "scripts", "Build-Release.ps1");
            if (File.Exists(script)) return script;
        }
        throw new FileNotFoundException("Cannot find the repository release script.");
    }

    private static async Task<string> GitAsync(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        RemoveGitOverrides(start);
        var result = await RunAsync(start);
        Assert.True(result.ExitCode == 0, result.Output);
        return result.Output;
    }

    private static void RemoveGitOverrides(ProcessStartInfo start)
    {
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) start.Environment.Remove(name);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        return (process.ExitCode, await output + await error);
    }
}
