namespace CircuitRF.WBond.Tests;

/// <summary>
/// The WB-C1 oracle ladder (brief-wbond-wbc §3) — the framework-free editing core.
/// </summary>
public class WireEditsTests
{
    /// <summary>
    /// <b>Every geometric edit quantises to one nanometre</b>, because <see cref="Point3"/> stores
    /// integer DBU — the choice WB-A made so that switching display units is lossless.
    ///
    /// <para>So no transform test in this file can assert tighter than ~1 nm, and one that appears to
    /// is asserting against its own rounding. On a 500,000 nm loop height that is 2e-6 relative;
    /// physically it is a fifth of a millionth of a mil, which is far below anything a bonder can
    /// place. Tolerances below are stated in nanometres for that reason rather than as relative
    /// fractions that hide the cause.</para>
    /// </summary>
    private const double QuantisationNm = 2.0;

    private static Wire Loop(double startZMil = 8.0, double endZMil = 1.0,
                             double spanMil = 120.0, double loopMil = 20.0, int points = 7)
    {
        return LoopShape.CreateSeedWire(
            Point3.Mils(0, 0, startZMil), Point3.Mils(spanMil, 30, endZMil),
            WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold",
            WBondUnits.ToNm(loopMil, WBondUnit.Mil), points);
    }

    private static double[] NormalisedHeights(Wire wire)
    {
        var start = wire.Points[0];
        var end = wire.Points[^1];

        var heights = new double[wire.Points.Count];
        for (int i = 0; i < wire.Points.Count; i++)
        {
            double s = WireEdits.ChordParameter(start, end, wire.Points[i]);
            double chordZ = start.Z + (end.Z - start.Z) * s;
            heights[i] = wire.Points[i].Z - chordZ;
        }

        double peak = heights.Max();
        return peak == 0.0 ? heights : [.. heights.Select(h => h / peak)];
    }

    // ---------------------------------------------------------------- tier 0

    /// <summary>
    /// TIER 0 — <b>both feet are bit-identical after any height scale</b>, including the case that
    /// motivates the whole formulation: feet at DIFFERENT z, die surface to package lead.
    ///
    /// <para>This is the property the chord-relative formulation exists to guarantee. A
    /// scale-about-a-flat-baseline implementation passes every other tier in this file and fails
    /// here — it drags one foot off its pad.</para>
    /// </summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(4.0)]
    public void Tier0_ScalingHeight_LeavesBothFeetBitIdentical(double factor)
    {
        var wire = Loop(startZMil: 8.0, endZMil: 1.0);
        var start = wire.Points[0];
        var end = wire.Points[^1];

        WireEdits.ScaleHeightAboutChord(wire, factor);

        Assert.Equal(start, wire.Points[0]);
        Assert.Equal(end, wire.Points[^1]);
    }

    /// <summary>TIER 0 — the same, for a wire whose feet are at the same z.</summary>
    [Fact]
    public void Tier0_LevelFeet_AreAlsoBitIdentical()
    {
        var wire = Loop(startZMil: 4.0, endZMil: 4.0);
        var start = wire.Points[0];
        var end = wire.Points[^1];

        WireEdits.ScaleHeightAboutChord(wire, 2.75);

        Assert.Equal(start, wire.Points[0]);
        Assert.Equal(end, wire.Points[^1]);
    }

    // ---------------------------------------------------------------- tier 1

    /// <summary>
    /// TIER 1 — the normalised SHAPE is preserved: every point's height as a fraction of the peak is
    /// unchanged. Scaling that also reshaped would pass tier 0 and be wrong.
    /// </summary>
    [Theory]
    [InlineData(0.4)]
    [InlineData(2.5)]
    public void Tier1_ScalingHeight_PreservesTheNormalisedShape(double factor)
    {
        var wire = Loop();
        var before = NormalisedHeights(wire);

        double peakBefore = PeakHeightAboveChord(wire);
        WireEdits.ScaleHeightAboutChord(wire, factor);
        var after = NormalisedHeights(wire);

        // Compared as an ABSOLUTE height error rather than a shape fraction, so the bound is the
        // model's 1 nm quantisation rather than a number chosen to make the test pass.
        double peakAfter = PeakHeightAboveChord(wire);
        for (int i = 0; i < before.Length; i++)
        {
            double heightBefore = before[i] * peakBefore * factor;
            double heightAfter = after[i] * peakAfter;
            Assert.True(Math.Abs(heightAfter - heightBefore) <= QuantisationNm,
                $"Point {i} moved {Math.Abs(heightAfter - heightBefore):F3} nm off its scaled height " +
                $"— more than the {QuantisationNm} nm the DBU grid allows.");
        }
    }

    /// <summary>TIER 1 — and the apex actually moves by the factor asked for.</summary>
    [Fact]
    public void Tier1_TheApexMovesByTheRequestedFactor()
    {
        var wire = Loop(startZMil: 8.0, endZMil: 1.0);

        double before = PeakHeightAboveChord(wire);
        WireEdits.ScaleHeightAboutChord(wire, 1.5);
        double after = PeakHeightAboveChord(wire);

        Assert.Equal(1.5, after / before, 1e-5);
    }

    // ---------------------------------------------------------------- tier 2

    /// <summary>
    /// TIER 2 — <b>span scales by FACTOR across an array, not to a common value</b> (D4).
    ///
    /// <para>An array whose wires deliberately have different spans — a fan-out from a common pad —
    /// must keep their ratios. Setting a common absolute span silently destroys exactly the geometry
    /// the flexible model exists to allow, and every "all the wires got longer" test would pass.</para>
    /// </summary>
    [Fact]
    public void Tier2_ScalingAnArraysSpan_PreservesTheRatiosBetweenMembers()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        var array = new WireArray { Name = "G1" };
        double[] spans = [60.0, 100.0, 140.0];
        foreach (double span in spans)
        {
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, 0, 4), Point3.Mils(span, 0, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm));
        }
        design.Arrays.Add(array);

        var before = array.Wires.Select(w => w.ChordLengthMetres()).ToArray();

        int moved = WireEdits.ScaleWires(array.Wires, heightFactor: 1.0, spanFactor: 1.4);
        Assert.Equal(3, moved);

        var after = array.Wires.Select(w => w.ChordLengthMetres()).ToArray();

        for (int i = 0; i < spans.Length; i++)
            Assert.Equal(1.4, after[i] / before[i], 1e-4);

        // The ratios between members survive — this is the assertion that separates "by factor"
        // from "to a common value".
        Assert.Equal(before[1] / before[0], after[1] / after[0], 1e-6);
        Assert.Equal(before[2] / before[0], after[2] / after[0], 1e-6);
    }

    /// <summary>
    /// TIER 2 — span scaling holds loop HEIGHT absolute (D3): a bonder running the same loop program
    /// over a longer span does not raise the loop proportionally.
    /// </summary>
    [Fact]
    public void Tier2_ScalingSpan_HoldsLoopHeightAbsolute()
    {
        var wire = Loop(spanMil: 100.0, loopMil: 20.0);

        double before = PeakHeightAboveChord(wire);
        WireEdits.ScaleSpan(wire, 2.0);
        double after = PeakHeightAboveChord(wire);

        Assert.Equal(before, after, before * 1e-3);
    }

    /// <summary>Alt+Shift IS similarity — height moves with span there, and only there.</summary>
    [Fact]
    public void Tier2_SimilarityScaling_MovesHeightWithSpan()
    {
        var wire = Loop(spanMil: 100.0, loopMil: 20.0);

        double before = PeakHeightAboveChord(wire);
        WireEdits.ScaleSimilarity(wire, 2.0);

        Assert.Equal(2.0, PeakHeightAboveChord(wire) / before, 1e-2);
    }

    /// <summary>The pinned foot stays put and the other one moves — whichever side was dragged.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tier2_SpanScaling_PinsTheUndraggedFoot(bool moveOutput)
    {
        var wire = Loop();
        var start = wire.Points[0];
        var end = wire.Points[^1];

        WireEdits.ScaleSpan(wire, 1.6, moveOutput);

        if (moveOutput)
        {
            Assert.Equal(start, wire.Points[0]);
            Assert.NotEqual(end, wire.Points[^1]);
        }
        else
        {
            Assert.NotEqual(start, wire.Points[0]);
            Assert.Equal(end, wire.Points[^1]);
        }
    }

    // ---------------------------------------------------------------- tier 3

    /// <summary>
    /// TIER 3 — <b>rotate-about-end-point leaves the pinned end exactly fixed</b> and carries the
    /// wire rigidly: every segment length is preserved.
    /// </summary>
    [Theory]
    [InlineData(true, 0.4)]
    [InlineData(false, -1.1)]
    [InlineData(true, Math.PI / 2)]
    public void Tier3_RotateAboutEndPoint_PinsOneEndAndPreservesSegmentLengths(bool pivotOnInput, double radians)
    {
        var wire = Loop();
        var pivot = pivotOnInput ? wire.Points[0] : wire.Points[^1];

        var lengthsBefore = SegmentLengths(wire);
        WireEdits.RotateAboutEndPoint(wire, pivotOnInput, radians);
        var lengthsAfter = SegmentLengths(wire);

        Assert.Equal(pivot, pivotOnInput ? wire.Points[0] : wire.Points[^1]);

        // Rigid to the DBU grid: a rotation rounds each point to the nearest nanometre, so a
        // segment length can shift by at most a nanometre or so.
        for (int i = 0; i < lengthsBefore.Length; i++)
            Assert.True(Math.Abs(lengthsAfter[i] - lengthsBefore[i]) <= QuantisationNm,
                $"Segment {i} changed by {Math.Abs(lengthsAfter[i] - lengthsBefore[i]):F3} nm under a " +
                "rigid rotation — more than the DBU grid allows.");
    }

    /// <summary>In the layout view rotation is about the vertical axis, so no point changes height.</summary>
    [Fact]
    public void Tier3_LayoutViewRotation_LeavesEveryHeightUnchanged()
    {
        var wire = Loop();
        var before = wire.Points.Select(p => p.Z).ToArray();

        WireEdits.RotateAboutEndPoint(wire, pivotOnInputFoot: true, 0.7, EditorView.Layout);

        for (int i = 0; i < before.Length; i++)
            Assert.Equal(before[i], wire.Points[i].Z);
    }

    /// <summary>In the profile view rotation is in-plane, so y never changes.</summary>
    [Fact]
    public void Tier3_ProfileViewRotation_LeavesEveryYUnchanged()
    {
        var wire = Loop();
        var before = wire.Points.Select(p => p.Y).ToArray();

        WireEdits.RotateAboutEndPoint(wire, pivotOnInputFoot: true, 0.7, EditorView.Profile);

        for (int i = 0; i < before.Length; i++)
            Assert.Equal(before[i], wire.Points[i].Y);
    }

    /// <summary>Peak height above the chord, in nanometres.</summary>
    private static double PeakHeightAboveChord(Wire wire)
    {
        var s = wire.Points[0];
        var e = wire.Points[^1];
        return wire.Points.Max(p => p.Z - (s.Z + (e.Z - s.Z) * WireEdits.ChordParameter(s, e, p)));
    }

    private static double[] SegmentLengths(Wire wire)
    {
        var lengths = new double[wire.Points.Count - 1];
        for (int i = 1; i < wire.Points.Count; i++)
        {
            var a = wire.Points[i - 1];
            var b = wire.Points[i];
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            lengths[i - 1] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return lengths;
    }

    // ---------------------------------------------------------------- tier 4

    /// <summary>
    /// TIER 4 — <b>reversing a wire negates exactly that wire's off-diagonal row and column of L,
    /// and nothing else.</b>
    ///
    /// <para>This is the physics consequence of D7's "direction is data": a silently-flipped wire
    /// gives a plausible wrong answer rather than a visible failure, so it is pinned here against the
    /// real inductance matrix.</para>
    /// </summary>
    [Fact]
    public void Tier4_ReversingAWire_NegatesExactlyItsOffDiagonalRowAndColumn()
    {
        var design = TestDesigns.ParallelArray(n: 4, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);

        var before = InductanceMatrix.Fill(WireMesh.Build(design));
        var beforeValues = (double[])before.Values.Clone();

        const int reversed = 1;
        design.AllWires().ElementAt(reversed).Reverse();
        var after = InductanceMatrix.Fill(WireMesh.Build(design));

        int n = before.Order;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double expected = (i == reversed) ^ (j == reversed)
                    ? -beforeValues[i * n + j]    // exactly one index is the reversed wire
                    : beforeValues[i * n + j];

                Assert.Equal(expected, after[i, j], Math.Abs(expected) * 1e-9);
            }
        }
    }

    /// <summary>Mirroring reverses traversal by default, and the checkbox genuinely suppresses it.</summary>
    [Fact]
    public void Tier4_Mirror_ReversesTraversalUnlessSuppressed()
    {
        var a = Loop();
        var firstBefore = a.Points[0];
        WireEdits.Mirror(a, 'y', 0);
        Assert.NotEqual(firstBefore.Y, a.Points[0].Y);

        var b = Loop();
        var bFirstX = b.Points[0].X;
        var bLastX = b.Points[^1].X;

        WireEdits.Mirror(b, 'y', 0, reverseTraversal: false);
        Assert.Equal(bFirstX, b.Points[0].X);
        Assert.Equal(bLastX, b.Points[^1].X);
    }

    // ---------------------------------------------------------------- tier 5

    /// <summary>
    /// TIER 5 — <b>straighten then re-apply the profile returns the original points exactly.</b>
    ///
    /// <para>Straighten preserves the point count precisely so this is possible; a straighten that
    /// resampled would make the edit destructive and would need a mesh rebuild (§0.3 item 4).</para>
    ///
    /// <para>The fixture routes the wire so its interior genuinely wanders in PLAN — a wire already
    /// straight in x-y has nothing for this operation to do, and the test would pass without
    /// exercising it.</para>
    /// </summary>
    [Fact]
    public void Tier5_StraightenThenRewriteTheSeedShape_ReturnsTheOriginalPoints()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var start = Point3.Mils(0, 0, 8);
        var end = Point3.Mils(120, 30, 1);

        var wire = LoopShape.CreateSeedWire(start, end, WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm);

        // Push one interior point sideways, so there is a route to straighten.
        var wandered = wire.Points[3];
        wire.Points[3] = wandered with { Y = wandered.Y + WBondUnits.ToNm(15, WBondUnit.Mil) };

        var original = wire.Points.ToArray();

        WireEdits.Straighten(wire);
        Assert.Equal(original.Length, wire.Points.Count);
        Assert.NotEqual(original[3].Y, wire.Points[3].Y);   // the route really was straightened

        LoopShape.Write(wire, wire.Points[0], wire.Points[^1], LoopShape.Seed(), loopNm);

        // A stamped shape owns z AND writes x-y between the feet, so re-stamping restores the
        // ORIGINAL loop — the pre-wander wire, not the wandered one this test started from.
        var pristine = LoopShape.CreateSeedWire(start, end, WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm);
        for (int i = 0; i < pristine.Points.Count; i++)
            Assert.Equal(pristine.Points[i], wire.Points[i]);
    }

    /// <summary>
    /// <b>Straighten touches x and y only — the loop height is left exactly alone.</b>
    ///
    /// <para>It straightens the wire's ROUTE, not its loop. Flattening z as well would turn "tidy up
    /// a wire that wanders sideways" into "destroy the loop", which is a different operation and not
    /// one anyone reaches for by this name.</para>
    /// </summary>
    [Fact]
    public void Tier5_Straighten_LeavesZUntouched()
    {
        var wire = LoopShape.CreateSeedWire(Point3.Mils(0, 0, 8), Point3.Mils(120, 30, 1),
                                            WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold",
                                            WBondUnits.ToNm(20.0, WBondUnit.Mil));

        var wandered = wire.Points[2];
        wire.Points[2] = wandered with { Y = wandered.Y + WBondUnits.ToNm(20, WBondUnit.Mil) };

        var zBefore = wire.Points.Select(p => p.Z).ToArray();

        WireEdits.Straighten(wire);

        Assert.Equal(zBefore, wire.Points.Select(p => p.Z).ToArray());
        Assert.NotEqual(wandered.Y + WBondUnits.ToNm(20, WBondUnit.Mil), wire.Points[2].Y);
    }

    /// <summary>Straighten pins both feet — it straightens the route, it does not move the bonds.</summary>
    [Fact]
    public void Tier5_Straighten_PinsBothFeet()
    {
        var wire = Loop();
        var start = wire.Points[0];
        var end = wire.Points[^1];

        WireEdits.Straighten(wire);

        Assert.Equal(start, wire.Points[0]);
        Assert.Equal(end, wire.Points[^1]);
    }

    // ---------------------------------------------------------------- tier 7

    /// <summary>
    /// TIER 7 — duplicate-with-pitch yields the asked-for count at the asked-for pitch, in one array.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(199)]
    public void Tier7_DuplicateWithPitch_YieldsTheRequestedCountAndPitch(int count)
    {
        var design = TestDesigns.ParallelArray(n: 1, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        var source = design.Arrays[0].Wires[0];
        long pitch = WBondUnits.ToNm(6.0, WBondUnit.Mil);

        var made = WireEdits.DuplicateWithPitch(design, source, pitchX: 0, pitchY: pitch, count);

        Assert.Equal(count, made.Count);
        Assert.Equal(count + 1, design.Arrays[0].Wires.Count);   // the source is not counted
        Assert.Single(design.Arrays);

        for (int k = 0; k < count; k++)
        {
            Assert.Equal(source.Points[0].Y + pitch * (k + 1), made[k].Points[0].Y);
            Assert.Equal(source.Points[0].X, made[k].Points[0].X);
            Assert.Equal(source.DiameterNm, made[k].DiameterNm);
            Assert.Equal(source.Material, made[k].Material);
        }

        design.Validate();
    }

    /// <summary>A zero pitch would land every copy on the source, so it is refused.</summary>
    [Fact]
    public void Tier7_ZeroPitch_IsRefused()
    {
        var design = TestDesigns.ParallelArray(n: 1, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        var source = design.Arrays[0].Wires[0];

        Assert.Throws<ArgumentException>(() => WireEdits.DuplicateWithPitch(design, source, 0, 0, 4));
    }

    // ---------------------------------------------------------------- nudge

    /// <summary>
    /// The nudge axis follows the VIEW: up is +z in the profile view and +y in the layout view (D8).
    /// The step itself is a bonder-process quantity and does not follow the display unit.
    /// </summary>
    [Fact]
    public void Nudge_UpMeansPlusYInLayoutAndPlusZInProfile()
    {
        foreach (var (view, expectY, expectZ) in new[]
        {
            (EditorView.Layout, 1L, 0L),
            (EditorView.Profile, 0L, 1L),
        })
        {
            var design = TestDesigns.ParallelArray(n: 1, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
            var wire = design.Arrays[0].Wires[0];
            var before = wire.Points[0];

            var selection = new WireSelection { Wires = { 0 } };
            long step = WireEdits.DefaultNudgeNm;

            WireEdits.Nudge(design, selection, 0, 1, step, view);

            Assert.Equal(before.Y + expectY * step, wire.Points[0].Y);
            Assert.Equal(before.Z + expectZ * step, wire.Points[0].Z);
        }
    }

    /// <summary>The two shipped steps are 1 mil and 5 mil (WB25).</summary>
    [Fact]
    public void NudgeSteps_AreOneAndFiveMil()
    {
        Assert.Equal(WBondUnits.ToNm(1.0, WBondUnit.Mil), WireEdits.DefaultNudgeNm);
        Assert.Equal(WBondUnits.ToNm(5.0, WBondUnit.Mil), WireEdits.CoarseNudgeNm);
    }

    /// <summary>
    /// A selected SEGMENT carries both its endpoints, which is what makes a dragged segment stay
    /// attached at both ends by construction rather than by a constraint (§6.3).
    /// </summary>
    [Fact]
    public void Nudge_ASelectedSegment_CarriesBothItsEndpoints()
    {
        var design = TestDesigns.ParallelArray(n: 1, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        var wire = design.Arrays[0].Wires[0];
        LoopShape.Write(wire, wire.Points[0], wire.Points[^1], LoopShape.Seed(),
                        WBondUnits.ToNm(20.0, WBondUnit.Mil));

        var before = wire.Points.ToArray();
        var selection = new WireSelection { Segments = { new SegmentRef(0, 2) } };

        WireEdits.Nudge(design, selection, 0, 1, WireEdits.DefaultNudgeNm, EditorView.Profile);

        long step = WireEdits.DefaultNudgeNm;
        Assert.Equal(before[2].Z + step, wire.Points[2].Z);
        Assert.Equal(before[3].Z + step, wire.Points[3].Z);
        Assert.Equal(before[1].Z, wire.Points[1].Z);
        Assert.Equal(before[4].Z, wire.Points[4].Z);
    }
}
