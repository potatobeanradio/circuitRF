using System;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Which layout generator a kit part places, for the code that is not the palette.
///
/// <para><b>Why this exists rather than a second lookup.</b> The palette already works out, per part,
/// which of a kit's parametric cells is that part's layout view — see <see cref="KitPaletteMerge"/>,
/// where the rules and the reasons live. Update-Layout-from-Schematic has to reach the same answer for
/// a PLACED part, and deriving it a second time is how the tile and the design come to disagree about
/// what a part's artwork is. So the palette publishes what it settled and everything else reads it.</para>
///
/// <para><b>Held per WORKSPACE, not per process</b> (MW1 R-mw1-4). A second workspace window publishes
/// its own kits' generators; before this was scoped, the second publish replaced the first workspace's
/// map wholesale and every kit part already placed in it silently stopped finding its artwork. Each
/// workspace's entries are replaced on its own publish and dropped when its window closes — never by
/// another workspace opening.</para>
/// </summary>
public static class KitLayoutGenerators
{
    /// <summary>What one workspace's palette settled: the mapping read two ways, and its refresher.</summary>
    private sealed class GeneratorScope
    {
        public readonly Dictionary<string, string> ByRef       = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ByGenerator = new(StringComparer.OrdinalIgnoreCase);
        public Func<bool>? Refresh;
    }

    private static readonly Dictionary<string, GeneratorScope> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _scopeOrder = [];
    private static readonly Lock _gate = new();

    /// <summary>Replaces the mapping <paramref name="workspaceRoot"/> holds with what
    /// <paramref name="composed"/> settled.</summary>
    public static void Publish(string? workspaceRoot, IEnumerable<PaletteItem> composed)
    {
        string key = WorkspaceRootFinder.Normalize(workspaceRoot);
        lock (_gate)
        {
            var scope = ScopeLocked(key, create: true)!;
            scope.ByRef.Clear();
            scope.ByGenerator.Clear();
            foreach (var item in composed)
                if (item.Pdk is { } pdk && item.PCellGeneratorId is { Length: > 0 } gen)
                {
                    string reference = PdkKitRegistry.RefFor(pdk.KitName, pdk.PartId);
                    scope.ByRef[reference]  = gen;
                    // One-to-one by construction: KitPaletteMerge attaches a generator to at most one
                    // part, and never the same generator twice. Recorded rather than searched for, so
                    // the two directions are one answer read two ways.
                    scope.ByGenerator[gen] = reference;
                }
        }
    }

    /// <summary>
    /// Forgets what ONE workspace published. Called where that workspace's kit references are
    /// cleared — its window closing, or it reloading its own kits.
    ///
    /// <para><b>The refresher is deliberately left in place.</b> It is how a lookup against a map
    /// nothing has filled yet still gets the answer, and clearing the map is precisely the moment
    /// that matters. It is withdrawn separately, by <see cref="SetRefresher"/> with a null hook, when
    /// the workspace it belongs to is actually being left.</para>
    /// </summary>
    public static void ClearWorkspace(string? workspaceRoot)
    {
        string key = WorkspaceRootFinder.Normalize(workspaceRoot);
        lock (_gate)
        {
            if (ScopeLocked(key, create: false) is not { } scope) return;
            scope.ByRef.Clear();
            scope.ByGenerator.Clear();
        }
    }

    /// <summary>Test-only reset. Not reachable from production code, by design (MW1 §9.6).</summary>
    internal static void ResetAllForTests()
    {
        lock (_gate) { _scopes.Clear(); _scopeOrder.Clear(); }
    }

    /// <summary>
    /// How to take the reading again, for the one caller that cannot wait for it.
    ///
    /// <para><b>Why a lookup is allowed to trigger work at all.</b> The map is filled from a reading
    /// that has to START a kit's interpreter, so it is taken off the UI thread and lands whenever it
    /// lands. Every lookup before then would otherwise answer "this kit names no layout cell for that
    /// part" — which is indistinguishable from the kit genuinely having none, and is what a user sees
    /// as their artwork silently not appearing. Asking once, here, turns a timing question into an
    /// answer.</para>
    ///
    /// <para>The hook is expected to be cheap when the map is already populated, and to return false
    /// when it published nothing — <see cref="For"/> asks at most once per lookup either way.</para>
    ///
    /// <para>Per workspace, because the reading it triggers starts THAT workspace's interpreters.</para>
    /// </summary>
    public static void SetRefresher(string? workspaceRoot, Func<bool>? refresh)
    {
        string key = WorkspaceRootFinder.Normalize(workspaceRoot);
        lock (_gate)
        {
            if (refresh is null)
            {
                if (ScopeLocked(key, create: false) is { } existing) existing.Refresh = null;
                return;
            }
            ScopeLocked(key, create: true)!.Refresh = refresh;
        }
    }

    /// <summary>Guards the hook against re-entering itself: it publishes, and publishing must not
    /// be able to ask for a refresh in the middle of one.</summary>
    [ThreadStatic] private static bool _refreshing;

    /// <summary>
    /// The generator this part places, or null when the kit supplies no layout cell for it — which is
    /// an ordinary state, not a fault, and is what the caller reports.
    /// </summary>
    public static string? For(string? workspaceRoot, string kitName, string partId)
    {
        string reference = PdkKitRegistry.RefFor(kitName, partId);

        Func<bool>? refresh = null;
        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
            {
                if (scope.ByRef.TryGetValue(reference, out string? known)) return known;
                refresh ??= _refreshing ? null : scope.Refresh;
            }
        }

        // A miss may mean the kit has no layout cell for this part — an ordinary state — or that
        // nothing has been read yet. Only the second is worth doing anything about, and the hook
        // itself is what tells the two apart.
        if (refresh is null) return null;

        _refreshing = true;
        try { if (!refresh()) return null; }
        catch { return null; }   // a reading that fails is a miss, never the caller's problem
        finally { _refreshing = false; }

        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
                if (scope.ByRef.TryGetValue(reference, out string? known)) return known;
            return null;
        }
    }

    /// <summary>
    /// The kit part <paramref name="generatorId"/> draws, as the reference a placed component
    /// carries (<c>pdk://kit/part</c>), or null when no kit part claims it — a built-in generator, or
    /// one of a kit's cells that no schematic part was matched to.
    ///
    /// <para>Read by "Update Schematic from Layout", which starts from a layout instance and has only
    /// the generator id: without this it can name no part, and every PDK component in a layout is
    /// silently passed over.</para>
    /// </summary>
    public static string? PartRefFor(string? workspaceRoot, string generatorId)
    {
        if (string.IsNullOrEmpty(generatorId)) return null;
        lock (_gate)
        {
            foreach (var scope in ScopesToSearchLocked(workspaceRoot))
                if (scope.ByGenerator.TryGetValue(generatorId, out string? hit)) return hit;
            return null;
        }
    }

    // ── Scope plumbing ────────────────────────────────────────────────────────

    private static GeneratorScope? ScopeLocked(string key, bool create)
    {
        if (_scopes.TryGetValue(key, out var existing)) return existing;
        if (!create) return null;

        var fresh = new GeneratorScope();
        _scopes[key] = fresh;
        _scopeOrder.Add(key);
        return fresh;
    }

    /// <summary>
    /// The scopes a lookup may consult: exactly one when the caller named a workspace, and every one
    /// in publish order when it could not. The unscoped form exists for the layout↔schematic
    /// generators, which are handed a document whose own workspace is found by walk-up — a document
    /// outside every workspace has none, and answering from any of them is better than answering
    /// nothing, because the map is keyed by a reference no other kit can produce.
    /// </summary>
    private static List<GeneratorScope> ScopesToSearchLocked(string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            string key = WorkspaceRootFinder.Normalize(workspaceRoot);
            return ScopeLocked(key, create: false) is { } one ? [one] : [];
        }

        var all = new List<GeneratorScope>(_scopeOrder.Count);
        foreach (string key in _scopeOrder)
            if (_scopes.TryGetValue(key, out var scope)) all.Add(scope);
        return all;
    }
}
