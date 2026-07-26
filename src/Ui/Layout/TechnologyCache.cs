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

    /// <summary>Raised after <see cref="Invalidate"/> (or once per previously-cached path from
    /// <see cref="InvalidateAll"/>), carrying the absolute path that was invalidated. This is the
    /// live-refresh seam — subscribers re-resolve and update whatever depended on that path.</summary>
    public event Action<string>? TechnologyChanged;

    /// <summary>
    /// Loads and caches the .ctech at <paramref name="absPath"/> on first request; returns the
    /// cached instance thereafter. Returns null when the file does not exist (a cache miss, not an
    /// error — the caller decides whether that's a diagnostic). Throws on corrupt JSON or an
    /// unreadable file / newer format version, exactly as <see cref="TechPersistence.LoadFromFile"/>
    /// does — the caller (<see cref="TechnologyResolver"/>) is responsible for catching that and
    /// turning it into a non-fatal diagnostic.
    /// </summary>
    public Technology? Get(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        if (_cache.TryGetValue(absPath, out var cached))
            return cached;

        if (!File.Exists(absPath))
            return null;

        var tech = TechPersistence.LoadFromFile(absPath);
        _cache[absPath] = tech;
        return tech;
    }

    /// <summary>Drops the cached entry for <paramref name="absPath"/> (if any) and raises
    /// <see cref="TechnologyChanged"/> so live documents can re-resolve.</summary>
    public void Invalidate(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        _cache.Remove(absPath);
        TechnologyChanged?.Invoke(absPath);
    }

    /// <summary>Drops every cached entry, raising <see cref="TechnologyChanged"/> once per
    /// previously-cached path.</summary>
    public void InvalidateAll()
    {
        var paths = _cache.Keys.ToList();
        _cache.Clear();
        foreach (var path in paths)
            TechnologyChanged?.Invoke(path);
    }
}
