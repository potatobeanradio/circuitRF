// DXF's named layers are the ideal input for L1g's LayoutLayerMapping (§2.4 note in
// src/Ui/CLAUDE.md: "its name-first matching was written for precisely this") — adapted here exactly
// the way GdsiiLayerReconciliation adapts GDSII's numeric-only (layer, datatype) identity onto the
// same unmodified LayoutLayerMapping.Propose. DXF is the opposite case: it has NAMES but no numeric
// key, so a synthetic LayerKey must be invented per distinct incoming name.

using CircuitRF.Ui.Theming;

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
    /// <summary><paramref name="layerTable"/> (brief-dxf-layer-colors.md R-col-3/R-col-4) is the file's
    /// own parsed <c>LAYER</c> table — carried alongside the name-based reconciliation this method
    /// already did, so the resulting <see cref="LayerDef"/> (installed verbatim by "Add to technology")
    /// carries the DXF's OWN colour rather than defaulting to black (<see cref="Rgba"/>'s zero value,
    /// what the pre-brief version of this method silently left every added layer at). Colour resolution
    /// (R-col-5): an exact group-420 true colour wins when present; otherwise the group-62 ACI index
    /// decodes through <see cref="DxfAciPalette"/> — EXCEPT ACI 7 (or a name entirely absent from the
    /// table), which is never taken literally as white/black and instead falls back to the SAME
    /// <see cref="FallbackPalette"/> gap-fill the renderer already uses for an undefined layer, keyed by
    /// this layer's own (possibly synthetic) <see cref="LayerKey"/> so the colour is at least
    /// deterministic and distinguishable from every other unresolved layer in the same import.</summary>
    public static (IReadOnlyList<LayerDef> SourceLayers, IReadOnlyDictionary<string, LayerKey> KeyByDxfName) BuildSourceLayers(
        IReadOnlyList<string> dxfLayerNames, IReadOnlyList<DxfLayerTableEntry> layerTable, Technology? destTech)
    {
        var distinct = dxfLayerNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sourceLayers = new List<LayerDef>(distinct.Count);
        var keyByName = new Dictionary<string, LayerKey>(StringComparer.OrdinalIgnoreCase);
        var tableByName = new Dictionary<string, DxfLayerTableEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in layerTable)
            tableByName.TryAdd(entry.Name, entry); // first record wins on a duplicate name (non-conformant, but never crash)

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
            tableByName.TryGetValue(dxfName, out var tableEntry);
            sourceLayers.Add(new LayerDef
            {
                Key = key,
                Name = name,
                Color = ResolveColor(tableEntry, key),
                Visible = !(tableEntry?.Off ?? false) && !(tableEntry?.Frozen ?? false),
            });
        }

        return (sourceLayers, keyByName);
    }

    private static Rgba ResolveColor(DxfLayerTableEntry? entry, LayerKey key)
    {
        if (entry?.TrueColor is { } trueColor) return trueColor;
        int aci = entry?.AciIndex ?? 7;
        // R-col-5: ACI 7 means "black or white, depending on background" — never take it literally,
        // including when the table is absent or this name is missing from it (both default to 7 above).
        return aci == 7 ? FallbackPalette.For(key).Color : DxfAciPalette.ToRgb(aci);
    }
}
