using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── L1 fix gate: LayoutRulerRenderer never writes outside its own strip rect ────────────────
// docs/sonnet-briefs/brief-L1-fix-clear-and-default-zoom.md Bug 1 — the identical unclipped
// canvas.Clear(...) bug as LayoutRenderer, on the ruler strips painted just before the canvas.

public class LayoutRulerRendererTests
{
    [Fact]
    public void Draw_NeverPaintsOutsideTheStripRect()
    {
        const int surfaceW = 300, surfaceH = 200;
        const int stripW = 120, stripH = 22;
        var sentinel = new SKColor(10, 20, 30, 255);

        using var surface = SKSurface.Create(new SKImageInfo(surfaceW, surfaceH));
        surface.Canvas.Clear(sentinel);

        // Zoom = 0 makes Draw return right after the background clip+fill, before it ever reaches
        // SkiaFonts.PlexRegular for tick labels — that font load goes through Avalonia's
        // IAssetLoader, which requires a real Avalonia app host not present in these framework-free
        // xunit tests (src/Ui/CLAUDE.md's "Testing without the Avalonia runtime"). The background
        // fill is exactly the code path this test needs to exercise; no ticks need to be drawn.
        var vp = new LayoutViewport(PanX: 0, PanY: 0, Zoom: 0, Width: stripW, Height: stripH);
        LayoutRulerRenderer.Draw(
            surface.Canvas, (stripW, stripH), LayoutRulerOrientation.Horizontal, vp,
            dbuPerMicron: 1000, displayUnit: LayoutUnit.Um, cursorWorld: null, LayoutRenderTheme.Light);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        for (int y = 0; y < surfaceH; y++)
        for (int x = 0; x < surfaceW; x++)
        {
            if (x < stripW && y < stripH) continue; // inside the strip — the renderer owns this region
            Assert.True(bmp.GetPixel(x, y) == sentinel,
                $"pixel ({x},{y}) outside the {stripW}x{stripH} ruler strip was overwritten — " +
                "canvas.Clear leaked past the control's own bounds");
        }
    }
}
