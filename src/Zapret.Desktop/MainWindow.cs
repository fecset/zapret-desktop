using System.ComponentModel;
using System.Collections;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private readonly Action<Uri> openReleasePage;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer feedbackTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly StackPanel page = new() { Spacing = 18 };
    private readonly Dictionary<string, Button> navButtons = [];
    private readonly List<(Button Button, Func<bool> CanExecute)> engineControls = [];
    private TextBlock footer = new();
    private Grid feedbackPanel = new();
    private TextBlock sidebarStatus = new();
    private TextBlock? sidebarUpstreamVersion;
    private TextBlock? upstreamUpdateStatus;
    private TextBlock? desktopUpdateStatus;
    private TextBlock? updateNotice;
    private TextBlock? updateNoticeDetails;
    private Border? updateNoticePanel;
    private Border? updateSettingsPanel;
    private Button? updateCheckButton;
    private Button? upstreamUpdateButton;
    private Button? desktopUpdateButton;
    private Button? updateWhatsNewButton;
    private ProgressBar? updateProgressBar;
    private TextBlock? updateProgressStage;
    private TextBlock? updateProgressPercent;
    private readonly Dictionary<string, TextBlock> strategyResultLabels = new(StringComparer.OrdinalIgnoreCase);
    private Border? strategySummaryPanel;
    private TextBlock? strategySummaryText;
    private Border? strategyProgressPanel;
    private TextBlock? strategyProgressText;
    private ProgressBar? strategyProgressBar;
    private Button? strategyStartButton;
    private Button? strategyCancelButton;
    private Button? strategyRecommendationButton;
    private Border sidebarStatusDot = new();
    private ScrollViewer contentScroll = new();
    private ContentControl contentHost = new();
    private Grid? fillLayout;
    private string currentPage = "Главная";
    private bool exitAllowed;
    private bool exitInProgress;
    private bool openedOnce;
    private string? payloadSourceSelection;

    private bool IsLight => vm.Settings.Theme == "Light" ||
        vm.Settings.Theme == "System" && Application.Current?.ActualThemeVariant == ThemeVariant.Light;
    private IBrush CanvasBrush => Brush(IsLight ? "#F5F6FA" : "#101115");
    private IBrush SidebarBrush => Brush(IsLight ? "#FFFFFF" : "#131417");
    private IBrush SurfaceBrush => Brush(IsLight ? "#FFFFFF" : "#1A1B20");
    private IBrush SurfaceRaisedBrush => Brush(IsLight ? "#F1F2F7" : "#24262C");
    private IBrush OutlineBrush => Brush(IsLight ? "#E4E5EC" : "#303139");
    private IBrush TextBrush => Brush(IsLight ? "#16171C" : "#F5F5F8");
    private IBrush MutedBrush => Brush(IsLight ? "#60636F" : "#A3A5AF");
    private static IBrush AccentBrush => Brush("#6544C8");
    private IBrush GoodBrush => Brush(IsLight ? "#147346" : "#47C98A");
    private IBrush WarnBrush => Brush(IsLight ? "#835C00" : "#F3C96C");
    private IBrush BadBrush => Brush(IsLight ? "#B42332" : "#F07479");
    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public MainWindow(MainViewModel vm, Action<Uri>? openReleasePage = null)
    {
        this.vm = vm;
        this.openReleasePage = openReleasePage ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        Title = "Zapret Desktop";
        using (var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Zapret.Desktop/Assets/app.ico")))
            Icon = new WindowIcon(iconStream);
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
            statusTimer.Start();
            if (vm.Settings.CheckUpdates) await CheckForUpdatesAsync();
        };
        statusTimer.Tick += async (_, _) => await vm.RefreshAsync();
        feedbackTimer.Tick += (_, _) => { feedbackTimer.Stop(); feedbackPanel.IsVisible = false; };
        Closed += (_, _) => { statusTimer.Stop(); feedbackTimer.Stop(); };
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
            CompleteExit();
        }
        finally { exitInProgress = false; }
    }

    private void CompleteExit()
    {
        exitAllowed = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            RefreshButtonActivity();
            if (e.PropertyName == nameof(MainViewModel.FeedbackVersion))
            {
                footer.Text = vm.Message;
                footer.Foreground = vm.MessageIsError ? BadBrush : MutedBrush;
                feedbackPanel.IsVisible = !string.IsNullOrWhiteSpace(vm.Message);
                feedbackTimer.Stop();
                if (feedbackPanel.IsVisible && !vm.MessageIsError) feedbackTimer.Start();
            }
            sidebarStatus.Text = vm.StatusText;
            sidebarStatusDot.Background = CurrentStatusBrush();
            if (e.PropertyName == nameof(MainViewModel.UpstreamVersion) && sidebarUpstreamVersion is not null)
                sidebarUpstreamVersion.Text = $"zapret {vm.UpstreamVersion}";
            if (e.PropertyName == nameof(MainViewModel.Busy)) RefreshEngineControls();
            if (e.PropertyName == nameof(MainViewModel.PreparingData) && currentPage == "Настройки") Show(currentPage);
            if (e.PropertyName == nameof(MainViewModel.FiltersNeedRestart) && currentPage == "Настройки") Show(currentPage);
            if (e.PropertyName == nameof(MainViewModel.TestingStrategies) && currentPage is "Списки" or "Диагностика") Show(currentPage);
            if (e.PropertyName == nameof(MainViewModel.RecoveryRequired) && currentPage is "Настройки" or "Главная") Show(currentPage);
            if (e.PropertyName is nameof(MainViewModel.UpstreamUpdate) or nameof(MainViewModel.DesktopUpdate)
                or nameof(MainViewModel.CheckingUpdates) or nameof(MainViewModel.UpdateError))
                RefreshUpdateDisplay();
            if (e.PropertyName is nameof(MainViewModel.PreparationProgress) or nameof(MainViewModel.PreparationStage)
                or nameof(MainViewModel.PreparationDetail))
                RefreshUpdateProgress();
            if (e.PropertyName == nameof(MainViewModel.SelectedStrategy) && currentPage is "Главная" or "Стратегии")
                Show(currentPage);
            else if (e.PropertyName is nameof(MainViewModel.StrategyTestSummary) or nameof(MainViewModel.StrategyChecks)
                or nameof(MainViewModel.BestTestStrategy) or nameof(MainViewModel.TestingStrategies)
                or nameof(MainViewModel.Busy) && currentPage == "Стратегии")
                RefreshStrategyResults();
            else if (e.PropertyName == nameof(MainViewModel.ConnectionHealth) && currentPage == "Главная")
                Show(currentPage);
            else if (e.PropertyName == nameof(MainViewModel.Status) && currentPage is "Главная" or "Стратегии")
                Show(currentPage);
            else if (e.PropertyName == nameof(MainViewModel.Diagnostics) && currentPage == "Диагностика")
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
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8, 0, 0, 36) };
        brand.Children.Add(BrandMark.Create(48));
        var wordmark = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        wordmark.Children.Add(new TextBlock
        {
            Text = "Zapret", FontSize = 22, FontWeight = FontWeight.ExtraBold,
            FontFamily = new FontFamily("fonts:Inter#Inter"), Foreground = TextBrush
        });
        wordmark.Children.Add(new TextBlock
        {
            Text = "DESKTOP", FontSize = 10, FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("fonts:Inter#Inter"),
            Foreground = Brush(IsLight ? "#52627F" : "#9FB1D9"),
            Margin = new Thickness(2, -2, 0, 0)
        });
        brand.Children.Add(wordmark);
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
        sidebarUpstreamVersion = new TextBlock { Text = $"zapret {vm.UpstreamVersion}", Foreground = MutedBrush, FontSize = 12 };
        sideFoot.Children.Add(sidebarUpstreamVersion);
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
            Text = vm.Message, TextWrapping = TextWrapping.Wrap,
            Foreground = vm.MessageIsError ? BadBrush : MutedBrush, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
        };
        feedbackPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(30, 8, 30, 18), IsVisible = !string.IsNullOrWhiteSpace(vm.Message)
        };
        var dismiss = MessageDismissButton();
        Grid.SetColumn(dismiss, 1);
        feedbackPanel.Children.Add(footer);
        feedbackPanel.Children.Add(dismiss);
        Grid.SetRow(feedbackPanel, 1);
        main.Children.Add(feedbackPanel);
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
    private void Show(string name, Vector? scrollOffset = null)
    {
        var offset = scrollOffset ?? (name == currentPage ? contentScroll.Offset : default);
        currentPage = name;
        DetachPage();
        page.Children.Clear();
        engineControls.Clear();
        actionButtons.Clear();
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
        // Reattaching the content clears its extent. Restore only after the new
        // page has been measured, otherwise ScrollViewer clamps the offset to zero.
        UpdateLayout();
        contentScroll.Offset = offset;
    }

    private async Task CheckForUpdatesAsync()
    {
        if (vm.CheckingUpdates) return;
        await vm.CheckUpdatesAsync();
    }

    private Task OpenUpdatesAsync()
    {
        Show("Настройки");
        ScrollUpdatesToCenter();
        return Task.CompletedTask;
    }

    private void ScrollUpdatesToCenter()
    {
        if (updateSettingsPanel is null) return;
        contentScroll.UpdateLayout();
        var origin = updateSettingsPanel.TranslatePoint(new Point(0, 0), page);
        if (origin is null) return;
        var viewport = contentScroll.Viewport.Height;
        var extent = contentScroll.Extent.Height;
        var panelHeight = updateSettingsPanel.Bounds.Height;
        var target = origin.Value.Y - Math.Max(0, (viewport - panelHeight) / 2);
        contentScroll.Offset = new Vector(0, Math.Clamp(target, 0, Math.Max(0, extent - viewport)));
    }

    private Task OpenReleaseNotesAsync()
    {
        var update = vm.DesktopUpdate?.Available == true ? vm.DesktopUpdate : vm.UpstreamUpdate;
        if (update is null) return Task.CompletedTask;
        var repository = update.Product.Contains("Desktop", StringComparison.OrdinalIgnoreCase)
            ? "fecset/zapret-desktop" : "Flowseal/zapret-discord-youtube";
        return OpenReleaseAsync(update, repository);
    }

    private Task OpenReleaseAsync(UpdateInfo? update, string repository)
    {
        if (update is null) return Task.CompletedTask;
        if (!Uri.TryCreate(update.ReleaseUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.Host != "github.com" || url.Port != 443 || url.UserInfo.Length != 0 ||
            !url.AbsolutePath.StartsWith($"/{repository}/releases/", StringComparison.Ordinal))
        {
            vm.ReportError("Не удалось открыть «Что нового»", new InvalidOperationException("Недоверенный адрес страницы релиза."));
            return Task.CompletedTask;
        }
        try { openReleasePage(url); }
        catch (Exception ex) { vm.ReportError("Не удалось открыть страницу релиза", ex); }
        return Task.CompletedTask;
    }

    private void RefreshUpdateDisplay()
    {
        if (updateCheckButton is not null) updateCheckButton.IsEnabled = !vm.CheckingUpdates;
        if (upstreamUpdateButton is not null)
            upstreamUpdateButton.IsVisible = vm.DistributionUpdatePlan is not null || vm.UpstreamUpdate?.Available == true;
        if (desktopUpdateButton is not null)
            desktopUpdateButton.IsVisible = vm.DesktopUpdatePlan is not null || vm.DesktopUpdate?.Available == true;
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
        var available = new[] { vm.UpstreamUpdate, vm.DesktopUpdate }.Where(x => x?.Available == true).ToArray();
        if (updateNoticePanel is not null) updateNoticePanel.IsVisible = available.Length > 0;
        if (updateNotice is not null)
            updateNotice.Text = available.Length > 1 ? "Доступны обновления" : "Доступно обновление";
        if (updateNoticeDetails is not null)
            updateNoticeDetails.Text = string.Join("\n", available.Select(x =>
                $"{DisplayProduct(x!.Product)} {x.LocalVersion} → {x.LatestVersion}"));
        if (updateWhatsNewButton is not null)
        {
            var notes = vm.DesktopUpdate?.Available == true ? vm.DesktopUpdate : vm.UpstreamUpdate;
            updateWhatsNewButton.IsVisible = notes is not null;
            if (notes is not null)
                ToolTip.SetTip(updateWhatsNewButton, $"Что нового в {DisplayProduct(notes.Product)} {notes.LatestVersion}");
        }
        RefreshUpdateProgress();
    }

    private void RefreshUpdateProgress()
    {
        if (updateProgressBar is null) return;
        updateProgressBar.IsIndeterminate = vm.PreparationIndeterminate;
        updateProgressBar.Value = (vm.PreparationProgress ?? 0) * 100;
        if (updateProgressStage is not null)
            updateProgressStage.Text = string.IsNullOrWhiteSpace(vm.PreparationDetail)
                ? vm.PreparationStage
                : vm.PreparationStage + " · " + vm.PreparationDetail;
        if (updateProgressPercent is not null)
        {
            updateProgressPercent.Text = vm.PreparationProgress is { } progress ? $"{progress * 100:F0}%" : "";
            updateProgressPercent.IsVisible = vm.PreparationProgress is not null;
        }
    }

    private static string DisplayProduct(string product) =>
        product.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ? "Zapret Desktop" : product;

    private string UpdateStatus(string product, UpdateInfo? update) => update is null
        ? vm.CheckingUpdates ? $"{product}: проверка..." : vm.UpdateError ?? $"{product}: проверка ещё не выполнена"
        : update.Available ? $"{product}: установлена {update.LocalVersion} → доступна {update.LatestVersion}"
        : $"{product}: установлена {update.LocalVersion} · актуальная версия";

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
    private Button EngineButton(string label, Func<Task> action, Func<bool>? canExecute = null,
        bool primary = false, bool danger = false, string? activityKey = null, Func<bool>? isLoading = null)
    {
        canExecute ??= () => true;
        var button = Button(label, action, primary, danger, activityKey, isLoading);
        engineControls.Add((button, canExecute));
        button.IsEnabled = !vm.Busy && canExecute();
        return button;
    }
    private void RefreshEngineControls()
    {
        foreach (var (button, canExecute) in engineControls)
            button.IsEnabled = !vm.Busy && canExecute();
    }
    private Button Button(string label, Func<Task> action, bool primary = false, bool danger = false,
        string? activityKey = null, Func<bool>? isLoading = null)
    {
        var button = new ActionButton
        {
            Content = label, MinHeight = 40, Padding = new Thickness(15, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = primary ? AccentBrush : danger ? Brush(IsLight ? "#FDE8E9" : "#40262A") : SurfaceRaisedBrush,
            Foreground = primary ? Brushes.White : danger ? BadBrush : TextBrush,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            FontSize = 14, FontWeight = FontWeight.SemiBold
        };
        TrackButton(button, activityKey ?? currentPage + ":" + label, action, isLoading);
        return button;
    }

    private Button IconButton(string label, Geometry icon, Func<Task> action,
        bool primary = false, bool danger = false, string? activityKey = null, Func<bool>? isLoading = null)
    {
        var button = new ActionButton
        {
            Content = new PathIcon
            {
                Data = icon, Width = 20, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 44, Height = 40, MinWidth = 44, MinHeight = 40, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = primary ? AccentBrush : danger ? Brush(IsLight ? "#FDE8E9" : "#40262A") : SurfaceRaisedBrush,
            Foreground = primary ? Brushes.White : danger ? BadBrush : TextBrush,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        TrackButton(button, activityKey ?? currentPage + ":" + label, action, isLoading);
        return button;
    }
    private ComboBox StrategyPicker()
    {
        var combo = Dropdown(vm.Strategies, vm.SelectedStrategy, 260);
        combo.ItemTemplate = new FuncDataTemplate<ZapretStrategy>((item, _) =>
            Text(item?.DisplayName ?? "Выберите стратегию", 14));
        combo.SelectionChanged += async (_, _) =>
        {
            // Resetting ItemsSource during import/update is not a user selection.
            if (combo.SelectedItem is ZapretStrategy selected && vm.Strategies.Contains(selected) &&
                combo.IsAttachedToVisualTree())
                await vm.SelectAsync(selected);
        };
        return combo;
    }
    private ComboBox Dropdown(IEnumerable items, object? selected, double width) => new()
    {
        ItemsSource = items, SelectedItem = selected, Width = width, Height = 40,
        Background = SurfaceRaisedBrush, Foreground = TextBrush,
        BorderBrush = OutlineBrush, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 0),
        FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private void ApplyTheme()
    {
        var offset = contentScroll.Offset;
        Application.Current!.RequestedThemeVariant = vm.Settings.Theme switch
        {
            "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default
        };
        BuildShell();
        Show(currentPage, offset);
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
