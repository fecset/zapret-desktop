using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class PayloadRecoveryTests
{
    [Fact]
    public void MissingBinDirectoryProducesAnEmptyPayloadList()
    {
        using var fixture = new PayloadFixture();
        Directory.Delete(Path.Combine(fixture.Root, "bin"));
        Assert.Empty(new FakePayloadService(new ZapretDistribution(fixture.Root)).GetAvailablePayloads());
    }

    [Fact]
    public async Task InspectionComparesSha256OfTheTargetAndSelectedSource()
    {
        using var fixture = new PayloadFixture();
        await fixture.WriteAsync("ACTIVE_GAME_UDP.bin", [1, 2, 3]);
        await fixture.WriteAsync("stun.bin", [1, 2, 3]);
        var inspection = await fixture.Recovery().InspectAsync("ACTIVE_GAME_UDP.bin", "stun.bin");
        Assert.Equal("039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81", inspection.TargetSha256);
        Assert.Equal(inspection.TargetSha256, inspection.SourceSha256);
        Assert.True(inspection.MatchesSource);
    }

    [Fact]
    public async Task MultipleReplacementsPreserveTheOriginalAcrossServiceInstances()
    {
        using var fixture = new PayloadFixture();
        await fixture.WriteAsync("ACTIVE_GAME_UDP.bin", [1, 2, 3]);
        await fixture.WriteAsync("stun.bin", [4, 5]);
        await fixture.WriteAsync("stun2.bin", [6, 7]);
        var payloads = new FakePayloadService(new ZapretDistribution(fixture.Root), fixture.Data);
        await payloads.ReplaceAsync("ACTIVE_GAME_UDP.bin", "stun.bin");
        await new FakePayloadService(new ZapretDistribution(fixture.Root), fixture.Data).ReplaceAsync("ACTIVE_GAME_UDP.bin", "stun2.bin");
        var recovery = fixture.Recovery();
        var changed = await recovery.InspectAsync("ACTIVE_GAME_UDP.bin", "stun2.bin");
        Assert.False(changed.IsOriginal);
        Assert.True(changed.CanRestore);
        Assert.True(changed.MatchesSource);
        await recovery.RestoreOriginalAsync("ACTIVE_GAME_UDP.bin");
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.PathFor("ACTIVE_GAME_UDP.bin")));
        Assert.True((await fixture.Recovery().InspectAsync("ACTIVE_GAME_UDP.bin")).IsOriginal);
    }

    [Fact]
    public async Task RecoveryRejectsUnknownTargetAndReportsMissingTarget()
    {
        using var fixture = new PayloadFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Recovery().InspectAsync("../outside.bin"));
        var missing = await fixture.Recovery().InspectAsync("ACTIVE_GAME_UDP.bin");
        Assert.Null(missing.TargetSha256);
        Assert.False(missing.IsOriginal);
        Assert.False(missing.CanRestore);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Recovery().RestoreOriginalAsync("ACTIVE_GAME_UDP.bin"));
    }

    [Fact]
    public async Task SavedOriginalCanRecreateADeletedActivePayload()
    {
        using var fixture = new PayloadFixture();
        await fixture.WriteAsync("ACTIVE_GAME_UDP.bin", [1, 2, 3]);
        await fixture.WriteAsync("stun.bin", [4]);
        await new FakePayloadService(new ZapretDistribution(fixture.Root), fixture.Data).ReplaceAsync("ACTIVE_GAME_UDP.bin", "stun.bin");
        File.Delete(fixture.PathFor("ACTIVE_GAME_UDP.bin"));
        var inspection = await fixture.Recovery().InspectAsync("ACTIVE_GAME_UDP.bin");
        Assert.Null(inspection.TargetSha256);
        Assert.True(inspection.CanRestore);
        await fixture.Recovery().RestoreOriginalAsync("ACTIVE_GAME_UDP.bin");
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.PathFor("ACTIVE_GAME_UDP.bin")));
    }

    private sealed class PayloadFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zapret-payload-" + Guid.NewGuid().ToString("N"));
        public string Root => System.IO.Path.Combine(directory, "distribution");
        public string Data => System.IO.Path.Combine(directory, "data");
        public PayloadFixture() => Directory.CreateDirectory(System.IO.Path.Combine(Root, "bin"));
        public string PathFor(string name) => System.IO.Path.Combine(Root, "bin", name);
        public Task WriteAsync(string name, byte[] data) => File.WriteAllBytesAsync(PathFor(name), data);
        public PayloadRecoveryService Recovery() => new(new ZapretDistribution(Root), Data);
        public void Dispose() => Directory.Delete(directory, true);
    }
}
