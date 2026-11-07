using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views;

/// <summary>
/// A <c>.cws</c>, or a workspace folder, dropped on a document opens that workspace — in a new window, or in this one when it has
/// no workspace open — the same thing a drop on the Project Tree does (<see cref="TreeDrop.ForDroppedPath"/>, R-mw3-12).
///
/// <para>Attached to the WINDOW, not to each canvas: drag events bubble, so a drop that no editor
/// claimed (none of them claims a workspace file) arrives here wherever it landed. Only a drop that is
/// still unhandled is taken, so the Project Tree — which opens a dropped <c>.cws</c> itself — is not
/// answered twice.</para>
///
/// <para><b>Every control takes the drag, so a document that accepts no drop of its own still gets here.</b> Avalonia reads
/// AllowDrop on the deepest control under the pointer, so with it set on the canvases alone there was no target at all over a
/// README, an EM setup or a panel. With every control a target, a drag this window will do nothing with has to say so, or the
/// cursor would promise a drop that does nothing.</para>
/// </summary>
internal static class WorkspaceFileDrop
{
    public static void Attach(Window window)
    {
        DragDrop.SetAllowDrop(window, true);   // inherited, so every control below it takes the drag
        window.AddHandler(DragDrop.DragOverEvent,  OnDragOver);
        window.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        window.AddHandler(DragDrop.DropEvent,      OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.DragEffects = WorkspacePathIn(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled     = true;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || WorkspacePathIn(e) is not { } cws) return;
        e.Handled = true;
        var workspace = WorkspaceLocator.For(e.Source) ?? (sender as StyledElement)?.DataContext as ViewModels.WorkspaceViewModel;
        // After the drag completes: opening a window from inside the platform's drop callback is what
        // the Project Tree defers for too (RunAfterDragCompletes).
        Dispatcher.UIThread.Post(() =>
        {
            if (workspace is not null) workspace.OpenDroppedWorkspaceHere(cws);
            else ViewModels.WorkspaceViewModel.OpenDroppedWorkspace(cws);
        }, DispatcherPriority.Background);
    }

    /// <summary>The <c>.cws</c> of the first dropped OS file or folder that is a workspace, or null.</summary>
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
                if (p is not null && TreeDrop.ForDroppedPath(p) is { Action: TreeDropAction.OpenWorkspace } workspace)
                    return workspace.Path;
        }
        return null;
    }
}
