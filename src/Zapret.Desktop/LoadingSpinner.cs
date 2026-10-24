using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;

namespace Zapret.Desktop;

public sealed class LoadingSpinner : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LoadingSpinner, IBrush?>(nameof(Foreground), Brushes.White);
    private static readonly Geometry Arc = Geometry.Parse("M 8,1 A 7,7 0 1 1 1,8");
    private static readonly IBrush LightPrimaryStroke = new SolidColorBrush(Color.Parse("#6544C8"));
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool attached;
    private readonly List<Visual> visibilityAncestors = [];
    private double angle;
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public LoadingSpinner()
    {
        Width = Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        AutomationProperties.SetAutomationId(this, "operation-spinner");
        AutomationProperties.SetName(this, "Выполняется");
        timer.Tick += (_, _) => { angle = (angle + Math.PI / 15) % (2 * Math.PI); InvalidateVisual(); };
    }
    public override void Render(DrawingContext context)
    {
        var stroke = ActualThemeVariant == ThemeVariant.Light && Foreground is ISolidColorBrush { Color: var color } && color == Colors.White
            ? LightPrimaryStroke : Foreground;
        using (context.PushTransform(Matrix.CreateRotation(angle, new Point(8, 8))))
            context.DrawGeometry(null, new Pen(stroke, 2, lineCap: PenLineCap.Round), Arc);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += AncestorVisibilityChanged;
        }
        UpdateAnimation();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        timer.Stop();
        foreach (var ancestor in visibilityAncestors) ancestor.PropertyChanged -= AncestorVisibilityChanged;
        visibilityAncestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateAnimation();
        if (change.Property == ForegroundProperty) InvalidateVisual();
    }
    private void UpdateAnimation()
    {
        if (attached && IsVisible && visibilityAncestors.All(ancestor => ancestor.IsVisible)) timer.Start();
        else timer.Stop();
    }
    private void AncestorVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty) UpdateAnimation();
    }
}
