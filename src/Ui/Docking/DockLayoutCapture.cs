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
        var groupsBySide = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var toolDock in EnumerateToolDocks(root))
        {
            var side = SideOf(toolDock);
            int group = groupsBySide.TryGetValue(side, out var g) ? g : 0;
            groupsBySide[side] = group + 1;

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
                    Proportion = toolDock.Proportion,
                    Group      = group,
                    Order      = order++,
                    Active     = ReferenceEquals(toolDock.ActiveDockable, dockable),
                });
            }
        }

        // ── Side column sizes ─────────────────────────────────────────────────
        // The left/right column's own width lives on the ProportionalDock that CONTAINS the tool
        // docks, not on any of them — recorded once per side.
        foreach (var (side, proportion) in EnumerateSideProportions(root))
            if (!layout.Sides.Any(s => s.Side == side))
                layout.Sides.Add(new CwsDockSide { Side = side, Proportion = proportion });

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
        }

        return layout;
    }

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

    private static string SideOf(IToolDock toolDock) => toolDock.Alignment switch
    {
        Alignment.Left   => DockSide.Left,
        Alignment.Right  => DockSide.Right,
        Alignment.Top    => DockSide.Top,
        Alignment.Bottom => DockSide.Bottom,
        _                => DockSide.Left,
    };

    /// <summary>
    /// Yields (side, proportion) for every ProportionalDock that directly hosts tool docks of a
    /// single side — that container's own proportion IS the column width for that side.
    /// </summary>
    private static IEnumerable<(string Side, double Proportion)> EnumerateSideProportions(IDockable dockable)
    {
        if (dockable is not IDock dock || dock.VisibleDockables is null) yield break;

        if (dockable is IProportionalDock pd)
        {
            var sides = pd.VisibleDockables!
                .OfType<IToolDock>()
                .Select(SideOf)
                .Distinct()
                .ToList();

            if (sides.Count == 1 && sides[0] is DockSide.Left or DockSide.Right)
                yield return (sides[0], pd.Proportion);
        }

        foreach (var child in dock.VisibleDockables)
        {
            if (child is null) continue;
            foreach (var found in EnumerateSideProportions(child)) yield return found;
        }
    }
}
