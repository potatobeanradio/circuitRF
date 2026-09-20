using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The constant-Q arcs and the swept band (<c>brief-smith-9-q-and-sweep.md</c> §3;
/// <c>docs/design/smith-chart.md</c> §4.4, §3.6). <b>One test per claim.</b>
///
/// <para><b>The gate is the first one</b>: the drawn circle IS the constant-Q locus. Everything
/// about these two features is closed form and framework-free, so the whole file runs with no
/// display — which is also what brief 10's headless render depends on.</para>
/// </summary>
public sealed class SmithConstantQTests
{
    private const double DesignHz = 2.0e9;
    private const double ChartZ0  = 50.0;

    /// <summary>The Q values the claims are stated over — two decades either side of 1, which is the
    /// range a matching network is actually argued about in.</summary>
    public static TheoryData<double> Qs => [0.5, 1.0, 3.0, 10.0, 100.0];

    private static SmithDesign Design()
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = ChartZ0;
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));
        return d;
    }

    /// <summary>Normalized z from Γ, written the way a reader would write it — <b>not the way
    /// <see cref="SmithQArcs"/> avoids writing it</b>, so the test and the code are not the same
    /// arithmetic agreeing with itself.</summary>
    private static Complex Z(Complex gamma) => (Complex.One + gamma) / (Complex.One - gamma);

    // ═════════════════════════════════════════════════════════════════════════
    //  1. The drawn circle IS the constant-Q locus  (R-smith9-1) — THE GATE
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Every point of the emitted arc has |x|/r = Q.</b>
    /// </summary>
    /// <remarks>
    /// <b>The two ENDPOINTS are excluded, and that is a statement about the locus rather than a
    /// tolerance.</b> Both branches pass exactly through Γ = ±1 — the open and the short — where r
    /// and x vanish or diverge together and the ratio is 0/0. Its LIMIT there is Q; its value is not
    /// a number. Claim 3 is what pins those two points.
    ///
    /// <para>1e-12 relative rather than an exact compare, because the map above forms
    /// <c>1 − u² − v²</c>, which on this circle equals <c>(2/Q)·v</c> and so cancels to a few ulps of
    /// 1 near the ends. The worst interior sample measured here is a few times 1e-15 relative.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Qs))]
    public void TheDrawnCircleIsTheConstantQLocus(double q)
    {
        foreach (bool inductive in new[] { true, false })
        {
            var arc = SmithQArcs.Arc(q, inductive);

            for (int i = 1; i < arc.Length - 1; i++)
            {
                var z = Z(arc[i]);

                Assert.True(z.Real > 0,
                    $"Q={q} inductive={inductive} sample {i}: r = {z.Real}, so the arc left the "
                  + "passive half of the plane.");

                // The BRANCH is the sign of x, which is the whole of what tells the two apart.
                Assert.Equal(inductive, z.Imaginary > 0);

                double measured = Math.Abs(z.Imaginary) / z.Real;
                Assert.True(Math.Abs(measured - q) <= 1e-12 * q,
                    $"Q={q} inductive={inductive} sample {i}: |x|/r = {measured:R}");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. Uniform steps  (R-smith9-1)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Every chord around the arc is the same length.</b>
    /// </summary>
    /// <remarks>
    /// This is the property the sample-and-map approach loses, and it is why the arc is emitted as
    /// <c>centre + radius·e^{jθ}</c> at uniform θ: equal steps in the z parameter become very unequal
    /// steps in Γ, and the rendered curve reads as jaggy at exactly the values people use
    /// (<c>docs/design/vswr-locus-gamma-plane.md</c>).
    /// </remarks>
    [Theory]
    [MemberData(nameof(Qs))]
    public void EveryChordAroundTheArcIsTheSameLength(double q)
    {
        var arc = SmithQArcs.Arc(q, inductive: true);

        var chords = Enumerable.Range(0, arc.Length - 1)
                               .Select(i => (arc[i + 1] - arc[i]).Magnitude)
                               .ToArray();

        double ratio = chords.Max() / chords.Min();
        Assert.True(ratio < 1.0005, $"Q={q}: longest/shortest chord = {ratio:R}");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. Mirror images, through Γ = ±1  (R-smith9-1)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The two branches are mirror images about the real axis, and both pass through Γ = ±1</b> —
    /// which is why they are drawn as the arc inside the unit disc and need no endpoint arithmetic.
    /// </summary>
    [Theory]
    [MemberData(nameof(Qs))]
    public void TheTwoBranchesAreMirrorImagesThroughPlusAndMinusOne(double q)
    {
        var inductive  = SmithQArcs.Arc(q, inductive: true);
        var capacitive = SmithQArcs.Arc(q, inductive: false);

        Assert.Equal(inductive.Length, capacitive.Length);

        // EXACTLY mirror images — the capacitive branch is the inductive one conjugated, not derived
        // a second time, so there is no tolerance to state.
        for (int i = 0; i < inductive.Length; i++)
            Assert.Equal(Complex.Conjugate(inductive[i]), capacitive[i]);

        foreach (var arc in new[] { inductive, capacitive })
        {
            Assert.Equal(Complex.One,  arc[0]);
            Assert.Equal(-Complex.One, arc[^1]);
        }

        // And the arc stays inside the disc between them — the half of each circle that lies outside
        // it is not emitted, because a Smith Plot clips its traces to the PLOT BOX and not to the
        // disc (a node outside the unit circle is drawn on purpose).
        Assert.All(inductive, g => Assert.True(g.Magnitude <= 1.0 + 1e-12));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. The drag inverts  (R-smith9-2)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Drag to a reachable Γ, and the re-emitted arc passes through the drag point</b> — through
    /// the window's own gesture, so the closed form and the drag loop are one answer.
    /// </summary>
    [Fact]
    public void TheDragInverts()
    {
        var design = Design();
        design.ConstantQ.Enabled = true;
        var vm = new SmithChartViewModel(design);

        var target = new Complex(0.15, 0.42);

        Assert.True(vm.BeginQDrag());
        vm.DragQTo(target, shift: false);
        vm.EndQDrag(cancelled: false);

        double q = design.ConstantQ.Q;
        Assert.True(double.IsFinite(q) && q > 0);
        Assert.Null(vm.DragPin);

        // The drag point is ON the circle the document now holds: its distance from the centre is
        // the radius.
        var circle = SmithQArcs.Circle(q, inductive: true);
        Assert.Equal(circle.Radius, (target - circle.Centre).Magnitude, 12);

        // …and the emitted arc goes through it, which is the claim as a reader would check it.
        var arc = SmithQArcs.Arc(q, inductive: true);
        Assert.True(arc.Min(g => (g - target).Magnitude) < 0.02);

        // ONE undo entry for the whole gesture (R-smith5-8's contract, reached by this route too).
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Equal(1.0, vm.Design.ConstantQ.Q);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. Shift rounds to a quarter BEFORE storing  (R-smith9-2)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A shift-drag stores an exact quarter, and an un-shifted drag afterwards starts from that
    /// exact quarter</b> rather than from a rounded display of something else.
    /// </summary>
    [Fact]
    public void ShiftRoundsToAQuarterBeforeItIsStored()
    {
        var design = Design();
        design.ConstantQ.Enabled = true;
        var vm = new SmithChartViewModel(design);

        // A point whose Q is deliberately not near a quarter.
        var target = new Complex(0.15, 0.42);
        double exact = SmithQArcs.QAt(target)!.Value;
        Assert.True(Math.Abs(exact / 0.25 - Math.Round(exact / 0.25)) > 0.05);

        Assert.True(vm.BeginQDrag());
        vm.DragQTo(target, shift: true);
        vm.EndQDrag(cancelled: false);

        double stored = design.ConstantQ.Q;

        // STORED, not displayed: the document holds a number that is an exact multiple of 0.25.
        Assert.Equal(Math.Round(exact / 0.25) * 0.25, stored);
        Assert.Equal(0.0, stored % 0.25);

        // The next un-shifted drag starts from THAT value — a press and release with no movement
        // leaves it bit-identical and pushes nothing, which is what "starts from the exact quarter"
        // has to mean for a number nobody re-reads from a label.
        int before = CountUndo(vm);
        Assert.True(vm.BeginQDrag());
        vm.EndQDrag(cancelled: false);

        Assert.Equal(stored, vm.Design.ConstantQ.Q);
        Assert.Equal(before, CountUndo(vm));
    }

    private static int CountUndo(SmithChartViewModel vm)
    {
        int n = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); n++; }
        for (int i = 0; i < n; i++) vm.UndoRedo.Redo();
        return n;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. A drag with no Q pins and reports  (R-smith9-2)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A drag outside the passive region pins at the last valid Q and says so</b> — and returns no
    /// infinity and no NaN.
    /// </summary>
    /// <remarks>
    /// <c>r_d ≤ 0</c> is Γ on or outside the unit circle. The real axis is the other case with no
    /// finite positive Q: |x|/r is zero there, and a Q of zero is the real axis itself rather than a
    /// pair of arcs — which is what <see cref="SmithDesign.Refusal"/> refuses to store.
    /// </remarks>
    [Theory]
    [InlineData(1.4, 0.9)]      // well outside the unit circle
    [InlineData(0.8, 0.6)]      // exactly on it
    [InlineData(0.3, 0.0)]      // the real axis
    public void ADragWithNoQPinsAndReports(double u, double v)
    {
        var design = Design();
        design.ConstantQ.Enabled = true;
        design.ConstantQ.Q       = 2.5;
        var vm = new SmithChartViewModel(design);

        Assert.True(vm.BeginQDrag());
        vm.DragQTo(new Complex(u, v), shift: false);

        Assert.Equal(2.5, vm.Design.ConstantQ.Q);
        Assert.True(double.IsFinite(vm.Design.ConstantQ.Q));

        // The sentence names the value it kept, and the strip is carrying it.
        Assert.NotNull(vm.DragPin);
        Assert.Contains("2.5", vm.DragPin);
        Assert.Equal(vm.DragPin, vm.Refusal);

        // A pinned drag that never moved anywhere valid pushes nothing.
        vm.EndQDrag(cancelled: false);
        Assert.False(vm.UndoRedo.CanUndo);

        // …and the closed form said so on its own: there is no Q at this point to return.
        Assert.Null(SmithQArcs.QAt(new Complex(u, v)));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. The band clamps, and says so  (R-smith9-4)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A band wider than the generator table is CLAMPED with a note naming the span, never
    /// refused; a band inside the span is not clamped and produces exactly <c>Points</c>
    /// samples.</b>
    /// </summary>
    /// <remarks>
    /// The distinction the clamp rests on: <i>a band is a viewing choice, where a design frequency is
    /// a design input</i>. It is the one caller allowed to clamp (<c>R-smith2-5</c>).
    /// </remarks>
    [Fact]
    public void TheBandClampsAndSaysSo()
    {
        // ── wider than the table (1.8…2.2 GHz) ───────────────────────────────
        var wide = Design();
        wide.Sweep.Enabled = true;
        wide.Sweep.StartHz = 1.0e9;
        wide.Sweep.StopHz  = 3.0e9;
        wide.Sweep.Points  = 21;

        Assert.Null(wide.Refusal());                    // clamped, NOT refused

        var clamped = SmithBand.Evaluate(wide);
        Assert.True(clamped.Clamped);
        Assert.Equal(1.8e9, clamped.StartHz);
        Assert.Equal(2.2e9, clamped.StopHz);
        Assert.Equal(21, clamped.Gamma.Count);

        var vmWide = new SmithChartViewModel(wide);
        Assert.True(vmWide.HasStripNotice);
        Assert.Contains("1.8", vmWide.StripNotice);
        Assert.Contains("2.2", vmWide.StripNotice);
        Assert.Contains("GHz", vmWide.StripNotice);

        // ── inside the table ─────────────────────────────────────────────────
        var inside = Design();
        inside.Sweep.Enabled = true;
        inside.Sweep.StartHz = 1.9e9;
        inside.Sweep.StopHz  = 2.1e9;
        inside.Sweep.Points  = 33;

        var band = SmithBand.Evaluate(inside);
        Assert.False(band.Clamped);
        Assert.Equal(1.9e9, band.StartHz);
        Assert.Equal(2.1e9, band.StopHz);
        Assert.Equal(33, band.Gamma.Count);

        var vmInside = new SmithChartViewModel(inside);
        Assert.False(vmInside.HasStripNotice);

        // The locus is on the chart as ONE trace, and it is the walk the evaluator produced.
        var trace = vmInside.ChartPlot.Traces.Single(t => t.CubeName == "band");
        Assert.Equal(33, trace.Points.Count);
        Assert.Equal((float)band.Gamma[0].Real, trace.Points[0].X);
    }

    /// <summary>
    /// <b>A band asking for more points than <see cref="SmithSweep.MaxPoints"/> is refused, and it
    /// draws NOTHING rather than being walked anyway.</b>
    /// </summary>
    /// <remarks>
    /// The cap is about the DRAG rather than the sweep: the band is one whole
    /// <c>SmithCascade.Evaluate</c> per point and it is re-walked inside every rebuild of the chart,
    /// which is every pointer move of a gripper drag. Without a ceiling there is a point count at
    /// which the window simply stops responding, reached by typing a number into a field. Both
    /// halves are the claim — a refusal the window hangs before displaying is not a refusal, so
    /// <c>SmithBand</c> has to decline the walk as well as the document declining the count.
    /// </remarks>
    [Fact]
    public void ABandPastThePointCapIsRefusedAndIsNotWalked()
    {
        var d = Design();
        d.Sweep.Enabled = true;
        d.Sweep.StartHz = 1.9e9;
        d.Sweep.StopHz  = 2.1e9;
        d.Sweep.Points  = SmithSweep.MaxPoints + 1;

        string refusal = Assert.IsType<string>(d.Refusal());
        Assert.Contains(SmithSweep.MaxPoints.ToString(CultureInfo.InvariantCulture), refusal);
        Assert.Empty(SmithBand.Evaluate(d).Gamma);

        // …and the cap itself is walked, so the refusal is off by nothing.
        d.Sweep.Points = SmithSweep.MaxPoints;
        Assert.Null(d.Refusal());
        Assert.Equal(SmithSweep.MaxPoints, SmithBand.Evaluate(d).Gamma.Count);
    }

    /// <summary>
    /// <b>The band reports the frequency it took each sample at, and it is the grid it walked.</b>
    /// </summary>
    /// <remarks>
    /// <c>circuitrf smith -o out.s1p</c> writes the band, and the only other way to know what
    /// frequency each Γ belongs to is to rebuild the spacing from the two ends — a second copy of the
    /// rule, which would agree with <c>SmithBand</c> until one of them changed and then differ
    /// silently in a file nobody would re-check.
    /// </remarks>
    [Fact]
    public void TheBandCarriesItsOwnFrequencyGrid()
    {
        var d = Design();
        d.Sweep.Enabled = true;
        d.Sweep.StartHz = 1.9e9;
        d.Sweep.StopHz  = 2.1e9;
        d.Sweep.Points  = 5;

        var band = SmithBand.Evaluate(d);

        Assert.Equal(band.Gamma.Count, band.FrequencyHz.Count);
        Assert.Equal(band.StartHz, band.FrequencyHz[0],  6);
        Assert.Equal(band.StopHz,  band.FrequencyHz[^1], 6);

        // Each Γ is the load at the frequency beside it — which is what makes the pairing an
        // assertion rather than an assumption.
        for (int i = 0; i < band.FrequencyHz.Count; i++)
        {
            var nodes = SmithCascade.Evaluate(d, band.FrequencyHz[i], null, SmithOutOfBand.Clamp);
            Assert.Equal(SmithCascade.Gamma(nodes[^1].Z, d.Chart.Z0Ohm), band.Gamma[i]);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Turning either one OFF does not throw its settings away
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Unchecking a box and checking it again hands back the same numbers.</b>
    /// </summary>
    /// <remarks>
    /// Both blocks used to be written to the `.csmith` only while their feature was ENABLED, so a
    /// disabled band carried no start, stop or point count — and because the undo stack is a round
    /// trip through that same writer, the loss happened WITHIN the session as well as across a save.
    /// The rule is now "absent means untouched" (<c>SmithDesignIo.IsDefault</c>); an untouched
    /// document still writes nothing.
    /// </remarks>
    [Fact]
    public void TurningEitherOneOffKeepsItsSettings()
    {
        var vm = new SmithChartViewModel(Design());

        vm.ConstantQEnabled = true;
        vm.ConstantQEntry   = "3.5";
        vm.SweepEnabled     = true;
        vm.SweepStartEntry  = "1.95 GHz";
        vm.SweepStopEntry   = "2.05 GHz";
        vm.SweepPointsEntry = "17";

        // Captured after the typing rather than restated: what is claimed is that the toggle changes
        // nothing, and a literal here would be claiming something about the text parser instead.
        double q     = vm.Design.ConstantQ.Q;
        double start = vm.Design.Sweep.StartHz;
        double stop  = vm.Design.Sweep.StopHz;

        vm.ConstantQEnabled = false;
        vm.SweepEnabled     = false;
        vm.ConstantQEnabled = true;
        vm.SweepEnabled     = true;

        Assert.Equal(3.5,   q);
        Assert.Equal(q,     vm.Design.ConstantQ.Q);
        Assert.Equal(start, vm.Design.Sweep.StartHz);
        Assert.Equal(stop,  vm.Design.Sweep.StopHz);
        Assert.Equal(17,    vm.Design.Sweep.Points);

        // A document nobody has touched still writes neither block, so no existing file changes.
        Assert.DoesNotContain("constantQ", SmithDesignIo.Serialize(Design()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sweep",     SmithDesignIo.Serialize(Design()), StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The arcs are chrome  (R-smith9-3)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The arcs are drawn BENEATH the trajectories, carry no marker and are out of the
    /// autoscale</b> — and nothing at all is drawn when the pair is off.
    /// </summary>
    [Fact]
    public void TheArcsAreChrome()
    {
        var off = new SmithChartViewModel(Design());
        Assert.DoesNotContain(off.ChartPlot.Traces, t => t.CubeName!.StartsWith("Q ", StringComparison.Ordinal));

        var design = Design();
        design.ConstantQ.Enabled = true;
        design.ConstantQ.Q       = 2.0;
        var vm = new SmithChartViewModel(design);

        var traces = vm.ChartPlot.Traces;

        // FIRST, which is what "beneath" means: a trace's draw order is its order in the collection.
        Assert.Equal("Q (inductive)",  traces[0].CubeName);
        Assert.Equal("Q (capacitive)", traces[1].CubeName);

        foreach (var t in traces.Take(2))
        {
            Assert.True(t.IsAnnotation);                // no marker, no Add Marker entry
            Assert.True(t.ExcludeFromAutoscale);        // the pair reaches Γ = ±1 at every Q
            Assert.Equal(SmithQArcs.DefaultSamples, t.Points.Count);
        }
    }
}
