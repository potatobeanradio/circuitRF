// Snap tracking's references, shared by the 3D view (SnapTracker3D) and the layout editor (LayoutSnapTracker): the ONE
// place a point becomes a reference and stops being one, so the two editors cannot drift apart on how long a dwell is
// or how long a reference lives.
//
// ACQUIRING. A point under the cursor for DwellMs becomes a reference; the same dwell on a point that already is one lets
// it go. Passing over a point acquires nothing — the dwell is what makes it deliberate. At most Max are held; another
// pushes out the oldest.
//
// FORGETTING. A reference not used for ForgetMs — no tracked point came off it — is dropped (never the one under the
// cursor). The editors also Clear when a gesture finishes or its tool is put away.
//
// Pure: the caller hands in the time.

namespace CircuitRF.Render;

public sealed class SnapReferences<T>(Func<T, T, bool> same) where T : struct
{
    /// <summary>How long a point must stay under the cursor to be acquired (or, if it is one, let go).</summary>
    public const long DwellMs = 500;

    /// <summary>How long a reference lives without a tracked point coming off it.</summary>
    public const long ForgetMs = 10_000;

    /// <summary>The most references held at once.</summary>
    public const int Max = 2;

    /// <summary>One reference, and when it was last used.</summary>
    public readonly record struct Entry(T Point, long LastUsedMs);

    private readonly List<Entry> _refs = [];
    private T? _hover;
    private long _hoverSince;
    private bool _hoverFired;

    /// <summary>The references, oldest first.</summary>
    public IReadOnlyList<Entry> Items => _refs;

    public int Count => _refs.Count;

    /// <summary>Whether the caller should keep ticking: a dwell is pending, or a reference may yet be forgotten.</summary>
    public bool NeedsTick => _refs.Count > 0 || (_hover is not null && !_hoverFired);

    /// <summary>Every reference dropped. True when there were any.</summary>
    public bool Clear()
    {
        bool had = _refs.Count > 0;
        _refs.Clear();
        _hover = null;
        _hoverFired = false;
        return had;
    }

    /// <summary>The point under the cursor now (null: none, or one that cannot be a reference): starts, keeps or ends a dwell.</summary>
    public void Observe(T? point, long nowMs)
    {
        if (point is not { } p) { _hover = null; return; }
        if (_hover is { } h && same(h, p)) return;
        _hover = p;
        _hoverSince = nowMs;
        _hoverFired = false;
    }

    /// <summary>The clock advanced: a dwell that has lasted acquires (or lets go of) its point, and an unused reference is
    /// forgotten. True when the references changed.</summary>
    public bool Tick(long nowMs)
    {
        bool changed = false;
        if (_hover is { } h && !_hoverFired && nowMs - _hoverSince >= DwellMs)
        {
            _hoverFired = true;
            int at = _refs.FindIndex(r => same(r.Point, h));
            if (at >= 0) _refs.RemoveAt(at);
            else
            {
                if (_refs.Count >= Max) _refs.RemoveAt(0);
                _refs.Add(new Entry(h, nowMs));
            }
            changed = true;
        }
        int before = _refs.Count;
        _refs.RemoveAll(r => nowMs - r.LastUsedMs > ForgetMs && !(_hover is { } hv && same(hv, r.Point)));
        return changed || _refs.Count != before;
    }

    /// <summary>Reference <paramref name="i"/> was tracked from now: its forgetting starts again.</summary>
    public void Use(int i, long nowMs) => _refs[i] = _refs[i] with { LastUsedMs = nowMs };
}
