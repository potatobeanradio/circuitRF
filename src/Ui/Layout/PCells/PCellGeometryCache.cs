using System.Globalization;
using System.Text;

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// R-pc-4/5: a PCell is evaluated once per unique (generator, parameter values, technology) —
/// never once per placement (the 50×50-array gate: 2,500 placements, one call). Built-in PCells in
/// this phase have no file-backed <see cref="LayoutView"/> (they are SymbolKind-registered, not
/// disk cell folders — see PCellOrigin's own doc comment), so this cache is independent of
/// <c>CellLayoutResolver</c>'s file-mtime cache; a future file-backed PCell would key its own
/// geometry cache the same way, on the same three inputs.
///
/// <see cref="Technology"/> has no value equality of its own, so the technology axis of the key is
/// REFERENCE identity, exactly like L3a's compiled-cell cache already keys on
/// <c>LayoutView</c> reference — the app-wide discipline (never mutate a <c>Technology</c> in
/// place; go through <c>TechnologyCache.SetLive</c>/<c>Invalidate</c>, which install/replace the
/// reference) is what keeps that a correct proxy for "did the technology change."
/// </summary>
public sealed class PCellGeometryCache
{
    private readonly Dictionary<CacheKey, PCellResult> _cache = new();

    /// <summary>Number of times a generator function was actually invoked (a cache miss) — the
    /// direct, timing-independent proof R-pc-5's gate asks for ("assert the call count, not the
    /// timing").</summary>
    public int GeneratorCallCount { get; private set; }

    public PCellResult GetOrGenerate(
        string generatorId,
        PCellGenerator generator,
        IReadOnlyDictionary<string, double> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        var key = new CacheKey(generatorId, technology, FingerprintParameters(parameters), FingerprintLayers(layerSelection));
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        GeneratorCallCount++;
        var result = generator(parameters, technology, layerSelection);
        _cache[key] = result;
        return result;
    }

    public void Clear() => _cache.Clear();

    private static string FingerprintParameters(IReadOnlyDictionary<string, double> parameters)
    {
        var sb = new StringBuilder();
        foreach (var kv in parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }
        return sb.ToString();
    }

    private static string FingerprintLayers(PCellLayerSelection sel)
        => $"{sel.SignalLayerNameOverride ?? ""}|{sel.GroundLayerNameOverride ?? ""}";

    private readonly record struct CacheKey(string GeneratorId, Technology? Technology, string ParamsFingerprint, string LayersFingerprint);
}
