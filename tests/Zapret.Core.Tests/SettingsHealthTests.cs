using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class SettingsHealthTests
{
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
