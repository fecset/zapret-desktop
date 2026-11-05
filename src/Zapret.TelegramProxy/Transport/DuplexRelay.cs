using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;

namespace Zapret.TelegramProxy.Transport;

internal sealed class RelayDirectionException(string direction, Exception cause) : IOException(direction, cause)
{
    public string Direction { get; } = direction;
}

internal static class DuplexRelay
{
    // Preserve the first outcome. Cancelling the other pump may throw a socket/WS
    // exception rather than OperationCanceledException; that is teardown, not a new failure.
    public static async Task RunAsync(Task upload, Task download, CancellationTokenSource session)
    {
        var first = await Task.WhenAny(upload, download).ConfigureAwait(false);
        var other = ReferenceEquals(first, upload) ? download : upload;
        var firstError = await ObserveAsync(first).ConfigureAwait(false);
        var otherAlreadyFaulted = other.IsFaulted;
        session.Cancel();
        var otherError = await ObserveAsync(other).ConfigureAwait(false);
        if (firstError is not null) ExceptionDispatchInfo.Capture(firstError).Throw();
        if (otherError is not null && (otherAlreadyFaulted || !IsTeardownException(otherError)))
            ExceptionDispatchInfo.Capture(otherError).Throw();
    }
    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception ex) { return ex; }
    }
    private static bool IsTeardownException(Exception error) => error is
        OperationCanceledException or WebSocketException or SocketException or IOException or ObjectDisposedException;
}
