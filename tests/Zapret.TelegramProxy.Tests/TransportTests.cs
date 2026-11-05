using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Zapret.TelegramProxy.MtProto;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task RealTlsWebSocketHandshakeAndFragmentedBinaryRoundTrip()
    {
        await using var local = await LocalServer.StartAsync();
        await using var remote = await WebSocketUpstream.ConnectAsync(local.Host, "127.0.0.1", "/apiws", "local WS", 4096, TestContext.Current.CancellationToken, local.Trust());
        byte[] sent = [1, 2, 3, 4, 5, 6]; await remote.SendAsync(sent, TestContext.Current.CancellationToken);
        var result = new byte[6]; var offset = 0;
        while (offset < result.Length) offset += await remote.ReceiveAsync(result.AsMemory(offset), TestContext.Current.CancellationToken);
        Assert.Equal(sent, result);
    }
    [Fact]
    public async Task UntrustedCertificateIsRejected()
    {
        await using var local = await LocalServer.StartAsync();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => WebSocketUpstream.ConnectAsync(local.Host, "127.0.0.1", "/apiws", "bad cert", 4096, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task TimeoutDoesNotLeaveHandshakeSocketRunning()
    {
        await using var local = await LocalServer.StartAsync(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WebSocketUpstream.ConnectAsync(local.Host, "127.0.0.1", "/hang", "timeout", 4096, timeout.Token, local.Trust()));
    }
    [Fact]
    public async Task Http2PreflightAndMtprotoPostCarryActualBinaryData()
    {
        await using var local = await LocalServer.StartAsync();
        await using var remote = await Http2Upstream.ConnectAsync(local.Host, ObfuscatedTransport.Intermediate, 4096, TestContext.Current.CancellationToken, local.Trust());
        var frame = PacketFraming.HttpReply(new byte[32], ObfuscatedTransport.Intermediate);
        await remote.SendAsync(frame, TestContext.Current.CancellationToken);
        var result = new byte[frame.Length]; var offset = 0;
        while (offset < result.Length) offset += await remote.ReceiveAsync(result.AsMemory(offset), TestContext.Current.CancellationToken);
        Assert.Equal(frame, result); Assert.True(local.UsedHttp2);
    }
    private sealed class LocalServer(WebApplication app, X509Certificate2 certificate) : IAsyncDisposable
    {
        public string Host => "localhost:" + new Uri(app.Urls.Single()).Port;
        public bool UsedHttp2;
        public SocketsHttpHandler Trust()
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(certificate);
            return new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy } };
        }
        public static async Task<LocalServer> StartAsync()
        {
            using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificate = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null);
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => { listen.Protocols = HttpProtocols.Http1AndHttp2; listen.UseHttps(certificate); }));
            var app = builder.Build(); var local = new LocalServer(app, certificate); app.UseWebSockets();
            app.Map("/apiws", async context =>
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync("binary"); var buffer = new byte[4096];
                try
                {
                    var read = await ws.ReceiveAsync(buffer, context.RequestAborted);
                    if (read.MessageType != WebSocketMessageType.Binary) return;
                    var half = read.Count / 2;
                    await ws.SendAsync(buffer.AsMemory(0, half), WebSocketMessageType.Binary, false, context.RequestAborted);
                    await ws.SendAsync(buffer.AsMemory(half, read.Count - half), WebSocketMessageType.Binary, true, context.RequestAborted);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            });
            app.Map("/api", async context =>
            {
                local.UsedHttp2 = context.Request.Protocol == "HTTP/2"; context.Response.ContentType = "application/octet-stream";
                if (context.Request.Method == "POST") await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestAborted);
            });
            app.Map("/hang", async context => { try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { } });
            await app.StartAsync(TestContext.Current.CancellationToken); return local;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(CancellationToken.None); await app.DisposeAsync(); certificate.Dispose(); }
    }
}
