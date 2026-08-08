namespace CircuitRF.WBond;

/// <summary>How far a click promotes: to the thing hit, its wire, or its whole array (§6.3).</summary>
public enum SelectionScope
{
    /// <summary>Just the point or segment under the cursor.</summary>
    Element,

    /// <summary>The whole wire — double-click, or the <c>w</c> modifier.</summary>
    Wire,

    /// <summary>Every wire in the hit wire's array — triple-click, or the <c>g</c> modifier.</summary>
    Array,
}

/// <summary>Which way a marquee was dragged, which decides what it catches.</summary>
public enum MarqueeDirection
{
    /// <summary>Left → right: <b>enclose</b>. Only wires lying entirely inside are caught.</summary>
    LeftToRight,

    /// <summary>
    /// Right → left: <b>crossing</b>. Any wire with a point inside is caught <b>whole</b>.
    /// </summary>
    RightToLeft,
}

/// <summary>Which projection a gesture happened in — it decides the nudge axis, not the arithmetic.</summary>
public enum EditorView
{
    /// <summary>X-Y. Up is +y.</summary>
    Layout,

    /// <summary>Span-Z, z always up. Up is +z.</summary>
    Profile,
}

/// <summary>One selected point of one wire.</summary>
public readonly record struct PointRef(int Wire, int Point);

/// <summary>One selected segment: the span between <c>Point</c> and <c>Point + 1</c> of a wire.</summary>
public readonly record struct SegmentRef(int Wire, int Point);

/// <summary>
/// A selection in the wBond editor: whole wires, plus individual points and segments within wires
/// that are not wholly selected.
/// </summary>
public sealed class WireSelection
{
    /// <summary>Wires selected in their entirety.</summary>
    public HashSet<int> Wires { get; init; } = [];

    public HashSet<PointRef> Points { get; init; } = [];

    public HashSet<SegmentRef> Segments { get; init; } = [];

    public bool IsEmpty => Wires.Count == 0 && Points.Count == 0 && Segments.Count == 0;

    /// <summary>
    /// Every wire the selection touches, whether wholly or through one point or segment — the set the
    /// incremental fill must be told about.
    /// </summary>
    public IReadOnlySet<int> TouchedWires()
    {
        var touched = new HashSet<int>(Wires);
        foreach (var p in Points) touched.Add(p.Wire);
        foreach (var s in Segments) touched.Add(s.Wire);
        return touched;
    }

    /// <summary>
    /// The point indices of <paramref name="wire"/> that a move should carry.
    ///
    /// <para>A wholly-selected wire carries every point. Otherwise a selected <b>segment</b> carries
    /// both its endpoints — which is what makes a dragged segment stay attached at both ends by
    /// construction rather than by a constraint solver (§6.3).</para>
    /// </summary>
    public IReadOnlySet<int> MovingPoints(int wire, int pointCount)
    {
        if (Wires.Contains(wire))
            return new HashSet<int>(Enumerable.Range(0, pointCount));

        var moving = new HashSet<int>();
        foreach (var p in Points)
            if (p.Wire == wire) moving.Add(p.Point);

        foreach (var s in Segments)
        {
            if (s.Wire != wire) continue;
            moving.Add(s.Point);
            if (s.Point + 1 < pointCount) moving.Add(s.Point + 1);
        }

        return moving;
    }

    public WireSelection Clone() => new()
    {
        Wires = [.. Wires],
        Points = [.. Points],
        Segments = [.. Segments],
    };
}

/// <summary>
/// Turns a hit and a set of modifiers into a selection (R-wbc-1).
///
/// <para>Every method here is a pure function of geometry and intent. That is the point of WB-C1: the
/// rules that can be wrong are testable without a canvas.</para>
/// </summary>
public static class SelectionResolver
{
    /// <summary>
    /// Promotes a hit according to <paramref name="scope"/>.
    /// </summary>
    public static WireSelection Resolve(WireMesh mesh, int wire, int point, bool isSegment,
                                        SelectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var selection = new WireSelection();

        switch (scope)
        {
            case SelectionScope.Array:
            {
                int array = mesh.ArrayOfWire[wire];
                for (int w = 0; w < mesh.WireCount; w++)
                    if (mesh.ArrayOfWire[w] == array) selection.Wires.Add(w);
                break;
            }

            case SelectionScope.Wire:
                selection.Wires.Add(wire);
                break;

            default:
                if (isSegment) selection.Segments.Add(new SegmentRef(wire, point));
                else selection.Points.Add(new PointRef(wire, point));
                break;
        }

        return selection;
    }

    /// <summary>
    /// Resolves a marquee.
    ///
    /// <para><b>Right → left promotes to whole WIRES, not to the points that happen to be inside.</b>
    /// That asymmetry is the entire behavioural difference between the two directions and it is the
    /// part that is easy to get subtly wrong: an implementation that returns the enclosed points for
    /// both directions passes any test that only counts how many things were selected.</para>
    /// </summary>
    /// <param name="view">Which projection the marquee was drawn in — it decides the two axes tested.</param>
    /// <param name="spanOf">
    /// Maps a wire's point to its profile-view horizontal coordinate. Only consulted for
    /// <see cref="EditorView.Profile"/>; null there means "use x", which is right for an unprojected
    /// preview.
    /// </param>
    public static WireSelection ResolveMarquee(
        WireMesh mesh, long minA, long minB, long maxA, long maxB,
        MarqueeDirection direction, EditorView view = EditorView.Layout,
        Func<int, int, long>? spanOf = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (minA > maxA) (minA, maxA) = (maxA, minA);
        if (minB > maxB) (minB, maxB) = (maxB, minB);

        var selection = new WireSelection();

        for (int w = 0; w < mesh.WireCount; w++)
        {
            var points = mesh.Wires[w].Points;

            int inside = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var (a, b) = Project(points[i], view, w, i, spanOf);
                if (a >= minA && a <= maxA && b >= minB && b <= maxB) inside++;
            }

            if (inside == 0) continue;

            if (direction == MarqueeDirection.RightToLeft)
            {
                // Crossing: ANY point inside catches the WHOLE wire.
                selection.Wires.Add(w);
                continue;
            }

            // Enclose: only a wire lying entirely inside is caught, and it is caught whole. A
            // partially-enclosed wire contributes its enclosed points instead, so a left-to-right
            // marquee can still be used to grab a few vertices.
            if (inside == points.Count)
            {
                selection.Wires.Add(w);
                continue;
            }

            for (int i = 0; i < points.Count; i++)
            {
                var (a, b) = Project(points[i], view, w, i, spanOf);
                if (a >= minA && a <= maxA && b >= minB && b <= maxB)
                    selection.Points.Add(new PointRef(w, i));
            }
        }

        return selection;
    }

    private static (long A, long B) Project(Point3 p, EditorView view, int wire, int index,
                                            Func<int, int, long>? spanOf)
        => view == EditorView.Layout
            ? (p.X, p.Y)
            : (spanOf?.Invoke(wire, index) ?? p.X, p.Z);

    /// <summary>Adds <paramref name="addition"/> to <paramref name="existing"/> — shift-click.</summary>
    public static WireSelection Union(WireSelection existing, WireSelection addition)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(addition);

        var merged = existing.Clone();
        merged.Wires.UnionWith(addition.Wires);
        merged.Points.UnionWith(addition.Points);
        merged.Segments.UnionWith(addition.Segments);

        // A wire selected whole subsumes its own points and segments — keeping both would move some
        // points twice under a nudge.
        merged.Points.RemoveWhere(p => merged.Wires.Contains(p.Wire));
        merged.Segments.RemoveWhere(s => merged.Wires.Contains(s.Wire));

        return merged;
    }
}
