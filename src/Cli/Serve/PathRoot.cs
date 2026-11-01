using CircuitRF.Diagnostics;

namespace CircuitRF.Cli.Serve;

/// <summary>
/// The server's filesystem boundary, in ONE place (R-aut5-8).
///
/// <para><b>Why a root is required rather than defaulted.</b> The server runs with the invoking
/// user's authority and touches their filesystem on behalf of something that is not a person. A
/// default of "the current directory" would be a policy nobody stated, and would move with wherever
/// the process happened to be started from.</para>
///
/// <para><b>A path that leaves the root is a REFUSAL, never a silent clamp.</b> Clamping runs a
/// different operation than the one asked for and says nothing about it — a client asking to write
/// <c>../../etc/x</c> and being handed <c>&lt;root&gt;/etc/x</c> has been lied to. The refusal names
/// the root, because that is the fact the caller is missing.</para>
///
/// <para><b>Symlinks are resolved before the test, on both sides.</b> A link inside the root pointing
/// out of it is the one escape a string comparison cannot see, and it is not exotic: on macOS
/// <c>/tmp</c> is itself a link to <c>/private/tmp</c>, so resolving only one side would refuse
/// perfectly legitimate paths. For a path that does not exist yet — an output file — the nearest
/// existing ancestor is resolved and the rest re-appended, which is the only thing that can be done
/// and is what the check has to mean anyway: a file is created inside the directory it lands in.
/// </para>
/// </summary>
internal sealed class PathRoot
{
    /// <summary>The root, fully resolved. Every refusal names this.</summary>
    public string Root { get; }

    private readonly string _realRoot;

    private PathRoot(string root, string realRoot) { Root = root; _realRoot = realRoot; }

    /// <summary>Resolves and validates the <c>--root</c> itself.</summary>
    public static PathRoot? Open(string given, out Diagnostic? refusal)
    {
        string full = Path.GetFullPath(given);
        if (!Directory.Exists(full))
        {
            refusal = CliDiagnostics.ServeRootNotFound(given);
            return null;
        }
        refusal = null;
        return new PathRoot(full, Real(full));
    }

    /// <summary>
    /// The absolute path a client's argument names, or null with a refusal.
    ///
    /// <para>A RELATIVE path is relative to the root, not to the server's working directory — the
    /// client cannot see that directory and has no way to reason about it.</para>
    /// </summary>
    public string? Resolve(string given, out Diagnostic? refusal)
    {
        string full = Path.GetFullPath(
            Path.IsPathRooted(given) ? given : Path.Combine(Root, given));

        if (!IsUnder(Real(full), _realRoot))
        {
            refusal = CliDiagnostics.ServePathOutsideRoot(given, Root, Scratch());
            return null;
        }
        refusal = null;
        return full;
    }

    /// <summary>EMB-5 R-emb5-5 — the folder under the root the server keeps for a caller's own files. Hidden, so it is
    /// not mistaken for a workspace's content; named by every root refusal.</summary>
    public const string ScratchFolderName = ".circuitrf-scratch";

    /// <summary>
    /// The scratch folder, created on demand — the moment a refusal is about to name it, so the folder it names exists.
    /// Creating it is the one write this class makes, and only inside the root. A root that cannot take it (read-only)
    /// still gets the sentence: the path is then the answer to "where would I write", not a promise.
    /// </summary>
    public string Scratch()
    {
        string dir = Path.Combine(Root, ScratchFolderName);
        try { Directory.CreateDirectory(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* named all the same */ }
        return dir;
    }

    private bool IsUnder(string candidate, string root)
    {
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(candidate, root, cmp)) return true;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, cmp);
    }

    /// <summary>
    /// The path with every symbolic link on it followed — at EVERY level, not just the last one.
    ///
    /// <para>That distinction is the whole of this method. <c>ResolveLinkTarget</c> answers about
    /// the item it is called on, so asking it about <c>&lt;root&gt;/link/file.cnl</c> reports "not a
    /// link" — the FILE is not one, the DIRECTORY above it is — and the escape goes straight through.
    /// So the path is walked from its root downward and each component resolved in turn.</para>
    ///
    /// <para>A component that does not exist is simply appended: an output file is created inside the
    /// directory it lands in, which is the directory this has already resolved.</para>
    /// </summary>
    private static string Real(string path)
    {
        string full = Path.GetFullPath(path);
        string cur  = Path.GetPathRoot(full) ?? "";
        if (cur.Length == 0) return full;

        foreach (string segment in full[cur.Length..]
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            cur = Path.Combine(cur, segment);

            FileSystemInfo? info =
                Directory.Exists(cur) ? new DirectoryInfo(cur) :
                File.Exists(cur)      ? new FileInfo(cur)      : null;
            if (info is null) continue;

            try
            {
                // A cycle throws. Leaving the component unresolved is the safe direction: an
                // unresolved path is the one that fails the containment test, so a link nobody can
                // follow is refused rather than admitted.
                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target) cur = target.FullName;
            }
            catch (IOException) { /* left unresolved, deliberately */ }
        }

        return Path.GetFullPath(cur);
    }
}
