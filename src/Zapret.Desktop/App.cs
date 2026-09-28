using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zapret.Core;
using Zapret.Infrastructure;

namespace Zapret.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Zapret.Desktop/"))
        {
            Source = new Uri("avares://Zapret.Desktop/Styles/Dropdowns.axaml")
        });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IZapretDistribution>(new ZapretDistribution(FindDistribution()));
            services.AddSingleton<IPrivilegeService, WindowsPrivilegeService>();
            services.AddSingleton<IServiceManager, ScServiceManager>();
            services.AddSingleton<IFilterService, FilterService>();
            services.AddSingleton<IStrategyProvider, BatStrategyProvider>();
            services.AddSingleton<IZapretProcessManager, ZapretProcessManager>();
            services.AddSingleton<IDiagnosticService, DiagnosticService>();
            services.AddSingleton<IListService, ListService>();
            services.AddSingleton<IStartupService, WindowsStartupService>();
            services.AddSingleton<IFakePayloadService, FakePayloadService>();
            services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
            services.AddSingleton<IUpdateService, GitHubUpdateService>();
            services.AddSingleton<IStrategyTester, PowerShellStrategyTester>();
            services.AddSingleton<ISettingsStore>(new JsonSettingsStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretDesktop", "settings.json")));
            services.AddSingleton<MainViewModel>();
            var provider = services.BuildServiceProvider();
            var vm = provider.GetRequiredService<MainViewModel>();
            var window = new MainWindow(vm);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ConfigureTray(desktop, window, vm);
        }
        base.OnFrameworkInitializationCompleted();
    }
    private void ConfigureTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel vm)
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Zapret.Desktop/Assets/app.ico"));
        var tray = new TrayIcon { Icon = new WindowIcon(iconStream), ToolTipText = "Zapret Desktop" };
        var menu = new NativeMenu();
        var status = new NativeMenuItem { Header = "🔴 Статус: Остановлен", IsEnabled = false };
        var strategy = new NativeMenuItem { Header = "Выбрана: —", IsEnabled = false };
        menu.Items.Add(status);
        menu.Items.Add(strategy);
        menu.Items.Add(new NativeMenuItemSeparator());
        var start = new NativeMenuItem { Header = "Запустить" };
        start.Click += async (_, _) => await vm.StartAsync();
        var stop = new NativeMenuItem { Header = "Остановить" };
        stop.Click += async (_, _) => await vm.StopAsync();
        menu.Items.Add(start);
        menu.Items.Add(stop);
        var removeService = new NativeMenuItem { Header = "Удалить службу zapret" };
        removeService.Click += async (_, _) =>
        {
            await vm.ServiceAsync("remove");
            if (!vm.Message.StartsWith("Ошибка:", StringComparison.Ordinal)) return;
            window.Show();
            window.Activate();
        };
        menu.Items.Add(removeService);
        var choose = new NativeMenuItem { Header = "Сменить стратегию" };
        var choices = new NativeMenu();
        var strategyChoices = new List<(ZapretStrategy Strategy, NativeMenuItem Item)>();
        choose.Menu = choices;
        menu.Items.Add(choose);
        menu.Items.Add(new NativeMenuItemSeparator());
        var open = new NativeMenuItem { Header = "Открыть" };
        open.Click += (_, _) => { window.Show(); window.Activate(); };
        menu.Items.Add(open);
        var exit = new NativeMenuItem { Header = "Выход из приложения" };
        exit.Click += async (_, _) => await window.ExitAsync();
        menu.Items.Add(exit);
        tray.Menu = menu;
        tray.Clicked += (_, _) => { window.Show(); window.Activate(); };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        desktop.Exit += (_, _) =>
        {
            tray.IsVisible = false;
            tray.Dispose();
        };
        void UpdateTray()
        {
            var state = vm.Status?.State ?? ZapretRunState.Stopped;
            var indicator = state switch
            {
                ZapretRunState.Running or ZapretRunState.ServiceRunning => "🟢",
                ZapretRunState.External => "🟠",
                _ => "🔴"
            };
            status.Header = $"{indicator} Статус: {vm.StatusText}";
            strategy.Header = "Выбрана: " + vm.SelectedStrategyName;
            choose.Header = "Сменить стратегию: " + vm.SelectedStrategyName;
            tray.ToolTipText = $"{indicator} Zapret Desktop · {vm.StatusText}";
            start.IsEnabled = !vm.Busy && state == ZapretRunState.Stopped && vm.SelectedStrategy is not null;
            stop.IsEnabled = !vm.Busy && (state is ZapretRunState.Running or ZapretRunState.ServiceRunning);
            removeService.IsEnabled = !vm.Busy && vm.Status?.ServiceStatus is not null and not "NotInstalled";
            foreach (var (item, choice) in strategyChoices)
                choice.IsChecked = item.Id == vm.SelectedStrategy?.Id;
        }
        vm.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(UpdateTray);
        vm.Strategies.CollectionChanged += (_, _) =>
        {
            choices.Items.Clear();
            strategyChoices.Clear();
            foreach (var item in vm.Strategies)
            {
                var choice = new NativeMenuItem { Header = item.DisplayName,
                    ToggleType = MenuItemToggleType.Radio };
                choice.Click += async (_, _) => await vm.SelectAsync(item);
                choices.Items.Add(choice);
                strategyChoices.Add((item, choice));
            }
            UpdateTray();
        };
        UpdateTray();
    }
    private static string FindDistribution()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "zapret");
            if (File.Exists(Path.Combine(candidate, "service.bat"))) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "zapret");
    }
}
