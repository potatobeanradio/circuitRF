// General designer feedback round 14 — the 3D view's labels (probes, heat sources, mesh regions, effective blocks, ports) were each
// drawn up and to the right of their point with no regard for one another. Probes on one solid share its centre, two probes on
// one face share a point exactly, and a Front or Side view lines a whole stack up on one vertical: the names landed on top of
// each other and could not be read. This places them so no two overlap.
//
// Greedy, in a fixed order (top of the screen first, then left), so a still view always lays out the same way. Each label tries
// the four corners round its point — up-right first, which is where every label used to go — then slides away from it, up and
// down, a label's height at a time. A label that had to move away from its point gets a leader line back to it. The points
// themselves (the probe diamonds) are obstacles too, so a label never covers another label's marker.

using Avalonia;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>Where each label's box goes, and whether it needs a leader line back to its point.</summary>
internal static class Viewer3DLabelLayout
{
    /// <summary>Pixels between a label's box and its point, and between two boxes.</summary>
    public const double Gap = 2;

    /// <summary>Half the side of the square kept clear round every point (a probe's diamond is 5 px).</summary>
    public const double MarkerHalf = 4;

    /// <summary>How many label heights a label may slide up or down before it gives up and overlaps.</summary>
    public const int MaxSlide = 12;

    /// <summary>
    /// Boxes for labels of <paramref name="sizes"/> at <paramref name="anchors"/> (screen pixels) in a
    /// <paramref name="width"/> × <paramref name="height"/> view, in the input's order. <c>Leader</c> is true for a box that does
    /// not touch its point.
    /// </summary>
    public static (Rect Box, bool Leader)[] Place(IReadOnlyList<Point> anchors, IReadOnlyList<Size> sizes, double width, double height)
    {
        int n = anchors.Count;
        var result = new (Rect, bool)[n];
        var placed = new List<Rect>(n);
        var markers = anchors.Select(a => new Rect(a.X - MarkerHalf, a.Y - MarkerHalf, 2 * MarkerHalf, 2 * MarkerHalf)).ToArray();
        var view = new Rect(0, 0, width, height);
        var order = Enumerable.Range(0, n).OrderBy(i => anchors[i].Y).ThenBy(i => anchors[i].X).ThenBy(i => i);
        foreach (int i in order)
        {
            var a = anchors[i];
            var s = sizes[i];
            Rect? inView = null, anywhere = null;
            foreach (var box in Candidates(a, s))
            {
                if (Collides(box, a, placed, anchors, markers)) continue;
                anywhere ??= box;
                if (view.Contains(box)) { inView = box; break; }
            }
            var chosen = inView ?? anywhere ?? new Rect(a.X, a.Y - s.Height, s.Width, s.Height);
            placed.Add(chosen);
            result[i] = (chosen, !Touches(chosen, a));
        }
        return result;
    }

    private static IEnumerable<Rect> Candidates(Point a, Size s)
    {
        double w = s.Width, h = s.Height;
        yield return new Rect(a.X, a.Y - h, w, h);                       // up-right: where a label always went
        yield return new Rect(a.X, a.Y + Gap, w, h);                     // down-right
        yield return new Rect(a.X - w, a.Y - h, w, h);                   // up-left
        yield return new Rect(a.X - w, a.Y + Gap, w, h);                 // down-left
        for (int k = 1; k <= MaxSlide; k++)
        {
            double step = k * (h + Gap);
            yield return new Rect(a.X + MarkerHalf, a.Y - h - step, w, h);
            yield return new Rect(a.X + MarkerHalf, a.Y + Gap + step, w, h);
            yield return new Rect(a.X - MarkerHalf - w, a.Y - h - step, w, h);
            yield return new Rect(a.X - MarkerHalf - w, a.Y + Gap + step, w, h);
        }
    }

    /// <summary>Whether <paramref name="box"/> overlaps a box already placed or covers another label's point. A point at this
    /// label's own place is not an obstacle: two probes on one face share it, and each box has to sit beside it.</summary>
    private static bool Collides(Rect box, Point own, List<Rect> placed, IReadOnlyList<Point> anchors, Rect[] markers)
    {
        foreach (var p in placed) if (p.Intersects(box)) return true;
        for (int j = 0; j < markers.Length; j++)
            if (Math.Abs(anchors[j].X - own.X) + Math.Abs(anchors[j].Y - own.Y) > 1 && markers[j].Intersects(box)) return true;
        return false;
    }

    /// <summary>Whether <paramref name="box"/> sits against <paramref name="a"/>: one that does not gets a leader line.</summary>
    internal static bool Touches(Rect box, Point a) => box.Inflate(Gap + 0.5).Contains(a);

    /// <summary>The point on <paramref name="box"/>'s edge nearest <paramref name="a"/> — where a leader line meets the box.</summary>
    public static Point NearestOnEdge(Rect box, Point a)
        => new(Math.Clamp(a.X, box.Left, box.Right), Math.Clamp(a.Y, box.Top, box.Bottom));
}
