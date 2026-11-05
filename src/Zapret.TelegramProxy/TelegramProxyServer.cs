using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Zapret.Core;
using Zapret.TelegramProxy.MtProto;
using Zapret.TelegramProxy.Routing;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy;

public sealed class TelegramProxyServer : ITelegramProxy
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly object stateLock = new();
    private readonly ConcurrentDictionary<long,Task> clients = new();
    private readonly ConcurrentDictionary<string,int> routes = new();
    private readonly Func<IUpstreamConnector> connectorFactory;
    private readonly DiagnosticCoalescer diagnostics;
    private IUpstreamConnector? connector;
    private TcpListener? listener;
    private CancellationTokenSource? lifetime;
    private Task? accepting;
    private TelegramProxySnapshot state = new(TelegramProxyState.Stopped);
    private int active;
    private long total, sent, received;
    private bool disposed;
    public TelegramProxyServer() : this(() => new UpstreamConnector()) { }
    internal TelegramProxyServer(Func<IUpstreamConnector> connectorFactory)
    {
        this.connectorFactory = connectorFactory;
        diagnostics = new(entry => Diagnostic?.Invoke(entry));
    }
    public event Action? Changed;
    public event Action<TelegramProxyDiagnostic>? Diagnostic;
    public TelegramProxySnapshot Snapshot
    {
        get
        {
            lock (stateLock) return state with { ActiveConnections = Volatile.Read(ref active), TotalConnections = Interlocked.Read(ref total),
                BytesSent = Interlocked.Read(ref sent), BytesReceived = Interlocked.Read(ref received),
                Routes = string.Join(", ", routes.Where(x => x.Value > 0).Select(x => x.Key).Order()) };
        }
    }
    private void SetState(TelegramProxyState value, int? port = null, string? error = null)
    {
        lock (stateLock) state = state with { State = value, Port = port ?? state.Port,
            StartedAt = value == TelegramProxyState.Running ? DateTimeOffset.UtcNow : null, LastError = error };
        Changed?.Invoke();
    }
    public async Task StartAsync(TelegramProxyOptions options, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private async Task StartCoreAsync(TelegramProxyOptions options, CancellationToken cancellationToken)
    {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Snapshot.State == TelegramProxyState.Running) return;
            cancellationToken.ThrowIfCancellationRequested(); options.Validate(); options = options.Copy();
            // Recover resources from a failed accept loop before binding again.
            await StopCoreAsync().ConfigureAwait(false);
            SetState(TelegramProxyState.Starting, options.Port);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lifetime = new CancellationTokenSource(); connector = connectorFactory();
                listener = new TcpListener(IPAddress.Loopback, options.Port); listener.Server.ExclusiveAddressUse = true;
                listener.Start(options.MaxConnections);
                cancellationToken.ThrowIfCancellationRequested();
                SetState(TelegramProxyState.Running);
                accepting = Task.Run(() => AcceptAsync(listener, connector, options, lifetime.Token));
            }
            catch (Exception ex)
            {
                await StopCoreAsync().ConfigureAwait(false);
                var error = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"Порт {options.Port} занят. Измените порт или остановите использующую его программу." : "Не удалось запустить Telegram прокси: " + ex.GetType().Name;
                SetState(TelegramProxyState.Error, options.Port, error);
                if (ex is OperationCanceledException) throw;
                throw new IOException(error, ex);
            }
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (Snapshot.State != TelegramProxyState.Stopped) SetState(TelegramProxyState.Stopping); await StopCoreAsync().ConfigureAwait(false); SetState(TelegramProxyState.Stopped); }
        catch (TimeoutException) { SetState(TelegramProxyState.Error, error: "Превышено время остановки соединений Telegram прокси."); throw; }
        finally { gate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        lifetime?.Cancel(); listener?.Stop();
        if (accepting is not null) await accepting.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Task.WhenAll(clients.Values).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        diagnostics.Flush();
        if (connector is not null) await connector.DisposeAsync().ConfigureAwait(false);
        connector = null; accepting = null; listener = null; lifetime?.Dispose(); lifetime = null; routes.Clear();
    }
    public async Task RestartAsync(TelegramProxyOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { SetState(TelegramProxyState.Stopping); await StopCoreAsync().ConfigureAwait(false); SetState(TelegramProxyState.Stopped); await StartCoreAsync(options, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task AcceptAsync(TcpListener server, IUpstreamConnector upstream, TelegramProxyOptions options, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (Volatile.Read(ref active) >= options.MaxConnections) { client.Dispose(); continue; }
                client.NoDelay = true; Interlocked.Increment(ref active); var id = Interlocked.Increment(ref total);
                var task = HandleClientAsync(client, upstream, options, ct); clients[id] = task;
                _ = task.ContinueWith(_ => clients.TryRemove(id, out var removed), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is SocketException or ObjectDisposedException) { }
        catch (Exception ex) { SetState(TelegramProxyState.Error, error: "Локальный сервер остановился: " + ex.GetType().Name); lifetime?.Cancel(); server.Stop(); }
    }
    private async Task HandleClientAsync(TcpClient client, IUpstreamConnector connector, TelegramProxyOptions options, CancellationToken stopping)
    {
        string? route = null; var routeCounted = false; ClientHello? hello = null; var phase = "чтение MTProto init";
        var secret = Convert.FromHexString(options.Secret);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using (client)
        try
        {
            var stream = client.GetStream(); var handshake = new byte[64];
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
            { deadline.CancelAfter(TimeSpan.FromSeconds(10)); await stream.ReadExactlyAsync(handshake, deadline.Token).ConfigureAwait(false); }
            phase = "проверка MTProto init";
            hello = ObfuscatedTransport.Parse(handshake, secret); var relay = ObfuscatedTransport.RelayInit(hello);
            using var crypto = new CryptoContext(hello, secret, relay);
            phase = "подключение upstream";
            await using var remote = await connector.ConnectAsync(hello, options, session.Token).ConfigureAwait(false);
            route = remote.Route; phase = "отправка relay init";
            if (!remote.Plaintext) await remote.SendAsync(relay, session.Token).ConfigureAwait(false);
             routes.AddOrUpdate(route, 1, (_, count) => count + 1); routeCounted = true; Changed?.Invoke();
            // Idle timeout measures successful progress in either direction.
            session.CancelAfter(TimeSpan.FromSeconds(options.IdleTimeoutSeconds));
            async Task Upload()
            {
                while (true)
                {
                    var packet = await PacketFraming.ReadAsync(stream, crypto.ClientRead, hello.Protocol, options.MaxPacketSize, session.Token).ConfigureAwait(false);
                    if (packet is null) return;
                    if (!remote.Plaintext) crypto.RelayWrite.Transform(packet);
                    await remote.SendAsync(packet, session.Token).ConfigureAwait(false);
                    Interlocked.Add(ref sent, packet.Length); session.CancelAfter(TimeSpan.FromSeconds(options.IdleTimeoutSeconds));
                }
            }
            async Task Download()
            {
                var buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
                try
                {
                    while (true)
                    {
                        var count = await remote.ReceiveAsync(buffer.AsMemory(0, options.BufferSize), session.Token).ConfigureAwait(false); if (count == 0) return;
                        if (!remote.Plaintext) crypto.RelayRead.Transform(buffer.AsSpan(0, count));
                        crypto.ClientWrite.Transform(buffer.AsSpan(0, count));
                        await stream.WriteAsync(buffer.AsMemory(0, count), session.Token).ConfigureAwait(false);
                        Interlocked.Add(ref received, count); session.CancelAfter(TimeSpan.FromSeconds(options.IdleTimeoutSeconds));
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            }
            phase = "передача данных";
            async Task DirectionAsync(Func<Task> transfer, string direction)
            {
                try { await transfer().ConfigureAwait(false); }
                catch (Exception ex) when (!session.IsCancellationRequested) { throw new RelayDirectionException(direction, ex); }
            }
            await DuplexRelay.RunAsync(DirectionAsync(Upload, "отправка в Telegram"), DirectionAsync(Download, "приём от Telegram"), session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested || stopping.IsCancellationRequested) { }
        catch (EndOfStreamException) { /* A partial init/packet closes this client only. */ }
        catch (Exception ex)
        {
            var cause = ex is RelayDirectionException direction ? direction.InnerException! : ex;
            var stage = ex is RelayDirectionException transfer ? transfer.Direction : phase;
            var clientEnded = stage == "отправка в Telegram" && (cause is EndOfStreamException ||
                cause is IOException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted } });
            if (clientEnded)
            {
                if (options.VerboseLogging) Diagnostic?.Invoke(new("INFO", "Telegram прокси: локальный клиент закрыл соединение."));
            }
            else
            {
                var initRejected = hello is null;
                var reason = initRejected && cause is InvalidDataException ? cause.Message switch
                {
                    "Неверный ключ или транспорт MTProto." => "ключ или транспорт не совпадает. Подключите Telegram с текущим ключом.",
                    "Неизвестный Telegram DC." => "запрошен неизвестный датацентр.",
                    _ => "недопустимый init-пакет."
                } : ConnectionDiagnostics.Describe(cause);
                var context = hello is null ? "локальный клиент" : $"DC{hello.Dc}{(hello.Media ? " media" : "")}, {route ?? "выбор маршрута"}";
                var detail = $"Telegram прокси: {context}, {stage}: {reason}";
                RecordConnectionIssue(detail, initRejected);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (routeCounted && route is not null) routes.AddOrUpdate(route, 0, (_, count) => Math.Max(0, count - 1));
            Interlocked.Decrement(ref active); Changed?.Invoke();
        }
    }
    private void RecordConnectionIssue(string detail, bool rejected)
    {
        lock (stateLock) state = state with
        {
            ConnectionErrors = state.ConnectionErrors + (rejected ? 0 : 1),
            RejectedConnections = state.RejectedConnections + (rejected ? 1 : 0),
            LastConnectionError = detail, LastConnectionErrorAt = DateTimeOffset.UtcNow
        };
        diagnostics.Report(new("WARN", detail)); Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); disposed = true; diagnostics.Dispose(); SetState(TelegramProxyState.Stopped); }
        finally { gate.Release(); }
    }
}
