using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.Schematic;

/// <summary>What a drop on a Project Tree should do.</summary>
public enum TreeDropAction
{
    /// <summary>Not ours, or nothing to do — the drag-over answers <c>DragDropEffects.None</c>.</summary>
    None,

    /// <summary>A cell from another workspace: ask copy-vs-reference (MW3 §1).</summary>
    Cell,

    /// <summary>A loose file from another workspace's tree: copy it in (R-mw3-11).</summary>
    File,

    /// <summary>A <c>.cws</c>: open that workspace in a window of its own (R-mw3-12).</summary>
    OpenWorkspace,

    /// <summary>An ordinary OS file drop: bookmark it, which is what the tree did before MW3.</summary>
    KnownFile,
}

/// <summary>The action and the path it applies to.</summary>
public readonly record struct TreeDropIntent(TreeDropAction Action, string Path)
{
    public static readonly TreeDropIntent None = new(TreeDropAction.None, "");
}

/// <summary>
/// The rule a Project Tree drop follows, kept out of the view so it can be asserted rather than
/// inferred (MW3 §6 gate 1). <see cref="Views.ProjectTree.ProjectTreeView"/> calls it from both
/// <c>DragOver</c> and <c>Drop</c>, so the effect the cursor promises and the thing that happens are
/// the same decision and cannot drift apart.
/// </summary>
public static class TreeDrop
{
    /// <summary>
    /// What one of circuitRF's own tree payloads means when dropped on the tree of
    /// <paramref name="receivingWorkspaceRoot"/>.
    ///
    /// <para><b>R-mw3-4: a SAME-workspace drag returns <see cref="TreeDropAction.None"/>.</b>
    /// Dragging a cell within one workspace's tree did nothing before this feature and must go on
    /// doing nothing. The comparison is the ordinary workspace-containment test, taken against the
    /// RECEIVING tree's root — the payload already carries an absolute path, so the drop knows
    /// precisely which cell in which workspace it came from and nothing had to be added to the
    /// wire.</para>
    /// </summary>
    public static TreeDropIntent ForPayload(string? text, string? receivingWorkspaceRoot)
    {
        if (text is null || receivingWorkspaceRoot is null) return TreeDropIntent.None;

        if (CellDragPayload.TryParse(text, out var cell))
            return Foreign(cell.CellAbsPath, receivingWorkspaceRoot, TreeDropAction.Cell);

        if (WorkspaceFileDragPayload.TryParse(text, out var file))
            return Foreign(file.FileAbsPath, receivingWorkspaceRoot, TreeDropAction.File);

        // The Data Display's own payload, arriving on a tree instead. A data file is still a file,
        // and giving it a second spelling for MW3's sake would have broken the drop it already
        // serves — so this reader accepts both rather than the drag source emitting both.
        if (NpyFileDragPayload.TryParse(text, out var npy))
            return Foreign(npy.AbsolutePath, receivingWorkspaceRoot, TreeDropAction.File);

        return TreeDropIntent.None;
    }

    /// <summary>
    /// What an OS file-list entry means. A <c>.cws</c> OPENS (R-mw3-12) and is never copied —
    /// copying a workspace into a workspace is not a thing; everything else is bookmarked, exactly as
    /// before MW3.
    /// </summary>
    public static TreeDropIntent ForDroppedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return TreeDropIntent.None;

        // ".cws" is the whole file name in a workspace folder, and an extension anywhere else.
        bool isCws = Path.GetFileName(path).Equals(".cws", StringComparison.OrdinalIgnoreCase)
                  || Path.GetExtension(path).Equals(".cws", StringComparison.OrdinalIgnoreCase);

        return new TreeDropIntent(isCws ? TreeDropAction.OpenWorkspace : TreeDropAction.KnownFile, path);
    }

    private static TreeDropIntent Foreign(string path, string receivingRoot, TreeDropAction action)
    {
        try
        {
            return WorkspaceRootFinder.IsOutside(Path.GetFullPath(path), receivingRoot)
                ? new TreeDropIntent(action, Path.GetFullPath(path))
                : TreeDropIntent.None;
        }
        catch { return TreeDropIntent.None; }
    }
}
