// Bbox scale handles (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md R-L1h-4/R-L1h-5) — a
// SEPARATE handle vocabulary from LayoutHandles (L1d's single-shape vertex/edge/bulge/control-point
// set), deliberately: these are derived from a selection's BOUNDING BOX, not from any one shape's
// geometry, so they exist independently of shape kind and apply uniformly to a multi-selection.

using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.Layout;

public enum ScaleHandleKind { Corner, Side }

/// <summary><see cref="Index"/> for Corner: 0=(MinX,MinY), 1=(MaxX,MinY), 2=(MaxX,MaxY), 3=(MinX,MaxY)
/// — the SAME corner-index convention <c>LayoutHandles.BuildRectCorners</c> uses. For Side:
/// 0=bottom midpoint, 1=right midpoint, 2=top midpoint, 3=left midpoint — the same convention
/// <c>LayoutHandles.BuildAxisAlignedEdgeMidpoints</c> uses. Reusing both conventions means "opposite
/// handle" is always <c>(Index + 2) % 4</c> for either kind.</summary>
public readonly record struct LayoutScaleHandle(ScaleHandleKind Kind, long X, long Y, int Index);

public static class LayoutScaleHandles
{
    /// <summary>The 8 handles for a selection's bounding box — 4 corners (uniform-scale drag) + 4 side
    /// midpoints (single-axis drag). Empty when <paramref name="bbox"/> is empty (nothing selected, or
    /// every selected shape has a degenerate bbox).</summary>
    public static IReadOnlyList<LayoutScaleHandle> Build(Bbox bbox)
    {
        if (bbox.IsEmpty) return [];
        long x1 = bbox.MinX, y1 = bbox.MinY, x2 = bbox.MaxX, y2 = bbox.MaxY;
        return
        [
            new LayoutScaleHandle(ScaleHandleKind.Corner, x1, y1, 0),
            new LayoutScaleHandle(ScaleHandleKind.Corner, x2, y1, 1),
            new LayoutScaleHandle(ScaleHandleKind.Corner, x2, y2, 2),
            new LayoutScaleHandle(ScaleHandleKind.Corner, x1, y2, 3),
            new LayoutScaleHandle(ScaleHandleKind.Side, (x1 + x2) / 2, y1, 0),
            new LayoutScaleHandle(ScaleHandleKind.Side, x2, (y1 + y2) / 2, 1),
            new LayoutScaleHandle(ScaleHandleKind.Side, (x1 + x2) / 2, y2, 2),
            new LayoutScaleHandle(ScaleHandleKind.Side, x1, (y1 + y2) / 2, 3),
        ];
    }

    /// <summary>The handle at <c>(Index + 2) % 4</c> of the same kind — the drag anchor when Alt is
    /// not held (§2.1: "Anchor is the opposite corner or side").</summary>
    public static LayoutScaleHandle Opposite(IReadOnlyList<LayoutScaleHandle> handles, LayoutScaleHandle h)
    {
        int oppositeIndex = (h.Index + 2) % 4;
        foreach (var candidate in handles)
            if (candidate.Kind == h.Kind && candidate.Index == oppositeIndex)
                return candidate;
        return h;
    }

    public static LayoutScaleHandle? HitTest(IReadOnlyList<LayoutScaleHandle> handles, long x, long y, long tolDbu)
    {
        LayoutScaleHandle? best = null;
        double bestDist = double.MaxValue;
        foreach (var h in handles)
        {
            double dx = h.X - x, dy = h.Y - y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > tolDbu) continue;
            if (dist < bestDist) { best = h; bestDist = dist; }
        }
        return best;
    }
}
