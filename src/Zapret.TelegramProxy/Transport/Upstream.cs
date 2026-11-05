using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
namespace Zapret.TelegramProxy.Transport;

internal interface IUpstream : IAsyncDisposable
{
    string Route { get; }
    bool Plaintext { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask<int> ReceiveAsync(Memory<byte> data, CancellationToken ct);
}
internal sealed class WebSocketUpstream(ClientWebSocket socket, HttpMessageInvoker invoker, string route, int maximum) : IUpstream
{
    private int messageBytes;
    public string Route => route;
    public bool Plaintext => false;
    public static async Task<IUpstream> ConnectAsync(string host, string? ip, string path, string route, int maximum, CancellationToken ct, SocketsHttpHandler? trustedHandler = null)
    {
        var handler = trustedHandler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        if (ip is not null) handler.ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(IPAddress.Parse(ip), context.DnsEndPoint.Port, token).ConfigureAwait(false); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        };
        var invoker = new HttpMessageInvoker(handler);
        var ws = new ClientWebSocket(); ws.Options.AddSubProtocol("binary");
        try
        {
            await ws.ConnectAsync(new Uri($"wss://{host}{path}"), invoker, ct).ConfigureAwait(false);
            return new WebSocketUpstream(ws, invoker, route, maximum);
        }
        catch { ws.Dispose(); invoker.Dispose(); throw; }
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => socket.SendAsync(data, WebSocketMessageType.Binary, true, ct);
    public async ValueTask<int> ReceiveAsync(Memory<byte> data, CancellationToken ct)
    {
        while (true)
        {
            var result = await socket.ReceiveAsync(data, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return 0;
            if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Upstream прислал не бинарный WebSocket-пакет.");
            messageBytes = checked(messageBytes + result.Count);
            if (messageBytes > maximum) throw new InvalidDataException("Превышен размер WebSocket-сообщения.");
            if (result.EndOfMessage) messageBytes = 0;
            if (result.Count != 0) return result.Count;
        }
    }
    public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); invoker.Dispose(); return ValueTask.CompletedTask; }
}
internal sealed class TcpUpstream(TcpClient client, string route) : IUpstream
{
    public string Route => route;
    public bool Plaintext => false;
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => client.GetStream().WriteAsync(data, ct);
    public ValueTask<int> ReceiveAsync(Memory<byte> data, CancellationToken ct) => client.GetStream().ReadAsync(data, ct);
    public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
}
