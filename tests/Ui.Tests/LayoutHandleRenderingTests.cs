using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── Phase L1d gate 2: handles render for a single-shape selection, and disappear for a
// multi-selection ("multi-selection shows no handles -- it is a move/delete selection"). Pixel
// oracle in the same style as LayoutSelectionRenderingTests.cs (L1c).
//
// Probe point: directly OUTSIDE the midpoint of a polygon edge, far from any corner. A corner's
// selection-outline miter join spreads diagonally a pixel or two past the vertex, which would be
// indistinguishable from a handle right at that corner -- an edge midpoint has no such ambiguity
// (the outline there is a plain ~2px-wide straight run), so a pixel a few px further out can only
// be painted by an EdgeMidpoint handle, never by the outline itself.

public class LayoutHandleRenderingTests
{
    private static Technology MakeTech(LayerKey key, SKColor color) => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = key, Name = "L", Color = new CircuitRF.Design.Theming.Rgba(color.Red, color.Green, color.Blue), FillOpacity = 0.35, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle };

    private static SKSurface Render(LayoutView view, Technology tech, LayoutViewport vp, LayoutOverlay? overlay)
    {
        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, Overlay = overlay };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return surface;
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(System.Math.Clamp(x, 0, bmp.Width - 1), System.Math.Clamp(y, 0, bmp.Height - 1));
    }

    private static bool IsAccent(SKColor c) => c.Blue > c.Red + 20 && c.Blue > c.Green + 10;

    private static bool AccentNear(SKSurface surface, int sx, int sy, int radius = 1)
    {
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                if (IsAccent(PixelAt(surface, sx + dx, sy + dy))) return true;
        return false;
    }

    private static PolygonShape Square(LayerKey key) => new() { Layer = key, Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000] };

    [Fact]
    public void SingleSelection_EdgeMidpointHandle_PaintsJustOutsideTheEdge()
    {
        var key = new LayerKey(1, 0);
        var view = MakeView();
        view.Shapes.Add(Square(key));
        var tech = MakeTech(key, new SKColor(20, 160, 20));
        var vp = new LayoutViewport(-50_000, -50_000, 0.001, 400, 400);

        // Directly above the top edge's midpoint (50000,100000) -- far from either corner.
        int sx = (int)vp.WorldToScreenX(50_000);
        int sy = (int)vp.WorldToScreenY(103_000);

        var surface = Render(view, tech, vp, new LayoutOverlay { SelectedIndices = [0] });

        Assert.True(AccentNear(surface, sx, sy), "expected an edge-midpoint handle just outside the top edge");
    }

    [Fact]
    public void MultiSelection_NoHandlePaintsOutsideTheEdge()
    {
        var key = new LayerKey(1, 0);
        var view = MakeView();
        view.Shapes.Add(Square(key));
        view.Shapes.Add(new RectShape { Layer = key, X1 = 200_000, Y1 = 0, X2 = 300_000, Y2 = 100_000 });
        var tech = MakeTech(key, new SKColor(20, 160, 20));
        var vp = new LayoutViewport(-50_000, -50_000, 0.001, 400, 400);

        int sx = (int)vp.WorldToScreenX(50_000);
        int sy = (int)vp.WorldToScreenY(103_000);

        var surfaceSingle = Render(view, tech, vp, new LayoutOverlay { SelectedIndices = [0] });
        var surfaceMulti = Render(view, tech, vp, new LayoutOverlay { SelectedIndices = [0, 1] });

        Assert.True(AccentNear(surfaceSingle, sx, sy), "sanity check: single selection shows the handle");
        Assert.False(AccentNear(surfaceMulti, sx, sy), "multi-selection must show no handles");
    }

    [Fact]
    public void NoSelection_NoHandlePaintsOutsideTheEdge()
    {
        var key = new LayerKey(1, 0);
        var view = MakeView();
        view.Shapes.Add(Square(key));
        var tech = MakeTech(key, new SKColor(20, 160, 20));
        var vp = new LayoutViewport(-50_000, -50_000, 0.001, 400, 400);

        int sx = (int)vp.WorldToScreenX(50_000);
        int sy = (int)vp.WorldToScreenY(103_000);

        var surface = Render(view, tech, vp, overlay: null);

        Assert.False(AccentNear(surface, sx, sy));
    }
}
