using System;
using System.IO;
using Dock.Model.Core;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Docking;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Reveal in Workspace Tree, on a document tab's own context menu — where the right-clicked document lives in this
/// workspace, for a workspace with too many files to find it by eye. Docked or floating alike: the workspace is asked of
/// the dockable (<see cref="WorkspaceOf"/>), never of the window the menu opened in.
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// True when the right-clicked document is a file of this workspace's tree — the menu item hides otherwise. A file
    /// under the workspace folder counts even before the tree has a row for it (saved since the last scan): the reveal
    /// rescans. A file elsewhere counts only when the tree lists it (a referenced library's, a known file).
    /// </summary>
    internal static bool HasTreeRevealRoute(IDockable? dockable)
    {
        if ((dockable as IFileBackedDocument)?.FilePath is not { Length: > 0 } path) return false;
        if (WorkspaceOf(dockable) is not { } workspace) return false;
        return workspace.IsInWorkspaceTree(path);
    }

    private bool IsInWorkspaceTree(string path)
    {
        if (_factory.ProjectTreeTool is not { HasWorkspace: true } tree) return false;
        if (tree.HasNodeFor(path)) return true;
        if (CurrentWorkspaceRoot is not { } root) return false;
        string full = Path.GetFullPath(path);
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(dir, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tab menu's Reveal in Workspace Tree: brings the Workspace Tree forward (opening it if it was closed),
    /// expands the path down to the document's row, selects it and scrolls it into sight.</summary>
    internal void RevealInWorkspaceTree(IDockable dockable)
    {
        if ((dockable as IFileBackedDocument)?.FilePath is not { Length: > 0 } path) return;
        if (_factory.ProjectTreeTool is not { } tree) return;

        ShowToolPanel(DockPanelIds.ProjectTree);
        if (!tree.Reveal(path))
            Messages.Info($"{Path.GetFileName(path)} is not listed in the Workspace Tree.");
    }
}
