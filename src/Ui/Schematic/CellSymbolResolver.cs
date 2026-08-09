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
    /// Whether this reference resolves WITHOUT a base directory.
    ///
    /// <para>A plain cell reference is a path relative to the schematic's own directory, so an
    /// unsaved schematic has no base for it and callers skip it. The two VIRTUAL forms below carry
    /// their own resolution rule and need none — which is what lets one dropped into a scratch
    /// schematic still draw its real pins.</para>
    ///
    /// <para><b>Ask here, never re-derive the list at a call site.</b> The exemption used to be
    /// spelled <c>!IsWBondRef(...)</c> in two places, so a kit part dropped on an unsaved schematic
    /// silently resolved to nothing and rendered as a pin-less placeholder — reading exactly like
    /// the drop having done nothing at all. A third virtual form must not be able to repeat that.</para>
    /// </summary>
    public static bool NeedsNoBaseDirectory(string cellRef)
        => PdkKitRegistry.IsKitRef(cellRef) || WBondSymbolProvider.IsWBondRef(cellRef);

    /// <summary>
    /// Resolves <paramref name="cellRef"/> relative to <paramref name="baseDir"/> and returns
    /// the three-state result.  On cache hit (same primary filename + mtime) returns immediately
    /// without touching the filesystem beyond the existence check.
    /// </summary>
    public static CellSymbolResolution Resolve(string cellRef, string? baseDir)
    {
        // 0. A kit part lives in memory, not on disk — checked FIRST, and never falling through to
        //    the path branch. Falling through would resolve "pdk://…" against baseDir, producing a
        //    NotFound that names a directory nobody ever expected to exist; the reference is not a
        //    path and must not be reported as a bad one. An unloaded kit is NotFound on purpose:
        //    that is the reported, repairable state, and it draws the same placeholder.
        if (PdkKitRegistry.IsKitRef(cellRef))
            return PdkKitRegistry.Find(cellRef) is { } part
                ? new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = part.Symbol }
                : CellSymbolResolution.NotFoundResult;

        // 0b. A wBond's symbol is GENERATED from the .wBond file it names — checked here for exactly
        //     the same reason, and resolved against the WORKSPACE ROOT rather than baseDir (R-wbb2-3).
        //     See WBondSymbolProvider for why this is a fourth mechanism rather than a .csym on disk.
        if (WBondSymbolProvider.IsWBondRef(cellRef))
            return WBondSymbolProvider.Resolve(cellRef, baseDir);

        // 1. Resolve path. A cell reference is relative to the schematic's own directory, so an
        //    unsaved schematic has nothing to resolve it against — NotFound, not a crash.
        if (baseDir is null) return CellSymbolResolution.NotFoundResult;

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

    // ── Resolution from a palette / drag payload ──────────────────────────────

    /// <summary>
    /// Resolves the value a palette tile and a drag payload carry, which is EITHER a virtual
    /// reference or an ABSOLUTE cell folder — the two forms one field has always held.
    ///
    /// <para><b>This exists because splitting the field is only correct for one of them.</b> Both
    /// call sites used to do <c>GetDirectoryName</c>/<c>GetFileName</c> and hand the halves to
    /// <see cref="Resolve"/>. That is right for a folder and destroys a virtual reference — a kit
    /// part's tile and its drag ghost both fell back to the placeholder glyph, so every part in an
    /// imported kit looked generic in the palette. Ask this instead of splitting by hand.</para>
    /// </summary>
    public static CellSymbolResolution ResolveCellDirOrRef(string? cellDirOrRef)
    {
        if (string.IsNullOrEmpty(cellDirOrRef)) return CellSymbolResolution.NotFoundResult;

        // A virtual reference is not a path and must never be taken apart as one.
        if (NeedsNoBaseDirectory(cellDirOrRef)) return Resolve(cellDirOrRef, null);

        try
        {
            string trimmed = cellDirOrRef.TrimEnd('/', '\\');
            string? parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent)) return CellSymbolResolution.NotFoundResult;
            return Resolve(Path.GetFileName(trimmed), parent);
        }
        catch
        {
            return CellSymbolResolution.NotFoundResult;
        }
    }

    // ── The cell's published interface ────────────────────────────────────────

    /// <summary>
    /// The <c>.ccell</c> a cell reference names — from memory for a kit part, from disk for a cell
    /// folder — or null when it cannot be read.
    ///
    /// <para><b>Why this lives beside symbol resolution rather than at each caller.</b> A cell
    /// reference has two halves: its artwork and its published interface. Symbol resolution was
    /// already funnelled here; the interface was read directly wherever it was wanted, which is why
    /// making kit parts virtual touches so many call sites. Both halves resolve through this file
    /// now, so a reference form added later is taught to one place, not to a dozen.</para>
    ///
    /// <para>Deliberately NOT cached: a <c>.ccell</c> is small, read at placement and at dialog-open
    /// rather than per frame, and a stale parameter interface is a silently wrong instance. The
    /// symbol cache above exists because symbols are re-read on every render; this is not.</para>
    /// </summary>
    public static CcellFile? ResolveCcell(string cellRef, string baseDir)
    {
        if (PdkKitRegistry.IsKitRef(cellRef))
            return PdkKitRegistry.Find(cellRef)?.Ccell;

        // A wBond is a built-in primitive whose SYMBOL happens to come from a file; it has no cell
        // and therefore no published parameter interface. Answered here rather than left to fall
        // through, so the path branch is never handed a reference that is not a path.
        if (WBondSymbolProvider.IsWBondRef(cellRef)) return null;

        try
        {
            string cellAbsDir = Path.GetFullPath(Path.Combine(baseDir, cellRef));
            string ccellPath  = Path.Combine(cellAbsDir, CellFolder.CcellFileName);
            return File.Exists(ccellPath) ? CellPersistence.LoadFromFile(ccellPath) : null;
        }
        catch
        {
            return null;
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
