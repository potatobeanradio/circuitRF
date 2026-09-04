using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Workspace;

/// <summary>One recorded move: where something used to be, where it is now, and when it happened.
/// Paths are ROOT-RELATIVE and forward-slash, the convention
/// <see cref="CircuitRF.Ui"/>'s <c>WorkspaceRefs.ToStoredRef</c> normalises to.</summary>
public sealed class MoveRedirect
{
    public string From { get; set; } = "";
    public string To   { get; set; } = "";

    /// <summary>UTC, round-trip format. A record three years old is exactly the one that matters,
    /// so the timestamp is what lets a reader say WHEN the reference it is repairing went stale.</summary>
    public string When { get; set; } = "";
}

/// <summary>The file itself. A flat, append-only list — see <see cref="MoveRedirects"/>.</summary>
public sealed class MovesFile
{
    public int FormatVersion { get; set; } = MoveRedirects.FormatVersion;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MoveRedirect>? Moves { get; set; }
}

/// <summary>
/// <c>.cmoves</c> — the forwarding record a move leaves behind at the root of the workspace or
/// library that OWNS the thing that moved (TM2 §3; written by TM1 §7).
///
/// <para><b>Why a record rather than a rewrite.</b> <c>CellUsageScanner.RewriteCellReferences</c>
/// repairs every referrer it can reach — this workspace, plus every other workspace open in this
/// process. A referrer in a workspace nobody has open cannot be found, cannot be written to, and in
/// general is not even visible from here. Within one workspace that limit is invisible; it stops
/// being invisible the moment another project references this one, because the reference it stored
/// is THIS workspace's own relative spelling and the move just invalidated it, in a file on someone
/// else's disk.</para>
///
/// <para><b>A separate file, not a <c>.cws</c> section</b>, for one decisive reason: a referenced
/// LIBRARY need not be a workspace and often has no <c>.cws</c> at all
/// (<c>WorkspaceScanner.ResolveLibrary</c> accepts a bare directory). A redirect that only worked
/// for libraries that happen to be workspaces would work in testing and fail in the field.</para>
///
/// <para><b>Written unconditionally</b>, including for a move inside a workspace nobody shares — a
/// workspace that is private today is referenced next month, and a redirect that was never written
/// cannot be reconstructed. <b>Append-only and never pruned</b>: the design that most needs the
/// record is the one authored three years ago.</para>
///
/// <para>It must be listed in <c>WorkspaceScanner.IsHiddenTreeFile</c>. That predicate is an explicit
/// opt-in set and NOT a dotfile rule — its own doc comment says so, and <c>.cws-lock</c> had to be
/// added to it by name for exactly this reason. Left out, <c>.cmoves</c> renders as a row in every
/// workspace and every library sub-tree, and travels into every archive.</para>
/// </summary>
public static class MoveRedirects
{
    /// <summary>The file's whole name at the root — no stem, like <c>.cws</c>.</summary>
    public const string FileName = ".cmoves";

    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static string PathFor(string rootDir) => Path.Combine(rootDir, FileName);

    /// <summary>
    /// Every record at this root, oldest first. An unreadable or foreign file reads as EMPTY rather
    /// than throwing: a redirect log is a repair aid, and one that stops a workspace opening would be
    /// worse than the broken reference it exists to explain.
    /// </summary>
    public static IReadOnlyList<MoveRedirect> Read(string rootDir)
    {
        string path = PathFor(rootDir);
        if (!File.Exists(path)) return [];

        try
        {
            var file = JsonSerializer.Deserialize<MovesFile>(File.ReadAllText(path));
            if (file is null || file.FormatVersion > FormatVersion) return [];
            return file.Moves ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Appends one record. Returns false with a sentence in <paramref name="error"/> when the write
    /// failed — which the caller REPORTS rather than treating as fatal: the move itself has already
    /// happened by then, and a move that succeeded is not undone by a log that did not.
    /// </summary>
    /// <param name="fromRootRelative">Where it was, relative to <paramref name="rootDir"/>.</param>
    /// <param name="toRootRelative">Where it is now, relative to <paramref name="rootDir"/>.</param>
    public static bool Append(
        string rootDir, string fromRootRelative, string toRootRelative, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(fromRootRelative) || string.IsNullOrWhiteSpace(toRootRelative))
            return true;   // nothing moved anywhere nameable — not a failure

        // A move that changes nothing is not a record. It cannot arise from a real drop (the
        // destination-already-holds-this-name refusal catches it first), but the menu path and a
        // future caller can both reach it.
        if (string.Equals(fromRootRelative, toRootRelative, StringComparison.Ordinal)) return true;

        try
        {
            var moves = Read(rootDir).ToList();
            moves.Add(new MoveRedirect
            {
                From = fromRootRelative,
                To   = toRootRelative,
                When = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                                                System.Globalization.CultureInfo.InvariantCulture),
            });

            File.WriteAllText(
                PathFor(rootDir),
                JsonSerializer.Serialize(
                    new MovesFile { FormatVersion = FormatVersion, Moves = moves }, WriteOpts));
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>The root-relative, forward-slash spelling of an absolute path — the form a record
    /// stores. Null when the path is not under the root, which is not a record this root can make.</summary>
    public static string? ToRootRelative(string rootDir, string absolutePath)
    {
        try
        {
            string rel = Path.GetRelativePath(Path.GetFullPath(rootDir), Path.GetFullPath(absolutePath));
            if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal)) return null;
            return rel.Replace('\\', '/');
        }
        catch { return null; }
    }
}
