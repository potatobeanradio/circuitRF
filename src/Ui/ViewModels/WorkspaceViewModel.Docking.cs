using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  Dock-layout persistence and the Hide/Show Dockers toggle
//  (brief-dock-layout-persistence.md).
//
//  Two rules govern everything here and are worth stating before the code:
//
//  R-dock-5 — a layout problem NEVER prevents a workspace from opening. Every restore
//  path below falls back to the default layout and reports; none of them can throw
//  into the workspace-open sequence.
//
//  R-dock-9 — the collapsed (Hide Dockers) arrangement is SESSION state, never .cws
//  state. A workspace saved while collapsed reopens EXPANDED with its real arrangement
//  intact; nobody wants to reopen a project and wonder where their panels went.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WorkspaceViewModel
{
    // ---- Hide/Show Dockers (§4A) --------------------------------------------

    /// <summary>
    /// The arrangement in force before the current collapse, held so §4A's toggle can restore it
    /// EXACTLY (R-dock-11: sizes, tab selections and floating geometry all come back as they were).
    /// Uses §2's schema rather than a second representation (R-dock-10), which is why the toggle
    /// exercises the persistence code on every use.
    ///
    /// <para>Session state, deliberately: never written to <c>.cws</c>, and it survives a workspace
    /// switch within a session because hiding the dockers is a view preference, not a property of
    /// the design.</para>
    /// </summary>
    private CwsDockLayout? _preCollapseLayout;

    [ObservableProperty] private bool _dockersCollapsed;

    /// <summary>Menu label — tells the user which way the next press will go.</summary>
    public string DockersMenuHeader => DockersCollapsed ? "Show Dockers" : "Hide Dockers";

    partial void OnDockersCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(DockersMenuHeader));
        DockersCollapsedChanged?.Invoke();
    }

    /// <summary>Raised when <see cref="DockersMenuHeader"/> changes, so the macOS NativeMenu item
    /// (which is not part of any visual tree and does not re-evaluate bindings) can be relabelled.</summary>
    public event Action? DockersCollapsedChanged;

    /// <summary>
    /// §4A: collapse every tool dock so the document area fills the window; press again to restore
    /// the previous arrangement exactly. Floating TOOL windows collapse with the docked panels —
    /// a toggle that leaves a floating Messages panel covering the canvas has not done its job.
    /// Document tabs, the menu bar and the status bar all stay: "hide the dockers" means the panels,
    /// not the application.
    /// </summary>
    [RelayCommand]
    private void HideShowDockers()
    {
        try
        {
            if (!DockersCollapsed)
            {
                _preCollapseLayout = CaptureDockLayout();
                ApplyDockLayout(DockLayoutDefaults.Collapsed(_preCollapseLayout));
                DockersCollapsed = true;
            }
            else
            {
                ApplyDockLayout(_preCollapseLayout ?? DockLayoutDefaults.Default());
                _preCollapseLayout = null;
                DockersCollapsed = false;
            }
        }
        catch (Exception ex)
        {
            // Same rule as restore: a layout operation never leaves the user stuck.
            Messages.Warning($"Could not toggle the dockers: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-applies the collapsed state after the layout tree has been rebuilt for some other reason
    /// (a workspace switch, Reset Layout). R-dock-9: the toggle is a view preference and survives a
    /// workspace switch within a session.
    /// </summary>
    private void ReapplyCollapsedStateIfNeeded()
    {
        if (!DockersCollapsed) return;
        // Capture what was just applied — after a workspace switch that is the NEW workspace's own
        // arrangement, which is what "Show Dockers" must restore to, not the previous workspace's.
        _preCollapseLayout = CaptureDockLayout() ?? _preCollapseLayout;
        ApplyDockLayout(DockLayoutDefaults.Collapsed(_preCollapseLayout));
    }

    // ---- Reopening a tool panel (View menu) ----------------------------------

    /// <summary>Default size for a tool panel opened from the View menu, in logical units.</summary>
    private const double ReopenedPanelWidth  = 380;
    private const double ReopenedPanelHeight = 520;

    /// <summary>
    /// View ▸ Project Tree / Library / Properties / Analyses / Messages.
    ///
    /// <para>Focuses the panel if it is showing anywhere — docked in the shell or in a floating
    /// window — and otherwise opens it in a new floating window. Closing a tool panel used to strand
    /// it: nothing brought it back short of View ▸ Reset Layout, which also discards every other
    /// panel placement the user had set up.</para>
    ///
    /// <para>The tool INSTANCE survives being closed (the factory holds it for the session), so this
    /// restores the panel the user had — the Properties inspector's context, the tree's filters, the
    /// Messages log — rather than a blank replacement.</para>
    /// </summary>
    [RelayCommand]
    private void ShowToolPanel(string? panelId)
    {
        if (_factory.ToolById(panelId) is not { } tool) return;

        try
        {
            if (_factory.TryFindTool(tool, out var parent, out var window))
            {
                if (parent is not null)
                {
                    _factory.SetActiveDockable(tool);
                    _factory.SetFocusedDockable(parent, tool);
                }

                // A panel in a floating window needs THAT window brought forward, not the shell's.
                if (window?.Host is Window host) host.Activate();
                else                             ShellWindow()?.Activate();
                return;
            }

            var placer = new FloatingWindowPlacer(CurrentScreens(), sameConfiguration: false);
            _factory.FloatTool(tool, placer.Place(DefaultReopenedPanelRect()));
        }
        catch (Exception ex)
        {
            Messages.Warning($"Could not open the {tool.Title} panel: {ex.Message}");
        }
    }

    /// <summary>
    /// Where a reopened panel goes when it has no remembered position: offset from the shell window so
    /// it lands over the app rather than at the screen origin, then validated like any other floating
    /// window (R-dock-6) so it can never open somewhere unreachable.
    /// </summary>
    private ScreenRect DefaultReopenedPanelRect()
    {
        var shell = ShellWindow();
        if (shell is null) return new ScreenRect(80, 80, ReopenedPanelWidth, ReopenedPanelHeight);

        var scaling = shell.Screens is { } screens
            ? AvaloniaScreenSource.ScalingAtDevicePoint(shell.Position.X, shell.Position.Y, screens)
            : 1.0;

        return new ScreenRect(
            ScreenPlacement.DeviceToLogical(shell.Position.X, scaling) + 96,
            ScreenPlacement.DeviceToLogical(shell.Position.Y, scaling) + 96,
            ReopenedPanelWidth,
            ReopenedPanelHeight);
    }

    // ---- Capture / apply -----------------------------------------------------

    /// <summary>Current screens' working areas in logical units; empty when no display is available.</summary>
    private IReadOnlyList<ScreenRect> CurrentScreens()
    {
        var screens = ShellWindow()?.Screens;
        return AvaloniaScreenSource.WorkingAreas(screens);
    }

    private Window? ShellWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.OfType<Views.WorkspaceWindow>().FirstOrDefault()
            : null;

    /// <summary>
    /// Snapshots the live arrangement. Returns null only when there is no root dock to read.
    /// </summary>
    internal CwsDockLayout? CaptureDockLayout()
    {
        if (Layout is not IRootDock root) return null;

        var screens = CurrentScreens();
        var shell   = ShellWindow()?.Screens;

        return DockLayoutCapture.Capture(
            root,
            screens,
            documentKey: DocumentKeyFor,
            windowGeometry: w => LiveGeometryOf(w, shell));
    }

    /// <summary>
    /// A floating window's geometry as it is on screen RIGHT NOW, in logical units.
    ///
    /// <para><b>The refresh is the whole point.</b> <c>IDockWindow.X/Y/Width/Height</c> are not live:
    /// they hold whatever <c>HostAdapter.Present</c> last wrote, and the only thing that pulls the real
    /// values back out of the host is <c>IDockWindow.Save()</c>. Dock calls that for drags IT drives,
    /// but dragging a floating window by its ordinary OS title bar never routes through Dock at all —
    /// so reading the model alone records where the window was first PLACED, not where the user left
    /// it, and a moved-then-saved panel comes back at its old position. Reported by the owner; the
    /// original capture had exactly this bug.</para>
    ///
    /// <para><c>Save()</c> is a no-op when the window has no host (a model built but never presented,
    /// as in every headless test), which is why it can be called unconditionally. It also reads NORMAL
    /// bounds rather than the maximized rect, so a maximized panel restores at a usable size.</para>
    /// </summary>
    private static ScreenRect LiveGeometryOf(IDockWindow window, Screens? screens)
    {
        try { window.Save(); }
        catch { /* best effort — a stale rectangle is far better than a failed capture */ }

        return AvaloniaScreenSource.ToLogical(window.X, window.Y, window.Width, window.Height, screens);
    }

    /// <summary>
    /// Rebuilds the shell around <paramref name="state"/>, keeping every open document and every
    /// tool instance. Floating windows go through R-dock-6 validation on the way.
    /// </summary>
    /// <param name="placer">
    /// Shared placement state, so the cascade covers tool AND document windows in one restore rather
    /// than letting two independent passes land windows on top of each other. Created fresh when null.
    /// </param>
    private void ApplyDockLayout(CwsDockLayout state, FloatingWindowPlacer? placer = null)
    {
        placer ??= FloatingWindowPlacer.For(state, CurrentScreens());

        // R-dock-2: the open list decides membership, so the builder is told which of the region's
        // documents are actually open. A pane whose documents are all gone is never built, rather
        // than restoring as a blank half-window the user cannot dismiss.
        var wsDirForRegion = CurrentWorkspacePath is { } cwsForRegion
            ? Path.GetDirectoryName(cwsForRegion)
            : null;

        var newLayout = _factory.CreateLayoutFromState(
            state,
            w => placer.Place(w.X, w.Y, w.Width, w.Height),
            wsDirForRegion is null ? null : key => ResolveOpenDocument(key, wsDirForRegion) is not null);
        // InitLayout also executes IRootDock.ShowWindows (confirmed by decompiling
        // FactoryBase.InitLayout), so the floating windows this arrangement re-created are presented
        // here — calling ShowWindows again afterwards would present each of them twice.
        _factory.InitLayout(newLayout);
        Layout = newLayout;

        // CreateLayoutFromState keeps the tool INSTANCES, so nothing that holds a reference to them
        // needs re-wiring — but the containers changed, so the two subscriptions keyed on the
        // container/tool identity are refreshed, exactly as Reset Layout already does.
        _factory.PaletteTool?.SetPlacementService(PlacementService);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        RestoreSplitDocumentPanes(wsDirForRegion);
    }

    /// <summary>
    /// Moves each restored document into the pane the saved layout put it in.
    ///
    /// <para>Every reopened document starts in the primary dock, because that is the one
    /// <c>BuildLayout</c> preserved with all its tabs. The builder created the other panes but left
    /// them empty on purpose — it has no way to resolve a document key to a document, and no business
    /// doing so. This is the same restore-then-move shape <see cref="RestoreFloatingDocumentWindows"/>
    /// already uses for torn-off windows, and it reuses <c>IFactory.MoveDockable</c> for the same
    /// reason: it is the path a user's own drag takes.</para>
    ///
    /// <para>Never throws. A pane that cannot be populated leaves its documents as ordinary tabs in
    /// the primary strip — a usable outcome, per R-dock-5.</para>
    /// </summary>
    private void RestoreSplitDocumentPanes(string? workspaceDir)
    {
        var panes = _factory.RestoredDocumentPanes;
        if (panes.Count < 2 || workspaceDir is null) return;
        if (_factory.DocumentDock is not { } primary) return;

        // Pane 0 IS the primary dock — its documents are already there and must not be moved.
        for (int i = 1; i < panes.Count; i++)
        {
            var pane = panes[i];
            try
            {
                IDockable? active = null;
                foreach (var key in pane.Documents)
                {
                    if (ResolveOpenDocument(key, workspaceDir) is not { } doc) continue;
                    if (!ReferenceEquals(doc.Owner, primary)) continue;

                    _factory.MoveDockable(primary, pane.Dock, doc, null);
                    if (pane.Active is { } a && string.Equals(a, key, StringComparison.OrdinalIgnoreCase))
                        active = doc;
                }

                active ??= pane.Dock.VisibleDockables?.FirstOrDefault();
                if (active is not null) _factory.SetActiveDockable(active);
            }
            catch (Exception ex)
            {
                Messages.Warning($"A split document pane could not be restored; its documents stay in the main tab strip. ({ex.Message})");
            }
        }
    }

    /// <summary>
    /// Workspace-relative key for a document, matching the form <c>.cws</c>'s own
    /// <c>OpenDocuments</c> uses so the two can be reconciled (R-dock-2). Null for a document with
    /// no stable identity (a scratch tab) or one outside the workspace.
    /// </summary>
    private string? DocumentKeyFor(IDockable dockable)
    {
        if (CurrentWorkspacePath is not { } cws) return null;
        var wsDir = Path.GetDirectoryName(cws);
        if (wsDir is null) return null;

        string? abs = dockable switch
        {
            SchematicDocument sd            => sd.FilePath,
            SymbolEditorDocument syed       => syed.ViewModel.CurrentSymbolPath,
            CellParameterEditorDocument cpd => Path.GetDirectoryName(cpd.ViewModel.EditModel.CcellPath),
            DataDisplayDocument dd          => dd.FilePath,
            LayoutDocument lad              => lad.FilePath,
            TechDocument td                 => td.FilePath,
            _                               => null,
        };

        if (abs is null || WorkspaceRootFinder.IsOutside(abs, wsDir)) return null;
        try   { return Path.GetRelativePath(wsDir, abs); }
        catch { return null; }
    }

    // ---- Torn-off document windows -------------------------------------------

    /// <summary>
    /// Closes torn-off documents that BELONG to the workspace being closed or switched away from.
    ///
    /// <para><b>Owner report this fixes:</b> a torn-off document survived a workspace close, so
    /// reopening that workspace showed the same file in two windows.</para>
    ///
    /// <para><b>This narrows brief-foreign-documents.md R-fgn-2, and only where it was wrong.</b> That
    /// rule ("a workspace switch replaces the contents of the WINDOW it happens in; other windows are
    /// not affected") was implemented as "every torn-off document survives" — a reading the original
    /// completion note explicitly flagged for owner confirmation. The principle behind it is sound and
    /// is kept: a switch performed in the main window has no business reaching into a separate one. But
    /// it only applies to a document that is genuinely not the workspace's. A document whose file lives
    /// INSIDE the workspace is that workspace's own, tear-off being presentation only (R-fgn-1) — it
    /// closes with it, exactly as its docked sibling always has. A FOREIGN document (opened from
    /// outside via File ▸ Open) still survives and becomes foreign to whatever opens next, which is
    /// what R-fgn-2 was actually protecting.</para>
    ///
    /// <para>Scratch documents have no path and therefore belong to no workspace; they survive, as
    /// before. Dirty work is not silently discarded: <see cref="HasAnyDirtyWork"/> and
    /// <see cref="PromptSaveBeforeClose"/> count these same documents (see their own note on what
    /// <c>includeFloated: false</c> now means).</para>
    /// </summary>
    /// <param name="cwsPath">The <c>.cws</c> of the workspace being left. Null = nothing belongs to it.</param>
    private void CloseFloatedDocumentsOwnedByWorkspace(string? cwsPath)
    {
        if (cwsPath is null || Path.GetDirectoryName(cwsPath) is not { } wsDir) return;

        foreach (var dockable in _openDocsByPath.Values.ToList())
        {
            if (IsDockableDocked(dockable)) continue;          // handled by the ordinary teardown
            if (!BelongsToWorkspace(dockable, wsDir)) continue; // foreign — not this workspace's to close

            try { _factory.ForceCloseDockable(dockable); }
            catch (Exception ex)
            {
                Messages.Warning($"A torn-off document window could not be closed: {ex.Message}");
            }
        }
    }

    /// <summary>True when the document's own file lives inside <paramref name="workspaceDir"/>.</summary>
    internal static bool BelongsToWorkspace(IDockable dockable, string workspaceDir)
    {
        string? abs = dockable switch
        {
            SchematicDocument sd            => sd.FilePath,
            SymbolEditorDocument syed       => syed.ViewModel.CurrentSymbolPath,
            CellParameterEditorDocument cpd => Path.GetDirectoryName(cpd.ViewModel.EditModel.CcellPath),
            DataDisplayDocument dd          => dd.FilePath,
            LayoutDocument lad              => lad.FilePath,
            TechDocument td                 => td.FilePath,
            _                               => null,
        };

        return abs is not null && !WorkspaceRootFinder.IsOutside(abs, workspaceDir);
    }

    /// <summary>
    /// Whether a floated document counts for a workspace-switch dirty check. Mirrors
    /// <see cref="CloseFloatedDocumentsOwnedByWorkspace"/> exactly — anything that switch will CLOSE
    /// must be something the switch first OFFERS TO SAVE, or unsaved work vanishes silently.
    /// </summary>
    internal bool FloatedDocumentClosesWithWorkspace(IDockable dockable) =>
        CurrentWorkspacePath is { } cws &&
        Path.GetDirectoryName(cws) is { } wsDir &&
        BelongsToWorkspace(dockable, wsDir);

    /// <summary>
    /// Re-floats the documents a saved layout says were torn off. Runs AFTER
    /// <see cref="ApplyDockLayout"/>, because that rebuilds the whole tree onto a fresh root: a
    /// window created before the rebuild would be orphaned by it.
    ///
    /// <para>The heavy lifting is <c>IFactory.SplitToWindow</c> — the very code path a user's own
    /// drag tear-off takes (remove from owner → <c>CreateWindowFrom</c> → <c>AddWindow</c> → set
    /// geometry → present → focus). Reusing it rather than hand-assembling a window model is what
    /// makes the restored window behave identically to a dragged one, owner mode included.</para>
    ///
    /// <para>R-dock-2 throughout: a document named here that is not actually open is skipped, and a
    /// window left with nothing simply never appears. Never throws — a float that cannot be
    /// reconstructed leaves its documents as ordinary docked tabs, which is a strictly usable
    /// outcome.</para>
    /// </summary>
    private void RestoreFloatingDocumentWindows(CwsDockLayout layout, FloatingWindowPlacer placer)
    {
        if (layout.FloatingDocumentWindows.Count == 0) return;
        if (_factory.DocumentDock is not { } shellDock) return;
        if (CurrentWorkspacePath is not { } cws) return;
        if (Path.GetDirectoryName(cws) is not { } wsDir) return;

        bool floatedAny = false;

        foreach (var saved in layout.FloatingDocumentWindows)
        {
            // Membership is the open list's call, not the layout's.
            var docs = saved.Documents
                .Select(key => ResolveOpenDocument(key, wsDir))
                .OfType<IDockable>()
                .Where(d => ReferenceEquals(d.Owner, shellDock))
                .ToList();

            if (docs.Count == 0) continue;

            try
            {
                var rect = placer.Place(saved.X, saved.Y, saved.Width, saved.Height);

                // SplitToWindow floats exactly one dockable; the rest of the tabs move in after it.
                _factory.SplitToWindow(shellDock, docs[0], rect.X, rect.Y, rect.Width, rect.Height, null);
                floatedAny = true;

                if (FindWindowHosting(docs[0]) is not { Layout: { } winLayout }) continue;
                if (DockLayoutCapture.FindDocumentDock(winLayout) is not { } targetDock) continue;

                foreach (var extra in docs.Skip(1))
                    _factory.MoveDockable(shellDock, targetDock, extra, null);

                var active = saved.Active is { } activeKey
                    ? ResolveOpenDocument(activeKey, wsDir) ?? docs[0]
                    : docs[0];
                _factory.SetActiveDockable(active);
            }
            catch (Exception ex)
            {
                Messages.Warning($"A torn-off document window could not be restored; its documents stay docked. ({ex.Message})");
            }
        }

        // Per-window wiring — the active-document override, per-window undo key bindings and the
        // macOS menu attach — is installed by the deferred scans below. They normally run off
        // OnDocumentDockPropertyChanged; a programmatic float does not go through that hook, so
        // without this nudge a restored torn-off window would show "Close Workspace" instead of
        // "Close Window" and, on macOS, no menu bar at all.
        if (floatedAny)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                TryWireHostWindowsUndo, Avalonia.Threading.DispatcherPriority.Background);
            Avalonia.Threading.Dispatcher.UIThread.Post(
                TryWireWindowFocusTracking, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>The open document for a workspace-relative layout key, or null when it is not open.</summary>
    private IDockable? ResolveOpenDocument(string key, string workspaceDir)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var abs = Path.IsPathRooted(key) ? key : Path.GetFullPath(Path.Combine(workspaceDir, key));
        return _openDocsByPath.TryGetValue(abs, out var doc) ? doc : null;
    }

    /// <summary>The floating window whose layout currently contains <paramref name="dockable"/>.</summary>
    private IDockWindow? FindWindowHosting(IDockable dockable)
    {
        if (Layout is not IRootDock root || root.Windows is null) return null;

        foreach (var window in root.Windows)
        {
            if (window?.Layout is not { } winLayout) continue;
            foreach (var docDock in DockLayoutCapture.EnumerateDocumentDocks(winLayout))
                if (docDock.VisibleDockables?.Contains(dockable) == true)
                    return window;
        }
        return null;
    }

    // ---- .cws integration ----------------------------------------------------

    /// <summary>
    /// The arrangement to persist. R-dock-9: while collapsed, the UNDERLYING arrangement is saved,
    /// never the collapsed one — the collapsed state is session-only and a workspace saved while
    /// collapsed reopens expanded.
    /// </summary>
    internal CwsDockLayout? DockLayoutToPersist() =>
        DockersCollapsed ? _preCollapseLayout : CaptureDockLayout();

    /// <summary>
    /// Parses a workspace's saved arrangement without applying it, so the caller can use its
    /// document order while restoring tabs and only rebuild the shell once, afterwards. Never
    /// throws. An ABSENT block reports nothing — that is the ordinary case for every workspace
    /// saved before this feature existed (R-dock-4).
    /// </summary>
    private static DockLayoutSerialization.ReadResult ReadDockLayout(CwsFile cws)
    {
        try   { return DockLayoutSerialization.TryRead(cws.DockLayout); }
        catch (Exception ex)
        {
            return new DockLayoutSerialization.ReadResult(
                null, $"Saved window layout could not be read; using the default layout. ({ex.Message})");
        }
    }

    /// <summary>
    /// Applies a parsed arrangement (and reports why it could not be used, when that is the case).
    /// R-dock-5 in one method: nothing here can throw into the workspace-open sequence, and every
    /// failure leaves the shell on the default layout with a message the user can act on.
    /// </summary>
    private void ApplyRestoredDockLayout(DockLayoutSerialization.ReadResult read)
    {
        if (read.Report is not null) Messages.Warning(read.Report);

        if (read.Layout is { } layout)
        {
            try
            {
                // One placer for the whole restore — see FloatingWindowPlacer's own note.
                var placer = FloatingWindowPlacer.For(layout, CurrentScreens());
                ApplyDockLayout(layout, placer);
                RestoreFloatingDocumentWindows(layout, placer);
            }
            catch (Exception ex)
            {
                Messages.Warning($"Saved window layout could not be applied; using the default layout. ({ex.Message})");
            }
        }

        // A view preference, not a property of the design — it survives the workspace switch.
        ReapplyCollapsedStateIfNeeded();
    }
}
