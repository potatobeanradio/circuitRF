namespace CircuitRF.WBond.Tests;

/// <summary>
/// WB-C3's framework-free half — the profile projection, hit-testing and the quality ladder.
/// </summary>
public class HitTestAndLadderTests
{
    private static WireMesh Mesh(double angleDeg = 0.0, double lengthMil = 100.0)
    {
        double rad = angleDeg * Math.PI / 180.0;
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };

        array.Wires.Add(new Wire
        {
            Points =
            {
                Point3.Mils(0, 0, 4),
                Point3.Mils(lengthMil * 0.3 * Math.Cos(rad), lengthMil * 0.3 * Math.Sin(rad), 24),
                Point3.Mils(lengthMil * Math.Cos(rad), lengthMil * Math.Sin(rad), 1),
            },
        });

        design.Arrays.Add(array);
        return WireMesh.Build(design);
    }

    // ---------------------------------------------------------------- projection

    /// <summary>
    /// <b>Two wires of the same loop shape at different angles and lengths project identically</b> in
    /// normalised mode — which is the whole point of §6.2's parameterisation.
    /// </summary>
    [Fact]
    public void NormalisedProjection_MakesAngleAndLengthStopMattering()
    {
        var flat = Mesh(angleDeg: 0.0, lengthMil: 100.0);
        var angled = Mesh(angleDeg: 37.0, lengthMil: 100.0);

        for (int i = 0; i < 3; i++)
        {
            var a = ProfileProjection.Project(flat.Wires[0], i, ProfileProjection.SpanMode.Normalised);
            var b = ProfileProjection.Project(angled.Wires[0], i, ProfileProjection.SpanMode.Normalised);

            Assert.Equal(a.Span, b.Span, 1e-6);
            Assert.Equal(a.Z, b.Z, 1.0);
        }
    }

    /// <summary>Absolute mode preserves true geometry, so a longer wire really is longer.</summary>
    [Fact]
    public void AbsoluteProjection_KeepsTrueGeometry()
    {
        var shortWire = Mesh(lengthMil: 60.0);
        var longWire = Mesh(lengthMil: 140.0);

        double a = ProfileProjection.Project(shortWire.Wires[0], 2, ProfileProjection.SpanMode.Absolute).Span;
        double b = ProfileProjection.Project(longWire.Wires[0], 2, ProfileProjection.SpanMode.Absolute).Span;

        Assert.Equal(140.0 / 60.0, b / a, 1e-3);
    }

    /// <summary>
    /// <b>Azimuths are averaged as VECTORS, not as angles.</b> Averaging 350° and 10° arithmetically
    /// gives 180°, which points the profile view exactly backwards.
    /// </summary>
    [Fact]
    public void MeanAzimuth_AveragesVectorsNotAngles()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };

        foreach (double deg in new[] { -10.0, 10.0 })
        {
            double rad = deg * Math.PI / 180.0;
            array.Wires.Add(new Wire
            {
                Points = { Point3.Mils(0, 0, 4), Point3.Mils(100 * Math.Cos(rad), 100 * Math.Sin(rad), 1) },
            });
        }
        design.Arrays.Add(array);

        double mean = ProfileProjection.MeanChordAzimuthRadians(design.AllWires());

        Assert.Equal(0.0, mean, 1e-9);            // not 180 degrees
        Assert.True(Math.Abs(mean) < 0.1);
    }

    /// <summary>An array of equal-span wires prefers the absolute axis; a mixed one prefers normalised.</summary>
    [Fact]
    public void PreferredMode_FollowsWhetherTheSpansAgree()
    {
        var uniform = TestDesigns.ParallelArray(n: 4, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        Assert.Equal(ProfileProjection.SpanMode.Absolute,
                     ProfileProjection.PreferredMode([.. uniform.AllWires()]));

        var mixed = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        foreach (double span in new[] { 40.0, 100.0, 180.0 })
            array.Wires.Add(new Wire { Points = { Point3.Mils(0, 0, 4), Point3.Mils(span, 0, 1) } });
        mixed.Arrays.Add(array);

        Assert.Equal(ProfileProjection.SpanMode.Normalised,
                     ProfileProjection.PreferredMode([.. mixed.AllWires()]));
    }

    // ---------------------------------------------------------------- hit testing

    /// <summary>A click on a vertex finds that vertex, not the segment through it.</summary>
    [Fact]
    public void AClickOnAVertex_FindsThePointNotTheSegment()
    {
        var mesh = Mesh();
        var apex = mesh.Wires[0].Points[1];

        var hit = WireHitTest.HitTestLayout(mesh, apex.X, apex.Y, WBondUnits.ToNm(2.0, WBondUnit.Mil));

        Assert.True(hit.Found);
        Assert.Equal(0, hit.Wire);
        Assert.Equal(1, hit.Point);
        Assert.False(hit.IsSegment);
    }

    /// <summary>A click in the middle of a run finds the segment.</summary>
    [Fact]
    public void AClickBetweenVertices_FindsTheSegment()
    {
        var mesh = Mesh();
        var a = mesh.Wires[0].Points[0];
        var b = mesh.Wires[0].Points[1];

        long midX = (a.X + b.X) / 2, midY = (a.Y + b.Y) / 2;
        var hit = WireHitTest.HitTestLayout(mesh, midX, midY, WBondUnits.ToNm(2.0, WBondUnit.Mil));

        Assert.True(hit.Found);
        Assert.True(hit.IsSegment);
        Assert.Equal(0, hit.Point);   // the segment's FIRST point, matching SegmentRef
    }

    /// <summary>
    /// <b>A vertex wins over a segment within the same tolerance</b>, because it is the smaller and
    /// more precise target — a user aiming at a vertex is not aiming at the line through it.
    /// </summary>
    [Fact]
    public void AVertex_WinsOverTheSegmentThroughIt()
    {
        var mesh = Mesh();
        var apex = mesh.Wires[0].Points[1];

        // Offset PERPENDICULAR to the wire's run. Offsetting ALONG it would put the cursor almost
        // exactly on a segment, where the segment genuinely IS the nearer thing and should win —
        // the bias prefers a vertex at comparable distance, it does not override geometry.
        long offset = WBondUnits.ToNm(0.4, WBondUnit.Mil);
        var hit = WireHitTest.HitTestLayout(mesh, apex.X, apex.Y + offset,
                                            WBondUnits.ToNm(3.0, WBondUnit.Mil));

        Assert.True(hit.Found);
        Assert.False(hit.IsSegment);
        Assert.Equal(1, hit.Point);
    }

    /// <summary>
    /// <b>A press INSIDE the drawn dot is a press on the vertex, even when it sits exactly on the
    /// segment through it</b> (owner, 2026-08-19: "the hitbox of the wire point needs to match the
    /// render size of the circle on the user's screen. Currently it feels smaller than the circle, so
    /// I get a lot of misses when I try to touch a wire point.").
    ///
    /// <para>This is the case the plain point-over-segment bias could not carry. Both segments meeting
    /// at an interior vertex PASS THROUGH it, so a press offset ALONG the wire is at distance ~0 from
    /// a segment while being however far from centre the user actually clicked — at bias 2 the segment
    /// took everything past half the dot's radius. The visible circle was therefore only half
    /// clickable, and only in the two lobes perpendicular to the line, which is exactly a hitbox that
    /// feels smaller than what is drawn.</para>
    ///
    /// <para>Probed at 90 % of the drawn radius so it is unambiguously INSIDE the circle, on a
    /// straight-in-plan wire where the segment distance is exactly zero — the hardest form of the
    /// case, not a near miss of it.</para>
    /// </summary>
    [Fact]
    public void APressInsideTheDrawnDot_FindsThePoint_EvenSittingOnTheSegment()
    {
        var mesh = Mesh();
        long diameter = mesh.Wires[0].DiameterNm;
        var apex = mesh.Wires[0].Points[1];

        double radius = WireHitTest.VertexRadiusNm(diameter);
        long along = (long)(radius * 0.9);
        Assert.True(along > 0);

        // Straight in plan (angleDeg 0), so this offset is ON the segment: its distance is zero.
        var hit = WireHitTest.HitTestLayout(mesh, apex.X + along, apex.Y,
                                            WBondUnits.ToNm(3.0, WBondUnit.Mil));

        Assert.True(hit.Found);
        Assert.False(hit.IsSegment, "a press inside the circle the user can see is a press on it");
        Assert.Equal(1, hit.Point);
    }

    /// <summary>
    /// The other half of the same rule: just OUTSIDE the dot the segment takes the press back. The
    /// vertex does not acquire an invisible catchment — the rule is "matches what is drawn", and a
    /// hitbox bigger than the circle would be the same complaint from the other side.
    /// </summary>
    [Fact]
    public void APressOutsideTheDrawnDot_StillFindsTheSegment()
    {
        var mesh = Mesh();
        double radius = WireHitTest.VertexRadiusNm(mesh.Wires[0].DiameterNm);
        var apex = mesh.Wires[0].Points[1];

        var hit = WireHitTest.HitTestLayout(mesh, apex.X + (long)(radius * 1.6), apex.Y,
                                            WBondUnits.ToNm(3.0, WBondUnit.Mil));

        Assert.True(hit.Found);
        Assert.True(hit.IsSegment);
    }

    /// <summary>Outside the tolerance nothing is hit, and it says so rather than returning wire 0.</summary>
    [Fact]
    public void AClickInEmptySpace_FindsNothing()
    {
        var mesh = Mesh();
        var hit = WireHitTest.HitTestLayout(mesh,
            WBondUnits.ToNm(5000, WBondUnit.Mil), WBondUnits.ToNm(5000, WBondUnit.Mil),
            WBondUnits.ToNm(2.0, WBondUnit.Mil));

        Assert.False(hit.Found);
        Assert.Equal(-1, hit.Wire);
    }

    /// <summary>The profile view hit-tests against span and z, so it finds the apex by its height.</summary>
    [Fact]
    public void TheProfileViewHitTest_UsesSpanAndZ()
    {
        var mesh = Mesh(angleDeg: 37.0);
        var projected = ProfileProjection.Project(mesh.Wires[0], 1);

        var hit = WireHitTest.HitTestProfile(mesh, projected.Span, (long)projected.Z,
                                             WBondUnits.ToNm(2.0, WBondUnit.Mil));

        Assert.True(hit.Found);
        Assert.Equal(1, hit.Point);
        Assert.False(hit.IsSegment);
    }

    // ---------------------------------------------------------------- the quality ladder

    /// <summary>
    /// <b>An overrunning frame steps the ladder down immediately.</b> A user feels one slow frame, so
    /// there is nothing to be gained by waiting for a second.
    ///
    /// <para>There are only TWO rungs since 2026-08-18 — the middle Chord rung published an
    /// inductance ~70 % low and rebuilt the mesh every frame, so it cost more than the top rung to
    /// produce a number nobody should look at. See <see cref="QualityLadder"/>'s own note.</para>
    /// </summary>
    [Fact]
    public void AnOverrunningFrame_StepsDownImmediately()
    {
        var ladder = new QualityLadder();
        Assert.Equal(DragQuality.Exact, ladder.Current);

        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Observe(30.0));

        // And it bottoms out rather than pretending to keep up.
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Observe(300.0));
    }

    /// <summary>
    /// <b>Stepping back up needs several comfortable frames.</b> Without that hysteresis the ladder
    /// oscillates between two rungs every frame, which is far more visible than staying one rung low.
    /// </summary>
    [Fact]
    public void SteppingBackUp_RequiresSeveralComfortableFrames()
    {
        var ladder = new QualityLadder();

        // Just over budget — down, but not far enough over to be locked out of retrying.
        ladder.Observe(QualityLadder.FrameBudgetMs * 1.5);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);
        Assert.False(ladder.IsLockedDown);

        // One comfortable frame is not enough.
        ladder.Observe(2.0);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);

        for (int i = 1; i < QualityLadder.StepUpAfterComfortableFrames; i++) ladder.Observe(2.0);
        Assert.Equal(DragQuality.Exact, ladder.Current);
    }

    /// <summary>
    /// <b>A frame that overruns BADLY is not retried for the rest of the drag.</b>
    ///
    /// <para>Feedback alone retries the top rung every four frames. At 500 wires one exact frame is
    /// seconds, so that retry is a periodic multi-second hitch for as long as the drag lasts — which
    /// is the owner's "the frame rate is slow when I drag 500 wires". Remembering that the rung was
    /// measured hopeless is not a cost model; it is the measurement.</para>
    /// </summary>
    [Fact]
    public void AFrameThatOverrunsBadly_IsNotRetriedInThisDrag()
    {
        var ladder = new QualityLadder();

        ladder.Observe(QualityLadder.FrameBudgetMs * QualityLadder.LockoutOverrunFactor * 2.0);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);
        Assert.True(ladder.IsLockedDown);

        for (int i = 0; i < 50; i++) ladder.Observe(0.1);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);

        // ...but the NEXT drag starts clean, because it may be a quite different selection.
        ladder.BeginDrag();
        Assert.Equal(DragQuality.Exact, ladder.Current);
        Assert.False(ladder.IsLockedDown);
    }

    /// <summary>
    /// <b>A drag whose fill obviously cannot fit never attempts one.</b>
    ///
    /// <para>Feedback has to PAY a catastrophic frame to learn what the block count says for free:
    /// 500 wires of 500 is 250,000 wire-pair blocks, ~2 s against a 16.7 ms budget. The bound is only
    /// asked whether that is hopeless, which no factor-of-two uncertainty in µs/block can change.</para>
    /// </summary>
    [Fact]
    public void ADragTooBigToFill_StartsFrozenAndStaysThere()
    {
        var ladder = new QualityLadder();

        ladder.BeginDrag(movingWires: 500, totalWires: 500);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);
        Assert.True(ladder.IsLockedDown);

        for (int i = 0; i < 50; i++) ladder.Observe(0.05);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);

        // A one-wire drag on the same design is affordable and starts at the top.
        ladder.BeginDrag(movingWires: 1, totalWires: 500);
        Assert.Equal(DragQuality.Exact, ladder.Current);
        Assert.False(ladder.IsLockedDown);
    }

    /// <summary>
    /// The block count is <c>k·N − k(k−1)/2</c> — one row per moved wire, less the intra-selection
    /// pairs the fill already covered — and it reduces to N for one wire, which is the 600 blocks
    /// WB13 measured at N = 600.
    /// </summary>
    [Theory]
    [InlineData(1, 600, 600L, true)]
    [InlineData(3, 600, 1797L, true)]
    [InlineData(50, 600, 28775L, false)]
    [InlineData(500, 500, 125250L, false)]
    public void TheAffordabilityBoundFollowsTheBlockCount(int moving, int total, long blocks, bool affordable)
    {
        var ladder = new QualityLadder();

        Assert.Equal(blocks, QualityLadder.FillBlocks(moving, total));
        Assert.Equal(affordable, ladder.CanAffordExactFill(moving, total));
    }

    /// <summary>
    /// A drag that is merely MARGINAL over the bound starts frozen but is allowed to prove itself —
    /// the bound is 2× pessimistic on purpose, so locking on it would freeze drags that would have
    /// kept up. Only a hopeless one is locked.
    /// </summary>
    [Fact]
    public void AMarginalDrag_StartsFrozenButIsNotLockedOut()
    {
        var ladder = new QualityLadder();

        ladder.BeginDrag(movingWires: 4, totalWires: 600);   // ~19 ms bound against a 16.7 ms budget
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);
        Assert.False(ladder.IsLockedDown);

        for (int i = 0; i < QualityLadder.StepUpAfterComfortableFrames; i++) ladder.Observe(2.0);
        Assert.Equal(DragQuality.Exact, ladder.Current);
    }

    /// <summary>
    /// A frame inside budget but not comfortably HOLDS the rung — it neither steps down (it fit) nor
    /// up (it only just fit). This is the band the hysteresis exists to create.
    /// </summary>
    [Fact]
    public void AFrameInsideBudgetButNotComfortably_HoldsTheRung()
    {
        var ladder = new QualityLadder();
        ladder.Observe(QualityLadder.FrameBudgetMs * 1.5);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);

        for (int i = 0; i < 10; i++)
            Assert.Equal(DragQuality.FreezeAndSnap, ladder.Observe(QualityLadder.FrameBudgetMs * 0.8));
    }

    /// <summary>
    /// <b>Headroom is measured, not assumed.</b> The drag path spends leftover budget on the
    /// capacitance and on nothing else, so this is what stops that spend from ever being the reason a
    /// frame is slow.
    /// </summary>
    [Fact]
    public void HeadroomIsOnlyReportedAfterAComfortableFrame()
    {
        var ladder = new QualityLadder();
        Assert.False(ladder.HasHeadroom);   // nothing measured yet

        ladder.Observe(QualityLadder.FrameBudgetMs * 0.9);
        Assert.False(ladder.HasHeadroom);   // it fit, but only just

        ladder.Observe(QualityLadder.FrameBudgetMs * 0.2);
        Assert.True(ladder.HasHeadroom);
    }

    /// <summary>A comfortable frame at the top rung stays at the top.</summary>
    [Fact]
    public void AComfortableFrameAtTheTop_StaysAtTheTop()
    {
        var ladder = new QualityLadder();
        for (int i = 0; i < 20; i++)
            Assert.Equal(DragQuality.Exact, ladder.Observe(5.27));   // the measured one-wire frame
    }

    /// <summary>
    /// Every drag begins optimistic. Inheriting the last drag's verdict would apply a 200-wire
    /// selection's conclusion to a one-wire one.
    /// </summary>
    [Fact]
    public void EachDrag_BeginsAtTheTopRung()
    {
        var ladder = new QualityLadder();
        ladder.Observe(300.0);
        ladder.Observe(300.0);
        Assert.Equal(DragQuality.FreezeAndSnap, ladder.Current);

        ladder.BeginDrag();
        Assert.Equal(DragQuality.Exact, ladder.Current);
        Assert.Equal(0.0, ladder.LastFrameMs);
    }

    /// <summary>Every rung below the top marks its readout provisional (WB15).</summary>
    [Fact]
    public void EveryDegradedRung_MarksTheReadoutProvisional()
    {
        var ladder = new QualityLadder();
        Assert.False(ladder.IsProvisional);

        ladder.Observe(30.0);
        Assert.True(ladder.IsProvisional);
    }

    /// <summary>
    /// Collapsing to the chord is <b>non-destructive</b>: the original points come back exactly. The
    /// degraded rung is a solving shortcut, never an edit.
    /// </summary>
    [Fact]
    public void CollapsingToTheChord_IsReversibleExactly()
    {
        var mesh = Mesh();
        var wire = mesh.Wires[0];
        var original = wire.Points.ToArray();

        var captured = QualityLadder.CollapseToChord(wire);

        Assert.Equal(2, wire.Points.Count);
        Assert.Equal(original[0], wire.Points[0]);
        Assert.Equal(original[^1], wire.Points[^1]);

        QualityLadder.RestoreFromChord(wire, captured);
        Assert.Equal(original, wire.Points.ToArray());
    }

    /// <summary>A two-point wire is already its own chord, so collapsing it changes nothing.</summary>
    [Fact]
    public void CollapsingAStraightWire_IsANoOp()
    {
        var wire = new Wire { Points = { Point3.Mils(0, 0, 20), Point3.Mils(100, 0, 20) } };
        var original = wire.Points.ToArray();

        QualityLadder.CollapseToChord(wire);
        Assert.Equal(original, wire.Points.ToArray());
    }

    /// <summary>An impossible budget is refused rather than making every frame overrun.</summary>
    [Fact]
    public void ANonPositiveBudget_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QualityLadder(0.0));
    }
}
