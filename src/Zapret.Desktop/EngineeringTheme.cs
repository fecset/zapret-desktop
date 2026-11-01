using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Zapret.Desktop;

/// <summary>Shared values from the approved FigCraft Engineering dashboard.</summary>
internal static class EngineeringTheme
{
    public static readonly FontFamily Font = new("avares://Zapret.Desktop/Assets/Fonts#JetBrains Mono");
    public const double SidebarWidth = 224;
    public const double CompactSidebarWidth = 220;
    public const double CompactBreakpoint = 1280;
    public const double Gutter = 32;
    public const double CompactGutter = 28;
    public const double SectionGap = 24;
    public const double CompactSectionGap = 20;
    public const double ControlHeight = 44;
    public const double ControlLineHeight = 20;
    public const double BrandMarkSize = 44;
    public const double SectionPadding = 20;
    public const double TitleBarHeight = 32;
    public const double ControlRadius = 5;
    public const double ActionWidth = 264;
    public const double CompactActionWidth = 264;

    public static void Install(Application app)
    {
        if (app.Styles.OfType<StyleInclude>().Any(s => s.Source?.OriginalString.EndsWith("Engineering.axaml") == true)) return;
        app.Styles.Add(new StyleInclude(new Uri("avares://Zapret.Desktop/"))
        {
            Source = new Uri("avares://Zapret.Desktop/Styles/Engineering.axaml")
        });
        // Keep Avalonia's native selection, text editing and checkbox behavior.
        // These are the Fluent 12.1.3 template resource contracts, mapped to our tokens.
        foreach (var light in new[] { false, true })
        {
            var theme = light ? ThemeVariant.Light : ThemeVariant.Dark;
            if (!app.Resources.ThemeDictionaries.TryGetValue(theme, out var existing) || existing is not ResourceDictionary resources)
                app.Resources.ThemeDictionaries[theme] = resources = new ResourceDictionary();
            void Map(string token, params string[] keys)
            {
                foreach (var key in keys) resources[key] = Brush(token, light);
            }
            Map("surface", "ComboBoxDropDownBackground", "ComboBoxBackground", "ComboBoxBackgroundUnfocused", "TextControlBackground", "TextControlBackgroundFocused");
            Map("border", "ComboBoxDropDownBorderBrush", "ComboBoxItemBorderBrushSelected", "ComboBoxItemBorderBrushSelectedPointerOver");
            Map("control", "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "ComboBoxBackgroundBorderBrushUnfocused", "TextControlBorderBrush",
                "TextControlBorderBrushPointerOver", "CheckBoxCheckBackgroundStrokeUnchecked");
            Map("text", "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed", "ComboBoxItemForeground",
                "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundPointerOver",
                "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver");
            Map("secondary", "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed",
                "ComboBoxPlaceHolderForeground", "TextControlPlaceholderForeground");
            Map("hover", "ComboBoxItemBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "TextControlBackgroundPointerOver");
            Map("active", "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundPressed", "ComboBoxBackgroundPressed");
            Map("hover", "SystemControlHighlightListLowBrush", "SystemControlHighlightListMediumBrush");
            Map("active", "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush", "SystemControlHighlightListAccentHighBrush");
            Map("text", "SystemControlHighlightAltBaseHighBrush");
            Map("info", "ComboBoxBackgroundBorderBrushFocused", "TextControlBorderBrushFocused", "SystemControlHighlightAccentBrush");
            Map("disabled", "ComboBoxBackgroundDisabled", "TextControlBackgroundDisabled");
            Map("disabledText", "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled", "TextControlForegroundDisabled");
            foreach (var state in new[] { "Checked", "Indeterminate" })
            foreach (var interaction in new[] { "", "PointerOver", "Pressed", "Disabled" })
            {
                Map(interaction == "Disabled" ? "disabled" : "primary", "CheckBoxCheckBackgroundFill" + state + interaction,
                    "CheckBoxCheckBackgroundStroke" + state + interaction);
                Map(interaction == "Disabled" ? "disabledText" : "onPrimary", "CheckBoxCheckGlyphForeground" + state + interaction);
                Map(interaction == "Disabled" ? "disabledText" : "text", "CheckBoxForeground" + state + interaction);
            }
        }
    }

    public static IBrush Brush(string token, bool light)
    {
        if (Application.Current!.TryGetResource("Engineering." + token,
                light ? ThemeVariant.Light : ThemeVariant.Dark, out var value) && value is IBrush brush)
            return brush;
        throw new InvalidOperationException("Missing engineering design token: " + token);
    }
}
