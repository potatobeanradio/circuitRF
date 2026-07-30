// ================================================================
//  RectYAxisLabelSpacingTests.cs
//
//  Owner report: on a Rect plot, traces on the RIGHT y-axis sometimes had their rotated
//  per-trace Y labels rendered overlapping the right axis's tick numbers.
//
//  Root cause: the label-column anchor reserved room for only the window ENDPOINTS
//  (Top/Bottom), while tick numbers are drawn at EVERY major gridline — an intermediate
//  tick can format wider than both endpoints, so the label column sat too close.
//  Affects both axes; the right is the more frequent offender because SecondaryShareGrid
//  (default true) interpolates its tick values from the primary's fractions, routinely
//  producing long decimals like "0.7" / "-9.73" between round endpoints like "0" and "7".
//
//  SkiaFonts.PlexRegular cannot load headlessly (src/Ui/CLAUDE.md), so these drive the
//  font-taking overloads with SKTypeface.Default — the value-selection logic under test is
//  typeface-independent.
// ================================================================

using Avalonia;
using CircuitRF.Ui.DataDisplay;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class RectYAxisLabelSpacingTests
{
    private static SKFont TickFont() => new(SKTypeface.Default, 12f);

    /// <summary>The pre-fix measurement: window endpoints only.</summary>
    private static float EndpointsOnlyWidth(SKFont f, Axes axes, bool secondary)
    {
        var win    = secondary ? axes.WindowSecondary : axes.Window;
        int digits = secondary ? axes.NumDigitsRightY : axes.NumDigitsLeftY;
        return System.Math.Max(f.MeasureText(win.Top   .ToString($"G{digits}")),
                               f.MeasureText(win.Bottom.ToString($"G{digits}")));
    }

    /// <summary>
    /// INDEPENDENT oracle for the geometric-invariant test below: re-derives the widest tick
    /// number DrawRectGrid actually paints, straight from the tick set and that method's own
    /// documented formatting — deliberately NOT by calling MaxYTickLabelWidth, which is the code
    /// under test. Deriving both sides of the comparison from the same function would make the
    /// assertion self-consistent and therefore vacuous (it passed against the broken code that way).
    /// </summary>
    private static float WidestDrawnTickWidth(SKFont f, Axes axes, bool secondary)
    {
        int digits = secondary ? axes.NumDigitsRightY : axes.NumDigitsLeftY;
        float max = 0f;
        foreach (var (primary, secondaryValue) in axes.Ticks(minorTicks: false).MajorY)
        {
            if (!double.IsFinite(primary)) continue;
            double raw = secondary ? secondaryValue : primary;
            if (!double.IsFinite(raw)) continue;
            double v = System.Math.Abs(raw) < 1e-12 ? 0 : raw;
            max = System.Math.Max(max, f.MeasureText(v.ToString($"G{digits}")));
        }
        return max;
    }

    // ── Right axis: shared-grid interpolation puts long decimals between round endpoints ──

    [Fact]
    public void RightYAxis_SharedGridInterpolatedTicks_ReservesTheWidestDrawnLabel_NotJustEndpoints()
    {
        var axes = new Axes { ShowSecondary = true };
        axes.Window          = new Rect(0, -100, 10, 200);   // primary ticks at -80,-40,0,40,80
        axes.WindowSecondary = new Rect(0,    0, 10,   7);   // → secondary 0.7, 2.1, 3.5, 4.9, 6.3

        using var f = TickFont();

        // Every drawn tick here is "N.N"; the endpoints alone are the single chars "0" and "7".
        float widestDrawn = f.MeasureText("0.7");
        float measured    = AxesRenderer.MaxYTickLabelWidth(f, axes, secondary: true);

        Assert.Equal(widestDrawn, measured, 3);
        Assert.True(measured > EndpointsOnlyWidth(f, axes, secondary: true),
            "the endpoints-only measurement must under-reserve here — that is the reported bug");
    }

    // ── Left axis: same defect, reachable with a half-step tick interval ──────────────────

    [Fact]
    public void LeftYAxis_HalfStepTicks_ReservesTheWidestDrawnLabel_NotJustEndpoints()
    {
        var axes = new Axes();
        axes.Window = new Rect(0, -100, 10, 200);
        axes.YTick  = 6.25; axes.MajorY = 2;                 // 12.5 step → …, -87.5, -75, …
        // NOTE: YTick must be assigned AFTER Window — the Window setter calls SetTicks(),
        // which recomputes YTick from the window height and would overwrite it.

        using var f = TickFont();

        float widestDrawn = f.MeasureText("-87.5");          // wider than the "-100" endpoint
        float measured    = AxesRenderer.MaxYTickLabelWidth(f, axes, secondary: false);

        Assert.Equal(widestDrawn, measured, 3);
        Assert.True(measured > EndpointsOnlyWidth(f, axes, secondary: false),
            "left axis has the identical defect — endpoints are not the widest drawn label");
    }

    // ── The geometric invariant the fix exists to guarantee ───────────────────────────────

    [Theory]
    [InlineData(0.0, 7.0)]        // shared-grid interpolation → "0.7", "2.1", …
    [InlineData(-10.0, 10.0)]
    [InlineData(0.0, 1.0)]
    public void YLabelColumns_ClearEveryDrawnTickNumber_OnBothAxes(double secTop, double secBottom)
    {
        var axes = new Axes { ShowSecondary = true };
        axes.Window          = new Rect(0, -100, 10, 200);
        axes.WindowSecondary = new Rect(0, secTop, 10, secBottom - secTop);

        using var f = TickFont();
        const float vpLeft = 100f, vpRight = 500f, lw = 2f;

        var (leftAnchor, rightAnchor) =
            AxesRenderer.ComputeYLabelAnchors(f, axes, vpLeft, vpRight, lw);

        // Mirrors DrawRectGrid's own tick-number placement exactly, with the extent derived from
        // the INDEPENDENT oracle above (never from the code under test):
        //   left  numbers are right-aligned ending at vpLeft - 4lw  → they extend LEFT  by maxW
        //   right numbers start at            vpRight + 4lw         → they extend RIGHT by maxW
        float leftTextLeftEdge   = vpLeft  - lw * 4f - WidestDrawnTickWidth(f, axes, secondary: false);
        float rightTextRightEdge = vpRight + lw * 4f + WidestDrawnTickWidth(f, axes, secondary: true);

        Assert.True(leftAnchor <= leftTextLeftEdge,
            $"left label column (right edge {leftAnchor}) must clear the tick numbers (left edge {leftTextLeftEdge})");
        Assert.True(rightAnchor >= rightTextRightEdge,
            $"right label column (left edge {rightAnchor}) must clear the tick numbers (right edge {rightTextRightEdge})");
    }

    // ── Degenerate axis must still leave the label off the axis line ──────────────────────

    [Fact]
    public void ZeroHeightWindow_StillReservesNonZeroWidth_LabelNeverFlushAgainstTheAxis()
    {
        var axes = new Axes { ShowSecondary = true };
        axes.Window          = new Rect(0, 5, 10, 0);
        axes.WindowSecondary = new Rect(0, 5, 10, 0);

        using var f = TickFont();
        Assert.True(AxesRenderer.MaxYTickLabelWidth(f, axes, secondary: false) > 0f);
        Assert.True(AxesRenderer.MaxYTickLabelWidth(f, axes, secondary: true)  > 0f);
    }
}
