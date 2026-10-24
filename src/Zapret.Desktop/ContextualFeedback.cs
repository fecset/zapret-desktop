using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private Button MessageDismissButton()
    {
        var button = new Button
        {
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = StreamGeometry.Parse("M1,1 L11,11 M11,1 L1,11"),
                Width = 12, Height = 12, Stroke = MutedBrush, StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            },
            Width = 28, Height = 28, MinWidth = 28, MinHeight = 28,
            MaxWidth = 28, MaxHeight = 28, Padding = new Thickness(0),
            Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, "Скрыть сообщение");
        ToolTip.SetTip(button, "Скрыть сообщение");
        button.Click += (_, _) => { feedbackTimer.Stop(); feedbackPanel.IsVisible = false; };
        return button;
    }

    private bool StrategyTestRestrictionNotice()
    {
        if (!vm.TestingStrategies) return false;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Text(MainViewModel.StrategyTestRestriction, 13, color: WarnBrush));
        content.Children.Add(Text("Тест сам запускает движок и временно меняет IPSet. После завершения эти действия снова станут доступны.", 13, color: MutedBrush));
        content.Children.Add(Button("Перейти к автоподбору", () => { Show("Стратегии"); return Task.CompletedTask; }));
        page.Children.Add(Panel(content, 16));
        return true;
    }
}
