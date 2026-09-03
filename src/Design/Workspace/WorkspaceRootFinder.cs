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

    // ── The workspace a path belongs to, memoised (MW1 R-mw1-5 / R-mw1-6) ─────
    //
    // Multi-window turns this walk-up into the answer to "which workspace is this call being made on
    // behalf of" — the question every process-global registry now has to ask, per instance, per
    // render. FindAncestorCws touches the filesystem once per directory level, so asking it per cell
    // instance per frame is not affordable; these two memoise it.

    private static readonly Dictionary<string, string?> _rootMemo =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _memoGate = new();

    /// <summary>
    /// The workspace ROOT DIRECTORY <paramref name="startDir"/> belongs to — the directory holding
    /// the nearest ancestor <c>.cws</c> — normalised, or null for a loose path with no parent
    /// workspace. Memoised by <paramref name="startDir"/>; see <see cref="InvalidateCache"/>.
    /// </summary>
    public static string? WorkspaceDirOf(string? startDir)
    {
        if (string.IsNullOrEmpty(startDir)) return null;

        lock (_memoGate)
            if (_rootMemo.TryGetValue(startDir, out string? memo)) return memo;

        string? cws  = FindAncestorCws(startDir);
        string? root = cws is null ? null : Normalize(Path.GetDirectoryName(cws));

        lock (_memoGate) _rootMemo[startDir] = root;
        return root;
    }

    /// <summary>
    /// Forgets the memoised walk-ups. Called wherever the resolvers that depend on them are already
    /// invalidated — a workspace open or close, an external edit — because a <c>.cws</c> appearing
    /// or disappearing changes which workspace a path belongs to and nothing else would notice.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_memoGate) _rootMemo.Clear();
    }

    /// <summary>
    /// The canonical spelling of a workspace root, used as the KEY every workspace-scoped registry
    /// partitions by. Absolute, with no trailing separator; comparison is ordinal-ignore-case, the
    /// same rule <c>TechnologyCache</c> already uses and for the same reason. Null and blank both
    /// normalise to <see cref="None"/> rather than to null, so a scope key is never itself null and
    /// "no workspace" is one value rather than two.
    /// </summary>
    public static string Normalize(string? workspaceRootDir)
    {
        if (string.IsNullOrWhiteSpace(workspaceRootDir)) return None;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRootDir));
        }
        catch
        {
            return workspaceRootDir.Trim();
        }
    }

    /// <summary>
    /// The scope key for "no workspace" — a scratch document, a test fixture, a loose file opened
    /// from outside any workspace. A real scope of its own rather than a null: entries put there
    /// belong to nothing and are never swept by a workspace closing.
    /// </summary>
    public const string None = "";
}
