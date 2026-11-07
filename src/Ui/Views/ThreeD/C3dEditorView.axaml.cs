using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Render;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.Ui.Views.Viewer3D;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-43 — the 3D editor's shell. Wires the pane's frames to the overlay, shows a present fault in
/// the pane's place, opens the selection's context menu (the view model's, per mode) and the tree's,
/// and follows the application theme (a switch regenerates the
/// scene, because colours are baked into the vertices).
/// </summary>
public partial class C3dEditorView : UserControl
{
    private C3dEditorViewModel? _vm;
    private C3dEditorDocument? _doc;
    private readonly ContextMenu _menu = new();
    private readonly ContextMenu _drawMenu = new();
    private readonly ContextMenu _treeMenu = new();

    public C3dEditorView()
    {
        InitializeComponent();
        // brief-emi-6 — Run is off for a comparison with fewer than two solvers that can run here: what is installed is read
        // when the view opens and whenever Settings ▸ Solvers closes.
        AttachedToVisualTree += (_, _) =>
        {
            CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.Changed -= OnSolverInstallChanged;
            CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.Changed += OnSolverInstallChanged;
            _ = CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.RefreshAsync();
        };
        DetachedFromVisualTree += (_, _) => CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.Changed -= OnSolverInstallChanged;
        // The Look panel is light-dismiss, and its dismiss overlay covers the window and swallows the wheel: the viewport is let
        // through so the picture being tuned can still be zoomed while the panel is open.
        if (LookButton.Flyout is Avalonia.Controls.Primitives.PopupFlyoutBase look) look.OverlayInputPassThroughElement = ViewportPanel;
        if (MeshPlaneButton.Flyout is Avalonia.Controls.Primitives.PopupFlyoutBase meshPlane) meshPlane.OverlayInputPassThroughElement = ViewportPanel;
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
        // brief-em3d-45 — the typed field sees Tab and Enter before the TextBox (and focus navigation) does; its Esc is
        // the view's (OnViewKeyTunnel).
        FieldInput.AddHandler(KeyDownEvent, OnFieldKey, RoutingStrategies.Tunnel);
        _drawMenu.AddHandler(KeyDownEvent, OnDrawMenuKey, RoutingStrategies.Tunnel);
        // 3D round 1 — the workspace window binds Escape to a command, and a window key binding marks the key handled
        // before routing reaches the focused control: no bubble handler in this view (the pane's, a text box's) ever
        // saw Esc while the view was docked. The view claims it first, with handled events too — the fix the layout,
        // schematic and symbol editors already carry.
        AddHandler(KeyDownEvent, OnViewKeyTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnViewGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        // 3D editor keys — the pane's keys were heard only while the pane had focus, and a toolbar click took it: after
        // clicking Isometric, 1 did nothing until the canvas was clicked. A toolbar click hands focus back (as the layout
        // editor's OnToolButtonClick does), and a key nobody in the view used reaches the pane wherever focus is.
        Toolbar.AddHandler(Button.ClickEvent, OnToolbarClick, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnViewKeyBubble, RoutingStrategies.Bubble);
        Pane.FramePresented += () => Overlay.InvalidateVisual();
        // brief-em3d-86 R-em3d86-3 — a slider drag previews; its release (a track click is one too) keeps the step: one entry.
        SweepCard.AddHandler(PointerReleasedEvent, (_, _) => _vm?.CommitSweepStep(), RoutingStrategies.Bubble, handledEventsToo: true);
        SweepCard.AddHandler(PointerCaptureLostEvent, (_, _) => _vm?.CommitSweepStep(), RoutingStrategies.Bubble, handledEventsToo: true);
        ObjectTree.TemplateApplied += OnObjectTreeTemplateApplied;
        ObjectTree.AddHandler(PointerPressedEvent, OnTreePointerPressedTunnel, RoutingStrategies.Tunnel);
        // The double-click is read off the press, handled or not, as a setup card's is: the first press selects the row.
        ObjectTree.AddHandler(PointerPressedEvent, OnTreeDoublePressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        // 3D editor groups — Ctrl/Cmd+G and Ctrl/Cmd+Shift+G on the tree's rows, as on the view (only the pane sees its keys).
        ObjectTree.AddHandler(KeyDownEvent, OnTreeGroupKey, RoutingStrategies.Tunnel);
        // brief-em3d-91 — H on the tree's rows, where a multi-selection is usually made; never while a text field (the rename
        // box) has the key.
        ObjectTree.AddHandler(KeyDownEvent, OnTreeHideKey, RoutingStrategies.Tunnel);
        // brief-em3d-95 — Ctrl/Cmd+C and Ctrl/Cmd+V on the tree's rows copy and paste objects; the pane's keys are unchanged.
        ObjectTree.AddHandler(KeyDownEvent, OnTreeClipboardKey, RoutingStrategies.Tunnel);
        Pane.ContextMenuRequested += () =>
        {
            if (_vm is null) return;
            // 3D editor bugs round 4 — Copy (the picture, to the clipboard) on anything or nothing, as the read-only
            // viewer has had it; this menu had been filled with no picture commands at all.
            void Report(string text) { if (_vm is not null) _vm.StatusMessage = text; }
            var copy = Viewer3DPictureCopy.Item(Pane, () => _vm?.Viewer, Report);
            // 3D vector copy and drawing export (2026-09-27) — the view as lines, beside each picture command.
            var copyVector = Viewer3DVectorExport.CopyItem(Pane, () => _vm?.Viewer, Report);
            // 3D editor bugs round 5 — Export Picture…, the read-only viewer's, for both.
            var export = new MenuItem { Header = "Export Picture…" };
            export.Click += OnExportPicture;
            Window? Owner() => TopLevel.GetTopLevel(this) as Window;
            string DocumentPath() => _vm?.FilePath ?? "";
            var exportVector = Viewer3DVectorExport.ExportItem(Owner, Pane, () => _vm?.Viewer, DocumentPath, Report);
            var drawing = Viewer3DVectorExport.DrawingItem(Owner, () => _vm?.Viewer, DocumentPath, Report);
            Viewer3DContextMenu.Fill(_menu, _vm.Viewer.OpenContextMenu(), [copy, copyVector, export, exportVector, drawing]);
            _menu.Open(Pane);
        };
        Pane.FaultChanged += why =>
        {
            FaultText.Text = why is null ? "" : "The 3D view cannot draw here: " + why;
            FaultPanel.IsVisible = why is not null;
        };
    }

    private void OnSolverInstallChanged() => _vm?.RefreshSimulateGate();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_doc is not null) _doc.ActivationFocusRequested -= OnActivationFocusRequested;
        _doc = DataContext as C3dEditorDocument;
        // The tab activated again (the view was already attached): the pane takes the keys, as the layout editor's canvas does.
        if (_doc is not null) _doc.ActivationFocusRequested += OnActivationFocusRequested;
        if (_vm is not null)
        {
            _vm.DrawMenuRequested -= OnDrawMenuRequested;
            _vm.FieldFocusRequested -= OnFieldFocusRequested;
            _vm.TextRequested -= OnTextRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.TreeRevealRequested -= OnTreeReveal;
            _vm.ClipboardWriteRequested -= OnClipboardWriteRequested;
            _vm.LookPanelRequested -= OnLookPanelRequested;
        }
        var doc = DataContext as C3dEditorDocument;
        _vm = doc?.ViewModel;
        if (_vm is null) return;
        _vm.DrawMenuRequested += OnDrawMenuRequested;
        _vm.FieldFocusRequested += OnFieldFocusRequested;
        _vm.TextRequested += OnTextRequested;
        _vm.PropertyChanged += OnVmPropertyChanged;
        MirrorTreeSelection();
        _vm.TreeRevealRequested += OnTreeReveal;
        _vm.ClipboardWriteRequested += OnClipboardWriteRequested;
        _vm.LookPanelRequested += OnLookPanelRequested;
        SyncPlotTheme();
        if (doc!.ConsumeActivationFocus()) Dispatcher.UIThread.Post(() => Pane.Focus(), DispatcherPriority.Loaded);
    }

    // ── brief-em3d-108 R-em3d108-3 — the Look panel ──────────────────────────────────────────────────────────────────

    /// <summary>3D ▸ View ▸ Look…: the drop-down's flyout, opened from the menu.</summary>
    private void OnLookPanelRequested() => LookButton.Flyout?.ShowAt(LookButton);

    private void OnLookOpened(object? sender, EventArgs e) => _vm?.LookPanel.Opened();

    private void OnLookClosed(object? sender, EventArgs e) => _vm?.LookPanel.Closed();

    private void OnLookCloseRequested(object? sender, EventArgs e) => LookButton.Flyout?.Hide();

    private void OnMeshPlaneOpened(object? sender, EventArgs e)
    {
        if (_vm is { } vm) vm.Viewer.MeshPlaneEnabled = true;
    }

    private void OnMeshPlaneClose(object? sender, RoutedEventArgs e) => MeshPlaneButton.Flyout?.Hide();

    /// <summary>brief-em3d-75 — the line plot follows the application's light or dark variant, as every PlotControl must.</summary>
    private void SyncPlotTheme()
        => ThermalLinePlot.PlotTheme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? CircuitRF.Render.DataDisplay.RenderTheme.Dark : CircuitRF.Render.DataDisplay.RenderTheme.Light;

    /// <summary>brief-em3d-49 — a value the view model asks for (a port's Z0, a box face's padding): asked, then committed;
    /// a refusal goes to the status line.</summary>
    private async void OnTextRequested(string title, string prompt, string current, Func<string, string?> commit)
    {
        if (TopLevel.GetTopLevel(this) is not Window window || _vm is null) return;
        var text = await new Dialogs.InputNameDialog(title, prompt, current).ShowDialog<string?>(window);
        if (text is null) return;
        if (commit(text) is { } why) _vm.StatusMessage = why;
    }

    /// <summary>
    /// 3D editor bugs round 5 — the read-only viewer's reveal, carried over: a node the scene selected is brought into view,
    /// its group (and, for an instance's part, its instance) opened first. A container is realized only once its parent
    /// is expanded and laid out, so the scroll waits for layout.
    /// </summary>
    private void OnTreeReveal(C3dTreeItem item)
    {
        if (_vm is null) return;
        foreach (var g in _vm.Tree)
        {
            if (g.Items.Contains(item)) g.IsExpanded = true;
            else if (g.Items.FirstOrDefault(i => i.Children.Contains(item)) is { } parent) { g.IsExpanded = true; parent.IsExpanded = true; }
            else continue;
            break;
        }
        Dispatcher.UIThread.Post(() => ObjectTree.TreeContainerFromItem(item)?.BringIntoView(), DispatcherPriority.Loaded);
    }

    /// <summary>brief-em3d-101 R-em3d101-2a — Insert Image…: the picker, then the one placement (C3dEditorViewModel.PlaceImageSheet).</summary>
    private async void OnInsertImage(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.InsertImageRefusal() is { } why) { _vm.StatusMessage = why; return; }
        var files = await ImageFilePicker.PickAsync(TopLevel.GetTopLevel(this), "Insert Image", multiple: true);
        if (files.Count > 0) _vm.InsertImages(files);
    }

    /// <summary>
    /// brief-em3d-29 R-em3d29-5 — Export picture…: the view drawn offscreen by the GPU at the chosen multiple of the pane's
    /// DEVICE-pixel size, read back, and written as PNG where the user says (3D editor bugs round 5: the read-only viewer's).
    /// </summary>
    private async void OnExportPicture(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var viewer = _vm.Viewer;
        // brief-em3d-107 R-em3d107-5 — supersampling and a transparent background are the realistic view's PICTURE only (Copy draws once)
        var shot = Viewer3DPictureCopy.Capture(Pane, viewer, viewer.ExportScale, out string? error, transparent: viewer.ExportTransparent,
                                               supersample: viewer.ExportSupersample);
        if (shot is null) { _vm.StatusMessage = "The picture could not be made: " + error; return; }
        var png = await Task.Run(shot.Png);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export Picture",
            SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_vm.FilePath) + "-3d",
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
        _vm.StatusMessage = $"Exported the view as {file.Name}.";
    }

    /// <summary>3D editor round 1 — a right-click on a tree node selects it, then opens its menu (the canvas's commands).
    /// Round 5: on one of several selected rows it keeps them all, and the menu is the canvas's for that selection — the
    /// one builder, so a boolean is offered exactly as it is there.</summary>
    private async void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null) return;
        var context = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext;
        // brief-em3d-95 — every branch answers now, the tree's empty area too (Paste), so the menu is this handler's
        e.Handled = true;
        if (context is C3dTreeItem row && !(_vm.SelectedTreeItems.Count > 1 && _vm.SelectedTreeItems.Contains(row)))
            _vm.SelectedTreeItem = row;
        // Paste is enabled from what the clipboard holds: read it before the menu is built
        _vm.ClipboardText = await C3dClipboard.ReadAsync(TopLevel.GetTopLevel(this)?.Clipboard) ?? _vm.ClipboardText;
        if (_vm is null) return;
        // brief-em3d-83 R-em3d83-3 — a group's header: the Field Plots group adds a plot.
        if (context is C3dTreeGroup group)
            Viewer3DContextMenu.Fill(_treeMenu, _vm.TreeEmptyMenuItems(_vm.TreeGroupMenuItems(group)), []);
        else if (context is not C3dTreeItem item)
            Viewer3DContextMenu.Fill(_treeMenu, _vm.TreeEmptyMenuItems(), []);
        else if (_vm.SelectedTreeItems.Count > 1 && _vm.SelectedTreeItems.Contains(item))
            Viewer3DContextMenu.Fill(_treeMenu, _vm.TreeSelectionMenuItems(), []);
        else
            Viewer3DContextMenu.Fill(_treeMenu, _vm.TreeMenuItems(item), []);
        if (_treeMenu.Items.Count > 0) _treeMenu.Open(ObjectTree);
    }

    /// <summary>brief-em3d-95 — Ctrl/Cmd+C and Ctrl/Cmd+V with focus in the object tree (the pane's Ctrl/Cmd+C is the picture,
    /// D3). Never while a text field (the rename box) has the key.</summary>
    private async void OnTreeClipboardKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Source is TextBox || e.Key is not (Key.C or Key.V)) return;
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if ((e.KeyModifiers & ~KeyModifiers.Shift) != command || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (e.Key == Key.C) { _vm.CopySelection(); return; }
        string? text = await C3dClipboard.ReadAsync(TopLevel.GetTopLevel(this)?.Clipboard) ?? _vm.ClipboardText;
        _vm?.Paste(text);
    }

    /// <summary>brief-em3d-95 — a copy made in the view model goes to the system clipboard.</summary>
    private void OnClipboardWriteRequested(string text) => _ = C3dClipboard.WriteAsync(TopLevel.GetTopLevel(this)?.Clipboard, text);

    private void OnTreeGroupKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.G || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _vm.UngroupSelection(); else _vm.GroupSelection();
        e.Handled = true;
    }

    private void OnTreeHideKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || !C3dEditorViewModel.IsHideKey(e.Key, e.KeyModifiers, e.Source is TextBox)) return;
        if (_vm.HideOrShowSelection()) e.Handled = true;
    }

    // ── 3D editor round 5: the tree's multiple selection ─────────────────────────────────────

    private bool _mirroringTree;

    /// <summary>
    /// Ctrl-click adds a row to the selection or takes it out, on every platform. The TreeView's own toggle key is the
    /// platform's COMMAND key — Ctrl on Windows and Linux, but Cmd on macOS, where Ctrl-click therefore did nothing but
    /// select the one row. Cmd-click still reaches the TreeView and toggles as before; Shift-click is still its range.
    /// A click on a row's tick or its expander is left to them.
    /// </summary>
    private void OnTreePointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
            !e.GetCurrentPoint(ObjectTree).Properties.IsLeftButtonPressed || e.Source is not Visual source ||
            TreeRowOf(source) is not { } row || ObjectTree.SelectedItems is not { } selected) return;
        if (selected.Contains(row)) selected.Remove(row); else selected.Add(row);
        e.Handled = true;
    }

    /// <summary>A plain double-click on a row brings the Properties Inspector forward, after the press has finished routing
    /// so the tree taking its focus does not follow it. The view model decides which rows do.</summary>
    private void OnTreeDoublePressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || e.KeyModifiers != KeyModifiers.None ||
            !e.GetCurrentPoint(ObjectTree).Properties.IsLeftButtonPressed || e.Source is not Visual source ||
            TreeRowOf(source) is not { } row) return;
        Dispatcher.UIThread.Post(() => _vm?.ShowPropertiesOfRow(row));
    }

    /// <summary>The row <paramref name="pressed"/> lies in; null on a row's tick or expander, which are left to themselves, and
    /// outside the rows.</summary>
    private C3dTreeItem? TreeRowOf(Visual pressed)
    {
        foreach (var v in pressed.GetSelfAndVisualAncestors())
        {
            if (v is Avalonia.Controls.Primitives.ToggleButton) return null;     // a tick (CheckBox) or the expander
            if (v is TreeViewItem { DataContext: C3dTreeItem row }) return row;
            if (ReferenceEquals(v, ObjectTree)) return null;
        }
        return null;
    }

    /// <summary>The user changed the tree's selection: the view model keeps the order and selects in the scene.</summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_mirroringTree || _vm is null) return;
        // Clear-then-Add (a plain click) raises a Reset that names no removed rows: the tree's whole selection is the truth.
        _vm.TreeSelectionChanged([.. ObjectTree.SelectedItems.OfType<C3dTreeItem>()]);
        // The view model may have settled on something else (a row it cannot select alongside others): show that.
        MirrorTreeSelection();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(C3dEditorViewModel.SelectedTreeItems)) MirrorTreeSelection();
    }

    /// <summary>The tree shows the view model's selection — every row of a canvas multi-selection highlighted.</summary>
    private void MirrorTreeSelection()
    {
        if (_vm is null || ObjectTree.SelectedItems is not { } selected) return;
        var want = _vm.SelectedTreeItems;
        if (selected.Count == want.Count && want.All(selected.Contains)) return;
        _mirroringTree = true;
        try
        {
            selected.Clear();
            foreach (var row in want) selected.Add(row);
        }
        finally { _mirroringTree = false; }
    }

    // ── 3D editor round 5: the tree's width and its horizontal scroll ─────────────────────────

    /// <summary>The grip on the tree's right edge: the panel follows the drag, between a usable minimum and most of the view.</summary>
    private void OnTreeGripDrag(object? sender, VectorEventArgs e)
    {
        double max = Math.Max(TreeMinWidth, Bounds.Width * 0.7);
        TreePanel.Width = Math.Clamp(TreePanel.Width + e.Vector.X, TreeMinWidth, max);
    }

    private const double TreeMinWidth = 150;

    /// <summary>3D editor bugs round 6 — the Variables panel's grip, the tree's on the other side: a drag LEFT widens it.
    /// Not saved.</summary>
    private void OnVariablesGripDrag(object? sender, VectorEventArgs e)
    {
        double max = Math.Max(VariablesMinWidth, Bounds.Width * 0.7);
        VariablesPanel.Width = Math.Clamp(VariablesPanel.Width - e.Vector.X, VariablesMinWidth, max);
    }

    private const double VariablesMinWidth = 200;

    private ScrollViewer? _treeScroll;

    /// <summary>The tree's scroll content takes every bring-into-view (a click on a row, a selection, a reveal) itself,
    /// before the scroll viewer does: vertical only. The TreeView brings the WHOLE row into view, and a row wider than
    /// the panel scrolled the horizontal bar to its right-hand end on every click.</summary>
    private void OnObjectTreeTemplateApplied(object? sender, Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        // The theme's TreeView template names only its items presenter; the unnamed scroll viewer is its parent.
        if (e.NameScope.Find<Control>("PART_ItemsPresenter") is not { Parent: ScrollViewer scroll } presenter) return;
        _treeScroll = scroll;
        presenter.AddHandler(RequestBringIntoViewEvent, OnTreeBringIntoView);
    }

    private void OnTreeBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (_treeScroll is null || sender is not Visual content || e.TargetObject is not Visual target) return;
        if (target.TransformToVisual(content) is not { } m) return;
        e.Handled = true;
        var rect = e.TargetRect.TransformToAABB(m);
        double y = _treeScroll.Offset.Y, h = _treeScroll.Viewport.Height;
        if (rect.Top < y) y = rect.Top;
        else if (rect.Bottom > y + h) y = Math.Min(rect.Top, rect.Bottom - h);
        if (y != _treeScroll.Offset.Y) _treeScroll.Offset = new Vector(_treeScroll.Offset.X, y);
    }

    // ── 3D round 1: Esc ─────────────────────────────────────────────────────────────────────

    private TextBox? _editBox;
    private string? _editText;
    private bool _cancellingEdit;

    /// <summary>A text box took focus: what it said then is what Esc puts back.</summary>
    private void OnViewGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not TextBox box) return;
        _editBox = box;
        _editText = box.Text;
    }

    /// <summary>
    /// Esc anywhere in the view. In a text box it CANCELS the edit — the text it had when it took focus comes back
    /// (through its binding, so the view model's copy too), nothing is committed on the way out, and focus returns
    /// to the canvas; the typed field closes, and a Define row goes back to the field, as their own keys said. An open
    /// drop-down just closes. Anywhere else it is one step of the pane's ladder.
    /// </summary>
    private void OnViewKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is null) return;
        e.Handled = true;
        if (e.Source is ComboBox { IsDropDownOpen: true } combo) { combo.IsDropDownOpen = false; return; }
        if (e.Source is not TextBox box) { Pane.Escape(); return; }
        if (ReferenceEquals(box, FieldInput)) _vm.FieldEscape();
        else if (box.DataContext is C3dDefineRow) { _vm.DefineEscape(); return; }
        else
        {
            if (ReferenceEquals(box, _editBox)) box.Text = _editText;
            if (ReferenceEquals(box, PlaneOffsetBox)) _vm.SetPlane(_vm.Plane);
            else if (box.DataContext is C3dPropertiesViewModel or C3dDimensionField) _vm.Properties.Reload();
        }
        _cancellingEdit = true;
        try { Pane.Focus(); }
        finally { _cancellingEdit = false; }
        _editBox = null;
    }

    // ── 3D editor keys: wherever focus is in the view ───────────────────────────────────────

    /// <summary>A toolbar button was clicked: the pane takes the keys back. A button that opens a flyout keeps focus for it.</summary>
    private void OnToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Flyout: not null }) return;
        Dispatcher.UIThread.Post(() => Pane.Focus(), DispatcherPriority.Background);
    }

    private void OnActivationFocusRequested()
    {
        _doc?.ConsumeActivationFocus();
        Dispatcher.UIThread.Post(() => Pane.Focus(), DispatcherPriority.Background);
    }

    /// <summary>
    /// A key that bubbled to the view unused, from anywhere but the pane itself: the pane's keys (1–7, O/F/E/V, G, P …). Never
    /// from a text field or a drop-down, whose letters are their own; Delete and Backspace only from the pane, so a key pressed
    /// on some other control never deletes geometry. Esc is the tunnel's (OnViewKeyTunnel).
    /// </summary>
    private void OnViewKeyBubble(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled || ReferenceEquals(e.Source, Pane)) return;
        if (e.Key is Key.Escape or Key.Delete or Key.Back) return;
        if (IsTextEntry(e.Source as Visual)) return;
        // An arrow the tree (or any list) left unused at its first or last row is still the tree's: only a toolbar button
        // just clicked hands the arrows on to pan and orbit the view.
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && e.Source is not Button) return;
        if (Pane.ForwardKey(e)) e.Handled = true;
    }

    private bool IsTextEntry(Visual? v)
    {
        for (; v is not null && !ReferenceEquals(v, this); v = v.GetVisualParent())
            if (v is TextBox or ComboBox or AutoCompleteBox or NumericUpDown) return true;
        return false;
    }

    // ── brief-em3d-45: the drawing ──────────────────────────────────────────────────────────

    /// <summary>Shift+A — the Draw popup at the cursor; one letter (underlined) arms each tool.</summary>
    private void OnDrawMenuRequested()
    {
        if (_vm is null) return;
        var vm = _vm;
        _drawMenu.Items.Clear();
        foreach (var (kind, letter, icon) in C3dEditorViewModel.DrawTools)
        {
            string name = kind.ToString();
            int at = name.IndexOf(letter.ToString(), StringComparison.OrdinalIgnoreCase);
            string header = at >= 0 ? name.Insert(at, "_") : name;
            var item = new MenuItem
            {
                Header = header,
                InputGesture = new KeyGesture(Enum.Parse<Key>(letter.ToString())),
                Icon = Viewer3DPathGlyph.Named(icon) is { } drawn
                    ? new Viewer3DPathGlyph { Data = drawn }
                    : new Material.Icons.Avalonia.MaterialIcon { Kind = Enum.Parse<Material.Icons.MaterialIconKind>(icon), Width = 16, Height = 16 },
            };
            item.Click += (_, _) => { vm.Arm(kind); Pane.Focus(); };
            _drawMenu.Items.Add(item);
        }
        _drawMenu.Open(Pane);
    }

    private void OnDrawMenuKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None) return;
        string k = e.Key.ToString();
        if (k.Length == 1 && _vm.ArmByLetter(k[0]))
        {
            _drawMenu.Close();
            Pane.Focus();
            e.Handled = true;
        }
    }

    private void OnFieldFocusRequested()
        => Dispatcher.UIThread.Post(() =>
        {
            FieldInput.Focus();
            FieldInput.CaretIndex = FieldInput.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);

    private void OnFieldKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Tab: _vm.FieldTab(); FieldInput.CaretIndex = FieldInput.Text?.Length ?? 0; e.Handled = true; break;
            case Key.Enter:
                _vm.FieldEnter();
                if (!_vm.FieldOpen) Pane.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnPlaneOffsetKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPlaneOffset(); Pane.Focus(); e.Handled = true; }
    }

    private void OnPlaneOffsetLostFocus(object? sender, RoutedEventArgs e) { if (!_cancellingEdit) _vm?.CommitPlaneOffset(); }

    // 3D editor bugs round 2 — the Snap combobox is the layout editor's: a ladder pick commits at once, typed text on
    // Enter or when focus leaves.
    private void OnSnapDistanceKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is ComboBox cb) { _vm?.CommitSnapDistanceText(cb.Text ?? ""); Pane.Focus(); e.Handled = true; }
    }

    private void OnSnapDistanceLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_cancellingEdit && sender is ComboBox cb) _vm?.CommitSnapDistanceText(cb.Text ?? "");
    }

    private void OnSnapDistanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string text }) _vm?.CommitSnapDistanceText(text);
    }

    private void OnDefineKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.DefineEnter(); e.Handled = true; }
    }

    private C3dVariableRow? RowOf(object? sender) => (sender as Control)?.DataContext as C3dVariableRow;

    private void OnVariableKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RowOf(sender) is not { } row) return;
        _vm?.Variables?.Edit(row);
        e.Handled = true;
    }

    private void OnVariableSet(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Edit(r); }
    private void OnVariableRename(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Rename(r); }
    private void OnVariableDelete(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Delete(r); }
    private void OnVariableInline(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.InlineAndDelete(r); }
    private void OnVariableLink(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Link(r, true); }
    private void OnVariableUnlink(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Link(r, false); }
    private void OnVariablePromote(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Promote(r); }
    private void OnVariableAdd(object? sender, RoutedEventArgs e) => _vm?.Variables?.Add();

    /// <summary>Enter in the Add a VAR row adds it, as Enter sets a row's value: only the + below it did, and a VAR typed and
    /// Entered was never added — the document stayed clean and the name a dimension used stayed unknown.</summary>
    private void OnNewVariableKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm?.Variables?.Add();
        e.Handled = true;
    }

    // brief-em3d-48 — the breadcrumb and the Pop Out button: the view model asks about a dirty child.
    private void OnPopOutClick(object? sender, RoutedEventArgs e) => _ = _vm?.PopOutAsync();

    private void OnPushInClick(object? sender, RoutedEventArgs e) => _vm?.PushIntoSelected();

    private void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index }) _ = _vm?.PopToAsync(index);
    }
}
