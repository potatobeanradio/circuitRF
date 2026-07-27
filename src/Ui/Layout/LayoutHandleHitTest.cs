// Framework-free handle hit-testing (docs/design/layout-view.md §6.3 R14, L1d brief).
// R-L1d-2: hit priority, strictly — cubic control point > bulge handle > vertex > edge midpoint.
// (Edge-line and shape-interior fallbacks live in LayoutShapeEditing/LayoutHitTest respectively —
// this type only orders the discrete point HANDLES against each other.)

namespace CircuitRF.Ui.Layout;

public static class LayoutHandleHitTest
{
    private static int PriorityOf(LayoutHandleKind kind) => kind switch
    {
        LayoutHandleKind.CubicControl => 0,
        LayoutHandleKind.Bulge        => 1,
        LayoutHandleKind.Vertex       => 2,
        LayoutHandleKind.Radius       => 2,
        LayoutHandleKind.CornerRadius => 2,
        LayoutHandleKind.EdgeMidpoint => 3,
        _ => 4,
    };

    /// <summary>Returns the highest-priority handle within <paramref name="tolDbu"/> of (x,y), ties
    /// broken by nearest distance. Null when nothing is within tolerance.</summary>
    public static LayoutHandle? HitTest(IReadOnlyList<LayoutHandle> handles, long x, long y, long tolDbu)
    {
        LayoutHandle? best = null;
        int bestPriority = int.MaxValue;
        double bestDist = double.MaxValue;

        foreach (var h in handles)
        {
            double dx = h.X - x, dy = h.Y - y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > tolDbu) continue;

            int p = PriorityOf(h.Kind);
            if (p < bestPriority || (p == bestPriority && dist < bestDist))
            {
                best = h;
                bestPriority = p;
                bestDist = dist;
            }
        }
        return best;
    }
}
