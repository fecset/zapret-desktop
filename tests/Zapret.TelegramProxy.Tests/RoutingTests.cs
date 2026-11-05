using Zapret.Core;
using Zapret.TelegramProxy.MtProto;
using Zapret.TelegramProxy.Routing;
using Zapret.TelegramProxy.Transport;
namespace Zapret.TelegramProxy.Tests;

public sealed class RoutingTests
{
    private static ClientHello Hello(int dc = 4, bool media = false, bool test = false) => new(dc, media, test, ObfuscatedTransport.Padded, new byte[48]);
    [Fact]
    public void RoutesMatchFlowsealOrderAndDcTables()
    {
        var options = new TelegramProxyOptions { WorkerDomains = ["worker.example"], CfDomains = ["cf.example"] };
        var targets = UpstreamConnector.Targets(Hello(media: true), options).ToArray();
        Assert.Equal(new[] { "WebSocket", "WebSocket", "Worker", "CfProxy HTTP/2", "CfProxy", "TCP" }, targets.Select(x => x.Kind));
        Assert.Equal("kws4-1.web.telegram.org", targets[0].Host); Assert.Equal("149.154.167.220", targets[0].Ip);
        Assert.Equal("/apiws?dst=149.154.167.91&dc=4", targets[2].Path); Assert.Equal("149.154.167.91", targets[^1].Host);
        Assert.Equal("kws2.web.telegram.org", DcRoutes.WsDomains(Hello(203)).Single());
        var test = UpstreamConnector.Targets(Hello(2, test: true), options).ToArray(); Assert.DoesNotContain(test, x => x.Kind.StartsWith("CfProxy"));
        Assert.Equal("/apiws_test", test[0].Path); Assert.Equal("149.154.167.40", test[^1].Host);
    }
    [Fact]
    public async Task FailedWsAndWorkerUseRealFallbackDialAndFailuresBackOff()
    {
        var seen = new List<string>();
        await using var connector = new UpstreamConnector((target, _, _, _) =>
        { seen.Add(target.Kind); return target.Kind == "TCP" ? Task.FromResult<IUpstream>(new NullUpstream()) : throw new IOException("unavailable"); });
        var options = new TelegramProxyOptions { WorkerDomains = ["worker.example"], CfDomains = ["cf.example"] };
        await using var first = await connector.ConnectAsync(Hello(), options, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "WebSocket", "Worker", "CfProxy", "TCP" }, seen);
        seen.Clear(); await using var second = await connector.ConnectAsync(Hello(), options, TestContext.Current.CancellationToken); Assert.Equal(new[] { "TCP" }, seen);
    }
    [Fact]
    public async Task DisabledDirectRouteCannotBeChosenAndCancellationPropagates()
    {
        await using var connector = new UpstreamConnector((_, _, _, _) => throw new IOException("unavailable"));
        await Assert.ThrowsAsync<UpstreamConnectionException>(() => connector.ConnectAsync(Hello(), new TelegramProxyOptions { CfProxy = false, DirectTcp = false }, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ConnectAsync(Hello(), new(), cancellation.Token));
    }
    private sealed class NullUpstream : IUpstream
    { public string Route => "TCP"; public bool Plaintext => false; public ValueTask SendAsync(ReadOnlyMemory<byte> data,CancellationToken ct) => ValueTask.CompletedTask; public ValueTask<int> ReceiveAsync(Memory<byte> data,CancellationToken ct) => ValueTask.FromResult(0); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
