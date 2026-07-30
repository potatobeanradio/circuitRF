using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.Docking;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Model.Mvvm.Core;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Builds the Dock layout from a <see cref="CwsDockLayout"/> — the same schema
/// <c>.cws</c> persists and Hide/Show Dockers stashes (brief-dock-layout-persistence.md
/// R-dock-10). There is ONE builder; the §2.0 default arrangement is simply
/// <see cref="DockLayoutDefaults.Default"/> fed through it, so the restore path is exercised on
/// every launch rather than only when a saved layout happens to exist.
///
/// The §2.0 default it produces:
///   RootDock
///     ProportionalDock (Horizontal)
///       ProportionalDock (Vertical)     [20% width — left column, full height]
///         ToolDock  group 0              [65% of left — Project Tree + Library Palette]
///         ToolDock  group 1              [35% of left — Properties + Analyses]
///       ProportionalDock (Vertical)     [document column]
///         DocumentDock
///         ToolDock  Messages             [20% — Bottom side, inside the document column]
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

    // Expose the ToolDock hosting the Project Tree so launch-pane focus can be applied after layout init.
    private IToolDock? _projectTreeDock;
    public IToolDock? ProjectTreeDock => _projectTreeDock;

    // The root produced by the last BuildLayout, so a rebuild can carry over floating DOCUMENT
    // windows that already exist as real OS windows (see CarryOverDocumentWindows).
    private IRootDock? _currentRoot;

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
        // CrfHostWindow neutralizes the OS close box for TOOL tear-offs (whose close path
        // crashes Dock's teardown); document tear-offs still close normally.
        DefaultHostWindowLocator = () => new CrfHostWindow();
    }

    // ── Layout construction ───────────────────────────────────────────────────

    public override IRootDock CreateLayout() =>
        BuildLayout(DockLayoutDefaults.Default(), freshTools: true, preserveDocumentDock: false);

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
    /// </summary>
    public IRootDock CreateLayoutPreservingContent() =>
        BuildLayout(DockLayoutDefaults.Default(), freshTools: false, preserveDocumentDock: true);

    /// <summary>
    /// Rebuilds the shell around an arbitrary arrangement while keeping every open document and
    /// every tool instance. This is the one entry point used by both <c>.cws</c> layout restore and
    /// the Hide/Show Dockers toggle.
    /// </summary>
    /// <param name="state">The arrangement to apply.</param>
    /// <param name="floatingGeometry">
    /// Validates each floating window's saved rectangle against the screens that actually exist
    /// (R-dock-6). Supplied by the caller because screen enumeration is an Avalonia concern; when
    /// null, saved geometry is used as-is (headless/test path).
    /// </param>
    public IRootDock CreateLayoutFromState(
        CwsDockLayout state,
        Func<CwsFloatingWindow, ScreenRect>? floatingGeometry = null) =>
        BuildLayout(state, freshTools: false, preserveDocumentDock: true, floatingGeometry);

    /// <summary>
    /// The single layout builder. Every arrangement in the app — default, restored, collapsed —
    /// comes through here.
    /// </summary>
    private IRootDock BuildLayout(
        CwsDockLayout state,
        bool freshTools,
        bool preserveDocumentDock,
        Func<CwsFloatingWindow, ScreenRect>? floatingGeometry = null)
    {
        // Every tool float is about to be rebuilt from the schema (docked, re-floated, or closed), so
        // the previous root's tool windows must go — otherwise the OS windows outlive the model that
        // owns them and a workspace reopened after a close comes back with a duplicate of every panel
        // that happened to be torn off. Document floats are deliberately untouched (see the method).
        PurgeClosedHostWindows();
        CloseFloatingToolWindows(_currentRoot);

        // ── Tool instances ────────────────────────────────────────────────────
        if (freshTools)
        {
            ProjectTreeTool = new ProjectTreeTool();
            PropertiesTool  = new PropertiesTool();
            AnalysesTool    = new AnalysesTool();
            PaletteTool     = new PaletteTool();
            MessagesTool    = new MessagesTool();
        }
        else
        {
            // Keep existing tool instances so their VM state (active schematic,
            // workspace binding, etc.) is not reset.
            ProjectTreeTool ??= new ProjectTreeTool();
            PropertiesTool  ??= new PropertiesTool();
            AnalysesTool    ??= new AnalysesTool();
            PaletteTool     ??= new PaletteTool();
            MessagesTool    ??= new MessagesTool();
        }

        ITool? ToolFor(string id) => id switch
        {
            DockPanelIds.ProjectTree => ProjectTreeTool,
            DockPanelIds.Palette     => PaletteTool,
            DockPanelIds.Properties  => PropertiesTool,
            DockPanelIds.Analyses    => AnalysesTool,
            DockPanelIds.Messages    => MessagesTool,
            _                        => null,
        };

        // ── DocumentDock ──────────────────────────────────────────────────────
        IDocumentDock documentDock;
        if (preserveDocumentDock && _documentDock is not null)
        {
            documentDock = _documentDock;
        }
        else
        {
            var welcome = new StubDocument("Welcome", StubDocument.StubKind.Welcome);
            documentDock = new DocumentDock
            {
                Id               = "Documents",
                Title            = "Documents",
                IsCollapsable    = false,
                VisibleDockables = CreateList<IDockable>(welcome),
                ActiveDockable   = welcome,
            };
        }
        _documentDock  = documentDock;
        _projectTreeDock = null;

        // A panel the saved layout never heard of (added in a later build) gets its default spot.
        state = DockLayoutDefaults.WithMissingPanelsFilled(state);

        // ── Tool docks, per side ──────────────────────────────────────────────
        List<IDock> BuildSide(string side)
        {
            var docks = new List<IDock>();
            var groups = state.Panels
                .Where(p => p.Open && p.Side == side)
                .GroupBy(p => p.Group)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                var ordered = group.OrderBy(p => p.Order).ToList();
                var tools   = ordered.Select(p => ToolFor(p.Id)).OfType<IDockable>().ToList();
                if (tools.Count == 0) continue;

                var activePanel = ordered.FirstOrDefault(p => p.Active) ?? ordered[0];
                var active      = ToolFor(activePanel.Id) as IDockable ?? tools[0];

                var proportion = ordered[0].Proportion;
                if (proportion is <= 0.0 or > 1.0) proportion = 1.0 / Math.Max(1, groups.Count());

                var toolDock = new ToolDock
                {
                    Id               = $"{side}ToolDock{group.Key}",
                    Title            = $"{side}ToolDock{group.Key}",
                    Proportion       = proportion,
                    ActiveDockable   = active,
                    VisibleDockables = CreateList(tools.ToArray()),
                    Alignment        = AlignmentOf(side),
                    GripMode         = GripMode.Visible,
                };

                if (tools.Contains(ProjectTreeTool!)) _projectTreeDock = toolDock;
                docks.Add(toolDock);
            }
            return docks;
        }

        var leftDocks   = BuildSide(DockSide.Left);
        var rightDocks  = BuildSide(DockSide.Right);
        var topDocks    = BuildSide(DockSide.Top);
        var bottomDocks = BuildSide(DockSide.Bottom);

        double SideProportion(string side, double fallback)
        {
            var s = state.Sides.FirstOrDefault(x => x.Side == side);
            return s is { Proportion: > 0.0 and < 1.0 } ? s.Proportion : fallback;
        }

        // ── Document column: [top docks…] / documents / [bottom docks…] ───────
        var columnChildren = new List<IDockable>();
        foreach (var d in topDocks) { columnChildren.Add(d); columnChildren.Add(new ProportionalDockSplitter()); }
        columnChildren.Add(documentDock);
        foreach (var d in bottomDocks) { columnChildren.Add(new ProportionalDockSplitter()); columnChildren.Add(d); }

        var documentColumn = new ProportionalDock
        {
            Id               = "DocumentColumn",
            Title            = "DocumentColumn",
            Orientation      = Orientation.Vertical,
            ActiveDockable   = documentDock,
            VisibleDockables = CreateList(columnChildren.ToArray()),
        };

        // ── Outer: [left column] | document column | [right column] ──────────
        var outerChildren = new List<IDockable>();

        if (leftDocks.Count > 0)
        {
            outerChildren.Add(BuildColumn("LeftColumn", leftDocks, SideProportion(DockSide.Left, DockLayoutDefaults.LeftColumnProportion)));
            outerChildren.Add(new ProportionalDockSplitter());
        }

        outerChildren.Add(documentColumn);

        if (rightDocks.Count > 0)
        {
            outerChildren.Add(new ProportionalDockSplitter());
            outerChildren.Add(BuildColumn("RightColumn", rightDocks, SideProportion(DockSide.Right, DockLayoutDefaults.LeftColumnProportion)));
        }

        var outerLayout = new ProportionalDock
        {
            Id               = "OuterLayout",
            Title            = "OuterLayout",
            Orientation      = Orientation.Horizontal,
            ActiveDockable   = documentDock,
            VisibleDockables = CreateList(outerChildren.ToArray()),
        };

        // ── Root ──────────────────────────────────────────────────────────────
        var root = CreateRootDock();
        root.Id               = "Root";
        root.Title            = "Root";
        root.IsCollapsable    = false;
        root.VisibleDockables = CreateList<IDockable>(outerLayout);
        root.ActiveDockable   = outerLayout;
        root.DefaultDockable  = outerLayout;

        // ── Floating tool windows ─────────────────────────────────────────────
        foreach (var saved in state.FloatingWindows)
        {
            var tools = saved.Panels.Select(ToolFor).OfType<IDockable>().ToList();
            if (tools.Count == 0) continue;

            var activeId = saved.Active is { } a && saved.Panels.Contains(a) ? a : saved.Panels[0];
            var active   = ToolFor(activeId) as IDockable ?? tools[0];

            var toolDock = new ToolDock
            {
                Id               = "FloatingToolDock",
                Title            = "FloatingToolDock",
                Proportion       = double.NaN,
                ActiveDockable   = active,
                VisibleDockables = CreateList(tools.ToArray()),
                Alignment        = Alignment.Unset,
                GripMode         = GripMode.Visible,
            };

            var winRoot = CreateRootDock();
            winRoot.Id               = "FloatingRoot";
            winRoot.Title            = "FloatingRoot";
            winRoot.VisibleDockables = CreateList<IDockable>(toolDock);
            winRoot.ActiveDockable   = toolDock;
            winRoot.DefaultDockable  = toolDock;

            // R-dock-6 lives entirely in the caller's validator — a floating window's saved
            // rectangle is NEVER trusted straight onto the screen.
            var geom = floatingGeometry?.Invoke(saved)
                       ?? new ScreenRect(saved.X, saved.Y, saved.Width, saved.Height);

            var window = CreateDockWindow();
            window.Id     = "FloatingWindow";
            window.Title  = active.Title ?? "";
            window.X      = geom.X;
            window.Y      = geom.Y;
            window.Width  = geom.Width;
            window.Height = geom.Height;
            window.Layout = winRoot;
            // R-dock-14: this window hosts tools by construction (the loop skips windows with none),
            // so it takes the owned mode — the same one CreateWindowFrom assigns to a drag tear-off,
            // resolved through the same helper so the two paths cannot drift. Ownership governs
            // stacking, not position: the window still went through R-dock-6's validation above.
            window.OwnerMode = OwnerModeFor(toolDock);

            AddWindowWithoutHost(root, window);
        }

        // A torn-off DOCUMENT window is already a real, shown OS window hosting live documents — a
        // rebuild must not orphan it. Carried over whenever the documents themselves are (Reset
        // Layout, a .cws layout restore, the Hide/Show Dockers toggle); a full reset drops them
        // along with every document.
        if (preserveDocumentDock)
            CarryOverDocumentWindows(_currentRoot, root);

        _currentRoot = root;
        return root;
    }

    /// <summary>
    /// Closes the previous root's floating TOOL windows before a new layout replaces it.
    ///
    /// <para><b>Tool floats go, document floats stay</b>, and the asymmetry is deliberate. A tool panel
    /// is an app-level singleton whose placement the layout being built is about to decide, so leaving
    /// the old window open would duplicate the panel. A torn-off document is the user's own work and
    /// survives a workspace switch by design (brief-foreign-documents.md R-fgn-2) — it is carried onto
    /// the new root by <see cref="CarryOverDocumentWindows"/> instead.</para>
    ///
    /// <para>Headless-safe: a window that was built but never presented has no <c>Host</c>, so nothing
    /// here touches Avalonia.</para>
    /// </summary>
    private void CloseFloatingToolWindows(IRootDock? root)
    {
        if (root?.Windows is not { Count: > 0 } windows) return;

        foreach (var window in windows.ToList())
        {
            if (window?.Layout is null) continue;
            if (!ContainsTool(window.Layout)) continue;

            windows.Remove(window);

            var host = window.Host;
            window.Owner = null;
            window.Host  = null;

            // Deregister HERE, from the factory that owns the collection, rather than relying on the
            // host to reach it through window.Factory: that chain is null whenever Dock has already
            // run RemoveWindow, and a MISSED removal leaves a closed Window in HostWindows —
            // which crashes the very next window drag inside
            // Window.SortWindowsByZOrder ("Invalid window at index N", thrown for a null PlatformImpl).
            if (host is not null) HostWindows.Remove(host);

            // CrfHostWindow knows how to close without triggering Dock's crashing CloseWindow
            // cascade; anything else falls back to Dock's own exit.
            if (host is CrfHostWindow crf) crf.CloseForLayoutRebuild();
            else                           host?.Exit();
        }
    }

    /// <summary>
    /// Drops any already-closed window from <see cref="Dock.Model.Core.IFactory.HostWindows"/>.
    ///
    /// <para>Belt-and-braces against an unrecoverable crash rather than a substitute for correct
    /// teardown. <c>Dock.Avalonia.Internal.WindowActivationHelper.ActivateAllWindows</c> — reached on
    /// every floating-window drag — passes <c>HostWindows.OfType&lt;Window&gt;()</c> straight to
    /// <c>Window.SortWindowsByZOrder</c>, which throws <c>ArgumentException("Invalid window at index
    /// N")</c> for any entry whose <c>PlatformImpl</c> is null, i.e. any closed window. One stale
    /// entry therefore takes the whole app down the next time the user moves a panel, with no way to
    /// recover. Cheap to check, and the failure it prevents is total.</para>
    /// </summary>
    public void PurgeClosedHostWindows()
    {
        foreach (var host in HostWindows.ToList())
            if (host is Avalonia.Controls.Window { PlatformImpl: null })
                HostWindows.Remove(host);
    }

    /// <summary>
    /// Moves already-presented floating DOCUMENT windows from the previous root onto the new one.
    ///
    /// <para>Deliberately does NOT go through <see cref="AddWindowWithoutHost"/> or
    /// <c>InitDockWindow</c>: those assign <c>window.Host</c>, and passing null would drop the live
    /// host of a window that is currently on screen — the window would stay visible but stop being
    /// reachable through the model. Only the ownership link needs updating.</para>
    /// </summary>
    private void CarryOverDocumentWindows(IRootDock? previous, IRootDock target)
    {
        if (previous?.Windows is not { Count: > 0 } windows) return;

        foreach (var window in windows.ToList())
        {
            if (window is null) continue;
            if (window.Layout is null) { windows.Remove(window); continue; }
            if (ContainsTool(window.Layout)) continue;   // tool floats are rebuilt from the schema

            // An EMPTY floating window is dropped, never carried and never re-presented.
            //
            // Owner report this fixes: tearing off a tool panel also produced a blank document
            // window, sized and positioned like one recently closed. Root cause: a floating window
            // whose documents are gone can still sit in root.Windows with a non-null (but empty)
            // layout — the close cascade runs through this factory's ASYNC CloseDockable confirm
            // hook, so the window's own removal and the dockable removals do not complete in
            // lockstep. `InitLayout` then runs `IRootDock.ShowWindows`, which presents EVERY window
            // in the list, and the leftover surfaces as a blank window at its old geometry. A
            // floating window with nothing in it is not a window worth keeping.
            if (!HasContent(window.Layout))
            {
                windows.Remove(window);
                var stale = window.Host;
                window.Host = null;
                if (stale is not null) HostWindows.Remove(stale);
                if (stale is CrfHostWindow crf) crf.CloseForLayoutRebuild();
                continue;
            }

            windows.Remove(window);                      // never listed under two roots at once
            target.Windows ??= CreateList<IDockWindow>();
            target.Windows.Add(window);
            window.Owner = target;
        }
    }

    // ── Reopening a closed tool panel ─────────────────────────────────────────

    /// <summary>
    /// The tool instance for a <see cref="DockPanelIds"/> id, or null for an unknown id.
    ///
    /// <para>The instances live on this factory for the session's lifetime, so a panel the user has
    /// CLOSED is still here with all of its state (the Properties inspector's current context, the
    /// tree's filter flags, the Messages log). Reopening one therefore restores the panel the user
    /// had, not a blank replacement.</para>
    /// </summary>
    public ITool? ToolById(string? id) => id switch
    {
        DockPanelIds.ProjectTree => ProjectTreeTool,
        DockPanelIds.Palette     => PaletteTool,
        DockPanelIds.Properties  => PropertiesTool,
        DockPanelIds.Analyses    => AnalysesTool,
        DockPanelIds.Messages    => MessagesTool,
        _                        => null,
    };

    /// <summary>
    /// Locates a tool that is currently shown, whether docked in the shell or in a floating window.
    ///
    /// <para>Deliberately searches the real trees by reference rather than asking
    /// <c>FindRoot</c>/<c>Owner</c>: a closed dockable can keep a stale <c>Owner</c> pointing at the
    /// dock it was removed from, which would report a closed panel as still open — the one answer this
    /// must never get wrong, since it decides between "focus it" and "make a new window for it".</para>
    /// </summary>
    public bool TryFindTool(ITool tool, out IDock? parent, out IDockWindow? window)
    {
        parent = null;
        window = null;
        if (_currentRoot is null) return false;

        if (FindParentDock(_currentRoot, tool) is { } dockedParent)
        {
            parent = dockedParent;
            return true;
        }

        foreach (var w in _currentRoot.Windows ?? Enumerable.Empty<IDockWindow>())
        {
            if (w?.Layout is null) continue;
            if (FindParentDock(w.Layout, tool) is { } floatingParent)
            {
                parent = floatingParent;
                window = w;
                return true;
            }
        }
        return false;
    }

    private static IDock? FindParentDock(IDockable root, IDockable target)
    {
        if (root is not IDock dock || dock.VisibleDockables is null) return null;

        foreach (var child in dock.VisibleDockables)
        {
            if (ReferenceEquals(child, target)) return dock;
            if (child is IDock && FindParentDock(child, target) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Opens <paramref name="tool"/> in a new floating window at <paramref name="geometry"/> and
    /// presents it. Used to bring back a panel the user has closed.
    /// </summary>
    public IDockWindow? FloatTool(ITool tool, ScreenRect geometry)
    {
        if (_currentRoot is null) return null;

        var toolDock = new ToolDock
        {
            Id               = "FloatingToolDock",
            Title            = "FloatingToolDock",
            Proportion       = double.NaN,
            ActiveDockable   = tool,
            VisibleDockables = CreateList<IDockable>(tool),
            Alignment        = Alignment.Unset,
            GripMode         = GripMode.Visible,
        };

        var winRoot = CreateRootDock();
        winRoot.Id               = "FloatingRoot";
        winRoot.Title            = "FloatingRoot";
        winRoot.VisibleDockables = CreateList<IDockable>(toolDock);
        winRoot.ActiveDockable   = toolDock;
        winRoot.DefaultDockable  = toolDock;

        var window = CreateDockWindow();
        window.Id        = "FloatingWindow";
        window.Title     = tool.Title ?? "";
        window.X         = geometry.X;
        window.Y         = geometry.Y;
        window.Width     = geometry.Width;
        window.Height    = geometry.Height;
        window.Layout    = winRoot;
        window.OwnerMode = OwnerModeFor(toolDock);   // same decision as every other float

        // The real AddWindow here (not AddWindowWithoutHost): this window is being opened NOW, so
        // resolving its host immediately is exactly right.
        AddWindow(_currentRoot, window);
        window.Present(false);
        return window;
    }

    /// <summary>True when the tree holds at least one real dockable (not just empty container docks).</summary>
    internal static bool HasContent(IDockable? dockable)
    {
        if (dockable is null) return false;
        if (dockable is not IDock dock) return true;
        if (dock.VisibleDockables is null) return false;

        foreach (var child in dock.VisibleDockables)
            if (HasContent(child)) return true;

        return false;
    }

    /// <summary>
    /// Registers a floating window on the root <b>without</b> creating its host window yet.
    ///
    /// <para><c>FactoryBase.AddWindow</c> calls <c>InitDockWindow(window, owner)</c>, whose
    /// single-argument form eagerly resolves a host — i.e. constructs a real <c>CrfHostWindow</c>,
    /// which needs an Avalonia windowing platform. Building a layout is not the moment that has to
    /// happen: <c>HostAdapter.Present</c> resolves the host lazily when the window is actually shown,
    /// which <c>InitLayout</c> → <c>IRootDock.ShowWindows</c> does in the running app. Deferring it
    /// keeps layout CONSTRUCTION free of a display requirement, which is what lets the whole
    /// build/capture round trip be tested headlessly.</para>
    /// </summary>
    private void AddWindowWithoutHost(IRootDock root, IDockWindow window)
    {
        root.Windows ??= CreateList<IDockWindow>();
        root.Windows.Add(window);
        OnWindowAdded(window);
        InitDockWindow(window, root, hostWindow: null);
    }

    private IProportionalDock BuildColumn(string id, List<IDock> docks, double proportion)
    {
        var children = new List<IDockable>();
        for (int i = 0; i < docks.Count; i++)
        {
            if (i > 0) children.Add(new ProportionalDockSplitter());
            children.Add(docks[i]);
        }

        return new ProportionalDock
        {
            Id               = id,
            Title            = id,
            Proportion       = proportion,
            Orientation      = Orientation.Vertical,
            ActiveDockable   = docks[0],
            VisibleDockables = CreateList(children.ToArray()),
        };
    }

    private static Alignment AlignmentOf(string side) => side switch
    {
        DockSide.Right  => Alignment.Right,
        DockSide.Top    => Alignment.Top,
        DockSide.Bottom => Alignment.Bottom,
        _               => Alignment.Left,
    };

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
    /// R-dock-14: a floating <b>tool</b> window belongs to the workspace window — it stays above its
    /// owner and raises with it on every platform, with no <c>Activated</c> hook to maintain and no
    /// risk of the raise stealing focus (R-dock-15). A torn-off <b>document</b> window is a peer and
    /// is explicitly left unowned. <c>Topmost</c> would be the wrong tool: it would float the panel
    /// above every other application, permanently.
    ///
    /// <para><b>Which owner mode actually does that here — decompiled, not assumed.</b>
    /// <c>HostWindow.ResolveOwnerWindow</c> handles <see cref="DockWindowOwnerMode.RootWindow"/> by
    /// looking for the root dock's own <c>IDockWindow</c>. Our shell's root dock has none — it is
    /// hosted by a <c>DockControl</c> inside <c>WorkspaceWindow</c>, not by a floating dock window —
    /// so that mode resolves to a <b>null owner</b> and would leave tool windows UNOWNED, the exact
    /// opposite of what is wanted. <see cref="DockWindowOwnerMode.Default"/> is the mode that works:
    /// its final fallback resolves <c>Factory.DockControls.First()</c>'s visual root, which IS the
    /// workspace window. <see cref="DockWindowOwnerMode.None"/> is the only mode that reliably
    /// produces a peer, so documents get it explicitly rather than by inheriting a global default.</para>
    ///
    /// <para>Note the trade, since it will be noticed: an owned window is always above its owner, so
    /// the workspace window can no longer be placed on top of a floating tool panel. That is standard
    /// tool-palette behaviour and is what was asked for.</para>
    /// </summary>
    public override IDockWindow? CreateWindowFrom(IDockable dockable)
    {
        var window = base.CreateWindowFrom(dockable);
        if (window is not null)
            window.OwnerMode = OwnerModeFor(dockable);
        return window;
    }

    /// <summary>
    /// The options-taking overload must re-assert the owner mode, not merely inherit it.
    /// <c>FactoryBase.CreateWindowFrom(dockable, options)</c> calls the single-argument form (so the
    /// override above does run) and then applies the options — and
    /// <c>DockWindowOptions.ApplyTo</c> assigns <c>window.OwnerMode = OwnerMode</c>
    /// <b>unconditionally</b>, silently overwriting our decision whenever a caller supplies options.
    /// <c>SplitToWindow</c> — the path both a drag tear-off and this app's own layout restore go
    /// through — is exactly such a caller. Verified by decompiling, after nearly shipping the fix on
    /// one overload only.
    /// </summary>
    public override IDockWindow? CreateWindowFrom(IDockable dockable, DockWindowOptions? options)
    {
        var window = base.CreateWindowFrom(dockable, options);
        if (window is not null)
            window.OwnerMode = OwnerModeFor(dockable);
        return window;
    }

    /// <summary>Tool floats are owned by the shell; document floats are peers. See <see cref="CreateWindowFrom"/>.</summary>
    internal static DockWindowOwnerMode OwnerModeFor(IDockable? dockable) =>
        ContainsTool(dockable) ? DockWindowOwnerMode.Default : DockWindowOwnerMode.None;

    internal static bool ContainsTool(IDockable? dockable)
    {
        if (dockable is null) return false;
        if (dockable is ITool) return true;
        if (dockable is IDock { VisibleDockables: { } children })
            foreach (var child in children)
                if (ContainsTool(child)) return true;
        return false;
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
    /// Closes a dockable immediately, bypassing the async dirty-save confirm hook.
    /// Used by Remove-to-Trash so the tab closes without a "Save before closing?" dialog
    /// (the file is going away — saving would be wrong).
    /// </summary>
    public void ForceCloseDockable(IDockable dockable) => base.CloseDockable(dockable);

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
}
