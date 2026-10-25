using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class SettingsSaveTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedSettingsTransactionRestoresStartupStateAndCanBeRetried(bool failSavingSettings)
    {
        var store = new FailingSettingsStore { FailNextSave = failSavingSettings };
        var startup = new StartupState { FailEnablingAfterChange = !failSavingSettings };
        var viewModel = CreateViewModel(store, startup);
        viewModel.Draft.StartWithWindows = true;
        viewModel.Draft.Theme = "Light";

        await viewModel.SaveSettingsAsync();

        Assert.False(startup.Enabled);
        Assert.False(viewModel.Settings.StartWithWindows);
        Assert.False(viewModel.Draft.StartWithWindows);
        Assert.Equal("System", viewModel.Settings.Theme);
        Assert.Equal("Light", viewModel.Draft.Theme);
        Assert.True(viewModel.MessageIsError);

        startup.FailEnablingAfterChange = false;
        viewModel.Draft.StartWithWindows = true;
        await viewModel.SaveSettingsAsync();

        Assert.True(startup.Enabled);
        Assert.True(store.Saved.StartWithWindows);
        Assert.True(viewModel.Settings.StartWithWindows);
        Assert.True(viewModel.Draft.StartWithWindows);
        Assert.Equal("Light", store.Saved.Theme);
        Assert.False(viewModel.MessageIsError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedStartupRollbackShowsActualStateAndAllowsSavingAgain(bool retryEnabled)
    {
        var store = new FailingSettingsStore();
        var startup = new StartupState { FailDisabling = true };
        var viewModel = CreateViewModel(store, startup);
        viewModel.Draft.StartWithWindows = true;

        await viewModel.SaveSettingsAsync();

        Assert.True(startup.Enabled);
        Assert.True(viewModel.Settings.StartWithWindows);
        Assert.True(viewModel.Draft.StartWithWindows);
        Assert.False(store.Saved.StartWithWindows);
        Assert.True(viewModel.MessageIsError);
        Assert.Contains("autostart rollback failed", viewModel.Message);

        startup.FailDisabling = false;
        viewModel.Draft.StartWithWindows = retryEnabled;
        await viewModel.SaveSettingsAsync();

        Assert.Equal(retryEnabled, startup.Enabled);
        Assert.Equal(retryEnabled, store.Saved.StartWithWindows);
        Assert.Equal(retryEnabled, viewModel.Settings.StartWithWindows);
        Assert.Equal(retryEnabled, viewModel.Draft.StartWithWindows);
        Assert.Equal(2, store.SaveAttempts);
        Assert.False(viewModel.MessageIsError);
    }

    private static MainViewModel CreateViewModel(ISettingsStore store, IStartupService startup) => new(
        distribution: null!, strategies: null!, process: null!, service: null!, filters: null!,
        diagnostics: null!, lists: null!, updates: null!, tester: null!, settingsStore: store,
        startup: startup, fakes: null!, privilege: null!, logger: NullLogger<MainViewModel>.Instance);

    private sealed class FailingSettingsStore : ISettingsStore
    {
        public DesktopSettings Saved { get; private set; } = new();
        public int SaveAttempts { get; private set; }
        public bool FailNextSave { get; set; } = true;
        public Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("settings save failed");
            }
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class StartupState : IStartupService
    {
        public bool Enabled { get; private set; }
        public bool FailDisabling { get; set; }
        public bool FailEnablingAfterChange { get; set; }
        public bool IsEnabled() => Enabled;
        public void SetEnabled(bool enabled)
        {
            if (!enabled && FailDisabling) throw new IOException("autostart rollback failed");
            Enabled = enabled;
            if (enabled && FailEnablingAfterChange) throw new IOException("autostart registration failed after changing state");
        }
        public bool MigrateLegacyRegistration() => false;
    }
}
