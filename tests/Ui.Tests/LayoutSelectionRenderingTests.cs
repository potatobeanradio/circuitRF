using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── Phase L1c rendering: selection outline, marquee, and live move-drag preview ──
// Pixel oracles in the same style as LayoutRendererTests.cs (L1a).

public class LayoutSelectionRenderingTests
{
    private static Technology MakeTech(LayerKey key, SKColor color) => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = key, Name = "L", Color = new CircuitRF.Ui.Theming.Rgba(color.Red, color.Green, color.Blue), FillOpacity = 0.35, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle };

    private static (SKSurface Surface, LayoutRenderResult Result) Render(LayoutView view, Technology tech, LayoutViewport vp, LayoutOverlay? overlay = null)
    {
        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, Overlay = overlay };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return (surface, result);
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static bool IsBlueDominant(SKColor c) => c.Blue > c.Red + 20 && c.Blue > c.Green + 10;

    [Fact]
    public void SelectedShape_GetsAccentOutline_FillUnchanged()
    {
        var key = new LayerKey(1, 0);
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 200_000, Y2 = 200_000 });
        var tech = MakeTech(key, new SKColor(20, 160, 20)); // green layer fill

        var vp = LayoutViewport.ZoomToFit(LayoutGeometry.BboxOf(view.Shapes[0]), 400, 400, 0.2);

        var (surfaceNoSel, _) = Render(view, tech, vp);
        var (surfaceSel, _) = Render(view, tech, vp, new LayoutOverlay { SelectedIndices = [0] });

        // Interior fill pixel is unaffected by selection.
        var interiorNoSel = PixelAt(surfaceNoSel, 200, 200);
        var interiorSel = PixelAt(surfaceSel, 200, 200);
        Assert.Equal(interiorNoSel, interiorSel);

        // Somewhere near the shape's screen-space border, the selected render must show the accent
        // (blue-dominant) outline color that the unselected render does not.
        int borderX = (int)vp.WorldToScreenX(0);
        bool foundAccent = false;
        for (int dx = -3; dx <= 3; dx++)
        {
            int x = System.Math.Clamp(borderX + dx, 0, 399);
            if (IsBlueDominant(PixelAt(surfaceSel, x, 200))) { foundAccent = true; break; }
        }
        Assert.True(foundAccent, "expected the selection accent outline near the shape's left edge");
    }

    [Fact]
    public void Marquee_RendersFilledAccentRect()
    {
        var view = MakeView();
        var tech = MakeTech(new LayerKey(1, 0), new SKColor(20, 160, 20));
        var vp = new LayoutViewport(-10_000, -10_000, 0.01, 400, 400);

        var overlay = new LayoutOverlay { Marquee = new LayoutMarquee(-5000, -5000, 25_000, 25_000) };
        var (surface, _) = Render(view, tech, vp, overlay);

        // Center of the marquee rect in screen space should show the accent fill.
        int cx = (int)vp.WorldToScreenX(10_000);
        int cy = (int)vp.WorldToScreenY(10_000);
        Assert.True(IsBlueDominant(PixelAt(surface, cx, cy)), "expected the marquee's accent fill at its center");
    }

    [Fact]
    public void DragOverride_RendersShapeAtTranslatedPosition_NotOriginal()
    {
        var key = new LayerKey(1, 0);
        var view = MakeView();
        var original = new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 };
        view.Shapes.Add(original);
        var tech = MakeTech(key, new SKColor(20, 160, 20));

        var vp = new LayoutViewport(-50_000, -50_000, 0.001, 400, 400); // visible span: -50,000..350,000 both axes

        var translated = new RectShape { Layer = key, X1 = 200_000, Y1 = 200_000, X2 = 250_000, Y2 = 250_000 };
        var overlay = new LayoutOverlay { DragOverrides = new System.Collections.Generic.Dictionary<int, LayoutShape> { [0] = translated } };

        var (surface, _) = Render(view, tech, vp, overlay);

        int origCx = (int)vp.WorldToScreenX(25_000), origCy = (int)vp.WorldToScreenY(25_000);
        int newCx = (int)vp.WorldToScreenX(225_000), newCy = (int)vp.WorldToScreenY(225_000);

        bool IsGreenDominant(SKColor c) => c.Green > c.Red + 10 && c.Green > c.Blue + 10;

        Assert.False(IsGreenDominant(PixelAt(surface, origCx, origCy)), "the shape must not render at its original position during a drag");
        Assert.True(IsGreenDominant(PixelAt(surface, newCx, newCy)), "the shape must render at the drag-override position");
    }
}
