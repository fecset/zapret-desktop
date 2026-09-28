using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class ListService(IZapretDistribution distribution) : IListService
{
    public IReadOnlyList<string> AvailableFiles => Directory.Exists(distribution.ListsDirectory)
        ? Directory.EnumerateFiles(distribution.ListsDirectory, "*.txt", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];
    private string Resolve(string fileName)
    {
        if (!AvailableFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Неизвестный файл списка.");
        var path = Path.Combine(distribution.ListsDirectory, fileName);
        return SafePaths.RequireDirectFile(distribution.Root, path);
    }
    public async Task<IReadOnlyList<string>> SearchAsync(string fileName, string query, int limit = 500, CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        using var stream = new FileStream(Resolve(fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Contains(query, StringComparison.OrdinalIgnoreCase)) result.Add(line);
            if (result.Count >= limit) break;
        }
        return result;
    }
    public async Task AddEntryAsync(string fileName, string entry, CancellationToken cancellationToken = default)
    {
        var path = Resolve(fileName);
        entry = ValidateEntry(fileName, entry);
        var original = await File.ReadAllTextAsync(path, cancellationToken);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Any(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase))) return;
        await WriteLinesAsync(path, original, lines.Append(entry), cancellationToken);
    }
    public async Task RemoveEntryAsync(string fileName, string entry, CancellationToken cancellationToken = default)
    {
        var path = Resolve(fileName);
        var original = await File.ReadAllTextAsync(path, cancellationToken);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        entry = entry.Trim();
        if (entry.Length == 0) throw new ArgumentException("Выберите запись или введите домен/IP-адрес для удаления.");
        var exact = lines.Any(x => x.Trim().Equals(entry, StringComparison.OrdinalIgnoreCase));
        if (!exact) entry = ValidateEntry(fileName, entry);
        if (!lines.Any(x => x.Trim().Equals(entry, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Запись не найдена в выбранном файле.");
        await WriteLinesAsync(path, original, lines.Where(x => !x.Trim().Equals(entry, StringComparison.OrdinalIgnoreCase)), cancellationToken);
    }
    public void OpenFolder() => Process.Start(new ProcessStartInfo(distribution.ListsDirectory) { UseShellExecute = true });
    private static Task WriteLinesAsync(string path, string original, IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = string.Join(newline, lines);
        if (output.Length > 0 && (original.Length == 0 || original.EndsWith('\n'))) output += newline;
        return AtomicFiles.WriteTextAsync(path, output, cancellationToken);
    }
    private static string ValidateEntry(string fileName, string value) =>
        fileName.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase) ? ValidateIpAddress(value) : ValidateDomain(value);
    private static string ValidateIpAddress(string value)
    {
        var parts = value.Trim().Split('/');
        if (parts.Length is < 1 or > 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var address))
            throw new ArgumentException("Введите IP-адрес или подсеть, например 1.2.3.4/24.");
        var result = address.ToString().ToLowerInvariant();
        if (parts.Length == 1) return result;
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix > maxPrefix)
            throw new ArgumentException($"Префикс подсети должен быть от 0 до {maxPrefix}.");
        return $"{result}/{prefix}";
    }
    private static string ValidateDomain(string value)
    {
        var raw = value.Trim();
        var prefix = raw.StartsWith('^') ? "^" : "";
        if (prefix.Length > 0) raw = raw[1..];
        if (IPAddress.TryParse(raw, out _))
            throw new ArgumentException("Для этого файла нужен домен; IP-адрес добавьте в файл ipset.");
        string domain;
        try { domain = new IdnMapping().GetAscii(raw).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ArgumentException("Введите корректный домен, например example.com."); }
        if (domain.Length > 253 || !Regex.IsMatch(domain, @"^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+$"))
            throw new ArgumentException("Введите корректный домен, например example.com.");
        return prefix + domain;
    }
}
