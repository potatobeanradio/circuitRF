namespace CircuitRF.Design.Layout;

/// <summary>
/// One-load-per-file cache for .ctech <see cref="Technology"/> files, keyed by absolute path
/// (<see cref="StringComparer.OrdinalIgnoreCase"/> — Windows and macOS paths are case-insensitive;
/// the loose comparison is harmless on Linux).
///
/// <b>Deliberately no <see cref="System.IO.FileSystemWatcher"/>.</b> Cross-platform watchers need
/// debouncing, behave differently on every OS, and fire during our own atomic writes. Invalidation
/// is explicit instead: the tree's "Reload Technology" command, a workspace rescan, and — in L0d —
/// the .ctech editor on save. This is a deliberate non-goal, not an oversight — do not add a
/// watcher later without discussing it first.
/// </summary>
public sealed class TechnologyCache
{
    private readonly Dictionary<string, Technology> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Live, unsaved overrides installed by an open <c>.ctech</c> editor (brief-L1-fix-path-seams-
    /// and-live-tech.md §2) — checked by <see cref="Get"/> before the file-backed cache, so an open
    /// layout sees an in-progress edit immediately, without a Save. Deliberately a SEPARATE
    /// dictionary from <see cref="_cache"/>, not a value stored inside it: <see cref="ClearLive"/>
    /// (discard-without-saving) must be able to drop the override and fall back to the last known
    /// on-disk value without also forcing a disk re-read of a file that was never touched.
    /// </summary>
    private readonly Dictionary<string, Technology> _live = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after <see cref="Invalidate"/> (or once per previously-cached path from
    /// <see cref="InvalidateAll"/>), or after <see cref="SetLive"/>/<see cref="ClearLive"/>, carrying
    /// the absolute path that changed. This is the live-refresh seam — subscribers re-resolve and
    /// update whatever depended on that path.</summary>
    public event Action<string>? TechnologyChanged;

    /// <summary>
    /// Returns the live override for <paramref name="absPath"/> if one is installed (an in-progress
    /// `.ctech` edit not yet saved), otherwise loads and caches the on-disk file on first request and
    /// returns the cached instance thereafter. Returns null when the file does not exist (a cache
    /// miss, not an error — the caller decides whether that's a diagnostic). Throws on corrupt JSON
    /// or an unreadable file / newer format version, exactly as <see cref="TechPersistence.LoadFromFile"/>
    /// does — the caller (<see cref="TechnologyResolver"/>) is responsible for catching that and
    /// turning it into a non-fatal diagnostic.
    /// </summary>
    public Technology? Get(string absPath)
    {
        absPath = Path.GetFullPath(absPath);

        if (_live.TryGetValue(absPath, out var live))
            return live;

        if (_held.TryGetValue(absPath, out var held))
            return held;

        if (_cache.TryGetValue(absPath, out var cached))
            return cached;

        if (!File.Exists(absPath))
            return null;

        var tech = TechPersistence.LoadFromFile(absPath, LoaderFor(absPath));
        _cache[absPath] = tech;
        return tech;
    }

    // ── Technologies held in memory (HZN-3) ────────────────────────────────────────────────────
    //
    // A technology that belongs to a document with no file of its own: a design held in a `.hzn` carries its project's
    // materials, and the 3D editor reaches them the way it reaches any technology, through a path. The path names no
    // file. It is kept apart from the live overrides because those are an editor's unsaved edits and are dropped by
    // Invalidate and InvalidateAll; a held technology has no file to fall back to, so dropping it would silently put
    // the design on the workspace's materials instead.

    private readonly Dictionary<string, Technology> _held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Installs <paramref name="tech"/> as what <see cref="Get"/> answers for <paramref name="absPath"/>, until
    /// <see cref="Release"/>. Raises <see cref="TechnologyChanged"/> when it replaces a different technology.</summary>
    public void Hold(string absPath, Technology tech)
    {
        absPath = Path.GetFullPath(absPath);
        bool replaced = _held.TryGetValue(absPath, out var was) && !ReferenceEquals(was, tech);
        _held[absPath] = tech;
        if (replaced) TechnologyChanged?.Invoke(absPath);
    }

    /// <summary>True when <paramref name="absPath"/> names a technology held in memory rather than a file.</summary>
    public bool IsHeld(string absPath) => _held.ContainsKey(Path.GetFullPath(absPath));

    /// <summary>Drops a held technology; no event, because nothing is left naming it.</summary>
    public void Release(string absPath) => _held.Remove(Path.GetFullPath(absPath));

    // ── Material libraries (brief-em3d-53 R-em3d53-1f) ────────────────────────────────────────
    //
    // A `.cmat` open in its own document with unsaved edits installs a live override here, and every
    // technology this cache holds that names that library is re-resolved against it — the same seam
    // a `.ctech` edit already uses, so a `.c3d` or a layout on the technology sees the edit without
    // a Save. A library is never cached on its own: it is read when a technology naming it loads.

    private readonly Dictionary<string, IReadOnlyList<TechMaterial>> _liveLibraries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many technologies a library change has re-resolved in this cache's lifetime — the
    /// counter brief-em3d-53's gate 9 reads ("exactly the cached technologies that name it").</summary>
    public int LibraryReresolutions { get; private set; }

    /// <summary>The loader this cache reads a technology's libraries through: the disk, with any
    /// library open in an editor answered from its live override.</summary>
    public MaterialLibraryLoader LoaderFor(string ctechPath)
        => MaterialLibraries.Disk(ctechPath, p => _liveLibraries.GetValueOrDefault(Path.GetFullPath(p)));

    /// <summary>The live override installed for a library, or null.</summary>
    public IReadOnlyList<TechMaterial>? LiveLibrary(string absLibraryPath)
        => _liveLibraries.GetValueOrDefault(Path.GetFullPath(absLibraryPath));

    /// <summary>Installs (or replaces) a live override for a library — a deep copy the caller no longer
    /// mutates — and re-resolves every technology that names it.</summary>
    public void SetLiveLibrary(string absLibraryPath, IReadOnlyList<TechMaterial> materials)
    {
        absLibraryPath = Path.GetFullPath(absLibraryPath);
        _liveLibraries[absLibraryPath] = materials;
        LibraryChanged(absLibraryPath);
    }

    /// <summary>Drops a library's live override (discard, or after its Save wrote the same content) and
    /// re-resolves every technology that names it from disk.</summary>
    public void ClearLiveLibrary(string absLibraryPath)
    {
        absLibraryPath = Path.GetFullPath(absLibraryPath);
        _liveLibraries.Remove(absLibraryPath);
        LibraryChanged(absLibraryPath);
    }

    /// <summary>
    /// A library's content changed (a live edit, a save, an external write): every technology naming it
    /// is re-resolved. A file-backed entry is dropped and re-read on the next <see cref="Get"/> — which is
    /// where a re-resolution that now REFUSES surfaces, as the resolver's diagnostic. A live technology
    /// override (an open technology editor) is re-resolved in place, keeping its unsaved own edits; one
    /// that now refuses keeps no library materials until fixed.
    /// </summary>
    public void LibraryChanged(string absLibraryPath)
    {
        absLibraryPath = Path.GetFullPath(absLibraryPath);
        var changed = new List<string>();
        foreach (var (path, tech) in _cache.ToList())
            if (Names(tech, absLibraryPath)) { _cache.Remove(path); changed.Add(path); }
        foreach (var (path, tech) in _live.ToList())
        {
            if (!Names(tech, absLibraryPath)) continue;
            // brief-em3d-108 — re-resolved as a NEW object, never in place: a 3D view decides whether a change was only a look by
            // comparing the technology it elaborated with against the one it is handed now, and an instance mutated under it would
            // be both. The editor installs deep copies for the same reason (SetLive).
            var fresh = TechPersistence.Clone(tech);
            try { TechPersistence.ResolveLibraries(fresh, LoaderFor(path), path); }
            catch (MaterialLibraryException) { /* reported when the technology is next resolved from disk */ }
            _live[path] = fresh;
            if (!changed.Contains(path, StringComparer.OrdinalIgnoreCase)) changed.Add(path);
        }
        LibraryReresolutions += changed.Count;
        foreach (string path in changed) TechnologyChanged?.Invoke(path);

        static bool Names(Technology t, string lib)
            => t.ResolvedLibraryPaths.Contains(lib, StringComparer.OrdinalIgnoreCase)
            || (t.MaterialLibraries?.Count > 0 && t.ResolvedLibraryPaths.Count == 0);
    }

    /// <summary>True when a live (unsaved) override is installed for <paramref name="absPath"/> —
    /// used to gate "Reload Technology" behind a discard confirmation rather than silently dropping
    /// unsaved editor changes.</summary>
    public bool HasLiveOverride(string absPath) => _live.ContainsKey(Path.GetFullPath(absPath));

    /// <summary>
    /// Installs (or replaces) a live override for <paramref name="absPath"/> and raises
    /// <see cref="TechnologyChanged"/>. <paramref name="tech"/> MUST be a value the caller does not
    /// keep mutating afterward — the `.ctech` editor always passes a deep clone of its working copy,
    /// never the working copy itself, for two reasons: the editor keeps mutating that object in
    /// place between commits (so handing it out directly would let a consumer observe half-applied
    /// edits), and undo/redo REPLACES the editor's working reference wholesale, so any consumer
    /// holding the old object would silently stop receiving updates after the first undo.
    /// </summary>
    public void SetLive(string absPath, Technology tech)
    {
        absPath = Path.GetFullPath(absPath);
        _live[absPath] = tech;
        TechnologyChanged?.Invoke(absPath);
    }

    /// <summary>Drops the live override for <paramref name="absPath"/> (if any) WITHOUT touching the
    /// file-backed cache, so <see cref="Get"/> falls back to the last known on-disk value (correct
    /// for "discard unsaved changes" — disk was never touched, so the old cached/lazily-reloaded
    /// value is still exactly right). No-op, no event, when no override was installed.</summary>
    public void ClearLive(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        if (_live.Remove(absPath))
            TechnologyChanged?.Invoke(absPath);
    }

    /// <summary>Drops both the live override AND the cached entry for <paramref name="absPath"/> (if
    /// either exists) and raises <see cref="TechnologyChanged"/> — used when the on-disk file itself
    /// changed (a save, an external edit, "Reload Technology") and any previously cached value —
    /// live or plain — is now stale.</summary>
    public void Invalidate(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        _live.Remove(absPath);
        _cache.Remove(absPath);
        TechnologyChanged?.Invoke(absPath);
    }

    /// <summary>Drops every cached and live entry, raising <see cref="TechnologyChanged"/> once per
    /// previously-known path.</summary>
    public void InvalidateAll()
    {
        var paths = _cache.Keys.Concat(_live.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _cache.Clear();
        _live.Clear();
        foreach (var path in paths)
            TechnologyChanged?.Invoke(path);
    }
}
