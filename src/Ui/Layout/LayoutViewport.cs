// Framework-free viewport math for the layout canvas (docs/design/layout-view.md — L1a brief).
// No SKPath / Avalonia types — this is pure transform arithmetic, shared by LayoutRenderer,
// LayoutCanvas, and LayoutRulerControl so the world<->screen convention is defined in one place.

namespace CircuitRF.Ui.Layout;

/// <summary>
/// The layout canvas's viewport state: pan + zoom, in world DBU, plus the current canvas pixel
/// size (needed for the Y-up flip — see <see cref="WorldToScreenY"/>).
///
/// <b>Convention: Y-up.</b> Layout coordinates are physical (GDSII/fab convention: increasing Y is
/// "up"), unlike the schematic/symbol canvases which are screen-sense (Y-down). <see cref="PanX"/>
/// is the world X at the LEFT edge; <see cref="PanY"/> is the world Y at the BOTTOM edge.
/// <see cref="Zoom"/> is device pixels per DBU.
/// </summary>
public readonly record struct LayoutViewport(double PanX, double PanY, double Zoom, double Width, double Height)
{
    public double WorldToScreenX(double worldX) => (worldX - PanX) * Zoom;

    public double WorldToScreenY(double worldY) => Height - (worldY - PanY) * Zoom;

    public double ScreenToWorldX(double screenX) => screenX / Zoom + PanX;

    public double ScreenToWorldY(double screenY) => (Height - screenY) / Zoom + PanY;

    /// <summary>Lowest visible world X (left edge).</summary>
    public double VisibleMinX => PanX;

    /// <summary>Highest visible world X (right edge).</summary>
    public double VisibleMaxX => PanX + Width / Zoom;

    /// <summary>Lowest visible world Y (bottom edge).</summary>
    public double VisibleMinY => PanY;

    /// <summary>Highest visible world Y (top edge).</summary>
    public double VisibleMaxY => PanY + Height / Zoom;

    /// <summary>
    /// Returns a new viewport with <see cref="Zoom"/> changed to <paramref name="newZoom"/> such
    /// that the world point under (<paramref name="anchorScreenX"/>, <paramref name="anchorScreenY"/>)
    /// stays under that same screen point (R-L1a / gate 5, "zoom anchors at the cursor").
    /// </summary>
    public LayoutViewport WithZoomAnchoredAt(double newZoom, double anchorScreenX, double anchorScreenY)
    {
        double wx = ScreenToWorldX(anchorScreenX);
        double wy = ScreenToWorldY(anchorScreenY);
        double newPanX = wx - anchorScreenX / newZoom;
        double newPanY = wy - (Height - anchorScreenY) / newZoom;
        return this with { PanX = newPanX, PanY = newPanY, Zoom = newZoom };
    }

    /// <summary>Returns a new viewport with the same pixel size, panned/zoomed so
    /// <paramref name="bbox"/> is centered with a small margin — gate 6, "Zoom Fit".</summary>
    public static LayoutViewport ZoomToFit(
        Bbox bbox, double width, double height,
        double marginFrac = 0.1, double minZoom = 1e-12, double maxZoom = 1e6)
    {
        if (bbox.IsEmpty || width < 1 || height < 1)
            return Default(width, height, minZoom: minZoom, maxZoom: maxZoom);

        double worldW = bbox.MaxX - bbox.MinX;
        double worldH = bbox.MaxY - bbox.MinY;
        if (worldW <= 0) worldW = 1;
        if (worldH <= 0) worldH = 1;

        double zoom = Math.Min(width / worldW, height / worldH) * (1.0 - 2 * marginFrac);
        zoom = Math.Clamp(zoom, minZoom, maxZoom);

        double cx = (bbox.MinX + bbox.MaxX) / 2.0;
        double cy = (bbox.MinY + bbox.MaxY) / 2.0;
        double panX = cx - width / (2.0 * zoom);
        double panY = cy - height / (2.0 * zoom);
        return new LayoutViewport(panX, panY, zoom, width, height);
    }

    /// <summary>
    /// A physically-meaningful default viewport for an empty layout, origin-centered.
    /// <see cref="Zoom"/> is device pixels per DBU, so a fixed <c>zoom = 1.0</c> (the old behavior)
    /// meant 1 screen pixel per DBU — at the default 1000 DBU/µm that is 1 pixel per NANOMETRE, so a
    /// PCB technology's 1-mil (25,400 DBU) snap step was ~17x wider than the entire visible canvas
    /// and every pointer position snapped to the same grid cell (docs/sonnet-briefs/
    /// brief-L1-fix-clear-and-default-zoom.md, Bug 2). Instead, frame ~200 of the layout's own snap
    /// steps across the viewport width — physically drawable immediately and with the grid visible
    /// at the §5 8-pixel threshold, for both the PCB (25,400 DBU snap → tens of mm across) and MMIC
    /// (5 DBU snap → ~1 µm across) starter technologies. Falls back to a plain micron-scale span via
    /// <paramref name="dbuPerMicron"/> when there is no meaningful snap step
    /// (<paramref name="snapDbu"/> &lt;= 0).
    /// </summary>
    public static LayoutViewport Default(
        double width, double height, long snapDbu = 0, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
        double minZoom = 1e-9, double maxZoom = 1e5)
    {
        const double TargetSnapStepsAcrossWidth = 200.0;
        const double FallbackSpanMicrons = 1000.0;

        double spanDbu = snapDbu > 0
            ? snapDbu * TargetSnapStepsAcrossWidth
            : Math.Max(1, dbuPerMicron) * FallbackSpanMicrons;

        double zoom = Math.Clamp(width / spanDbu, minZoom, maxZoom);
        double panX = -width / (2.0 * zoom);
        double panY = -height / (2.0 * zoom);
        return new LayoutViewport(panX, panY, zoom, width, height);
    }
}
