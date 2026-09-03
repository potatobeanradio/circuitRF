using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using CircuitRF.WBond;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.WBond;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.Layout;

public partial class LayoutEditorView : UserControl
{
    private LayoutDocument? _subscribedDoc;

    public LayoutEditorView()
    {
        InitializeComponent();

        LayoutCanvasCtrl.ViewportChanged     += (_, _) => SyncRulers();
        LayoutCanvasCtrl.LayoutUpdated       += (_, _) => SyncRulers();
        LayoutCanvasCtrl.CursorWorldChanged  += OnCanvasCursorWorldChanged;
        LayoutCanvasCtrl.FrameUnknownLayers  += OnFrameUnknownLayers;
        LayoutCanvasCtrl.FrameMissingInstanceCellRefs += OnFrameMissingInstanceCellRefs;
        LayoutCanvasCtrl.InstanceDoubleTapped          += OnInstanceDoubleTapped;

        LayoutCanvasCtrl.ClipboardCopyRequested        += async (_, _) => await OnClipboardCopy();
        LayoutCanvasCtrl.ClipboardCutRequested         += async (_, _) => await OnClipboardCut();
        LayoutCanvasCtrl.ClipboardPasteRequested        += async (_, _) => await OnClipboardPaste(inPlace: false);
        LayoutCanvasCtrl.ClipboardPasteInPlaceRequested += async (_, _) => await OnClipboardPaste(inPlace: true);
        // Ctrl+D prompts for the copy's offset exactly as the context menu's own "Duplicate…" does —
        // same method, so the two surfaces cannot disagree (owner, 2026-08-27).
        LayoutCanvasCtrl.DuplicateRequested             += async (_, _) => await LayoutCanvasCtrl.ShowDuplicateDialogAsync();

        // brief-layout-testing-fixes.md item 3/R-fix-3: a click into the canvas always re-focuses it
        // (GotFocus fires whenever focus WASN'T already here — e.g. after a project-tree click moved
        // it away), which is exactly the signal that this document's Properties/undo/save-scope
        // routing needs re-asserting, since Dock's own ActiveDockable never actually changed.
        LayoutCanvasCtrl.GotFocus += (_, _) => _subscribedDoc?.NotifyCanvasInteracted();

        DataContextChanged += (_, _) => SyncRulerUnits();
        DataContextChanged += OnDataContextChangedForFocus;

        // A wirebond cell's overlay resolves its wire palette from the theme, and it is a plain
        // object with no theme notifications of its own (WB40). BOTH signals are needed and they are
        // different events: this one is light-vs-dark, and ThemeService.ThemeChanged (subscribed on
        // attach, because it is a static event) is a different THEME being selected. Without the
        // second, picking a new theme repainted the layout underneath and left the wires in the old
        // colours — the canvas invalidates itself, but it redraws the overlay from the stale
        // WBondRenderTheme this view handed it (owner, 2026-08-17).
        ActualThemeVariantChanged += (_, _) => ApplyCanvasOverlay();

        // Focus-independent Escape handler — mirrors SchematicView/SymbolEditorView's
        // OnViewKeyDownTunnel. Window.KeyBindings (WorkspaceWindow.axaml's "Escape" ->
        // DisarmPlacementCommand) are processed before visual-tree routing and always mark the
        // event Handled, so LayoutCanvas's own plain bubble-phase KeyDown handler never fires for
        // Escape otherwise — this tunnel handler (registered with handledEventsToo: true) claims
        // Escape first and forwards it to the VM directly.
        this.AddHandler(
            InputElement.KeyDownEvent,
            OnViewKeyDownTunnel,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    // ── The host surface (WB39a) ──────────────────────────────────────────────
    //
    // wbond.md §6.11: the wBond editor HOSTS this control rather than transcribing it. Everything a
    // host legitimately needs from the canvas is reached through these few members — deliberately a
    // handful of pass-throughs rather than exposing LayoutCanvas itself, and never by walking the
    // visual tree, which would break silently the first time this view's XAML is restructured.

    /// <summary>
    /// Something drawn over the layout canvas and given first refusal on its input — the wBond wire
    /// layer (WB23) is the one implementation. Null for an ordinary layout document.
    ///
    /// <para><b>A host's overlay outranks the frame's own.</b> In the wBond editor the wires are the
    /// DOCUMENT, and they stay on screen while the user pushes into a sub-cell to nudge the pad under
    /// them (WB27) — so a wirebond cell reached from there must not replace them with its own. In the
    /// ordinary Layout Editor there is no host overlay, and each frame brings whatever wires its cell
    /// has (WB40).</para>
    /// </summary>
    public ILayoutCanvasOverlay? CanvasOverlay
    {
        get => _hostOverlay;
        set { _hostOverlay = value; ApplyCanvasOverlay(); }
    }

    private ILayoutCanvasOverlay? _hostOverlay;
    private WBondLayoutOverlay? _frameOverlay;

    /// <summary>
    /// Puts the right overlay on the canvas for the frame currently on screen, and gives a wirebond
    /// cell's own overlay the two things it cannot resolve for itself: the wire palette, and a repaint
    /// when its geometry changes.
    /// </summary>
    private void ApplyCanvasOverlay()
    {
        var frameOverlay = _hostOverlay is null
            ? (DataContext as LayoutDocument)?.ActiveViewModel.WireOverlay
            : null;

        if (!ReferenceEquals(_frameOverlay, frameOverlay))
        {
            if (_frameOverlay is not null) _frameOverlay.OverlayChanged -= OnFrameOverlayChanged;
            _frameOverlay = frameOverlay;
            if (_frameOverlay is not null) _frameOverlay.OverlayChanged += OnFrameOverlayChanged;
        }

        if (_frameOverlay is not null) _frameOverlay.Theme = ResolveWireTheme();

        LayoutCanvasCtrl.CanvasOverlay = _hostOverlay ?? _frameOverlay;
    }

    // A WIRE selection is enough for Rotate/Mirror (LayoutEditorViewModel.RotateAvailability), and it
    // lives in the wBond overlay rather than in the view model's own selection — so this repaint
    // signal, not SelectionStatusText, is where a wire-only selection change becomes observable here.
    private void OnFrameOverlayChanged()
    {
        LayoutCanvasCtrl.InvalidateOverlay();
        UpdateSelectionButtonStates();
    }

    // ── The two wBond panel buttons (wbond.md §10.1) ──────────────────────────

    /// <summary>
    /// Shows the two wBond panel buttons only where they can do something: on a WIREBOND CELL, and only
    /// with a workspace shell in reach.
    ///
    /// <para>The second half is what keeps them out of the standalone wBond app (owner, 2026-08-17): that
    /// window hosts both panels inline and has no dock at all, so a button to show one has nothing to
    /// show it in. It is the same "is a shell reachable" test <see cref="OnCheckDesignRules"/> and
    /// <see cref="OnOpenEmSetup"/> already use for "can I open a document from here".</para>
    /// </summary>
    private void UpdateWirePanelButtonStates()
    {
        var workspace = ResolveWorkspace();
        bool show = (DataContext as LayoutDocument)?.ActiveViewModel.HasWireDesign == true
                 && workspace is not null;

        WirePanelSeparator.IsVisible = show;
        WireProfileBtn.IsVisible = show;
        WireInductanceBtn.IsVisible = show;
        WireDrawBtn.IsVisible = show;
        WireRotateBtn.IsVisible = show;
        WireTransformBtn.IsVisible = show;
        WireExportTouchstoneBtn.IsVisible = show;
        WireCompareModelBtn.IsVisible = show;

        SubscribeToPanelVisibility(workspace);
        UpdateWirePanelCheckedStates();
    }

    /// <summary>
    /// Pushes the two buttons' CHECKED state from the dock tree (owner, 2026-08-17: the buttons said
    /// nothing about whether their panel was on screen).
    ///
    /// <para>Read from the workspace rather than tracked here, and re-read on every notification: a
    /// panel is also closed by its own tab X, dragged into a floating window, or replaced wholesale
    /// by a layout restore — none of which pass through this view.</para>
    /// </summary>
    private void UpdateWirePanelCheckedStates()
    {
        var workspace = _panelVisibilityWorkspace;

        WireProfileBtn.IsChecked = workspace?.IsToolPanelShowing(DockPanelIds.WBondProfile) == true;
        WireInductanceBtn.IsChecked = workspace?.IsToolPanelShowing(DockPanelIds.WBondInductance) == true;
    }

    private WorkspaceViewModel? _panelVisibilityWorkspace;

    /// <summary>
    /// Drops the panel-visibility subscription when this view leaves the tree. The workspace outlives
    /// every document view, so a handler left on it holds a dead view — and every torn-off or closed
    /// document would add another.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SubscribeToPanelVisibility(null);
        ThemeService.ThemeChanged -= OnActiveThemeChanged;
    }

    /// <summary>
    /// A different THEME was selected (not a light/dark flip) — re-resolve the wire palette and
    /// repaint. Subscribed on attach and dropped on detach because <c>ThemeService.ThemeChanged</c> is
    /// a static, process-wide event: a handler left on it holds every document view ever opened.
    /// </summary>
    private void OnActiveThemeChanged(object? sender, EventArgs e)
    {
        ApplyCanvasOverlay();
        LayoutCanvasCtrl.InvalidateOverlay();
    }

    /// <summary>
    /// Re-establishes it on the way back in. A document view is re-realised by a dock rebuild without
    /// its DataContext ever changing, and the subscription above was dropped when it left — so
    /// without this the buttons would stop tracking after the first tear-off or layout reset.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateWirePanelButtonStates();

        // Unsubscribe first: a re-attach must not stack a second handler on a static event.
        ThemeService.ThemeChanged -= OnActiveThemeChanged;
        ThemeService.ThemeChanged += OnActiveThemeChanged;
        ApplyCanvasOverlay();   // the theme may have moved while this view was out of the tree
    }

    /// <summary>Re-points the panel-visibility subscription; idempotent, and unsubscribes first.</summary>
    private void SubscribeToPanelVisibility(WorkspaceViewModel? workspace)
    {
        if (ReferenceEquals(_panelVisibilityWorkspace, workspace)) return;

        if (_panelVisibilityWorkspace is not null)
            _panelVisibilityWorkspace.ToolPanelVisibilityChanged -= UpdateWirePanelCheckedStates;

        _panelVisibilityWorkspace = workspace;

        if (_panelVisibilityWorkspace is not null)
            _panelVisibilityWorkspace.ToolPanelVisibilityChanged += UpdateWirePanelCheckedStates;
    }

    // P and A are handled by the SHELL WINDOW's own tunnel handler, not here — see
    // WorkspaceWindow.TryHandleWirePanelKeys. A key handler on a view, gated on where keyboard focus is,
    // cannot survive an action that MOVES focus, which is exactly what showing or hiding a dockable does.

    /// <summary>
    /// <b>Transform Wires…</b> — the wBond editor's own dialog, on the same wire editor (owner,
    /// 2026-08-17: "perhaps we need also add the whole Transform button back in for when there's
    /// wirebonds").
    ///
    /// <para>The same call the wBond editor makes, deliberately: the dialog owns rotate, scale and
    /// translate by typed values, and a second entry point that reimplemented any of that would be a
    /// second set of arithmetic to keep in step.</para>
    ///
    /// <para>Refused with nothing selected rather than opening on nothing — the dialog's whole
    /// subject is the selection, and it says so.</para>
    /// </summary>
    private async void OnWireTransform(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as LayoutDocument)?.ActiveViewModel is not { WireEditor: { } editor } vm) return;

        if (editor.Selection.IsEmpty)
        {
            vm.ReportError("Select one or more wires to transform.");
            return;
        }

        int touched = await WBondTransformDialog.ShowAsync(
            TopLevel.GetTopLevel(this) as Window, editor, editor.DisplayUnit);

        if (touched > 0) InvalidateOverlay();
    }

    /// <summary>
    /// Export Touchstone, from the layout editor — because a <c>.clay</c> with a <c>.wBond</c> beside it
    /// has no <c>WBondEditorView</c> in it, so this editor's wire group was the only place the action
    /// could be reached from and it was not there (owner, 2026-08-18).
    ///
    /// <para>Runs <see cref="WBondPublishCommands"/>, the same implementation <c>WBondEditorView</c>
    /// calls.</para>
    /// </summary>
    private async void OnWireExportTouchstone(object? sender, RoutedEventArgs e) =>
        await RunWirePublishAsync(WBondPublishCommands.ExportTouchstoneAsync);

    private async void OnWireCompareDistributedModel(object? sender, RoutedEventArgs e) =>
        await RunWirePublishAsync(WBondPublishCommands.CompareDistributedModelAsync);

    /// <summary>
    /// This layout's message sink, for the live progress rows a wirebond computation posts.
    ///
    /// <para>Resolved through the workspace rather than through the layout's own injected sink: a
    /// <c>LayoutEditorViewModel</c> built without one (a torn-off window, a test) would otherwise run
    /// silently, and the panel is where the EM run already reports.</para>
    /// </summary>
    private static IMessageSink? ResolveMessages() => ResolveWorkspace()?.Messages;

    /// <summary>
    /// The shared half: find this layout's wirebond design and a window to parent a dialog on, run the
    /// action, and report through the layout's own message sink.
    /// </summary>
    private async Task RunWirePublishAsync(
        Func<Window, WBondDesign, IMessageSink?, Task<WBondPublishCommands.Outcome>> action)
    {
        if ((DataContext as LayoutDocument)?.ActiveViewModel is not { WireDesign: { } design } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var outcome = await action(owner, design, ResolveMessages());
        if (outcome.IsSilent) return;

        // This editor's only report surface IS the Messages panel, so an outcome the command has
        // already posted there must not be posted again — see WBondPublishCommands.Outcome.Posted for
        // what that duplicate cost (the linkless copy landed last, below the line with the link).
        if (outcome.Posted) return;

        if (outcome.IsWarning) vm.ReportWarning(outcome.Message);
        else vm.ReportMessage(outcome.Message);
    }

    private void OnToggleWireProfilePanel(object? sender, RoutedEventArgs e) =>
        ToggleWirePanel(DockPanelIds.WBondProfile);

    private void OnToggleWireInductancePanel(object? sender, RoutedEventArgs e) =>
        ToggleWirePanel(DockPanelIds.WBondInductance);

    /// <summary>
    /// Toggles a wBond panel — and deliberately does NOT touch keyboard focus.
    ///
    /// <para>An earlier version re-asserted canvas focus here, to keep the P/A keys working after a close
    /// moved focus away. That was a patch on the symptom and lost the race often enough to be useless; the
    /// shell window handles those keys now, without caring where focus is, so nothing here has to fight
    /// for it — and focus is left wherever the user actually wants it, including inside the panel that has
    /// just appeared.</para>
    /// </summary>
    private void ToggleWirePanel(string panelId) =>
        ResolveWorkspace()?.ToggleToolPanelCommand.Execute(panelId);

    /// <summary>
    /// The workspace shell's view model, or null when there is none — a torn-off window with no shell in
    /// reach, or the standalone wBond binary, which has no <c>WorkspaceWindow</c> at all.
    ///
    /// <para>Resolved by walking the application's own windows, the same mechanism
    /// <c>TornOffFileMenuView</c> and this view's own DRC and EM buttons already use, and for the same
    /// reason: this view's DataContext is a <see cref="LayoutDocument"/>, not the workspace.</para>
    /// </summary>
    private static WorkspaceViewModel? ResolveWorkspace() =>
        Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.OfType<WorkspaceWindow>()
                              .Select(w => w.DataContext as WorkspaceViewModel)
                              .FirstOrDefault(v => v is not null)
            : null;

    private WBondRenderTheme ResolveWireTheme() =>
        WBondRenderTheme.FromTheme(
            ThemeService.Active,
            ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light);

    /// <summary>Repaints the overlay without disturbing the layout's path cache (WB17).</summary>
    public void InvalidateOverlay() => LayoutCanvasCtrl.InvalidateOverlay();

    /// <summary>Repaints the layout itself.</summary>
    public void InvalidateCanvas() => LayoutCanvasCtrl.InvalidateVisual();

    /// <summary>Puts keyboard focus on the canvas, so its own key handling applies.</summary>
    public void FocusCanvas() => LayoutCanvasCtrl.Focus();

    public void ZoomCanvasToFit() => LayoutCanvasCtrl.ZoomToFit();
    public void ZoomCanvasIn()    => LayoutCanvasCtrl.ZoomIn();
    public void ZoomCanvasOut()   => LayoutCanvasCtrl.ZoomOut();
    public void ZoomCanvas1To1()  => LayoutCanvasCtrl.Zoom1To1();

    /// <summary>
    /// Whether this view's own rulers are showing. A host with a ruler switch of its own drives it
    /// from here rather than hosting a second pair of ruler strips — the wBond editor's one toggle
    /// covers both of its canvases that way (owner, 2026-08-16).
    /// </summary>
    public bool RulersVisible
    {
        get => RulerRow.IsVisible;
        set { RulerRow.IsVisible = value; VRuler.IsVisible = value; }
    }

    /// <summary>
    /// Suppresses the torn-off File menu. Set by a host that is itself a document: a wBond editor
    /// floated into its own window already has one File menu, and the layout half must not add a
    /// second one describing a different file.
    /// </summary>
    public bool IsHostedInAnotherDocument
    {
        get => !FileMenuHost.IsVisible;
        set => FileMenuHost.IsVisible = !value;
    }

    // ── Keyboard shortcuts (tunnel — see constructor comment) ─────────────────

    private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!LayoutCanvasCtrl.IsKeyboardFocusWithin) return; // a toolbar text field owns its own Escape
        if (DataContext is not LayoutDocument doc) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl)
        {
            // Push Into Cell: Ctrl/⌘+] — mirrors SchematicView's identical keyboard path exactly.
            if (e.Key == Key.OemCloseBrackets)
            {
                var inst = doc.ActiveViewModel.SingleSelectedInstance;
                if (inst is not null) DoPushInto(doc, inst);
                e.Handled = true;
                return;
            }
            // Pop Out: Ctrl/⌘+[
            if (e.Key == Key.OemOpenBrackets)
            {
                DoPopOut(doc);
                e.Handled = true;
                return;
            }
        }

        // T opens the wire Transform dialog — the wBond editor's own key for it. Claimed ONLY when
        // there is a wire selection to transform, so T stays free on every ordinary layout and on a
        // wirebond cell with nothing selected (owner, 2026-08-17).
        if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.None
            && doc.ActiveViewModel.WireEditor is { } wireEditor && !wireEditor.Selection.IsEmpty)
        {
            OnWireTransform(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;
        doc.ActiveViewModel.OnKeyDown(e.Key, e.KeyModifiers);
        e.Handled = true;
    }

    // ── Hierarchy navigation (L3b, docs/sonnet-briefs/brief-L3b-hierarchy-navigation.md) ──────────
    // Capture-before-navigate-then-apply, centralized here so every entry point (double-click,
    // toolbar Pop Out, breadcrumb click, and this file's own Ctrl+]/Ctrl+[ handling above) restores
    // the SAME per-frame viewport the same way — see LayoutDocument's NavFrame doc comment for why
    // this capture step exists at all (pan/zoom is canvas-owned, not VM-owned, unlike selection).

    private void DoPushInto(LayoutDocument doc, LayoutInstance instance)
    {
        if (doc.Hierarchy is not { } host) return;
        if (!host.CanPushInto(instance, doc.ActiveViewModel, out var reason))
        {
            if (reason is not null) doc.ActiveViewModel.ReportError($"Can't push into cell: {reason}");
            return;
        }
        doc.CaptureActiveViewport(LayoutCanvasCtrl.CurrentViewport);
        host.PushIntoCell(doc, instance);
        ApplyFrameViewport(doc);
    }

    private void DoPopOut(LayoutDocument doc)
    {
        if (!doc.CanPopOut) return;
        doc.CaptureActiveViewport(LayoutCanvasCtrl.CurrentViewport);
        doc.Hierarchy?.PopOutOf(doc);
        ApplyFrameViewport(doc);
    }

    private void DoPopToLevel(LayoutDocument doc, int frameIndex)
    {
        if (frameIndex == doc.NavDepth) return;
        doc.CaptureActiveViewport(LayoutCanvasCtrl.CurrentViewport);
        doc.Hierarchy?.PopToLevel(doc, frameIndex);
        ApplyFrameViewport(doc);
    }

    private void ApplyFrameViewport(LayoutDocument doc)
    {
        if (doc.ActiveFrameSavedViewport is { } vp) LayoutCanvasCtrl.SetViewport(vp);
        else LayoutCanvasCtrl.ZoomToFit();
        LayoutCanvasCtrl.InvalidateVisual();
    }

    /// <summary>
    /// brief-L5-followups-3.md §1 (R-L5h-1/2), corrected per owner follow-up (2026-07-29, 4th report):
    /// the dispatch decision itself — a PCell instance must never reach <see cref="DoPushInto"/> at all
    /// (every previous round guarded push-in AFTER it was already called, which is why the user kept
    /// seeing push-in's own polite refusal message instead of anything opening).
    /// <see cref="LayoutHierarchyResolver.IsPCellInstance"/> is checked FIRST, here, before push-in is
    /// ever reached; a PCell instance is selected (so the Properties Inspector shows it too, staying in
    /// sync) AND a popup <see cref="LayoutPCellParameterDialog"/> is opened — the Layout Editor's
    /// counterpart to the Schematic Editor's own double-click-opens-<c>ParameterEditorDialog</c>
    /// behavior (<c>SchematicView.axaml.cs :: OnComponentDoubleTapped</c>), which R-L5h-2's original
    /// "the SAME editor, not a new dialog" reading had wrongly read as "route to the docked Properties
    /// panel only" — the owner's own repeated reports make clear a popup was always what was wanted, to
    /// match the schematic side exactly. The dialog hosts the identical
    /// <c>PCellParameterListView</c> the docked panel uses (never a second parameter-editing
    /// implementation) and is shown non-modally (<c>Window.Show</c>, not <c>ShowDialog</c>), matching
    /// the schematic dialog's own non-modal default. An ordinary (non-PCell) instance falls through to
    /// push-in exactly as before, including its own correct refusal reason for a genuinely unresolvable
    /// one.
    /// </summary>
    private void OnInstanceDoubleTapped(object? sender, LayoutInstance instance)
    {
        if (DataContext is not LayoutDocument doc) return;

        if (LayoutHierarchyResolver.IsPCellInstance(instance, doc.ActiveViewModel))
        {
            int index = doc.ActiveViewModel.Model.Instances.IndexOf(instance);
            if (index >= 0) doc.ActiveViewModel.SelectInstance(index);
            doc.NotifyCanvasInteracted(); // re-assert Properties/undo/save-scope routing (R-fix-3)

            var owner = TopLevel.GetTopLevel(this) as Window;
            var dialogVm = new LayoutShapePropertiesViewModel();
            dialogVm.SetContext(doc.ActiveViewModel);
            var dialog = new LayoutPCellParameterDialog { DataContext = dialogVm };
            dialog.Closed += (_, _) => dialogVm.SetContext(null); // unsubscribe from the layout VM
            dialog.Show(owner!); // owner may be null when no window parent (e.g. embedded in non-Window host)
            return;
        }

        DoPushInto(doc, instance);
    }

    // Mirrors SchematicView's OnToolbarPushIn/OnToolbarPopOut exactly: the toolbar buttons only fire
    // when a single selected instance is push-in-able / a pop is possible — the actual gating is
    // maintained live in UpdateHierarchyButtonStates (below), same shape as SchematicView's
    // UpdateDisableButtonStates.
    private void OnToolbarPushIn(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutDocument doc) return;
        var inst = doc.ActiveViewModel.SingleSelectedInstance;
        if (inst is not null) DoPushInto(doc, inst);
    }

    private void OnToolbarPopOut(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LayoutDocument doc) DoPopOut(doc);
    }

    private const string PushInDefaultTip = "Push Into Cell  (Ctrl+])";

    /// <summary>Mirrors SchematicView's UpdateDisableButtonStates for PushInBtn/PopOutBtn exactly:
    /// Push Into Cell enabled only when exactly one selected instance resolves to a pushable cell;
    /// Pop Out enabled whenever the active document's nav stack has depth &gt; 0. brief-L5-followups-3.md
    /// R-L5h-1/R13a: the button's own tooltip carries WHY it's disabled (e.g. a PCell selection's
    /// "generated; edit its parameters instead" reason) rather than the static default text — the one
    /// case a disabled push-in button already correctly refuses BEFORE this brief; this only makes the
    /// stated reason visible on hover instead of only in the Messages pane after a click.</summary>
    private void UpdateHierarchyButtonStates()
    {
        if (DataContext is not LayoutDocument doc)
        {
            PushInBtn.IsEnabled = false;
            ToolTip.SetTip(PushInBtn, PushInDefaultTip);
            PopOutBtn.IsEnabled = false;
            return;
        }
        var vm = doc.ActiveViewModel;
        var inst = vm.SingleSelectedInstance;
        bool canPush;
        string? reason = null;
        if (doc.Hierarchy is { } host) canPush = host.CanPushInto(inst, vm, out reason);
        else canPush = false;
        PushInBtn.IsEnabled = canPush;
        ToolTip.SetTip(PushInBtn, canPush ? PushInDefaultTip : reason ?? PushInDefaultTip);
        PopOutBtn.IsEnabled = doc.CanPopOut;
    }

    /// <summary>
    /// Rotate/Mirror follow the SELECTION, mirroring SchematicView's own UpdateDisableButtonStates —
    /// they used to sit permanently enabled here, so with nothing selected the click was a silent
    /// no-op (owner, 2026-08-27). The single source of truth is the view model's
    /// <c>RotateAvailability</c> (which <c>MirrorAvailability</c> forwards to), so the toolbar and the
    /// context menu's own Rotate items can never disagree about when the command can run — including
    /// its wire case, where a wirebond cell's selected WIRES are enough on their own.
    /// </summary>
    private void UpdateSelectionButtonStates()
    {
        bool canTransform = (DataContext as LayoutDocument)?.ActiveViewModel.RotateAvailability.CanExecute == true;
        RotateCcwBtn.IsEnabled = canTransform;
        RotateCwBtn.IsEnabled  = canTransform;
        MirrorHBtn.IsEnabled   = canTransform;
        MirrorVBtn.IsEnabled   = canTransform;
    }

    private void OnPopToTop(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LayoutDocument doc) DoPopToLevel(doc, 0);
    }

    private void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LayoutDocument doc && sender is Button btn && btn.Tag is int frameIndex)
            DoPopToLevel(doc, frameIndex);
    }

    // ── Activation focus — tab switch grabs keyboard focus (mirrors SchematicView/SymbolEditorView) ──

    private void OnDataContextChangedForFocus(object? sender, System.EventArgs e)
    {
        if (_subscribedDoc is not null)
        {
            _subscribedDoc.ActivationFocusRequested -= OnActivationFocusRequested;
            _subscribedDoc.ActiveViewModelChanged    -= OnActiveViewModelChangedForNav;
            _subscribedDoc.ExportGdsiiRequested       -= OnExportGdsiiRequestedFromMenu;
            _subscribedDoc.ExportDxfRequested         -= OnExportDxfRequestedFromMenu;
            _subscribedDoc.ExportGerberRequested      -= OnExportGerberRequestedFromMenu;
            _subscribedDoc.ExportBoardRequested       -= OnExportBoardRequestedFromMenu;
            _subscribedDoc.ZoomToFitRequested         -= OnZoomToFitRequestedFromMenu;
            _subscribedDoc.CutRequested                -= OnCutRequestedFromMenu;
            _subscribedDoc.CopyRequested                -= OnCopyRequestedFromMenu;
            _subscribedDoc.PasteRequested                -= OnPasteRequestedFromMenu;
        }
        RebindDrcZoomSubscription(null);
        _subscribedDoc = DataContext as LayoutDocument;
        if (_subscribedDoc is not null)
        {
            _subscribedDoc.ActivationFocusRequested += OnActivationFocusRequested;
            _subscribedDoc.ActiveViewModelChanged    += OnActiveViewModelChangedForNav;
            _subscribedDoc.ExportGdsiiRequested       += OnExportGdsiiRequestedFromMenu;
            _subscribedDoc.ExportDxfRequested         += OnExportDxfRequestedFromMenu;
            _subscribedDoc.ExportGerberRequested      += OnExportGerberRequestedFromMenu;
            _subscribedDoc.ExportBoardRequested       += OnExportBoardRequestedFromMenu;
            _subscribedDoc.ZoomToFitRequested         += OnZoomToFitRequestedFromMenu;
            _subscribedDoc.CutRequested                += OnCutRequestedFromMenu;
            _subscribedDoc.CopyRequested                += OnCopyRequestedFromMenu;
            _subscribedDoc.PasteRequested                += OnPasteRequestedFromMenu;
            RebindDrcZoomSubscription(_subscribedDoc.ActiveViewModel);
            if (_subscribedDoc.ConsumeActivationFocus()) FocusCanvasDeferred();
        }
        ApplyCanvasOverlay();   // WB40 — this frame's own wires, if its cell has any
        UpdateHierarchyButtonStates();
        UpdateSelectionButtonStates();
        UpdateWirePanelButtonStates();
    }

    // L3b: DisplayUnit/DbuPerMicron and the hierarchy button enable-state are both read off whichever
    // frame is ACTIVE — a push-in into a sub-cell with a different resolution/unit must relabel the
    // rulers too, and Pop Out's enabled-ness depends on nav depth. Both subscriptions, unlike toolbar
    // bindings, are code-behind (not AXAML), so they have to be explicitly re-pointed at the new
    // active VM on every navigation — AXAML's own {Binding ActiveViewModel.X} paths rebind for free
    // through Avalonia's binding engine; this does not.
    private void OnActiveViewModelChangedForNav(object? sender, System.EventArgs e)
    {
        if (DataContext is LayoutDocument doc)
        {
            RebindRulerUnitsSubscription(doc);
            // Same reason as the ruler-unit subscription immediately above: this is a code-behind
            // subscription on the ACTIVE frame's own view model, and a push-in swaps that instance.
            RebindDrcZoomSubscription(doc.ActiveViewModel);
        }
        ApplyCanvasOverlay();   // WB40 — pushing into a wirebond cell brings ITS wires with it
        UpdateHierarchyButtonStates();
        UpdateSelectionButtonStates();
        UpdateWirePanelButtonStates();
    }

    // ── L5b DRC ───────────────────────────────────────────────────────────────

    private LayoutEditorViewModel? _drcZoomVm;

    private void RebindDrcZoomSubscription(LayoutEditorViewModel? vm)
    {
        if (ReferenceEquals(_drcZoomVm, vm)) return;
        if (_drcZoomVm is not null) _drcZoomVm.ZoomToRegionRequested -= OnDrcZoomToRegion;
        _drcZoomVm = vm;
        if (_drcZoomVm is not null) _drcZoomVm.ZoomToRegionRequested += OnDrcZoomToRegion;

        RebindWBondDropSubscription(vm);
    }

    private LayoutEditorViewModel? _wBondDropVm;

    /// <summary>
    /// Follows the active frame's "this layout just gained wires" signal (WB40b). Rebound alongside
    /// the DRC subscription above and for the same reason: it is a code-behind subscription on the
    /// ACTIVE frame's own view model, and a push-in swaps that instance.
    /// </summary>
    private void RebindWBondDropSubscription(LayoutEditorViewModel? vm)
    {
        if (ReferenceEquals(_wBondDropVm, vm)) return;
        if (_wBondDropVm is not null) _wBondDropVm.WireLayerAdded -= OnWireLayerAdded;
        _wBondDropVm = vm;
        if (_wBondDropVm is not null) _wBondDropVm.WireLayerAdded += OnWireLayerAdded;
    }

    /// <summary>
    /// A layout that has just gained wires — dropped from the palette, or pasted in — gets the same
    /// welcome Update Layout from Schematic gives one: the two panels, arranged the first time this
    /// installation ever needs them. Someone who has just put a wirebond somewhere has no reason to
    /// know two panels exist.
    /// </summary>
    private void OnWireLayerAdded()
    {
        UpdateWirePanelButtonStates();
        ResolveWorkspace()?.ShowWBondPanels();
    }

    private void OnDrcZoomToRegion(Bbox region) => LayoutCanvasCtrl.ZoomToRegion(region);

    /// <summary>
    /// Toolbar entry point.
    ///
    /// <para>Delegates to the SAME <c>WorkspaceViewModel.CheckDesignRulesCommand</c> the Design menu
    /// runs, rather than calling <c>RunDrc</c> here. Not tidiness: that command also brings the DRC
    /// panel forward, and a check whose findings land in a panel the user cannot see is a check that
    /// reads as having done nothing. Two entry points doing the same thing differently is exactly how
    /// that inconsistency gets shipped.</para>
    ///
    /// <para>The workspace view model is resolved by walking the application's own windows — the same
    /// mechanism <c>TornOffFileMenuView</c> already uses, and for the same reason: this view's own
    /// DataContext is a <c>LayoutDocument</c>, not the workspace. A torn-off window with no workspace
    /// shell reachable falls back to running the check directly, so the button never does nothing.</para>
    /// </summary>
    /// <summary>
    /// EM toolbar button — opens (creating on first use) the <c>.cem</c> EM setup for THIS layout.
    /// Resolves the workspace by walking the application's own windows, exactly like
    /// <see cref="OnCheckDesignRules"/> and for the same reason (this view's DataContext is a
    /// <c>LayoutDocument</c>, not the workspace). A scratch layout with no path yet has no name to
    /// derive the setup's from, so it is reported rather than silently doing nothing.
    /// </summary>
    private void OnOpenEmSetup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutDocument doc) return;

        if (doc.FilePath is not { Length: > 0 } clayPath)
        {
            doc.ActiveViewModel.ReportWarning(
                "Save this layout first — its EM setup is named after the layout file.");
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var workspace = desktop.Windows
                .OfType<WorkspaceWindow>()
                .Select(w => w.DataContext as WorkspaceViewModel)
                .FirstOrDefault(v => v is not null);

            if (workspace is not null)
            {
                workspace.OpenOrCreateEmSetupForLayout(clayPath);
                return;
            }
        }

        // A torn-off window with no workspace shell in reach — opening a document needs the shell,
        // so say so rather than appearing to do nothing.
        doc.ActiveViewModel.ReportWarning(
            "Open the EM setup from the main window — a torn-off layout window cannot open documents.");
    }

    private void OnCheckDesignRules(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutDocument doc) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var workspace = desktop.Windows
                .OfType<WorkspaceWindow>()
                .Select(w => w.DataContext as WorkspaceViewModel)
                .FirstOrDefault(v => v is not null);

            if (workspace?.CheckDesignRulesCommand.CanExecute(null) == true)
            {
                workspace.CheckDesignRulesCommand.Execute(null);
                return;
            }
        }

        // No workspace shell in reach (a torn-off window). Run the check and report it here; the
        // markers still draw and the panel, if it is open, still fills.
        var vm     = doc.ActiveViewModel;
        var result = vm.RunDrc();

        Ui.Layout.Drc.DrcRunReport.Post(vm.MessageSink, result);
    }

    /// <summary>
    /// R16d's pre-export check, shared by GDSII, DXF and Gerber so the three can never drift on what
    /// "checked before writing" means. Returns false only when the user cancelled.
    ///
    /// <para>Off by preference → no check, no dialog, no delay. On and clean → no dialog either (see
    /// <c>DrcExportGateDialog</c> for why a "nothing found" modal is worse than none). On and dirty →
    /// the violations are shown, the DRC panel's own list is populated behind it, and the export
    /// still goes ahead if the user says so.</para>
    /// </summary>
    private async Task<bool> ConfirmDesignRulesBeforeExportAsync(
        LayoutEditorViewModel vm, Window owner, string format)
    {
        if ((AppPreferencesIo.Load().CheckDrcOnExport ?? true) is false) return true;

        DrcRunResult result;
        try { result = vm.RunDrc(); }
        catch (Exception ex)
        {
            // A check that itself failed must never be the thing that stops an export.
            vm.ReportWarning($"DRC before export: the check could not run ({ex.Message}). Exporting anyway.");
            return true;
        }

        foreach (var d in result.Diagnostics) vm.ReportWarning($"DRC — {d}");
        if (result.IsClean) return true;

        return await new DrcExportGateDialog(result, format).ShowDialog<bool>(owner);
    }

    private void OnActivationFocusRequested()
    {
        _subscribedDoc?.ConsumeActivationFocus();
        FocusCanvasDeferred();
    }

    private void FocusCanvasDeferred() =>
        Dispatcher.UIThread.Post(() => LayoutCanvasCtrl.Focus(), DispatcherPriority.Background);

    // brief-layout-testing-fixes.md item 8: File → Export → GDSII/DXF fire these via
    // LayoutDocument.RequestExportGdsii/RequestExportDxf — the SAME toolbar code path, never a second
    // export entry point (item 5/R-fix-4's own "route every entry point through the same accessor").
    private void OnExportGdsiiRequestedFromMenu() => _ = OnExportGdsiiAsync();
    private void OnExportDxfRequestedFromMenu() => _ = OnExportDxfAsync();
    private void OnExportGerberRequestedFromMenu() => _ = OnExportGerberAsync();
    private void OnExportBoardRequestedFromMenu() => _ = OnExportBoardAsync();

    /// <summary>
    /// File → Export → Board. Mirrors the Gerber path: analyze, show what the format cannot carry,
    /// pick a file, write.
    ///
    /// <para>There is deliberately no design-rule confirmation step here, unlike Gerber's — this format
    /// carries no design rules at all (they left its board file at the 20211014 epoch), so the report
    /// says what the technology holds that was NOT written instead of asking the user to confirm rules
    /// that would then be silently dropped.</para>
    /// </summary>
    private async Task OnExportBoardAsync()
    {
        if (Vm is not { } vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        if (vm.CurrentCellDir is not { Length: > 0 } cellDir)
        {
            vm.ReportError("Export Board: save this layout to a cell before exporting.");
            return;
        }

        PcbExport.ExportPlan plan;
        try { plan = PcbExport.Analyze(cellDir, vm.Technology, vm.Model.DbuPerMicron, vm.Model); }
        catch (Exception ex) { vm.ReportError($"Export Board: {ex.Message}"); return; }

        if (!plan.CanWrite)
        {
            foreach (var line in PcbExport.Describe(plan)) vm.ReportError(line);
            return;
        }

        var cellName = Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Board",
            SuggestedFileName = cellName + ".kicad_pcb",
            DefaultExtension = "kicad_pcb",
            FileTypeChoices = [new FilePickerFileType("Board") { Patterns = ["*.kicad_pcb"] }],
        });
        if (file is null) return;

        try
        {
            PcbExport.Write(file.Path.LocalPath, plan);
            foreach (var line in PcbExport.Describe(plan)) vm.ReportMessage(line);
            vm.ReportMessage("Exported Board", file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export Board: {ex.Message}");
        }
    }

    // View->Zoom to Fit dispatches here from WorkspaceViewModel via LayoutDocument.RequestZoomToFit().
    private void OnZoomToFitRequestedFromMenu() => LayoutCanvasCtrl.ZoomToFit();

    // Workspace toolbar Cut/Copy/Paste dispatch here — the same OnClipboardCopy/Cut/Paste this view
    // already wires to the canvas's own Ctrl+C/X/V events (see the constructor).
    private void OnCutRequestedFromMenu()   => _ = OnClipboardCut();
    private void OnCopyRequestedFromMenu()  => _ = OnClipboardCopy();
    private void OnPasteRequestedFromMenu() => _ = OnClipboardPaste(inPlace: false);

    private void SyncRulers()
    {
        HRuler.SetViewport(LayoutCanvasCtrl.CurrentPanX, LayoutCanvasCtrl.CurrentPanY, LayoutCanvasCtrl.CurrentZoom,
            LayoutCanvasCtrl.Bounds.Width, LayoutCanvasCtrl.Bounds.Height);
        VRuler.SetViewport(LayoutCanvasCtrl.CurrentPanX, LayoutCanvasCtrl.CurrentPanY, LayoutCanvasCtrl.CurrentZoom,
            LayoutCanvasCtrl.Bounds.Width, LayoutCanvasCtrl.Bounds.Height);
    }

    // Switching the display-unit combo relabels both rulers and moves no geometry (L0b's invariant,
    // now visible) — re-read the VM's current DisplayUnit whenever the document (re)binds.
    private void SyncRulerUnits()
    {
        if (DataContext is not LayoutDocument doc) return;
        RebindRulerUnitsSubscription(doc);
    }

    private LayoutEditorViewModel? _subscribedVmForRulers;

    private void RebindRulerUnitsSubscription(LayoutDocument doc)
    {
        if (_subscribedVmForRulers is not null)
            _subscribedVmForRulers.PropertyChanged -= OnActiveVmPropertyChangedForRulers;

        var vm = doc.ActiveViewModel;
        _subscribedVmForRulers = vm;
        vm.PropertyChanged += OnActiveVmPropertyChangedForRulers;

        HRuler.SetUnits(vm.Model.DbuPerMicron, vm.DisplayUnit);
        VRuler.SetUnits(vm.Model.DbuPerMicron, vm.DisplayUnit);
    }

    private void OnActiveVmPropertyChangedForRulers(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LayoutEditorViewModel vm) return;
        if (e.PropertyName is nameof(LayoutEditorViewModel.DisplayUnit))
        {
            HRuler.SetUnits(vm.Model.DbuPerMicron, vm.DisplayUnit);
            VRuler.SetUnits(vm.Model.DbuPerMicron, vm.DisplayUnit);
        }
        // Selection changes (shape or instance) drive PushInBtn's enabled state — mirrors
        // SchematicView's own Selection.Changed -> UpdateDisableButtonStates hook, just via
        // PropertyChanged since LayoutEditorViewModel's selection has no separate Changed event.
        else if (e.PropertyName is nameof(LayoutEditorViewModel.SelectionStatusText))
        {
            UpdateHierarchyButtonStates();
            UpdateSelectionButtonStates();
        }
        // WB40: a wirebond cell's wires can arrive while this document is already open — Update Layout
        // from Schematic writes the sidecar into a layout it has just brought to the front (§9.5). The
        // overlay is attached at DataContext and frame changes, neither of which happens then.
        else if (e.PropertyName is nameof(LayoutEditorViewModel.WireDesign))
        {
            ApplyCanvasOverlay();
            UpdateWirePanelButtonStates();
            LayoutCanvasCtrl.InvalidateOverlay();
        }
    }

    private void OnCanvasCursorWorldChanged(object? sender, (double X, double Y)? world)
    {
        HRuler.SetCursorWorld(world?.X);
        VRuler.SetCursorWorld(world?.Y);
        if (DataContext is LayoutDocument doc)
            doc.ActiveViewModel.SetCursorWorld(world?.X, world?.Y);
    }

    private void OnFrameUnknownLayers(IReadOnlyList<LayerKey> keys)
    {
        if (keys.Count == 0) return;
        if (DataContext is LayoutDocument doc)
            doc.ActiveViewModel.ReportUnknownLayers(keys);
    }

    private void OnFrameMissingInstanceCellRefs(IReadOnlyList<string> cellRefs)
    {
        if (cellRefs.Count == 0) return;
        if (DataContext is LayoutDocument doc)
            doc.ActiveViewModel.ReportMissingInstanceCellRefs(cellRefs);
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e) => LayoutCanvasCtrl.ZoomToFit();
    private void OnZoomIn(object? sender, RoutedEventArgs e)    => LayoutCanvasCtrl.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e)   => LayoutCanvasCtrl.ZoomOut();
    private void OnZoom1To1(object? sender, RoutedEventArgs e)  => LayoutCanvasCtrl.Zoom1To1();

    // ── Rotate (mirrors the Schematic Editor's own pair) ──────────────────────────────────────
    // Routed to ActiveViewModel, not the base session VM, so rotating while pushed into a sub-cell
    // edits the frame the user is actually looking at.

    private void OnRotateCcw(object? sender, RoutedEventArgs e)
        => (DataContext as LayoutDocument)?.ActiveViewModel?.RotateSelection(clockwise: false);

    private void OnRotateCw(object? sender, RoutedEventArgs e)
        => (DataContext as LayoutDocument)?.ActiveViewModel?.RotateSelection(clockwise: true);

    private void OnMirrorH(object? sender, RoutedEventArgs e)
        => (DataContext as LayoutDocument)?.ActiveViewModel?.MirrorSelection(horizontal: true);

    private void OnMirrorV(object? sender, RoutedEventArgs e)
        => (DataContext as LayoutDocument)?.ActiveViewModel?.MirrorSelection(horizontal: false);

    // ── Insert Bitmap (R-bmp-5, docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md) ──────
    // UI firewall: the StorageProvider file picker lives here in code-behind; the VM only ever sees
    // the resulting path. Placement (viewport-centred sizing) is LayoutCanvas.InsertBitmapAtViewportCenter.

    private async void OnInsertBitmap(object? sender, RoutedEventArgs e)
    {
        var picker = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (picker is null) return;

        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert Bitmap",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.tif", "*.webp" } }
            }
        });
        if (files.Count > 0)
            LayoutCanvasCtrl.InsertBitmapAtViewportCenter(files[0].Path.LocalPath);
    }

    // ── Shape context menu (L1-fix, brief-L1-fix-context-menu-stacking.md) ─────────────────────────
    // The ONE ContextMenu instance is declared in this view's XAML on LayoutCanvasCtrl; this handler
    // rebuilds its ItemsSource fresh from LayoutCanvasCtrl's recorded click, and cancels when nothing
    // should show (mirrors SymbolEditorView.axaml.cs's OnBitmapContextMenuOpening exactly).
    private void OnLayoutContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var target = LayoutCanvasCtrl.ConsumeContextMenuTarget();
        if (target is not { } t) { e.Cancel = true; return; }

        var items = LayoutCanvasCtrl.BuildContextMenuItems(t.Wx, t.Wy);
        if (sender is ContextMenu menu) menu.ItemsSource = items;
    }

    // ── Toolbar field commit (§1 R6 typed entry — LostFocus commits; Enter commits + refocuses canvas) ──

    private LayoutEditorViewModel? Vm => (DataContext as LayoutDocument)?.ActiveViewModel;

    // brief-snap-distance-and-geometry-snap.md §1 — the snap-distance ComboBox is editable (typed
    // entry, R-snp-3) AND offers a technology-relative ladder (R-snp-2); a ladder pick commits
    // immediately (SelectionChanged), typed text commits on LostFocus/Enter like every other toolbar
    // dimension field.
    private void OnSnapDistanceCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb) Vm?.CommitSnapDistanceText(cb.Text ?? "");
    }
    private void OnSnapDistanceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is ComboBox cb) { Vm?.CommitSnapDistanceText(cb.Text ?? ""); LayoutCanvasCtrl.Focus(); }
    }
    private void OnSnapDistanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string text }) Vm?.CommitSnapLadderSelection(text);
    }

    private void OnCornerRadiusCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitCornerRadiusText(tb.Text ?? "");
    }
    private void OnCornerRadiusKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) { Vm?.CommitCornerRadiusText(tb.Text ?? ""); LayoutCanvasCtrl.Focus(); }
    }

    private void OnPathWidthCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitPathWidthText(tb.Text ?? "");
    }
    private void OnPathWidthKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) { Vm?.CommitPathWidthText(tb.Text ?? ""); LayoutCanvasCtrl.Focus(); }
    }

    private void OnLabelHeightCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitLabelHeightText(tb.Text ?? "");
    }
    private void OnLabelHeightKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) { Vm?.CommitLabelHeightText(tb.Text ?? ""); LayoutCanvasCtrl.Focus(); }
    }

    // Live Rect W/H — gate 9: typing a value commits the shape at exactly that size. Both fields
    // stage first (CommitDrawWidthText/CommitDrawHeightText), Enter finalizes (CommitTypedRect).
    private void OnDrawWidthCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitDrawWidthText(tb.Text ?? "");
    }
    private void OnDrawWidthKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not TextBox tb) return;
        Vm?.CommitDrawWidthText(tb.Text ?? "");
        Vm?.CommitTypedRect();
        LayoutCanvasCtrl.Focus();
    }

    private void OnDrawHeightCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitDrawHeightText(tb.Text ?? "");
    }
    private void OnDrawHeightKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not TextBox tb) return;
        Vm?.CommitDrawHeightText(tb.Text ?? "");
        Vm?.CommitTypedRect();
        LayoutCanvasCtrl.Focus();
    }

    // ── Clipboard (L1f, docs/sonnet-briefs/brief-L1f-clipboard.md) ──────────────────────────────
    // Mirrors SchematicView.axaml.cs's OnClipboardCopy/Cut/Paste exactly: this view owns the actual
    // IClipboard traffic (via LayoutClipboard) and the layer-reconciliation dialog loop; the VM never
    // touches IClipboard or shows a dialog itself.

    /// <summary>
    /// The wire editor of the wirebond cell on screen (WB40), or null — the wires ARE part of this
    /// document's content, so the clipboard commands have to see them.
    /// </summary>
    private WBondViewModel? WireEditorWithSelection =>
        Vm?.WireEditor is { } editor && !editor.Selection.IsEmpty ? editor : null;

    private async Task OnClipboardCopy()
    {
        if (Vm is not { } vm) return;

        // WIRES first, when any are selected (owner, 2026-08-17: copy/paste of wires did nothing in
        // the Layout Editor). Routed through the wBond editor's OWN writer rather than a second
        // implementation, so a copy made here and a copy made there put byte-identical payloads —
        // and the same PDF/SVG/PNG alongside them — on the clipboard.
        if (await CopyWithWiresAsync()) return;

        // brief-L3a-followups.md §2/R-fix-2: BuildCopyPayload already carries BOTH selected shapes AND
        // selected instances (a mixed selection is now normal) — pass the whole payload straight
        // through rather than re-deriving a shapes-only fragment a second way.
        var payload = vm.BuildCopyPayload();
        if (payload is null) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        IntPtr ownerHwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // baseDir is what lets the graphic export resolve a placed instance's own cell — without it
        // a schematic-generated selection copies as an empty picture. The mesh — and, per owner
        // request, the DRC violation markers — ride along when showing, in the graphic only: neither
        // is geometry, so the JSON payload (what circuitRF's own paste reads) deliberately carries
        // none of either. vm.Overlay.DrcMarkers is already exactly "current markers, or empty when
        // the panel's toggle is off / nothing has been checked" (LayoutEditorViewModel.Drc.cs).
        await LayoutClipboard.CopyAsync(
            clipboard, payload, vm.Technology, ownerHwnd,
            vm.InstanceBaseDir,
            vm.ShowPlanarMesh ? vm.PlanarMeshReport : null,
            vm.ShowPlanarMesh ? vm.PlanarCurrentDensity : null,
            vm.Overlay.DrcMarkers.Count > 0 ? vm.Overlay.DrcMarkers : null);
    }

    /// <summary>
    /// Copies a selection that includes WIRES — wires alone, or wires and geometry together — and
    /// returns false when there are none, so the caller falls through to the geometry-only path it
    /// has always taken.
    ///
    /// <para>A mixed selection is wrapped in <see cref="WBondMixedClipboard"/>'s envelope and a
    /// single-kind one is not, which is what keeps this readable by every existing paste path: the
    /// Layout Editor's own <c>LayoutClipboard.PasteAsync</c> already unwraps the envelope for its
    /// half, and the wBond editor unwraps it for both.</para>
    /// </summary>
    private async Task<bool> CopyWithWiresAsync()
    {
        if (Vm is not { } vm || WireEditorWithSelection is not { } wires) return false;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;

        string? wiresJson = wires.CopySelection();
        if (string.IsNullOrEmpty(wiresJson)) return false;

        var fragment = vm.BuildCopyPayload();
        string? layoutJson = fragment is null ? null : LayoutFragment.Serialize(fragment);

        if (WBondMixedClipboard.Compose(wiresJson, layoutJson) is not { } text) return false;

        var (variant, _) = ClipboardRenderPolicy.Resolve();

        return await WBondClipboardWriter.CopyAsync(
            this, clipboard, text,
            WBondClipboardWriter.SelectionDesign(wires.Design, wires.Selection),
            WBondClipboardWriter.TransientLayout(fragment),
            vm.Technology, vm.InstanceBaseDir,
            ResolveWireTheme(),
            LayoutRenderTheme.FromTheme(ThemeService.Active, variant),
            _frameOverlay?.Thickness ?? WireThicknessMode.Thin);
    }

    private async Task OnClipboardCut()
    {
        // Read the wire selection BEFORE the copy: nothing in the copy clears it, but the order
        // states the dependency, and the count is what says whether anything was cut.
        var wires = WireEditorWithSelection;

        await OnClipboardCopy();

        // Both kinds go, matching what the copy just wrote — the wires and the geometry were on the
        // clipboard together, so leaving one of them behind would be a cut that half happened.
        if (wires is not null && wires.DeleteSelectedWires() > 0) InvalidateOverlay();

        Vm?.CutSelectionAfterCopy();
        LayoutCanvasCtrl.InvalidateVisual();
    }

    private async Task OnClipboardPaste(bool inPlace)
    {
        if (Vm is not { } vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        // A clipboard carrying WIRES is pasted whole, here, by the same arithmetic the wBond editor
        // uses — including the free-pitch offset that stops a second paste landing exactly on the
        // first (which makes the inductance fill singular; see PasteWiresAtFreePitch). The geometry
        // half rides on the SAME displacement, so a mixed paste arrives together rather than one half
        // following the cursor while the other is already down.
        if (await PasteWithWiresAsync(clipboard)) return;

        var payload = await LayoutClipboard.PasteAsync(clipboard);
        if (payload is null) return;   // no marker, or nothing on the clipboard — a clean no-op

        var rescale = vm.RescaleFragment(payload);
        var mapping = vm.ProposeFragmentLayerMapping(rescale.Shapes, payload.Layers);

        // R-L1g-2: confirmation is required whenever any row is a low-confidence proposal
        // (same key, different name — the Drill->Substrate trap) or has no proposal at all. Every
        // row being a confident match (same-tech paste, or a confidently-renamed layer) stays
        // silent — this is what keeps ordinary same-technology paste frictionless.
        if (LayoutLayerMapping.RequiresConfirmation(mapping))
        {
            // Propose() only returns rows when a technology resolved (§1's null-destTech short
            // circuit) — RequiresConfirmation can only be true here when vm.Technology is non-null.
            var resolved = await ResolveLayerMappingAsync("Paste", payload.TechName, vm.Technology!, mapping);
            if (resolved is null) return;   // user cancelled — abandon the whole paste
            mapping = resolved;
        }

        var choices = LayoutLayerMapping.BuildChoices(mapping);
        var reconciled = vm.ApplyFragmentReconciliation(rescale.Shapes, payload.Layers, choices);

        // brief-L3a-followups.md §2/R-fix-2: a mixed copy's instances travel alongside the shapes as
        // one placement — rebased (CellRef relative to THIS document) the same way the L3a-era
        // instance-only paste path already did, just no longer gated on "no shapes in the payload."
        var rebasedInstances = vm.RebaseFragmentInstances(payload);

        // docs/design/layout-view.md §9B.9: rulers travel with the fragment, already rescaled by
        // RescaleFragment (their endpoints are coordinates like any other — R-L1f-2, so a ruler
        // pasted into a document at a different resolution still measures the same PHYSICAL
        // distance). No layer reconciliation applies: a ruler has no layer.
        var rescaledRulers = rescale.Rulers ?? [];

        if (inPlace)
            vm.PasteInPlace(reconciled, rebasedInstances, rescaledRulers);
        else
            vm.BeginPastePlacement(reconciled, rescale.AnchorX, rescale.AnchorY, rebasedInstances, rescaledRulers);

        ReportLayerMappingSummary("Pasted", vm.Technology?.Name, reconciled.Count, mapping);

        LayoutCanvasCtrl.InvalidateVisual();
        LayoutCanvasCtrl.Focus();
    }

    /// <summary>
    /// Pastes a clipboard that carries WIRES into this document's wire layer, together with the
    /// geometry half when there is one. Returns false — changing nothing — when the clipboard holds
    /// no wires or this layout has no wire layer to put them in, so the caller falls through to the
    /// ordinary layout paste.
    ///
    /// <para>Transcribed from <c>WBondEditorView.PasteAsync</c> in ONE respect that matters: the
    /// displacement. Both halves move by the same offset, and that offset is the wire half's own
    /// free-pitch answer, so a second paste of the same clipboard does not land on the first.</para>
    ///
    /// <para>The geometry half goes in PLACE here rather than through the paste-placement ghost, and
    /// skips the layer-mapping dialog, exactly as the wBond editor's does — a ghost that follows the
    /// cursor while the wires are already down is the one outcome a mixed paste must not have.</para>
    /// </summary>
    private async Task<bool> PasteWithWiresAsync(IClipboard clipboard)
    {
        if (Vm is not { } vm) return false;

        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { return false; }

        var (wiresJson, layoutJson) = WBondMixedClipboard.Unwrap(text);

        var payload = WBondClipboard.TryParse(wiresJson);
        if (payload is null) return false;   // no wire half — not ours

        // …but never onto a layout whose wires belong to a HOST. The wBond editor hosts this view and
        // puts its own document's wires over it, so creating a layer here would attach a second,
        // invisible wire design to the reference layout — saved to disk and never drawn. The same
        // guard the palette drop carries, asked the way this view can ask it.
        if (_hostOverlay is not null && vm.WireEditor is null) return false;

        // The target may have no wire layer at all, which is every ORDINARY layout — and this path
        // used to give up there, so the wires were silently dropped and only the geometry arrived
        // (owner, 2026-08-17). A layout that is being pasted wires into is a layout that has wires;
        // EnsureWireLayer makes it one and says so.
        if (vm.EnsureWireLayer(
                "This layout had no bond wires; pasting them has made it a wirebond cell. Its wires "
                + "are saved beside it as a .wBond when you save.") is not { } editor)
            return false;

        var (dx, dy) = editor.FreePasteOffset(payload, WBondDefaults.PastePitchNm);

        int pasted = editor.PasteWires(wiresJson, dx, dy);
        pasted += PasteLayoutHalf(layoutJson, dx, dy);

        if (pasted <= 0) return false;

        InvalidateOverlay();
        LayoutCanvasCtrl.InvalidateVisual();
        LayoutCanvasCtrl.Focus();
        return true;
    }

    /// <summary>The geometry half of a mixed paste, in place and displaced to match the wires.</summary>
    private int PasteLayoutHalf(string? json, long dxNm, long dyNm)
    {
        if (Vm is not { } vm) return 0;
        if (!LayoutFragment.TryDeserialize(json, out var payload) || payload is null) return 0;

        // nm and DBU coincide at the 1,000 DBU/µm default these documents work at; see WBondSnap for
        // why that bridge is restated wherever it is crossed.
        var shapes = LayoutFragment.Translate(payload.Shapes, dxNm, dyNm);
        var instances = vm.RebaseFragmentInstances(payload);
        if (instances.Count > 0) instances = LayoutFragment.Translate(instances, dxNm, dyNm);
        var rulers = LayoutFragment.Translate(payload.Rulers, dxNm, dyNm);

        if (shapes.Count == 0 && instances.Count == 0 && rulers.Count == 0) return 0;

        vm.PasteInPlace(shapes, instances, rulers);
        return shapes.Count + instances.Count + rulers.Count;
    }

    /// <summary>Shows the shared layer-mapping dialog (docs/sonnet-briefs/brief-L1g-technology-retarget.md
    /// §2) framed for <paramref name="verb"/> ("Paste" / "Change technology"). Returns the user's
    /// settled rows, or null on cancel — the caller treats that as "abandon the whole operation,"
    /// since partially reconciling a fragment (or a whole layout) is more confusing than not
    /// proceeding at all. <paramref name="destTech"/> is passed explicitly rather than read off
    /// <c>vm.Technology</c> — for a retarget it is the TARGET technology, not the document's current
    /// one.</summary>
    private async Task<IReadOnlyList<LayerMappingRow>?> ResolveLayerMappingAsync(
        string verb, string? sourceTechName, Technology destTech, IReadOnlyList<LayerMappingRow> mapping)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return null;

        var title = $"{verb} into '{destTech.Name}'";
        var dialog = new LayerMappingDialog(title, sourceTechName, destTech, mapping);
        var result = await dialog.ShowDialog<LayerMappingDialogResult?>(owner);
        return result?.Rows;
    }

    /// <summary>Posts a Messages summary after a bulk layer-mapping operation (gate 13) — a record of
    /// a bulk change to the user's geometry that they can read after the dialog is gone.</summary>
    private void ReportLayerMappingSummary(string verb, string? techName, int shapeCount, IReadOnlyList<LayerMappingRow> mapping)
    {
        if (Vm is not { } vm) return;
        if (mapping.Count == 0) return; // same-tech (or no-tech) paste — nothing to report

        string techPart = techName is { Length: > 0 } ? $" into {techName}" : "";
        string layerSummary = LayoutLayerMapping.SummarizeMapping(mapping, vm.Technology);
        vm.ReportMessage($"{verb}{techPart} · {shapeCount} shape(s) · {layerSummary}");
    }

    // ── Change Technology (L1g Gap 1) — metadata-bar affordance ─────────────────────────────────

    // ── Place a parametric cell (C2) — the toolbar button was REMOVED by owner request ────────────
    // Its handler went with it. The picker dialog and vm.PlaceablePCells/BeginPCellPlacement stay:
    // dragging a generator out of the palette arms the same instance-placement ghost.

    // ── L3a — Instance-place tool (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md §6) ────────

    private async void OnInstanceTool(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new InstanceCellPickerDialog(vm.WorkspaceRootDir, vm.InstanceBaseDir, vm.CurrentCellDir);
        var cellRef = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrEmpty(cellRef)) return;

        vm.BeginInstancePlacement(cellRef);
        LayoutCanvasCtrl.InvalidateVisual();
    }

    // ── Export GDSII (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md, R-L4a-3) ──────────────────
    // UI firewall: StorageProvider file picking stays here in code-behind; GdsiiExport does the
    // actual hierarchy walk + write. The fidelity dialog states what will change BEFORE any bytes
    // are written, and blocks the write outright if the plan carries a coordinate overflow.

    private async void OnExportGdsii(object? sender, RoutedEventArgs e) => await OnExportGdsiiAsync();

    private async Task OnExportGdsiiAsync()
    {
        if (Vm is not { } vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        if (vm.CurrentCellDir is not { Length: > 0 } cellDir)
        {
            vm.ReportError("Export GDSII: save this layout to a cell before exporting.");
            return;
        }

        GdsiiExport.ExportPlan plan;
        try
        {
            // item 5/R-fix-4: the root cell's view is ALWAYS the live in-memory Model, never a re-read
            // of the last-saved .clay — an unsaved edit must export exactly what is on screen.
            plan = GdsiiExport.Analyze(cellDir, vm.Technology, vm.Model.DbuPerMicron, vm.Model);
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export GDSII: {ex.Message}");
            return;
        }

        // brief-layout-testing-fixes.md item 4: R-L4a-3's dialog exists to state what WILL CHANGE
        // before writing — when nothing will (ExportPlan.HasNothingToReport), showing a dialog that
        // says "nothing will change" only trains users to dismiss dialogs unread, which defeats the
        // ones that actually matter. Skip straight to the save picker in that case.
        if (!plan.HasNothingToReport)
        {
            var confirmed = await new GdsiiExportFidelityDialog(plan).ShowDialog<bool>(owner);
            if (!confirmed || !plan.CanWrite) return;
        }
        else if (!plan.CanWrite) return;

        if (!await ConfirmDesignRulesBeforeExportAsync(vm, owner, "GDSII")) return;

        var cellName = Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export GDSII",
            DefaultExtension  = "gds",
            SuggestedFileName = cellName,
            FileTypeChoices   = [new FilePickerFileType("GDSII Stream") { Patterns = ["*.gds"] }],
        });
        if (file is null) return;

        try
        {
            GdsiiExport.Write(file.Path.LocalPath, plan);
            vm.ReportMessage(
                $"Exported GDSII · {plan.CurvedShapesFlattened} curve(s) flattened, " +
                $"{plan.HolesKeyholed} hole(s) keyholed, {plan.BitmapsSkipped} bitmap(s) skipped, " +
                $"{plan.LabelRecordsWritten} label(s) written.",
                file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export GDSII: {ex.Message}");
        }
    }

    // ── Export DXF (docs/sonnet-briefs/brief-L4b-dxf-interchange.md) ──────────────────────────────
    // Mirrors OnExportGdsiiAsync's shape exactly — StorageProvider file picking stays here in
    // code-behind; DxfExport does the actual hierarchy walk + write. The options dialog IS the real
    // write (a dry run), so its preview can never disagree with what's actually produced.

    private static bool _lastFlattenSplines;
    private static bool _lastPathAsOutline;
    private static DxfViewMode _lastViewMode = DxfViewMode.FitToExtents;
    // brief-dxf-layer-colors.md R-col-1a: session-scoped only (a process-static field, exactly like the
    // three above) — never persisted to disk, never per-document. Defaults to AC1032 (R2018) per R-col-1.
    private static DxfAcadVersion _lastAcadVersion = DxfAcadVersion.R2018;

    private async void OnExportDxf(object? sender, RoutedEventArgs e) => await OnExportDxfAsync();

    private async Task OnExportDxfAsync()
    {
        if (Vm is not { } vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        if (vm.CurrentCellDir is not { Length: > 0 } cellDir)
        {
            vm.ReportError("Export DXF: save this layout to a cell before exporting.");
            return;
        }

        DxfExport.ExportPlan plan;
        try
        {
            // item 5/R-fix-4: mirrors OnExportGdsiiAsync's own live-view substitution exactly.
            plan = DxfExport.Analyze(cellDir, vm.Technology, vm.Model.DbuPerMicron, vm.Model);
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export DXF: {ex.Message}");
            return;
        }

        double canvasAspect = LayoutCanvasCtrl.Bounds.Height > 0
            ? LayoutCanvasCtrl.Bounds.Width / LayoutCanvasCtrl.Bounds.Height
            : 1.0;
        var previewOptions = new DxfExportOptions(
            _lastFlattenSplines, _lastPathAsOutline, _lastViewMode, LayoutCanvasCtrl.CurrentViewport, canvasAspect,
            AcadVersion: _lastAcadVersion);
        // WB40: a wirebond CELL carries its wires in a sidecar beside the .clay, so a .clay opened in
        // THIS editor can have them — and exporting it wrote the artwork and silently dropped every
        // wire (owner, 2026-08-27: primitives present in QCAD, wires absent). Passed to the preview
        // as well as to the write, or the fidelity dialog would report a different file from the one
        // that lands. Null on an ordinary layout, which writes exactly what it always did.
        var wires = vm.WireDesign;
        var preview = DxfExport.Preview(plan, previewOptions, wires);

        var dialog = new DxfExportOptionsDialog(
            plan, preview, _lastFlattenSplines, _lastPathAsOutline, _lastViewMode, _lastAcadVersion);
        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        _lastFlattenSplines = dialog.FlattenSplines;
        _lastPathAsOutline = dialog.PathAsOutlinePolygon;
        _lastViewMode = dialog.ViewMode;
        _lastAcadVersion = dialog.AcadVersion;

        if (!await ConfirmDesignRulesBeforeExportAsync(vm, owner, "DXF")) return;

        var cellName = Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export DXF",
            DefaultExtension  = "dxf",
            SuggestedFileName = cellName,
            FileTypeChoices   = [new FilePickerFileType("DXF Drawing") { Patterns = ["*.dxf"] }],
        });
        if (file is null) return;

        try
        {
            var options = new DxfExportOptions(
                dialog.FlattenSplines, dialog.PathAsOutlinePolygon, dialog.ViewMode,
                LayoutCanvasCtrl.CurrentViewport, canvasAspect, AcadVersion: dialog.AcadVersion);
            var summary = DxfExport.Write(file.Path.LocalPath, plan, options, wires);
            vm.ReportMessage(
                $"Exported DXF · {summary.CurvedShapesWritten} curved shape(s), " +
                $"{summary.HolesAsHatch} hole(s) as HATCH, {summary.BitmapsSkipped} bitmap(s) skipped, " +
                $"{summary.LabelRecordsWritten} label(s) written, " +
                $"{summary.WiresWritten} bond wire(s), " +
                $"{summary.RulersWritten} ruler(s) as DIMENSION.",
                file.Path.LocalPath);

            // §9B.10's "one Messages note per export" — it says how many rulers were written AND, per
            // R-rul-18b, that a Fixed-size ruler's text height was resolved against this drawing's own
            // extents, because a height that changes with the extents is otherwise a surprise the
            // second time someone exports.
            foreach (var d in summary.Diagnostics) vm.ReportMessage(d);
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export DXF: {ex.Message}");
        }
    }

    // ── Export Gerber (docs/sonnet-briefs/brief-L4c-gerber-export.md) — closes Phase L4 ────────────
    // Mirrors OnExportGdsiiAsync/OnExportDxfAsync's shape, with two differences: Gerber writes MULTIPLE
    // files (a folder picker replaces the single-file save picker), and the whole design's hierarchy
    // must flatten first (R-L4c-6) — any cross-technology sub-cell reconciliation resolves through the
    // SAME LayerMappingDialog paste/retarget/flatten already use, one round trip per distinct pending
    // sub-cell technology, before the fidelity dialog (which IS the real write, run as a dry run) is
    // ever shown.

    private async void OnExportGerber(object? sender, RoutedEventArgs e) => await OnExportGerberAsync();

    private async Task OnExportGerberAsync()
    {
        if (Vm is not { } vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        if (vm.CurrentCellDir is not { Length: > 0 } cellDir)
        {
            vm.ReportError("Export Gerber: save this layout to a cell before exporting.");
            return;
        }

        var resolvedMappings = new Dictionary<string, IReadOnlyList<LayerMappingRow>>();
        GerberExport.ExportPlan plan;
        try
        {
            while (true)
            {
                plan = GerberExport.Analyze(cellDir, vm.Technology, vm.Model.DbuPerMicron, vm.Model, vm.ResolveTechAt, resolvedMappings);
                if (!plan.RequiresMappingConfirmation) break;

                var pending = plan.PendingCrossTechMappings.First();
                var settled = await ResolveLayerMappingAsync("Export Gerber", null, vm.Technology!, pending.Value);
                if (settled is null) return; // cancelled — abandon the whole export, matches paste/retarget cancel semantics
                resolvedMappings[pending.Key] = settled;
            }
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export Gerber: {ex.Message}");
            return;
        }

        if (!plan.HasNothingToReport)
        {
            var confirmed = await new GerberExportFidelityDialog(plan).ShowDialog<bool>(owner);
            if (!confirmed || !plan.CanWrite) return;
        }
        else if (!plan.CanWrite)
        {
            return;
        }

        if (!await ConfirmDesignRulesBeforeExportAsync(vm, owner, "Gerber")) return;

        var folder = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export Gerber — Choose Output Folder",
        });
        if (folder.Count == 0) return;

        var cellName = Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        try
        {
            var result = GerberExport.Write(folder[0].Path.LocalPath, cellName, plan);
            vm.ReportMessage(
                $"Exported Gerber · {result.FilesWritten.Count} file(s) written, " +
                $"{result.DrillToolsDefined} drill tool(s), {result.DrillHitsWritten} drill hit(s).",
                folder[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export Gerber: {ex.Message}");
        }
    }

    // ── Save toolbar button — mirrors SchematicView.axaml.cs's OnSaveCsch exactly ────────────────
    private async void OnSaveClay(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutDocument doc) return;
        if (doc.Hierarchy is { } host)
            await host.SaveLayoutDocumentAsync(doc);
    }

    private async void OnChangeTechnologyClick(object? sender, RoutedEventArgs e) => await OnChangeTechnologyAsync();

    private async Task OnChangeTechnologyAsync()
    {
        if (Vm is not { } vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new ChangeTechnologyDialog(vm);
        var choice = await dialog.ShowDialog<ChangeTechnologyResult?>(owner);
        if (choice is null) return;

        TechResolution target;
        string? newTechRef;
        if (choice.AbsoluteTechPath is null)
        {
            target = vm.ResolveWorkspaceDefaultTech?.Invoke() ?? new TechResolution(null, null, TechResolutionSource.None, []);
            newTechRef = null;
        }
        else
        {
            Technology tech;
            try { tech = TechPersistence.LoadFromFile(choice.AbsoluteTechPath); }
            catch (Exception ex) { vm.ReportError($"Failed to load technology: {ex.Message}"); return; }
            target = new TechResolution(tech, choice.AbsoluteTechPath, TechResolutionSource.LayoutRef, TechValidation.Validate(tech));
            newTechRef = ComputeRelativeTechRef(vm, choice.AbsoluteTechPath);
        }

        var sourceLayers = vm.Technology?.Layers ?? [];
        var mapping = LayoutLayerMapping.Propose(vm.Model.Shapes, sourceLayers, target.Tech);

        if (LayoutLayerMapping.RequiresConfirmation(mapping))
        {
            var resolved = await ResolveLayerMappingAsync("Change technology", vm.Technology?.Name, target.Tech!, mapping);
            if (resolved is null) return;   // user cancelled — abandon the whole retarget
            mapping = resolved;
        }

        var summary = vm.RetargetTo(newTechRef, target, choice.AdoptUnits, mapping);
        vm.ReportMessage(
            $"Retargeted to {summary.TechName ?? "(no technology)"} · {summary.ShapeCount} shape(s) · " +
            LayoutLayerMapping.SummarizeMapping(summary.Rows, target.Tech));

        LayoutCanvasCtrl.InvalidateVisual();
    }

    /// <summary>A retargeted <c>TechRef</c> always resolves relative to the .clay file's own
    /// directory (L0c's resolution order) — falls back to the workspace root, then the chosen
    /// technology's own directory, only for a not-yet-saved scratch layout with no workspace open
    /// (a rare edge case: per <c>TechnologyResolver</c>, a non-null TechRef cannot resolve at all
    /// without a clay directory, so the persisted string only matters once this document is saved).</summary>
    private static string ComputeRelativeTechRef(LayoutEditorViewModel vm, string absoluteTechPath)
    {
        string baseDir = vm.CurrentLayoutPath is { } clay ? Path.GetDirectoryName(clay)!
            : vm.WorkspaceTechDir is { } td ? Path.GetDirectoryName(td)!
            : Path.GetDirectoryName(absoluteTechPath)!;
        return Path.GetRelativePath(baseDir, absoluteTechPath);
    }

    // ── brief-foreign-documents.md §4 item 2: the edge band's "Open Workspace" affordance ─────────
    // Mirrors TornOffFileMenuView.RefreshForCurrentWindow's own WorkspaceViewModel resolution — this
    // view's DataContext is the LayoutDocument, not the WorkspaceViewModel, so the command has to be
    // reached via the same desktop.Windows scan rather than a XAML binding.
    /// <summary>
    /// Technology ▾ ▸ Edit… — opens the resolved <c>.ctech</c> as a document, in its own pane to the
    /// RIGHT of this layout (owner request, 2026-09-02). Same WorkspaceViewModel resolution as
    /// OnOpenSourceWorkspaceClick below: this view's DataContext is the LayoutDocument, so the
    /// workspace is reached by a desktop-windows scan, not a binding.
    ///
    /// <para>The LayoutDocument is handed over as the neighbour to split from, and this view's own
    /// width as the space the two panes will divide — a dock pane is sized by PROPORTION, so the
    /// width the request is stated in has to be measured against something.</para>
    /// </summary>
    private void OnEditTechnologyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutDocument doc || doc.ActiveViewModel is not { } vm) return;

        // Nothing to open when the layout resolved no technology — say so rather than doing nothing,
        // and point at the action that WOULD help (R13a: act, or explain).
        if (vm.ResolvedTechPath is not { Length: > 0 } techPath)
        {
            vm.ReportWarning("This layout has no technology to edit — use \u201cChange Technology\u2026\u201d to pick one.");
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.Windows
            .OfType<WorkspaceWindow>()
            .Select(w => w.DataContext as ViewModels.WorkspaceViewModel)
            .FirstOrDefault(v => v is not null)
            ?.OpenTechnologyDocumentBesideLayout(techPath, doc, Bounds.Width);
    }

    private void OnOpenSourceWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutDocument doc) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var vm = desktop.Windows
            .OfType<WorkspaceWindow>()
            .Select(w => w.DataContext as ViewModels.WorkspaceViewModel)
            .FirstOrDefault(v => v is not null);

        vm?.OpenSourceWorkspaceCommand.Execute(doc.SourceWorkspaceCwsPath);
    }
}
