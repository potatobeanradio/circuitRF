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

/// <summary>
/// Three-state result of resolving a cell reference to its primary symbol.
///
/// <para><b>Why TM2's <c>Moved</c> is NOT a fourth member here</b> (R-tm2-11, and see
/// <c>src/Ui/RESOLVED.md</c>). These three are three ways a symbol can be MISSING, and every one of
/// them replaces the drawn glyph. A reference that resolved through a forwarding record is not one of
/// those: the cell resolves, the symbol is right, the drawing is right, and only the stored spelling
/// is stale. Adding it here would put it on the "draw a placeholder instead" path at a dozen
/// <c>State == Resolved</c> call sites, which is precisely what R-tm2-12 forbids ("not the rendered
/// geometry — R36 holds without exception"). It travels on
/// <see cref="CellSymbolResolution.Redirect"/> instead — the same shape SL3 shipped
/// <c>InterfaceChanged</c> in, for the same reason and after making the same argument.</para>
/// </summary>
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

    /// <summary>
    /// Non-null when this reference only resolved because the owning root's <c>.cmoves</c> said where
    /// the cell went (TM2 R-tm2-11). It is carried BESIDE the state, not folded into it — see
    /// <see cref="CellSymbolState"/>'s own note on why <c>Moved</c> is not a fifth member.
    /// </summary>
    public MoveRedirectHit? Redirect { get; init; }

    /// <summary>The same result with a redirect attached. Used by <see cref="CellSymbolResolver"/>'s
    /// early returns, which hand back the shared singletons.</summary>
    public CellSymbolResolution With(MoveRedirectHit? redirect) =>
        redirect is null ? this : new CellSymbolResolution { State = State, Symbol = Symbol, Redirect = redirect };
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
        => PdkKitRegistry.IsKitRef(cellRef) || WBondSymbolProvider.IsWBondRef(cellRef)
        // A SpiceModel DOES resolve a path, so it is not exempt in general — but an unconfigured one
        // (blank File) draws its generic two-port with nothing looked up, and an ABSOLUTE path needs
        // no base either. Both have to work on an unsaved scratch schematic, where a component that
        // resolved to nothing would render pin-less and read as the drop having failed.
        || (SpiceModelSymbolProvider.Parse(cellRef) is { } r
                && (r.File.Length == 0 || System.IO.Path.IsPathRooted(r.File)));

    /// <summary>
    /// Resolves <paramref name="cellRef"/> relative to <paramref name="baseDir"/> and returns
    /// the three-state result.  On cache hit (same primary filename + mtime) returns immediately
    /// without touching the filesystem beyond the existence check.
    /// </summary>
    /// <param name="workspaceRoot">
    /// The workspace this resolution is being made on behalf of, when the caller knows it — a palette
    /// tile or a drag ghost, which have no document to walk up from. Left null, the workspace is the
    /// one <paramref name="baseDir"/> belongs to (MW1 R-mw1-5): the same ancestor-<c>.cws</c> walk-up
    /// technology already uses, memoised in <see cref="WorkspaceRootFinder.WorkspaceDirOf"/>.
    /// </param>
    public static CellSymbolResolution Resolve(string cellRef, string? baseDir, string? workspaceRoot = null)
    {
        // 0. A kit part lives in memory, not on disk — checked FIRST, and never falling through to
        //    the path branch. Falling through would resolve "pdk://…" against baseDir, producing a
        //    NotFound that names a directory nobody ever expected to exist; the reference is not a
        //    path and must not be reported as a bad one. An unloaded kit is NotFound on purpose:
        //    that is the reported, repairable state, and it draws the same placeholder.
        //
        //    WHICH workspace's kits is the question multi-window added, and the reference cannot
        //    answer it — it is written into user files and names no machine-specific path. The ASKER
        //    answers instead: the referencing document's own parent workspace (R-mw1-5).
        if (PdkKitRegistry.IsKitRef(cellRef))
            return KitPartFor(cellRef, baseDir, workspaceRoot) is { } part
                ? new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = part.Symbol }
                : CellSymbolResolution.NotFoundResult;

        // 0b. A wBond's symbol is GENERATED from the .wBond file it names — checked here for exactly
        //     the same reason, and resolved against the WORKSPACE ROOT rather than baseDir (R-wbb2-3).
        //     See WBondSymbolProvider for why this is a fourth mechanism rather than a .csym on disk.
        if (WBondSymbolProvider.IsWBondRef(cellRef))
            return WBondSymbolProvider.Resolve(cellRef, baseDir);

        // 0c. A SpiceModel's symbol is GENERATED from the SPICE file its reference names, and the
        //     definition it picks out of that file decides both the artwork and the pin count. Same
        //     placement and the same reason as the two above: the reference is not a cell path.
        if (SpiceModelSymbolProvider.IsSpiceModelRef(cellRef))
            return SpiceModelSymbolProvider.Resolve(cellRef, baseDir);

        // 1. Resolve path. A cell reference is relative to the schematic's own directory, so an
        //    unsaved schematic has nothing to resolve it against — NotFound, not a crash. A ws://
        //    reference is resolved through the referencing workspace's alias table instead (MW2
        //    R-mw2-2); an alias the workspace does not declare, or one whose target has moved, comes
        //    back null and lands on the same reported, repairable NotFound.
        // TM2 R-tm2-8: a reference that resolves to nothing is retried through the owning root's
        // .cmoves, INSIDE ResolveCellDir. What comes back here is the folder it finally named, plus
        // the record that named it — which every state below carries, because a cell that moved and
        // then lost its primary symbol still needs to say where it went.
        if (ExternalCellRef.ResolveCellDir(cellRef, baseDir, out var redirect, out bool folderExists)
                is not { } cellAbsDir)
            return CellSymbolResolution.NotFoundResult;

        // SL4 R-sl4-6/-7: the whole resolution path's filesystem access goes through CellStat —
        // ResolveCellDir's existence check above, ResolvePrimary's three, and the mtime below. See
        // that type for the count this makes measurable and for the stated bound T the positive
        // answers are cached within.
        //
        // THIS is the path that caches; the project tree's own scan calls the same helpers and does
        // NOT (it passes the default), because a user who just created a symbol and pressed Refresh
        // must see it. What is bounded by T is a CELL REFERENCE being re-resolved on every edit —
        // four filesystem round trips per referenced component, measured — which over a link with
        // tens of milliseconds of latency is a few hundred per keystroke-scale edit.
        //
        // TM2: the existence answer is TAKEN from ResolveCellDir rather than asked again. That step
        // has to ask it anyway (R-tm2-8's step 2 is what makes the redirect safe), and asking twice
        // would be a fifth round trip per component per edit in the uncached world — which is the
        // number R-sl4-6's gate pins exactly, on purpose, so it cannot drift up one call at a time.
        if (!folderExists)
            return CellSymbolResolution.NotFoundResult.With(redirect);

        // 2. Determine primary symbol via CellFolder.ResolvePrimary (single primacy source).
        PrimaryResolution primary;
        try
        {
            primary = CellFolder.ResolvePrimary(cellAbsDir, ViewType.Symbol, useStatCache: true);
        }
        catch
        {
            return CellSymbolResolution.PrimaryMissingResult.With(redirect);
        }

        switch (primary.State)
        {
            case PrimaryState.MissingNamedPrimary:
                return CellSymbolResolution.PrimaryMissingResult.With(redirect);
            case PrimaryState.NoView:
            case PrimaryState.NoPrimary:
                return CellSymbolResolution.PrimaryMissingResult.With(redirect);
        }

        // 3. Load symbol (with cache check on mtime).
        string primaryName = primary.ResolvedName!;
        string symDir      = CellFolder.SubFolderPath(cellAbsDir, ViewType.Symbol);
        string symPath     = Path.Combine(symDir, primaryName);

        try
        {
            var mtime = CellStat.LastWriteTimeUtc(symPath, cache: true);
            var key   = new CacheKey(cellAbsDir, primaryName);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached) && cached.SymMtime == mtime)
                    return new CellSymbolResolution
                        { State = CellSymbolState.Resolved, Symbol = cached.Symbol, Redirect = redirect };
            }

            var symbol = SymbolPersistence.LoadFromFile(symPath);
            // Re-read mtime after load in case the file changed while loading. Deliberately OUTSIDE
            // CellStat: the whole point is to observe the file as it is right now, and a cached
            // answer would hand back the stamp read a moment ago and defeat the check. It is reached
            // only on a symbol-cache MISS, so it is not part of the per-edit steady state the count
            // measures — a load is happening on this path anyway.
            var mtimeAfter = File.GetLastWriteTimeUtc(symPath);

            lock (_lock)
            {
                _cache[key] = new CacheEntry(mtimeAfter, symbol);
            }

            return new CellSymbolResolution
                { State = CellSymbolState.Resolved, Symbol = symbol, Redirect = redirect };
        }
        catch
        {
            return CellSymbolResolution.PrimaryMissingResult.With(redirect);
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
    public static CellSymbolResolution ResolveCellDirOrRef(string? cellDirOrRef, string? workspaceRoot = null)
    {
        if (string.IsNullOrEmpty(cellDirOrRef)) return CellSymbolResolution.NotFoundResult;

        // A virtual reference is not a path and must never be taken apart as one.
        if (NeedsNoBaseDirectory(cellDirOrRef)) return Resolve(cellDirOrRef, null, workspaceRoot);

        // A ws:// reference is not a path either, and it also cannot be resolved from here: it names
        // an alias the REFERENCING DOCUMENT's workspace declares, and this overload is reached only
        // from a palette tile or a drag ghost, which have no document. NotFound is the honest answer;
        // splitting it with GetDirectoryName would produce "ws:/A" and report a path nobody wrote.
        // Unreachable today — a tree drag carries the ABSOLUTE cell folder and the alias form is
        // produced at the drop by ExternalCellRef.MakeCellRef — and guarded so it stays that way.
        if (ExternalCellRef.IsExternalRef(cellDirOrRef)) return CellSymbolResolution.NotFoundResult;

        try
        {
            string trimmed = cellDirOrRef.TrimEnd('/', '\\');
            string? parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent)) return CellSymbolResolution.NotFoundResult;
            return Resolve(Path.GetFileName(trimmed), parent, workspaceRoot);
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
    public static CcellFile? ResolveCcell(string cellRef, string baseDir, string? workspaceRoot = null)
    {
        if (PdkKitRegistry.IsKitRef(cellRef))
            return KitPartFor(cellRef, baseDir, workspaceRoot)?.Ccell;

        // A wBond is a built-in primitive whose SYMBOL happens to come from a file; it has no cell
        // and therefore no published parameter interface. Answered here rather than left to fall
        // through, so the path branch is never handed a reference that is not a path.
        if (WBondSymbolProvider.IsWBondRef(cellRef)) return null;

        try
        {
            if (ExternalCellRef.ResolveCellDir(cellRef, baseDir) is not { } cellAbsDir) return null;
            string ccellPath = Path.Combine(cellAbsDir, CellFolder.CcellFileName);
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
        // SL4: the stat answers this cell's resolution rests on are dropped with it, so a
        // Make-Primary is seen at once rather than within T. The user changed it themselves; the
        // freshness bound is about someone ELSE's edit arriving over a wire.
        CellStat.Invalidate(cellAbsDir);
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
        // The workspace a path belongs to is memoised for the same reason the symbols are — it is
        // asked per cell instance per render (R-mw1-6) — so it is dropped at the same moments. A
        // .cws appearing or disappearing changes the answer and nothing else would notice.
        WorkspaceRootFinder.InvalidateCache();
    }

    // ── Which workspace's kits (MW1 R-mw1-5) ──────────────────────────────────

    /// <summary>
    /// The kit part <paramref name="cellRef"/> names, resolved against the workspace the caller
    /// named or — failing that — the one <paramref name="baseDir"/> belongs to.
    ///
    /// <para><b>When neither is available it falls back to every mounted workspace</b>, and that is a
    /// deliberate, narrow compromise rather than an oversight. It is reached only by a preview that
    /// has no document and no window behind it: an unsaved scratch schematic, or a palette tile whose
    /// host did not supply its own workspace. The consequence is bounded — a kit reference names a
    /// kit and a part, so the worst case is a same-named kit in another open workspace supplying the
    /// GLYPH; anything a design actually carries reaches here with a baseDir.</para>
    /// </summary>
    private static PdkKitPart? KitPartFor(string cellRef, string? baseDir, string? workspaceRoot)
    {
        string? root = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? workspaceRoot
            : WorkspaceRootFinder.WorkspaceDirOf(baseDir);

        return root is null
            ? PdkKitRegistry.FindInAnyWorkspace(cellRef)
            : PdkKitRegistry.Find(cellRef, root);
    }
}
