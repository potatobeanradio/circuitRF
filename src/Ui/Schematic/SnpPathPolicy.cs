using System;
using System.IO;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Decides how to store a picked SnP/Touchstone file path in the <c>File</c> parameter, given the
/// workspace root. Prefers a workspace-root-relative path (forward-slash, cross-platform) when the
/// file is inside the workspace subtree or at most <see cref="MaxParentLevels"/> directories above it;
/// otherwise the absolute path. Mirrors the engine's resolution base (the workspace root).
/// </summary>
public static class SnpPathPolicy
{
    /// <summary>Max number of parent levels above the workspace root for which a relative path is kept.</summary>
    public const int MaxParentLevels = 2;

    /// <summary>
    /// Returns the string to store in the <c>File</c> parameter for a picked absolute path.
    /// Relative (forward-slash) when within the workspace subtree or ≤ MaxParentLevels above the root;
    /// absolute otherwise. When <paramref name="workspaceRoot"/> is null/empty, or the inputs are not
    /// rootable to a common base, the absolute path is returned unchanged.
    /// </summary>
    public static string ToStored(string absolutePath, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(absolutePath) || !Path.IsPathRooted(absolutePath))
            return absolutePath;
        if (string.IsNullOrEmpty(workspaceRoot))
            return absolutePath;

        string rel;
        try { rel = Path.GetRelativePath(workspaceRoot, absolutePath); }
        catch { return absolutePath; }

        // Different volume/root → GetRelativePath returns an absolute path → keep absolute.
        if (Path.IsPathRooted(rel)) return absolutePath;
        if (rel == ".") return absolutePath;   // the root dir itself (not a file) — defensive

        // Count leading ".." segments = how far above the workspace root the file sits.
        var segs = rel.Split('/', '\\');
        int up = 0;
        while (up < segs.Length && segs[up] == "..") up++;
        if (up > MaxParentLevels) return absolutePath;

        return rel.Replace('\\', '/');   // portable separators
    }
}
