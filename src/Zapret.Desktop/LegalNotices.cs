using System.Text;

namespace Zapret.Desktop;

internal static class LegalNotices
{
    private static readonly (string Title, string Resource)[] Documents =
    [
        ("Zapret Desktop — MIT", "Legal.ZapretDesktop.LICENSE"),
        ("Сторонние компоненты", "Legal.THIRD_PARTY_NOTICES.md"),
        ("zapret — лицензия и авторы", "Legal.Zapret.LICENSE.txt"),
        ("Avalonia — MIT", "Legal.Avalonia-LICENSE.md"),
        (".NET Runtime — MIT", "Legal.DotNet-LICENSE.txt"),
        ("Fluent UI System Icons — MIT", "Legal.FluentIcons-LICENSE.txt"),
        ("Inter — SIL OFL 1.1", "Legal.Inter-OFL.txt"),
        ("WinDivert — LGPLv3 / GPLv2", "Legal.WinDivert-LICENSE.txt")
    ];

    public static string ReadAll()
    {
        var assembly = typeof(LegalNotices).Assembly;
        var text = new StringBuilder();
        foreach (var (title, resource) in Documents)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Не найден встроенный текст лицензии: {resource}");
            using var reader = new StreamReader(stream);
            if (text.Length > 0) text.AppendLine().AppendLine();
            text.AppendLine(title).AppendLine(new string('═', 64));
            text.Append(reader.ReadToEnd());
        }
        return text.ToString();
    }
}
