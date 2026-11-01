using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private void BuildStrategies()
    {
        strategyResultLabels.Clear();
        Heading("Стратегии", "Выберите способ запуска. Zapret автоматически обнаруживает новые general*.bat.");
        page.Children.Add(Text($"Доступные стратегии: {vm.Strategies.Count}", 14, FontWeight.SemiBold, MutedBrush));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        strategyStartButton = Button("Автоподбор", vm.TestStrategiesAsync, primary: true,
            isLoading: () => vm.TestingStrategies);
        strategyCancelButton = Button("Отменить тест", () => { vm.CancelStrategyTest(); return Task.CompletedTask; });
        bar.Children.Add(strategyStartButton);
        bar.Children.Add(strategyCancelButton);
        page.Children.Add(bar);
        page.Children.Add(Text("Автоподбор проверяет соединение через каждую стратегию. Для него остановите winws и удалите службу zapret.", 12, color: MutedBrush));
        var progressContent = new StackPanel { Spacing = 8 };
        strategyProgressText = Text("", 13, FontWeight.SemiBold);
        strategyProgressBar = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, vm.Strategies.Count), Height = 8 };
        progressContent.Children.Add(strategyProgressText);
        progressContent.Children.Add(strategyProgressBar);
        strategyProgressPanel = Panel(progressContent, 14);
        page.Children.Add(strategyProgressPanel);
        strategySummaryText = Text("", 13, FontWeight.SemiBold);
        strategySummaryPanel = Panel(strategySummaryText, 14);
        page.Children.Add(strategySummaryPanel);
        strategyRecommendationButton = Button("Применить рекомендацию", () => vm.ApplyRecommendationAsync());
        page.Children.Add(strategyRecommendationButton);
        StrategyHistoryPanel();
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
            var resultLabel = Text("", 12, color: MutedBrush);
            info.Children.Add(resultLabel);
            strategyResultLabels[Path.GetFileName(item.SourceFile)] = resultLabel;
            row.Children.Add(info);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 0, 0) };
            var select = EngineButton(selected ? "Выбрана" : "Выбрать", () => vm.SelectAsync(item), () => !selected,
                activityKey: "strategy-select:" + item.Id);
            actions.Children.Add(select);
            var start = EngineButton("Запустить", async () =>
            {
                if (await vm.SelectAsync(item)) await vm.StartAsync();
            }, () => vm.Status?.CanStart == true, primary: true, activityKey: "engine-start:" + item.Id);
            actions.Children.Add(start);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            strategyCards.Children.Add(Panel(row, 16, registrationMarks: false));
        }
        page.Children.Add(Panel(strategyCards, 10));
        RefreshStrategyResults();
    }

    private void RefreshStrategyResults()
    {
        if (strategyStartButton is not null) strategyStartButton.IsEnabled = !vm.Busy && vm.Strategies.Count > 0;
        if (strategyCancelButton is not null) strategyCancelButton.IsEnabled = vm.TestingStrategies;
        if (strategyRecommendationButton is not null)
            strategyRecommendationButton.IsEnabled = vm.BestTestStrategy is not null && !vm.Busy;
        if (strategySummaryPanel is not null) strategySummaryPanel.IsVisible = vm.StrategyTestSummary is not null;
        if (strategySummaryText is not null)
        {
            strategySummaryText.Text = (vm.StrategyTestSummary ?? "") +
                (vm.BestTestStrategy is null ? "" : " Нажмите «Применить рекомендацию», чтобы сохранить выбор.");
            strategySummaryText.Foreground = vm.BestTestStrategy is null ? MutedBrush : GoodBrush;
        }
        if (strategyProgressPanel is not null) strategyProgressPanel.IsVisible = vm.StrategyTestSummary is not null;
        var done = vm.StrategyChecks.Values.Count(x => x.State is StrategyCheckState.Completed or StrategyCheckState.Failed);
        var running = vm.StrategyChecks.Values.FirstOrDefault(x => x.State == StrategyCheckState.Running);
        if (strategyProgressText is not null)
            strategyProgressText.Text = $"Проверено {done} из {vm.Strategies.Count}" +
                (running is null ? "" : $" · Сейчас: {Path.GetFileNameWithoutExtension(running.FileName)}");
        if (strategyProgressBar is not null) strategyProgressBar.Value = done;
        foreach (var (fileName, label) in strategyResultLabels)
        {
            if (!vm.StrategyChecks.TryGetValue(fileName, out var result))
            {
                label.Text = "Ожидает проверки";
                label.Foreground = MutedBrush;
                continue;
            }
            label.Text = result.State switch
            {
                StrategyCheckState.Running => "Проверяется…",
                StrategyCheckState.Failed => "Не удалось запустить стратегию",
                StrategyCheckState.Cancelled => "Проверка прервана",
                _ => $"HTTP/TLS: {result.HttpOk} успешно, {result.HttpFailed} ошибок, {result.Unsupported} не поддерживается · " +
                    $"Ping: {result.PingOk} ответов, {result.PingFailed} без ответа"
            };
            if (string.Equals(fileName, vm.BestTestStrategy, StringComparison.OrdinalIgnoreCase))
                label.Text = "★ Рекомендуется · " + label.Text;
            label.Foreground = result.State switch
            {
                StrategyCheckState.Completed when result.HttpOk > 0 && result.HttpFailed == 0 => GoodBrush,
                StrategyCheckState.Completed when result.HttpOk == 0 => BadBrush,
                StrategyCheckState.Failed => BadBrush,
                StrategyCheckState.Running => AccentBrush,
                _ => WarnBrush
            };
        }
    }
}
