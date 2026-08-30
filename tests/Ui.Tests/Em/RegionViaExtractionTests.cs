// MIM-1 — drawn REGIONS on a via-bound drawing layer become PlanarVia footprints.
//
// The gap this closes, in one sentence: a MIM capacitor's plate connection is a rectangle nearly as
// large as the plate itself, and until MIM-1 the extractor recognised a via-bound layer ONLY inside
// its ViaShape branch — so a rectangle drawn there missed `binding` (BuildStack skips every Via
// entry, so a via's drawing layer is never in that map), fell into `ignoredOther`, and was reported
// by a sentence about Paths and unbound layers. The engine end needed nothing: PlanarVia has always
// carried an arbitrary polygon list and the mesher makes one vertical basis per covered cell.
//
// The fixtures are the L9 phase gate's own MMIC starter and its own layer keys, deliberately: the
// point-via path this must not disturb is gated there, and re-stating the technology here would let
// the two drift.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class RegionViaExtractionTests(ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey Metal1      = new(1, 0);
    private static readonly LayerKey Metal2      = new(2, 0);
    private static readonly LayerKey Post        = new(3, 0);   // -> "Metal1-Metal2 Post"
    private static readonly LayerKey BacksideVia = new(8, 0);   // -> "Backside Via"

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    /// <summary>A point via whose EQUAL-AREA SQUARE has the given side — the extractor replaces a
    /// round barrel by side = 0.886 × drill, so a fixture that wants to control its own gridlines
    /// has to state the square and invert. Copied from <c>L9PhaseGateTests</c> on purpose: the two
    /// files must agree about what a point via's footprint is, and that is the arithmetic.</summary>
    private static ViaShape PointVia(LayerKey layer, double cx, double cy, double squareSideUm)
    {
        double drill = squareSideUm / (Math.Sqrt(Math.PI) / 2.0);
        return new ViaShape
        {
            Layer = layer, X = Um(cx), Y = Um(cy),
            DrillSize = Um(drill), PadSize = Um(1.3 * drill),
        };
    }

    private static LabelShape PortLabel(LayerKey layer, double xUm, double yUm, string name) =>
        new() { Layer = layer, X = Um(xUm), Y = Um(yUm), Text = name, Height = Um(20), IsPort = true };

    /// <summary>
    /// The airbridge geometry, with the two posts left OFF so each test says for itself what via
    /// artwork it wants. Same edge coordinates as the L9 gate's fixture, for the same reason: every
    /// edge is ≥ 20 µm from every other, so the mesh pitch is not set by a sliver run.
    /// </summary>
    private static LayoutView TwoLevelMetal()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(Metal1, 0,   0, 120, 100));
        view.Shapes.Add(Rect(Metal1, 180, 0, 300, 100));
        view.Shapes.Add(Rect(Metal2, 20,  0, 280, 100));
        view.Shapes.Add(PortLabel(Metal1, 0,   50, "P1"));
        view.Shapes.Add(PortLabel(Metal1, 300, 50, "P2"));
        return view;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 1 — a region on a via-bound layer becomes a PlanarVia
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ARectangleOnAViaBoundLayer_BecomesAViaFootprintAtTheOutlineItWasDrawn()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(Rect(Post, 40, 30, 80, 70));

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.Equal(0, via.LowerLayerIndex);       // Metal1
        Assert.Equal(1, via.UpperLayerIndex);       // Metal2
        Assert.False(via.ToGround);

        // NOT squared: the drawn outline IS the footprint. Stated as the four corners in metres,
        // because "the outline you drew" is the entire claim.
        var poly = Assert.Single(via.Polygons);
        var xs = poly.Outer.Select(p => p.X).ToArray();
        var ys = poly.Outer.Select(p => p.Y).ToArray();
        Assert.Equal(40e-6,  xs.Min(), 12);
        Assert.Equal(80e-6,  xs.Max(), 12);
        Assert.Equal(30e-6,  ys.Min(), 12);
        Assert.Equal(70e-6,  ys.Max(), 12);
        Assert.Equal(40e-6 * 40e-6, poly.Area(), 15);

        Assert.Contains(r.Notes, n => n.Contains("drawn region(s)", StringComparison.Ordinal));
        // …and the equal-area sentence must NOT fire, because nothing here was a barrel.
        Assert.DoesNotContain(r.Notes, n => n.Contains("EQUAL-AREA", StringComparison.Ordinal));
    }

    /// <summary>A plate connection is often drawn as a polygon, sometimes with a relief hole in it.
    /// The conductor path's own conversion is reused, so outer ring plus holes comes for free — this
    /// asserts it actually does rather than that it ought to.</summary>
    [Fact]
    public void APolygonWithAHole_KeepsItsHoleInTheViaFootprint()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(new PolygonShape
        {
            Layer = Post,
            Xy    = [Um(40), Um(30), Um(80), Um(30), Um(80), Um(70), Um(40), Um(70)],
            Holes = [[Um(55), Um(45), Um(65), Um(45), Um(65), Um(55), Um(55), Um(55)]],
        });

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);

        var poly = Assert.Single(Assert.Single(r.Problem!.ViaList).Polygons);
        Assert.Single(poly.HoleRings);
        // 40×40 outer less a 10×10 relief.
        Assert.Equal((40e-6 * 40e-6) - (10e-6 * 10e-6), poly.Area(), 15);
    }

    /// <summary>
    /// <b>Multiple regions on one via layer are multiple FOOTPRINT POLYGONS of one via, not multiple
    /// vias.</b> The span, the conductivity and the ground rule all come from the stackup ENTRY, so
    /// every region on it resolves to the identical terminals; and the mesher stops at the first
    /// polygon that covers a cell, so grouping is also what stops two OVERLAPPING drawn rectangles
    /// from contributing two vertical bases to the cell they share.
    /// </summary>
    [Fact]
    public void TwoRegionsOnOneViaLayer_AreTwoFootprintsOfOneVia()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(Rect(Post, 40,  30, 80,  70));
        view.Shapes.Add(Rect(Post, 220, 30, 260, 70));

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.Equal(2, via.Polygons.Count);
        Assert.Contains(r.Notes, n => n.Contains("2 drawn region(s) became the footprints of 1 via", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The reason grouping is a correctness decision and not tidiness.</b> The mesher scans every
    /// grid cell against a via's polygon list and stops at the FIRST polygon that covers it, so two
    /// OVERLAPPING footprints inside one <c>PlanarVia</c> give a shared cell one vertical basis. As
    /// separate <c>PlanarVia</c>s they would give it one each — silently doubling the vertical
    /// current in the overlap. A plate connection drawn as two overlapping rectangles is an ordinary
    /// thing to draw, so this is not a corner case.
    ///
    /// <para>The claim is stated as a counter that is INDEPENDENT of the mesh pitch: one vertical
    /// basis per distinct cell, i.e. no cell index appears twice.</para>
    /// </summary>
    [Fact]
    public void TwoOverlappingRegions_GiveTheirSharedCellsOneVerticalBasisEach()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(Rect(Post, 40, 30, 80, 70));
        view.Shapes.Add(Rect(Post, 60, 30, 100, 70));      // overlaps the first over 60…80 µm

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(2, Assert.Single(r.Problem!.ViaList).Polygons.Count);

        var mesh = SurfaceMesher.Mesh(r.Problem!);
        Assert.True(mesh.CanSolve, mesh.Refusal);

        var cells = mesh.Mesh.Bases.Where(b => b.Direction == PlanarBasisDirection.Z)
                                   .Select(b => b.CellB).ToList();
        Assert.NotEmpty(cells);
        Assert.Equal(cells.Count, cells.Distinct().Count());

        // …and the footprint really is the UNION, not the sum: 60 µm × 40 µm, not 2 × 40 × 40.
        Assert.Equal(60e-6 * 40e-6, VerticalArea(mesh), 15);
    }

    /// <summary>A region via to the ground plane is the SHUNT MIM's bottom-plate connection, and it
    /// takes the same ground-attachment path a drawn point backside via takes.</summary>
    [Fact]
    public void ARegionOnTheBacksideViaLayer_IsAGroundAttachment()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(Rect(BacksideVia, 40, 30, 80, 70));

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.True(via.ToGround);
        Assert.Equal(PlanarVia.GroundTerminal, via.LowerLayerIndex);
        Assert.Equal(0, via.UpperLayerIndex);
        Assert.Contains(r.Notes, n => n.Contains("BACKSIDE", StringComparison.Ordinal));

        var mesh = SurfaceMesher.Mesh(r.Problem!);
        Assert.True(mesh.CanSolve, mesh.Refusal);
        Assert.True(mesh.ViaUnknownCount > 0, "the region ground via produced no vertical unknown");
    }

    /// <summary>A region via participates in the SAME accounting a point via does — this is the
    /// wrongGround leg, which is the one whose disappearance would be silently wrong.</summary>
    [Fact]
    public void ARegionViaToSomeOtherConductor_IsStillDroppedByName()
    {
        var tech = StarterTechnologies.MmicGaAs();
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Via && l.Name == "Backside Via")
                l.SpanToLayer = "Not A Real Layer";

        var view = TwoLevelMetal();
        view.Shapes.Add(Rect(BacksideVia, 40, 30, 80, 70));

        var r = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Problem!.ViaList);
        Assert.Contains(r.Notes, n => n.Contains("Not A Real Layer", StringComparison.Ordinal)
                                   && n.Contains("ground plane", StringComparison.Ordinal));
    }

    /// <summary>…and the noSpan leg, counted in SHAPES rather than in entries, because a shape is
    /// what the user drew and can go and look at.</summary>
    [Fact]
    public void RegionViasOnAnEntryWithNoSpan_AreCountedAsShapes()
    {
        var tech = StarterTechnologies.MmicGaAs();
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Via && l.Name == "Metal1-Metal2 Post")
                { l.SpanFromLayer = null; l.SpanToLayer = null; }

        var view = TwoLevelMetal();
        view.Shapes.Add(Rect(Post, 40,  30, 80,  70));
        view.Shapes.Add(Rect(Post, 220, 30, 260, 70));

        var r = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Problem!.ViaList);
        Assert.Contains(r.Notes, n => n.Contains("2 via shape(s) were ignored because their stackup",
                                                 StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 2 — the silence becomes a sentence
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Nothing on a via-bound layer may fall into <c>ignoredOther</c> any more.</b> That counter's
    /// note tells the user to bind the layer to a stackup entry — which is exactly the wrong advice
    /// for artwork on a layer that IS bound, and was the whole shape of the MIM-1 defect. A Path is
    /// still ignored (it encloses no area) but now says so in its own words.
    /// </summary>
    [Fact]
    public void APathOnAViaBoundLayer_IsNamedRatherThanFoldedIntoTheUnboundSentence()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(new PathShape { Layer = Post, Xy = [Um(40), Um(50), Um(80), Um(50)], Width = Um(10) });

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Problem!.ViaList);

        Assert.Contains(r.Notes, n => n.Contains("Path shape(s) on a via-bound drawing layer",
                                                 StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("not bound to a stackup conductor or via entry",
                                                      StringComparison.Ordinal));
    }

    /// <summary>The other half of the same claim: artwork that IS bound and IS a region still reaches
    /// the via path, so the unbound sentence stays silent there too. A shape on a genuinely unbound
    /// layer must still produce it — otherwise this test would pass by the note being deleted.</summary>
    [Fact]
    public void AnUnboundLayerStillProducesTheUnboundSentence_AndAViaLayerDoesNot()
    {
        var withRegion = TwoLevelMetal();
        withRegion.Shapes.Add(Rect(Post, 40, 30, 80, 70));
        var bound = PlanarExtractor.Extract(withRegion.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(bound.Ok, bound.Refusal);
        Assert.DoesNotContain(bound.Notes, n => n.Contains("not bound to a stackup conductor or via entry",
                                                          StringComparison.Ordinal));

        var withStray = TwoLevelMetal();
        withStray.Shapes.Add(Rect(new LayerKey(99, 0), 40, 30, 80, 70));
        var stray = PlanarExtractor.Extract(withStray.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(stray.Ok, stray.Refusal);
        Assert.Contains(stray.Notes, n => n.Contains("not bound to a stackup conductor or via entry",
                                                     StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 3 — point vias unchanged, provably
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The point-via path is bit-identical.</b> The equal-area square is a documented modelling
    /// decision and existing runs must not move, so this asserts the extracted <c>PlanarVia</c> list
    /// of the L9 gate's own airbridge artwork exactly — terminals, conductivity, and every footprint
    /// coordinate as a <c>double</c> bit pattern, not to a tolerance.
    /// </summary>
    [Fact]
    public void APointVia_IsByteIdenticalAfterTheRegionPathLanded()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(PointVia(Post, 60,  50, 40));
        view.Shapes.Add(PointVia(Post, 240, 50, 40));

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(2, r.Problem!.ViaList.Count);

        // The square the extractor must produce, restated here from the documented rule rather than
        // read back from the object under test: side = 0.886 × drill, centred on the via.
        foreach (var (via, cxUm) in r.Problem!.ViaList.Zip(new[] { 60.0, 240.0 }))
        {
            Assert.Equal(0, via.LowerLayerIndex);
            Assert.Equal(1, via.UpperLayerIndex);

            double drillDbu = Math.Round(40.0 / (Math.Sqrt(Math.PI) / 2.0) * Dbu);
            double d = drillDbu * (1.0 / (Dbu * 1e6));
            double half = 0.5 * d * Math.Sqrt(Math.PI) / 2.0;
            double cx = Um(cxUm) * (1.0 / (Dbu * 1e6)), cy = Um(50) * (1.0 / (Dbu * 1e6));

            var poly = Assert.Single(via.Polygons);
            var expected = new[]
            {
                (cx - half, cy - half), (cx + half, cy - half),
                (cx + half, cy + half), (cx - half, cy + half),
            };
            Assert.Equal(expected.Length, poly.Outer.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                // BitConverter, not a tolerance — "unchanged" is the claim.
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Item1),
                             BitConverter.DoubleToInt64Bits(poly.Outer[i].X));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Item2),
                             BitConverter.DoubleToInt64Bits(poly.Outer[i].Y));
            }
            Assert.Empty(poly.HoleRings);
        }

        Assert.Contains(r.Notes, n => n.Contains("2 via(s) were extracted", StringComparison.Ordinal)
                                   && n.Contains("EQUAL-AREA", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("drawn region(s)", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE GATE — structural equivalence between region and point footprints
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The brief's structural gate — a region via covering the cells of an N×N array of touching
    /// point vias must yield the same vertical basis functions — in the two halves it splits into
    /// once the shared tensor grid and the DBU grid are both taken seriously.</b>
    ///
    /// <para><b>The invariant compared is the AREA the vertical bases cover, not the basis list</b>,
    /// and that is forced by L9c's own mesher finding rather than chosen for convenience: a via
    /// footprint must contribute HARD GRIDLINES or the via vanishes silently. So N×N touching
    /// footprints put N−1 interior gridlines per axis into the shared grid that one big footprint
    /// does not — and those lines SUBDIVIDE the covered cells without moving the covered boundary.
    /// A basis-list comparison would therefore be asserting something false; the covered area is the
    /// grid-independent statement of the same claim, and it is still a cell counter (one basis per
    /// covered cell, summed over the cells' own areas) rather than an S-parameter.</para>
    ///
    /// <para><b>Half A — region vs region, EXACT.</b> N×N touching drawn squares against one drawn
    /// rectangle over their union. Both are DBU-exact artwork, so the boundaries coincide exactly
    /// and the covered area must match to the last bit; the single rectangle must also need FEWER
    /// unknowns, which is the subdivision claim above stated as a counter.</para>
    ///
    /// <para><b>Half B — point vs region, to the equal-area square's own rounding.</b> A point via's
    /// square is side = 0.886 × drill, and a drill is an integer number of DBU, so the square's edges
    /// land at sub-DBU coordinates that no drawn rectangle can be placed on. Touching point vias
    /// therefore do not quite touch — the fixture below leaves ≈ 0.5 nm between them, which is real
    /// and is reported. That is a fact about the equal-area substitution, not about MIM-1, and the
    /// honest gate is a relative tolerance sized to it rather than an equality that would only pass
    /// by accident.</para>
    /// </summary>
    [Fact]
    public void ARegionVia_MeshesTheSameVerticalCurrentAsTheArrayOfPointViasItCovers()
    {
        var tech = StarterTechnologies.MmicGaAs();
        const double side = 20;                        // µm, one square of the array
        const double x0 = 40, y0 = 30;                 // its lower-left corner
        const int n = 2;

        // ── The point-via array ──────────────────────────────────────────────────────────────
        var points = TwoLevelMetal();
        for (int iy = 0; iy < n; iy++)
            for (int ix = 0; ix < n; ix++)
                points.Shapes.Add(PointVia(Post, x0 + (ix + 0.5) * side, y0 + (iy + 0.5) * side, side));

        var pr = PlanarExtractor.Extract(points.Shapes, tech, Dbu, 30e9);
        Assert.True(pr.Ok, pr.Refusal);
        Assert.Equal(n * n, pr.Problem!.ViaList.Count);
        var pMesh = SurfaceMesher.Mesh(pr.Problem!);
        Assert.True(pMesh.CanSolve, pMesh.Refusal);

        // ── Half A: the array as DRAWN squares, and as ONE drawn rectangle ────────────────────
        var drawnArray = TwoLevelMetal();
        for (int iy = 0; iy < n; iy++)
            for (int ix = 0; ix < n; ix++)
                drawnArray.Shapes.Add(Rect(Post, x0 + ix * side,       y0 + iy * side,
                                                 x0 + (ix + 1) * side, y0 + (iy + 1) * side));

        var ar = PlanarExtractor.Extract(drawnArray.Shapes, tech, Dbu, 30e9);
        Assert.True(ar.Ok, ar.Refusal);
        Assert.Single(ar.Problem!.ViaList);                       // one entry, n² footprints
        Assert.Equal(n * n, ar.Problem!.ViaList[0].Polygons.Count);
        var aMesh = SurfaceMesher.Mesh(ar.Problem!);
        Assert.True(aMesh.CanSolve, aMesh.Refusal);

        var region = TwoLevelMetal();
        region.Shapes.Add(Rect(Post, x0, y0, x0 + n * side, y0 + n * side));

        var rr = PlanarExtractor.Extract(region.Shapes, tech, Dbu, 30e9);
        Assert.True(rr.Ok, rr.Refusal);
        Assert.Single(Assert.Single(rr.Problem!.ViaList).Polygons);
        var rMesh = SurfaceMesher.Mesh(rr.Problem!);
        Assert.True(rMesh.CanSolve, rMesh.Refusal);

        double aArea = VerticalArea(aMesh), rArea = VerticalArea(rMesh);
        Assert.True(aArea > 0, "the drawn array meshed no vertical current at all");
        Assert.Equal(BitConverter.DoubleToInt64Bits(aArea), BitConverter.DoubleToInt64Bits(rArea));
        Assert.Equal(n * side * 1e-6 * (n * side * 1e-6), rArea, 15);
        Assert.True(rMesh.UnknownCount <= aMesh.UnknownCount,
            $"one footprint needed {rMesh.UnknownCount} unknowns against the array's {aMesh.UnknownCount} — " +
            "the array's interior gridlines are supposed to SUBDIVIDE, never coarsen");

        // …and both mesh the same GROUND, not merely the same total: every cell either path gives a
        // vertical basis to has its centre inside the union rectangle, at either resolution.
        foreach (var centres in new[] { ViaCellCentres(aMesh), ViaCellCentres(rMesh) })
        {
            Assert.NotEmpty(centres);
            Assert.All(centres, c =>
            {
                Assert.InRange(c.X, x0 * 1e-6, (x0 + n * side) * 1e-6);
                Assert.InRange(c.Y, y0 * 1e-6, (y0 + n * side) * 1e-6);
            });
        }

        // ── Half B: the point-via array, to the equal-area square's own DBU rounding ──────────
        double pArea = VerticalArea(pMesh);
        Assert.True(pArea > 0, "the point-via array meshed no vertical current at all");
        // The whole discrepancy is PREDICTED, not bounded: a point via's square has side
        // 0.886 × drill and a drill is an integer number of DBU, so the square that actually gets
        // meshed is s' rather than the nominal s, and the array covers n²s' ² against the region's
        // (ns)². Nothing else may differ — if the two disagree by anything the rounding does not
        // account for, that is a real defect in one of the two paths.
        double drillDbu  = Math.Round(side / (Math.Sqrt(Math.PI) / 2.0) * Dbu);
        double sPrime    = drillDbu * (1.0 / (Dbu * 1e6)) * Math.Sqrt(Math.PI) / 2.0;
        double predicted = 1.0 - (sPrime / (side * 1e-6)) * (sPrime / (side * 1e-6));
        double measured  = (rArea - pArea) / rArea;
        Assert.Equal(predicted, measured, 12);
        double gap = side * 1e-6 - sPrime;

        output.WriteLine($"MIM-1 structural gate — {n}×{n} array of {side} µm squares:");
        output.WriteLine($"  point vias  : N = {pMesh.UnknownCount} ({pMesh.ViaUnknownCount} vertical), " +
                         $"footprint {pArea * 1e12:G8} µm²");
        output.WriteLine($"  drawn array : N = {aMesh.UnknownCount} ({aMesh.ViaUnknownCount} vertical), " +
                         $"footprint {aArea * 1e12:G8} µm²");
        output.WriteLine($"  one region  : N = {rMesh.UnknownCount} ({rMesh.ViaUnknownCount} vertical), " +
                         $"footprint {rArea * 1e12:G8} µm²");
        output.WriteLine($"  the equal-area square's DBU rounding leaves {gap * 1e9:G3} nm between " +
                         $"nominally touching point vias — predicted area shortfall {predicted:E4}, " +
                         $"measured {measured:E4}");
    }

    /// <summary>The plan-view centres of the cells that carry a vertical basis — the "meshed via
    /// cells" the gate compares, before they are summed into an area.</summary>
    private static List<(double X, double Y)> ViaCellCentres(PlanarMeshReport m) =>
        [.. m.Mesh.Bases.Where(b => b.Direction == PlanarBasisDirection.Z)
                        .Select(b => m.Mesh.Cells[b.CellB])
                        .Select(c => (0.5 * (c.XMin + c.XMax), 0.5 * (c.YMin + c.YMax)))];

    /// <summary>The plan-view area the vertical bases cover — the grid-independent invariant, since
    /// a finer grid over the same footprint subdivides its cells rather than moving its boundary.
    /// One basis per covered cell, so summing the cells' own areas is summing the footprint.</summary>
    private static double VerticalArea(PlanarMeshReport m) =>
        m.Mesh.Bases.Where(b => b.Direction == PlanarBasisDirection.Z)
                    .Sum(b => m.Mesh.Cells[b.CellB].Area);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 4 — the paths this brief ASSUMED, verified rather than assumed
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>"Which drawing layer is the capacitor via on" is answered by the Via entry's own binding,
    /// and the technology editor already asks it.</b> A Via row shows the drawing-layer picker (and a
    /// Conductor row does not show it in the same place — a conductor binds through the layer table),
    /// and a Via row states its span.
    /// </summary>
    [Fact]
    public void AViaStackupRow_ShowsTheDrawingLayerPickerAndItsSpan()
    {
        string path = Path.Combine(Path.GetTempPath(), "crf-mim1-" + Guid.NewGuid().ToString("N")[..8] + ".ctech");
        var vm = new TechEditorViewModel(path, StarterTechnologies.MmicGaAs());

        var viaRow  = vm.StackupLayers.First(r => r.IsVia && r.StagedName == "Metal1-Metal2 Post");
        var condRow = vm.StackupLayers.First(r => r.Kind == StackupKind.Conductor);

        Assert.True(viaRow.ShowsDrawingLayerPicker);
        Assert.False(condRow.ShowsDrawingLayerPicker);
        Assert.Equal("Metal1", viaRow.SelectedSpanFrom);
        Assert.Equal("Metal2", viaRow.SelectedSpanTo);

        // …and the picker is not empty: the layer the extractor keys on is one of the choices, and
        // the entry is already bound to it on the shipped starter.
        Assert.Contains(viaRow.DrawingLayerOptions, o => o.Key == Post);
        Assert.Contains(Post, vm.Working.Stackup.Layers
            .First(l => l.Kind == StackupKind.Via && l.Name == "Metal1-Metal2 Post").DrawingLayers);
    }

    /// <summary>
    /// <b>The via count the run reports includes region vias</b>, which is the observable a user
    /// actually has. There is no via counter on <c>EmDiagnostics</c> — its family is the run
    /// service's REFUSALS — so the count lives where every other extraction quantity does: the run's
    /// notes, which <c>EmRunService</c> concatenates from the extractor and the mesher. Both must
    /// count a region via.
    /// </summary>
    [Fact]
    public void TheRunsOwnViaCount_IncludesRegionVias()
    {
        var view = TwoLevelMetal();
        view.Shapes.Add(PointVia(Post, 60, 50, 40));           // one point via…
        view.Shapes.Add(Rect(BacksideVia, 220, 30, 260, 70));  // …and one drawn region, to ground

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(2, r.Problem!.ViaList.Count);

        var mesh = SurfaceMesher.Mesh(r.Problem!);
        Assert.True(mesh.CanSolve, mesh.Refusal);
        Assert.Contains(mesh.Notes, n => n.Contains("2 via(s) resolve onto", StringComparison.Ordinal));

        // The level summary the extractor writes counts both kinds too — that sentence is the one a
        // user reads to find out whether the thing they drew is in the answer.
        Assert.Contains(r.Notes, n => n.Contains("2 via(s) carry z-directed current", StringComparison.Ordinal));
    }
}
