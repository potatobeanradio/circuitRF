// Tier E — one test per row of R-em-6's refusal taxonomy, plus R-em-5's "a missing or nonsensical
// stackup value is a refusal, not a default".
//
// Every assertion checks the refusal is SPECIFIC — it names the feature, where it was found, and
// where the capability arrives — not merely that it is non-empty. That is the same bar the engine
// half's own R-mom-17 tests hold, and it is what makes the difference between v1 reading as bounded
// and reading as broken (§10.3.3).

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class ExtractionRefusalTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static long Mm(double v) => (long)Math.Round(v * 1000.0 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static readonly LayerKey TopCopper = new(1, 0);
    private static readonly LayerKey SilkTop   = new(5, 0);
    private static readonly LayerKey Drill     = new(7, 0);
    private static readonly LayerKey Metal1    = new(1, 0);
    private static readonly LayerKey Metal2    = new(2, 0);

    private static EmExtractionResult Extract(
        Technology tech, params LayoutShape[] shapes)
        => CrossSectionExtractor.Extract(shapes, tech, Dbu, null);

    private static string Refused(EmExtractionResult r)
    {
        Assert.False(r.Ok);
        Assert.NotNull(r.Refusal);
        return r.Refusal!;
    }

    /// <summary>A coordinate as the extractor prints it — in the technology's OWN display unit
    /// (mil for <c>Pcb2Layer</c>), which is what the user reads. Not circular: the thing under test
    /// is that the extractor names the RIGHT coordinate, and <c>LayoutUnits.Format</c> is a separate,
    /// already-gated primitive.</summary>
    private static string Coord(long dbu, LayoutUnit unit = LayoutUnit.Mil)
        => LayoutUnits.Format(dbu, unit, Dbu);

    private static void Mentions(string reason, params string[] fragments)
    {
        foreach (var f in fragments)
            Assert.True(reason.Contains(f, StringComparison.OrdinalIgnoreCase),
                $"refusal must mention \"{f}\".\nGot: {reason}");
    }

    // ── Row 1: a non-straight edge (arc, curve, polygon vertex turning) ───────────────────────

    [Fact]
    public void ABentTrace_NamesTheBendCoordinate_AndTheKernelThatDoesAnalyseIt()
    {
        // An L: 20 mm along +X, then 20 mm along +Y, 2.9 mm wide. The inner corner is at
        // (17.1 mm, 2.9 mm) — the coordinate a user recognises as "the bend".
        var tech = StarterTechnologies.Pcb2Layer();
        long w = Mm(2.9), l = Mm(20);
        var ell = new PolygonShape
        {
            Layer = TopCopper,
            Xy = [0, 0, l, 0, l, l, l - w, l, l - w, w, 0, w],
        };

        var reason = Refused(Extract(tech, ell));
        // L8e/D6: this refusal was TRUE-as-a-promise and is now TRUE-as-a-capability — kernel B
        // exists, so the message names it and how to reach it instead of naming a phase.
        Mentions(reason, "bend", "uniform cross-sections only", "planar kernel (B)", "Auto");
        // The INNER corner — the one a user recognises as the bend — at (17.1 mm, 2.9 mm).
        Assert.Contains(Coord(l - w), reason, StringComparison.Ordinal);
        Assert.Contains(Coord(w),     reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArcBearingCurve_NamesTheCurvedEdgeAndItsStart()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var curve = new CurveShape
        {
            Layer = TopCopper,
            Xy = [0, 0, Mm(20), 0, Mm(20), Mm(2.9), 0, Mm(2.9)],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var reason = Refused(Extract(tech, curve));
        Mentions(reason, "arc", "uniform cross-sections only", "planar kernel (B)", "Auto");
        Assert.Contains(Coord(Mm(20)), reason, StringComparison.Ordinal);   // the arc edge's start
    }

    [Fact]
    public void ACircle_IsRefusedByName_NotSilentlyFlattenedIntoAPolygon()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var reason = Refused(Extract(tech,
            new CircleShape { Layer = TopCopper, Cx = Mm(5), Cy = Mm(5), R = Mm(1) }));

        Mentions(reason, "circle", "uniform cross-sections only", "planar kernel (B)", "Auto");
    }

    // ── Row 2: two conductors not mutually parallel ───────────────────────────────────────────

    [Fact]
    public void TwoNonParallelConductors_NameBothDirectionsAndTheAngleBetweenThem()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var alongX = new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) };
        var alongY = new RectShape { Layer = TopCopper, X1 = Mm(30), Y1 = 0, X2 = Mm(32.9), Y2 = Mm(20) };

        var reason = Refused(Extract(tech, alongX, alongY));
        Mentions(reason, "not parallel", "90", "planar kernel (B)", "Auto");
        Assert.Contains("0", reason, StringComparison.Ordinal);
    }

    // ── Row 3: width varying along the run (a taper) ──────────────────────────────────────────

    [Fact]
    public void ATaper_NamesWhereTheWidthChanges_AndFromWhatToWhat()
    {
        // 20 mm long, 1 mm wide at x = 0, 3 mm wide at x = 20 mm.
        var tech = StarterTechnologies.Pcb2Layer();
        var taper = new PolygonShape
        {
            Layer = TopCopper,
            Xy = [0, 0, Mm(20), Mm(-1), Mm(20), Mm(2), 0, Mm(1)],
        };

        var reason = Refused(Extract(tech, taper));
        Mentions(reason, "taper", "width changes", "planar kernel (B)", "MicrostripTaperModel");
        Assert.Contains("1", reason, StringComparison.Ordinal);
        Assert.Contains("3", reason, StringComparison.Ordinal);
    }

    // ── Row 4: shapes on a layer bound to no stackup conductor layer ──────────────────────────

    [Fact]
    public void GeometryOnAnUnboundLayer_NamesTheLayerAndPointsAtTheDrawingLayersBinding()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var reason = Refused(Extract(tech,
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) },
            new RectShape { Layer = SilkTop,   X1 = 0, Y1 = Mm(5), X2 = Mm(3), Y2 = Mm(6) }));

        Mentions(reason, "Silk Top", "5/0", "DrawingLayers", "conductor");
    }

    // ── Row 5: shapes on two or more SIGNAL conductor stackup layers ──────────────────────────

    /// <summary>
    /// L9e/M4 — UPDATED, NOT LOOSENED. This used to require the refusal to say "L9". L9 arrived and
    /// BUILT multi-level stacks with z-directed current, so the phase number became a promise about
    /// a schedule that had already been kept — the refusal must now name kernel B and how to reach
    /// it. What is still asserted is everything that mattered: both levels named, and the reason.
    /// </summary>
    [Fact]
    public void GeometryOnTwoSignalConductorLevels_NamesBoth_AndPointsAtKernelB()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var reason = Refused(Extract(tech,
            new RectShape { Layer = Metal1, X1 = 0,       Y1 = 0, X2 = Mm(2), Y2 = Um(160) },
            new RectShape { Layer = Metal2, X1 = Um(500), Y1 = 0, X2 = Mm(2), Y2 = Um(160) }));

        Mentions(reason, "Metal1", "Metal2", "z-directed", "Planar");
        Assert.DoesNotContain("at L9", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroundDesignatedLevelNeverCountsAsASecondSignalLevel()
    {
        // R-em-4 already ignores the ground-designated conductor, so a ground pour plus a line is
        // one signal level, not two — this must extract, not refuse.
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech,
            new RectShape { Layer = TopCopper,    X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) },
            new RectShape { Layer = new(2, 0),    X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(30) });

        Assert.Null(r.Refusal);
    }

    // ── Row 6: Stackup.Top == Ground (stripline) ──────────────────────────────────────────────

    [Fact]
    public void AStacupClosedAtTheTop_IsRefusedAsStripline_NamingTheImageSeries()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        tech.Stackup.Top = BoundaryCondition.Ground;

        var reason = Refused(Extract(tech,
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) }));

        Mentions(reason, "stripline", "image", "series", "not yet built");
    }

    // ── Row 7: zero extent along the propagation axis ─────────────────────────────────────────

    [Fact]
    public void AZeroLengthConductor_SaysLengthMustBePositive()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var reason = Refused(Extract(tech,
            new RectShape { Layer = TopCopper, X1 = Mm(5), Y1 = 0, X2 = Mm(5), Y2 = Mm(2.9) }));

        Mentions(reason, "ℓ must be positive");
    }

    // ── Row 8: no shapes on any bound conductor layer ─────────────────────────────────────────

    [Fact]
    public void NothingOnAConductorLayer_SaysWhatItIsPointedAtAndWhatItFound()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var settings = new EmExtractionSettings(SubjectDescription: "Amp/layout/Amp.clay");
        var r = CrossSectionExtractor.Extract(
            [new ViaShape { Layer = Drill, X = 0, Y = 0, PadSize = Um(600), DrillSize = Um(300) }],
            tech, Dbu, settings);

        var reason = Refused(r);
        Mentions(reason, "Amp/layout/Amp.clay", "conductor", "PCB 2-Layer");
    }

    [Fact]
    public void AnEmptyLayout_SaysItHoldsNoGeometryAtAll()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var reason = Refused(CrossSectionExtractor.Extract([], tech, Dbu, null));
        Mentions(reason, "no drawn geometry", "conductor");
    }

    // ── R-em-5: a missing or nonsensical stackup value is a refusal, not a default ────────────

    [Fact]
    public void ADielectricWithZeroEpsr_NamesTheLayerAndWhatToSet()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        tech.Stackup.Layers.First(l => l.Name == "FR-4").Epsr = 0;

        var reason = Refused(Extract(tech,
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) }));

        Mentions(reason, "FR-4", "εr", "Stackup");
    }

    [Fact]
    public void ASignalConductorWithZeroSigma_NamesTheLayerAndAPlausibleValue()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        tech.Stackup.Layers.First(l => l.Name.StartsWith("Top Copper", StringComparison.Ordinal)).SigmaSm = 0;

        var reason = Refused(Extract(tech,
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) }));

        Mentions(reason, "Top Copper", "σ", "5.8e7");
    }

    [Fact]
    public void ALayerWithZeroThickness_IsRefused_BecauseEveryHeightAboveItWouldBeWrong()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        tech.Stackup.Layers.First(l => l.Name == "FR-4").ThicknessDbu = 0;

        var reason = Refused(Extract(tech,
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) }));

        Mentions(reason, "FR-4", "thickness", "Stackup");
    }

    [Fact]
    public void ATechnologyWithNoStackupAtAll_SaysSoRatherThanGuessing()
    {
        var tech = StarterTechnologies.Empty();
        var reason = Refused(Extract(tech,
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) }));

        Mentions(reason, "no stackup", "technology editor");
    }

    [Fact]
    public void NamingASignalLayerThatDoesNotExist_ListsTheOnesThatDo()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var r = CrossSectionExtractor.Extract(
            [new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) }],
            tech, Dbu, new EmExtractionSettings(SignalStackupLayerName: "Metal7"));

        var reason = Refused(r);
        Mentions(reason, "Metal7", "Top Copper", "Bottom Copper");
    }
}
