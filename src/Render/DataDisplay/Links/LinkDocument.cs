// ================================================================
//  LinkDocument.cs  —  the whole display, as the links see it
//
//  A link crosses plots and tabs, so it can only be resolved against the
//  DOCUMENT: every tab, every plot in it. The window builds one of these
//  over its live tabs; `circuitrf render` builds one over the plots it
//  loaded. Both then resolve through LinkResolve (R-ddl1-9).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Render.DataDisplay;

public sealed class LinkDocument
{
    /// <summary>One tab: its name, its plots in canvas order, and its display controls (brief-ddl-5), which are link
    /// sources of their own.</summary>
    public sealed record Tab(string Name, IReadOnlyList<Plot> Plots, IReadOnlyList<DisplayControl>? Controls = null);

    /// <summary>Where one marker lives.</summary>
    public readonly record struct MarkerSite(int TabIndex, Plot Plot, Trace Trace, Marker Marker);

    public IReadOnlyList<Tab> Tabs { get; }

    public LinkDocument(IReadOnlyList<Tab> tabs) => Tabs = tabs;

    /// <summary>
    /// The id and name of the trial selection as a link source (brief-ddl-4 R-ddl4-5). Not a marker: it is one per
    /// source FILE, so it resolves against each consumer's own source, and it never goes away. The name is reserved —
    /// <c>@selected</c> always means the selection.
    /// </summary>
    public const string SelectionId = "selected";

    /// <summary>True when a link id or name is the trial selection.</summary>
    public static bool IsSelection(string? idOrName) => string.Equals(idOrName, SelectionId, StringComparison.Ordinal);

    /// <summary>
    /// The trials selected in a source file, or null when none are. The window answers from its shared selection;
    /// a caller with none (<c>circuitrf render</c>) leaves it null, and a pin following the selection draws where it
    /// was saved.
    /// </summary>
    public Func<string?, IReadOnlySet<int>?>? SelectedTrials { get; init; }

    /// <summary>A document of one tab — a caller with no tabs of its own (a test, a single plot).</summary>
    public static LinkDocument Of(params Plot[] plots) => new([new Tab("Tab 1", plots)]);

    public IEnumerable<MarkerSite> Markers()
    {
        for (int ti = 0; ti < Tabs.Count; ti++)
            foreach (var p in Tabs[ti].Plots)
                foreach (var t in p.Traces)
                    foreach (var m in t.Markers)
                        yield return new MarkerSite(ti, p, t, m);
    }

    public IEnumerable<(int TabIndex, Plot Plot, Trace Trace)> Traces()
    {
        for (int ti = 0; ti < Tabs.Count; ti++)
            foreach (var p in Tabs[ti].Plots)
                foreach (var t in p.Traces)
                    yield return (ti, p, t);
    }

    /// <summary>Every display control, with its tab.</summary>
    public IEnumerable<(int TabIndex, DisplayControl Control)> Controls()
    {
        for (int ti = 0; ti < Tabs.Count; ti++)
            foreach (var c in Tabs[ti].Controls ?? [])
                yield return (ti, c);
    }

    /// <summary>The control with this id, or null (brief-ddl-5).</summary>
    public DisplayControl? FindControl(string id)
    {
        foreach (var (_, c) in Controls())
            if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>The control named <paramref name="name"/>: the consumer's tab first, then the document; null when
    /// there is none or the name is ambiguous across other tabs.</summary>
    public DisplayControl? FindControlByName(string name, int consumerTab)
    {
        var all = Controls().Where(x => string.Equals(x.Control.Name, name, StringComparison.Ordinal)).ToList();
        var own = all.Where(x => x.TabIndex == consumerTab).ToList();
        if (own.Count == 1) return own[0].Control;
        return own.Count == 0 && all.Count == 1 ? all[0].Control : null;
    }

    /// <summary>The marker with this id, or null when it is not in the document (R-ddl1-7: the link freezes).</summary>
    public MarkerSite? FindMarker(string id)
    {
        foreach (var s in Markers())
            if (string.Equals(s.Marker.Id.ToString("D"), id, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>The tab a trace is on, or −1.</summary>
    public int TabOf(Trace trace)
    {
        foreach (var (ti, _, t) in Traces()) if (ReferenceEquals(t, trace)) return ti;
        return -1;
    }

    /// <summary>The plot a trace is on, or null.</summary>
    public Plot? PlotOf(Trace trace)
    {
        foreach (var (_, p, t) in Traces()) if (ReferenceEquals(t, trace)) return p;
        return null;
    }

    /// <summary>
    /// The marker a typed <c>@name</c> means for a consumer on tab <paramref name="consumerTab"/> (R-ddl1-5,
    /// overview D5): the consumer's own tab first, then the document. A name on two OTHER tabs is refused, listing
    /// them; so is a name used twice in one tab; an unknown name is refused, naming the markers that exist.
    /// </summary>
    public bool TryFindByName(string name, int consumerTab, out MarkerSite site, out string error)
    {
        site = default; error = "";
        var all = Markers().Where(s => string.Equals(s.Marker.Name, name, StringComparison.Ordinal)).ToList();
        if (all.Count == 0)
        {
            var names = Markers().Select(s => s.Marker.Name).Concat(Controls().Select(c => c.Control.Name))
                                 .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            error = names.Count == 0
                ? $"No marker '{name}': this display has no markers."
                : $"No marker '{name}'. Markers: {string.Join(", ", names)}.";
            return false;
        }

        var own = all.Where(s => s.TabIndex == consumerTab).ToList();
        if (own.Count == 1) { site = own[0]; return true; }
        if (own.Count > 1) { error = $"'{name}' names {own.Count} markers on this tab; rename one."; return false; }

        var tabs = all.Select(s => s.TabIndex).Distinct().ToList();
        if (all.Count == 1) { site = all[0]; return true; }
        error = tabs.Count > 1
            ? $"'{name}' is a marker on {string.Join(" and ", tabs.Select(t => $"'{Tabs[t].Name}'"))}; rename one."
            : $"'{name}' names {all.Count} markers on '{Tabs[tabs[0]].Name}'; rename one.";
        return false;
    }

    /// <summary>The lowest <c>m&lt;n&gt;</c> no marker in the document uses (R-ddl1-2).</summary>
    public static int NextMarkerIndex(IEnumerable<Marker> markers)
    {
        var used = new HashSet<int>();
        foreach (var m in markers)
            if (m.Name.StartsWith("m", StringComparison.Ordinal) && int.TryParse(m.Name.AsSpan(1), out int n))
                used.Add(n);
        int idx = 1;
        while (used.Contains(idx)) idx++;
        return idx;
    }
}
