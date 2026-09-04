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

    /// <summary>
    /// A cell, folder or loose file from THIS workspace's own tree: move it into the folder it was
    /// dropped on (TM1). This is the drop MW3 deliberately left inert — see
    /// <see cref="TreeDrop.ForPayload"/>.
    /// </summary>
    Move,
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
    /// <para><b>MW3's R-mw3-4 said a SAME-workspace drag returns
    /// <see cref="TreeDropAction.None"/>; TM1 supersedes that half and nothing else.</b> It now
    /// returns <see cref="TreeDropAction.Move"/> — the drop MW3 deliberately made inert, because the
    /// reference repointing a move needs did not exist yet. The cross-workspace half is unchanged:
    /// the comparison is still the ordinary workspace-containment test taken against the RECEIVING
    /// tree's root, and the payload still carries an absolute path so the drop knows precisely which
    /// cell in which workspace it came from with nothing added to the wire.</para>
    ///
    /// <para><b>Whether the move is ALLOWED is a separate question, asked by <see cref="TreeMove"/>
    /// against the destination row</b> — which this method does not see. The two are deliberately not
    /// merged: this one says what the payload MEANS, and that one says whether it may land there,
    /// with its own sentence per refusal.</para>
    /// </summary>
    public static TreeDropIntent ForPayload(string? text, string? receivingWorkspaceRoot)
    {
        if (text is null || receivingWorkspaceRoot is null) return TreeDropIntent.None;

        if (CellDragPayload.TryParse(text, out var cell))
            return Sort(cell.CellAbsPath, receivingWorkspaceRoot, TreeDropAction.Cell);

        if (WorkspaceFileDragPayload.TryParse(text, out var file))
            return Sort(file.FileAbsPath, receivingWorkspaceRoot, TreeDropAction.File);

        // A folder has no cross-workspace meaning at all — TM1 is the only thing that can be done
        // with one, so it is a Move or it is nothing.
        if (FolderDragPayload.TryParse(text, out var folder))
        {
            try
            {
                string abs = Path.GetFullPath(folder.FolderAbsPath);
                return WorkspaceRootFinder.IsOutside(abs, receivingWorkspaceRoot)
                    ? TreeDropIntent.None
                    : new TreeDropIntent(TreeDropAction.Move, abs);
            }
            catch { return TreeDropIntent.None; }
        }

        // The Data Display's own payload, arriving on a tree instead. A data file is still a file,
        // and giving it a second spelling for MW3's sake would have broken the drop it already
        // serves — so this reader accepts both rather than the drag source emitting both.
        if (NpyFileDragPayload.TryParse(text, out var npy))
            return Sort(npy.AbsolutePath, receivingWorkspaceRoot, TreeDropAction.File);

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

    /// <summary>Outside the receiving workspace, it is <paramref name="foreignAction"/> — MW3's
    /// copy-or-reference. Inside, it is TM1's move.</summary>
    private static TreeDropIntent Sort(string path, string receivingRoot, TreeDropAction foreignAction)
    {
        try
        {
            string abs = Path.GetFullPath(path);
            return new TreeDropIntent(
                WorkspaceRootFinder.IsOutside(abs, receivingRoot) ? foreignAction : TreeDropAction.Move,
                abs);
        }
        catch { return TreeDropIntent.None; }
    }
}
