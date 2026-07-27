// R-L1j-6 (docs/sonnet-briefs/brief-L1j-properties-inspector.md §3.2): Avalonia virtualizes
// CONTAINERS, not ITEMS — an ObservableCollection<T> still allocates one row VM per item on every
// selection/refresh, so a 20,000-vertex polygon would build 20,000 VertexRowViewModels even though
// only ~30 are ever on screen. This index-addressed, lazily-materializing list is the fix: Count is
// known up front (cheap — ring vertex counts, not a full traversal), and the indexer constructs (and
// caches) a row on FIRST access only. With container virtualization on top, only the realized rows
// (the ones actually painted) ever get built.

using System.Collections;
using System.Collections.Generic;

namespace CircuitRF.Ui.ViewModels;

public sealed class LazyIndexedList<T> : IReadOnlyList<T>
{
    private readonly System.Func<int, T> _factory;
    private readonly Dictionary<int, T> _cache = new();

    public LazyIndexedList(int count, System.Func<int, T> factory)
    {
        Count = count;
        _factory = factory;
    }

    public int Count { get; }

    /// <summary>How many rows have actually been constructed — the gate-11 test hook proving
    /// virtualization holds (stays in the tens for a 20,000-vertex polygon, not the thousands).</summary>
    public int MaterializedCount => _cache.Count;

    public IEnumerable<int> MaterializedIndices => _cache.Keys;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new System.ArgumentOutOfRangeException(nameof(index));
            if (!_cache.TryGetValue(index, out var row)) { row = _factory(index); _cache[index] = row; }
            return row;
        }
    }

    // Deliberately realizes everything if actually enumerated — nothing in this codebase should ever
    // foreach/ToList() this collection; only indexed access (what Avalonia's virtualizing panel and
    // this class's own consumers use) stays lazy.
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
