// brief-ipx-1 R-ipx1-2 — the Nets panel's nets over the artwork.
//
// A net can be most of a board, and a user arrows down the list: each key is a repaint. So a frame draws one merged path per
// layer per net (one stroke, plus one fill under Dim other copper) and never walks the net's shapes. The merged paths are built
// once per net per document state and held on the LayoutNetHighlight, which the Nets panel keeps by name.
//
// Dimming is a wash of the canvas colour over what is already drawn, not the board drawn again at lower opacity, so it costs one
// rectangle and leaves the tile cache alone. The net's own copper is then filled back at its layer's colour. Each shape's path is
// added with one winding, because the shapes of a net overlap (a trace ends on a pad) and under a winding fill two opposite
// directions cancel, which punches a hole in the copper exactly where they meet. Holes stay holes: a polygon's are wound opposite
// to its outer ring (AddHoleRings) and a reversal reverses both.

using CircuitRF.Design.Layout;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>One net to outline: its shapes, and the geometry they merge to, built on first draw and kept.</summary>
/// <remarks><b>Holds committed shapes as they are now.</b> Whoever made this replaces it when the document changes.</remarks>
public sealed class LayoutNetHighlight(string net, IReadOnlyList<LayoutShape> shapes)
{
    private LayoutRenderer.NetHighlightGeometry? _geometry;
    private readonly object _gate = new();

    /// <summary>The net's name.</summary>
    public string Net { get; } = net;

    /// <summary>The shapes outlined.</summary>
    public IReadOnlyList<LayoutShape> Shapes { get; } = shapes;

    /// <summary>How many times the merged paths were built. Once, however many frames draw them.</summary>
    public int GeometryBuilds { get; private set; }

    internal LayoutRenderer.NetHighlightGeometry GeometryFor(int dbuPerMicron)
    {
        lock (_gate)
        {
            if (_geometry is { } built) return built;
            GeometryBuilds++;
            return _geometry = LayoutRenderer.BuildNetHighlightGeometry(Shapes, dbuPerMicron);
        }
    }
}

public static partial class LayoutRenderer
{
    private const double NetHighlightStrokeDevicePixels = 1.6;

    /// <summary>How much of the canvas colour Dim other copper lays over the artwork, out of 255.</summary>
    private const byte NetDimWashAlpha = 190;

    /// <summary>A net's paths, per layer, in a path space of their own whose origin is <paramref name="OriginX"/>,
    /// <paramref name="OriginY"/>: the frame's own origin moves with the view, and a path built in it would have to be rebuilt.</summary>
    internal sealed record NetHighlightGeometry(long OriginX, long OriginY, IReadOnlyList<(LayerKey Layer, SKPath Path)> Layers);

    internal static NetHighlightGeometry BuildNetHighlightGeometry(IReadOnlyList<LayoutShape> shapes, int dbuPerMicron)
    {
        var bounds = Bbox.Empty;
        foreach (var shape in shapes) bounds = bounds.Union(LayoutGeometry.BboxOf(shape));
        long originX = bounds.IsEmpty ? 0 : bounds.MinX, originY = bounds.IsEmpty ? 0 : bounds.MinY;

        var local = new PathSpace(originX, originY, 1.0 / System.Math.Max(1, dbuPerMicron));
        var byLayer = new Dictionary<LayerKey, SKPath>();
        foreach (var shape in shapes)
        {
            using var outline = BuildOutlinePathForSelection(shape, local, dbuPerMicron: dbuPerMicron);
            if (outline is null || outline.IsEmpty) continue;
            if (!byLayer.TryGetValue(shape.Layer, out var merged)) byLayer[shape.Layer] = merged = new SKPath();
            if (SignedPathArea(outline) < 0) merged.AddPathReverse(outline);
            else merged.AddPath(outline);
        }
        return new NetHighlightGeometry(originX, originY, [.. byLayer.Select(e => (e.Key, e.Value))]);
    }

    /// <summary>The signed area of a path's contours, read off its on-curve points: only its sign is used.</summary>
    private static double SignedPathArea(SKPath path)
    {
        double area = 0;
        SKPoint start = default, last = default;
        var points = new SKPoint[4];
        using var it = path.CreateRawIterator();
        SKPathVerb verb;
        while ((verb = it.Next(points)) != SKPathVerb.Done)
        {
            SKPoint? end = verb switch
            {
                SKPathVerb.Line  => points[1],
                SKPathVerb.Quad  => points[2],
                SKPathVerb.Conic => points[2],
                SKPathVerb.Cubic => points[3],
                SKPathVerb.Close => start,
                _                => null,
            };
            if (verb == SKPathVerb.Move)
            {
                area += (double)last.X * start.Y - (double)start.X * last.Y;
                start = last = points[0];
            }
            else if (end is { } e)
            {
                area += (double)last.X * e.Y - (double)e.X * last.Y;
                last = e;
            }
        }
        return area + ((double)last.X * start.Y - (double)start.X * last.Y);
    }

    private static void DrawNetHighlights(
        SKCanvas canvas, IReadOnlyList<LayoutNetHighlight> highlights, bool dim,
        IReadOnlyDictionary<LayerKey, LayerDef>? layerMap, LayoutRenderTheme theme, PathSpace ps, LayoutViewport vp,
        double scaleUm, int dbuPerMicron, LayoutFrameCounters counters)
    {
        var visibleRect = NormalizedRect(ps.X(vp.VisibleMinX), ps.Y(vp.VisibleMinY), ps.X(vp.VisibleMaxX), ps.Y(vp.VisibleMaxY));
        float pad = DevicePixelsToPathSpace(scaleUm, NetHighlightStrokeDevicePixels);

        // On every VISIBLE layer, lowest first so a fill stacks as the artwork does, and only what can reach the screen.
        var drawn = new List<(LayerDef Def, NetHighlightGeometry Net, SKPath Path)>();
        foreach (var highlight in highlights)
        {
            var net = highlight.GeometryFor(dbuPerMicron);
            float dx = ps.X(net.OriginX), dy = ps.Y(net.OriginY);
            foreach (var (layer, path) in net.Layers)
            {
                var def = layerMap is not null && layerMap.TryGetValue(layer, out var found) ? found : FallbackPalette.For(layer);
                if (!def.Visible) continue;
                var box = path.Bounds;
                box.Offset(dx, dy);
                box.Inflate(pad, pad);
                if (box.IntersectsWith(visibleRect)) drawn.Add((def, net, path));
            }
        }
        drawn.Sort(static (a, b) => a.Def.ZOrder.CompareTo(b.Def.ZOrder));

        if (dim)
        {
            using var wash = new SKPaint { Style = SKPaintStyle.Fill, Color = theme.Background.WithAlpha(NetDimWashAlpha) };
            canvas.DrawRect(visibleRect, wash);

            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            foreach (var (def, net, path) in drawn)
            {
                fill.Color = new SKColor(def.Color.R, def.Color.G, def.Color.B,
                                         (byte)System.Math.Clamp(System.Math.Round(def.FillOpacity * 255.0), 0, 255));
                DrawNetPath(path, net, fill);
            }
        }

        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, NetHighlightStrokeDevicePixels),
            Color = theme.NetHighlight,
        };
        foreach (var (_, net, path) in drawn) DrawNetPath(path, net, stroke);

        void DrawNetPath(SKPath path, NetHighlightGeometry net, SKPaint paint)
        {
            canvas.Save();
            canvas.Translate(ps.X(net.OriginX), ps.Y(net.OriginY));
            canvas.DrawPath(path, paint);
            canvas.Restore();
            counters.NetHighlightDrawCalls++;
        }
    }
}
