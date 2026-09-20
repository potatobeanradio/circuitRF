using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Engine;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// <b>The engine is the oracle</b> (brief-smith-2-cascade.md R-smith2-10; smith-chart.md §4.6).
///
/// <para><c>SmithCascade</c> is closed form and <c>SParameterEngine</c> is not, and that is exactly
/// what makes this gate worth having. For every <see cref="SmithElementKind"/>, in every legal
/// <see cref="SmithPlacement"/>, the headline test builds the equivalent <c>.cnl</c> — the generator
/// as a <c>Z_Port</c> to ground, the cascade as real component lines, a <c>Port</c> where the walk
/// is being read — runs the ordinary S-parameter analysis, converts S₁₁ back to an impedance and
/// compares. Tolerance 1e-9 RELATIVE on Z: the numerical floor, not an engineering allowance.</para>
///
/// <para><b>What it catches is convention, not arithmetic</b> — a sign, a port order, a reference
/// impedance, a <c>tan</c> where a <c>cot</c> belongs. That is the class of error which produces a
/// plausible picture and survives inspection. It is affordable only because every element is a
/// component <c>ComponentTypeRegistry</c> already declares, and the netlist is written through
/// <see cref="SmithComponentMap"/> and <see cref="ComponentTypeRegistry.EngineReference"/> — the
/// same function the product uses. A second mapping written here would let the test and the product
/// agree about a component neither of them spells correctly.</para>
/// </summary>
public sealed class SmithCascadeTests : IDisposable
{
    private const double DesignHz = 1.8e9;
    private const double ChartZ0  = 50.0;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-smith-" + Guid.NewGuid().ToString("N")[..8]);

    public SmithCascadeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The headline gate — one test, parameterised over the vocabulary
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Every kind in every placement <see cref="SmithComponentMap.AllowedPlacement"/>
    /// allows — read from that table, so a kind added there arrives here without being listed
    /// twice.</summary>
    public static TheoryData<SmithElementKind, SmithPlacement> Vocabulary()
    {
        var data = new TheoryData<SmithElementKind, SmithPlacement>();
        foreach (var kind in SmithComponentMap.AllKinds)
        foreach (var placement in new[] { SmithPlacement.Series, SmithPlacement.Shunt })
            if (SmithComponentMap.AllowedPlacement(kind) is not { } only || only == placement)
                data.Add(kind, placement);
        return data;
    }

    /// <summary>
    /// <b>The closed form agrees with the engine, at every node of the walk.</b>
    ///
    /// <para>The element under test sits between a series inductor and a shunt capacitor, so what is
    /// compared is not only the element's own immittance but its place in the recurrence: the
    /// netlist is truncated at each node in turn and the port moved there, which is exactly what
    /// "the impedance looking back toward the generator" means.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Vocabulary))]
    public void TheClosedFormAgreesWithTheEngine_EveryKind_EveryPlacement(
        SmithElementKind kind, SmithPlacement placement)
    {
        var design = CascadeAround(Dut(kind, placement));
        var nodes  = SmithCascade.Evaluate(design, DesignHz, _dir);

        Assert.Equal(design.Elements.Count + 1, nodes.Length);

        for (int k = 1; k < nodes.Length; k++)
        {
            Complex engine = EngineImpedance(design, throughElements: k);
            Complex closed = nodes[k].Z;

            double relative = (engine - closed).Magnitude / engine.Magnitude;

            Assert.True(relative < 1e-9,
                $"{kind} in {placement}, node {k}: the closed form says {closed} and the engine says "
              + $"{engine} — {relative:E3} relative. A disagreement here is a CONVENTION, not a "
              + "rounding error: a sign, a port order, a reference impedance, or a tan where a cot "
              + "belongs.");
        }
    }

    /// <summary>
    /// The vacuity guard for the gate above: the element under test must actually MOVE the load
    /// node, or every case would pass with the element silently absent from one side or both.
    /// </summary>
    [Theory]
    [MemberData(nameof(Vocabulary))]
    public void TheElementUnderTest_ActuallyMovesTheLoadNode(
        SmithElementKind kind, SmithPlacement placement)
    {
        var withIt = CascadeAround(Dut(kind, placement));

        var withoutIt = CascadeAround(Dut(kind, placement));
        withoutIt.Elements[1].Enabled = false;

        Complex a = SmithCascade.Evaluate(withIt,    DesignHz, _dir)[^1].Z;
        Complex b = SmithCascade.Evaluate(withoutIt, DesignHz, _dir)[^1].Z;

        Assert.True((a - b).Magnitude / b.Magnitude > 1e-3,
            $"{kind} in {placement} moved the load node by only {(a - b).Magnitude:E3} Ω — the "
          + "oracle gate would pass vacuously.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The rest of the gate — one test per claim
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>1. The classical constructions.</b> A series reactance from a real Z walks that Z's
    /// constant-RESISTANCE circle; a shunt susceptance walks the constant-CONDUCTANCE circle. This
    /// is what a user checks by eye, so it is worth checking by test — and it is the direct
    /// consequence of scaling the IMMITTANCE rather than the component values.
    /// </summary>
    [Fact]
    public void ASeriesReactanceWalksConstantR_AndAShuntSusceptanceWalksConstantG()
    {
        var series = Design(Element(SmithElementKind.L, SmithPlacement.Series,
                                    v => v.LHenry = 4.7e-9));
        var shunt  = Design(Element(SmithElementKind.C, SmithPlacement.Shunt,
                                    v => v.CFarad = 1.5e-12));

        // A REAL generator, so "constant resistance" and "constant conductance" have a value to be
        // constant at.
        foreach (var d in new[] { series, shunt })
        {
            d.Generator.Rows.Clear();
            d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 25.0, 0.0));
        }

        foreach (var g in SmithCascade.Trajectories(series, DesignHz, Canvas)[0].Gamma)
            Assert.Equal(25.0, Z(g).Real, 9);

        foreach (var g in SmithCascade.Trajectories(shunt, DesignHz, Canvas)[0].Gamma)
            Assert.Equal(1.0 / 25.0, (Complex.One / Z(g)).Real, 12);
    }

    /// <summary>
    /// <b>2. A 135° open stub is ONE continuous polyline.</b> Past a quarter wave <c>tan θ</c> runs
    /// through a pole and the susceptance sweeps to +∞ and returns from −∞ — which on the chart is
    /// not a discontinuity at all but the closed constant-conductance circle, traversed through the
    /// SHORT. Sampling in θ walks it; sampling in B cannot.
    /// </summary>
    [Fact]
    public void AnOverQuarterWaveOpenStub_IsOneContinuousPolylineThroughThePole()
    {
        var d = Design(Element(SmithElementKind.StubOpen, SmithPlacement.Shunt, v =>
        {
            v.Z0Ohm               = 75.0;
            v.ElectricalLengthDeg = 135.0;
            v.ReferenceFrequencyHz = DesignHz;
        }));

        var t = Assert.Single(SmithCascade.Trajectories(d, DesignHz, Canvas, tolerance: 0.25, budget: 512));

        Assert.All(t.Gamma, g =>
        {
            Assert.True(double.IsFinite(g.Real) && double.IsFinite(g.Imaginary),
                $"A sample came back {g} — the θ parameterisation is supposed to have no pole in it.");
        });

        // One path: successive CANVAS steps stay bounded. A curve emitted as two pieces, or sampled
        // in B, shows up here as a jump of the chart's own width.
        for (int i = 1; i < t.Gamma.Count; i++)
        {
            var (ax, ay) = Canvas(t.Gamma[i - 1]);
            var (bx, by) = Canvas(t.Gamma[i]);
            double step  = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));

            Assert.True(step < 20.0,
                $"Step {i} of the stub's polyline is {step:F1} canvas units — a 135° stub walked "
              + "in θ has no jump in it, so this is a curve that came apart at the pole.");
        }

        // And it really did go round: a 135° open stub passes through the short.
        Assert.Contains(t.Gamma, g => (g - new Complex(-1.0, 0.0)).Magnitude < 0.05);
    }

    /// <summary>
    /// <b>3. A TLIN's length tracks frequency.</b> θ(f) = (π/180)·E·f/F_ref, so the same element has
    /// twice the electrical length at twice its reference frequency — and a quarter-wave line
    /// transforms Z → Z₀ₗ²/Z at F_ref and NOT at 2·F_ref, where it is a half wave and the identity.
    /// </summary>
    [Fact]
    public void ALinesLengthScalesWithFrequency_AndAQuarterWaveInvertsOnlyAtItsOwnFref()
    {
        var e = Element(SmithElementKind.Tline, SmithPlacement.Series, v =>
        {
            v.Z0Ohm                = 62.0;
            v.ElectricalLengthDeg  = 90.0;
            v.ReferenceFrequencyHz = 2.0e9;
        });

        Assert.Equal(Math.PI / 2.0, SmithCascade.ThetaRadians(e, 2.0e9), 12);
        Assert.Equal(Math.PI,       SmithCascade.ThetaRadians(e, 4.0e9), 12);

        var d = Design(e);
        d.Generator.Rows.Clear();
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 30.0, 15.0));

        Complex zGen = new(30.0, 15.0);

        Complex quarter = SmithCascade.Evaluate(d, 2.0e9)[^1].Z;
        Assert.True((quarter - 62.0 * 62.0 / zGen).Magnitude < 1e-9,
            $"A quarter wave should transform {zGen} to {62.0 * 62.0 / zGen}; it gave {quarter}.");

        Complex half = SmithCascade.Evaluate(d, 4.0e9)[^1].Z;
        Assert.True((half - zGen).Magnitude < 1e-9,
            $"At 2·F_ref the same line is a HALF wave and the identity; it gave {half}.");
    }

    /// <summary>
    /// <b>4. A disabled element is absent from the node array and from the trajectories</b>, the
    /// array's length is enabled + 1, and each node names the element that produced it — which is
    /// what keeps brief 5's grippers and brief 6's selection from each rewriting the same skip.
    /// </summary>
    [Fact]
    public void ADisabledElement_OccupiesNoNodeAndDrawsNothing()
    {
        var d = CascadeAround(Dut(SmithElementKind.R, SmithPlacement.Series));
        d.Elements[1].Enabled = false;

        var nodes = SmithCascade.Evaluate(d, DesignHz);

        Assert.Equal(3, nodes.Length);                                   // 2 enabled + 1
        Assert.Equal([-1, 0, 2], nodes.Select(n => n.ElementIndex));     // index 1 is skipped

        var curves = SmithCascade.Trajectories(d, DesignHz, Canvas);
        Assert.Equal([0, 2], curves.Select(c => c.ElementIndex));
    }

    /// <summary>
    /// <b>5. The generator interpolates in R and X, refuses outside its span, and a one-row table
    /// accepts anything.</b> The refusal's sentence names the span, because that is the thing that
    /// answers it.
    /// </summary>
    [Fact]
    public void TheGenerator_InterpolatesInsideItsSpan_RefusesOutside_AndIsFlatWithOneRow()
    {
        var g = new SmithGenerator();
        g.Rows.Add(new SmithGeneratorRow(1.0e9, 20.0, -10.0));
        g.Rows.Add(new SmithGeneratorRow(3.0e9, 40.0, +30.0));

        Assert.Equal(new Complex(30.0, 10.0), SmithCascade.GeneratorImpedance(g, 2.0e9));

        var refusal = Assert.Throws<InvalidDataException>(
            () => SmithCascade.GeneratorImpedance(g, 4.0e9));
        // The span as the user would have typed it, never "1E+09" — R-smith11-4, and
        // SmithDesign.Fmt's own remarks.
        Assert.Contains("1 GHz", refusal.Message);
        Assert.Contains("3 GHz", refusal.Message);

        // The swept band is the one caller allowed to clamp, and it has to ASK.
        Assert.Equal(new Complex(40.0, 30.0),
                     SmithCascade.GeneratorImpedance(g, 4.0e9, SmithOutOfBand.Clamp));

        var one = new SmithGenerator();
        one.Rows.Add(new SmithGeneratorRow(1.0e9, 50.0, 0.0));
        Assert.Equal(new Complex(50.0, 0.0), SmithCascade.GeneratorImpedance(one, 99.0e9));
    }

    /// <summary>
    /// <b>6. A file that does not resolve, or is the wrong port count, is a refusal naming the
    /// element AND the file</b> — never a silently-skipped element, which would draw a perfectly
    /// smooth network that is missing a part.
    /// </summary>
    [Fact]
    public void AnUnresolvableOrWrongPortCountFile_RefusesNamingTheElementAndTheFile()
    {
        var missing = Design(Element(SmithElementKind.S1P, SmithPlacement.Shunt,
                                     _ => { }, file: "nowhere.s1p"));

        var r1 = Assert.Throws<InvalidDataException>(() => SmithCascade.Evaluate(missing, DesignHz, _dir));
        Assert.Contains("DUT",         r1.Message);
        Assert.Contains("nowhere.s1p", r1.Message);

        // A 2-port handed to a 1-port element: far more likely to be the wrong file than a request
        // for the corner of its matrix.
        var wrong = Design(Element(SmithElementKind.S1P, SmithPlacement.Series,
                                   _ => { }, file: TwoPortFile()));

        var r2 = Assert.Throws<InvalidDataException>(() => SmithCascade.Evaluate(wrong, DesignHz, _dir));
        Assert.Contains("DUT", r2.Message);
        Assert.Contains("2-port", r2.Message);
    }

    /// <summary>
    /// <b>7. A Touchstone file is fitted ONCE.</b> A 201-point sweep over an S2P element re-uses the
    /// spline fit <c>TouchstoneCache</c> holds; a fit per sample would be 201 of them, and here the
    /// cost would land inside a drag. A COUNTER, not a stopwatch.
    /// </summary>
    [Fact]
    public void AnSnpElement_IsFittedOnceForAWholeSweep()
    {
        var d = Design(Element(SmithElementKind.S2P, SmithPlacement.Series,
                               _ => { }, file: TwoPortFile()));

        SmithCascade.Evaluate(d, 1.5e9, _dir);          // warm: this file's one parse and one fit

        long fitsBefore = TouchstoneCache.FitCount;
        for (int i = 0; i < 201; i++)
            SmithCascade.Evaluate(d, 1.0e9 + i * (2.0e9 / 200.0), _dir);
        long added = TouchstoneCache.FitCount - fitsBefore;

        // The threshold, rather than zero, is the price of a PROCESS-WIDE counter that another test
        // class running in parallel may also touch. 201 versus a handful is the claim.
        Assert.True(added < 10,
            $"A 201-point sweep over one S2P element added {added} spline fits. The fit depends on "
          + "the file and not on the target frequency, so it is built once and re-used.");
    }

    /// <summary>
    /// <b>A negative-real-part impedance is DRAWN, not clamped</b> (R-smith2-8). A Z1P with a
    /// negative R — an active S2P is the other way in — puts a node outside the unit circle, and
    /// clamping it to the disc would be a lie about a stability result. The chart's own autoscale
    /// enforces a unit-circle MINIMUM and grows past it when the data asks.
    /// </summary>
    [Fact]
    public void ANegativeResistance_LeavesTheUnitCircleRatherThanBeingClamped()
    {
        var d = Design(Element(SmithElementKind.Z1P, SmithPlacement.Series,
                               v => v.ImpedanceOhm = new Complex(-80.0, 10.0)));

        Complex gamma = SmithCascade.Gamma(SmithCascade.Evaluate(d, DesignHz)[^1].Z, ChartZ0);
        Assert.True(gamma.Magnitude > 1.0,
            $"|Γ| came back {gamma.Magnitude:F4} for a node whose resistance is negative.");

        var t = Assert.Single(SmithCascade.Trajectories(d, DesignHz, Canvas));
        Assert.Contains(t.Gamma, g => g.Magnitude > 1.0);
    }

    /// <summary>
    /// <b>A SHORTED stub's trajectory begins at the short, not at Γ_in</b> — the one place §3.5's
    /// rule and its own sentence part company, and it is pinned here so a later change cannot
    /// quietly re-parameterise it.
    ///
    /// <para>θ(t) = t·θ_total means a stub of ZERO length, and a zero-length shorted stub is a dead
    /// short across the node. The curve is the right constant-conductance circle — it just starts at
    /// the short rather than at Γ_in, and it reaches Γ_in only when the stub is longer than a
    /// quarter wave (below that its susceptance never crosses zero). The open stub and the series
    /// line both DO start at Γ_in, because a zero-length open stub contributes nothing. Recorded in
    /// src/Design/RESOLVED.md.</para>
    /// </summary>
    [Fact]
    public void AShortedStubsCurveStartsAtTheShort_AndAnOpenStubsStartsAtItsInput()
    {
        SmithDesign Stub(SmithElementKind kind) => Design(Element(kind, SmithPlacement.Shunt, v =>
        {
            v.Z0Ohm                = 50.0;
            v.ElectricalLengthDeg  = 60.0;
            v.ReferenceFrequencyHz = DesignHz;
        }));

        var shorted = Stub(SmithElementKind.StubShorted);
        var gIn     = SmithCascade.Gamma(SmithCascade.Evaluate(shorted, DesignHz)[0].Z, ChartZ0);
        var curve   = Assert.Single(SmithCascade.Trajectories(shorted, DesignHz, Canvas)).Gamma;

        Assert.True((curve[0] - new Complex(-1.0, 0.0)).Magnitude < 1e-12,
            $"A shorted stub of zero length is a short; the curve starts at {curve[0]}.");

        var open = Stub(SmithElementKind.StubOpen);
        var openCurve = Assert.Single(SmithCascade.Trajectories(open, DesignHz, Canvas)).Gamma;
        Assert.True((openCurve[0] - gIn).Magnitude < 1e-12,
            $"An open stub of zero length contributes nothing; the curve starts at {openCurve[0]}.");
    }

    /// <summary>
    /// <b>Direction is part of the geometry</b> (R-smith2-9): each trajectory reports its arc-length
    /// midpoint and a unit tangent pointing from input to output, so two adjacent arcs sharing a
    /// gripper are not ambiguous about which way the walk goes.
    /// </summary>
    [Fact]
    public void EachTrajectoryReportsItsMidpointAndADirectionFromInputToOutput()
    {
        var d = Design(Element(SmithElementKind.L, SmithPlacement.Series, v => v.LHenry = 6.8e-9));

        var t     = Assert.Single(SmithCascade.Trajectories(d, DesignHz, Canvas));
        var nodes = SmithCascade.Evaluate(d, DesignHz);

        Assert.Equal(1.0, t.Tangent.Magnitude, 9);

        // The curve runs from Γ_in to Γ_out, and the midpoint lies on it.
        Assert.True((t.Gamma[0]  - SmithCascade.Gamma(nodes[0].Z, ChartZ0)).Magnitude < 1e-12);
        Assert.True((t.Gamma[^1] - SmithCascade.Gamma(nodes[1].Z, ChartZ0)).Magnitude < 1e-12);
        Assert.Contains(t.Gamma, g => (g - t.Midpoint).Magnitude < 0.05);

        // A series inductance walks anticlockwise from the generator's point: the tangent has to
        // point ALONG the walk, not back down it.
        var chord = t.Gamma[^1] - t.Gamma[0];
        Assert.True((t.Tangent * Complex.Conjugate(chord)).Real > 0,
            $"The tangent {t.Tangent} points away from the walk {chord}.");
    }

    /// <summary>
    /// <b>A zero in a RECIPROCAL position is the short or the open — never a NaN.</b>
    ///
    /// <para>§3.3 states each kind's immittance in ONE form, so half the placements need the other,
    /// and <c>1/0</c> in <see cref="Complex"/> is <c>(NaN, NaN)</c> rather than an infinity. A
    /// document is refused for a NEGATIVE R, L or C and zero is not negative, so every case below is
    /// typable — and each one used to put a NaN into every node downstream, the readout, the
    /// grippers and the picture, with nothing said. The walk is projective for exactly this reason;
    /// this is the claim that it is projective in the ELEMENT as well as in the node.</para>
    /// </summary>
    [Theory]
    // A shunt element of zero impedance is a dead short across the line: Γ = −1.
    [InlineData(SmithElementKind.R,    SmithPlacement.Shunt,  SmithParameter.R, -1.0)]
    [InlineData(SmithElementKind.L,    SmithPlacement.Shunt,  SmithParameter.L, -1.0)]
    [InlineData(SmithElementKind.Prlc, SmithPlacement.Shunt,  SmithParameter.R, -1.0)]
    [InlineData(SmithElementKind.Prlc, SmithPlacement.Shunt,  SmithParameter.L, -1.0)]
    // A series element of zero admittance is an open: Γ = +1.
    [InlineData(SmithElementKind.C,    SmithPlacement.Series, SmithParameter.C, +1.0)]
    [InlineData(SmithElementKind.Srlc, SmithPlacement.Series, SmithParameter.C, +1.0)]
    public void AZeroValueInAReciprocalPosition_IsTheLimitAndNotANaN(
        SmithElementKind kind, SmithPlacement placement, SmithParameter zeroed, double expectedGamma)
    {
        var dut = Dut(kind, placement);
        SmithInverse.Apply(dut, zeroed, 0.0);

        var d = Design(dut);
        Assert.Null(d.Refusal());

        var gamma = SmithCascade.Gamma(SmithCascade.Evaluate(d, DesignHz)[^1].Z, ChartZ0);
        Assert.Equal(expectedGamma, gamma.Real, 12);
        Assert.Equal(0.0,           gamma.Imaginary, 12);

        // …and the curve that leads there is drawn, rather than being a polyline of NaN.
        foreach (var g in Assert.Single(SmithCascade.Trajectories(d, DesignHz, Canvas)).Gamma)
            Assert.True(double.IsFinite(g.Real) && double.IsFinite(g.Imaginary),
                $"{kind} in {placement} with {zeroed} = 0 emitted {g}.");
    }

    /// <summary>
    /// <b><c>SmithImmittance</c>'s other form is an INFINITY at zero, not a NaN.</b> The walk does
    /// not come through these accessors — it folds the reciprocal into its projective pair — but a
    /// caller holding one element's immittance on its own has nowhere else to go, and
    /// <c>Complex.One / Complex.Zero</c> in .NET is <c>(NaN, NaN)</c>.
    /// </summary>
    [Fact]
    public void AnImmittanceOfZero_ReportsTheOtherFormAsInfiniteRatherThanNaN()
    {
        // A zero capacitance states itself as an admittance of zero; its impedance is the open.
        var open = SmithCascade.Immittance(
            Element(SmithElementKind.C, SmithPlacement.Series, v => v.CFarad = 0.0), DesignHz);
        Assert.Equal(Complex.Zero, open.Y);
        Assert.True(double.IsInfinity(open.Z.Real));

        // A zero resistance states itself as an impedance of zero; its admittance is the short.
        var short_ = SmithCascade.Immittance(
            Element(SmithElementKind.R, SmithPlacement.Shunt, v => v.ROhm = 0.0), DesignHz);
        Assert.Equal(Complex.Zero, short_.Z);
        Assert.True(double.IsInfinity(short_.Y.Real));
    }

    /// <summary>
    /// <b>A line a whole number of turns long is still sampled as a curve.</b>
    ///
    /// <para>The three line kinds are PERIODIC in θ, so at E = 360° the samples at t = 0, ½ and 1
    /// are the same point: adaptive subdivision measured a chord error of zero, stopped on its first
    /// test and emitted the whole trajectory as two coincident points — a line that goes right round
    /// the chart drawn as a dot, with nothing said. It is not an exotic document either: a 90° line
    /// quoted at 1 GHz is θ = 720° at 8 GHz.</para>
    /// </summary>
    [Theory]
    [InlineData(SmithElementKind.Tline,       SmithPlacement.Series)]
    [InlineData(SmithElementKind.StubOpen,    SmithPlacement.Shunt)]
    [InlineData(SmithElementKind.StubShorted, SmithPlacement.Shunt)]
    public void APeriodicLineIsSeededPastItsOwnPeriod(SmithElementKind kind, SmithPlacement placement)
    {
        var dut = Dut(kind, placement);
        dut.Values.ElectricalLengthDeg  = 360.0;
        dut.Values.ReferenceFrequencyHz = DesignHz;

        var gamma = Assert.Single(SmithCascade.Trajectories(Design(dut), DesignHz, Canvas)).Gamma;

        // The number is not the claim; that the curve was WALKED rather than collapsed is. A full
        // turn at this canvas scale cannot be two points inside the chord tolerance.
        Assert.True(gamma.Count > 16, $"A 360° {kind} was emitted as {gamma.Count} point(s).");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The equivalent netlist — written through the product's OWN mapping
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The impedance the ENGINE reports at node <paramref name="throughElements"/>: the same design,
    /// truncated there, with a <see cref="SymbolKind.Term"/> reading it against the chart's Z₀.
    /// </summary>
    private Complex EngineImpedance(SmithDesign design, int throughElements)
    {
        var (lib, tb) = new CnlReader().Read(Cnl(design, throughElements));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var s11 = (Complex)SParameterEngine.Run(netlist, [DesignHz])["S"][0, 0, 0];

        return ChartZ0 * (Complex.One + s11) / (Complex.One - s11);
    }

    /// <summary>
    /// The design as a <c>.cnl</c>, truncated after <paramref name="throughElements"/> of them.
    ///
    /// <para><b>The type token comes from <see cref="SmithComponentMap"/> and
    /// <see cref="ComponentTypeRegistry.EngineReference"/></b>, never from a table written here —
    /// that is R-smith2-10's own condition, and the reason is that a second mapping would let this
    /// test and the product agree about a component neither of them spells correctly.</para>
    /// </summary>
    private string Cnl(SmithDesign design, int throughElements)
    {
        var text = new StringBuilder();

        // The generator is an impedance to ground and nothing else. No source: an S-parameter run
        // needs none, and this tool has no available power to give one.
        text.AppendLine($"Z_Port:ZGEN  n0 0  Z[1,1]=complex({N(design.Generator.Rows[0].ResistanceOhm)},"
                      + $"{N(design.Generator.Rows[0].ReactanceOhm)})");

        string net = "n0";
        int    seen = 0;

        for (int i = 0; i < design.Elements.Count && seen < throughElements; i++)
        {
            var e = design.Elements[i];
            if (!e.Enabled) continue;
            seen++;

            var    binding = SmithComponentMap.Component(e.Kind);
            string token   = ComponentTypeRegistry.EngineReference(binding.SymbolKind);
            var    v       = e.Values;

            // A shunt element hangs off the node it found; only a series part or a two-port makes a
            // new one.
            bool advances = e.Placement == SmithPlacement.Series;
            string next   = advances ? $"n{i + 1}" : net;

            string nets = e.Kind switch
            {
                // Ground-referenced two-port: port 1 faces the generator.
                SmithElementKind.S2P => $"{net} {next}",
                SmithElementKind.Tline => $"{net} {next}",
                // The stubs are the same ideal line with its far end left open or tied to ground.
                SmithElementKind.StubOpen    => $"{net} nopen{i}",
                SmithElementKind.StubShorted => $"{net} 0",
                // A one-port file in SERIES binds N+1 nets, the extra one being its floating
                // reference; in shunt it binds N and the reference is ground.
                SmithElementKind.S1P => advances ? $"{net} {next}" : net,
                _ => advances ? $"{net} {next}" : $"{net} 0",
            };

            string parameters = e.Kind switch
            {
                SmithElementKind.R    => $"R={N(v.ROhm)}",
                SmithElementKind.L    => $"L={N(v.LHenry)}",
                SmithElementKind.C    => $"C={N(v.CFarad)}",
                // The RLC family writes EXACTLY the parameters its kind declares — an SRL's line
                // has no C, and ComponentTypeRegistry would refuse one.
                _ when SmithComponentMap.RlcElementsOf(e.Kind) is not null
                                      => string.Join(' ', SmithComponentMap.Parameters(e.Kind)
                                             .Select(sp => sp switch
                                             {
                                                 SmithParameter.R => $"R={N(v.ROhm)}",
                                                 SmithParameter.L => $"L={N(v.LHenry)}",
                                                 _                => $"C={N(v.CFarad)}",
                                             })),
                SmithElementKind.Z1P  => $"Z[1,1]=complex({N(v.ImpedanceOhm.Real)},{N(v.ImpedanceOhm.Imaginary)})",
                SmithElementKind.S1P or SmithElementKind.S2P
                                      => $"NumPorts={binding.NumPorts} File=\"{FullPath(e.FileRef!)}\"",
                _                     => $"Z={N(v.Z0Ohm)} E={N(v.ElectricalLengthDeg)} deg "
                                       + $"F={N(v.ReferenceFrequencyHz)}",
            };

            text.AppendLine($"{token}:{e.Name}  {nets}  {parameters}");
            net = next;
        }

        text.AppendLine($"Port:P1  {net} 0  Num=1  Z={N(ChartZ0)} Ohm");
        return text.ToString();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Fixtures
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>A world-to-canvas map of the shape brief 5 supplies: 200 units per Γ, y down.</summary>
    private static (double X, double Y) Canvas(Complex gamma)
        => (200.0 * gamma.Real, -200.0 * gamma.Imaginary);

    private static Complex Z(Complex gamma) => ChartZ0 * (Complex.One + gamma) / (Complex.One - gamma);

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private string FullPath(string relative) => Path.Combine(_dir, relative);

    private static SmithElement Element(
        SmithElementKind kind, SmithPlacement placement,
        Action<SmithElementValues> set, string? file = null, string name = "DUT")
    {
        var e = new SmithElement { Kind = kind, Placement = placement, Name = name, FileRef = file };
        set(e.Values);
        return e;
    }

    /// <summary>The element under test, at values chosen to be ordinary rather than degenerate —
    /// the engine's own treatment of R = 0 or C = 0 is a documented special case on its side and
    /// this gate is about conventions, not about the corners.</summary>
    private SmithElement Dut(SmithElementKind kind, SmithPlacement placement) => kind switch
    {
        SmithElementKind.R    => Element(kind, placement, v => v.ROhm   = 22.0),
        SmithElementKind.L    => Element(kind, placement, v => v.LHenry = 3.3e-9),
        SmithElementKind.C    => Element(kind, placement, v => v.CFarad = 1.2e-12),
        SmithElementKind.Srlc => Element(kind, placement, v =>
                                 { v.ROhm = 0.4; v.LHenry = 0.8e-9; v.CFarad = 4.7e-12; }),
        SmithElementKind.Prlc => Element(kind, placement, v =>
                                 { v.ROhm = 800.0; v.LHenry = 2.5e-9; v.CFarad = 1.5e-12; }),
        // The six two-element members. Each carries only its own values, at the same ordinary
        // magnitudes as the three-element pair above — the point of the gate is the CONVENTION,
        // and a value the engine treats as a special case would test that instead.
        SmithElementKind.Srl  => Element(kind, placement, v =>
                                 { v.ROhm = 0.4;   v.LHenry = 0.8e-9; }),
        SmithElementKind.Src  => Element(kind, placement, v =>
                                 { v.ROhm = 0.4;   v.CFarad = 4.7e-12; }),
        SmithElementKind.Slc  => Element(kind, placement, v =>
                                 { v.LHenry = 0.8e-9; v.CFarad = 4.7e-12; }),
        SmithElementKind.Prl  => Element(kind, placement, v =>
                                 { v.ROhm = 800.0; v.LHenry = 2.5e-9; }),
        SmithElementKind.Prc  => Element(kind, placement, v =>
                                 { v.ROhm = 800.0; v.CFarad = 1.5e-12; }),
        SmithElementKind.Plc  => Element(kind, placement, v =>
                                 { v.LHenry = 2.5e-9; v.CFarad = 1.5e-12; }),
        SmithElementKind.Z1P  => Element(kind, placement, v => v.ImpedanceOhm = new Complex(18.0, -27.0)),
        SmithElementKind.S1P  => Element(kind, placement, _ => { }, file: OnePortFile()),
        SmithElementKind.S2P  => Element(kind, placement, _ => { }, file: TwoPortFile()),
        SmithElementKind.Tline => Element(kind, placement, v =>
                                 { v.Z0Ohm = 62.0; v.ElectricalLengthDeg = 57.0; v.ReferenceFrequencyHz = 2.4e9; }),
        SmithElementKind.StubOpen => Element(kind, placement, v =>
                                 { v.Z0Ohm = 75.0; v.ElectricalLengthDeg = 135.0; v.ReferenceFrequencyHz = 1.6e9; }),
        SmithElementKind.StubShorted => Element(kind, placement, v =>
                                 { v.Z0Ohm = 40.0; v.ElectricalLengthDeg = 40.0; v.ReferenceFrequencyHz = 2.0e9; }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A one-element design on a complex single-row generator.</summary>
    private static SmithDesign Design(params SmithElement[] elements)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm            = ChartZ0;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 30.0, 15.0));
        foreach (var e in elements) d.Elements.Add(e);
        return d;
    }

    /// <summary>The element under test between a series inductor and a shunt capacitor, so the
    /// recurrence is exercised and not only the immittance.</summary>
    private static SmithDesign CascadeAround(SmithElement dut) => Design(
        Element(SmithElementKind.L, SmithPlacement.Series, v => v.LHenry = 2.2e-9, name: "L1"),
        dut,
        Element(SmithElementKind.C, SmithPlacement.Shunt, v => v.CFarad = 0.9e-12, name: "C1"));

    // ── The Touchstone fixtures, referenced to a NON-50 Ω of their own ───────

    private string OnePortFile() => TouchstoneFile("dut.s1p", 1, (f, _) =>
    {
        var z = new Mat<Complex>(1, 1);
        z[0, 0] = new Complex(12.0, 2.0 * Math.PI * f * 1.4e-9);
        return z;
    });

    private string TwoPortFile() => TouchstoneFile("dut.s2p", 2, (f, _) =>
    {
        // A T network, so the Z matrix exists — a pure series element's does not.
        double  w  = 2.0 * Math.PI * f;
        Complex z1 = new(8.0,  w * 1.1e-9);
        Complex z3 = new(5.0,  w * 0.6e-9);
        Complex z2 = Complex.One / new Complex(0.0, w * 0.8e-12);

        var z = new Mat<Complex>(2, 2);
        z[0, 0] = z1 + z2; z[0, 1] = z2;
        z[1, 0] = z2;      z[1, 1] = z3 + z2;
        return z;
    });

    /// <summary>Writes a Touchstone file through <c>TouchstoneIO</c> — this test adds no second
    /// Touchstone interpretation either.</summary>
    private string TouchstoneFile(string name, int ports, Func<double, int, Mat<Complex>> zAt)
    {
        string path = Path.Combine(_dir, name);
        if (File.Exists(path)) return name;

        const double fileZ0 = 75.0;                      // NOT the chart's, and not the port's

        double[] hz  = [.. Enumerable.Range(0, 21).Select(i => 1.0e9 + i * 0.1e9)];
        var      mat = new Mat<Complex>[hz.Length];
        for (int i = 0; i < hz.Length; i++)
            mat[i] = RFNetwork.ZToS(zAt(hz[i], ports), fileZ0);

        TouchstoneIO.WriteFile(new SNP(hz, mat, MatrixType.S, MatrixFormat.RI, fileZ0), path,
                               writeComments: false, precision: "G17");
        return name;
    }
}
