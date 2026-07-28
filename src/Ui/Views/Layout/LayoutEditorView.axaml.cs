using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
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
        LayoutCanvasCtrl.DuplicateRequested             += (_, _) => { Vm?.Duplicate(); LayoutCanvasCtrl.InvalidateVisual(); };

        // brief-layout-testing-fixes.md item 3/R-fix-3: a click into the canvas always re-focuses it
        // (GotFocus fires whenever focus WASN'T already here — e.g. after a project-tree click moved
        // it away), which is exactly the signal that this document's Properties/undo/save-scope
        // routing needs re-asserting, since Dock's own ActiveDockable never actually changed.
        LayoutCanvasCtrl.GotFocus += (_, _) => _subscribedDoc?.NotifyCanvasInteracted();

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

    private void OnInstanceDoubleTapped(object? sender, LayoutInstance instance)
    {
        if (DataContext is not LayoutDocument doc) return;
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

    /// <summary>Mirrors SchematicView's UpdateDisableButtonStates for PushInBtn/PopOutBtn exactly:
    /// Push Into Cell enabled only when exactly one selected instance resolves to a pushable cell;
    /// Pop Out enabled whenever the active document's nav stack has depth &gt; 0.</summary>
    private void UpdateHierarchyButtonStates()
    {
        if (DataContext is not LayoutDocument doc)
        {
            PushInBtn.IsEnabled = false;
            PopOutBtn.IsEnabled = false;
            return;
        }
        var vm = doc.ActiveViewModel;
        var inst = vm.SingleSelectedInstance;
        PushInBtn.IsEnabled = doc.Hierarchy?.CanPushInto(inst, vm, out _) ?? false;
        PopOutBtn.IsEnabled = doc.CanPopOut;
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
        }
        _subscribedDoc = DataContext as LayoutDocument;
        if (_subscribedDoc is not null)
        {
            _subscribedDoc.ActivationFocusRequested += OnActivationFocusRequested;
            _subscribedDoc.ActiveViewModelChanged    += OnActiveViewModelChangedForNav;
            _subscribedDoc.ExportGdsiiRequested       += OnExportGdsiiRequestedFromMenu;
            _subscribedDoc.ExportDxfRequested         += OnExportDxfRequestedFromMenu;
            if (_subscribedDoc.ConsumeActivationFocus()) FocusCanvasDeferred();
        }
        UpdateHierarchyButtonStates();
    }

    // L3b: DisplayUnit/DbuPerMicron and the hierarchy button enable-state are both read off whichever
    // frame is ACTIVE — a push-in into a sub-cell with a different resolution/unit must relabel the
    // rulers too, and Pop Out's enabled-ness depends on nav depth. Both subscriptions, unlike toolbar
    // bindings, are code-behind (not AXAML), so they have to be explicitly re-pointed at the new
    // active VM on every navigation — AXAML's own {Binding ActiveViewModel.X} paths rebind for free
    // through Avalonia's binding engine; this does not.
    private void OnActiveViewModelChangedForNav(object? sender, System.EventArgs e)
    {
        if (DataContext is LayoutDocument doc) RebindRulerUnitsSubscription(doc);
        UpdateHierarchyButtonStates();
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
        // brief-L3a-followups.md §2/R-fix-2: BuildCopyPayload already carries BOTH selected shapes AND
        // selected instances (a mixed selection is now normal) — pass the whole payload straight
        // through rather than re-deriving a shapes-only fragment a second way.
        var payload = vm.BuildCopyPayload();
        if (payload is null) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        IntPtr ownerHwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await LayoutClipboard.CopyAsync(clipboard, payload, vm.Technology, ownerHwnd);
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

        if (inPlace)
            vm.PasteInPlace(reconciled, rebasedInstances);
        else
            vm.BeginPastePlacement(reconciled, rescale.AnchorX, rescale.AnchorY, rebasedInstances);

        ReportLayerMappingSummary("Pasted", vm.Technology?.Name, reconciled.Count, mapping);

        LayoutCanvasCtrl.InvalidateVisual();
        LayoutCanvasCtrl.Focus();
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
                $"Exported GDSII to {file.Path.LocalPath} · {plan.CurvedShapesFlattened} curve(s) flattened, " +
                $"{plan.HolesKeyholed} hole(s) keyholed, {plan.BitmapsSkipped} bitmap(s) skipped, " +
                $"{plan.LabelRecordsWritten} label(s) written.");
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
        var preview = DxfExport.Preview(plan, previewOptions);

        var dialog = new DxfExportOptionsDialog(
            plan, preview, _lastFlattenSplines, _lastPathAsOutline, _lastViewMode, _lastAcadVersion);
        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        _lastFlattenSplines = dialog.FlattenSplines;
        _lastPathAsOutline = dialog.PathAsOutlinePolygon;
        _lastViewMode = dialog.ViewMode;
        _lastAcadVersion = dialog.AcadVersion;

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
            var summary = DxfExport.Write(file.Path.LocalPath, plan, options);
            vm.ReportMessage(
                $"Exported DXF to {file.Path.LocalPath} · {summary.CurvedShapesWritten} curved shape(s), " +
                $"{summary.HolesAsHatch} hole(s) as HATCH, {summary.BitmapsSkipped} bitmap(s) skipped, " +
                $"{summary.LabelRecordsWritten} label(s) written.");
        }
        catch (Exception ex)
        {
            vm.ReportError($"Export DXF: {ex.Message}");
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
}
