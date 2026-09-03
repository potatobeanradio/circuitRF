// §2A — the exported file must open with the design on screen. Two independent layers, because they
// fail independently: correct $EXTMIN/$EXTMAX (§2A.1, the part that degrades gracefully — many viewers
// zoom-to-extents on open regardless of any stored view) and the stored *ACTIVE VPORT (§2A.2, a bonus
// where honoured). §2A.4's degenerate-case guards live here too, since both the extents and the view
// height are derived from the same bbox.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What the stored <c>*ACTIVE</c> VPORT/HEADER view vars describe — centre + height (drawing
/// units) + aspect (width/height).</summary>
public readonly record struct DxfView(double CenterX, double CenterY, double Height, double Aspect);

/// <summary>The written <c>$EXTMIN</c>/<c>$EXTMAX</c> (== <c>$LIMMIN</c>/<c>$LIMMAX</c>) pair, in
/// drawing units — never zero-span (§2A.4).</summary>
public readonly record struct DxfExtentGuard(double ExtMinX, double ExtMinY, double ExtMaxX, double ExtMaxY);

public static class DxfViewCalc
{
    /// <summary>A zero or negative view height/extent span is invalid and can make a file unopenable
    /// (§2A.4) — clamp to at least this many drawing units.</summary>
    private const double MinSpanDrawingUnits = 1e-6;

    /// <summary>Fit-to-extents margin (~10%, R-L4b-6's own wording).</summary>
    private const double FitMarginFrac = 0.10;

    public static (DxfView View, DxfExtentGuard Guard) Compute(Bbox bboxDbu, DxfExportOptions options, double dbuToDrawingUnit)
    {
        double minX, minY, maxX, maxY;
        if (bboxDbu.IsEmpty)
        {
            // An empty layout has no extents (§2A.4) — emit a sensible default rather than zeros.
            minX = minY = -0.5;
            maxX = maxY = 0.5;
        }
        else
        {
            minX = bboxDbu.MinX * dbuToDrawingUnit;
            minY = bboxDbu.MinY * dbuToDrawingUnit;
            maxX = bboxDbu.MaxX * dbuToDrawingUnit;
            maxY = bboxDbu.MaxY * dbuToDrawingUnit;
        }

        // A single point or a zero-width/zero-height design hits the same problem in one axis —
        // clamp to a minimum span before computing anything derived from it.
        if (maxX - minX <= 0)
        {
            double cx = (minX + maxX) / 2.0;
            minX = cx - MinSpanDrawingUnits / 2.0;
            maxX = cx + MinSpanDrawingUnits / 2.0;
        }
        if (maxY - minY <= 0)
        {
            double cy = (minY + maxY) / 2.0;
            minY = cy - MinSpanDrawingUnits / 2.0;
            maxY = cy + MinSpanDrawingUnits / 2.0;
        }

        var guard = new DxfExtentGuard(minX, minY, maxX, maxY);
        double spanX = maxX - minX, spanY = maxY - minY;

        DxfView view;
        if (options.ViewMode == DxfViewMode.MatchCurrentView && options.MatchViewport is { } vp)
        {
            double cx = (vp.VisibleMinX + vp.VisibleMaxX) / 2.0 * dbuToDrawingUnit;
            double cy = (vp.VisibleMinY + vp.VisibleMaxY) / 2.0 * dbuToDrawingUnit;
            double height = Math.Max((vp.VisibleMaxY - vp.VisibleMinY) * dbuToDrawingUnit, MinSpanDrawingUnits);
            double aspect = vp.Width > 0 && vp.Height > 0 ? vp.Width / vp.Height : PositiveAspect(options.CanvasAspect);
            view = new DxfView(cx, cy, height, PositiveAspect(aspect));
        }
        else
        {
            // R-L4b-6: height = max(bboxH, bboxW / aspect) * margin — err toward showing too much,
            // since the viewer's own window aspect is unknown at export time.
            double aspect = PositiveAspect(options.CanvasAspect);
            double height = Math.Max(spanY, spanX / aspect) * (1.0 + 2.0 * FitMarginFrac);
            double cx = (minX + maxX) / 2.0;
            double cy = (minY + maxY) / 2.0;
            view = new DxfView(cx, cy, Math.Max(height, MinSpanDrawingUnits), aspect);
        }

        return (view, guard);
    }

    private static double PositiveAspect(double a) => a > 0 && double.IsFinite(a) ? a : 1.0;
}
