using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zapret.Core;
namespace Zapret.Infrastructure;

public sealed class TelegramProxySettingsStore(string path) : ITelegramProxySettingsStore
{
    private readonly SemaphoreSlim gate = new(1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZapretDesktop.TelegramProxy.v1");
    private sealed record Stored(int Version, TelegramProxyOptions Options, string ProtectedSecret);
    public async Task<TelegramProxyOptions?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            try
            {
                var info = new FileInfo(path); if (info.Length > 65536) throw new InvalidDataException();
                var stored = JsonSerializer.Deserialize<Stored>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                if (stored is null || stored.Version != 1 || stored.Options is null || string.IsNullOrEmpty(stored.ProtectedSecret)) throw new InvalidDataException();
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedSecret), Entropy, DataProtectionScope.CurrentUser);
                try { var options = stored.Options with { Secret = Convert.ToHexStringLower(bytes) }; options.Validate(); return options; }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException or ArgumentException or InvalidDataException)
            { throw new InvalidDataException("Настройки Telegram Proxy повреждены или ключ принадлежит другому пользователю Windows. Сохранённый файл telegram-proxy.json оставлен без изменений."); }
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(TelegramProxyOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = Convert.FromHexString(options.Secret);
            try
            {
                var protectedSecret = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
                await AtomicFiles.WriteTextAsync(path, JsonSerializer.Serialize(new Stored(1, options.Copy() with { Secret = "" }, Convert.ToBase64String(protectedSecret)),
                    new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { gate.Release(); }
    }
}
