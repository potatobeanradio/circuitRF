using Avalonia;
using Avalonia.Controls;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Texts of different sizes side by side on one baseline. Centring them separately in a row does not do it: each lands on
/// its own rounded pixel, so the smaller text's baseline drifts from the larger's by a device pixel or so at some display
/// scales and lines up at others. Here the two offsets are worked out together, from the baselines the text reports.
/// </summary>
public sealed class TextBaselineRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TextBaselineRow, double>(nameof(Spacing));

    static TextBaselineRow() => AffectsMeasure<TextBaselineRow>(SpacingProperty);

    /// <summary>The gap between neighbouring children.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static double BaselineOf(Control child) => child is TextBlock { TextLayout.TextLines: [var line, ..] } text
        ? text.Margin.Top + text.Padding.Top + line.Baseline
        : child.DesiredSize.Height;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, above = 0, below = 0;
        int shown = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (!child.IsVisible) continue;
            double baseline = BaselineOf(child);
            width += child.DesiredSize.Width;
            above = Math.Max(above, baseline);
            below = Math.Max(below, child.DesiredSize.Height - baseline);
            shown++;
        }
        return new Size(width + Spacing * Math.Max(0, shown - 1), above + below);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double above = Children.Where(c => c.IsVisible).Select(BaselineOf).DefaultIfEmpty(0).Max();
        double x = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Arrange(new Rect(x, above - BaselineOf(child), child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }
        return finalSize;
    }
}
