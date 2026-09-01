using System;
using System.IO;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The two halves of one contract: how a picked SnP/Touchstone path is STORED in the <c>File</c>
/// parameter, and how a stored one is READ back.
///
/// <para><b>The base is the WORKSPACE ROOT, and it is the same base on both sides and at Run.</b>
/// <see cref="ToStored"/> prefers a workspace-root-relative path (forward-slash, cross-platform)
/// when the file is inside the workspace subtree or at most <see cref="MaxParentLevels"/>
/// directories above it, so a design is portable; <c>Elaborator.ResolveSnpFilePath</c> resolves that
/// string against the root <c>WorkspaceViewModel</c> hands the run
/// (<c>CurrentWorkspaceRoot</c>), and <see cref="Resolve"/> is the editor's copy of that same rule.</para>
///
/// <para><b><see cref="Resolve"/> exists because the editor kept getting the base wrong.</b>
/// <c>SetSnpFileCommand</c> resolved a stored path against the SCHEMATIC's own directory when it
/// sniffed the port count off disk. The two bases agree only when the schematic sits at the
/// workspace root — the usual layout, and why nothing reported it — and for a schematic in a
/// sub-folder the sniff silently missed a perfectly readable file and left <c>NumPorts</c> at its
/// previous value, so the symbol drew the wrong pin count and the netlist bound the wrong number of
/// nets. <c>EmBackAnnotation</c> already documented this as the reason it does not reuse that
/// command. There is now one function, so a caller cannot pick the wrong base by writing a
/// <c>Path.Combine</c> of its own.</para>
///
/// <para><b>Not a duplicate of <c>WorkspaceRefs.Resolve</c>, and please do not add a third.</b>
/// That one is the general workspace-reference resolver — datasets, layouts, bitmaps — and states
/// the same rooted/relative rule. This one adds the two things the SnP editor needs and it does
/// not: the no-workspace fallback, and null rather than a still-relative string when a value cannot
/// be resolved, so a caller reports instead of silently probing the process's current directory. If
/// they ever need to merge, merge them; do not write a fourth.</para>
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

    /// <summary>
    /// The absolute path a stored <c>File</c> value names, or null when it cannot be made absolute.
    ///
    /// <para><b>Deliberately mirrors <c>Elaborator.ResolveSnpFilePath</c></b>, including its
    /// tolerance of a Windows-authored <c>'\'</c> in a relative path — a design authored on one
    /// operating system has to open on another, and an editor that reported a missing file for one
    /// the engine reads perfectly well would be a worse bug than the one this replaces.</para>
    ///
    /// <para><paramref name="schematicDirectory"/> is the fallback for the NO-WORKSPACE case only,
    /// never a second rule competing with the root. <see cref="ToStored"/> never writes a relative
    /// path without a root (it keeps the absolute one), so a relative value can only get there by
    /// hand — and beside the file being edited is the only thing a hand-typed one can reasonably
    /// mean. With neither base the answer is null: the process's current directory is wherever
    /// circuitRF happened to be launched from, which is not a place any design meant.</para>
    /// </summary>
    public static string? Resolve(string? stored, string? workspaceRoot, string? schematicDirectory)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        string raw = stored.Trim();

        try
        {
            if (Path.IsPathRooted(raw)) return Path.GetFullPath(raw);

            string rel  = raw.Replace('\\', '/');
            string? bas = !string.IsNullOrEmpty(workspaceRoot) ? workspaceRoot : schematicDirectory;
            return string.IsNullOrEmpty(bas) ? null : Path.GetFullPath(Path.Combine(bas, rel));
        }
        catch
        {
            // A path the OS will not even parse (an illegal character, a length limit). Unresolvable
            // is the honest answer; the caller reports it against the string the user typed.
            return null;
        }
    }
}
