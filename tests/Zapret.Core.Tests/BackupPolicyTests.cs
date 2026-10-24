using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class BackupPolicyTests
{
    [Fact]
    public async Task IdenticalAutomaticSnapshotsReuseTheExistingArchiveAcrossServiceInstances()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        var first = await fixture.Service.CreateAutomaticAsync("before list edit");
        var reloaded = new UserDataBackupService(fixture.Distribution, fixture.Settings, fixture.DataDirectory);

        var second = await ((IUserDataBackupService)reloaded).CreateAutomaticAsync("before filter edit");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await fixture.Service.GetBackupsAsync());
    }

    [Fact]
    public async Task ChangedAutomaticSnapshotCreatesANewArchiveAndManualCopiesRemainDistinct()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        var first = await fixture.Service.CreateAutomaticAsync("before list edit");
        await fixture.SetStateAsync(1);
        var changed = await fixture.Service.CreateAutomaticAsync("before list edit");
        var manual = await fixture.Service.CreateAsync();
        var anotherManual = await fixture.Service.CreateAsync();

        Assert.NotEqual(first.Id, changed.Id);
        Assert.NotEqual(changed.Id, manual.Id);
        Assert.NotEqual(manual.Id, anotherManual.Id);
        Assert.Equal(4, (await fixture.Service.GetBackupsAsync()).Count);
        await fixture.Service.RestoreAsync(first.Id);
        Assert.Equal("state0.example\n", await File.ReadAllTextAsync(fixture.ListFile));
    }

    [Fact]
    public async Task AutomaticRetentionKeepsTenNewArchivesAndPreservesManualAndLegacyCopies()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        var manual = await fixture.Service.CreateAsync();
        var legacyId = await fixture.CreateLegacyArchiveAsync();
        var automatic = new List<UserDataBackupInfo>();
        for (var index = 0; index < 12; index++)
        {
            await fixture.SetStateAsync(index);
            automatic.Add(await fixture.Service.CreateAutomaticAsync("before list edit"));
        }

        var backups = await fixture.Service.GetBackupsAsync();

        Assert.Equal(12, backups.Count);
        Assert.Contains(backups, x => x.Id == manual.Id);
        Assert.Contains(backups, x => x.Id == legacyId);
        Assert.DoesNotContain(backups, x => x.Id == automatic[0].Id || x.Id == automatic[1].Id);
        Assert.All(automatic.Skip(2), backup => Assert.Contains(backups, x => x.Id == backup.Id));
    }

    [Fact]
    public async Task RestoreProtectsItsSelectedAutomaticArchiveAndThePreOperationSnapshot()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        var selected = await fixture.Service.CreateAutomaticAsync("selected restore copy");
        var automatic = new List<UserDataBackupInfo>();
        for (var index = 1; index < 10; index++)
        {
            await fixture.SetStateAsync(index);
            automatic.Add(await fixture.Service.CreateAutomaticAsync("before list edit"));
        }
        await fixture.SetStateAsync(10);

        await fixture.Service.RestoreAsync(selected.Id);

        var backups = await fixture.Service.GetBackupsAsync();
        Assert.Equal(10, backups.Count);
        Assert.Contains(backups, x => x.Id == selected.Id);
        Assert.DoesNotContain(backups, x => x.Id == automatic[0].Id);
        var previous = Assert.Single(backups, x => x.Reason == "before restore");
        Assert.Equal("state0.example\n", await File.ReadAllTextAsync(fixture.ListFile));
        await fixture.Service.RestoreAsync(previous.Id);
        Assert.Equal("state10.example\n", await File.ReadAllTextAsync(fixture.ListFile));
        Assert.Contains(await fixture.Service.GetBackupsAsync(), x => x.Id == previous.Id);
    }

    [Fact]
    public async Task ImportReusesAValidPreOperationSnapshotThatCanRestoreThePreviousState()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        await fixture.SetStateAsync(1);
        var previous = await fixture.Service.CreateAutomaticAsync("before list edit");
        archive.Position = 0;

        await fixture.Service.ImportAsync(archive);

        Assert.Equal("state0.example\n", await File.ReadAllTextAsync(fixture.ListFile));
        Assert.Equal(previous.Id, Assert.Single(await fixture.Service.GetBackupsAsync()).Id);
        await fixture.Service.RestoreAsync(previous.Id);
        Assert.Equal("state1.example\n", await File.ReadAllTextAsync(fixture.ListFile));
    }

    [Fact]
    public async Task ImportAndRestoreOfUnchangedStateDoNotCreateAutomaticBackups()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        archive.Position = 0;

        await fixture.Service.ImportAsync(archive);
        Assert.Empty(await fixture.Service.GetBackupsAsync());
        var manual = await fixture.Service.CreateAsync();
        await fixture.Service.RestoreAsync(manual.Id);
        Assert.Equal(manual.Id, Assert.Single(await fixture.Service.GetBackupsAsync()).Id);
    }

    [Fact]
    public async Task ImportRejectsAnInvalidCurrentFilterBeforeChangingStateOrSavingAnUnreadableRecoveryCopy()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        await File.WriteAllTextAsync(fixture.FilterFile, "mode=all\n");
        using var archive = new MemoryStream();
        await fixture.Service.ExportAsync(archive);
        await fixture.SetStateAsync(1);
        await File.WriteAllTextAsync(fixture.FilterFile, "mode=all\ncommand=unsafe\n");
        archive.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(archive));

        Assert.Equal("state1.example\n", await File.ReadAllTextAsync(fixture.ListFile));
        Assert.Equal("mode=all\ncommand=unsafe\n", await File.ReadAllTextAsync(fixture.FilterFile));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "backups")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptOwnershipIndexProtectsPreviouslyCreatedArchives(bool corrupt)
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        var protectedCopy = await fixture.Service.CreateAutomaticAsync("before list edit");
        var indexFile = Path.Combine(fixture.DataDirectory, "backups", "automatic-backups.json");
        if (corrupt) await File.WriteAllTextAsync(indexFile, "{ broken index");
        else File.Delete(indexFile);
        for (var index = 1; index <= 12; index++)
        {
            await fixture.SetStateAsync(index);
            await fixture.Service.CreateAutomaticAsync("before list edit");
        }

        var backups = await fixture.Service.GetBackupsAsync();

        Assert.Equal(11, backups.Count);
        Assert.Contains(backups, x => x.Id == protectedCopy.Id);
    }

    [Fact]
    public async Task OwnershipIndexWithAnArchiveDigestMismatchCannotDeleteAManualArchive()
    {
        using var fixture = new BackupFixture();
        await fixture.SetStateAsync(0);
        var automatic = await fixture.Service.CreateAutomaticAsync("before list edit");
        var manual = await fixture.Service.CreateAsync();
        var indexFile = Path.Combine(fixture.DataDirectory, "backups", "automatic-backups.json");
        Assert.True(File.Exists(indexFile), "Automatic backups must persist their ownership metadata.");
        var indexContent = await File.ReadAllTextAsync(indexFile);
        await File.WriteAllTextAsync(indexFile, indexContent.Replace(automatic.Id, manual.Id, StringComparison.Ordinal));
        for (var index = 1; index <= 11; index++)
        {
            await fixture.SetStateAsync(index);
            await fixture.Service.CreateAutomaticAsync("before list edit");
        }

        var backups = await fixture.Service.GetBackupsAsync();

        Assert.Equal(12, backups.Count);
        Assert.Contains(backups, x => x.Id == automatic.Id);
        Assert.Contains(backups, x => x.Id == manual.Id);
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "zapret-backup-policy-" + Guid.NewGuid().ToString("N"));
        private string Root => Path.Combine(directory, "distribution");
        public string DataDirectory => Path.Combine(directory, "data");
        public string ListFile => Path.Combine(Root, "lists", "list-general-user.txt");
        public string FilterFile => Path.Combine(Root, "utils", "game_filter.enabled");
        public ZapretDistribution Distribution => new(Root);
        public JsonSettingsStore Settings { get; }
        public IUserDataBackupService Service { get; }

        public BackupFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "lists"));
            Directory.CreateDirectory(Path.Combine(Root, "utils"));
            Settings = new JsonSettingsStore(Path.Combine(DataDirectory, "settings.json"));
            Service = new UserDataBackupService(Distribution, Settings, DataDirectory);
        }

        public Task SetStateAsync(int index) => File.WriteAllTextAsync(ListFile, $"state{index}.example\n");

        public async Task<string> CreateLegacyArchiveAsync()
        {
            var id = Guid.NewGuid().ToString("N");
            var file = Path.Combine(DataDirectory, "backups", id + ".zip");
            await using var stream = File.Create(file);
            await Service.ExportAsync(stream);
            return id;
        }

        public void Dispose() => Directory.Delete(directory, true);
    }
}
