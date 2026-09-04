using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Moving a cell, a folder or a loose file inside ONE workspace's Project Tree (TM1).
///
/// <para><b>The gesture is an afternoon; the reference repointing is the work.</b> The owner's
/// requirement was stated separately from the drag itself and as a requirement rather than a wish:
/// moving a cell inside a workspace must not hurt the cells that reference it. Everything below the
/// <c>Directory.Move</c> is that sentence. <see cref="WorkspaceMove"/> holds the map, and
/// <see cref="MoveRefRegistry"/> holds the one table of what a move has to repair.</para>
///
/// <para><b>What this deliberately does NOT do.</b> There is no in-app undo, exactly as there is
/// none for Rename or Remove Cell (<c>ITreeActions.RemoveCellAsync</c>'s own doc comment says so),
/// and no move-specific undo stack is invented for it. Undo is a re-drag — which is why the success
/// message names BOTH the old and the new location: that sentence is what lets a user put it back
/// (R-tm1-19).</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// Moves <paramref name="sourcePath"/> into <paramref name="destFolderDir"/>. Called by the
    /// Project Tree's own drop handler after the platform drag loop has unwound (R-mw3-5/-6 — a
    /// modal shown from inside a drop handler is how a drag-drop deadlock is written; this one can
    /// show a save prompt).
    /// </summary>
    /// <param name="destFolderDir">The folder the drop resolved to, or null for the workspace root.</param>
    public async Task MoveInsideWorkspaceAsync(string sourcePath, string? destFolderDir)
    {
        if (CurrentWorkspacePath is null) return;
        string workspaceRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;

        // The SAME rule the drag-over asked, so the effect the cursor promised and the thing that
        // happens cannot disagree — and the source's kind is re-derived from disk rather than taken
        // from the payload, which is a string on the platform pasteboard.
        var intent = TreeMove.For(
            sourcePath, TreeMove.ClassifyForMove(sourcePath), destFolderDir, workspaceRoot);

        if (!intent.Permitted)
        {
            if (intent.Refusal != MoveRefusal.AlreadyThere && intent.Message.Length > 0)
                Messages.Warning(intent.Message);
            return;
        }

        // ── R-tm2-15: the safety net is laid BEFORE the move, or the move does not happen ──
        // A move whose forwarding record was lost is worse than a move that did not happen: the first
        // breaks every design that references the cell, quietly, on somebody else's machine; the
        // second is a message the librarian reads immediately. This is the ONE place in the feature
        // where refusing is correct, and it is the opposite of R-tm2-1 on purpose — R-tm2-1 refuses
        // to block the ORGANISING; this refuses to complete a move whose safety net could not be laid.
        //
        // The probe is a real write (R-sl2-1's rule), because a share ACL, a POSIX mode and a
        // read-only mount are all invisible to File.GetAttributes. It runs before anything is closed
        // or moved, so a refusal costs the user nothing but the gesture.
        if (!MoveRedirects.CanRecord(workspaceRoot, out string? recordError))
        {
            Messages.Warning(
                $"'{Path.GetFileName(intent.SourcePath)}' was not moved: its forwarding record could "
              + $"not be written to {MoveRedirects.FileName} ({recordError}). Without that record, any "
              + "project referencing this cell that is not open here would silently fail to find it.");
            return;
        }

        string oldPath = intent.SourcePath;
        string newPath = intent.DestPath;
        string name    = Path.GetFileName(oldPath);
        bool   isDir   = Directory.Exists(oldPath);

        // ── R-tm1-15: save and close what is open under the moved subtree ─────
        // The same block Rename uses, and for the same reason — a document held open on a file that
        // is about to move is a document writing to a path that no longer exists. Cancelling the
        // save prompt cancels the move.
        var reopen = new List<string>();
        var openHere = _openDocsByPath
            .Where(kvp => IsPathOrUnder(kvp.Key, oldPath))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();

        if (openHere.Count > 0)
        {
            var window = ResolveOwner(null);
            if (window is null) return;
            if (HasAnyDirtyWork() && !await PromptSaveBeforeClose(window, $"moving '{name}'"))
                return;

            foreach (var (key, dockable) in openHere)
            {
                reopen.Add(key);
                _factory.ForceCloseDockable(dockable);
                if (key.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))
                    RetireSessionIfUnreferenced(key);
                else if (key.EndsWith(".clay", StringComparison.OrdinalIgnoreCase))
                    RetireLayoutSessionIfUnreferenced(key);
            }
        }

        // ── R-tm1-3: resolve BEFORE the move ──────────────────────────────────
        // The alias table is memoised and ResolvePrimary reads the filesystem, so every reference is
        // resolved to an absolute path while the tree is still in its old shape. What is written
        // afterwards is pure path arithmetic on those absolutes.
        var scanRoots = new List<string> { workspaceRoot };
        scanRoots.AddRange(OtherOpenWorkspaceRoots());
        var capture = WorkspaceMove.Capture(scanRoots);

        // ── The move ──────────────────────────────────────────────────────────
        try
        {
            if (isDir) Directory.Move(oldPath, newPath);
            else       File.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Move failed: {ex.Message}");
            foreach (var key in reopen) ReopenMoved(key);   // nothing moved — put the tabs back
            return;
        }

        // ── R-tm1-16: rewrite second, and a failure is REPORTED, never rolled back ──
        // This is Rename's shipped bargain and it is the right one here too: a partial rewrite
        // leaves references a re-run repairs, whereas an attempted rollback moves the folder back
        // underneath references that were already updated.
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();

        var result = WorkspaceMove.Apply(capture, oldPath, newPath);
        foreach (var failure in result.Failures)
            Messages.Warning($"Reference rewrite failed: {failure}");

        // ── R-tm1-20: record the move for what the rewrite cannot reach ───────
        // Written UNCONDITIONALLY, including inside a workspace nobody shares — a workspace that is
        // private today is referenced next month, and a redirect that was never written cannot be
        // reconstructed.
        RecordMoveRedirect(workspaceRoot, oldPath, newPath);

        // ── R-tm1-17: reopen what was closed, at its new path ─────────────────
        foreach (var key in reopen)
            ReopenMoved(WorkspaceMove.Relocate(key, oldPath, newPath));

        // ── R-tm1-18: ONE refresh, at the end ─────────────────────────────────
        _factory.ProjectTreeTool?.Refresh();

        string where = FolderLabel(workspaceRoot, Path.GetDirectoryName(oldPath));
        string into  = FolderLabel(workspaceRoot, intent.DestFolder);
        Messages.Success(
            result.RewrittenFiles.Count == 0
                ? $"Moved '{name}' from {where} to {into}."
                : $"Moved '{name}' from {where} to {into}; updated {result.RewrittenFiles.Count} "
                + $"reference{(result.RewrittenFiles.Count == 1 ? "" : "s")}.",
            newPath);
    }

    /// <summary>
    /// Appends the forwarding record, after the move.
    ///
    /// <para>Whether one COULD be written was settled before the move by
    /// <see cref="MoveRedirects.CanRecord"/> (R-tm2-15), which is what makes gate 9's "the library is
    /// left untouched" true. A failure that still happens here — the share dropped in the interval —
    /// is reported and nothing more: the move has already happened, and a move that succeeded is not
    /// undone by a log that did not.</para>
    /// </summary>
    private void RecordMoveRedirect(string workspaceRoot, string oldPath, string newPath)
    {
        string? from = MoveRedirects.ToRootRelative(workspaceRoot, oldPath);
        string? to   = MoveRedirects.ToRootRelative(workspaceRoot, newPath);
        if (from is null || to is null) return;

        if (!MoveRedirects.Append(workspaceRoot, from, to, out string? error))
            Messages.Warning(
                $"The move succeeded, but its forwarding record could not be written to "
              + $"{MoveRedirects.FileName}: {error}. Another project referencing '{from}' will not "
              + "be able to find where it went.");
    }

    /// <summary>
    /// Reopens one document at a path. A directory is the cell placeholder;
    /// <see cref="OpenDocumentByPath"/> already dispatches every file kind by extension, which is
    /// what kept R-tm1-17 inside its own "if this is more than Relocate plus the existing open path,
    /// leave it out" budget.
    /// </summary>
    private void ReopenMoved(string path)
    {
        try
        {
            if (Directory.Exists(path)) OpenOrActivateCellPlaceholder(path, Path.GetFileName(path));
            else if (File.Exists(path)) OpenDocumentByPath(path);
        }
        catch (Exception ex)
        {
            // Not fatal and not silent: the move worked, one tab did not come back.
            Messages.Warning($"'{Path.GetFileName(path)}' could not be reopened after the move: {ex.Message}");
        }
    }

    /// <summary>A folder named the way the user sees it — its workspace-relative path, or the
    /// workspace's own name for the root. Both ends appear in the success message because that
    /// sentence is the only undo a move has (R-tm1-19).</summary>
    private static string FolderLabel(string workspaceRoot, string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return "the workspace root";
        try
        {
            string rel = Path.GetRelativePath(workspaceRoot, folder);
            return rel is "." or "" ? "the workspace root" : $"'{rel.Replace('\\', '/')}'";
        }
        catch { return $"'{folder}'"; }
    }
}
