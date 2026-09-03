namespace CircuitRF.Design.Workspace;

/// <summary>
/// brief-shared-library-2-read-only-workspaces.md §2: answers "can circuitRF write into this
/// directory?" — the question the product had no concept of before SL2 (a repo-wide grep for
/// <c>IsReadOnly</c> returned nothing), so every path took the optimistic branch and discovered the
/// truth at the write, after the user had done the work.
///
/// <para><b>R-sl2-1 — the answer is DISCOVERED, by attempting a write.</b> <c>File.GetAttributes</c>
/// reports the DOS read-only bit and says nothing at all about a share ACL, a POSIX mode or a
/// read-only mount option; <c>Directory.Exists</c> says less than that. Creating a uniquely-named
/// file and deleting it is the only answer that is the same answer on Windows, macOS and Linux, and
/// it is the answer to the question actually being asked.</para>
///
/// <para><b>R-sl2-2 — a probe that throws for ANY reason means read-only.</b> Not "read-only unless
/// the exception was <c>UnauthorizedAccessException</c>": an <c>IOException</c> from a full disk, a
/// disconnected share or a locked-down directory all mean the same thing to every caller downstream
/// — do not attempt writes, and say so. Distinguishing them buys nothing and multiplies the states.
/// (<see cref="CircuitRF.Diagnostics.FileAccessDiagnostics"/> still describes a write that failed
/// anyway; this type is about not getting there.)</para>
///
/// <para><b>R-sl2-3 — memoised, and dropped by <see cref="WorkspaceRootFinder.InvalidateCache"/>.</b>
/// That is where the two other per-workspace memos already live and are already dropped together
/// (<c>WorkspaceRootFinder</c>'s walk-up and <c>ExternalCellRef</c>'s alias table). A third memo with
/// a lifecycle of its own would be the one that goes stale.</para>
///
/// <para>Framework-free and in <c>src/Design</c> rather than <c>src/Ui</c> for the ordinary reason:
/// <c>src/Cli</c> writes workspaces too and cannot reference Avalonia.</para>
/// </summary>
public static class WorkspaceWritability
{
    /// <summary>
    /// The seam the behavioural gate drives (brief §5 item 1). A directory that is genuinely
    /// unwritable is one <c>chmod 500</c> away on macOS and Linux and an ACL edit away on Windows,
    /// where a test running elevated may be able to write regardless — so the tests that protect the
    /// BEHAVIOUR (R-sl2-5 … R-sl2-13) drive this predicate and run identically everywhere, and one
    /// real-filesystem test per platform capability checks the probe itself.
    ///
    /// <para>Given a normalised absolute directory path, returns true for writable. Null (the
    /// default) uses <see cref="Probe"/>. Setting it clears the memo, since it changes every
    /// answer.</para>
    /// </summary>
    public static Func<string, bool>? WritabilityProbe
    {
        get => _probe;
        set { _probe = value; InvalidateCache(); }
    }
    private static Func<string, bool>? _probe;

    private static readonly Dictionary<string, bool> _memo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _memoGate = new();

    /// <summary>
    /// True when a file can be created in <paramref name="directory"/>. Memoised per directory.
    ///
    /// <para>A null or blank directory answers <b>true</b> and is not memoised: "no directory" is a
    /// scratch document that has never been saved, and the picker it saves through asks the
    /// filesystem its own question. Answering false there would disable Save on a document that has
    /// no read-only workspace behind it at all.</para>
    /// </summary>
    public static bool IsWritable(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return true;

        string key = WorkspaceRootFinder.Normalize(directory);
        if (key.Length == 0) return true;

        lock (_memoGate)
            if (_memo.TryGetValue(key, out bool memo)) return memo;

        // R-sl2-2 applies to the ANSWER, not only to the real probe's own internals: a writability
        // question that throws would take down whatever asked it — a menu enablement, a render, a
        // save — for a question whose whole purpose is to be answerable. Any failure is read-only.
        bool writable;
        try   { writable = (_probe ?? Probe)(key); }
        catch { writable = false; }

        lock (_memoGate) _memo[key] = writable;
        return writable;
    }

    /// <summary>The negation, for the many call sites that read better that way.</summary>
    public static bool IsReadOnly(string? directory) => !IsWritable(directory);

    /// <summary>
    /// R-sl2-1: the WORKSPACE question — is the workspace <paramref name="pathInsideWorkspace"/>
    /// belongs to writable? Walks up to the nearest ancestor <c>.cws</c> (memoised by
    /// <see cref="WorkspaceRootFinder.WorkspaceDirOf"/>) and probes that root.
    ///
    /// <para>A path with no ancestor workspace answers <b>true</b>: there is no read-only workspace
    /// to refuse on behalf of, and the per-DOCUMENT question (R-sl2-4,
    /// <see cref="IsDocumentReadOnly"/>) is the one that still applies to a loose file.</para>
    /// </summary>
    public static bool IsWorkspaceReadOnly(string? pathInsideWorkspace)
        => WorkspaceRootFinder.WorkspaceDirOf(pathInsideWorkspace) is { } root && IsReadOnly(root);

    /// <summary>
    /// R-sl2-4: the per-DOCUMENT question, which is the same probe on the document's own directory.
    /// A workspace can be writable while one cell folder inside it is not, and — more commonly — a
    /// document open from a read-only library sits in a workspace this window did not open at all.
    /// Ask about the directory that would actually be written.
    ///
    /// <para>A null path is a scratch document and is never read-only (see
    /// <see cref="IsWritable"/>).</para>
    /// </summary>
    public static bool IsDocumentReadOnly(string? documentAbsPath)
    {
        if (string.IsNullOrWhiteSpace(documentAbsPath)) return false;
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(documentAbsPath)); }
        catch { return true; } // an unusable path is not a path we are going to write to
        return IsReadOnly(dir);
    }

    /// <summary>
    /// R-sl2-3: forgets every memoised answer. Called from
    /// <see cref="WorkspaceRootFinder.InvalidateCache"/>, alongside the walk-up and alias memos,
    /// because a workspace opening, closing or being remounted is exactly when a share's writability
    /// can have changed underneath us.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_memoGate) _memo.Clear();
    }

    /// <summary>The probe file's name prefix. One place, because the sweep has to agree with the
    /// writer exactly or it either misses litter or deletes something that is not ours.</summary>
    private const string ProbePrefix = ".crf-write-probe-";

    /// <summary>
    /// The real probe (R-sl2-1/-2): create a uniquely-named file, then delete it. Uniquely-named
    /// because two circuitRF windows — or two engineers on one share — probe the same root in the
    /// same second, and a fixed name would make them collide and report each other as read-only.
    ///
    /// <para><b>A hard crash between the create and the delete DOES leave the file behind, and
    /// <see cref="FileOptions.DeleteOnClose"/> does not save us — measured, not assumed.</b> That
    /// flag is a kernel flag on Windows (the OS removes the file however the process dies) but on
    /// Unix .NET emulates it by unlinking when the handle closes, and a <c>SIGKILL</c> closes no
    /// handles. Killing a process holding an open DeleteOnClose stream on macOS leaves a 1-byte file
    /// in the directory, every time. It is still passed because it is free and it is the right
    /// behaviour on Windows; it is simply not a guarantee.</para>
    ///
    /// <para>That matters more than a stray temp file usually would, because the project tree hides
    /// only <c>.DS_Store</c> and <c>*.source</c> — <b>not dotfiles in general</b> — so an orphaned
    /// probe would appear as a loose file node at the workspace root, and travel into an archive.
    /// Hence <see cref="SweepStaleProbes"/>: the probe self-heals rather than relying on a flag that
    /// does not hold on two of the three platforms.</para>
    /// </summary>
    private static bool Probe(string directory)
    {
        string path = Path.Combine(directory, ProbePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            using (var s = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                          bufferSize: 1, FileOptions.DeleteOnClose))
            {
                s.WriteByte(0);
            }
            SweepStaleProbes(directory);
            return true;
        }
        catch
        {
            // R-sl2-2: any failure at all means read-only to every caller downstream.
            return false;
        }
        finally
        {
            // DeleteOnClose covers the normal path on Windows; this is what actually removes it on
            // macOS and Linux, and on the network filesystems where the flag is advisory. Costs
            // nothing when there is nothing there.
            try { if (File.Exists(path)) File.Delete(path); } catch { /* nothing further to try */ }
        }
    }

    /// <summary>
    /// Removes probe files a previous session was killed in the middle of writing. Called only after
    /// a probe SUCCEEDED, so the directory is known writable and the sweep cannot itself fail for
    /// permissions; once per directory per session, since the answer is memoised.
    ///
    /// <para><b>The age cut-off is what makes this safe against a concurrent probe</b> — another
    /// window, or another engineer on the same share, mid-probe right now. A live probe file is
    /// milliseconds old and is never touched. (Deleting one would in fact be survivable on both
    /// platforms — Unix keeps the open handle valid, Windows refuses the delete outright and the
    /// per-file catch swallows it — but "never race in the first place" is one line and needs no
    /// per-platform reasoning to stay true.)</para>
    /// </summary>
    private static void SweepStaleProbes(string directory)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (string stale in Directory.EnumerateFiles(directory, ProbePrefix + "*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(stale) < cutoff) File.Delete(stale);
                }
                catch { /* someone else's live probe, or a file that vanished — either is fine */ }
            }
        }
        catch { /* the sweep is a courtesy; never let it affect the answer */ }
    }
}
