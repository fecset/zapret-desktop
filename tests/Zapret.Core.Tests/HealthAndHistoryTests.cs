using System.Net;
using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class HealthAndHistoryTests
{
    [Fact]
    public async Task HealthDistinguishesHttpFailuresFromSuccessfulTlsRequests()
    {
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.NotEmpty(request.Headers.UserAgent);
            return new HttpResponseMessage(request.RequestUri.Host == "www.youtube.com" ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        }));
        var before = DateTimeOffset.UtcNow;
        var result = await new ConnectionHealthService(client).CheckAsync();
        Assert.InRange(result.CheckedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(2, result.Targets.Count);
        var youtube = Assert.Single(result.Targets, target => target.Name == "YouTube");
        Assert.True(youtube.Passed);
        Assert.Equal(200, youtube.HttpStatusCode);
        Assert.True(youtube.Duration >= TimeSpan.Zero);
        var discord = Assert.Single(result.Targets, target => target.Name == "Discord");
        Assert.False(discord.Passed);
        Assert.Equal(403, discord.HttpStatusCode);
        Assert.Contains("403", discord.Detail);
    }

    [Fact]
    public async Task HealthReportsTransportErrorsWithoutInventingHttpStatus()
    {
        using var client = new HttpClient(new ResponseHandler(_ => throw new HttpRequestException("TLS certificate failure")));
        var result = await new ConnectionHealthService(client).CheckAsync();
        Assert.All(result.Targets, target =>
        {
            Assert.False(target.Passed);
            Assert.Null(target.HttpStatusCode);
            Assert.Contains("TLS certificate failure", target.Detail);
        });
    }

    [Fact]
    public async Task HealthReportsTimeoutButPropagatesUserCancellation()
    {
        using var client = new HttpClient(new DelayedHandler());
        var service = new ConnectionHealthService(client, TimeSpan.FromMilliseconds(25));
        var result = await service.CheckAsync();
        Assert.All(result.Targets, target =>
        {
            Assert.False(target.Passed);
            Assert.Contains("время", target.Detail, StringComparison.OrdinalIgnoreCase);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancellation.Token));
    }

    [Fact]
    public async Task HistoryPersistsReportAndCountersAfterTheSourceAndUiHaveChanged()
    {
        using var fixture = new HistoryFixture();
        var file = Path.Combine(fixture.Reports, "test_results_example.txt");
        await File.WriteAllTextAsync(file, "YouTube TLS1.3:OK\r\nDiscord TLS1.2:ERROR\r\nРекомендация: general.bat");
        var checks = new List<StrategyCheckProgress> { new("general.bat", StrategyCheckState.Completed, 3, 2, 1, 4, 5) };
        var entry = await fixture.Service.SaveAsync(new("general.bat", file, Cancelled: true), checks);
        checks.Clear();
        File.Delete(file);
        var reopened = new StrategyHistoryService(new ZapretDistribution(fixture.Root), fixture.History);
        var listed = Assert.Single(await reopened.GetHistoryAsync());
        Assert.Equal(entry.Id, listed.Id);
        Assert.True(listed.Cancelled);
        Assert.Equal("general.bat", listed.BestStrategy);
        Assert.Equal(new StrategyCheckProgress("general.bat", StrategyCheckState.Completed, 3, 2, 1, 4, 5), Assert.Single(listed.Checks));
        var details = Assert.IsType<StrategyHistoryDetails>(await reopened.GetDetailsAsync(entry.Id));
        Assert.Contains("Discord TLS1.2:ERROR", details.ReportText);
        Assert.Contains("Рекомендация", details.ReportText);
    }

    [Fact]
    public async Task HistoryRejectsOutsideReportsButStillSavesTheCounters()
    {
        using var fixture = new HistoryFixture();
        var outside = Path.Combine(fixture.Root, "private.txt");
        await File.WriteAllTextAsync(outside, "PRIVATE DATA MUST NOT BE IMPORTED");
        var entry = await fixture.Service.SaveAsync(new(null, outside), [new("general.bat", StrategyCheckState.Failed)]);
        var details = Assert.IsType<StrategyHistoryDetails>(await fixture.Service.GetDetailsAsync(entry.Id));
        Assert.Single(entry.Checks);
        Assert.DoesNotContain("PRIVATE DATA", details.ReportText);
        Assert.Contains("недоступен", entry.ReportStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HistoryHandlesMissingOversizedAndCorruptedFilesWithoutLosingValidEntries()
    {
        using var fixture = new HistoryFixture();
        var missing = await fixture.Service.SaveAsync(new(null, Path.Combine(fixture.Reports, "missing.txt")), []);
        Assert.Contains("отсутствует", missing.ReportStatus, StringComparison.OrdinalIgnoreCase);
        var largeFile = Path.Combine(fixture.Reports, "large.txt");
        await File.WriteAllTextAsync(largeFile, new string('x', 1024 * 1024 + 1));
        var large = await fixture.Service.SaveAsync(new(null, largeFile), []);
        Assert.Contains("размер", large.ReportStatus, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(Path.Combine(fixture.History, Guid.NewGuid().ToString("N") + ".json"), "{ invalid json");
        await File.WriteAllTextAsync(Path.Combine(fixture.History, Guid.NewGuid().ToString("N") + ".json"), new string('x', 2 * 1024 * 1024 + 1));
        Assert.Equal(2, (await fixture.Service.GetHistoryAsync()).Count);
        Assert.Null(await fixture.Service.GetDetailsAsync("..\\private"));
        Assert.Null(await fixture.Service.GetDetailsAsync(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task HistoryRetainsOnlyTheLatestHundredRuns()
    {
        using var fixture = new HistoryFixture();
        var first = await fixture.Service.SaveAsync(new("old.bat", ""), []);
        for (var i = 0; i < 100; i++) await fixture.Service.SaveAsync(new("new.bat", ""), []);
        Assert.Equal(100, (await fixture.Service.GetHistoryAsync()).Count);
        Assert.Null(await fixture.Service.GetDetailsAsync(first.Id));
        Assert.Equal(100, Directory.GetFiles(fixture.History, "*.json").Length);
    }

    [Fact]
    public async Task HistoryKeepsAReadablePrefixWhenAReportExceedsTheTextLimit()
    {
        using var fixture = new HistoryFixture();
        var file = Path.Combine(fixture.Reports, "long.txt");
        await File.WriteAllTextAsync(file, "First completed result\n" + new string('x', 300 * 1024));
        var entry = await fixture.Service.SaveAsync(new("general.bat", file), [new("general.bat", StrategyCheckState.Completed, 1)]);
        var details = Assert.IsType<StrategyHistoryDetails>(await fixture.Service.GetDetailsAsync(entry.Id));
        Assert.Contains("частично", entry.ReportStatus, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("First completed result", details.ReportText);
        Assert.True(details.ReportText.Length < 300 * 1024);
        Assert.Equal(1, Assert.Single(details.Entry.Checks).HttpOk);
    }

    [Fact]
    public async Task CancelledHistorySaveDoesNotCreateAnEntry()
    {
        using var fixture = new HistoryFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SaveAsync(new(null, ""), [], cancellation.Token));
        Assert.Empty(await fixture.Service.GetHistoryAsync());
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class HistoryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ZapretHistory-tests-" + Guid.NewGuid().ToString("N"));
        public string Reports => Path.Combine(Root, "utils", "test results");
        public string History => Path.Combine(Root, "history");
        public StrategyHistoryService Service { get; }
        public HistoryFixture()
        {
            Directory.CreateDirectory(Reports);
            Service = new(new ZapretDistribution(Root), History);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
