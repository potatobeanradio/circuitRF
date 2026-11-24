// ================================================================
//  LinkCatalog.cs  —  what a pin row's link picker offers (R-ddl2-1)
//
//  Every link source in the display, for one pinned axis of one trace:
//  the ones whose coordinates fit the axis first, then the ones that do
//  not (with what they supply), then — greyed — the ones that would
//  close a cycle (with the chain). Below the firewall so a test, and a
//  later headless caller, sees the same list the window shows.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

/// <summary>One entry of a link picker.</summary>
/// <param name="Where">The plot and trace the source sits on: "Smith 1, GammaLoad".</param>
/// <param name="Matches">Whether its coordinates fit the axis by name or unit (R-ddl1-6 rules 1–3).</param>
/// <param name="Supplies">What it gives, for an entry that does not fit: "pinStep (dBm)".</param>
/// <param name="CycleChain">The chain linking to it would close; null when it would not.</param>
public sealed record LinkSourceItem(string Id, string Name, string Where, bool Matches, string Supplies, string? CycleChain)
{
    public bool    IsEnabled => CycleChain is null;
    public string  Label     => $"{Name} — {Where}";
    public string? Tooltip   => CycleChain is { } c ? $"That would make a cycle: {c}."
                              : Matches ? null : $"{Name} gives {Supplies}.";
    public override string ToString() => Label;
}

public static class LinkCatalog
{
    /// <summary>
    /// The sources a pin at <paramref name="sliceIndex"/> of <paramref name="consumer"/> may follow. A marker on the
    /// consumer itself is listed too — greyed, as the cycle it would be.
    /// </summary>
    public static IReadOnlyList<LinkSourceItem> For(LinkDocument doc, Trace consumer, int sliceIndex, Axis? axis,
                                                   IPlotDataSources? sources)
    {
        int tab = doc.TabOf(consumer);
        var items = new List<LinkSourceItem>();
        DataSet? data = consumer.SourcePath is { } sp ? sources?.DataFor(sp) : null;
        // The trial selection is a source for a trial axis only (brief-ddl-4 R-ddl4-5): it supplies nothing else.
        if (axis?.Name == "trial" && sliceIndex >= 0)
            items.Add(new LinkSourceItem(LinkDocument.SelectionId, LinkDocument.SelectionId, "the trials selected",
                                         Matches: true, "trial", null));
        foreach (var site in doc.Markers())
        {
            string id = site.Marker.Id.ToString("D");
            var value = MarkerLinkSource.Resolve(site, sources);
            bool matches = axis is not null && LinkMatch.Match(value, axis, 0, data).IsLive;
            items.Add(new LinkSourceItem(id, site.Marker.Name, Where(doc, site, tab), matches,
                                         LinkMatch.Supplies(value), WouldCycle(doc, consumer, sliceIndex, id)));
        }
        // Display controls (brief-ddl-5 R-ddl5-8) sit beside the markers; they sit on no trace, so they close no cycle.
        foreach (var (ti, control) in doc.Controls())
        {
            var value = ControlLinkSource.Resolve(control, sources);
            bool matches = axis is not null && LinkMatch.Match(value, axis, 0, data).IsLive;
            string where = ti == tab ? "control" : $"{doc.Tabs[ti].Name} ▸ control";
            items.Add(new LinkSourceItem(control.Id, control.Name, where, matches, LinkMatch.Supplies(value), null));
        }
        return [.. items.OrderBy(i => i.IsEnabled ? (i.Matches ? 0 : 1) : 2)
                        .ThenBy(i => i.Name, StringComparer.Ordinal)];
    }

    /// <summary>A pin a marker can drive, for its context menu's <b>Link plots ▸</b> (R-ddl2-2).</summary>
    public sealed record Target(Trace Consumer, int SliceIndex, string Label, bool IsLinked);

    /// <summary>
    /// Every pinned, linkable axis in the display that the marker at <paramref name="site"/> fits (R-ddl1-6 rules
    /// 1–3) without closing a cycle — the one-gesture route from an overview to a detail plot. Its own trace is not a
    /// target: a marker cannot choose the curve it sits on.
    /// </summary>
    public static IReadOnlyList<Target> TargetsFor(LinkDocument doc, LinkDocument.MarkerSite site, IPlotDataSources sources)
    {
        var value = MarkerLinkSource.Resolve(site, sources);
        string id = site.Marker.Id.ToString("D");
        var list = new List<Target>();
        foreach (var (ti, plot, t) in doc.Traces())
        {
            if (ReferenceEquals(t, site.Trace) || !LinkGraph.IsSingleCube(t) || t.SourcePath is not { } sp) continue;
            if (sources.DataFor(sp) is not { } ds || !ds.Contains(t.CubeName!)) continue;
            var cube = ds[t.CubeName!];
            for (int i = 0; i < t.Slice!.Length; i++)
            {
                var e = t.Slice[i];
                if (e.Role != AxisRole.PinToIndex) continue;
                var axis = cube.Axes.FirstOrDefault(a => a.Name == e.AxisName);
                if (axis is null || !SliceTokenParser.IsLinkableAxis(axis.Name, axis.Labels, axis.Length)) continue;
                bool linked = string.Equals(e.Link, id, StringComparison.OrdinalIgnoreCase);
                if (!linked && (!LinkMatch.Match(value, axis, e.Index, ds).IsLive || WouldCycle(doc, t, i, id) is not null)) continue;
                list.Add(new Target(t, i, $"{PlotName(doc, ti, plot, site.TabIndex)}, {LinkGraph.NameOf(t)} — {axis.Name}", linked));
            }
        }
        return list;
    }

    /// <summary>The chain a link from this pin to source <paramref name="id"/> would close, or null.</summary>
    public static string? WouldCycle(LinkDocument doc, Trace consumer, int sliceIndex, string id)
    {
        if (LinkDocument.IsSelection(id)) return null;   // the selection sits on no trace, so it closes nothing
        string? Probe() => LinkGraph.Build(doc).Cycles.TryGetValue(consumer, out var chain) ? chain : null;

        // A contour's or an expression's frequency (brief-ddl-4 R-ddl4-3, R-ddl4-4).
        if (sliceIndex == LinkGraph.ContourFreqSlot && consumer.ContourData is { } cd)
        {
            var (was, wasName) = (cd.FreqLink, cd.FreqLinkName);
            cd.FreqLink = id; cd.FreqLinkName ??= id;
            try { return Probe(); }
            finally { cd.FreqLink = was; cd.FreqLinkName = wasName; }
        }
        if (sliceIndex == LinkGraph.ExpressionFreqSlot)
        {
            var (was, wasName) = (consumer.ExpressionFreqLink, consumer.ExpressionFreqLinkName);
            consumer.ExpressionFreqLink = id; consumer.ExpressionFreqLinkName ??= id;
            try { return Probe(); }
            finally { consumer.ExpressionFreqLink = was; consumer.ExpressionFreqLinkName = wasName; }
        }

        if (consumer.Slice is not { } slice || sliceIndex < 0 || sliceIndex >= slice.Length) return null;
        var probe = (AxisSlice[])slice.Clone();
        probe[sliceIndex] = probe[sliceIndex] with { Role = AxisRole.PinToIndex, Link = id };
        consumer.Slice = probe;
        try { return Probe(); }
        finally { consumer.Slice = slice; }
    }

    /// <summary>
    /// A plot as a person names it in a list: its own title when it has one, else its kind and its place among
    /// that tab's plots of the kind ("Smith 1", "Rect 2"); on another tab, the tab's name first.
    /// </summary>
    public static string PlotName(LinkDocument doc, int tabIndex, Plot plot, int fromTab)
    {
        string name;
        if (plot.CustomTitleOn && !string.IsNullOrWhiteSpace(plot.CustomTitle)) name = plot.CustomTitle!;
        else
        {
            int k = doc.Tabs[tabIndex].Plots.Where(p => p.PlotType == plot.PlotType).ToList().IndexOf(plot) + 1;
            name = $"{plot.PlotType} {k}";
        }
        return tabIndex == fromTab ? name : $"{doc.Tabs[tabIndex].Name} ▸ {name}";
    }

    private static string Where(LinkDocument doc, LinkDocument.MarkerSite site, int fromTab)
        => $"{PlotName(doc, site.TabIndex, site.Plot, fromTab)}, {LinkGraph.NameOf(site.Trace)}";
}

/// <summary>Copy and paste of linked plots (R-ddl2-7).</summary>
public static class LinkRemap
{
    /// <summary>
    /// Gives every marker in a pasted set a new id, and points every link inside the set that named one of those
    /// markers at its copy — so a pasted overview and detail pair drives itself. A link to a marker outside the set
    /// keeps pointing at the original.
    /// </summary>
    public static void Renew(IEnumerable<PlotContainerConfig> plots)
    {
        var list = plots.ToList();
        var map  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mc in list.SelectMany(p => p.Traces).SelectMany(t => t.Markers))
        {
            string fresh = Guid.NewGuid().ToString("D");
            if (mc.Id is { Length: > 0 } old) map[old] = fresh;
            mc.Id = fresh;
        }
        foreach (var s in list.SelectMany(p => p.Traces).SelectMany(t => t.CubeSlice))
            if (s.Link is { } link && map.TryGetValue(link, out var copy)) s.Link = copy;
        // The frequency links (brief-ddl-4 R-ddl4-3, R-ddl4-4) are remapped alike.
        foreach (var t in list.SelectMany(p => p.Traces))
        {
            if (t.ExpressionFreqLink is { } ef && map.TryGetValue(ef, out var efCopy)) t.ExpressionFreqLink = efCopy;
            if (t.ContourTrace?.FreqLink is { } cf && map.TryGetValue(cf, out var cfCopy)) t.ContourTrace.FreqLink = cfCopy;
        }
    }
}
