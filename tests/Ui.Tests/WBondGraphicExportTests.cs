using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-C4's remaining two items: the graphic copy (§6.7 "to other applications") and the empty
/// reference layout that makes project-tree drag-drop reach something (§6.6).
/// </summary>
public class WBondGraphicExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wbond-graphic-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A design with three wires ~1 mm long, plus one rect of reference geometry beside it.</summary>
    private static (WBondDesign Design, LayoutView Layout) MakeFixture()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        for (int i = 0; i < 3; i++)
        {
            long y = i * 150_000;             // 150 µm pitch
            array.Wires.Add(new Wire
            {
                Points =
                [
                    new Point3(0, y, 0),
                    new Point3(250_000, y, 120_000),
                    new Point3(500_000, y, 160_000),
                    new Point3(750_000, y, 120_000),
                    new Point3(1_000_000, y, 0),
                ],
                DiameterNm = 25_400,
            });
        }
        design.Arrays.Add(array);

        var layout = new LayoutView();
        layout.Shapes.Add(new RectShape
        {
            X1 = -200_000, Y1 = -100_000, X2 = 0, Y2 = 400_000,
            Layer = new LayerKey(1, 0),
        });

        return (design, layout);
    }

    // ---------------------------------------------------------------- §6.7 framing

    /// <summary>
    /// <b>The frame includes the reference geometry, not just the wires.</b> Framing on the wires
    /// alone silently crops away the pads a reader needs to make sense of them — which reads as the
    /// export being broken rather than as a framing choice.
    /// </summary>
    [Fact]
    public void FitViewport_FramesTheLayoutAsWellAsTheWires()
    {
        var (design, layout) = MakeFixture();

        var withLayout = WBondGraphicExport.FitViewport(design, layout, 792f, 612f, 1000);
        var wiresOnly = WBondGraphicExport.FitViewport(design, layout: null, 792f, 612f, 1000);

        // The rect sits to the LEFT of every wire point, so including it must push the left edge out.
        Assert.True(withLayout.VisibleMinX < wiresOnly.VisibleMinX);

        // And the rect's own left edge is genuinely inside the framed region.
        Assert.True(withLayout.VisibleMinX <= -200_000.0);   // the rect's own left edge, in DBU
    }

    /// <summary>The design's own centre lands at the page centre, at any page shape.</summary>
    [Theory]
    [InlineData(792f, 612f)]
    [InlineData(400f, 900f)]
    public void FitViewport_CentresTheDesignOnThePage(float pageW, float pageH)
    {
        var (design, layout) = MakeFixture();

        var vp = WBondGraphicExport.FitViewport(design, layout, pageW, pageH, 1000);

        double cx = (vp.VisibleMinX + vp.VisibleMaxX) * 0.5;
        double cy = (vp.VisibleMinY + vp.VisibleMaxY) * 0.5;

        // At the 1,000 DBU/µm default a wire nanometre IS one DBU, so the fixture's extent in DBU is
        // X ∈ [-200,000, 1,000,000] and Y ∈ [-100,000, 400,000] — see WBondSnap for why this bridge
        // is restated wherever it is crossed rather than assumed.
        Assert.Equal(400_000.0, cx, 3);
        Assert.Equal(150_000.0, cy, 3);
    }

    /// <summary>An empty design exports a blank page rather than dividing by zero.</summary>
    [Fact]
    public void FitViewport_EmptyDesign_ProducesAUsableViewport_NotADivideByZero()
    {
        var vp = WBondGraphicExport.FitViewport(new WBondDesign(), layout: null, 792f, 612f, 1000);

        Assert.True(vp.Zoom > 0);
        Assert.True(double.IsFinite(vp.VisibleMinX) && double.IsFinite(vp.VisibleMaxY));
    }

    // ---------------------------------------------------------------- §6.7 rendering

    /// <summary>
    /// The composed page actually paints the wires. Rendered against a pre-filled sentinel so this
    /// cannot pass on a blank surface — the failure mode a "did it throw?" test would miss entirely.
    /// </summary>
    [Fact]
    public void Render_PaintsTheWires_OnAnOtherwiseTransparentPage()
    {
        var (design, layout) = MakeFixture();

        const int w = 396, h = 306;   // half-page, keeps the test cheap
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);

        WBondGraphicExport.Render(
            surface.Canvas, design, layout, technology: null, instanceBaseDir: null,
            WBondRenderTheme.Fallback, LayoutRenderTheme.Light,
            WireThicknessMode.Thin, w, h);

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);

        int painted = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0) painted++;

        Assert.True(painted > 200, $"expected the wires to paint; only {painted} px were touched");
    }

    /// <summary>
    /// <b>An export never paints a background.</b> A copied graphic pasted into a document must take
    /// that document's own background, not carry the editor's canvas colour with it.
    /// </summary>
    [Fact]
    public void Render_NeverPaintsABackground_TheCornersStayTransparent()
    {
        var (design, layout) = MakeFixture();

        const int w = 396, h = 306;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);

        WBondGraphicExport.Render(
            surface.Canvas, design, layout, technology: null, instanceBaseDir: null,
            WBondRenderTheme.Fallback, LayoutRenderTheme.Light,
            WireThicknessMode.Thin, w, h);

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);

        // The margin guarantees all four corners are outside the framed content.
        Assert.Equal((byte)0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)0, bitmap.GetPixel(w - 1, 0).Alpha);
        Assert.Equal((byte)0, bitmap.GetPixel(0, h - 1).Alpha);
        Assert.Equal((byte)0, bitmap.GetPixel(w - 1, h - 1).Alpha);
    }

    /// <summary>
    /// The export is a pure function of the DESIGN, never of whatever the user's canvas happens to be
    /// panned to — the same design renders identically however the editor is scrolled.
    /// </summary>
    [Fact]
    public void FitViewport_IsAFunctionOfTheDesignAlone_NotOfAnyOnScreenViewport()
    {
        var (design, layout) = MakeFixture();

        var a = WBondGraphicExport.FitViewport(design, layout, 792f, 612f, 1000);
        var b = WBondGraphicExport.FitViewport(design, layout, 792f, 612f, 1000);

        Assert.Equal(a, b);
    }

    // ---------------------------------------------------------------- §6.6 drag-drop target

    /// <summary>
    /// <b>A blank editor still has somewhere to drop a cell.</b> Without a reference layout the
    /// canvas has no view model, so the existing project-tree drag-drop path silently does nothing —
    /// which reads as drag-and-drop being broken rather than as there being no layout yet.
    /// </summary>
    [Fact]
    public void EnsureReferenceLayout_GivesABlankEditorSomethingToDropInto()
    {
        var doc = new WBondDocumentViewModel();
        Assert.Null(doc.ReferenceLayout);

        string dir = Path.Combine(_root, "scratch");
        doc.EnsureReferenceLayout(dir);

        Assert.NotNull(doc.ReferenceLayout);
        Assert.True(Directory.Exists(dir));

        // A dropped instance's CellRef resolves against the layout's own directory, so a layout with
        // no path could hold a dropped cell but never resolve it.
        Assert.Equal(dir, doc.ReferenceLayout!.InstanceBaseDir);
    }

    /// <summary>An editor that already has geometry is never overwritten by the blank fallback.</summary>
    [Fact]
    public void EnsureReferenceLayout_NeverReplacesAnExistingReferenceLayout()
    {
        var doc = new WBondDocumentViewModel();
        var existing = new LayoutEditorViewModel(new LayoutView());
        doc.ReferenceLayout = existing;

        doc.EnsureReferenceLayout(Path.Combine(_root, "scratch2"));

        Assert.Same(existing, doc.ReferenceLayout);
    }

    /// <summary>The overlay follows the reference layout the fallback installs, so the wires draw over it.</summary>
    [Fact]
    public void EnsureReferenceLayout_WiresTheOverlayToTheNewLayout()
    {
        var doc = new WBondDocumentViewModel();
        doc.EnsureReferenceLayout(Path.Combine(_root, "scratch3"));

        Assert.Same(doc.ReferenceLayout!.Model, doc.Overlay.ReferenceLayout);
        Assert.Equal(doc.ReferenceLayout.InstanceBaseDir, doc.Overlay.ReferenceBaseDir);
    }
}
