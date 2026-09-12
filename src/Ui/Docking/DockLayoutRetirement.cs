using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// <b>A saved layout naming a panel that no longer exists, made to open correctly</b>
/// (<c>docs/design/revision-control.md</c> §5.10; RC-10 R-rc10-3).
///
/// <para><b>This is the one place the merge can break a workspace somebody already has.</b>
/// <c>RestorePoints</c> and <c>VersionHistory</c> are written into every <c>.cwsuser</c> in existence,
/// and both must now resolve to one panel. Three cases, and the third is the one that needed a type
/// rather than a rename:</para>
/// <list type="number">
///   <item><description>a layout naming ONE of them opens the merged panel where that one was;</description></item>
///   <item><description>a layout naming NEITHER is untouched;</description></item>
///   <item><description><b>a layout naming BOTH collapses to one instance</b> — not two tabs of the
///   same panel, and not an empty pane. The dock builder maps an id to a single tool INSTANCE, so two
///   surviving entries would put one dockable into two docks, which is a tree the docking library has
///   no correct behaviour for.</description></item>
/// </list>
///
/// <para><b>The first mention wins</b>, deliberately: a designer who has both open is most likely
/// looking at whichever is in front, and the alternative — a fixed preference for one of the two ids —
/// would move the panel for half of them for no reason anybody could see.</para>
///
/// <para><b>It runs inside the one layout builder</b>, before <see cref="DockLayoutDefaults"/> fills in
/// panels a saved file never heard of. That ordering matters: filling in first would add the merged
/// panel at its default spot and then find it already placed, so a designer who had the old panel
/// docked somewhere would get a second copy of it somewhere else.</para>
///
/// <para><b>And this is the shape of every future panel retirement</b>, which is why the mapping lives
/// on <see cref="DockPanelIds.Retired"/> rather than here: the next one adds a row, not a type.</para>
/// </summary>
public static class DockLayoutRetirement
{
    /// <summary>
    /// The same arrangement with every retired id rewritten and any duplicate it produced removed.
    /// Returns <paramref name="layout"/> itself when there is nothing to rewrite, which is every
    /// layout saved after this build.
    /// </summary>
    public static CwsDockLayout Apply(CwsDockLayout layout)
    {
        bool touched = layout.Panels.Any(p => DockPanelIds.Retired.ContainsKey(p.Id))
                    || layout.FloatingWindows.Any(w => w.Panels.Any(DockPanelIds.Retired.ContainsKey));
        if (!touched) return layout;

        // The lists are rebuilt and everything else is carried across by reference. A field added to
        // CwsDockLayout and not copied here would be discarded on every restore of an OLD file,
        // silently — the defect DockLayoutDefaults.WithMissingPanelsFilled records having already
        // happened once, which is why that method has a reflection gate and why this one touches as
        // few fields as it can.
        var merged = new CwsDockLayout
        {
            Version                 = layout.Version,
            Screens                 = layout.Screens,
            Sides                   = layout.Sides,
            FloatingDocumentWindows = layout.FloatingDocumentWindows,
            DocumentOrder           = layout.DocumentOrder,
            ActiveDocument          = layout.ActiveDocument,
            DocumentRegion          = layout.DocumentRegion,
        };

        // ONE set across docked panels and floating windows together: the two-panel case includes the
        // designer who tore one of them off, and a panel cannot be docked and floating at once.
        HashSet<string> placed = new(StringComparer.Ordinal);
        HashSet<string> closed = new(StringComparer.Ordinal);

        foreach (var panel in layout.Panels)
        {
            string id = DockPanelIds.Resolve(panel.Id);

            // A CLOSED entry is not a placement and must not consume the id: a file with the
            // restore-point panel closed and the versions panel open would otherwise open on whichever
            // of the two came first in it. It still has to be de-duplicated, or the next capture would
            // write two rows for one panel.
            if (!panel.Open)
            {
                if (closed.Add(id)) merged.Panels.Add(Rename(panel, id));
                continue;
            }

            if (placed.Add(id)) merged.Panels.Add(Rename(panel, id));
        }

        foreach (var window in layout.FloatingWindows)
        {
            List<string> panels = [];
            foreach (string raw in window.Panels)
            {
                string id = DockPanelIds.Resolve(raw);
                if (placed.Add(id)) panels.Add(id);
            }

            // A floating window left with no panels is not a window. Dropping it is what stops the
            // "both were torn off into their own windows" case producing an empty one.
            if (panels.Count == 0) continue;

            string active = DockPanelIds.Resolve(window.Active ?? "");
            merged.FloatingWindows.Add(new CwsFloatingWindow
            {
                X      = window.X,
                Y      = window.Y,
                Width  = window.Width,
                Height = window.Height,
                Panels = panels,
                Active = panels.Contains(active) ? active : panels[0],
            });
        }

        // A panel that ended up open somewhere — docked or floating — no longer needs its closed row.
        // That row is what the schema uses to say "known about and not shown", and both at once is a
        // contradiction the capture would then write back out.
        merged.Panels.RemoveAll(p => !p.Open && placed.Contains(p.Id));
        return merged;
    }

    /// <summary>The same entry under a different id. A field-by-field copy because the schema types are
    /// mutable classes rather than records — and because mutating the caller's layout would rewrite a
    /// default arrangement that other callers share.</summary>
    private static CwsDockPanel Rename(CwsDockPanel panel, string id) => new()
    {
        Id         = id,
        Open       = panel.Open,
        Side       = panel.Side,
        Proportion = panel.Proportion,
        Group      = panel.Group,
        Order      = panel.Order,
        Active     = panel.Active,
        Inboard    = panel.Inboard,
        AutoHidden = panel.AutoHidden,
    };
}
