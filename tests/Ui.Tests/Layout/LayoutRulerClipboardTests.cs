using System.Linq;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// docs/design/layout-view.md §9B.9 — gate 12. The internal fragment (a pasted ruler must measure the
/// same PHYSICAL distance in a document at a different resolution) and the PowerPoint path (the ruler
/// appears in the vector graphic, and a <c>Fixed</c>-mode ruler's text is fully inside the page).
/// </summary>
public class LayoutRulerClipboardTests : System.IDisposable
{
    public LayoutRulerClipboardTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        System.GC.SuppressFinalize(this);
    }

    private static readonly LayerKey Metal = new(1, 0);

    // ── Internal fragment ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Copy_CarriesTheSelectedRulers_AlongsideTheShapes()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 3_000, TextHeightDbu = 500 });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);

        // A left-to-right marquee over everything — the real gesture, and the one path that produces a
        // genuinely MIXED shape-plus-ruler selection.
        vm.OnPointerPressed(-1_000, -1_000, Avalonia.Input.KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(9_000, 9_000, leftDown: true, Avalonia.Input.KeyModifiers.None, 40);
        vm.OnPointerReleased(9_000, 9_000, Avalonia.Input.KeyModifiers.None);
        Assert.Single(vm.SelectedIndices);
        Assert.Single(vm.SelectedRulerIndices);

        var payload = vm.BuildCopyPayload();
        Assert.NotNull(payload);

        Assert.Single(payload!.Shapes);
        Assert.Single(payload.Rulers);
        Assert.Equal(4_000, payload.Rulers[0].X2);
        // R-rul-6: the fragment carries the source document's display unit so the graphic export can
        // render the readout in it without hard-coding one.
        Assert.Equal(LayoutUnit.Um, payload.DisplayUnit);
    }

    [Fact]
    public void CanCopySelection_IsTrue_ForARulerOnlySelection()
    {
        // R-rul-11: rulers are selectable BECAUSE of Copy — "rulers work with copy and paste" is not
        // expressible unless a ruler can be selected.
        var model = new LayoutView { DbuPerMicron = 1000 };
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 0 });
        var vm = new LayoutEditorViewModel(model);

        Assert.False(vm.CanCopySelection);
        vm.SelectRulers([0]);
        Assert.True(vm.CanCopySelection);
    }

    [Fact]
    public void Rescale_AcrossResolutions_KeepsTheSamePhysicalDistance()
    {
        // R-L1f-2 / §9B.9. 4,000 DBU at 1,000 DBU/µm is 4 µm; at 2,000 DBU/µm the same 4 µm must be
        // 8,000 DBU. The reported number is computed from the endpoints, so it follows by construction.
        var source = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        source.Rulers.Add(new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 0,
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 500,
        });
        var vm = new LayoutEditorViewModel(source);
        vm.SelectRulers([0]);
        var payload = vm.BuildCopyPayload()!;

        var rescaled = LayoutFragment.Rescale(payload, destDbuPerMicron: 2000);

        var r = Assert.Single(rescaled.Rulers!);
        Assert.Equal(8_000, r.X2);
        Assert.Equal(8_000, r.DistanceDbu);
        Assert.Equal(4m, LayoutUnits.FromDbu(r.DistanceDbu, LayoutUnit.Um, 2000));
        // A world text HEIGHT is a length too, and travels with the coordinates.
        Assert.Equal(1_000, r.TextHeightDbu);
    }

    [Fact]
    public void Paste_LandsTheRuler_AsPartOfTheSameUndoEntry()
    {
        var source = new LayoutView { DbuPerMicron = 1000 };
        source.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        source.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 0 });
        var svm = new LayoutEditorViewModel(source);
        svm.SelectAllCommand.Execute(null);
        svm.SelectRulers([0]);
        var payload = svm.BuildCopyPayload()!;

        var dest = new LayoutView { DbuPerMicron = 1000 };
        var dvm = new LayoutEditorViewModel(dest);
        dvm.PasteInPlace(payload.Shapes, [], payload.Rulers);

        Assert.Single(dest.Rulers);
        dvm.UndoCommand.Execute(null);
        Assert.Empty(dest.Rulers);
    }

    // ── The PowerPoint path (§9B.9) ───────────────────────────────────────────────────────────────

    private static LayoutFragment.Payload PayloadWith(params RulerAnnotation[] rulers)
    {
        var p = new LayoutFragment.Payload { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        p.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 20_000 });
        p.Rulers.AddRange(rulers);
        return p;
    }

    private static Technology Tech() => new()
    {
        Name = "T",
        Layers = [new LayerDef { Key = Metal, Name = "L", Color = new CircuitRF.Design.Theming.Rgba(0, 200, 0), Visible = true }],
    };

    [Fact]
    public void TheRuler_ItsReadoutAndItsCaption_AppearInTheVectorGraphic()
    {
        var payload = PayloadWith(new RulerAnnotation
        {
            X1 = 2_000, Y1 = 4_000, X2 = 38_000, Y2 = 4_000,
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 2_500,
            Caption = "RulerCaptionMarker",
        });

        var ctx = LayoutClipboard.MakeExportContext(payload, Tech(), LayoutRenderTheme.Light, transparent: true);
        var svg = LayoutClipboard.TryRenderToSvg(ctx);
        Assert.NotNull(svg);

        // Skia's SVG canvas writes text runs as <text> elements — the readout and the caption are both
        // there, from the ONE line that copies payload.Rulers into the transient view.
        Assert.Contains("RulerCaptionMarker", svg!.Value.Svg);
        Assert.Contains("36 µm", svg.Value.Svg);
    }

    [Fact]
    public void FixedModeRulerText_IsFullyInsideThePage_R_rul_16()
    {
        // The two-pass bounds. Pass 1 has no idea how large a Fixed ruler's text will be in world
        // space; pass 2 measures it at the scale pass 1 chose. Skipping pass 2 crops the readout off
        // the page, which is the same family as the ports-cropped-off-the-page bug
        // ComputeSelectionBounds' own doc comment records.
        var ruler = new RulerAnnotation
        {
            X1 = 2_000, Y1 = 18_000, X2 = 38_000, Y2 = 18_000,   // near the TOP of the artwork
            SizeMode = RulerSizeMode.Fixed, TextSizePt = 18.0, TextHeightDbu = 1,
            Caption = "a caption wide enough to matter",
        };
        var ctx = LayoutClipboard.MakeExportContext(PayloadWith(ruler), Tech(), LayoutRenderTheme.Light, true);

        var bounds = LayoutClipboard.SelectionBoundsForTests(ctx);
        Assert.NotNull(bounds);
        var (worldW, worldH, minX, minY) = bounds!.Value;

        // Re-derive the scale the PDF flavour picks from these bounds, then measure the ruler's own
        // painted extent at exactly that scale — the number pass 2 used.
        const double pad = 0.15;
        double zoom = System.Math.Min(720.0 / (worldW * (1 + 2 * pad)), 540.0 / (worldH * (1 + 2 * pad)));
        var painted = LayoutRenderer.MeasureRulerWorldBbox(ruler, LayoutUnit.Um, 1000, zoom);

        Assert.True(painted.MinX >= minX, "the readout runs off the left of the page");
        Assert.True(painted.MinY >= minY, "the readout runs off the bottom of the page");
        Assert.True(painted.MaxX <= minX + worldW, "the readout runs off the right of the page");
        Assert.True(painted.MaxY <= minY + worldH, "the readout runs off the TOP of the page");
    }

    [Fact]
    public void OnePass_WouldHaveCropped_SoTheSecondPassIsLoadBearing()
    {
        // The same ruler, with the bounds taken WITHOUT its Fixed text (which is exactly what pass 1
        // alone produces): the painted extent then escapes the page. This is the assertion that makes
        // "do not skip pass 2" a tested rule rather than a comment.
        var ruler = new RulerAnnotation
        {
            X1 = 2_000, Y1 = 19_500, X2 = 38_000, Y2 = 19_500,
            SizeMode = RulerSizeMode.Fixed, TextSizePt = 18.0, TextHeightDbu = 1,
        };
        var payload = PayloadWith(ruler);

        // Pass 1 only: geometry + the ruler's own line endpoints.
        var pass1 = new Bbox(0, 0, 40_000, 20_000);
        const double pad = 0.15;
        double w = pass1.MaxX - pass1.MinX, h = pass1.MaxY - pass1.MinY;
        double zoom1 = System.Math.Min(720.0 / (w * (1 + 2 * pad)), 540.0 / (h * (1 + 2 * pad)));
        var painted = LayoutRenderer.MeasureRulerWorldBbox(ruler, LayoutUnit.Um, 1000, zoom1);
        Assert.True(painted.MaxY > pass1.MaxY, "the fixture must actually escape a one-pass bbox");

        // The real two-pass answer contains it.
        var ctx = LayoutClipboard.MakeExportContext(payload, Tech(), LayoutRenderTheme.Light, true);
        var (worldW, worldH, minX, minY) = LayoutClipboard.SelectionBoundsForTests(ctx)!.Value;
        Assert.True(minY + worldH >= painted.MaxY);
        _ = (worldW, minX);
    }

    [Fact]
    public void ARulerFreeSelection_ProducesTheSameBounds_AsBefore()
    {
        // Nothing about the two-pass addition may move the page for a document with no rulers.
        var withNone = LayoutClipboard.MakeExportContext(PayloadWith(), Tech(), LayoutRenderTheme.Light, true);
        var b = LayoutClipboard.SelectionBoundsForTests(withNone);
        Assert.NotNull(b);
        Assert.Equal(40_000, b!.Value.WorldW);
        Assert.Equal(20_000, b.Value.WorldH);
        Assert.Equal(0, b.Value.BbMinX);
        Assert.Equal(0, b.Value.BbMinY);
    }
}
