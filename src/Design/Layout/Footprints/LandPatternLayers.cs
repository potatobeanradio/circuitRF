// Layers by ROLE, never by key — R-fp1-3. The whole reason a land pattern is generated rather than
// shipped as a .clay per case size (series overview §1b).

using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>Which drawing layer each of a land pattern's four roles resolved to on one technology.
/// Copper is required; the other three are null when the technology declares nothing for them, and
/// their shapes are then OMITTED rather than relocated (R-fp1-3a).</summary>
public sealed record LandPatternRoles(
    LayerKey? Copper,
    LayerKey? Soldermask,
    LayerKey? Silkscreen,
    LayerKey? Assembly);

/// <summary>
/// Resolves the four roles a land pattern draws on.
/// </summary>
public static class LandPatternLayers
{
    /// <summary>
    /// Resolves every role, appending one sentence to <paramref name="diagnostics"/> for each
    /// OPTIONAL role that is missing, and one for a missing COPPER role — which is a refusal, not a
    /// warning (R-fp1-3b): there is no land pattern without lands.
    ///
    /// <para><b>A missing optional role never falls back to another layer.</b> Putting a silkscreen
    /// outline on Soldermask Top because the technology has no silk is worse than drawing no
    /// outline: the mask opening is manufacturing data, and a stray rectangle in it is a defect
    /// nobody sees until fabrication.</para>
    /// </summary>
    public static LandPatternRoles Resolve(
        Technology? technology, PCellLayerSelection layerSelection, List<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        layerSelection ??= PCellLayerSelection.Default;

        if (technology is null)
        {
            diagnostics.Add(
                "no technology resolved for this document, so there is no copper layer to put lands on. " +
                "A land pattern needs a front-copper layer; attach the design to a workspace whose " +
                "technology declares one.");
            return new LandPatternRoles(null, null, null, null);
        }

        var copper = ResolveCopper(technology, layerSelection, diagnostics);
        if (copper is null) return new LandPatternRoles(null, null, null, null);

        var mask = ResolveOptional(technology, "F.Mask", ["soldermask", "solder mask"], "soldermask");
        if (mask is null)
            diagnostics.Add(Missing(technology, "soldermask", "F.Mask", "the mask openings were omitted"));

        var silk = ResolveOptional(technology, "F.SilkS", ["silkscreen", "silk"], "silkscreen");
        if (silk is null)
            diagnostics.Add(Missing(technology, "silkscreen", "F.SilkS", "the body outline was omitted"));

        // R-fp1-3c: `Edge.Cuts` is NOT the courtyard layer. That layer is the BOARD OUTLINE, and a
        // courtyard rectangle on it is a routed slot. Where the technology declares no assembly
        // layer, the courtyard is omitted and is NOT relocated. The Power Rail example's technology
        // has neither, which is exactly why this is written down before anyone tries it.
        var assembly = ResolveOptional(technology, "F.CrtYd", ["courtyard", "assembly"], "courtyard")
                    ?? ResolveOptional(technology, "F.Fab", ["fabrication"], "assembly");
        if (assembly is null)
            diagnostics.Add(Missing(technology, "courtyard/assembly", "F.CrtYd or F.Fab",
                                    "the courtyard outline was omitted — it is NOT drawn on the board outline"));

        return new LandPatternRoles(copper, mask, silk, assembly);
    }

    /// <summary>
    /// The front copper of a BOARD.
    ///
    /// <para><b>Two steps, and the second one has a condition the brief's own wording does not.</b>
    /// An explicit <c>F.Cu</c> alias is the technology author's statement that this layer is a
    /// board's mounting surface, and it is taken at its word. Absent one, the topmost stackup
    /// conductor is the candidate — <c>SubstrateResolver.ResolveSignalLayerKey</c>'s own rule — but
    /// it must also BE a mounting surface, and that is tested rather than assumed.</para>
    ///
    /// <para><b>Why the test exists.</b> R-fp1-3b says a MMIC technology is refused, and the brief
    /// names <c>ResolveSignalLayerKey</c> as what refuses it. It does not: that function never
    /// fails, and on <c>mmic-GaAs_2LM_100um</c> it happily returns Metal2. Without some further
    /// condition a chip land pattern would be generated on a GaAs die, which R-fp1-3b calls — and
    /// it is — a category error. The condition used here is the physical one: a solderable surface
    /// is copper bonded to a laminate, so the candidate conductor must sit directly on a SOLID
    /// dielectric. The MMIC's topmost metal sits on <c>Air</c> (it is an air-bridge level), and is
    /// refused; every PCB technology's top copper sits on its prepreg or core, and is not — including
    /// the Power Rail example's, whose layers carry no interchange aliases at all.</para>
    /// </summary>
    private static LayerKey? ResolveCopper(
        Technology technology, PCellLayerSelection layerSelection, List<string> diagnostics)
    {
        foreach (var layer in technology.Layers)
            if (string.Equals(layer.Interchange?.PcbLayerName, "F.Cu", StringComparison.OrdinalIgnoreCase))
                return layer.Key;

        var conductors = technology.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        var top = conductors.Count > 0 ? conductors[0] : null;
        if (top is null || top.DrawingLayers.Count == 0)
        {
            diagnostics.Add(
                $"technology '{technology.Name}' declares no front-copper layer, so there is nothing to put " +
                "lands on. A land pattern needs a top conductor with a drawing layer, or a layer aliased F.Cu.");
            return null;
        }

        int topIndex = technology.Stackup.Layers.IndexOf(top);
        var beneath = topIndex >= 0 && topIndex + 1 < technology.Stackup.Layers.Count
            ? technology.Stackup.Layers[topIndex + 1]
            : null;

        bool onLaminate = beneath is { Kind: StackupKind.Dielectric } && beneath.Epsr > 1.0;
        if (!onLaminate)
        {
            string what = beneath is null
                ? "nothing beneath it"
                : beneath.Kind == StackupKind.Dielectric
                    ? $"the dielectric '{beneath.Name}' (relative permittivity {beneath.Epsr:G4})"
                    : $"'{beneath.Name}', which is not a dielectric";
            diagnostics.Add(
                $"technology '{technology.Name}' has no front-copper layer a part can be soldered to: its topmost " +
                $"conductor '{top.Name}' sits on {what}, so it is not a laminate surface. A chip land pattern " +
                "needs a board front copper (F.Cu); this technology is not a board technology.");
            return null;
        }

        // The signal-layer override is honoured the same way every other PCell honours it, and by
        // the same function, so a land pattern and an MLIN on one design never disagree about which
        // conductor is the top one.
        return SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
    }

    /// <summary>
    /// An optional role: the board-format alias first — the technology author's own statement —
    /// then <see cref="LayerDef.Purpose"/>, then the layer's own NAME. Name matching requires the
    /// layer to say it is the FRONT side, because a technology declares both sides and putting a
    /// top-side land pattern's silkscreen on Silk Bottom is a defect that renders perfectly.
    /// </summary>
    private static LayerKey? ResolveOptional(
        Technology technology, string pcbAlias, string[] words, string purpose)
    {
        foreach (var layer in technology.Layers)
            if (string.Equals(layer.Interchange?.PcbLayerName, pcbAlias, StringComparison.OrdinalIgnoreCase))
                return layer.Key;

        foreach (var layer in technology.Layers)
            if (layer.Purpose is { Length: > 0 } p &&
                string.Equals(p.Trim(), purpose, StringComparison.OrdinalIgnoreCase))
                return layer.Key;

        foreach (var layer in technology.Layers)
        {
            string name = layer.Name ?? "";
            if (string.Equals(name, pcbAlias, StringComparison.OrdinalIgnoreCase)) return layer.Key;
            if (!IsFrontSide(name)) continue;
            foreach (string w in words)
                if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return layer.Key;
        }

        return null;
    }

    private static bool IsFrontSide(string name)
        => name.StartsWith("F.", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("top", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("front", StringComparison.OrdinalIgnoreCase);

    private static string Missing(Technology technology, string role, string alias, string consequence)
        => $"technology '{technology.Name}' declares no {role} layer ({alias}), so {consequence}. " +
           "Nothing was drawn on another layer in its place.";
}
