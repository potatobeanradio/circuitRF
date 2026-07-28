// DXF's named layers are the ideal input for L1g's LayoutLayerMapping (§2.4 note in
// src/Ui/CLAUDE.md: "its name-first matching was written for precisely this") — adapted here exactly
// the way GdsiiLayerReconciliation adapts GDSII's numeric-only (layer, datatype) identity onto the
// same unmodified LayoutLayerMapping.Propose. DXF is the opposite case: it has NAMES but no numeric
// key, so a synthetic LayerKey must be invented per distinct incoming name.

namespace CircuitRF.Ui.Layout.Interchange;

public static class DxfLayerReconciliation
{
    /// <summary>
    /// Builds the synthetic "source layers" <see cref="LayoutLayerMapping.Propose"/> expects, one per
    /// distinct DXF layer name actually used by <paramref name="dxfLayerNames"/>. For each name: if a
    /// destination layer declares that name as its <see cref="InterchangeMapping.DxfLayerName"/> alias,
    /// the synthetic key/name become THAT destination layer's own key/name (mirrors
    /// GdsiiLayerReconciliation exactly — this is what makes <c>Propose</c> see
    /// <see cref="LayerMatchKind.SameKeySameName"/> for an aliased layer, not merely
    /// <see cref="LayerMatchKind.ExactName"/>). Otherwise a fresh synthetic key (never colliding with a
    /// real destination key) is minted and the synthetic name is the raw DXF name itself, so a
    /// destination layer whose own <c>Name</c> literally equals the DXF name still matches by
    /// <see cref="LayerMatchKind.ExactName"/> with zero technology authoring required.
    /// </summary>
    public static (IReadOnlyList<LayerDef> SourceLayers, IReadOnlyDictionary<string, LayerKey> KeyByDxfName) BuildSourceLayers(
        IReadOnlyList<string> dxfLayerNames, Technology? destTech)
    {
        var distinct = dxfLayerNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sourceLayers = new List<LayerDef>(distinct.Count);
        var keyByName = new Dictionary<string, LayerKey>(StringComparer.OrdinalIgnoreCase);

        var usedKeys = new HashSet<LayerKey>(destTech?.Layers.Select(l => l.Key) ?? []);
        int nextSyntheticLayer = -1;

        foreach (var dxfName in distinct)
        {
            var aliasOwner = destTech?.Layers.FirstOrDefault(l =>
                string.Equals(l.Interchange?.DxfLayerName, dxfName, StringComparison.OrdinalIgnoreCase));

            LayerKey key;
            string name;
            if (aliasOwner is not null)
            {
                key = aliasOwner.Key;
                name = aliasOwner.Name; // the destination's own name — trivially matches itself
            }
            else
            {
                key = new LayerKey(nextSyntheticLayer, 0);
                while (usedKeys.Contains(key)) key = new LayerKey(--nextSyntheticLayer, 0);
                nextSyntheticLayer--;
                usedKeys.Add(key);
                name = dxfName;
            }

            keyByName[dxfName] = key;
            sourceLayers.Add(new LayerDef { Key = key, Name = name });
        }

        return (sourceLayers, keyByName);
    }
}
