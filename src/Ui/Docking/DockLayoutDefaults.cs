using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// The default arrangement, expressed in the SAME schema a saved layout uses.
///
/// <para>This is what makes §2's schema load-bearing rather than decorative: the shell's own default
/// layout, a restored <c>.cws</c> layout, and Hide/Show Dockers' stashed arrangement are all one
/// data type driving one builder (R-dock-10). The default is therefore exercised on every launch,
/// which is worth more than any test of the restore path alone.</para>
/// </summary>
public static class DockLayoutDefaults
{
    /// <summary>Left column's share of the window width.</summary>
    public const double LeftColumnProportion = 0.20;

    /// <summary>Project Tree / Palette group's share of the left column.</summary>
    public const double ProjectTreeGroupProportion = 0.65;

    /// <summary>Properties / Analyses group's share of the left column.</summary>
    public const double PropertiesGroupProportion = 0.35;

    /// <summary>Messages' share of the document column.</summary>
    public const double MessagesProportion = 0.20;

    /// <summary>
    /// The §2.0 layout: Project Tree + Library tabbed above Properties + Analyses in a left column,
    /// Messages under the documents.
    /// </summary>
    public static CwsDockLayout Default() => new()
    {
        Version = CwsDockLayout.CurrentVersion,
        Sides =
        [
            new CwsDockSide { Side = DockSide.Left, Proportion = LeftColumnProportion },
        ],
        Panels =
        [
            new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left,   Group = 0, Order = 0, Active = true,  Proportion = ProjectTreeGroupProportion },
            new CwsDockPanel { Id = DockPanelIds.Palette,     Side = DockSide.Left,   Group = 0, Order = 1, Active = false, Proportion = ProjectTreeGroupProportion },
            new CwsDockPanel { Id = DockPanelIds.Properties,  Side = DockSide.Left,   Group = 1, Order = 0, Active = true,  Proportion = PropertiesGroupProportion  },
            new CwsDockPanel { Id = DockPanelIds.Analyses,    Side = DockSide.Left,   Group = 1, Order = 1, Active = false, Proportion = PropertiesGroupProportion  },
            new CwsDockPanel { Id = DockPanelIds.Messages,    Side = DockSide.Bottom, Group = 0, Order = 0, Active = true,  Proportion = MessagesProportion         },
        ],
    };

    /// <summary>
    /// The collapsed (full-canvas) arrangement for §4A: every tool panel closed, no floating tool
    /// windows, document tabs untouched. Document arrangement is carried over from
    /// <paramref name="from"/> so collapsing never reorders or re-selects a document tab —
    /// "hide the dockers" means the panels, not the application.
    /// </summary>
    public static CwsDockLayout Collapsed(CwsDockLayout? from = null) => new()
    {
        Version = CwsDockLayout.CurrentVersion,
        Panels  = DockPanelIds.All
                    .Select(id => new CwsDockPanel { Id = id, Open = false })
                    .ToList(),
        DocumentOrder  = from?.DocumentOrder is { } order ? new List<string>(order) : [],
        ActiveDocument = from?.ActiveDocument,
    };

    /// <summary>
    /// Fills in a default placement for any panel the given layout does not mention at all — a panel
    /// added in a later build than the <c>.cws</c> was written by. Returns a new instance; the input
    /// is not modified.
    /// </summary>
    public static CwsDockLayout WithMissingPanelsFilled(CwsDockLayout layout)
    {
        var known = new HashSet<string>(
            layout.Panels.Select(p => p.Id)
                  .Concat(layout.FloatingWindows.SelectMany(w => w.Panels)));

        var merged = new CwsDockLayout
        {
            Version                 = layout.Version,
            Screens                 = layout.Screens,
            Panels                  = [.. layout.Panels],
            Sides                   = [.. layout.Sides],
            FloatingWindows         = layout.FloatingWindows,
            FloatingDocumentWindows = layout.FloatingDocumentWindows,
            DocumentOrder           = layout.DocumentOrder,
            ActiveDocument          = layout.ActiveDocument,
            DocumentRegion          = layout.DocumentRegion,
        };
        // NOTE: this is a hand-maintained field-by-field copy — a field added to CwsDockLayout and
        // not added here is silently discarded on every restore, with no error anywhere. That has
        // already happened once (DocumentRegion, 2026-07-30). EveryLayoutField_SurvivesWithMissingPanelsFilled
        // walks the type by reflection so the next omission fails a test instead of a bug report.

        foreach (var d in Default().Panels)
            if (!known.Contains(d.Id))
                merged.Panels.Add(d);

        // A side that gained its first panel this way needs a column size too.
        foreach (var d in Default().Sides)
            if (!merged.Sides.Any(s => s.Side == d.Side))
                merged.Sides.Add(d);

        return merged;
    }
}
