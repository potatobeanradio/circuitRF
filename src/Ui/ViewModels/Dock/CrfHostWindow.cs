using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Host window used for dock tear-offs. Subclasses Dock's <see cref="HostWindow"/> to (1) close a
/// floating window that contains a <b>Tool</b> (Properties, Analyses, Project Tree, Palette,
/// Messages) our own way rather than the platform's, and (2) give every tear-off window the SAME
/// window-level background <see cref="WorkspaceWindow"/> itself uses.
///
/// Background (crash fix): closing a torn-off TOOL window drives Dock's FactoryBase.CloseDockable
/// down a window-teardown path that dereferences an already-stripped floating RootDock and throws
/// an unrecoverable NullReferenceException (confirmed by instrumentation). Document tear-off windows
/// do not hit that path and close cleanly. Rather than patch the moving null inside the library's
/// teardown, we never enter it: the OS close is cancelled, and the window is torn down instead by
/// <see cref="CloseFloatedToolPanels"/> -- write each panel's place down, detach, close -- which is
/// the same route the panel toggle has taken for a floating panel since 2026-08-17. Document float
/// windows are unaffected and close normally.
///
/// <b>This used to make the close box INERT, and that was a bug in its own right</b> (owner,
/// 2026-09-02: the Library palette's floating window could not be closed). Avoiding the crash never
/// required refusing the close, only refusing the library's cascade. The panels survive the close --
/// the factory holds the instances for the session -- so the toolbar toggles, View ▸ Panels and the
/// P/A keys bring the same panel back at the same rectangle, with its own state intact.
///
/// Background (§1, brief-housekeeping-tearoff-palette-repo.md): <c>WorkspaceWindow.axaml</c>
/// explicitly sets <c>Background="{DynamicResource SystemChromeLowColor}"</c> on itself, but
/// Dock's own <see cref="HostWindow"/> base has no such setting — a tear-off window falls back to
/// whatever background the FluentTheme's stock Window template provides, which is close to but not
/// necessarily byte-identical to <c>SystemChromeLowColor</c> depending on theme/compositing
/// specifics. Setting the SAME resource at the window level here, exactly like
/// <c>WorkspaceWindow.axaml</c> does, removes the possibility of the two ever resolving
/// differently — the fix is at application/resource scope, never a hard-coded color (R-hk-2).
/// </summary>
public class CrfHostWindow : HostWindow
{
    // True only while CloseForLayoutRebuild is tearing this window down, so the OnClosing hook below
    // lets that close through instead of re-entering its own teardown.
    private bool _closingForLayoutRebuild;

    /// <summary>
    /// The workspace this float belongs to (MW1 R-mw1-11).
    ///
    /// <para><b>Stamped by the factory that creates it, never inferred.</b> Every
    /// <c>CrfHostWindow</c> in the process is built through one <c>CircuitRfDockFactory</c>'s
    /// <c>DefaultHostWindowLocator</c>, and the factory is per view model — so the creating factory
    /// already knows the answer. With two workspace windows open, the alternatives (window position,
    /// z-order, title) are guesses, and every one of them would sometimes attribute a panel to the
    /// wrong workspace: its Window menu would list it, its close prompt would count its dirty work,
    /// and on macOS it would be handed the wrong shell's menu.</para>
    /// </summary>
    public WorkspaceViewModel? OwningWorkspace { get; set; }

    public CrfHostWindow()
    {
        this[!BackgroundProperty] = new DynamicResourceExtension("SystemChromeLowColor");

        // A floating tool panel stays above its workspace's windows while that workspace is in use.
        FloatingPanelsOnTop.Install();

        // P / A must keep working while a floating panel has focus, which it does the moment one is
        // presented — otherwise the toggle dies after two presses and only a click on the shell revives it
        // (owner, 2026-08-17). This is a second TopLevel, so it needs its own registration; the shortcut
        // itself, and why it is not solved by keeping focus in the shell, is in Views.WirePanelKeys.
        Views.WirePanelKeys.Attach(this, () => Views.WorkspaceLocator.For(this));

        // The ✕ on this float's tool chrome is inert in Dock 12.0.0.2 — see Views.ToolChromeCloseButton,
        // which is shared with the shell because the docked chrome has exactly the same dead button. The
        // fallback is this window: a floating tool window exists to hold panels, so closing it is what
        // the ✕ means even when no single panel can be named.
        Views.ToolChromeCloseButton.Attach(this, () => Views.WorkspaceLocator.For(this), CloseFloatedToolPanels);

        // A click on this window's title bar leaves Dock's `:dragging` class set, because the mouse-up
        // that would clear it is swallowed by the platform's own window-drag loop — so the window sits
        // at half opacity until some later click happens to release inside it. See HostWindowDragLatch.
        Views.HostWindowDragLatch.Attach(this);

        // A .cws dropped on a torn-off document opens that workspace in a new window, as in the shell.
        Views.WorkspaceFileDrop.Attach(this);

        Activated += (_, _) => RaiseToolWindowsAboveThisDocument();

        // Undo/Redo from a floating TOOL panel: the shell's KeyBindings live on the WORKSPACE window, so
        // with a floating panel focused Ctrl+Z reached nothing — a Push from a floating Tuning panel could
        // not be undone without clicking the schematic first. A DOCUMENT float binds its own document's
        // history instead (WorkspaceViewModel's float key bindings) and is left alone. BUBBLE, so a
        // focused text box keeps its own Ctrl+Z.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (OwningWorkspace is not { } ws || !FloatsAnyTool()) return;
            var command = UndoRedoGesture(e.Key, e.KeyModifiers, OperatingSystem.IsMacOS()) switch
            {
                true  => ws.UndoCommand,
                false => ws.RedoCommand,
                null  => null,
            };
            if (command is null) return;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>
    /// True for the shell's Undo gesture, false for its Redo gestures, null for anything else. ⌘ is left
    /// to the application menu on macOS, whose key equivalents already reach every window of the app;
    /// handling it here as well would undo twice.
    /// </summary>
    internal static bool? UndoRedoGesture(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods, bool macOs)
    {
        var command = mods & ~Avalonia.Input.KeyModifiers.Shift;
        bool primary = command == Avalonia.Input.KeyModifiers.Control
                       || (!macOs && command == Avalonia.Input.KeyModifiers.Meta);
        if (!primary) return null;
        bool shift = mods.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        return key switch
        {
            Avalonia.Input.Key.Z => !shift,
            Avalonia.Input.Key.Y when !shift => false,
            _ => null,
        };
    }

    // One raise at a time across every float: our own Activate() calls re-raise Activated.
    private static bool _raisingTools;

    /// <summary>
    /// A torn-off DOCUMENT window coming forward brings its workspace's floating TOOL windows with it,
    /// as the shell does on its own <c>Activated</c> (R-dock-14, <c>WorkspaceWindow.RaiseFloatingToolWindows</c>).
    /// A document float is a peer of the shell, so without this a Data Display torn off beside a
    /// floating Tuning or Optimizer panel covered the panel the moment it was clicked, and the panel
    /// stayed behind it. Focus comes straight back to this window (R-dock-15). Only this workspace's
    /// panels move; another workspace's are left where they are.
    /// </summary>
    private void RaiseToolWindowsAboveThisDocument()
    {
        if (_raisingTools || OwningWorkspace is null || FloatsAnyTool()) return;
        // A quit or save prompt outranks the panels, for the shell's reason (ModalPromptFront).
        if (Views.ModalPromptFront.HasAnyOpenPrompt()) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;

        var tools = desktop.Windows.OfType<CrfHostWindow>()
            .Where(w => w.PlatformImpl is not null && ReferenceEquals(w.OwningWorkspace, OwningWorkspace) && w.FloatsAnyTool())
            .ToList();
        if (tools.Count == 0) return;

        _raisingTools = true;
        try
        {
            foreach (var tool in tools) tool.Activate();
            Activate();
        }
        catch (Exception ex)
        {
            OwningWorkspace.Messages.Warning($"Could not raise the floating panels: {ex.Message}");
        }
        finally
        {
            // Released a dispatcher pass later, for the shell's reason: the Activated events these
            // calls produce arrive asynchronously, and must land while the guard is still set.
            Dispatcher.UIThread.Post(() => _raisingTools = false, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// <b>How wide a floating window holding an EM setup is allowed to be.</b> Owner request,
    /// 2026-08-25 — 1600, then 800, then 700. (The first was a no-op: the window measured 1600
    /// already, so capping it there changed nothing, which is why it still read as too wide.)
    ///
    /// <para>The EM Setup panel is one narrow scrolling column of labelled fields, and almost
    /// nothing in it reflows into extra width: every numeric box is <c>TextBox.num</c>, a fixed
    /// 86 units wide; the analysis combo caps at 200; the frequency group is three 150-wide blocks.
    /// A tear-off that inherits the shell's size — which is what Dock's drag hands it, and on a
    /// maximized shell that is the whole screen — is mostly empty chrome beside a column of
    /// controls.</para>
    ///
    /// <para><b>The stackup table is what sets the floor, and 700 is close to it.</b> Its row is
    /// <c>76,*,90,190,*</c> plus spacing — about 380 units of FIXED columns — so at 700 the two
    /// stretching columns (conductor name and drawing layers) get roughly 150 each. Both already
    /// carry <c>TextTrimming="CharacterEllipsis"</c>, so a long conductor name trims rather than
    /// overflowing; that is the visible cost of this number, and it is the owner's chosen trade. A
    /// cap much below the fixed columns' own ~380 would squeeze them to nothing.</para>
    ///
    /// <para><b>Logical units, not device pixels.</b> Avalonia's <c>Window.Width</c> is in logical
    /// units, so on a 2× Retina display this window is 1600 device pixels across. That is the same
    /// convention every other window size in this codebase uses — see
    /// <c>ScreenPlacement.DeviceToLogical</c>, which exists because mixing the two is invisible on
    /// an unscaled display.</para>
    /// </summary>
    internal const double EmSetupFloatMaxWidth = 700;

    /// <summary>
    /// Applies <see cref="EmSetupFloatMaxWidth"/> once the window is actually on screen.
    ///
    /// <para><b>Here rather than in <c>CircuitRfDockFactory.CreateWindowFrom</c>, and the ordering is
    /// the reason.</b> A width set during window creation is not final: <c>DockWindowOptions.ApplyTo</c>
    /// assigns geometry unconditionally, which is the same overwrite the <c>OwnerMode</c> override
    /// beside it already has to work around, and Dock's drag tear-off supplies the dragged tab's own
    /// bounds through exactly that path. This is the last point in the sequence that this codebase
    /// owns, so it is the only one that cannot be silently undone.</para>
    ///
    /// <para><b>It is a CAP, not an initial size</b> — a narrower window is left alone, and the user
    /// can still widen this one by hand for as long as it is open. Re-floating it applies the cap
    /// again, because the cap is about what the panel's content can use rather than about what was
    /// last done to the window.</para>
    ///
    /// <para><b>The height bonus rides INSIDE the narrowing, and that is what stops it ratcheting.</b>
    /// Written as an unconditional <c>Height += 200</c> it would compound across launches: this
    /// window's geometry is captured into the <c>.cws</c>, so next launch would restore 200 taller,
    /// add 200 again, and keep going. Gating it on the width cap actually firing makes it
    /// idempotent — a restored float is already within the cap, so it is left exactly as the user
    /// last sized it, while a fresh tear-off (which arrives at the shell's full width) is narrowed
    /// and given the height back in the same breath.</para>
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Width <= EmSetupFloatMaxWidth || !FloatsAnyEmSetup()) return;

        Width  = EmSetupFloatMaxWidth;
        Height = Math.Min(Height + EmSetupFloatExtraHeight, AvailableHeight());
    }

    /// <summary>
    /// <b>What the window gains in height when it is narrowed</b> (owner request, 2026-08-25: "add
    /// 200 pixels to the window's height"). The panel is one long scrolling column, so height it
    /// does not have is height the user pays for in scrolling — and taking width away makes several
    /// of its groups (the two-column Analysis grid, the Frequency wrap panel) taller still.
    /// </summary>
    internal const double EmSetupFloatExtraHeight = 200;

    /// <summary>
    /// The working height of the screen this window is on, so the bonus cannot push the window's
    /// bottom off it. Falls back to <see cref="double.PositiveInfinity"/> — i.e. no limit — when
    /// there is no screen to ask, which is the headless case and every case where
    /// <c>ScreenPlacement</c> is the backstop anyway.
    /// </summary>
    private double AvailableHeight()
    {
        var screen = Screens?.ScreenFromWindow(this);
        if (screen is null) return double.PositiveInfinity;

        // WorkingArea is DEVICE pixels while Height is logical — mixing them is the bug
        // AvaloniaScreenSource exists to prevent, and it is invisible on an unscaled display.
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        return screen.WorkingArea.Height / scaling;
    }

    /// <summary>True when the floated layout contains at least one EM setup document. Same walk, and
    /// the same null-guarding against a partially torn-down tree, as
    /// <see cref="FloatsAnyTool"/>.</summary>
    internal bool FloatsAnyEmSetup()
    {
        var layout = Window?.Layout;
        return layout is not null && ContainsEmSetup(layout);
    }

    private static bool ContainsEmSetup(IDockable dockable)
    {
        if (dockable is Layout.Em.EmSetupDocument)
            return true;

        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (child is not null && ContainsEmSetup(child))
                    return true;
            }
        }

        return false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ShouldTearDownOurWay(_closingForLayoutRebuild, IsTracked, FloatsAnyDocument()))
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(CloseFloatedToolPanels);
            return;
        }

        if (Window?.Factory is CircuitRfDockFactory factory
            && FloatedDocuments() is { Count: > 0 } documents
            && ShouldAskBeforeClosing(_closingForLayoutRebuild, IsTracked,
                                      factory.IsDirtyForClose is { } dirty && documents.Any(dirty)))
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(() => CloseFloatedDocumentsThenWindow(factory, documents));
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Whether this close must be cancelled and re-run as a prompted close of the documents it holds.
    ///
    /// <para><b>The bug:</b> (owner, 2026-10-17: a dirty undocked <c>.cem</c> closed by its window's
    /// close box — the window vanished, THEN the save prompt appeared, and Cancel lost the edits.) Dock's
    /// <c>HostWindow</c> runs the layout's close and, on <c>OnClosed</c>, <c>IFactory.CloseWindow</c> —
    /// both reach <see cref="CircuitRfDockFactory.CloseDockable"/>, whose prompt is asynchronous, so
    /// neither can hold the native close open. The window closed, the prompt came up afterwards, and a
    /// Cancel kept a document whose floating layout had already been removed: open, dirty, and nowhere
    /// on screen. Nothing on the path branches on document kind, so every torn-off document —
    /// schematic, symbol, layout, EM setup, 3D view, technology, materials, wBond, Data Display, Smith
    /// chart — lost its edits the same way.</para>
    ///
    /// <para><b>The fix asks first and lets the documents close the window</b>: the close is cancelled
    /// and the float's documents go through the same one-prompt bulk close a tab strip's Close All
    /// uses. Closing the last one collapses the float and Dock closes the window itself (arriving here
    /// with <paramref name="isTracked"/> false, which is let through); Cancel closes nothing and the
    /// window stays. A clean float keeps Dock's own close, which was never wrong.</para>
    /// </summary>
    internal static bool ShouldAskBeforeClosing(bool closingForLayoutRebuild, bool isTracked, bool anyDocumentDirty)
        => !closingForLayoutRebuild && isTracked && anyDocumentDirty;

    private async void CloseFloatedDocumentsThenWindow(CircuitRfDockFactory factory, IReadOnlyList<IDockable> documents)
    {
        // Another close prompt is on screen; this close box asks again once that one is answered.
        if (factory.IsClosePromptOpen) return;

        await factory.CloseDockablesAsync(documents);

        // A float of documents only has closed with its last one. One that also holds a tool panel is
        // still here, and finishing the close is the tool panels' own route.
        if (IsTracked && Window is not null && !FloatsAnyDocument())
            CloseFloatedToolPanels();
    }

    /// <summary>Every <see cref="IDocument"/> in the floated layout, outermost first.</summary>
    internal IReadOnlyList<IDockable> FloatedDocuments()
    {
        var found = new List<IDockable>();
        if (Window?.Layout is { } layout) CollectDocuments(layout, found);
        return found;
    }

    private static void CollectDocuments(IDockable dockable, List<IDockable> found)
    {
        if (dockable is IDocument)
        {
            found.Add(dockable);
            return;
        }

        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (child is not null) CollectDocuments(child, found);
            }
        }
    }

    /// <summary>
    /// Whether this close must be cancelled and re-run as <see cref="CloseFloatedToolPanels"/>, rather
    /// than handed to the platform. Pure, static and internal so it can be gated headlessly — a
    /// <c>CrfHostWindow</c> cannot be constructed in the test suite.
    ///
    /// <para><b>Anything that is not a DOCUMENT float is closed OUR way, never the platform's.</b>
    /// Asked as "no documents here" rather than "a tool is here" on purpose. A real close was traced
    /// arriving with <c>tool=False</c> on a window titled "Window" — a host whose model layout no
    /// longer named what it was showing — and the old question sent exactly that case into Dock's
    /// crashing cascade, which is the one place it must never go. The teardown is correct for an empty
    /// window too: it detaches and closes it.</para>
    ///
    /// <para><b><paramref name="isTracked"/> is what stops that rule eating Dock's OWN teardown</b>
    /// (owner, 2026-09-05: closing an undocked <c>.csym</c> by its tab's ✕ closed the tab and left the
    /// native window on screen — and nothing about it is specific to <c>.csym</c>; every torn-off
    /// document type reaches the same code). Closing the last tab in a float runs
    /// <c>FactoryBase.CloseDockable</c> → <c>RemoveDockable</c> → <c>CollapseDock</c>, which walks up
    /// the emptied floating tree to <c>RemoveWindow</c> → <c>IDockWindow.Exit()</c> → this window's
    /// <c>Close()</c>. So the close arrives AFTER the document has left the layout, and the question
    /// above answers "no documents here" for what is really a document float finishing its own
    /// teardown. Cancelling it was fatal rather than merely late: <c>HostAdapter.Exit</c> sets
    /// <c>window.Host = null</c> the moment <c>Close()</c> returns, and <c>RemoveWindow</c> then clears
    /// <c>Factory</c>, <c>Layout</c> and <c>Owner</c> — so by the time the posted teardown ran there was
    /// no host left for it to find, and it closed nothing at all.</para>
    ///
    /// <para><c>HostAdapter.Exit</c> clears <see cref="Dock.Model.Core.IHostWindow.IsTracked"/> in the
    /// line before it calls <c>Close()</c>, and nothing else in Dock 12.0.0.2 ever clears it — so
    /// <c>IsTracked == false</c> here means exactly "Dock is already tearing this window down", and the
    /// only correct answer is to let the close finish. It is safe as well as correct: the sole caller
    /// of <c>RemoveWindow</c> is <c>CollapseDock</c>, which reaches it only once the floating layout is
    /// EMPTY, so the <c>HostWindow.OnClosed</c> cascade this class exists to avoid
    /// (<c>IFactory.CloseWindow</c> → a recursive <c>CloseDockable</c>) has nothing left to walk. A
    /// user pressing the window's own close box still arrives with <c>IsTracked == true</c> and is
    /// unaffected.</para>
    /// </summary>
    internal static bool ShouldTearDownOurWay(bool closingForLayoutRebuild, bool isTracked, bool floatsAnyDocument)
        => !closingForLayoutRebuild && isTracked && !floatsAnyDocument;

    /// <summary>
    /// True when the floated layout holds a real DOCUMENT — an <see cref="IDocument"/> that is not a tool panel.
    ///
    /// <para><b>Not the same question as <see cref="FloatsAnyDocument"/>, which answers true for every tool float:</b>
    /// Dock's <c>Tool</c> implements <see cref="IDocument"/> as well as <see cref="ITool"/>, so <c>is IDocument</c>
    /// cannot tell a floating Wire Profile from a torn-off layout. A trace on the owner's Mac showed the Wire Profile
    /// float reading as a document on every check, which is why keeping panels on top never acted on it.
    /// <see cref="FloatsAnyDocument"/> is left as it is because the close path reads it (<see cref="OnClosing"/>), and
    /// correcting it there changes how every tool float closes.</para>
    /// </summary>
    internal bool FloatsAnyNonToolDocument()
    {
        var layout = Window?.Layout;
        return layout is not null && ContainsNonToolDocument(layout);
    }

    internal static bool ContainsNonToolDocument(IDockable dockable)
    {
        if (dockable is IDocument and not ITool) return true;
        if (dockable is IDock { VisibleDockables: { } children })
            foreach (var child in children)
                if (child is not null && ContainsNonToolDocument(child)) return true;
        return false;
    }

    /// <summary>True when the floated layout contains at least one <see cref="IDocument"/> — which includes every tool
    /// panel, see <see cref="FloatsAnyNonToolDocument"/>.</summary>
    internal bool FloatsAnyDocument()
    {
        var layout = Window?.Layout;
        return layout is not null && ContainsDocument(layout);
    }

    private static bool ContainsDocument(IDockable dockable)
    {
        if (dockable is IDocument)
            return true;

        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (child is not null && ContainsDocument(child))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Closes this tool float the way the panel toggle already closes one — record where each panel was,
    /// detach the window, close it — so the close box works without ever entering Dock's crashing cascade.
    ///
    /// <para><b>Posted rather than run inside <see cref="OnClosing"/></b>: the teardown closes this same
    /// window, and re-entering the platform's close handling from inside its own notification is a trap
    /// worth not setting.</para>
    ///
    /// <para>The three fallbacks below are the three hosts this class serves. With a workspace — the
    /// application — the workspace does it, because only it can remember where the panels go. Without one
    /// (the standalone wBond and harmonicaRF binaries), the factory still knows how to deregister a
    /// floating window. With neither, <see cref="CloseForLayoutRebuild"/> is the same detach-then-close
    /// minus the bookkeeping.</para>
    /// </summary>
    private void CloseFloatedToolPanels()
    {
        var dockWindow = Window;

        // ALREADY DETACHED — neither route below can reach this host any more, and both would return
        // having closed nothing, leaving the window on screen for good. That is not hypothetical: it is
        // exactly the state Dock's own teardown leaves behind (see ShouldTearDownOurWay), because
        // HostAdapter.Exit nulls window.Host the instant Close() returns and RemoveWindow then clears
        // Factory, Layout and Owner. ShouldTearDownOurWay now keeps that case out of here entirely; this
        // guard is the belt to its braces, so a close box can never be inert again for a reason nobody
        // anticipated.
        if (dockWindow is null || !ReferenceEquals(dockWindow.Host, this))
        {
            CloseForLayoutRebuild();
            return;
        }

        if (Views.WirePanelKeys.ResolveWorkspace() is { } workspace)
        {
            workspace.CloseFloatingToolWindow(dockWindow);
            return;
        }

        if (dockWindow.Factory is CircuitRfDockFactory factory)
        {
            factory.CloseFloatingWindow(factory.CurrentRoot, dockWindow);
            return;
        }

        CloseForLayoutRebuild();
    }

    /// <summary>
    /// Closes this floating window as part of replacing the dock layout — a workspace close or
    /// switch, Reset Layout, or the Hide/Show Dockers toggle.
    ///
    /// <para><b>The bug this fixes:</b> replacing <c>WorkspaceViewModel.Layout</c> swaps the MODEL, but
    /// a floating window is a real OS window that nothing closes. Dock's own <c>IDockWindow.Exit()</c>
    /// would do it — except it calls <c>Close()</c>, which <see cref="OnClosing"/> above cancels for
    /// any window hosting a tool. So a torn-off tool panel outlived the workspace it belonged to, and
    /// reopening that workspace restored a SECOND copy of the same panel.</para>
    ///
    /// <para><b>Why it detaches before closing rather than calling <c>Exit()</c>:</b>
    /// <c>HostWindow.OnClosed</c> calls <c>IFactory.CloseWindow</c>, which recursively
    /// <c>CloseDockable</c>s every dockable in the floating layout — precisely the teardown that
    /// crashes <c>FactoryBase.CloseDockable</c> and that this whole class exists to avoid (see the
    /// class note above). Clearing <c>Window</c> first makes <c>OnClosed</c> return at its own
    /// <c>if (Window == null)</c> guard, so the window closes with no cascade at all. The panels
    /// themselves are not lost: they are re-hosted by the layout being built.</para>
    /// </summary>
    internal void CloseForLayoutRebuild()
    {
        _closingForLayoutRebuild = true;

        var dockWindow = Window;
        // Second line of defence only — the factory deregisters this host itself, because this chain
        // is null once Dock has run RemoveWindow and a missed removal leaves a closed window in
        // HostWindows, which crashes the next window drag.
        dockWindow?.Factory?.HostWindows.Remove(this);
        if (dockWindow is not null) dockWindow.Host = null;
        Window = null;

        // A host built but never presented has no platform window to close; calling Close() on it
        // would throw rather than tidy anything up.
        if (PlatformImpl is not null) Close();
    }

    /// <summary>
    /// True when the floated layout contains at least one <see cref="ITool"/>. Walks the dock tree
    /// rooted at this host window's <see cref="IDockWindow.Layout"/>. Defensive against the partially
    /// torn-down state present during close: every dereference is null-guarded.
    /// </summary>
    internal bool FloatsAnyTool()
    {
        var layout = Window?.Layout;
        return layout is not null && ContainsTool(layout);
    }

    private static bool ContainsTool(IDockable dockable)
    {
        if (dockable is ITool)
            return true;

        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (child is not null && ContainsTool(child))
                    return true;
            }
        }

        return false;
    }
}
