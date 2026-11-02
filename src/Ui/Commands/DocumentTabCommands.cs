using CommunityToolkit.Mvvm.Input;
using Dock.Model.Core;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Commands;

/// <summary>
/// Commands for circuitRF's own entries in the document tab-strip context menu
/// (<c>Styles/DocumentTabContextMenu.axaml</c>).
///
/// Static, and parameterized by the dockable itself, on purpose. The menu is one shared
/// <see cref="Avalonia.Controls.ContextMenu"/> instance whose DataContext is whichever
/// <c>IDockable</c> was right-clicked, and a torn-off document lives in a floating host window
/// whose DataContext is NOT the WorkspaceViewModel — so routing through
/// <c>$parent[DockControl].DataContext</c> would work in the main window and quietly do nothing in
/// a floating one.
/// </summary>
public static class DocumentTabCommands
{
    /// <summary>Platform-correct label, bound by the menu item via <c>x:Static</c>.</summary>
    public static string RevealLabel => FileReveal.Label;

    /// <summary>
    /// Reveals the right-clicked document's file. The parameter is the dockable (the menu's own
    /// DataContext); a document that is not file-backed, or is still a scratch document, is a
    /// no-op — the menu item hides itself in that case rather than showing as disabled.
    /// </summary>
    public static IRelayCommand<object?> Reveal { get; } = new RelayCommand<object?>(
        dockable => FileReveal.Reveal((dockable as IFileBackedDocument)?.FilePath));

    /// <summary>Copy Path — the folder holding the right-clicked document's file, onto the clipboard.
    /// Hidden, like <see cref="Reveal"/>, on a document with no file.</summary>
    public static IAsyncRelayCommand<object?> CopyPath { get; } = new AsyncRelayCommand<object?>(
        dockable => PathClipboard.CopyAsync((dockable as IFileBackedDocument)?.FilePath, full: false),
        AsyncRelayCommandOptions.AllowConcurrentExecutions);

    /// <summary>Copy Full Path — the right-clicked document's file path, onto the clipboard.</summary>
    public static IAsyncRelayCommand<object?> CopyFullPath { get; } = new AsyncRelayCommand<object?>(
        dockable => PathClipboard.CopyAsync((dockable as IFileBackedDocument)?.FilePath, full: true),
        AsyncRelayCommandOptions.AllowConcurrentExecutions);

    /// <summary>Reveal in Workspace Tree — the Workspace Tree takes focus with the right-clicked document's row expanded
    /// to, selected and scrolled into sight. Hidden on a document that is not a file of the workspace's tree. The
    /// workspace is resolved from the dockable, as <see cref="Save"/>'s is, so a floating tab reveals into its own
    /// workspace's tree.</summary>
    public static IRelayCommand<object?> RevealInTree { get; } = new RelayCommand<object?>(
        dockable =>
        {
            if (dockable is IDockable d && WorkspaceViewModel.WorkspaceOf(d) is { } workspace)
                workspace.RevealInWorkspaceTree(d);
        });

    /// <summary>
    /// Saves the right-clicked document — the tab it was invoked on, never the active one, which is
    /// the whole difference between this and File ▸ Save.
    ///
    /// <para>Both save commands resolve the workspace from the DOCKABLE
    /// (<see cref="WorkspaceViewModel.WorkspaceOf"/>) for the same reason
    /// <see cref="Reveal"/> takes the dockable: a torn-off document's window has the document as its
    /// DataContext, so anything routed through the visual tree works docked and quietly does nothing
    /// floating. Nothing is written here — every route is the one File ▸ Save already uses; see
    /// <c>WorkspaceViewModel.TabSave.cs</c>.</para>
    ///
    /// <para><b><c>AllowConcurrentExecutions</c> is what keeps the item permanently enabled</b>, and
    /// that is the point rather than a side effect. An <c>AsyncRelayCommand</c> otherwise reports
    /// <c>CanExecute</c> false while it runs, and the shared menu is re-parented and re-DataContexted
    /// between right-clicks — a missed <c>CanExecuteChanged</c> would leave Save greyed on a dirty
    /// document with nothing on screen to say why. The two states a gate would have expressed are
    /// reported instead, by the route, in words.</para>
    /// </summary>
    public static IAsyncRelayCommand<object?> Save { get; } = new AsyncRelayCommand<object?>(
        async dockable =>
        {
            if (dockable is IDockable d && WorkspaceViewModel.WorkspaceOf(d) is { } workspace)
                await workspace.SaveFromTabAsync(d);
        },
        AsyncRelayCommandOptions.AllowConcurrentExecutions);

    /// <summary>Writes the right-clicked document to a file the user picks, and follows it there.
    /// Offered on every document kind that can BE followed to a new file — see
    /// <see cref="WorkspaceViewModel.HasSaveAsRoute"/> for the one saveable kind that cannot.</summary>
    public static IAsyncRelayCommand<object?> SaveAs { get; } = new AsyncRelayCommand<object?>(
        async dockable =>
        {
            if (dockable is IDockable d && WorkspaceViewModel.WorkspaceOf(d) is { } workspace)
                await workspace.SaveAsFromTabAsync(d);
        },
        AsyncRelayCommandOptions.AllowConcurrentExecutions);
}
