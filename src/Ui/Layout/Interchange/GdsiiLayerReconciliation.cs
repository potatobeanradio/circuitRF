// R-L4a-2: layer reconciliation reuses L1g's LayoutLayerMapping verbatim — GDSII import is exactly
// the problem L1g solved (geometry authored against one layer vocabulary arriving in another), and its
// name-before-number matching is precisely what an import needs. This file does NOT write a second
// reconciliation algorithm; it only adapts GDSII's "no layer names" reality (§8: "no layer names" is
// listed among GDSII's own risks) onto LayoutLayerMapping.Propose's existing (shapes, sourceLayers,
// destTech) signature.

namespace CircuitRF.Ui.Layout.Interchange;

public static class GdsiiLayerReconciliation
{
    /// <summary>
    /// Builds the synthetic "source layers" <see cref="LayoutLayerMapping.Propose"/> expects, for a
    /// format (GDSII) whose files carry only numeric <see cref="LayerKey"/>s and no names. For each
    /// distinct incoming key, the synthetic <see cref="LayerDef.Name"/> is whichever layer in
    /// <paramref name="destTech"/> declares that GDSII identity (<see
    /// cref="InterchangeMapping.GdsiiLayer"/>/<see cref="InterchangeMapping.GdsiiDatatype"/>, falling
    /// back to the destination layer's own <see cref="LayerDef.Key"/> when it declares no alias) — or
    /// empty when no destination layer claims it. Feeding these into the UNMODIFIED
    /// <see cref="LayoutLayerMapping.Propose"/> reproduces exactly the right <see
    /// cref="LayerMatchKind"/> (<c>SameKeySameName</c> when the destination technology already owns
    /// that GDSII identity, <c>NoMatch</c> otherwise) with zero changes to L1g's own code.
    /// </summary>
    public static IReadOnlyList<LayerDef> BuildSourceLayers(
        IReadOnlyList<LayoutShape> shapes, Technology? destTech)
    {
        var order = new List<LayerKey>();
        var seen = new HashSet<LayerKey>();
        foreach (var shape in shapes)
            if (seen.Add(shape.Layer))
                order.Add(shape.Layer);

        var result = new List<LayerDef>(order.Count);
        foreach (var key in order)
        {
            string name = "";
            if (destTech is not null)
            {
                foreach (var l in destTech.Layers)
                {
                    if (GdsiiIdentityOf(l) != key) continue;
                    name = l.Name;
                    break;
                }
            }
            result.Add(new LayerDef { Key = key, Name = name });
        }
        return result;
    }

    /// <summary>A technology layer's effective GDSII identity — the declared alias when both
    /// GdsiiLayer and GdsiiDatatype are set, otherwise the layer's own native <see cref="LayerKey"/>
    /// (§2.1 R7: a layer's (Layer,Datatype) pair already IS the GDSII model by construction).</summary>
    private static LayerKey GdsiiIdentityOf(LayerDef l) =>
        l.Interchange is { GdsiiLayer: { } gl, GdsiiDatatype: { } gd }
            ? new LayerKey(gl, gd)
            : l.Key;
}
