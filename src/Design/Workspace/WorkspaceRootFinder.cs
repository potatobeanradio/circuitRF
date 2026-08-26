namespace CircuitRF.Design.Workspace;

/// <summary>
/// brief-foreign-documents.md R-fgn-3: walks up from a document's own absolute path to find the
/// nearest ancestor workspace (a directory containing a <c>.cws</c> file) — the git/solution-root
/// pattern. Framework-free, no Avalonia. This is what makes a document's technology resolve against
/// its OWN parent workspace rather than whichever workspace happens to be currently open: no new
/// state to carry and nothing to keep in sync, since the document already knows its own path, and the
/// walk stays correct even if the whole project folder is moved wholesale.
/// </summary>
public static class WorkspaceRootFinder
{
    /// <summary>
    /// Returns the absolute path to the nearest ancestor <c>.cws</c> file, walking up from
    /// <paramref name="startDir"/> (inclusive), or null if none is found — a loose file with no
    /// parent workspace (§2.1).
    /// </summary>
    public static string? FindAncestorCws(string? startDir)
    {
        if (string.IsNullOrEmpty(startDir)) return null;

        string? dir;
        try { dir = Path.GetFullPath(startDir); }
        catch { return null; }

        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".cws");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="absolutePath"/> does NOT live inside <paramref name="workspaceRootDir"/>
    /// — i.e. the document at that path is foreign to that workspace. Used for marking (§4) and for
    /// keeping workspace-scoped operations (Save All's <c>.cws</c> write, Remove/Rename Cell) from
    /// reaching documents that belong elsewhere.
    /// </summary>
    public static bool IsOutside(string absolutePath, string workspaceRootDir)
    {
        string rel;
        try { rel = Path.GetRelativePath(workspaceRootDir, absolutePath); }
        catch { return true; } // different drive/root on Windows — definitely outside

        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel);
    }
}
