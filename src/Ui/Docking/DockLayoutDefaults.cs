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
    /// Library column's share of the window width in the two-column preset — the fraction that
    /// arranges exactly <see cref="Views.Palette.PaletteColumnWidth.DefaultGlyphColumns"/> component
    /// glyph columns at the window's own opening width.
    ///
    /// <para>Derived rather than dialled in, because a number that is nearly right here is a palette
    /// with a strip of empty space beside its glyphs and no way to tell from the number itself. It is
    /// the OPENING width only: a proportion is a fraction of the window, so it stops being two glyphs
    /// the moment the window is resized, which is what <see cref="Views.Palette.PaletteColumnPin"/>
    /// exists to hold — including making this exact on the first layout, should the chrome the
    /// measurement below records ever move.</para>
    /// </summary>
    public static readonly double LibraryColumnProportion =
        Views.Palette.PaletteColumnWidth.ProportionFor(
            Views.Palette.PaletteColumnWidth.TargetWidth(PaletteColumnChrome),
            OpeningWindowWidth - OuterSplitterTotal);

    /// <summary>
    /// <c>WorkspaceWindow.axaml</c>'s declared opening width. Restated here because a proportion can
    /// only be turned into pixels against some window size, and this is the one a new workspace
    /// opens at; <c>PaletteColumnWidthTests</c> holds the two together.
    /// </summary>
    internal const double OpeningWindowWidth = 1200.0;

    /// <summary>
    /// The two splitters between the three columns of the two-column preset, at Dock's 4 px default
    /// thickness. Proportions divide up the row LESS its splitters, so they are not part of the pool.
    /// </summary>
    internal const double OuterSplitterTotal = 8.0;

    /// <summary>
    /// What the dock theme puts between the Library column's outer edge and its tile area — measured
    /// at 2 px in the running panel, the same at every window width. Only the opening number depends
    /// on it: <see cref="Views.Palette.PaletteColumnPin"/> reads the real one off the live panel.
    /// </summary>
    internal const double PaletteColumnChrome = 2.0;

    /// <summary>Project Tree's share of the left column when the Library is not tabbed with it.</summary>
    public const double ProjectTreeAloneProportion = 0.466;

    /// <summary>Properties / Analyses' share of the left column in the two-column preset.</summary>
    public const double PropertiesGroupAloneProportion = 0.534;

    /// <summary>
    /// The arrangement for one <see cref="Theming.WindowLayout"/> setting.
    ///
    /// <para>The two "focus" presets are the SAME arrangement — they differ only in which tab of the
    /// Project Tree / Library group is on top, which is not part of a layout at all but of the
    /// caller's follow-up <c>SetActiveDockable</c>. They are listed together here so every caller
    /// (launch, Reset Layout, Fit Windows to Frame) reads one function rather than each deciding for
    /// itself what a setting means.</para>
    /// </summary>
    public static CwsDockLayout For(Theming.WindowLayout preset) => preset switch
    {
        Theming.WindowLayout.ProjectTreeAndLibrary => ProjectTreeAndLibrary(),
        _                                          => Default(),
    };

    /// <summary>
    /// The §2.0 layout: Project Tree + Library tabbed above Properties + Analyses in a left column,
    /// Messages — with DRC and LVS behind it — under the documents.
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
            new CwsDockPanel { Id = DockPanelIds.Drc,         Side = DockSide.Bottom, Group = 0, Order = 1, Active = false, Proportion = MessagesProportion         },
            new CwsDockPanel { Id = DockPanelIds.Lvs,         Side = DockSide.Bottom, Group = 0, Order = 2, Active = false, Proportion = MessagesProportion         },
        ],
    };

    /// <summary>
    /// The shipped default since 2026-08-15: Project Tree above Properties + Analyses on the left,
    /// the Library in its OWN column on the RIGHT of the documents, Messages + DRC + LVS below them.
    ///
    /// <para>Originally transcribed from the owner's own <c>new_layout.cws</c>, which is why most of
    /// these proportions are the untidy numbers a dragged splitter leaves rather than round ones. Its
    /// Library sat beside the documents as a share of the window WIDTH — the same number
    /// <c>DockLayoutCapture.EnumerateSideProportions</c> would record for that arrangement — and
    /// <see cref="LibraryColumnProportion"/> was hand-tuned towards two columns of component symbols
    /// through 2026-08 (0.125 → 0.09 → 0.1 → 0.1125) before being DERIVED from that width instead.</para>
    /// </summary>
    public static CwsDockLayout ProjectTreeAndLibrary() => new()
    {
        Version = CwsDockLayout.CurrentVersion,
        Sides =
        [
            new CwsDockSide { Side = DockSide.Left,  Proportion = LeftColumnProportion    },
            new CwsDockSide { Side = DockSide.Right, Proportion = LibraryColumnProportion },
        ],
        Panels =
        [
            new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left,   Group = 0, Order = 0, Active = true,  Proportion = ProjectTreeAloneProportion    },
            new CwsDockPanel { Id = DockPanelIds.Properties,  Side = DockSide.Left,   Group = 1, Order = 0, Active = true,  Proportion = PropertiesGroupAloneProportion },
            new CwsDockPanel { Id = DockPanelIds.Analyses,    Side = DockSide.Left,   Group = 1, Order = 1, Active = false, Proportion = PropertiesGroupAloneProportion },
            new CwsDockPanel { Id = DockPanelIds.Palette,     Side = DockSide.Right,  Group = 0, Order = 0, Active = true,  Proportion = 1.0                            },
            new CwsDockPanel { Id = DockPanelIds.Messages,    Side = DockSide.Bottom, Group = 0, Order = 0, Active = true,  Proportion = MessagesProportion             },
            new CwsDockPanel { Id = DockPanelIds.Drc,         Side = DockSide.Bottom, Group = 0, Order = 1, Active = false, Proportion = MessagesProportion             },
            new CwsDockPanel { Id = DockPanelIds.Lvs,         Side = DockSide.Bottom, Group = 0, Order = 2, Active = false, Proportion = MessagesProportion             },
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
