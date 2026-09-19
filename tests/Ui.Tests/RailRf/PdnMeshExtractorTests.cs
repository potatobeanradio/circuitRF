// ================================================================
//  PdnMeshExtractorTests.cs — brief-railrf-3-mesh-extractor.md §7
//
//  The netlist contract, and the accurate DC extractor. Copper in, ElaboratedNetlist out.
//
//  ── THE HEADLINE GATE IS EXTERNAL ARITHMETIC, WHICH IS THE POINT ──────────────────────────────
//
//  A straight trace of known width, thickness and length has R = L / (σ·W·T) exactly, and a stepped
//  trace is the sum over its sections. That closed form is not ours and it is not circuitRF's — it
//  is the definition of resistivity — so a mesh that reproduces it is not a model agreeing with
//  itself, which is what the PRD's validation rule asks for.
//
//  The rows come from railrf.md §2.8's own table so the numbers are checkable by hand:
//
//      50 mm of 0.3 mm inner trace, 0.5 oz    ~165 mΩ   (167 squares × 0.99 mΩ/sq)
//      30 mm of 0.5 mm outer trace, 1 oz      ~29 mΩ
//      10 mm of 1 mm-wide 1 oz trace          ~5 mΩ
//      one 0.3 mm plated via, 1.6 mm board    ~1.2 mΩ
//
//  Per the standing rule on minimal tests, the InlineData is trimmed to the rows that STRADDLE the
//  behaviour — a thin inner trace and a wide outer one — and the arithmetic-only rows are folded
//  into one sheet-resistance test.
//
//  ── THE TESTS SOLVE; THE EXTRACTOR NEVER DOES ─────────────────────────────────────────────────
//
//  Brief 3's scope is explicit: "this brief produces a netlist and never calls anything that
//  factorises it". So the little conjugate-gradient solver at the foot of this file is the TEST's,
//  reading resistor values straight off the ElaboratedNetlist — which also means the gate measures
//  what a solver would see rather than what the extractor believes it built.
//
//  One test per CLAIM the brief makes, not one per measured rung.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Clipper2Lib;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnMeshExtractorTests
{
    // ── the board ──────────────────────────────────────────────────────────────────────────────

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey Mid = new(3, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private const double CopperSigma = 5.8e7;          // S/m at 20 °C
    private const double CopperRho = 1.0 / CopperSigma;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    private static Technology Board(double topUm, double botUm, double coreMm)
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(topUm), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(coreMm), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(botUm), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>A rail fed at <c>BT1.1</c> and observed at <c>U1.VDD</c>, with no refinement — the
    /// closed-form gates want a uniform mesh so the number under test is the copper and not the
    /// grading.</summary>
    private static PdnExtractionRequest Request(
        Technology tech, IReadOnlyList<LayoutShape> shapes,
        (long X, long Y) source, (long X, long Y) load,
        int cellsAcross = 3, int refine = 1, double? cellSize = null)
    {
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" },
            DcCurrentA = 0.5,
        });

        return new PdnExtractionRequest
        {
            Rail = rail,
            Shapes = shapes,
            Technology = tech,
            DbuPerMicron = DbuPerMicron,
            Pads =
            [
                new PdnPad("BT1", "1", "VDD", source.X, source.Y),
                new PdnPad("U1", "VDD", "VDD", load.X, load.Y),
            ],
            Mesh = new PdnMeshSettings
            {
                CellsAcrossMinimumFeature = cellsAcross,
                PortRefinementRatio = refine,
                CellSizeMetres = cellSize,
            },
        };
    }

    // ── R-rail18-1: the pooled measurement, which read zero on every multilayer board ───────────

    private static Paths64 RectPaths(long x1, long y1, long x2, long y2) =>
        [[new Point64(x1, y1), new Point64(x2, y1), new Point64(x2, y2), new Point64(x1, y2)]];

    private static PdnRegion Island(params (LayerKey Layer, Paths64 Paths)[] copper)
    {
        var b = Bbox.Empty;
        foreach (var (_, paths) in copper) b = b.Union(DrcRegionBounds(paths));
        return new PdnRegion(0, copper, b, 0);
    }

    private static Bbox DrcRegionBounds(Paths64 paths)
    {
        var b = Bbox.Empty;
        foreach (var path in paths)
            foreach (var pt in path) b = b.Union(new Bbox(pt.X, pt.Y, pt.X, pt.Y));
        return b;
    }

    /// <summary>
    /// <b>The whole defect, in two lines.</b> One square measures its own width; the SAME square
    /// listed twice measures the same width.
    /// </summary>
    /// <remarks>
    /// It read 0 mm before R-rail18-1, and the arithmetic is why: the opening's area is a UNION's
    /// and the total it is compared against is a SUM of per-path areas, so any overlap between two
    /// pooled paths makes the comparison unsatisfiable at every width and the bisection returns its
    /// 1 DBU floor. <b>A rail on more than one layer crosses itself at every via</b>, so the
    /// duplicate here is not a contrived input — it is the ordinary board reduced to two lines.
    /// </remarks>
    [Fact]
    public void R_rail18_1_TheSameCopperListedTwiceMeasuresTheSameWidth()
    {
        var square = RectPaths(0, 0, Mm(5), Mm(5));

        long once  = PdnMeshExtractor.MinimumFeatureWidthDbu([Island((Top, square))]);
        long twice = PdnMeshExtractor.MinimumFeatureWidthDbu([Island((Top, square)), Island((Top, square))]);

        // The bisection stops on a geometric ladder rather than exactly, so the square reads a little
        // under its own 5 mm. What matters is that it reads its own width at all.
        Assert.InRange(once / (double)Mm(5), 0.95, 1.0);
        Assert.Equal(once, twice);
    }

    /// <summary>
    /// A rail on two layers is measured per layer, and the answer is the NARROWER layer's width —
    /// not the union's (which would read the wide layer, because the narrow one crosses it) and not
    /// the pooled set's (which read nothing at all).
    /// </summary>
    [Fact]
    public void R_rail18_1_ATwoLayerRailMeasuresItsNarrowerLayer()
    {
        // A 1 mm run on TOP, and a 0.3 mm one on BOT crossing it at right angles — the shape a via
        // makes, and the two overlap over a 0.3 x 1 mm patch.
        var wide   = RectPaths(0, 0, Mm(10), Mm(1));
        var narrow = RectPaths(Mm(4), -Mm(3), Mm(4.3), Mm(4));

        long w = PdnMeshExtractor.MinimumFeatureWidthDbu([Island((Top, wide), (Bot, narrow))]);

        Assert.InRange(w / (double)Mm(0.3), 0.9, 1.05);
    }

    /// <summary>
    /// End to end on the board shape this defect is ABOUT — a rail on two layers, joined by vias,
    /// with its reference on a third. The accurate mesh's cell size is R-rail3-14's rule, and the
    /// provenance names a real width rather than 0 mm.
    /// </summary>
    /// <remarks>
    /// At HEAD this reported <c>"the rail's narrowest copper, 0 mm, at 3 cells across it"</c> with
    /// <c>CellSizeMetres = 1e-9</c>, and <c>PdnGrid.Build</c>'s <c>MaxCells</c> cap then chose the
    /// mesh. Nothing failed — the cap produces a workable mesh and the answer stayed plausible —
    /// which is why the assertion is on the STATED RULE and not on the resistance.
    /// </remarks>
    [Fact]
    public void R_rail18_1_TheAccurateCellSizeFollowsTheNarrowestCopper_OnATwoLayerRail()
    {
        var tech = ThreeConductorBoard();

        // 0.6 mm on TOP, a 0.15 mm run on BOT, and a via joining them. The BOT trace is the
        // narrowest copper the rail has anywhere and no single layer's measurement can see it and
        // the TOP run at once.
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(4), Mm(0.6)),
            Rect(Bot, Mm(3.2), 0, Mm(3.8), Mm(0.6)),                  // the landing
            Rect(Bot, Mm(3.425), Mm(0.6), Mm(3.575), Mm(3)),          // 0.15 mm
            Rect(Mid, -Mm(0.3), -Mm(0.3), Mm(4.3), Mm(3.3)),          // the reference plane
            new ViaShape
            {
                Layer = ViaLayer, LandingLayer = Top,
                X = Mm(3.5), Y = Mm(0.3), DrillSize = Mm(0.3), PadSize = Mm(0.45),
            },
        };

        var request = Request(tech, shapes, (Mm(0.2), Mm(0.3)), (Mm(3.5), Mm(2.8)), cellsAcross: 3);
        var multiLayer = new PdnExtractionRequest
        {
            Rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Mid },
            Shapes = shapes,
            Technology = tech,
            DbuPerMicron = DbuPerMicron,
            Pads = request.Pads,
            Mesh = request.Mesh,
        };
        multiLayer.Rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.7,
        });
        multiLayer.Rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.5,
        });

        var result = PdnMeshExtractor.Extract(multiLayer);
        Assert.Null(result.Refusal);

        var p = result.Netlist!.Provenance;

        // The rail really is on both layers — otherwise this gates the single-layer case again.
        Assert.True(result.Regions!.Power.SelectMany(r => r.Copper)
                          .Select(c => c.Layer).Distinct().Count() >= 2,
                    "the rail came back on one layer, so this board is not the case under test.");

        Assert.DoesNotContain("0 mm", p.CellSizeBasis, StringComparison.Ordinal);
        Assert.Contains("narrowest copper", p.CellSizeBasis, StringComparison.Ordinal);

        // The rule, arithmetically: the cell is the narrowest copper over the cells-across setting.
        // 1e-9 m is what the floor of 1 DBU reads as, and it is what this produced at HEAD.
        Assert.InRange(p.CellSizeMetres, 0.9 * 0.15e-3 / 3, 1.05 * 0.15e-3 / 3);
    }

    /// <summary>A stackup with the reference in the MIDDLE, so a rail can be on two layers at once —
    /// which <see cref="Board"/>'s two-conductor stackup cannot express.</summary>
    private static Technology ThreeConductorBoard()
    {
        var tech = new Technology { Name = "test board, 3 conductors" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "PP1", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "MID",
                ThicknessDbu = Um(18), SigmaSm = CopperSigma, DrawingLayers = [Mid],
                IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.1), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot],
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
        ];
        return tech;
    }

    // ── the headline gate ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R = L/(σ·W·T), to under 1 %, on the two rows of §2.8 that straddle the behaviour: a long thin
    /// inner-layer run on 0.5 oz copper, and a shorter wider outer-layer one on 1 oz.
    ///
    /// <para>The span compared against is the one the extraction actually MEASURED — the distance
    /// between the two port cells' centres, read back out of <c>NodeCells</c> — because a port sits
    /// at a cell centre and not at the end of the copper. The test asserts separately that that span
    /// is the trace's own length to within one cell, so the comparison cannot be satisfied by a mesh
    /// that lost half the trace.</para>
    /// </summary>
    [Theory]
    [InlineData(50.0, 0.3, 17.5)]   // 50 mm of 0.3 mm inner trace, 0.5 oz  →  ~165 mΩ
    [InlineData(30.0, 0.5, 35.0)]   // 30 mm of 0.5 mm outer trace, 1 oz    →  ~29 mΩ
    public void MeshReproducesTheClosedForm(double lengthMm, double widthMm, double thicknessUm)
    {
        var tech = Board(thicknessUm, 35.0, 1.6);

        long w = Mm(widthMm), l = Mm(lengthMm);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, l, w),
            Rect(Bot, 0, -Mm(0.35), l, w + Mm(0.35)),   // a reference under the whole run
        };

        var result = PdnMeshExtractor.Extract(
            Request(tech, shapes, source: (Mm(0.05), w / 2), load: (l - Mm(0.05), w / 2)));

        Assert.Null(result.Refusal);
        var pdn = result.Netlist!;

        int a = SourceNode(pdn), b = NodeAt(pdn, "U1.VDD");
        double measured = ResistanceBetween(pdn.Netlist, a, b);

        double spanM = Math.Abs(pdn.NodeCells[a].CentreX - pdn.NodeCells[b].CentreX)
                     / (DbuPerMicron * 1e6);
        double expected = CopperRho * spanM / (widthMm * 1e-3 * thicknessUm * 1e-6);

        // The measured span is the trace, to within one cell of it — so `expected` above is the run
        // and not a fraction of it.
        double cell = pdn.Provenance.CellSizeMetres;
        Assert.InRange(spanM, lengthMm * 1e-3 - 3 * cell, lengthMm * 1e-3);

        Assert.InRange(measured, expected * 0.99, expected * 1.01);
    }

    /// <summary>
    /// A stepped trace is the sum over its sections, to under 1 % — the same closed form applied
    /// twice. The residue is the spreading at the step, which is real physics the closed form omits
    /// and which the mesh includes; the gate is that it is small, not that it is absent.
    /// </summary>
    [Fact]
    public void SteppedTraceIsTheSumOverItsSections()
    {
        var tech = Board(35.0, 35.0, 1.6);

        long w1 = Mm(0.5), w2 = Mm(0.3), l1 = Mm(20), l2 = Mm(20);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, l1, w1),
            Rect(Top, l1, (w1 - w2) / 2, l1 + l2, (w1 + w2) / 2),
            Rect(Bot, -Mm(0.5), -Mm(0.5), l1 + l2 + Mm(0.5), w1 + Mm(0.5)),
        };

        var result = PdnMeshExtractor.Extract(
            Request(tech, shapes, source: (Mm(0.05), w1 / 2), load: (l1 + l2 - Mm(0.05), w1 / 2)));

        Assert.Null(result.Refusal);
        var pdn = result.Netlist!;

        int a = SourceNode(pdn), b = NodeAt(pdn, "U1.VDD");
        double measured = ResistanceBetween(pdn.Netlist, a, b);

        double xa = pdn.NodeCells[a].CentreX / (DbuPerMicron * 1e6);
        double xb = pdn.NodeCells[b].CentreX / (DbuPerMicron * 1e6);
        double step = l1 / (DbuPerMicron * 1e6);

        double expected = CopperRho * (step - xa) / (0.5e-3 * 35e-6)
                        + CopperRho * (xb - step) / (0.3e-3 * 35e-6);

        Assert.InRange(measured, expected * 0.99, expected * 1.01);
    }

    /// <summary>
    /// The sheet resistances every row of §2.8's table is built from — 0.49 mΩ/square at 1 oz,
    /// 0.99 mΩ/square at 0.5 oz — and the two arithmetic-only rows that follow from them. Folded
    /// into one test because they exercise no mesh at all.
    /// </summary>
    [Fact]
    public void SheetResistanceAndTheArithmeticRows()
    {
        var oneOz = new PdnConductor("TOP", Top, 35e-6, CopperSigma);
        var halfOz = new PdnConductor("IN1", Top, 17.5e-6, CopperSigma);

        Assert.Equal(0.49e-3, oneOz.SheetResistanceOhmsPerSquare, 5);
        Assert.Equal(0.99e-3, halfOz.SheetResistanceOhmsPerSquare, 5);

        // 10 mm of 1 mm-wide 1 oz trace — ten squares. ~5 mΩ.
        Assert.Equal(4.93e-3, oneOz.SheetResistanceOhmsPerSquare * (10.0 / 1.0), 4);

        // One 0.3 mm plated via through a 1.6 mm board — ~1.2 mΩ.
        double via = PdnViaModel.BarrelResistanceOhms(
            0.3e-3, PdnViaModel.DefaultPlatingMicrometres * 1e-6, 1.6e-3, CopperRho);
        Assert.InRange(via, 1.1e-3, 1.35e-3);
    }

    /// <summary>
    /// <b>The factor of two of §4.1, measured rather than asserted.</b>
    ///
    /// <para>The design note writes the plane pair's series resistance per cell edge as
    /// <c>R = 2·Rs</c>, "both planes, in series in the loop", and rev 2 got exactly this wrong — it
    /// used ONE plane's sheet resistance and every derived crossover frequency came out at half its
    /// real value.</para>
    ///
    /// <para>The 2 belongs to the LOOP, and this extractor pays it by meshing BOTH conductors, which
    /// is what §4.3's separate power and reference nodes require. So the gate is the loop: an
    /// identical conductor above and below, out along one and back along the other, is twice one of
    /// them. A reader who "optimises" by meshing only the rail and tying the reference to ground
    /// halves every loop resistance in the answer, and nothing else reports it.</para>
    /// </summary>
    [Fact]
    public void ThePlanePairLoopIsTwiceOnePlane()
    {
        var tech = Board(35.0, 35.0, 0.1);

        long w = Mm(0.5), l = Mm(20);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, l, w),
            Rect(Bot, 0, 0, l, w),      // the return mirrors the rail exactly
        };

        var result = PdnMeshExtractor.Extract(
            Request(tech, shapes, source: (Mm(0.1), w / 2), load: (l - Mm(0.1), w / 2), cellsAcross: 1));

        Assert.Null(result.Refusal);
        var pdn = result.Netlist!;

        int sourcePower = SourceNode(pdn);
        int loadPower = pdn.Ports[0].PowerNode;
        int loadReference = pdn.Ports[0].ReferenceNode;
        int sourceReference = 0;                       // the reference point, by construction

        double outward = ResistanceBetween(pdn.Netlist, sourcePower, loadPower);
        double back = ResistanceBetween(pdn.Netlist, loadReference, sourceReference);

        Assert.True(outward > 0);
        Assert.Equal(outward, back, 9);                // one plane each way
        Assert.Equal(2.0, (outward + back) / outward, 9);
    }

    // ── R-rail3-7: the mesh follows the copper ─────────────────────────────────────────────────

    /// <summary>
    /// A plane with a cutout, a split and an antipad field produces a mesh with no cells in any of
    /// them — asserted BY AREA against the copper, which is the strong form of the claim: a mesh
    /// whose cells summed to more than the copper would have filled something in.
    /// </summary>
    [Fact]
    public void MeshFollowsTheCopperThroughCutoutsSplitsAndAntipads()
    {
        var tech = Board(35.0, 35.0, 1.6);

        // A 10 × 6 mm pour, split by a 0.4 mm slot, with a 1 mm cutout and a 3 × 3 antipad field.
        var pour = new PolygonShape
        {
            Layer = Top,
            Xy = [0, 0, Mm(10), 0, Mm(10), Mm(6), 0, Mm(6)],
            Holes =
            [
                [Mm(4.8), Mm(1.5), Mm(5.2), Mm(1.5), Mm(5.2), Mm(6), Mm(4.8), Mm(6)],   // the split
                [Mm(1), Mm(4), Mm(2), Mm(4), Mm(2), Mm(5), Mm(1), Mm(5)],               // the cutout
            ],
        };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                pour.Holes!.Add([Mm(6.5 + i * 0.8), Mm(1.0 + j * 0.8),
                                 Mm(6.9 + i * 0.8), Mm(1.0 + j * 0.8),
                                 Mm(6.9 + i * 0.8), Mm(1.4 + j * 0.8),
                                 Mm(6.5 + i * 0.8), Mm(1.4 + j * 0.8)]);

        var shapes = new List<LayoutShape> { pour, Rect(Bot, 0, 0, Mm(10), Mm(6)) };

        var result = PdnMeshExtractor.Extract(
            Request(tech, shapes, source: (Mm(0.5), Mm(3)), load: (Mm(9.5), Mm(3)), cellsAcross: 1));

        Assert.Null(result.Refusal);

        // The copper the flattener produced, and the copper the mesh cells account for, agree — so
        // the split, the cutout and every antipad removed cells rather than being averaged away, and
        // nothing outside the copper was filled in.
        var flat = PdnMeshExtractor.BuildLayerRegions(shapes, tech);
        double dbuPerMetre = DbuPerMicron * 1e6;
        double copper = (Math.Abs(Clipper.Area(flat[Top])) + Math.Abs(Clipper.Area(flat[Bot])))
                      / (dbuPerMetre * dbuPerMetre);

        Assert.Equal(copper, result.Netlist!.Provenance.MeshedAreaSquareMetres, 12);

        // And the holes are big enough that the claim bites: a 1 mm cutout at this cell size holds
        // whole cells, and none of them was handed a node.
        var holes = HolesOf(pour);
        Assert.DoesNotContain(
            result.Netlist.NodeCells.Values.Where(c => !c.IsReference),
            c => PdnRailRegions.Contains(holes, c.CentreX, c.CentreY)
              && PdnRailRegions.Contains(holes, c.CentreX - Mm(0.2), c.CentreY - Mm(0.2))
              && PdnRailRegions.Contains(holes, c.CentreX + Mm(0.2), c.CentreY + Mm(0.2)));
    }

    private static Paths64 HolesOf(PolygonShape p)
    {
        var paths = new Paths64();
        foreach (var hole in p.Holes!)
        {
            var path = new Path64();
            for (int i = 0; i + 1 < hole.Length; i += 2) path.Add(new Point64(hole[i], hole[i + 1]));
            paths.Add(path);
        }
        return paths;
    }

    // ── R-rail3-9: vias ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Twenty parallel vias are 1/20 of one, to under 1 %, with no special case anywhere — they are
    /// twenty resistors between the same two cells because that is what the artwork says, and
    /// parallel resistance is then arithmetic.
    /// </summary>
    [Fact]
    public void TwentyParallelViasAreOneTwentiethOfOne()
    {
        var tech = Board(35.0, 35.0, 1.6);

        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(4), Mm(4)),
            Rect(Bot, 0, 0, Mm(4), Mm(4)),
        };
        for (int i = 0; i < 20; i++)
            shapes.Add(new ViaShape
            {
                Layer = ViaLayer, LandingLayer = Top,
                X = Mm(1.0 + 0.1 * i), Y = Mm(2), DrillSize = Mm(0.3), PadSize = Mm(0.5),
            });

        // One cell per conductor, so every barrel joins the same two nodes and the answer is the
        // parallel combination and nothing else.
        var result = PdnMeshExtractor.Extract(
            Request(tech, shapes, source: (Mm(2), Mm(2)), load: (Mm(3), Mm(3)), cellSize: 10e-3));

        Assert.Null(result.Refusal);
        var vias = result.Netlist!.Origins.Where(o => o.Kind == PdnOriginKind.Via).ToList();
        Assert.Equal(20, vias.Count);

        double one = vias[0].ResistanceOhms!.Value;
        Assert.All(vias, v => Assert.Equal(one, v.ResistanceOhms!.Value, 12));

        double top = NodeOfCell(result.Netlist, Top);
        double bot = NodeOfCell(result.Netlist, Bot);
        double measured = ResistanceBetween(result.Netlist.Netlist, (int)top, (int)bot);

        Assert.InRange(measured, one / 20 * 0.99, one / 20 * 1.01);
    }

    /// <summary>
    /// Two parts sharing one return via are coupled through it because the mesh has a SINGLE NODE
    /// there, not two — asserted on the node, which is the sentence the design note writes as a test.
    /// A mesh that gave them a node each would produce a plausible number and no error.
    /// </summary>
    [Fact]
    public void TwoPartsOnOneReturnViaShareOneNode()
    {
        var tech = Board(35.0, 35.0, 1.6);

        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(10), Mm(4)),
            Rect(Bot, 0, 0, Mm(10), Mm(4)),
        };

        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.2,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U2", Pin = "VDD" }, DcCurrentA = 0.2,
        });

        var request = new PdnExtractionRequest
        {
            Rail = rail,
            Shapes = shapes,
            Technology = tech,
            DbuPerMicron = DbuPerMicron,
            Pads =
            [
                new PdnPad("BT1", "1", "VDD", Mm(0.5), Mm(2)),
                // Both parts sit within one cell of the shared return.
                new PdnPad("U1", "VDD", "VDD", Mm(5.1), Mm(2)),
                new PdnPad("U2", "VDD", "VDD", Mm(5.4), Mm(2)),
            ],
            Mesh = new PdnMeshSettings { CellSizeMetres = 1e-3, PortRefinementRatio = 1 },
        };

        var result = PdnMeshExtractor.Extract(request);
        Assert.Null(result.Refusal);

        var ports = result.Netlist!.Ports;
        Assert.Equal(2, ports.Count);
        Assert.Equal(ports[0].ReferenceNode, ports[1].ReferenceNode);
    }

    // ── R-rail3-8: the refinement is a correctness requirement ─────────────────────────────────

    /// <summary>
    /// Halving Δ under the port region changes the port resistance by under a stated tolerance.
    /// Structural, not timed: a mesh that is not converged under a pin field under-estimates the
    /// spreading resistance, optimistically, and looks entirely ordinary.
    /// </summary>
    [Fact]
    public void RefiningUnderThePortConverges()
    {
        var tech = Board(35.0, 35.0, 1.6);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(20), Mm(10)),
            Rect(Bot, 0, 0, Mm(20), Mm(10)),
        };

        double Measure(int refine)
        {
            var r = PdnMeshExtractor.Extract(Request(
                tech, shapes, source: (Mm(1), Mm(5)), load: (Mm(19), Mm(5)),
                refine: refine, cellSize: 0.5e-3));
            Assert.Null(r.Refusal);
            var pdn = r.Netlist!;
            return ResistanceBetween(pdn.Netlist, SourceNode(pdn), NodeAt(pdn, "U1.VDD"));
        }

        double coarse = Measure(1), fine = Measure(2);
        Assert.InRange(Math.Abs(fine - coarse) / coarse, 0.0, RefinementTolerance);
    }

    /// <summary>The tolerance <see cref="RefiningUnderThePortConverges"/> states, stated once so a
    /// reader can see the number rather than infer it.</summary>
    private const double RefinementTolerance = 0.10;

    // ── R-rail3-11: a series part is an ELEMENT ────────────────────────────────────────────────

    /// <summary>
    /// A protection FET at 350 mΩ appears in <c>Origins</c> as an element, and the same board with
    /// that element at zero differs by exactly 350 mΩ on the path. <b>Elements, never
    /// annotations</b> — at DC these are the largest terms after the source, and an annotation does
    /// not appear in a ranked breakdown.
    /// </summary>
    [Fact]
    public void ASeriesPartIsAnElementOnThePath()
    {
        var tech = Board(35.0, 35.0, 1.6);

        // Two runs of copper, the FET's two pads bridging the gap the artwork leaves between them.
        long w = Mm(0.5);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(10), w),
            Rect(Top, Mm(11), 0, Mm(21), w),
            Rect(Bot, -Mm(0.5), -Mm(0.5), Mm(21.5), w + Mm(0.5)),
        };

        double Measure(double ohms, out PdnNetlist pdn)
        {
            var request = Request(tech, shapes, source: (Mm(0.1), w / 2), load: (Mm(20.9), w / 2),
                                  cellsAcross: 1);
            var withFet = new PdnExtractionRequest
            {
                Rail = request.Rail,
                Shapes = request.Shapes,
                Technology = request.Technology,
                DbuPerMicron = request.DbuPerMicron,
                Pads = [.. request.Pads,
                        new PdnPad("Q1", "D", "VDD", Mm(9.9), w / 2),
                        new PdnPad("Q1", "S", "VDD", Mm(11.1), w / 2)],
                SeriesElements =
                [
                    new PdnSeriesElement(
                        "Q1",
                        new RailPortAnchor { Refdes = "Q1", Pin = "D" },
                        new RailPortAnchor { Refdes = "Q1", Pin = "S" },
                        ohms, "the part library's on-resistance"),
                ],
                Mesh = request.Mesh,
            };

            var r = PdnMeshExtractor.Extract(withFet);
            Assert.Null(r.Refusal);
            pdn = r.Netlist!;
            return ResistanceBetween(pdn.Netlist, SourceNode(pdn), NodeAt(pdn, "U1.VDD"));
        }

        double on = Measure(0.350, out var withFet);
        double shorted = Measure(0.0, out _);

        var origin = Assert.Single(
            withFet.Origins.Where(o => o.Kind == PdnOriginKind.SeriesElement));
        Assert.Equal("Q1", origin.Refdes);
        Assert.Equal(0.350, origin.ResistanceOhms!.Value, 12);

        // Not bit-exact, and the residue is not the mesh: a zero-ohm part is the engine's own
        // near-short conductance rather than a true short, so the "without it" board keeps 1 pΩ.
        Assert.Equal(0.350, on - shorted, 7);
    }

    // ── R-rail3-2: the one-write-path scan ─────────────────────────────────────────────────────

    /// <summary>
    /// Nothing under <c>src/Design/Layout/Pdn/</c> builds a matrix, factorises anything, owns a
    /// result type, or names the full-wave engine.
    ///
    /// <para><b>This is not style.</b> A second solve path here is the defect that makes §2.8's "one
    /// extractor, one mesh, one solver" false, and it would not announce itself — the DC and AC
    /// answers would simply disagree by a few percent on boards nobody checked by hand. Comments are
    /// stripped so the scan cannot be satisfied, or broken, by prose.</para>
    /// </summary>
    [Fact]
    public void NothingUnderPdnSolvesAnything()
    {
        string dir = Path.Combine(RepoRoot(), "src", "Design", "Layout", "Pdn");
        string[] banned =
        [
            "MnaSystem", "SparseLU", "CompressedColumnStorage",
            "CircuitRF.Engine.Mom", "EmProblem",
            "new DataSet(", "new DataCube(",
        ];

        var files = Directory.GetFiles(dir, "*.cs");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string code = StripComments(File.ReadAllText(file));
            foreach (string token in banned)
                Assert.False(code.Contains(token, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} names '{token}'. Nothing under Layout/Pdn may build a " +
                    "matrix, factorise anything, own a result type or reach the full-wave engine " +
                    "(R-rail3-2).");
        }
    }

    // ── determinism ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The same board extracted twice produces element-wise identical netlists. An extraction that
    /// depended on a dictionary's hash order would produce a drop map that moved between runs, and
    /// briefs 9 and 17 both depend on it not doing that.
    /// </summary>
    [Fact]
    public void TheSameBoardExtractsIdentically()
    {
        var tech = Board(35.0, 35.0, 1.6);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(8), Mm(0.4)),
            Rect(Top, Mm(3), 0, Mm(3.4), Mm(4)),
            Rect(Bot, -Mm(0.5), -Mm(0.5), Mm(8.5), Mm(4.5)),
        };

        static string Signature(PdnExtraction e)
        {
            var pdn = e.Netlist!;
            return string.Join("\n", pdn.Netlist.Components.Select(c =>
                $"{c.ComponentType} {c.InstancePath} {string.Join(",", c.Nodes)} " +
                string.Join(",", c.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                                             .Select(p => $"{p.Key}={p.Value.AsReal():R}"))));
        }

        var a = PdnMeshExtractor.Extract(
            Request(tech, shapes, source: (Mm(0.2), Mm(0.2)), load: (Mm(7.8), Mm(0.2))));
        var b = PdnMeshExtractor.Extract(
            Request(tech, shapes, source: (Mm(0.2), Mm(0.2)), load: (Mm(7.8), Mm(0.2))));

        Assert.Null(a.Refusal);
        Assert.Equal(Signature(a), Signature(b));

        // And it is deterministic BY CONSTRUCTION rather than by luck: the conductors are walked in
        // a sorted order, the rail's before the reference's, so the node numbering cannot depend on
        // which layer a dictionary happened to yield first.
        var cells = a.Netlist!.NodeCells.Where(kv => kv.Key > 0).OrderBy(kv => kv.Key).ToList();
        int lastRail = cells.Where(kv => !kv.Value.IsReference).Select(kv => kv.Key).DefaultIfEmpty(-1).Max();
        int firstRef = cells.Where(kv => kv.Value.IsReference).Select(kv => kv.Key).DefaultIfEmpty(int.MaxValue).Min();
        Assert.True(lastRail < firstRef,
            "The rail's cells and the reference's are interleaved, which means the layer order is " +
            "whatever a hash yielded rather than the sorted one.");
    }

    // ── R-rail3-5 / R-rail3-4: what the result carries ─────────────────────────────────────────

    /// <summary>
    /// The reference extent is applied here and STAMPED, and a <c>FilledToOutline</c> with no outline
    /// is a refusal naming what answers it rather than a quiet fallback to the copper.
    /// </summary>
    [Fact]
    public void TheReferenceExtentIsAppliedAndCarried()
    {
        var tech = Board(35.0, 35.0, 1.6);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(8), Mm(0.5)),
            Rect(Bot, 0, 0, Mm(8), Mm(0.5)),
        };

        var asImported = Request(tech, shapes, (Mm(0.2), Mm(0.25)), (Mm(7.8), Mm(0.25)), cellsAcross: 1);
        var result = PdnMeshExtractor.Extract(asImported);
        Assert.Null(result.Refusal);
        Assert.Equal(RailReferenceExtent.AsImported, result.Netlist!.Provenance.ReferenceExtent);
        Assert.Contains("one region", result.Netlist.Provenance.IslandReport, StringComparison.Ordinal);

        asImported.Rail.ReferenceExtent = RailReferenceExtent.FilledToOutline;
        var refused = PdnMeshExtractor.Extract(asImported);
        Assert.NotNull(refused.Refusal);
        Assert.Contains("outline", refused.Refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(refused.Netlist);

        asImported.Rail.ReferenceExtent = RailReferenceExtent.Infinite;
        var infinite = PdnMeshExtractor.Extract(asImported);
        Assert.Null(infinite.Refusal);
        Assert.Equal(RailReferenceExtent.Infinite, infinite.Netlist!.Provenance.ReferenceExtent);
        Assert.Contains(infinite.Netlist.Provenance.Notes,
                        n => n.Contains("optimistic", StringComparison.Ordinal));
    }

    /// <summary>
    /// A rail in three galvanic pieces is reported as three regions, not as an error — two islands
    /// joined by nothing at DC and by a capacitor at AC is exactly what an imported board looks like
    /// before anyone has said what bridges each gap (R-rail3-4).
    /// </summary>
    [Fact]
    public void IslandsAreReportedRatherThanRefused()
    {
        var tech = Board(35.0, 35.0, 1.6);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(3), Mm(0.5)),
            Rect(Top, Mm(4), 0, Mm(7), Mm(0.5)),
            Rect(Top, Mm(8), 0, Mm(11), Mm(0.5)),
            Rect(Bot, 0, 0, Mm(11), Mm(0.5)),
        };

        // Geometry alone cannot say a piece of copper is VDD, so the board netlist is what names all
        // three — which is exactly the join R-rail3-3 describes.
        var request = Request(tech, shapes, (Mm(0.2), Mm(0.25)), (Mm(2.8), Mm(0.25)), cellsAcross: 1);
        var named = new PdnExtractionRequest
        {
            Rail = request.Rail,
            Shapes = request.Shapes,
            Technology = request.Technology,
            DbuPerMicron = request.DbuPerMicron,
            Pads = request.Pads,
            NetPoints =
            [
                new PdnNetPoint("VDD", Mm(1.5), Mm(0.25)),
                new PdnNetPoint("VDD", Mm(5.5), Mm(0.25)),
                new PdnNetPoint("VDD", Mm(9.5), Mm(0.25)),
            ],
            Mesh = request.Mesh,
        };

        var result = PdnMeshExtractor.Extract(named);

        Assert.Null(result.Refusal);
        Assert.Equal(3, result.Regions!.Power.Count);
        Assert.Contains("3 regions", result.Regions.IslandReport, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics,
                        d => d.Contains("galvanically separate", StringComparison.Ordinal));
    }

    /// <summary>
    /// A capacitor is IN the netlist and carries no DC path. Leaving it out would make brief 14's
    /// netlist a different netlist from this one; stamping a DC path through it would make the drop
    /// answer wrong in the flattering direction.
    /// </summary>
    [Fact]
    public void ACapacitorIsPresentAndBridgesNothingAtDc()
    {
        var tech = Board(35.0, 35.0, 1.6);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(8), Mm(0.5)),
            Rect(Bot, 0, 0, Mm(8), Mm(0.5)),
        };

        var request = Request(tech, shapes, (Mm(0.2), Mm(0.25)), (Mm(7.8), Mm(0.25)), cellsAcross: 1);
        var withCap = new PdnExtractionRequest
        {
            Rail = request.Rail,
            Shapes = request.Shapes,
            Technology = request.Technology,
            DbuPerMicron = request.DbuPerMicron,
            Pads = [.. request.Pads, new PdnPad("C1", "1", "VDD", Mm(4), Mm(0.25))],
            ShuntParts =
            [
                new PdnShuntPart("C1", new RailPortAnchor { Refdes = "C1", Pin = "1" }, 1e-6),
            ],
            Mesh = request.Mesh,
        };

        var result = PdnMeshExtractor.Extract(withCap);
        Assert.Null(result.Refusal);

        var pdn = result.Netlist!;
        var cap = Assert.Single(pdn.Netlist.Components.Where(c => c.Model is CapacitorModel));
        Assert.Equal(1e-6, cap.Parameters["C"].AsReal(), 12);

        // The rail's copper and the reference's are separate in the RESISTIVE network: the capacitor
        // joins them and contributes no conductance, which is what "bridges nothing at DC" means.
        Assert.False(ResistivelyConnected(pdn.Netlist, cap.Nodes[0], cap.Nodes[1]));
    }

    // ── the little solver, and the readers it needs ────────────────────────────────────────────

    /// <summary>The rail-side node of an observation port, by the name the anchor reads as.</summary>
    private static int NodeAt(PdnNetlist pdn, string portName) =>
        pdn.Ports.First(p => p.Name == portName).PowerNode;

    /// <summary>
    /// The rail-side node of the first source. A source is not a port — it is a branch — so it is
    /// found through the elements it stamped: the series resistance's far end where it has one, and
    /// the voltage branch's own node where it does not.
    /// </summary>
    private static int SourceNode(PdnNetlist pdn)
    {
        var series = pdn.Netlist.Components.FirstOrDefault(c => c.InstancePath == "source.1.r");
        if (series is not null) return series.Nodes[1];
        return pdn.Netlist.Components.First(c => c.InstancePath == "source.1.v").Nodes[0];
    }

    private static int NodeOfCell(PdnNetlist pdn, LayerKey layer) =>
        pdn.NodeCells.First(kv => kv.Value.Layer == layer).Key;

    /// <summary>
    /// Every resistor of the netlist, as a conductance. <b>R = 0 becomes the same near-short
    /// conductance <c>ResistorModel</c> itself stamps</b> — dropping it instead would disconnect a
    /// board whose series part is a link, which is precisely the comparison
    /// <see cref="ASeriesPartIsAnElementOnThePath"/> makes.
    /// </summary>
    private static List<(int A, int B, double G)> Resistors(ElaboratedNetlist nl)
    {
        var edges = new List<(int, int, double)>();
        foreach (var c in nl.Components)
        {
            if (c.Model is not ResistorModel) continue;
            if (!c.Parameters.TryGetValue("R", out var r)) continue;
            double ohms = r.AsReal();
            if (ohms < 0) continue;
            edges.Add((c.Nodes[0], c.Nodes[1], ohms > 0 ? 1.0 / ohms : ResistorModel.DefaultGmax));
        }
        return edges;
    }

    private static bool ResistivelyConnected(ElaboratedNetlist nl, int a, int b)
    {
        var adj = Adjacency(nl, out _);
        var seen = new HashSet<int> { a };
        var stack = new Stack<int>([a]);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (n == b) return true;
            if (!adj.TryGetValue(n, out var nbrs)) continue;
            foreach (var (m, _) in nbrs) if (seen.Add(m)) stack.Push(m);
        }
        return false;
    }

    private static Dictionary<int, List<(int To, double G)>> Adjacency(
        ElaboratedNetlist nl, out int count)
    {
        var adj = new Dictionary<int, List<(int, double)>>();
        foreach (var (a, b, g) in Resistors(nl))
        {
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = [];
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = [];
            la.Add((b, g));
            lb.Add((a, g));
        }
        count = adj.Count;
        return adj;
    }

    /// <summary>
    /// The DC resistance between two nodes of the netlist's RESISTIVE network, by injecting 1 A at
    /// <paramref name="a"/> with <paramref name="b"/> held at zero and solving the Laplacian with
    /// Jacobi-preconditioned conjugate gradients.
    ///
    /// <para>Written here, in the test, because brief 3's extractor never solves anything — and
    /// because a gate that measures what a solver would see is a better gate than one that reads the
    /// extractor's own belief about what it built.</para>
    /// </summary>
    private static double ResistanceBetween(ElaboratedNetlist nl, int a, int b)
    {
        var adj = Adjacency(nl, out _);

        // Only the component containing `a`: a source's internal node dangles in a resistor-only
        // reading and would make the system singular.
        var index = new Dictionary<int, int>();
        var order = new List<int>();
        var stack = new Stack<int>([a]);
        index[a] = 0;
        order.Add(a);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (!adj.TryGetValue(n, out var nbrs)) continue;
            foreach (var (m, _) in nbrs)
                if (!index.ContainsKey(m)) { index[m] = order.Count; order.Add(m); stack.Push(m); }
        }

        Assert.True(index.ContainsKey(b), "The two nodes are not resistively connected.");

        int n0 = order.Count;
        var rows = new List<(int To, double G)>[n0];
        var diag = new double[n0];
        for (int i = 0; i < n0; i++) rows[i] = [];

        foreach (int node in order)
        {
            int i = index[node];
            if (!adj.TryGetValue(node, out var nbrs)) continue;
            foreach (var (m, g) in nbrs)
            {
                rows[i].Add((index[m], g));
                diag[i] += g;
            }
        }

        int grounded = index[b];
        var rhs = new double[n0];
        rhs[index[a]] = 1.0;
        rhs[grounded] = 0.0;

        void Mul(double[] x, double[] into)
        {
            for (int i = 0; i < n0; i++)
            {
                if (i == grounded) { into[i] = x[i]; continue; }
                double s = diag[i] * x[i];
                foreach (var (j, g) in rows[i]) s -= g * x[j];
                into[i] = s;
            }
        }

        var v = new double[n0];
        var r = new double[n0];
        var p = new double[n0];
        var z = new double[n0];
        var ap = new double[n0];

        Mul(v, ap);
        for (int i = 0; i < n0; i++) r[i] = rhs[i] - ap[i];

        double Pre(int i) => i == grounded ? 1.0 : (diag[i] > 0 ? diag[i] : 1.0);
        for (int i = 0; i < n0; i++) p[i] = z[i] = r[i] / Pre(i);

        double rz = Dot(r, z);
        for (int it = 0; it < 20000 && Math.Sqrt(Dot(r, r)) > 1e-14; it++)
        {
            Mul(p, ap);
            double alpha = rz / Dot(p, ap);
            for (int i = 0; i < n0; i++) { v[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
            for (int i = 0; i < n0; i++) z[i] = r[i] / Pre(i);
            double rzNew = Dot(r, z);
            double beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < n0; i++) p[i] = z[i] + beta * p[i];
        }

        return v[index[a]] - v[grounded];
    }

    private static double Dot(double[] x, double[] y)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += x[i] * y[i];
        return s;
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
