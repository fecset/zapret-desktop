using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Zapret.Desktop;

internal static class BrandMark
{
    // Paths and gradient stops are the vector artwork in Assets/app.svg.
    public static Control Create(double size)
    {
        var artwork = new Canvas { Width = 256, Height = 256 };
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(9d / 224, 9d / 224, RelativeUnit.Relative),
            EndPoint = new RelativePoint(208d / 224, 214d / 224, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse("#347BFF"), 0),
                new GradientStop(Color.Parse("#315EF6"), 0.52),
                new GradientStop(Color.Parse("#7755EF"), 1)
            ]
        };

        artwork.Children.Add(new ShapePath
        {
            Data = Geometry.Parse("M128 240C189.856 240 240 189.856 240 128C240 66.1441 189.856 16 128 16C66.1441 16 16 66.1441 16 128C16 189.856 66.1441 240 128 240Z"),
            Fill = gradient,
            Width = 256,
            Height = 256,
            Stretch = Stretch.None
        });
        artwork.Children.Add(new ShapePath
        {
            Data = Geometry.Parse("M151 43L76 132H124L103 214L182 113H135L151 43Z"),
            Fill = Brushes.White,
            Width = 256,
            Height = 256,
            Stretch = Stretch.None
        });
        artwork.Children.Add(new ShapePath
        {
            Data = Geometry.Parse("M76 132L87 119H155L135 145H88L76 132Z"),
            Fill = new SolidColorBrush(Color.Parse("#DCE9FF"), 0.55),
            Width = 256,
            Height = 256,
            Stretch = Stretch.None
        });

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = artwork
        };
    }
}
