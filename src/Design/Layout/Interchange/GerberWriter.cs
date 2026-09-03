// Gerber RS-274X / X2 per-layer writer (docs/sonnet-briefs/brief-L4c-gerber-export.md §2/§3). One file
// per layer — the caller (GerberExport) has already grouped world-space, hierarchy-flattened shapes by
// LayerKey and converted labels to polygons upstream, so this file only turns ONE layer's shapes into
// bytes. Coordinates are written as the literal DBU integer (R-L4c-1 — GerberFormat already guarantees
// 1 output unit == 1 DBU). Format-specific: touches only text/records, no CellFolder/Messages/dialog
// concerns, matching GdsiiWriter/DxfWriter's own scope.

using Clipper2Lib;

using System.Globalization;

namespace CircuitRF.Design.Layout.Interchange;

public static class GerberWriter
{
    /// <summary>What actually happened writing one layer's file — the counters the fidelity dialog
    /// reports, produced by the SAME write path the real export uses (mirrors <c>GdsiiExportSummary</c>'s
    /// own "the preview can never disagree with the write" discipline).</summary>
    public sealed record LayerWriteResult(
        int ArcEdgesWritten,
        int CubicEdgesFlattened,
        int HolesWritten,
        int CirclesFlashed,
        int ViasFlashed,
        int PathsAsStroke,
        int PathsAsRegion);

    public static LayerWriteResult Write(
        Stream stream, LayerDef? layerDef, IReadOnlyList<LayoutShape> shapes,
        GerberFormat format, Technology? tech, DateTime creationTimeUtc)
    {
        var apertures = new GerberApertureTable();
        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case CircleShape c: apertures.CircleAperture(c.R * 2); break;
                case ViaShape v: apertures.CircleAperture(v.PadSize); break;
                case PathShape p when IsStroke(p): apertures.CircleAperture(p.Width); break;
            }
        }

        using var w = new StreamWriter(stream, System.Text.Encoding.ASCII, -1, leaveOpen: true) { NewLine = "\n" };

        w.WriteLine($"%TF.GenerationSoftware,circuitRF,{Version}*%");
        // InvariantCulture is load-bearing, not decoration. In a CUSTOM date format string ':' is the
        // culture's TIME-SEPARATOR PLACEHOLDER, not a literal colon — so under a Finnish locale this
        // attribute came out as "2026-08-27T14.23.05Z", which is not ISO-8601 and not what the Gerber
        // spec's %TF.CreationDate demands. Caught by FormatCultureInvarianceTests, which is why that
        // gate probes fi-FI as well as de-DE (brief-localization-groundwork.md §5).
        w.WriteLine($"%TF.CreationDate,{creationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}*%");
        if (layerDef?.Interchange?.GerberFileFunction is { Length: > 0 } fn)
            w.WriteLine($"%TF.FileFunction,{fn}*%");
        w.WriteLine("%TF.FilePolarity,Positive*%");
        w.WriteLine("%MOMM*%");
        w.WriteLine($"%FSLAX{format.DigitPair}Y{format.DigitPair}*%");
        foreach (var (code, diameter) in apertures.Ordered)
            w.WriteLine($"%ADD{code}C,{format.FormatDecimalMm(diameter)}*%");

        // R-L4c-3: G75 (multi-quadrant) once, before ANY geometry — trivially satisfies "before any
        // arc, always" without needing to track whether a given file happens to contain one. G74
        // (single-quadrant) is deprecated and its I/J offsets mean something different; never emitted.
        w.WriteLine("G01*");
        w.WriteLine("G75*");

        int arcs = 0, cubics = 0, holes = 0, circles = 0, vias = 0, strokes = 0, regions = 0;
        string? currentNet = NoNetSentinel;
        int currentAperture = -1;

        foreach (var shape in shapes)
        {
            EmitNetAttributeIfChanged(w, shape.Net, ref currentNet);

            switch (shape)
            {
                case RectShape rect:
                    WriteRegionWithHoles(w, GerberGeometry.Ring([rect.X1, rect.Y1, rect.X2, rect.Y1, rect.X2, rect.Y2, rect.X1, rect.Y2], null), null, format, ref holes);
                    break;

                case PolygonShape poly:
                    WriteRegionWithHoles(w, GerberGeometry.Ring(poly.Xy, null), poly.Holes, format, ref holes);
                    break;

                case RoundedRectShape rr:
                {
                    var ring = GerberGeometry.RoundedRectRing(rr);
                    arcs += CountArcs(ring);
                    WriteRegionWithHoles(w, ring, null, format, ref holes);
                    break;
                }

                case CurveShape curve:
                {
                    var ring = GerberGeometry.Ring(curve.Xy, curve.Edges);
                    if (GerberGeometry.HasCubic(ring))
                    {
                        int cubicCount = 0;
                        foreach (var e in ring) if (e.Kind == EdgeKind.Cubic) cubicCount++;
                        long tol = LayoutFlattener.ResolveTolDbu(curve, tech);
                        ring = GerberGeometry.FlattenCubicsInRing(ring, tol);
                        cubics += cubicCount;
                    }
                    arcs += CountArcs(ring);
                    WriteRegionWithHoles(w, ring, curve.Holes, format, ref holes);
                    break;
                }

                case CircleShape c:
                    w.WriteLine("%LPD*%");
                    SelectAperture(w, apertures.CircleAperture(c.R * 2), ref currentAperture);
                    w.WriteLine($"X{format.FormatCoordinate(c.Cx)}Y{format.FormatCoordinate(c.Cy)}D03*");
                    circles++;
                    break;

                case ViaShape via:
                    w.WriteLine("%LPD*%");
                    SelectAperture(w, apertures.CircleAperture(via.PadSize), ref currentAperture);
                    w.WriteLine($"X{format.FormatCoordinate(via.X)}Y{format.FormatCoordinate(via.Y)}D03*");
                    vias++;
                    break;

                case PathShape path:
                    if (IsStroke(path))
                    {
                        WritePathStroke(w, path, format, tech, apertures, ref currentAperture, ref cubics, ref arcs);
                        strokes++;
                    }
                    else
                    {
                        WritePathAsRegion(w, path, tech, format);
                        regions++;
                    }
                    break;

                case BitmapShape:
                case LabelShape:
                    // §3.1b R10e / R-L4c-5 — never reach the writer: GerberExport filters bitmaps and
                    // converts labels to polygons (or omits port labels) before grouping shapes by layer.
                    throw new NotSupportedException(
                        $"{shape.GetType().Name} must be resolved by GerberExport before reaching GerberWriter.");

                default:
                    throw new NotSupportedException($"Gerber export does not support shape type {shape.GetType().Name}.");
            }
        }

        w.WriteLine("M02*");
        w.Flush();

        return new LayerWriteResult(arcs, cubics, holes, circles, vias, strokes, regions);
    }

    // ── Net attributes (R-L4c-2, %TO.N%) ──────────────────────────────────────

    private const string NoNetSentinel = "\0__no_net_seen_yet__";

    private static void EmitNetAttributeIfChanged(StreamWriter w, string? net, ref string? currentNet)
    {
        if (net == currentNet) return;
        w.WriteLine(net is { Length: > 0 } ? $"%TO.N,{EscapeAttribute(net)}*%" : "%TD.N*%");
        currentNet = net;
    }

    /// <summary>
    /// R-L4h-9: the four characters an attribute VALUE cannot carry literally, written as the
    /// <c>\uXXXX</c> escape the format defines for exactly this — <c>*</c> and <c>%</c> terminate a
    /// block, <c>,</c> separates fields, and <c>\</c> introduces an escape and so must escape itself
    /// or the reader undoes one the writer never wrote.
    ///
    /// <para><b>This replaced a substitution with <c>_</c>, on L4h round-trip evidence.</b> A net named
    /// <c>A,B*C%D</c> went out as <c>A_B_C_D</c> and came back as <c>A_B_C_D</c> — a rename, silent, and
    /// permanent after one cycle. It was the one loss on L4h §2's table that was ours and not the
    /// format's: a third-party tool round-trips such a name because it writes the escape, and
    /// <c>GerberReader</c> has undone <c>\uXXXX</c> since L4e (its R-L4e-18), so only this half was
    /// missing. <b>Files exported before this change carry the underscores</b> and re-import with the
    /// renamed nets; files exported after it carry the real name.</para>
    /// </summary>
    internal static string EscapeAttribute(string s)
    {
        if (s.AsSpan().IndexOfAny('*', '%', ',') < 0 && !s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c is '*' or '%' or ',' or '\\')
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    // ── Regions (Line/Arc boundary + Line-only holes) ─────────────────────────

    private static void WriteRegionWithHoles(
        StreamWriter w, List<GerberGeometry.RingEdge> outer, List<long[]>? holeRings, GerberFormat format, ref int holesWritten)
    {
        w.WriteLine("%LPD*%");
        w.WriteLine("G36*");
        WriteRingMoves(w, outer, format);
        w.WriteLine("G37*");

        if (holeRings is not { Count: > 0 }) return;

        foreach (var hole in holeRings)
        {
            w.WriteLine("%LPC*%");
            w.WriteLine("G36*");
            WriteRingMoves(w, GerberGeometry.Ring(hole, null), format);
            w.WriteLine("G37*");
            holesWritten++;
        }
        w.WriteLine("%LPD*%"); // restore dark polarity for whatever geometry follows
    }

    /// <summary>Writes one closed ring's D02 move-to-start followed by one D01 per edge, each preceded
    /// by its own G01 (line) or G02/G03 (arc, always after G75 was already set — R-L4c-3) — no modal
    /// state is tracked between edges; a redundant G-code line before every D01 costs a few bytes and
    /// removes an entire class of "which mode is currently active" bugs.</summary>
    private static void WriteRingMoves(StreamWriter w, List<GerberGeometry.RingEdge> ring, GerberFormat format)
    {
        if (ring.Count == 0) return;
        w.WriteLine("G01*");
        w.WriteLine($"X{format.FormatCoordinate(ring[0].X0)}Y{format.FormatCoordinate(ring[0].Y0)}D02*");
        foreach (var e in ring) WriteEdgeDraw(w, e, format);
    }

    private static void WriteEdgeDraw(StreamWriter w, GerberGeometry.RingEdge e, GerberFormat format)
    {
        if (e.Kind == EdgeKind.Arc && e.Bulge != 0)
        {
            var arc = LayoutArc.FromBulge(e.X0, e.Y0, e.X1, e.Y1, e.Bulge);
            long i = (long)Math.Round(arc.Cx) - e.X0;
            long j = (long)Math.Round(arc.Cy) - e.Y0;
            // Positive sweep is an increasing-angle (atan2) sweep in this codebase's Y-up DBU plane
            // (LayoutArc.FromBulge's own doc comment) — the same sense as counterclockwise in Gerber's
            // own Y-up coordinate plane, so positive sweep -> G03 (CCW), negative -> G02 (CW).
            w.WriteLine(arc.Sweep >= 0 ? "G03*" : "G02*");
            w.WriteLine($"X{format.FormatCoordinate(e.X1)}Y{format.FormatCoordinate(e.Y1)}I{format.FormatCoordinate(i)}J{format.FormatCoordinate(j)}D01*");
        }
        else
        {
            w.WriteLine("G01*");
            w.WriteLine($"X{format.FormatCoordinate(e.X1)}Y{format.FormatCoordinate(e.Y1)}D01*");
        }
    }

    private static int CountArcs(List<GerberGeometry.RingEdge> ring)
    {
        int n = 0;
        foreach (var e in ring) if (e.Kind == EdgeKind.Arc && e.Bulge != 0) n++;
        return n;
    }

    // ── Circle / Via flash, Path stroke/region — aperture selection ───────────

    private static void SelectAperture(StreamWriter w, int code, ref int currentAperture)
    {
        if (code == currentAperture) return;
        w.WriteLine($"D{code}*");
        currentAperture = code;
    }

    internal static bool IsStroke(PathShape path) => path.End == PathEndStyle.Round;

    private static void WritePathStroke(
        StreamWriter w, PathShape path, GerberFormat format, Technology? tech, GerberApertureTable apertures,
        ref int currentAperture, ref int cubicsFlattened, ref int arcsWritten)
    {
        w.WriteLine("%LPD*%");
        SelectAperture(w, apertures.CircleAperture(path.Width), ref currentAperture);

        var open = OpenRing(path.Xy, path.Edges);
        bool hadCubic = GerberGeometry.HasCubic(open);
        if (hadCubic)
        {
            long tol = LayoutFlattener.ResolveTolDbu(path, tech);
            int cubicCount = 0;
            foreach (var e in open) if (e.Kind == EdgeKind.Cubic) cubicCount++;
            open = GerberGeometry.FlattenCubicsInRing(open, tol);
            cubicsFlattened += cubicCount;
        }
        arcsWritten += CountArcs(open);

        if (open.Count == 0) return;
        w.WriteLine("G01*");
        w.WriteLine($"X{format.FormatCoordinate(open[0].X0)}Y{format.FormatCoordinate(open[0].Y0)}D02*");
        foreach (var e in open) WriteEdgeDraw(w, e, format);
    }

    /// <summary>Open (non-wrapping) edge walk — the Path/stroke analogue of <see cref="GerberGeometry.Ring"/>,
    /// which always wraps the last vertex back to the first (only correct for a CLOSED boundary).</summary>
    private static List<GerberGeometry.RingEdge> OpenRing(long[] xy, List<LayoutEdge>? edges)
    {
        int n = xy.Length / 2;
        var result = new List<GerberGeometry.RingEdge>(Math.Max(0, n - 1));
        for (int i = 0; i < n - 1; i++)
        {
            var e = edges is not null && i < edges.Count ? edges[i] : null;
            result.Add(new GerberGeometry.RingEdge(
                xy[2 * i], xy[2 * i + 1], xy[2 * (i + 1)], xy[2 * (i + 1) + 1],
                e?.Kind ?? EdgeKind.Line, e?.Bulge ?? 0.0,
                e?.C1X ?? 0, e?.C1Y ?? 0, e?.C2X ?? 0, e?.C2Y ?? 0));
        }
        return result;
    }

    /// <summary>R-L4c-4: non-Round end styles export via the path's GEOMETRY outline (Clipper2's
    /// InflatePaths on the flattened centerline, per <c>LayoutClipper.ToClipperPaths</c> — R-L1e-1's
    /// split: never the display outline) as one or more plain dark regions.</summary>
    private static void WritePathAsRegion(StreamWriter w, PathShape path, Technology? tech, GerberFormat format)
    {
        long tol = LayoutFlattener.ResolveTolDbu(path, tech);
        Paths64 outline = LayoutClipper.ToClipperPaths(path, tol);
        foreach (var p in outline)
        {
            if (p.Count < 3) continue;
            var xy = new long[p.Count * 2];
            for (int i = 0; i < p.Count; i++) { xy[2 * i] = p[i].X; xy[2 * i + 1] = p[i].Y; }
            int dummyHoles = 0;
            WriteRegionWithHoles(w, GerberGeometry.Ring(xy, null), null, format, ref dummyHoles);
        }
    }

    // ── Small helpers ──────────────────────────────────────────────────────────

    /// <summary>Shared with <see cref="GerberJobFile"/> (via <see cref="GerberExport"/>) so the
    /// per-file <c>%TF.GenerationSoftware%</c> attribute and the <c>.gbrjob</c>'s own header always
    /// report the identical version string.</summary>
    internal static readonly string Version =
        typeof(GerberWriter).Assembly.GetName().Version?.ToString() ?? "0.0";
}
