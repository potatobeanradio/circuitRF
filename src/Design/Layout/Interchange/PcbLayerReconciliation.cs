// A board file's named layers feed the SAME unmodified LayoutLayerMapping.Propose every other
// interchange path uses (docs/sonnet-briefs/brief-L4d-kicad-pcb-import.md §3, R-L4d-4). This is
// DxfLayerReconciliation's shape, for the same reason it took that shape: the format has NAMES but no
// numeric key circuitRF can adopt, so a synthetic LayerKey must be minted per distinct incoming name.
//
// It is deliberately NOT a second reconciliation. §3's own words: "Reconciliation reuses the shared
// dialog... Do not write a second reconciliation."

using CircuitRF.Design.Theming;

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbLayerReconciliation
{
    /// <summary>
    /// Builds the synthetic "source layers" <see cref="LayoutLayerMapping.Propose"/> expects, one per
    /// distinct source layer name actually used. A destination layer that declares the name as its
    /// <see cref="InterchangeMapping.PcbLayerName"/> alias donates its own key and name, so
    /// <c>Propose</c> sees <see cref="LayerMatchKind.SameKeySameName"/> rather than merely
    /// <see cref="LayerMatchKind.ExactName"/>; otherwise a fresh synthetic key is minted (never
    /// colliding with a real destination key) and the source name is kept verbatim, so a technology
    /// whose own layer is literally called <c>F.Cu</c> still matches with zero authoring.
    /// </summary>
    /// <param name="layerNames">Every distinct source layer name the geometry actually used.</param>
    /// <param name="layerTable">The file's own <c>(layers …)</c> table, which supplies each source
    /// layer's copper-ness — the one piece of colour/ordering information the format gives us.</param>
    public static (IReadOnlyList<LayerDef> SourceLayers, IReadOnlyDictionary<string, LayerKey> KeyByName) BuildSourceLayers(
        IReadOnlyList<string> layerNames, IReadOnlyList<PcbLayerTableEntry> layerTable, Technology? destTech)
    {
        var distinct = layerNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sourceLayers = new List<LayerDef>(distinct.Count);
        var keyByName = new Dictionary<string, LayerKey>(StringComparer.OrdinalIgnoreCase);

        var tableByName = new Dictionary<string, PcbLayerTableEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in layerTable)
        {
            tableByName.TryAdd(entry.CanonicalName, entry);
            if (entry.UserName is { Length: > 0 } user) tableByName.TryAdd(user, entry);
        }

        // A copper layer's CANONICAL board name, derived from its rank among the file's own copper
        // entries rather than from its name — because at the 20171130 epoch a renamed layer's user name
        // occupies the canonical slot outright, so a real file's copper can be called "top_layer" with
        // no "F.Cu" anywhere in it. Ordinal order IS top-to-bottom order in this format, which is the
        // same fact ExpandLayerSpec already relies on.
        var copperRank = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copper = layerTable.Where(e => e.IsCopper).OrderBy(e => e.Ordinal).ToList();
        for (int i = 0; i < copper.Count; i++)
            copperRank[copper[i].CanonicalName] =
                i == 0 ? "F.Cu" : i == copper.Count - 1 ? "B.Cu" : $"In{i}.Cu";

        var usedKeys = new HashSet<LayerKey>(destTech?.Layers.Select(l => l.Key) ?? []);
        int nextSynthetic = -1;

        foreach (var name in distinct)
        {
            var aliasOwner = destTech?.Layers.FirstOrDefault(l =>
                string.Equals(l.Interchange?.PcbLayerName, name, StringComparison.OrdinalIgnoreCase));

            LayerKey key;
            string layerName;
            if (aliasOwner is not null)
            {
                key = aliasOwner.Key;
                layerName = aliasOwner.Name;
            }
            else
            {
                key = new LayerKey(nextSynthetic, 0);
                while (usedKeys.Contains(key)) key = new LayerKey(--nextSynthetic, 0);
                nextSynthetic--;
                usedKeys.Add(key);
                layerName = name;
            }

            keyByName[name] = key;
            tableByName.TryGetValue(name, out var tableEntry);
            sourceLayers.Add(new LayerDef
            {
                Key = key,
                Name = layerName,
                // R-L4d-4 in the other direction: record the SOURCE layer name on the layer this import
                // mints, so an imported board can be written back out without the user hand-authoring an
                // alias per layer first. Measured need, not speculation — round-tripping a real 63-part
                // board without this put every one of its 16 layers on a general drawing layer, which
                // turned 370 tracks into graphics and collapsed 57 distinct footprints into 21.
                Interchange = aliasOwner?.Interchange ?? new InterchangeMapping(null, null, null, null, null,
                    tableEntry is { IsCopper: true } && copperRank.TryGetValue(tableEntry.CanonicalName, out var canonical)
                        ? canonical : name),
                // The format carries no per-layer colour a reader can trust across epochs (the stackup's
                // own (color "Green") describes the SOLDER MASK, not the drawing layer), so this takes
                // the same deterministic gap-fill the renderer already uses for an undefined layer —
                // exactly what DxfLayerReconciliation falls back to for ACI 7.
                Color = FallbackPalette.For(key).Color,
                Purpose = tableEntry?.IsCopper == true ? "conductor"
                        : string.Equals(name, PcbReader.DrillLayerName, StringComparison.OrdinalIgnoreCase) ? "drill"
                        : null,
            });
        }

        return (sourceLayers, keyByName);
    }
}
