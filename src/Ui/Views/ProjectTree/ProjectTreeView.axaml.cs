using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView : UserControl
{
    // ── Cell drag source state ─────────────────────────────────────────────────

    private PointerPressedEventArgs? _cellPressArgs;
    private Point                    _cellPressPos;
    private const double             DragThreshold = 5.0;

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
        if (GetCellNodeFromSource(e.Source as Visual) is null) return;
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

        var vm        = GetCellNodeFromSource(_cellPressArgs.Source as Visual);
        var savedArgs = _cellPressArgs;
        _cellPressArgs = null; // clear before await to prevent re-entry
        if (vm is null) return;

        var payload      = new CellDragPayload(vm.AbsolutePath);
        var transferItem = new DataTransferItem();
        transferItem.Set(DataFormat.Text, payload.Serialize());
        var transfer = new DataTransfer();
        transfer.Add(transferItem);
        await DragDrop.DoDragDropAsync(savedArgs, transfer, DragDropEffects.Copy);
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
        => _cellPressArgs = null;

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
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Double-click → open/activate the selected node ────────────────────────

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProjectTreeTool tool) return;
        if (tool.SelectedItem is not ProjectTreeNodeViewModel node) return;
        node.ActivateCommand.Execute(null);
    }
}
