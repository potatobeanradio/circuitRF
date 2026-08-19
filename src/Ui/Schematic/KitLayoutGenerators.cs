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
/// <para>Process-wide, like every other kit-scoped registry here, and replaced wholesale on every
/// publish — a kit that is no longer loaded must not keep answering.</para>
/// </summary>
public static class KitLayoutGenerators
{
    private static readonly Dictionary<string, string> _byRef = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _byGenerator = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _gate = new();

    /// <summary>Replaces the mapping with what <paramref name="composed"/> settled.</summary>
    public static void Publish(IEnumerable<PaletteItem> composed)
    {
        lock (_gate)
        {
            _byRef.Clear();
            _byGenerator.Clear();
            foreach (var item in composed)
                if (item.Pdk is { } pdk && item.PCellGeneratorId is { Length: > 0 } gen)
                {
                    string reference = PdkKitRegistry.RefFor(pdk.KitName, pdk.PartId);
                    _byRef[reference]  = gen;
                    // One-to-one by construction: KitPaletteMerge attaches a generator to at most one
                    // part, and never the same generator twice. Recorded rather than searched for, so
                    // the two directions are one answer read two ways.
                    _byGenerator[gen] = reference;
                }
        }
    }

    /// <summary>Forgets everything. Called where kit references are cleared.</summary>
    public static void Clear()
    {
        lock (_gate) { _byRef.Clear(); _byGenerator.Clear(); }
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
    /// </summary>
    public static void SetRefresher(Func<bool>? refresh)
    {
        lock (_gate) _refresh = refresh;
    }

    private static Func<bool>? _refresh;

    /// <summary>Guards the hook against re-entering itself: it publishes, and publishing must not
    /// be able to ask for a refresh in the middle of one.</summary>
    [ThreadStatic] private static bool _refreshing;

    /// <summary>
    /// The generator this part places, or null when the kit supplies no layout cell for it — which is
    /// an ordinary state, not a fault, and is what the caller reports.
    /// </summary>
    public static string? For(string kitName, string partId)
    {
        string reference = PdkKitRegistry.RefFor(kitName, partId);

        Func<bool>? refresh;
        lock (_gate)
        {
            if (_byRef.TryGetValue(reference, out string? known)) return known;
            refresh = _refreshing ? null : _refresh;
        }

        // A miss may mean the kit has no layout cell for this part — an ordinary state — or that
        // nothing has been read yet. Only the second is worth doing anything about, and the hook
        // itself is what tells the two apart.
        if (refresh is null) return null;

        _refreshing = true;
        try { if (!refresh()) return null; }
        catch { return null; }   // a reading that fails is a miss, never the caller's problem
        finally { _refreshing = false; }

        lock (_gate) return _byRef.GetValueOrDefault(reference);
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
    public static string? PartRefFor(string generatorId)
    {
        if (string.IsNullOrEmpty(generatorId)) return null;
        lock (_gate) return _byGenerator.GetValueOrDefault(generatorId);
    }
}
