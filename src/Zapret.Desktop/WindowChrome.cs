using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private readonly List<Control> resizeGrips = [];

    private void AddResizeGrips(Grid root)
    {
        resizeGrips.Clear();
        Add(WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 4, StandardCursorType.SizeNorthSouth);
        Add(WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 4, StandardCursorType.SizeNorthSouth);
        Add(WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, 4, double.NaN, StandardCursorType.SizeWestEast);
        Add(WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, 4, double.NaN, StandardCursorType.SizeWestEast);
        Add(WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, 6, 6, StandardCursorType.TopLeftCorner);
        Add(WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, 6, 6, StandardCursorType.TopRightCorner);
        Add(WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, 6, 6, StandardCursorType.BottomLeftCorner);
        Add(WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, 6, 6, StandardCursorType.BottomRightCorner);

        void Add(WindowEdge edge, HorizontalAlignment horizontal, VerticalAlignment vertical, double width, double height, StandardCursorType cursor)
        {
            var grip = new Border { Width = width, Height = height, HorizontalAlignment = horizontal, VerticalAlignment = vertical,
                Background = Brushes.Transparent, Cursor = new Cursor(cursor), IsVisible = CanResize && WindowState == WindowState.Normal };
            AutomationProperties.SetAutomationId(grip, "Resize:" + edge);
            grip.PointerPressed += (_, e) =>
            {
                if (!CanResize || WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                BeginResizeDrag(edge, e);
                e.Handled = true;
            };
            resizeGrips.Add(grip);
            root.Children.Add(grip);
        }
    }
}
