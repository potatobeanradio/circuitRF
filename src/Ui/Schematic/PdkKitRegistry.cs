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
    ///
    /// <para><b>The reference carries no workspace, and that is deliberate</b> — it is written into
    /// user files (docs/design/pdk-import.md), so it cannot name a machine-specific path. The
    /// workspace comes from the ASKER instead: MW1 R-mw1-5, the same walk-up technology already
    /// uses.</para>
    /// </summary>
    public const string Scheme = "pdk://";

    /// <summary>
    /// Everything one workspace has mounted. Held per workspace rather than per process because a
    /// second workspace window must not be able to unmount the first one's kits — which is exactly
    /// what the single global dictionary this replaces did, silently, on every workspace open
    /// (MW1 §1.2 / R-mw1-4).
    /// </summary>
    private sealed class KitScope
    {
        public readonly Dictionary<string, PdkKitPart> Parts = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Kits = [];
        public readonly Dictionary<string, IReadOnlyList<OsdiModel>> Osdi = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, KitScope> _scopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scope keys in mount order, so an unscoped lookup answers deterministically.</summary>
    private static readonly List<string> _scopeOrder = [];

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
    /// Replaces everything held for one kit OF ONE WORKSPACE. Replacing rather than merging is what
    /// makes a re-import or a repaired reference produce the kit as it is NOW, instead of the union
    /// of every version of it seen this session.
    /// </summary>
    /// <param name="workspaceRoot">
    /// The workspace that mounted this kit — the directory holding its <c>.cws</c>. Two workspaces
    /// may mount kits of the same name and they do not collide.
    /// </param>
    /// <param name="osdiModels">
    /// The compiled Verilog-A artefacts found for this kit, if any. Replaced with the parts, for the
    /// same reason: a re-import must produce the kit as it is NOW.
    /// </param>
    public static void SetKit(
        string? workspaceRoot, string kitName, IEnumerable<PdkKitPart> parts,
        IReadOnlyList<OsdiModel>? osdiModels = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kitName);
        ArgumentNullException.ThrowIfNull(parts);

        string scopeKey = WorkspaceRootFinder.Normalize(workspaceRoot);
        var fresh = parts.ToList();

        lock (_gate)
        {
            var scope = ScopeLocked(scopeKey, create: true)!;
            RemoveKitLocked(scope, kitName);
            foreach (var p in fresh) scope.Parts[RefFor(kitName, p.PartId)] = p;
            scope.Kits.Add(kitName);
            if (osdiModels is { Count: > 0 }) scope.Osdi[kitName] = osdiModels;
        }
    }

    /// <summary>
    /// The compiled Verilog-A artefacts this kit's devices resolve against. Empty for the ordinary
    /// kit, which has none — and also for a kit whose models the user has not compiled yet, which is
    /// why a device that finds no implementor must report rather than assume anything.
    /// </summary>
    public static IReadOnlyList<OsdiModel> OsdiModelsOf(string? workspaceRoot, string? kitName)
    {
        if (string.IsNullOrWhiteSpace(kitName)) return [];
        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
                if (scope.Osdi.TryGetValue(kitName, out var hit)) return hit;
            return [];
        }
    }

    /// <summary>
    /// Drops one kit from one workspace. Parts placed in a design keep their references and become
    /// unresolvable — which is the reported, repairable state, not a loss: re-adding the kit resolves
    /// them again.
    /// </summary>
    public static void RemoveKit(string? workspaceRoot, string kitName)
    {
        string scopeKey = WorkspaceRootFinder.Normalize(workspaceRoot);
        lock (_gate)
        {
            if (ScopeLocked(scopeKey, create: false) is { } scope) RemoveKitLocked(scope, kitName);
        }
    }

    /// <summary>
    /// Drops everything ONE workspace mounted — called when that workspace's window closes, or when
    /// it is about to reload its own kits.
    ///
    /// <para><b>This replaces the old process-wide <c>Clear()</c>, and the difference is the whole
    /// point of MW1 §3.</b> Clearing everything on a workspace open silently unmounted every OTHER
    /// open workspace's kits, and the first symptom was their parts drawing as pin-less placeholders
    /// with nothing reported. There is deliberately no way to clear every scope at once from
    /// production code.</para>
    /// </summary>
    public static void ClearWorkspace(string? workspaceRoot)
    {
        string scopeKey = WorkspaceRootFinder.Normalize(workspaceRoot);
        lock (_gate)
        {
            _scopes.Remove(scopeKey);
            _scopeOrder.RemoveAll(k => string.Equals(k, scopeKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Test-only reset. Not reachable from production code, by design (MW1 §9.6).</summary>
    internal static void ResetAllForTests()
    {
        lock (_gate) { _scopes.Clear(); _scopeOrder.Clear(); }
    }

    /// <summary>
    /// The part this reference names within <paramref name="workspaceRoot"/>, or null when that
    /// workspace has not mounted its kit.
    /// </summary>
    public static PdkKitPart? Find(string? cellRef, string? workspaceRoot)
    {
        if (!IsKitRef(cellRef)) return null;
        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
                if (scope.Parts.TryGetValue(cellRef!, out var hit)) return hit;
            return null;
        }
    }

    /// <summary>
    /// The part this reference names in ANY open workspace — for the handful of callers that have no
    /// document and no window to attribute the question to.
    ///
    /// <para><b>Named rather than reached by passing null</b>, so it reads as the compromise it is at
    /// the call site. It is used for PREVIEW artwork only (a palette tile's glyph, a drag ghost);
    /// everything that resolves a reference a design actually carries goes through
    /// <see cref="Find(string?, string?)"/> with the referencing document's own workspace.</para>
    /// </summary>
    public static PdkKitPart? FindInAnyWorkspace(string? cellRef)
        => Find(cellRef, workspaceRoot: null);

    /// <summary>True when this workspace has mounted this kit — the distinction a broken reference
    /// rests on.</summary>
    public static bool HasKit(string? workspaceRoot, string kitName)
    {
        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
                if (scope.Kits.Contains(kitName, StringComparer.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Kits this workspace has mounted, in mount order.</summary>
    public static IReadOnlyList<string> LoadedKits(string? workspaceRoot)
    {
        lock (_gate)
        {
            var all = new List<string>();
            foreach (var scope in ScopesToSearchLocked(workspaceRoot)) all.AddRange(scope.Kits);
            return all;
        }
    }

    /// <summary>Every part of one kit this workspace has mounted, in registration order.</summary>
    public static IReadOnlyList<PdkKitPart> PartsOf(string? workspaceRoot, string kitName)
    {
        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
            {
                var hits = scope.Parts
                    .Where(kv => TryParse(kv.Key, out string k, out _)
                              && string.Equals(k, kitName, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Value)
                    .ToList();
                if (hits.Count > 0) return hits;
            }
            return [];
        }
    }

    /// <summary>
    /// The reference of the mounted part with this id, or null when no kit this workspace mounted
    /// has one.
    ///
    /// <para>Two kits offering a part of the same name is refused rather than resolved: the name does
    /// not identify one part, and guessing would swap in the wrong vendor's model. Same rule the
    /// workspace's own cell lookup follows, for the same reason. The refusal is judged WITHIN one
    /// workspace — another workspace mounting a part of the same name is not an ambiguity, because
    /// nothing here would ever have reached for it.</para>
    /// </summary>
    public static string? FindRefByPartId(string? workspaceRoot, string partId)
    {
        if (string.IsNullOrWhiteSpace(partId)) return null;

        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
            {
                string? found = null;
                foreach (var key in scope.Parts.Keys)
                {
                    if (!TryParse(key, out _, out string id)) continue;
                    if (!string.Equals(id, partId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found is not null) { found = null; break; }   // ambiguous — refuse rather than guess
                    found = key;
                }
                if (found is not null) return found;
            }
            return null;
        }
    }

    /// <summary>Workspaces with at least one kit mounted, in mount order. For diagnostics.</summary>
    public static IReadOnlyList<string> MountedWorkspaces
    {
        get { lock (_gate) return [.. _scopeOrder]; }
    }

    // ── Scope plumbing ────────────────────────────────────────────────────────

    private static KitScope? ScopeLocked(string scopeKey, bool create)
    {
        if (_scopes.TryGetValue(scopeKey, out var existing)) return existing;
        if (!create) return null;

        var fresh = new KitScope();
        _scopes[scopeKey] = fresh;
        _scopeOrder.Add(scopeKey);
        return fresh;
    }

    /// <summary>
    /// The scopes a lookup may consult: exactly one when the caller named a workspace, and every one
    /// in mount order when it could not (see <see cref="FindInAnyWorkspace"/>).
    /// </summary>
    private static List<KitScope> ScopesToSearchLocked(string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            string key = WorkspaceRootFinder.Normalize(workspaceRoot);
            return ScopeLocked(key, create: false) is { } one ? [one] : [];
        }

        var all = new List<KitScope>(_scopeOrder.Count);
        foreach (string key in _scopeOrder)
            if (_scopes.TryGetValue(key, out var scope)) all.Add(scope);
        return all;
    }

    private static void RemoveKitLocked(KitScope scope, string kitName)
    {
        var stale = scope.Parts.Keys
            .Where(k => TryParse(k, out string kit, out _)
                     && string.Equals(kit, kitName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in stale) scope.Parts.Remove(k);
        scope.Kits.RemoveAll(k => string.Equals(k, kitName, StringComparison.OrdinalIgnoreCase));
        scope.Osdi.Remove(kitName);
    }
}
