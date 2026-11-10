using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Plugins;

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

    /// <summary>
    /// A table readout (<c>plugins.md</c> §5.2): a header row and the rows, every cell already formatted in its column's
    /// unit, numbers right-aligned. Brief 3's <c>table</c> view draws the same object.
    /// </summary>
    public static Control Table(PluginReadoutValue value)
    {
        if (!value.IsTable) return new SelectableTextBlock { Text = value.Text, FontSize = 12 };
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 2 };
        int cols = System.Math.Max(value.Columns!.Count, value.Rows!.Count == 0 ? 0 : value.Rows.Max(r => r.Count));
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r <= value.Rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < value.Columns.Count; c++)
            Put(new TextBlock { Text = value.Columns[c], FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.8 }, 0, c);
        for (int r = 0; r < value.Rows.Count; r++)
            for (int c = 0; c < value.Rows[r].Count; c++)
            {
                string text = value.Rows[r][c];
                bool numeric = text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.');
                Put(new SelectableTextBlock { Text = text, FontSize = 12, HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left }, r + 1, c);
            }
        return grid;

        void Put(Control cell, int row, int col) { Grid.SetRow(cell, row); Grid.SetColumn(cell, col); grid.Children.Add(cell); }
    }

    /// <summary>A theme resource as a brush: some keys hold a <see cref="Color"/>, which a brush property cannot take.</summary>
    public static object? AsBrush(object? value) => value is Color c ? new SolidColorBrush(c) : value;
}
