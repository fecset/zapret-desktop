namespace Zapret.Core;

public sealed record UserDataBackupInfo(string Id, DateTimeOffset CreatedAt, string Reason,
    int FileCount, long SizeBytes);

public interface IUserDataBackupService
{
    Task<UserDataBackupInfo> CreateAsync(string reason = "manual", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserDataBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(Stream destination, CancellationToken cancellationToken = default);
    Task ImportAsync(Stream source, CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, CancellationToken cancellationToken = default);
}

public sealed record PayloadInspection(string TargetName, string? SourceName, string? TargetSha256,
    string? SourceSha256, string? OriginalSha256, bool IsOriginal, bool CanRestore)
{
    public bool MatchesSource => TargetSha256 is not null && SourceSha256 is not null &&
        string.Equals(TargetSha256, SourceSha256, StringComparison.OrdinalIgnoreCase);
}

public interface IPayloadRecoveryService
{
    Task<PayloadInspection> InspectAsync(string targetName, string? sourceName = null,
        CancellationToken cancellationToken = default);
    Task RestoreOriginalAsync(string targetName, CancellationToken cancellationToken = default);
}
