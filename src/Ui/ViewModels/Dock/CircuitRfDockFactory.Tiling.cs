using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Docking;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>Window ▸ Tile: carrying out a <see cref="TilePlan"/> on the live tree.</summary>
public partial class CircuitRfDockFactory
{
    /// <summary>Docks the factory's own layout is built from. Tiling never flattens one of these away,
    /// even when the documents are all that is left in it — capture and Reset Layout look them up.</summary>
    private static readonly HashSet<string> StructuralDockIds =
        new(StringComparer.Ordinal) { "Root", "OuterLayout", "DocumentColumn", "DocumentRow", "FloatingRoot" };

    /// <summary>
    /// Rearranges the docked document area to <paramref name="plan"/>. Returns false, changing nothing,
    /// when the primary strip is not docked in <paramref name="root"/>, or a planned document is neither
    /// docked there nor in one of its floating windows (Merge All Documents brings those home).
    ///
    /// <para><b>It goes through the same three operations a user's own drags do</b>, so the result is an
    /// ordinary split document area — captured into <c>.cws</c>, restored on reopen, collapsed when a
    /// pane's last tab closes:</para>
    /// <list type="number">
    /// <item>every document is MOVED into the primary strip (<c>MoveDockable</c>, the drag path), and
    /// each pane that empties collapses — undoing whatever split was there before;</item>
    /// <item>split wrappers left holding only the primary strip are flattened away, so tiling twice
    /// does not nest a level deeper each time;</item>
    /// <item>the primary strip is replaced in its parent by the new split, with itself as the first
    /// pane — exactly what Dock's own <c>SplitToDock</c> does for one split — and the other panes'
    /// documents are moved into them.</item>
    /// </list>
    /// <para>The primary strip is always the first pane because it is the one that is not collapsable
    /// (the document area must never vanish) and the one every newly opened document lands in.</para>
    /// </summary>
    public bool ArrangeDocuments(IRootDock root, TilePlan plan)
    {
        if (_documentDock is not { } primary) return false;
        if (!IsInTree(root, primary)) return false;

        var all = plan.Panes.SelectMany(p => p).ToList();
        if (all.Any(d => d.Owner is not IDock owner || !(IsInTree(root, owner) || IsFloating(root, owner)))) return false;

        // 1. Everything into the primary strip; the panes that empty give their space back. A document
        //    in a floating window comes home the way a drag back onto the strip brings it.
        bool emptiedFloats = false;
        foreach (var doc in all)
        {
            if (doc.Owner is not IDock source || ReferenceEquals(source, primary)) continue;
            bool floated = !IsInTree(root, source);
            MoveDockable(source, primary, doc, null);
            if (floated) emptiedFloats = true;
            else         CollapseIfEmptied(source);
        }

        // A floating window holding nothing now is closed, not left on screen (a float that also
        // holds a tool panel keeps it, and stays).
        if (emptiedFloats) CloseEmptiedFloatingWindows();

        // 2. No wrapper left around the primary strip that holds nothing else.
        FlattenAround(primary);

        // 3. The new split, if the plan has more than one pane.
        var panes = new List<IDock>();
        if (plan.PaneCount > 1 && primary.Owner is IDock host && host.VisibleDockables is { } siblings)
        {
            int index = siblings.IndexOf(primary);
            if (index < 0) return false;

            // The split takes the primary strip's place, so it takes its share of the parent too.
            double share = primary.Proportion;
            var tree = BuildTileTree(plan, primary, panes);

            bool wasActive = ReferenceEquals(host.ActiveDockable, primary);
            RemoveVisibleDockableAt(host, index);
            InsertVisibleDockable(host, index, tree);
            InitDockable(tree, host);
            if (wasActive) host.ActiveDockable = tree;

            // Every share is set as BOTH Proportion and CollapsedProportion. The primary strip keeps
            // the CollapsedProportion it had in the document column (0.8, typically), Dock's panel
            // restores from that, and side by side rendered 55% / 23% / 23% instead of thirds — with
            // every Proportion in the model reading 1/3 right up to the first layout pass. After a
            // layout pass Dock holds the two equal itself, so equal is what it expects.
            SetShare(tree, share);
            EqualizeShares(tree);

            int p = 0;
            foreach (var paneDocs in plan.Panes)
            {
                var pane = panes[p++];
                if (ReferenceEquals(pane, primary)) continue;
                foreach (var doc in paneDocs)
                    MoveDockable(primary, pane, doc, null);
            }
        }
        else
        {
            panes.Add(primary);
        }

        // Tabs in the planned order — a move appends wherever Dock chooses, which is not always last.
        int at = 0;
        foreach (var paneDocs in plan.Panes)
            if (at < panes.Count) PutInOrder(panes[at++], paneDocs);

        // The front tab of every pane. Focus is the caller's: it knows which document had it.
        for (int i = 0; i < panes.Count && i < plan.Active.Count; i++)
            if (ReferenceEquals(plan.Active[i].Owner, panes[i]))
                panes[i].ActiveDockable = plan.Active[i];

        return true;
    }

    /// <summary>
    /// Builds the split described by <paramref name="plan"/>: the outer split along
    /// <see cref="TilePlan.Outer"/>, each line split the other way when it holds more than one pane.
    /// Every pane but the first is a new, collapsable strip; the first is <paramref name="primary"/>.
    /// <paramref name="panes"/> receives every pane in plan order.
    /// </summary>
    private IDock BuildTileTree(TilePlan plan, IDocumentDock primary, List<IDock> panes)
    {
        var inner = plan.Outer == Orientation.Horizontal ? Orientation.Vertical : Orientation.Horizontal;

        IDockable Pane()
        {
            IDock pane = panes.Count == 0
                ? primary
                : new DocumentDock
                {
                    Id               = $"DocumentsTile{++_tilePaneCount}",
                    Title            = "Documents",
                    IsCollapsable    = true,   // closing its last tab gives the space back (see SplitDocumentRightOf)
                    EmptyContent     = null,   // Dock's default is the string "No documents open" — see EmptyDocumentArea
                    VisibleDockables = CreateList<IDockable>(),
                };
            panes.Add(pane);
            return pane;
        }

        var outerChildren = new List<IDockable>();
        foreach (var line in plan.Lines)
        {
            IDockable node;
            if (line.Count == 1)
            {
                node = Pane();
            }
            else
            {
                var lineChildren = new List<IDockable>();
                for (int i = 0; i < line.Count; i++)
                {
                    if (i > 0) lineChildren.Add(new ProportionalDockSplitter());
                    lineChildren.Add(Pane());
                }
                node = new ProportionalDock
                {
                    Id               = "DocumentTile",
                    Title            = "DocumentTile",
                    Orientation      = inner,
                    IsCollapsable    = true,
                    ActiveDockable   = lineChildren[0],
                    VisibleDockables = CreateList(lineChildren.ToArray()),
                };
            }

            if (outerChildren.Count > 0) outerChildren.Add(new ProportionalDockSplitter());
            outerChildren.Add(node);
        }

        if (outerChildren.Count == 1) return (IDock)outerChildren[0];

        return new ProportionalDock
        {
            Id               = "DocumentTile",
            Title            = "DocumentTile",
            Orientation      = plan.Outer,
            IsCollapsable    = true,
            ActiveDockable   = outerChildren[0],
            VisibleDockables = CreateList(outerChildren.ToArray()),
        };
    }

    private int _tilePaneCount;

    private static void SetShare(IDockable dockable, double share)
    {
        dockable.Proportion          = share;
        dockable.CollapsedProportion = share;
    }

    /// <summary>Equal shares for every child of every split in a tile tree.</summary>
    private static void EqualizeShares(IDock node)
    {
        if (node is not IProportionalDock || node.VisibleDockables is not { } kids) return;
        var parts = kids.Where(k => k is not null and not IProportionalDockSplitter).ToList();
        foreach (var part in parts)
        {
            SetShare(part!, 1.0 / parts.Count);
            if (part is IDock sub) EqualizeShares(sub);
        }
    }

    /// <summary>Reorders <paramref name="pane"/>'s tabs to <paramref name="order"/> (same-dock moves,
    /// the path a tab drag within a strip takes).</summary>
    private void PutInOrder(IDock pane, IReadOnlyList<IDockable> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (pane.VisibleDockables is not { } tabs || i >= tabs.Count) return;
            if (ReferenceEquals(tabs[i], order[i]) || !tabs.Contains(order[i])) continue;
            MoveDockable(pane, pane, order[i], tabs[i]);
        }
    }

    /// <summary>Collapses a pane a move has just emptied, if it is still in the tree and collapsable.
    /// Guarded because a move may already have collapsed it, and collapsing a detached dock again
    /// would act on an owner it is no longer part of.</summary>
    private void CollapseIfEmptied(IDock source)
    {
        if (source.VisibleDockables is { Count: > 0 }) return;
        if (!source.IsCollapsable) return;
        if (source.Owner is not IDock owner || owner.VisibleDockables?.Contains(source) != true) return;
        CollapseDock(source);
    }

    /// <summary>
    /// Removes split wrappers that hold nothing but <paramref name="primary"/> (and splitters), so a
    /// second tile replaces the first rather than nesting inside it. Never removes a structural dock or
    /// one holding a tool panel.
    /// </summary>
    private void FlattenAround(IDocumentDock primary)
    {
        for (int guard = 0; guard < 32; guard++)
        {
            if (primary.Owner is not IProportionalDock wrapper) return;
            if (wrapper is IRootDock || StructuralDockIds.Contains(wrapper.Id ?? "")) return;
            if (ContainsTool(wrapper)) return;
            if (wrapper.VisibleDockables is not { } kids) return;
            if (kids.Any(k => k is not IProportionalDockSplitter && !ReferenceEquals(k, primary))) return;
            if (wrapper.Owner is not IDock outer || outer.VisibleDockables is not { } outerKids) return;

            int index = outerKids.IndexOf(wrapper);
            if (index < 0) return;

            bool wasActive = ReferenceEquals(outer.ActiveDockable, wrapper);
            RemoveVisibleDockable(wrapper, primary);
            SetShare(primary, wrapper.Proportion);
            RemoveVisibleDockableAt(outer, index);
            InsertVisibleDockable(outer, index, primary);
            InitDockable(primary, outer);
            if (wasActive) outer.ActiveDockable = primary;
        }
    }

    /// <summary>True when <paramref name="dock"/> is inside one of <paramref name="root"/>'s floating windows.</summary>
    private static bool IsFloating(IRootDock root, IDockable dock) =>
        root.Windows?.Any(w => w?.Layout is { } wl && IsInTree(wl, dock)) == true;

    /// <summary>True when <paramref name="target"/> is <paramref name="node"/> or docked beneath it
    /// (floating windows are not beneath the root's VisibleDockables, so they never count).</summary>
    private static bool IsInTree(IDockable node, IDockable target)
    {
        if (ReferenceEquals(node, target)) return true;
        if (node is IDock { VisibleDockables: { } kids })
            foreach (var k in kids)
                if (k is not null && IsInTree(k, target)) return true;
        return false;
    }
}
