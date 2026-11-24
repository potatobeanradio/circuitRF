// ================================================================
//  LinkResolve.cs  —  every linked pin in a display, chosen and drawn
//  (brief-ddl-1 R-ddl1-6 … R-ddl1-9)
//
//  ONE resolution path. The window calls this over its live tabs once a
//  document has opened, and `circuitrf render` calls it over the plots it
//  loaded, both with their own IPlotDataSources — so a linked `.cdd`
//  draws the same picture in either (the RND-4 rule, data-display.md
//  §2.9). It runs AFTER every plot has been loaded and resolved once at
//  its stored indices, because a link crosses plots and tabs.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class LinkResolve
{
    /// <summary>
    /// Chooses the index of every linked pin in <paramref name="doc"/> from where its source sits, in dependency
    /// order, and re-resolves each consumer against <paramref name="sources"/>. A source that is gone, or a cycle,
    /// leaves the pin at its stored index with <see cref="Trace.LinkFrozen"/> set and the reason in
    /// <see cref="Trace.LinkNote"/>. Returns the consumers it re-resolved.
    /// </summary>
    public static IReadOnlyList<Trace> Resolve(LinkDocument doc, IPlotDataSources sources)
    {
        var graph = LinkGraph.Build(doc);
        foreach (var t in graph.Order)
        {
            if (doc.PlotOf(t) is not { } plot) continue;
            var notes  = new List<string>();
            bool frozen = false;
            graph.Cycles.TryGetValue(t, out var cycle);
            if (cycle is not null)
            {
                frozen = true;
                notes.Add($"{cycle}: a cycle, so it is drawn where it was.");
            }

            if (LinkGraph.IsSingleCube(t))
                frozen |= ResolveSlice(t, graph.SlotsOf(t), cycle is not null, sources, notes);
            else
                t.ExpressionLinks = ExpressionResolver(doc, doc.TabOf(t), sources, cycle is not null, notes, () => frozen = true);

            TraceResolve.ResolveCubeTrace(t, sources, plot.PlotType, plot.FreqUnits);
            t.LinkNote   = notes.Count > 0 ? string.Join(" ", notes.Distinct(StringComparer.Ordinal)) : null;
            t.LinkFrozen = frozen;
        }
        return graph.Order;
    }

    /// <summary>The pinned entries of a single-cube consumer, each at the index its source chooses. True when any is frozen.</summary>
    private static bool ResolveSlice(Trace t, IReadOnlyList<LinkGraph.Slot> slots, bool inCycle,
                                     IPlotDataSources sources, List<string> notes)
    {
        var slice = (AxisSlice[])t.Slice!.Clone();
        DataCube? cube = t.SourcePath is { } sp && sources.DataFor(sp) is { } ds && ds.Contains(t.CubeName!)
            ? ds[t.CubeName!] : null;
        bool frozen = false;
        foreach (var slot in slots)
        {
            if (slot.SliceIndex < 0) continue;
            var entry = slice[slot.SliceIndex];
            var axis  = cube?.Axes.FirstOrDefault(a => a.Name == entry.AxisName);
            // The pin keeps following the source it names, by id; its name is refreshed to the source's own, so a
            // renamed marker is written back as its new name (overview D1).
            entry = entry with { Link = slot.Id ?? entry.Link, LinkName = slot.Site?.Marker.Name ?? entry.LinkName };
            if (axis is not null && !inCycle)
            {
                var value = ValueOf(slot, sources);
                var r = LinkMatch.Match(value, axis, entry.Index);
                entry = entry with { Index = r.Index };
                if (r.Note is { } n) notes.Add(slot.Error is { } err && r.Kind == LinkMatchKind.Frozen ? err : n);
                frozen |= r.Kind == LinkMatchKind.Frozen;
            }
            slice[slot.SliceIndex] = entry;
        }
        bool renamed = slice.Where((e, i) => e.LinkName != t.Slice![i].LinkName).Any();
        t.Slice = slice;
        // A renamed marker keeps its links (D1); the spec text the card shows follows the new name.
        if (renamed && t.Expression is not null) t.Expression = t.BuildPickerExpression();
        return frozen;
    }

    /// <summary>
    /// How an <c>@name</c> operand of a multi-cube expression chooses its index: by name, in the consumer's tab first,
    /// matched by the same rules. An expression has no stored index to fall back to, so a missing source draws at 0
    /// and says so.
    /// </summary>
    private static Func<string, Axis, LinkMatchResult> ExpressionResolver(
        LinkDocument doc, int tab, IPlotDataSources sources, bool inCycle, List<string> notes, Action markFrozen)
        => (name, axis) =>
        {
            LinkMatchResult r;
            if (inCycle) r = new LinkMatchResult(0, LinkMatchKind.Frozen, null);
            else if (!doc.TryFindByName(name, tab, out var site, out var err))
                r = new LinkMatchResult(0, LinkMatchKind.Frozen, err);
            else
                r = LinkMatch.Match(MarkerLinkSource.Resolve(site, sources), axis, 0);
            if (r.Note is { } n) notes.Add(n);
            if (r.Kind == LinkMatchKind.Frozen) markFrozen();
            return r;
        };

    private static LinkValue ValueOf(LinkGraph.Slot slot, IPlotDataSources sources)
        => slot.Site is { } site
            ? MarkerLinkSource.Resolve(site, sources)
            : new LinkValue(slot.Id ?? "", slot.Name, [], Frozen: true);
}
