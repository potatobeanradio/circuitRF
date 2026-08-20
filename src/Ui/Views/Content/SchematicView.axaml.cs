using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.Content;

public partial class SchematicView : UserControl
{
    // Ascender ratio measured from the Skia font the renderer uses.
    // Computed once at construction — update SkiaFonts.PlexRegular to switch fonts.
    private readonly double _fontAscenderRatio;

    // Tracks the VM we're currently subscribed to so we can unsubscribe on retarget.
    private SchematicViewModel? _subscribedVm;

    // Tracks the SchematicDocument we're subscribed to for ActiveViewModelChanged.
    private SchematicDocument? _subscribedDoc;

    public SchematicView()
    {
        InitializeComponent();
        _fontAscenderRatio = MeasureAscenderRatio();

        DataContextChanged += OnDataContextChanged;

        // Canvas events
        SchematicCanvasCtrl.ComponentDoubleTapped  += OnComponentDoubleTapped;
        SchematicCanvasCtrl.TextLabelDoubleTapped  += OnTextLabelDoubleTapped;
        SchematicCanvasCtrl.WireDoubleTapped       += OnWireDoubleTapped;
        SchematicCanvasCtrl.ViewportChanged        += OnViewportChanged;

        // Wire ContextMenu.Closed in code-behind (AXAML can't bind RoutedEvents on ContextMenu).
        if (InlineEditBox.ContextMenu is { } inlineMenu)
            inlineMenu.Closed += OnInlineEditContextMenuClosed;

        // Forward wheel events from the inline edit box to the canvas so zoom works
        // even when the mouse is over the TextBox.  Tunneling fires before the TextBox's
        // internal ScrollViewer can consume the event.
        InlineEditBox.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnInlineEditWheel,
            RoutingStrategies.Tunnel);

        // Focus-independent shortcut handler.  Window.KeyBindings marks Escape handled before
        // visual-tree routing begins, so a plain OnKeyDown override never fires after a toolbar
        // click.  Tunnel + handledEventsToo:true lets us intercept the key regardless.
        this.AddHandler(
            InputElement.KeyDownEvent,
            OnViewKeyDownTunnel,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Clipboard shortcuts (async; must be handled here, not in the canvas)
        SchematicCanvasCtrl.ClipboardCopyRequested  += async (_, _) => await OnClipboardCopy();
        SchematicCanvasCtrl.ClipboardCutRequested   += async (_, _) => await OnClipboardCut();
        SchematicCanvasCtrl.ClipboardPasteRequested += async (_, _) => await OnClipboardPaste();
    }

    // ── VM binding ────────────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from the previous document's events.
        if (_subscribedDoc is not null)
        {
            _subscribedDoc.ActiveViewModelChanged   -= OnActiveViewModelChanged;
            _subscribedDoc.ActivationFocusRequested -= OnActivationFocusRequested;
            _subscribedDoc.ZoomToFitRequested       -= OnZoomToFitRequestedFromMenu;
            _subscribedDoc = null;
        }

        RebindActiveViewModel();

        // Subscribe to the new document's events.
        if (DataContext is SchematicDocument doc)
        {
            _subscribedDoc = doc;
            doc.ActiveViewModelChanged   += OnActiveViewModelChanged;
            doc.ActivationFocusRequested += OnActivationFocusRequested;
            doc.ZoomToFitRequested       += OnZoomToFitRequestedFromMenu;
            // If this tab was activated before the view bound (first open), the request is pending.
            if (doc.ConsumeActivationFocus()) FocusCanvasDeferred();
        }
    }

    // View->Zoom to Fit dispatches here from WorkspaceViewModel via SchematicDocument.RequestZoomToFit().
    private void OnZoomToFitRequestedFromMenu() => SchematicCanvasCtrl.ZoomToFit();

    // Tab activated → grab keyboard focus so Select All / nudges work without a click.
    private void OnActivationFocusRequested()
    {
        _subscribedDoc?.ConsumeActivationFocus();
        FocusCanvasDeferred();
    }

    private void FocusCanvasDeferred() =>
        Dispatcher.UIThread.Post(() => SchematicCanvasCtrl.Focus(), DispatcherPriority.Background);

    private void OnActiveViewModelChanged(object? sender, EventArgs e)
    {
        RebindActiveViewModel();
        SchematicCanvasCtrl.InvalidateVisual();
    }

    private void RebindActiveViewModel()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged   -= OnViewModelPropertyChanged;
            _subscribedVm.Selection.Changed -= OnSelectionChanged;
            _subscribedVm.AutoGenSymbolCallback = null;
            _subscribedVm = null;
        }

        var vm = DataContext is SchematicDocument doc ? doc.ActiveViewModel : null;
        SchematicCanvasCtrl.EditContext = vm;

        if (vm is not null)
        {
            _subscribedVm = vm;
            vm.PropertyChanged   += OnViewModelPropertyChanged;
            vm.Selection.Changed += OnSelectionChanged;
            vm.AutoGenSymbolCallback = ShowAutoGenPromptAsync;
            UpdateToolButtonStates();
            UpdateDisableButtonStates();
            UpdateSnapModeButton();
        }
    }

    private async Task<bool> ShowAutoGenPromptAsync(string cellName)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return false;
        var dialog = new SaveChangesDialog(
            $"A symbol for \"{cellName}\" has not been created. Do you want one to be auto-generated?",
            saveLabel:     "Yes",
            dontSaveLabel: null,
            cancelLabel:   "No",
            title:         "Generate Symbol");
        var result = await dialog.ShowDialog<SaveChangesResult>(owner);
        return result == SaveChangesResult.Save;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SchematicViewModel.ActiveTool)
                           or nameof(SchematicViewModel.PlacementSymbol))
            UpdateToolButtonStates();
        if (e.PropertyName == nameof(SchematicViewModel.SnapMode))
            UpdateSnapModeButton();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateDisableButtonStates();

    private void UpdateToolButtonStates()
    {
        var vm   = Vm;
        var tool = vm?.ActiveTool ?? SchematicViewModel.Tool.Select;

        SelectToolBtn.Classes.Set("ToolActive", tool == SchematicViewModel.Tool.Select);
        ZoomBoxToolBtn.Classes.Set("ToolActive", tool == SchematicViewModel.Tool.ZoomBox);
        WireToolBtn.Classes.Set("ToolActive", tool == SchematicViewModel.Tool.Wire);
        PlaceGroundBtn.Classes.Set("ToolActive",
            tool == SchematicViewModel.Tool.Place && vm?.PlacementSymbol == SymbolKind.Ground);
        PlacePortBtn.Classes.Set("ToolActive",
            tool == SchematicViewModel.Tool.Place && vm?.PlacementSymbol == SymbolKind.Term);
    }

    private void UpdateDisableButtonStates()
    {
        bool hasSelection = Vm?.Selection.Ids.Count > 0;
        RotateCcwBtn.IsEnabled    = hasSelection;
        RotateCwBtn.IsEnabled     = hasSelection;
        MirrorHBtn.IsEnabled      = hasSelection;
        MirrorVBtn.IsEnabled      = hasSelection;
        DeleteBtn.IsEnabled       = hasSelection;
        DisableOpenBtn.IsEnabled  = hasSelection;
        DisableShortBtn.IsEnabled = hasSelection;

        // Push In: enabled when exactly one cell-instance is selected and has a resolvable schematic.
        var doc  = DataContext as SchematicDocument;
        var vm   = Vm;
        EditableComponent? singleComp = null;
        if (vm?.Selection.Ids.Count == 1)
            singleComp = vm.EditModel.FindComponent(vm.Selection.Ids.First());
        PushInBtn.IsEnabled = singleComp?.CellRef is not null
                              && (doc?.Hierarchy?.CanPushInto(singleComp, vm?.EditModel, out _) ?? false);

        // Pop Out: enabled whenever the doc nav stack has depth > 0.
        PopOutBtn.IsEnabled = doc?.CanPopOut ?? false;
    }

    private SchematicViewModel? Vm =>
        (DataContext as SchematicDocument)?.ActiveViewModel;

    // ── Global schematic key handling (focus-independent via tunnel) ──────────────

    // Tunnel handler registered in constructor with handledEventsToo:true.
    // Fires whenever focus is inside this UserControl regardless of which child holds it,
    // and regardless of whether the Window-level Escape KeyBinding already marked the event handled.
    private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!IsKeyboardFocusWithin) return;               // focus not inside this view — skip

        // The inline edit box owns its own typing and Enter. Escape is special: the Window-level
        // Escape KeyBinding (DisarmPlacementCommand) marks the event Handled before the TextBox's
        // bubble KeyDown (OnInlineEditKeyDown) can run, so the box's own Escape branch never fires —
        // leaving the box open and letting the deferred LostFocus commit MOVE a net label. This tunnel
        // handler is registered handledEventsToo:true, so intercept Escape HERE to guarantee a full
        // cancel (the net label must not move) and to close the box. Other keys fall through to the box.
        if (InlineEditBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape && Vm is not null)
            {
                Vm.CancelInlineEdit();   // kind → None: any deferred MaybeDismissInlineEdit/Commit is a no-op
                DismissInlineEditBox();  // IsVisible = false → MaybeDismissInlineEdit early-returns
                Vm.SetSelectTool();
                e.Handled = true;
            }
            return;                       // box owns Enter + typing; only Escape needs handling here
        }

        var vm = Vm;
        if (vm is null) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl)
        {
            // Push Into Cell: Ctrl/⌘+]
            if (e.Key == Key.OemCloseBrackets)
            {
                var doc  = DataContext as SchematicDocument;
                var comp = vm.Selection.Ids.Count == 1
                    ? vm.EditModel.FindComponent(vm.Selection.Ids.First()) : null;
                if (doc is not null && comp?.CellRef is not null
                    && doc.Hierarchy?.CanPushInto(comp, vm.EditModel, out _) == true)
                    doc.Hierarchy.PushIntoCell(doc, comp);
                e.Handled = true;
                return;
            }
            // Pop Out: Ctrl/⌘+[
            if (e.Key == Key.OemOpenBrackets)
            {
                var doc = DataContext as SchematicDocument;
                if (doc?.CanPopOut == true)
                    doc.Hierarchy?.PopOutOf(doc);
                e.Handled = true;
                return;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.HasActiveOperation) vm.SetSelectTool();
                else vm.Selection.Clear();
                SchematicCanvasCtrl.InvalidateVisual();
                e.Handled = true;
                break;
            case Key.S:
                vm.SetSelectTool();
                SchematicCanvasCtrl.InvalidateVisual();
                e.Handled = true;
                break;
            case Key.W:
                vm.SetWireTool();
                SchematicCanvasCtrl.InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Z:
                vm.SetZoomBoxTool();
                SchematicCanvasCtrl.InvalidateVisual();
                e.Handled = true;
                break;
            case Key.F:
                SchematicCanvasCtrl.ZoomToFit();
                e.Handled = true;
                break;
            // Q — Disable → Open Circuit (same as the toolbar button).
            case Key.Q:
                vm.DisableSelection(DisableState.Open);
                SchematicCanvasCtrl.InvalidateVisual();
                e.Handled = true;
                break;
            // T — Place Term, P — Place Pin (quick placement, like W for Wire).
            case Key.T:
                vm.BeginPlacement(SymbolKind.Term);
                SchematicCanvasCtrl.Focus();
                e.Handled = true;
                break;
            case Key.P:
                vm.BeginPlacement(SymbolKind.Pin);
                SchematicCanvasCtrl.Focus();
                e.Handled = true;
                break;
            // Shift+G — Place Ground (plain G stays snap-mode cycle, below).
            case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                vm.BeginPlacement(SymbolKind.Ground);
                SchematicCanvasCtrl.Focus();
                e.Handled = true;
                break;
            case Key.G:
                vm.CycleSnapMode();
                UpdateSnapModeButton();
                e.Handled = true;
                break;
        }
    }

    // ── Zoom buttons ──────────────────────────────────────────────────────────

    private void OnZoomToFit(object? sender, RoutedEventArgs e) => SchematicCanvasCtrl.ZoomToFit();
    private void OnZoomToPage(object? sender, RoutedEventArgs e) => SchematicCanvasCtrl.ZoomToPage();

    // ── Tool buttons ──────────────────────────────────────────────────────────

    private void OnSelectTool(object? sender, RoutedEventArgs e)
    {
        Vm?.SetSelectTool();
        SchematicCanvasCtrl.Focus();
    }

    private void OnWireTool(object? sender, RoutedEventArgs e)
    {
        Vm?.SetWireTool();
        SchematicCanvasCtrl.Focus();
    }

    private void OnZoomBoxTool(object? sender, RoutedEventArgs e)
    {
        Vm?.SetZoomBoxTool();
        SchematicCanvasCtrl.Focus();
    }

    private void OnPlaceGround(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginPlacement(SymbolKind.Ground);
        SchematicCanvasCtrl.Focus();
    }

    private void OnPlaceTerm(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginPlacement(SymbolKind.Term);
        SchematicCanvasCtrl.Focus();
    }

    private void OnPlacePin(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginPlacement(SymbolKind.Pin);
        SchematicCanvasCtrl.Focus();
    }

    // ── Transform buttons ─────────────────────────────────────────────────────

    private void OnRotateCcw(object? sender, RoutedEventArgs e)  => Vm?.RotateSelection(clockwise: false);
    private void OnRotateCw(object? sender, RoutedEventArgs e)   => Vm?.RotateSelection(clockwise: true);
    private void OnMirrorH(object? sender, RoutedEventArgs e)    => Vm?.MirrorSelection(horizontal: true);
    private void OnMirrorV(object? sender, RoutedEventArgs e)    => Vm?.MirrorSelection(horizontal: false);
    private void OnDelete(object? sender, RoutedEventArgs e)     => Vm?.DeleteSelection();

    // ── Grid snap tri-state ───────────────────────────────────────────────────

    private void OnCycleSnapMode(object? sender, RoutedEventArgs e)
    {
        Vm?.CycleSnapMode();
        UpdateSnapModeButton();
        SchematicCanvasCtrl.Focus();
    }

    private void UpdateSnapModeButton()
    {
        var mode = Vm?.SnapMode ?? SnapMode.FineGrid;
        SnapModeBtn.Classes.Set("snap-connection", mode == SnapMode.ConnectionGrid);
        SnapModeBtn.Classes.Set("snap-fine",       mode == SnapMode.FineGrid);
        ToolTip.SetTip(SnapModeBtn, Vm?.SnapModeTooltip ?? "Snap: Off  (G)");
    }

    // ── Hierarchy toolbar ─────────────────────────────────────────────────────

    private void OnToolbarPushIn(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc || Vm is null) return;
        var comp = Vm.Selection.Ids.Count == 1
            ? Vm.EditModel.FindComponent(Vm.Selection.Ids.First()) : null;
        if (comp is null) return;
        doc.Hierarchy?.PushIntoCell(doc, comp);
        SchematicCanvasCtrl.InvalidateVisual();
    }

    private void OnToolbarPopOut(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc) return;
        doc.Hierarchy?.PopOutOf(doc);
        SchematicCanvasCtrl.InvalidateVisual();
    }

    // ── Breadcrumb bar ────────────────────────────────────────────────────────

    private void OnPopToTop(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc) return;
        doc.Hierarchy?.PopToLevel(doc, 0);
        SchematicCanvasCtrl.InvalidateVisual();
    }

    private void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc || sender is not Button btn) return;
        if (btn.Tag is int frameIndex)
            doc.Hierarchy?.PopToLevel(doc, frameIndex);
        SchematicCanvasCtrl.InvalidateVisual();
    }

    // ── Disable state ─────────────────────────────────────────────────────────

    private void OnDisableOpen(object? sender, RoutedEventArgs e)  => Vm?.DisableSelection(DisableState.Open);
    private void OnDisableShort(object? sender, RoutedEventArgs e) => Vm?.DisableSelection(DisableState.Short);

    // ── Save ──────────────────────────────────────────────────────────────────

    private async void OnSaveCsch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc) return;
        if (doc.Hierarchy is { } host)
            await host.SaveSchematicDocumentAsync(doc);
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool hasTarget = !string.IsNullOrEmpty(SchematicCanvasCtrl.ContextMenuTargetId);
        if (!hasTarget)
        {
            e.Cancel = true; // don't show menu on empty canvas click
            return;
        }

        var id   = SchematicCanvasCtrl.ContextMenuTargetId;
        var comp = id is not null ? Vm?.EditModel.FindComponent(id) : null;

        // GND is a special symbol — hide the items that have no meaning for it.
        bool isGnd  = comp?.Symbol == SymbolKind.Ground;
        bool isCell = comp?.CellRef is not null;
        CtxEditParameters.IsVisible  = !isGnd;
        CtxSep1.IsVisible            = !isGnd;
        CtxMoveLabels.IsVisible      = !isGnd;
        CtxResetLabels.IsVisible     = !isGnd;
        CtxLabelsSubMenu.IsVisible   = !isGnd;
        CtxSep2.IsVisible            = !isGnd;

        CtxPushIn.IsVisible      = isCell;
        CtxOpenInNewTab.IsVisible = isCell;

        // Flatten to Cell — a Match and nothing else. Shown only for one, and DISABLED with the
        // reason as its tooltip when the design refuses, the schematic is unsaved or there is no
        // workspace to write into: those are states the user has to act on, and a silently missing
        // item reads as a bug.
        bool isMatch = comp?.Symbol == SymbolKind.Match;
        CtxFlattenMatch.IsVisible = isMatch;
        if (isMatch)
        {
            var availability = Matching.MatchFlattenService.Availability(Vm, comp);
            CtxFlattenMatch.IsEnabled = availability.CanRun;
            ToolTip.SetTip(CtxFlattenMatch, availability.Reason);
        }
        if (isCell && comp is not null)
        {
            var doc      = DataContext as SchematicDocument;
            string? reason = null;
            bool can = doc?.Hierarchy?.CanPushInto(comp, Vm?.EditModel, out reason) ?? false;
            CtxPushIn.IsEnabled       = can;
            CtxOpenInNewTab.IsEnabled = can;
            if (!can && reason is not null)
            {
                ToolTip.SetTip(CtxPushIn,      reason);
                ToolTip.SetTip(CtxOpenInNewTab, reason);
            }
            else
            {
                ToolTip.SetTip(CtxPushIn,      null);
                ToolTip.SetTip(CtxOpenInNewTab, null);
            }
        }

        // Reflect the target component's current label-visibility state via eye icons.
        bool typeLabelVisible    = comp?.ShowTypeLabel    ?? true;
        bool instanceNameVisible = comp?.ShowInstanceName ?? true;
        CtxShowTypeLabel.Icon    = MakeEyeIcon(typeLabelVisible);
        CtxShowInstanceName.Icon = MakeEyeIcon(instanceNameVisible);
    }

    private static Material.Icons.Avalonia.MaterialIcon MakeEyeIcon(bool visible) =>
        new() { Kind = visible ? Material.Icons.MaterialIconKind.Eye
                               : Material.Icons.MaterialIconKind.EyeOff,
                Width = 14, Height = 14 };

    /// <summary>
    /// Context menu ▸ Edit Parameters — the SAME dialog double-clicking the component body opens
    /// (owner, 2026-08-17: it "opens an inline text editor. It is supposed to open the Component
    /// Parameters dialog box").
    ///
    /// <para>This was a placeholder left from before that dialog existed: it opened the inline label
    /// editor on the component's FIRST parameter, which is neither the dialog nor a choice the user
    /// made — a component with several parameters silently offered exactly one of them, and a component
    /// with none did nothing at all. It now routes through <see cref="OpenParameterEditorFor"/>, so the
    /// two ways of asking for a component's parameters cannot answer differently.</para>
    /// </summary>
    private void OnCtxEditParameters(object? sender, RoutedEventArgs e)
    {
        var id   = SchematicCanvasCtrl.ContextMenuTargetId;
        var comp = id is not null ? Vm?.EditModel.FindComponent(id) : null;
        if (comp is null) return;

        OpenParameterEditorFor(comp);
    }

    /// <summary>
    /// Context menu ▸ Flatten to Cell… — the SAME operation the Match Designer's own footer button
    /// runs (match.md §11, brief §4: "both routes go through one command object"). The dialog and
    /// the window ownership are the view's; everything else is
    /// <see cref="Matching.MatchFlattenService"/>'s.
    /// </summary>
    private async void OnCtxFlattenMatch(object? sender, RoutedEventArgs e)
    {
        var id   = SchematicCanvasCtrl.ContextMenuTargetId;
        var comp = id is not null ? Vm?.EditModel.FindComponent(id) : null;
        if (Vm is null || comp is not { Symbol: SymbolKind.Match }) return;

        var availability = Matching.MatchFlattenService.Availability(Vm, comp);
        if (!availability.CanRun || availability.ParentDir is null)
        {
            Vm.MessageSink?.Warning(availability.Reason);
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Dialogs.MatchFlattenDialog(
            comp.InstanceName, availability.DefaultName, availability.ParentDir);
        var choice = owner is null
            ? null
            : await dialog.ShowDialog<Dialogs.MatchFlattenChoice?>(owner);
        if (choice is null) return;

        var result = Matching.MatchFlattenService.Run(
            Vm, comp, choice.ParentDir, choice.CellName, choice.ReplaceInPlace);
        if (result.Ok) Vm.MessageSink?.Success(result.Message);
        else Vm.MessageSink?.Warning(result.Message);

        SchematicCanvasCtrl.InvalidateVisual();
    }

    private void OnCtxPushIn(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc) return;
        var id   = SchematicCanvasCtrl.ContextMenuTargetId;
        var comp = id is not null ? Vm?.EditModel.FindComponent(id) : null;
        if (comp is null) return;
        doc.Hierarchy?.PushIntoCell(doc, comp);
        SchematicCanvasCtrl.InvalidateVisual();
    }

    private void OnCtxOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc) return;
        var id   = SchematicCanvasCtrl.ContextMenuTargetId;
        var comp = id is not null ? Vm?.EditModel.FindComponent(id) : null;
        if (comp is null) return;
        doc.Hierarchy?.OpenCellInNewTab(doc, comp);
    }

    private void OnCtxRotate(object? sender, RoutedEventArgs e) =>
        Vm?.RotateSelection(clockwise: false);

    private void OnCtxDisconnect(object? sender, RoutedEventArgs e)
        => Vm?.DisconnectSelection();

    private void OnCtxMoveLabels(object? sender, RoutedEventArgs e)  => Vm?.BeginMoveLabels();
    private void OnCtxResetLabels(object? sender, RoutedEventArgs e) => Vm?.ResetLabelOffsets();

    private void OnCtxShowTypeLabel(object? sender, RoutedEventArgs e)
    {
        var id = SchematicCanvasCtrl.ContextMenuTargetId;
        if (id is not null) Vm?.ToggleLabelVisibility(id, isTypeLabel: true);
    }

    private void OnCtxShowInstanceName(object? sender, RoutedEventArgs e)
    {
        var id = SchematicCanvasCtrl.ContextMenuTargetId;
        if (id is not null) Vm?.ToggleLabelVisibility(id, isTypeLabel: false);
    }

    // ── Clipboard (Ctrl+C / Ctrl+X / Ctrl+V) ─────────────────────────────────

    private async Task OnClipboardCopy()
    {
        if (DataContext is not SchematicDocument doc) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await CopySelectionToClipboardAsync(doc, clipboard, cut: false);
    }

    private async Task OnClipboardCut()
    {
        if (DataContext is not SchematicDocument doc) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await CopySelectionToClipboardAsync(doc, clipboard, cut: true);
    }

    private async Task OnClipboardPaste()
    {
        if (DataContext is not SchematicDocument doc) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var result = await SchematicClipboard.PasteAsync(clipboard);
        if (result is null) return;
        var (comps, wires, cobjs, srcGrid) = result.Value;

        // View-relative placement + the undoable paste both live in the VM so this path and the
        // Edit-menu path (SchematicViewModel.ClipboardPasteAsync) cannot drift apart.
        doc.ActiveViewModel.PasteFragment(comps, wires, cobjs, srcGrid);
    }

    private async Task CopySelectionToClipboardAsync(SchematicDocument doc, IClipboard clipboard, bool cut)
    {
        var vm    = doc.ActiveViewModel;
        var model = vm.EditModel;

        var comps = vm.Selection.GetSelectedComponentIds(model)
            .Select(id => model.FindComponent(id)).OfType<EditableComponent>().ToList();
        var wires = vm.Selection.GetSelectedWireIds(model)
            .Select(id => model.FindWire(id)).OfType<EditableWire>().ToList();
        var objs  = vm.Selection.GetSelectedCanvasObjectIds(model)
            .Select(id => model.FindCanvasObject(id)).OfType<EditableCanvasObject>().ToList();

        // Segments selected via per-segment click: each segment becomes a 2-point wire.
        // Whole-wire copies take precedence — don't duplicate a wire that's already included.
        var wholeWireIds = new HashSet<string>(wires.Select(w => w.Id));
        foreach (var (wireId, segIdx) in vm.Selection.GetSelectedSegments(model))
        {
            if (wholeWireIds.Contains(wireId)) continue;   // whole wire already included
            var srcWire = model.FindWire(wireId);
            if (srcWire is null || segIdx >= srcWire.Points.Count - 1) continue;
            var segWire = new EditableWire();
            segWire.Points.Add(srcWire.Points[segIdx]);
            segWire.Points.Add(srcWire.Points[segIdx + 1]);
            wires.Add(segWire);
        }

        if (comps.Count == 0 && wires.Count == 0 && objs.Count == 0) return;

        var netLabels = model.NetLabels
            .Where(n => n.IsAnchored && wholeWireIds.Contains(n.OwnerWireId))
            .ToList();
        IntPtr ownerHwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await SchematicClipboard.CopyAsync(clipboard, comps, wires, objs, model.GridSize,
                                           netLabels, model.SchematicDirectory, ownerHwnd);
        if (cut) vm.DeleteSelection();
    }

    private async void OnCtxCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await CopySelectionToClipboardAsync(doc, clipboard, cut: false);
    }

    private void OnCtxDelete(object? sender, RoutedEventArgs e) =>
        Vm?.DeleteSelection();

    // ── Inline text editing (text label double-tap) ───────────────────────────

    // World-space anchor kept while a component label is being edited so the edit box
    // can follow zoom and pan.  Null for wire net-label edits (those use screen-click pos).
    // PrefixWorldUnits = width of the "<Name> = " prefix in world units (measured at the
    //   renderer's reference size 70 so it is zoom-independent); 0 for type/name rows and
    //   for name-mode edits (InlineEditIncludesName), where the box starts at the label's left edge.
    private sealed record ComponentLabelAnchor(
        double CompX, double CompY, int Row, double ODx, double ODy,
        SymbolKind Symbol, int PortCount,
        double PrefixWorldUnits = 0, double? GlyphHalfH = null);
    private ComponentLabelAnchor? _labelAnchor;

    private void OnTextLabelDoubleTapped(object? sender, TextLabelHitArgs e)
    {
        if (Vm is null) return;
        Vm.BeginInlineEditForHit(e.HitResult, e.ScreenX, e.ScreenY);

        // Build world-space anchor so the box can reposition on zoom/pan.
        var hit      = e.HitResult;
        var editComp = Vm.EditModel.FindComponent(hit.Id);
        if (editComp is not null)
        {
            // Compute the visual row (0=type, 1=name, 2+=displayed params in order).
            // For ComponentParam, hit.SubIndex is the FULL-list parameter index; we must
            // count how many shown params come before it to get the visual row number.
            int row;
            if (hit.Kind == SchematicHitTest.HitKind.ComponentType)
            {
                row = 0;
            }
            else if (hit.Kind == SchematicHitTest.HitKind.ComponentName)
            {
                row = 1;
            }
            else // ComponentParam
            {
                int dispIdx = 0;
                for (int pi = 0; pi < editComp.Parameters.Count && pi < hit.SubIndex; pi++)
                {
                    var pp = editComp.Parameters[pi];
                    if (pp.ShowOnSchematic && !string.IsNullOrEmpty(pp.Expression))
                        dispIdx++;
                }
                row = 2 + dispIdx;
            }

            var (oDx, oDy) = editComp.GetLabelOffset(row);

            // Prefix width in WORLD units: measure "<Name> = " at the renderer's reference size (70)
            // so it is zoom-independent (the renderer scales the same text by zoom*70, and
            // multiplying by zoom at reposition time gives the correct screen offset at any zoom).
            // For name-mode edits (InlineEditIncludesName) the box starts at the label's left edge.
            double prefixWorldUnits = 0;
            if (hit.Kind == SchematicHitTest.HitKind.ComponentParam
                && hit.SubIndex < editComp.Parameters.Count
                && !(Vm?.InlineEditIncludesName ?? false))
            {
                var pName = editComp.Parameters[hit.SubIndex].Name;
                if (!string.IsNullOrEmpty(pName))
                {
                    using var mf = new SKFont(SkiaFonts.PlexRegular, 70f);
                    prefixWorldUnits = mf.MeasureText($"{pName} = ");
                }
            }

            // Match the renderer's DrawLabels (glyphHalfH = GlyphBbMaxY - Y). Always the REAL
            // drawn extent, via the one shared definition — never a per-SymbolKind list. The list
            // that used to be here named SnP and the Tuner family and so silently excluded every
            // cell-reference component (an imported kit part), leaving its inline editor over the
            // built-in placeholder's height instead of the resolved cell symbol's.
            double? anchorGlyphHalfH = Vm is null
                ? null
                : Vm.EditModel.EffectiveGlyphBbOf(editComp).MaxY - editComp.Y;
            _labelAnchor = new ComponentLabelAnchor(
                editComp.X, editComp.Y, row, oDx, oDy,
                editComp.Symbol, editComp.PortCount, prefixWorldUnits, anchorGlyphHalfH);
        }
        else
        {
            _labelAnchor = null;
        }

        ShowInlineEditBoxForLabel();
    }

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        if (InlineEditBox.IsVisible && _labelAnchor is not null)
            RepositionInlineEditBox();
    }

    // Screen position of this label row's text anchor (Skia baseline / left edge), derived from the
    // SAME LabelRowGeometry the renderer uses — the single source of truth for the inline box position.
    // For value-only edits the box starts past the "<Name> = " prefix; for name-edits (VAR/SDD) it
    // starts at the label's left edge (InlineEditIncludesName → PrefixWorldUnits already zero).
    private (double X, double Y) ComputeComponentLabelScreen()
    {
        var a = _labelAnchor!;
        var (baseXw, baseYw, _, _) = SchematicComponent.LabelRowGeometry(
            a.CompX, a.CompY, a.Row, a.ODx, a.ODy, a.Symbol, a.PortCount, a.GlyphHalfH);
        double offsetW = (Vm?.InlineEditIncludesName ?? false) ? 0 : a.PrefixWorldUnits;
        return SchematicCanvasCtrl.WorldToScreen(baseXw + offsetW, baseYw);
    }

    private void RepositionInlineEditBox()
    {
        if (_labelAnchor is null) return;

        double zoom     = SchematicCanvasCtrl.CurrentZoom;
        double fontSize = Math.Max(zoom * 70, 9.0);   // matches renderer; floor for legibility
        InlineEditBox.FontSize = fontSize;

        var (sx, sy) = ComputeComponentLabelScreen();   // sy = Skia baseline in screen px

        InlineEditBox.Width   = CalcInlineEditWidth(InlineEditBox.Text ?? "", fontSize);
        _inlineEditAnchorLeft = sx - TextBoxLeftPad;
        InlineEditBox.Margin  = new Thickness(
            _inlineEditAnchorLeft,
            sy - TextBoxTopPad - fontSize * _fontAscenderRatio,
            0, 0);
    }

    // ── Wire net-label double-tap ─────────────────────────────────────────────

    private void OnWireDoubleTapped(object? sender, WireHitArgs e)
    {
        if (Vm is null) return;
        Vm.BeginWireNodeLabelEdit(e.WireId, e.WorldX, e.WorldY, e.ScreenX, e.ScreenY);
        ShowInlineEditBox(e.ScreenX, e.ScreenY, Vm.InlineEditValue);
    }

    // ── Component body double-tap → param edit ────────────────────────────────

    private void OnComponentDoubleTapped(object? sender, EditableComponent comp)
        => OpenParameterEditorFor(comp);

    /// <summary>
    /// Opens the parameter editor for one component — the single implementation behind BOTH the
    /// component-body double-click and the context menu's Edit Parameters. VAR/MEAS get their own
    /// multi-line editor; Ground gets nothing, which the context menu also enforces by hiding the item.
    /// </summary>
    private void OpenParameterEditorFor(EditableComponent comp)
    {
        if (Vm is null) return;

        // Guard: Ground → do not open (single check, per spec).
        if (comp.Symbol == SymbolKind.Ground) return;

        var owner = TopLevel.GetTopLevel(this) as Window;

        // VAR / MEAS → dedicated multi-line editor (Mode A text paste, Mode B rows).
        if (comp.Symbol is SymbolKind.Var or SymbolKind.Meas)
        {
            var varVm = new VarEditorViewModel();
            varVm.SetTarget(Vm, comp, showClose: true);
            var varDialog = new VarEditorDialog { DataContext = varVm };
            varDialog.Closed += (_, _) => varVm.Dispose();
            varDialog.Show(owner!);
            return;
        }

        // Match -> the Match Designer, NOT the 420 px generic dialog (match.md §9.8). A matching
        // network's parameters are a band, two terminations and a rack of linked sliders; the generic
        // editor can only offer them as text rows, and its ONE interesting parameter (`Design`) is a
        // base64 blob it deliberately hides. One window per instance — MatchDesignerWindow.Show
        // raises an existing one rather than opening a second working copy of the same design.
        if (comp.Symbol == SymbolKind.Match)
        {
            Views.Match.MatchDesignerWindow.Show(Vm, comp, owner);
            return;
        }

        var editorVm = new ParameterEditorViewModel();
        editorVm.SetTargetDirect(Vm, comp, showClose: true);

        var dialog = new ParameterEditorDialog { DataContext = editorVm };
        dialog.Closed += (_, _) => editorVm.Dispose();

        // DIALOG_MODAL_FLAG: false = non-modal (default, lets user see schematic update live).
        //                    true  = modal (one-line flip for owner experiment).
        const bool isModal = false;
        if (isModal && owner is not null)
            _ = dialog.ShowDialog(owner);
        else
            dialog.Show(owner!);  // owner may be null when no window parent (e.g. embedded in non-Window host)
    }

    // ── Shared inline edit box helper ─────────────────────────────────────────

    // TextBox has Padding="4,2" (set in AXAML) and FontFamily=IBMPlexSans to match the renderer.
    // Text left edge is at Margin.Left + TextBoxLeftPad.
    // Skia baseline aligns with Margin.Top + TextBoxTopPad + fontSize * _fontAscenderRatio.
    // Height is left unset so Avalonia auto-sizes to the font's natural line height (no multiplier needed).
    private const double TextBoxLeftPad = 4.0;
    private const double TextBoxTopPad  = 2.0;

    // Measures the ascender ratio from the Skia typeface the renderer uses.
    // Skia's Ascent is negative (distance above baseline); negating gives the ratio.
    // Works for any font — swap SkiaFonts.PlexRegular to change the label font everywhere.
    private static double MeasureAscenderRatio()
    {
        using var font = new SKFont(SkiaFonts.PlexRegular, 100f);
        font.GetFontMetrics(out var m);
        return -m.Ascent / 100.0;
    }

    // TextBox Margin.Left anchor (= text left edge - TextBoxLeftPad); fixed while user types.
    private double _inlineEditAnchorLeft;

    // Component-label path: position from the world anchor (single source of truth = LabelRowGeometry).
    private void ShowInlineEditBoxForLabel()
    {
        double zoom = SchematicCanvasCtrl.CurrentZoom;
        InlineEditBox.FontSize = Math.Max(zoom * 70, 9.0);
        InlineEditBox.Text     = Vm!.InlineEditValue;

        InlineEditBox.TextChanged -= OnInlineEditTextChanged;
        InlineEditBox.TextChanged += OnInlineEditTextChanged;

        InlineEditBox.IsVisible = true;
        RepositionInlineEditBox();        // position from the world anchor (single source)
        FocusAndSelectInlineEditBox();
    }

    private void FocusAndSelectInlineEditBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            InlineEditBox.Focus();
            int selLen = Vm?.InlineEditSelLength ?? -1;
            if (selLen < 0) { InlineEditBox.SelectAll(); return; }   // -1 ⇒ select all
            var t     = InlineEditBox.Text ?? "";
            int start = Math.Clamp(Vm?.InlineEditSelStart ?? 0, 0, t.Length);
            int end   = Math.Clamp(start + selLen, start, t.Length);
            InlineEditBox.SelectionStart = start;
            InlineEditBox.SelectionEnd   = end;
        }, DispatcherPriority.Input);
    }

    private void ShowInlineEditBox(double screenX, double screenY, string initialText)
    {
        // screenX = text left edge in screen pixels (from DrawLabels lx).
        // screenY = Skia text baseline in screen pixels (from DrawLabels ly).
        double zoom     = SchematicCanvasCtrl.CurrentZoom;
        double fontSize = Math.Max(zoom * 70, 9.0);   // no upper cap — matches renderer
        InlineEditBox.FontSize = fontSize;

        double width = CalcInlineEditWidth(initialText, fontSize);
        InlineEditBox.Width  = width;
        // Height is not set explicitly — Avalonia auto-sizes to the font's natural line height.

        _inlineEditAnchorLeft = screenX - TextBoxLeftPad;
        double top            = screenY - TextBoxTopPad - fontSize * _fontAscenderRatio;
        InlineEditBox.Margin  = new Thickness(_inlineEditAnchorLeft, top, 0, 0);
        InlineEditBox.Text    = initialText;

        InlineEditBox.TextChanged -= OnInlineEditTextChanged;
        InlineEditBox.TextChanged += OnInlineEditTextChanged;

        InlineEditBox.IsVisible = true;
        FocusAndSelectInlineEditBox();
    }

    private void OnInlineEditTextChanged(object? sender, TextChangedEventArgs e)
    {
        string text  = InlineEditBox.Text ?? "";
        double width = CalcInlineEditWidth(text, InlineEditBox.FontSize);
        InlineEditBox.Width  = width;
        // Keep left edge fixed; box grows to the right as user types.
        InlineEditBox.Margin = new Thickness(_inlineEditAnchorLeft, InlineEditBox.Margin.Top, 0, 0);
    }

    private static double CalcInlineEditWidth(string text, double fontSize)
    {
        double charWidth = fontSize * 0.55;  // average char width for IBM Plex Sans proportional font
        return Math.Max(fontSize * 2.0, text.Length * charWidth + fontSize * 0.8);
    }

    private void OnInlineEditWheel(object? sender, PointerWheelEventArgs e)
    {
        // Intercept wheel events on the TextBox (tunneling) and forward them to the canvas
        // so scroll-wheel zoom works even when the mouse is over the inline edit box.
        var canvasPos = InlineEditBox.TranslatePoint(e.GetPosition(InlineEditBox), SchematicCanvasCtrl);
        if (canvasPos is { } p)
            SchematicCanvasCtrl.ZoomAtPoint(p, e.Delta.Y);
        e.Handled = true;
    }

    private void DismissInlineEditBox()
    {
        _labelAnchor = null;
        InlineEditBox.TextChanged -= OnInlineEditTextChanged;
        InlineEditBox.IsVisible    = false;
    }

    // True while the InlineEditBox's ContextMenu is open.  We use this in MaybeDismissInlineEdit
    // to avoid committing the edit the instant the popup steals keyboard focus.
    private bool _inlineEditContextMenuOpen;

    private void OnInlineEditLostFocus(object? sender, RoutedEventArgs e)
    {
        // Defer so the ContextMenu (if just opened) has time to set _inlineEditContextMenuOpen.
        Dispatcher.UIThread.Post(MaybeDismissInlineEdit, DispatcherPriority.Background);
    }

    private void MaybeDismissInlineEdit()
    {
        if (!InlineEditBox.IsVisible) return;
        if (InlineEditBox.IsFocused || InlineEditBox.IsKeyboardFocusWithin) return;
        if (_inlineEditContextMenuOpen) return;  // popup is open; wait for Closed
        CommitAndDismissInlineEdit();
    }

    private void OnInlineEditContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _inlineEditContextMenuOpen = true;
        InlineCtxCut.IsEnabled  = !string.IsNullOrEmpty(InlineEditBox.SelectedText);
        InlineCtxCopy.IsEnabled = !string.IsNullOrEmpty(InlineEditBox.SelectedText);
    }

    // Fired when the ContextMenu popup closes (user picked an item, pressed Escape, or clicked
    // outside).  Re-check focus at Background priority; if focus went to the canvas the edit
    // box will be committed and dismissed, otherwise it stays open.
    private void OnInlineEditContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        _inlineEditContextMenuOpen = false;
        Dispatcher.UIThread.Post(MaybeDismissInlineEdit, DispatcherPriority.Background);
    }

    private async void OnInlineCtxCopy(object? sender, RoutedEventArgs e)
    {
        var text = InlineEditBox.SelectedText;
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text);
    }

    private async void OnInlineCtxCut(object? sender, RoutedEventArgs e)
    {
        var text = InlineEditBox.SelectedText;
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text);

        var start = Math.Min(InlineEditBox.SelectionStart, InlineEditBox.SelectionEnd);
        var end   = Math.Max(InlineEditBox.SelectionStart, InlineEditBox.SelectionEnd);
        var full  = InlineEditBox.Text ?? "";
        InlineEditBox.Text       = full[..start] + full[end..];
        InlineEditBox.CaretIndex = start;
    }

    private async void OnInlineCtxPaste(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var paste = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(paste)) return;

        var start = Math.Min(InlineEditBox.SelectionStart, InlineEditBox.SelectionEnd);
        var end   = Math.Max(InlineEditBox.SelectionStart, InlineEditBox.SelectionEnd);
        var full  = InlineEditBox.Text ?? "";
        InlineEditBox.Text       = full[..start] + paste + full[end..];
        InlineEditBox.CaretIndex = start + paste.Length;
    }

    private void OnInlineCtxSelectAll(object? sender, RoutedEventArgs e) =>
        InlineEditBox.SelectAll();

    private void CommitAndDismissInlineEdit()
    {
        // Dismiss first so that the LostFocus this triggers (and any already-queued deferred post)
        // sees IsVisible=false and returns early from MaybeDismissInlineEdit — belt-and-suspenders
        // on top of the VM-level idempotency guard in CommitInlineEdit.
        string text = InlineEditBox.Text ?? "";
        DismissInlineEditBox();
        if (Vm is not null)
        {
            Vm.InlineEditValue = text;
            Vm.CommitInlineEdit();
        }
    }

    private void OnInlineEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key is Key.Return or Key.Enter)
        {
            CommitAndDismissInlineEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.CancelInlineEdit();
            DismissInlineEditBox();
            Vm.SetSelectTool();
            e.Handled = true;
        }
    }

}
