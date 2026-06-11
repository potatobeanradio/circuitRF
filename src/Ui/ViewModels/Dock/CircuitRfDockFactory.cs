using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dock.Avalonia.Controls;
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
///         ToolDock  projectTreeDock      [65% of left — Project Tree + Library Palette tabs]
///         ToolDock  propertiesDock       [35% of left — Properties + Analyses tabs]
///       ProportionalDock (Vertical)     [80% width — right column]
///         DocumentDock                   [80% of right]
///         ToolDock  Messages             [20% of right]
///
/// Both left ToolDocks are tabbed. Tab switching works correctly because App.axaml applies
/// CrfToolControlCachedContentTemplate (the tool analog of DockDocumentControlCachedContentTemplate):
/// a plain ContentControl replaces DeferredContentControl+ControlRecyclingDataTemplate so
/// Avalonia re-resolves the App DataTemplate on each tab switch, realizing the correct view
/// for the new dockable type rather than retaining the previously realized view.
///
/// Messages is aligned with the document area only (not spanning the left panel).
/// The left column spans the full window height so both left panels share the height.
/// </summary>
public class CircuitRfDockFactory : Factory
{
    // Expose the document dock so WorkspaceViewModel can add/remove tabs.
    private IDocumentDock? _documentDock;
    public IDocumentDock? DocumentDock => _documentDock;

    // Expose the left project-tree ToolDock so launch-pane focus can be applied after layout init.
    private IToolDock? _projectTreeDock;
    public IToolDock? ProjectTreeDock => _projectTreeDock;

    // Expose the dock tools so WorkspaceViewModel can access the message sink and properties panel.
    public MessagesTool?     MessagesTool     { get; private set; }
    public ProjectTreeTool?  ProjectTreeTool  { get; private set; }
    public PropertiesTool?   PropertiesTool   { get; private set; }
    public AnalysesTool?     AnalysesTool     { get; private set; }
    public PaletteTool?      PaletteTool      { get; private set; }

    public CircuitRfDockFactory()
    {
        // Required for tab tear-off: tells Dock what window type to create when a tab
        // is dragged outside the DockControl bounds.  DockControl.HostWindowFactory
        // is also set in WorkspaceWindow code-behind (belt-and-suspenders).
        DefaultHostWindowLocator = () => new HostWindow();
    }

    public override IRootDock CreateLayout()
    {
        // ── Create dockable items ──────────────────────────────────────────────
        ProjectTreeTool = new ProjectTreeTool();
        PropertiesTool  = new PropertiesTool();
        AnalysesTool    = new AnalysesTool();
        PaletteTool     = new PaletteTool();
        var properties  = PropertiesTool;
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

        // ── Left column: two tabbed ToolDocks stacked vertically ─────────────
        //
        // Tab switching works app-wide because App.axaml overrides ToolControl.Template
        // with CrfToolControlCachedContentTemplate (plain ContentControl instead of
        // DeferredContentControl+ControlRecyclingDataTemplate). Avalonia re-resolves the
        // correct App DataTemplate on each tab switch rather than retaining the old view.
        //
        // Layout:
        //   projectTreeDock  (65%) — Project Tree + Library Palette (tabbed)
        //   propertiesDock   (35%) — Properties + Analyses (tabbed)
        var projectTreeDock = new ToolDock
        {
            Id               = "ProjectTreePane",
            Title            = "ProjectTreePane",
            Proportion       = 0.65,
            ActiveDockable   = ProjectTreeTool,
            VisibleDockables = CreateList<IDockable>(ProjectTreeTool, PaletteTool),
            Alignment        = Alignment.Left,
            GripMode         = GripMode.Visible,
        };
        _projectTreeDock = projectTreeDock;

        var propertiesDock = new ToolDock
        {
            Id               = "PropertiesPane",
            Title            = "PropertiesPane",
            Proportion       = 0.35,
            ActiveDockable   = properties,
            VisibleDockables = CreateList<IDockable>(properties, AnalysesTool),
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

    // ── Tab-close hook ─────────────────────────────────────────────────────────

    /// <summary>
    /// Set by WorkspaceViewModel. Called before each CloseDockable with the dockable
    /// being closed. Return true to proceed with the close, false to cancel it.
    /// Null = no hook (always proceed).
    /// </summary>
    public Func<IDockable, Task<bool>>? CloseDockableConfirm { get; set; }

    public override async void CloseDockable(IDockable dockable)
    {
        if (CloseDockableConfirm is not null)
        {
            var proceed = await CloseDockableConfirm(dockable);
            if (!proceed) return; // user cancelled — tab stays open
        }

        // FactoryBase.DockableClosed fires from base.CloseDockable internally.
        base.CloseDockable(dockable);
    }

    /// <summary>
    /// Opens a document tab in the DocumentDock. Accepts any Dock Document subclass
    /// (StubDocument, SchematicDocument, etc.). Called by WorkspaceViewModel.
    /// </summary>
    public void OpenDocument(Document doc)
    {
        if (_documentDock is null) return;
        AddDockable(_documentDock, doc);
        SetActiveDockable(doc);
        SetFocusedDockable(_documentDock, doc);
    }

    /// <summary>
    /// Removes the welcome stub tab synchronously, bypassing the async confirm hook.
    /// Called by RestoreOpenDocuments before re-opening a workspace's saved documents.
    /// No-op if no welcome stub is present.
    /// </summary>
    public void RemoveWelcomeStub()
    {
        if (_documentDock is null) return;
        var stub = _documentDock.VisibleDockables?.OfType<StubDocument>().FirstOrDefault();
        if (stub is not null)
            base.CloseDockable(stub); // bypass async confirm hook — welcome stub is never dirty
    }

    /// <summary>
    /// Full reset — creates fresh tool instances and a welcome stub DocumentDock.
    /// Used by New Workspace to start a completely clean session.
    /// </summary>
    public IRootDock CreateDefaultLayout() => CreateLayout();

    /// <summary>
    /// Geometry-only reset — rebuilds the default proportional skeleton while
    /// re-hosting the EXISTING <see cref="DocumentDock"/> (with all open tabs,
    /// active tab, and selection intact) and the existing tool instances.
    /// Used by View → Reset Layout so document content is never discarded.
    /// Falls back to <see cref="CreateLayout"/> if no layout has been initialized yet.
    /// </summary>
    public IRootDock CreateLayoutPreservingContent()
    {
        // No existing content to preserve — do a full create.
        if (_documentDock is null)
            return CreateLayout();

        // Keep existing tool instances so their VM state (active schematic,
        // workspace binding, etc.) is not reset.
        ProjectTreeTool ??= new ProjectTreeTool();
        PropertiesTool  ??= new PropertiesTool();
        AnalysesTool    ??= new AnalysesTool();
        PaletteTool     ??= new PaletteTool();
        MessagesTool    ??= new MessagesTool();

        var projectTreeDock = new ToolDock
        {
            Id               = "ProjectTreePane",
            Title            = "ProjectTreePane",
            Proportion       = 0.65,
            ActiveDockable   = ProjectTreeTool,
            VisibleDockables = CreateList<IDockable>(ProjectTreeTool, PaletteTool),
            Alignment        = Alignment.Left,
            GripMode         = GripMode.Visible,
        };
        _projectTreeDock = projectTreeDock;

        var propertiesDock = new ToolDock
        {
            Id               = "PropertiesPane",
            Title            = "PropertiesPane",
            Proportion       = 0.35,
            ActiveDockable   = PropertiesTool,
            VisibleDockables = CreateList<IDockable>(PropertiesTool, AnalysesTool),
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

        // Re-use the existing DocumentDock — its documents, active tab, and
        // per-document selection state are all preserved.
        var rightColumn = new ProportionalDock
        {
            Id               = "RightColumn",
            Title            = "RightColumn",
            Orientation      = Orientation.Vertical,
            ActiveDockable   = _documentDock,
            VisibleDockables = CreateList<IDockable>(
                _documentDock,
                new ProportionalDockSplitter(),
                messagesDock),
        };

        var outerLayout = new ProportionalDock
        {
            Id               = "OuterLayout",
            Title            = "OuterLayout",
            Orientation      = Orientation.Horizontal,
            ActiveDockable   = _documentDock,
            VisibleDockables = CreateList<IDockable>(
                leftColumn,
                new ProportionalDockSplitter(),
                rightColumn),
        };

        var root = CreateRootDock();
        root.Id               = "Root";
        root.Title            = "Root";
        root.IsCollapsable    = false;
        root.VisibleDockables = CreateList<IDockable>(outerLayout);
        root.ActiveDockable   = outerLayout;
        root.DefaultDockable  = outerLayout;

        return root;
    }
}
