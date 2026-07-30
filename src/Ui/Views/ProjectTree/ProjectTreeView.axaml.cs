using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
    private void OnWindowActivated(object? sender, System.EventArgs e)
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (DataContext is ProjectTreeTool tool)
                tool.Refresh();
        }, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
    }

}
