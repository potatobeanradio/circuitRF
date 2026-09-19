using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The gripper inverse (brief-smith-3-gripper-inverse.md; smith-chart.md §4.3).
///
/// <para><b>The headline is the round trip, and it is the only test that cannot pass while a formula
/// is wrong.</b> For every parameter in §4.3's table: set the parameter to a target, evaluate
/// through <see cref="SmithCascade"/> to get a Γ that is REACHABLE by construction, reset the
/// element to some other value, solve, write the answer back, and land on the same Γ. A sign error,
/// a Z where a Y belongs or a placement read the wrong way round all fail it; nothing else here
/// would.</para>
///
/// <para>The rest are the four things that are closed form but not obvious: the TLIN's Z₀
/// quadratic and its degenerate case, the stub's branch unwrapping across a quarter wave, where
/// each parameter PINS, and that no drag anywhere returns a NaN.</para>
/// </summary>
public sealed class SmithInverseTests
{
    private const double DesignHz = 1.8e9;
    private const double ChartZ0  = 50.0;

    /// <summary>The impedance arriving at the element under test — complex, so a real-only slip in
    /// either direction shows up.</summary>
    private static readonly Complex ZIn = new(30.0, 15.0);

    // ═════════════════════════════════════════════════════════════════════════
    //  1. The gate — the inverse inverts, over the whole table
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Every row of §4.3's table: the kind, its placement, the parameter being dragged, the
    /// value the drag STARTS from, and the value it should recover.</summary>
    public static TheoryData<SmithElementKind, SmithPlacement, SmithParameter, double, double> Table()
    {
        var d = new TheoryData<SmithElementKind, SmithPlacement, SmithParameter, double, double>();

        void Row(SmithElementKind k, SmithPlacement pl, SmithParameter p, double from, double to)
            => d.Add(k, pl, p, from, to);

        const SmithPlacement Se = SmithPlacement.Series;
        const SmithPlacement Sh = SmithPlacement.Shunt;

        Row(SmithElementKind.R, Se, SmithParameter.R, 10.0,  22.0);
        Row(SmithElementKind.R, Sh, SmithParameter.R, 200.0, 120.0);
        Row(SmithElementKind.L, Se, SmithParameter.L, 1.0e-9, 3.3e-9);
        Row(SmithElementKind.L, Sh, SmithParameter.L, 5.0e-9, 2.2e-9);
        Row(SmithElementKind.C, Se, SmithParameter.C, 2.0e-12, 1.2e-12);
        Row(SmithElementKind.C, Sh, SmithParameter.C, 0.5e-12, 0.9e-12);

        Row(SmithElementKind.Srlc, Se, SmithParameter.R, 0.4,     1.6);
        Row(SmithElementKind.Srlc, Se, SmithParameter.L, 0.8e-9,  1.5e-9);
        Row(SmithElementKind.Srlc, Se, SmithParameter.C, 4.7e-12, 2.2e-12);
        // SHUNT, which is the cross-placement case: the required admittance is exact and the
        // projection happens in the SRLC's own Z, where its parameters are the linear ones.
        Row(SmithElementKind.Srlc, Sh, SmithParameter.L, 0.8e-9,  1.5e-9);

        Row(SmithElementKind.Prlc, Sh, SmithParameter.R, 800.0,   450.0);
        Row(SmithElementKind.Prlc, Sh, SmithParameter.L, 2.5e-9,  1.1e-9);
        Row(SmithElementKind.Prlc, Sh, SmithParameter.C, 1.5e-12, 2.4e-12);
        Row(SmithElementKind.Prlc, Se, SmithParameter.C, 1.5e-12, 2.4e-12);   // and its dual

        Row(SmithElementKind.Z1P, Se, SmithParameter.ImpedanceReal,  18.0, 25.0);
        Row(SmithElementKind.Z1P, Se, SmithParameter.ImpedanceImag, -27.0, 12.0);
        Row(SmithElementKind.Z1P, Sh, SmithParameter.ImpedanceImag, -27.0, 12.0);

        Row(SmithElementKind.Tline,       Se, SmithParameter.ElectricalLength, 57.0, 80.0);
        Row(SmithElementKind.Tline,       Se, SmithParameter.Z0,               62.0, 78.0);
        Row(SmithElementKind.StubOpen,    Sh, SmithParameter.ElectricalLength, 40.0, 65.0);
        Row(SmithElementKind.StubOpen,    Sh, SmithParameter.Z0,               75.0, 95.0);
        Row(SmithElementKind.StubShorted, Sh, SmithParameter.ElectricalLength, 40.0, 65.0);
        Row(SmithElementKind.StubShorted, Sh, SmithParameter.Z0,               40.0, 58.0);

        return d;
    }

    /// <summary>
    /// <b>The inverse inverts.</b> One claim, the whole table.
    /// </summary>
    [Theory]
    [MemberData(nameof(Table))]
    public void TheInverseInverts_EveryParameterInTheTable(
        SmithElementKind kind, SmithPlacement placement, SmithParameter p, double from, double to)
    {
        var design = OneElement(Base(kind, placement));
        var e      = design.Elements[0];

        // A Γ that is reachable BY CONSTRUCTION: the element itself put it there.
        Set(e, p, to);
        Complex target = LoadGamma(design);

        // …and the drag starts from somewhere else, so a formula that simply returned the current
        // value could not pass.
        Set(e, p, from);
        Assert.True((LoadGamma(design) - target).Magnitude > 1e-6,
            $"{kind}/{placement}/{p}: the two values land in the same place, so this row proves "
          + "nothing.");

        var r = SmithInverse.Solve(design, 0, p, ZIn, target, DesignHz, ChartZ0);

        Assert.False(r.Pinned, $"{kind}/{placement}/{p} pinned on a reachable drag: {r.PinReason}");
        Assert.Null(r.PinReason);

        Set(e, p, r.Value);
        Complex landed = LoadGamma(design);

        Assert.True((landed - target).Magnitude < 1e-12,
            $"{kind}/{placement}/{p}: solved {r.Value} (wanted {to}), which lands at {landed} "
          + $"rather than {target}.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. The TLIN's Z₀ quadratic (R-smith3-2)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The quadratic picks the physical root, and its degenerate case is inert rather than
    /// NaN.</b>
    ///
    /// <para>The impedance arriving here is chosen so that BOTH roots have positive real part —
    /// which is not the ordinary case, and is what makes "nearest the current value" do any work.
    /// Vieta's product is the vacuity guard: it names the other root without re-deriving the
    /// quadratic here.</para>
    /// </summary>
    [Fact]
    public void TheTlineZ0Quadratic_TakesThePhysicalRoot_AndIsInertAtAHalfWave()
    {
        var zk     = new Complex(1.0, -5.0);
        var design = OneElement(Base(SmithElementKind.Tline, SmithPlacement.Series));
        var e      = design.Elements[0];

        e.Values.ElectricalLengthDeg  = 130.0;
        e.Values.ReferenceFrequencyHz = 2.4e9;

        e.Values.Z0Ohm = 62.0;
        Complex zd     = Transform(e, zk);
        e.Values.Z0Ohm = 55.0;                                   // where the drag starts

        var r = SmithInverse.Solve(
            design, 0, SmithParameter.Z0, zk, SmithCascade.Gamma(zd, ChartZ0), DesignHz, ChartZ0);

        Assert.False(r.Pinned, r.PinReason);
        Assert.Equal(62.0, r.Value, 9);

        // The root that was NOT taken — r₁·r₂ = c/a = −Z_k·Z_d — is ALSO positive-real, and it is
        // the one the quadratic hands back first. Without this the "nearest the current value"
        // rule would be untested: in the ordinary case the other root has a negative real part and
        // the sign filter alone decides.
        Complex other = -zk * zd / 62.0;
        Assert.True(other.Real > 0 && (other - 55.0).Magnitude > 3.0 * (62.0 - 55.0),
            $"The other root is {other}, so this case does not test the choice.");

        // tan θ = 0 — here a HALF WAVE, whose tangent is 1.2e-16 rather than 0, which is exactly
        // why the guard is a threshold. A line that transforms nothing has no Z₀ the drag can move.
        e.Values.ElectricalLengthDeg = 240.0;                    // θ = π at 1.8 GHz against 2.4 GHz
        var inert = SmithInverse.Solve(
            design, 0, SmithParameter.Z0, zk, SmithCascade.Gamma(zd, ChartZ0), DesignHz, ChartZ0);

        Assert.True(inert.Pinned);
        Assert.Equal(55.0, inert.Value);
        Assert.Contains("transforms nothing", inert.PinReason);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. The stub's branch, across a quarter wave (R-smith3-3)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A stub drag through the quarter wave is continuous.</b>
    ///
    /// <para>The walk is the real gesture: each step solves against the length the PREVIOUS step
    /// returned, which is what the element carries mid-drag. An <c>atan</c> read as a principal
    /// value jumps a half-turn at θ = 90° — 160° of E here — the moment the susceptance runs
    /// through its pole, and the gripper leaps to the far side of the chart under a hand that moved
    /// two pixels.</para>
    /// </summary>
    [Fact]
    public void AStubDragThroughTheQuarterWave_IsContinuous()
    {
        var design = OneElement(Base(SmithElementKind.StubOpen, SmithPlacement.Shunt));
        var e      = design.Elements[0];

        double z0L   = e.Values.Z0Ohm;                                   // 75 Ω
        double scale = e.Values.ReferenceFrequencyHz / DesignHz;         // 1.6/1.8 — E per degree of θ
        double step  = 0.5 * scale;

        // θ from 80.25° to 99.75°, straddling the pole. Offset off the exact 90° so the fixture is
        // not measuring how one infinity round-trips through a reciprocal.
        double[] thetaDeg = [.. Enumerable.Range(0, 40).Select(i => 80.25 + 0.5 * i)];

        e.Values.ElectricalLengthDeg = thetaDeg[0] * scale;
        double previous = e.Values.ElectricalLengthDeg;

        Complex yIn = Complex.One / ZIn;

        for (int i = 1; i < thetaDeg.Length; i++)
        {
            // A point on the constant-G circle: the stub can only add susceptance.
            double  b  = Math.Tan(Math.PI / 180.0 * thetaDeg[i]) / z0L;
            Complex zd = Complex.One / (yIn + new Complex(0.0, b));

            var r = SmithInverse.Solve(
                design, 0, SmithParameter.ElectricalLength, ZIn,
                SmithCascade.Gamma(zd, ChartZ0), DesignHz, ChartZ0);

            Assert.False(r.Pinned, $"θ = {thetaDeg[i]}°: {r.PinReason}");

            double delta = r.Value - previous;
            Assert.True(delta > 0 && Math.Abs(delta - step) < 1e-6,
                $"θ = {thetaDeg[i]}°: E went {previous}° → {r.Value}°, a step of {delta}° where "
              + $"{step}° was due. A half-turn here is the principal value of an atan, unwrapped "
              + "into the wrong branch.");

            e.Values.ElectricalLengthDeg = r.Value;              // what the element carries mid-drag
            previous = r.Value;
        }

        // And the walk really did cross the quarter wave rather than creeping up to it.
        Assert.True(previous > 90.0 * scale && thetaDeg[0] * scale < 90.0 * scale);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. Where each parameter pins (R-smith3-4)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>An unphysical drag pins, and the reason names the parameter.</b>
    ///
    /// <para>Two kinds of pin, and the difference is the point. A parameter that enters LINEARLY
    /// pins at ZERO, the boundary it overshot. One that enters RECIPROCALLY — a shunt R, a series C
    /// — reaches its demand's boundary only at INFINITY, so there is no representable value to sit
    /// on and the current one is held; pinning those at zero would swing a near-open shunt R to a
    /// dead short.</para>
    /// </summary>
    [Theory]
    // kind, placement, parameter, the drag's target immittance, whether it is a Y, expected value
    [InlineData(SmithElementKind.R, SmithPlacement.Series, SmithParameter.R, 10.0, 15.0,  false, 0.0)]
    [InlineData(SmithElementKind.L, SmithPlacement.Series, SmithParameter.L, 30.0, 0.0,   false, 0.0)]
    [InlineData(SmithElementKind.C, SmithPlacement.Shunt,  SmithParameter.C, 0.026667, -0.05, true, 0.0)]
    // …and the reciprocal pair, held at the value the element already had.
    [InlineData(SmithElementKind.R, SmithPlacement.Shunt,  SmithParameter.R, 0.01,  -0.013333, true, 22.0)]
    [InlineData(SmithElementKind.C, SmithPlacement.Series, SmithParameter.C, 30.0,  20.0, false, 1.2e-12)]
    public void AnUnphysicalDragPins_AndTheReasonNamesTheParameter(
        SmithElementKind kind, SmithPlacement placement, SmithParameter p,
        double re, double im, bool isAdmittance, double expected)
    {
        var design = OneElement(Base(kind, placement));
        var target = new Complex(re, im);
        var zd     = isAdmittance ? Complex.One / target : target;

        var r = SmithInverse.Solve(
            design, 0, p, ZIn, SmithCascade.Gamma(zd, ChartZ0), DesignHz, ChartZ0);

        Assert.True(r.Pinned, $"{kind}/{placement}/{p} did not pin; it returned {r.Value}.");
        Assert.Equal(expected, r.Value, 15);
        Assert.NotNull(r.PinReason);
        Assert.Contains(p.ToString(), r.PinReason);
    }

    /// <summary>The two line parameters, whose pins are their own: E at zero, Z₀ at the value it
    /// had, because a line of zero characteristic impedance is not one anybody can build.</summary>
    [Fact]
    public void ALinesLengthPinsAtZero_AndItsZ0IsHeldAtTheLastPositiveOne()
    {
        var design = OneElement(Base(SmithElementKind.StubOpen, SmithPlacement.Shunt));
        var e      = design.Elements[0];

        // A susceptance the wrong side of zero, which an open stub reaches only with a negative
        // length (θ = atan(Z₀·B) < 0) or a negative Z₀.
        Complex zd = Complex.One / (Complex.One / ZIn + new Complex(0.0, -0.002));
        Complex gd = SmithCascade.Gamma(zd, ChartZ0);

        e.Values.ElectricalLengthDeg = 5.0;
        var length = SmithInverse.Solve(
            design, 0, SmithParameter.ElectricalLength, ZIn, gd, DesignHz, ChartZ0);

        Assert.True(length.Pinned);
        Assert.Equal(0.0, length.Value);
        Assert.Contains("E", length.PinReason);

        e.Values.ElectricalLengthDeg = 40.0;                     // θ = 45°, tan θ = 1
        var z0 = SmithInverse.Solve(design, 0, SmithParameter.Z0, ZIn, gd, DesignHz, ChartZ0);

        Assert.True(z0.Pinned);
        Assert.Equal(75.0, z0.Value);
        Assert.Contains("Z0", z0.PinReason);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. Nothing returns NaN
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>No drag anywhere returns a NaN</b>, and <c>Pinned</c> and <c>PinReason</c> always agree.
    ///
    /// <para>Every kind, every placement it allows, every parameter it exposes, against the Γ
    /// values that break the arithmetic — the centre, both ends of the real axis (the short and the
    /// OPEN, where Z_d is infinite), both poles, and a point outside the disc — each from an
    /// ordinary input impedance, from a dead short and from the chart's own Z₀.</para>
    /// </summary>
    [Fact]
    public void NothingReturnsNaN_AtAnyDegenerateDrag()
    {
        Complex[] gammas =
        [
            Complex.Zero, Complex.One, -Complex.One,
            Complex.ImaginaryOne, -Complex.ImaginaryOne,
            new(2.0, 2.0), new(0.999999999, 0.0),
        ];

        Complex[] inputs = [ZIn, Complex.Zero, new Complex(ChartZ0, 0.0)];

        int checkedCases = 0;

        foreach (var kind in SmithComponentMap.AllKinds)
        foreach (var placement in new[] { SmithPlacement.Series, SmithPlacement.Shunt })
        {
            if (SmithComponentMap.AllowedPlacement(kind) is { } only && only != placement) continue;

            foreach (var p in SmithComponentMap.Parameters(kind))     // empty for S1P and S2P
            foreach (var zIn in inputs)
            foreach (var g in gammas)
            {
                var design = OneElement(Base(kind, placement));
                var r      = SmithInverse.Solve(design, 0, p, zIn, g, DesignHz, ChartZ0);
                checkedCases++;

                Assert.True(double.IsFinite(r.Value),
                    $"{kind}/{placement}/{p} at Γ = {g} from Z = {zIn} returned {r.Value}.");
                Assert.Equal(r.Pinned, r.PinReason is not null);
            }
        }

        Assert.True(checkedCases > 500, $"Only {checkedCases} cases ran.");
    }

    /// <summary>A gripper on a file element is a PROGRAMMING error, not a refusal the user could act
    /// on — so it throws, in Release as well as Debug, rather than returning a pinned nothing.</summary>
    [Fact]
    public void AFileElementHasNoGripper_AndAskingForItsInverseThrows()
    {
        var s2p = OneElement(new SmithElement
        {
            Kind      = SmithElementKind.S2P,
            Placement = SmithPlacement.Series,
            Name      = "X1",
            FileRef   = "part.s2p",
        });

        Assert.Throws<InvalidOperationException>(() => SmithInverse.Solve(
            s2p, 0, SmithParameter.L, ZIn, Complex.Zero, DesignHz, ChartZ0));

        // …and so is a parameter the kind does not have.
        var l = OneElement(Base(SmithElementKind.L, SmithPlacement.Series));
        Assert.Throws<InvalidOperationException>(() => SmithInverse.Solve(
            l, 0, SmithParameter.Z0, ZIn, Complex.Zero, DesignHz, ChartZ0));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Fixtures
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>A one-element design whose generator IS <see cref="ZIn"/>, so node 0 of the walk is
    /// the impedance the inverse is told about and node 1 is where the drag lands.</summary>
    private static SmithDesign OneElement(SmithElement e)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = ChartZ0;
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, ZIn.Real, ZIn.Imaginary));
        d.Elements.Add(e);
        return d;
    }

    private static Complex LoadGamma(SmithDesign d)
        => SmithCascade.Gamma(SmithCascade.Evaluate(d, DesignHz)[^1].Z, ChartZ0);

    /// <summary>What the element does to an arbitrary input impedance — the walk, run from a
    /// generator of <paramref name="zIn"/> instead of the fixture's own.</summary>
    private static Complex Transform(SmithElement e, Complex zIn)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = ChartZ0;
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, zIn.Real, zIn.Imaginary));
        d.Elements.Add(e);
        return SmithCascade.Evaluate(d, DesignHz)[^1].Z;
    }

    /// <summary>Ordinary values for every kind — the round trip is about the formulas, not about
    /// the corners, which test 5 owns.</summary>
    private static SmithElement Base(SmithElementKind kind, SmithPlacement placement)
    {
        var e = new SmithElement { Kind = kind, Placement = placement, Name = "DUT" };
        var v = e.Values;

        switch (kind)
        {
            case SmithElementKind.R:    v.ROhm   = 22.0;    break;
            case SmithElementKind.L:    v.LHenry = 3.3e-9;  break;
            case SmithElementKind.C:    v.CFarad = 1.2e-12; break;
            case SmithElementKind.Srlc: v.ROhm = 0.4;   v.LHenry = 0.8e-9; v.CFarad = 4.7e-12; break;
            case SmithElementKind.Prlc: v.ROhm = 800.0; v.LHenry = 2.5e-9; v.CFarad = 1.5e-12; break;
            case SmithElementKind.Z1P:  v.ImpedanceOhm = new Complex(18.0, -27.0); break;

            case SmithElementKind.Tline:
                v.Z0Ohm = 62.0; v.ElectricalLengthDeg = 57.0; v.ReferenceFrequencyHz = 2.4e9; break;
            case SmithElementKind.StubOpen:
                v.Z0Ohm = 75.0; v.ElectricalLengthDeg = 40.0; v.ReferenceFrequencyHz = 1.6e9; break;
            case SmithElementKind.StubShorted:
                v.Z0Ohm = 40.0; v.ElectricalLengthDeg = 40.0; v.ReferenceFrequencyHz = 2.0e9; break;
        }

        return e;
    }

    /// <summary>
    /// Committing the answer.
    /// </summary>
    /// <remarks>
    /// <b>The product's own writer</b>, which brief 5 added beside <see cref="SmithInverse.Current"/>
    /// for the reason that method's own remarks give: a second copy of the switch is a second place
    /// for <see cref="SmithParameter.ElectricalLength"/> to reach the wrong field. It was eight
    /// hand-written lines here while brief 3 was the only caller and this brief had promised not to
    /// write to the design; it is the shipped one now, so what this file exercises is what the drag
    /// loop actually does.
    /// </remarks>
    private static void Set(SmithElement e, SmithParameter p, double value)
        => SmithInverse.Apply(e, p, value);
}
