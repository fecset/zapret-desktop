#if UI_PREVIEW
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Zapret.Desktop.Tests;

// Explicit opt-in native host. Scenarios use temporary files and isolated services.
internal static class NativeUiPreview
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<NativePreviewApp>()
        .UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}

internal sealed class NativePreviewApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new PreviewGalleryWindow(args.Contains("--light"), !args.Contains("--large"));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
#endif
