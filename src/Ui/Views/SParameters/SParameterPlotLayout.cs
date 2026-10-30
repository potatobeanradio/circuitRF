using System;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Ui.Controls;

namespace CircuitRF.Ui.Views.SParameters;

/// <summary>
/// Lays the viewer's four plots out for whichever are on. One plot fills the pane. Smith and polar
/// sit side by side. Magnitude and phase, with neither round chart, stack, magnitude on top. Two of
/// different shapes stack, the round chart on top. All four are Smith and polar across the top,
/// magnitude and phase across the bottom.
/// </summary>
public sealed class SParameterPlotLayout : Panel
{
    /// <summary>Empty band between two plots, in pixels.</summary>
    public const double Gap = 22;

    /// <summary>Width divided by height for a rectangular plot that has the pane to itself.</summary>
    public const double LoneRectRatio = 1.7;

    public static readonly StyledProperty<bool> ShowSmithProperty =
        AvaloniaProperty.Register<SParameterPlotLayout, bool>(nameof(ShowSmith));

    public static readonly StyledProperty<bool> ShowPolarProperty =
        AvaloniaProperty.Register<SParameterPlotLayout, bool>(nameof(ShowPolar));

    public static readonly StyledProperty<bool> ShowMagnitudeProperty =
        AvaloniaProperty.Register<SParameterPlotLayout, bool>(nameof(ShowMagnitude));

    public static readonly StyledProperty<bool> ShowPhaseProperty =
        AvaloniaProperty.Register<SParameterPlotLayout, bool>(nameof(ShowPhase));

    static SParameterPlotLayout()
    {
        AffectsMeasure<SParameterPlotLayout>(ShowSmithProperty, ShowPolarProperty, ShowMagnitudeProperty, ShowPhaseProperty);
        AffectsArrange<SParameterPlotLayout>(ShowSmithProperty, ShowPolarProperty, ShowMagnitudeProperty, ShowPhaseProperty);
    }

    public bool ShowSmith { get => GetValue(ShowSmithProperty); set => SetValue(ShowSmithProperty, value); }
    public bool ShowPolar { get => GetValue(ShowPolarProperty); set => SetValue(ShowPolarProperty, value); }
    public bool ShowMagnitude { get => GetValue(ShowMagnitudeProperty); set => SetValue(ShowMagnitudeProperty, value); }
    public bool ShowPhase { get => GetValue(ShowPhaseProperty); set => SetValue(ShowPhaseProperty, value); }

    /// <summary>Where each plot goes. A default rect is a plot that is off.</summary>
    public readonly record struct Slots(Rect Smith, Rect Polar, Rect Magnitude, Rect Phase);

    /// <summary>The rectangles the four hosts occupy in a pane of <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static Slots SlotsFor(bool smith, bool polar, bool magnitude, bool phase, double width, double height)
    {
        double w = Math.Max(0, width), h = Math.Max(0, height);
        int round = (smith ? 1 : 0) + (polar ? 1 : 0);
        int rect  = (magnitude ? 1 : 0) + (phase ? 1 : 0);
        if (round + rect == 0) return default;
        if (round + rect == 1) return Alone(smith, polar, magnitude, phase, w, h);
        // Magnitude and phase alone are one column. Beside a round chart they stay one row.
        if (round == 0 && magnitude && phase) return StackedRects(w, h);

        double row = Math.Max(0, (h - (round > 0 && rect > 0 ? Gap : 0)) / (round > 0 && rect > 0 ? 2 : 1));
        double yRect = round > 0 && rect > 0 ? row + Gap : 0;
        double hRound = round > 0 && rect > 0 ? row : h;
        double hRect = round > 0 && rect > 0 ? row : h;

        var top = Pair(smith, polar, 0, hRound, w);
        var bot = Pair(magnitude, phase, yRect, hRect, w);
        return new Slots(
            top.A.Width > 0 ? top.A : default,
            top.B.Width > 0 ? top.B : default,
            bot.A.Width > 0 ? bot.A : default,
            bot.B.Width > 0 ? bot.B : default);
    }

    /// <summary>One plot, filling the pane the way it did before polar existed.</summary>
    private static Slots Alone(bool smith, bool polar, bool magnitude, bool phase, double w, double h)
    {
        if (smith || polar)
        {
            var disc = RoundHost(w, h);
            return smith ? new Slots(disc, default, default, default) : new Slots(default, disc, default, default);
        }
        var size = AspectRatioPanel.Fit(new Size(w, h), LoneRectRatio);
        var box = new Rect((w - size.Width) / 2, (h - size.Height) / 2, size.Width, size.Height);
        return magnitude ? new Slots(default, default, box, default) : new Slots(default, default, default, box);
    }

    /// <summary>Magnitude above phase, each the full width of the pane.</summary>
    private static Slots StackedRects(double w, double h)
    {
        double row = Math.Max(0, (h - Gap) / 2);
        return new Slots(default, default, new Rect(0, 0, w, row), new Rect(0, row + Gap, w, row));
    }

    /// <summary>One or two plots of one shape across a row. A single one takes the whole row.</summary>
    private static (Rect A, Rect B) Pair(bool a, bool b, double y, double rowH, double w)
    {
        if (a && b)
        {
            double col = Math.Max(0, (w - Gap) / 2);
            return (new Rect(0, y, col, rowH), new Rect(col + Gap, y, col, rowH));
        }
        if (a) return (new Rect(0, y, w, rowH), default);
        if (b) return (default, new Rect(0, y, w, rowH));
        return default;
    }

    /// <summary>A round chart's host: wide enough that the centred disc leaves a strip for its names.</summary>
    private static Rect RoundHost(double w, double h)
    {
        const double names = 120;
        double disc = Math.Min(w, h);
        if ((w - disc) / 2 >= names || w <= names + 160)
            return new Rect(0, 0, w, h);
        double d = Math.Max(160, Math.Min(h, w - 2 * names));
        return new Rect(0, Math.Max(0, (h - d) / 2), w, d);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size available)
    {
        double w = Finite(available.Width), hgt = Finite(available.Height);
        if (w <= 0 || hgt <= 0) return new Size(w, hgt);
        var slots = SlotsFor(ShowSmith, ShowPolar, ShowMagnitude, ShowPhase, w, hgt);
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
        var slots = SlotsFor(ShowSmith, ShowPolar, ShowMagnitude, ShowPhase, finalSize.Width, finalSize.Height);
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
        "SmithHost"     => slots.Smith.Width > 0 ? slots.Smith : new Rect(0, 0, 0, 0),
        "PolarHost"     => slots.Polar.Width > 0 ? slots.Polar : new Rect(0, 0, 0, 0),
        "MagnitudeHost" => slots.Magnitude.Width > 0 ? slots.Magnitude : new Rect(0, 0, 0, 0),
        "PhaseHost"     => slots.Phase.Width > 0 ? slots.Phase : new Rect(0, 0, 0, 0),
        _               => null,
    };

    private static double Finite(double v) => double.IsFinite(v) && v > 0 ? v : 0;
}
