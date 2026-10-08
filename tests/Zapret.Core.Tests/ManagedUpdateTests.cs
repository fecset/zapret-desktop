using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class ManagedUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ZapretUpdateTests", Guid.NewGuid().ToString("N"));
    private string DistributionRoot => Path.Combine(root, "zapret");
    private string HostsPath => Path.Combine(root, "hosts");

    public ManagedUpdateTests()
    {
        Directory.CreateDirectory(DistributionRoot);
        Directory.CreateDirectory(Path.Combine(DistributionRoot, "lists"));
        File.WriteAllText(Path.Combine(DistributionRoot, "lists", "ipset-all.txt"), "8.8.8.8/32\n");
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\n192.0.2.1 my.local\n");
    }

    [Theory]
    [InlineData("list-general-user.txt")]
    [InlineData("list-general.txt")]
    public async Task DistributionPlanDoesNotApplyUntilRequestedAndRollbackPreservesUserFiles(string listName)
    {
        File.WriteAllText(Path.Combine(DistributionRoot, "lists", listName), "custom.example\n");
        var archive = Zip(DistributionFiles());
        using var client = Client(archive);
        var service = Service(client);
        var plan = await service.PrepareDistributionAsync();
        Assert.Equal("1.11.0", plan.Version);
        Assert.False(File.Exists(Path.Combine(DistributionRoot, "service.bat")));
        var result = await service.ApplyDistributionAsync(plan);
        Assert.True(File.Exists(Path.Combine(DistributionRoot, "bin", "winws.exe")));
        Assert.Equal("custom.example\n", File.ReadAllText(Path.Combine(DistributionRoot, "lists", listName)));
        File.WriteAllText(Path.Combine(DistributionRoot, "lists", listName), "changed.example\n");
        await service.RollbackDistributionAsync(result);
        Assert.False(File.Exists(Path.Combine(DistributionRoot, "service.bat")));
        Assert.Equal("changed.example\n", File.ReadAllText(Path.Combine(DistributionRoot, "lists", listName)));
        Assert.Equal("8.8.8.8/32\n", File.ReadAllText(Path.Combine(DistributionRoot, "lists", "ipset-all.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("bin/../escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("bin/file.txt:stream")]
    [InlineData("bin/CON.txt")]
    [InlineData("bin/file.txt.")]
    public async Task RejectsUnsafeArchivePaths(string name)
    {
        using var client = Client(Zip(DistributionFiles().Append((name, Encoding.UTF8.GetBytes("bad")))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).PrepareDistributionAsync());
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
    }

    [Fact]
    public async Task RejectsCaseInsensitiveDuplicatesAndArchiveSymlinks()
    {
        using var duplicates = Client(Zip(DistributionFiles().Append(("BIN/WINWS.EXE", Pe()))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(duplicates).PrepareDistributionAsync());
        using var linked = Client(Zip(DistributionFiles().Append(("bin/link", Encoding.UTF8.GetBytes("winws.exe"))), "bin/link"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(linked).PrepareDistributionAsync());
    }

    [Fact]
    public async Task RejectsTamperedDownloadAndPreparedFiles()
    {
        using var bad = Client(Zip(DistributionFiles()), digest: new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(bad).PrepareDistributionAsync());
        using var client = Client(Zip(DistributionFiles()));
        var service = Service(client);
        var plan = await service.PrepareDistributionAsync();
        File.AppendAllText(Path.Combine(plan.PreparedDirectory, "service.bat"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ApplyDistributionAsync(plan));
        Assert.False(File.Exists(Path.Combine(DistributionRoot, "service.bat")));
    }

    [Theory]
    [InlineData("http://github.com/Flowseal/zapret-discord-youtube/releases/download/1.11.0/zapret-discord-youtube-1.11.0.zip")]
    [InlineData("https://github.com/evil/repo/releases/download/1.11.0/zapret-discord-youtube-1.11.0.zip")]
    [InlineData("https://example.com/archive.zip")]
    public async Task RejectsUntrustedReleaseAssetUrls(string url)
    {
        using var client = Client(Zip(DistributionFiles()), assetUrl: url);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).PrepareDistributionAsync());
    }

    [Fact]
    public async Task FollowsTrustedAssetRedirectAndRejectsOffHostHopWithoutRequestingIt()
    {
        using var trusted = Client(Zip(DistributionFiles()), redirect: "https://release-assets.githubusercontent.com/example/archive.zip?signature=fake");
        Assert.Equal("1.11.0", (await Service(trusted).PrepareDistributionAsync()).Version);
        using var untrusted = Client(Zip(DistributionFiles()), redirect: "https://example.com/archive.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(untrusted).PrepareDistributionAsync());
        using var downgraded = Client(Zip(DistributionFiles()), redirect: "http://release-assets.githubusercontent.com/archive.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(downgraded).PrepareDistributionAsync());
    }

    [Fact]
    public async Task RejectsRedirectsForFixedRawSource()
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.com/ipset.txt");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).PrepareIpSetAsync());
    }

    [Fact]
    public async Task RejectsInvalidStrategyOrMissingBinary()
    {
        using var missing = Client(Zip(DistributionFiles().Where(x => x.Item1 != "bin/winws.exe")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(missing).PrepareDistributionAsync());
        using var strategy = Client(Zip(DistributionFiles().Where(x => x.Item1 != "general.bat")
            .Append(("general.bat", Encoding.UTF8.GetBytes("start \"zapret\" \"%BIN%winws.exe\" --bad=%EVIL%")))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(strategy).PrepareDistributionAsync());
    }

    [Fact]
    public async Task IpSetUsesFixedSourceValidatesEntriesAndPreservesDisabledMode()
    {
        File.WriteAllText(Path.Combine(DistributionRoot, "lists", "ipset-all.txt"), "203.0.113.113/32\n");
        using var client = TextClient("# source\n1.1.1.0/24\n2001:db8::/32\n");
        var service = Service(client);
        var plan = await service.PrepareIpSetAsync();
        Assert.Equal(2, plan.EntryCount);
        Assert.Equal("203.0.113.113/32\n", File.ReadAllText(Path.Combine(DistributionRoot, "lists", "ipset-all.txt")));
        await service.ApplyIpSetAsync(plan);
        Assert.Equal("203.0.113.113/32\n", File.ReadAllText(Path.Combine(DistributionRoot, "lists", "ipset-all.txt")));
        Assert.Contains("1.1.1.0/24", File.ReadAllText(Path.Combine(DistributionRoot, "lists", "ipset-all.txt.backup")));
    }

    [Theory]
    [InlineData("999.1.1.1/32")]
    [InlineData("1.1.1.1/33")]
    [InlineData("example.com")]
    [InlineData("")]
    public async Task RejectsInvalidIpSet(string text)
    {
        using var client = TextClient(text);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).PrepareIpSetAsync());
    }

    [Fact]
    public async Task HostsPreviewRejectsMarkerPrefixAndPreservesUnrelatedUserBlock()
    {
        var original = "# BEGIN Zapret Desktop managed hosts extra-comment\n127.0.0.1 important.local\n# END Zapret Desktop managed hosts\n";
        File.WriteAllText(HostsPath, original);
        using var client = TextClient("203.0.113.5 youtube.com\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).PreviewHostsAsync());
        Assert.Equal(original, File.ReadAllText(HostsPath));
    }

    [Fact]
    public void DesktopHelperPolicyExcludesUnelevatedUserAndProtectsOwner()
    {
        var policy = DesktopUpdateHelper.JobSecurity();
        Assert.True(policy.AreAccessRulesProtected);
        Assert.Equal("S-1-5-32-544", policy.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))?.Value);
        var rules = policy.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule => Assert.Contains(rule.IdentityReference.Value, new[] { "S-1-5-18", "S-1-5-32-544" }));
    }

    [Fact]
    public void InstalledDesktopCanBeReadBeforeElevationWithoutGrantingUserWriteAccess()
    {
        var policy = DesktopUpdateHelper.InstallationSecurity();
        var user = Assert.Single(policy.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>(), rule => rule.IdentityReference.Value == "S-1-5-32-545");
        Assert.Equal(System.Security.AccessControl.FileSystemRights.ReadAndExecute, user.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadAndExecute);
        Assert.Equal(0, (int)(user.FileSystemRights & (System.Security.AccessControl.FileSystemRights.Write |
            System.Security.AccessControl.FileSystemRights.Delete | System.Security.AccessControl.FileSystemRights.ChangePermissions |
            System.Security.AccessControl.FileSystemRights.TakeOwnership)));
    }

    [Fact]
    public async Task NativePowerShellHelperCreatesTheSameInstallationPermissionsAsDotNet()
    {
        var script = DesktopUpdateHelper.Script;
        var start = script.IndexOf("function Get-InstallSecurity", StringComparison.Ordinal);
        var end = script.IndexOf("function Copy-SafeTree", start, StringComparison.Ordinal);
        var probe = "$ErrorActionPreference='Stop'\n" + script[start..end] + "\n(Get-InstallSecurity).GetSecurityDescriptorSddlForm('All')";
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(probe)) })
            info.ArgumentList.Add(argument);
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(child.ExitCode == 0, await error);
        Assert.Equal(DesktopUpdateHelper.InstallationSecurity().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.All),
            (await output).Trim());
    }

    [Fact]
    public async Task HostsPreviewIsExplicitPreservesCustomEntriesAndRejectsStalePreview()
    {
        using var client = TextClient("# upstream\n203.0.113.5 youtube.com www.youtube.com\n");
        var service = Service(client);
        var original = File.ReadAllText(HostsPath);
        var preview = await service.PreviewHostsAsync();
        Assert.Equal(original, File.ReadAllText(HostsPath));
        Assert.Contains("my.local", preview.ProposedContent);
        var result = await service.ApplyHostsAsync(preview);
        Assert.Equal(original, File.ReadAllText(result.BackupPath!));
        Assert.Contains("youtube.com", File.ReadAllText(HostsPath));
        var second = await service.PreviewHostsAsync();
        File.AppendAllText(HostsPath, "127.0.0.1 added.local\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyHostsAsync(second));
    }

    [Fact]
    public async Task RejectsOversizedAndChangedIpSetPlans()
    {
        using var client = TextClient("1.1.1.1\n");
        var service = Service(client);
        var plan = await service.PrepareIpSetAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyIpSetAsync(plan with { Content = "9.9.9.9\n" }));
        File.WriteAllText(Path.Combine(DistributionRoot, "lists", "ipset-all.txt"), "8.8.4.4\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyIpSetAsync(plan));
        using var large = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1])
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(large).PrepareIpSetAsync());
    }

    [Fact]
    public async Task DesktopPlanValidatesPackageWithoutReplacingInstalledExecutable()
    {
        var app = Path.Combine(root, "application");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Zapret.Desktop.exe"), "installed");
        var files = DistributionFiles().Select(x => ("zapret/" + x.Item1, x.Item2))
            .Append(("Zapret.Desktop.exe", Pe()))
            .Concat(new[] { "LICENSE", "THIRD_PARTY_NOTICES.md", "licenses/Zapret-LICENSE.txt", "licenses/WinDivert-LICENSE.txt" }
                .Select(x => (x, Encoding.UTF8.GetBytes("license"))));
        using var client = Client(Zip(files), desktop: true);
        var service = Service(client);
        var plan = await service.PrepareDesktopAsync("0.1.3");
        Assert.Equal(ManagedUpdateProduct.Desktop, plan.Product);
        Assert.Equal("0.2.0", plan.Version);
        Assert.Equal("installed", File.ReadAllText(Path.Combine(app, "Zapret.Desktop.exe")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScheduleDesktopUpdateAsync(plan, int.MaxValue));
        Assert.Equal("installed", File.ReadAllText(Path.Combine(app, "Zapret.Desktop.exe")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopHelperWaitsForOwnerExitThenSwapsOnlyTemporaryFixtureAndPreservesUserData(bool tampered)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(root, "helper-source");
        var target = Path.Combine(root, "helper-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "Zapret.Desktop.exe"), "new-version");
        File.WriteAllText(Path.Combine(source, "settings.json"), "default-settings");
        File.WriteAllText(Path.Combine(target, "Zapret.Desktop.exe"), "old-version");
        File.WriteAllText(Path.Combine(target, "settings.json"), "user-settings");
        File.WriteAllText(Path.Combine(target, "custom.txt"), "user-data");
        Directory.CreateDirectory(Path.Combine(source, "zapret", "lists"));
        Directory.CreateDirectory(Path.Combine(target, "zapret", "lists"));
        File.WriteAllText(Path.Combine(source, "zapret", "lists", "list-general.txt"), "upstream.example\n");
        File.WriteAllText(Path.Combine(target, "zapret", "lists", "list-general.txt"), "custom.example\n");
        var helperType = typeof(ManagedUpdateService).Assembly.GetType("Zapret.Infrastructure.DesktopUpdateHelper")!;
        var script = (string)helperType.GetField("Script", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var helperPath = Path.Combine(root, "helper.ps1");
        File.WriteAllText(helperPath, script);
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var ownerStart = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) ownerStart.ArgumentList.Add(argument);
        using var owner = Process.Start(ownerStart)!;
        var jobPath = Path.Combine(root, "helper-job.json");
        var backup = target + ".backup";
        var ready = Path.Combine(root, "helper-ready");
        var log = Path.Combine(root, "helper.log");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(new
        {
            ProcessId = owner.Id, ProcessStartTicks = owner.StartTime.ToUniversalTime().Ticks.ToString(),
            Source = source, Target = target, Replacement = target + ".update", Backup = backup, Log = log, Ready = ready,
            Hashes = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(x => Path.GetRelativePath(source, x), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))).ToLowerInvariant())
        }));
        if (tampered) File.AppendAllText(Path.Combine(source, "Zapret.Desktop.exe"), "modified");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helperPath, "-ManifestPath", jobPath }) start.ArgumentList.Add(argument);
        using var updater = Process.Start(start)!;
        try
        {
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(ready) && !updater.HasExited && DateTime.UtcNow < timeout) await Task.Delay(50);
            if (tampered)
            {
                using var failureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await updater.WaitForExitAsync(failureTimeout.Token);
                Assert.Equal(1, updater.ExitCode);
                Assert.False(File.Exists(ready));
                Assert.False(Directory.Exists(backup));
                Assert.Equal("old-version", File.ReadAllText(Path.Combine(target, "Zapret.Desktop.exe")));
                Assert.Contains("hash mismatch", File.ReadAllText(log));
                return;
            }
            Assert.True(File.Exists(ready), File.Exists(log) ? File.ReadAllText(log) : "Helper never initialized.");
            Assert.False(owner.HasExited);
            Assert.Equal("old-version", File.ReadAllText(Path.Combine(target, "Zapret.Desktop.exe")));
            owner.Kill();
            await owner.WaitForExitAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await updater.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, updater.ExitCode);
            Assert.Equal("new-version", File.ReadAllText(Path.Combine(target, "Zapret.Desktop.exe")));
            Assert.Equal("old-version", File.ReadAllText(Path.Combine(backup, "Zapret.Desktop.exe")));
            Assert.Equal("user-settings", File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.Equal("user-data", File.ReadAllText(Path.Combine(target, "custom.txt")));
            Assert.Equal("custom.example\n", File.ReadAllText(Path.Combine(target, "zapret", "lists", "list-general.txt")));
            Assert.StartsWith("SUCCESS:", File.ReadAllText(log));
        }
        finally
        {
            if (!owner.HasExited) owner.Kill();
            if (!updater.HasExited) updater.Kill();
        }
    }

    private ManagedUpdateService Service(HttpClient client) => new(new ZapretDistribution(DistributionRoot), client,
        Path.Combine(root, "application"), Path.Combine(root, "stage"), HostsPath);

    private static IEnumerable<(string, byte[])> DistributionFiles()
    {
        yield return ("bin/winws.exe", Pe());
        yield return ("bin/WinDivert.dll", Pe());
        yield return ("bin/WinDivert64.sys", Pe());
        yield return ("service.bat", Encoding.UTF8.GetBytes("set LOCAL_VERSION=1.11.0\n"));
        yield return ("general.bat", Encoding.UTF8.GetBytes("start \"zapret\" \"%BIN%winws.exe\" --wf-tcp=443\n"));
        yield return ("lists/list-general.txt", Encoding.UTF8.GetBytes("example.com\n"));
        yield return ("lists/ipset-all.txt", Encoding.UTF8.GetBytes("1.1.1.1\n"));
    }

    private static byte[] Pe()
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[60] = 64;
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E'; bytes[68] = 0x64; bytes[69] = 0x86;
        return bytes;
    }

    private static byte[] Zip(IEnumerable<(string, byte[])> files, string? symlink = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                if (name == symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var output = entry.Open();
                output.Write(content);
            }
        return stream.ToArray();
    }

    private static HttpClient Client(byte[] archive, string? digest = null, string? assetUrl = null, bool desktop = false, string? redirect = null)
    {
        var repository = desktop ? "fecset/zapret-desktop" : "Flowseal/zapret-discord-youtube";
        var tag = desktop ? "v0.2.0" : "1.11.0";
        var name = desktop ? "ZapretDesktop-v0.2.0-win-x64.zip" : "zapret-discord-youtube-1.11.0.zip";
        var metadata = JsonSerializer.Serialize(new
        {
            tag_name = tag, html_url = $"https://github.com/{repository}/releases/tag/{tag}",
            assets = new[] { new { name, size = archive.Length, digest = "sha256:" + (digest ?? Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()),
                browser_download_url = assetUrl ?? $"https://github.com/{repository}/releases/download/{tag}/{name}" } }
        });
        return new(new Handler(request =>
        {
            if (redirect is not null && request.RequestUri!.Host == "github.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(redirect);
                return response;
            }
            Assert.DoesNotContain("example.com", request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(metadata) : new ByteArrayContent(archive)
            };
        }));
    }

    private static HttpClient TextClient(string content) => new(new Handler(request =>
    {
        Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
        Assert.Contains("/Flowseal/zapret-discord-youtube/refs/heads/main/.service/", request.RequestUri.AbsolutePath);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
    }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
