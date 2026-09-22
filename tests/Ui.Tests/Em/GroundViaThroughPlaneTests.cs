// ================================================================
//  GroundViaThroughPlaneTests.cs — GVIA-1: a through via that CROSSES the return plane is
//  classified from the plane's own artwork, per via, and not from the technology.
//
//  The gap this closes, in one sentence: a board with an inner ground plane declares ONE via entry
//  spanning top copper to bottom copper — which is true of every barrel on it — so the extractor
//  found a span naming a conductor that is neither an analysis level nor the return plane and
//  dropped EVERY via with the wrongGround note. The user-reported board lost all 327 of them and
//  was solved as top copper over a plane it was not stitched to. The technology is not wrong and
//  editing it cannot help: which vias are ground stitches is a fact about the PLANE ARTWORK.
//
//  The order below is the substance. Gate 1 is that nothing this touches moved — an entry whose
//  span resolves today is bit-for-bit what it was. Then the split itself, then the island rule
//  (the case a plain containment test gets wrong and the reason this file exists), then the
//  refusal that must survive, then the note.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class GroundViaThroughPlaneTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey TopCopper    = new(1, 0);
    private static readonly LayerKey BottomCopper = new(2, 0);
    private static readonly LayerKey GndArt       = new(9, 0);
    private static readonly LayerKey Drill        = new(7, 0);
    private static readonly LayerKey Upper2       = new(11, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    // ── The technology: a 4-layer board whose ground plane is INNER ───────────────────────────
    //
    // Deliberately not a starter: every shipped one has its plane at an outer face, which is the
    // shape where a via's span never crosses anything. The numbers are the reported board's own
    // (0.9 mm of FR-4 either side of an 18 µm plane) so the fixture and the report describe the
    // same structure.
    private static Technology FourLayer(string spanFrom = "Top Copper", string spanTo = "Bottom Copper")
        => new()
        {
            Name = "Test 4-Layer Inner Ground",
            DefaultDisplayUnit = LayoutUnit.Mm,
            DefaultSnapDbu = Mm(0.001),
            DefaultFlattenTolDbu = Mm(0.001),
            DefaultLabelHeightDbu = Mm(0.3),
            Layers =
            [
                new LayerDef { Key = TopCopper,    Name = "Top Copper",    ZOrder = 4, Purpose = "conductor" },
                new LayerDef { Key = BottomCopper, Name = "Bottom Copper", ZOrder = 3, Purpose = "conductor" },
                new LayerDef { Key = GndArt,       Name = "gnd",           ZOrder = 2, Purpose = "drawing" },
                new LayerDef { Key = Drill,        Name = "Drill",         ZOrder = 1, Purpose = "drill" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open,
                Bottom = BoundaryCondition.Ground,
                Layers =
                [
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Top Copper",
                        ThicknessDbu = Mm(0.035), SigmaSm = 5.8e7, DrawingLayers = [TopCopper],
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Dielectric, Name = "Upper Core",
                        ThicknessDbu = Mm(0.9), Epsr = 4.4, TanD = 0.02,
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "GND plane",
                        ThicknessDbu = Mm(0.018), SigmaSm = 5.8e7, DrawingLayers = [GndArt],
                        IsGroundReference = true,
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Via, Name = "Plated Through-Hole",
                        DrawingLayers = [Drill], Fill = ViaFillKind.Plated,
                        SpanFromLayer = spanFrom, SpanToLayer = spanTo,
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Dielectric, Name = "Lower Core",
                        ThicknessDbu = Mm(0.9), Epsr = 4.4, TanD = 0.02,
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Bottom Copper",
                        ThicknessDbu = Mm(0.035), SigmaSm = 5.8e7, DrawingLayers = [BottomCopper],
                    },
                ],
            },
        };

    /// <summary>The same board with a SECOND conductor above the plane, so a span can name two
    /// conductors on one side of it — which is the shape both "this already resolved" and "this is
    /// still refused" need, and neither is expressible on a stack with one upper conductor.</summary>
    private static Technology TwoUpperConductors()
    {
        var tech = FourLayer();
        tech.Layers.Add(new LayerDef { Key = Upper2, Name = "Top Copper 2", ZOrder = 5, Purpose = "conductor" });
        tech.Stackup.Layers.Insert(0, new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Top Copper 2",
            ThicknessDbu = Mm(0.035), SigmaSm = 5.8e7, DrawingLayers = [Upper2],
        });
        tech.Stackup.Layers.Insert(1, new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Prepreg", ThicknessDbu = Mm(0.1), Epsr = 4.4,
        });
        return tech;
    }

    // ── The artwork ───────────────────────────────────────────────────────────────────────────
    //
    // Top copper is one 10 x 6 mm pour, so every via below has metal on the analysis level to
    // attach to and the only thing separating them is the PLANE's artwork.
    //
    // The plane is a 10 x 6 mm pour with one 2 x 2 mm VOID in it at (6,2)-(8,4), and inside that
    // void a 0.6 x 0.6 mm ISLAND — a signal via's own annular pad, which is copper drawn on the
    // plane layer and is not the plane.

    private static PolygonShape Rect(LayerKey layer, double x0, double y0, double x1, double y1,
                                     params (double X0, double Y0, double X1, double Y1)[] holes)
        => new()
        {
            Layer = layer,
            Xy = [Mm(x0), Mm(y0), Mm(x1), Mm(y0), Mm(x1), Mm(y1), Mm(x0), Mm(y1)],
            Holes = holes.Length == 0
                ? null
                : [.. holes.Select(h => new[] { Mm(h.X0), Mm(h.Y0), Mm(h.X1), Mm(h.Y0),
                                                Mm(h.X1), Mm(h.Y1), Mm(h.X0), Mm(h.Y1) })],
        };

    /// <summary>A 0.3 mm drill at (x, y) mm.</summary>
    private static ViaShape Via(double xMm, double yMm) => new()
    {
        Layer = Drill, X = Mm(xMm), Y = Mm(yMm), DrillSize = Mm(0.3), PadSize = Mm(0.5),
    };

    private static LabelShape Port(string name, double xMm, double yMm) => new()
    {
        Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = name, Height = Mm(0.3), IsPort = true,
    };

    private static List<LayoutShape> Board() =>
    [
        Rect(TopCopper, 0, 0, 10, 6),
        Rect(GndArt,    0, 0, 10, 6, (6, 2, 8, 4)),   // the pour, with one void
        Rect(GndArt,    6.7, 2.7, 7.3, 3.3),          // the island INSIDE that void
        Port("P1", 0, 3),
        Port("P2", 10, 3),
    ];

    private static EmExtractionSettings TopLevel =>
        new(AnalysisLevelNames: ["Top Copper"], GroundStackupLayerName: "GND plane");

    private static PlanarExtractionResult Extract(
        IEnumerable<LayoutShape> shapes, Technology? tech = null, EmExtractionSettings? settings = null)
        => PlanarExtractor.Extract([.. shapes], tech ?? FourLayer(), Dbu, 10e9, settings ?? TopLevel);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — an entry whose span resolves TODAY is untouched
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The per-shape rule may only reach a span that used to be dropped outright. A via
    /// entry naming the return plane itself already resolved before GVIA-1 and must still resolve
    /// for EVERY shape on it, with no artwork consulted — a plane-layer void is a clearance for a
    /// barrel that passes THROUGH, and a via that terminates on the plane by declaration is a
    /// different object.</summary>
    [Fact]
    public void AnEntryThatNamesThePlaneItself_StillGroundsEveryViaOnIt_WhereverItIsDrawn()
    {
        var shapes = Board();
        shapes.Add(Via(2, 3));      // over pour
        shapes.Add(Via(7, 3));      // over the island, inside the void
        shapes.Add(Via(6.5, 3.5));  // bare dielectric inside the void

        var r = Extract(shapes, FourLayer(spanTo: "GND plane"));
        Assert.True(r.Ok, r.Refusal);

        Assert.Equal(3, r.Problem!.ViaList.Count);
        Assert.All(r.Problem.ViaList, v => Assert.True(v.ToGround));

        // And the run says it the old way, not the new one.
        Assert.Contains(r.Notes, n => n.Contains("BACKSIDE vias", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("on the far side of the plane", StringComparison.Ordinal));
    }

    /// <summary>The other half of the same gate: a span between two MESHED levels is resolved from
    /// the technology for every shape, as it always was — including the one drawn in a void, which
    /// is a clearance for a barrel that is not going anywhere near the plane.</summary>
    [Fact]
    public void AnEntryBetweenTwoAnalysisLevels_IsUntouched()
    {
        var tech = TwoUpperConductors();
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Via)
            { l.SpanFromLayer = "Top Copper"; l.SpanToLayer = "Top Copper 2"; }

        var shapes = Board();
        shapes.Add(Rect(Upper2, 0, 0, 10, 6));
        shapes.Add(Via(7, 3));      // inside the void, and irrelevant to a level-to-level span

        var r = Extract(shapes, tech,
                        new EmExtractionSettings(AnalysisLevelNames: ["Top Copper", "Top Copper 2"],
                                                 GroundStackupLayerName: "GND plane"));
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.False(via.ToGround);
        Assert.Equal(0, via.LowerLayerIndex);
        Assert.Equal(1, via.UpperLayerIndex);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 2 — the split, and the island rule that a containment test alone gets wrong
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The whole feature in one assertion: same entry, same declared span, four barrels,
    /// and only the one standing on plane copper becomes a ground attachment.</summary>
    [Fact]
    public void AThroughViaIsGroundedByTheArtwork_NotByTheStackup()
    {
        var shapes = Board();
        shapes.Add(Via(2, 3));       // on the pour           -> stitched
        shapes.Add(Via(6.5, 3.5));   // in the void, bare     -> passes through
        shapes.Add(Via(7, 3));       // in the void, ISLAND   -> passes through
        shapes.Add(Via(12, 3));      // off the pour entirely -> passes through

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.True(via.ToGround);
        Assert.Equal(PlanarVia.GroundTerminal, via.LowerLayerIndex);
        Assert.Equal(0, via.UpperLayerIndex);

        // It is the one over the pour, and the footprint is the equal-area square of the drill.
        var poly = Assert.Single(via.Polygons);
        var (minX, minY, maxX, maxY) = poly.Bounds();
        Assert.Equal(2e-3, 0.5 * (minX + maxX), 9);
        Assert.Equal(3e-3, 0.5 * (minY + maxY), 9);
        Assert.Equal(0.3e-3 * Math.Sqrt(Math.PI) / 2.0, maxX - minX, 9);

        _out.WriteLine(r.Notes.First(n => n.Contains("on the far side of the plane", StringComparison.Ordinal)));
    }

    /// <summary>The island is the case that makes this a connectivity question rather than a
    /// containment one, so it is gated on its own. A via over the island IS over copper drawn on
    /// the plane layer; removing the island from the drawing is the only difference between the two
    /// runs below, and it is the difference between grounded and not.</summary>
    [Fact]
    public void CopperIsolatedInsideAVoid_IsNotThePlane()
    {
        var withIsland = Board();
        withIsland.Add(Via(7, 3));
        var a = Extract(withIsland);
        Assert.True(a.Ok, a.Refusal);
        Assert.Empty(a.Problem!.ViaList);

        // The same via, with the island's rectangle deleted and nothing else changed.
        var noIsland = Board();
        noIsland.RemoveAll(s => s is PolygonShape p && p.Layer == GndArt && p.Holes is null);
        noIsland.Add(Via(7, 3));
        var b = Extract(noIsland);
        Assert.True(b.Ok, b.Refusal);
        Assert.Empty(b.Problem!.ViaList);   // still in the void — the island was never what grounded it

        // And the island is counted as an island rather than as plane.
        Assert.Contains(a.Notes, n => n.Contains("isolated islands inside a void rather than the plane",
                                                 StringComparison.Ordinal));
    }

    /// <summary>A drawn REGION on the via layer is classified the same way, and the group keeps
    /// only the footprints that reach the plane — one PlanarVia carrying the survivors, never one
    /// per shape (which would double the metal where two footprints overlap) and never none
    /// (which would lose the half that stitches).</summary>
    [Fact]
    public void ADrawnFootprintIsClassifiedTheSameWay_AndTheGroupKeepsTheSurvivors()
    {
        var shapes = Board();
        shapes.Add(Rect(Drill, 1.8, 2.8, 2.2, 3.2));   // on the pour
        shapes.Add(Rect(Drill, 6.4, 3.4, 6.6, 3.6));   // in the void
        shapes.Add(Rect(Drill, 6.9, 2.9, 7.1, 3.1));   // on the island in the void

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.True(via.ToGround);
        var poly = Assert.Single(via.Polygons);
        var (minX, _, maxX, _) = poly.Bounds();
        Assert.Equal(1.8e-3, minX, 9);
        Assert.Equal(2.2e-3, maxX, 9);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 3 — the refusal this is carved out of must survive everywhere else
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A span naming a conductor that does NOT lie on the far side of the plane is still
    /// the wrongGround refusal. Here the plane is below both named conductors, so nothing crosses
    /// it and there is no artwork question to ask — treating it as one would model a via to a
    /// finite pour as an attachment to the infinite PEC, which is the structure nobody drew.</summary>
    [Fact]
    public void AConductorOnTheSameSideOfThePlane_IsStillRefused()
    {
        var tech = TwoUpperConductors();
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Via) l.SpanToLayer = "Top Copper 2";

        var shapes = Board();
        shapes.Add(Via(2, 3));

        var r = Extract(shapes, tech);
        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Problem!.ViaList);
        Assert.Contains(r.Notes, n => n.Contains(
            "neither an analysis level nor the ground plane", StringComparison.Ordinal));
    }

    /// <summary>With no artwork on the plane layer at all there is nothing to classify FROM, and a
    /// rule that grounded every via in that case would be a guess. The refusal stands.</summary>
    [Fact]
    public void WithNoPlaneArtwork_TheRefusalStands()
    {
        List<LayoutShape> shapes =
        [
            Rect(TopCopper, 0, 0, 10, 6), Port("P1", 0, 3), Port("P2", 10, 3), Via(2, 3),
        ];

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Problem!.ViaList);
        Assert.Contains(r.Notes, n => n.Contains(
            "neither an analysis level nor the ground plane", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 4 — the run SAYS what it decided, with both counts
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A structural change this size that a user cannot see is the failure mode this whole
    /// area is written against — 266 vias appearing in a model is not something to discover from a
    /// resonance that moved. Both counts, the conductor that made it a crossing, and the fact that
    /// the technology was not touched.</summary>
    [Fact]
    public void TheNoteNamesBothCountsAndTheConductorThatMadeItACrossing()
    {
        var shapes = Board();
        shapes.Add(Via(2, 3));
        shapes.Add(Via(4, 1));
        shapes.Add(Via(6.5, 3.5));

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);

        string note = Assert.Single(r.Notes, n => n.Contains("on the far side of the plane", StringComparison.Ordinal));
        _out.WriteLine(note);

        Assert.Contains("'Bottom Copper'", note, StringComparison.Ordinal);
        Assert.Contains("'GND plane'", note, StringComparison.Ordinal);
        Assert.Contains("2 via(s) on this entry stitch", note, StringComparison.Ordinal);
        Assert.Contains("1 pass through a void", note, StringComparison.Ordinal);
        Assert.Contains("which needs no change", note, StringComparison.Ordinal);

        // Short, for the warning's own reason — this one fires on EVERY board of this shape.
        Assert.True(note.Length <= 600, $"note is {note.Length} chars: {note}");

        // And it is the ONLY note about those vias. The BACKSIDE note says the same thing about a
        // via whose entry NAMES the plane; firing both put "these vias go to the plane" on screen
        // twice in a row, which is the noise that makes notes unread.
        Assert.DoesNotContain(r.Notes, n => n.Contains("BACKSIDE vias", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 5 — GVIA-2: the via that bypasses the plane and JOINS the two outer conductors
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"Landed in a void" and "joins the two outer conductors" are different sets, and only
    /// the second damages the answer: the structure continues through it into metal this run does
    /// not contain, and nothing in the s-parameters says a path ended early. It gets its OWN note
    /// rather than a clause in the paragraph about via counts.</summary>
    [Fact]
    public void AViaThatBypassesThePlaneAndJoinsBothOuterConductors_IsWarnedAboutByItself()
    {
        var shapes = Board();
        shapes.Add(Rect(BottomCopper, 6.2, 3.2, 6.8, 3.8));   // far-side copper under the void via
        shapes.Add(Via(2, 3));                                 // stitched
        shapes.Add(Via(6.5, 3.5));                             // bypasses, and lands on that copper

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);
        Assert.Single(r.Problem!.ViaList);                      // only the stitch is modelled

        // EM-SEV R-emsev-1: the CLASS, not a prefix in the prose. Narrowed to the via warning
        // because this board legitimately carries a SECOND one — the artwork it puts on 'Bottom
        // Copper' is not in the analysis levels, which R-emsev-1 also classes as a warning now.
        string warn = Assert.Single(r.Warnings, w => w.Contains("via(s) join", StringComparison.Ordinal));
        _out.WriteLine(warn);

        Assert.Contains("1 via(s) join", warn, StringComparison.Ordinal);
        Assert.Contains("'Top Copper'", warn, StringComparison.Ordinal);
        Assert.Contains("'Bottom Copper'", warn, StringComparison.Ordinal);
        Assert.Contains("'GND plane'", warn, StringComparison.Ordinal);

        // ── IT MUST STAY SHORT, and that is a requirement rather than a preference ────────────
        //
        // Owner, 2026-09-12: a designer does not read a paragraph, so a long warning warns nobody.
        // An absolute cap and a sentence count catch a paragraph creeping back in. The relative one
        // says what the rule IS rather than picking a number — the note that has to be ACTED on is
        // never the longest thing on screen — and it holds whatever layer names a board has.
        Assert.True(warn.Length <= 400, $"warning is {warn.Length} chars: {warn}");
        Assert.True(warn.Count(c => c == '.') <= 4, $"more than 4 sentences: {warn}");

        string companion = r.Notes.First(
            n => n.Contains("on the far side of the plane", StringComparison.Ordinal));
        Assert.True(warn.Length <= companion.Length,
                    $"warning {warn.Length} chars vs companion {companion.Length}");
    }

    /// <summary>The counterexample, and it is the reason the warning tests the ARTWORK on both sides
    /// rather than trusting the span. The stackup says this barrel lands on Bottom Copper; there is
    /// no copper there, so it carries nothing away and is a drilled hole, not a signal via. Warning
    /// on it would make the number the user is asked to act on mostly noise.</summary>
    [Fact]
    public void AViaThatBypassesThePlaneWithNoFarSideCopper_IsNotWarnedAbout()
    {
        var shapes = Board();
        shapes.Add(Via(2, 3));
        shapes.Add(Via(6.5, 3.5));    // bypasses the plane, but nothing on Bottom Copper under it

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);

        // Still reported as passing through — just not as carrying the structure away.
        Assert.Contains(r.Notes, n => n.Contains("1 pass through a void", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("via(s) join", StringComparison.Ordinal));
    }

    /// <summary>Nothing bypasses, so there is nothing to warn about. A warning that fires on a clean
    /// board is a warning people learn to skip.</summary>
    [Fact]
    public void WhenEveryViaStitches_ThereIsNoWarning()
    {
        var shapes = Board();
        shapes.Add(Rect(BottomCopper, 0, 0, 10, 6));   // far-side copper everywhere
        shapes.Add(Via(2, 3));
        shapes.Add(Via(4, 1));

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);
        // Two POINT vias are two PlanarVias — only drawn regions are grouped per entry.
        Assert.Equal(2, r.Problem!.ViaList.Count);
        Assert.All(r.Problem.ViaList, v => Assert.True(v.ToGround));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("via(s) join", StringComparison.Ordinal));
    }

    /// <summary>A drawn footprint is counted the same way — the accounting is one accounting, and a
    /// region via that bypasses the plane onto far-side copper is the same warning.</summary>
    [Fact]
    public void ADrawnFootprintThatBypassesOntoFarSideCopper_CountsToo()
    {
        var shapes = Board();
        shapes.Add(Rect(BottomCopper, 6.3, 3.3, 6.7, 3.7));
        shapes.Add(Rect(Drill, 1.8, 2.8, 2.2, 3.2));    // on the pour  -> stitched
        shapes.Add(Rect(Drill, 6.4, 3.4, 6.6, 3.6));    // in the void, onto far-side copper

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);

        Assert.Contains(r.Notes, n => n.Contains("1 via(s) join", StringComparison.Ordinal));
    }

    /// <summary>The same board analysed from the OTHER side. The stack flips, the far conductor is
    /// now the top one, and the identical vias stitch — the rule is about the plane lying between,
    /// not about which way up the technology lists it.</summary>
    [Fact]
    public void TheOtherSideOfThePlaneWorksTheSameWay()
    {
        var shapes = Board();
        shapes.Add(Rect(BottomCopper, 0, 0, 10, 6));
        shapes.Add(Via(2, 3));
        shapes.Add(Via(6.5, 3.5));

        var r = Extract(shapes, FourLayer(),
                        new EmExtractionSettings(AnalysisLevelNames: ["Bottom Copper"],
                                                 GroundStackupLayerName: "GND plane"));
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.True(via.ToGround);
        Assert.Contains(r.Notes, n => n.Contains("'Top Copper' on the far side of the plane",
                                                 StringComparison.Ordinal));
    }
}
