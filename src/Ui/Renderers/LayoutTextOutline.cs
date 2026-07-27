// Bridges a Label's text into DBU-space glyph contours (R-lbl-4/R-lbl-6, docs/sonnet-briefs/
// brief-layout-label-fix-and-text-flatten.md) — the ONE place text-to-polygon flattening touches
// SkiaSharp. Everything downstream (flattening the resulting Cubic edges via LayoutFlattener, nesting
// via Clipper2, emitting PolygonShapes, undo) is framework-free, in
// CircuitRF.Ui.Layout.LayoutTextFlatten — this file only builds its INPUT.

using System.Collections.Generic;
using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

public static class LayoutTextOutline
{
    /// <summary>Test-only override (set only by <c>CircuitRF.Ui.Tests</c>, via <c>InternalsVisibleTo</c>)
    /// substituting <c>SKTypeface.Default</c> — the one typeface guaranteed loadable without a live
    /// Avalonia app host — so the FULL flatten pipeline, including every VM entry point
    /// (<c>LayoutEditorViewModel.FlattenSelectionToPolygon</c>/<c>FlattenAllCurves</c>), can be
    /// exercised headlessly. Production code (and <see cref="LayoutRenderer.DrawLabelText"/>) never
    /// sets this — every real label still flattens with the exact typeface it renders with.</summary>
    internal static SKTypeface? TestOverrideTypeface;

    /// <summary>The ONE mapping from a label's <see cref="LabelFontStyle"/> to the typeface it
    /// renders/flattens with — shared by <see cref="LayoutRenderer.DrawLabelText"/>,
    /// <see cref="LayoutRenderer.MeasureLabelWorldBbox"/>, and <see cref="BuildGlyphContours"/>, so all
    /// three can never disagree about what a given label looks like. Mirrors
    /// <c>SchematicRenderer</c>'s <c>TextPrimitive.FontStyle</c> mapping (same four
    /// <c>SkiaFonts.Plex*</c> targets — Condensed intentionally maps to the Light weight, matching that
    /// precedent exactly, not a typo). <see cref="TestOverrideTypeface"/> always wins when set, so a
    /// headless test never needs to know or vary a label's style to substitute a loadable typeface.</summary>
    public static SKTypeface ResolveTypeface(LabelFontStyle style) => TestOverrideTypeface ?? style switch
    {
        LabelFontStyle.Bold      => SkiaFonts.PlexBold,
        LabelFontStyle.Italic    => SkiaFonts.PlexItalic,
        LabelFontStyle.Condensed => SkiaFonts.PlexLight,
        _                        => SkiaFonts.PlexRegular,
    };

    /// <summary>
    /// One <see cref="CurveShape"/> per glyph CONTOUR — 'O' has two (outer ring + inner counter),
    /// '8' has three, 'i' has two disconnected pieces (dot + stem). Nesting (which contour is a hole
    /// vs. a separate outer boundary) is NOT decided here — that is <c>LayoutTextFlatten</c>'s job, via
    /// Clipper2. Uses <see cref="SkiaFonts.PlexRegular"/>, the SAME typeface
    /// <c>LayoutRenderer.DrawLabelText</c> renders with, and mirrors that method's transform EXACTLY
    /// (including its Y-down-path-space rotation-sign table) so the flattened result matches what the
    /// user actually sees rather than a re-derived approximation. <paramref name="label"/>'s own DBU
    /// <c>Height</c> is used directly as the SkFont size — Skia doesn't care about units, so the
    /// returned glyph path is already in exact DBU-scale coordinates, needing no further scaling
    /// (only rotation + translation, both applied below). Returns an empty list for blank text or a
    /// non-positive height. <paramref name="typeface"/> is test-only visibility into the algorithm
    /// (headless xunit tests cannot resolve <see cref="SkiaFonts.PlexRegular"/> at all — it loads via
    /// Avalonia's <c>AssetLoader</c>, which throws <c>InvalidOperationException</c> with no live
    /// Avalonia app host; confirmed empirically, not assumed) — production code never passes it, so
    /// every real label still flattens with the exact typeface it renders with.
    /// </summary>
    public static List<CurveShape> BuildGlyphContours(LabelShape label, SKTypeface? typeface = null)
    {
        var contours = new List<CurveShape>();
        if (string.IsNullOrEmpty(label.Text) || label.Height <= 0) return contours;

        using var font = new SKFont(typeface ?? ResolveTypeface(label.Style), label.Height);
        using var glyphPath = font.GetTextPath(label.Text, new SKPoint(0, 0));

        // Mirrors LayoutRenderer.DrawLabelText's rotation table exactly — path space is Y-down, so the
        // DBU-space (Y-up) CCW rotation is negated for R90/R270.
        float rotationDeg = label.Rotation switch
        {
            LayoutRotation.R90  => -90f,
            LayoutRotation.R180 => 180f,
            LayoutRotation.R270 => 90f,
            _                   => 0f,
        };
        if (rotationDeg != 0f)
            glyphPath.Transform(SKMatrix.CreateRotationDegrees(rotationDeg));

        // DrawLabelText draws at path-space (label.X, label.Y) with Y flipped once (DBU is Y-up, path
        // space is Y-down) — the same single negation applied here, directly in DBU, no PathSpace/zoom
        // involved (flattening must be viewport-independent, unlike rendering).
        (long X, long Y) ToDbu(SKPoint p) =>
            (label.X + (long)System.Math.Round(p.X), label.Y - (long)System.Math.Round(p.Y));

        var xy = new List<long>();
        var edges = new List<LayoutEdge>();

        void FlushContour()
        {
            if (xy.Count >= 6) // >= 3 vertices — anything smaller is a degenerate/empty contour
                contours.Add(new CurveShape { Layer = label.Layer, Net = label.Net, Xy = [.. xy], Edges = [.. edges] });
            xy = [];
            edges = [];
        }

        using var iter = glyphPath.CreateIterator(false);
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iter.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    FlushContour();
                    var (mx, my) = ToDbu(pts[0]);
                    xy.Add(mx); xy.Add(my);
                    break;

                case SKPathVerb.Line:
                {
                    var (lx, ly) = ToDbu(pts[1]);
                    xy.Add(lx); xy.Add(ly);
                    edges.Add(new LayoutEdge { Kind = EdgeKind.Line });
                    break;
                }

                case SKPathVerb.Quad:
                {
                    // Exact quadratic-to-cubic degree elevation — LayoutEdge has no Quad kind, and
                    // elevation (unlike flattening) loses nothing: C1 = P0 + 2/3(Pc-P0), C2 = P2 + 2/3(Pc-P2).
                    var p0 = pts[0]; var pc = pts[1]; var p2 = pts[2];
                    var c1 = new SKPoint(p0.X + 2f / 3f * (pc.X - p0.X), p0.Y + 2f / 3f * (pc.Y - p0.Y));
                    var c2 = new SKPoint(p2.X + 2f / 3f * (pc.X - p2.X), p2.Y + 2f / 3f * (pc.Y - p2.Y));
                    AddCubicEdge(xy, edges, ToDbu(c1), ToDbu(c2), ToDbu(p2));
                    break;
                }

                case SKPathVerb.Cubic:
                    AddCubicEdge(xy, edges, ToDbu(pts[1]), ToDbu(pts[2]), ToDbu(pts[3]));
                    break;

                case SKPathVerb.Close:
                    // Glyph contours are drawn fully closed (the last on-curve point already equals the
                    // Move point) — drop that duplicate closing vertex so Xy matches every other closed
                    // shape's "never repeat vertex 0" convention. Deliberately does NOT also drop the
                    // trailing edge: that edge (leaving the now-second-to-last vertex) IS the implicit
                    // closing edge LayoutFlattener.FlattenClosedEdgeList expects at index n-1 — removing
                    // it would silently straight-line what may be a curved closing segment.
                    if (xy.Count >= 4 && xy[^2] == xy[0] && xy[^1] == xy[1])
                        xy.RemoveRange(xy.Count - 2, 2);
                    break;
            }
        }
        FlushContour();
        return contours;
    }

    private static void AddCubicEdge(List<long> xy, List<LayoutEdge> edges,
        (long X, long Y) c1, (long X, long Y) c2, (long X, long Y) end)
    {
        xy.Add(end.X); xy.Add(end.Y);
        edges.Add(new LayoutEdge { Kind = EdgeKind.Cubic, C1X = c1.X, C1Y = c1.Y, C2X = c2.X, C2Y = c2.Y });
    }
}
