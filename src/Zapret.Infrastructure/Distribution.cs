using System.Text.RegularExpressions;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class ZapretDistribution(string root) : IZapretDistribution
{
    public string Root { get; } = Path.GetFullPath(root);
    public string BinDirectory => Path.Combine(Root, "bin");
    public string ListsDirectory => Path.Combine(Root, "lists");
    public string? Version
    {
        get
        {
            var file = Path.Combine(Root, ".service", "version.txt");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            file = Path.Combine(Root, "service.bat");
            if (!File.Exists(file)) return null;
            return Regex.Match(File.ReadAllText(file), "LOCAL_VERSION=([0-9.]+)").Groups[1].Value is { Length: > 0 } version ? version : null;
        }
    }
    public IReadOnlyList<string> Validate()
    {
        var missing = new List<string>();
        foreach (var file in new[] { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys", "service.bat", "lists/list-general.txt" })
            if (!File.Exists(Path.Combine(Root, file))) missing.Add(file);
        if (!Directory.Exists(ListsDirectory)) missing.Add("lists/");
        if (!Directory.Exists(Root) || !Directory.EnumerateFiles(Root, "general*.bat").Any()) missing.Add("general*.bat");
        return missing;
    }
}

public static class SafePaths
{
    public static string RequireDirectFile(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path is outside the distribution.");
        for (var current = Path.GetDirectoryName(full); current is not null && current.Length >= fullRoot.Length - 1; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse points are not allowed in distribution paths.");
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("Distribution file is missing or linked.", full);
        return full;
    }
}
