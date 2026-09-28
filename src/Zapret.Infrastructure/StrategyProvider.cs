using System.Text;
using System.Text.RegularExpressions;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class BatStrategyProvider(IZapretDistribution distribution) : IStrategyProvider
{
    private readonly List<string> errors = [];
    public IReadOnlyList<string> Errors => errors;
    public async Task<IReadOnlyList<ZapretStrategy>> GetStrategiesAsync(CancellationToken cancellationToken = default)
    {
        errors.Clear();
        var files = Directory.EnumerateFiles(distribution.Root, "general*.bat", SearchOption.TopDirectoryOnly)
            .OrderBy(x => NaturalKey(Path.GetFileName(x)), StringComparer.OrdinalIgnoreCase).ToArray();
        var strategies = new List<ZapretStrategy>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SafePaths.RequireDirectFile(distribution.Root, file);
                var source = await File.ReadAllTextAsync(file, cancellationToken);
                var name = Path.GetFileNameWithoutExtension(file);
                var args = BatStrategyParser.Parse(source, distribution.Root, file,
                    await ReadGameFilterAsync(cancellationToken));
                strategies.Add(new(name.ToLowerInvariant(), name, name.Replace("general", "General", StringComparison.OrdinalIgnoreCase),
                    file, args, $"{args.Count} {ParameterWord(args.Count)} winws", name.Contains("EXP", StringComparison.OrdinalIgnoreCase),
                    name.Contains("FAKE TLS", StringComparison.OrdinalIgnoreCase), name.Contains("SIMPLE FAKE", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return strategies;
    }
    private async Task<GameFilterSettings> ReadGameFilterAsync(CancellationToken ct)
    {
        var filters = new FilterService(distribution);
        return await filters.GetGameFilterAsync(ct);
    }
    private static string NaturalKey(string name) => Regex.Replace(name, @"\d+", m => m.Value.PadLeft(8, '0'));
    private static string ParameterWord(int count)
    {
        if (count % 100 is >= 11 and <= 14) return "параметров";
        return (count % 10) switch
        {
            1 => "параметр",
            >= 2 and <= 4 => "параметра",
            _ => "параметров"
        };
    }
}

public static class BatStrategyParser
{
    public static IReadOnlyList<string> Parse(string source, string root, string sourceFile, GameFilterSettings game)
    {
        SafePaths.RequireDirectFile(root, sourceFile);
        var lines = source.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, x => x.TrimStart().StartsWith("start ", StringComparison.OrdinalIgnoreCase) && x.Contains("winws.exe", StringComparison.OrdinalIgnoreCase));
        if (start < 0) throw new FormatException("No winws invocation found.");
        var command = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var continues = line.EndsWith('^');
            command.Append(continues ? line[..^1] : line).Append(' ');
            if (!continues) break;
            if (i == lines.Length - 1) throw new FormatException("Unfinished batch continuation.");
        }
        var raw = command.ToString();
        var exeEnd = raw.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0) throw new FormatException("Unsupported winws invocation.");
        raw = raw[(exeEnd + "winws.exe\"".Length)..];
        var bin = Path.Combine(root, "bin") + Path.DirectorySeparatorChar;
        var lists = Path.Combine(root, "lists") + Path.DirectorySeparatorChar;
        raw = raw.Replace("%BIN%", bin, StringComparison.OrdinalIgnoreCase)
            .Replace("%LISTS%", lists, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterTCP%", game.Mode is GameFilterMode.All or GameFilterMode.Tcp ? game.TcpPorts : "12", StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", game.Mode is GameFilterMode.All or GameFilterMode.Udp ? game.UdpPorts : "12", StringComparison.OrdinalIgnoreCase)
            .Replace("^!", "!");
        if (raw.Contains('%') || raw.Contains('^') || raw.Contains('&') || raw.Contains('|') || raw.Contains('>') || raw.Contains('<'))
            throw new FormatException("Unsupported batch expansion or shell operator in strategy.");
        var tokens = Tokenize(raw);
        if (tokens.Count == 0 || tokens.Any(x => !x.StartsWith("--", StringComparison.Ordinal)))
            throw new FormatException("Unsupported winws argument form.");
        foreach (var arg in tokens)
        {
            var value = arg[(arg.IndexOf('=') + 1)..];
            if (Path.IsPathFullyQualified(value)) SafePaths.RequireDirectFile(root, value);
        }
        return tokens;
    }

    public static IReadOnlyList<string> Tokenize(string command)
    {
        var output = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in command)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { output.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (quoted) throw new FormatException("Unclosed quote in strategy.");
        if (current.Length > 0) output.Add(current.ToString());
        return output;
    }
}
