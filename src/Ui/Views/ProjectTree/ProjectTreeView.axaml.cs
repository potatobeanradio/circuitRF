using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView : UserControl
{
    public ProjectTreeView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent,     OnFileDrop);
    }

    // ── Drag-drop receive (Layer 1) ────────────────────────────────────────────

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
