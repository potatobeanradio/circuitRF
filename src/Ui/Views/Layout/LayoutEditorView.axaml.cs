using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
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

        LayoutCanvasCtrl.ClipboardCopyRequested        += async (_, _) => await OnClipboardCopy();
        LayoutCanvasCtrl.ClipboardCutRequested         += async (_, _) => await OnClipboardCut();
        LayoutCanvasCtrl.ClipboardPasteRequested        += async (_, _) => await OnClipboardPaste(inPlace: false);
        LayoutCanvasCtrl.ClipboardPasteInPlaceRequested += async (_, _) => await OnClipboardPaste(inPlace: true);
        LayoutCanvasCtrl.DuplicateRequested             += (_, _) => { Vm?.Duplicate(); LayoutCanvasCtrl.InvalidateVisual(); };

        DataContextChanged += (_, _) => SyncRulerUnits();
        DataContextChanged += OnDataContextChangedForFocus;

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

    // ── Keyboard shortcuts (tunnel — see constructor comment) ─────────────────

    private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (!LayoutCanvasCtrl.IsKeyboardFocusWithin) return; // a toolbar text field owns its own Escape
        var vm = (DataContext as LayoutDocument)?.ViewModel;
        if (vm is null) return;

        vm.OnKeyDown(e.Key, e.KeyModifiers);
        e.Handled = true;
    }

    // ── Activation focus — tab switch grabs keyboard focus (mirrors SchematicView/SymbolEditorView) ──

    private void OnDataContextChangedForFocus(object? sender, System.EventArgs e)
    {
        if (_subscribedDoc is not null) _subscribedDoc.ActivationFocusRequested -= OnActivationFocusRequested;
        _subscribedDoc = DataContext as LayoutDocument;
        if (_subscribedDoc is not null)
        {
            _subscribedDoc.ActivationFocusRequested += OnActivationFocusRequested;
            if (_subscribedDoc.ConsumeActivationFocus()) FocusCanvasDeferred();
        }
    }

    private void OnActivationFocusRequested()
    {
        _subscribedDoc?.ConsumeActivationFocus();
        FocusCanvasDeferred();
    }

    private void FocusCanvasDeferred() =>
        Dispatcher.UIThread.Post(() => LayoutCanvasCtrl.Focus(), DispatcherPriority.Background);

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
        HRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);
        VRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);

        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LayoutEditorViewModel.DisplayUnit))
            {
                HRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);
                VRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);
            }
        };
    }

    private void OnCanvasCursorWorldChanged(object? sender, (double X, double Y)? world)
    {
        HRuler.SetCursorWorld(world?.X);
        VRuler.SetCursorWorld(world?.Y);
        if (DataContext is LayoutDocument doc)
            doc.ViewModel.SetCursorWorld(world?.X, world?.Y);
    }

    private void OnFrameUnknownLayers(IReadOnlyList<LayerKey> keys)
    {
        if (keys.Count == 0) return;
        if (DataContext is LayoutDocument doc)
            doc.ViewModel.ReportUnknownLayers(keys);
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e) => LayoutCanvasCtrl.ZoomToFit();
    private void OnZoomIn(object? sender, RoutedEventArgs e)    => LayoutCanvasCtrl.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e)   => LayoutCanvasCtrl.ZoomOut();
    private void OnZoom1To1(object? sender, RoutedEventArgs e)  => LayoutCanvasCtrl.Zoom1To1();

    // ── Toolbar field commit (§1 R6 typed entry — LostFocus commits; Enter commits + refocuses canvas) ──

    private LayoutEditorViewModel? Vm => (DataContext as LayoutDocument)?.ViewModel;

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

    private async Task OnClipboardCopy()
    {
        if (Vm is not { } vm) return;
        var payload = vm.BuildCopyPayload();
        if (payload is null) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var shapes = vm.SelectedIndices.Select(i => vm.Model.Shapes[i]).ToList();
        IntPtr ownerHwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await LayoutClipboard.CopyAsync(clipboard, shapes, vm.Technology, vm.Model.DbuPerMicron, ownerHwnd);
    }

    private async Task OnClipboardCut()
    {
        await OnClipboardCopy();
        Vm?.CutSelectionAfterCopy();
        LayoutCanvasCtrl.InvalidateVisual();
    }

    private async Task OnClipboardPaste(bool inPlace)
    {
        if (Vm is not { } vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var payload = await LayoutClipboard.PasteAsync(clipboard);
        if (payload is null) return;   // no marker, or nothing on the clipboard — a clean no-op

        var rescale = vm.RescaleFragment(payload);
        var missing = vm.GetMissingFragmentLayers(rescale.Shapes);

        Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices = null;
        if (missing.Count > 0)
        {
            choices = await ResolveLayerReconciliationAsync(vm, missing, payload.Layers);
            if (choices is null) return;   // user cancelled a reconciliation prompt — paste nothing
        }

        var reconciled = vm.ApplyFragmentReconciliation(rescale.Shapes, payload.Layers, choices);

        if (inPlace)
            vm.PasteInPlace(reconciled);
        else
            vm.BeginPastePlacement(reconciled, rescale.AnchorX, rescale.AnchorY);

        LayoutCanvasCtrl.InvalidateVisual();
        LayoutCanvasCtrl.Focus();
    }

    /// <summary>Prompts once per distinct missing layer key (R-L1f-3), honouring "Apply to all
    /// remaining" once the user checks it. Returns null if the user cancels any prompt — the caller
    /// treats that as "abandon the whole paste."</summary>
    private async Task<Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?> ResolveLayerReconciliationAsync(
        LayoutEditorViewModel vm, IReadOnlyList<LayerKey> missing, IReadOnlyList<LayerDef> fragmentLayers)
    {
        var choices = new Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>();
        LayoutFragment.LayerReconciliationChoice? applyToAll = null;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return null;

        foreach (var key in missing)
        {
            LayoutFragment.LayerReconciliationChoice choice;
            if (applyToAll is { } all)
            {
                choice = all;
            }
            else
            {
                var dialog = new LayerReconciliationDialog(vm, key, fragmentLayers);
                var result = await dialog.ShowDialog<LayerReconciliationDialogResult?>(owner);
                if (result is not { } r) return null;
                choice = r.Choice;
                if (r.ApplyToAllRemaining) applyToAll = choice;
            }
            choices[key] = choice;
        }

        return choices;
    }
}
