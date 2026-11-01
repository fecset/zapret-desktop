using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Zapret.Desktop;

/// <summary>Shared section frame, including the registration marks used throughout the application.</summary>
internal sealed class EngineeringSection : Border
{
    public IBrush NodeBrush { get; init; } = Brushes.Gray;

    public EngineeringSection(Control content, Thickness padding)
    {
        var grid = new Grid();
        grid.Children.Add(new Border { Child = content, Padding = padding });
        grid.Children.Add(new RegistrationMarks(this) { IsHitTestVisible = false });
        Child = grid;
    }

    private sealed class RegistrationMarks(EngineeringSection owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var pen = new Pen(owner.BorderBrush, 1);
            foreach (var x in new[] { -0.5, Bounds.Width + 0.5 })
            foreach (var y in new[] { -0.5, Bounds.Height + 0.5 })
            {
                context.DrawLine(pen, new Point(x - 3, y), new Point(x + 3, y));
                context.DrawLine(pen, new Point(x, y - 3), new Point(x, y + 3));
                context.DrawRectangle(owner.NodeBrush, null, new Rect(x - 1, y - 1, 2, 2));
            }
        }
    }
}
