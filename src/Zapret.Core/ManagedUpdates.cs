namespace Zapret.Core;

public enum ManagedUpdateProduct { Distribution, Desktop }

/// <summary>A downloaded, validated update. Creating this plan never changes installed files.</summary>
public sealed record ManagedUpdatePlan(string Id, ManagedUpdateProduct Product, string Version,
    string ReleaseUrl, string DownloadUrl, long Size, string Sha256, int FileCount,
    string PreparedDirectory, string Summary);

public sealed record ManagedUpdateResult(string Id, string Version, string InstalledDirectory,
    string BackupDirectory, string Summary);

public sealed record DesktopUpdateSchedule(string HelperPath, string LogPath, string BackupDirectory,
    string Summary);

public sealed record IpSetUpdatePlan(string Id, string SourceUrl, string TargetPath, int EntryCount,
    long Size, string Sha256, string Content, string Summary);

/// <summary>Only this proposed managed block is replaced; unrelated hosts entries are retained.</summary>
public sealed record HostsUpdatePreview(string Id, string SourceUrl, string TargetPath,
    string CurrentContent, string ProposedContent, int EntryCount, string Summary);

public sealed record DataUpdateResult(string TargetPath, string? BackupPath, string Summary);

/// <summary>The caller must stop winws, the service and strategy tests before applying an update.</summary>
public interface IManagedUpdateService
{
    Task<ManagedUpdatePlan> PrepareDistributionAsync(CancellationToken cancellationToken = default);
    Task<ManagedUpdatePlan> PrepareDesktopAsync(string localVersion, CancellationToken cancellationToken = default);
    Task<ManagedUpdateResult> ApplyDistributionAsync(ManagedUpdatePlan plan, CancellationToken cancellationToken = default);
    Task RollbackDistributionAsync(ManagedUpdateResult result, CancellationToken cancellationToken = default);
    Task<DesktopUpdateSchedule> ScheduleDesktopUpdateAsync(ManagedUpdatePlan plan, int currentProcessId,
        CancellationToken cancellationToken = default);
    Task<IpSetUpdatePlan> PrepareIpSetAsync(CancellationToken cancellationToken = default);
    Task<DataUpdateResult> ApplyIpSetAsync(IpSetUpdatePlan plan, CancellationToken cancellationToken = default);
    Task<HostsUpdatePreview> PreviewHostsAsync(CancellationToken cancellationToken = default);
    Task<DataUpdateResult> ApplyHostsAsync(HostsUpdatePreview preview, CancellationToken cancellationToken = default);
}
