using System.Text.Json;
using CircuitRF.Design.Cells;
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

/// <summary>
/// A redirect that FIRED: the record that matched, the root whose <c>.cmoves</c> holds it, and the
/// folder the reference finally resolved to (TM2 R-tm2-11/-12).
///
/// <para>It is carried out of resolution rather than swallowed by it because the cell resolving is
/// only half the answer: the drawing is right, and <b>the file on disk says something that is no
/// longer true</b>. Silently resolving and saying nothing would let a workspace drift arbitrarily far
/// from its stored references with the <c>.cmoves</c> chain as the only thing holding it together.</para>
/// </summary>
/// <param name="RootDir">The workspace or library root whose <c>.cmoves</c> was consulted.</param>
/// <param name="From">The root-relative path the reference still spells.</param>
/// <param name="To">The root-relative path it is at now.</param>
/// <param name="When">When the move was recorded, as the record spells it.</param>
/// <param name="ResolvedDir">The absolute folder the reference finally resolved to.</param>
public sealed record MoveRedirectHit(
    string RootDir, string From, string To, string When, string ResolvedDir)
{
    /// <summary>The root's own folder name — what a user calls the library or workspace the cell
    /// came from, which is the half of the report that makes it actionable.</summary>
    public string RootName =>
        Path.GetFileName(RootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>The date half of <see cref="When"/>, for a sentence a person reads. Falls back to the
    /// whole string when the record was hand-written in some other shape.</summary>
    public string WhenDate =>
        When.Length >= 10 && When[4] == '-' && When[7] == '-' ? When[..10] : When;
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
        string key = WorkspaceRootFinder.Normalize(rootDir);

        lock (_memoGate)
            if (_recordMemo.TryGetValue(key, out var memo)) return memo;

        var records = ReadUncached(rootDir);

        lock (_memoGate) _recordMemo[key] = records;
        return records;
    }

    private static IReadOnlyList<MoveRedirect> ReadUncached(string rootDir)
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

            WriteAtomic(rootDir,
                JsonSerializer.Serialize(
                    new MovesFile { FormatVersion = FormatVersion, Moves = moves }, WriteOpts));
            InvalidateCache();
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // ── R-tm2-15: can the safety net be laid at all? ──────────────────────────

    /// <summary>
    /// Whether a record could be written at this root RIGHT NOW — asked before the move, because a
    /// move whose forwarding record was lost is worse than a move that did not happen: the first
    /// breaks forty designs quietly, the second is a message the librarian reads immediately
    /// (R-tm2-15).
    ///
    /// <para><b>It is a real write, not an attribute read.</b> That is R-sl2-1's rule and it is the
    /// only answer that means the same thing on Windows, macOS and Linux — a share ACL, a POSIX mode
    /// and a read-only mount are invisible to <c>File.GetAttributes</c>. The probe file is uniquely
    /// named and removed; nothing is left behind, and in particular an empty <c>.cmoves</c> is NOT
    /// created for a move that is about to be refused for some other reason.</para>
    ///
    /// <para><b>This is the one place in the feature where refusing is correct</b>, and it is the
    /// opposite of R-tm2-1 on purpose: R-tm2-1 refuses to block the ORGANISING, this refuses to
    /// complete a move whose safety net could not be laid.</para>
    /// </summary>
    public static bool CanRecord(string rootDir, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rootDir)) { error = "no workspace root"; return false; }

        // An existing .cmoves that cannot be REWRITTEN is the failure this is really guarding: the
        // directory is writable, the file itself is not, and a probe that only created a new file
        // would say yes and then lose the record.
        string existing = PathFor(rootDir);
        if (File.Exists(existing))
        {
            try
            {
                using var fs = new FileStream(existing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        string probe = Path.Combine(rootDir, FileName + "." + Guid.NewGuid().ToString("N")[..8] + ".probe");
        try
        {
            File.WriteAllText(probe, "");
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { try { File.Delete(probe); } catch { /* a probe, not a step */ } }
    }

    /// <summary>
    /// Replace-in-one-step, so a `.cmoves` is never observed half-written. The temp file is created
    /// beside the target — a different volume would defeat the atomic replace, and the root is the one
    /// directory we already know we can write.
    /// </summary>
    private static void WriteAtomic(string rootDir, string json)
    {
        string target = PathFor(rootDir);
        string temp   = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch { /* best effort */ } } }
    }

    // ── R-tm2-8 … R-tm2-10: consulting the record ─────────────────────────────

    /// <summary>How far a chain of redirects is followed. <c>Amp → rf/Amp → rf/pa/Amp</c> must
    /// resolve in one call, and a <c>.cmoves</c> hand-edited into a cycle must produce nothing rather
    /// than hang — so the walk is capped as well as cycle-guarded. Eight is generous: it is eight
    /// successive reorganisations of the same cell.</summary>
    public const int MaxHops = 8;

    /// <summary>
    /// The folder <paramref name="missingCellDir"/> was moved to, or null when no record covers it.
    ///
    /// <para><b>Only ever asked when direct resolution found NOTHING</b> (R-tm2-8). That order is what
    /// makes the mechanism safe when a NEW cell is later created at the old path: the new cell wins,
    /// the redirect never fires, and the reference means what it says. A redirect consulted first
    /// would silently reroute a live reference to a different cell.</para>
    ///
    /// <para><b>Longest-prefix</b> (R-tm2-7): a record names the moved ROOT, so one record covers a
    /// whole moved subtree and a fifty-cell reorganisation writes one line rather than fifty. Among
    /// records with equally long <c>From</c>s the most recent is tried first, and a chain that
    /// dead-ends falls back to the next candidate — which is what makes an old reference survive a
    /// path that has since been reused and moved again.</para>
    /// </summary>
    public static MoveRedirectHit? Resolve(string? missingCellDir)
    {
        if (string.IsNullOrWhiteSpace(missingCellDir)) return null;

        string abs;
        try { abs = Path.GetFullPath(missingCellDir); }
        catch { return null; }

        if (RootAbove(abs) is not { } root) return null;
        if (ToRootRelative(root, abs) is not { } rel || rel.Length == 0) return null;

        var records = Read(root);
        if (records.Count == 0) return null;

        // A bounded depth-first walk. The visited set is the cycle guard (R-tm2-9) and is shared
        // across branches, so a hand-edited A→B→A terminates rather than spinning; the depth cap is
        // the second, independent stop.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rel };
        var stack   = new Stack<(string Rel, MoveRedirect Record, int Depth)>();

        foreach (var candidate in MatchesFor(records, rel)) PushCandidate(stack, rel, candidate, 1);

        while (stack.Count > 0)
        {
            var (nextRel, record, depth) = stack.Pop();
            if (!visited.Add(nextRel)) continue;

            string nextAbs;
            try { nextAbs = Path.GetFullPath(Path.Combine(root, nextRel.Replace('/', Path.DirectorySeparatorChar))); }
            catch { continue; }

            // Through CellStat like every other filesystem question resolution asks, so the cost of a
            // redirect is COUNTED rather than invisible. Only positives are cached there, so a
            // dead-end rung of a chain is re-asked next time rather than remembered as absent.
            if (CellStat.DirectoryExists(nextAbs, cache: true))
                return new MoveRedirectHit(root, rel, nextRel, record.When, nextAbs);

            if (depth >= MaxHops) continue;
            foreach (var candidate in MatchesFor(records, nextRel))
                PushCandidate(stack, nextRel, candidate, depth + 1);
        }

        return null;
    }

    private static void PushCandidate(
        Stack<(string, MoveRedirect, int)> stack, string rel, MoveRedirect record, int depth)
    {
        string remainder = rel[record.From.Length..];               // "" or "/sub/Cell"
        stack.Push((Norm(record.To) + remainder, record, depth));
    }

    /// <summary>
    /// Every record whose <c>From</c> is <paramref name="rel"/> or a folder ANCESTOR of it, ordered
    /// so the best candidate is tried last — a <see cref="Stack{T}"/> pops in reverse, so the longest
    /// <c>From</c> and, within that, the most recently recorded, comes off first.
    /// </summary>
    private static IEnumerable<MoveRedirect> MatchesFor(IReadOnlyList<MoveRedirect> records, string rel)
    {
        var hits = new List<(MoveRedirect Record, int Index)>();
        for (int i = 0; i < records.Count; i++)
        {
            string from = Norm(records[i].From);
            if (from.Length == 0 || Norm(records[i].To).Length == 0) continue;

            // A prefix match must land on a SEPARATOR. Without that, a record for "cells/Amp" would
            // capture "cells/AmpX" — the near-miss TM1's own gate exists to catch, arriving here by a
            // different door.
            bool covers = rel.Equals(from, StringComparison.OrdinalIgnoreCase)
                       || rel.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase);
            if (covers) hits.Add((records[i], i));
        }

        return hits
            .OrderBy(h => Norm(h.Record.From).Length)
            .ThenBy(h => h.Index)
            .Select(h => h.Record);
    }

    private static string Norm(string? p) => (p ?? "").Replace('\\', '/').Trim('/');

    // ── Which root owns the reference (R-tm2-8 step 3) ────────────────────────

    /// <summary>
    /// The workspace or library root whose <c>.cmoves</c> could carry a record for
    /// <paramref name="absolutePath"/> — the nearest ancestor directory holding one.
    ///
    /// <para><b>Not <c>WorkspaceRootFinder.WorkspaceDirOf</c>, and that is the point.</b> That helper
    /// walks up for a <c>.cws</c>, and <b>a referenced library need not be a workspace</b> —
    /// <c>WorkspaceScanner.ResolveLibrary</c> accepts a bare directory. A redirect that only worked
    /// for libraries that happen to be workspaces would work in testing and fail in the field, which
    /// is the same reason <c>.cmoves</c> is a file of its own rather than a <c>.cws</c> section.</para>
    ///
    /// <para>The walk STOPS at the first <c>.cws</c> it meets, inclusive: that directory is a
    /// workspace root, and a root above it owns a different tree and cannot have recorded a move of
    /// this cell. That is also what bounds the walk on a path with no workspace above it at all.</para>
    /// </summary>
    public static string? RootAbove(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        string key = WorkspaceRootFinder.Normalize(absolutePath);

        lock (_memoGate)
            if (_rootMemo.TryGetValue(key, out string? memo)) return memo;

        string? found = WalkForRoot(key);

        lock (_memoGate) _rootMemo[key] = found;
        return found;
    }

    private static string? WalkForRoot(string startPath)
    {
        string? dir;
        try { dir = Path.GetFullPath(startPath); }
        catch { return null; }

        for (int level = 0; dir is not null && level <= MaxWalkDepth; level++)
        {
            if (File.Exists(Path.Combine(dir, FileName))) return WorkspaceRootFinder.Normalize(dir);
            if (File.Exists(Path.Combine(dir, ".cws")))   return null;   // a root, and it has no record
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>A backstop on the walk-up. Reached only on a reference that resolves to nothing, and
    /// only when nothing above it is a workspace or a library — a deep path outside any project.</summary>
    private const int MaxWalkDepth = 64;

    // ── The memos (R-tm2-10) ──────────────────────────────────────────────────
    //
    // Two answers are memoised: which root owns a path, and what that root's .cmoves says. Both are
    // asked on the UNRESOLVED path only, but SL0 §2.4 measured four to five filesystem round trips
    // per component per edit and a .cmoves read per unresolved reference per BuildRenderModel would
    // be a fifth. They are dropped by WorkspaceRootFinder.InvalidateCache — reached from
    // ExternalCellRef.InvalidateCache, which SL2 R-sl2-3 already establishes as the one place the
    // per-root memos live and are dropped TOGETHER. A memo with a lifecycle of its own is the one
    // that goes stale.

    private static readonly Dictionary<string, string?> _rootMemo =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IReadOnlyList<MoveRedirect>> _recordMemo =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _memoGate = new();

    /// <summary>Forgets both memos. Called by <see cref="ExternalCellRef.InvalidateCache"/>, and by
    /// <see cref="Append"/> — a record just written must be visible to the very next resolve, not to
    /// the one after the next explicit invalidation.</summary>
    public static void InvalidateCache()
    {
        lock (_memoGate) { _rootMemo.Clear(); _recordMemo.Clear(); }
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
