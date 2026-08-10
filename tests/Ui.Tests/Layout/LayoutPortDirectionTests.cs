// Owner report, 2026-08-09: "The Port button doesn't seem to do anything. It appears to simply be a
// Label when I inspect it using the Properties Inspector. An EM port should render indicating
// direction and how wide the port is. User should be able to change the Port's direction by rotating
// it (or using context menu Rotate command)."
//
// A port IS a LabelShape with IsPort set (L8e/D3 — there is deliberately no PortShape). What was
// missing was that its DIRECTION existed nowhere on the shape: it was inferred at extraction time and
// nowhere else, so nothing drew it, nothing named it, and there was nothing to rotate.
//
// These gates cover the whole arc: the direction↔side convention, seeding it at placement, advancing
// it with Rotate, honouring it at extraction, rendering it, persisting it, and renumbering a pasted
// port so a copy cannot silently break the s-parameter matrix.

using Avalonia.Input;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortDirectionTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    /// <summary>§10.7's own hero footprint: a 2.9 × 20 mm line on the PCB starter's Top Copper.</summary>
    private static RectShape Line() =>
        new() { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) };

    private static LabelShape Port(string text, double xMm, double yMm, LayoutRotation? dir = null) =>
        new() { Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = text, Height = Mm(0.5), IsPort = true, PortDirection = dir };

    /// <summary>Selects the port at the low-x end by clicking it. A label's stored bbox is a POINT,
    /// so the hit-test's ascending-area tie-break puts it above the conductor it sits on.</summary>
    private static void SelectPort(LayoutEditorViewModel vm) =>
        vm.OnPointerPressed(Mm(0), Mm(1.45), KeyModifiers.None, 1, Mm(0.05));

    // ── The convention ────────────────────────────────────────────────────────────────────────
    // R0 = +x̂, R90 = +ŷ, R180 = −x̂, R270 = −ŷ, and a port whose current flows +x̂ sits on the
    // conductor's LOW-x end. Both halves are inversions that are smooth, plausible and wrong when
    // flipped — a wrong side is a hard π in S₂₁ that no magnitude plot can show.

    [Theory]
    [InlineData(LayoutRotation.R0,   1,  0)]
    [InlineData(LayoutRotation.R90,  0,  1)]
    [InlineData(LayoutRotation.R180, -1, 0)]
    [InlineData(LayoutRotation.R270, 0, -1)]
    public void TheDirectionVector_IsCounterClockwiseFromPlusX(LayoutRotation r, int ux, int uy)
        => Assert.Equal((ux, uy), LayoutPortDirection.UnitVector(r));

    [Theory]
    [InlineData(LayoutRotation.R0,   PlanarPortSide.MinX)]
    [InlineData(LayoutRotation.R90,  PlanarPortSide.MinY)]
    [InlineData(LayoutRotation.R180, PlanarPortSide.MaxX)]
    [InlineData(LayoutRotation.R270, PlanarPortSide.MaxY)]
    public void TheStatedDirection_IsTheINVERSEOfTheSideItNames(LayoutRotation dir, PlanarPortSide side)
    {
        Assert.Equal(side, EmPortExtraction.SideFromDirection(dir));

        // …and it agrees with PlanarPortSide's own documented current direction, so the two
        // statements of the same fact cannot drift apart.
        var (ux, uy) = LayoutPortDirection.UnitVector(dir);
        var port = new PlanarPort(1, new EmPoint(0, 0), side, new System.Numerics.Complex(50, 0));
        bool alongX = port.Direction == PlanarBasisDirection.X;
        Assert.Equal(alongX, ux != 0);
        Assert.Equal(port.IncidenceSign, alongX ? ux : uy);
    }

    [Theory]
    [InlineData(0.0,  1.45, LayoutRotation.R0)]     // low-x end   -> current flows +x̂
    [InlineData(20.0, 1.45, LayoutRotation.R180)]   // high-x end  -> current flows −x̂
    [InlineData(10.0, 0.0,  LayoutRotation.R90)]    // low-y edge  -> current flows +ŷ
    [InlineData(10.0, 2.9,  LayoutRotation.R270)]   // high-y edge -> current flows −ŷ
    public void TheNearestConductorSide_DecidesTheInferredDirection(double xMm, double yMm, LayoutRotation expected)
    {
        var bb = LayoutGeometry.BboxOf(Line());
        Assert.Equal(expected, LayoutPortDirection.FromBbox(bb, Mm(xMm), Mm(yMm)));
    }

    [Fact]
    public void TheWidth_IsTheConductorsExtentACROSSTheDirection()
    {
        var bb = LayoutGeometry.BboxOf(Line());
        Assert.Equal(Mm(2.9), LayoutPortDirection.WidthAcross(bb, LayoutRotation.R0));
        Assert.Equal(Mm(2.9), LayoutPortDirection.WidthAcross(bb, LayoutRotation.R180));
        Assert.Equal(Mm(20),  LayoutPortDirection.WidthAcross(bb, LayoutRotation.R90));
    }

    // ── Placement seeds it; no artwork seeds nothing ──────────────────────────────────────────

    [Fact]
    public void ThePortTool_SeedsTheDirectionFromTheArtworkUnderTheClick()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(Line());
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Port };

        vm.OnPointerPressed(Mm(0), Mm(1.45), default);

        var port = view.Shapes.OfType<LabelShape>().Single(l => l.IsPort);
        Assert.Equal(LayoutRotation.R0, port.PortDirection);
    }

    [Fact]
    public void APortPlacedOffTheMetal_IsRefusedByName_AndNothingIsCreated()
    {
        // UPDATED, not loosened: this test used to assert that a port placed on bare dielectric was
        // created with a null direction ("infer it later"). Owner report, 2026-08-09 — "in place-port
        // mode, when I clicked away from the metal, a port was created" — makes that deliberately
        // false. A port names the END OF A CONDUCTOR; off the metal there is nothing to name, so the
        // click is refused at the moment it is made rather than accepted here and refused much later,
        // at Simulate, by EmPortExtraction. (A null PortDirection on an EXISTING .clay still means
        // "infer it" — see APreExistingPortWithNoDirection_... below; only PLACEMENT changed.)
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(view, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Port };

        vm.OnPointerPressed(Mm(50), Mm(50), default);

        Assert.Empty(view.Shapes);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning
                                       && m.Text.Contains("conductor", StringComparison.OrdinalIgnoreCase));
    }

    // ── Rotate advances the DIRECTION and leaves the text upright ─────────────────────────────

    [Fact]
    public void RotatingAPort_AdvancesItsDirection_AndLeavesItsTextRotationAlone()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(Line());
        view.Shapes.Add(Port("P1", 0, 1.45, LayoutRotation.R0));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        SelectPort(vm);

        vm.RotateSelection(clockwise: false);
        var port = (LabelShape)view.Shapes[1];
        Assert.Equal(LayoutRotation.R90, port.PortDirection);
        Assert.Equal(LayoutRotation.R0, port.Rotation);   // the GLYPH stays upright

        vm.RotateSelection(clockwise: true);
        Assert.Equal(LayoutRotation.R0, ((LabelShape)view.Shapes[1]).PortDirection);
    }

    [Fact]
    public void RotatingAPortWithNoDirectionYet_AdoptsTheInferredOneAndAdvancesFromIt()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(Line());
        view.Shapes.Add(Port("P1", 0, 1.45));             // null -> infer: R0 at the low-x end
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        SelectPort(vm);

        vm.RotateSelection(clockwise: false);
        Assert.Equal(LayoutRotation.R90, ((LabelShape)view.Shapes[1]).PortDirection);
    }

    [Fact]
    public void RotatingAnOrdinaryLabel_StillRotatesItsTextAndNotAPortDirection()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new LabelShape { Layer = TopCopper, X = 0, Y = 0, Text = "note", Height = Mm(0.5) });
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.RotateSelection(clockwise: false);
        var label = (LabelShape)view.Shapes[0];
        Assert.Equal(LayoutRotation.R90, label.Rotation);
        Assert.Null(label.PortDirection);
    }

    // ── Extraction honours it ─────────────────────────────────────────────────────────────────

    private static EmPortExtractionResult Extract(params LayoutShape[] shapes)
    {
        var r = PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);
        return EmPortExtraction.Extract(shapes, r.Problem!, Dbu);
    }

    [Fact]
    public void AStatedDirection_IsUsedVerbatim_EvenWhereInferenceWouldHaveChosenTheOtherEnd()
    {
        // Both labels sit at the LOW-x end, where inference says MinX. The second one states R180,
        // and the extractor must take it — this is the whole point of the field existing.
        var r = Extract(Line(), Port("P1", 0, 1.45, LayoutRotation.R0), Port("P2", 0.1, 1.45, LayoutRotation.R180));
        Assert.True(r.Ok, r.Refusal);

        Assert.Equal(PlanarPortSide.MinX, r.Ports[0].Side);
        Assert.Equal(PlanarPortSide.MaxX, r.Ports[1].Side);
        Assert.Contains(r.Notes, n => n.Contains("the port's own direction"));
    }

    [Fact]
    public void AStatedDirection_ResolvesAPlacementThatInferenceRefusesAsAmbiguous()
    {
        // A label right in the corner is equidistant from two edges, so R-res-5 refuses it rather
        // than guessing. Stating the direction is now the way out, and the refusal says so.
        var corner = Port("P1", 0.02, 0.02);
        var refused = Extract(Line(), corner);
        Assert.False(refused.Ok);
        Assert.Contains("Rotate the port", refused.Refusal);

        corner.PortDirection = LayoutRotation.R0;
        var ok = Extract(Line(), corner);
        Assert.True(ok.Ok, ok.Refusal);
        Assert.Equal(PlanarPortSide.MinX, ok.Ports[0].Side);
    }

    [Fact]
    public void ANullDirection_StillTakesTheInferencePath_UnchangedFromBeforeTheFieldExisted()
    {
        var r = Extract(Line(), Port("P1", 0, 1.45), Port("P2", 20, 1.45));
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(PlanarPortSide.MinX, r.Ports[0].Side);
        Assert.Equal(PlanarPortSide.MaxX, r.Ports[1].Side);
        Assert.Contains(r.Notes, n => n.Contains("inferred from the nearest conductor boundary"));
    }

    // ── Persistence: additive, and a port-free .clay is byte-identical ─────────────────────────

    [Fact]
    public void PortDirection_RoundTripsThroughClay()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Port("P1", 0, 1.45, LayoutRotation.R270));

        var reloaded = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        Assert.Equal(LayoutRotation.R270, ((LabelShape)reloaded.Shapes[0]).PortDirection);
    }

    [Fact]
    public void ALabelWithNoPortDirection_WritesNoSuchFieldAtAll()
    {
        // Additive in the file as well as in behaviour: no FormatVersion bump, and every existing
        // .clay re-serializes byte-identically.
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new LabelShape { Layer = TopCopper, X = 0, Y = 0, Text = "note", Height = Mm(0.5) });

        string json = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("PortDirection", json);
    }

    // ── Copy/paste: a pasted port takes the next free number ──────────────────────────────────

    [Fact]
    public void PastingAPortThatCollides_TakesTheLowestFreeNumber_PreservingTheUsersNaming()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(Line());
        view.Shapes.Add(Port("Port 1", 0, 1.45, LayoutRotation.R0));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        SelectPort(vm);
        vm.Duplicate();

        var ports = view.Shapes.OfType<LabelShape>().Where(l => l.IsPort).ToList();
        Assert.Equal(2, ports.Count);
        Assert.Equal("Port 1", ports[0].Text);
        Assert.Equal("Port 2", ports[1].Text);          // the digit run substituted, prefix kept
        Assert.Equal(LayoutRotation.R0, ports[1].PortDirection);
    }

    [Fact]
    public void PastingTwoPortsAtOnce_DoesNotLetThemCollideWithEachOther()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(Line());
        view.Shapes.Add(Port("P1", 0, 1.45, LayoutRotation.R0));
        view.Shapes.Add(Port("P2", 20, 1.45, LayoutRotation.R180));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        SelectPort(vm);
        vm.OnPointerPressed(Mm(20), Mm(1.45), KeyModifiers.Shift, 1, Mm(0.05));
        vm.Duplicate();

        var numbers = view.Shapes.OfType<LabelShape>().Where(l => l.IsPort)
            .Select(l => { EmPortExtraction.TryParseNumber(l.Text, out int n); return n; })
            .OrderBy(n => n).ToList();
        Assert.Equal([1, 2, 3, 4], numbers);
    }

    [Fact]
    public void APastedPortWhoseNumberIsFree_KeepsIt()
    {
        // Renumbering must be a COLLISION fix, not a rewrite — a port pasted into a document that
        // does not already use its number keeps the name the user gave it.
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(Line());
        var vm = new LayoutEditorViewModel(view);

        vm.PasteInPlace([Port("P7", 0, 1.45, LayoutRotation.R0)]);

        Assert.Equal("P7", view.Shapes.OfType<LabelShape>().Single(l => l.IsPort).Text);
    }

    [Fact]
    public void APortSurvivesTheCLIPBOARD_FlagAndDirectionIntact()
    {
        // The system clipboard carries the fragment as JSON (a cross-session, cross-instance copy),
        // so a port has to survive the same converter a .clay does — not only the in-process clone
        // path Duplicate takes.
        var payload = new LayoutFragment.Payload
        {
            Marker = LayoutFragment.Marker,
            DbuPerMicron = Dbu,
            Shapes = [Port("P3", 0, 1.45, LayoutRotation.R270)],
        };

        Assert.True(LayoutFragment.TryDeserialize(LayoutFragment.Serialize(payload), out var back));
        var port = Assert.IsType<LabelShape>(Assert.Single(back!.Shapes));
        Assert.True(port.IsPort);
        Assert.Equal("P3", port.Text);
        Assert.Equal(LayoutRotation.R270, port.PortDirection);
    }

    [Fact]
    public void TheGraphicExport_IncludesThePortMarker()
    {
        // Copy-as-graphic (PDF/SVG/bitmap -> PowerPoint, Keynote) renders through LayoutRenderer.Draw
        // on a transient view of the exported shapes, so the marker rides along for free — but "for
        // free" is exactly the kind of claim that stops being true silently, so it is pinned by
        // comparing the SAME export with and without the port.
        var withPort  = new List<LayoutShape> { Line(), Port("P1", 0, 1.45, LayoutRotation.R0) };
        var without   = new List<LayoutShape> { Line() };

        var a = LayoutClipboard.TryRenderToSvg(withPort, StarterTechnologies.Pcb2Layer(), LayoutRenderTheme.Light, transparent: true);
        var b = LayoutClipboard.TryRenderToSvg(without,  StarterTechnologies.Pcb2Layer(), LayoutRenderTheme.Light, transparent: true);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(b!.Value.Svg, a!.Value.Svg);
    }

    [Theory]
    [InlineData("P1", 5, "P5")]
    [InlineData("Port 3", 12, "Port 12")]
    [InlineData("#2", 4, "#4")]
    [InlineData("gate", 3, "P3")]
    public void SubstitutingAPortNumber_KeepsWhateverPrefixTheUserTyped(string text, int n, string expected)
        => Assert.Equal(expected, LayoutEditorViewModel.SubstitutePortNumber(text, n));

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(Math.Clamp(x, 0, bmp.Width - 1), Math.Clamp(y, 0, bmp.Height - 1));
    }

    private static bool AnythingPaintedNear(SKSurface surface, int sx, int sy, SKColor background, int radius)
    {
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                var c = PixelAt(surface, sx + dx, sy + dy);
                if (Math.Abs(c.Red - background.Red) > 12 || Math.Abs(c.Green - background.Green) > 12
                    || Math.Abs(c.Blue - background.Blue) > 12) return true;
            }
        return false;
    }

    /// <summary>Renders one port on a line, on an otherwise EMPTY layer, so the only thing that can
    /// paint in the probed region is the marker.</summary>
    private static (SKSurface Surface, LayoutViewport Vp, SKColor Background) RenderPort(LayoutRotation? dir)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        // A conductor on a DIFFERENT layer, hidden, so the marker is the only ink near the probe —
        // the port label itself keeps its own layer, which stays visible.
        view.Shapes.Add(Line());
        view.Shapes.Add(Port("P1", 0, 1.45, dir));

        var tech = StarterTechnologies.Pcb2Layer();
        var vp = new LayoutViewport(Mm(-2), Mm(-2), 0.00002, 400, 400);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return (surface, vp, LayoutRenderTheme.Light.Background);
    }

    [Fact]
    public void APortWithADirection_PaintsAMarkerReachingIntoTheMetal()
    {
        // The arrow points INTO the conductor (+x̂ from the low-x end). Probe a point along it that
        // is clear of both the label glyph and the conductor's own outline stroke, and compare
        // against the same probe with the port removed — a differential oracle, so the conductor's
        // own fill cannot make this pass for the wrong reason.
        var (withPort, vp, bg) = RenderPort(LayoutRotation.R0);

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Line());
        using var withoutPort = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(withoutPort.Canvas, view, StarterTechnologies.Pcb2Layer(), vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false });

        int differing = 0;
        using (var a = SKBitmap.FromImage(withPort.Snapshot()))
        using (var b = SKBitmap.FromImage(withoutPort.Snapshot()))
            for (int x = 0; x < 400; x++)
                for (int y = 0; y < 400; y++)
                    if (a.GetPixel(x, y) != b.GetPixel(x, y)) differing++;

        withPort.Dispose();
        Assert.True(differing > 200, $"a port marker plus its label should paint a substantial number of pixels; got {differing}");
    }

    [Fact]
    public void APortWithNoDirectionAndNoConductorUnderIt_DrawsNoMarker()
    {
        // Nothing to be a width of and nothing to point along. Drawing a guessed arrow here would be
        // the one thing worse than drawing none — the extractor refuses this port by name anyway.
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Port("P1", 0, 0));

        Assert.Null(LayoutPortDirection.Resolve(view.Shapes, (LabelShape)view.Shapes[0]));
    }

    [Fact]
    public void ResolvingAPort_ReportsWhetherTheDirectionWasStatedOrInferred()
    {
        var shapes = new List<LayoutShape> { Line(), Port("P1", 0, 1.45) };
        var inferred = LayoutPortDirection.Resolve(shapes, (LabelShape)shapes[1]);
        Assert.NotNull(inferred);
        Assert.True(inferred!.Value.Inferred);
        Assert.Equal(LayoutRotation.R0, inferred.Value.Direction);
        Assert.Equal(Mm(2.9), inferred.Value.WidthDbu);

        ((LabelShape)shapes[1]).PortDirection = LayoutRotation.R90;
        var stated = LayoutPortDirection.Resolve(shapes, (LabelShape)shapes[1]);
        Assert.NotNull(stated);
        Assert.False(stated!.Value.Inferred);
        Assert.Equal(Mm(20), stated.Value.WidthDbu);   // width is measured ACROSS the new direction
    }
}
