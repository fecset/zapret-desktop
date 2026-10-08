namespace Zapret.Core;

public sealed record ConnectionTargetResult(string Name, string Url, bool Passed,
    int? HttpStatusCode, TimeSpan Duration, string Detail);

public sealed record ConnectionHealthResult(DateTimeOffset CheckedAt,
    IReadOnlyList<ConnectionTargetResult> Targets);

public interface IConnectionHealthService
{
    Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record StrategyHistoryEntry(string Id, DateTimeOffset CreatedAt,
    string? BestStrategy, bool Cancelled, string ResultFile, string ReportStatus,
    IReadOnlyList<StrategyCheckProgress> Checks);

public sealed record StrategyHistoryDetails(StrategyHistoryEntry Entry, string ReportText);

public interface IStrategyHistoryService
{
    Task<StrategyHistoryEntry> SaveAsync(StrategyTestResult result,
        IReadOnlyList<StrategyCheckProgress> checks, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StrategyHistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task<StrategyHistoryDetails?> GetDetailsAsync(string id, CancellationToken cancellationToken = default);
}
