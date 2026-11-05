using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Zapret.Core;
using Zapret.TelegramProxy.MtProto;
using Zapret.TelegramProxy.Routing;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Tests;

public sealed class ConnectionIssueTests
{
    [Fact]
    public async Task ClosingLocalClientDoesNotPublishFakeConnectionFailure()
    {
        var options = ServerTests.Options(); var events = new ConcurrentQueue<TelegramProxyDiagnostic>();
        await using var server = new TelegramProxyServer(() => new Connector(() => new ControlledUpstream())); server.Diagnostic += events.Enqueue;
        await server.StartAsync(options,TestContext.Current.CancellationToken);
        using var client = await ConnectAsync(options); await ServerTests.WaitFor(server,s => s.Routes == "WebSocket");
        client.Dispose(); await ServerTests.WaitFor(server,s => s.ActiveConnections == 0);
        Assert.Empty(events); Assert.Equal(0,server.Snapshot.ConnectionErrors); Assert.Null(server.Snapshot.LastError); Assert.Null(server.Snapshot.LastConnectionError);
        Assert.Equal(TelegramProxyState.Running,server.Snapshot.State);
    }
    [Fact]
    public async Task BadKeyIsReportedOnceAsClientRejectionWithoutPoisoningServerStatus()
    {
        var options = ServerTests.Options(); var events = new ConcurrentQueue<TelegramProxyDiagnostic>();
        await using var server = new TelegramProxyServer(); server.Diagnostic += events.Enqueue; await server.StartAsync(options,TestContext.Current.CancellationToken);
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback,options.Port,TestContext.Current.CancellationToken);
        await client.GetStream().WriteAsync(new byte[64],TestContext.Current.CancellationToken);
        Assert.Equal(0,await client.GetStream().ReadAsync(new byte[1],TestContext.Current.CancellationToken));
        await ServerTests.WaitFor(server,s => s.ActiveConnections == 0);
        var issue = Assert.Single(events); Assert.Equal("WARN",issue.Level); Assert.Contains("ключ или транспорт",issue.Message);
        Assert.DoesNotContain(options.Secret,issue.Message); Assert.Equal(1,server.Snapshot.RejectedConnections); Assert.Equal(0,server.Snapshot.ConnectionErrors);
        Assert.Equal(TelegramProxyState.Running,server.Snapshot.State); Assert.Null(server.Snapshot.LastError); Assert.NotNull(server.Snapshot.LastConnectionErrorAt);
    }
    [Fact]
    public async Task GenuineUpstreamFailureRetainsDcRouteDirectionAndTypedReason()
    {
        var options = ServerTests.Options(); var upstream = new ControlledUpstream(); var events = new ConcurrentQueue<TelegramProxyDiagnostic>();
        await using var server = new TelegramProxyServer(() => new Connector(() => upstream)); server.Diagnostic += events.Enqueue;
        await server.StartAsync(options,TestContext.Current.CancellationToken); using var client = await ConnectAsync(options);
        await ServerTests.WaitFor(server,s => s.Routes == "WebSocket");
        upstream.Download.TrySetException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        await ServerTests.WaitFor(server,s => s.ActiveConnections == 0);
        var issue = Assert.Single(events); Assert.Equal("WARN",issue.Level); Assert.Contains("DC4 media, WebSocket",issue.Message); Assert.Contains("приём от Telegram",issue.Message);
        Assert.Contains("ConnectionClosedPrematurely",issue.Message); Assert.Equal(1,server.Snapshot.ConnectionErrors); Assert.Null(server.Snapshot.LastError);
    }
    [Fact]
    public async Task OversizedClientPacketIsNotMisreportedAsAnInitError()
    {
        var options = ServerTests.Options(); var events = new ConcurrentQueue<TelegramProxyDiagnostic>();
        await using var server = new TelegramProxyServer(() => new Connector(() => new ControlledUpstream())); server.Diagnostic += events.Enqueue;
        await server.StartAsync(options,TestContext.Current.CancellationToken); using var client = await ConnectAsync(options);
        await ServerTests.WaitFor(server,s => s.Routes == "WebSocket");
        var init = ProtocolTests.ClientInit(-4,ObfuscatedTransport.Padded,Convert.FromHexString(options.Secret));
        var key = System.Security.Cryptography.SHA256.HashData(init[8..40].Concat(Convert.FromHexString(options.Secret)).ToArray());
        var encrypted = ProtocolTests.Transform(new byte[] {255,255,255,127},key,init[40..56],64);
        await client.GetStream().WriteAsync(encrypted,TestContext.Current.CancellationToken); await ServerTests.WaitFor(server,s => s.ActiveConnections == 0);
        var issue = Assert.Single(events); Assert.Contains("отправка в Telegram",issue.Message); Assert.DoesNotContain("init",issue.Message); Assert.Equal(0,server.Snapshot.RejectedConnections);
    }
    [Fact]
    public async Task FailedRelayInitDoesNotEraseAnotherActiveRoute()
    {
        var options = ServerTests.Options(); var count = 0;
        await using var server = new TelegramProxyServer(() => new Connector(() => new ControlledUpstream(failSend: Interlocked.Increment(ref count) == 2)));
        await server.StartAsync(options,TestContext.Current.CancellationToken);
        using var first = await ConnectAsync(options); await ServerTests.WaitFor(server,s => s.Routes == "WebSocket");
        using var second = await ConnectAsync(options); await ServerTests.WaitFor(server,s => s.TotalConnections == 2 && s.ActiveConnections == 1);
        Assert.Equal("WebSocket",server.Snapshot.Routes); Assert.Equal(1,server.Snapshot.ConnectionErrors);
    }
    [Fact]
    public async Task RepeatedRejectedClientsKeepCountersAndStopFlushesWarningSummary()
    {
        var options = ServerTests.Options(); var events = new ConcurrentQueue<TelegramProxyDiagnostic>();
        await using var server = new TelegramProxyServer(); server.Diagnostic += events.Enqueue;
        await server.StartAsync(options,TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback,options.Port,TestContext.Current.CancellationToken);
            await client.GetStream().WriteAsync(new byte[64],TestContext.Current.CancellationToken);
            Assert.Equal(0,await client.GetStream().ReadAsync(new byte[1],TestContext.Current.CancellationToken));
        }
        await ServerTests.WaitFor(server,s => s.ActiveConnections == 0);
        Assert.Single(events); Assert.Equal(5,server.Snapshot.RejectedConnections);
        await server.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2,events.Count); Assert.EndsWith("(повторений: 4)",events.Last().Message);
        await server.StartAsync(options,TestContext.Current.CancellationToken);
        using var next = new TcpClient(); await next.ConnectAsync(IPAddress.Loopback,options.Port,TestContext.Current.CancellationToken);
        await next.GetStream().WriteAsync(new byte[64],TestContext.Current.CancellationToken);
        Assert.Equal(0,await next.GetStream().ReadAsync(new byte[1],TestContext.Current.CancellationToken));
        await ServerTests.WaitFor(server,s => s.ActiveConnections == 0);
        Assert.Equal(3,events.Count); Assert.Equal(6,server.Snapshot.RejectedConnections);
    }
    private static async Task<TcpClient> ConnectAsync(TelegramProxyOptions options)
    {
        var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback,options.Port,TestContext.Current.CancellationToken);
        await client.GetStream().WriteAsync(ProtocolTests.ClientInit(-4,ObfuscatedTransport.Padded,Convert.FromHexString(options.Secret)),TestContext.Current.CancellationToken); return client;
    }
    private sealed class Connector(Func<IUpstream> factory) : IUpstreamConnector
    {
        public Task<IUpstream> ConnectAsync(ClientHello hello,TelegramProxyOptions options,CancellationToken ct) => Task.FromResult(factory());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ControlledUpstream(bool failSend = false) : IUpstream
    {
        public TaskCompletionSource<int> Download { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Route => "WebSocket"; public bool Plaintext => false;
        public ValueTask SendAsync(ReadOnlyMemory<byte> data,CancellationToken ct) => failSend ? ValueTask.FromException(new IOException("init failed")) : ValueTask.CompletedTask;
        public async ValueTask<int> ReceiveAsync(Memory<byte> data,CancellationToken ct)
        {
            try { return await Download.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
