// L8b — the surface mesher's oracle ladder, Tiers 0-5 and 7.
//
// There is no solver in this slice and nothing to compare against a reference, which makes every
// tier here EXACT rather than tolerant: a mesh either tiles its input or it does not; a cell either
// honours λ_g/N or it does not; N either equals the cell count's shared-internal-edge count or it
// does not. That is a luxury, not a shortcut — the assertions below are equalities wherever the
// arithmetic permits one.
//
// Tier 6 (staircasing measured on REAL library PCells) and the PCell half of Tier 7 live in
// tests/Ui.Tests/Em/PlanarMeshPCellTests.cs — MBendPCell/MKlopfPCell/MTaperPCell are in src/Ui, and
// the reference graph is Ui -> Engine, so an Engine test cannot reach them. The physics half of
// Tier 7 (§10.7's own closed-form worked example) is here, where it belongs.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom;

public class SurfaceMesherTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    private static PlanarProblem Problem(
        GroundedSlab slab, double fHz, params PlanarPolygon[] polys) =>
        new([new PlanarConductorLayer("Metal", polys, 5.8e7, 35e-6)], slab, fHz);

    /// <summary>§10.7's own hero: 50 Ω microstrip on 1.6 mm FR-4 is W ≈ 2.9 mm; a 20 mm line.</summary>
    private static PlanarProblem Fr4Hero(double fHz = 10e9) =>
        Problem(GroundedSlab.Fr4Starter, fHz, Rect(0, 0, 20e-3, 2.9e-3));

    /// <summary>The MMIC counterpart: a 72 µm line on 100 µm GaAs, 2 mm long.</summary>
    private static PlanarProblem GaAsHero(double fHz = 10e9) =>
        Problem(GroundedSlab.GaAsStarter, fHz, Rect(0, 0, 2e-3, 72e-6));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 0 — the mesh is a mesh
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T0_1_UnionOfCellsEqualsTheInputArea_Exactly_ForManhattanArtwork()
    {
        // Deliberately not at the origin and not a round multiple of anything: the grid is anchored
        // to the ARTWORK, so an awkward placement must tile just as exactly as a tidy one.
        var problem = Problem(GroundedSlab.Fr4Starter, 10e9, Rect(1.234e-3, -5.678e-3, 21.234e-3, -2.778e-3));
        var r = SurfaceMesher.Mesh(problem);

        double meshArea = 0;
        foreach (var c in r.Mesh.Cells) meshArea += c.Area;
        double inputArea = problem.Layers[0].Polygons[0].Area();

        Assert.True(Math.Abs(meshArea - inputArea) <= 1e-12 * inputArea,
            $"mesh area {meshArea:G17} vs input {inputArea:G17}");
    }

    [Fact]
    public void T0_1b_UnionOfCellsMatchesTheInputBOUNDARY_NotOnlyItsArea()
    {
        // Two different wrong meshes can have the same area. The boundary check: the union's own
        // extent equals the input's, and no cell strays outside the metal.
        var problem = Fr4Hero();
        var r = SurfaceMesher.Mesh(problem);
        var poly = problem.Layers[0].Polygons[0];
        var (px0, py0, px1, py1) = poly.Bounds();

        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var c in r.Mesh.Cells)
        {
            x0 = Math.Min(x0, c.XMin); y0 = Math.Min(y0, c.YMin);
            x1 = Math.Max(x1, c.XMax); y1 = Math.Max(y1, c.YMax);

            // No cell strays outside the metal: every corner is inside or exactly on the boundary.
            Assert.True(c.XMin >= px0 - 1e-15 && c.XMax <= px1 + 1e-15 &&
                        c.YMin >= py0 - 1e-15 && c.YMax <= py1 + 1e-15,
                $"cell ({c.XMin:G6},{c.YMin:G6})-({c.XMax:G6},{c.YMax:G6}) leaves the metal");
        }

        Assert.Equal(px0, x0, 15);
        Assert.Equal(py0, y0, 15);
        Assert.Equal(px1, x1, 15);
        Assert.Equal(py1, y1, 15);
    }

    [Fact]
    public void T0_2_NoTwoCellsOverlap()
    {
        // A tensor grid makes this structural, which is exactly why it is worth asserting: a future
        // conformal/diagonal boundary cell (D8's noted extension) would be the first thing that could
        // break it, and this test is what would say so.
        var r = SurfaceMesher.Mesh(GaAsHero());
        var cells = r.Mesh.Cells;
        Assert.NotEmpty(cells);

        for (int i = 0; i < cells.Count; i++)
            for (int j = i + 1; j < cells.Count; j++)
            {
                var a = cells[i];
                var b = cells[j];
                if (a.LayerIndex != b.LayerIndex) continue;
                bool disjoint = a.XMax <= b.XMin || b.XMax <= a.XMin ||
                                a.YMax <= b.YMin || b.YMax <= a.YMin;
                Assert.True(disjoint, $"cells {i} and {j} overlap");
            }
    }

    [Fact]
    public void T0_3_EveryCellIsNonDegenerate()
    {
        foreach (var problem in new[] { Fr4Hero(), GaAsHero() })
        {
            var r = SurfaceMesher.Mesh(problem);
            Assert.NotEmpty(r.Mesh.Cells);
            foreach (var c in r.Mesh.Cells)
            {
                Assert.True(c.Width  > 0, "zero-width cell");
                Assert.True(c.Height > 0, "zero-height cell");
            }
        }
    }

    [Fact]
    public void T0_4_APolygonWithAHole_LeavesTheHoleUnmeshed()
    {
        // The hole is on-grid on all four sides, so the tiling stays exact and the area check is the
        // strongest available statement that the hole was genuinely excluded rather than merely
        // rendered differently.
        var outer = Rect(0, 0, 4e-3, 4e-3);
        var withHole = new PlanarPolygon(outer.Outer,
        [
            [new EmPoint(1e-3, 1e-3), new EmPoint(2e-3, 1e-3), new EmPoint(2e-3, 2e-3), new EmPoint(1e-3, 2e-3)],
        ]);
        var problem = Problem(GroundedSlab.Fr4Starter, 10e9, withHole);
        var r = SurfaceMesher.Mesh(problem);

        double meshArea = 0;
        foreach (var c in r.Mesh.Cells) meshArea += c.Area;
        Assert.Equal(withHole.Area(), meshArea, 12);

        foreach (var c in r.Mesh.Cells)
            Assert.False(c.CenterX > 1e-3 && c.CenterX < 2e-3 && c.CenterY > 1e-3 && c.CenterY < 2e-3,
                "a cell was placed inside the hole");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 1 — the rules are honoured, on geometry chosen to violate them
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T1_1_MaxCellHonoursLambdaGOverN_InTheLOCALDielectric_OnBothStarters()
    {
        const double f = 10e9;
        var fr4  = SurfaceMesher.Mesh(Problem(GroundedSlab.Fr4Starter,  f, Rect(0, 0, 20e-3, 2.9e-3)));
        var gaas = SurfaceMesher.Mesh(Problem(GroundedSlab.GaAsStarter, f, Rect(0, 0, 20e-3, 2.9e-3)));

        Assert.True(fr4.MaxCellEdgeM  <= fr4.MaxCellSizeM  * (1 + 1e-9));
        Assert.True(gaas.MaxCellEdgeM <= gaas.MaxCellSizeM * (1 + 1e-9));

        // The whole point of "local dielectric": the two caps differ by exactly √(εᵣ ratio), not by
        // nothing. Getting this wrong is invisible without a test that names the ratio.
        double expected = Math.Sqrt(GroundedSlab.GaAsStarter.Material.EpsR / GroundedSlab.Fr4Starter.Material.EpsR);
        Assert.Equal(expected, fr4.MaxCellSizeM / gaas.MaxCellSizeM, 9);
        Assert.True(expected > 1.7 && expected < 1.72, $"√εᵣ ratio {expected:G4} — the brief's own 1.7×");
    }

    [Fact]
    public void T1_2_AtLeastTheConfiguredCellsAcrossTheNARROWESTConductor_NotOnAverage()
    {
        // A 2.9 mm feed and a 100 µm stub in one drawing. A mesh sized off the big one resolves the
        // small one with a single cell.
        var problem = Problem(GroundedSlab.Fr4Starter, 10e9,
            Rect(0, 0, 20e-3, 2.9e-3),                 // the feed
            Rect(8e-3, 2.9e-3, 8.1e-3, 6e-3));         // a 100 µm stub off it

        var r = SurfaceMesher.Mesh(problem);

        Assert.True(r.NarrowestConductorWidthM <= 100e-6 * 1.001,
            $"narrowest measured {r.NarrowestConductorWidthM:G4} m — the 100 µm stub was missed");
        Assert.True(r.CellsAcrossNarrowestConductor >= PlanarMeshSettings.MinCellsAcrossConductor,
            $"only {r.CellsAcrossNarrowestConductor} cell(s) across the narrowest run");
    }

    [Fact]
    public void T1_3_EdgeCellsArePresentAndGraded_AtEveryMetalEdge_IncludingAHolesEdges()
    {
        var outer = Rect(0, 0, 4e-3, 4e-3);
        var withHole = new PlanarPolygon(outer.Outer,
        [
            [new EmPoint(1.5e-3, 1.5e-3), new EmPoint(2.5e-3, 1.5e-3),
             new EmPoint(2.5e-3, 2.5e-3), new EmPoint(1.5e-3, 2.5e-3)],
        ]);
        var problem = Problem(GroundedSlab.Fr4Starter, 10e9, withHole);

        var graded = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false));
        var flat   = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, EdgeMesh: false));

        // The edge mesh must actually refine: more cells, and a smallest cell far below the flat one.
        Assert.True(graded.CellCount > flat.CellCount);
        Assert.True(graded.MinCellEdgeM < 0.5 * flat.MinCellEdgeM,
            $"graded min cell {graded.MinCellEdgeM:G4} vs flat {flat.MinCellEdgeM:G4}");

        // Graded at the OUTER edges: the first few gridlines in from x = 0 grow geometrically.
        var gx = graded.Mesh.GridX;
        double c0 = PlanarMeshSettings.EdgeFractionOfReference * graded.EdgeReferenceLengthM;
        Assert.Equal(c0, gx[1] - gx[0], Math.Abs(c0) * 0.35);
        Assert.True(gx[2] - gx[1] > gx[1] - gx[0], "the second cell in from the edge is not larger");

        // Graded at the HOLE's edges too — the case a perimeter walk gets wrong. Find the gridline
        // sitting on the hole's own boundary and check the cell just inside the metal beside it.
        int at = IndexOfNearest(gx, 1.5e-3);
        Assert.True(Math.Abs(gx[at] - 1.5e-3) < 1e-9, "the hole's own edge is not a gridline");
        double justOutside = gx[at] - gx[at - 1];      // the last cell of metal before the hole
        Assert.True(justOutside <= 2.0 * c0,
            $"no edge refinement at the hole boundary — cell {justOutside:G4} m against c₀ {c0:G4} m");
    }

    private static int IndexOfNearest(IReadOnlyList<double> xs, double v)
    {
        int best = 0;
        for (int i = 1; i < xs.Count; i++)
            if (Math.Abs(xs[i] - v) < Math.Abs(xs[best] - v)) best = i;
        return best;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 2 — N is exact
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T2_1_UnknownCountEqualsAnIndependentlyRecountedSharedInternalEdgeCount(bool edgeMesh)
    {
        // The count is re-derived from the CELLS, never from the mesher's own basis list — this is
        // the number R17 refuses on and the number L8c is scheduled against, so it gets its own tier.
        var r = SurfaceMesher.Mesh(Fr4Hero(), new PlanarMeshSettings(Auto: false, EdgeMesh: edgeMesh));

        var present = new HashSet<(int L, int X, int Y)>();
        foreach (var c in r.Mesh.Cells) present.Add((c.LayerIndex, c.IX, c.IY));

        int shared = 0;
        foreach (var c in r.Mesh.Cells)
        {
            if (present.Contains((c.LayerIndex, c.IX + 1, c.IY))) shared++;
            if (present.Contains((c.LayerIndex, c.IX, c.IY + 1))) shared++;
        }

        Assert.Equal(shared, r.UnknownCount);
        Assert.Equal(shared, r.Mesh.Bases.Count);
        Assert.NotEqual(r.CellCount, r.UnknownCount);   // N is not the cell count — R-msh-6
    }

    [Fact]
    public void T2_2_TwoDisjointConductorsShareNoBasisFunction()
    {
        // A rooftop that bridged a gap would silently short two nets together.
        var problem = Problem(GroundedSlab.Fr4Starter, 10e9,
            Rect(0, 0, 4e-3, 1e-3),
            Rect(6e-3, 0, 10e-3, 1e-3));
        var r = SurfaceMesher.Mesh(problem);

        foreach (var b in r.Mesh.Bases)
        {
            var a = r.Mesh.Cells[b.CellA];
            var c = r.Mesh.Cells[b.CellB];
            Assert.True(a.XMax >= c.XMin - 1e-15 && c.XMax >= a.XMin - 1e-15,
                "a basis function bridges the gap between two disjoint conductors");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 3 — convergence and scaling
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T3_1_RefiningAnyControlMonotonicallyIncreasesN()
    {
        var problem = Fr4Hero();
        int prev = 0;
        foreach (int cpw in new[] { 10, 20, 30, 40 })
        {
            var r = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw));
            Assert.True(r.UnknownCount >= prev, $"N fell from {prev} to {r.UnknownCount} at {cpw} cells/λ");
            prev = r.UnknownCount;
        }

        // Turning the edge mesh ON is a refinement too.
        var off = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, EdgeMesh: false));
        var on  = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, EdgeMesh: true));
        Assert.True(on.UnknownCount > off.UnknownCount);

        // …and so is asking for more edge cells.
        var more = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, EdgeCells: 5));
        Assert.True(more.UnknownCount >= on.UnknownCount);
    }

    [Fact]
    public void T3_2_HalvingTheTargetCellSizeQuadruplesN_OnARectangle()
    {
        // A 2-D mesh that scales like N¹ has a bug that no visual inspection finds. Measured with the
        // edge mesh OFF so the comparison is against the bulk cell size alone.
        var problem = Problem(GroundedSlab.Fr4Starter, 10e9, Rect(0, 0, 20e-3, 20e-3));
        var coarse = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false));
        var fine   = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: false));

        double ratio = (double)fine.UnknownCount / coarse.UnknownCount;
        Assert.True(ratio > 3.5 && ratio < 4.5, $"N scaled by {ratio:G4}, not ~4");
    }

    [Fact]
    public void T3_3_TheMeshIsTranslationInvariant_BecauseTheGridIsAnchoredToTheARTWORK()
    {
        // Tier 3 asks for invariance under a whole-number-of-cells translation and "changes only near
        // the edges" under a sub-cell one. This mesher gives the STRONGER property for free: gridlines
        // come from the artwork's own boundary rather than from the world origin, so an arbitrary
        // translation reproduces the mesh exactly, shifted. Asserted at full precision, and worth
        // knowing because it is what makes a design's mesh independent of where it was drawn.
        double d = 3.7e-3;
        var a = SurfaceMesher.Mesh(Problem(GroundedSlab.Fr4Starter, 10e9, Rect(0, 0, 20e-3, 2.9e-3)));
        var b = SurfaceMesher.Mesh(Problem(GroundedSlab.Fr4Starter, 10e9, Rect(d, -d, 20e-3 + d, 2.9e-3 - d)));

        Assert.Equal(a.CellCount, b.CellCount);
        Assert.Equal(a.UnknownCount, b.UnknownCount);
        for (int i = 0; i < a.Mesh.Cells.Count; i++)
        {
            Assert.Equal(a.Mesh.Cells[i].Width,  b.Mesh.Cells[i].Width,  15);
            Assert.Equal(a.Mesh.Cells[i].Height, b.Mesh.Cells[i].Height, 15);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 4 — determinism
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T4_1_SameInputProducesBitIdenticalOutput_IncludingORDER()
    {
        var problem = Problem(GroundedSlab.Fr4Starter, 10e9,
            Rect(0, 0, 20e-3, 2.9e-3),
            Rect(8e-3, 2.9e-3, 8.1e-3, 6e-3));

        var a = SurfaceMesher.Mesh(problem);
        var b = SurfaceMesher.Mesh(problem);

        Assert.Equal(a.Mesh.Cells.Count, b.Mesh.Cells.Count);
        for (int i = 0; i < a.Mesh.Cells.Count; i++)
        {
            var p = a.Mesh.Cells[i];
            var q = b.Mesh.Cells[i];
            Assert.Equal(p.LayerIndex, q.LayerIndex);
            Assert.Equal(p.IX, q.IX);
            Assert.Equal(p.IY, q.IY);
            Assert.True(BitConverter.DoubleToInt64Bits(p.XMin) == BitConverter.DoubleToInt64Bits(q.XMin) &&
                        BitConverter.DoubleToInt64Bits(p.YMin) == BitConverter.DoubleToInt64Bits(q.YMin) &&
                        BitConverter.DoubleToInt64Bits(p.XMax) == BitConverter.DoubleToInt64Bits(q.XMax) &&
                        BitConverter.DoubleToInt64Bits(p.YMax) == BitConverter.DoubleToInt64Bits(q.YMax),
                $"cell {i} differs bit-for-bit between two runs");
        }

        Assert.Equal(a.Mesh.Bases.Count, b.Mesh.Bases.Count);
        for (int i = 0; i < a.Mesh.Bases.Count; i++) Assert.Equal(a.Mesh.Bases[i], b.Mesh.Bases[i]);
    }

    [Fact]
    public void T4_2_CellOrderIsLayerThenYThenX_Monotonically()
    {
        // R-msh-2's stated key, asserted rather than described.
        var r = SurfaceMesher.Mesh(Fr4Hero());
        for (int i = 1; i < r.Mesh.Cells.Count; i++)
        {
            var p = r.Mesh.Cells[i - 1];
            var q = r.Mesh.Cells[i];
            bool ordered = q.LayerIndex > p.LayerIndex
                        || (q.LayerIndex == p.LayerIndex && q.IY > p.IY)
                        || (q.LayerIndex == p.LayerIndex && q.IY == p.IY && q.IX > p.IX);
            Assert.True(ordered, $"cell {i} breaks the (layer, y, x) order");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 5 — R17
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T5_1_TheRefusalFiresAtTheCeiling_NamesTheCountAndARemedy()
    {
        // A large plate, meshed finely enough to blow the budget.
        var problem = Problem(GroundedSlab.Fr4Starter, 40e9, Rect(0, 0, 60e-3, 60e-3));
        var r = SurfaceMesher.Mesh(problem);

        Assert.Equal(PlanarBudgetVerdict.Refused, r.Verdict);
        Assert.False(r.CanSolve);
        Assert.NotNull(r.Refusal);
        Assert.Contains(SurfaceMesher.UnknownCeiling.ToString("N0"), r.Refusal);
        Assert.Contains("Cells per wavelength", r.Refusal);
        Assert.Contains("edge mesh", r.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T5_2_AGeometryUnderTheCeilingIsAccepted_AndTheWarnBandFiresBelowIt()
    {
        var ok = SurfaceMesher.Mesh(Fr4Hero());
        Assert.True(ok.UnknownCount < SurfaceMesher.UnknownCeiling);
        Assert.True(ok.CanSolve);
        Assert.Null(ok.Refusal);

        // Walk the frequency up until N crosses into the warn band, and confirm the three verdicts
        // partition the range in the stated order rather than jumping straight to a refusal.
        var seen = new List<(double F, int N, PlanarBudgetVerdict V)>();
        foreach (double f in new[] { 5e9, 10e9, 20e9, 30e9, 40e9, 60e9 })
            seen.Add((f, SurfaceMesher.Mesh(Fr4Hero(f)).UnknownCount, SurfaceMesher.Mesh(Fr4Hero(f)).Verdict));

        Assert.Contains(seen, e => e.V == PlanarBudgetVerdict.Ok);
        Assert.Contains(seen, e => e.V == PlanarBudgetVerdict.Warn);
        foreach (var e in seen)
        {
            if (e.N <= SurfaceMesher.UnknownCeiling * SurfaceMesher.WarnFraction) Assert.Equal(PlanarBudgetVerdict.Ok, e.V);
            else if (e.N <= SurfaceMesher.UnknownCeiling)                          Assert.Equal(PlanarBudgetVerdict.Warn, e.V);
            else                                                                   Assert.Equal(PlanarBudgetVerdict.Refused, e.V);
        }
    }

    [Fact]
    public void T5_3_AnAnalyticAlternativeIsNOTED_NeverRefused()
    {
        // R-msh-8a: name the thing, name the alternative — the R-mom-17 shape applied to a COST.
        //
        // UPDATED 2026-08-14 on the owner's instruction, not loosened. The note used to be framed by
        // this file as "X already has a validated analytic model, which is effectively free … this is
        // a note about cost, not a refusal", and the frame is now gone: the whole sentence comes from
        // the alternative's own Reason, which is written to say what FULL-WAVE ADDS. The reason is
        // that the only person who ever sees this note has deliberately opened an EM setup on this
        // part — telling them the cheap model exists reads as telling them they are wasting their
        // time. What survives unchanged is the half this test is named for: it is a NOTE, and the
        // mesh still solves.
        var problem = Fr4Hero() with
        {
            AnalyticAlternatives =
            [
                new PlanarAnalyticAlternative("MKLOPF1", "MicrostripKlopfModel",
                    "a Klopfenstein taper. What full-wave adds is radiation along the flare."),
            ],
        };
        var r = SurfaceMesher.Mesh(problem);

        Assert.True(r.CanSolve);
        Assert.Contains(r.Notes, n => n.StartsWith("MKLOPF1 — ", StringComparison.Ordinal)
                                   && n.Contains("What full-wave adds"));
    }

    [Fact]
    public void T5_3b_TheAnalyticAlternativeNote_DoesNotReadAsARECOMMENDATION()
    {
        // The owner's correction of 2026-08-14, asserted rather than left to wording discipline: the
        // mesher must not editorialise about the alternative on top of whatever the alternative says
        // about itself. A future edit that reinstates "already has a validated analytic model, which
        // is effectively free" fails here rather than reaching a user who is standing in the EM setup
        // precisely because they want the full-wave answer for this part.
        var problem = Fr4Hero() with
        {
            AnalyticAlternatives =
            [
                new PlanarAnalyticAlternative("MKLOPF1", "MicrostripKlopfModel", "a Klopfenstein taper."),
            ],
        };
        var note = Assert.Single(SurfaceMesher.Mesh(problem).Notes, n => n.Contains("MKLOPF1"));

        Assert.Equal("MKLOPF1 — a Klopfenstein taper.", note);
        foreach (string steer in new[] { "effectively free", "at no cost", "instead", "not needed",
                                         "already has" })
            Assert.DoesNotContain(steer, note, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 7 — §10.7's own worked example, which is a CLOSED-FORM PREDICTION
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T7_1_TheDesignNotesOwnHero_LandsAtAFewHundredUnknowns()
    {
        // §10.7: "50 Ω microstrip on 1.6 mm FR-4 is W ≈ 2.9 mm; a 20 mm line at 10 GHz has λ_g ≈ 16.5
        // mm, so λ_g/20 ≈ 0.8 mm → ~24 cells long × ~6 across with edge refinement → N of a few
        // hundred." This is the only end-to-end sanity check available before a solver exists, and it
        // catches the whole class of errors — wrong λ_g medium, wrong units, cells confused with
        // unknowns — that produce a mesh which is internally consistent and completely the wrong size.
        //
        // NOTE ON λ_g: the design note's 16.5 mm is λ₀/√ε_eff (ε_eff ≈ 3.3). This mesher uses
        // λ₀/√εᵣ = 14.3 mm — the SHORTEST wavelength any part of the structure can see, which is the
        // conservative direction and the only one available before a solve. It makes the mesh ~15%
        // finer than the note's arithmetic, which is why the numbers below are close to rather than
        // equal to "24 × 6".
        var r = SurfaceMesher.Mesh(Fr4Hero());

        double expectedLambda = EmConstants.C0 / (10e9 * Math.Sqrt(GroundedSlab.Fr4Starter.Material.EpsR));
        Assert.Equal(expectedLambda, r.GuidedWavelengthM, 15);
        Assert.Equal(expectedLambda / 20.0, r.MaxCellSizeM, 15);
        Assert.True(Math.Abs(r.GuidedWavelengthM - 14.29e-3) < 0.05e-3,
            $"λ_g = {r.GuidedWavelengthM * 1e3:G6} mm — the note's 16.5 mm is λ₀/√ε_eff, this is λ₀/√εᵣ");
        Assert.True(r.UnknownCount is > 100 and < 1000,
            $"N = {r.UnknownCount} — §10.7 predicts 'a few hundred'");

        WriteTier7("FR-4 §10.7 hero (2.9 × 20 mm, 10 GHz)", r);
    }

    [Fact]
    public void T7_2_TheGaAsCounterpart_IsReportedToo()
    {
        var r = SurfaceMesher.Mesh(GaAsHero());
        Assert.True(r.CanSolve);
        WriteTier7("GaAs MMIC line (72 µm × 2 mm, 10 GHz)", r);
    }

    [Fact]
    public void T7_3_TheEdgeReferenceLengthComparison_IsMEASURED_AndReported()
    {
        // R-msh-5: kernel A recorded a measurement rather than a preference; so does this. What is
        // available at L8b is N and the mesh's own behaviour — the CONVERGENCE half of R-mom-8's
        // measurement needs a solver and belongs to L8c, which is stated in the output rather than
        // faked.
        foreach (var (name, problem) in new[]
                 {
                     ("FR-4 hero", Fr4Hero()),
                     ("GaAs hero", GaAsHero()),
                 })
        {
            var width = SurfaceMesher.Mesh(problem, null, PlanarEdgeReference.ConductorWidth);
            var cell  = SurfaceMesher.Mesh(problem, null, PlanarEdgeReference.CellSize);

            Console.WriteLine(
                $"[L8b R-msh-5] {name}: width-reference c₀ = {width.EdgeReferenceLengthM * 0.03:G4} m, " +
                $"N = {width.UnknownCount}, min cell = {width.MinCellEdgeM:G4} m | " +
                $"cell-size-reference c₀ = {cell.EdgeReferenceLengthM * 0.03:G4} m, " +
                $"N = {cell.UnknownCount}, min cell = {cell.MinCellEdgeM:G4} m");

            Assert.True(width.UnknownCount > 0 && cell.UnknownCount > 0);
        }
    }

    private static void WriteTier7(string label, PlanarMeshReport r) =>
        Console.WriteLine(
            $"[L8b Tier 7] {label}: cells = {r.CellCount}, N = {r.UnknownCount}, " +
            $"λ_g = {r.GuidedWavelengthM * 1e3:G4} mm, max cell = {r.MaxCellEdgeM * 1e6:G4} µm, " +
            $"min cell = {r.MinCellEdgeM * 1e6:G4} µm, across narrowest = {r.CellsAcrossNarrowestConductor}, " +
            $"narrowest conductor = {r.NarrowestConductorWidthM * 1e6:G4} µm, verdict = {r.Verdict}");
}
