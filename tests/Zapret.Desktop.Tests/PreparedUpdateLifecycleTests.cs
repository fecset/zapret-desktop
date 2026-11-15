using Zapret.Core;

namespace Zapret.Desktop.Tests;

public sealed class PreparedUpdateLifecycleTests
{
    [Theory]
    [InlineData("distribution")]
    [InlineData("desktop")]
    [InlineData("ipset")]
    [InlineData("hosts")]
    public async Task ReplacingAPreviewDiscardsOnlyTheSupersededPlan(string kind)
    {
        var updates = new PendingUpdates();
        using var app = new TestApplication(managedUpdates: updates);
        await PrepareAsync(kind, app.ViewModel);
        var first = updates.LastId;
        await PrepareAsync(kind, app.ViewModel);
        Assert.Equal(new[] { first }, updates.Discarded);
        Assert.NotEqual(first, updates.LastId);
        Assert.False(app.ViewModel.PreparingData);
        Assert.False(app.ViewModel.MessageIsError);
    }

    [Theory]
    [InlineData("distribution")]
    [InlineData("desktop")]
    [InlineData("ipset")]
    [InlineData("hosts")]
    public async Task CancellationAfterServicePreparationDiscardsTheUnpublishedPlan(string kind)
    {
        var updates = new PendingUpdates();
        using var app = new TestApplication(managedUpdates: updates);
        updates.Prepared = app.ViewModel.CancelPreparation;
        await PrepareAsync(kind, app.ViewModel);
        Assert.Equal(new[] { updates.LastId }, updates.Discarded);
        Assert.Null(app.ViewModel.DistributionUpdatePlan);
        Assert.Null(app.ViewModel.DesktopUpdatePlan);
        Assert.Null(app.ViewModel.IpSetUpdatePlan);
        Assert.Null(app.ViewModel.HostsPreview);
    }

    [Fact]
    public async Task NormalExitDiscardsAllOwnedPendingPlans()
    {
        var updates = new PendingUpdates();
        using var app = new TestApplication(managedUpdates: updates);
        var ids = new List<string>();
        foreach (var kind in new[] { "distribution", "desktop", "ipset", "hosts" })
        {
            await PrepareAsync(kind, app.ViewModel);
            ids.Add(updates.LastId);
        }
        Assert.True(await app.ViewModel.StopOwnedForExitAsync());
        Assert.Equal(ids, updates.Discarded);
        Assert.Null(app.ViewModel.DesktopUpdatePlan);
    }

    [Fact]
    public async Task DesktopInstallationKeepsItsPreparedPlanUntilOwnershipTransfersToHelper()
    {
        var updates = new PendingUpdates();
        using var app = new TestApplication(managedUpdates: updates);
        await app.ViewModel.PrepareIpSetUpdateAsync();
        var ipset = updates.LastId;
        await app.ViewModel.PrepareDesktopUpdateAsync();
        var desktop = updates.LastId;
        Assert.True(await app.ViewModel.InstallDesktopUpdateForExitAsync());
        Assert.Equal(desktop, updates.ScheduledId);
        Assert.Equal(new[] { ipset }, updates.Discarded);
    }

    private static Task PrepareAsync(string kind, MainViewModel vm) => kind switch
    {
        "distribution" => vm.PrepareDistributionUpdateAsync(),
        "desktop" => vm.PrepareDesktopUpdateAsync(),
        "ipset" => vm.PrepareIpSetUpdateAsync(),
        "hosts" => vm.PreviewHostsAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class PendingUpdates : IManagedUpdateService
    {
        private int next;
        public string LastId { get; private set; } = "";
        public string? ScheduledId { get; private set; }
        public Action? Prepared { get; set; }
        public List<string> Discarded { get; } = [];
        private string NewId() { LastId = "plan-" + Interlocked.Increment(ref next); return LastId; }
        private Task<T> Complete<T>(T plan) { Prepared?.Invoke(); return Task.FromResult(plan); }
        public Task<ManagedUpdatePlan> PrepareDistributionAsync(IProgress<ManagedUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Complete(
                new ManagedUpdatePlan(NewId(), ManagedUpdateProduct.Distribution, "1.2.3", "", "", 1, "", 1, "", "prepared"));
        public Task<ManagedUpdatePlan> PrepareDesktopAsync(string localVersion, IProgress<ManagedUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Complete(
                new ManagedUpdatePlan(NewId(), ManagedUpdateProduct.Desktop, "1.2.3", "", "", 1, "", 1, "", "prepared"));
        public Task<IpSetUpdatePlan> PrepareIpSetAsync(CancellationToken cancellationToken = default) => Complete(
            new IpSetUpdatePlan(NewId(), "", "", 1, 1, "", "1.2.3.0/24\n", "prepared"));
        public Task<HostsUpdatePreview> PreviewHostsAsync(CancellationToken cancellationToken = default) => Complete(
            new HostsUpdatePreview(NewId(), "", "", "", "127.0.0.1 example.com\n", 1, "prepared"));
        public Task DiscardPreparedAsync(string id, CancellationToken cancellationToken = default)
        { Discarded.Add(id); return Task.CompletedTask; }
        public Task<DesktopUpdateSchedule> ScheduleDesktopUpdateAsync(ManagedUpdatePlan plan, int currentProcessId,
            CancellationToken cancellationToken = default)
        { ScheduledId = plan.Id; return Task.FromResult(new DesktopUpdateSchedule("", "", "", "scheduled")); }
        public Task<ManagedUpdateResult> ApplyDistributionAsync(ManagedUpdatePlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RollbackDistributionAsync(ManagedUpdateResult result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DataUpdateResult> ApplyIpSetAsync(IpSetUpdatePlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DataUpdateResult> ApplyHostsAsync(HostsUpdatePreview preview, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
