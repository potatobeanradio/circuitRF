namespace CircuitRF.Ui.Layout;

/// <summary>
/// The bounded accumulator <see cref="LayoutSnapQuery.FindCandidates"/> collects into — it keeps the
/// best <see cref="Cap"/> candidates under the SAME order the query has always returned (priority
/// <see cref="SnapFeatureKind"/> first, then distance to the cursor) and discards the rest as it goes.
///
/// <para><b>Why a bound is not a compromise here.</b> Snap tolerance is a fixed SCREEN distance
/// converted to world units, so how many features fall inside it depends entirely on how dense the
/// geometry is on screen. Over a generated capacitor carrying a six-figure via field, a cursor at
/// Zoom-to-Fit has tens of thousands of features within eight device pixels of it — all of them at,
/// for practical purposes, the same point. Collecting and sorting them was ~11 ms of a pointer move,
/// and it bought nothing: every caller but one reads <c>[0]</c>, and the one that does not
/// (<c>_snapCycleCache</c>, which cycles through coincident features on repeated clicks) cannot ask a
/// user to page through fifteen thousand indistinguishable vias.</para>
///
/// <para><b>What it does not change.</b> The candidate any caller actually acts on is the FIRST, and
/// the first is by definition never discarded — this only ever drops entries that a full sort would
/// have placed past <see cref="Cap"/>. Nothing about the tolerance, the priority order, or which
/// features qualify is touched.</para>
/// </summary>
internal struct LayoutSnapCandidateSet(long cursorX, long cursorY)
{
    /// <summary>How many candidates survive. Large enough that cycling through genuinely distinct
    /// coincident features at one point never runs out (real overlaps are a handful), small enough
    /// that the final sort is free.</summary>
    internal const int Cap = 64;

    /// <summary>Where the trim happens. Collecting to twice the cap and cutting back amortises the
    /// sort over the entries it admits, so a query examining tens of thousands of features still
    /// sorts a hundred-odd of them a bounded number of times rather than all of them once.</summary>
    private const int TrimAt = Cap * 2;

    private readonly long _cursorX = cursorX, _cursorY = cursorY;
    private List<SnapCandidate>? _items;

    /// <summary>Set once the first trim has happened: nothing ranked worse than the surviving tail can
    /// ever make the final answer, so it is rejected on arrival and never allocated into the list.
    /// </summary>
    private bool _saturated;
    private SnapFeatureKind _worstKind;
    private double _worstDistSq;

    private readonly double DistSqOf(long x, long y)
    {
        double dx = x - _cursorX, dy = y - _cursorY;
        return dx * dx + dy * dy;
    }

    public void Add(SnapCandidate c)
    {
        if (_saturated)
        {
            // Same comparison the sort uses, applied one candidate at a time.
            int k = c.Kind.CompareTo(_worstKind);
            if (k > 0 || (k == 0 && DistSqOf(c.X, c.Y) >= _worstDistSq)) return;
        }

        (_items ??= new List<SnapCandidate>(TrimAt)).Add(c);
        if (_items.Count >= TrimAt) Trim();
    }

    private void Trim()
    {
        Sort();
        _items!.RemoveRange(Cap, _items.Count - Cap);
        var worst = _items[^1];
        _worstKind = worst.Kind;
        _worstDistSq = DistSqOf(worst.X, worst.Y);
        _saturated = true;
    }

    private readonly void Sort()
    {
        // Copied to locals: a lambda inside a struct may not close over `this`.
        long cx = _cursorX, cy = _cursorY;
        _items!.Sort((a, b) =>
        {
            int k = a.Kind.CompareTo(b.Kind);
            if (k != 0) return k;
            double ax = a.X - cx, ay = a.Y - cy, bx = b.X - cx, by = b.Y - cy;
            return (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
        });
    }

    /// <summary>The sorted, capped result. Safe to call once — it sorts in place and hands back the
    /// backing list.</summary>
    public readonly IReadOnlyList<SnapCandidate> ToSortedList()
    {
        if (_items is null || _items.Count == 0) return [];
        Sort();
        if (_items.Count > Cap) _items.RemoveRange(Cap, _items.Count - Cap);
        return _items;
    }

    public readonly int Count => _items?.Count ?? 0;
}
