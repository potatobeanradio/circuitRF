// One answer to "which two conductors does this via join?", shared by every consumer that needs it.
//
// The split it enforces is the one brief-via-primitive-and-stackup.md R-via-3 and PlanarExtractor's
// BuildVias already state: the ARTWORK says WHERE a via is, the STACKUP says WHICH TWO CONDUCTORS it
// joins. A fab plates every via of a given kind between the same two layers whatever the drawing
// says, so the span is a process parameter carried on the StackupKind.Via entry
// (SpanFromLayer/SpanToLayer) and a via's drawing layer is what selects the entry.
//
// This existed only inside DrcConnectivity and PlanarExtractor, each with its own copy of the lookup,
// which is why the interchange writers had no access to it and invented their own answers: PcbWriter
// wrote every via from its pad's copper to the OPPOSITE outer copper (so a blind or buried via became
// a through via, silently), and GdsiiWriter/DxfWriter/GerberExport keyed the pad off the per-shape
// ViaShape.LandingLayer, which the layout editor's Via tool has never set.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Design.Layout;

/// <summary>
/// The two conductor entries a via joins, resolved against a technology's stackup and ordered so
/// <see cref="Top"/> is the one nearer the top of <c>Stackup.Layers</c> (which is ordered top to
/// bottom, R-em-3). <see cref="SpanFromLayer"/>/<see cref="SpanToLayer"/> carry no ordering promise of
/// their own — a hand-authored technology may name them either way round — so every consumer that
/// needs a direction takes it from here rather than from the raw fields.
/// </summary>
/// <param name="Entry">The <see cref="StackupKind.Via"/> entry the via's drawing layer selected.</param>
/// <param name="Top">The conductor nearer the top of the stackup.</param>
/// <param name="Bottom">The conductor nearer the bottom.</param>
public sealed record ViaSpan(StackupLayer Entry, StackupLayer Top, StackupLayer Bottom);

public static class ViaSpanResolver
{
    /// <summary>Every drawing layer bound to some <see cref="StackupKind.Via"/> entry — R-via-5's own
    /// identity for "drill-function layer". A via drawn on any other layer belongs to no entry, and is
    /// therefore inert in DRC connectivity, in EM extraction and in every export's span.</summary>
    public static HashSet<LayerKey> DrillLayerKeys(Technology? tech)
        => tech is null
            ? []
            : [.. tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).SelectMany(l => l.DrawingLayers)];

    /// <summary>The <see cref="StackupKind.Via"/> entry that claims <paramref name="barrelLayer"/>, or
    /// null when nothing does. <paramref name="barrelLayer"/> is <see cref="LayoutShape.Layer"/> — the
    /// BARREL — never <see cref="ViaShape.LandingLayer"/>, which is the pad's copper.</summary>
    public static StackupLayer? EntryFor(LayerKey barrelLayer, Technology? tech)
        => tech?.Stackup.Layers.FirstOrDefault(
            l => l.Kind == StackupKind.Via && l.DrawingLayers.Contains(barrelLayer));

    /// <summary>
    /// The span a via on <paramref name="barrelLayer"/> resolves to, or null when it cannot be
    /// resolved — no technology, no via entry claiming the layer, the entry naming no span, or a span
    /// naming a conductor the stackup does not declare. Callers distinguish those cases with
    /// <see cref="Explain"/>; null here means only "do not pretend to know".
    /// </summary>
    public static ViaSpan? Resolve(LayerKey barrelLayer, Technology? tech)
        => EntryFor(barrelLayer, tech) is { } entry ? Resolve(entry, tech) : null;

    /// <summary>The span of a known via entry — the overload for a caller already iterating
    /// <c>Stackup.Layers</c>.</summary>
    public static ViaSpan? Resolve(StackupLayer entry, Technology? tech)
    {
        if (tech is null || entry.Kind != StackupKind.Via) return null;
        if (entry.SpanFromLayer is not { Length: > 0 } from) return null;
        if (entry.SpanToLayer is not { Length: > 0 } to) return null;

        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        int a = conductors.FindIndex(c => string.Equals(c.Name, from, StringComparison.Ordinal));
        int b = conductors.FindIndex(c => string.Equals(c.Name, to, StringComparison.Ordinal));
        if (a < 0 || b < 0) return null;

        // Stackup.Layers is ordered TOP to BOTTOM, so the smaller index is the upper conductor.
        return new ViaSpan(entry, conductors[Math.Min(a, b)], conductors[Math.Max(a, b)]);
    }

    /// <summary>True when this span runs between the OUTERMOST conductors of the stackup — the
    /// definition a through via has, and the one the board format's reader applies when it decides
    /// whether a written span is through or blind/buried.</summary>
    public static bool IsThrough(ViaSpan span, Technology? tech)
    {
        if (tech is null) return false;
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        return conductors.Count >= 2
            && ReferenceEquals(conductors[0], span.Top)
            && ReferenceEquals(conductors[^1], span.Bottom);
    }

    /// <summary>
    /// Which drawing layer a via's PAD belongs on.
    ///
    /// <para>Explicit <see cref="ViaShape.LandingLayer"/> first — that is an importer's own statement
    /// about a file it read, and it must not be overridden. Otherwise the span's TOP conductor, which
    /// is what the process says the via lands on. Null only when neither is available, and then a
    /// writer must report rather than guess: the barrel's own layer is a DRILL layer, and putting
    /// copper there is "an export that looks plausible and puts copper where the hole should be"
    /// (§4.3's own warning).</para>
    /// </summary>
    public static LayerKey? PadLayer(ViaShape via, Technology? tech)
    {
        if (via.LandingLayer is { } landing) return landing;
        if (Resolve(via.Layer, tech) is not { } span) return null;
        return span.Top.DrawingLayers.Count > 0 ? span.Top.DrawingLayers[0] : null;
    }

    /// <summary>
    /// Why <see cref="Resolve"/> returned null, as a sentence naming the remedy — or null when it did
    /// not. Written once here so the editor's tooltip, the properties inspector and three export
    /// diagnostics all say the same thing about the same state.
    /// </summary>
    public static string? Explain(LayerKey barrelLayer, Technology? tech)
    {
        if (tech is null)
            return "no technology is resolved for this layout, so nothing states which conductors a via joins.";

        if (EntryFor(barrelLayer, tech) is not { } entry)
        {
            var layerName = tech.Layers.FirstOrDefault(l => l.Key.Equals(barrelLayer))?.Name
                            ?? $"({barrelLayer.Layer},{barrelLayer.Datatype})";
            var drill = tech.Stackup.Layers
                .Where(l => l.Kind == StackupKind.Via)
                .SelectMany(l => l.DrawingLayers)
                .Distinct()
                .Select(k => tech.Layers.FirstOrDefault(d => d.Key.Equals(k))?.Name)
                .Where(n => n is { Length: > 0 })
                .ToList();

            return drill.Count > 0
                ? $"layer \"{layerName}\" is not bound to any via entry in this technology's stackup — " +
                  $"draw vias on {string.Join(", ", drill)} instead, or bind this layer to a via entry in the Stackup tab."
                : $"layer \"{layerName}\" is not bound to any via entry, and this technology's stackup declares none — " +
                  "add a via entry in the Stackup tab and bind a drawing layer to it.";
        }

        if (entry.SpanFromLayer is not { Length: > 0 } || entry.SpanToLayer is not { Length: > 0 })
            return $"via entry \"{entry.Name}\" names no Spans conductors — set them in the technology " +
                   "editor's Stackup tab. Which two levels a via joins is a property of the process, not of the drawing.";

        var names = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor)
            .Select(l => l.Name)
            .ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        if (!names.Contains(entry.SpanFromLayer!)) missing.Add(entry.SpanFromLayer!);
        if (!names.Contains(entry.SpanToLayer!)) missing.Add(entry.SpanToLayer!);
        if (missing.Count > 0)
            return $"via entry \"{entry.Name}\" spans {string.Join(" and ", missing.Select(m => $"\"{m}\""))}, " +
                   "which this technology's stackup declares no conductor for.";

        return null;
    }
}
