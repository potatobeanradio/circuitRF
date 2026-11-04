// The stackup's conductors, as anything reading copper needs them — R-lvs2-1.
//
// ── WHAT IS HERE AND WHAT DELIBERATELY IS NOT ──────────────────────────────────────────────────
//
// A reader of copper needs to know WHICH DRAWING LAYERS ARE CONDUCTORS AND IN WHAT ORDER: the
// connectivity walk needs the order to work out which conductors a via barrel passes through, and
// brief 3's device reading needs the set to know what counts as metal at all.
//
// It does NOT need a sheet resistance, and this type does not carry one. SHEET RESISTANCE STAYS
// BEHIND, on PdnConductor, with the thickness and the conductivity it is computed from — railRF
// PRICES copper and LVS never prices anything. PdnConductor projects onto this type
// (PdnConductor.AsConductor) rather than this type growing railRF's half back.

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>
/// One conductor of the stackup: what it is called, and which drawing layers its metal is on.
/// </summary>
/// <param name="StackupName">The <c>StackupLayer.Name</c> it came from.</param>
/// <param name="DrawingLayers">The layers that conductor's copper is drawn on, in the technology's
/// own order. Empty is ordinary and is the case LVS overview §1g is about — a backside metal
/// declared in the stackup and drawn nowhere.</param>
public readonly record struct Conductor(string StackupName, IReadOnlyList<LayerKey> DrawingLayers);

/// <summary>The conductor enumeration, written once.</summary>
public static class Conductors
{
    /// <summary>
    /// Every conductor of <paramref name="tech"/>'s stackup, <b>top to bottom</b> — which is what
    /// <c>Stackup.Layers</c> is (R-em-3), and is what makes "the conductors a via passes THROUGH
    /// are the ones between its two span ends" a list-range operation.
    ///
    /// <para><b>One entry per stackup layer, duplicates included.</b> Two conductor entries sharing
    /// a name stay two entries, because the order is read by index and collapsing them would move
    /// every index after the collapse.</para>
    /// </summary>
    public static IReadOnlyList<Conductor> Of(Technology tech)
    {
        ArgumentNullException.ThrowIfNull(tech);

        var found = new List<Conductor>();
        foreach (var sl in tech.Stackup.Layers)
            if (sl.Kind == StackupKind.Conductor && sl.Name.Length > 0)
                found.Add(new Conductor(sl.Name, sl.DrawingLayers));
        return found;
    }
}
