// Framework-free layer-mapping component (docs/sonnet-briefs/brief-L1g-technology-retarget.md §1).
// Shared by both cross-technology paste reconciliation and technology retargeting — they are the
// same question asked twice ("these shapes were authored against technology A and are moving to
// technology B; where does each layer go?"), and answering it in two places guarantees the two
// answers drift apart. See docs/design/layout-view.md §2.4 (technology scope) and §2.1 (layer
// identity is (Layer, Datatype) WITHIN one technology — across two technologies the numeric key
// carries no meaning at all, because each process numbers its own layers; names are what survive
// the crossing).

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Design.Layout;

/// <summary>Why a row's target was proposed — shown in the mapping dialog so a 20-row table is
/// scannable rather than a black box the user must either rubber-stamp or distrust.</summary>
public enum LayerMatchKind
{
    /// <summary>Destination has a layer at the same (Layer, Datatype) key with the same name
    /// (case-/whitespace-insensitive). Effectively the same layer. High confidence.</summary>
    SameKeySameName,

    /// <summary>Destination has a layer at a DIFFERENT key whose name matches exactly
    /// (case-/whitespace-insensitive). Names are what a human authored and they survive
    /// renumbering. High confidence.</summary>
    ExactName,

    /// <summary>Destination has a layer at the same key but a DIFFERENT name — the Drill→Substrate
    /// trap. Proposed, but low confidence: never applied silently (R-L1g-2).</summary>
    SameKeyDifferentName,

    /// <summary>Nothing in the destination technology corresponds to this source layer at all.
    /// Defaults to Keep as unknown.</summary>
    NoMatch,
}

/// <summary>One row of a layer-mapping table: a source layer actually used by the shapes being
/// moved, how many shapes use it (sort key — the layers that matter appear first), what target was
/// proposed and why, and what the user (or the default) settled on.</summary>
public sealed record LayerMappingRow(
    LayerKey Source,
    string? SourceName,
    int ShapeCount,
    LayerKey? Proposed,
    LayerMatchKind Match,
    LayoutFragment.LayerReconciliationChoice Choice);

/// <summary>
/// Proposes a layer mapping for a set of shapes moving from one technology to another — the single
/// component behind both cross-technology paste (L1f's <c>LayoutFragment.GetMissingLayers</c> asked
/// the wrong question: "which keys are absent?" instead of "which layers need confirmation?") and
/// technology retargeting (new in this phase).
/// </summary>
public static class LayoutLayerMapping
{
    /// <summary>What a settled mapping did — the payload behind the §5 "report what happened"
    /// Messages summary, shared by both the retarget and cross-tech-paste callers.</summary>
    public sealed record Summary(string? TechName, int ShapeCount, IReadOnlyList<LayerMappingRow> Rows);


    /// <summary>
    /// Builds one row per distinct source layer key actually used by <paramref name="shapes"/>,
    /// sorted by shape count descending. Returns an empty list when <paramref name="destTech"/> is
    /// null — with no technology at all, every layer already renders identically via
    /// <see cref="FallbackPalette"/> regardless of key, so there is nothing to reconcile (mirrors
    /// L1f's <c>GetMissingLayers</c> null-tech behavior).
    /// </summary>
    public static IReadOnlyList<LayerMappingRow> Propose(
        IReadOnlyList<LayoutShape> shapes,
        IReadOnlyList<LayerDef> sourceLayers,
        Technology? destTech)
    {
        if (destTech is null) return [];

        var counts = new Dictionary<LayerKey, int>();
        foreach (var s in shapes)
            counts[s.Layer] = counts.GetValueOrDefault(s.Layer) + 1;

        var rows = new List<LayerMappingRow>(counts.Count);
        foreach (var (key, count) in counts)
        {
            string? sourceName = sourceLayers.FirstOrDefault(l => l.Key == key)?.Name;
            var (proposed, match) = ProposeTarget(key, sourceName, destTech);

            var choice = match is LayerMatchKind.SameKeySameName or LayerMatchKind.ExactName
                ? new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.MapToExisting, proposed)
                : new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.KeepUnknown);

            rows.Add(new LayerMappingRow(key, sourceName, count, proposed, match, choice));
        }

        return rows.OrderByDescending(r => r.ShapeCount).ThenBy(r => r.Source.Layer).ThenBy(r => r.Source.Datatype).ToList();
    }

    /// <summary>
    /// R-L1g-2: confirmation is required whenever any row is <see cref="LayerMatchKind.SameKeyDifferentName"/>
    /// or <see cref="LayerMatchKind.NoMatch"/>. If every row is <see cref="LayerMatchKind.SameKeySameName"/>
    /// or <see cref="LayerMatchKind.ExactName"/>, the mapping may be applied silently — that is the
    /// same-technology (or confidently-renamed) case, and it must stay frictionless.
    /// </summary>
    public static bool RequiresConfirmation(IReadOnlyList<LayerMappingRow> rows) =>
        rows.Any(r => r.Match is LayerMatchKind.SameKeyDifferentName or LayerMatchKind.NoMatch);

    /// <summary>
    /// One-line human summary of a settled mapping (§5: "Report what happened") — e.g. "Top
    /// Copper→Metal1 (name), Drill→(unknown)". <paramref name="destTech"/> supplies target layer
    /// names; without one every target renders by key only.
    /// </summary>
    public static string SummarizeMapping(IReadOnlyList<LayerMappingRow> rows, Technology? destTech)
    {
        if (rows.Count == 0) return "no layer changes";

        var parts = rows.Select(r =>
        {
            string source = r.SourceName is { Length: > 0 } n ? n : $"{r.Source.Layer}/{r.Source.Datatype}";

            string target = r.Choice switch
            {
                { Action: LayoutFragment.LayerReconciliationAction.MapToExisting, MapTarget: { } t } =>
                    destTech?.Layers.FirstOrDefault(l => l.Key == t)?.Name is { Length: > 0 } tn
                        ? tn : $"{t.Layer}/{t.Datatype}",
                { Action: LayoutFragment.LayerReconciliationAction.AddToTechnology } => "added",
                _ => "(unknown)",
            };

            string qualifier = r.Match is LayerMatchKind.SameKeySameName or LayerMatchKind.ExactName ? " (name)" : "";
            return $"{source}→{target}{qualifier}";
        });

        return string.Join(", ", parts);
    }

    /// <summary>Projects the rows' settled choices into the shape used by
    /// <see cref="LayoutFragment.ApplyReconciliation"/> — the paste path reuses that method verbatim;
    /// only the trigger (this class, not <c>GetMissingLayers</c>) and the dialog (one table, not a
    /// per-key loop) changed.</summary>
    public static IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice> BuildChoices(
        IReadOnlyList<LayerMappingRow> rows) =>
        rows.ToDictionary(r => r.Source, r => r.Choice);

    private static (LayerKey? Proposed, LayerMatchKind Match) ProposeTarget(LayerKey sourceKey, string? sourceName, Technology destTech)
    {
        var sameKey = destTech.Layers.FirstOrDefault(l => l.Key == sourceKey);

        // 1. Same key, same name.
        if (sameKey is not null && NamesMatch(sameKey.Name, sourceName))
            return (sameKey.Key, LayerMatchKind.SameKeySameName);

        // 2. Exact name match on a DIFFERENT key — checked before falling back to the same-key
        // result below, so a confident name match always beats a low-confidence numeric coincidence.
        if (sourceName is not null)
        {
            var byName = destTech.Layers.FirstOrDefault(l => l.Key != sourceKey && NamesMatch(l.Name, sourceName));
            if (byName is not null)
                return (byName.Key, LayerMatchKind.ExactName);
        }

        // 3. Same key, different name — the Drill->Substrate trap.
        if (sameKey is not null)
            return (sameKey.Key, LayerMatchKind.SameKeyDifferentName);

        // 4. Nothing corresponds at all.
        return (null, LayerMatchKind.NoMatch);
    }

    private static bool NamesMatch(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
