using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Schematic;

/// <summary>What a move's reference repair did. Both halves are reported: a rewrite failure is
/// SURFACED, never rolled back (R-tm1-16).</summary>
/// <param name="RewrittenFiles">Files whose stored references changed.</param>
/// <param name="Failures">"path: message" per file that could not be read or written.</param>
public sealed record MoveRewriteResult(
    IReadOnlyList<string> RewrittenFiles,
    IReadOnlyList<string> Failures);

/// <summary>
/// Moving a cell, a folder or a loose file inside a workspace, and repairing every reference the
/// move invalidates (TM1).
///
/// <h3>A rename changes ONE set of references; a move changes TWO (R-tm1-1)</h3>
/// <para>A rename keeps the thing in its parent folder, so its DEPTH is unchanged and every
/// reference stored INSIDE it — <c>../../lib/Rload</c>, <c>../Other/x.wBond</c> — still resolves
/// afterwards with no edit at all. A move changes the depth, so both directions break:</para>
/// <list type="bullet">
///   <item><b>inbound</b> — every reference from elsewhere INTO the moved subtree, and</item>
///   <item><b>outbound</b> — every relative reference stored INSIDE the moved subtree that points
///     anywhere outside it.</item>
/// </list>
/// <para>An implementation that handles only the inbound half is a move that silently guts the cell
/// it just tidied away. That is why this is not an extension of
/// <c>CellUsageScanner.RewriteCellReferences</c>, which is a last-path-SEGMENT rewriter built for
/// Rename and cannot express either half.</para>
///
/// <h3>Both halves are ONE map (R-tm1-2)</h3>
/// <code>
///   Relocate(abs) = abs is inside oldRoot ? newRoot + abs.Substring(oldRoot.Length) : abs
/// </code>
/// <para>and the whole rewrite is: for every registered reference in every reachable file, resolve
/// it to an absolute path BEFORE the directory moves, and re-store it afterwards as
/// <c>Store(Relocate(target), Relocate(base))</c>. A referrer and a target that moved together
/// produce an unchanged string and no write; everything else falls out. There is deliberately no
/// second rewriter — a second one is where the two halves drift apart.</para>
///
/// <h3>Resolve before, write after (R-tm1-3)</h3>
/// <para>Path arithmetic needs no filesystem, but <c>ResolvePrimary</c>, the alias table and
/// <c>Directory.Exists</c> do, and the alias table is memoised. <see cref="Capture"/> runs while the
/// tree is still in its old shape; <see cref="Apply"/> runs after <c>Directory.Move</c>. Do not mix
/// this with <c>RewriteCellReferences</c>'s opposite convention (it runs AFTER the move precisely
/// because a stale reference still spells the old path) — the two are not interchangeable.</para>
///
/// <h3>What is never touched (R-tm1-6)</h3>
/// <para>A slot whose resolved target did not move AND whose base did not move is left byte for
/// byte alone — not re-derived and re-written to an equivalent spelling. That is what keeps
/// <c>board/R0402</c>'s referrers untouched when <c>parts/R0402</c> moves, and <c>cells/AmpX</c>'s
/// untouched when <c>cells/Amp</c> does. Matching is on RESOLVED ABSOLUTE PATHS — never last
/// segments, never a string-prefix match on the stored spelling.</para>
/// </summary>
public static class WorkspaceMove
{
    // ── The captured state ────────────────────────────────────────────────────

    internal sealed record CapturedSlot(int SiteIndex, int Ordinal, string AbsTarget, string BaseDir);

    internal sealed record CapturedFile(string Path, List<CapturedSlot> Slots);

    /// <summary>Every resolvable reference in every reachable file, taken while the tree is still in
    /// its pre-move shape. Opaque — build it with <see cref="Capture"/>, spend it with
    /// <see cref="Apply"/>.</summary>
    public sealed class MoveCapture
    {
        internal List<CapturedFile> Files { get; } = [];
        internal List<string>       Failures { get; } = [];

        /// <summary>Files that hold at least one resolvable reference. Reported, because "scanned N
        /// files" is the sentence that makes a zero-rewrite move legible rather than suspicious.</summary>
        public int ScannedFileCount => Files.Count;
    }

    // ── Capture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves every registered reference under <paramref name="scanRoots"/>, before the move.
    ///
    /// <para><paramref name="scanRoots"/> is the moving workspace plus every OTHER workspace open in
    /// this process — the same reach <c>CellUsageScanner</c> has, and the same limit: a referrer in a
    /// workspace nobody has open cannot be found. That remainder is what
    /// <see cref="CircuitRF.Design.Workspace.MoveRedirects"/> exists for.</para>
    /// </summary>
    public static MoveCapture Capture(IEnumerable<string> scanRoots)
    {
        var capture = new MoveCapture();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sites   = MoveRefRegistry.Sites;

        foreach (var root in scanRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var file in EnumerateFiles(root))
            {
                if (!seen.Add(file)) continue;

                JsonNode? node = null;
                var slots = new List<CapturedSlot>();

                for (int s = 0; s < sites.Count; s++)
                {
                    var site = sites[s];
                    if (!site.MatchesFile(file)) continue;

                    if (node is null)
                    {
                        try { node = JsonNode.Parse(GzipTextFile.ReadAllTextAutoGzip(file)); }
                        catch (Exception ex) { capture.Failures.Add($"{file}: {ex.Message}"); break; }
                        if (node is null) break;
                    }

                    if (site.BaseDirOf(file) is not { } baseDir) continue;

                    int ordinal = 0;
                    foreach (var slot in Enumerate(site, node))
                    {
                        int here = ordinal++;
                        if (site.Resolve(slot.Stored, baseDir) is not { } abs) continue;
                        slots.Add(new CapturedSlot(s, here, Normalize(abs), Normalize(baseDir)));
                    }
                }

                if (slots.Count > 0) capture.Files.Add(new CapturedFile(file, slots));
            }
        }

        return capture;
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-stores every captured reference against the post-move tree. Call AFTER
    /// <c>Directory.Move</c>: <paramref name="oldRoot"/> no longer exists and does not need to.
    /// </summary>
    /// <param name="oldRoot">Absolute path of the moved item BEFORE the move.</param>
    /// <param name="newRoot">Absolute path of the moved item AFTER the move.</param>
    public static MoveRewriteResult Apply(MoveCapture capture, string oldRoot, string newRoot)
    {
        ArgumentNullException.ThrowIfNull(capture);

        string from = Normalize(oldRoot);
        string to   = Normalize(newRoot);

        var rewritten = new List<string>();
        var failures  = new List<string>(capture.Failures);
        var sites     = MoveRefRegistry.Sites;

        foreach (var captured in capture.Files)
        {
            string path = Relocate(captured.Path, from, to);

            try
            {
                var node = JsonNode.Parse(GzipTextFile.ReadAllTextAutoGzip(path));
                if (node is null) continue;

                bool changed = false;

                foreach (var group in captured.Slots.GroupBy(sl => sl.SiteIndex))
                {
                    var site = sites[group.Key];
                    var live = Enumerate(site, node).ToList();

                    foreach (var cap in group)
                    {
                        if (cap.Ordinal >= live.Count) continue;   // the file changed under us
                        var slot = live[cap.Ordinal];

                        string newTarget = Relocate(cap.AbsTarget, from, to);
                        string newBase   = Relocate(cap.BaseDir,   from, to);

                        // R-tm1-6: neither end moved, so this reference is none of the move's
                        // business. Not re-derived, not normalised, not written.
                        if (ReferenceUnmoved(newTarget, cap.AbsTarget, newBase, cap.BaseDir)) continue;

                        string next = site.Store(newTarget, newBase, slot.Stored);
                        if (string.Equals(next, slot.Stored, StringComparison.Ordinal)) continue;

                        slot.Set(next);
                        changed = true;
                    }
                }

                if (!changed) continue;

                File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                rewritten.Add(path);
            }
            catch (Exception ex)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        return new MoveRewriteResult(rewritten, failures);
    }

    private static bool ReferenceUnmoved(string newTarget, string oldTarget, string newBase, string oldBase)
        => string.Equals(newTarget, oldTarget, StringComparison.Ordinal)
        && string.Equals(newBase,   oldBase,   StringComparison.Ordinal);

    // ── The map itself ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Relocate</c> — the whole of R-tm1-2. Defined on ABSOLUTE paths precisely so the
    /// absolute-storage cases (an SnP above two levels, a rooted model file, an absolute Known File)
    /// are not a separate branch: an absolute reference stays absolute and is still relocated.
    /// </summary>
    public static string Relocate(string absolutePath, string oldRoot, string newRoot)
    {
        string abs = Normalize(absolutePath);
        string from = Normalize(oldRoot);

        if (string.Equals(abs, from, StringComparison.OrdinalIgnoreCase)) return Normalize(newRoot);

        string prefix = from + Path.DirectorySeparatorChar;
        return abs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Normalize(newRoot), abs[prefix.Length..])
            : abs;
    }

    // ── Walking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every file under a workspace root that a registered site could match. Unlike
    /// <c>CellUsageScanner</c>'s walk this is NOT confined to cell folders: a <c>.cem</c>, a
    /// <c>.wBond</c> or a bookmarked <c>.s2p</c> lives wherever the user put it, and a reference in a
    /// file the walk never reached is exactly the dangling reference R-tm1-4 is about.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(root));

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var d in subs)
            {
                // A referenced workspace or library nested inside this one is someone else's disk and
                // is reached, if at all, as its own scan root — never by falling into it from here.
                if (File.Exists(Path.Combine(d, ".cws"))) continue;
                stack.Push(d);
            }
        }
    }

    private static IEnumerable<RefSlot> Enumerate(MoveRefSite site, JsonNode node)
    {
        // A malformed or unexpected shape yields nothing rather than throwing: one bad document must
        // not stop the repair of every other file in the workspace.
        List<RefSlot> slots = [];
        try { slots = site.Locate(node).ToList(); }
        catch { return []; }
        return slots;
    }

    internal static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return path; }
    }
}
