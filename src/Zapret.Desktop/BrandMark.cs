using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Zapret.Desktop;

internal static class BrandMark
{
    // Matches the single master artwork in Assets/app.svg.
    private static readonly Geometry Bolt = Geometry.Parse("M157 38 L58 132 H121 L94 218 L198 112 H136 Z");
    public static WindowIcon CreateWindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Zapret.Desktop/Assets/app.ico"));
        return new WindowIcon(stream);
    }

    public static Control Create(double size, IBrush? surface = null, IBrush? foreground = null) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 29 / 256),
        Background = surface ?? new SolidColorBrush(Color.Parse("#222428")),
        Child = new Avalonia.Controls.Shapes.Path
        {
            Data = Bolt, Fill = foreground ?? new SolidColorBrush(Color.Parse("#E5E5E5")),
            Width = size * 140 / 256, Height = size * 180 / 256, Stretch = Stretch.Fill,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        }
    };
}
