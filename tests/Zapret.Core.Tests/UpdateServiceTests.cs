using System.Net;
using System.Text;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public class UpdateServiceTests
{
    [Fact]
    public async Task ChecksBothGitHubReleasesAndUsesTheirReleasePages()
    {
        var requested = new List<string>();
        using var client = new HttpClient(new ReleaseHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            Assert.Contains("ZapretDesktop", request.Headers.UserAgent.ToString());
            var desktop = request.RequestUri.AbsolutePath.Contains("fecset", StringComparison.Ordinal);
            var url = desktop
                ? "https://github.com/fecset/zapret-desktop/releases/tag/v0.1.2"
                : "https://github.com/Flowseal/zapret-discord-youtube/releases/tag/v1.10.4";
            var tag = desktop ? "v0.1.2" : "v1.10.4";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"tag_name":"{{tag}}","html_url":"{{url}}"}""", Encoding.UTF8, "application/json")
            };
        }));
        var updates = new GitHubUpdateService(new ZapretDistribution(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../zapret"))), client);

        var upstream = await updates.CheckUpstreamAsync();
        var desktop = await updates.CheckDesktopAsync("0.1.1");

        Assert.True(upstream.Available);
        Assert.Equal("1.10.4", upstream.LatestVersion);
        Assert.Equal("https://github.com/Flowseal/zapret-discord-youtube/releases/tag/v1.10.4", upstream.ReleaseUrl);
        Assert.True(desktop.Available);
        Assert.Equal("0.1.2", desktop.LatestVersion);
        Assert.Equal("https://github.com/fecset/zapret-desktop/releases/tag/v0.1.2", desktop.ReleaseUrl);
        Assert.Contains("https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest", requested);
        Assert.Contains("https://api.github.com/repos/fecset/zapret-desktop/releases/latest", requested);
    }

    [Theory]
    [InlineData("0.1.1", "v0.1.1", false)]
    [InlineData("0.1.2", "v0.1.1", false)]
    [InlineData("unknown", "v0.1.2", false)]
    [InlineData("0.1.1", "v0.1.2", true)]
    public async Task OnlyNewerDesktopVersionsAreAvailable(string local, string tag, bool expected)
    {
        using var client = new HttpClient(new ReleaseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"tag_name":"{{tag}}","html_url":"https://github.com/fecset/zapret-desktop/releases/latest"}""", Encoding.UTF8, "application/json")
        }));
        var updates = new GitHubUpdateService(new ZapretDistribution(Path.GetTempPath()), client);

        Assert.Equal(expected, (await updates.CheckDesktopAsync(local)).Available);
    }

    private sealed class ReleaseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
