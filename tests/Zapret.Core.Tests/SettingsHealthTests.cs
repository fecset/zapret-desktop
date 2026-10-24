using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class SettingsHealthTests
{
    [Fact]
    public async Task OlderSettingsKeepAutomaticConnectionChecksEnabled()
    {
        var file = Path.Combine(Path.GetTempPath(), "Zapret-old-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(file, "{\"Theme\":\"Dark\",\"CheckUpdates\":false}");
            Assert.True((await new JsonSettingsStore(file).LoadAsync()).CheckConnectionAfterStart);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task CorruptSettingsAreReportedAndPreservedBeforeDefaultsAreSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZapretSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.json");
        try
        {
            await File.WriteAllTextAsync(file, "{broken settings");
            var store = new JsonSettingsStore(file);
            var health = Assert.IsAssignableFrom<ISettingsHealth>(store);
            var defaults = await store.LoadAsync();
            Assert.NotNull(health.LastWarning);
            await store.SaveAsync(defaults);
            var backup = Assert.Single(Directory.EnumerateFiles(root, "settings.json.corrupt-*"));
            Assert.Equal("{broken settings", await File.ReadAllTextAsync(backup));
            await store.LoadAsync();
            Assert.Null(health.LastWarning);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
