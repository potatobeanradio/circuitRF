// L8e Tier 1 — D3/R-res-4/R-res-5: ports from the layout's own IsPort labels.
//
// Numbering, side inference, the ambiguous refusal, and a .clay round-trip with ports.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmPortExtractionTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    /// <summary>§10.7's own hero footprint: a 2.9 × 20 mm line on the PCB starter's Top Copper.</summary>
    private static RectShape Line() =>
        new() { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) };

    private static LabelShape Port(string text, double xMm, double yMm) =>
        new() { Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = text, Height = Mm(0.5), IsPort = true };

    private static PlanarProblem Problem(params LayoutShape[] shapes)
    {
        var r = PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }

    private static EmPortExtractionResult Extract(params LayoutShape[] shapes)
        => EmPortExtraction.Extract(shapes, Problem(shapes), Dbu);

    // ── Numbering ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1",      1)]
    [InlineData("P1",     1)]
    [InlineData("p2",     2)]
    [InlineData("#3",     3)]
    [InlineData("Port 4", 4)]
    [InlineData("port5",  5)]
    public void ALabelNamingANumber_KeepsIt(string text, int expected)
    {
        Assert.True(EmPortExtraction.TryParseNumber(text, out int n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ALabelNamingSomethingElse_IsAutoNumberedRatherThanRefused(string text)
        => Assert.False(EmPortExtraction.TryParseNumber(text, out _));

    [Fact]
    public void AnUnnumberedLabel_TakesTheLowestFreeNumber()
    {
        var r = Extract(Line(), Port("P2", 0, 1.45), Port("gate", 20, 1.45));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal([1, 2], r.Ports.Select(p => p.Number).Order());
    }

    [Fact]
    public void TwoLabelsNamingTheSameNumber_AreRefusedByName()
    {
        var r = Extract(Line(), Port("P1", 0, 1.45), Port("P1", 20, 1.45));

        Assert.False(r.Ok);
        Assert.Contains("both name port 1", r.Refusal!, StringComparison.Ordinal);
    }

    // ── R-res-5: the side is inferred, reported, and refused when ambiguous ───────────────────

    [Fact]
    public void TheSideIsInferredFromTheNearestConductorBoundary()
    {
        var r = Extract(Line(), Port("P1", 0, 1.45), Port("P2", 20, 1.45));

        Assert.True(r.Ok, r.Refusal);
        var byNumber = r.Ports.ToDictionary(p => p.Number);
        Assert.Equal(PlanarPortSide.MinX, byNumber[1].Side);
        Assert.Equal(PlanarPortSide.MaxX, byNumber[2].Side);
    }

    /// <summary>An x-directed line rotated 90° is a y-directed one; the inference must follow the
    /// geometry, not an assumption about which axis a line runs along.</summary>
    [Fact]
    public void AYDirectedLine_InfersTheYSides()
    {
        var vertical = new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(2.9), Y2 = Mm(20) };
        var shapes = new LayoutShape[] { vertical, Port("P1", 1.45, 0), Port("P2", 1.45, 20) };
        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu);

        Assert.True(r.Ok, r.Refusal);
        var byNumber = r.Ports.ToDictionary(p => p.Number);
        Assert.Equal(PlanarPortSide.MinY, byNumber[1].Side);
        Assert.Equal(PlanarPortSide.MaxY, byNumber[2].Side);
    }

    [Fact]
    public void TheInference_IsREPORTED_NotSilent()
    {
        var r = Extract(Line(), Port("P1", 0, 1.45), Port("P2", 20, 1.45));

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n =>
            n.Contains("Port 1", StringComparison.Ordinal) &&
            n.Contains("low-x", StringComparison.Ordinal) &&
            n.Contains("inferred", StringComparison.Ordinal));
        // …and which way current flows in, because that is the thing a wrong side gets wrong.
        Assert.Contains(r.Notes, n => n.Contains("+x direction", StringComparison.Ordinal));
    }

    /// <summary>
    /// The headline R-res-5 case: a label at the exact CORNER is equally close to two edges, so which
    /// end of the conductor it names has no answer. Guessing reverses the direction of current into
    /// the structure — a hard π in S₂₁, smooth and plausible and invisible in a magnitude plot.
    /// </summary>
    [Fact]
    public void AnAmbiguousPort_IsRefusedByName_NeverGuessed()
    {
        var r = Extract(Line(), Port("P1", 0, 0), Port("P2", 20, 1.45));

        Assert.False(r.Ok);
        Assert.Contains("Port 1", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("ambiguous", r.Refusal!, StringComparison.Ordinal);
        // R-mom-17's shape: name the feature, name what to do about it. Since 2026-08-09 there are
        // TWO remedies — a port carries its own direction now, so ROTATING it is the direct answer
        // and moving the label off the corner is the other. Both must be named.
        Assert.Contains("Rotate the port", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("move the label", r.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The regression for the defect L8's own phase gate found</b>, pinned cheaply here so nobody
    /// has to run a two-minute full-wave solve to catch it again.
    ///
    /// <para>An L-shaped bend's bounding box is arm × arm. When the ambiguity threshold was a fraction
    /// of that box's smaller dimension, the MMIC bend's threshold was 5% of 0.995 mm = 49.8 µm — and a
    /// port at the exact centre of the 72 µm-wide line end is 36 µm from the flanking edge, so it was
    /// refused as "sits about equally close to the low-x and low-y edges". The port was correct; the
    /// scale was not.</para>
    /// </summary>
    [Fact]
    public void APortAtTheCentreOfANarrowEndOfAnLBend_IsNotAmbiguous()
    {
        // The MMIC bend, to scale: 72 µm of metal, arms just under a millimetre.
        const double w = 0.072, armMm = 0.995;
        var bend = new PolygonShape
        {
            Layer = TopCopper,
            Xy =
            [
                0,             0,
                Mm(armMm),     0,
                Mm(armMm),     Mm(armMm),
                Mm(armMm - w), Mm(armMm),
                Mm(armMm - w), Mm(w),
                0,             Mm(w),
            ],
        };
        var shapes = new LayoutShape[]
        {
            bend,
            Port("P1", 0,               0.5 * w),
            Port("P2", armMm - 0.5 * w, armMm),
        };

        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu);

        Assert.True(r.Ok, r.Refusal);
        var byNumber = r.Ports.ToDictionary(p => p.Number);
        Assert.Equal(PlanarPortSide.MinX, byNumber[1].Side);
        Assert.Equal(PlanarPortSide.MaxY, byNumber[2].Side);
    }

    /// <summary>…and the corner of that same bend is still refused, so the fix narrowed the rule
    /// rather than removing it.</summary>
    [Fact]
    public void APortAtTheCornerOfThatSameLBend_IsSTILLRefused()
    {
        const double w = 0.072, armMm = 0.995;
        var bend = new PolygonShape
        {
            Layer = TopCopper,
            Xy =
            [
                0, 0, Mm(armMm), 0, Mm(armMm), Mm(armMm),
                Mm(armMm - w), Mm(armMm), Mm(armMm - w), Mm(w), 0, Mm(w),
            ],
        };
        var shapes = new LayoutShape[]
        {
            bend, Port("P1", 0, 0), Port("P2", armMm - 0.5 * w, armMm),
        };

        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu);

        Assert.False(r.Ok);
        Assert.Contains("Port 1", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("ambiguous", r.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void APortLabelOffTheMetal_IsRefusedByName()
    {
        var r = Extract(Line(), Port("P1", 60, 40), Port("P2", 20, 1.45));

        Assert.False(r.Ok);
        Assert.Contains("Port 1", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("not on any conductor", r.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void ALayoutWithNoPortLabels_IsRefusedWithAPointerAtThePortTool()
    {
        var r = Extract(Line());

        Assert.False(r.Ok);
        Assert.Contains("Port tool", r.Refusal!, StringComparison.Ordinal);
    }

    // ── A port's ambiguity is between LEVELS, and levels are what gets counted ────────────────
    //
    // Owner report, 2026-09-17: a `.clay` with the same taper drawn twice, one exactly over the
    // other, was refused with "sits on metal on 2 of this EM setup's 1 conductor levels" — a
    // sentence that cannot be true. The count was of POLYGONS. Metal over metal on one level is
    // manufacturable artwork and meshes as one conductor (the mesher rasterises a level's polygons
    // into cell occupancy: measured identical, 297 cells and 552 bases, with one copy and with two).

    [Fact]
    public void MetalDrawnTwiceOnOneLevel_ResolvesRatherThanBeingCalledTwoLevels()
    {
        var r = Extract(Line(), Line(), Port("P1", 0, 1.45), Port("P2", 20, 1.45));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal([1, 2], r.Ports.Select(p => p.Number).Order());
    }

    [Fact]
    public void MetalOnTwoLevelsUnderOnePort_IsStillRefused_AndNamesThoseLevels()
    {
        var metal1 = new LayerKey(1, 0);
        var metal2 = new LayerKey(2, 0);
        long len = 200 * Dbu, wide = 30 * Dbu;   // 200 x 30 µm, the scale this stackup is drawn at

        LayoutShape[] shapes =
        [
            new RectShape { Layer = metal1, X1 = 0, Y1 = 0, X2 = len, Y2 = wide },
            new RectShape { Layer = metal2, X1 = 0, Y1 = 0, X2 = len, Y2 = wide },
            new LabelShape { Layer = metal1, X = 0, Y = wide / 2, Text = "P1", Height = 5 * Dbu, IsPort = true },
            new LabelShape { Layer = metal1, X = len, Y = wide / 2, Text = "P2", Height = 5 * Dbu, IsPort = true },
        ];

        var x = PlanarExtractor.Extract(shapes, StarterTechnologies.MmicGaAs(), Dbu, 20e9);
        Assert.True(x.Ok, x.Refusal);
        Assert.Equal(2, x.Problem!.Layers.Count);

        var r = EmPortExtraction.Extract(shapes, x.Problem!, Dbu);

        Assert.False(r.Ok);
        Assert.Contains("2 of this EM setup's 2 conductor levels", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("'Metal1'", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("'Metal2'", r.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows report, 2026-09-26: a two-layer board's 3D view was empty, refused with "Port 3 … sits on
    /// metal on 2 of this EM setup's 2 conductor levels". A port whose OWN layer names one of the levels
    /// under it — its committed PortLayer, else the label's drawing layer — is on that level, so it is
    /// not ambiguous. One that names neither is still refused, and a 3D setup's refusal offers no
    /// analysis levels, which it does not have.
    /// </summary>
    [Fact]
    public void MetalOnTwoLevelsUnderOnePort_ResolvesToTheLevelThePortsOwnLayerNames()
    {
        var metal1 = new LayerKey(1, 0);
        var metal2 = new LayerKey(2, 0);
        var text   = new LayerKey(63, 0);
        long len = 200 * Dbu, wide = 30 * Dbu;
        var tech = StarterTechnologies.MmicGaAs();
        LayoutShape[] Shapes(LabelShape p1, LabelShape p2) =>
        [
            new RectShape { Layer = metal1, X1 = 0, Y1 = 0, X2 = len, Y2 = wide },
            new RectShape { Layer = metal2, X1 = 0, Y1 = 0, X2 = len, Y2 = wide },
            p1, p2,
        ];
        LabelShape Label(string t, long x, LayerKey layer, LayerKey? committed = null) =>
            new() { Layer = layer, PortLayer = committed, X = x, Y = wide / 2, Text = t, Height = 5 * Dbu, IsPort = true };

        // The label's drawing layer is Metal1's.
        var onLayer = Shapes(Label("P1", 0, metal1), Label("P2", len, metal1));
        var problem = PlanarExtractor.Extract(onLayer, tech, Dbu, 20e9).Problem!;
        int m1 = problem.Layers.ToList().FindIndex(l => l.Name == "Metal1");
        int m2 = problem.Layers.ToList().FindIndex(l => l.Name == "Metal2");
        var r = EmPortExtraction.Extract(onLayer, problem, Dbu, technology: tech);
        Assert.True(r.Ok, r.Refusal);
        Assert.All(r.Ports, p => Assert.Equal(m1, p.LayerIndex ?? 0));

        // A committed PortLayer wins over the label's own layer (a text layer here, which names nothing).
        var committed = Shapes(Label("P1", 0, text, metal2), Label("P2", len, text, metal2));
        r = EmPortExtraction.Extract(committed, problem, Dbu, technology: tech);
        Assert.True(r.Ok, r.Refusal);
        Assert.All(r.Ports, p => Assert.Equal(m2, p.LayerIndex ?? 0));

        // Neither names a level: still ambiguous, and a 3D setup is not told to narrow analysis levels.
        var silent = Shapes(Label("P1", 0, text), Label("P2", len, text));
        r = EmPortExtraction.Extract(silent, problem, Dbu, technology: tech, analysisLevelsApply: false);
        Assert.False(r.Ok);
        Assert.Contains("2 of this EM setup's 2 conductor levels", r.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("analysis levels", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("drawing layer", r.Refusal!, StringComparison.Ordinal);
    }

    // ── The impedance lives in the .cem, never on the shape ───────────────────────────────────

    [Fact]
    public void TheReferenceImpedance_ComesFromTheSetup_NotFromTheLabel()
    {
        var shapes = new LayoutShape[] { Line(), Port("P1", 0, 1.45), Port("P2", 20, 1.45) };
        var setup  = new EmSetup { PortZ0s = [new Complex(75, 0), new Complex(25, -3)] };

        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu, setup.ResolvePortZ0);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(new Complex(75, 0),  r.Ports[0].Z0);
        Assert.Equal(new Complex(25, -3), r.Ports[1].Z0);
    }

    // ── R-res-4: no new shape type, no .clay schema change ────────────────────────────────────

    [Fact]
    public void AClayWithPortLabels_RoundTripsByteIdentically()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Line());
        view.Shapes.Add(Port("P1", 0, 1.45));
        view.Shapes.Add(Port("P2", 20, 1.45));

        string once  = LayoutPersistence.Serialize(view);
        var reloaded = LayoutPersistence.Deserialize(once);
        string twice = LayoutPersistence.Serialize(reloaded);

        Assert.Equal(once, twice);
        Assert.Equal(2, reloaded.Shapes.OfType<LabelShape>().Count(l => l.IsPort));
    }

    /// <summary>D3's own claim, asserted rather than assumed: a port is an ORDINARY label and the
    /// port flag is the only thing that distinguishes it.</summary>
    [Fact]
    public void APortIsALabelShape_NotANewShapeType()
    {
        var r = Extract(Line(), Port("P1", 0, 1.45), Port("P2", 20, 1.45));
        Assert.True(r.Ok, r.Refusal);

        // A label with the flag CLEARED is artwork annotation and contributes no port.
        var plain = Port("P1", 0, 1.45);
        plain.IsPort = false;
        var shapes = new LayoutShape[] { Line(), plain, Port("P2", 20, 1.45) };

        var r2 = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu);
        Assert.True(r2.Ok, r2.Refusal);
        Assert.Single(r2.Ports);
    }

    // ── The Port tool's own auto-numbering shares the extractor's parser ──────────────────────

    [Fact]
    public void ThePortTool_AutoNumbersFromTheExistingLabels()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        var vm   = new LayoutEditorViewModel(view);

        Assert.Equal("P1", vm.NextPortName());

        view.Shapes.Add(Port("P1", 0, 1.45));
        Assert.Equal("P2", vm.NextPortName());

        view.Shapes.Add(Port("P3", 10, 1.45));
        Assert.Equal("P2", vm.NextPortName());   // the LOWEST free, not the next after the highest
    }

    [Fact]
    public void ThePortTool_PlacesALabelWithTheFlagSet_AsOneUndoEntry()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        // The Port tool refuses a click that is not on metal (owner report, 2026-08-09), so the
        // conductor has to be here for this test to be about what it says it is about.
        view.Shapes.Add(Line());
        var vm   = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Port };

        vm.OnPointerPressed(Mm(0), Mm(1.45), default);

        var label = Assert.IsType<LabelShape>(view.Shapes.OfType<LabelShape>().Single());
        Assert.True(label.IsPort);
        Assert.Equal("P1", label.Text);

        vm.UndoRedo.Undo();
        Assert.Empty(view.Shapes.OfType<LabelShape>());
    }
}
