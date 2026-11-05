using System.Net;
using System.Security.Cryptography;

namespace Zapret.Core;

public enum TelegramProxyState { Stopped, Starting, Running, Stopping, Error }
public sealed record TelegramProxySnapshot(TelegramProxyState State, int Port = 1443,
    DateTimeOffset? StartedAt = null, int ActiveConnections = 0, long TotalConnections = 0,
    long BytesSent = 0, long BytesReceived = 0, string? Routes = null, string? LastError = null,
    long ConnectionErrors = 0, long RejectedConnections = 0, string? LastConnectionError = null, DateTimeOffset? LastConnectionErrorAt = null);

public sealed record TelegramProxyDiagnostic(string Level, string Message);

public interface ITelegramProxy : IAsyncDisposable
{
    TelegramProxySnapshot Snapshot { get; }
    event Action? Changed;
    event Action<TelegramProxyDiagnostic>? Diagnostic;
    Task StartAsync(TelegramProxyOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(TelegramProxyOptions options, CancellationToken cancellationToken = default);
}
public interface ITelegramProxySettingsStore
{
    Task<TelegramProxyOptions?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TelegramProxyOptions options, CancellationToken cancellationToken = default);
}

// Configuration lives in Core so storage and UI do not own transport logic.
public sealed record TelegramProxyOptions
{
    public int Port { get; init; } = 1443;
    public string Secret { get; init; } = "";
    public bool AutoStart { get; init; }
    public bool VerboseLogging { get; init; }
    public bool CfProxy { get; init; } = true;
    public bool Http2Media { get; init; } = true;
    public bool DirectTcp { get; init; } = true;
    public Dictionary<int, string> DcOverrides { get; init; } = new() { [2] = "149.154.167.220", [4] = "149.154.167.220" };
    public string[] CfDomains { get; init; } = [];
    public string[] WorkerDomains { get; init; } = [];
    public int MaxConnections { get; init; } = 64;
    public int BufferSize { get; init; } = 64 * 1024;
    public int MaxPacketSize { get; init; } = 4 * 1024 * 1024;
    public int ConnectTimeoutSeconds { get; init; } = 8;
    public int IdleTimeoutSeconds { get; init; } = 300;
    public static string GenerateSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public TelegramProxyOptions Copy() => this with { DcOverrides = new(DcOverrides), CfDomains = [.. CfDomains], WorkerDomains = [.. WorkerDomains] };
    public void Validate()
    {
        if (Port is < 1 or > 65535) throw new ArgumentException("Порт должен быть от 1 до 65535.");
        if (Secret is null || Secret.Length != 32 || !Secret.All(Uri.IsHexDigit))
            throw new ArgumentException("Ключ должен содержать 32 шестнадцатеричных символа.");
        if (MaxConnections is < 1 or > 256 || BufferSize is < 4096 or > 262144 ||
            MaxPacketSize is < 4096 or > 16 * 1024 * 1024 || ConnectTimeoutSeconds is < 1 or > 30 ||
            IdleTimeoutSeconds is < 10 or > 3600) throw new ArgumentException("Недопустимые ограничения ресурсов прокси.");
        if (DcOverrides is null || DcOverrides.Count > 6 || DcOverrides.Any(x =>
            x.Key is not (1 or 2 or 3 or 4 or 5 or 203) || !IPAddress.TryParse(x.Value, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) || ip.GetAddressBytes()[0] >= 224))
            throw new ArgumentException("DC → IP: допустимы DC 1–5 и 203 с IPv4-адресом upstream.");
        if (CfDomains is null || WorkerDomains is null || CfDomains.Length > 32 || WorkerDomains.Length > 16 ||
            CfDomains.Concat(WorkerDomains).Any(x => !ValidDomain(x)))
            throw new ArgumentException("Укажите домены без схемы, пути и порта.");
    }
    private static bool ValidDomain(string? value) => value is { Length: > 2 and <= 253 } &&
        value.Contains('.') && !IPAddress.TryParse(value, out _) && value.Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    public string ConnectionLink()
    {
        Validate();
        return $"tg://proxy?server=127.0.0.1&port={Port}&secret=dd{Secret.ToLowerInvariant()}";
    }
    public override string ToString() => $"TelegramProxyOptions (127.0.0.1:{Port}, secret hidden)";
}
