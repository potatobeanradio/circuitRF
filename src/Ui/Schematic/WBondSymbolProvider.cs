using CircuitRF.WBond;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The symbol of a placed <c>wBond</c>, generated from the <c>.wBond</c> file it references
/// (wbond.md §5.1, brief-wbond-wbb2 R-wbb2-1).
///
/// <h3>Why this is a FOURTH mechanism, and what it was chosen on</h3>
/// <para>Three mechanisms already produce a component's symbol, and none of them fits:</para>
/// <list type="bullet">
///   <item><b>A built-in <c>SymbolKind</c></b> has fixed artwork; a wBond has no fixed pin count.</item>
///   <item><b>A variadic <c>SymbolKind</c> + <c>PortCount</c></b> (SnP, SDD, ZPort) lets the USER set
///     the count; a wBond's is a property of the FILE, and its pins carry NAMES that route has
///     nowhere to put.</item>
///   <item><b>A <c>CellRef</c> to a cell folder</b> needs a <c>.csym</c> on disk. Writing one makes a
///     second copy of the array list, and that copy goes stale the moment the <c>.wBond</c> is
///     edited — the exact MTee failure <c>project-brief-L5-followups</c> already records.</item>
/// </list>
///
/// <para><b>The criterion the choice was made on is R-wbb2-1's own question — what happens when the
/// referenced design changes.</b> This mechanism answers it structurally rather than by remembering
/// to invalidate something: <b>there is no persisted copy of the symbol at all.</b> The symbol is
/// generated on demand from the file's current contents and held only in a process-lifetime cache
/// keyed by the file's own mtime and length. A stale symbol is therefore not a bug to avoid but a
/// state that cannot be represented — which is a stronger guarantee than a content-addressed
/// on-disk store gives, because that store still has to be told the generator changed.</para>
///
/// <h3>The reference is DERIVED, never stored a second time</h3>
/// <para>A wBond component carries exactly one fact about its design: the <c>File</c> parameter.
/// The reference this resolver takes is computed from it (<see cref="RefFor"/>), so there is no
/// second field to keep in step — an edit to <c>File</c> re-points the symbol by construction. That
/// is why <c>EditableComponent.CellRef</c> stays null for a wBond: a second persisted path is
/// exactly the drift this whole mechanism exists to avoid.</para>
///
/// <para>It plugs into the seam <see cref="CellSymbolResolver"/> already has for
/// <see cref="PdkKitRegistry"/>, checked ahead of the path branch and for the same reason: the
/// reference is not a path and must not be reported as a bad one.</para>
/// </summary>
public static class WBondSymbolProvider
{
    /// <summary>
    /// Marks a symbol reference as naming a <c>.wBond</c> design rather than a cell folder. Same
    /// role <see cref="PdkKitRegistry.Scheme"/> plays, for the same reason — a reference that states
    /// its own kind can never be mistaken for a mistyped relative path.
    /// </summary>
    public const string Scheme = "wbond://";

    // ── The reference form ────────────────────────────────────────────────────

    /// <summary>
    /// The symbol reference for a wBond component whose <c>File</c> parameter holds
    /// <paramref name="file"/>. Never null for a wBond — a blank <c>File</c> yields a reference that
    /// resolves to <see cref="CellSymbolState.NotFound"/>, which is what draws the existing
    /// placeholder and reports, rather than silently falling back to a two-pin built-in glyph.
    /// </summary>
    public static string RefFor(string? file) => Scheme + (file ?? "").Trim();

    /// <summary>True when this reference names a <c>.wBond</c> design.</summary>
    public static bool IsWBondRef(string? symbolRef)
        => symbolRef is not null && symbolRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored <c>File</c> value carried by a wBond reference, or null.</summary>
    public static string? FileOf(string? symbolRef)
        => IsWBondRef(symbolRef) ? symbolRef![Scheme.Length..] : null;

    // ── R-wbb2-3: File resolves against the WORKSPACE ROOT ────────────────────

    /// <summary>
    /// The workspace root a schematic belongs to — the directory holding the nearest ancestor
    /// <c>.cws</c>, or null for a loose/scratch schematic.
    /// </summary>
    public static string? WorkspaceRootOf(string? schematicDir)
        => WorkspaceRootFinder.FindAncestorCws(schematicDir) is { } cws
            ? Path.GetDirectoryName(cws)
            : null;

    /// <summary>
    /// R-wbb2-3 — resolves a stored <c>File</c> value to an absolute path <b>the same way
    /// <c>Elaborator.ResolveWBondParameters</c> does</b>: rooted paths as written, relative ones
    /// against the WORKSPACE ROOT (never the schematic's own directory, which is what a
    /// <c>CellRef</c> resolves against). The two must agree or the symbol a user sees is generated
    /// from a different file than the one that is simulated.
    /// </summary>
    public static string? ResolveFilePath(string? file, string? schematicDir)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;

        // Tolerate Windows-authored separators, exactly as the elaborator's own resolver does.
        string f = file.Trim().Replace('\\', '/');
        try
        {
            if (Path.IsPathRooted(f)) return Path.GetFullPath(f);
            string? root = WorkspaceRootOf(schematicDir);
            return root is null ? null : Path.GetFullPath(Path.Combine(root, f));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// §5 question 1 — the value the placement path WRITES: workspace-relative when the design lives
    /// inside the workspace, absolute when it does not.
    ///
    /// <para>A relative value is portable and travels with a shared workspace; a <c>.wBond</c>
    /// outside the workspace has no relative form that means anything on another machine, so an
    /// absolute path is the honest answer there. Same rule <c>.clay</c>'s <c>TechRef</c> and the
    /// Known-File reference already follow, and stated here rather than left to each caller.</para>
    /// </summary>
    public static string StoredFileValueFor(string absolutePath, string? workspaceRootDir)
    {
        if (string.IsNullOrWhiteSpace(workspaceRootDir)) return absolutePath;
        try
        {
            if (WorkspaceRootFinder.IsOutside(absolutePath, workspaceRootDir!)) return absolutePath;
            return Path.GetRelativePath(workspaceRootDir!, absolutePath).Replace('\\', '/');
        }
        catch
        {
            return absolutePath;
        }
    }

    // ── The array list a placed instance was wired against ────────────────────

    /// <summary>
    /// The design's array names in order — the identity a placed instance's wiring was drawn
    /// against (§5 question 3). Deliberately NOT
    /// <c>WBondSymbolGenerator.ContentKey</c>: that carries the generator's own content version, so
    /// bumping it would report an array reorder on every placed instance in the field.
    /// </summary>
    public static string ArraysKeyOf(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return string.Join('|', design.Arrays.Select(a => a.Name));
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>What one <c>.wBond</c> file yields: its generated symbol and its array identity.</summary>
    /// <param name="Symbol">Null when the design declares no arrays — there are no pins, so nothing placeable.</param>
    public sealed record LoadedDesign(Symbol? Symbol, string ArraysKey, int ArrayCount, string AbsolutePath);

    // Keyed by absolute path. The (mtime, length, generator content version) triple is what makes a
    // saved edit take effect: a changed file cannot hit a cached entry. Length is carried alongside
    // mtime because a same-second overwrite can leave the timestamp unmoved on some filesystems.
    private sealed record CacheEntry(DateTime Mtime, long Length, int ContentVersion, LoadedDesign Value);

    private static readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _gate = new();

    /// <summary>
    /// Reads and generates for one absolute path, or null when the file is absent or unreadable.
    /// Never throws: an unreadable design is a reported, repairable state, not a crash on a render
    /// pass.
    /// </summary>
    public static LoadedDesign? Load(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        DateTime mtime;
        long length;
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists) return null;
            mtime  = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch { return null; }

        lock (_gate)
        {
            if (_cache.TryGetValue(absolutePath, out var hit)
                && hit.Mtime == mtime && hit.Length == length
                && hit.ContentVersion == WBondSymbolGenerator.ContentVersion)
                return hit.Value;
        }

        LoadedDesign loaded;
        try
        {
            var design = WBondIo.ReadFile(absolutePath);
            loaded = new LoadedDesign(
                WBondSymbolGenerator.Build(design),
                ArraysKeyOf(design),
                design.Arrays.Count,
                absolutePath);
        }
        catch { return null; }

        lock (_gate)
        {
            _cache[absolutePath] = new CacheEntry(mtime, length, WBondSymbolGenerator.ContentVersion, loaded);
        }
        return loaded;
    }

    // ── The CellSymbolResolver seam ───────────────────────────────────────────

    /// <summary>
    /// Resolves a <c>wbond://</c> reference to the three-state result every other symbol source
    /// produces, so the renderer, the hit-test and the extractor need no wBond-specific branch.
    ///
    /// <list type="bullet">
    ///   <item><b>Resolved</b> — the file was read and declares at least one array.</item>
    ///   <item><b>NotFound</b> — no <c>File</c>, or the file is absent/unreadable. Draws the existing
    ///     Not-Found placeholder.</item>
    ///   <item><b>PrimaryMissing</b> — the file reads but declares no arrays, so there is nothing to
    ///     wire. Kept distinct from NotFound because the two need different fixes.</item>
    /// </list>
    /// </summary>
    public static CellSymbolResolution Resolve(string symbolRef, string? schematicDir)
    {
        string? abs = ResolveFilePath(FileOf(symbolRef), schematicDir);
        if (abs is null) return CellSymbolResolution.NotFoundResult;

        var loaded = Load(abs);
        if (loaded is null) return CellSymbolResolution.NotFoundResult;

        return loaded.Symbol is { } sym
            ? new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = sym }
            : CellSymbolResolution.PrimaryMissingResult;
    }

    // ── Invalidation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Drops the cached load for one design. Called when a <c>.wBond</c> editor saves, so an open
    /// schematic re-generates rather than waiting for a filesystem timestamp comparison it has no
    /// reason to make until something asks it to re-render.
    /// </summary>
    public static void Invalidate(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        lock (_gate) _cache.Remove(absolutePath!);
    }

    /// <summary>Clears every cached load — called when a workspace is left.</summary>
    public static void InvalidateAll()
    {
        lock (_gate) _cache.Clear();
    }
}
