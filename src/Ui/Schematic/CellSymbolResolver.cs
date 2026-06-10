namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  CellSymbolResolver — resolves a cell reference (relative path) to a Symbol.
//  Framework-free (no Avalonia / Skia).
//
//  Resolution chain (workspace-and-project-tree.md §4):
//    CellRef (relative path) → cell folder → .ccell → primary .csym → Symbol
//
//  Three result states (§4.2 — keep distinct):
//    Resolved         — path resolves, primary .csym loaded successfully
//    NotFound         — cell folder doesn't exist at the resolved path
//    PrimaryMissing   — cell resolves but .ccell names a missing/absent .csym,
//                       or cell folder exists but has no primary symbol yet
//
//  Cache: keyed by (cellAbsDir, primaryFilename, symFileMtime).
//  Invalidated by Make-Primary and Symbol Editor save paths.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Three-state result of resolving a cell reference to its primary symbol.</summary>
public enum CellSymbolState
{
    /// <summary>Cell resolves and primary .csym loaded; Symbol carries the primitives + pins.</summary>
    Resolved,
    /// <summary>The relative path does not resolve to an existing cell folder.</summary>
    NotFound,
    /// <summary>Cell folder resolves but its primary .csym is absent or contradicted.</summary>
    PrimaryMissing,
}

/// <summary>Result of a cell-symbol resolution attempt.</summary>
public sealed class CellSymbolResolution
{
    public static readonly CellSymbolResolution NotFoundResult    = new() { State = CellSymbolState.NotFound };
    public static readonly CellSymbolResolution PrimaryMissingResult = new() { State = CellSymbolState.PrimaryMissing };

    public CellSymbolState State  { get; init; }
    /// <summary>Non-null when State == Resolved.</summary>
    public Symbol?         Symbol { get; init; }
}

/// <summary>
/// Framework-free resolver: CellRef (relative path) + base directory → CellSymbolResolution.
/// Caches loaded symbols by (cellAbsDir, primaryFilename, symFileMtime); invalidate on
/// Make-Primary or .csym save so open schematics re-render to the new symbol.
/// </summary>
public static class CellSymbolResolver
{
    private sealed record CacheKey(string CellAbsDir, string PrimaryName);
    private sealed record CacheEntry(DateTime SymMtime, Symbol Symbol);

    private static readonly Dictionary<CacheKey, CacheEntry> _cache
        = new(EqualityComparer<CacheKey>.Default);
    private static readonly object _lock = new();

    // ── Resolve ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <paramref name="cellRef"/> relative to <paramref name="baseDir"/> and returns
    /// the three-state result.  On cache hit (same primary filename + mtime) returns immediately
    /// without touching the filesystem beyond the existence check.
    /// </summary>
    public static CellSymbolResolution Resolve(string cellRef, string baseDir)
    {
        // 1. Resolve path.
        string cellAbsDir;
        try
        {
            cellAbsDir = Path.GetFullPath(Path.Combine(baseDir, cellRef));
        }
        catch
        {
            return CellSymbolResolution.NotFoundResult;
        }

        if (!Directory.Exists(cellAbsDir))
            return CellSymbolResolution.NotFoundResult;

        // 2. Determine primary symbol via CellFolder.ResolvePrimary (single primacy source).
        PrimaryResolution primary;
        try
        {
            primary = CellFolder.ResolvePrimary(cellAbsDir, ViewType.Symbol);
        }
        catch
        {
            return CellSymbolResolution.PrimaryMissingResult;
        }

        switch (primary.State)
        {
            case PrimaryState.MissingNamedPrimary:
                return CellSymbolResolution.PrimaryMissingResult;
            case PrimaryState.NoView:
            case PrimaryState.NoPrimary:
                return CellSymbolResolution.PrimaryMissingResult;
        }

        // 3. Load symbol (with cache check on mtime).
        string primaryName = primary.ResolvedName!;
        string symDir      = CellFolder.SubFolderPath(cellAbsDir, ViewType.Symbol);
        string symPath     = Path.Combine(symDir, primaryName);

        try
        {
            var mtime = File.GetLastWriteTimeUtc(symPath);
            var key   = new CacheKey(cellAbsDir, primaryName);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached) && cached.SymMtime == mtime)
                    return new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = cached.Symbol };
            }

            var symbol = SymbolPersistence.LoadFromFile(symPath);
            // Re-read mtime after load in case the file changed while loading.
            var mtimeAfter = File.GetLastWriteTimeUtc(symPath);

            lock (_lock)
            {
                _cache[key] = new CacheEntry(mtimeAfter, symbol);
            }

            return new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = symbol };
        }
        catch
        {
            return CellSymbolResolution.PrimaryMissingResult;
        }
    }

    // ── Invalidation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the cached entry for the given cell folder (absolute path).
    /// Call after Make-Primary writes a new .ccell for this cell.
    /// </summary>
    public static void Invalidate(string cellAbsDir)
    {
        lock (_lock)
        {
            var toRemove = _cache.Keys
                .Where(k => string.Equals(k.CellAbsDir, cellAbsDir, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in toRemove) _cache.Remove(k);
        }
    }

    /// <summary>
    /// Clears the entire cache.  Call after any .csym save when the cell dir is unknown
    /// (e.g. after a symbol editor save that could affect any cell).
    /// </summary>
    public static void InvalidateAll()
    {
        lock (_lock) _cache.Clear();
    }
}
