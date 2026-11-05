using System.Collections.Concurrent;
using System.Net.Sockets;
using Zapret.Core;
using Zapret.TelegramProxy.MtProto;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Routing;

internal interface IUpstreamConnector : IAsyncDisposable
{
    Task<IUpstream> ConnectAsync(ClientHello hello, TelegramProxyOptions options, CancellationToken ct);
}
internal sealed record RouteTarget(string Kind, string Host, string? Ip, string Path)
{ public string Key => $"{Kind}:{Host}:{Ip}:{Path}"; }
internal sealed class UpstreamConnector(Func<RouteTarget,ClientHello,TelegramProxyOptions,CancellationToken,Task<IUpstream>>? dial = null) : IUpstreamConnector
{
    private readonly ConcurrentDictionary<string,(int Failures, DateTimeOffset Until)> failures = new();
    private readonly ConcurrentDictionary<int,string> preferred = new();
    private readonly SemaphoreSlim attempts = new(8);
    internal static IEnumerable<RouteTarget> Targets(ClientHello hello, TelegramProxyOptions options, string? first = null)
    {
        if (options.DcOverrides.TryGetValue(hello.Dc, out var ip))
            foreach (var domain in DcRoutes.WsDomains(hello)) yield return new("WebSocket", domain, ip, hello.Test ? "/apiws_test" : "/apiws");
        var dst = (hello.Test ? DcRoutes.Test : DcRoutes.Production)[hello.Dc];
        foreach (var worker in options.WorkerDomains) yield return new("Worker", worker, null, $"/apiws?dst={Uri.EscapeDataString(dst)}&dc={hello.Dc}");
        if (options.CfProxy && !hello.Test)
        {
            var domains = (options.CfDomains.Length == 0 ? DcRoutes.DefaultCfDomains : options.CfDomains).ToArray();
            Random.Shared.Shuffle(domains);
            domains = domains.OrderBy(x => $"kws{hello.Dc}.{x}" == first ? 0 : 1).ToArray();
            if (hello.Media && options.Http2Media)
                foreach (var domain in domains) yield return new("CfProxy HTTP/2", $"kws{hello.Dc}.{domain}", null, "/api");
            foreach (var domain in domains) yield return new("CfProxy", $"kws{hello.Dc}.{domain}", null, "/apiws");
        }
        if (options.DirectTcp) yield return new("TCP", dst, null, "");
    }
    public async Task<IUpstream> ConnectAsync(ClientHello hello, TelegramProxyOptions options, CancellationToken ct)
    {
        await attempts.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var errors = new List<string>();
            preferred.TryGetValue(hello.Dc, out var first);
            // Give each route family a bounded budget, so dead CF hosts cannot starve TCP.
            foreach (var group in Targets(hello, options, first).GroupBy(x => x.Kind))
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
                foreach (var target in group)
                {
                    ct.ThrowIfCancellationRequested();
                    if (failures.TryGetValue(target.Key, out var failed) && failed.Until > DateTimeOffset.UtcNow) continue;
                    try
                    {
                        var result = await (dial ?? DialAsync)(target, hello, options, budget.Token).ConfigureAwait(false);
                        failures.TryRemove(target.Key, out _); if (target.Kind.StartsWith("CfProxy")) preferred[hello.Dc] = target.Host;
                        return result;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or System.Net.WebSockets.WebSocketException or SocketException or IOException or OperationCanceledException)
                    {
                        ct.ThrowIfCancellationRequested(); var count = failed.Failures + 1;
                        failures[target.Key] = (count, DateTimeOffset.UtcNow.AddSeconds(Math.Min(30 * Math.Pow(2, Math.Min(count - 1, 7)), 3600)));
                        errors.Add(target.Kind + ": " + ConnectionDiagnostics.Describe(ex)); if (budget.IsCancellationRequested) break;
                    }
                }
            }
            throw new UpstreamConnectionException("Маршруты Telegram DC недоступны: " + (errors.Count == 0 ? "повтор ограничен после ошибок" : string.Join(", ", errors.Distinct())));
        }
        finally { attempts.Release(); }
    }
    private static async Task<IUpstream> DialAsync(RouteTarget target, ClientHello hello, TelegramProxyOptions options, CancellationToken ct)
    {
        if (target.Kind == "CfProxy HTTP/2") return await Http2Upstream.ConnectAsync(target.Host, hello.Protocol, options.MaxPacketSize, ct).ConfigureAwait(false);
        if (target.Kind != "TCP") return await WebSocketUpstream.ConnectAsync(target.Host, target.Ip, target.Path, target.Kind, options.MaxPacketSize + 4, ct).ConfigureAwait(false);
        var tcp = new TcpClient { NoDelay = true };
        try { await tcp.ConnectAsync(target.Host, 443, ct).ConfigureAwait(false); return new TcpUpstream(tcp, "TCP"); }
        catch { tcp.Dispose(); throw; }
    }
    public ValueTask DisposeAsync() { attempts.Dispose(); return ValueTask.CompletedTask; }
}
