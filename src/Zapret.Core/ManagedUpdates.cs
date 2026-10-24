using System.Net;

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
    string CurrentContent, string ProposedContent, int EntryCount, string Summary)
{
    public bool HasChanges => !string.Equals(Normalize(CurrentContent), Normalize(ProposedContent), StringComparison.Ordinal);
    public int AddedCount => CountDifference(ProposedContent, CurrentContent);
    public int RemovedCount => CountDifference(CurrentContent, ProposedContent);

    private static string Normalize(string content) => content.TrimStart('\uFEFF').ReplaceLineEndings("\n").TrimEnd('\n');

    private static int CountDifference(string content, string previous)
    {
        var mappings = Mappings(content);
        mappings.ExceptWith(Mappings(previous));
        return mappings.Count;
    }

    private static HashSet<string> Mappings(string content)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n'))
        {
            var parts = line.Split('#', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address)) continue;
            foreach (var domain in parts.Skip(1)) result.Add(address + " " + domain);
        }
        return result;
    }
}

public sealed record DataUpdateResult(string TargetPath, string? BackupPath, string Summary, bool Changed = true);

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
