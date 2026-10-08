using System.Text;

namespace Zapret.Desktop.Tests;

public sealed class RemainingIssuesTests
{
    [Fact]
    public async Task LogExportUsesASnapshotWhenNewEventsArrive()
    {
        using var app = new TestApplication();
        app.ViewModel.Log("INFO", new string('a', 2000));
        app.ViewModel.Log("INFO", "second event");
        await using var stream = new PausingStream();
        var export = app.ViewModel.SaveLogsAsync(stream, TestContext.Current.CancellationToken);
        await stream.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        app.ViewModel.Log("INFO", "new event during export");
        stream.Resume.TrySetResult();
        await export.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("second event", text);
        Assert.DoesNotContain("new event during export", text);
    }

    private sealed class PausingStream : MemoryStream
    {
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writing.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
