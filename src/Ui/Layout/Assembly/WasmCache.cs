namespace CircuitRF.Ui.Layout.Assembly;

/// <summary>
/// One-load-per-file cache for `.wasm` assembly rule files, keyed by absolute path — a direct mirror
/// of <see cref="TechnologyCache"/>, including its deliberate lack of a
/// <see cref="System.IO.FileSystemWatcher"/> (cross-platform watchers need debouncing, behave
/// differently on every OS, and fire during our own atomic writes). Invalidation is explicit.
///
/// <para>The live-override half of <see cref="TechnologyCache"/> is deliberately NOT mirrored: WB-D
/// ships no `.wasm` EDITOR (§5 open question 4), so there is nothing that can hold an unsaved
/// in-progress edit for a consumer to see. When an editor does land it adds
/// <c>SetLive</c>/<c>ClearLive</c> here in exactly the shape the technology cache already proves.</para>
/// </summary>
public sealed class WasmCache
{
    private readonly Dictionary<string, WasmFile> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after <see cref="Invalidate"/> (or once per previously-cached path from
    /// <see cref="InvalidateAll"/>) with the absolute path that changed — the live-refresh seam.</summary>
    public event Action<string>? AssemblyRulesChanged;

    /// <summary>
    /// Loads and caches on first request, returns the cached instance thereafter. Returns null when
    /// the file does not exist — a MISS, not an error; the caller decides whether that is a
    /// diagnostic (for `.wasm` it usually is not; see <see cref="WasmResolver"/>). Throws on corrupt
    /// JSON or a newer format version, exactly as <see cref="WasmPersistence.LoadFromFile"/> does.
    /// </summary>
    public WasmFile? Get(string absPath)
    {
        absPath = Path.GetFullPath(absPath);

        if (_cache.TryGetValue(absPath, out var cached)) return cached;
        if (!File.Exists(absPath)) return null;

        var wasm = WasmPersistence.LoadFromFile(absPath);
        _cache[absPath] = wasm;
        return wasm;
    }

    public void Invalidate(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        _cache.Remove(absPath);
        AssemblyRulesChanged?.Invoke(absPath);
    }

    public void InvalidateAll()
    {
        var paths = _cache.Keys.ToList();
        _cache.Clear();
        foreach (var p in paths) AssemblyRulesChanged?.Invoke(p);
    }
}
