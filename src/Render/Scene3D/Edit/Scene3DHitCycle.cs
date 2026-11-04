// brief-em3d-43 R-em3d43-4b — B and Shift+B: layout's R13 overlap cycling (layout-view.md §6.2), in depth.
//
// The list and the cursor point it was built at are CACHED: B advances, Shift+B goes back, both wrapping at
// the ends. It is discarded — and the next B builds a new one — when the cursor moves more than a few
// pixels, the scene changes (a new generation), or the mode changes; each discard is counted, which is
// what gate 4 reads. The FIRST B after a click starts from the clicked item's place in the list, so B from
// the top face of a stack goes to the face just behind it, not back to the front.

namespace CircuitRF.Render.Scene3D.Edit;

public sealed class Scene3DHitCycle
{
    /// <summary>A cursor move beyond this many pixels (either axis) discards the list.</summary>
    public const float ResetPixels = 4;

    private List<Scene3DHit>? _list;
    private int _index;
    private float _x, _y;
    private Scene3DSelectMode _mode;
    private long _generation;

    /// <summary>How many lists have been built (one per B after a discard).</summary>
    public long ListsBuilt { get; private set; }

    /// <summary>How many lists have been discarded.</summary>
    public long Resets { get; private set; }

    /// <summary>Whether a list is cached.</summary>
    public bool Active => _list is not null;

    /// <summary>The list's length, 0 when there is none.</summary>
    public int Count => _list?.Count ?? 0;

    /// <summary>The current item's place in the list, 0-based; −1 when there is none.</summary>
    public int Index => _list is null ? -1 : _index;

    /// <summary>The cursor moved to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void CursorMoved(float x, float y)
    {
        if (_list is not null && (MathF.Abs(x - _x) > ResetPixels || MathF.Abs(y - _y) > ResetPixels)) Reset();
    }

    /// <summary>The scene became generation <paramref name="generation"/>.</summary>
    public void SceneChanged(long generation)
    {
        if (_list is not null && generation != _generation) Reset();
    }

    /// <summary>The selection mode became <paramref name="mode"/>.</summary>
    public void ModeChanged(Scene3DSelectMode mode)
    {
        if (_list is not null && mode != _mode) Reset();
    }

    /// <summary>Discards the list.</summary>
    public void Reset()
    {
        if (_list is null) return;
        _kept = _list;
        _list = null;
        Resets++;
    }

    private List<Scene3DHit>? _kept;

    /// <summary>brief-dfr34-1 — takes back the list the last <see cref="Reset"/> discarded, at the same place in it: a click
    /// resets the cycle before it knows whether it is a repeated press on a port, which steps on through the same list.</summary>
    public void Resume()
    {
        if (_list is null && _kept is not null) { _list = _kept; Resets--; }
        _kept = null;
    }

    /// <summary>
    /// One step: <paramref name="direction"/> +1 is B (next behind), −1 is Shift+B (next in front).
    /// <paramref name="build"/> makes the list when none is cached for this cursor, mode and generation;
    /// <paramref name="current"/> is what is selected now, where a fresh list starts. Null when the ray
    /// crosses nothing.
    /// </summary>
    public Scene3DHit? Step(int direction, float x, float y, Scene3DSelectMode mode, long generation,
                            Scene3DItem? current, Func<List<Scene3DHit>> build)
    {
        CursorMoved(x, y);
        SceneChanged(generation);
        ModeChanged(mode);
        if (_list is null)
        {
            _list = build();
            ListsBuilt++;
            (_x, _y, _mode, _generation) = (x, y, mode, generation);
            int at = current is { } c ? _list.FindIndex(h => h.Item == c) : -1;
            // Nothing (or something off this ray) selected: the first B lands on the nearest.
            _index = at >= 0 ? at : direction > 0 ? -1 : 0;
        }
        int n = _list.Count;
        if (n == 0) return null;
        _index = ((_index + direction) % n + n) % n;
        return _list[_index];
    }
}
