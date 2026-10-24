using CommunityToolkit.Mvvm.ComponentModel;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class SettingsDraft : ObservableObject
{
    [ObservableProperty] private GameFilterMode gameMode;
    [ObservableProperty] private string tcpPorts = "1024-65535";
    [ObservableProperty] private string udpPorts = "1024-65535";
    [ObservableProperty] private IpSetMode ipSetMode;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool startZapretOnLaunch;
    [ObservableProperty] private bool minimizeToTray = true;
    [ObservableProperty] private bool checkUpdates = true;
    [ObservableProperty] private bool checkConnectionAfterStart = true;
    [ObservableProperty] private string theme = "System";

    public GameFilterSettings GameFilter => new(GameMode, TcpPorts, UdpPorts);

    public void Load(DesktopSettings settings, GameFilterSettings game, IpSetMode ipset)
    {
        SetGame(game);
        IpSetMode = ipset;
        StartWithWindows = settings.StartWithWindows;
        StartZapretOnLaunch = settings.StartZapretOnLaunch;
        MinimizeToTray = settings.MinimizeToTray;
        CheckUpdates = settings.CheckUpdates;
        CheckConnectionAfterStart = settings.CheckConnectionAfterStart;
        Theme = settings.Theme;
    }

    public void SetGame(GameFilterSettings game)
    {
        GameMode = game.Mode;
        TcpPorts = game.TcpPorts;
        UdpPorts = game.UdpPorts;
    }

    public DesktopSettings ToSettings(DesktopSettings current) => new()
    {
        SelectedStrategyId = current.SelectedStrategyId, LogLevel = current.LogLevel,
        StartWithWindows = StartWithWindows, StartZapretOnLaunch = StartZapretOnLaunch,
        MinimizeToTray = MinimizeToTray, CheckUpdates = CheckUpdates,
        CheckConnectionAfterStart = CheckConnectionAfterStart, Theme = Theme
    };
}
