using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class StrategyHistoryService : IStrategyHistoryService
{
    private const int MaximumHistory = 100;
    private const int MaximumReportBytes = 1024 * 1024;
    private const int MaximumReportCharacters = 240 * 1024;
    private const int MaximumEntryBytes = 2 * 1024 * 1024;
    private const int MaximumChecks = 256;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly IZapretDistribution distribution;
    private readonly string historyDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public StrategyHistoryService(IZapretDistribution distribution, string? historyDirectory = null)
    {
        this.distribution = distribution;
        this.historyDirectory = Path.GetFullPath(historyDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretDesktop", "history"));
    }

    public async Task<StrategyHistoryEntry> SaveAsync(StrategyTestResult result,
        IReadOnlyList<StrategyCheckProgress> checks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(checks);
        // Copy before the first await: a UI progress collection may change after the test returns.
        var snapshot = checks.Take(MaximumChecks).Select(check => check with { FileName = Clip(check.FileName, 256) }).ToArray();
        var (report, status) = await CaptureReportAsync(result.ResultFile, cancellationToken);
        var entry = new StrategyHistoryEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            result.BestStrategy is null ? null : Clip(result.BestStrategy, 512), result.Cancelled,
            Clip(result.ResultFile, 4096), status, snapshot);
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireUnlinkedDirectory();
            Directory.CreateDirectory(historyDirectory);
            RequireUnlinkedDirectory();
            var json = JsonSerializer.Serialize(new StrategyHistoryDetails(entry, report), JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumEntryBytes)
                throw new IOException("Размер записи истории превышает допустимый предел.");
            await AtomicFiles.WriteTextAsync(Path.Combine(historyDirectory, entry.Id + ".json"), json, cancellationToken);
            PruneHistory();
            return entry;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<StrategyHistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(historyDirectory)) return [];
            RequireUnlinkedDirectory();
            var result = new List<StrategyHistoryEntry>();
            foreach (var file in HistoryFiles().Take(MaximumHistory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ReadDetailsAsync(Path.GetFileNameWithoutExtension(file), cancellationToken) is { } details)
                    result.Add(details.Entry);
            }
            return result.OrderByDescending(entry => entry.CreatedAt).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<StrategyHistoryDetails?> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id)) return null;
        await gate.WaitAsync(cancellationToken);
        try { return await ReadDetailsAsync(id, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<(string Report, string Status)> CaptureReportAsync(string file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file)) return ("", "Файл отчёта не указан; сохранены счётчики проверок.");
        try
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(distribution.Root, "utils", "test results")) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file, distribution.Root);
            if (!full.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                return ("", "Отчёт недоступен: путь находится вне utils/test results.");
            if (!File.Exists(full)) return ("", "Файл отчёта отсутствует; сохранены счётчики проверок.");
            SafePaths.RequireDirectFile(distribution.Root, full);
            await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (input.Length > MaximumReportBytes) return ("", "Размер отчёта превышает 1 МиБ; сохранены счётчики проверок.");
            using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            if (text.Length > MaximumReportCharacters)
                return (text[..MaximumReportCharacters], "Отчёт сохранён частично: превышен размер текста.");
            return (text, "Отчёт сохранён.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return ("", "Отчёт недоступен: " + Clip(ex.Message, 512));
        }
    }

    private async Task<StrategyHistoryDetails?> ReadDetailsAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id)) return null;
        try
        {
            RequireUnlinkedDirectory();
            var file = SafePaths.RequireDirectFile(historyDirectory, Path.Combine(historyDirectory, id + ".json"));
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (input.Length > MaximumEntryBytes) return null;
            var details = await JsonSerializer.DeserializeAsync<StrategyHistoryDetails>(input, JsonOptions, cancellationToken);
            if (details?.Entry is not { } entry || entry.Id != id || entry.CreatedAt == default ||
                entry.Checks is null || entry.Checks.Count > MaximumChecks || details.ReportText is null ||
                details.ReportText.Length > MaximumReportCharacters || entry.ReportStatus is null || entry.ReportStatus.Length > 1024 ||
                entry.ResultFile is null || entry.ResultFile.Length > 4096 || entry.BestStrategy?.Length > 512 ||
                entry.Checks.Any(check => check is null || check.FileName is null || check.FileName.Length > 256 ||
                    !Enum.IsDefined(check.State) || check.HttpOk < 0 || check.HttpFailed < 0 || check.Unsupported < 0 || check.PingOk < 0 || check.PingFailed < 0))
                return null;
            return details;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private IEnumerable<string> HistoryFiles()
    {
        var candidates = new List<(string Path, DateTime Modified)>();
        // A store normally contains at most 100 files. Bound work even if unrelated files were added manually.
        foreach (var file in Directory.EnumerateFiles(historyDirectory, "*.json").Take(1000))
        {
            if (!IsValidId(Path.GetFileNameWithoutExtension(file))) continue;
            try { candidates.Add((SafePaths.RequireDirectFile(historyDirectory, file), File.GetLastWriteTimeUtc(file))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return candidates.OrderByDescending(item => item.Modified).ThenByDescending(item => item.Path, StringComparer.Ordinal).Select(item => item.Path);
    }

    private void PruneHistory()
    {
        foreach (var file in HistoryFiles().Skip(MaximumHistory))
        {
            try { File.Delete(SafePaths.RequireDirectFile(historyDirectory, file)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
    }

    private void RequireUnlinkedDirectory()
    {
        for (var current = historyDirectory; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка истории не должна быть символьной ссылкой.");
    }

    private static bool IsValidId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);
    private static string Clip(string? value, int limit) => string.IsNullOrEmpty(value) ? "" : value.Length <= limit ? value : value[..limit];
}
