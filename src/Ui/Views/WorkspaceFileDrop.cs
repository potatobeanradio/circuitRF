using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views;

/// <summary>
/// A <c>.cws</c> dropped on a document opens that workspace in a new window — the same thing a drop
/// on the Project Tree does (<see cref="TreeDrop.ForDroppedPath"/>, R-mw3-12).
///
/// <para>Attached to the WINDOW, not to each canvas: drag events bubble, so a drop that no editor
/// claimed (none of them claims a workspace file) arrives here wherever it landed. Only a drop that is
/// still unhandled is taken, so the Project Tree — which opens a dropped <c>.cws</c> itself — is not
/// answered twice.</para>
/// </summary>
internal static class WorkspaceFileDrop
{
    public static void Attach(Interactive window)
    {
        window.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        window.AddHandler(DragDrop.DropEvent,     OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled || WorkspacePathIn(e) is null) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled     = true;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || WorkspacePathIn(e) is not { } cws) return;
        e.Handled = true;
        // After the drag completes: opening a window from inside the platform's drop callback is what
        // the Project Tree defers for too (RunAfterDragCompletes).
        Dispatcher.UIThread.Post(() => ViewModels.WorkspaceViewModel.OpenDroppedWorkspace(cws),
                                 DispatcherPriority.Background);
    }

    /// <summary>The first dropped OS file that is a workspace, or null.</summary>
    internal static string? WorkspacePathIn(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            IEnumerable<string?> paths = item.TryGetRaw(DataFormat.File) switch
            {
                IStorageItem single             => [single.Path?.LocalPath],
                IEnumerable<IStorageItem> files => files.Select(f => f.Path?.LocalPath),
                _                               => [],
            };
            foreach (var p in paths)
                if (p is not null && TreeDrop.ForDroppedPath(p).Action == TreeDropAction.OpenWorkspace)
                    return p;
        }
        return null;
    }
}
