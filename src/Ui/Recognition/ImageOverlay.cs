// What the picture canvas draws over the picture — brief-img-5-dialog.md R-im5-3.
//
// Built ONLY from what the reading returned (the trace, and the recognition when a schematic is made) and carried into
// pixel space through the trace's own PixelFrame, so every item lies on the pixels it was read from. Nothing here reads
// the picture or decides anything: a part is boxed because the recognition found it, amber because the recognition
// left it unknown. The canvas draws these items and nothing else.
//
// The items are also the cross-probe: a parts-table row and an overlay item name each other by the board's designator,
// and a report anchor finds the item nearest to it — a pure mapping, so it is tested without rendering.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;

namespace CircuitRF.Ui.Recognition;

/// <summary>The overlay's classes, each toggled from the legend.</summary>
public enum ImageOverlayClass { Copper, Drill, Ignored, Part, Line, Port, Unknown }

/// <summary>A rectangle in the picture, pixels.</summary>
public readonly record struct PixelBounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CentreX => (Left + Right) / 2;
    public double CentreY => (Top + Bottom) / 2;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public PixelBounds Inflate(double d) => new(Left - d, Top - d, Right + d, Bottom + d);

    public static PixelBounds Of(IEnumerable<(double X, double Y)> points)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var (x, y) in points)
        {
            l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
        }
        return l > r ? default : new(l, t, r, b);
    }
}

/// <summary>One thing drawn over the picture.</summary>
/// <param name="Id">Unique within the overlay: <c>part:C1</c>, <c>line:MLIN2</c>, <c>port:1</c>, <c>copper:TOP:3</c>.</param>
/// <param name="Paths">Pixel paths — closed rings for copper, a box for a part, a centre line for a line.</param>
/// <param name="Closed">Whether the paths are rings.</param>
/// <param name="Label">What is written beside it, or null.</param>
/// <param name="Rgb">Its colour (a layer's own), 0xRRGGBB, or null for the class colour.</param>
/// <param name="Unknown">Something about it was not read: drawn in amber whatever its class.</param>
/// <param name="PartKey">The board designator of the part it is, for the parts-table cross-probe.</param>
/// <param name="Circle">For a drill or a port: its centre and radius, pixels.</param>
public sealed record ImageOverlayItem(
    string Id, ImageOverlayClass Class, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Paths, bool Closed,
    string? Label, int? Rgb, bool Unknown, string? PartKey, PixelBounds Bounds, (double X, double Y, double R)? Circle = null);

/// <summary>Everything the canvas draws over one reading.</summary>
public sealed class ImageOverlay
{
    private ImageOverlay(IReadOnlyList<ImageOverlayItem> items, ColourClusterSet? clusters, IReadOnlyList<int> ignored,
                         IReadOnlyList<int> unmapped, PixelFrame? frame)
    {
        Items = items;
        Clusters = clusters;
        IgnoredClusters = ignored;
        UnmappedClusters = unmapped;
        Frame = frame;
    }

    public static ImageOverlay Empty { get; } = new([], null, [], [], null);

    public IReadOnlyList<ImageOverlayItem> Items { get; }

    /// <summary>The picture's colours — the canvas hatches the ignored ones and flashes a hovered layer row's.</summary>
    public ColourClusterSet? Clusters { get; }

    /// <summary>Colours read as Ignore: hatched.</summary>
    public IReadOnlyList<int> IgnoredClusters { get; }

    /// <summary>Colours mapped to a layer the technology does not have: amber.</summary>
    public IReadOnlyList<int> UnmappedClusters { get; }

    /// <summary>Pixels → DBU, or null without a scale.</summary>
    public PixelFrame? Frame { get; }

    /// <summary>The item of the part the board calls <paramref name="boardRefdes"/>, or null.</summary>
    public ImageOverlayItem? ItemForPart(string? boardRefdes) =>
        boardRefdes is null ? null
        : Items.FirstOrDefault(i => i.Class == ImageOverlayClass.Part && string.Equals(i.PartKey, boardRefdes, StringComparison.OrdinalIgnoreCase));

    /// <summary>The topmost item under a pixel, the selectable classes first (a part over the copper it sits on).</summary>
    public ImageOverlayItem? HitTest(double x, double y, double tolerancePx, Func<ImageOverlayClass, bool> shown)
    {
        ImageOverlayItem? best = null;
        double bestArea = double.MaxValue;
        foreach (var item in Items)
        {
            if (!shown(item.Class) && !(item.Unknown && shown(ImageOverlayClass.Unknown))) continue;
            if (!item.Bounds.Inflate(tolerancePx).Contains(x, y)) continue;
            int rank = item.Class switch { ImageOverlayClass.Part or ImageOverlayClass.Port => 0, ImageOverlayClass.Line or ImageOverlayClass.Drill => 1, _ => 2 };
            double area = rank * 1e12 + Math.Max(1, item.Bounds.Width * item.Bounds.Height);
            if (item.Class == ImageOverlayClass.Line && DistanceToPaths(item, x, y) > tolerancePx) continue;
            if (area < bestArea) { bestArea = area; best = item; }
        }
        return best;
    }

    /// <summary>The item a report anchor points at: the nearest part, port or line within a few pixels, else null.</summary>
    public ImageOverlayItem? ItemForAnchor(RecognitionAnchor anchor)
    {
        if (Frame is not { } f) return null;
        var (x, y) = f.ToPixel(anchor.X, anchor.Y);
        return Items.Where(i => i.Class is ImageOverlayClass.Part or ImageOverlayClass.Port or ImageOverlayClass.Line or ImageOverlayClass.Drill)
                    .Select(i => (Item: i, D: i.Bounds.Contains(x, y) ? 0 : DistanceToBounds(i.Bounds, x, y)))
                    .Where(t => t.D <= 6)
                    .OrderBy(t => t.D).ThenBy(t => t.Item.Bounds.Width * t.Item.Bounds.Height)
                    .Select(t => t.Item).FirstOrDefault();
    }

    /// <summary>A DBU point in pixels, or null without a scale.</summary>
    public (double X, double Y)? ToPixel(long x, long y) => Frame is { } f ? f.ToPixel(x, y) : null;

    private static double DistanceToBounds(PixelBounds b, double x, double y)
    {
        double dx = Math.Max(0, Math.Max(b.Left - x, x - b.Right)), dy = Math.Max(0, Math.Max(b.Top - y, y - b.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToPaths(ImageOverlayItem item, double x, double y)
    {
        double best = double.MaxValue;
        foreach (var path in item.Paths)
            for (int i = 1; i < path.Count; i++)
            {
                var (ax, ay) = path[i - 1];
                var (bx, by) = path[i];
                double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
                double t = len2 == 0 ? 0 : Math.Clamp(((x - ax) * vx + (y - ay) * vy) / len2, 0, 1);
                double px = ax + t * vx - x, py = ay + t * vy - y;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }
        return best;
    }

    // ── building ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The overlay of a trace and, when a schematic is being made, the recognition over it.</summary>
    public static ImageOverlay Build(ImageTraceResult trace, RecognitionResult? recognition, Technology? technology)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var items = new List<ImageOverlayItem>();

        // Copper: each traced region outlined in its layer's own colour.
        foreach (var layer in trace.Layers.Where(l => l.Role == ImageLayerRole.Layer))
        {
            int? rgb = technology?.Layers.FirstOrDefault(l => string.Equals(l.Name, layer.Name, StringComparison.Ordinal)) is { } def
                ? (def.Color.R << 16) | (def.Color.G << 8) | def.Color.B : null;
            for (int r = 0; r < layer.PixelRegions.Count; r++)
            {
                var region = layer.PixelRegions[r];
                var paths = new List<IReadOnlyList<(double X, double Y)>> { Points(region.Outer) };
                paths.AddRange(region.Holes.Select(Points));
                items.Add(new ImageOverlayItem($"copper:{layer.Name}:{r}", ImageOverlayClass.Copper, paths, true, null, rgb,
                                               layer.Key is null, null, PixelBounds.Of(paths[0])));
            }
        }

        // Drills, as the circles read.
        for (int d = 0; d < trace.Drills.Count; d++)
        {
            var c = trace.Drills[d];
            items.Add(new ImageOverlayItem($"drill:{d}", ImageOverlayClass.Drill, [], true, null, null, false, null,
                                           new PixelBounds(c.Cx - c.R, c.Cy - c.R, c.Cx + c.R, c.Cy + c.R), (c.Cx, c.Cy, c.R)));
        }

        // What the recognition found, carried back onto the picture through the trace's frame.
        if (recognition is not null && trace.Frame is { } frame)
        {
            (double X, double Y) Px(long x, long y) => frame.ToPixel(x, y);

            foreach (var row in recognition.Parts.Rows)
            {
                var pads = row.Terminals.Count > 0 ? row.Terminals.Select(t => Px(t.X, t.Y)).ToList() : [Px(row.X, row.Y)];
                var b = PixelBounds.Of(pads);
                double pad = Math.Max(5, 0.25 * Math.Max(b.Width, b.Height));
                b = b.Inflate(pad);
                var box = new List<(double X, double Y)> { (b.Left, b.Top), (b.Right, b.Top), (b.Right, b.Bottom), (b.Left, b.Bottom) };
                bool unknown = row.Kind == PartKind.Unknown || (row.Value is null && row.Model != PartModelKind.SnP);
                items.Add(new ImageOverlayItem($"part:{row.BoardRefdes}", ImageOverlayClass.Part, [box], true, row.Refdes, null,
                                               unknown, row.BoardRefdes, b));
            }

            foreach (var line in recognition.Lines.Elements)
            {
                if (line.Anchor.Count == 0) continue;
                var pts = line.Anchor.Select(a => Px(a.X, a.Y)).ToList();
                string label = line.Width is { } w
                    ? $"{line.Type} W={ImageScale.FormatLength(w)}"
                    : line.Type.ToString();
                var b = PixelBounds.Of(pts);
                if (pts.Count == 1) b = b.Inflate(4);
                items.Add(new ImageOverlayItem($"line:{line.Name}", ImageOverlayClass.Line, [pts], false, label, null,
                                               false, null, b));
            }

            if (recognition.Board is { } board)
                foreach (var port in board.Ports)
                {
                    var (x, y) = Px(port.X, port.Y);
                    const double r = 7;
                    items.Add(new ImageOverlayItem($"port:{port.Number}", ImageOverlayClass.Port, [], true,
                                                   $"P{port.Number.ToString(CultureInfo.InvariantCulture)}", null, false, null,
                                                   new PixelBounds(x - r, y - r, x + r, y + r), (x, y, r)));
                }
        }

        var map = trace.LayerMap;
        var ignored = map?.Rows.Where(r => r.Role == ImageLayerRole.Ignore).Select(r => r.Cluster).ToList() ?? [];
        var unmapped = map?.Rows.Where(r => r.Role is ImageLayerRole.Layer or ImageLayerRole.Drill or ImageLayerRole.Silkscreen or ImageLayerRole.BoardOutline
                                            && r.Layers.Count == 0).Select(r => r.Cluster).ToList() ?? [];
        return new ImageOverlay(items, trace.Clusters, ignored, unmapped, trace.Frame);
    }

    private static IReadOnlyList<(double X, double Y)> Points(Clipper2Lib.PathD path) => [.. path.Select(p => (p.x, p.y))];
}
