using System.Collections.Generic;

namespace CircuitRF.Ui.Docking;

// ─────────────────────────────────────────────────────────────────────────────
//  The dock-layout block persisted inside .cws — OUR schema, not the docking
//  library's (brief-dock-layout-persistence.md §2, R-dock-3).
//
//  .cws is a human-readable, long-lived file. A third-party library's serialized
//  object graph is neither: it is opaque to a reader, and a library upgrade can
//  invalidate every saved workspace in the field. A dozen fields of our own cost
//  little and stay stable.
//
//  These are plain POCOs so System.Text.Json round-trips them with the same
//  conventions the rest of .cws already uses (PascalCase, no naming policy).
//  They carry no Avalonia and no Dock types — DockLayoutCapture/the factory are
//  where the Dock model is touched.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Which edge of the shell a tool panel is docked to.</summary>
public static class DockSide
{
    public const string Left   = "Left";
    public const string Right  = "Right";
    public const string Top    = "Top";
    public const string Bottom = "Bottom";

    public static bool IsValid(string? s) =>
        s is Left or Right or Top or Bottom;
}

/// <summary>
/// One tool panel's placement. Identity is the panel's compile-time <see cref="Id"/>
/// (R-dock-1) — never a file path, never a title, never a list index.
/// </summary>
public sealed class CwsDockPanel
{
    /// <summary>Stable compile-time panel id (e.g. "ProjectTree"). See <see cref="DockPanelIds"/>.</summary>
    public string Id { get; set; } = "";

    /// <summary>False when the panel is closed. A closed panel is still recorded so the state is explicit.</summary>
    public bool Open { get; set; } = true;

    /// <summary>One of <see cref="DockSide"/>.</summary>
    public string Side { get; set; } = DockSide.Left;

    /// <summary>Size along the docking axis, as the docking library's proportion (0..1).</summary>
    public double Proportion { get; set; }

    /// <summary>Index of the tabbed group within this side, outermost-first.</summary>
    public int Group { get; set; }

    /// <summary>Tab order within the group.</summary>
    public int Order { get; set; }

    /// <summary>True for the visible tab of its group.</summary>
    public bool Active { get; set; }
}

/// <summary>
/// Size of one whole docking side across its own axis (the left/right column's share of the window
/// width). The split BETWEEN groups on that side lives on each group's own
/// <see cref="CwsDockPanel.Proportion"/>; this is the one number that has nowhere else to live.
/// Only meaningful for <see cref="DockSide.Left"/>/<see cref="DockSide.Right"/> — Top and Bottom
/// groups sit inside the document column and are sized entirely by their own proportion.
/// </summary>
public sealed class CwsDockSide
{
    public string Side       { get; set; } = DockSide.Left;
    public double Proportion { get; set; }
}

/// <summary>One torn-off window: logical geometry (R-dock-7) plus which panels it contains.</summary>
public sealed class CwsFloatingWindow
{
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }

    /// <summary>Panel ids, in tab order.</summary>
    public List<string> Panels { get; set; } = [];

    /// <summary>Id of the visible tab. Null/unknown falls back to the first panel.</summary>
    public string? Active { get; set; }
}

/// <summary>
/// A torn-off DOCUMENT window: logical geometry plus the documents it hosts, by the same
/// workspace-relative key <c>.cws</c>'s own <c>OpenDocuments</c> uses.
///
/// <para>Kept separate from <see cref="CwsFloatingWindow"/> because the two restore completely
/// differently: a tool panel is a compile-time singleton the layout builder can place directly, while
/// a document is opened by the workspace (file I/O, dirty tracking, undo routing) and only then moved
/// into a window. Merging them into one list would hide that difference behind a type check.</para>
///
/// <para>R-dock-2 still holds: this is ARRANGEMENT. A document named here that is not in
/// <c>OpenDocuments</c> is dropped, and a window left with no documents is dropped with it.</para>
/// </summary>
public sealed class CwsFloatingDocumentWindow
{
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }

    /// <summary>Workspace-relative document paths, in tab order.</summary>
    public List<string> Documents { get; set; } = [];

    /// <summary>Path of the visible tab. Null/unknown falls back to the first document.</summary>
    public string? Active { get; set; }
}

/// <summary>
/// One node of the DOCKED document area, when the user has split it into several panes.
///
/// <para><b>Why this exists:</b> <see cref="CwsDockLayout.DocumentOrder"/> records docked documents
/// as one flat list, which can only ever describe a single tab strip. Dragging a document to the edge
/// of the document area splits it into two side-by-side <c>IDocumentDock</c>s — an arrangement the
/// flat list cannot express at all, so it restored as an ordinary tab. Owner-reported (2026-07-30).</para>
///
/// <para>A node is either a SPLIT (<see cref="Children"/> non-empty, <see cref="Orientation"/> set)
/// or a LEAF pane (<see cref="Documents"/> holding workspace-relative keys in tab order). The tree
/// mirrors the docking library's own proportional nesting, so a split-then-split-again arrangement
/// round-trips without a special case.</para>
///
/// <para>R-dock-2 still governs: this is ARRANGEMENT. <c>.cws</c>'s own <c>OpenDocuments</c> decides
/// WHAT is open — a key named here that is not open is dropped, and a pane left with nothing is
/// dropped with it rather than restoring as an empty pane.</para>
/// </summary>
public sealed class CwsDocumentRegion
{
    /// <summary>"Horizontal" or "Vertical" for a split; null for a leaf pane.</summary>
    public string? Orientation { get; set; }

    /// <summary>This node's share of its parent's axis (0..1). 0 means "let the library decide".</summary>
    public double Proportion { get; set; }

    /// <summary>Child nodes, outermost-first. Empty for a leaf.</summary>
    public List<CwsDocumentRegion> Children { get; set; } = [];

    /// <summary>Leaf only: workspace-relative document keys, in tab order.</summary>
    public List<string> Documents { get; set; } = [];

    /// <summary>Leaf only: key of the visible tab. Null/unknown falls back to the first document.</summary>
    public string? Active { get; set; }

    /// <summary>True when this node is a pane rather than a split.</summary>
    public bool IsLeaf => Children.Count == 0;
}

/// <summary>
/// A screen's working area in LOGICAL (DPI-independent) units — recorded alongside the
/// layout per R-dock-8 so restore can tell "the same setup as last time" from "a different
/// setup", and so a bug report about a lost window is diagnosable at all.
/// </summary>
public sealed class CwsScreen
{
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// The versioned dock-layout block (R-dock-4). Absent = default layout, silently.
/// A <see cref="Version"/> the running code does not understand = default layout, reported.
/// Neither is an error.
/// </summary>
public sealed class CwsDockLayout
{
    /// <summary>Bump when a change would make an older build misread a newer file.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Working areas of the screens present when this layout was saved (R-dock-8).</summary>
    public List<CwsScreen> Screens { get; set; } = [];

    /// <summary>Docked and closed tool panels. A panel listed in no collection gets its default placement.</summary>
    public List<CwsDockPanel> Panels { get; set; } = [];

    /// <summary>Per-side column sizes. See <see cref="CwsDockSide"/>.</summary>
    public List<CwsDockSide> Sides { get; set; } = [];

    public List<CwsFloatingWindow> FloatingWindows { get; set; } = [];

    /// <summary>Torn-off document windows. See <see cref="CwsFloatingDocumentWindow"/>.</summary>
    public List<CwsFloatingDocumentWindow> FloatingDocumentWindows { get; set; } = [];

    /// <summary>
    /// Document tab ARRANGEMENT only (R-dock-2) — workspace-relative paths, in tab order.
    /// <c>.cws</c>'s own <c>OpenDocuments</c> stays authoritative for WHAT is open; when the
    /// two disagree, the open list wins and the layout entry is dropped.
    /// </summary>
    public List<string> DocumentOrder { get; set; } = [];

    /// <summary>Workspace-relative path of the visible document tab, or null.</summary>
    public string? ActiveDocument { get; set; }

    /// <summary>
    /// The docked document area's own pane structure — written ONLY when it is actually SPLIT.
    ///
    /// <para>Null (the overwhelmingly common case) means one tab strip, and
    /// <see cref="DocumentOrder"/>/<see cref="ActiveDocument"/> describe it exactly as they always
    /// have. Confining the new structure to the split case keeps every unsplit workspace's block
    /// byte-identical to before, so the new code path cannot regress the ordinary layout.</para>
    ///
    /// <para><b>No <see cref="Version"/> bump:</b> the field is purely additive. An older build
    /// ignores an unknown JSON property and falls back to <see cref="DocumentOrder"/> — the split
    /// flattens back to tabs, which is exactly the old behaviour, not a misread. Bumping the version
    /// would instead make an older build discard the WHOLE layout, which is strictly worse.</para>
    /// </summary>
    public CwsDocumentRegion? DocumentRegion { get; set; }
}

/// <summary>
/// The compile-time identity of every tool panel (R-dock-1). These strings are a FILE FORMAT —
/// they appear verbatim in every saved <c>.cws</c>. Renaming one silently drops that panel's
/// saved placement for every existing workspace; add a new id instead, or migrate explicitly.
/// </summary>
public static class DockPanelIds
{
    public const string ProjectTree = "ProjectTree";
    public const string Palette     = "Palette";
    public const string Properties  = "Properties";
    public const string Analyses    = "Analyses";
    public const string Messages    = "Messages";

    /// <summary>L5b's violations panel. Tabbed with Messages by default — both are "what the tool has
    /// to tell you about this design", and neither is worth its own permanent strip of window.</summary>
    public const string Drc         = "Drc";

    public static readonly string[] All =
    [
        ProjectTree, Palette, Properties, Analyses, Messages, Drc,
    ];
}
