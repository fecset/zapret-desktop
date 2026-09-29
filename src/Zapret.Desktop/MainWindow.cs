using System.ComponentModel;
using System.Collections;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private readonly Bitmap brandImage;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer feedbackTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly StackPanel page = new() { Spacing = 18 };
    private readonly Dictionary<string, Button> navButtons = [];
    private TextBlock footer = new();
    private TextBlock sidebarStatus = new();
    private TextBlock? upstreamUpdateStatus;
    private TextBlock? desktopUpdateStatus;
    private TextBlock? updateNotice;
    private Border? updateNoticePanel;
    private Button? updateCheckButton;
    private Border sidebarStatusDot = new();
    private ScrollViewer contentScroll = new();
    private ContentControl contentHost = new();
    private Grid? fillLayout;
    private string currentPage = "Главная";
    private bool exitAllowed;
    private bool exitInProgress;
    private bool openedOnce;

    private bool IsLight => vm.Settings.Theme == "Light" ||
        vm.Settings.Theme == "System" && Application.Current?.ActualThemeVariant == ThemeVariant.Light;
    private IBrush CanvasBrush => Brush(IsLight ? "#F5F6FA" : "#101115");
    private IBrush SidebarBrush => Brush(IsLight ? "#FFFFFF" : "#131417");
    private IBrush SurfaceBrush => Brush(IsLight ? "#FFFFFF" : "#1A1B20");
    private IBrush SurfaceRaisedBrush => Brush(IsLight ? "#F1F2F7" : "#24262C");
    private IBrush OutlineBrush => Brush(IsLight ? "#E4E5EC" : "#303139");
    private IBrush TextBrush => Brush(IsLight ? "#16171C" : "#F5F5F8");
    private IBrush MutedBrush => Brush(IsLight ? "#60636F" : "#A3A5AF");
    private static IBrush AccentBrush => Brush("#8B6CF6");
    private static IBrush GoodBrush => Brush("#47C98A");
    private static IBrush WarnBrush => Brush("#F3C96C");
    private static IBrush BadBrush => Brush("#F07479");
    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public MainWindow(MainViewModel vm)
    {
        this.vm = vm;
        Title = "Zapret Desktop";
        using (var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Zapret.Desktop/Assets/app.ico")))
            Icon = new WindowIcon(iconStream);
        using (var brandStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Zapret.Desktop/Assets/app.png")))
            brandImage = new Bitmap(brandStream);
        Width = 1180;
        Height = 800;
        MinWidth = 890;
        MinHeight = 620;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        BuildShell();
        vm.PropertyChanged += OnViewModelChanged;
        Opened += async (_, _) =>
        {
            if (openedOnce) return;
            openedOnce = true;
            try { await vm.InitializeAsync(); }
            catch (Exception ex) { vm.Log("ERROR", ex.ToString()); }
            ApplyTheme();
            Show("Главная");
            statusTimer.Start();
            if (vm.Settings.CheckUpdates) await CheckAndOpenUpdatesAsync();
        };
        statusTimer.Tick += async (_, _) => await vm.RefreshAsync();
        feedbackTimer.Tick += (_, _) => { feedbackTimer.Stop(); footer.IsVisible = false; };
        Closed += (_, _) => { statusTimer.Stop(); feedbackTimer.Stop(); brandImage.Dispose(); };
        Closing += (_, e) =>
        {
            if (!exitAllowed && vm.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
            }
            else if (!exitAllowed)
            {
                e.Cancel = true;
                _ = ExitAsync();
            }
        };
    }

    public async Task ExitAsync()
    {
        if (exitInProgress) return;
        exitInProgress = true;
        try
        {
            if (!await vm.StopOwnedForExitAsync())
            {
                Show();
                Activate();
                return;
            }
            exitAllowed = true;
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        finally { exitInProgress = false; }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (e.PropertyName == nameof(MainViewModel.Message))
            {
                footer.Text = vm.Message;
                footer.IsVisible = !string.IsNullOrWhiteSpace(vm.Message);
                feedbackTimer.Stop();
                if (footer.IsVisible) feedbackTimer.Start();
            }
            sidebarStatus.Text = vm.StatusText;
            sidebarStatusDot.Background = CurrentStatusBrush();
            if (e.PropertyName is nameof(MainViewModel.UpstreamUpdate) or nameof(MainViewModel.DesktopUpdate)
                or nameof(MainViewModel.CheckingUpdates) or nameof(MainViewModel.UpdateError))
                RefreshUpdateDisplay();
            if (e.PropertyName == nameof(MainViewModel.SelectedStrategy) && currentPage is "Главная" or "Стратегии")
                Show(currentPage);
            else if (e.PropertyName == nameof(MainViewModel.StrategyTestSummary) && currentPage == "Стратегии")
                Show("Стратегии");
            else if (e.PropertyName == nameof(MainViewModel.Status) && currentPage is "Главная" or "Стратегии")
                Show(currentPage);
        });

    private void BuildShell()
    {
        DetachPage();
        Background = CanvasBrush;
        Foreground = TextBrush;
        navButtons.Clear();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("238,*") };
        var side = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = SidebarBrush };
        var sideContent = new StackPanel { Margin = new Thickness(18, 24, 18, 0), Spacing = 3 };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, Margin = new Thickness(8, 0, 0, 36) };
        brand.Children.Add(new Image { Source = brandImage, Width = 32, Height = 32,
            Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock { Text = "Zapret", FontSize = 21, FontWeight = FontWeight.Bold,
            Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
        sideContent.Children.Add(brand);
        sideContent.Children.Add(SideLabel("ОСНОВНОЕ"));
        AddNavigation(sideContent, "Главная", FluentIcons.Home);
        AddNavigation(sideContent, "Стратегии", FluentIcons.Apps);
        AddNavigation(sideContent, "Списки", FluentIcons.List);
        sideContent.Children.Add(new Border { Height = 1, Background = OutlineBrush, Margin = new Thickness(10, 18, 10, 16) });
        sideContent.Children.Add(SideLabel("СИСТЕМА"));
        AddNavigation(sideContent, "Диагностика", FluentIcons.ShieldCheckmark);
        AddNavigation(sideContent, "Логи", FluentIcons.DocumentText);
        AddNavigation(sideContent, "Настройки", FluentIcons.Settings);
        side.Children.Add(sideContent);
        var sideFoot = new StackPanel { Margin = new Thickness(25, 16, 18, 25), Spacing = 7 };
        var online = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sidebarStatusDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = CurrentStatusBrush(), VerticalAlignment = VerticalAlignment.Center };
        online.Children.Add(sidebarStatusDot);
        sidebarStatus = new TextBlock { Text = vm.StatusText, Foreground = TextBrush, FontSize = 13 };
        online.Children.Add(sidebarStatus);
        sideFoot.Children.Add(online);
        sideFoot.Children.Add(new TextBlock { Text = $"Zapret Desktop {vm.DesktopVersion}", Foreground = MutedBrush, FontSize = 12 });
        sideFoot.Children.Add(new TextBlock { Text = "Автор: fecset · Основа: bol-van, Flowseal", Foreground = MutedBrush, FontSize = 11,
            TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(sideFoot, 2);
        side.Children.Add(sideFoot);
        Grid.SetColumn(side, 0);
        grid.Children.Add(side);

        var main = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        contentScroll = new ScrollViewer
        {
            Content = page, Padding = new Thickness(30, 25, 30, 12),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        contentHost = new ContentControl { Content = contentScroll };
        main.Children.Add(contentHost);
        footer = new TextBlock
        {
            Text = vm.Message, IsVisible = false,
            Foreground = MutedBrush, FontSize = 13,
            Margin = new Thickness(30, 8, 30, 18), TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(footer, 1);
        main.Children.Add(footer);
        Grid.SetColumn(main, 1);
        grid.Children.Add(main);
        Content = grid;
        UpdateNavigation();
    }

    private TextBlock SideLabel(string label) => new()
    {
        Text = label, Foreground = MutedBrush, FontSize = 12, FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(13, 0, 0, 12)
    };
    private void AddNavigation(StackPanel container, string name, Geometry icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new PathIcon { Data = icon, Width = 20, Height = 20,
            VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content, Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(13, 0),
            Background = Brushes.Transparent, Foreground = MutedBrush, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 0, 3)
        };
        button.Click += (_, _) => Show(name);
        navButtons[name] = button;
        container.Children.Add(button);
    }
    private void UpdateNavigation()
    {
        foreach (var (name, button) in navButtons)
        {
            button.Background = name == currentPage ? SurfaceRaisedBrush : Brushes.Transparent;
            button.Foreground = name == currentPage ? TextBrush : MutedBrush;
            button.FontWeight = name == currentPage ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }
    private void Show(string name)
    {
        currentPage = name;
        DetachPage();
        page.Children.Clear();
        contentScroll.Content = page;
        contentHost.Content = contentScroll;
        UpdateNavigation();
        switch (name)
        {
            case "Главная": BuildHome(); break;
            case "Стратегии": BuildStrategies(); break;
            case "Списки": BuildLists(); break;
            case "Диагностика": BuildDiagnostics(); break;
            case "Логи": BuildLogs(); break;
            case "Настройки": BuildSettings(); break;
        }
        footer.Text = vm.Message;
    }

    private async Task CheckAndOpenUpdatesAsync()
    {
        if (vm.CheckingUpdates) return;
        await vm.CheckUpdatesAsync();
        foreach (var update in new[] { vm.UpstreamUpdate, vm.DesktopUpdate })
        {
            if (update?.Available != true || !Uri.TryCreate(update.ReleaseUrl, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" ||
                !url.AbsolutePath.Contains("/releases/", StringComparison.OrdinalIgnoreCase)) continue;
            try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { vm.Log("ERROR", $"Не удалось открыть страницу выпуска {update.Product}: {ex.Message}"); }
        }
    }

    private void RefreshUpdateDisplay()
    {
        if (updateCheckButton is not null) updateCheckButton.IsEnabled = !vm.CheckingUpdates;
        if (upstreamUpdateStatus is not null)
        {
            upstreamUpdateStatus.Text = UpdateStatus("zapret", vm.UpstreamUpdate);
            upstreamUpdateStatus.Foreground = vm.UpstreamUpdate?.Available == true ? WarnBrush :
                vm.UpstreamUpdate is null ? MutedBrush : GoodBrush;
        }
        if (desktopUpdateStatus is not null)
        {
            desktopUpdateStatus.Text = UpdateStatus("Zapret Desktop", vm.DesktopUpdate);
            desktopUpdateStatus.Foreground = vm.DesktopUpdate?.Available == true ? WarnBrush :
                vm.DesktopUpdate is null ? MutedBrush : GoodBrush;
        }
        var available = new[] { vm.UpstreamUpdate, vm.DesktopUpdate }.Where(x => x?.Available == true)
            .Select(x => $"{x!.Product} {x.LatestVersion}").ToArray();
        if (updateNoticePanel is not null) updateNoticePanel.IsVisible = available.Length > 0;
        if (updateNotice is not null)
            updateNotice.Text = "Доступны обновления: " + string.Join(", ", available) + ". Подробнее на страницах выпусков GitHub.";
    }

    private string UpdateStatus(string product, UpdateInfo? update) => update is null
        ? vm.CheckingUpdates ? $"{product}: проверка..." : vm.UpdateError ?? $"{product}: проверка ещё не выполнена"
        : update.Available ? $"{product}: доступна версия {update.LatestVersion} — {update.ReleaseUrl}"
        : $"{product}: установлена актуальная версия {update.LocalVersion}";

    private void DetachPage()
    {
        if (fillLayout is not null)
        {
            fillLayout.Children.Remove(page);
            fillLayout = null;
        }
        contentScroll.Content = null;
        contentHost.Content = null;
    }

    private void FillRemainingHeight(Control list)
    {
        contentScroll.Content = null;
        contentHost.Content = null;
        fillLayout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        fillLayout.Children.Add(page);
        list.Margin = new Thickness(0, 18, 0, 0);
        Grid.SetRow(list, 1);
        fillLayout.Children.Add(list);
        contentHost.Content = new Border
        {
            Padding = new Thickness(30, 25, 30, 12),
            Child = fillLayout
        };
    }

    private void Heading(string title, string subtitle)
    {
        var header = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 7) };
        header.Children.Add(Text(title, 28, FontWeight.SemiBold));
        header.Children.Add(Text(subtitle, 14, color: MutedBrush));
        page.Children.Add(header);
    }
    private TextBlock Text(string value, double size = 14, FontWeight? weight = null, IBrush? color = null) => new()
    {
        Text = value, FontSize = Math.Max(size, 13), FontWeight = weight ?? FontWeight.Normal,
        Foreground = color ?? TextBrush, TextWrapping = TextWrapping.Wrap
    };
    private Border Panel(Control content, int padding = 20) => new()
    {
        Child = content, Padding = new Thickness(padding), Background = SurfaceBrush,
        BorderBrush = OutlineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13)
    };
    private Border Pill(string label, IBrush color, IBrush? background = null) => new()
    {
        Background = background ?? SurfaceRaisedBrush, Padding = new Thickness(9, 4),
        CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left,
        Child = Text(label, 12, FontWeight.SemiBold, color)
    };
    private Button Button(string label, Func<Task> action, bool primary = false, bool danger = false)
    {
        var button = new Button
        {
            Content = label, MinHeight = 40, Padding = new Thickness(15, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = primary ? AccentBrush : danger ? Brush(IsLight ? "#FDE8E9" : "#40262A") : SurfaceRaisedBrush,
            Foreground = primary ? Brushes.White : danger ? BadBrush : TextBrush,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            FontSize = 14, FontWeight = FontWeight.SemiBold
        };
        button.Click += async (_, _) => await action();
        return button;
    }
    private ComboBox StrategyPicker()
    {
        var combo = Dropdown(vm.Strategies, vm.SelectedStrategy, 260);
        combo.ItemTemplate = new FuncDataTemplate<ZapretStrategy>((item, _) =>
            Text(item?.DisplayName ?? "Выберите стратегию", 14));
        combo.SelectionChanged += async (_, _) => await vm.SelectAsync(combo.SelectedItem as ZapretStrategy);
        return combo;
    }
    private ComboBox Dropdown(IEnumerable items, object? selected, double width) => new()
    {
        ItemsSource = items, SelectedItem = selected, Width = width, MinHeight = 40,
        Background = SurfaceRaisedBrush, Foreground = TextBrush,
        BorderBrush = OutlineBrush, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7),
        FontSize = 14, VerticalAlignment = VerticalAlignment.Center
    };
    private void ApplyTheme()
    {
        Application.Current!.RequestedThemeVariant = vm.Settings.Theme switch
        {
            "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default
        };
        BuildShell();
        Show(currentPage);
    }
    private static string ServiceLabel(string? state) => state switch
    {
        "Running" => "Запущена", "Stopped" => "Остановлена", "NotInstalled" => "Не установлена",
        "StartPending" => "Запускается", "StopPending" => "Останавливается",
        "AccessDenied" => "Нет доступа", "Unknown" => "Не удалось проверить", _ => state ?? "—"
    };
    private IBrush CurrentStatusBrush() => vm.Status?.State switch
    {
        ZapretRunState.Running or ZapretRunState.ServiceRunning => GoodBrush,
        ZapretRunState.External => WarnBrush,
        _ => BadBrush
    };
}
