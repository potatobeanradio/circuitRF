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
    /// <param name="electricalOnly">
    /// <b>True — the default and every existing caller — drops a declared layer no stackup entry
    /// claims</b>, because a soldermask opening is drawn ON a land and a silkscreen outline runs
    /// between two of them; left in, the connectivity walk joins whatever they overlap.
    ///
    /// <para>False keeps every layer, which is what a reader asking a GEOMETRIC question rather
    /// than an electrical one needs: a DRC rule measures a mask clearance, and brief 14's
    /// recognition <c>Body</c> may legitimately name a dielectric — <c>MIM Metal AND Nitride</c> is
    /// the deck's own example, and with the drop applied it would evaluate to nothing and recognise
    /// nothing, silently. <c>DrcEngine</c> already builds its own regions this way; this is that
    /// same reading, sharing the one expansion rather than a second copy of it.</para>
    /// </param>
    public static Dictionary<LayerKey, Paths64> Build(
        IReadOnlyList<LayoutShape> shapes, Technology tech, List<string>? diagnostics = null,
        bool electricalOnly = true)
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
                FaceOneWay(paths);
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
            if (electricalOnly && !claimed.Contains(layer) && declared.TryGetValue(layer, out string? name))
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

    /// <summary>
    /// Turns one shape's rings so its OUTER ring winds positively — and with it, its holes the
    /// other way.
    /// </summary>
    /// <remarks>
    /// <b>Without this, two pieces of copper that overlap can come back as a HOLE.</b>
    /// <c>LayoutClipper.Rule</c> is <c>FillRule.NonZero</c>, so a clockwise ring carries winding
    /// −1; union it with a counter-clockwise ring and the overlap sums to zero. The union then
    /// punches a gap exactly where the two meet and the partition reports them as separate nets —
    /// silently, with the picture showing them plainly joined.
    ///
    /// <para><b>It is reachable three ways, and none of them is exotic</b>: the polygon tool
    /// follows the user's clicks, so a polygon drawn clockwise is clockwise; a MIRRORED or
    /// negatively-scaled placement reverses every ring its sub-cell contributes, because a
    /// reflection reverses orientation; and before this brief a rotated <c>RectShape</c> came out
    /// of the flatten with its corners swapped, which <c>LayoutCoordinateWalk</c> now prevents at
    /// source. Found by brief 3's rotation-and-mirror gate.</para>
    ///
    /// <para><b>Here rather than in <see cref="LayoutClipper.ToClipperPaths"/></b>, which is the
    /// other candidate and the funnel every export shares: reversing a ring changes the ORDER the
    /// vertices are written in, and the interchange gates compare exported files byte for byte.
    /// Orientation matters only when rings from DIFFERENT shapes are unioned, and this function is
    /// the one place in the repository where that happens to copper.</para>
    ///
    /// <para>The outer ring is the one with the largest absolute area — a hole is inside it by
    /// construction, so it cannot be larger.</para>
    /// </remarks>
    private static void FaceOneWay(Paths64 paths)
    {
        if (paths.Count == 0) return;

        int outer = 0;
        double largest = -1;
        for (int i = 0; i < paths.Count; i++)
        {
            double area = Math.Abs(Clipper.Area(paths[i]));
            if (area > largest) { largest = area; outer = i; }
        }

        if (Clipper.Area(paths[outer]) >= 0) return;
        foreach (var path in paths) path.Reverse();
    }
}
