namespace CircuitRF.Design.Cells;

// ──────────────────────────────────────────────────────────────────────────────
//  SL4 §2 — the filesystem questions a cell reference's resolution asks, COUNTED,
//  and a short-lived cache over the POSITIVE ones.
//
//  brief-shared-library-4-concurrency-and-latency.md R-sl4-6 … R-sl4-9.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The one place <see cref="CellFolder.ResolvePrimary"/> and <c>CellSymbolResolver.Resolve</c> touch
/// the filesystem, so the cost of resolving a cell reference is a NUMBER rather than an intuition
/// (<see cref="Calls"/>), and so a short-lived cache over it is one mechanism rather than five.
///
/// <para><b>Why a count and not a stopwatch (R-sl4-6).</b> The repo's standing rule: a timing
/// assertion measures the machine, flakes under parallel test load, and inverts under a debug build.
/// The call count is what describes the ALGORITHM, and it is the number a regression moves — a
/// future change that reintroduces a per-component walk shows up here as an integer, on every
/// machine, identically.
/// </para>
///
/// <para><b>The cache weakens one stated guarantee, deliberately and by a stated bound
/// (R-sl4-7).</b> Before SL4 the product's promise was <i>"a change on disk is seen at the next
/// resolve"</i> — that is what makes the librarian's edit reach every user without a restart, and it
/// is the property the whole shared-library workflow rests on. It is now <b>"a change on disk is seen
/// within <see cref="Freshness"/> of the next resolve"</b>. Nothing else about resolution changed:
/// the mtime check in the symbol cache is still the mechanism, and this only bounds how stale the
/// mtime itself may be.
/// </para>
///
/// <para><b>A negative is NEVER cached (R-sl4-8).</b> A directory that did not exist, a symbol folder
/// with no <c>.csym</c> in it, an mtime for a file that is not there — every one of those is asked
/// again immediately. A share that blinks, or a folder the librarian is half-way through renaming,
/// would otherwise fill a design with Not-Found glyphs that persist after the network has recovered,
/// which reads as data loss and is not.
/// </para>
///
/// <para><b>Keyed and dropped exactly as the other per-workspace memos are (R-sl4-9)</b> —
/// <c>WorkspaceRootFinder</c>'s walk-up, <c>ExternalCellRef</c>'s alias table and
/// <see cref="CircuitRF.Design.Workspace.WorkspaceWritability"/>'s probe all live behind one
/// <c>WorkspaceRootFinder.InvalidateCache</c>, and this is the fourth. A memo with a lifecycle of its
/// own is the one that goes stale.
/// </para>
/// </summary>
public static class CellStat
{
    // ── The bound (R-sl4-7) ───────────────────────────────────────────────────

    /// <summary>
    /// <b>T — how stale a cached filesystem answer about a cell folder may be. Two seconds.</b>
    ///
    /// <para>The bound has to be shorter than the fastest way a person can observe a change they just
    /// made: save a cell on the librarian's machine, then walk or alt-tab to a second machine and
    /// look at a design that places it. That round trip is never under a few seconds, so two is
    /// invisible to it. It sits at the top of the brief's one-to-two-second band rather than the
    /// bottom because what the cache actually collapses is a BURST of resolves — consecutive discrete
    /// edits are seconds apart, not milliseconds — and a one-second bound misses most of them while
    /// buying no guarantee anybody can perceive.</para>
    ///
    /// <para>It is a bound, not a schedule: nothing refreshes on a timer, and the first resolve after
    /// T re-asks the filesystem. In the worst case a librarian's edit is seen T later than it would
    /// have been before SL4; in the ordinary case — a design nobody is touching — it is seen at the
    /// same moment, because the first resolve after the edit is already more than T after the last
    /// one.</para>
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(2);

    // ── The count (R-sl4-6) ───────────────────────────────────────────────────

    /// <summary>
    /// Filesystem calls made through this type since the last <see cref="ResetCalls"/>. This is the
    /// counting seam the gate asserts on: calls per referenced component, and calls per edit.
    /// </summary>
    public static long Calls => Interlocked.Read(ref _calls);

    /// <summary>Zeroes <see cref="Calls"/>. The gate brackets one edit with this.</summary>
    public static void ResetCalls() => Interlocked.Exchange(ref _calls, 0);

    private static long _calls;

    // ── Seams ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The clock the freshness bound is measured against; null is <see cref="DateTime.UtcNow"/>.
    /// Driven by the gate so the bound is tested by MOVING TIME rather than by sleeping — a test that
    /// sleeps for T takes T, flakes under load, and measures the machine. Setting it drops the cache,
    /// since it changes every entry's age.
    /// </summary>
    public static Func<DateTime>? Clock
    {
        get => _clock;
        set { _clock = value; InvalidateCache(); }
    }
    private static Func<DateTime>? _clock;

    private static DateTime Now => _clock is { } c ? c() : DateTime.UtcNow;

    /// <summary>
    /// Whether positive answers are cached at all; on by default. Off is what the gate's
    /// before-and-after measurement compares against, and it is the one switch that restores the
    /// pre-SL4 guarantee exactly.
    /// </summary>
    public static bool CacheEnabled
    {
        get => _cacheEnabled;
        set { _cacheEnabled = value; InvalidateCache(); }
    }
    private static bool _cacheEnabled = true;

    // ── The cache ─────────────────────────────────────────────────────────────

    private sealed record Entry(DateTime Taken, object Value);

    private static readonly Dictionary<string, Entry> _memo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _memoGate = new();

    /// <summary>
    /// R-sl4-9: forgets every cached answer. Called from
    /// <c>WorkspaceRootFinder.InvalidateCache</c> alongside the other three per-workspace memos, and
    /// directly by the symbol resolver's own <c>InvalidateAll</c> — so a Make-Primary or a
    /// symbol-editor save is seen at once rather than within T.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_memoGate) _memo.Clear();
    }

    /// <summary>
    /// Forgets every cached answer that mentions <paramref name="cellFolder"/> — the targeted drop
    /// behind <c>CellSymbolResolver.Invalidate</c>, which a Make-Primary calls for the one cell it
    /// rewrote. Matching on the path as a substring drops a superset (a cell folder is a prefix of
    /// its own view sub-folders' keys), and dropping too much here only costs the next resolve a
    /// round trip — where keeping too much would keep a stale primary on screen for T after the user
    /// changed it themselves, which is not a bound anyone agreed to.
    /// </summary>
    public static void Invalidate(string cellFolder)
    {
        if (string.IsNullOrEmpty(cellFolder)) return;
        lock (_memoGate)
        {
            var drop = _memo.Keys
                .Where(k => k.Contains(cellFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in drop) _memo.Remove(k);
        }
    }

    private static T? Cached<T>(string key, bool cache) where T : class
    {
        if (!cache || !_cacheEnabled) return null;
        lock (_memoGate)
        {
            if (!_memo.TryGetValue(key, out var found)) return null;
            if (Now - found.Taken > Freshness) { _memo.Remove(key); return null; }
            return found.Value as T;
        }
    }

    private static void Put(string key, object value, bool cache)
    {
        if (!cache || !_cacheEnabled) return;
        lock (_memoGate) _memo[key] = new Entry(Now, value);
    }

    // ── The wrapped calls ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Directory.Exists(string)"/>. A TRUE answer is cached for <see cref="Freshness"/>;
    /// a false one never is (R-sl4-8) — a cell folder missing because a share blinked must be
    /// re-asked on the very next resolve, not T later.
    /// </summary>
    public static bool DirectoryExists(string path, bool cache = false)
    {
        string key = "D " + path;
        if (Cached<string>(key, cache) is not null) return true;

        Interlocked.Increment(ref _calls);
        bool exists = Directory.Exists(path);
        if (exists) Put(key, key, cache);
        return exists;
    }

    /// <summary>
    /// <see cref="Directory.GetFiles(string,string)"/>. A NON-EMPTY result is cached; an empty one is
    /// not — "this cell has no symbol yet" is a negative in exactly the sense R-sl4-8 means, and a
    /// cell whose first <c>.csym</c> has just been written must draw at the next resolve.
    /// </summary>
    public static string[] GetFiles(string directory, string searchPattern, bool cache = false)
    {
        string key = "F " + directory + " " + searchPattern;
        if (Cached<string[]>(key, cache) is { } hit) return hit;

        Interlocked.Increment(ref _calls);
        string[] files = Directory.GetFiles(directory, searchPattern);
        if (files.Length > 0) Put(key, files, cache);
        return files;
    }

    /// <summary>
    /// <see cref="File.GetLastWriteTimeUtc(string)"/>, which does not throw for a missing file — it
    /// returns the 1601 epoch. That sentinel is a negative and is not cached; a real stamp is.
    /// </summary>
    public static DateTime LastWriteTimeUtc(string path, bool cache = false)
    {
        string key = "M " + path;
        if (Cached<StampBox>(key, cache) is { } hit) return hit.Stamp;

        Interlocked.Increment(ref _calls);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        // The "no such file" sentinel. Caching it would hold a Not-Found placeholder on screen for T
        // after the file appeared, which is the failure R-sl4-8 exists to prevent.
        if (stamp.Year > 1601) Put(key, new StampBox(stamp), cache);
        return stamp;
    }

    private sealed record StampBox(DateTime Stamp);

    /// <summary>
    /// The <c>.ccell</c>'s named primary for one view type — the fourth of the brief's five steps, and
    /// the only one that reads CONTENT rather than metadata. Reached only when a view sub-folder holds
    /// more than one file, which is the minority of cells.
    ///
    /// <para><b>This is the "which file is primary" question, and it is bounded by T like the other
    /// four. It is NOT <c>CellSymbolResolver.ResolveCcell</c></b>, which answers the cell's published
    /// PARAMETER interface and is deliberately uncached at any T — a stale parameter interface is a
    /// silently wrong instance, and SL3's interface watch reads through it.</para>
    ///
    /// <para>A <c>.ccell</c> that is absent, unreadable, or names no primary for this view is a
    /// negative and is not cached.</para>
    /// </summary>
    public static string? NamedPrimary(string cellFolder, ViewType viewType, bool cache = false)
    {
        string key = "C " + cellFolder + " " + (int)viewType;
        if (Cached<string>(key, cache) is { } hit) return hit;

        string ccellPath = Path.Combine(cellFolder, CellFolder.CcellFileName);

        Interlocked.Increment(ref _calls);
        if (!File.Exists(ccellPath)) return null;

        string? named;
        Interlocked.Increment(ref _calls);
        try
        {
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            named = viewType switch
            {
                ViewType.Schematic => ccell.PrimarySchematic,
                ViewType.Symbol    => ccell.PrimarySymbol,
                ViewType.Layout    => ccell.PrimaryLayout,
                _                  => null,
            };
        }
        catch (InvalidDataException)
        {
            return null;   // format mismatch or corrupt .ccell → treat as no primary named
        }

        if (named is { Length: > 0 }) Put(key, named, cache);
        return named;
    }
}
