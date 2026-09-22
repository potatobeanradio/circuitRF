// Drawing layers carrying geometry that no stackup conductor claims — R-rail27-1d.
//
// ── THE CIRCLE THIS BREAKS ──────────────────────────────────────────────────────────────────────
//
// `PdnMeshExtractor.ResolveConductors` already refuses a rail that reaches a layer no Conductor
// entry claims, and on the board this came from that refusal is useless: it needs a RUN, a run needs
// a confirmed reference, and the missing conductor is precisely why there is no reference to
// confirm. Circular, and the user is inside the circle.
//
// railRF holds the flattened shapes and the technology the moment the board opens, so the question
// can be answered THERE, before the first run rather than after a run that cannot happen. That is
// all this file is: the same fact, asked of the artwork instead of asked of a rail.
//
// It states no rule of its own and reaches no different conclusion — the predicate is the extractor's
// (a drawing layer bound to some Conductor entry, with via drawing layers exempt because a barrel
// disc is a bridge between conductors and not sheet copper of its own), and the remedy it names is
// the extractor's too.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>One drawing layer that carries copper and belongs to no conductor.</summary>
/// <param name="Layer">The drawing layer.</param>
/// <param name="Name">What the technology calls it, or its key where it names nothing.</param>
/// <param name="ShapeCount">How many shapes are on it. <b>The actionable part</b> — "unclaimed"
/// says nothing, <i>328 shapes</i> is what distinguishes a plane from a stray drawing.</param>
public sealed record PdnUnclaimedLayer(LayerKey Layer, string Name, int ShapeCount);

/// <summary>Which of a board's drawing layers carry copper no stackup conductor claims.</summary>
public static class PdnUnclaimedCopper
{
    /// <summary>
    /// The layers <paramref name="shapes"/> put geometry on that no <see cref="StackupKind.Conductor"/>
    /// entry of <paramref name="tech"/> binds, largest first.
    /// </summary>
    public static IReadOnlyList<PdnUnclaimedLayer> On(
        Technology? tech, IReadOnlyList<LayoutShape>? shapes)
    {
        if (tech is null || shapes is null || shapes.Count == 0) return [];

        var claimed = tech.Stackup.Layers
            .Where(l => l.Kind is StackupKind.Conductor or StackupKind.Via)
            .SelectMany(l => l.DrawingLayers)
            .ToHashSet();

        // ── MASK, PASTE, LEGEND AND THE DRAWINGS ARE NOT MISSING CONDUCTORS ──────────────────
        //
        // A fabrication set carries five or six layers that are artwork by nature and belong in no
        // stackup, and every one of them would otherwise be named here on every board — which is the
        // note nobody reads. The rule is the identification cascade's own and is asked of the
        // TECHNOLOGY: the declared FileFunction where the layer carries one, its name otherwise.
        // A miss costs a line of text and never a wrong answer, which is the same bargain
        // `GerberImport.IsMaskPasteOrLegend` states for its own sentence.
        foreach (var layer in tech.Layers)
            if (GerberLayerCascade.IsNonConductorArtwork(
                    layer.Interchange?.GerberFileFunction, layer.Name, layer.Interchange?.GerberSuffix)
                || string.Equals(layer.Purpose, GerberLayerCascade.DrillPurpose, StringComparison.Ordinal))
                claimed.Add(layer.Key);

        var counts = new Dictionary<LayerKey, int>();
        foreach (var shape in shapes)
            if (!claimed.Contains(shape.Layer))
                counts[shape.Layer] = counts.GetValueOrDefault(shape.Layer) + 1;

        return [.. counts
            .Select(kv => new PdnUnclaimedLayer(
                kv.Key,
                tech.Layers.FirstOrDefault(l => l.Key == kv.Key)?.Name is { Length: > 0 } n
                    ? n : $"{kv.Key.Layer}/{kv.Key.Datatype}",
                kv.Value))
            .OrderByDescending(u => u.ShapeCount)
            .ThenBy(u => u.Layer.Layer)
            .ThenBy(u => u.Layer.Datatype)];
    }

    /// <summary>
    /// What a reader is told, or empty where there is nothing to say.
    /// </summary>
    /// <remarks>
    /// <b>The extractor's own remedy, worded for a board nobody has run yet</b> — the two must not
    /// drift, because they are one fact reported from two places and a user who saw one will read the
    /// other. The reference clause is the addition: at open time, the consequence a designer is about
    /// to hit is that the combo does not offer their plane.
    /// </remarks>
    /// <param name="layers">What <see cref="On"/> found.</param>
    /// <param name="tech">The technology, where the caller has it — so the sentence can name the
    /// CONDUCTOR the layer belongs on as well as the layer (field report, 2026-09-22: a designer told
    /// that drawing layer <c>gnd</c> was unclaimed and that conductor <c>GND</c> claimed nothing
    /// answered "I have a gnd layer" — the two sentences never said they were about each other).</param>
    public static string Sentence(IReadOnlyList<PdnUnclaimedLayer> layers, Technology? tech = null)
    {
        if (layers.Count == 0) return "";

        string named = string.Join(", ", layers.Select(
            u => $"'{u.Name}' ({u.Layer.Layer}/{u.Layer.Datatype}, {u.ShapeCount:N0} shape(s))"));

        string sentence =
            $"{(layers.Count == 1 ? "Drawing layer" : "Drawing layers")} {named} " +
            $"{(layers.Count == 1 ? "carries" : "carry")} copper that no Conductor entry of the " +
            "stackup claims. Map it onto a conductor in the technology's stackup, or the copper on " +
            "it has no thickness and no resistance, nothing on it is priced or extracted, and it " +
            "cannot be named as a reference return.";

        if (tech is null) return sentence;

        foreach (var conductor in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Count == 0))
            if (TechValidation.LikelyDrawingLayerFor(tech, conductor) is { } likely
                && layers.Any(u => u.Layer == likely.Key))
                sentence += $" The stackup's conductor '{conductor.Name}' has no drawing layer at all, " +
                            $"and '{likely.Name}' is very likely its copper: attach it there with Edit " +
                            "Technology.";

        return sentence;
    }
}
