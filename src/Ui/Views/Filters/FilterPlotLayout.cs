using System;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Ui.Controls;

namespace CircuitRF.Ui.Views.Filters;

/// <summary>
/// Lays the Filter Designer's magnitude and phase plots out for whichever are on. One plot fills
/// the pane (S-parameter viewer's lone-rect aspect). Both on sit side by side with a gap — the
/// same row the window used before the toggles existed.
/// </summary>
public sealed class FilterPlotLayout : Panel
{
    /// <summary>Empty band between the two plots, in pixels.</summary>
    public const double Gap = 6;

    /// <summary>Width divided by height when one rectangular plot has the pane to itself.</summary>
    public const double LoneRectRatio = 1.7;

    public static readonly StyledProperty<bool> ShowMagnitudeProperty =
        AvaloniaProperty.Register<FilterPlotLayout, bool>(nameof(ShowMagnitude), defaultValue: true);

    public static readonly StyledProperty<bool> ShowPhaseProperty =
        AvaloniaProperty.Register<FilterPlotLayout, bool>(nameof(ShowPhase), defaultValue: false);

    static FilterPlotLayout()
    {
        AffectsMeasure<FilterPlotLayout>(ShowMagnitudeProperty, ShowPhaseProperty);
        AffectsArrange<FilterPlotLayout>(ShowMagnitudeProperty, ShowPhaseProperty);
    }

    public bool ShowMagnitude { get => GetValue(ShowMagnitudeProperty); set => SetValue(ShowMagnitudeProperty, value); }
    public bool ShowPhase { get => GetValue(ShowPhaseProperty); set => SetValue(ShowPhaseProperty, value); }

    /// <summary>Where each plot goes. A default rect is a plot that is off.</summary>
    public readonly record struct Slots(Rect Magnitude, Rect Phase);

    /// <summary>The rectangles the two hosts occupy in a pane of <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static Slots SlotsFor(bool magnitude, bool phase, double width, double height)
    {
        double w = Math.Max(0, width), h = Math.Max(0, height);
        if (!magnitude && !phase) return default;
        if (magnitude && !phase) return new Slots(Alone(w, h), default);
        if (!magnitude && phase) return new Slots(default, Alone(w, h));
        double col = Math.Max(0, (w - Gap) / 2);
        return new Slots(new Rect(0, 0, col, h), new Rect(col + Gap, 0, col, h));
    }

    private static Rect Alone(double w, double h)
    {
        var size = AspectRatioPanel.Fit(new Size(w, h), LoneRectRatio);
        return new Rect((w - size.Width) / 2, (h - size.Height) / 2, size.Width, size.Height);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size available)
    {
        double w = Finite(available.Width), hgt = Finite(available.Height);
        if (w <= 0 || hgt <= 0) return new Size(w, hgt);
        var slots = SlotsFor(ShowMagnitude, ShowPhase, w, hgt);
        foreach (var child in Children)
        {
            if (child is not Control c) continue;
            var slot = SlotOf(c, slots);
            c.Measure(slot is { } r ? r.Size : new Size(w, hgt));
        }
        return new Size(w, hgt);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var slots = SlotsFor(ShowMagnitude, ShowPhase, finalSize.Width, finalSize.Height);
        var full = new Rect(finalSize);
        foreach (var child in Children)
        {
            if (child is not Control c) continue;
            c.Arrange(SlotOf(c, slots) ?? full);
        }
        return finalSize;
    }

    private static Rect? SlotOf(Control child, Slots slots) => child.Name switch
    {
        "MagnitudeHost" => slots.Magnitude.Width > 0 ? slots.Magnitude : new Rect(0, 0, 0, 0),
        "PhaseHost"     => slots.Phase.Width > 0 ? slots.Phase : new Rect(0, 0, 0, 0),
        _               => null,
    };

    private static double Finite(double v) => double.IsFinite(v) && v > 0 ? v : 0;
}
