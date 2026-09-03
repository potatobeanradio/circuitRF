// ================================================================
//  WorkspaceRefs.cs  —  how a referenced file is stored in .cws
//
//  brief-stability-passivity-touchstone.md §5.1. This CORRECTS brief-data-display-multifile-ui.md's
//  R-dd-6, which required "a bare filename resolved against results/ — never a rooted path, and
//  never one containing a directory separator." That rule was written before the owner clarified
//  that a Known File is a REFERENCE and may point outside the workspace, and it fails two ways: it
//  cannot express a file elsewhere INSIDE the workspace, and it cannot express one outside it at all.
// ================================================================

using System;
using System.IO;

namespace CircuitRF.Ui.Schematic;

public static class WorkspaceRefs
{
    /// <summary>
    /// R-stb-10/11 — the stored form of a referenced file.
    ///
    /// <para><b>Inside the workspace → a workspace-relative path</b>, so moving or sharing the
    /// workspace still resolves. <b>Outside → the absolute path</b>, because no encoding can make
    /// an outside reference portable; storing it plainly and telling the user (R-stb-12's
    /// "external" marking) is the honest option.</para>
    ///
    /// <para><b>Separators in relative refs are normalized to `/`.</b> R-dd-6's no-separator rule
    /// existed to dodge the macOS-versus-Windows separator problem; now that relative refs may
    /// contain directories, normalizing is how that problem stays dodged. This is the git/URI
    /// convention, and it is the part most likely to be skipped because it only fails when a
    /// workspace crosses platforms — i.e. never on the machine that wrote it.</para>
    /// </summary>
    public static string ToStoredRef(string absolutePath, string? workspaceRootDir)
    {
        string abs = Path.GetFullPath(absolutePath);
        if (string.IsNullOrEmpty(workspaceRootDir)) return abs;

        if (!IsInside(abs, workspaceRootDir!)) return abs;

        string rel = Path.GetRelativePath(Path.GetFullPath(workspaceRootDir!), abs);
        return rel.Replace('\\', '/');
    }

    /// <summary>
    /// Resolves a stored ref back to an absolute path. Mirrors WorkspaceScanner.ResolveRef (rooted
    /// → as-is, relative → under the workspace root) and additionally converts `/` back to the
    /// platform separator, which is the load-bearing half on Windows.
    /// </summary>
    public static string Resolve(string storedRef, string workspaceRootDir)
    {
        if (Path.IsPathRooted(storedRef)) return storedRef;
        string native = storedRef.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRootDir, native));
    }

    /// <summary>
    /// SL1 R-sl1-6 — <see cref="Resolve"/> plus <c>${NAME}</c> expansion, for the stored fields that
    /// name a location OUTSIDE the workspace and may therefore carry a token: a Known File and a
    /// library ref. Returns <b>null</b> when a token is unset (R-sl1-7), which is a broken reference
    /// and not an empty expansion — a caller listing files drops it, and one rendering a node says
    /// which variable to set.
    ///
    /// <para><see cref="Resolve"/> itself is deliberately left alone: it is also the resolver for
    /// <c>PdkRefs</c>, and R-sl1-6 bounds expansion to three fields rather than letting a second
    /// place a cross-workspace path can hide arrive by a different door.</para>
    /// </summary>
    public static string? ResolveExternalRef(string storedRef, string workspaceRootDir)
        => CircuitRF.Design.Workspace.PathTokens.ExpandOrNull(storedRef) is { } expanded
            ? Resolve(expanded, workspaceRootDir)
            : null;

    /// <summary>
    /// True when the reference points outside the workspace and therefore will NOT travel with it
    /// (R-stb-12). Drives the Datasets list's "external" status so a user about to share a
    /// workspace can see which sources will break on someone else's machine — without that, the
    /// failure surfaces there as a missing file with no explanation.
    /// </summary>
    public static bool IsExternal(string storedRefOrAbsPath, string? workspaceRootDir)
    {
        if (string.IsNullOrEmpty(workspaceRootDir)) return false;
        // A relative stored ref is by construction inside; only a rooted one can be outside.
        if (!Path.IsPathRooted(storedRefOrAbsPath)) return false;
        return !IsInside(Path.GetFullPath(storedRefOrAbsPath), workspaceRootDir!);
    }

    private static bool IsInside(string absPath, string workspaceRootDir)
    {
        string root = Path.GetFullPath(workspaceRootDir)
                          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rel;
        try { rel = Path.GetRelativePath(root, absPath); }
        catch { return false; }

        // GetRelativePath returns the input unchanged when the two share no root (different drive
        // on Windows), and an ".."-leading path when the target sits above the workspace.
        if (Path.IsPathRooted(rel)) return false;
        return rel != ".."
            && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !rel.StartsWith("../", StringComparison.Ordinal);
    }
}
