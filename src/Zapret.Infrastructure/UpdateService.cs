using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class GitHubUpdateService(IZapretDistribution distribution, HttpClient client) : IUpdateService
{
    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("html_url")] string Url);
    public async Task<UpdateInfo> CheckUpstreamAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest");
        request.Headers.UserAgent.ParseAdd("ZapretDesktop/0.1");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken) ??
            throw new InvalidOperationException("Invalid GitHub release response.");
        var local = distribution.Version ?? "unknown";
        var latest = release.Tag.TrimStart('v', 'V');
        var available = Version.TryParse(local, out var installed) && Version.TryParse(latest, out var remote)
            ? remote > installed : !string.Equals(local, latest, StringComparison.OrdinalIgnoreCase);
        return new("zapret upstream", local, latest, release.Url, available);
    }
}
