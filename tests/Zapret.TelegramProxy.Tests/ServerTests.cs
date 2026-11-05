using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Zapret.Core;
using Zapret.TelegramProxy.MtProto;
using Zapret.TelegramProxy.Routing;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Tests;

public sealed class ServerTests
{
    internal static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    internal static TelegramProxyOptions Options() => new() { Port = Port(), Secret = new string('1', 32) };
    [Fact]
    public async Task StartStopStartAndConcurrentStartBindOnce()
    {
        await using var server = new TelegramProxyServer(); var options = Options();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => server.StartAsync(options, TestContext.Current.CancellationToken)));
        Assert.Equal(TelegramProxyState.Running, server.Snapshot.State);
        await server.StopAsync(TestContext.Current.CancellationToken); using (var bound = new TcpListener(IPAddress.Loopback, options.Port)) bound.Start();
        await server.StartAsync(options, TestContext.Current.CancellationToken); Assert.Equal(TelegramProxyState.Running, server.Snapshot.State);
        await server.StopAsync(TestContext.Current.CancellationToken); await server.StopAsync(TestContext.Current.CancellationToken); Assert.Equal(TelegramProxyState.Stopped, server.Snapshot.State);
    }
    [Fact]
    public async Task RestartChangesPortAndCancelledInitializationClosesListener()
    {
        await using var server = new TelegramProxyServer(); var options = Options();
        using var cancelled = new CancellationTokenSource();
        void CancelStart() { if (server.Snapshot.State == TelegramProxyState.Starting) cancelled.Cancel(); }
        server.Changed += CancelStart;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.StartAsync(options, cancelled.Token));
        server.Changed -= CancelStart;
        await server.StopAsync(TestContext.Current.CancellationToken);
        await server.StartAsync(options, TestContext.Current.CancellationToken);
        var replacement = Options(); await server.RestartAsync(replacement, TestContext.Current.CancellationToken);
        Assert.Equal(replacement.Port, server.Snapshot.Port);
        using var originalPort = new TcpListener(IPAddress.Loopback, options.Port); originalPort.Start();
    }
    [Fact]
    public async Task StopRacingWithStartAndDisposeLeavesNoServer()
    {
        var options = Options(); var server = new TelegramProxyServer();
        await Task.WhenAll(server.StartAsync(options, TestContext.Current.CancellationToken), server.StopAsync(TestContext.Current.CancellationToken));
        await server.DisposeAsync(); Assert.Equal(TelegramProxyState.Stopped,server.Snapshot.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync(options, TestContext.Current.CancellationToken));
        using var port = new TcpListener(IPAddress.Loopback, options.Port); port.Start();
    }
    [Fact]
    public async Task OccupiedPortAndCancellationAreRecoverable()
    {
        await using var server = new TelegramProxyServer(); var options = Options(); using var bound = new TcpListener(IPAddress.Loopback, options.Port); bound.Start();
        var error = await Assert.ThrowsAsync<IOException>(() => server.StartAsync(options, TestContext.Current.CancellationToken)); Assert.Contains("занят", error.Message);
        Assert.Equal(TelegramProxyState.Error, server.Snapshot.State); bound.Stop();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.StartAsync(options, cts.Token));
        await server.StartAsync(options, TestContext.Current.CancellationToken); await server.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task StopClosesIncompleteHandshakesAndReleasesPort()
    {
        var options = Options(); await using var server = new TelegramProxyServer(); await server.StartAsync(options, TestContext.Current.CancellationToken);
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, options.Port, TestContext.Current.CancellationToken);
        await WaitFor(server, s => s.ActiveConnections == 1); await tcp.GetStream().WriteAsync(new byte[3], TestContext.Current.CancellationToken);
        await server.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken); Assert.Equal(0, server.Snapshot.ActiveConnections);
        using var listener = new TcpListener(IPAddress.Loopback, options.Port); listener.Start();
    }
    [Fact]
    public async Task ConnectionLimitRejectsExcessClients()
    {
        var options = Options() with { MaxConnections = 1 }; await using var server = new TelegramProxyServer(); await server.StartAsync(options, TestContext.Current.CancellationToken);
        using var first = new TcpClient(); await first.ConnectAsync(IPAddress.Loopback, options.Port, TestContext.Current.CancellationToken); await WaitFor(server, s => s.ActiveConnections == 1);
        using var second = new TcpClient(); await second.ConnectAsync(IPAddress.Loopback, options.Port, TestContext.Current.CancellationToken);
        var read = await second.GetStream().ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken); Assert.Equal(0, read);
        Assert.Equal(1, server.Snapshot.ActiveConnections);
    }
    [Fact]
    public async Task ClientCiphertextIsRelayedAndRepliesAreReencryptedOverRealLocalTcp()
    {
        var options = Options(); var connector = new EchoConnector(); await using var server = new TelegramProxyServer(() => connector); await server.StartAsync(options, TestContext.Current.CancellationToken);
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, options.Port, TestContext.Current.CancellationToken);
        var secret = Convert.FromHexString(options.Secret); var init = ProtocolTests.ClientInit(-4, ObfuscatedTransport.Padded, secret);
        // Deliberately split both init and transport packet over TCP writes.
        await tcp.GetStream().WriteAsync(init.AsMemory(0, 9), TestContext.Current.CancellationToken); await tcp.GetStream().WriteAsync(init.AsMemory(9), TestContext.Current.CancellationToken);
        var clear = PacketFraming.HttpReply(Enumerable.Range(0, 128).Select(x => (byte)x).ToArray(), ObfuscatedTransport.Padded);
        var encrypted = ProtocolTests.Transform(clear, System.Security.Cryptography.SHA256.HashData(init[8..40].Concat(secret).ToArray()), init[40..56], 64);
        await tcp.GetStream().WriteAsync(encrypted.AsMemory(0, 2), TestContext.Current.CancellationToken); await tcp.GetStream().WriteAsync(encrypted.AsMemory(2), TestContext.Current.CancellationToken);
        var reply = new byte[clear.Length]; await tcp.GetStream().ReadExactlyAsync(reply, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var reverse = init[8..56].Reverse().ToArray();
        Assert.Equal(clear, ProtocolTests.Transform(reply, System.Security.Cryptography.SHA256.HashData(reverse[..32].Concat(secret).ToArray()), reverse[32..]));
        await WaitFor(server, s => s.BytesReceived == reply.Length);
        Assert.Equal(clear, connector.Echo.Clear); Assert.Equal(4, connector.Hello!.Dc); Assert.True(connector.Hello.Media);
        Assert.Equal(encrypted.Length, server.Snapshot.BytesSent); Assert.Equal("test echo", server.Snapshot.Routes);
        await server.StopAsync(TestContext.Current.CancellationToken); Assert.Equal(0, server.Snapshot.ActiveConnections);
    }
    [Fact]
    public async Task InvalidSecretNeverReachesUpstream()
    {
        var connector = new EchoConnector(); var options = Options(); await using var server = new TelegramProxyServer(() => connector); await server.StartAsync(options, TestContext.Current.CancellationToken);
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, options.Port, TestContext.Current.CancellationToken); await tcp.GetStream().WriteAsync(new byte[64], TestContext.Current.CancellationToken);
        Assert.Equal(0, await tcp.GetStream().ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Null(connector.Hello);
    }
    internal static async Task WaitFor(TelegramProxyServer server, Func<TelegramProxySnapshot,bool> predicate)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (predicate(server.Snapshot)) done.TrySetResult(); }
        server.Changed += Check; try { Check(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); } finally { server.Changed -= Check; }
    }
    private sealed class EchoConnector : IUpstreamConnector
    {
        public ClientHello? Hello; public EchoUpstream Echo = new();
        public Task<IUpstream> ConnectAsync(ClientHello hello, TelegramProxyOptions options, CancellationToken ct) { Hello = hello; return Task.FromResult<IUpstream>(Echo); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class EchoUpstream : IUpstream
    {
        private byte[]? init; public byte[]? Clear; private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
        public string Route => "test echo"; public bool Plaintext => false;
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (init is null) { init = data.ToArray(); return ValueTask.CompletedTask; }
            Clear = ProtocolTests.Transform(data.ToArray(), init[8..40], init[40..56], 64);
            var reverse = init[8..56].Reverse().ToArray(); replies.Writer.TryWrite(ProtocolTests.Transform(Clear, reverse[..32], reverse[32..])); return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReceiveAsync(Memory<byte> data, CancellationToken ct) { var result = await replies.Reader.ReadAsync(ct); result.CopyTo(data); return result.Length; }
        public ValueTask DisposeAsync() { replies.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
