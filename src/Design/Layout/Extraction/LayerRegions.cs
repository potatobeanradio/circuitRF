// Per-layer unioned copper — brief-lvs-2-shared-extraction.md R-lvs2-1.
//
// Was `PdnMeshExtractor.BuildLayerRegions`. It is the first step of EVERY reading of this board's
// copper — railRF's mesh, railRF's region walk, the DRC's net-aware rules and, from brief 3, LVS —
// and it was a private detail of the one that happened to write it first. Promoting it changes
// nothing about what it returns; what it changes is that the next reader does not write a second
// one.
//
// DO NOT WRITE A SECOND EXPANSION. It goes through DrcRegions.Expand, which is the same expansion
// the DRC run performs, including its decomposition of a ViaShape into a barrel on its own layer
// and a pad on its landing layer. Sharing it is what keeps the readers from disagreeing about what
// a via IS.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>Everything drawn on this board, unioned per drawing layer.</summary>
public static class LayerRegions
{
    /// <summary>
    /// Per-layer unioned copper, built through <see cref="DrcRegions.Expand"/> — the same expansion
    /// the DRC run performs, including its decomposition of a <see cref="ViaShape"/> into a barrel on
    /// its own layer and a pad on its landing layer. Sharing it is what keeps the two from disagreeing
    /// about what a via IS.
    /// </summary>
    public static Dictionary<LayerKey, Paths64> Build(
        IReadOnlyList<LayoutShape> shapes, Technology tech, List<string>? diagnostics = null)
    {
        var byLayer = new Dictionary<LayerKey, Paths64>();
        var order = new List<LayerKey>();

        foreach (var shape in shapes)
        {
            if (!DrcRegions.IsCheckable(shape)) continue;
            DrcRegions.Expand(shape, tech, _ => long.MaxValue, (layer, _, paths) =>
            {
                if (!byLayer.TryGetValue(layer, out var acc))
                {
                    byLayer[layer] = acc = [];
                    order.Add(layer);
                }
                acc.AddRange(paths);
            });
        }

        // ── ARTWORK THAT IS NOT COPPER IS NOT COPPER ────────────────────────────────────────────
        //
        // A soldermask opening is drawn ON a land, by construction, and a silkscreen outline runs
        // between two of them. Left in, the connectivity walk joins whatever they overlap — which on
        // a board with footprints on it is the rail to its own reference through every part's mask —
        // and the run then refuses with "the rail reaches layer 5/0", naming a layer nobody thought
        // was electrical. Before footprints existed no board here carried a non-conducting layer at
        // all, which is why this was never wrong until now.
        //
        // A layer the TECHNOLOGY declares and the STACKUP does not claim is the technology author's
        // own statement that it is a drawing layer. It is dropped, and the drop is REPORTED rather
        // than silent, because the one case that looks identical from here is a copper layer
        // somebody forgot to add to the stackup — and that reader needs the sentence. A layer the
        // technology has never heard of is left alone and still reaches ResolveConductors' refusal,
        // which is the case that message was written for.
        var claimed = tech.Stackup.Layers
            .Where(l => l.Kind is StackupKind.Conductor or StackupKind.Via)
            .SelectMany(l => l.DrawingLayers)
            .ToHashSet();
        var declared = tech.Layers.ToDictionary(l => l.Key, l => l.Name);

        var unioned = new Dictionary<LayerKey, Paths64>();
        var dropped = new List<string>();
        foreach (var layer in order)
        {
            if (!claimed.Contains(layer) && declared.TryGetValue(layer, out string? name))
            {
                dropped.Add($"{layer.Layer}/{layer.Datatype} ('{name}')");
                continue;
            }
            unioned[layer] = DrcRegions.Union(byLayer[layer]);
        }

        // ONE sentence, not one per layer: every board carries a soldermask and a silkscreen, and
        // three notes saying so on every run is noise a reader learns to skip past.
        if (dropped.Count > 0)
            diagnostics?.Add(
                $"{string.Join(", ", dropped)} carry geometry that no Conductor or Via entry of the " +
                "stackup claims, so they were read as non-conducting artwork — mask openings, " +
                "silkscreen, the board outline — and left out of the electrical model. If any of " +
                "them is copper, add it to the stackup.");

        return unioned;
    }
}
