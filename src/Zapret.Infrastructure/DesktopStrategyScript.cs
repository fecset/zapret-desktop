using System.Text;

namespace Zapret.Infrastructure;

internal sealed class DesktopStrategyScript : IDisposable
{
    public string Path { get; }
    private readonly DirectoryInfo directory;

    private DesktopStrategyScript(DirectoryInfo directory)
    {
        this.directory = directory;
        Path = System.IO.Path.Combine(directory.FullName, "test.ps1");
    }

    internal static async Task<string> ReadSourceAsync(CancellationToken cancellationToken)
    {
        using var stream = typeof(DesktopStrategyScript).Assembly.GetManifestResourceStream(
            "Zapret.Infrastructure.DesktopStrategyTest.ps1")
            ?? throw new InvalidOperationException("В приложении отсутствует скрипт автоподбора стратегий.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    internal static async Task<DesktopStrategyScript> CreateAsync(string source, CancellationToken cancellationToken)
    {
        var script = new DesktopStrategyScript(Directory.CreateTempSubdirectory("ZapretDesktop-test-"));
        try
        {
            // Windows PowerShell 5.1 needs a BOM to read the script's Unicode text reliably.
            await File.WriteAllTextAsync(script.Path, source, new UTF8Encoding(true), cancellationToken);
            return script;
        }
        catch
        {
            script.Dispose();
            throw;
        }
    }

    public void Dispose() => directory.Delete(recursive: true);
}
