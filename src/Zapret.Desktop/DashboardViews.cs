using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private void BuildHome()
    {
        Heading("Главная", "Ваше подключение и всё важное — на одном экране.");
        var state = vm.Status?.State ?? ZapretRunState.Stopped;
        var running = state is ZapretRunState.Running or ZapretRunState.ServiceRunning;
        var statusColor = running ? GoodBrush : state == ZapretRunState.External ? WarnBrush : BadBrush;
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var summary = new StackPanel { Spacing = 10 };
        summary.Children.Add(StatusPill(running ? "ZAPRET АКТИВЕН" : state == ZapretRunState.External ? "ВНЕШНИЙ ЗАПУСК" : "ZAPRET ОСТАНОВЛЕН", statusColor));
        summary.Children.Add(Text(running ? "Соединение под управлением Zapret" : state == ZapretRunState.External ?
            "winws запущен другим приложением" : "Готов к запуску", 22, FontWeight.SemiBold));
        summary.Children.Add(Text(running ? "Вы можете остановить Zapret одним нажатием." :
            state == ZapretRunState.External ? "Чтобы избежать конфликта, остановите внешний экземпляр там, где он был запущен." :
            "Выберите стратегию ниже и нажмите «Запустить Zapret». Для работы нужны права администратора.", 13, color: MutedBrush));
        hero.Children.Add(summary);
        var mainAction = Button(running ? "Остановить Zapret" : "Запустить Zapret",
            running ? vm.StopAsync : vm.StartAsync, primary: !running, danger: running);
        mainAction.MinWidth = 177;
        mainAction.MinHeight = 46;
        mainAction.HorizontalContentAlignment = HorizontalAlignment.Center;
        mainAction.VerticalContentAlignment = VerticalAlignment.Center;
        mainAction.FontSize = 14;
        mainAction.IsEnabled = running || (state == ZapretRunState.Stopped && vm.SelectedStrategy is not null);
        mainAction.VerticalAlignment = VerticalAlignment.Center;
        mainAction.Margin = new Thickness(28, 0, 0, 0);
        Grid.SetColumn(mainAction, 1);
        hero.Children.Add(mainAction);
        page.Children.Add(Panel(hero, 24));

        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        AddMetric(metrics, 0, "ДВИЖОК", state == ZapretRunState.Stopped ? "Остановлен" : "Работает",
            vm.Status?.ProcessId is { } pid ? $"winws.exe · PID {pid}" : "winws.exe", state == ZapretRunState.Stopped ? BadBrush : GoodBrush);
        var driverStatus = vm.Status?.DriverStatus;
        AddMetric(metrics, 1, "СЕТЕВОЙ ДРАЙВЕР", driverStatus switch
            {
                "Running" when state == ZapretRunState.Stopped => "Остался загружен",
                "Running" => "Загружен", "Stopped" => "Остановлен",
                "NotInstalled" => "Не загружен", _ => "Не удалось проверить"
            }, driverStatus == "Running" && state == ZapretRunState.Stopped
                ? "Zapret выключен; драйвер может оставаться в памяти" : "WinDivert",
            driverStatus == "Running" && state != ZapretRunState.Stopped ? GoodBrush :
                driverStatus is "Unknown" or "AccessDenied" ? WarnBrush : MutedBrush);
        AddMetric(metrics, 2, "СЛУЖБА WINDOWS", ServiceLabel(vm.Status?.ServiceStatus),
            "Автозапуск системы", vm.Status?.ServiceStatus == "Running" ? GoodBrush : MutedBrush);
        page.Children.Add(metrics);

        var strategy = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var strategyText = new StackPanel { Spacing = 5 };
        strategyText.Children.Add(Text("Стратегия для следующего запуска", 16, FontWeight.SemiBold));
        strategyText.Children.Add(Text("Выбор сохраняется. Если Zapret уже работает, смена вступит в силу после перезапуска.", 12, color: MutedBrush));
        strategy.Children.Add(strategyText);
        var picker = StrategyPicker();
        picker.Margin = new Thickness(20, 0, 0, 0);
        picker.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(picker, 1);
        strategy.Children.Add(picker);
        page.Children.Add(Panel(strategy));

        var section = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 9, 0, 0) };
        section.Children.Add(Text("Быстрые действия", 17, FontWeight.SemiBold));
        page.Children.Add(section);
        var quick = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var diag = QuickAction(FluentIcons.ShieldCheckmark, "Проверить систему", "Файлы, драйвер и служба", () =>
        {
            Show("Диагностика");
            return vm.DiagnoseAsync();
        });
        diag.Margin = new Thickness(0, 0, 8, 0);
        quick.Children.Add(diag);
        var choose = QuickAction(FluentIcons.Apps, "Подобрать стратегию", "Сравнить доступные варианты", () =>
        {
            Show("Стратегии");
            return Task.CompletedTask;
        });
        choose.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(choose, 1);
        quick.Children.Add(choose);
        page.Children.Add(quick);
        page.Children.Add(Text($"Zapret {vm.UpstreamVersion}  ·  Zapret Desktop 0.1.1  ·  {vm.Status?.StartedAt?.ToLocalTime().ToString("g") ?? "не запущен"}", 11, color: MutedBrush));
    }

    private void AddMetric(Grid grid, int column, string label, string value, string detail, IBrush statusColor)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Text(label, 10, FontWeight.SemiBold, MutedBrush));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        line.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = statusColor, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(Text(value, 18, FontWeight.SemiBold));
        stack.Children.Add(line);
        stack.Children.Add(Text(detail, 11, color: MutedBrush));
        var card = Panel(stack, 18);
        card.Margin = column switch { 0 => new Thickness(0, 0, 7, 0), 1 => new Thickness(7, 0), _ => new Thickness(7, 0, 0, 0) };
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }
    private Border StatusPill(string label, IBrush color)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        line.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
            Background = color, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(Text(label, 12, FontWeight.SemiBold, color));
        return new Border { Child = line, Background = SurfaceRaisedBrush, Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Left };
    }
    private Button QuickAction(Geometry icon, string title, string subtitle, Func<Task> action)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13 };
        line.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
            Background = SurfaceRaisedBrush, Child = new PathIcon { Data = icon, Width = 20, Height = 20,
                Foreground = AccentBrush, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center } });
        var words = new StackPanel { Spacing = 4 };
        words.Children.Add(Text(title, 14, FontWeight.SemiBold));
        words.Children.Add(Text(subtitle, 13, color: MutedBrush));
        line.Children.Add(words);
        var button = new Button { Content = line, Padding = new Thickness(16), Background = SurfaceBrush,
            BorderBrush = OutlineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 75 };
        button.Click += async (_, _) => await action();
        return button;
    }

    private void BuildStrategies()
    {
        Heading("Стратегии", "Выберите способ запуска. Zapret автоматически обнаруживает новые general*.bat.");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        bar.Children.Add(Text($"Доступные стратегии: {vm.Strategies.Count}", 14, FontWeight.SemiBold, MutedBrush));
        bar.Children.Add(Button("Автоподбор", vm.TestStrategiesAsync, primary: true));
        bar.Children.Add(Button("Отменить тест", () => { vm.CancelStrategyTest(); return Task.CompletedTask; }));
        page.Children.Add(bar);
        page.Children.Add(Text("Автоподбор использует тестовый скрипт upstream. Для него остановите winws и удалите службу zapret.", 12, color: MutedBrush));
        if (vm.StrategyTestSummary is { } summary)
            page.Children.Add(Panel(Text(summary + (vm.BestTestStrategy is null ? "" : " Выберите её вручную, чтобы сохранить выбор."),
                13, FontWeight.SemiBold, vm.BestTestStrategy is null ? MutedBrush : GoodBrush)));
        var strategyCards = new StackPanel { Spacing = 12 };
        foreach (var item in vm.Strategies)
        {
            var selected = item.Id == vm.SelectedStrategy?.Id;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var info = new StackPanel { Spacing = 7 };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            top.Children.Add(Text(item.DisplayName, 15, FontWeight.SemiBold));
            if (selected) top.Children.Add(Pill("ВЫБРАНА", GoodBrush));
            if (item.IsExperimental) top.Children.Add(Pill("ЭКСПЕРИМЕНТ", WarnBrush));
            else if (item.IsFakeTls) top.Children.Add(Pill("FAKE TLS", AccentBrush));
            else if (item.IsSimpleFake) top.Children.Add(Pill("SIMPLE FAKE", AccentBrush));
            info.Children.Add(top);
            info.Children.Add(Text($"{Path.GetFileName(item.SourceFile)}  ·  {item.Description}", 11, color: MutedBrush));
            row.Children.Add(info);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 0, 0) };
            var select = Button(selected ? "Выбрана" : "Выбрать", () => vm.SelectAsync(item));
            select.IsEnabled = !selected;
            actions.Children.Add(select);
            var start = Button("Запустить", async () =>
            {
                await vm.SelectAsync(item);
                await vm.StartAsync();
            }, primary: true);
            start.IsEnabled = vm.Status?.State == ZapretRunState.Stopped;
            actions.Children.Add(start);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            strategyCards.Children.Add(Panel(row, 16));
        }
        FillRemainingHeight(Panel(new ScrollViewer
        {
            Content = strategyCards,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        }, 10));
    }
}
