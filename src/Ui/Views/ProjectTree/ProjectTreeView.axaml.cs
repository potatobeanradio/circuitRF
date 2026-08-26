using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView : UserControl
{
    // ── Cell drag source state + double-click tracking ────────────────────────

    private PointerPressedEventArgs?     _cellPressArgs;
    private Point                        _cellPressPos;
    private const double                 DragThreshold = 5.0;

    // Manual double-click detection — avoids Avalonia's DoubleTapped ~200ms
    // single-tap confirmation delay by tracking two rapid PointerPressed events
    // on the same node ourselves.
    private ProjectTreeNodeViewModel? _lastPressVm;
    private long                      _lastPressTick;        // Environment.TickCount64
    private const int                 DoubleClickMs = 400;

    public ProjectTreeView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent,     OnFileDrop);

        // Drag source: cell nodes in the tree. Subscribe on TheTreeView so only
        // drags originating from within the tree are candidates.
        TheTreeView.AddHandler(PointerPressedEvent,  OnTreePointerPressed,  handledEventsToo: true);
        TheTreeView.AddHandler(PointerMovedEvent,    OnTreePointerMoved,    handledEventsToo: false);
        TheTreeView.AddHandler(PointerReleasedEvent, OnTreePointerReleased, handledEventsToo: true);

        // Selecting a row must not drag the HORIZONTAL scroll (owner, 2026-08-25). Subscribed on the
        // TreeView rather than on this control because RequestBringIntoView only BUBBLES (probed —
        // it has no tunnel route), and the TreeView sits below TreeScroll, so a handler here runs
        // before the ScrollViewer's own.
        TheTreeView.AddHandler(RequestBringIntoViewEvent, OnTreeBringIntoView);

        // Page Up / Page Down / Home / End scroll the listing. Tunnelled, for the same reason the
        // .ctech editor's own handler is: the focused TreeViewItem would otherwise move the SELECTION
        // on Home/End first and swallow the key, and a moved selection is not what was asked for here
        // — a workspace with a board import in it is long, and paging through it must not change what
        // is selected on the way past.
        AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel);

        // Opening the search has to put the caret in the field — a magnifier button that reveals a
        // box you then have to click is two gestures for one intent.
        DataContextChanged += OnDataContextChangedForSearch;

        // …and activating the PANEL has to put focus somewhere inside it, or the scroll keys below
        // never reach this control at all. See OnActivationFocusRequested.
        DataContextChanged += OnDataContextChangedForActivation;

        // Escape closes the search (which clears it, so the tree comes back).
        //
        // handledEventsToo: true is REQUIRED, not defensive, and leaving it off is why Escape did
        // nothing at first (owner, 2026-08-25). WorkspaceWindow.axaml binds Escape to
        // DisarmPlacementCommand, and Window.KeyBindings are processed before visual-tree routing and
        // always mark the event Handled — so a handler that skips handled events never sees Escape at
        // all. SchematicView, SymbolEditorView and LayoutEditorView all carry the identical argument
        // on their own Escape handlers; this panel is the fourth to need it.
        AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // ── Activation focus (owner, 2026-08-25) ──────────────────────────────────

    private IActivatableTool? _activationTool;

    private void OnDataContextChangedForActivation(object? sender, EventArgs e)
    {
        if (_activationTool is not null) _activationTool.ActivationFocusRequested -= OnActivationFocusRequested;
        _activationTool = DataContext as IActivatableTool;
        if (_activationTool is null) return;

        _activationTool.ActivationFocusRequested += OnActivationFocusRequested;

        // The panel can be activated BEFORE this view exists — first layout, or a restored dock
        // arrangement. The request is held until a view turns up to consume it, exactly as
        // IActivatableDocument does for document tabs.
        if (_activationTool.ConsumeActivationFocus()) OnActivationFocusRequested();
    }

    /// <summary>
    /// Puts keyboard focus inside this panel when it is activated.
    ///
    /// <para><b>Owner:</b> <i>"if I click on the title bar of a window (like Project, or Library) …
    /// I am forced to click somewhere inside the window before the keystrokes will register."</i>
    /// Clicking a tool's tab leaves focus on the TAB, which is Dock's chrome and lives outside this
    /// control — so a key event's route never passes through here and
    /// <see cref="OnScrollKeyDown"/> is never called.</para>
    ///
    /// <para>The TreeView is preferred over the scroller because it takes the arrow keys too, so an
    /// activated panel is fully navigable; the scroller is the fallback for the states where the tree
    /// cannot take focus (no workspace open, so it is not even visible).</para>
    /// </summary>
    /// <para>Declined when focus already sits inside this panel — activation fires for a click
    /// ANYWHERE in it, the Search box included, and this grab is posted, so it would otherwise land
    /// after the click gave the box the caret and take it straight back (owner, 2026-08-26, against
    /// the Library palette; the same shape is here). See <see cref="PanelActivationFocus"/>.</para>
    private void OnActivationFocusRequested() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (PanelActivationFocus.AlreadyInside(this)) return;
            if (!TheTreeView.Focus()) TreeScroll.Focus();
        }, Avalonia.Threading.DispatcherPriority.Input);

    // ── Bring-into-view: vertical only ────────────────────────────────────────

    /// <summary>
    /// Clicking a row focuses it, and Avalonia's focus handling asks the nearest scroller to bring it
    /// into view — on BOTH axes. With horizontal scrolling enabled (§3.1) a row wider than the panel is
    /// never fully "in view", so simply selecting a long cell name nudged the horizontal scrollbar
    /// (owner, 2026-08-25). Vertical bring-into-view is wanted and keeps working: arrow-key navigation
    /// still scrolls the selected row into sight.
    ///
    /// <para><c>ScrollViewer.BringIntoViewOnFocusChange="False"</c> would have been the one-liner and is
    /// the wrong tool — it kills both axes, taking keyboard navigation with it.</para>
    ///
    /// <para>The rect arrives in the TARGET's coordinate space, and the transform from a tree row to the
    /// scroller is a pure translation, so placing a zero-width rect at <c>-tx</c> maps it to x = 0 in the
    /// scroller's space — the left edge of the viewport, which is by definition already visible. Nothing
    /// to scroll to horizontally; Y and Height are passed through untouched.</para>
    /// </summary>
    private void OnTreeBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is not Visual target) return;
        if (target.TransformToVisual(TreeScroll) is not { } toScroll) return;

        double tx = toScroll.Transform(new Point(0, 0)).X;
        e.TargetRect = new Rect(-tx, e.TargetRect.Y, 0, e.TargetRect.Height);
    }

    // ── Search field focus + Escape ───────────────────────────────────────────

    private ProjectTreeTool? _searchTool;

    private void OnDataContextChangedForSearch(object? sender, EventArgs e)
    {
        if (_searchTool is not null) _searchTool.PropertyChanged -= OnToolPropertyChangedForSearch;
        _searchTool = DataContext as ProjectTreeTool;
        if (_searchTool is not null) _searchTool.PropertyChanged += OnToolPropertyChangedForSearch;
    }

    private void OnToolPropertyChangedForSearch(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectTreeTool.IsSearchOpen)) return;
        if (_searchTool is not { } tool) return;

        if (tool.IsSearchOpen)
        {
            // Posted: the box is only just becoming visible, and a control that has not been laid out
            // yet cannot take focus.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => SearchBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);
            return;
        }

        // Closing leaves the caret inside a control that is no longer on screen — which both swallows
        // subsequent keystrokes and makes OnScrollKeyDown read `e.Source is TextBox` as true, so
        // Home/End would go on yielding to a field the user cannot see. Hand focus back to the tree,
        // which is where the keys are meant to land once the search is put away.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => TheTreeView.Focus(), Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not ProjectTreeTool { IsSearchOpen: true } tool) return;

        tool.IsSearchOpen = false;   // clears the query on its way (see OnIsSearchOpenChanged)
        e.Handled = true;
    }

    // ── Page Up / Page Down / Home / End over the tree ────────────────────────

    private void OnScrollKeyDown(object? sender, KeyEventArgs e)
    {
        // An open dropdown owns all four keys — it is navigating its own items, and the list behind
        // it is not what the user is looking at.
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        var action = PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        PanelScrollKeys.Apply(action.Value, TreeScroll);
        e.Handled = true;
    }

    // ── Cell drag SOURCE ─────────────────────────────────────────────────────

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _cellPressArgs = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var source = e.Source as Visual;

        // Clicks on the expander ToggleButton (disclosure triangle) are for expand/collapse only —
        // exclude them from double-click activation tracking so rapid expand→collapse doesn't
        // accidentally open a document.
        if (IsOnExpander(source)) return;

        // Double-click detection: two left-presses on the same node within DoubleClickMs.
        var vm   = GetAnyNodeFromSource(source);
        var tick = Environment.TickCount64;
        if (vm is not null && vm == _lastPressVm && tick - _lastPressTick <= DoubleClickMs)
        {
            _lastPressVm   = null;       // reset so a third click doesn't re-fire
            _lastPressTick = 0;
            vm.ActivateCommand.Execute(null);
            return;
        }
        _lastPressVm   = vm;
        _lastPressTick = tick;

        // Cell DnD source capture, and (R-dd-3) .npy file DnD source capture — dragging a data
        // file from the tree onto a Data Display plot adds it as a dataset in one motion, the
        // same idiom as palette→schematic/palette→layout drag-drop.
        if (GetCellNodeFromSource(source) is null && GetNpyFileNodeFromSource(source) is null) return;
        _cellPressArgs = e;
        _cellPressPos  = e.GetPosition(this);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_cellPressArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _cellPressArgs = null;
            return;
        }

        var pos   = e.GetPosition(this);
        var delta = pos - _cellPressPos;
        if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < DragThreshold) return;

        var source    = _cellPressArgs.Source as Visual;
        var cellVm    = GetCellNodeFromSource(source);
        var npyVm     = cellVm is null ? GetNpyFileNodeFromSource(source) : null;
        var savedArgs = _cellPressArgs;
        _cellPressArgs = null; // clear before await to prevent re-entry
        if (cellVm is null && npyVm is null) return;

        string serialized = cellVm is not null
            ? new CellDragPayload(cellVm.AbsolutePath).Serialize()
            : new NpyFileDragPayload(npyVm!.AbsolutePath).Serialize();

        var transferItem = new DataTransferItem();
        transferItem.Set(DataFormat.Text, serialized);
        var transfer = new DataTransfer();
        transfer.Add(transferItem);
        await DragDrop.DoDragDropAsync(savedArgs, transfer, DragDropEffects.Copy);
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
        => _cellPressArgs = null;

    // Returns true when the visual is on the expander ToggleButton (the disclosure triangle),
    // so we can exclude those clicks from double-click activation tracking.
    private static bool IsOnExpander(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is ToggleButton) return true;
            if (v is TreeViewItem)  return false;   // reached row content — not the expander
            v = v.GetVisualParent();
        }
        return false;
    }

    // Walk upward to the nearest TreeViewItem with any ProjectTreeNodeViewModel.
    private static ProjectTreeNodeViewModel? GetAnyNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem { DataContext: ProjectTreeNodeViewModel vm })
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // Walk the visual tree upward from the event source to find a cell TreeViewItem.
    private static ProjectTreeNodeViewModel? GetCellNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem item
                && item.DataContext is ProjectTreeNodeViewModel { Kind: NodeKind.Cell } vm)
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // Walk the visual tree upward from the event source to find an .npy file TreeViewItem
    // (R-dd-3) — .npy files classify as NodeKind.OtherFile (no dedicated NodeKind exists for them).
    private static ProjectTreeNodeViewModel? GetNpyFileNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem item
                && item.DataContext is ProjectTreeNodeViewModel { Kind: NodeKind.OtherFile } vm
                && string.Equals(Path.GetExtension(vm.AbsolutePath), ".npy", StringComparison.OrdinalIgnoreCase))
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // ── Drag-drop receive (file drop onto tree) ──────────────────────────────

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (HasDroppedPaths(e))
        { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
        else
            e.DragEffects = DragDropEffects.None;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ProjectTreeTool tool) return;
        foreach (var path in ExtractDroppedPaths(e))
            tool.AddKnownFile(path);
    }

    // Returns true if the drag contains at least one valid file/directory path.
    private static bool HasDroppedPaths(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            var raw = item.TryGetRaw(DataFormat.File);
            if (RawContainsValidPath(raw)) return true;
        }
        return false;
    }

    // Extracts all valid file/directory paths from a drop event.
    // Mirrors SchematicCanvas.TryExtractImagePath but accepts any extension and directories.
    private static IEnumerable<string> ExtractDroppedPaths(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            var raw = item.TryGetRaw(DataFormat.File);
            foreach (var path in PathsFromRaw(raw))
                yield return path;
        }
    }

    private static bool RawContainsValidPath(object? raw)
    {
        foreach (var p in PathsFromRaw(raw))
            if (p is not null) return true;
        return false;
    }

    private static IEnumerable<string> PathsFromRaw(object? raw)
    {
        IEnumerable<string?> candidates = raw switch
        {
            IStorageItem single             => [single.Path?.LocalPath],
            IEnumerable<IStorageItem> files => System.Linq.Enumerable.Select(files, f => f.Path?.LocalPath),
            string s                        => [(string?)s],
            _                               => [],
        };
        foreach (var path in candidates)
        {
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
                yield return path;
        }
    }

    // ── Context menu Opening handler (Item 4) ────────────────────────────────

    // Fires INPC on IsSaveable/SaveHeader so the menu shows the real live state.
    private void OnNodeContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is ContextMenu { DataContext: ProjectTreeNodeViewModel vm })
            vm.RefreshDynamicMenuState();
    }

    // ── On-focus refresh (Layer 4) ─────────────────────────────────────────────

    private bool _refreshPending;
    private Window? _attachedWindow;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedWindow = TopLevel.GetTopLevel(this) as Window;
        if (_attachedWindow is not null)
            _attachedWindow.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_attachedWindow is not null)
        {
            _attachedWindow.Activated -= OnWindowActivated;
            _attachedWindow = null;
        }
    }

    // Debounced re-scan on window focus: guards against rapid consecutive Activated events.
    //
    // RefreshAsync, not Refresh: this is the FREQUENT path — it fires on open, on every alt-tab back
    // and on every dialog close — and nothing waits on its result, so the filesystem walk (~92 ms on a
    // 600-cell workspace, measured) belongs off the UI thread. It also does nothing at all when the
    // scan finds the same tree that is already rendered, which is what stopped the tree flashing on
    // every window activation (owner, 2026-08-25).
    private void OnWindowActivated(object? sender, System.EventArgs e)
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (DataContext is ProjectTreeTool tool)
                _ = tool.RefreshAsync();
        }, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
    }

}
