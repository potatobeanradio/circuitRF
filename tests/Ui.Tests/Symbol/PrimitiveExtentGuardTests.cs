using System.Globalization;
using Avalonia.Data;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Converters;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>A resize can never leave a primitive with a size it cannot come back from</b> (owner report,
/// 2026-08-26): shrink a circle in the Symbol Editor and eventually it stops rendering, with
/// <c>System.InvalidCastException: Could not convert '(unset)' … to System.Double</c> filling the
/// Properties Inspector's R field where the number should be.
///
/// <para><b>One root cause, three symptoms.</b> Dragging a resize gripper past its anchor flips an
/// axis, and it flips ONE axis first — the pointer crosses the anchor in x before it crosses in y.
/// A circle scaled that way took <c>Math.Sqrt(sx * sy)</c> of a NEGATIVE product and landed on NaN.
/// Nothing then draws; the bounding box is NaN too, so the resize grippers vanish and the shape
/// cannot be dragged back out; and the NaN reaches the inspector, where <c>Convert.ToDecimal</c>
/// throws (decimal has no NaN), the field empties, the empty field writes back, and a
/// <c>ConvertBack</c> answering <c>UnsetValue</c> means "there is no value" — which the binding then
/// tried to store in a non-nullable double and reported that it could not. That last step is the
/// message the user saw; it was three removes from the actual fault.</para>
/// </summary>
public class PrimitiveExtentGuardTests
{
    // ── The fault itself ──────────────────────────────────────────────────────

    /// <summary>
    /// The exact gesture: a gripper dragged past its anchor in ONE axis. Both signs of both axes are
    /// covered, because the mixed-sign pair is the only one that used to produce NaN and it is easy
    /// to "fix" in a way that only handles the both-negative case.
    /// </summary>
    [Theory]
    [InlineData(-0.2,  0.5)]
    [InlineData( 0.5, -0.2)]
    [InlineData(-0.5, -0.2)]
    [InlineData(-1.0,  1.0)]
    public void ARadiusSurvivesAGripperDraggedPastItsAnchor(double sx, double sy)
    {
        var c = new CirclePrimitive { Cx = 0, Cy = 0, R = 100 };
        var a = new ArcPrimitive    { Cx = 0, Cy = 0, R = 100 };

        SymbolGeometry.ScaleBy(c, 0, 0, sx, sy);
        SymbolGeometry.ScaleBy(a, 0, 0, sx, sy);

        Assert.True(double.IsFinite(c.R), $"circle radius went {c.R} at ({sx}, {sy})");
        Assert.True(double.IsFinite(a.R), $"arc radius went {a.R} at ({sx}, {sy})");
        // A reflection scales a radius by the MAGNITUDE of the factors — their geometric mean.
        Assert.Equal(100 * System.Math.Sqrt(System.Math.Abs(sx) * System.Math.Abs(sy)), c.R, 9);
    }

    /// <summary>Nothing a scale can do leaves an extent at zero — a primitive scaled to nothing draws
    /// nothing and has no grippers to drag it back out with.</summary>
    [Theory]
    [InlineData(0.0,   0.0)]
    [InlineData(1e-12, 1e-12)]
    [InlineData(1e-9,  1.0)]
    public void NoScaleLeavesAnExtentAtZero(double sx, double sy)
    {
        var c  = new CirclePrimitive      { Cx = 0, Cy = 0, R = 100 };
        var e  = new EllipsePrimitive     { Cx = 0, Cy = 0, Rx = 100, Ry = 50 };
        var r  = new RectPrimitive        { Cx = 0, Cy = 0, W = 100, H = 50 };
        var rr = new RoundedRectPrimitive { Cx = 0, Cy = 0, W = 100, H = 50, Radius = 10 };

        foreach (var p in new SymbolPrimitive[] { c, e, r, rr })
            SymbolGeometry.ScaleBy(p, 0, 0, sx, sy);

        Assert.True(c.R  >= SymbolGeometry.MinExtent);
        Assert.True(e.Rx >= SymbolGeometry.MinExtent);
        Assert.True(e.Ry >= SymbolGeometry.MinExtent);
        Assert.True(r.W  >= SymbolGeometry.MinExtent);
        Assert.True(r.H  >= SymbolGeometry.MinExtent);
        Assert.True(rr.W >= SymbolGeometry.MinExtent);
        Assert.True(rr.H >= SymbolGeometry.MinExtent);
        // A CORNER radius of zero is a square corner, not a degenerate shape — its floor is zero.
        Assert.True(rr.Radius >= 0);
    }

    /// <summary>
    /// A shape that survives the scale is still hit-testable, which is what "recoverable" means in
    /// practice: the user can click it and type a real size into the inspector. The NaN case failed
    /// this — <c>dist &lt;= NaN</c> is false for every point on the canvas.
    /// </summary>
    [Fact]
    public void AShrunkCircleIsStillSelectable()
    {
        var c = new CirclePrimitive { Cx = 0, Cy = 0, R = 100, Filled = true };
        SymbolGeometry.ScaleBy(c, 0, 0, -1e-9, 1e-9);

        Assert.True(SymbolGeometry.HitTest(c, 0, 0, tol: 5.0));
    }

    /// <summary>The bounding box stays real, so the resize grippers still have somewhere to be.</summary>
    [Fact]
    public void TheBoundingBoxStaysFinite()
    {
        var c = new CirclePrimitive { Cx = 10, Cy = 20, R = 100 };
        SymbolGeometry.ScaleBy(c, 0, 0, -0.3, 0.7);

        var (x0, y0, x1, y1) = SymbolGeometry.BboxOf(c);
        Assert.True(double.IsFinite(x0) && double.IsFinite(y0)
                 && double.IsFinite(x1) && double.IsFinite(y1));
    }

    // ── The inspector field ───────────────────────────────────────────────────

    /// <summary>
    /// A value decimal cannot hold shows as an EMPTY field, never as an error string. The field is
    /// then simply waiting for a number — a state the user can fix by typing one — which is what
    /// "graceful" means here.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TheInspectorShowsNothingRatherThanAnErrorForAValueDecimalCannotHold(double bad)
        => Assert.Null(NumericFieldConverter.Instance.Convert(
               bad, typeof(decimal?), null, CultureInfo.InvariantCulture));

    /// <summary>
    /// An empty or unparseable field makes NO assignment. <c>UnsetValue</c> — what this used to
    /// return — means "there is no value", which a binding onto a non-nullable double reports as the
    /// InvalidCastException the owner saw. <c>DoNothing</c> means "make no assignment", which is
    /// what an empty box actually wants and leaves the model holding what it had.
    /// </summary>
    [Fact]
    public void AnEmptyFieldLeavesTheModelAlone_RatherThanAssigningUnset()
    {
        var back = NumericFieldConverter.Instance.ConvertBack(
            null, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Same(BindingOperations.DoNothing, back);
        Assert.NotSame(Avalonia.AvaloniaProperty.UnsetValue, back);
    }

    /// <summary>A real number still round-trips — the guards above must not have cost the ordinary
    /// case.</summary>
    [Fact]
    public void AnOrdinaryValueStillRoundTrips()
    {
        var fwd = NumericFieldConverter.Instance.Convert(42.5, typeof(decimal?), null, CultureInfo.InvariantCulture);
        Assert.Equal(42.5m, Assert.IsType<decimal>(fwd));

        var back = NumericFieldConverter.Instance.ConvertBack(42.5m, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(42.5, Assert.IsType<double>(back));
    }
}
