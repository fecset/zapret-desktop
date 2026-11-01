using Zapret.Core;

namespace Zapret.Desktop;

public static class WindowStartup
{
    public static Task StartAsync(MainWindow window, DesktopSettings settings, IReadOnlyList<string> arguments)
    {
        if (settings.StartWithWindows && settings.StartInTray && arguments.Contains("--autostart", StringComparer.Ordinal))
            return window.InitializeAsync();
        window.Show();
        return Task.CompletedTask;
    }
}
