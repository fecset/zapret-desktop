using System.Text.Json;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class JsonSettingsStore(string file) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public async Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(file)) return new DesktopSettings();
        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<DesktopSettings>(stream, Options, cancellationToken) ?? new DesktopSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DesktopSettings();
        }
    }
    public Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default) =>
        AtomicFiles.WriteTextAsync(file, JsonSerializer.Serialize(settings, Options), cancellationToken);
}
