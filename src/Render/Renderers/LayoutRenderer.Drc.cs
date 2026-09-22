// L5b — the DRC violation-marker overlay (docs/design/layout-view.md §9A.1).
//
// §9A.1 requires a violation to carry "a GEOMETRIC MARKER (the region that actually violates), not
// just a point" — "a spacing violation somewhere on M1 is not usable." So this draws the region the
// engine computed, in world space, over the artwork: a translucent fill so the metal underneath stays
// readable, plus a solid outline so a very thin violation (a one-DBU-wide gap band is the common case)
// is still visible at any zoom.
//
// R-em-15's contract is copied exactly, as every other overlay in this renderer already copies it:
// never layer geometry, never counted in LayoutFrameCounters, never reachable by an exporter. That
// last one is true by construction here rather than by a flag — a marker lives on LayoutOverlay, and
// every export path passes Overlay = null.

using System.Linq;
using CircuitRF.Diagnostics;
using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    /// <summary>Marker outline, device pixels — constant at any zoom, like every other overlay stroke.</summary>
    private const float DrcMarkerStrokeDevicePixels = 1.4f;

    /// <summary>The selected row's marker draws heavier so click-to-zoom lands somewhere obvious.</summary>
    private const float DrcSelectedStrokeDevicePixels = 2.6f;

    private const byte DrcFillAlpha        = 70;
    private const byte DrcWaivedFillAlpha  = 30;
    private const byte DrcOutlineAlpha     = 235;

    /// <summary>
    /// A marker whose on-screen extent falls below this many device pixels is drawn as a fixed-size
    /// CROSSHAIR at its own centre instead of as a region. Not cosmetic: the most common violation is
    /// a hairline — a gap band a fraction of a micron wide — which at any usable zoom paints under one
    /// pixel and would be invisible. A violation the user cannot see is a check that did not run.
    /// </summary>
    private const double DrcMinRegionDevicePixels = 5.0;

    private const float DrcCrosshairDevicePixels = 9.0f;

    internal static void DrawDrcMarkers(
        SKCanvas canvas, IReadOnlyList<DrcMarker> markers, LayoutRenderTheme theme,
        PathSpace ps, double scaleUm)
        => DrawFindingMarkers(canvas, markers.Select(m => (
               m.Rings,
               m.Waived
                   ? theme.DrcWaived
                   : m.Severity == DrcSeverity.Error ? theme.DrcError : theme.DrcWarning,
               m.Waived, m.Selected)), ps, scaleUm);

    /// <summary>
    /// brief-lvs-12-gui.md R-lvs12-2a/5b: the LVS findings, drawn by the same routine.
    /// </summary>
    /// <remarks>
    /// <b>The DRC marker colours, deliberately reused.</b> They are named for the surface that first
    /// needed them, but what they mean is "a finding of this severity, over the artwork" — and three
    /// more <c>ColorRole</c>s would have to be added to the <c>.ccolor</c> format, the theme editor
    /// and every shipped theme to say the same thing twice. The two panels toggle independently, so
    /// a user who wants to tell them apart turns one off.
    ///
    /// <para><see cref="DiagnosticSeverity.Info"/> draws in the warning colour: an info-level finding
    /// with a region is still somewhere the user asked to be shown, and a fourth colour for it would
    /// be a distinction nothing else in the panel makes.</para>
    /// </remarks>
    internal static void DrawLvsMarkers(
        SKCanvas canvas, IReadOnlyList<LvsFindingMarker> markers, LayoutRenderTheme theme,
        PathSpace ps, double scaleUm)
        => DrawFindingMarkers(canvas, markers.Select(m => (
               m.Rings,
               m.Waived
                   ? theme.DrcWaived
                   : m.Severity == DiagnosticSeverity.Error ? theme.DrcError : theme.DrcWarning,
               m.Waived, m.Selected)), ps, scaleUm);

    /// <summary>
    /// One region per marker: a translucent fill so the metal underneath stays readable, a solid
    /// outline so a hairline is visible at any zoom, and the crosshair fallback below.
    /// </summary>
    /// <remarks>
    /// <b>Built once and used by both callers above</b> — the DRC panel and the LVS panel — because
    /// the part worth sharing is not the colour but the <see cref="DrcMinRegionDevicePixels"/> rule:
    /// a marker the user cannot see is a check that did not run, and that is as true of an LVS
    /// finding on a via as it is of a spacing violation.
    /// </remarks>
    private static void DrawFindingMarkers(
        SKCanvas canvas,
        IEnumerable<(IReadOnlyList<long[]> Rings, SKColor Colour, bool Muted, bool Selected)> markers,
        PathSpace ps, double scaleUm)
    {
        float stroke         = DevicePixelsToPathSpace(scaleUm, DrcMarkerStrokeDevicePixels);
        float selectedStroke = DevicePixelsToPathSpace(scaleUm, DrcSelectedStrokeDevicePixels);
        float crossHalf      = DevicePixelsToPathSpace(scaleUm, DrcCrosshairDevicePixels) / 2f;

        using var fillPaint   = new SKPaint { IsStroke = false, IsAntialias = true };
        using var strokePaint = new SKPaint { IsStroke = true,  IsAntialias = true };

        foreach (var (rings, colour, muted, selected) in markers)
        {
            if (rings.Count == 0) continue;

            using var path = new SKPath();
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

            foreach (var ring in rings)
            {
                if (ring.Length < 6) continue;   // fewer than 3 points bounds no region
                for (int i = 0; i < ring.Length; i += 2)
                {
                    float x = ps.X(ring[i]);
                    float y = ps.Y(ring[i + 1]);
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
                path.Close();
            }

            if (path.IsEmpty) continue;

            fillPaint.Color = colour.WithAlpha(muted ? DrcWaivedFillAlpha : DrcFillAlpha);
            canvas.DrawPath(path, fillPaint);

            strokePaint.Color       = colour.WithAlpha(DrcOutlineAlpha);
            strokePaint.StrokeWidth = selected ? selectedStroke : stroke;
            canvas.DrawPath(path, strokePaint);

            // See DrcMinRegionDevicePixels: a hairline region gets a crosshair so it can be found.
            double widthPx  = (maxX - minX) * scaleUm;
            double heightPx = (maxY - minY) * scaleUm;
            if (widthPx >= DrcMinRegionDevicePixels && heightPx >= DrcMinRegionDevicePixels) continue;

            float cx = (float)((minX + maxX) / 2.0);
            float cy = (float)((minY + maxY) / 2.0);
            canvas.DrawLine(cx - crossHalf, cy, cx + crossHalf, cy, strokePaint);
            canvas.DrawLine(cx, cy - crossHalf, cx, cy + crossHalf, strokePaint);
        }
    }
}
