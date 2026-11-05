using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private void BuildHome()
    {
        Heading("Главная", "Состояние Zapret и проверка доступности сервисов");
        foreach (var warning in vm.StartupWarnings)
            page.Children.Add(Panel(Text(warning, 12, color: WarnBrush), 12));
        HomeUpdateNotice();

        var state = vm.Status?.State;
        var running = state is ZapretRunState.Running or ZapretRunState.ServiceRunning;
        var uncertain = vm.Status is null || vm.Status.ServiceStatus is "Unknown" or "AccessDenied";
        var statusColor = running ? GoodBrush : state == ZapretRunState.External || uncertain ? WarnBrush : MutedBrush;
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var summary = new StackPanel { Spacing = Compact ? 6 : 8 };
        summary.Children.Add(StatusPill(running ? "ZAPRET АКТИВЕН" : state == ZapretRunState.External ? "ВНЕШНИЙ ЗАПУСК" :
            uncertain ? "СТАТУС НЕ ПОДТВЕРЖДЁН" : "ZAPRET ОСТАНОВЛЕН", statusColor));
        var headline = Text(running ? "Zapret запущен" : state == ZapretRunState.External ? "Обнаружен внешний winws" :
            uncertain ? "Состояние не подтверждено" : "Zapret остановлен", Compact ? 22 : 24, FontWeight.Medium);
        headline.MinHeight = Compact ? 32 : 36;
        summary.Children.Add(headline);
        var active = running ? state == ZapretRunState.ServiceRunning
            ? "Активные параметры службы: фактическая команда показана в диагностике"
            : "Запущена стратегия: " + vm.RunningStrategyName
            : state == ZapretRunState.External ? "Остановите внешний процесс в приложении, которое его запустило."
            : uncertain ? "Проверьте состояние службы в разделе «Диагностика»."
            : "Выберите стратегию для следующего запуска.";
        var activeText = Text(active, 13, color: MutedBrush);
        activeText.MinHeight = 20;
        summary.Children.Add(activeText);
        hero.Children.Add(summary);
        var mainAction = EngineButton(running ? "Остановить Zapret" : "Запустить Zapret",
            running ? vm.StopAsync : vm.StartAsync,
            () => running || (vm.Status?.CanStart == true && vm.SelectedStrategy is not null),
            primary: !running, danger: running,
            activityKey: running ? "engine-stop" : "engine-start:" + vm.SelectedStrategy?.Id);
        mainAction.Width = ActionWidth;
        mainAction.VerticalAlignment = VerticalAlignment.Center;
        mainAction.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(mainAction, 1);
        hero.Children.Add(mainAction);
        page.Children.Add(DashboardSection(hero, "EngineControl", new Thickness(EngineeringTheme.SectionPadding)));

        ConnectionPanel();
        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        AddMetric(metrics, 0, "ДВИЖОК", state switch
            {
                ZapretRunState.Running or ZapretRunState.ServiceRunning => "Работает",
                ZapretRunState.External => "Внешний процесс",
                _ => uncertain ? "Не подтверждено" : "Остановлен"
            }, vm.Status?.ProcessId is { } pid ? $"winws.exe · PID {pid}" : "winws.exe", statusColor);
        var driver = vm.Status?.DriverStatus;
        AddMetric(metrics, 1, "СЕТЕВОЙ ДРАЙВЕР", driver switch
            {
                "Running" when state == ZapretRunState.Stopped => "Остался загружен",
                "Running" => "Загружен", "Stopped" => "Остановлен", "NotInstalled" => "Не загружен",
                "AccessDenied" => "Нет доступа", "StartPending" => "Загружается", "StopPending" => "Выгружается",
                _ => "Не удалось проверить"
            }, "WinDivert", driver == "Running" && running ? GoodBrush :
                driver is null or "Unknown" or "AccessDenied" or "StartPending" or "StopPending" || driver == "Running" && !running
                    ? WarnBrush : MutedBrush);
        AddMetric(metrics, 2, "СЛУЖБА WINDOWS", ServiceLabel(vm.Status?.ServiceStatus),
            "Запуск до входа в Windows", vm.Status?.ServiceStatus == "Running" ? GoodBrush :
                vm.Status?.ServiceStatus is null or "Unknown" or "AccessDenied" ? WarnBrush : MutedBrush);
        page.Children.Add(DashboardSection(metrics, "ComponentStatus", new Thickness(0)));
        if (state == ZapretRunState.Stopped && driver is "Running" or "StartPending" or "StopPending")
        {
            var unload = new StackPanel { Spacing = 10 };
            unload.Children.Add(Text("WinDivert ещё загружен. Выгрузите драйвер, чтобы освободить файлы для обновления.", 12, color: WarnBrush));
            unload.Children.Add(EngineButton("Выгрузить WinDivert", vm.StopAsync));
            page.Children.Add(Panel(unload, 16));
        }

        HomeLaunchStrategy(running, state);
        var quickTitle = Text("Быстрые действия", 17, FontWeight.Medium);
        quickTitle.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(quickTitle);
        var quick = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        quick.Children.Add(QuickAction(FluentIcons.ShieldCheckmark, "Проверить систему", "Файлы, драйвер и служба", async () =>
        {
            Show("Диагностика");
            await vm.DiagnoseAsync();
            if (currentPage == "Диагностика") Show("Диагностика");
        }, "diagnostics"));
        var strategyAction = QuickAction(FluentIcons.Apps, "Подобрать стратегию", "Сравнить доступные варианты", () =>
        {
            Show("Стратегии");
            return Task.CompletedTask;
        });
        Grid.SetColumn(strategyAction, 1);
        quick.Children.Add(strategyAction);
        page.Children.Add(quick);
        var started = vm.Status?.StartedAt?.ToLocalTime().ToString("g") ??
            (running ? "Время запуска не определено" : state == ZapretRunState.External ? "Внешний процесс" : "Не запущен");
        page.Children.Add(Text($"Zapret {vm.UpstreamVersion} · Zapret Desktop {vm.DesktopVersion} · {started}", 12, color: MutedBrush));
    }

    private void HomeLaunchStrategy(bool running, ZapretRunState? state)
    {
        var different = running && (vm.Status?.StrategyId is not { } active ||
            !string.Equals(active, vm.SelectedStrategy?.Id, StringComparison.OrdinalIgnoreCase));
        var pending = different || vm.FiltersNeedRestart;
        var selection = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 20 };
        var words = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text("Стратегия для следующего запуска", 16, FontWeight.Medium));
        words.Children.Add(Text("Выбор сохраняется. Если Zapret уже работает, смена вступит в силу после перезапуска.", 12, color: MutedBrush));
        words.Children.Add(Text("Стратегия — набор настроек для вашей сети. Выберите вариант вручную; если нужные сервисы не работают, сравните варианты в разделе «Стратегии».", 12, color: MutedBrush));
        if (pending)
        {
            var notice = new Grid();
            var message = Text(vm.FiltersNeedRestart ? "Изменения применятся после перезапуска." :
                "Применится после перезапуска. Сейчас: " + vm.RunningStrategyName, 12, color: WarnBrush);
            message.Margin = new Thickness(0, 6, 0, 0);
            notice.Children.Add(message);
            AutomationProperties.SetAutomationId(notice, "RestartNotice");
            words.Children.Add(notice);
        }
        selection.Children.Add(words);
        var picker = StrategyPicker();
        picker.Width = 260;
        Grid.SetColumn(picker, 1);
        selection.Children.Add(picker);
        page.Children.Add(DashboardSection(selection, "LaunchStrategy", new Thickness(EngineeringTheme.SectionPadding)));
        var restart = EngineButton("Применить выбор и перезапустить", vm.ApplyAndRestartAsync,
            () => vm.SelectedStrategy is not null && state != ZapretRunState.External, primary: pending);
        restart.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(restart, "ApplyLaunchStrategy");
        page.Children.Add(restart);
    }

    private Border DashboardSection(Control content, string id, Thickness padding)
    {
        var panel = new EngineeringSection(content, padding) {
            Background = SurfaceBrush, BorderBrush = OutlineBrush, BorderThickness = new Thickness(1),
            NodeBrush = Token("node") };
        AutomationProperties.SetAutomationId(panel, id);
        return panel;
    }

    private void AddMetric(Grid grid, int column, string label, string value, string detail, IBrush statusColor)
    {
        var stack = new StackPanel { Spacing = Compact ? 6 : 10, Margin = new Thickness(EngineeringTheme.SectionPadding) };
        var caption = Text(label, 12, color: MutedBrush);
        caption.LineHeight = 18;
        stack.Children.Add(caption);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("6,*") };
        line.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = statusColor, VerticalAlignment = VerticalAlignment.Center });
        var valueText = Text(value, Compact ? 16 : 18, FontWeight.Medium);
        valueText.LineHeight = Compact ? 24 : 27;
        valueText.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(valueText, 1);
        line.Children.Add(valueText);
        stack.Children.Add(line);
        var technical = Text(detail, 12, color: MutedBrush);
        technical.LineHeight = 18;
        stack.Children.Add(technical);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
        if (column == 0) return;
        var separator = new Border { Width = 1, Background = OutlineBrush, Margin = new Thickness(0, EngineeringTheme.SectionPadding),
            HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(separator, column);
        grid.Children.Add(separator);
    }

    private Border StatusPill(string label, IBrush color, IBrush? background = null)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("6,*"), ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = color, VerticalAlignment = VerticalAlignment.Center });
        var text = Text(label, 12, color: color);
        text.LineHeight = 18;
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        return new Border { Child = line, Background = background ?? StatusBackground(), Padding = new Thickness(10, 4),
            MinWidth = 220, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private Button QuickAction(Geometry icon, string title, string subtitle, Func<Task> action, string? activityKey = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        line.Children.Add(new PathIcon { Data = icon, Width = 20, Height = 20, Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center });
        var words = new StackPanel { Spacing = 4 };
        words.Children.Add(Text(title, 13, FontWeight.Medium));
        words.Children.Add(Text(subtitle, 12, color: MutedBrush));
        line.Children.Add(words);
        var button = new ActionButton { Content = line, Padding = new Thickness(16), Background = SurfaceBrush,
            Foreground = TextBrush, BorderBrush = OutlineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 72 };
        StyleAction(button);
        AutomationProperties.SetName(button, title);
        TrackButton(button, activityKey ?? "quick:" + title, action);
        return button;
    }

    private void HomeUpdateNotice()
    {
        updateNotice = Text("Доступно обновление", 15, FontWeight.Medium);
        updateNoticeDetails = Text("", 12, color: MutedBrush);
        var words = new StackPanel { Spacing = 8 };
        words.Children.Add(updateNotice);
        words.Children.Add(updateNoticeDetails);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(Button("Открыть обновления", OpenUpdatesAsync, primary: true));
        updateWhatsNewButton = Button("Что нового", OpenReleaseNotesAsync);
        actions.Children.Add(updateWhatsNewButton);
        words.Children.Add(actions);
        updateNoticePanel = Panel(words, 16);
        updateNoticePanel.Background = Token("infoBg");
        AutomationProperties.SetAutomationId(updateNoticePanel, "UpdateBanner");
        page.Children.Add(updateNoticePanel);
        RefreshUpdateDisplay();
    }
}
