namespace Zapret.Core;

public sealed record ZapretStrategy(string Id, string Name, string DisplayName,
    string SourceFile, IReadOnlyList<string> Arguments, string Description,
    bool IsExperimental, bool IsFakeTls, bool IsSimpleFake);

public enum ZapretRunState { Stopped, Running, External, ServiceRunning }
public sealed record ZapretStatus(ZapretRunState State, int? ProcessId, DateTimeOffset? StartedAt,
    string? StrategyId, string DriverStatus, string ServiceStatus)
{
    public bool DriverRunning => DriverStatus == "Running";
}
public enum GameFilterMode { Disabled, All, Tcp, Udp }
public sealed record GameFilterSettings(GameFilterMode Mode, string TcpPorts, string UdpPorts);
public enum IpSetMode { None, Loaded, Any }
public sealed class DesktopSettings
{
    public string? SelectedStrategyId { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartZapretOnLaunch { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public string Theme { get; set; } = "System";
    public string LogLevel { get; set; } = "Information";
}

public interface IZapretDistribution
{
    string Root { get; }
    string BinDirectory { get; }
    string ListsDirectory { get; }
    string? Version { get; }
    IReadOnlyList<string> Validate();
}
public interface IStrategyProvider
{
    IReadOnlyList<string> Errors { get; }
    Task<IReadOnlyList<ZapretStrategy>> GetStrategiesAsync(CancellationToken cancellationToken = default);
}
public interface IZapretProcessManager
{
    Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default);
    Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default);
    Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default);
    Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default);
    event Action<string, string>? OutputReceived;
}
public interface IPrivilegeService
{
    bool IsAdministrator { get; }
    void RestartElevated();
}
public interface ISettingsStore
{
    Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default);
}
public interface IFilterService
{
    Task<GameFilterSettings> GetGameFilterAsync(CancellationToken cancellationToken = default);
    Task SetGameFilterAsync(GameFilterSettings value, CancellationToken cancellationToken = default);
    Task<IpSetMode> GetIpSetModeAsync(CancellationToken cancellationToken = default);
    Task SetIpSetModeAsync(IpSetMode mode, CancellationToken cancellationToken = default);
}
public interface IServiceManager
{
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<bool> GetAutoStartAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default);
    Task RemoveAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
    Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default);
}
public sealed record DiagnosticItem(string Name, bool Passed, string Detail, string Solution);
public interface IDiagnosticService
{
    Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default);
}
public interface IListService
{
    IReadOnlyList<string> AvailableFiles { get; }
    Task<IReadOnlyList<string>> SearchAsync(string fileName, string query, int limit = 500, CancellationToken cancellationToken = default);
    Task AddEntryAsync(string fileName, string entry, CancellationToken cancellationToken = default);
    Task RemoveEntryAsync(string fileName, string entry, CancellationToken cancellationToken = default);
    void OpenFolder();
}
public sealed record UpdateInfo(string Product, string LocalVersion, string LatestVersion, string ReleaseUrl, bool Available);
public interface IUpdateService
{
    Task<UpdateInfo> CheckUpstreamAsync(CancellationToken cancellationToken = default);
    Task<UpdateInfo> CheckDesktopAsync(string localVersion, CancellationToken cancellationToken = default);
}
public sealed record StrategyTestResult(string? BestStrategy, string ResultFile, bool Cancelled = false);
public interface IStrategyTester
{
    Task<StrategyTestResult> RunAsync(IProgress<string> progress, CancellationToken cancellationToken = default);
}
public interface IStartupService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
public interface IFakePayloadService
{
    IReadOnlyList<string> GetAvailablePayloads();
    Task ReplaceAsync(string targetName, string sourceName, CancellationToken cancellationToken = default);
}
