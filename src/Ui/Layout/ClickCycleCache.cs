// The overlap-cycling algorithm (docs/design/layout-view.md §6.2, R-L1c-2): a click within tolerance
// of the cache's own point advances to the NEXT entry in the same ordered stack; a click elsewhere
// (or a bypass modifier, e.g. Alt) rebuilds from a freshly-supplied stack. ONE generic implementation,
// shared by shape-selection overlap cycling (LayoutEditorViewModel's own `_cycleCache`) and geometry-
// snap candidate cycling (R-snp-9, docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md) — do
// not add a second cycling mechanism for either payload type.

namespace CircuitRF.Ui.Layout;

public sealed class ClickCycleCache<T>
{
    public long ClickX { get; private set; }
    public long ClickY { get; private set; }
    public IReadOnlyList<T> Stack { get; private set; } = [];
    public int Index { get; private set; }

    public bool HasStack => Stack.Count > 0;

    /// <summary>True when a click at (x,y) should ADVANCE this cache rather than rebuild it — within
    /// <paramref name="tolDbu"/> of the cache's own click point, or <paramref name="bypassDistance"/>
    /// is set (e.g. Alt-click, an explicit "next candidate regardless of exact pixel").</summary>
    public bool Matches(long x, long y, long tolDbu, bool bypassDistance)
    {
        if (!HasStack) return false;
        long thresh = Math.Max(tolDbu, 1);
        return bypassDistance || (Math.Abs(ClickX - x) <= thresh && Math.Abs(ClickY - y) <= thresh);
    }

    /// <summary>Advances to the next stack entry (wrapping) and updates the click point to (x,y) — so
    /// a short cumulative drift across several successive clicks stays within tolerance of the LAST
    /// click, not just the original one. Call only after <see cref="Matches"/> returned true.</summary>
    public T Advance(long x, long y)
    {
        Index = (Index + 1) % Stack.Count;
        ClickX = x; ClickY = y;
        return Stack[Index];
    }

    /// <summary>Rebuilds from a freshly hit-tested stack, starting at entry 0. The caller is
    /// responsible for ensuring <paramref name="stack"/> is non-empty.</summary>
    public T Rebuild(long x, long y, IReadOnlyList<T> stack)
    {
        ClickX = x; ClickY = y; Stack = stack; Index = 0;
        return Stack[0];
    }

    /// <summary>Invalidates the cache (movement past tolerance, a model mutation, or a selection
    /// change from elsewhere) — the next press must rebuild.</summary>
    public void Clear()
    {
        Stack = [];
        Index = 0;
    }
}
