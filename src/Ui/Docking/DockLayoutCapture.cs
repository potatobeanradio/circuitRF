using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// Walks a live Dock tree into a <see cref="CwsDockLayout"/>. The inverse of
/// <c>CircuitRfDockFactory.BuildLayout</c>.
///
/// <para>Touches only <c>Dock.Model</c> interfaces — no Avalonia — so it is directly testable
/// against hand-built <c>RootDock</c>/<c>ToolDock</c>/<c>Tool</c> instances, the same way this
/// codebase already tests <c>FindAnyDocumentInDock</c>.</para>
/// </summary>
public static class DockLayoutCapture
{
    /// <summary>
    /// Captures the current arrangement.
    /// </summary>
    /// <param name="root">The shell's root dock.</param>
    /// <param name="screens">Current screen working areas, logical units (R-dock-8).</param>
    /// <param name="documentKey">
    /// Maps a document dockable to the key stored in the layout (a workspace-relative path). Return
    /// null for a document with no stable identity (a scratch tab) — it is simply not recorded, which
    /// is correct: R-dock-2 makes the open-document list authoritative for membership anyway.
    /// </param>
    /// <param name="windowGeometry">
    /// Supplies a floating window's LIVE logical geometry. Defaults to the model's own X/Y/W/H, which
    /// Dock updates on move/resize; a caller with access to the host window can pass something more
    /// current.
    /// </param>
    public static CwsDockLayout Capture(
        IRootDock root,
        IReadOnlyList<ScreenRect> screens,
        Func<IDockable, string?>? documentKey = null,
        Func<IDockWindow, ScreenRect>? windowGeometry = null)
    {
        var layout = new CwsDockLayout
        {
            Version = CwsDockLayout.CurrentVersion,
            Screens = screens.Select(s => new CwsScreen { X = s.X, Y = s.Y, Width = s.Width, Height = s.Height }).ToList(),
        };

        // ── Docked tool panels ────────────────────────────────────────────────
        var parentMap = BuildParentMap(root);
        var shellDocuments = FindDocumentDock(root);

        // Two Left columns are two different places, so the group counter is keyed on BOTH — otherwise
        // an inboard panel and an outer one would be told they are in the same group and rebuilt into
        // one column, which is the reported bug in a second form.
        var groupsBySide = new Dictionary<(string Side, bool Inboard), int>();
        foreach (var toolDock in EnumerateToolDocks(root))
        {
            var side = SideOf(toolDock, root);

            // Top and bottom are inboard by construction (the builder has always put them inside the
            // document column), so the flag would carry a distinction that does not exist there.
            bool inboard = side is DockSide.Left or DockSide.Right
                        && shellDocuments is not null
                        && IsInboard(toolDock, shellDocuments, root, parentMap);

            int group = groupsBySide.TryGetValue((side, inboard), out var g) ? g : 0;
            groupsBySide[(side, inboard)] = group + 1;

            int order = 0;
            foreach (var dockable in toolDock.VisibleDockables ?? Enumerable.Empty<IDockable>())
            {
                if (dockable is not ITool tool) continue;
                if (!DockPanelIds.All.Contains(tool.Id)) continue;

                layout.Panels.Add(new CwsDockPanel
                {
                    Id         = tool.Id,
                    Open       = true,
                    Side       = side,
                    Proportion = FiniteProportion(toolDock.Proportion),
                    Group      = group,
                    Order      = order++,
                    Active     = ReferenceEquals(toolDock.ActiveDockable, dockable),
                    Inboard    = inboard,
                });
            }
        }

        // ── Side column sizes ─────────────────────────────────────────────────
        // The left/right column's own width lives on the ProportionalDock that CONTAINS the tool
        // docks, not on any of them — recorded once per side. Keyed on (side, inboard): a side can have
        // BOTH an outer column and an inboard one, at different widths, and they are different columns.
        foreach (var (side, proportion, inboard) in EnumerateSideProportions(root, root))
            if (!layout.Sides.Any(s => s.Side == side && s.Inboard == inboard))
                layout.Sides.Add(new CwsDockSide
                {
                    Side = side, Proportion = FiniteProportion(proportion), Inboard = inboard,
                });

        // ── Floating windows ──────────────────────────────────────────────────
        foreach (var window in root.Windows ?? Enumerable.Empty<IDockWindow>())
        {
            if (window?.Layout is not { } winLayout) continue;

            var panels = new List<string>();
            string? active = null;
            foreach (var toolDock in EnumerateToolDocks(winLayout))
            {
                foreach (var dockable in toolDock.VisibleDockables ?? Enumerable.Empty<IDockable>())
                {
                    if (dockable is not ITool tool) continue;
                    if (!DockPanelIds.All.Contains(tool.Id)) continue;
                    panels.Add(tool.Id);
                    if (ReferenceEquals(toolDock.ActiveDockable, dockable)) active ??= tool.Id;
                }
            }

            var geom = windowGeometry?.Invoke(window)
                       ?? new ScreenRect(window.X, window.Y, window.Width, window.Height);
            geom = new ScreenRect(
                FiniteProportion(geom.X), FiniteProportion(geom.Y),
                FiniteProportion(geom.Width), FiniteProportion(geom.Height));

            if (panels.Count > 0)
            {
                layout.FloatingWindows.Add(new CwsFloatingWindow
                {
                    X = geom.X, Y = geom.Y, Width = geom.Width, Height = geom.Height,
                    Panels = panels,
                    Active = active ?? panels[0],
                });
                continue;
            }

            // No tool panels — a torn-off DOCUMENT window. Its documents are identified by the same
            // key the caller uses for docked tabs, so a scratch tab (no stable identity) and a
            // foreign document (outside this workspace) both resolve to null and are simply not
            // recorded — R-dock-2 and R-fgn-6 hold here for free, with no second guard.
            if (documentKey is null) continue;

            var docs = new List<string>();
            string? activeDoc = null;
            foreach (var docDock in EnumerateDocumentDocks(winLayout))
            {
                foreach (var dockable in docDock.VisibleDockables ?? Enumerable.Empty<IDockable>())
                {
                    if (dockable is null) continue;
                    var key = documentKey(dockable);
                    if (string.IsNullOrEmpty(key)) continue;
                    docs.Add(key!);
                    if (ReferenceEquals(docDock.ActiveDockable, dockable)) activeDoc ??= key;
                }
            }

            if (docs.Count == 0) continue;

            layout.FloatingDocumentWindows.Add(new CwsFloatingDocumentWindow
            {
                X = geom.X, Y = geom.Y, Width = geom.Width, Height = geom.Height,
                Documents = docs,
                Active    = activeDoc ?? docs[0],
            });
        }

        // ── Closed panels ─────────────────────────────────────────────────────
        // Recorded explicitly (Open = false) rather than by omission, so "closed" and "written by an
        // older build that had no such panel" stay distinguishable — the second gets a default
        // placement (DockLayoutDefaults.WithMissingPanelsFilled), the first stays closed.
        var placed = layout.Panels.Select(p => p.Id)
                        .Concat(layout.FloatingWindows.SelectMany(w => w.Panels))
                        .ToHashSet(StringComparer.Ordinal);
        foreach (var d in DockLayoutDefaults.Default().Panels)
            if (!placed.Contains(d.Id))
                layout.Panels.Add(new CwsDockPanel
                {
                    Id = d.Id, Open = false, Side = d.Side,
                    Group = d.Group, Order = d.Order, Proportion = d.Proportion,
                });

        // ── Document arrangement (R-dock-2 — arrangement only) ────────────────
        if (documentKey is not null && FindDocumentDock(root) is { } documentDock)
        {
            foreach (var dockable in documentDock.VisibleDockables ?? Enumerable.Empty<IDockable>())
            {
                var key = documentKey(dockable);
                if (!string.IsNullOrEmpty(key)) layout.DocumentOrder.Add(key!);
            }
            if (documentDock.ActiveDockable is { } activeDoc)
            {
                var key = documentKey(activeDoc);
                if (!string.IsNullOrEmpty(key)) layout.ActiveDocument = key;
            }

            // …and, when the document area has actually been SPLIT into several panes, its pane
            // structure as well. Written only in that case, so an unsplit workspace's block is
            // byte-identical to before this existed.
            layout.DocumentRegion = CaptureDocumentRegion(root, documentKey);
        }

        return layout;
    }

    /// <summary>
    /// The docked document area's pane structure, or null when it is a single tab strip.
    ///
    /// <para><b>Walks DOWN from the root and prunes, rather than walking UP to find a boundary.</b>
    /// Two earlier attempts tried to locate a "document region root" by ascending from a document dock
    /// and stopping at the first tool dock. That is wrong, and a real <c>.cws</c> proved it: dropping a
    /// document against the outer edge splits the whole DOCUMENT COLUMN, so the resulting region
    /// legitimately CONTAINS the Messages tool dock —
    /// <c>Outer[ LeftColumn | Split( DocumentColumn[ Documents, Messages ] | pane ) ]</c>. The ascent
    /// stopped at DocumentColumn, saw one pane, and recorded nothing.</para>
    ///
    /// <para><see cref="BuildRegion"/> already yields null for any branch holding no documents (a tool
    /// column prunes itself, since a Tool has no document key) and collapses a split with one surviving
    /// child. So starting at the root and letting it prune finds the panes wherever they are, with no
    /// boundary to guess at and no special case for tool docks in the middle. Tool placement is not
    /// lost — it is described independently by <see cref="CwsDockLayout.Panels"/>.</para>
    ///
    /// <para><b>Consequence worth knowing:</b> a tool dock that sat INSIDE the split (Messages, above)
    /// is not part of the rebuilt region; it returns to its own recorded side, spanning the document
    /// area rather than one pane of it. The panes come back side by side, which is the thing being
    /// restored.</para>
    /// </summary>
    public static CwsDocumentRegion? CaptureDocumentRegion(IDockable root, Func<IDockable, string?> documentKey)
    {
        var region = BuildRegion(root, documentKey);

        // A single pane is exactly what DocumentOrder already describes. Emitting it anyway would add
        // a second, redundant description of the same thing — two records that can disagree.
        return region is null || region.IsLeaf ? null : region;
    }

    /// <summary>
    /// One node of the region tree.
    ///
    /// <para><b>A pane is identified by what it HOLDS, not by its type — this is the whole subtlety.</b>
    /// Dragging a DOCUMENT to an edge does not produce a second <c>IDocumentDock</c>: decompiling
    /// <c>FactoryBase.CreateSplitLayout</c> shows the non-<c>IDock</c> branch wraps the dragged
    /// dockable in a plain <c>CreateProportionalDock()</c> and adds the document straight into it. So
    /// the new pane is an ordinary <c>ProportionalDock</c> holding a document. Requiring
    /// <c>IDocumentDock</c> here finds only the ORIGINAL strip, collapses the split to one pane, and
    /// silently records nothing — which is exactly how this shipped broken the first time.</para>
    /// </summary>
    private static CwsDocumentRegion? BuildRegion(IDockable node, Func<IDockable, string?> documentKey)
    {
        if (node is not IDock dock || dock.VisibleDockables is null) return null;

        // Does this dock directly hold documents? Then it is a pane, whatever its concrete type.
        var documents = new List<string>();
        foreach (var child in dock.VisibleDockables)
        {
            if (child is null) continue;
            var key = documentKey(child);
            if (!string.IsNullOrEmpty(key)) documents.Add(key!);
        }

        if (documents.Count > 0)
        {
            var leaf = new CwsDocumentRegion
            {
                Proportion = FiniteProportion(dock.Proportion),
                Documents  = documents,
            };
            if (dock.ActiveDockable is { } active && documentKey(active) is { Length: > 0 } activeKey)
                leaf.Active = activeKey;
            return leaf;
        }

        // An IDocumentDock holding nothing this workspace can key (all scratch/foreign) is not a pane
        // worth recording — there would be nothing to put back into it.
        if (node is IDocumentDock) return null;

        var children = new List<CwsDocumentRegion>();
        foreach (var child in dock.VisibleDockables)
        {
            // Splitters carry no state of their own — the proportions live on the panes.
            if (child is null || child is IProportionalDockSplitter) continue;
            if (BuildRegion(child, documentKey) is { } sub) children.Add(sub);
        }

        if (children.Count == 0) return null;
        // A split with one surviving child is that child — nesting adds a level that means nothing.
        if (children.Count == 1) return children[0];

        return new CwsDocumentRegion
        {
            Orientation = (node as IProportionalDock)?.Orientation == Orientation.Vertical ? "Vertical" : "Horizontal",
            Proportion  = FiniteProportion(dock.Proportion),
            Children    = children,
        };
    }

    /// <summary>
    /// Replaces a non-finite number with 0 ("let the library decide") on its way into the block.
    ///
    /// <para><b>This guards a pre-existing hazard, not just the region.</b> Dock uses
    /// <c>double.NaN</c> for "no explicit proportion" and assigns it outright during a split
    /// (<c>CreateSplitLayout</c>: <c>dock.Proportion = double.NaN</c>). NaN fails EVERY range
    /// comparison silently, so it sails through a bounds check — and then System.Text.Json refuses to
    /// write it, which throws inside <c>WriteWorkspaceFile</c>'s layout try/catch and loses the WHOLE
    /// block behind a generic "window layout was not saved" warning that points nowhere near here.
    /// Applied to every number that reaches the block, tool-dock proportions and floating-window
    /// geometry included.</para>
    /// </summary>
    private static double FiniteProportion(double p) => double.IsFinite(p) ? p : 0.0;

    /// <summary>Depth-first walk of every <see cref="IToolDock"/> under <paramref name="dockable"/>.</summary>
    public static IEnumerable<IToolDock> EnumerateToolDocks(IDockable dockable)
    {
        if (dockable is IToolDock td) { yield return td; yield break; }
        if (dockable is not IDock dock || dock.VisibleDockables is null) yield break;

        foreach (var child in dock.VisibleDockables)
        {
            if (child is null) continue;
            foreach (var found in EnumerateToolDocks(child)) yield return found;
        }
    }

    /// <summary>Depth-first walk of every <see cref="IDocumentDock"/> under <paramref name="dockable"/>.</summary>
    public static IEnumerable<IDocumentDock> EnumerateDocumentDocks(IDockable dockable)
    {
        if (dockable is IDocumentDock dd) { yield return dd; yield break; }
        if (dockable is not IDock dock || dock.VisibleDockables is null) yield break;

        foreach (var child in dock.VisibleDockables)
        {
            if (child is null) continue;
            foreach (var found in EnumerateDocumentDocks(child)) yield return found;
        }
    }

    /// <summary>First <see cref="IDocumentDock"/> under <paramref name="dockable"/>, or null.</summary>
    public static IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock dd) return dd;
        if (dockable is not IDock dock || dock.VisibleDockables is null) return null;

        foreach (var child in dock.VisibleDockables)
        {
            if (child is null) continue;
            if (FindDocumentDock(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// A tool dock's side, derived from WHERE IT SITS IN THE TREE — the direct inverse of
    /// <c>CircuitRfDockFactory.BuildLayout</c>, which assembles
    /// <c>Outer(Horizontal): [LeftColumn] | DocumentColumn(Vertical: top… documents …bottom) |
    /// [RightColumn]</c>.
    ///
    /// <para><b>The bug this replaces (owner-reported 2026-07-30).</b> This used to read
    /// <see cref="IToolDock.Alignment"/>. That property records the edge a dockable was dropped
    /// against RELATIVE TO ITS NEIGHBOUR — not which outer column it ended up in. Docking Properties
    /// onto the BOTTOM edge of the Project Tree dock (still in the LEFT column) therefore captured
    /// <c>Side = Bottom</c>, and the restore faithfully put it where Bottom means: under the
    /// documents pane. Reset Layout appeared to "fix" it only because the freshly built docks carry
    /// the Alignment we set ourselves.</para>
    ///
    /// <para>Position is authoritative because it is what the user actually sees, and it stays
    /// correct however Dock chooses to set Alignment during a drag.</para>
    ///
    /// <para><b>The second bug, same shape, owner-reported 2026-08-14.</b> Dropping the Library
    /// BESIDE the documents captured as <c>Bottom</c>, and restored under them. Dock's
    /// <c>CreateSplitLayout</c> does not move the document dock into the outer row — it wraps
    /// documents+tool in a NEW horizontal ProportionalDock that takes the document dock's place
    /// inside <c>DocumentColumn</c>. Both dockables therefore live in the SAME branch of that
    /// vertical column, so step 1 found <c>toolIdx == docIdx</c> and fell out of
    /// <c>toolIdx &lt; docIdx</c> as "Bottom" — a coin flip decided by an operator, not by
    /// position. A container that does not SEPARATE the two says nothing about the side, so it is
    /// now skipped and the search continues outward, where the inner horizontal split answers
    /// Left/Right correctly.</para>
    /// </summary>
    private static string SideOf(IToolDock toolDock, IDockable root)
    {
        var documentDock = FindDocumentDock(root);
        if (documentDock is null) return SideFromAlignment(toolDock);

        var parents = BuildParentMap(root);

        // Ancestor chain of the document dock, nearest-first.
        var docChain = ChainToRoot(documentDock, parents);

        // 1. Same vertical container as the documents => Top or Bottom, by index.
        //    (BuildLayout puts top docks before the document dock and bottom docks after it.)
        foreach (var container in docChain)
        {
            if (container is not IProportionalDock { Orientation: Orientation.Vertical } column) continue;
            if (column.VisibleDockables is not { } children) continue;

            var toolBranch = BranchChildOf(toolDock, column, parents);
            var docBranch  = BranchChildOf(documentDock, column, parents);
            if (toolBranch is null || docBranch is null) continue;
            // Same branch => this container does not separate the two at all; keep looking outward
            // (owner-reported 2026-08-14 — "the second bug" in this method's remarks).
            if (ReferenceEquals(toolBranch, docBranch)) continue;

            var toolIdx = children.IndexOf(toolBranch);
            var docIdx  = children.IndexOf(docBranch);
            if (toolIdx < 0 || docIdx < 0) continue;

            return toolIdx < docIdx ? DockSide.Top : DockSide.Bottom;
        }

        // 2. Otherwise it lives in an outer column => Left or Right, by index against the
        //    document column in the shared horizontal container.
        foreach (var container in docChain)
        {
            if (container is not IProportionalDock { Orientation: Orientation.Horizontal } outer) continue;
            if (outer.VisibleDockables is not { } children) continue;

            var toolBranch = BranchChildOf(toolDock, outer, parents);
            var docBranch  = BranchChildOf(documentDock, outer, parents);
            if (toolBranch is null || docBranch is null) continue;
            if (ReferenceEquals(toolBranch, docBranch)) continue;

            var toolIdx = children.IndexOf(toolBranch);
            var docIdx  = children.IndexOf(docBranch);
            if (toolIdx < 0 || docIdx < 0) continue;

            return toolIdx < docIdx ? DockSide.Left : DockSide.Right;
        }

        // Nothing structural resolved (a tree shape we did not build) — fall back rather than guess.
        return SideFromAlignment(toolDock);
    }

    /// <summary>
    /// Whether <paramref name="toolDock"/> sits INSIDE the documents' own branch — i.e. it is a column
    /// between the outer side column and the document tabs, not part of that outer column.
    ///
    /// <para>The test is one question asked at the OUTERMOST proportional container: does that container
    /// separate the tool from the documents? If it puts them in the same branch, everything that
    /// distinguishes them happens further in, which is exactly what "inboard" means. If it separates
    /// them, the tool is in an outer column.</para>
    ///
    /// <para><b>Why the side alone was not enough.</b> <see cref="SideOf"/> answers Left/Right correctly
    /// for both arrangements — it deliberately walks outward past any container that does not separate
    /// the two (see its own remarks). That is the right answer to "which side", and it is silent on
    /// "which column", which is the part the owner's report is about.</para>
    /// </summary>
    private static bool IsInboard(IToolDock toolDock, IDockable documentDock, IDockable root,
                                  Dictionary<IDockable, IDock> parents)
    {
        // Outermost first — ChainToRoot is nearest-first, so the last proportional dock in it is the
        // one that holds the columns.
        var outermost = ChainToRoot(documentDock, parents).OfType<IProportionalDock>().LastOrDefault();
        if (outermost is null) return false;

        var toolBranch = BranchChildOf(toolDock, outermost, parents);
        var docBranch  = BranchChildOf(documentDock, outermost, parents);
        if (toolBranch is null || docBranch is null) return false;

        return ReferenceEquals(toolBranch, docBranch);
    }

    /// <summary>Last-resort inference for a tree we did not assemble. See <see cref="SideOf"/>.</summary>
    private static string SideFromAlignment(IToolDock toolDock) => toolDock.Alignment switch
    {
        Alignment.Left   => DockSide.Left,
        Alignment.Right  => DockSide.Right,
        Alignment.Top    => DockSide.Top,
        Alignment.Bottom => DockSide.Bottom,
        _                => DockSide.Left,
    };

    /// <summary>child → parent for every dockable under <paramref name="root"/>.</summary>
    /// <summary>
    /// Whether <paramref name="target"/> is still somewhere in <paramref name="root"/>'s tree.
    ///
    /// <para>The question a caller holding a REFERENCE to a dock has to ask before using it: a dock the
    /// user has since collapsed, closed or dragged away is a live object with a stale place in it, and
    /// inserting into one puts a panel somewhere nobody can see.</para>
    /// </summary>
    public static bool Contains(IDockable root, IDockable target)
    {
        if (ReferenceEquals(root, target)) return true;
        if (root is not IDock dock || dock.VisibleDockables is null) return false;

        foreach (var child in dock.VisibleDockables)
            if (child is not null && Contains(child, target)) return true;

        return false;
    }

    private static Dictionary<IDockable, IDock> BuildParentMap(IDockable root)
    {
        var map = new Dictionary<IDockable, IDock>(ReferenceEqualityComparer.Instance as IEqualityComparer<IDockable>
                                                   ?? EqualityComparer<IDockable>.Default);
        void Walk(IDockable d)
        {
            if (d is not IDock dock || dock.VisibleDockables is null) return;
            foreach (var child in dock.VisibleDockables)
            {
                if (child is null) continue;
                map[child] = dock;
                Walk(child);
            }
        }
        Walk(root);
        return map;
    }

    /// <summary>Ancestors of <paramref name="d"/>, nearest first.</summary>
    private static List<IDock> ChainToRoot(IDockable d, Dictionary<IDockable, IDock> parents)
    {
        var chain = new List<IDock>();
        var current = d;
        var guard = 0;
        while (parents.TryGetValue(current, out var parent) && guard++ < 64)
        {
            chain.Add(parent);
            current = parent;
        }
        return chain;
    }

    /// <summary>
    /// The ancestor of <paramref name="d"/> that is a DIRECT child of <paramref name="container"/>
    /// — i.e. which branch of the container <paramref name="d"/> lives in. Null if unrelated.
    /// </summary>
    private static IDockable? BranchChildOf(IDockable d, IDock container, Dictionary<IDockable, IDock> parents)
    {
        var current = d;
        var guard = 0;
        while (guard++ < 64)
        {
            if (!parents.TryGetValue(current, out var parent)) return null;
            if (ReferenceEquals(parent, container)) return current;
            current = parent;
        }
        return null;
    }

    /// <summary>
    /// Yields (side, proportion) for every ProportionalDock that directly hosts tool docks of a
    /// single side — that container's own proportion IS the column width for that side.
    ///
    /// <para>…except when that container is a SPLIT the user made by dropping a panel beside the
    /// documents: there the container spans the whole width (its own proportion is Dock's
    /// "unset" NaN) and the panel's share lives on the tool dock itself. Falling back to the tool
    /// dock's proportion there is what makes a Library dropped at 12% of the width come back at
    /// 12%, rather than at the 20% default column width.</para>
    /// </summary>
    private static IEnumerable<(string Side, double Proportion, bool Inboard)> EnumerateSideProportions(IDockable dockable, IDockable root)
    {
        if (dockable is not IDock dock || dock.VisibleDockables is null) yield break;

        if (dockable is IProportionalDock pd)
        {
            var toolDocks = pd.VisibleDockables!.OfType<IToolDock>().ToList();
            var sides = toolDocks.Select(td => SideOf(td, root)).Distinct().ToList();

            // An INBOARD column gets an entry of its own, flagged, rather than being skipped — see
            // CwsDockSide.Inboard for the bug that came of inferring its width from a panel instead.
            // Flagging rather than sharing the side's entry matters twice over: a side can have both
            // columns at once, and the caller keeps the FIRST entry per key, so an unflagged inboard
            // column would silently replace the outer one's real width.
            var parents = BuildParentMap(root);
            var documents = FindDocumentDock(root);
            bool inboard = toolDocks.Count > 0 && documents is not null
                        && IsInboard(toolDocks[0], documents, root, parents);

            if (sides.Count == 1 && sides[0] is DockSide.Left or DockSide.Right)
            {
                // An inboard column's share of the document row is the CONTAINER's own proportion and
                // nothing else's. The outer column can fall back to its first tool dock, which for a
                // single-dock column is the same number; for an inboard one that fallback would be the
                // vertical share again, so it is better to emit nothing and let the default stand.
                if (!inboard)
                    yield return (sides[0],
                                  pd.Proportion is > 0.0 and < 1.0 ? pd.Proportion : toolDocks[0].Proportion,
                                  false);
                else if (pd.Proportion is > 0.0 and < 1.0)
                    yield return (sides[0], pd.Proportion, true);
            }
        }

        foreach (var child in dock.VisibleDockables)
        {
            if (child is null) continue;
            foreach (var found in EnumerateSideProportions(child, root)) yield return found;
        }
    }
}
