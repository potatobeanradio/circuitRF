// ANT-2 — the detail floor (M1) and the locally-graded edge fan (M2).
//
// Both changes exist because of the same measurement: an imported patch board asked for 704,482
// unknowns on the default mesh, and 14x of that was bought by connector via lands and
// aperture-rounded corners ~310 um across — lambda_g/265 on that board, and ~9 mm from anything
// electrically interesting. Neither change is antenna-specific; both fix every imported board.
//
// MESHER TESTS ONLY, and that is the brief's own rule for this file's subject: SurfaceMesher.Mesh on
// a hand-built PlanarProblem is milliseconds where a de-embedded planar point is tens of seconds.
// The one accuracy question that genuinely needs solved s-parameters was taken once in a scratch
// harness and reported in RESOLVED.md; it is deliberately not a test.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class DetailFloorTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>
    /// <b>Auto: false in every fixture here, and it is not decoration.</b>
    /// <c>PlanarMeshSettings.Default with { EdgeMesh = false }</c> is INERT — <c>Default</c> has
    /// <c>Auto = true</c> and <c>Resolved</c> collapses it back to the defaults — so a fixture that
    /// varies the edge mesh through <c>Default</c> silently tests the same mesh twice. That trap is
    /// recorded in brief-em-transmission-line-mesh.md §0a and has already cost this area once.
    /// </summary>
    private static PlanarMeshSettings At(int div, bool tl = true, bool edge = true,
                                         int cpw = 20, int across = 4) =>
        new(Auto: false, CellsPerWavelength: cpw, EdgeMesh: edge, EdgeCells: 3,
            MinCellsAcrossConductor: across,
            CurrentModel: tl ? PlanarCurrentModel.TransmissionLine : PlanarCurrentModel.None,
            DetailFloorDivisor: div);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The fixture: the shape the brief measured, reduced to its essential.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A patch fed by a narrow line, with a cluster of sub-wavelength connector detail at the far
    /// end of the feed. <b>The detail is what a Gerber import brings in</b> — via lands and rounded
    /// aperture corners — and on the measured board it, not the feed, was the narrowest metal.
    /// </summary>
    private static PlanarProblem PatchWithConnectorDetail(
        double detailM = 310e-6, double fHz = 1.74e9, bool detail = true)
    {
        const double patchW = 41.3e-3, patchH = 49.4e-3, feedW = 349.3e-6, totalX = 55.61e-3;
        double patchX0 = totalX - patchW, yc = patchH / 2;

        var polys = new List<PlanarPolygon>
        {
            PlanarLineFixtures.Rect(patchX0, 0, totalX, patchH),
            PlanarLineFixtures.Rect(4e-3, yc - feedW / 2, patchX0, yc + feedW / 2),
        };
        if (detail)
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                double x = 2.2e-3 + 2e-3 * Math.Cos(a), y = yc + 2e-3 * Math.Sin(a);
                polys.Add(PlanarLineFixtures.Rect(x - detailM / 2, y - detailM / 2,
                                                  x + detailM / 2, y + detailM / 2));
            }

        return new PlanarProblem([new PlanarConductorLayer("Top", polys, 5.8e7, 35e-6)],
                                 new GroundedSlab(203.2e-6, new EmMaterial(4.4, 0.02)), fHz);
    }

    private static IEnumerable<PlanarProblem> Fixtures()
    {
        yield return PatchWithConnectorDetail();
        yield return PatchWithConnectorDetail(detail: false);
        yield return PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 200e-6, 10e-3, 10e9);
        yield return PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 2.9e-3, 20e-3, 10e9);
        yield return PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 200e-6, 10e-3, 10e9);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 1 — MONOTONICITY. Both changes may only COARSEN, and that is structural, not a hope.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RaisingTheDetailFloor_NeverRaisesTheCellCount()
    {
        // Every width the mesher measures is raised to the floor and none is lowered, so the size
        // field is pointwise non-decreasing in the floor. A count that goes UP means a floor was
        // applied somewhere as a ceiling, or an attractor's own c0 escaped it — and because the
        // control exists to get an over-refined import under the ceiling, a user who raises it and
        // gets MORE unknowns has been handed the opposite of the control they reached for.
        int[] ladder = [0, 2000, 1000, 500, 200, 100];
        foreach (var problem in Fixtures())
            foreach (bool tl in new[] { false, true })
                foreach (bool edge in new[] { false, true })
                {
                    int prev = int.MaxValue;
                    foreach (int div in ladder)
                    {
                        var r = SurfaceMesher.Mesh(problem, At(div, tl, edge),
                                                   PlanarEdgeReference.LocalConductorWidth);
                        // div == 0 is "off", which is the FINEST rung and therefore the first.
                        Assert.True(r.CellCount <= prev,
                            $"tl={tl} edge={edge} λ_g/{div}: {r.CellCount} cells against {prev} at " +
                            "the finer floor — the detail floor coarsened nothing and refined something");
                        prev = r.CellCount;
                    }
                }
    }

    [Fact]
    public void WithTheEdgeMeshOff_NoFanIsBuilt_UnderAnyReference()
    {
        // A regression this brief's own change introduced, and this is the shape that catches it.
        // c0 is ZERO when the edge mesh is off, and the per-edge branch computes max(c0, 0.03·w) —
        // which is 0.03·w even at c0 = 0. That was harmless only while the "is anything graded" gate
        // read a single global growth rate (negative there); a PER-ATTRACTOR rate makes that gate
        // read the attractors themselves, so an edge mesh the user had turned OFF came back on.
        // Measured on the patch fixture before the fix: 40,952 unknowns with it off against 11,754
        // with it on — not merely wrong but the wrong way round.
        //
        // The gate is stated as "every reference gives the SAME mesh" rather than as a count
        // comparison against the edge mesh being on, because the count comparison is not an
        // invariant: with the transmission-line mesh on, turning the edge mesh OFF drops the field's
        // own Lipschitz rate to MinGrowthRatio (c0Probe is 0, so there is no fan ratio to borrow),
        // which smooths the pitch field harder and can legitimately produce MORE cells. That is
        // pre-existing and the mesher already says so in its own note.
        foreach (var problem in Fixtures())
            foreach (bool tl in new[] { false, true })
                foreach (int div in new[] { 0, 500 })
                {
                    var s = At(div, tl, edge: false);
                    var global = SurfaceMesher.Mesh(problem, s, PlanarEdgeReference.ConductorWidth);
                    var local  = SurfaceMesher.Mesh(problem, s, PlanarEdgeReference.LocalConductorWidth);

                    Assert.Equal(0, local.LongestEdgeFanCells);
                    Assert.Equal(global.CellCount, local.CellCount);
                    Assert.Equal(global.Mesh.GridX, local.Mesh.GridX);
                    Assert.Equal(global.Mesh.GridY, local.Mesh.GridY);
                }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 2 — BIT-IDENTITY where nothing should change
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(200e-6, 10e-3)]
    [InlineData(2.9e-3, 20e-3)]
    public void OnAUniformLineWhoseMetalIsAboveTheFloor_TheMeshIsBitIdenticalToTheFloorBeingOff(
        double w, double len)
    {
        // The brief's own gate: "a board whose narrowest metal is already above the detail floor,
        // with a uniform rim, meshes bit-identically to today". At 10 GHz on FR-4 λ_g is 14.3 mm, so
        // the shipped λ_g/200 floor is 71.5 µm and both these lines are far above it — nothing may
        // move, to the last bit, on either the pitch or the fan. (The extent cap holds the floor
        // lower still on a line this narrow, which only makes the statement stronger.)
        var line = PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, w, len, 10e9);
        foreach (var kind in new[] { PlanarEdgeReference.ConductorWidth,
                                     PlanarEdgeReference.LocalConductorWidth })
        {
            var off = SurfaceMesher.Mesh(line, At(0, tl: false), kind);
            var on  = SurfaceMesher.Mesh(line, At(PlanarMeshSettings.DefaultDetailFloorDivisor,
                                                  tl: false), kind);
            Assert.Equal(off.CellCount, on.CellCount);
            Assert.Equal(off.Mesh.GridX, on.Mesh.GridX);
            Assert.Equal(off.Mesh.GridY, on.Mesh.GridY);
            Assert.Equal(0, on.ShapesBelowDetailFloor);
        }
    }

    [Fact]
    public void TheGlobalEdgeReferences_StillMeshExactlyAsTheyAlwaysDid()
    {
        // Every recorded number in HISTORY.md was taken on ConductorWidth or CellSize with the
        // per-axis pitch rule, so M2's per-attractor growth rate must be INERT there — and it is,
        // structurally: with no pitch field the local bulk IS hMax, so every attractor derives the
        // same ratio the global one already had. 198 is M0's own post-fix ladder at 4 across; the
        // PRE-M0 number was 180 and quoting that one is the mistake this line exists to prevent.
        var line = PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 200e-6, 10e-3, 10e9);
        Assert.Equal(198, SurfaceMesher.Mesh(line, At(PlanarMeshSettings.DefaultDetailFloorDivisor,
                                                     tl: false),
                                             PlanarEdgeReference.ConductorWidth).CellCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 3 — TRANSLATION INVARIANCE, the hard gate. Both changes touch a size field.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MovingTheArtwork3Point7mm_LeavesTheMeshUnchanged()
    {
        // L8b's own knife edge: a discontinuous size field puts the marcher on the discontinuity and
        // moving the same rectangle 3.7 mm changed the mesh by 33%. The detail floor is a floor on a
        // MEASUREMENT rather than on a coordinate, and the per-attractor rate is still a Lipschitz
        // lower envelope, so neither can reintroduce it — but both touch the field, so it is asserted
        // rather than argued.
        const double d = 3.7e-3;
        foreach (int div in new[] { 0, 500, 200 })
            foreach (bool tl in new[] { false, true })
            {
                var at0 = SurfaceMesher.Mesh(PatchWithConnectorDetail(), At(div, tl),
                                             PlanarEdgeReference.LocalConductorWidth);
                var moved = SurfaceMesher.Mesh(Translate(PatchWithConnectorDetail(), d), At(div, tl),
                                               PlanarEdgeReference.LocalConductorWidth);

                Assert.Equal(at0.CellCount, moved.CellCount);
                Assert.Equal(at0.Mesh.GridX.Count, moved.Mesh.GridX.Count);
                Assert.Equal(at0.Mesh.GridY.Count, moved.Mesh.GridY.Count);
                for (int i = 0; i < at0.Mesh.GridX.Count; i++)
                    Assert.Equal(at0.Mesh.GridX[i], moved.Mesh.GridX[i] - d, 12);
                for (int i = 0; i < at0.Mesh.GridY.Count; i++)
                    Assert.Equal(at0.Mesh.GridY[i], moved.Mesh.GridY[i] - d, 12);
            }

        static PlanarProblem Translate(PlanarProblem p, double d) => new(
            [.. p.Layers.Select(l => new PlanarConductorLayer(l.Name,
                [.. l.Polygons.Select(g => new PlanarPolygon(
                    [.. g.Outer.Select(v => new EmPoint(v.X + d, v.Y + d))]))],
                l.SigmaSm, l.ThicknessM))],
            p.Slab, p.MaxFrequencyHz);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 4 — the two changes actually DO something, on the shape they were written for
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SubWavelengthConnectorDetail_StopsSettingTheMeshOnceItIsBelowTheFloor()
    {
        // A gate nothing can fail is not a gate, so this asserts a real reduction and not merely
        // "<=". λ_g at 1.74 GHz in εᵣ 4.4 is 82.1 mm, so λ_g/200 is 411 µm and the 310 µm detail
        // falls below it while λ_g/1000 (82 µm) leaves it alone.
        var p = PatchWithConnectorDetail();
        var loose = SurfaceMesher.Mesh(p, At(1000), PlanarEdgeReference.LocalConductorWidth);
        var tight = SurfaceMesher.Mesh(p, At(200),  PlanarEdgeReference.LocalConductorWidth);

        _out.WriteLine($"λ_g/1000: {loose.CellCount:N0} cells, {loose.UnknownCount:N0} unknowns, " +
                       $"{loose.ShapesBelowDetailFloor} shape(s) below");
        _out.WriteLine($"λ_g/200 : {tight.CellCount:N0} cells, {tight.UnknownCount:N0} unknowns, " +
                       $"{tight.ShapesBelowDetailFloor} shape(s) below");

        Assert.Equal(0, loose.ShapesBelowDetailFloor);
        // NINE, not eight: λ_g/200 is 411 µm here and the 349.3 µm FEED is below it too. That is the
        // §3 finding rather than a fixture accident — the connector detail and the feed on this board
        // are within 13% of each other in width, so no λ-relative floor separates them, and a floor
        // coarse enough to reach the artefact also coarsens a conductor the current flows along.
        Assert.Equal(9, tight.ShapesBelowDetailFloor);
        Assert.True(tight.CellCount < loose.CellCount / 2,
            $"the detail floor did not reach the detail it exists for: {tight.CellCount} against " +
            $"{loose.CellCount}");

        // And the reduction is in the GRID, not in cells that happened to miss the metal — which is
        // the difference between not meshing an artefact and merely rejecting its cells later.
        Assert.True(tight.Mesh.GridX.Count < loose.Mesh.GridX.Count);
        Assert.True(tight.Mesh.GridY.Count < loose.Mesh.GridY.Count);

        // The shapes are still MESHED. The floor changes what the geometry may ASK for, never what
        // is drawn — removing a rounded corner is a modelling decision and it is the user's.
        Assert.True(tight.MeshedAreaM2 > 0.99 * loose.MeshedAreaM2,
            "metal went missing: the floor is dropping artwork rather than declining to size on it");
    }

    [Fact]
    public void TheEdgeFanIsNoLongerRatedAgainstTheFinestPitchSomewhereElseOnTheBoard()
    {
        // M2. A fan's length is log_r(bulk/c0), and r was ONE global number derived against
        // min(hx, hy) — which with the pitch field on is its FINEST value anywhere, not the bulk any
        // particular fan climbs to. So every fan on the board was rated for the narrowest neck on it.
        //
        // <b>The baseline is recorded here rather than computed</b>, because the pre-change
        // arithmetic cannot be reached from the shipped code: with the rate forced back to the
        // global one this fixture meshes at 3,828 cells / 7,356 unknowns, against the 3,373 / 6,471
        // below — and on the fuller reconstruction of the measured board in the scratch harness,
        // 7,570 / 14,709 against 6,071 / 11,754. Pinned rather than asserted as an inequality, so a
        // later change that moves it is a deliberate act with a number to update.
        // Asked with the detail floor OFF, so this measures M2 alone rather than M1 through it.
        var r = SurfaceMesher.Mesh(PatchWithConnectorDetail(), At(0),
                                   PlanarEdgeReference.LocalConductorWidth);
        _out.WriteLine($"{r.CellCount:N0} cells, {r.UnknownCount:N0} unknowns, " +
                       $"longest fan {r.LongestEdgeFanCells}");
        Assert.Equal(3373, r.CellCount);
        Assert.Equal(6471, r.UnknownCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 5 — the report SAYS what happened
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheReportNamesTheFloor_TheShapesBelowIt_AndTheFanLength()
    {
        // A floor that silently coarsens a mesh is the same failure mode as a control that silently
        // does nothing: in both cases the number on screen is not the number the geometry asked for.
        var r = SurfaceMesher.Mesh(PatchWithConnectorDetail(), At(200),
                                   PlanarEdgeReference.LocalConductorWidth);
        string notes = string.Join("\n", r.Notes);
        _out.WriteLine(notes);

        Assert.Contains("Detail floor", notes);
        Assert.Contains("λ_g/200", notes);
        Assert.Contains("9 shape(s) are narrower", notes);
        Assert.Contains("would have forced", notes);       // the pitch the floor displaced
        Assert.Contains("Longest edge fan", notes);

        // …and when it changed nothing, it says THAT — "the floor is on and it did nothing here" is
        // the answer to a question a user reading the count will ask.
        var clean = SurfaceMesher.Mesh(PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 2.9e-3, 20e-3, 10e9),
                                       At(500, tl: false), PlanarEdgeReference.LocalConductorWidth);
        Assert.Contains("changed nothing here", string.Join("\n", clean.Notes));
    }

    [Fact]
    public void WithNoSweepFrequency_ThereIsNoFloorAtAll_AndTheReportSaysSo()
    {
        // It is λ-RELATIVE, which is the whole reason it can have a default; with no frequency there
        // is no λ_g to be relative to and geometry is all there is to go on. Inventing an absolute
        // number here would be exactly the decision §3 forbids.
        var p = new PlanarProblem(
            [new PlanarConductorLayer("Top", [PlanarLineFixtures.Rect(0, 0, 10e-3, 200e-6)], 5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, 0);
        var r = SurfaceMesher.Mesh(p, At(500, tl: false), PlanarEdgeReference.LocalConductorWidth);

        Assert.Equal(0, r.DetailFloorM);
        Assert.Contains("no wavelength to be relative to", string.Join("\n", r.Notes));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 6 — §7's defect: the refusal must branch on WHICH PITCH RULE built the mesh
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WithTheTransmissionLineMeshOn_TheRefusalDoesNotClaimTheWavelengthKnobsAreInert()
    {
        // The measured defect: on this board the refusal said "LOWERING CELLS PER WAVELENGTH OR MESH
        // FREQUENCY WILL NOT REDUCE THIS COUNT" while the field it had just built spanned 78 µm to
        // 3.485 mm — and lowering cells/λ was exactly what took the same board from 23,416 unknowns
        // to 1,909. The sentence is correct for the per-axis rule and wrong for the field, and it
        // sends a user in the opposite direction from the one that works.
        var p = PatchWithConnectorDetail();
        var withField = SurfaceMesher.Mesh(p, At(0, tl: true), PlanarEdgeReference.LocalConductorWidth);
        Assert.Equal(PlanarBudgetVerdict.Refused, withField.Verdict);
        _out.WriteLine(withField.Refusal!);

        Assert.DoesNotContain("WILL NOT REDUCE THIS COUNT", withField.Refusal);
        Assert.Contains("lower Cells per wavelength", withField.Refusal);
        Assert.Contains("coarsen the Detail floor", withField.Refusal);

        // …and the per-axis rule's own refusal is unchanged, because there the sentence is TRUE.
        var perAxis = SurfaceMesher.Mesh(p, At(0, tl: false), PlanarEdgeReference.LocalConductorWidth);
        Assert.Equal(PlanarBudgetVerdict.Refused, perAxis.Verdict);
        Assert.Contains("WILL NOT REDUCE THIS COUNT", perAxis.Refusal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 7 — the bounded grading ratio and the aspect cap still hold
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheFanReachesTheEdgeCellsAsked_OrTheReportSaysWhyItCannot()
    {
        // <b>This is what "the bounded grading ratio still holds" is observable AS.</b> Every
        // realised rate is Math.Max of two GrowthRatioFor results and GrowthRatioFor clamps to
        // [MinGrowthRatio, MaxGrowthRatio], so the bound holds by construction; what a test can see
        // is its consequence, which is how many cells a fan takes to reach the bulk. Before M2 the
        // rate was derived against the FINEST pitch the field asks for anywhere rather than the bulk
        // the fan actually climbs to, so every fan on a board was rated for its narrowest neck.
        //
        // <b>The bound is not always reachable, and where it is not the report SAYS so rather than
        // the mesher quietly coarsening the edge cell to make the number come out right.</b> The
        // ratio is clamped at 3×, so a climb steeper than 3^EdgeCells cannot be made in EdgeCells
        // cells — a 349.3 µm feed rim beside a λ_g/20 bulk of 2.9 mm is 276×, which takes 5. Forcing
        // it back by flooring c0 was built and removed: it changed not one cell on the board this
        // brief is about, and it coupled the edge cell to Cells per wavelength, which broke the
        // transmission-line mesh's own orthogonality gate.
        //
        // <b>MaxCellAspect is NOT asserted here</b>, and that is not an omission — it governs the
        // pitch FIELD's own requirement and is gated there, by
        // TransmissionLineMeshTests.TheAspectCapBindsOnTheBULKPITCH_WhichIsTheOnlyThingItCanBind, whose
        // comment explains at length why asking it of the realised cells is the wrong test. Asking it of
        // the cells here read 101:1 with the cap working perfectly, for exactly that reason.
        //
        // <b>Asserting the CONSECUTIVE-GRIDLINE ratio instead was tried and is wrong</b>, and that is
        // worth keeping: the ratio between adjacent cells is not a grading rate. Two intervals either
        // side of a hard gridline are marched and rescaled onto their own endpoints independently
        // (3.62× across the feed's end face at λ_g/200), and inside one interval the enforcement pass
        // subdivides against the pitch FIELD at each cell's own midpoint, which on a field spanning
        // 27× legitimately steps 4.51× between neighbours. Neither is a fan.
        foreach (var problem in Fixtures())
            foreach (int div in new[] { 0, 500, 200 })
            {
                var r = SurfaceMesher.Mesh(problem, At(div, tl: true),
                                           PlanarEdgeReference.LocalConductorWidth);
                if (r.LongestEdgeFanCells <= 3) continue;
                Assert.Contains("That is more than the 3 asked for", string.Join("\n", r.Notes));
            }

        // …and the number is PINNED rather than merely allowed, so closing it later is a deliberate
        // act with a test to update rather than a silent drift. On this board the longest climb is
        // the connector detail's own 9.3 µm edge cell against a λ_g/20 bulk of 2.9 mm — 312×, which
        // at the clamped 3× per cell is 8 — and the shipped λ_g/200 floor takes it to 7.
        // The board this brief is about reaches it, which is the point of M2 — and the numbers are
        // PINNED rather than merely bounded, so a later change that moves them is a deliberate act
        // with a test to update rather than a silent drift.
        Assert.Equal(3, SurfaceMesher.Mesh(PatchWithConnectorDetail(), At(0, tl: true),
                                           PlanarEdgeReference.LocalConductorWidth)
                                     .LongestEdgeFanCells);
        Assert.Equal(3, SurfaceMesher.Mesh(PatchWithConnectorDetail(),
                                           At(PlanarMeshSettings.DefaultDetailFloorDivisor, tl: true),
                                           PlanarEdgeReference.LocalConductorWidth)
                                     .LongestEdgeFanCells);

        // …and the case that does NOT reach it is pinned too, so the overrun path is exercised
        // rather than merely allowed for. Connector deleted, this is the 349.3 µm feed rim's own
        // 10.5 µm edge cell against a λ_g/20 bulk of 4.1 mm — 391×, which at the clamped 3× per cell
        // is 5. The shipped λ_g/200 floor coarsens that rim's own width and takes it back to 3.
        var feedOnly = SurfaceMesher.Mesh(PatchWithConnectorDetail(detail: false), At(0, tl: true),
                                          PlanarEdgeReference.LocalConductorWidth);
        Assert.Equal(5, feedOnly.LongestEdgeFanCells);
        Assert.Contains("That is more than the 3 asked for", string.Join("\n", feedOnly.Notes));
        Assert.Equal(3, SurfaceMesher.Mesh(PatchWithConnectorDetail(detail: false),
                                           At(PlanarMeshSettings.DefaultDetailFloorDivisor, tl: true),
                                           PlanarEdgeReference.LocalConductorWidth)
                                     .LongestEdgeFanCells);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 8 — Auto does not throw the control away
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheDetailFloorSurvivesAuto()
    {
        // Auto means "choose the RESOLUTION for me", and which geometry is electrically real is not
        // a resolution. The failure shape this prevents is the one BoundaryCells, MeshFrequencyHz and
        // TransmissionLineMesh were each protected from in turn: a user sets a control, leaves Auto
        // on, and silently gets the other mesh.
        var s = PlanarMeshSettings.Default with { DetailFloorDivisor = 100 };
        Assert.True(s.Auto);
        Assert.Equal(100, s.Resolved.DetailFloorDivisor);

        var p = PatchWithConnectorDetail();
        Assert.Equal(SurfaceMesher.Mesh(p, s, PlanarEdgeReference.LocalConductorWidth).CellCount,
                     SurfaceMesher.Mesh(p, s.Resolved, PlanarEdgeReference.LocalConductorWidth).CellCount);
    }
}
