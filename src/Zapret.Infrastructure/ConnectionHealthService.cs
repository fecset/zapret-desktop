using System.Diagnostics;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class ConnectionHealthService : IConnectionHealthService
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    internal static readonly string[] Hosts = ["www.youtube.com", "discord.com"];

    public ConnectionHealthService() : this(SharedClient) { }

    public ConnectionHealthService(HttpClient client, TimeSpan? timeout = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.timeout = timeout ?? TimeSpan.FromSeconds(8);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedAt = DateTimeOffset.UtcNow;
        var results = await Task.WhenAll(
            CheckTargetAsync("YouTube", "https://www.youtube.com/", cancellationToken),
            CheckTargetAsync("Discord", "https://discord.com/api/v10/gateway", cancellationToken));
        return new(checkedAt, results);
    }

    private async Task<ConnectionTargetResult> CheckTargetAsync(string name, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ZapretDesktop-Connectivity/1.0");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var code = (int)response.StatusCode;
            return new(name, url, response.IsSuccessStatusCode, code, timer.Elapsed,
                response.IsSuccessStatusCode ? $"HTTPS: HTTP {code}, сертификат TLS проверен." : $"HTTPS ответил HTTP {code} ({response.ReasonPhrase}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(name, url, false, null, timer.Elapsed, "Превышено время ожидания HTTP/TLS.");
        }
        catch (HttpRequestException ex)
        {
            return new(name, url, false, null, timer.Elapsed, "Ошибка HTTP/TLS: " + ex.Message);
        }
    }
}
