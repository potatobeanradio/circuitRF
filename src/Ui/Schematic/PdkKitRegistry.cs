using CircuitRF.Core.Devices.External;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// One part of an imported kit, held in memory. Exactly what installing it to disk used to write —
/// a symbol and a <c>.ccell</c> — with nothing in between.
/// </summary>
/// <param name="PartId">The kit's own id for this part. Opaque: rendered, never interpreted.</param>
/// <param name="Symbol">The translated symbol, as the primary symbol of a cell would have been.</param>
/// <param name="Ccell">The part's published interface, exactly as the written <c>.ccell</c> carried it.</param>
/// <param name="IconPath">Absolute path to the kit's own browser icon, when it has one.</param>
public sealed record PdkKitPart(
    string    PartId,
    Symbol    Symbol,
    CcellFile Ccell,
    string?   IconPath);

/// <summary>
/// Kit parts held in memory, keyed by the virtual cell reference a placed part carries.
///
/// <para><b>An import writes nothing into the workspace.</b> A kit's translated symbols and
/// parameter interfaces are the vendor's content; putting them in the workspace makes them travel
/// with a shared design, and the paths written alongside them are absolute and meaningless on
/// anyone else's machine. So they live here for the session and the workspace records only a
/// reference to the kit — see <c>docs/design/pdk-import.md</c>.</para>
///
/// <para><b>Why a registry keyed by reference, and not a cache in front of the disk.</b> There is no
/// disk copy to fall back to. This is the only place a kit part exists, which is what makes
/// "the kit is not mounted" a state the resolver can report rather than a file that silently is not
/// there. It mirrors the shape <c>TechnologyCache</c> already uses for its live overrides: a
/// separate dictionary consulted ahead of the file-backed path, never a value smuggled into the
/// file cache.</para>
///
/// <para>Nothing here knows anything about any particular kit — a kit name and a part id are
/// strings that arrived at run time.</para>
/// </summary>
public static class PdkKitRegistry
{
    /// <summary>
    /// Marks a cell reference as belonging to an imported kit rather than a folder on disk.
    ///
    /// <para><b>The scheme is what makes "the kit is missing" different from "the path is wrong".</b>
    /// A relative path that happens not to resolve is indistinguishable from a typo, so every
    /// reachability check would have to guess which it was looking at and no repair flow could say
    /// anything useful. A reference that states its own kind cannot be mistaken for either.</para>
    /// </summary>
    public const string Scheme = "pdk://";

    private static readonly Dictionary<string, PdkKitPart> _parts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kits currently loaded, in the order they were added. Rendered, never interpreted.</summary>
    private static readonly List<string> _kits = [];

    /// <summary>
    /// The compiled Verilog-A artefacts found for each kit, keyed by kit name.
    ///
    /// <para><b>A kit-level fact, so it is held once per kit rather than copied onto every part.</b>
    /// It is what turns a <c>.model</c> card's type into the file that implements it at extraction
    /// time, and every part of one kit resolves against the same set.</para>
    ///
    /// <para><b>Held for the session and never written down, deliberately.</b> These artefacts are
    /// the USER'S build output, not kit content: they can be rebuilt, moved or deleted without the
    /// kit changing at all, so a recorded index is the one thing here that could go stale in silence.
    /// Re-establishing it costs a directory walk and one worker <c>describe</c> per artefact —
    /// measured at 73 ms for the four a kit's owner had built, against a 405 ms import of the
    /// same kit — which is not a saving worth trading for an answer that can be wrong.</para>
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<OsdiModel>> _osdi =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lock _gate = new();

    // ── The reference form ────────────────────────────────────────────────────

    /// <summary>The virtual cell reference for one part of one kit.</summary>
    public static string RefFor(string kitName, string partId) => $"{Scheme}{kitName}/{partId}";

    /// <summary>True when this reference names a kit part rather than a cell folder.</summary>
    public static bool IsKitRef(string? cellRef) =>
        cellRef is not null && cellRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a kit reference into its kit and part. The kit name runs to the FIRST separator and
    /// the part id takes the rest, so a part id containing a separator survives the round trip —
    /// a kit names its own parts and circuitRF does not get to constrain them.
    /// </summary>
    public static bool TryParse(string? cellRef, out string kitName, out string partId)
    {
        kitName = partId = "";
        if (!IsKitRef(cellRef)) return false;

        string rest = cellRef![Scheme.Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;

        kitName = rest[..slash];
        partId  = rest[(slash + 1)..];
        return true;
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces everything held for one kit. Replacing rather than merging is what makes a re-import
    /// or a repaired reference produce the kit as it is NOW, instead of the union of every version
    /// of it seen this session.
    /// </summary>
    /// <param name="osdiModels">
    /// The compiled Verilog-A artefacts found for this kit, if any. Replaced with the parts, for the
    /// same reason: a re-import must produce the kit as it is NOW.
    /// </param>
    public static void SetKit(
        string kitName, IEnumerable<PdkKitPart> parts, IReadOnlyList<OsdiModel>? osdiModels = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kitName);
        ArgumentNullException.ThrowIfNull(parts);

        var fresh = parts.ToList();
        lock (_gate)
        {
            RemoveKitLocked(kitName);
            foreach (var p in fresh) _parts[RefFor(kitName, p.PartId)] = p;
            _kits.Add(kitName);
            if (osdiModels is { Count: > 0 }) _osdi[kitName] = osdiModels;
        }
    }

    /// <summary>
    /// The compiled Verilog-A artefacts this kit's devices resolve against. Empty for the ordinary
    /// kit, which has none — and also for a kit whose models the user has not compiled yet, which is
    /// why a device that finds no implementor must report rather than assume anything.
    /// </summary>
    public static IReadOnlyList<OsdiModel> OsdiModelsOf(string? kitName)
    {
        if (string.IsNullOrWhiteSpace(kitName)) return [];
        lock (_gate) return _osdi.TryGetValue(kitName, out var hit) ? hit : [];
    }

    /// <summary>
    /// Drops one kit. Parts placed in a design keep their references and become unresolvable — which
    /// is the reported, repairable state, not a loss: re-adding the kit resolves them again.
    /// </summary>
    public static void RemoveKit(string kitName)
    {
        lock (_gate) RemoveKitLocked(kitName);
    }

    /// <summary>
    /// Drops every kit. Called when a workspace is left, because kit references belong to the
    /// workspace that named them — carrying one into the next workspace would resolve a part that
    /// workspace never referenced.
    /// </summary>
    public static void Clear()
    {
        lock (_gate) { _parts.Clear(); _kits.Clear(); _osdi.Clear(); }
    }

    /// <summary>The part this reference names, or null when its kit is not loaded.</summary>
    public static PdkKitPart? Find(string? cellRef)
    {
        if (!IsKitRef(cellRef)) return null;
        lock (_gate) return _parts.GetValueOrDefault(cellRef!);
    }

    /// <summary>True when this kit is loaded — the distinction a broken reference rests on.</summary>
    public static bool HasKit(string kitName)
    {
        lock (_gate) return _kits.Contains(kitName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Kits currently loaded.</summary>
    public static IReadOnlyList<string> LoadedKits
    {
        get { lock (_gate) return [.. _kits]; }
    }

    /// <summary>Every part of one loaded kit, in registration order.</summary>
    public static IReadOnlyList<PdkKitPart> PartsOf(string kitName)
    {
        lock (_gate)
            return [.. _parts.Where(kv => TryParse(kv.Key, out string k, out _)
                                       && string.Equals(k, kitName, StringComparison.OrdinalIgnoreCase))
                             .Select(kv => kv.Value)];
    }

    /// <summary>
    /// The reference of the loaded part with this id, or null when no loaded kit has one.
    ///
    /// <para>Two kits offering a part of the same name is refused rather than resolved: the name does
    /// not identify one part, and guessing would swap in the wrong vendor's model. Same rule the
    /// workspace's own cell lookup follows, for the same reason.</para>
    /// </summary>
    public static string? FindRefByPartId(string partId)
    {
        if (string.IsNullOrWhiteSpace(partId)) return null;

        lock (_gate)
        {
            string? found = null;
            foreach (var key in _parts.Keys)
            {
                if (!TryParse(key, out _, out string id)) continue;
                if (!string.Equals(id, partId, StringComparison.OrdinalIgnoreCase)) continue;
                if (found is not null) return null;   // ambiguous — refuse rather than guess
                found = key;
            }
            return found;
        }
    }

    private static void RemoveKitLocked(string kitName)
    {
        var stale = _parts.Keys
            .Where(k => TryParse(k, out string kit, out _)
                     && string.Equals(kit, kitName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in stale) _parts.Remove(k);
        _kits.RemoveAll(k => string.Equals(k, kitName, StringComparison.OrdinalIgnoreCase));
        _osdi.Remove(kitName);
    }
}
