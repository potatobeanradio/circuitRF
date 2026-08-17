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
            if (!DockersCollapsed) CollapseDockers();
            else                   ExpandDockers();
        }
        catch (Exception ex)
        {
            // Same rule as restore: a layout operation never leaves the user stuck.
            Messages.Warning($"Could not toggle the dockers: {ex.Message}");
        }
    }

    private void CollapseDockers()
    {
        _preCollapseLayout = CaptureDockLayout();
        ApplyDockLayout(DockLayoutDefaults.Collapsed(_preCollapseLayout));
        DockersCollapsed = true;
    }

    private void ExpandDockers()
    {
        ApplyDockLayout(_preCollapseLayout ?? DockLayoutDefaults.Default());
        _preCollapseLayout = null;
        DockersCollapsed = false;
    }

    /// <summary>
    /// Applies the "Show Dockers" On Launch preference (default true = shown). Called once after
    /// app launch and once after every new workspace is created. When <paramref name="showDockers"/>
    /// is false, collapses the dockers exactly as View ▸ Hide Dockers would — but only when they are
    /// not ALREADY collapsed, so this can never re-expand a session someone (or an earlier call to
    /// this same method) already collapsed.
    /// </summary>
    internal void ApplyShowDockersOnLaunchPreference(bool showDockers)
    {
        if (showDockers || DockersCollapsed) return;
        try { CollapseDockers(); }
        catch (Exception ex)
        {
            Messages.Warning($"Could not apply the Show Dockers preference: {ex.Message}");
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
    /// <summary>
    /// <b>Toggle</b> a tool panel — strictly two states: showing anywhere, or not showing.
    ///
    /// <para><b>Two states, not three</b> (owner, 2026-08-17: "I should be able to press A repeatedly and
    /// the Array Inductance view toggle on and off"). An earlier version had a middle state — showing but
    /// behind another tab meant "bring it forward" — and it made the key non-deterministic: a panel tabbed
    /// with another needed THREE presses for one cycle, and which press did what depended on a tab order
    /// the user was not thinking about. A key that means "show/hide this" has to mean that every time.</para>
    ///
    /// <para>Separate from <see cref="ShowToolPanel"/> rather than replacing it: a MENU item named after a
    /// panel means "show me that panel", and a menu that closed what you asked for would be a trap. A KEY
    /// or a button that reads as a state is the case a second press should undo.</para>
    ///
    /// <para>Closed through <c>ForceCloseDockable</c>, bypassing the dirty-confirm hook: a tool panel is
    /// never dirty, and asking about one would be asking a question with one answer.</para>
    /// </summary>
    [RelayCommand]
    private void ToggleToolPanel(string? panelId)
    {
        if (panelId is null || _factory.ToolById(panelId) is not { } tool) return;

        // Not in the tree at all: put it back where it was last (see RestorePanelToItsHome), falling
        // through to ShowToolPanel's float only when there is nowhere remembered.
        if (!_factory.TryFindTool(tool, out var parent, out var window) || parent is null)
        {
            if (!RestorePanelToItsHome(panelId, tool)) ShowToolPanel(panelId);
            return;
        }

        // Showing — anywhere, front tab or not — so this press hides it.
        RememberPanelHome(panelId, tool);

        // A FLOATING panel is hidden by closing its window, not by Dock's hide.
        //
        // Owner, 2026-08-17: "their window contents disappears and the window is not closed when toggling,
        // also I see that flash bug." Both, measured against the real library rather than reasoned about:
        // HideDockable files the tool under the FLOAT's root, not the shell's — so the empty floating
        // window stays on screen (the vanished contents), and the shell-root hidden check at restore time
        // misses it and falls all the way through to the rebuild (the flash).
        //
        // Hide/restore is the right answer for a DOCKED panel because its place is a slot in a tree that
        // has to be held open. A floating panel's place is a rectangle on a screen, which is a value —
        // already recorded by RememberPanelHome — so there is nothing to hold open and no reason to keep
        // an empty window alive to hold it.
        if (window is not null)
        {
            HideFloatingPanel(panelId, tool, window);
            return;
        }

        // HIDE, not close — see DockPanelHiding for the whole reasoning. Closing lets the emptied
        // ToolDock collapse out of the tree, and the only way back is then a full rebuild: the flash the
        // owner reported, and the reason the keyboard shortcut stopped working (the view handling the key
        // was re-created underneath it).
        try { DockPanelHiding.Hide(_factory, tool); }
        catch (Exception ex)
        {
            Messages.Warning($"Could not hide the {tool.Title} panel: {ex.Message}");
            try { _factory.ForceCloseDockable(tool); } catch { /* reported above */ }
        }
    }

    /// <summary>
    /// Hides a panel that is in a floating window: closes the window when this panel is all it holds, and
    /// otherwise closes just the panel and leaves its co-tenants alone.
    /// </summary>
    private void HideFloatingPanel(string panelId, ITool tool, IDockWindow window)
    {
        try
        {
            // A float the user has dragged a second panel into is still that other panel's window. Close
            // only this one; RememberPanelHome has already promised the restore its OWN rectangle rather
            // than a seat back in a window it no longer shares.
            if (window.Layout is { } layout && DockPanelHiding.HoldsOtherTools(layout, tool))
            {
                _factory.ForceCloseDockable(tool);
                return;
            }

            _factory.CloseFloatingWindow(_factory.CurrentRoot, window);
        }
        catch (Exception ex)
        {
            Messages.Warning($"Could not hide the {tool.Title} panel: {ex.Message}");
        }
    }

    // ── Where a closed panel goes back to ─────────────────────────────────────
    //
    // Owner, 2026-08-17: pressing A hid the Array Inductance panel and pressing it again brought it back
    // as a FLOATING window — because ShowToolPanel's only answer for a panel that is not in the tree is to
    // float one. "Put it back where it was" needs somewhere to have remembered it.
    //
    // Two records per panel, because they answer two different questions:
    //   • the live ToolDock it sat in, plus its index — an exact, CHEAP restore that needs no rebuild, and
    //     therefore does not re-realise the document views (which would throw away every open canvas's
    //     pan and zoom for a keystroke);
    //   • its schema placement — side, group, order, width, inboard, or its floating rectangle — which
    //     survives that dock being disposed, and survives a restart once it is written to the `.cws`.

    private readonly Dictionary<string, PanelHome> _panelHomes = new(StringComparer.Ordinal);

    /// <param name="Dock">The tool dock it was in, if that dock is still alive. Weakly held by intent —
    /// see <see cref="RestorePanelToItsHome"/>, which verifies it is still in the tree before using it.</param>
    private readonly record struct PanelHome(IToolDock? Dock, int Index, CwsDockPanel? Docked, CwsFloatingWindow? Float);

    /// <summary>Records where <paramref name="tool"/> is, so closing it does not lose the place.</summary>
    private void RememberPanelHome(string panelId, ITool tool)
    {
        IToolDock? hostDock = null;
        int index = 0;

        if (_factory.TryFindTool(tool, out var parent, out _) && parent is IToolDock td)
        {
            hostDock = td;
            index = Math.Max(0, td.VisibleDockables?.IndexOf(tool) ?? 0);
        }

        CwsDockPanel? docked = null;
        CwsFloatingWindow? floated = null;

        if (CaptureDockLayout() is { } live)
        {
            if (live.Panels.FirstOrDefault(p => p.Id == panelId) is { } p)
            {
                p.Open = false;   // recorded as a PLACE, not as something showing
                docked = p;
            }
            else if (live.FloatingWindows.FirstOrDefault(w => w.Panels.Contains(panelId)) is { } w)
            {
                // Its own rectangle, holding only this panel: restoring one member of a shared float into
                // a window with the others still in it is not a thing this can promise.
                floated = new CwsFloatingWindow
                {
                    X = w.X, Y = w.Y, Width = w.Width, Height = w.Height,
                    Panels = [panelId], Active = panelId,
                };
            }
        }

        // Nothing found anywhere, and something already remembered: KEEP the older record.
        //
        // Not defensive padding — it is the ordering that makes the floating toggle work. Hiding a float
        // records the rectangle and then closes the window, and closing raises DockableClosing, which
        // arrives back here through OnToolPanelClosing after the window has left the tree. A second pass
        // that finds nothing would overwrite the rectangle with "nowhere" and the panel would come back as
        // a fresh default-placed float. A record naming no place carries no information, so there is never
        // a reason to prefer one over a record that names one.
        if (hostDock is null && docked is null && floated is null && _panelHomes.ContainsKey(panelId)) return;

        _panelHomes[panelId] = new PanelHome(hostDock, index, docked, floated);
    }

    /// <summary>
    /// Puts a closed panel back where it was. Returns false when nothing is remembered, so the caller can
    /// fall back to opening it in a new window.
    /// </summary>
    private bool RestorePanelToItsHome(string panelId, ITool tool)
    {
        // ── The library's own path: it is HIDDEN, so it knows where it goes ──
        // The one that costs nothing and LOOKS like nothing — the same operation as dragging the panel
        // back by hand, and the only path that does not redraw the shell.
        if (Layout is IRootDock shellRoot && shellRoot.HiddenDockables?.Contains(tool) == true)
        {
            try
            {
                if (DockPanelHiding.Restore(_factory, shellRoot, tool))
                {
                    _panelHomes.Remove(panelId);

                    // The tab in front, but NOT keyboard focus — a key that shows a panel must leave the
                    // keyboard where the user had it.
                    if (tool.Owner is IDock back)
                    {
                        back.ActiveDockable = tool;
                        _factory.SetActiveDockable(tool);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Messages.Warning($"Could not restore the {tool.Title} panel: {ex.Message}");
            }
        }

        if (!_panelHomes.TryGetValue(panelId, out var home)) return false;

        // ── It was FLOATING: re-open its own window, at its own rectangle ─────
        // Before the two paths below, and not merely because they would miss: a panel the user floated
        // belongs in a float, and the rectangle is the whole of what "where it was" means for one. This
        // adds a window and presents it — it does not touch the shell, so there is no rebuild and no flash.
        //
        // The remembered rectangle still goes through the placer (R-dock-6): a float that was on a second
        // monitor at hide time must not re-open off the edge of the world if that monitor has since gone.
        if (home.Float is { } saved)
        {
            try
            {
                var placer = new FloatingWindowPlacer(CurrentScreens(), sameConfiguration: false);

                if (_factory.FloatTool(tool, placer.Place(new ScreenRect(saved.X, saved.Y, saved.Width, saved.Height))) is not null)
                {
                    _panelHomes.Remove(panelId);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Messages.Warning($"Could not reopen the {tool.Title} panel window: {ex.Message}");
            }
        }

        // ── The cheap path: its old tool dock is still there ──────────────────
        // Preferred whenever it applies, because the alternative rebuilds the whole shell — and that
        // re-realises every document's view, discarding the pan and zoom of every open canvas. Not a price
        // a keystroke should ever pay.
        if (home.Dock is { } dock && Layout is IRootDock root && DockLayoutCapture.Contains(root, dock))
        {
            try
            {
                int at = Math.Clamp(home.Index, 0, dock.VisibleDockables?.Count ?? 0);
                _factory.InsertDockable(dock, tool, at);

                // The same triple OpenDocument uses, plus the direct assignment: the panel the user just
                // asked for has to be the tab IN FRONT, or the next press of the same key reads it as
                // "showing but behind another" and brings it forward instead of hiding it — three presses
                // for one cycle (owner, 2026-08-17).
                // The tab in front, but NOT keyboard focus: a key that shows a panel must leave the
                // keyboard where the user had it.
                dock.ActiveDockable = tool;
                _factory.SetActiveDockable(tool);

                _panelHomes.Remove(panelId);
                return true;
            }
            catch (Exception ex)
            {
                Messages.Warning($"Could not restore the {tool.Title} panel in place: {ex.Message}");
            }
        }

        // ── The exact path: rebuild around its remembered placement ───────────
        // Needed when its column no longer exists at all (it was the only panel in it), and it is the only
        // path a placement read back from a `.cws` can take.
        if (CaptureDockLayout() is not { } live) return false;

        if (home.Docked is { } d)
        {
            // Only ONE panel in a group can be the tab in front, and the group it is going back into
            // already has one — whatever was left showing when this panel was hidden.
            //
            // This is the whole of the owner's 2026-08-17 report. BuildSide resolves the active tab as
            // `ordered.FirstOrDefault(p => p.Active)` over panels sorted by Order, so leaving the old
            // active flag set meant the panel with the LOWER order won — the user pressed A, the Array
            // Inductance panel appeared behind the Wire Profile, and the next A brought it forward
            // rather than hiding it.
            foreach (var sibling in live.Panels)
                if (sibling.Side == d.Side && sibling.Group == d.Group && sibling.Inboard == d.Inboard)
                    sibling.Active = false;

            live.Panels.Add(new CwsDockPanel
            {
                Id = panelId, Open = true, Side = d.Side, Group = d.Group, Order = d.Order,
                Active = true, Proportion = d.Proportion, Inboard = d.Inboard,
            });
        }
        else if (home.Float is { } f)
            live.FloatingWindows.Add(f);
        else
            return false;

        _panelHomes.Remove(panelId);
        ApplyDockLayout(live);
        return true;
    }

    /// <summary>
    /// Remembers where a panel was when it is closed by any route — its own tab X, not only the P/A
    /// toggle — so every way of hiding one leaves the same trail back.
    /// </summary>
    private void OnToolPanelClosing(IDockable? dockable)
    {
        if (dockable is ITool { Id: { Length: > 0 } id } tool && DockPanelIds.All.Contains(id))
            RememberPanelHome(id, tool);
    }

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
    /// The arrangement to WRITE: the live one, plus a <c>Open = false</c> entry for each closed panel whose
    /// place is remembered.
    ///
    /// <para>That is what lets "put it back where it was" survive a restart rather than only a session.
    /// <see cref="DockLayoutCapture.Capture"/> walks the live tree and a closed panel is not in it, so the
    /// place would otherwise be forgotten the moment the workspace was saved with the panel hidden — and
    /// pressing the key next session would float it, which is the behaviour being fixed.</para>
    ///
    /// <para>Harmless to every reader: <c>BuildSide</c> filters on <c>Open</c>, so a closed entry places
    /// nothing. Deliberately NOT folded into <c>Capture</c> itself, which is a pure walker of a live tree
    /// and has no business knowing what this view model remembers.</para>
    /// </summary>
    private CwsDockLayout? CaptureDockLayoutForPersistence()
    {
        if (CaptureDockLayout() is not { } layout) return null;

        foreach (var (id, home) in _panelHomes)
        {
            if (home.Docked is not { } d) continue;
            if (layout.Panels.Any(p => p.Id == id)) continue;
            if (layout.FloatingWindows.Any(w => w.Panels.Contains(id))) continue;

            layout.Panels.Add(new CwsDockPanel
            {
                Id = id, Open = false, Side = d.Side, Group = d.Group, Order = d.Order,
                Active = false, Proportion = d.Proportion, Inboard = d.Inboard,
            });
        }

        return layout;
    }

    /// <summary>
    /// Seeds the remembered places from a layout just read off disk, so the first press of P or A in a new
    /// session puts the panel back where the last one left it.
    /// </summary>
    private void SeedPanelHomesFrom(CwsDockLayout layout)
    {
        foreach (var p in layout.Panels)
        {
            if (p.Open || p.Id is not { Length: > 0 }) continue;
            _panelHomes[p.Id] = new PanelHome(null, 0, p, null);
        }
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
    // ── Persisting the arrangement when it actually changes ───────────────────
    //
    // Owner report this fixes (2026-08-17): "the Wire Profile and Array Inductance dockable positions are
    // not respected when I re-open the saved workspace."
    //
    // The `.cws` — and with it the dock layout — was written only on an explicit save, the tree-filter
    // debounce, clean exit, or a workspace switch. **Nothing wrote it because a panel MOVED.** So an
    // arrangement was recorded only by accident, whenever some unrelated action happened to trigger a
    // save while the panels were where the user wanted them. That is the identical failure shape already
    // documented for open documents on PersistOutgoingWorkspaceSession, one layer along.
    //
    // Why it surfaced on these two panels and not the others: every OTHER panel is in the shipped default
    // layout at roughly the position users expect, so a missing or stale layout block still puts them
    // somewhere plausible and they LOOK respected. The two wBond panels are deliberately absent from that
    // default (most designs have no wirebonds), so a stale block loses them completely and they come back
    // closed — "including whether they were docked or not", exactly as reported.

    /// <summary>
    /// Subscribes to the Dock events that mean "the arrangement changed", so a moved, docked, floated or
    /// closed panel is recorded like any other session state. Called once, at construction.
    ///
    /// <para><b>Deliberately not <c>DockableAdded</c>/<c>DockableRemoved</c> or the activation events.</b>
    /// Those fire in bulk while a layout is being BUILT, and on every tab switch — which is not an
    /// arrangement change and would arm a disk write on every click.</para>
    /// </summary>
    private void WireDockArrangementPersistence()
    {
        // BEFORE it goes, while it still has a place to record — see OnToolPanelClosing.
        _factory.DockableClosing   += (_, e) => OnToolPanelClosing(e.Dockable);

        _factory.DockableDocked    += (_, _) => OnDockArrangementChanged();
        _factory.DockableUndocked  += (_, _) => OnDockArrangementChanged();
        _factory.DockableClosed    += (_, _) => OnDockArrangementChanged();
        _factory.DockableMoved     += (_, _) => OnDockArrangementChanged();
        _factory.DockableSwapped   += (_, _) => OnDockArrangementChanged();
        _factory.WindowMoveDragEnd += (_, _) => OnDockArrangementChanged();
        _factory.WindowOpened      += (_, _) => OnDockArrangementChanged();
        _factory.WindowClosed      += (_, _) => OnDockArrangementChanged();
    }

    private void OnDockArrangementChanged()
    {
        if (_layoutRebuildDepth > 0) return;
        ScheduleCwsSave();
    }

    private int _layoutRebuildDepth;

    /// <summary>
    /// Runs <paramref name="rebuild"/> with arrangement-change persistence suppressed.
    ///
    /// <para><b>This guard is not tidiness, it prevents data loss.</b> Applying a layout raises the very
    /// events above, so a restore would arm a save of what it just applied — and if the restore had
    /// DEGRADED (a layout block that failed to apply falls back to the default, R-dock-5), that debounced
    /// write would overwrite the user's good saved arrangement with the fallback three seconds after
    /// opening the workspace.</para>
    /// </summary>
    internal void WhileRebuildingLayout(Action rebuild)
    {
        _layoutRebuildDepth++;
        try { rebuild(); }
        finally { _layoutRebuildDepth--; }
    }

    private void ApplyDockLayout(CwsDockLayout state, FloatingWindowPlacer? placer = null)
    {
        _layoutRebuildDepth++;
        try { ApplyDockLayoutCore(state, placer); }
        finally { _layoutRebuildDepth--; }
    }

    private void ApplyDockLayoutCore(CwsDockLayout state, FloatingWindowPlacer? placer)
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
        DockersCollapsed ? _preCollapseLayout : CaptureDockLayoutForPersistence();

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
            // Before applying it: the CLOSED entries are places, not panels, and the apply drops them.
            SeedPanelHomesFrom(layout);

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
