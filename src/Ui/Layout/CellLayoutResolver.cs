// Resolves a layout cell reference (relative path) to a LayoutView. Framework-free (no Avalonia/Skia).
// Mirrors CircuitRF.Ui.Schematic.CellSymbolResolver structurally (docs/sonnet-briefs/
// brief-L3a-instances-and-arrays.md §1: "reuse, do not reinvent") — same three-state shape, same
// (path, mtime)-keyed cache, same explicit-invalidation-only lifecycle (no FileSystemWatcher; L0c
// already ruled that out and the reasoning is unchanged here).
//
// Resolution chain: CellRef (relative path) -> cell folder -> .ccell -> primary .clay -> LayoutView.

using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>Three-state result of resolving a cell reference to its primary layout, mirroring
/// <see cref="CellSymbolState"/>.</summary>
public enum CellLayoutState
{
    /// <summary>Cell resolves and the primary .clay loaded; <see cref="CellLayoutResolution.View"/>
    /// carries the parsed <see cref="LayoutView"/>.</summary>
    Resolved,
    /// <summary>The relative path does not resolve to an existing cell folder.</summary>
    NotFound,
    /// <summary>Cell folder resolves but its primary .clay is absent, contradicted, or fails to parse.</summary>
    PrimaryMissing,
}

/// <summary>Result of a cell-layout resolution attempt.</summary>
public sealed class CellLayoutResolution
{
    public static readonly CellLayoutResolution NotFoundResult       = new() { State = CellLayoutState.NotFound };
    public static readonly CellLayoutResolution PrimaryMissingResult = new() { State = CellLayoutState.PrimaryMissing };

    public CellLayoutState State           { get; init; }
    /// <summary>Non-null when State == Resolved.</summary>
    public LayoutView?     View            { get; init; }
    /// <summary>Non-null when State == Resolved — the absolute cell folder directory, used as the
    /// identity key for cycle detection (R-L3a-2) and as the base directory for resolving THIS cell's
    /// own nested instance references.</summary>
    public string?         ResolvedCellDir { get; init; }
}

/// <summary>
/// Framework-free resolver: CellRef (relative path) + base directory -> CellLayoutResolution.
/// Caches loaded LayoutViews by (cellAbsDir, primaryFilename, layoutFileMtime); invalidate explicitly
/// (Make-Primary, a .clay save elsewhere, or a manual "Reload" action — L3b's concern, not built here)
/// so open documents referencing this cell re-render with fresh geometry.
/// </summary>
public static class CellLayoutResolver
{
    private sealed record CacheKey(string CellAbsDir, string PrimaryName);
    private sealed record CacheEntry(DateTime LayoutMtime, LayoutView View);

    private static readonly Dictionary<CacheKey, CacheEntry> _cache = new();

    /// <summary>
    /// Live, unsaved overrides installed by an open push-in session or an ordinarily-open tab on the
    /// SAME <c>.clay</c> (brief-L3b-hierarchy-navigation.md §2/R-L3b-1) — checked by <see cref="Resolve"/>
    /// before the file-backed cache, so a parent showing this cell sees an in-progress edit
    /// immediately, without a Save. Keyed by the resolved <c>.clay</c> ABSOLUTE FILE path (not the cell
    /// folder) — the same identity <c>LayoutSessionRegistry</c> uses. Deliberately a SEPARATE dictionary
    /// from <see cref="_cache"/>, mirroring <c>TechnologyCache._live</c>'s own reasoning: a session
    /// closing without saving must be able to drop its override and fall back to the last known
    /// on-disk value without forcing a disk re-read of a file that was never touched.
    /// </summary>
    private static readonly Dictionary<string, LayoutView> _live = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _lock = new();

    /// <summary>Bumped by <see cref="Invalidate"/>/<see cref="InvalidateAll"/>/<see cref="SetLive"/>/
    /// <see cref="ClearLive"/> — an opaque freshness token consumers (the spatial index, in particular)
    /// can compare against a previously-observed value to know "something about resolution may have
    /// changed" without needing to know WHAT changed. See <see cref="LayoutSpatialIndex"/>'s own doc
    /// comment for how this closes the "EnsureFresh must account for a resolution change" requirement
    /// (R-L3a-4) — the SAME mechanism also covers R-L3b-1's spatial-index half for free, since every
    /// query already passes this value as <c>resolutionVersion</c>.</summary>
    public static long Generation { get; private set; }

    /// <summary>Raised after <see cref="SetLive"/>/<see cref="ClearLive"/>, or once per affected
    /// <c>.clay</c> path from <see cref="Invalidate"/>/<see cref="InvalidateAll"/>, carrying the
    /// absolute file path that changed. The live-refresh seam — subscribers (the workspace) re-resolve
    /// and repaint whatever depended on that path.</summary>
    public static event Action<string>? LiveViewChanged;

    // ── Resolve ───────────────────────────────────────────────────────────────

    public static CellLayoutResolution Resolve(string cellRef, string baseDir)
    {
        string cellAbsDir;
        try
        {
            cellAbsDir = Path.GetFullPath(Path.Combine(baseDir, cellRef));
        }
        catch
        {
            return CellLayoutResolution.NotFoundResult;
        }

        if (!Directory.Exists(cellAbsDir))
            return CellLayoutResolution.NotFoundResult;

        PrimaryResolution primary;
        try
        {
            primary = CellFolder.ResolvePrimary(cellAbsDir, ViewType.Layout);
        }
        catch
        {
            return CellLayoutResolution.PrimaryMissingResult;
        }

        switch (primary.State)
        {
            case PrimaryState.MissingNamedPrimary:
            case PrimaryState.NoView:
            case PrimaryState.NoPrimary:
                return CellLayoutResolution.PrimaryMissingResult;
        }

        string primaryName = primary.ResolvedName!;
        string layoutDir  = CellFolder.SubFolderPath(cellAbsDir, ViewType.Layout);
        string layoutPath = Path.Combine(layoutDir, primaryName);

        lock (_lock)
        {
            if (_live.TryGetValue(layoutPath, out var liveView))
                return new CellLayoutResolution { State = CellLayoutState.Resolved, View = liveView, ResolvedCellDir = cellAbsDir };
        }

        try
        {
            var mtime = File.GetLastWriteTimeUtc(layoutPath);
            var key   = new CacheKey(cellAbsDir, primaryName);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached) && cached.LayoutMtime == mtime)
                    return new CellLayoutResolution { State = CellLayoutState.Resolved, View = cached.View, ResolvedCellDir = cellAbsDir };
            }

            var view = LayoutPersistence.LoadFromFile(layoutPath);
            var mtimeAfter = File.GetLastWriteTimeUtc(layoutPath);

            lock (_lock)
            {
                _cache[key] = new CacheEntry(mtimeAfter, view);
            }

            return new CellLayoutResolution { State = CellLayoutState.Resolved, View = view, ResolvedCellDir = cellAbsDir };
        }
        catch
        {
            return CellLayoutResolution.PrimaryMissingResult;
        }
    }

    // ── Live overrides (R-L3b-1's in-session-edit path) ────────────────────────

    /// <summary>
    /// Installs (or replaces) a live override for <paramref name="clayAbsPath"/> and raises
    /// <see cref="LiveViewChanged"/> — called on every edit of an open push-in session (fires "on the
    /// edit, not on save," per R-L3b-1), not just once at session-open. Always safe to call with the
    /// SAME <see cref="LayoutView"/> reference repeatedly (a session's model is mutated in place
    /// across edits): <see cref="Generation"/> still bumps and the event still fires every time, which
    /// is what re-triggers the L2b spatial index's freshness check and the workspace's repaint
    /// broadcast even when the dictionary entry's VALUE didn't change reference.
    /// </summary>
    public static void SetLive(string clayAbsPath, LayoutView view)
    {
        clayAbsPath = Path.GetFullPath(clayAbsPath);
        lock (_lock) { _live[clayAbsPath] = view; Generation++; }
        LiveViewChanged?.Invoke(clayAbsPath);
    }

    /// <summary>Drops the live override for <paramref name="clayAbsPath"/> (if any) WITHOUT touching
    /// the file-backed cache, so <see cref="Resolve"/> falls back to the last known on-disk value —
    /// correct for a session closing/retiring without having been saved (disk was never touched, so
    /// the old cached/lazily-reloaded value is still exactly right). No-op, no event, when no override
    /// was installed.</summary>
    public static void ClearLive(string clayAbsPath)
    {
        clayAbsPath = Path.GetFullPath(clayAbsPath);
        bool removed;
        lock (_lock) { removed = _live.Remove(clayAbsPath); if (removed) Generation++; }
        if (removed) LiveViewChanged?.Invoke(clayAbsPath);
    }

    /// <summary>True when a live (unsaved-to-this-path) override is installed for
    /// <paramref name="clayAbsPath"/>.</summary>
    public static bool HasLiveOverride(string clayAbsPath)
    {
        lock (_lock) return _live.ContainsKey(Path.GetFullPath(clayAbsPath));
    }

    // ── Invalidation (R-L3b-1's on-disk-change path) ───────────────────────────

    /// <summary>
    /// Drops every plain-cache AND live-override entry for <paramref name="cellAbsDir"/> — used when
    /// the cell's <c>.clay</c> changed on disk (a save from any surface). Mirrors
    /// <c>TechnologyCache.Invalidate</c>: a save clears the live override too (the just-saved content
    /// IS the new baseline; if the session is still open, its next edit installs a fresh override via
    /// <see cref="SetLive"/> automatically) — no separate "clear then re-set" dance needed at the call
    /// site. Raises <see cref="LiveViewChanged"/> once per affected path.
    /// </summary>
    public static void Invalidate(string cellAbsDir)
    {
        var affected = new List<string>();
        lock (_lock)
        {
            var layoutDir = CellFolder.SubFolderPath(cellAbsDir, ViewType.Layout);

            var toRemove = _cache.Keys
                .Where(k => string.Equals(k.CellAbsDir, cellAbsDir, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in toRemove)
            {
                affected.Add(Path.Combine(layoutDir, k.PrimaryName));
                _cache.Remove(k);
            }

            var liveToRemove = _live.Keys
                .Where(p => string.Equals(Path.GetDirectoryName(p), layoutDir, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var p in liveToRemove)
            {
                _live.Remove(p);
                if (!affected.Contains(p, StringComparer.OrdinalIgnoreCase)) affected.Add(p);
            }

            Generation++;
        }
        foreach (var p in affected) LiveViewChanged?.Invoke(p);
    }

    public static void InvalidateAll()
    {
        List<string> affected;
        lock (_lock)
        {
            affected = _cache.Keys
                .Select(k => Path.Combine(CellFolder.SubFolderPath(k.CellAbsDir, ViewType.Layout), k.PrimaryName))
                .Concat(_live.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _cache.Clear();
            _live.Clear();
            Generation++;
        }
        foreach (var p in affected) LiveViewChanged?.Invoke(p);
    }
}
