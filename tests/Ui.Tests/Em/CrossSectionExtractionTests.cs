// Tier E — cross-section extraction, headless, no document (brief-L6-L7-em-ui.md §7).
//
// The strongest test in the tier, and the reason R-em-1 makes the extractor framework-free: the
// starter technologies ARE the two stackups the kernel's own Tier 3 gate is built on, so extracting
// Pcb2Layer + one rectangle must reproduce, field for field, the EmProblem the engine is already
// validated against. That is checkable, not merely "reasonable".
//
// Expected values are RESTATED here rather than referenced from tests/Engine.Tests/Mom/Support/
// (the two test projects share no code — this repo's own convention). Each is annotated with the
// EmProblemBuilders call it mirrors so a future divergence is traceable.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class CrossSectionExtractionTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;   // 1000 DBU/µm ⇒ 1 DBU = 1 nm

    private static long Mm(double v) => (long)Math.Round(v * 1000.0 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static readonly LayerKey TopCopper    = new(1, 0);
    private static readonly LayerKey BottomCopper = new(2, 0);
    private static readonly LayerKey Drill        = new(7, 0);
    private static readonly LayerKey Metal1       = new(1, 0);
    private static readonly LayerKey Metal2       = new(2, 0);

    /// <summary>A rectangle running along +X: <paramref name="lengthDbu"/> long,
    /// <paramref name="widthDbu"/> wide, its lower-left corner at the origin.</summary>
    private static RectShape Line(LayerKey layer, long lengthDbu, long widthDbu, long x0 = 0, long y0 = 0)
        => new() { Layer = layer, X1 = x0, Y1 = y0, X2 = x0 + lengthDbu, Y2 = y0 + widthDbu };

    private static EmExtractionResult Extract(
        Technology tech, EmExtractionSettings? settings = null, params LayoutShape[] shapes)
        => CrossSectionExtractor.Extract(shapes, tech, Dbu, settings);

    private static void Near(double want, double got, double relTol, string what)
    {
        double scale = Math.Max(Math.Abs(want), 1e-30);
        Assert.True(Math.Abs(got - want) / scale <= relTol,
            $"{what}: got {got:G9}, want {want:G9}");
    }

    // ── The headline field-for-field comparisons ──────────────────────────────────────────────

    [Fact]
    public void Pcb2Layer_RectangleOnTopCopper_ReproducesTheKernelsOwnFr4Microstrip()
    {
        // Mirrors EmProblemBuilders.Fr4Microstrip(2.9e-3, tanD: 0.02, lengthMeters: 0.020)
        //   = Microstrip(w: 2.9e-3, h: 1.6e-3, t: 35e-6, epsR: 4.4, tanD: 0.02, sigma: 5.8e7).
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech, null, Line(TopCopper, Mm(20), Mm(2.9)));

        Assert.Null(r.Refusal);
        Assert.True(r.Ok);
        var p = r.Problem!;

        // One conductor, centred, 2.9 mm wide, spanning 1.600 → 1.635 mm, copper.
        var c = Assert.Single(p.Conductors);
        Near(5.8e7, c.SigmaSm, 1e-12, "strip σ");
        Assert.Equal(4, c.Outline.Count);
        Near(-1.45e-3, c.Outline[0].X, 1e-12, "x0");
        Near( 1.60e-3, c.Outline[0].Y, 1e-12, "z bottom");
        Near( 1.45e-3, c.Outline[1].X, 1e-12, "x1");
        Near( 1.45e-3, c.Outline[2].X, 1e-12, "x1 (top)");
        Near( 1.635e-3, c.Outline[2].Y, 1e-12, "z top");
        Near(-1.45e-3, c.Outline[3].X, 1e-12, "x0 (top)");

        // Two regions: FR-4 to −∞, air to +∞ — exactly the builder's own pair.
        Assert.Equal(2, p.Regions.Count);
        Assert.True(double.IsNegativeInfinity(p.Regions[0].YBottom));
        Near(1.6e-3, p.Regions[0].YTop, 1e-12, "substrate top");
        Near(4.4,  p.Regions[0].Material.EpsR, 1e-12, "εr");
        Near(0.02, p.Regions[0].Material.TanD, 1e-12, "tanδ");
        Near(1.6e-3, p.Regions[1].YBottom, 1e-12, "air bottom");
        Assert.True(double.IsPositiveInfinity(p.Regions[1].YTop));
        Assert.Equal(EmMaterial.Air, p.Regions[1].Material);

        // Ground at y = 0 — the TOP surface of Bottom Copper (R-em-4).
        Assert.NotNull(p.Ground);
        Assert.Equal(0.0, p.Ground!.Y);
        Near(5.8e7, p.Ground.SigmaSm, 1e-12, "ground σ");

        Assert.Equal(2, p.Ports.Count);
        Assert.All(p.Ports, port => Assert.Equal(c.Name, port.Conductor));
        Assert.All(p.Ports, port => Assert.Null(port.ReferenceConductor));
        Assert.All(p.Ports, port => Assert.Equal(new Complex(50, 0), port.Z0));

        Near(0.020, p.LengthMeters, 1e-12, "ℓ");
    }

    [Fact]
    public void MmicGaAs_RectangleOnMetal1_ReproducesTheKernelsOwnGaAsMicrostrip()
    {
        // Mirrors EmProblemBuilders.GaAsMicrostrip(160e-6, tanD: 0.0006, lengthMeters: 0.002)
        //   = Microstrip(w, h: 100e-6, t: 3e-6, epsR: 12.9, tanD: 0.0006, sigma: GoldSigma 4.1e7).
        // One deliberate difference: that builder leaves groundSigmaSm at its CopperSigma DEFAULT,
        // while the extractor reads the stackup's own Backside Metal (gold, 4.1e7) — the stackup's
        // value is the physically correct one, and it moves only Wheeler's conductor-loss term.
        var tech = StarterTechnologies.MmicGaAs();
        var r = Extract(tech, null, Line(Metal1, Mm(2), Um(160)));

        Assert.Null(r.Refusal);
        var p = r.Problem!;

        var c = Assert.Single(p.Conductors);
        Near(4.1e7, c.SigmaSm, 1e-12, "strip σ");
        Near(-80e-6, c.Outline[0].X, 1e-9, "x0");
        Near( 80e-6, c.Outline[1].X, 1e-9, "x1");
        Near(100e-6, c.Outline[0].Y, 1e-12, "z bottom");
        Near(103e-6, c.Outline[2].Y, 1e-12, "z top");

        Assert.Equal(2, p.Regions.Count);
        Assert.True(double.IsNegativeInfinity(p.Regions[0].YBottom));
        Near(100e-6, p.Regions[0].YTop, 1e-12, "substrate top");
        Near(12.9,   p.Regions[0].Material.EpsR, 1e-12, "εr");
        Near(0.0006, p.Regions[0].Material.TanD, 1e-12, "tanδ");
        Assert.Equal(EmMaterial.Air, p.Regions[1].Material);

        Assert.Equal(0.0, p.Ground!.Y);
        Near(4.1e7, p.Ground.SigmaSm, 1e-12, "ground σ (Backside Metal)");
        Near(0.002, p.LengthMeters, 1e-12, "ℓ");
    }

    [Fact]
    public void MmicGaAs_RectangleOnMetal2_CollapsesThreeAirBands_AndSitsAt106To109Um()
    {
        // R-em-4a's own table, Metal2 row: the explicit Air stackup layer, Metal1's empty band and
        // Metal2's own band are all εr = 1, so they merge into ONE air region. Two spurious extra
        // regions here would be the tell that the merge is missing.
        var tech = StarterTechnologies.MmicGaAs();
        var r = Extract(tech, null, Line(Metal2, Mm(2), Um(160)));

        Assert.Null(r.Refusal);
        var p = r.Problem!;

        Assert.Equal(2, p.Regions.Count);
        Near(100e-6, p.Regions[0].YTop, 1e-12, "substrate top");
        Near(100e-6, p.Regions[1].YBottom, 1e-12, "air bottom");
        Assert.True(double.IsPositiveInfinity(p.Regions[1].YTop));

        var c = Assert.Single(p.Conductors);
        Near(106e-6, c.Outline[0].Y, 1e-12, "z bottom");
        Near(109e-6, c.Outline[2].Y, 1e-12, "z top");
        Assert.Equal(0.0, p.Ground!.Y);
    }

    // ── R-em-4: the ground plane is the TOP of the ground conductor, not the boundary condition ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroundPlane_IsTheTopSurfaceOfTheGroundConductor_NotOneMetalThicknessLower(bool pcb)
    {
        // Taking Stackup.Bottom = Ground literally would put the plane a whole metal thickness low
        // (35 µm on FR-4 — a 2% error on h that looks perfectly plausible). The observable is that
        // the substrate region's top lands at exactly h, not h + t.
        var tech = pcb ? StarterTechnologies.Pcb2Layer() : StarterTechnologies.MmicGaAs();
        var layer = pcb ? TopCopper : Metal1;
        double h = pcb ? 1.6e-3 : 100e-6;
        double t = pcb ? 35e-6 : 3e-6;

        var r = Extract(tech, null, Line(layer, Mm(20), Mm(1)));
        Assert.Null(r.Refusal);

        Assert.Equal(0.0, r.Problem!.Ground!.Y);
        Near(h, r.Problem.Regions[0].YTop, 1e-12, "substrate top = h");
        Near(h, r.Problem.Conductors[0].Outline[0].Y, 1e-12, "strip sits on h");
        Assert.False(Math.Abs(r.Problem.Regions[0].YTop - (h + t)) < 1e-15,
            "substrate top must be h, not h + t — that is the literal-boundary-condition bug");
    }

    [Fact]
    public void ShapesOnTheGroundDesignatedConductorLayer_AreIgnoredAndReported()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech, null,
            Line(TopCopper, Mm(20), Mm(2.9)),
            Line(BottomCopper, Mm(30), Mm(30)));   // a ground pour

        Assert.Null(r.Refusal);
        Assert.Single(r.Problem!.Conductors);
        Assert.Contains(r.Notes, n => n.Contains("ground-designated", StringComparison.Ordinal)
                                   && n.Contains("Bottom Copper", StringComparison.Ordinal));
    }

    [Fact]
    public void ShapesOnAViaDrawingLayer_AreIgnoredAndReported()
    {
        // R-em-4c: a Via stackup layer is ignored — a uniform cross-section has no vias — and
        // ignoring it is REPORTED, not silent.
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech, null,
            Line(TopCopper, Mm(20), Mm(2.9)),
            new ViaShape { Layer = Drill, X = Mm(5), Y = Mm(1), PadSize = Um(600), DrillSize = Um(300) });

        Assert.Null(r.Refusal);
        Assert.Single(r.Problem!.Conductors);
        Assert.Contains(r.Notes, n => n.Contains("via", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ADielectricLayersOwnDrawingLayerBinding_DoesNotMakeItAConductor()
    {
        // MmicGaAs's GaAs stackup entry legitimately carries DrawingLayers (a substrate outline).
        // R-em-4c: that binding is for other purposes and must never produce a conductor.
        var tech = StarterTechnologies.MmicGaAs();
        var substrate = new LayerKey(7, 0);
        var r = Extract(tech, null,
            Line(Metal1, Mm(2), Um(160)),
            Line(substrate, Mm(3), Mm(3)));

        Assert.Null(r.Refusal);
        Assert.Single(r.Problem!.Conductors);
        Assert.Contains(r.Notes, n => n.Contains("DIELECTRIC", StringComparison.Ordinal));
    }

    // ── R-em-2: DBU → metres happens exactly once, here ───────────────────────────────────────

    [Theory]
    [InlineData(1000)]     // 1 nm
    [InlineData(100)]      // 10 nm
    [InlineData(10000)]    // 0.1 nm
    public void DbuToMetres_RoundTrips_AtSeveralResolutions(int dbuPerMicron)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        long len = (long)Math.Round(20_000.0 * dbuPerMicron);    // 20 mm
        long wid = (long)Math.Round( 2_900.0 * dbuPerMicron);    // 2.9 mm

        var shapes = new LayoutShape[] { Line(TopCopper, len, wid) };
        var r = CrossSectionExtractor.Extract(shapes, tech, dbuPerMicron, null);

        Assert.Null(r.Refusal);
        Near(0.020, r.Problem!.LengthMeters, 1e-12, "ℓ");
        Near(2.9e-3, r.Readback!.Conductors[0].WidthMeters, 1e-9, "W");
        // The stackup is in DBU too, so h must survive the same conversion.
        Near(1.6e-3, r.Problem.Regions[0].YTop, 1e-12, "h");
    }

    [Fact]
    public void ClockwiseAndCounterClockwiseRectangles_ExtractIdentically()
    {
        // The engine normalises winding, but the extractor must not depend on that.
        var tech = StarterTechnologies.Pcb2Layer();
        long[] ccw = [0, 0, Mm(20), 0, Mm(20), Mm(2.9), 0, Mm(2.9)];
        long[] cw  = [0, 0, 0, Mm(2.9), Mm(20), Mm(2.9), Mm(20), 0];

        var a = Extract(tech, null, new PolygonShape { Layer = TopCopper, Xy = ccw });
        var b = Extract(tech, null, new PolygonShape { Layer = TopCopper, Xy = cw });

        Assert.Null(a.Refusal);
        Assert.Null(b.Refusal);
        Assert.Equal(a.Problem!.Conductors[0].Outline, b.Problem!.Conductors[0].Outline);
        Assert.Equal(a.Problem.LengthMeters, b.Problem.LengthMeters);
        Assert.Equal(a.Problem.Regions, b.Problem.Regions);
    }

    [Fact]
    public void AStraightPathShape_ExtractsAsAUniformLineOfItsOwnWidth()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var path = new PathShape
        {
            Layer = TopCopper,
            Xy = [0, 0, Mm(20), 0],
            Width = Mm(2.9),
        };
        var r = Extract(tech, null, path);

        Assert.Null(r.Refusal);
        Near(2.9e-3, r.Readback!.Conductors[0].WidthMeters, 1e-9, "W");
        Near(0.020,  r.Problem!.LengthMeters, 1e-9, "ℓ");
    }

    // ── R-em-7: the propagation axis is DERIVED, never assumed ────────────────────────────────

    [Fact]
    public void PropagationAxis_FollowsTheGeometry_NotAHardcodedX()
    {
        var tech = StarterTechnologies.Pcb2Layer();

        var alongX = Extract(tech, null, Line(TopCopper, Mm(20), Mm(2.9)));
        var alongY = Extract(tech, null, new RectShape
        { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(2.9), Y2 = Mm(20) });

        Assert.Equal(EmPropagationAxis.X, alongX.Readback!.Axis);
        Assert.Equal(EmPropagationAxis.Y, alongY.Readback!.Axis);
        Assert.Equal(alongX.Problem!.LengthMeters, alongY.Problem!.LengthMeters);
        Near(alongX.Readback.Conductors[0].WidthMeters,
             alongY.Readback.Conductors[0].WidthMeters, 1e-12, "W is axis-independent");
    }

    // ── R-em-8: the readback is structured, and the panel recomputes nothing ──────────────────

    [Fact]
    public void Readback_CarriesEveryNumberThePanelShows_AndTheOneLineSummary()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech, null, Line(TopCopper, Mm(20), Mm(2.9)));
        var rb = r.Readback!;

        Assert.Equal("Top Copper (1 oz)", rb.SignalLayerName);
        Assert.Equal("Bottom Copper (1 oz)", rb.GroundLayerName);
        Near(2.9e-3, rb.Conductors[0].WidthMeters, 1e-9, "W");
        Near(35e-6,  rb.Conductors[0].ThicknessMeters, 1e-12, "t");
        Near(0.020,  rb.LengthMeters, 1e-12, "ℓ");
        Assert.Empty(rb.GapsMeters);
        Assert.Equal(2, rb.Regions.Count);
        Near(4.4, rb.Regions[0].EpsR, 1e-12, "εr in the readback");

        Assert.Contains("uniform 1-conductor cross-section", rb.Summary, StringComparison.Ordinal);
        Assert.Contains("W = 2.9 mm", rb.Summary, StringComparison.Ordinal);
        Assert.Contains("ℓ = 20 mm", rb.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoParallelLines_ReportGapAndBothWidths_AndLeaveTheL7bRefusalToTheKernel()
    {
        // The extractor's job is to produce the problem; refusing a coupled pair is the KERNEL's
        // (R-em-6: "the kernel owns the problem-level ones and must not be duplicated").
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech, null,
            Line(TopCopper, Mm(20), Mm(1), y0: 0),
            Line(TopCopper, Mm(20), Mm(1), y0: Mm(1.5)));

        Assert.Null(r.Refusal);
        Assert.Equal(2, r.Problem!.Conductors.Count);
        Near(0.5e-3, Assert.Single(r.Readback!.GapsMeters), 1e-9, "gap");

        // R-cpl-6 / D3 — 2N ports, numbered 2k−1 = conductor k's NEAR end, 2k its FAR end. Kernel A
        // built exactly two, both on conductors[0]; that was right when only one line could be
        // solved and silently wrong the moment a coupled pair could.
        Assert.Equal(4, r.Problem.Ports.Count);
        Assert.Equal([1, 2, 3, 4], r.Problem.Ports.Select(p => p.Number));
        Assert.Equal(r.Problem.Conductors[0].Name, r.Problem.Ports[0].Conductor);
        Assert.Equal(r.Problem.Conductors[0].Name, r.Problem.Ports[1].Conductor);
        Assert.Equal(r.Problem.Conductors[1].Name, r.Problem.Ports[2].Conductor);
        Assert.Equal(r.Problem.Conductors[1].Name, r.Problem.Ports[3].Conductor);

        // …and a SYMMETRIC pair is now accepted. The kernel still owns the verdict; L7b narrowed
        // what that verdict is (R-cpl-5), it did not move the decision to the extractor.
        var verdict = new QuasiStaticKernel().CanSolve(r.Problem);
        Assert.True(verdict.Ok, verdict.Reason);
    }
}
