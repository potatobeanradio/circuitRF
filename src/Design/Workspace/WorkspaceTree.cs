using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Workspace;

/// <summary>What <see cref="WorkspaceTree.CopyFiles"/> did.</summary>
/// <param name="FileCount">Files written into the destination.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="Failures">"path: message" per file or folder that could not be copied.</param>
public sealed record WorkspaceTreeCopyResult(int FileCount, long Bytes, IReadOnlyList<string> Failures);

/// <summary>
/// A workspace FOLDER as a tree of files: which of them a copy or an archive carries, how the tree is
/// walked, and the file-by-file copy itself.
///
/// <para><b>Why it is here and not in <c>src/Ui</c>.</b> These rules were written for Save Workspace
/// As and the archive, both GUI commands, and lived beside them in <c>WorkspaceArchiveScanner</c> and
/// <c>WorkspaceCopy</c>. <c>circuitrf new example</c> copies a shipped example into the caller's tree
/// and cannot reference <c>src/Ui</c>, and a second skip list in <c>src/Cli</c> would be a second
/// opinion about what a copy carries — the one that disagreed would hand an agent a workspace holding
/// another session's advisory lock. So the rules moved, and both GUI classes forward to them.</para>
///
/// <para><b>What did NOT move is the reference repair</b> (<c>WorkspaceMove</c>), which needs the
/// GUI's reference registry. <c>WorkspaceCopy.Run</c> is <see cref="CopyFiles"/> followed by that
/// repair. A shipped example references nothing outside itself, so for an example the repair re-derives
/// every reference to its existing spelling and writes nothing — which
/// <c>ExamplesCliTests</c> holds by comparing the CLI's copy to the GUI's byte for byte.</para>
/// </summary>
public static class WorkspaceTree
{
    // ── Skip rules ────────────────────────────────────────────────────────────

    /// <summary>
    /// Directories neither an archive nor a copy ever carries: the workspace's history, and the
    /// clutter a file manager leaves behind.
    /// </summary>
    private static readonly string[] SkippedDirectories =
    [
        RepositoryFolderName,
        "__MACOSX", ".Spotlight-V100", ".Trashes", ".fseventsd", ".TemporaryItems",
    ];

    /// <summary>
    /// Directories no generic walk descends into — <see cref="SkippedDirectories"/> plus
    /// <c>.generated-cells</c>. A copy leaves the generated cells out (<see cref="IsSkipped"/>), and
    /// an archive offers them as one row of their own built from the LIVE cells only
    /// (<c>WorkspaceArchiveScanner.AddGeneratedCells</c>), so neither needs the walk to enumerate a
    /// folder that can hold thousands of cells just to decide about them one file at a time.
    /// </summary>
    private static readonly string[] PrunedDirectories =
    [
        GeneratedCellStore.ReservedFolderName,
        .. SkippedDirectories,
    ];

    /// <summary>
    /// The workspace's own history (RC-5 R-rc5-18, <c>docs/design/revision-control.md</c> §9A).
    ///
    /// <para><b>It is excluded in the brief that first puts one inside the folder this scanner
    /// walks</b>, and that timing is the requirement rather than a detail. The moment a restore point
    /// creates one, an archive would otherwise carry the whole history — <b>including every earlier
    /// version of every file kept, and files no longer in the workspace at all.</b> A designer who
    /// imported a customer's artwork, finished with it, deleted it, and then archived the workspace
    /// for a different customer would ship that artwork, with nothing in the visible file tree
    /// showing it.</para>
    ///
    /// <para><b>That is disclosure, and unlike every loss in the architecture it cannot be undone by
    /// anyone at any later time.</b> RC-8 adds the CHOICE to include history; the exclusion is not
    /// RC-8's to defer — an archive that carried history because nobody told the scanner is that
    /// failure arrived at through pure inattention, and it would ship in the first build that keeps a
    /// restore point.</para>
    ///
    /// <para><b>Both consumers want the same answer here, and that is what makes it the trap's other
    /// half.</b> It sits in the SHARED list, so <c>WorkspaceCopy.Run</c> stops copying the repository
    /// too — which is right for §9A.1's identical reason, a copy made for a different customer must
    /// not carry the first customer's deleted files (R-rc5-19). Read this beside
    /// <c>WorkspaceArchiveScanner.IsSkippedFromArchive</c>'s note on <c>.cwsuser</c>, where the two
    /// consumers want OPPOSITE answers: one shared list, two consumers, and the correct resolution
    /// differs by entry.</para>
    /// </summary>
    public const string RepositoryFolderName = ".git";

    private static readonly string[] SkippedFileNames =
    [
        ".DS_Store", "Thumbs.db", "desktop.ini", ".localized",
    ];

    private static readonly string[] SkippedExtensions =
    [
        ".tmp", ".temp", ".bak", ".swp", ".crdownload", ".source",
    ];

    /// <summary>
    /// True for a path a COPY (<c>WorkspaceCopy.Run</c>, Save Workspace As, <c>new example</c>)
    /// leaves out: everything both consumers leave out, plus <c>.generated-cells</c>.
    ///
    /// <para><b>The generated cells are the second entry where the two consumers want different
    /// answers</b> (brief generated-cells-1). A copy is the same person on the same machine with the
    /// same kits, and the GUI rebuilds every cell on open — so the cache stays behind as it always
    /// has. An ARCHIVE goes to somebody who may not have the kit (a vendor PDK is licensed, and its
    /// row is the sender's choice) and to command-line runs that rebuild nothing, and for them the
    /// generated cells are the only copy of that artwork — so
    /// <c>WorkspaceArchiveScanner.IsSkippedFromArchive</c> does not skip them and the scan offers the
    /// live ones as a row. Read beside that method's note on <c>.cwsuser</c>, which differs the other
    /// way.</para>
    /// </summary>
    public static bool IsSkipped(string relativePath)
        => IsSkippedByBoth(relativePath) || IsGeneratedCellsPath(relativePath);

    /// <summary>True when any segment of <paramref name="relativePath"/> is the generated-cells folder.</summary>
    public static bool IsGeneratedCellsPath(string relativePath)
        => relativePath.Split('/', '\\').Any(
               s => string.Equals(s, GeneratedCellStore.ReservedFolderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a path neither an archive nor a copy carries, whatever the user ticks.</summary>
    public static bool IsSkippedByBoth(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment.Length == 0) continue;
            if (SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)) return true;
            // AppleDouble sidecars ("._Foo.csch") — metadata for a file that is itself archived.
            if (segment.StartsWith("._", StringComparison.Ordinal)) return true;
        }

        var name = Path.GetFileName(relativePath);
        if (SkippedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (SkippedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return true;
        // AtomicFile's in-flight temp ("<name>.crf-tmp-1234") — present only during a write.
        if (name.Contains(".crf-tmp", StringComparison.OrdinalIgnoreCase)) return true;

        // circuitRF's own per-SESSION bookkeeping, dropped in a workspace root: SL4's advisory lock
        // (".crf-open.json") and SL2's write probe (".crf-write-probe-…"). Neither describes the
        // DESIGN, both are meaningless on the recipient's machine, and the lock carries a user name
        // and a host name — so archiving one would put a colleague's account into a file sent
        // outside the company, and would greet the recipient with a notice about a session that has
        // nothing to do with them. Matched by prefix so the next one is covered before it exists.
        if (name.StartsWith(".crf-", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    // ── The walk ──────────────────────────────────────────────────────────────

    /// <summary>Every file under <paramref name="dir"/>, never descending into a pruned directory,
    /// and surviving one unreadable branch.</summary>
    public static IEnumerable<string> EnumerateFilesSafe(string dir)
    {
        if (!Directory.Exists(dir)) yield break;

        // Manual walk rather than EnumerateFiles(AllDirectories): one unreadable subfolder must not
        // abort the whole enumeration, which is exactly what the recursive overload does.
        var stack = new Stack<string>();
        stack.Push(dir);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(current); }
            catch { continue; }
            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (PrunedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(s);
            }
        }
    }

    /// <summary>Every directory under <paramref name="root"/>, pruning what a copy skips, and
    /// surviving one unreadable branch — the directory twin of <see cref="EnumerateFilesSafe"/>.</summary>
    public static IEnumerable<string> EnumerateDirectoriesSafe(string root)
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
                if (IsSkipped(Rel(root, s))) continue;
                yield return s;
                stack.Push(s);
            }
        }
    }

    /// <summary>A file's size, or -1 when it cannot be read.</summary>
    public static long SizeOf(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return -1; }
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/>, with forward slashes.</summary>
    public static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Whether <paramref name="path"/> is strictly inside <paramref name="root"/>, compared
    /// the way this platform's file system compares names.</summary>
    public static bool IsInside(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var r    = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, DocumentFileRefs.PathComparer == StringComparer.Ordinal
                                        ? StringComparison.Ordinal
                                        : StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── The copy ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The reason a copy of the workspace at <paramref name="sourceRoot"/> to
    /// <paramref name="destRoot"/> must not be attempted, or null when it may be. Asked separately
    /// from <see cref="CopyFiles"/> so a dialog can ask while the user is still typing, and so the
    /// caller can re-ask at commit time — the checks touch the filesystem, and the answer can change
    /// between the two.
    /// </summary>
    public static string? CopyRefusal(string? sourceRoot, string? destRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return "There is no workspace folder to copy.";

        if (!File.Exists(Path.Combine(sourceRoot, WorkspaceFileName)))
            return $"'{sourceRoot}' is not a circuitRF workspace (no {WorkspaceFileName} found).";

        if (string.IsNullOrWhiteSpace(destRoot))
            return "Choose where to save the copy.";

        if (Directory.Exists(destRoot) || File.Exists(destRoot))
            return $"'{destRoot}' already exists. Choose a name that is not in use.";

        // Copying a workspace into itself walks its own output. The destination does not exist yet,
        // so this is a question about the PATH, not about what is on disk — and the New Workspace
        // dialog's own "not inside another workspace" check does not cover it: a subfolder of the
        // workspace has no `.cws` of its own.
        if (IsInside(destRoot, sourceRoot))
            return "A workspace cannot be copied into itself. Choose a location outside "
                 + $"'{Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar))}'.";

        return null;
    }

    /// <summary>
    /// Copies every file and folder under <paramref name="from"/> that <see cref="IsSkipped"/> does
    /// not exclude into <paramref name="to"/>, which is created. Repairs no reference — see the type
    /// comment. A failure is REPORTED, never rolled back: the files are on disk either way.
    /// </summary>
    public static WorkspaceTreeCopyResult CopyFiles(string from, string to)
    {
        var failures = new List<string>();
        int files    = 0;
        long bytes   = 0;

        Directory.CreateDirectory(to);

        foreach (string file in EnumerateFilesSafe(from))
        {
            string rel = Rel(from, file);
            if (IsSkipped(rel)) continue;

            string target = Path.Combine(to, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                files++;
                bytes += Math.Max(0, SizeOf(target));
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
            string rel = Rel(from, dir);
            try { Directory.CreateDirectory(Path.Combine(to, rel.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) { failures.Add($"{rel}: {ex.Message}"); }
        }

        return new WorkspaceTreeCopyResult(files, bytes, failures);
    }

    private const string WorkspaceFileName = ".cws";
}
