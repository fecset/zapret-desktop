using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Zapret.Desktop;

internal enum WindowCaptionGlyph { Minimize, Maximize, Restore, Close }

/// <summary>Official Fluent UI System Icons, 16 Regular, in a shared 16×16 slot.</summary>
internal sealed class WindowCaptionIcon : Control
{
    public static readonly StyledProperty<WindowCaptionGlyph> GlyphProperty =
        AvaloniaProperty.Register<WindowCaptionIcon, WindowCaptionGlyph>(nameof(Glyph));
    public WindowCaptionGlyph Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public IBrush Foreground { get; init; } = Brushes.Gray;
    static WindowCaptionIcon() => AffectsRender<WindowCaptionIcon>(GlyphProperty);
    public WindowCaptionIcon()
    {
        Width = Height = 16; Name = "WindowCaptionGlyph"; IsHitTestVisible = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Antialias);
    }

    public override void Render(DrawingContext context)
    {
        var geometry = Glyph switch
        {
            WindowCaptionGlyph.Minimize => FluentIcons.Subtract,
            WindowCaptionGlyph.Maximize => FluentIcons.Maximize,
            WindowCaptionGlyph.Restore => FluentIcons.SquareMultiple,
            _ => FluentIcons.Dismiss
        };
        context.DrawGeometry(Foreground, null, geometry);
    }
}
