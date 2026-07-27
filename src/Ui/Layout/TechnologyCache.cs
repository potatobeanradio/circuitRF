namespace CircuitRF.Ui.Layout;

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

        if (_cache.TryGetValue(absPath, out var cached))
            return cached;

        if (!File.Exists(absPath))
            return null;

        var tech = TechPersistence.LoadFromFile(absPath);
        _cache[absPath] = tech;
        return tech;
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
