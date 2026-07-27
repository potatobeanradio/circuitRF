using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1a gates 5/6: zoom anchors at the cursor; Zoom Fit frames the bbox ──

public class LayoutViewportTests
{
    [Fact]
    public void WorldToScreen_ScreenToWorld_RoundTrips()
    {
        var vp = new LayoutViewport(PanX: 1000, PanY: -500, Zoom: 2.0, Width: 800, Height: 600);
        var (wx, wy) = (12345.0, -6789.0);
        double sx = vp.WorldToScreenX(wx);
        double sy = vp.WorldToScreenY(wy);
        Assert.Equal(wx, vp.ScreenToWorldX(sx), 6);
        Assert.Equal(wy, vp.ScreenToWorldY(sy), 6);
    }

    [Fact]
    public void WorldToScreenY_IsYUp_HigherWorldYIsHigherOnScreen()
    {
        var vp = new LayoutViewport(PanX: 0, PanY: 0, Zoom: 1.0, Width: 400, Height: 400);
        double screenYLow  = vp.WorldToScreenY(worldY: 10);
        double screenYHigh = vp.WorldToScreenY(worldY: 100);
        Assert.True(screenYHigh < screenYLow, "a larger world Y (physically higher) must be a SMALLER screen Y (higher on screen)");
    }

    // ── Gate 5: zoom anchors at the cursor ───────────────────────────────────

    [Theory]
    [InlineData(100, 100, 1.15)]
    [InlineData(700, 50, 1.0 / 1.15)]
    [InlineData(0, 0, 2.0)]
    public void WithZoomAnchoredAt_WorldPointUnderCursor_StaysUnderCursor(double cursorSx, double cursorSy, double zoomFactor)
    {
        var vp = new LayoutViewport(PanX: 5000, PanY: -2000, Zoom: 0.5, Width: 800, Height: 600);
        double worldBefore_X = vp.ScreenToWorldX(cursorSx);
        double worldBefore_Y = vp.ScreenToWorldY(cursorSy);

        var vp2 = vp.WithZoomAnchoredAt(vp.Zoom * zoomFactor, cursorSx, cursorSy);

        double worldAfter_X = vp2.ScreenToWorldX(cursorSx);
        double worldAfter_Y = vp2.ScreenToWorldY(cursorSy);

        Assert.Equal(worldBefore_X, worldAfter_X, 6);
        Assert.Equal(worldBefore_Y, worldAfter_Y, 6);

        // Also check the reverse direction: the screen position of that world point is unchanged.
        Assert.Equal(cursorSx, vp2.WorldToScreenX(worldBefore_X), 6);
        Assert.Equal(cursorSy, vp2.WorldToScreenY(worldBefore_Y), 6);
    }

    // ── Gate 6: Zoom Fit ──────────────────────────────────────────────────────

    [Fact]
    public void ZoomToFit_TinyFixture_CentersAndFramesWithMargin()
    {
        var bbox = new Bbox(0, 0, 100, 50);
        var vp = LayoutViewport.ZoomToFit(bbox, width: 400, height: 400, marginFrac: 0.1);

        // The bbox corners should be within the viewport, with margin (not touching the edges).
        double x1 = vp.WorldToScreenX(bbox.MinX);
        double x2 = vp.WorldToScreenX(bbox.MaxX);
        double y1 = vp.WorldToScreenY(bbox.MinY);
        double y2 = vp.WorldToScreenY(bbox.MaxY);

        Assert.True(x1 > 0 && x1 < 400);
        Assert.True(x2 > 0 && x2 < 400);
        Assert.True(y1 > 0 && y1 < 400);
        Assert.True(y2 > 0 && y2 < 400);

        // Centered: the bbox's world center should map close to the canvas center.
        double cx = vp.WorldToScreenX((bbox.MinX + bbox.MaxX) / 2.0);
        double cy = vp.WorldToScreenY((bbox.MinY + bbox.MaxY) / 2.0);
        Assert.Equal(200, cx, 1);
        Assert.Equal(200, cy, 1);
    }

    [Fact]
    public void ZoomToFit_VeryLargeFixture_StillFramesCorrectly()
    {
        // A ~300mm board at 1nm resolution, matching the R-L1a-1 example scale.
        var bbox = new Bbox(0, 0, 300_000_000, 200_000_000);
        var vp = LayoutViewport.ZoomToFit(bbox, width: 800, height: 600, marginFrac: 0.1);

        Assert.True(vp.Zoom > 0);
        double x1 = vp.WorldToScreenX(bbox.MinX);
        double x2 = vp.WorldToScreenX(bbox.MaxX);
        double y1 = vp.WorldToScreenY(bbox.MinY);
        double y2 = vp.WorldToScreenY(bbox.MaxY);

        Assert.True(x1 >= -1 && x1 <= 800 + 1);
        Assert.True(x2 >= -1 && x2 <= 800 + 1);
        Assert.True(y1 >= -1 && y1 <= 600 + 1);
        Assert.True(y2 >= -1 && y2 <= 600 + 1);
        Assert.True(x2 - x1 > 400, "the wide (X) extent should dominate and fill most of the width");
    }

    [Fact]
    public void ZoomToFit_EmptyBbox_FallsBackToDefault_NoThrow()
    {
        var vp = LayoutViewport.ZoomToFit(Bbox.Empty, 400, 300);
        Assert.True(vp.Zoom > 0);
    }
}
