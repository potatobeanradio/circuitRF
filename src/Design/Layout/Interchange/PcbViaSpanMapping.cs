// The read-side counterpart of ViaSpanResolver (docs/sonnet-briefs/brief-via-span-import.md).
//
// The resolver answers "which two conductors does this via join?" for everything that CONSUMES a
// layout. This answers the inverse, and only an importer asks it: a board file states a span per via,
// circuitRF states it once per PROCESS on a StackupKind.Via stackup entry, and something has to turn N
// vias into the small set of entries they actually represent — one per distinct span, each bound to
// its own drawing layer, with the vias moved onto the layer that selects theirs.
//
// Two rules do most of the work here, and both exist to stop an import accumulating junk:
//   * An existing entry whose span already IS this span is REUSED, never duplicated. The common case
//     is a board of ordinary through vias landing in a technology that already ships a PTH entry, and
//     that case must add nothing at all.
//   * A span is identified by the two CONDUCTOR ENTRIES it joins, not by the names the file used. The
//     names only ever reach here as reconciled drawing-layer keys, so an import into a technology
//     whose conductors are called "Top"/"Inner 1" produces entries naming those, and the span the
//     resolver reads back is the one the destination can actually express.

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbViaSpanMapping
{
    /// <summary>One distinct span, as the reconciled drawing-layer keys of the two copper layers the
    /// file's own <c>(layers …)</c> pair named. Ordered top-first by the source file; the ordering that
    /// is USED is re-derived from the destination stackup, because that is the one the span is
    /// expressed against.</summary>
    public readonly record struct SourceSpan(LayerKey From, LayerKey To);

    /// <param name="BarrelLayerBySpan">Which drawing layer each resolved span's vias must be drawn on
    /// for <see cref="ViaSpanResolver"/> to read the span back — the whole point of the exercise. A
    /// span absent from here could not be expressed and its vias stay where the reader put them.</param>
    /// <param name="NewEntries">The <see cref="StackupKind.Via"/> entries to APPEND to whatever stackup
    /// is in force. Additive by construction: a via entry declares a drill, it cannot invalidate a
    /// substrate, which is why it is carried separately from the whole-stackup import that a non-empty
    /// destination stackup refuses.</param>
    /// <param name="NewDrawingLayers">The drill-function layers those entries bind, one per new entry.</param>
    public sealed record Result(
        IReadOnlyDictionary<SourceSpan, LayerKey> BarrelLayerBySpan,
        IReadOnlyList<StackupLayer> NewEntries,
        IReadOnlyList<LayerDef> NewDrawingLayers,
        IReadOnlyList<string> Messages);

    private static readonly Result Empty = new(
        new Dictionary<SourceSpan, LayerKey>(), [], [], []);

    /// <summary>
    /// One <see cref="StackupKind.Via"/> entry per distinct span in <paramref name="spans"/>, reusing
    /// any <paramref name="stackup"/> already declares.
    /// </summary>
    /// <param name="spans">The distinct spans the import read, as reconciled drawing-layer keys.</param>
    /// <param name="stackup">The stackup that will actually be in force after the import — the
    /// DESTINATION technology's when it declares one (its own is never replaced), otherwise the one
    /// the board file brought. Null, or one declaring no conductors, means no span is expressible.</param>
    /// <param name="reservedKeys">Every <see cref="LayerKey"/> already spoken for — the destination
    /// technology's and the import's own synthetic ones — so a minted drill layer collides with
    /// neither.</param>
    public static Result Build(
        IReadOnlyCollection<SourceSpan> spans,
        Stackup? stackup,
        IReadOnlyCollection<LayerKey> reservedKeys)
    {
        if (spans.Count == 0) return Empty;

        var conductors = stackup?.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList() ?? [];
        var messages = new List<string>();

        if (conductors.Count == 0)
        {
            messages.Add(
                $"{spans.Count:N0} via span(s) were read from the file but could not be expressed: the technology " +
                "this import landed in declares no conductor stackup layers, and a via entry names the two it " +
                "joins. The vias were imported on the drill layer and resolve no span until a stackup exists.");
            return Empty with { Messages = messages };
        }

        var barrelBySpan = new Dictionary<SourceSpan, LayerKey>();
        var newEntries = new List<StackupLayer>();
        var newLayers = new List<LayerDef>();
        var reused = new List<string>();
        var minted = new List<string>();
        var unresolved = new List<SourceSpan>();

        // Keys are minted BELOW everything already in play. PcbLayerReconciliation mints its synthetic
        // source keys the same way and from the same negative space, so the two must not overlap —
        // hence reservedKeys carrying both, rather than each half guessing about the other.
        var used = new HashSet<LayerKey>(reservedKeys);
        int next = used.Count == 0 ? -1 : Math.Min(-1, used.Min(k => k.Layer) - 1);

        // Deterministic order, so two runs of the same import mint the same keys and the same names.
        foreach (var span in spans.OrderBy(s => s.From.Layer).ThenBy(s => s.From.Datatype)
                                  .ThenBy(s => s.To.Layer).ThenBy(s => s.To.Datatype))
        {
            int a = conductors.FindIndex(c => c.DrawingLayers.Contains(span.From));
            int b = conductors.FindIndex(c => c.DrawingLayers.Contains(span.To));
            if (a < 0 || b < 0) { unresolved.Add(span); continue; }
            if (a == b) { unresolved.Add(span); continue; }

            // Stackup.Layers is ordered TOP to BOTTOM (R-em-3), the same rule ViaSpanResolver reads a
            // span back by — so the smaller index is the upper conductor whatever the file said.
            var top = conductors[Math.Min(a, b)];
            var bottom = conductors[Math.Max(a, b)];

            if (ExistingEntryFor(stackup!, top, bottom, conductors) is { } existing)
            {
                barrelBySpan[span] = existing.DrawingLayers[0];
                reused.Add($"{top.Name}→{bottom.Name} (\"{existing.Name}\")");
                continue;
            }

            var key = new LayerKey(next, 0);
            while (used.Contains(key)) key = new LayerKey(--next, 0);
            next--;
            used.Add(key);

            string label = $"{top.Name}-{bottom.Name}";
            newLayers.Add(new LayerDef
            {
                Key = key,
                Name = $"Drill {label}",
                Color = FallbackPalette.For(key).Color,
                Purpose = "drill",
            });
            var entry = new StackupLayer
            {
                Kind = StackupKind.Via,
                Name = $"Via {label}",
                DrawingLayers = [key],
                SpanFromLayer = top.Name,
                SpanToLayer = bottom.Name,
            };
            newEntries.Add(entry);
            barrelBySpan[span] = key;
            minted.Add($"{top.Name}→{bottom.Name} (\"{entry.Name}\" on \"Drill {label}\")");
        }

        if (minted.Count > 0)
            messages.Add(
                $"Via spans: {minted.Count:N0} via entr{(minted.Count == 1 ? "y" : "ies")} added to the stackup, " +
                $"each with its own drill layer — {string.Join(", ", minted)}. The vias were drawn on those layers, " +
                "which is what states the span; a via drawn elsewhere joins nothing.");

        if (reused.Count > 0)
            messages.Add(
                $"Via spans: {reused.Count:N0} span(s) matched a via entry this technology already declares and " +
                $"added nothing — {string.Join(", ", reused)}.");

        if (unresolved.Count > 0)
            messages.Add(
                $"{unresolved.Count:N0} via span(s) name copper this technology's stackup binds to no conductor " +
                "layer, so no via entry could be built for them. Those vias stay on the drill layer and resolve " +
                "no span — bind the copper layers to conductor entries in the Stackup tab and re-import.");

        return new Result(barrelBySpan, newEntries, newLayers, messages);
    }

    /// <summary>An existing via entry joining exactly these two conductors, or null. Matched through
    /// <see cref="ViaSpanResolver"/>'s own rules — by conductor IDENTITY rather than by name text —
    /// so an entry that names its span the other way round still counts as the same span, and one
    /// naming a conductor the stackup does not declare counts as no span at all. An entry that binds
    /// no drawing layer is skipped: there would be nothing to draw the vias on.</summary>
    private static StackupLayer? ExistingEntryFor(
        Stackup stackup, StackupLayer top, StackupLayer bottom, List<StackupLayer> conductors)
    {
        foreach (var entry in stackup.Layers)
        {
            if (entry.Kind != StackupKind.Via || entry.DrawingLayers.Count == 0) continue;
            if (entry.SpanFromLayer is not { Length: > 0 } from) continue;
            if (entry.SpanToLayer is not { Length: > 0 } to) continue;

            int a = conductors.FindIndex(c => string.Equals(c.Name, from, StringComparison.Ordinal));
            int b = conductors.FindIndex(c => string.Equals(c.Name, to, StringComparison.Ordinal));
            if (a < 0 || b < 0) continue;

            if (ReferenceEquals(conductors[Math.Min(a, b)], top) &&
                ReferenceEquals(conductors[Math.Max(a, b)], bottom))
                return entry;
        }
        return null;
    }
}
