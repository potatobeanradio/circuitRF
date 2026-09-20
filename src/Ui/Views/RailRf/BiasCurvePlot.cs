using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

/// <summary>
/// The selected part's capacitance-versus-bias curve, drawn.
/// </summary>
/// <remarks>
/// <b>R-rail24-1c's second half, and it is not decoration:</b> "a monotonic curve with one transposed
/// point is invisible in a grid and obvious on a chart". A derating table is entered by hand from a
/// datasheet graph, which is exactly the transcription a swapped pair of rows survives unnoticed —
/// and the consequence is a rail answer derated by the wrong factor, with nothing anywhere saying so.
///
/// <para><b>It draws the MARKED capacitance as the top of the scale</b>, rather than the curve's own
/// maximum, because the question a reader has is <i>how much of what I bought is left</i>. A curve
/// auto-scaled to itself always fills the box and therefore always looks the same, which answers
/// nothing.</para>
///
/// <para>An ordinary Avalonia <c>Control</c> and not a Skia renderer: this is a panel decoration of a
/// handful of points, not design content, so it has no business in <c>CircuitRF.Render</c> — that
/// project draws documents.</para>
/// </remarks>
public sealed class BiasCurvePlot : Control
{
    public static readonly StyledProperty<IReadOnlyList<PartBiasPoint>?> CurveProperty =
        AvaloniaProperty.Register<BiasCurvePlot, IReadOnlyList<PartBiasPoint>?>(nameof(Curve));

    /// <summary>The row's marked capacitance, which sets the top of the vertical scale. Null falls
    /// back to the curve's own largest point.</summary>
    public static readonly StyledProperty<double?> MarkedCapacitanceProperty =
        AvaloniaProperty.Register<BiasCurvePlot, double?>(nameof(MarkedCapacitance));

    public IReadOnlyList<PartBiasPoint>? Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public double? MarkedCapacitance
    {
        get => GetValue(MarkedCapacitanceProperty);
        set => SetValue(MarkedCapacitanceProperty, value);
    }

    static BiasCurvePlot()
    {
        AffectsRender<BiasCurvePlot>(CurveProperty, MarkedCapacitanceProperty);
    }

    /// <summary>Room for the two axis labels, on all four sides. The left gutter is the widest
    /// because it carries a percentage.</summary>
    private const double GutterLeft = 34, GutterBottom = 20, GutterTop = 10, GutterRight = 10;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var axis  = new Pen(new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), 1);
        var faint = new SolidColorBrush(Color.FromArgb(160, 128, 128, 128));

        double w = Bounds.Width  - GutterLeft - GutterRight;
        double h = Bounds.Height - GutterTop  - GutterBottom;
        if (w <= 4 || h <= 4) return;

        var origin = new Point(GutterLeft, GutterTop + h);
        context.DrawLine(axis, origin, new Point(GutterLeft + w, GutterTop + h));
        context.DrawLine(axis, new Point(GutterLeft, GutterTop), origin);

        var curve = Curve;
        if (curve is not { Count: > 0 })
        {
            DrawLabel(context, faint, "No bias curve on this part.", GutterLeft + 8, GutterTop + h / 2 - 7);
            return;
        }

        double maxBias = 0, maxCap = 0;
        foreach (var p in curve)
        {
            maxBias = Math.Max(maxBias, p.BiasVolts);
            maxCap  = Math.Max(maxCap,  p.CapacitanceFarads);
        }
        if (MarkedCapacitance is { } marked && marked > maxCap) maxCap = marked;
        if (!(maxBias > 0)) maxBias = 1;
        if (!(maxCap  > 0)) maxCap  = 1;

        Point Map(PartBiasPoint p) => new(
            GutterLeft + w * (p.BiasVolts / maxBias),
            GutterTop  + h * (1 - p.CapacitanceFarads / maxCap));

        // The line first, then the markers on top of it — a transposed point shows as a marker off
        // the run of the line, which is only legible if the marker is not under it.
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(230, 30, 110, 220)), 1.6);
        for (int i = 1; i < curve.Count; i++)
            context.DrawLine(stroke, Map(curve[i - 1]), Map(curve[i]));

        var marker = new SolidColorBrush(Color.FromArgb(255, 30, 110, 220));
        foreach (var p in curve)
            context.DrawEllipse(marker, null, Map(p), 3, 3);

        DrawLabel(context, faint, "0", GutterLeft - 8, GutterTop + h + 3);
        DrawLabel(context, faint, Volts(maxBias), GutterLeft + w - 22, GutterTop + h + 3);
        DrawLabel(context, faint, "100 %", 2, GutterTop - 2);
    }

    private static string Volts(double v) =>
        v.ToString(v >= 10 ? "0" : "0.##", CultureInfo.InvariantCulture) + " V";

    private static void DrawLabel(DrawingContext context, IBrush brush, string text, double x, double y)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 9.5, brush);
        context.DrawText(formatted, new Point(x, y));
    }
}
