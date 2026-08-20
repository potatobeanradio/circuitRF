using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// The numbered accent dot an indexed figure pins to the thing its legend row is about.
///
/// <para>One definition, because there are now two indexed figure kinds — a toolbar's per-button
/// numbers (<see cref="ToolbarCatalog.WithCallouts"/>) and the workspace's per-region numbers
/// (<see cref="WorkspaceRegions"/>) — and two dots that were "the same" only by hand would drift
/// the first time either was retuned.</para>
/// </summary>
public static class CalloutDot
{
    /// <summary>The accent the dot is filled with, and the colour the number is drawn in on top of it.</summary>
    public static (Color Accent, Color OnAccent) Palette(ColorVariant variant)
        => variant == ColorVariant.Dark
            ? (Color.Parse("#56D7EE"), Color.Parse("#0E1820"))
            : (Color.Parse("#0E7C99"), Color.Parse("#FFFFFF"));

    /// <summary>A filled circle of <paramref name="diameter"/> carrying <paramref name="index"/>.</summary>
    public static Border Build(int index, ColorVariant variant, double diameter = 18)
    {
        var (accent, onAccent) = Palette(variant);

        return new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            Background = new SolidColorBrush(accent),
            Child = new TextBlock
            {
                Text = index.ToString(),
                FontSize = diameter * 10.5 / 18.0,   // exactly 10.5 at the toolbar's 18 px dot
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(onAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
