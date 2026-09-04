// Framework-free handle model for shape reshaping (docs/design/layout-view.md §6.3 R14, L1d brief
// "2. Handles"). No SKPath / Avalonia types — LayoutRenderer draws these, LayoutEditorViewModel
// hit-tests and drags them.

namespace CircuitRF.Ui.Layout;

public enum LayoutHandleKind { Vertex, EdgeMidpoint, Bulge, CubicControl, Radius, CornerRadius }

/// <summary>
/// One draggable handle on the single currently-selected shape. <see cref="Index"/> means: vertex
/// index for <see cref="LayoutHandleKind.Vertex"/> (or corner index 0..3 for <c>Rect</c>/<c>RoundedRect</c>,
/// order (X1,Y1)→(X2,Y1)→(X2,Y2)→(X1,Y2)); edge index for <see cref="LayoutHandleKind.EdgeMidpoint"/>/
/// <see cref="LayoutHandleKind.Bulge"/>/<see cref="LayoutHandleKind.CubicControl"/> (edge i runs from
/// vertex i to vertex i+1, wrapping for a closed shape); unused (0) for <see cref="LayoutHandleKind.Radius"/>;
/// the CORNER index (same 0..3 order) for <see cref="LayoutHandleKind.CornerRadius"/>, which a
/// <c>RoundedRect</c> carries one of per corner. <see cref="SubIndex"/> is only meaningful for
/// <see cref="LayoutHandleKind.CubicControl"/> (0 = C1, 1 = C2).
/// </summary>
public readonly record struct LayoutHandle(LayoutHandleKind Kind, long X, long Y, int Index, int SubIndex = 0);

/// <summary>Builds the handle set for a single selected shape — never called for a multi-selection
/// (§2 of the brief: "multi-selection shows no handles — it is a move/delete selection").</summary>
public static class LayoutHandles
{
    public static IReadOnlyList<LayoutHandle> Build(LayoutShape shape) => shape switch
    {
        RectShape r         => BuildRectHandles(r.X1, r.Y1, r.X2, r.Y2),
        RoundedRectShape rr => BuildRoundedRectHandles(rr),
        CircleShape c       => [new LayoutHandle(LayoutHandleKind.Radius, c.Cx + c.R, c.Cy, 0)],
        PolygonShape p      => BuildClosedStraightHandles(p.Xy),
        CurveShape curve    => BuildEdgeListHandles(curve.Xy, curve.Edges, closed: true),
        PathShape path      => BuildEdgeListHandles(path.Xy, path.Edges, closed: false),
        _ => [], // Via, Label — not geometry-reshape targets in L1d
    };

    private static IReadOnlyList<LayoutHandle> BuildRectCorners(long x1, long y1, long x2, long y2) =>
    [
        new LayoutHandle(LayoutHandleKind.Vertex, x1, y1, 0),
        new LayoutHandle(LayoutHandleKind.Vertex, x2, y1, 1),
        new LayoutHandle(LayoutHandleKind.Vertex, x2, y2, 2),
        new LayoutHandle(LayoutHandleKind.Vertex, x1, y2, 3),
    ];

    /// <summary>The 4 straight edges' midpoints, in the SAME edge-index order
    /// <see cref="LayoutShapeEditing.TranslateRectEdge"/>/<see cref="LayoutShapeEditing.FindEdgeLineHit"/>
    /// use: 0=bottom (X1,Y1)→(X2,Y1), 1=right (X2,Y1)→(X2,Y2), 2=top (X2,Y2)→(X1,Y2),
    /// 3=left (X1,Y2)→(X1,Y1). Corner-radius rounding on a <c>RoundedRect</c> only shortens the ENDS
    /// of each straight run symmetrically, so the midpoint position is unaffected by it.</summary>
    private static IReadOnlyList<LayoutHandle> BuildAxisAlignedEdgeMidpoints(long x1, long y1, long x2, long y2) =>
    [
        new LayoutHandle(LayoutHandleKind.EdgeMidpoint, (x1 + x2) / 2, y1, 0),
        new LayoutHandle(LayoutHandleKind.EdgeMidpoint, x2, (y1 + y2) / 2, 1),
        new LayoutHandle(LayoutHandleKind.EdgeMidpoint, (x1 + x2) / 2, y2, 2),
        new LayoutHandle(LayoutHandleKind.EdgeMidpoint, x1, (y1 + y2) / 2, 3),
    ];

    private static IReadOnlyList<LayoutHandle> BuildRectHandles(long x1, long y1, long x2, long y2)
    {
        var list = new List<LayoutHandle>(BuildRectCorners(x1, y1, x2, y2));
        list.AddRange(BuildAxisAlignedEdgeMidpoints(x1, y1, x2, y2));
        return list;
    }

    /// <summary>
    /// A corner-radius grip in EVERY corner, not only the top-left one (owner request, 2026-09-04).
    /// The radius is a single shape-wide field, so all four drag the SAME value — what four grips buy
    /// is that the control is wherever the corner being looked at is, rather than always across the
    /// shape from it.
    ///
    /// <para><b><see cref="LayoutHandle.Index"/> is the CORNER index</b>, the same 0..3 order
    /// <see cref="BuildRectCorners"/> and <see cref="LayoutShapeEditing.ResizeRoundedRectCorner"/> use
    /// — (X1,Y1)→(X2,Y1)→(X2,Y2)→(X1,Y2). It is what tells the drag which way is "bigger": each grip
    /// sits R along the horizontal edge measured FROM ITS OWN corner, so on the right-hand pair it
    /// walks INWARD as R grows and the drag must read x2−px, not px−x1. Without that the two right
    /// grips would run away from the cursor at twice the speed and shrink the radius that a rightward
    /// drag grows on the left — see <c>BuildHandleDragPreview</c>'s CornerRadius case.</para>
    ///
    /// <para>At R = 0 all four coincide with their corner's own Vertex handle, which wins the tie
    /// (same priority tier, and vertices are added first — <see cref="LayoutHandleHitTest"/>). That is
    /// unchanged from when there was one grip: a rounded rect with square corners is resized by its
    /// corners, and the radius is typed into the toolbar field.</para>
    /// </summary>
    private static IReadOnlyList<LayoutHandle> BuildRoundedRectHandles(RoundedRectShape rr)
    {
        var list = new List<LayoutHandle>(BuildRectCorners(rr.X1, rr.Y1, rr.X2, rr.Y2));
        list.AddRange(BuildAxisAlignedEdgeMidpoints(rr.X1, rr.Y1, rr.X2, rr.Y2));

        long r = rr.CornerRadius;
        list.Add(new LayoutHandle(LayoutHandleKind.CornerRadius, rr.X1 + r, rr.Y1, 0));
        list.Add(new LayoutHandle(LayoutHandleKind.CornerRadius, rr.X2 - r, rr.Y1, 1));
        list.Add(new LayoutHandle(LayoutHandleKind.CornerRadius, rr.X2 - r, rr.Y2, 2));
        list.Add(new LayoutHandle(LayoutHandleKind.CornerRadius, rr.X1 + r, rr.Y2, 3));
        return list;
    }

    private static IReadOnlyList<LayoutHandle> BuildClosedStraightHandles(long[] xy)
    {
        int n = xy.Length / 2;
        var list = new List<LayoutHandle>(n * 2);
        for (int i = 0; i < n; i++)
            list.Add(new LayoutHandle(LayoutHandleKind.Vertex, xy[2 * i], xy[2 * i + 1], i));
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            list.Add(new LayoutHandle(LayoutHandleKind.EdgeMidpoint,
                (xy[2 * i] + xy[2 * j]) / 2, (xy[2 * i + 1] + xy[2 * j + 1]) / 2, i));
        }
        return list;
    }

    private static IReadOnlyList<LayoutHandle> BuildEdgeListHandles(long[] xy, List<LayoutEdge>? edges, bool closed)
    {
        int n = xy.Length / 2;
        if (n == 0) return [];

        var list = new List<LayoutHandle>();
        for (int i = 0; i < n; i++)
            list.Add(new LayoutHandle(LayoutHandleKind.Vertex, xy[2 * i], xy[2 * i + 1], i));

        int edgeCount = closed ? n : n - 1;
        for (int i = 0; i < edgeCount; i++)
        {
            int j = closed ? (i + 1) % n : i + 1;
            long x0 = xy[2 * i], y0 = xy[2 * i + 1];
            long x1 = xy[2 * j], y1 = xy[2 * j + 1];
            var edge = edges is not null && i < edges.Count ? edges[i] : null;

            switch (edge?.Kind ?? EdgeKind.Line)
            {
                case EdgeKind.Line:
                    list.Add(new LayoutHandle(LayoutHandleKind.EdgeMidpoint, (x0 + x1) / 2, (y0 + y1) / 2, i));
                    break;
                case EdgeKind.Arc:
                {
                    var (hx, hy) = ArcHandlePosition(x0, y0, x1, y1, edge!.Bulge);
                    list.Add(new LayoutHandle(LayoutHandleKind.Bulge, hx, hy, i));
                    break;
                }
                case EdgeKind.Cubic:
                    list.Add(new LayoutHandle(LayoutHandleKind.CubicControl, edge!.C1X, edge.C1Y, i, 0));
                    list.Add(new LayoutHandle(LayoutHandleKind.CubicControl, edge.C2X, edge.C2Y, i, 1));
                    break;
            }
        }
        return list;
    }

    /// <summary>Position of an Arc edge's bulge handle: the arc's true midpoint, or the chord
    /// midpoint when bulge is exactly 0 (a "straight arc", immediately draggable — §4).</summary>
    internal static (long X, long Y) ArcHandlePosition(long x0, long y0, long x1, long y1, double bulge)
    {
        if (bulge == 0) return ((x0 + x1) / 2, (y0 + y1) / 2);
        var arc = LayoutArc.FromBulge(x0, y0, x1, y1, bulge);
        if (arc.R <= 0) return ((x0 + x1) / 2, (y0 + y1) / 2);
        double mid = arc.StartAngle + arc.Sweep / 2.0;
        return ((long)Math.Round(arc.Cx + arc.R * Math.Cos(mid)), (long)Math.Round(arc.Cy + arc.R * Math.Sin(mid)));
    }
}
