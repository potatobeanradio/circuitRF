using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Archive;

namespace CircuitRF.Ui.Schematic;

/// <summary>What a workspace copy did. A reference-repair failure is REPORTED, never rolled back —
/// the same contract as <see cref="MoveRewriteResult"/>, and for the same reason: the files are on
/// disk either way, and silently discarding a copy because one referrer would not parse is worse
/// than handing it over with the problem named.</summary>
/// <param name="FileCount">Files written into the destination.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="RewrittenFiles">Files in the COPY whose stored references were re-spelled.</param>
/// <param name="Failures">"path: message" per file that could not be copied, read or written.</param>
public sealed record WorkspaceCopyResult(
    int FileCount,
    long Bytes,
    IReadOnlyList<string> RewrittenFiles,
    IReadOnlyList<string> Failures);

/// <summary>
/// <c>File ▸ Save Workspace As…</c> — the whole workspace FOLDER copied somewhere else, with every
/// reference the copy invalidates repaired.
///
/// <h3>Why this is not a file save (owner, 2026-09-05)</h3>
/// <para>A workspace is a directory whose manifest is a dotfile named literally <c>.cws</c> — no
/// stem. The command used to be an ordinary <c>SaveFilePicker</c> that wrote that manifest, and only
/// it, to a path of the user's choosing. Every consequence of that was wrong: the file landed as
/// <c>untitled.cws</c>, a name <c>Open Workspace…</c> cannot find (it looks for
/// <c>&lt;folder&gt;/.cws</c>, as do the scanner, the reference registry and the archive scanner);
/// no cell, technology or document was copied; assigning the picked path to
/// <c>CurrentWorkspacePath</c> silently re-rooted the live window at a folder with nothing in it;
/// and <c>WriteWorkspaceFile</c> then found every open document OUTSIDE that new root and dropped
/// the whole open-document list, so the file it wrote was empty of everything but a dock layout.
/// The advisory lock was left on the workspace being left, too — the switch path moves it, that path
/// never did.</para>
///
/// <h3>The copy (R-swa-1)</h3>
/// <para>Everything under the workspace root that <see cref="WorkspaceArchiveScanner.IsSkipped"/>
/// does not exclude. That filter is shared with Archive deliberately rather than restated: it names
/// the rebuildable pCell cache, the OS clutter, the in-flight atomic-write temporaries, and — the one
/// that matters here — circuitRF's own <c>.crf-</c> session bookkeeping. Copying the advisory lock
/// into the destination would hand the copy a held lock naming a session that has nothing to do with
/// it.</para>
///
/// <h3>The repair (R-swa-2)</h3>
/// <para><see cref="WorkspaceMove"/> already expresses exactly this: capture every resolvable
/// reference against the old tree, then re-store it as <c>Store(Relocate(target), Relocate(base))</c>.
/// A copy is that same map with one difference — <b>only the copied workspace is captured</b>, never
/// the other workspaces open in this process. A move makes the old location vanish, so a referrer
/// elsewhere has to follow it; a copy leaves the original exactly where it was, so a referrer
/// elsewhere must be left pointing at it. Handing <c>Capture</c> the other roots is the one mistake
/// that turns Save-a-Copy into a move.</para>
/// <para>Inside the copy the map does the rest for free: a reference whose target and base both moved
/// re-derives to the identical spelling and is not written at all (<c>WorkspaceMove</c>'s R-tm1-6),
/// while a reference OUT of the workspace — an external cell, a referenced workspace's <c>.cws</c>, a
/// bookmarked Touchstone two folders up — has a moved base and an unmoved target, so it is re-spelled
/// to still resolve to the very same file.</para>
/// </summary>
public static class WorkspaceCopy
{
    /// <summary>The workspace manifest's file name — a dotfile with no stem, which is the whole
    /// reason this command copies a FOLDER.</summary>
    public const string CwsFileName = ".cws";

    // ── Refusals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The reason this copy must not be attempted, or null when it may be. Separate from
    /// <see cref="Run"/> so the dialog can ask the same question while the user is still typing, and
    /// so the caller can re-ask at commit time — the checks touch the filesystem, and the answer can
    /// change between the two.
    /// </summary>
    public static string? Refusal(string? sourceRoot, string? destRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return "There is no workspace folder to copy.";

        if (!File.Exists(Path.Combine(sourceRoot, CwsFileName)))
            return $"'{sourceRoot}' is not a circuitRF workspace (no {CwsFileName} found).";

        if (string.IsNullOrWhiteSpace(destRoot))
            return "Choose where to save the copy.";

        if (Directory.Exists(destRoot) || File.Exists(destRoot))
            return $"'{destRoot}' already exists. Choose a name that is not in use.";

        // Copying a workspace into itself walks its own output. The destination does not exist yet,
        // so this is a question about the PATH, not about what is on disk — and the New Workspace
        // dialog's own "not inside another workspace" check does not cover it: a subfolder of the
        // workspace has no `.cws` of its own.
        if (WorkspaceArchiveScanner.IsInside(destRoot, sourceRoot))
            return "A workspace cannot be copied into itself. Choose a location outside "
                 + $"'{Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar))}'.";

        return null;
    }

    // ── The copy ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies the workspace rooted at <paramref name="sourceRoot"/> to <paramref name="destRoot"/>
    /// and repairs the copy's references. Blocking and framework-free — call it off the UI thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The copy was refused; see <see cref="Refusal"/>.</exception>
    public static WorkspaceCopyResult Run(string sourceRoot, string destRoot)
    {
        if (Refusal(sourceRoot, destRoot) is { } refusal)
            throw new InvalidOperationException(refusal);

        string from = Path.GetFullPath(sourceRoot);
        string to   = Path.GetFullPath(destRoot);

        // Captured BEFORE anything is written, against the tree as it stands. Only this workspace:
        // see R-swa-2 in the type comment — the other open workspaces are referrers a COPY must
        // leave alone.
        var capture = WorkspaceMove.Capture([from]);

        var failures = new List<string>();
        int files    = 0;
        long bytes   = 0;

        Directory.CreateDirectory(to);

        foreach (string file in WorkspaceArchiveScanner.EnumerateFilesSafe(from))
        {
            string rel = WorkspaceArchiveScanner.Rel(from, file);
            if (WorkspaceArchiveScanner.IsSkipped(rel)) continue;

            string target = Path.Combine(to, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                files++;
                bytes += Math.Max(0, WorkspaceArchiveScanner.SizeOf(target));
            }
            catch (Exception ex)
            {
                failures.Add($"{rel}: {ex.Message}");
            }
        }

        // An empty folder carries meaning here — the user made it, and a copy that quietly drops it
        // is a copy that changed the project tree. EnumerateFilesSafe only yields files.
        foreach (string dir in EnumerateDirectoriesSafe(from))
        {
            string rel = WorkspaceArchiveScanner.Rel(from, dir);
            if (WorkspaceArchiveScanner.IsSkipped(rel)) continue;
            try { Directory.CreateDirectory(Path.Combine(to, rel.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) { failures.Add($"{rel}: {ex.Message}"); }
        }

        // The destination is new to this process, and Apply is about to ask questions about it:
        // StoreCellRef routes through ExternalCellRef, whose alias table and workspace walk-up are
        // both memoised per directory. The move path invalidates at exactly this point and for
        // exactly this reason — a .cws appearing changes which workspace a path belongs to, and
        // nothing else notices.
        WorkspaceRootFinder.InvalidateCache();

        var rewrite = WorkspaceMove.Apply(capture, from, to);
        failures.AddRange(rewrite.Failures);

        return new WorkspaceCopyResult(files, bytes, rewrite.RewrittenFiles, failures);
    }

    /// <summary>Every directory under <paramref name="root"/>, pruning what the copy skips, and
    /// surviving one unreadable branch — the directory twin of
    /// <c>WorkspaceArchiveScanner.EnumerateFilesSafe</c>.</summary>
    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string current = stack.Pop();

            string[] subs;
            try { subs = Directory.GetDirectories(current); }
            catch { continue; }

            foreach (string s in subs)
            {
                if (WorkspaceArchiveScanner.IsSkipped(WorkspaceArchiveScanner.Rel(root, s))) continue;
                yield return s;
                stack.Push(s);
            }
        }
    }
}
