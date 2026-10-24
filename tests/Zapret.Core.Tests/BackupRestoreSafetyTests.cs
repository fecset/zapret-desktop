using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class BackupRestoreSafetyTests
{
    [Fact]
    public async Task RepeatedRestoreDoesNotCreateArchivesOrPruneExistingCopies()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("lists/list-general-user.txt", "first.example\n");
        var first = await fixture.Service.CreateAsync();
        await fixture.WriteAsync("lists/list-general-user.txt", "second.example\n");
        var second = await fixture.Service.CreateAsync();
        for (var index = 0; index < 4; index++)
        {
            await fixture.Service.RestoreAsync(first.Id);
            await fixture.Service.RestoreAsync(second.Id);
        }
        Assert.Equal(2, (await fixture.Service.GetBackupsAsync()).Count);
        Assert.False(File.Exists(Path.Combine(fixture.Data, "backups", "automatic-backups.json")));
        Assert.Equal("second.example\n", await File.ReadAllTextAsync(fixture.PathFor("lists/list-general-user.txt")));
    }

    [Fact]
    public async Task ImportChangedDataDoesNotCreateABackup()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("lists/list-general-user.txt", "saved.example\n");
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        await fixture.WriteAsync("lists/list-general-user.txt", "current.example\n");
        archive.Position = 0;
        await fixture.Service.ImportAsync(archive);
        Assert.Empty(await fixture.Service.GetBackupsAsync());
        Assert.Equal("saved.example\n", await File.ReadAllTextAsync(fixture.PathFor("lists/list-general-user.txt")));
    }

    [Fact]
    public async Task SettingsOnlyLegacyArchivePreservesMissingFilesAndUnspecifiedPreferences()
    {
        using var fixture = new Fixture();
        await fixture.Settings.SaveAsync(new DesktopSettings
        {
            Theme = "Dark", SelectedStrategyId = "my-strategy", MinimizeToTray = false,
            CheckUpdates = false, CheckConnectionAfterStart = false, LogLevel = "Warning"
        });
        await fixture.WriteAsync("lists/list-exclude-user.txt", "personal.example\r\n");
        await fixture.WriteAsync("utils/game_filter.enabled", "mode=all\ntcp=443\nudp=5000\n");
        await File.WriteAllBytesAsync(fixture.PathFor("bin/ACTIVE_GAME_UDP.bin"), [7, 8, 9]);
        var listTime = File.GetLastWriteTimeUtc(fixture.PathFor("lists/list-exclude-user.txt"));
        using var archive = Archive(new() { ["settings.json"] = Encoding.UTF8.GetBytes("{\"Theme\":\"Light\"}") });
        await fixture.Service.ImportAsync(archive);
        var settings = await fixture.Settings.LoadAsync();
        Assert.Equal("Light", settings.Theme);
        Assert.Equal("my-strategy", settings.SelectedStrategyId);
        Assert.False(settings.MinimizeToTray);
        Assert.False(settings.CheckUpdates);
        Assert.False(settings.CheckConnectionAfterStart);
        Assert.Equal("Warning", settings.LogLevel);
        Assert.Equal("personal.example\r\n", await File.ReadAllTextAsync(fixture.PathFor("lists/list-exclude-user.txt")));
        Assert.Equal(listTime, File.GetLastWriteTimeUtc(fixture.PathFor("lists/list-exclude-user.txt")));
        Assert.Equal("mode=all\ntcp=443\nudp=5000\n", await File.ReadAllTextAsync(fixture.PathFor("utils/game_filter.enabled")));
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(fixture.PathFor("bin/ACTIVE_GAME_UDP.bin")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Data, "payload-originals")));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Theory]
    [InlineData("lists/list-general-user.txt", "")]
    [InlineData("lists/list-exclude-user.txt", "")]
    [InlineData("lists/ipset-exclude-user.txt", "")]
    [InlineData("lists/list-custom.txt", "")]
    [InlineData("lists/ipset-custom.txt", "")]
    [InlineData("lists/list-exclude-user.txt", "\uFEFF# empty archived list\r\n \t\r\n")]
    public async Task EmptyPersonalListInAnArchiveDoesNotEraseCurrentEntries(string file, string saved)
    {
        using var fixture = new Fixture();
        const string current = "personal.example\r\n";
        await fixture.WriteAsync(file, current);
        var time = File.GetLastWriteTimeUtc(fixture.PathFor(file));
        using var archive = Archive(new()
        {
            ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(await fixture.Settings.LoadAsync()),
            [file] = Encoding.UTF8.GetBytes(saved)
        });
        await fixture.Service.ImportAsync(archive);
        Assert.Equal(current, await File.ReadAllTextAsync(fixture.PathFor(file)));
        Assert.Equal(time, File.GetLastWriteTimeUtc(fixture.PathFor(file)));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task NewlyExportedDisabledGameFilterCanBeRestored()
    {
        using var fixture = new Fixture();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        await fixture.WriteAsync("utils/game_filter.enabled", "mode=all\ntcp=443\nudp=5000\n");
        archive.Position = 0;
        await fixture.Service.ImportAsync(archive);
        Assert.Equal(GameFilterMode.Disabled, (await new FilterService(fixture.Distribution).GetGameFilterAsync()).Mode);
    }

    [Theory]
    [InlineData("utf-16", false)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", false)]
    [InlineData("utf-16BE", true)]
    [InlineData("utf-32", false)]
    [InlineData("utf-32", true)]
    [InlineData("utf-32BE", false)]
    [InlineData("utf-32BE", true)]
    public async Task BomEncodedEmptyListsDoNotEraseCurrentEntries(string encodingName, bool comments)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("lists/list-exclude-user.txt", "personal.example\n");
        var encoding = Encoding.GetEncoding(encodingName);
        using var archive = Archive(new()
        {
            ["settings.json"] = Encoding.UTF8.GetBytes("{}"),
            ["lists/list-exclude-user.txt"] = encoding.GetPreamble().Concat(encoding.GetBytes(comments ? "# no entries\r\n \t" : "")).ToArray()
        });
        await fixture.Service.ImportAsync(archive);
        Assert.Equal("personal.example\n", await File.ReadAllTextAsync(fixture.PathFor("lists/list-exclude-user.txt")));
    }

    [Fact]
    public async Task CurrentListsWithCarriageReturnLineEndingsArePreserved()
    {
        using var fixture = new Fixture();
        const string current = "# header\rpersonal.example\r";
        await fixture.WriteAsync("lists/list-exclude-user.txt", current);
        using var archive = Archive(new()
        {
            ["settings.json"] = Encoding.UTF8.GetBytes("{}"), ["lists/list-exclude-user.txt"] = []
        });
        await fixture.Service.ImportAsync(archive);
        Assert.Equal(current, await File.ReadAllTextAsync(fixture.PathFor("lists/list-exclude-user.txt")));
    }

    [Fact]
    public async Task FailedPayloadRollbackRetainsTheOriginalForRecovery()
    {
        using var fixture = new Fixture();
        var payloadPath = fixture.PathFor("bin/ACTIVE_GAME_UDP.bin");
        await File.WriteAllBytesAsync(payloadPath, [1, 2, 3]);
        await fixture.Settings.SaveAsync(new DesktopSettings { Theme = "Dark" });
        using var settings = new FailingSettingsStore(fixture.Settings, payloadPath);
        var service = new UserDataBackupService(fixture.Distribution, settings, fixture.Data);
        using var archive = Archive(new()
        {
            ["settings.json"] = Encoding.UTF8.GetBytes("{\"Theme\":\"Light\"}"),
            ["bin/ACTIVE_GAME_UDP.bin"] = [7, 8, 9]
        });
        await Assert.ThrowsAsync<AggregateException>(() => service.ImportAsync(archive));
        var original = Assert.Single(Directory.GetFiles(fixture.Data, "ACTIVE_GAME_UDP.bin", SearchOption.AllDirectories));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(original));
        Assert.Equal("Dark", (await fixture.Settings.LoadAsync()).Theme);
        Assert.Empty(await service.GetBackupsAsync());
    }

    private sealed class FailingSettingsStore(ISettingsStore inner, string payload) : ISettingsStore, IDisposable
    {
        private FileStream? held;
        private bool armed = true;
        public Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
        {
            if (!armed) return inner.SaveAsync(settings, cancellationToken);
            armed = false;
            held = new(payload, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromException(new IOException("Simulated settings write failure with a locked changed payload."));
        }
        public void Dispose() => held?.Dispose();
    }

    private static MemoryStream Archive(Dictionary<string, byte[]> files)
    {
        var result = new MemoryStream();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, CreatedAt = DateTimeOffset.UtcNow, Reason = "legacy partial archive",
            Files = files.ToDictionary(x => x.Key, x => new { Length = x.Value.Length, Sha256 = Convert.ToHexString(SHA256.HashData(x.Value)) })
        });
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
        foreach (var pair in files.Append(new("manifest.json", manifest)))
        {
            using var stream = zip.CreateEntry(pair.Key).Open();
            stream.Write(pair.Value);
        }
        result.Position = 0;
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "zapret-restore-safety-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(root, "data");
        public ZapretDistribution Distribution { get; }
        public JsonSettingsStore Settings { get; }
        public UserDataBackupService Service { get; }
        public Fixture()
        {
            Distribution = new(Path.Combine(root, "distribution"));
            foreach (var directory in new[] { "lists", "utils", "bin" }) Directory.CreateDirectory(Path.Combine(Distribution.Root, directory));
            Settings = new(Path.Combine(Data, "settings.json"));
            Service = new(Distribution, Settings, Data);
        }
        public string PathFor(string relative) => Path.Combine(Distribution.Root, relative);
        public Task WriteAsync(string relative, string value) => File.WriteAllTextAsync(PathFor(relative), value);
        public void Dispose() => Directory.Delete(root, true);
    }
}
