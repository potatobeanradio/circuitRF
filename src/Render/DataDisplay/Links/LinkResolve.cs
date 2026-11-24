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
using RfCore.Loadpull;

namespace CircuitRF.Render.DataDisplay;

public static class LinkResolve
{
    /// <summary>
    /// Chooses the index of every linked pin in <paramref name="doc"/> from where its source sits, in dependency
    /// order, and re-resolves each consumer against <paramref name="sources"/>. A source that is gone, or a cycle,
    /// leaves the pin at its stored index with <see cref="Trace.LinkFrozen"/> set and the reason in
    /// <see cref="Trace.LinkNote"/>. Returns the consumers it re-resolved.
    /// </summary>
    /// <param name="released">False while a source is being DRAGGED: a consumer that is slow by kind
    /// (<see cref="UpdatesOnRelease"/>) is left as it is, and resolved by the call made when the drag ends
    /// (brief-ddl-2 R-ddl2-3).</param>
    public static IReadOnlyList<Trace> Resolve(LinkDocument doc, IPlotDataSources sources, bool released = true)
    {
        var graph = LinkGraph.Build(doc);
        var done  = new List<Trace>(graph.Order.Count);
        foreach (var t in graph.Order)
        {
            if (doc.PlotOf(t) is not { } plot) continue;
            if (!released && UpdatesOnRelease(t)) continue;
            done.Add(t);
            t.FrozenLinks.Clear();
            var notes  = new List<string>();
            bool frozen = false;
            graph.Cycles.TryGetValue(t, out var cycle);
            if (cycle is not null)
            {
                frozen = true;
                notes.Add($"{cycle}: a cycle, so it is drawn where it was.");
            }

            var slots = graph.SlotsOf(t);
            if (t.IsContourTrace)
            {
                // A contour's frequency (brief-ddl-4 R-ddl4-3): a refit, so it never runs while a source is dragged.
                frozen |= ResolveContour(t, plot, slots, cycle is not null, sources, notes);
                if (UpdatesOnRelease(t)) notes.Add("Updates on release.");
                t.LinkNote   = notes.Count > 0 ? string.Join(" ", notes.Distinct(StringComparer.Ordinal)) : null;
                t.LinkFrozen = frozen;
                continue;
            }
            if (LinkGraph.IsSingleCube(t))
            {
                var (f, deferred, synthesis) = ResolveSlice(doc, t, plot, slots, cycle is not null, sources, notes, released);
                if (deferred)
                {
                    // A synthesised read whose surface is not built yet waits for the drag to end (R-ddl3-7).
                    done.Remove(t);
                    t.LinkNote = string.Join(" ", notes.Distinct(StringComparer.Ordinal));
                    continue;
                }
                frozen |= f;
                t.AfterResolve = synthesis;
                TraceResolve.ResolveCubeTrace(t, sources, plot.PlotType, plot.FreqUnits);
            }
            else
            {
                t.ExpressionLinks = ExpressionResolver(doc, t, sources, cycle is not null, notes, () => frozen = true);
                var freqSlot = slots.FirstOrDefault(s => s.SliceIndex == LinkGraph.ExpressionFreqSlot);
                bool hadAxis = t.ExpressionFreqPin is not null;
                if (freqSlot is not null) frozen |= ApplyExpressionFreq(t, freqSlot, cycle is not null, sources, notes);
                TraceResolve.ResolveCubeTrace(t, sources, plot.PlotType, plot.FreqUnits);
                // The frequency list is known only once the expression has been evaluated: a first resolve that found
                // it chooses the index and draws again.
                if (freqSlot is not null && !hadAxis && t.ExpressionFreqPin is not null)
                {
                    frozen |= ApplyExpressionFreq(t, freqSlot, cycle is not null, sources, notes);
                    TraceResolve.ResolveCubeTrace(t, sources, plot.PlotType, plot.FreqUnits);
                }
            }

            // The plot frames the new case as it framed the old one: an autoscaled axis follows, a pinned one stays.
            // Here rather than in each caller, so the window and `render` frame a followed plot the same way.
            plot.Autoscale();
            if (UpdatesOnRelease(t)) notes.Add("Updates on release.");
            t.LinkNote   = notes.Count > 0 ? string.Join(" ", notes.Distinct(StringComparer.Ordinal)) : null;
            t.LinkFrozen = frozen;
        }

        // Summary tables read at a marker (R-ddl3-5): leaf consumers, after every trace the marker could sit on has
        // followed whatever it follows. A marker that is gone leaves the table at the load it last gave.
        foreach (var plot in doc.Tabs.SelectMany(tab => tab.Plots).Distinct())
        {
            if (plot.TableOptimum != TableOptimum.AtMarker || !plot.Traces.Any(x => x.IsSummaryColumn)) continue;
            if (plot.TableMarkerLink is { } id && doc.FindMarker(id) is { } site)
            {
                plot.TableMarkerLinkName = site.Marker.Name;
                if (LoadpullLinks.PositionOf(site, sources) is { } pos) plot.TableMarkerPosition = pos;
            }
            var ds    = sources.SelectedData;
            var views = ds is null ? [] : LoadpullRecognition.FindLoadpullViews(ds);
            SummaryResolve.Rebuild(plot, ds, ContourResolve.GroupFor(views, plot.SummaryLoadpullGroup));
            done.AddRange(plot.Traces.Where(x => x.IsSummaryColumn));
        }
        return done;
    }

    /// <summary>
    /// A consumer whose resolution is too slow to follow a drag frame by frame — a contour refit, an off-grid loadpull
    /// read (DDL-3). Decided by what the consumer IS, never by a timer, so the same plot behaves the same way on a
    /// fast machine and a slow one. Every consumer DDL-1 and DDL-2 make is a cube slice, which follows live.
    /// </summary>
    public static bool UpdatesOnRelease(Trace t) => t.IsContourTrace;

    /// <summary>
    /// The pinned entries of a single-cube consumer, each at the index its source chooses: whether any is frozen, whether
    /// the consumer waits for a drag to end, and the synthesised curve that replaces its own when a loadpull read at a
    /// free load calls for one (brief-ddl-3).
    /// </summary>
    private static (bool Frozen, bool Deferred, Action<Trace, PlotType, FreqUnit>? Synthesis) ResolveSlice(
        LinkDocument doc, Trace t, Plot plot, IReadOnlyList<LinkGraph.Slot> slots, bool inCycle,
        IPlotDataSources sources, List<string> notes, bool released)
    {
        var slice = (AxisSlice[])t.Slice!.Clone();
        DataSet? data = t.SourcePath is { } sp ? sources.DataFor(sp) : null;
        DataCube? cube = data is not null && data.Contains(t.CubeName!) ? data[t.CubeName!] : null;
        Action<Trace, PlotType, FreqUnit>? synthesis = null;
        bool frozen = false;
        foreach (var slot in slots)
        {
            if (slot.SliceIndex < 0) continue;
            var entry = slice[slot.SliceIndex];
            var axis  = cube?.Axes.FirstOrDefault(a => a.Name == entry.AxisName);
            // The pin keeps following the source it names, by id; its name is refreshed to the source's own, so a
            // renamed marker is written back as its new name (overview D1).
            entry = entry with { Link = slot.Id ?? entry.Link, LinkName = slot.Site?.Marker.Name ?? slot.Control?.Name ?? entry.LinkName };
            if (inCycle) t.FrozenLinks.Add(entry.Link ?? entry.LinkName ?? "");
            if (axis is not null && !inCycle && slot.IsSelection)
            {
                // The trial selection (R-ddl4-5): the lowest selected trial in this consumer's own source.
                entry = entry with { Index = SelectionIndex(doc, t, axis, entry.Index, notes) };
            }
            else if (axis is not null && !inCycle)
            {
                var value = ValueOf(slot, sources);
                // A loadpull position on this loadpull's gridPoint: the node, the nearest node, or a synthesised curve.
                if (value.Position is { IsLoadpull: true } pos && entry.AxisName == "gridPoint" && data is not null)
                {
                    slice[slot.SliceIndex] = entry;
                    var kind = LoadpullLinks.Read(t, plot, slice, slot.SliceIndex, pos, value.Name, data, released, notes,
                                                  out int lpIndex, out var lpSynthesis);
                    if (kind == LoadpullLinks.ReadKind.Deferred) return (frozen, true, null);
                    if (kind != LoadpullLinks.ReadKind.NotLoadpull)
                    {
                        slice[slot.SliceIndex] = entry with { Index = lpIndex };
                        synthesis ??= lpSynthesis;
                        continue;
                    }
                }
                var r = LinkMatch.Match(value, axis, entry.Index, data);
                entry = entry with { Index = r.Index };
                if (r.Note is { } n) notes.Add(slot.Error is { } err && r.Kind == LinkMatchKind.Frozen ? err : n);
                if (r.Kind == LinkMatchKind.Frozen) { frozen = true; t.FrozenLinks.Add(entry.Link ?? entry.LinkName ?? ""); }
            }
            slice[slot.SliceIndex] = entry;
        }
        bool renamed = slice.Where((e, i) => e.LinkName != t.Slice![i].LinkName).Any();
        t.Slice = slice;
        // A renamed marker keeps its links (D1); the spec text the card shows follows the new name.
        if (renamed && t.Expression is not null) t.Expression = t.BuildPickerExpression();
        return (frozen, false, synthesis);
    }

    /// <summary>
    /// How an <c>@name</c> operand of a multi-cube expression chooses its index: by name, in the consumer's tab first,
    /// matched by the same rules. An expression has no stored index to fall back to, so a missing source draws at 0
    /// and says so.
    /// </summary>
    private static Func<string, Axis, LinkMatchResult> ExpressionResolver(
        LinkDocument doc, Trace t, IPlotDataSources sources, bool inCycle, List<string> notes, Action markFrozen)
    {
        int tab = doc.TabOf(t);
        DataSet? data = t.SourcePath is { } sp ? sources.DataFor(sp) : null;
        return (name, axis) =>
        {
            LinkMatchResult r;
            if (inCycle) r = new LinkMatchResult(0, LinkMatchKind.Frozen, null);
            else if (LinkDocument.IsSelection(name))
            {
                // No stored index to fall back on: with nothing selected the operand reads trial index 0.
                var local = new List<string>();
                int i = SelectionIndex(doc, t, axis, 0, local);
                notes.AddRange(local);
                r = new LinkMatchResult(i, LinkMatchKind.SameAxis, null);
            }
            else if (doc.FindControlByName(name, tab) is { } control)
                r = LinkMatch.Match(ControlLinkSource.Resolve(control, sources), axis, 0, data);
            else if (!doc.TryFindByName(name, tab, out var site, out var err))
                r = new LinkMatchResult(0, LinkMatchKind.Frozen, err);
            else
                r = LinkMatch.Match(MarkerLinkSource.Resolve(site, sources), axis, 0, data);
            if (r.Note is { } n) notes.Add(n);
            if (r.Kind == LinkMatchKind.Frozen) markFrozen();
            return r;
        };
    }

    /// <summary>
    /// The index of the trial a pin on <paramref name="axis"/> follows (brief-ddl-4 R-ddl4-5): the lowest trial selected
    /// in the consumer's own source, by value on the axis. With several selected the note says which it shows; with
    /// none, the pin stays at <paramref name="stored"/> — the last one it showed — and the note says so.
    /// </summary>
    private static int SelectionIndex(LinkDocument doc, Trace t, Axis axis, int stored, List<string> notes)
    {
        int fallback = Math.Clamp(stored, 0, Math.Max(0, axis.Length - 1));
        string Trial(int i) => axis.Values.Length > i ? LinkMatch.Format(axis.Values[i], axis.Unit) : i.ToString();
        var selected = doc.SelectedTrials?.Invoke(t.SourcePath);
        if (selected is not { Count: > 0 })
        {
            notes.Add($"No trial is selected; showing trial {Trial(fallback)}, the last one selected.");
            return fallback;
        }
        int lowest = selected.Min();
        var value = new LinkValue(LinkDocument.SelectionId, LinkDocument.SelectionId,
            [new LinkCoordinate(t.SourcePath, "", "trial", -1, lowest, "", [])]);
        var r = LinkMatch.Match(value, axis, stored);
        if (r.Note is { } n) notes.Add(n);
        if (r.Kind == LinkMatchKind.Unresolved) return r.Index;
        if (selected.Count > 1)
            notes.Add($"{selected.Count} trials selected; showing trial {Trial(r.Index)}, the lowest.");
        return r.Index;
    }

    /// <summary>
    /// A contour whose frequency follows a link (R-ddl4-3): the index its source chooses on the contour's own frequency
    /// list, and the refit there. Returns whether the link is frozen.
    /// </summary>
    private static bool ResolveContour(Trace t, Plot plot, IReadOnlyList<LinkGraph.Slot> slots, bool inCycle,
                                       IPlotDataSources sources, List<string> notes)
    {
        var cd = t.ContourData!;
        if (t.SourcePath is not { } sp || sources.DataFor(sp) is not { } ds) return false;
        string group = ContourResolve.GroupFor(LoadpullRecognition.FindLoadpullViews(ds), cd.LoadpullGroup);
        if (LoadpullLinks.Surface(ds, group) is not { } surface) return false;
        bool frozen = false;
        if (slots.FirstOrDefault(s => s.SliceIndex == LinkGraph.ContourFreqSlot) is { } slot)
        {
            var axis = new Axis(surface.LeadingAxisName, [.. surface.Frequencies], surface.LeadingAxisUnit);
            cd.FreqLinkName = slot.Site?.Marker.Name ?? slot.Control?.Name ?? cd.FreqLinkName;
            cd.FreqLink   ??= slot.Id;
            if (inCycle) t.FrozenLinks.Add(cd.FreqLink ?? cd.FreqLinkName ?? "");
            else
            {
                var r = LinkMatch.Match(ValueOf(slot, sources), axis, cd.FreqIndex, ds);
                cd.FreqIndex = r.Index;
                if (r.Note is { } n) notes.Add(slot.Error is { } err && r.Kind == LinkMatchKind.Frozen ? err : n);
                if (r.Kind == LinkMatchKind.Frozen) { frozen = true; t.FrozenLinks.Add(cd.FreqLink ?? cd.FreqLinkName ?? ""); }
            }
        }
        ContourResolve.Rebuild(t, surface, plot.PlotType);
        return frozen;
    }

    /// <summary>The frequency an expression follows (R-ddl4-4), on the frequency list its last resolve pinned.</summary>
    private static bool ApplyExpressionFreq(Trace t, LinkGraph.Slot slot, bool inCycle, IPlotDataSources sources,
                                            List<string> notes)
    {
        t.ExpressionFreqLinkName = slot.Site?.Marker.Name ?? slot.Control?.Name ?? t.ExpressionFreqLinkName;
        t.ExpressionFreqLink   ??= slot.Id;
        if (inCycle) { t.FrozenLinks.Add(t.ExpressionFreqLink ?? t.ExpressionFreqLinkName ?? ""); return false; }
        if (t.ExpressionFreqPin?.Freq is not { } axis) return false;
        DataSet? data = t.SourcePath is { } sp ? sources.DataFor(sp) : null;
        var r = LinkMatch.Match(ValueOf(slot, sources), axis, Math.Max(0, t.ExpressionFreqIndex), data);
        t.ExpressionFreqIndex = r.Index;
        if (r.Note is { } n) notes.Add(slot.Error is { } err && r.Kind == LinkMatchKind.Frozen ? err : n);
        if (r.Kind != LinkMatchKind.Frozen) return false;
        t.FrozenLinks.Add(t.ExpressionFreqLink ?? t.ExpressionFreqLinkName ?? "");
        return true;
    }

    private static LinkValue ValueOf(LinkGraph.Slot slot, IPlotDataSources sources)
        => slot.Control is { } control ? ControlLinkSource.Resolve(control, sources)
         : slot.Site is { } site ? MarkerLinkSource.Resolve(site, sources)
         : new LinkValue(slot.Id ?? "", slot.Name, [], Frozen: true);
}
