using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class DataProtectionTests
{
    [Fact]
    public async Task ExportAndImportRestoresSettingsListsFiltersAndActivePayloads()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        await fixture.Settings.SaveAsync(new DesktopSettings { Theme = "Light" });
        await File.WriteAllTextAsync(fixture.File("lists/list-general-user.txt"), "changed.example\n");
        File.Delete(fixture.File("utils/game_filter.enabled"));
        await File.WriteAllBytesAsync(fixture.File("bin/ACTIVE_GAME_UDP.bin"), [9]);
        await File.WriteAllTextAsync(fixture.File("lists/ipset-exclude-user.txt"), "8.8.8.8\n");
        archive.Position = 0;
        await fixture.Service.ImportAsync(archive);
        Assert.Equal("Dark", (await fixture.Settings.LoadAsync()).Theme);
        Assert.Equal("my.example\r\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
        Assert.Equal("mode=all\ntcp=443\nudp=5000\n", await File.ReadAllTextAsync(fixture.File("utils/game_filter.enabled")));
        Assert.Equal("1.2.3.0/24\n", await File.ReadAllTextAsync(fixture.File("lists/ipset-all.txt.backup")));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.File("bin/ACTIVE_GAME_UDP.bin")));
        Assert.False(File.Exists(fixture.File("lists/ipset-exclude-user.txt")));
        Assert.Single(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task RestoringSelectedBackupRetainsTheStateBeforeRestore()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        var first = await fixture.Service.CreateAsync("before edit");
        await File.WriteAllTextAsync(fixture.File("lists/list-general-user.txt"), "changed.example\n");
        await fixture.Service.RestoreAsync(first.Id);
        Assert.Equal("my.example\r\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
        var backups = await fixture.Service.GetBackupsAsync();
        Assert.Equal(2, backups.Count);
        Assert.Contains(backups, x => x.Id == first.Id && x.Reason == "before edit" && x.FileCount > 3 && x.SizeBytes > 0);
        var previous = Assert.Single(backups, x => x.Id != first.Id);
        await fixture.Service.RestoreAsync(previous.Id);
        Assert.Equal("changed.example\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
        Assert.False(Directory.Exists(fixture.File("backups")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("bin/winws.exe")]
    [InlineData("lists/../list-general-user.txt")]
    [InlineData("lists\\list-general-user.txt")]
    public async Task ImportRejectsUnmanagedAndTraversalEntriesBeforeChangingFiles(string entryName)
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write("untrusted");
        }
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));
        Assert.Equal("my.example\r\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task ImportRejectsCaseInsensitiveDuplicateEntries()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("LISTS/LIST-GENERAL-USER.TXT").Open());
            writer.Write("duplicate.example");
        }
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));
    }

    [Fact]
    public async Task ImportRollsBackEarlierFileChangesWhenReplacingALockedPayloadFails()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        await File.WriteAllTextAsync(fixture.File("lists/list-general-user.txt"), "changed.example\n");
        await fixture.Settings.SaveAsync(new DesktopSettings { Theme = "Light" });
        await File.WriteAllBytesAsync(fixture.File("bin/ACTIVE_GAME_UDP.bin"), [9]);
        using var locked = new FileStream(fixture.File("bin/ACTIVE_GAME_UDP.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
        archive.Position = 0;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.ImportAsync(archive));
        Assert.Equal("changed.example\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
        Assert.Equal("Light", (await fixture.Settings.LoadAsync()).Theme);
        Assert.Single(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task ExportedOriginalPayloadCanBeRecoveredAfterImportOnAnotherInstallation()
    {
        using var source = new BackupFixture();
        await source.PopulateAsync();
        await File.WriteAllBytesAsync(source.File("bin/stun.bin"), [4, 5]);
        await new FakePayloadService(source.Distribution, source.DataDirectory).ReplaceAsync("ACTIVE_GAME_UDP.bin", "stun.bin");
        using var archive = new MemoryStream();
        await source.Service.ExportAsync(archive);
        using var destination = new BackupFixture();
        await destination.PopulateAsync();
        await File.WriteAllBytesAsync(destination.File("bin/ACTIVE_GAME_UDP.bin"), [7]);
        archive.Position = 0;
        await destination.Service.ImportAsync(archive);
        var recovery = new PayloadRecoveryService(destination.Distribution, destination.DataDirectory);
        Assert.True((await recovery.InspectAsync("ACTIVE_GAME_UDP.bin")).CanRestore);
        await recovery.RestoreOriginalAsync("ACTIVE_GAME_UDP.bin");
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination.File("bin/ACTIVE_GAME_UDP.bin")));
    }

    [Fact]
    public async Task ImportKeepsTheExistingLocalOriginalInsteadOfOverwritingIt()
    {
        using var source = new BackupFixture();
        await source.PopulateAsync();
        await File.WriteAllBytesAsync(source.File("bin/stun.bin"), [4]);
        await new FakePayloadService(source.Distribution, source.DataDirectory).ReplaceAsync("ACTIVE_GAME_UDP.bin", "stun.bin");
        using var archive = new MemoryStream();
        await source.Service.ExportAsync(archive);
        using var destination = new BackupFixture();
        await destination.PopulateAsync();
        await File.WriteAllBytesAsync(destination.File("bin/ACTIVE_GAME_UDP.bin"), [7]);
        await File.WriteAllBytesAsync(destination.File("bin/stun.bin"), [9]);
        await new FakePayloadService(destination.Distribution, destination.DataDirectory).ReplaceAsync("ACTIVE_GAME_UDP.bin", "stun.bin");
        archive.Position = 0;
        await destination.Service.ImportAsync(archive);
        await new PayloadRecoveryService(destination.Distribution, destination.DataDirectory).RestoreOriginalAsync("ACTIVE_GAME_UDP.bin");
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(destination.File("bin/ACTIVE_GAME_UDP.bin")));
    }

    [Fact]
    public async Task CancelledImportDoesNotWriteFilesOrCreateABackup()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        await File.WriteAllTextAsync(fixture.File("lists/list-general-user.txt"), "changed.example\n");
        archive.Position = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ImportAsync(archive, cancellation.Token));
        Assert.Equal("changed.example\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task BackupRetentionKeepsFiftyCopiesAndProtectsTheSelectedRestoreCopy()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        var selected = await fixture.Service.CreateAsync("selected restore copy");
        for (var index = 1; index < 50; index++) await fixture.Service.CreateAsync("copy " + index);
        await File.WriteAllTextAsync(fixture.File("lists/list-general-user.txt"), "changed.example\n");
        await fixture.Service.RestoreAsync(selected.Id);
        var afterRestore = await fixture.Service.GetBackupsAsync();
        Assert.Equal(50, afterRestore.Count);
        Assert.Contains(afterRestore, x => x.Id == selected.Id);
        Assert.Equal("my.example\r\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
        await fixture.Service.CreateAsync("next operation");
        var afterCreation = await fixture.Service.GetBackupsAsync();
        Assert.Equal(50, afterCreation.Count);
        Assert.DoesNotContain(afterCreation, x => x.Id == selected.Id);
    }

    [Theory]
    [InlineData("{\"Theme\":\"Dark\",\"Command\":\"powershell.exe\"}")]
    [InlineData("{\"Theme\":\"Dark\",\"Theme\":\"Light\"}")]
    [InlineData("{\"Theme\":\"Unknown\"}")]
    [InlineData("{\"StartWithWindows\":\"true\"}")]
    public async Task ImportRejectsInvalidSettingsEvenWithMatchingArchiveChecksum(string json)
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        RewriteEntry(archive, "settings.json", Encoding.UTF8.GetBytes(json), updateChecksum: true);
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));
        Assert.Equal("Dark", (await fixture.Settings.LoadAsync()).Theme);
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task ImportRejectsTamperedDataWhenManifestHashDoesNotMatch()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        RewriteEntry(archive, "lists/list-general-user.txt", Encoding.UTF8.GetBytes("tampered.example"));
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));
        Assert.Equal("my.example\r\n", await File.ReadAllTextAsync(fixture.File("lists/list-general-user.txt")));
    }

    [Fact]
    public async Task ImportRejectsEntriesMarkedAsSymbolicLinks()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, true))
            zip.GetEntry("lists/list-general-user.txt")!.ExternalAttributes = unchecked((int)0xA1FF0000);
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));
    }

    [Fact]
    public async Task ImportRejectsCompressedEntryOverTheExpandedSizeLimit()
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        RewriteEntry(archive, "lists/list-general-user.txt", new byte[16 * 1024 * 1024 + 1]);
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task BackupRejectsAnUnboundedReason()
    {
        using var fixture = new BackupFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(new string('x', 121)));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Theory]
    [InlineData("mode=all\ntcp=443\nudp=5000\" & echo unsafe\n")]
    [InlineData("mode=all\ncommand=unsafe\n")]
    [InlineData("mode=all\ntcp=70000\n")]
    [InlineData("mode=all\nmode=disabled\n")]
    public async Task ImportRejectsUnsafeGameFilterContentWithAMatchingChecksum(string content)
    {
        using var fixture = new BackupFixture();
        await fixture.PopulateAsync();
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        RewriteEntry(archive, "utils/game_filter.enabled", Encoding.UTF8.GetBytes(content), updateChecksum: true);
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));
        Assert.Equal("mode=all\ntcp=443\nudp=5000\n", await File.ReadAllTextAsync(fixture.File("utils/game_filter.enabled")));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    private static void RewriteEntry(MemoryStream archive, string name, byte[] value, bool updateChecksum = false)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Update, true);
        zip.GetEntry(name)!.Delete();
        using (var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open()) stream.Write(value);
        if (!updateChecksum) return;
        var manifestEntry = zip.GetEntry("manifest.json")!;
        JsonNode manifest;
        using (var reader = new StreamReader(manifestEntry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!;
        manifest["Files"]![name]!["Length"] = value.Length;
        manifest["Files"]![name]!["Sha256"] = Convert.ToHexString(SHA256.HashData(value));
        manifestEntry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        writer.Write(manifest.ToJsonString());
    }

    [Fact]
    public async Task RestoreRejectsAPathInsteadOfABackupId()
    {
        using var fixture = new BackupFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RestoreAsync("../outside"));
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "zapret-backup-" + Guid.NewGuid().ToString("N"));
        private string Root => Path.Combine(directory, "distribution");
        public string DataDirectory => Path.Combine(directory, "data");
        public ZapretDistribution Distribution => new(Root);
        public JsonSettingsStore Settings { get; }
        public UserDataBackupService Service { get; }
        public BackupFixture()
        {
            foreach (var subdir in new[] { "lists", "bin", "utils" }) Directory.CreateDirectory(Path.Combine(Root, subdir));
            Settings = new JsonSettingsStore(Path.Combine(DataDirectory, "settings.json"));
            Service = new UserDataBackupService(Distribution, Settings, DataDirectory);
        }
        public string File(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public async Task PopulateAsync()
        {
            await Settings.SaveAsync(new DesktopSettings { Theme = "Dark", SelectedStrategyId = "general", MinimizeToTray = false });
            await System.IO.File.WriteAllTextAsync(File("lists/list-general-user.txt"), "my.example\r\n");
            await System.IO.File.WriteAllTextAsync(File("lists/ipset-all.txt"), "");
            await System.IO.File.WriteAllTextAsync(File("lists/ipset-all.txt.backup"), "1.2.3.0/24\n");
            await System.IO.File.WriteAllTextAsync(File("utils/game_filter.enabled"), "mode=all\ntcp=443\nudp=5000\n");
            await System.IO.File.WriteAllBytesAsync(File("bin/ACTIVE_GAME_UDP.bin"), [1, 2, 3]);
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
