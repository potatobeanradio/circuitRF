// brief-em3d-21 R-em3d21-2 — will a Palace run fit in this machine's memory, and how much is it using.
//
// Before a run: Em3dSizeEstimate's Palace memory figure against the machine's physical memory. It is a
// WARNING, never a refusal on its own: the estimate errs high by construction (it uses the highest
// per-unknown figure F0 measured), so refusing on it would refuse runs that fit. Only past 150 % does
// a run need to be confirmed — the panel asks, the CLI takes --force.
//
// During a run: the memory of the solver's whole process tree (the Palace wrapper, mpirun, every rank),
// shown beside the stage. brief-emi-10: read every 100 ms as a FOOTPRINT (swapped and compressed pages
// included), and held to a ceiling by the one process loop (PalaceRun.RunProcess) — see Em3dProcessLimits.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CircuitRF.Design.Em3d;

/// <summary>The machine's memory, as a fit check reads it.</summary>
public static class MachineMemory
{
    /// <summary>
    /// Physical memory, bytes. <b>Read with <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c></b>,
    /// which is the machine's physical memory — or, inside a container, the container's memory limit.
    /// The limit is kept rather than replaced by the host's figure on purpose: Palace's processes are
    /// started in the same container and are held to the same limit, so it is the figure a run has to
    /// fit in. circuitRF sets no GC heap limit of its own, which is the one thing that would make the
    /// figure this process's heap instead. Before the first collection the runtime may report 0; the
    /// OS's own figure is read then (sysctl hw.memsize, /proc/meminfo's MemTotal).
    /// </summary>
    public static long PhysicalBytes
    {
        get
        {
            long gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return gc > 0 ? gc : OsPhysicalBytes() ?? 0;
        }
    }

    private static long? OsPhysicalBytes()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
                foreach (string line in File.ReadLines("/proc/meminfo"))
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal) &&
                        long.TryParse(line["MemTotal:".Length..].Replace("kB", "").Trim(), out long kb))
                        return kb * 1024;
            if (OperatingSystem.IsMacOS() && RunTool("/usr/sbin/sysctl", "-n hw.memsize") is { } text &&
                long.TryParse(text.Trim(), out long bytes))
                return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static string? RunTool(string exe, string args)
    {
        if (!File.Exists(exe)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return null;
            p.StandardInput.Close();   // never the serve pipe — Em3dProcessLauncher.Start says why
            string text = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(5000) && p.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Bytes as a reader wants them: "7.4 GB", "850 MB".</summary>
    public static string Format(double bytes)
        => bytes >= 1e9 ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                        : (bytes / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>brief-emi-10 R-emi10-1 — one reading of a process tree: what is resident, and the footprint, which also counts the
/// pages the operating system has compressed or swapped out. Once a tree swaps its resident size stops growing while its
/// footprint does not, so a ceiling is held against the footprint.</summary>
public readonly record struct TreeMemory(long ResidentBytes, long FootprintBytes)
{
    /// <summary>The stage detail's figure: the resident one, which users already read, with the footprint beside it only where
    /// the two differ by more than 10 %.</summary>
    public string Describe()
        => FootprintBytes > 1.1 * ResidentBytes
            ? $"{MachineMemory.Format(ResidentBytes)} in use ({MachineMemory.Format(FootprintBytes)} with swapped and compressed memory)"
            : $"{MachineMemory.Format(ResidentBytes)} in use";
}

/// <summary>
/// R-em3d21-2c, brief-emi-10 R-emi10-1 — the memory of a process and every process under it, cheap enough to read every 100 ms:
/// on macOS each process's physical footprint (<c>proc_pid_rusage</c>) and its children (<c>proc_listchildpids</c>), both
/// system calls; on Linux <c>/proc/&lt;pid&gt;/status</c>'s <c>VmRSS</c> + <c>VmSwap</c>, with the tree from a scan of
/// <c>/proc</c> once a second; on Windows the Job Object the solver was started into (<see cref="Em3dProcessJob"/>).
/// </summary>
public static class ProcessTreeMemory
{
    /// <summary>One row of a process table: the process, its parent, its resident bytes and its footprint.</summary>
    public readonly record struct Row(int Pid, int Parent, long ResidentBytes, long FootprintBytes = 0);

    /// <summary>A reader for one tree, to be called every tick: it keeps what it learnt of the tree between calls.</summary>
    public static Func<TreeMemory?> Sampler(int rootPid)
    {
        if (OperatingSystem.IsMacOS()) return () => ReadMac(rootPid);
        if (OperatingSystem.IsLinux())
        {
            var tree = new LinuxTree(rootPid);
            return tree.Read;
        }
        return () => null;          // Windows reads the job instead
    }

    /// <summary><c>ps -A -o pid=,ppid=,rss=</c>'s output: three integers a row, the last in KiB.
    /// A row that is not three integers is skipped.</summary>
    public static IReadOnlyList<Row> ParsePs(string text)
    {
        var rows = new List<Row>();
        foreach (string line in text.Split('\n'))
        {
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 3 && int.TryParse(f[0], out int pid) && int.TryParse(f[1], out int parent) &&
                long.TryParse(f[2], out long kib))
                rows.Add(new Row(pid, parent, kib * 1024, kib * 1024));
        }
        return rows;
    }

    /// <summary>
    /// A Linux <c>/proc/&lt;pid&gt;/stat</c> line and <c>statm</c> line as one row. The parent is the
    /// second field after the command, which is parenthesised and may itself hold spaces or parentheses,
    /// so the fields are counted from the LAST ')'. Resident pages are statm's second field.
    /// </summary>
    public static Row? ParseProcStat(int pid, string stat, string statm, long pageSize)
    {
        int close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var after = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pages = statm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (after.Length < 2 || pages.Length < 2 || !int.TryParse(after[1], out int parent) ||
            !long.TryParse(pages[1], out long resident))
            return null;
        return new Row(pid, parent, resident * pageSize, resident * pageSize);
    }

    /// <summary>
    /// brief-emi-10 — a Linux <c>/proc/&lt;pid&gt;/status</c> as one row: <c>PPid</c>, <c>VmRSS</c> and <c>VmSwap</c> (kB). The
    /// footprint is resident plus swapped. A kernel thread has no <c>VmRSS</c> line and reads as zero.
    /// </summary>
    public static Row? ParseProcStatus(int pid, string status)
    {
        int? parent = null;
        long rss = 0, swap = 0;
        foreach (string line in status.Split('\n'))
        {
            if (line.StartsWith("PPid:", StringComparison.Ordinal) && int.TryParse(line[5..].Trim(), out int pp)) parent = pp;
            else if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kib(line[6..]);
            else if (line.StartsWith("VmSwap:", StringComparison.Ordinal)) swap = Kib(line[7..]);
        }
        return parent is { } p ? new Row(pid, p, rss, rss + swap) : null;

        static long Kib(string v) => long.TryParse(v.Replace("kB", "").Trim(), out long k) ? k * 1024 : 0;
    }

    /// <summary>The root's resident bytes plus every descendant's.</summary>
    public static long SumTree(IReadOnlyList<Row> rows, int root) => Sum(rows, root).ResidentBytes;

    /// <summary>The root's reading plus every descendant's.</summary>
    public static TreeMemory Sum(IReadOnlyList<Row> rows, int root)
    {
        var children = rows.ToLookup(r => r.Parent);
        var own = rows.GroupBy(r => r.Pid).ToDictionary(g => g.Key, g => g.First());
        long resident = 0, footprint = 0;
        foreach (int pid in Descendants(root, p => children[p].Select(c => c.Pid)))
            if (own.TryGetValue(pid, out var row)) { resident += row.ResidentBytes; footprint += row.FootprintBytes; }
        return new(resident, footprint);
    }

    /// <summary>The root and every process under it, each once.</summary>
    private static List<int> Descendants(int root, Func<int, IEnumerable<int>> children)
    {
        var seen = new HashSet<int>();
        var order = new List<int>();
        var stack = new Stack<int>([root]);
        while (stack.Count > 0)
        {
            int pid = stack.Pop();
            if (!seen.Add(pid)) continue;
            order.Add(pid);
            foreach (int c in children(pid)) stack.Push(c);
        }
        return order;
    }

    // ── Linux ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tree from a scan of every <c>/proc/&lt;pid&gt;/stat</c>, once a second; each member's <c>status</c> every
    /// call. A process started since the last scan is counted from the next.</summary>
    private sealed class LinuxTree(int root)
    {
        private List<int> _members = [root];
        private readonly Stopwatch _scanned = new();

        public TreeMemory? Read()
        {
            try
            {
                if (!_scanned.IsRunning || _scanned.ElapsedMilliseconds >= 1000)
                {
                    _scanned.Restart();
                    var rows = ScanParents();
                    var children = rows.ToLookup(r => r.Parent, r => r.Pid);
                    _members = Descendants(root, p => children[p]);
                }
                long resident = 0, footprint = 0;
                foreach (int pid in _members)
                {
                    string text;
                    try { text = File.ReadAllText($"/proc/{pid}/status"); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // it exited
                    if (ParseProcStatus(pid, text) is { } row) { resident += row.ResidentBytes; footprint += row.FootprintBytes; }
                }
                return new(resident, footprint);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        private static List<(int Pid, int Parent)> ScanParents()
        {
            var rows = new List<(int, int)>();
            foreach (string dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
                try
                {
                    string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    int close = stat.LastIndexOf(')');
                    var after = close < 0 ? [] : stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (after.Length >= 2 && int.TryParse(after[1], out int parent)) rows.Add((pid, parent));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // it exited
            }
            return rows;
        }
    }

    // ── macOS ────────────────────────────────────────────────────────────────────────────────

    private static TreeMemory? ReadMac(int root)
    {
        try
        {
            long resident = 0, footprint = 0;
            bool any = false;
            foreach (int pid in Descendants(root, MacChildren))
            {
                if (MacUsage(pid) is not { } u) continue;
                resident += u.Resident;
                footprint += u.Footprint;
                any = true;
            }
            return any ? new(resident, footprint) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // rusage_info_v2: a 16-byte uuid, then 64-bit fields; ri_resident_size is the 7th and ri_phys_footprint the 8th.
    private const int RusageInfoV2 = 2;
    private const int ResidentOffset = 16 + 6 * 8;
    private const int FootprintOffset = 16 + 7 * 8;

    private static (long Resident, long Footprint)? MacUsage(int pid)
    {
        var buffer = new byte[256];         // rusage_info_v2 is 160 bytes
        if (proc_pid_rusage(pid, RusageInfoV2, buffer) != 0) return null;    // it exited, or is not ours to read
        return (BitConverter.ToInt64(buffer, ResidentOffset), BitConverter.ToInt64(buffer, FootprintOffset));
    }

    private static IEnumerable<int> MacChildren(int pid)
    {
        var buffer = new int[256];
        while (true)
        {
            int n = proc_listchildpids(pid, buffer, buffer.Length * sizeof(int));
            if (n <= 0) return [];
            if (n < buffer.Length) return buffer.Take(n).Where(c => c > 0).ToArray();
            buffer = new int[buffer.Length * 4];        // full: there may be more
        }
    }

    // DllImport rather than LibraryImport, which needs AllowUnsafeBlocks (src/Ui/Updates/AtomicFile.cs says why). Both live in
    // the system's own C library, so nothing is shipped (owner decision O1).
#pragma warning disable SYSLIB1054
    [DllImport("libc")]
    private static extern int proc_pid_rusage(int pid, int flavor, [Out] byte[] buffer);

    [DllImport("libc")]
    private static extern int proc_listchildpids(int ppid, [Out] int[] buffer, int bufferSize);
#pragma warning restore SYSLIB1054
}

/// <summary>How a run's memory estimate sits against the machine.</summary>
public enum Em3dMemoryLevel { Fits, Warning, Severe }

/// <summary>brief-dfr27-5 R-dfr27-5-2 — what a pre-run verdict is about: a solver's memory, or how long openEMS will take.</summary>
public enum Em3dRunConcern { Memory, Time }

/// <summary>One remedy a warning offers, and the estimate it would give.</summary>
public sealed record Em3dMemoryRemedy(string What, long? EstimateBytes)
{
    /// <summary>brief-dfr27-5 R-dfr27-5-5 — a remedy THIS run can take without changing the design: Palace on this many MPI
    /// processes. The question before a run offers it as a button; null for a remedy that is an edit (a preset, a crop).</summary>
    public int? Processes { get; init; }

    /// <summary>A remedy THIS run can take without changing the design: Palace's iterative linear solver in place of the
    /// direct one the setup asks for. The question before a run offers it as a button.</summary>
    public bool Iterative { get; init; }

    /// <summary>brief-dfr27-5 R-dfr27-5-5 — the remedy was SIZED to fit (a crop of the share that would), so its estimate says
    /// how much, not whether: it never counts as a setting that fits.</summary>
    public bool Sized { get; init; }

    /// <summary>Whether this remedy, a setting rather than a size, brings the run within <paramref name="physical"/>.</summary>
    public bool Fits(long physical) => !Sized && EstimateBytes is { } b && b <= physical;
}

/// <summary>brief-dfr27-5 R-dfr27-5-5 — what the question before a run was answered with, beyond yes or no: the remedy the run
/// is to take. Set by whoever asked (the panel's dialog), read by the run.</summary>
public sealed class Em3dMemoryChoice
{
    public Em3dMemoryRemedy? Remedy { get; set; }
}

/// <summary>R-em3d21-2b — the verdict, and the sentence a warning or a confirmation shows.</summary>
/// <param name="File">A file <see cref="Text"/> names and the reader may want to open — the Linux
/// subsystem's <c>.wslconfig</c> when the sentence says to edit it. The Messages panel shows it as a
/// reveal link; a terminal prints it after the sentence.</param>
public sealed record Em3dMemoryVerdict(Em3dMemoryLevel Level, long? EstimateBytes, long PhysicalBytes, string? Text,
                                       string? File = null)
{
    /// <summary>Above this share of physical memory a run is warned about.</summary>
    public const double WarnFraction = 0.75;

    /// <summary>Above this share a run must be confirmed where no one can be asked as well (the CLI needs --force). The panel asks
    /// from <see cref="PastMemory"/>.</summary>
    public const double SevereFraction = 1.5;

    /// <summary>During a run, the share of its memory ceiling (of physical memory, on a run forced past the size check) the
    /// process tree may reach before a warning that the ceiling is near.</summary>
    public const double InUseNoteFraction = 0.9;

    /// <summary>brief-emi-10 — the remedies the verdict was evaluated with, in their order: what a run stopped at its memory
    /// ceiling names.</summary>
    public IReadOnlyList<Em3dMemoryRemedy> Remedies { get; init; } = [];

    /// <summary>brief-dfr27-5 R-dfr27-5-2 — what the verdict is about. A <see cref="Em3dRunConcern.Time"/> verdict asks the
    /// same question the memory check does, through the same confirmation, and its <see cref="Text"/> is the estimate.</summary>
    public Em3dRunConcern Concern { get; init; } = Em3dRunConcern.Memory;

    /// <summary>brief-dfr27-5 R-dfr27-5-5 — the remedy chosen when this verdict was confirmed, if one was.</summary>
    public Em3dMemoryChoice Choice { get; init; } = new();

    /// <summary>The remedies this run can take itself (<see cref="Em3dMemoryRemedy.Processes"/>) and that fit.</summary>
    public IReadOnlyList<Em3dMemoryRemedy> Applicable =>
        [.. Remedies.Where(r => (r.Processes is not null || r.Iterative) && r.Fits(PhysicalBytes)).OrderByDescending(r => r.Iterative)];

    /// <summary>The estimate is past the memory the run's ceiling is set at, so as it stands it would most likely be started only to be
    /// stopped there. Asked about where a question can be asked, and answering "anyway" is what lets a run swap; below this, a run
    /// is not asked about, and keeps its ceiling.</summary>
    public bool PastMemory => EstimateBytes is { } e && PhysicalBytes > 0 && e > PhysicalBytes;

    /// <summary>The run's processes are held to a memory ceiling that starting it anyway lifts, so it may swap: a 3D run's verdict, as
    /// the planar solver's is not.</summary>
    public bool LiftsCeiling { get; init; }

    /// <summary>Past the confirmation with no remedy that is a setting and fits: the question says so, and its default is
    /// not to run.</summary>
    public bool NothingFits => Level == Em3dMemoryLevel.Severe && !Remedies.Any(r => r.Fits(PhysicalBytes));

    public static readonly Em3dMemoryVerdict Unknown = new(Em3dMemoryLevel.Fits, null, 0, null);

    /// <summary>
    /// The verdict for <paramref name="estimate"/> on a machine with <paramref name="physical"/> bytes.
    /// <paramref name="remedies"/> are listed in the order given — the order of their effect.
    /// </summary>
    /// <param name="basis">A sentence put first, saying what the estimate rests on.</param>
    /// <param name="scope">Whose memory <paramref name="physical"/> is — brief-em3d-26: the Linux subsystem's,
    /// with the setting that raises it. Null is this machine's.</param>
    /// <param name="solver">brief-emi-5 — whose estimate it is, as the sentence names it.</param>
    public static Em3dMemoryVerdict Evaluate(long? estimate, long physical, IReadOnlyList<Em3dMemoryRemedy> remedies,
                                             string? basis = null, Em3dMemoryScope? scope = null, string solver = "Palace")
    {
        if (estimate is not { } e || physical <= 0) return new(Em3dMemoryLevel.Fits, estimate, physical, null) { Remedies = remedies };
        double share = (double)e / physical;
        if (share <= WarnFraction) return new(Em3dMemoryLevel.Fits, e, physical, null) { Remedies = remedies };

        var level = share > SevereFraction ? Em3dMemoryLevel.Severe : Em3dMemoryLevel.Warning;
        string pct = (share * 100).ToString("0", CultureInfo.InvariantCulture);
        // Designer feedback round 11 — the warning had grown to a paragraph; the estimate's provenance
        // (the highest memory per unknown measured) is in the user guide, and "errs high" is what a reader needs of it.
        string head = (basis is null ? "" : basis + " ") + $"{solver} may need about {MachineMemory.Format(e)}, {pct} % of " +
                      $"{scope?.Owner ?? "this machine's"} {MachineMemory.Format(physical)}" +
                      (level == Em3dMemoryLevel.Severe
                          ? ". At that size the run will very likely swap or be killed by the operating system. "
                          : share > 1
                              ? $"; as it stands it will probably be stopped at {scope?.Owner ?? "this machine's"} memory limit, unless it is " +
                                "started anyway, which lets it swap (the estimate errs high). "
                              : "; a run this size may slow the machine or swap (the estimate errs high). ");
        var useful = remedies.Where(r => r.EstimateBytes is { } b && b < e).ToList();
        // brief-dfr27-5 R-dfr27-5-5 — each says whether it fits; past the confirmation, a list none of which fits says so
        // plainly, rather than leave "anyway" the only way forward the reader is shown.
        string tail = useful.Count == 0 ? ""
            : "In order of effect: " + string.Join("; ", useful.Select(r =>
                  $"{r.What} — about {MachineMemory.Format(r.EstimateBytes!.Value)}{(r.Fits(physical) ? ", which fits" : "")}")) + ".";
        if (level == Em3dMemoryLevel.Severe && !useful.Any(r => r.Fits(physical)))
            tail += $" No setting fits it in {scope?.Owner ?? "this machine's"} {MachineMemory.Format(physical)}" +
                    (useful.Any(r => r.Sized) ? "; only a smaller crop would" : "") +
                    ". As it stands, the run would very likely be stopped for memory part-way through.";
        var raise = scope?.Remedy?.Invoke(e);
        return new(level, e, physical, (head + tail).TrimEnd() + (raise?.Text is { } r ? " " + r : ""), raise?.File) { Remedies = remedies, LiftsCeiling = true };
    }
}

/// <summary>
/// brief-em3d-26 R-em3d26-2c — whose memory a run is checked against, when it is not this machine's: a
/// Palace in the Linux subsystem runs in a virtual machine that gets a FRACTION of the host's memory by
/// default, so its check uses that VM's figure (<c>free -b</c> inside it) and names the setting that
/// raises it.
/// </summary>
/// <param name="Bytes">The memory the run can use.</param>
/// <param name="Owner">Possessive, as the verdict reads it: "the Linux subsystem's".</param>
/// <param name="Remedy">Given the estimate, the sentence about the setting that raises
/// <paramref name="Bytes"/> and the file it lives in — or the sentence saying raising it cannot be enough.
/// Null: nothing to say.</param>
public sealed record Em3dMemoryScope(long Bytes, string Owner, Func<long, Em3dScopeRemedy?>? Remedy);

/// <summary>What <see cref="Em3dMemoryScope.Remedy"/> says, and the file it names (null when it names none).</summary>
public sealed record Em3dScopeRemedy(string Text, string? File);
