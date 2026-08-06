// L8b D5 — the PLAN-VIEW surface-mesh overlay.
//
// This is the one place L8b reverses an earlier decision, and it reverses it for a good reason.
// LayoutRenderer.Mesh.cs draws kernel A's mesh as an INSET CROSS-SECTION PANEL, and its header says
// why: "the mesh lives in the CROSS-SECTION plane (x across the line, y above the ground plane); the
// layout canvas shows the PLAN view. There is no coordinate mapping between them, so painting mesh
// segments onto plan-view artwork would be a picture of nothing." That was correct and it STAYS
// correct for kernel A, which still exists and still produces cross-section meshes.
//
// Kernel B's surface mesh lives in the SAME (x, y) plane the canvas already draws. For the first time
// the coordinate mapping exists, and §10.5's "a system layer superimposed on the geometry drawing
// cell boundaries" is the right picture. Both overlays are here; WHICH ONE IS DRAWN FOLLOWS FROM
// WHICH MESH WAS COMPUTED, NOT FROM A MODE.
//
// R-em-15's contract is copied EXACTLY, as the inset already copies it from ShowPCellPins: never
// layer geometry, never counted in LayoutFrameCounters, never reachable by any exporter, defaulting
// to false so every export / one-shot render draws no mesh BY CONSTRUCTION, with the toggle default
// at the VM layer. This method takes no LayoutFrameCounters — deliberately, so "contributes to no
// geometry count" is true by construction rather than by remembering not to increment one.
//
// The engine works in METRES (R-mom-2) and the canvas in DBU. The mapping is one scalar because
// PlanarExtractor deliberately does NOT translate or centre the geometry: metres = dbu / (dbuPerMicron
// × 1e6), so metres → DBU is a multiply and nothing else. Do not add a centring step to the extractor
// without also giving this overlay the offset.

using SkiaSharp;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Renderers;

public static partial class LayoutRenderer
{
    /// <summary>Cell-boundary stroke, device pixels — constant at any zoom, like every other overlay
    /// stroke in this renderer.</summary>
    private const float PlanarMeshStrokeDevicePixels = 0.9f;

    /// <summary>
    /// Below this many device pixels per cell the individual boundaries stop being readable and the
    /// overlay would just be a solid wash. It then draws the mesh EXTENT only, so a user who has
    /// zoomed out still sees that a mesh exists and where — rather than a misleading solid block.
    /// </summary>
    private const double PlanarMeshMinCellDevicePixels = 2.5;

    /// <summary>
    /// Draws the surface mesh over the artwork, in world coordinates.
    ///
    /// <para><b>L8e provision, and the whole of it.</b> §10.5's current-density heat map is a
    /// PER-CELL SCALAR added later, not a rewrite: <paramref name="cellScalar"/> is that scalar, and
    /// passing null — which is all L8b ever does — takes the plain cell-boundary path. One
    /// colour-per-cell path with a null scalar today is the entire provision needed, and it is
    /// deliberately not exercised here because a heat map needs a SOLUTION to display and there is
    /// no solver until L8d.</para>
    /// </summary>
    internal static void DrawPlanarMeshOverlay(
        SKCanvas canvas, PlanarMeshReport report, LayoutRenderTheme theme,
        PathSpace ps, double dbuPerMicron, double scaleUm,
        Func<int, double>? cellScalar = null)
    {
        var cells = report.Mesh.Cells;
        if (cells.Count == 0) return;

        // metres → DBU. The extractor preserves layout coordinates, so there is no offset.
        double toDbu = dbuPerMicron * 1e6;

        float stroke = DevicePixelsToPathSpace(scaleUm, PlanarMeshStrokeDevicePixels);

        // Device pixels per metre = (device px per micron) × (microns per metre).
        double pxPerMetre = scaleUm * 1e6;
        double smallestCellPx = Math.Min(report.MinCellEdgeM, report.MaxCellEdgeM) * pxPerMetre;

        using var cellPaint = new SKPaint
        {
            Color       = theme.PlanarMeshCell.WithAlpha(200),
            IsStroke    = true,
            StrokeWidth = stroke,
            IsAntialias = true,
        };

        if (smallestCellPx < PlanarMeshMinCellDevicePixels && cellScalar is null)
        {
            // Too dense to read. Draw the mesh's own extent rather than a wash that says nothing.
            double ex0 = double.PositiveInfinity, ey0 = double.PositiveInfinity;
            double ex1 = double.NegativeInfinity, ey1 = double.NegativeInfinity;
            foreach (var c in cells)
            {
                ex0 = Math.Min(ex0, c.XMin); ey0 = Math.Min(ey0, c.YMin);
                ex1 = Math.Max(ex1, c.XMax); ey1 = Math.Max(ey1, c.YMax);
            }
            using var dash = new SKPaint
            {
                Color = theme.PlanarMeshCell.WithAlpha(200), IsStroke = true, StrokeWidth = stroke,
                IsAntialias = true, PathEffect = SKPathEffect.CreateDash([6f * stroke, 4f * stroke], 0),
            };
            canvas.DrawRect(
                SKRect.Create(ps.X(ex0 * toDbu), ps.Y(ey1 * toDbu),
                              (float)((ex1 - ex0) * toDbu * ps.DbuToUm),
                              (float)((ey1 - ey0) * toDbu * ps.DbuToUm)),
                dash);
            return;
        }

        if (cellScalar is null)
        {
            // One batched path for every cell boundary — the same batching rule DrawLayer's opaque
            // stroke pass uses, and for the same reason: overlapping identical strokes are idempotent.
            using var path = new SKPath();
            foreach (var c in cells)
            {
                float x0 = ps.X(c.XMin * toDbu), x1 = ps.X(c.XMax * toDbu);
                float y0 = ps.Y(c.YMax * toDbu), y1 = ps.Y(c.YMin * toDbu);   // Y is flipped in path space
                path.AddRect(SKRect.Create(x0, y0, x1 - x0, y1 - y0));
            }
            canvas.DrawPath(path, cellPaint);
            return;
        }

        // L8e's path: one colour per cell. Not reached in L8b.
        using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            fill.Color = HeatColor(cellScalar(i), theme);
            float x0 = ps.X(c.XMin * toDbu), x1 = ps.X(c.XMax * toDbu);
            float y0 = ps.Y(c.YMax * toDbu), y1 = ps.Y(c.YMin * toDbu);
            canvas.DrawRect(SKRect.Create(x0, y0, x1 - x0, y1 - y0), fill);
        }
    }

    /// <summary>
    /// The L8e heat-map ramp. <b>Single-hue, alpha-graded, over the mesh's own colour</b> rather than
    /// a rainbow: a rainbow needs a legend to be read at all, reads differently to a colour-blind
    /// user, and implies precision this quantity does not have. Alpha over one hue reads as "more
    /// current here" with no legend, and the SCALE — with its units and its normalisation — is stated
    /// in words beside it (<c>PlanarCurrentDensityMap.ScaleCaption</c>, R-res-8) rather than left to
    /// be guessed from the colours.
    /// </summary>
    private static SKColor HeatColor(double t, LayoutRenderTheme theme)
    {
        t = double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;
        var c = theme.PlanarMeshCell;
        return new SKColor(c.Red, c.Green, c.Blue, (byte)Math.Round(40 + 200 * t));
    }

    /// <summary>
    /// §10.6's "show the de-embedding reference plane in the layout, so its location is never a
    /// mystery" — a line across the conductor at each port's own cut.
    ///
    /// <para><b>Nothing here is user-positionable and nothing here is computed.</b> L8d's D2 fixes
    /// the plane at the shared edge of the two outermost cells, one cell in from the drawn metal,
    /// with deliberately no offset knob — offering one would offer a way to get a different answer
    /// for the same structure. So this draws <c>PlanarPortResolution.ReferencePlaneM</c> verbatim,
    /// between that port's own outermost transverse gridlines.</para>
    /// </summary>
    internal static void DrawPlanarReferencePlanes(
        SKCanvas canvas, IReadOnlyList<PlanarPortResolution> ports, LayoutRenderTheme theme,
        PathSpace ps, double dbuPerMicron, double scaleUm)
    {
        if (ports.Count == 0) return;

        double toDbu = dbuPerMicron * 1e6;
        float stroke = DevicePixelsToPathSpace(scaleUm, 2.0f);

        using var paint = new SKPaint
        {
            Color       = theme.Selection,
            IsStroke    = true,
            StrokeWidth = stroke,
            IsAntialias = true,
            StrokeCap   = SKStrokeCap.Round,
        };

        foreach (var p in ports)
        {
            if (p.TransverseLines.Count < 2) continue;
            double t0 = p.TransverseLines[0] * toDbu;
            double t1 = p.TransverseLines[^1] * toDbu;
            double plane = p.ReferencePlaneM * toDbu;

            // Direction X means current flows along x, so the cut is a line of constant x.
            if (p.Direction == PlanarBasisDirection.X)
                canvas.DrawLine(ps.X(plane), ps.Y(t0), ps.X(plane), ps.Y(t1), paint);
            else
                canvas.DrawLine(ps.X(t0), ps.Y(plane), ps.X(t1), ps.Y(plane), paint);
        }
    }
}
