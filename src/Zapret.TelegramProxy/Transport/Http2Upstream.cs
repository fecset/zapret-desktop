using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Zapret.TelegramProxy.MtProto;
namespace Zapret.TelegramProxy.Transport;

// Native HTTP/2 replaces httpx/h2. Each POST carries one unframed MTProto body.
internal sealed class Http2Upstream : IUpstream
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly uint protocol;
    private readonly int maximum;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim slots = new(2);
    private readonly Channel<byte[]> replies = Channel.CreateBounded<byte[]>(2);
    private readonly List<Task> pending = [];
    private readonly object sync = new();
    private byte[]? remainder, replay;
    private int offset, replayCount;
    private DateTimeOffset replayUntil, replaySentAt, replayLastAttempt;
    private byte[]? replayReplyId;
    public string Route => "CfProxy HTTP/2";
    public bool Plaintext => true;
    private Http2Upstream(HttpClient client, string host, uint protocol, int maximum)
    { this.client = client; endpoint = new Uri($"https://{host}/api"); this.protocol = protocol; this.maximum = maximum; }
    public static async Task<IUpstream> ConnectAsync(string host, uint protocol, int maximum, CancellationToken ct, SocketsHttpHandler? trustedHandler = null)
    {
        var client = new HttpClient(trustedHandler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxConnectionsPerServer = 1 })
        { Timeout = TimeSpan.FromSeconds(65) };
        try
        {
            using var request = Request(HttpMethod.Head, new Uri($"https://{host}/api"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.Version.Major != 2 || !(response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
                throw new IOException("CF /api не поддерживает HTTP/2.");
            return new Http2Upstream(client, host, protocol, maximum);
        }
        catch { client.Dispose(); throw; }
    }
    private static HttpRequestMessage Request(HttpMethod method, Uri uri) => new(method, uri)
    { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var body = PacketFraming.HttpBody(frame.ToArray(), protocol, maximum);
        lock (sync)
        {
            if (body.Length <= 65536 && !body.AsSpan(0, 8).SequenceEqual(new byte[8]))
            { replay = body; replayCount = 0; replaySentAt = DateTimeOffset.UtcNow; replayUntil = replaySentAt.AddSeconds(30); replayLastAttempt = default; replayReplyId = null; }
            else replay = null;
        }
        await QueueAsync(body, ct).ConfigureAwait(false);
    }
    private async Task QueueAsync(byte[] body, CancellationToken ct)
    {
        await slots.WaitAsync(ct).ConfigureAwait(false);
        lock (sync)
        {
            pending.RemoveAll(x => x.IsCompleted);
            pending.Add(PostAsync(body));
        }
    }
    private async Task PostAsync(byte[] body)
    {
        try
        {
            using var request = Request(HttpMethod.Post, endpoint);
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)response.StatusCode == 444)
            {
                var error = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(error, -(int)response.StatusCode);
                await replies.Writer.WriteAsync(PacketFraming.HttpReply(error, protocol), lifetime.Token).ConfigureAwait(false);
                replies.Writer.TryComplete(); return;
            }
            if (response.Version.Major != 2 || response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentType?.MediaType == "text/html" || response.Content.Headers.ContentLength > maximum)
                throw new IOException("Недопустимый ответ CF HTTP/2.");
            await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token).ConfigureAwait(false);
            using var content = new MemoryStream(); var buffer = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false); if (count == 0) break;
                if (content.Length + count > maximum) throw new InvalidDataException("Превышен размер ответа CF HTTP/2.");
                content.Write(buffer, 0, count);
            }
            if (content.Length != 0)
            {
                var responseBody = content.ToArray();
                lock (sync)
                {
                    if (ReferenceEquals(body, replay) && (replayReplyId is null || !responseBody.AsSpan(0, Math.Min(24,responseBody.Length)).SequenceEqual(replayReplyId)))
                    { replayReplyId = responseBody[..Math.Min(24,responseBody.Length)]; replayCount = 0; }
                }
                await replies.Writer.WriteAsync(PacketFraming.HttpReply(responseBody, protocol), lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { replies.Writer.TryComplete(new IOException("Ошибка передачи CF HTTP/2: " + ex.GetType().Name, ex)); }
        finally { slots.Release(); }
    }
    public async ValueTask<int> ReceiveAsync(Memory<byte> data, CancellationToken ct)
    {
        while (remainder is null)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(1));
            try { remainder = await replies.Reader.ReadAsync(wait.Token).ConfigureAwait(false); offset = 0; }
            catch (ChannelClosedException ex) { if (ex.InnerException is not null) throw new IOException("CF HTTP/2 завершил соединение.", ex.InnerException); return 0; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                byte[]? body = null;
                lock (sync)
                {
                    if (replay is not null && replayCount < 3 && DateTimeOffset.UtcNow < replayUntil &&
                        slots.CurrentCount > 0 && DateTimeOffset.UtcNow - replayLastAttempt >= TimeSpan.FromSeconds(2) &&
                        (pending.All(x => x.IsCompleted) || DateTimeOffset.UtcNow - replaySentAt >= TimeSpan.FromSeconds(3)))
                    { body = replay; replayCount++; replayLastAttempt = DateTimeOffset.UtcNow; }
                }
                if (body is not null) await QueueAsync(body, ct).ConfigureAwait(false);
            }
        }
        var count = Math.Min(data.Length, remainder.Length - offset); remainder.AsMemory(offset, count).CopyTo(data); offset += count;
        if (offset == remainder.Length) remainder = null;
        return count;
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); replies.Writer.TryComplete(); client.Dispose();
        Task[] tasks; lock (sync) tasks = pending.ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false); slots.Dispose(); lifetime.Dispose();
    }
}
