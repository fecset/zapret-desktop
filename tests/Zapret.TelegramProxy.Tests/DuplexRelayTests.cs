using System.Net.WebSockets;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Tests;

public sealed class DuplexRelayTests
{
    [Fact]
    public async Task ClientEofDoesNotTurnCancellationOfOtherDirectionIntoWebSocketFailure()
    {
        using var session = new CancellationTokenSource();
        var receiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session.Token.Register(() => receiver.TrySetException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely)));
        await DuplexRelay.RunAsync(Task.CompletedTask,receiver.Task,session);
        Assert.True(session.IsCancellationRequested); Assert.True(receiver.Task.IsCompleted);
    }
    [Fact]
    public async Task PrimaryUpstreamFailureSurvivesSecondaryCancellationFailure()
    {
        using var session = new CancellationTokenSource(); var sender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session.Token.Register(() => sender.TrySetException(new IOException("teardown")));
        var failure = new RelayDirectionException("приём",new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        var actual = await Assert.ThrowsAsync<RelayDirectionException>(() => DuplexRelay.RunAsync(sender.Task,Task.FromException(failure),session));
        Assert.Same(failure,actual); Assert.True(sender.Task.IsCompleted);
    }
    [Fact]
    public async Task AlreadyFaultedSiblingIsNotHiddenByNormalEof()
    {
        using var session = new CancellationTokenSource(); var failure = new IOException("actual upstream failure");
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(() => DuplexRelay.RunAsync(Task.CompletedTask,Task.FromException(failure),session)));
    }
    [Fact]
    public async Task UnexpectedProgrammingFailureDuringTeardownIsNotSuppressed()
    {
        using var session = new CancellationTokenSource(); var receiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session.Token.Register(() => receiver.TrySetException(new InvalidOperationException("bug")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DuplexRelay.RunAsync(Task.CompletedTask,receiver.Task,session));
    }
}
