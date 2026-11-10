using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CircuitRF.Ui.Plugins;

/// <summary>Small pieces the plugin dialogs share, built in code as <c>CellExistsDialog</c> is.</summary>
internal static class PluginUi
{
    /// <summary>The bordered tile <c>PCellTrustDialog</c> draws its details in, following the theme.</summary>
    public static Border Tile(Control child)
    {
        var border = new Border
        {
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = child,
        };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("CrfTileBorderBrush", AsBrush));
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("SystemChromeLowColor", AsBrush));
        return border;
    }

    /// <summary>
    /// The width of a box holding about <paramref name="chars"/> characters in <paramref name="box"/>'s own font —
    /// measured, so it follows the theme's font and size rather than assuming one. Digits are the measure because
    /// they are what most plugin fields hold, and every font draws them at one width.
    /// </summary>
    public static double TypedWidth(TextBox box, int chars)
    {
        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight);
        var text = new FormattedText(new string('0', chars), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     typeface, box.FontSize, null);
        // The template's padding and border are not applied until the box is styled; this is what they add.
        const double Chrome = 22;
        return System.Math.Ceiling(text.Width + Chrome);
    }

    /// <summary>A theme resource as a brush: some keys hold a <see cref="Color"/>, which a brush property cannot take.</summary>
    public static object? AsBrush(object? value) => value is Color c ? new SolidColorBrush(c) : value;
}
