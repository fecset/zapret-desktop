using Zapret.Core;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Tests;

public sealed class DiagnosticCoalescerTests
{
    [Fact]
    public void RepeatsKeepFirstWarningAndExactCountAtWindowEnd()
    {
        var clock = new ManualClock(); var output = new List<TelegramProxyDiagnostic>();
        using var log = new DiagnosticCoalescer(output.Add, clock);
        var issue = new TelegramProxyDiagnostic("WARN", "DC4 media: ConnectionClosedPrematurely");
        for (var i = 0; i < 101; i++) log.Report(issue);
        Assert.Equal(issue, Assert.Single(output)); Assert.Equal(TimeSpan.FromSeconds(30), clock.Timer.Due);
        clock.Timer.Fire();
        Assert.Equal(2, output.Count); Assert.Equal(issue.Level, output[1].Level);
        Assert.Equal(issue.Message + " (повторений: 100)", output[1].Message); Assert.False(clock.Timer.Active);
        clock.Timer.Fire(); Assert.Equal(2, output.Count);
        log.Report(issue); Assert.Equal(issue, output[2]);
    }
    [Fact]
    public void DistinctReasonsAndRoutesAreReportedImmediatelyAndFlushKeepsCounts()
    {
        var clock = new ManualClock(); var output = new List<TelegramProxyDiagnostic>();
        using var log = new DiagnosticCoalescer(output.Add, clock);
        log.Report(new("WARN", "DC2: TLS")); log.Report(new("WARN", "DC2: TLS"));
        log.Report(new("WARN", "DC4: TLS")); log.Report(new("WARN", "DC4: Socket"));
        Assert.Equal(3, output.Count); log.Flush();
        Assert.Equal("DC2: TLS (повторений: 1)", output[3].Message); Assert.False(clock.Timer.Active);
        log.Flush(); Assert.Equal(4, output.Count);
    }
    [Fact]
    public void BoundedStorageFlushesPendingCountsAndDisposalStopsTimer()
    {
        var clock = new ManualClock(); var output = new List<TelegramProxyDiagnostic>();
        var log = new DiagnosticCoalescer(output.Add, clock);
        for (var i = 0; i < DiagnosticCoalescer.MaxDistinct; i++)
        { log.Report(new("WARN", i.ToString())); log.Report(new("WARN", i.ToString())); }
        log.Report(new("WARN", "last"));
        Assert.Equal(DiagnosticCoalescer.MaxDistinct * 2 + 1, output.Count);
        log.Report(new("WARN", "last")); log.Dispose();
        Assert.Equal("last (повторений: 1)", output[^1].Message); Assert.True(clock.Timer.Disposed);
        var count = output.Count; clock.Timer.Fire(); log.Dispose(); Assert.Equal(count, output.Count);
    }
    private sealed class ManualClock : TimeProvider
    {
        public ManualTimer Timer { get; private set; } = null!;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new ManualTimer(callback, state, dueTime);
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; private set; } = due;
        public bool Active => !Disposed && Due != Timeout.InfiniteTimeSpan;
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) { if (Disposed) return false; Due = dueTime; return true; }
        public void Fire() { if (!Active) return; Due = Timeout.InfiniteTimeSpan; callback(state); }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
