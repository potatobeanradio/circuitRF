using System.Collections.Generic;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Model.Mvvm.Core;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Builds the default §2.0 Dock layout:
///   RootDock
///     ProportionalDock (Horizontal)
///       ProportionalDock (Vertical)     [20% width — left column, full height]
///         ToolDock  Project Tree         [50% of left]
///         ToolDock  Properties           [50% of left]
///       ProportionalDock (Vertical)     [80% width — right column]
///         DocumentDock                   [80% of right]
///         ToolDock  Messages             [20% of right]
///
/// Messages is aligned with the document area only (not spanning the left panel).
/// The left column spans the full window height so Properties fills the freed space.
///
/// RUNTIME NOTE: Dock.Avalonia 12.0.0.2 — verify the style include path in App.axaml
/// and that DockControl can locate the avares:// Dock theme assets. If the dock renders
/// blank panels without chrome, the style path is wrong.
/// </summary>
public class CircuitRfDockFactory : Factory
{
    // Expose the document dock so WorkspaceViewModel can add/remove tabs.
    private IDocumentDock? _documentDock;
    public IDocumentDock? DocumentDock => _documentDock;

    // Expose the dock tools so WorkspaceViewModel can access the message sink.
    public MessagesTool? MessagesTool { get; private set; }
    public ProjectTreeTool? ProjectTreeTool { get; private set; }

    public override IRootDock CreateLayout()
    {
        // ── Create dockable items ──────────────────────────────────────────────
        ProjectTreeTool = new ProjectTreeTool();
        var properties  = new PropertiesTool();
        MessagesTool    = new MessagesTool();
        var welcome     = new StubDocument("Welcome", StubDocument.StubKind.Welcome);

        // ── DocumentDock (center) ─────────────────────────────────────────────
        var documentDock = new DocumentDock
        {
            Id                 = "Documents",
            Title              = "Documents",
            IsCollapsable      = false,
            VisibleDockables   = CreateList<IDockable>(welcome),
            ActiveDockable     = welcome,
        };
        _documentDock = documentDock;

        // ── Left column: Project Tree (top 50%) + Properties (bottom 50%, full height) ─
        var projectTreeDock = new ToolDock
        {
            Id               = "ProjectTreePane",
            Title            = "ProjectTreePane",
            Proportion       = 0.50,
            ActiveDockable   = ProjectTreeTool,
            VisibleDockables = CreateList<IDockable>(ProjectTreeTool),
            Alignment        = Alignment.Left,
            GripMode         = GripMode.Visible,
        };

        var propertiesDock = new ToolDock
        {
            Id               = "PropertiesPane",
            Title            = "PropertiesPane",
            Proportion       = 0.50,
            ActiveDockable   = properties,
            VisibleDockables = CreateList<IDockable>(properties),
            Alignment        = Alignment.Left,
            GripMode         = GripMode.Visible,
        };

        var leftColumn = new ProportionalDock
        {
            Id               = "LeftColumn",
            Title            = "LeftColumn",
            Proportion       = 0.20,
            Orientation      = Orientation.Vertical,
            ActiveDockable   = ProjectTreeTool,
            VisibleDockables = CreateList<IDockable>(
                projectTreeDock,
                new ProportionalDockSplitter(),
                propertiesDock),
        };

        // ── Messages (bottom of right column only — same width as content) ────
        var messagesDock = new ToolDock
        {
            Id               = "MessagesPane",
            Title            = "MessagesPane",
            Proportion       = 0.20,
            ActiveDockable   = MessagesTool,
            VisibleDockables = CreateList<IDockable>(MessagesTool),
            Alignment        = Alignment.Bottom,
            GripMode         = GripMode.Visible,
        };

        // ── Right column: Documents (top 80%) + Messages (bottom 20%) ─────────
        var rightColumn = new ProportionalDock
        {
            Id               = "RightColumn",
            Title            = "RightColumn",
            Orientation      = Orientation.Vertical,
            ActiveDockable   = documentDock,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                messagesDock),
        };

        // ── Outer: left column | right column (horizontal) ───────────────────
        var outerLayout = new ProportionalDock
        {
            Id               = "OuterLayout",
            Title            = "OuterLayout",
            Orientation      = Orientation.Horizontal,
            ActiveDockable   = documentDock,
            VisibleDockables = CreateList<IDockable>(
                leftColumn,
                new ProportionalDockSplitter(),
                rightColumn),
        };

        // ── Root ──────────────────────────────────────────────────────────────
        var root = CreateRootDock();
        root.Id               = "Root";
        root.Title            = "Root";
        root.IsCollapsable    = false;
        root.VisibleDockables = CreateList<IDockable>(outerLayout);
        root.ActiveDockable   = outerLayout;
        root.DefaultDockable  = outerLayout;

        return root;
    }

    /// <summary>
    /// Opens a new stub document tab in the DocumentDock. Called by WorkspaceViewModel
    /// when the user double-clicks an item in the Project Tree.
    /// </summary>
    public void OpenDocument(StubDocument doc)
    {
        if (_documentDock is null) return;
        AddDockable(_documentDock, doc);
        SetActiveDockable(doc);
        SetFocusedDockable(_documentDock, doc);
    }

    /// <summary>
    /// Re-creates the default layout. Called by View → Reset Layout.
    /// The caller must replace WorkspaceViewModel.Layout with the returned IRootDock.
    /// </summary>
    public IRootDock CreateDefaultLayout() => CreateLayout();
}
