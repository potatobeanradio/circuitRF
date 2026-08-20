using System;
using Avalonia;
using Avalonia.Controls;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Gives its child a fixed width-to-height ratio, taking as much of the offered width as it can and
/// deriving the height from it (or the other way round when the height is what is scarce).
/// </summary>
/// <remarks>
/// <b>Avalonia has no aspect-ratio panel and a <c>Viewbox</c> is not one</b> — a Viewbox SCALES its
/// child, which for a plot means scaling the axis text and the line widths along with the frame. The
/// Match Designer's two response plots are asked to be golden-ratio (owner, 2026-08-19), and what
/// that has to mean is that the plot is LAID OUT at that shape and draws itself at its native
/// stroke weights.
///
/// <para>Used in an <c>Auto</c> grid row, the panel is offered a finite width and an infinite height,
/// so the height simply follows the width. In a <c>*</c> row both are finite and whichever constraint
/// binds first wins, which keeps the pane usable when the window is short.</para>
/// </remarks>
public sealed class AspectRatioPanel : Decorator
{
    /// <summary>The golden ratio, 1.618… — this panel's default and the only value in use today.</summary>
    public const double Golden = 1.6180339887498949;

    /// <summary>Width divided by height. Must be finite and positive; anything else is ignored.</summary>
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectRatioPanel, double>(nameof(Ratio), Golden);

    static AspectRatioPanel() => AffectsMeasure<AspectRatioPanel>(RatioProperty);

    /// <inheritdoc cref="RatioProperty"/>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        double ratio = Ratio;
        if (!double.IsFinite(ratio) || ratio <= 0) return base.MeasureOverride(availableSize);

        var size = Fit(availableSize, ratio);
        Child?.Measure(size);
        return size;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        double ratio = Ratio;
        var size = double.IsFinite(ratio) && ratio > 0 ? Fit(finalSize, ratio) : finalSize;
        Child?.Arrange(new Rect(
            (finalSize.Width - size.Width) / 2, (finalSize.Height - size.Height) / 2,
            size.Width, size.Height));
        return finalSize;
    }

    /// <summary>The largest box of the given ratio that fits inside <paramref name="available"/>.</summary>
    /// <remarks>
    /// An infinite dimension is not a constraint — it is the one the other dimension DERIVES. Both
    /// infinite is a degenerate ask (nothing bounds the layout at all) and falls back to zero rather
    /// than to an infinity that would propagate into an arrange pass.
    /// </remarks>
    public static Size Fit(Size available, double ratio)
    {
        bool wFinite = double.IsFinite(available.Width);
        bool hFinite = double.IsFinite(available.Height);

        if (wFinite && hFinite)
            return available.Width / ratio <= available.Height
                ? new Size(available.Width, available.Width / ratio)
                : new Size(available.Height * ratio, available.Height);

        if (wFinite) return new Size(available.Width, available.Width / ratio);
        if (hFinite) return new Size(available.Height * ratio, available.Height);
        return new Size(0, 0);
    }
}
