namespace CircuitRF.Design.Workspace;

/// <summary>
/// Whether a workspace can be opened without the window waiting on a file system that does not
/// answer, asked BEFORE the open reads anything on the UI thread.
///
/// <para>Opening a workspace reads its <c>.cws</c>, its cells and the documents it last had open,
/// synchronously and on the UI thread. That is fine on a local disk and a frozen window everywhere
/// else: on a share whose server is gone each read waits out the network timeout, and in a
/// cloud-synced folder whose service is down each ONLINE-ONLY file waits out the sync client's
/// download attempt. So the front door asks three questions off the UI thread, each with a time
/// limit, and refuses with a sentence if any goes unanswered:</para>
/// <list type="number">
///   <item><description>does the <c>.cws</c> exist;</description></item>
///   <item><description>can it be READ (which is what downloads it if it is online-only);</description></item>
///   <item><description>on Windows, are any of the workspace's documents online-only, and if so can
///   one of them be read — a workspace whose <c>.cws</c> was kept on the device while its cells were
///   not is the case the first two miss.</description></item>
/// </list>
/// <para>macOS has no portable way to see an online-only file from .NET, so it gets the first two;
/// the <c>.cws</c> read still catches a folder whose service is down when the <c>.cws</c> itself is
/// not on the device.</para>
/// </summary>
public static class WorkspaceReachability
{
    /// <summary>The outcome. <see cref="Message"/> is null when the workspace answered.</summary>
    public sealed record Result(PathState State, string? Message)
    {
        public bool Ok => State == PathState.Present;
    }

    /// <summary>How many entries the online-only scan looks at before it stops. Enough to see a
    /// workspace's cells; a results folder of thousands of files is skipped by name anyway.</summary>
    private const int ScanLimit = 2000;

    // FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_OPEN, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS:
    // what a sync client's placeholder carries. FileAttributes is the raw Win32 value cast to the
    // enum, so the bits the enum does not name are still there.
    private const int CloudOnlyBits = 0x1000 | 0x40000 | 0x400000;

    /// <summary>Folders that hold run output or history rather than the documents an open reads.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "results", ".git", ".history", "bin", "obj",
    };

    /// <summary>Checks <paramref name="cwsPath"/>; never throws and never blocks the caller for
    /// longer than about two timeouts.</summary>
    public static async Task<Result> CheckAsync(string cwsPath, TimeSpan? timeout = null)
    {
        var limit = timeout ?? PathProbe.DefaultTimeout;

        var state = await PathProbe.ProbeAsync(cwsPath, limit).ConfigureAwait(false);
        if (state == PathState.Missing) return new Result(PathState.Missing, null);
        if (state == PathState.NotResponding)
            return new Result(PathState.NotResponding, PathProbe.NotRespondingSentence(Path.GetDirectoryName(cwsPath) ?? cwsPath));

        // A read, not a stat: the stat of an online-only file answers from the placeholder and the
        // read is what asks the sync client for the bytes.
        var cws = await PathProbe.WithinAsync(() => File.ReadAllBytes(cwsPath), limit, cwsPath).ConfigureAwait(false);
        if (cws is null)
            return new Result(PathState.NotResponding, PathProbe.NotRespondingSentence(Path.GetDirectoryName(cwsPath) ?? cwsPath));

        if (OperatingSystem.IsWindows() && Path.GetDirectoryName(cwsPath) is { } root)
        {
            var sample = await PathProbe.WithinAsync(() => SmallestCloudOnlyDocument(root), limit, root).ConfigureAwait(false);
            if (sample is null)
                return new Result(PathState.NotResponding, PathProbe.NotRespondingSentence(root));
            if (sample.Length > 0)
            {
                var read = await PathProbe.WithinAsync(() => ReadFirstByte(sample), limit, sample).ConfigureAwait(false);
                if (read is null)
                    return new Result(PathState.NotResponding,
                        $"Some of this workspace's files are online-only and the service that downloads them is not "
                        + $"responding, so opening it would stall. Try again when it is back, or set the folder "
                        + $"'{root}' to always keep its files on this device.");
            }
        }

        return new Result(PathState.Present, null);
    }

    /// <summary>The smallest online-only document under <paramref name="root"/>, or "" when there is
    /// none. Enumerating does not download anything; only reading does.</summary>
    internal static string SmallestCloudOnlyDocument(string root)
    {
        string best = "";
        long bestLength = long.MaxValue;
        int seen = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0 && seen < ScanLimit)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos(); }
            catch { continue; }
            foreach (var e in entries)
            {
                if (++seen > ScanLimit) break;
                if (e is DirectoryInfo sub)
                {
                    // A symbolic link or junction is not followed (it can loop); a sync client's own
                    // placeholder folders are reparse points too, which is why the test is LinkTarget
                    // and not the ReparsePoint attribute.
                    if (!SkippedFolders.Contains(sub.Name) && !IsLink(sub))
                        pending.Push(sub);
                    continue;
                }
                if (((int)e.Attributes & CloudOnlyBits) == 0) continue;
                long length = e is FileInfo fi ? fi.Length : long.MaxValue;
                if (length < bestLength) { best = e.FullName; bestLength = length; }
            }
        }
        return best;
    }

    private static bool IsLink(DirectoryInfo d)
    {
        try { return d.LinkTarget is not null; }
        catch { return true; }
    }

    private static byte[] ReadFirstByte(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        var b = new byte[1];
        _ = s.Read(b, 0, 1);
        return b;
    }
}
