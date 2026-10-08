using System.Text.Json;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class JsonSettingsStore(string file) : ISettingsStore, ISettingsHealth
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string? LastWarning { get; private set; }
    public async Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        LastWarning = null;
        if (!File.Exists(file)) return new DesktopSettings();
        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<DesktopSettings>(stream, Options, cancellationToken) ?? new DesktopSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastWarning = "Не удалось прочитать настройки. Использованы значения по умолчанию: " + ex.Message;
            if (ex is JsonException)
            {
                try
                {
                    var backup = file + ".corrupt-" + Guid.NewGuid().ToString("N");
                    File.Copy(file, backup);
                    LastWarning += " Исходный файл сохранён: " + backup;
                }
                catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
                {
                    LastWarning += " Не удалось сохранить копию: " + copyError.Message;
                }
            }
            return new DesktopSettings();
        }
    }
    public Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default) =>
        AtomicFiles.WriteTextAsync(file, JsonSerializer.Serialize(settings, Options), cancellationToken);
}
