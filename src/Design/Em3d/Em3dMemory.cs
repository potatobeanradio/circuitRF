// brief-em3d-21 R-em3d21-2 — will a Palace run fit in this machine's memory, and how much is it using.
//
// Before a run: Em3dSizeEstimate's Palace memory figure against the machine's physical memory. It is a
// WARNING, never a refusal on its own: the estimate errs high by construction (it uses the highest
// per-unknown figure F0 measured), so refusing on it would refuse runs that fit. Only past 150 % does
// a run need to be confirmed — the panel asks, the CLI takes --force.
//
// During a run: the resident memory of the solver's whole process tree (the Palace wrapper, mpirun,
// every rank), sampled once a second and shown beside the stage. At 90 % of physical memory the run
// gets a one-time note naming it. Nothing here kills a run: the user decides.

using System.Diagnostics;
using System.Globalization;

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
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return null;
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

/// <summary>R-em3d21-2c — the resident memory of a process and every process under it.</summary>
public static class ProcessTreeMemory
{
    /// <summary>One row of a process table: the process, its parent, and its resident bytes.</summary>
    public readonly record struct Row(int Pid, int Parent, long ResidentBytes);

    /// <summary>The tree's resident bytes, or null where this OS offers no process table circuitRF
    /// reads (Windows: Palace does not run there natively).</summary>
    public static long? ResidentBytes(int rootPid)
    {
        IReadOnlyList<Row>? rows = null;
        try
        {
            if (OperatingSystem.IsLinux()) rows = ReadProc();
            else if (OperatingSystem.IsMacOS() && MachineMemory.RunTool("/bin/ps", "-A -o pid=,ppid=,rss=") is { } ps)
                rows = ParsePs(ps);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return rows is null ? null : SumTree(rows, rootPid);
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
                rows.Add(new Row(pid, parent, kib * 1024));
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
        return new Row(pid, parent, resident * pageSize);
    }

    /// <summary>The root's resident bytes plus every descendant's.</summary>
    public static long SumTree(IReadOnlyList<Row> rows, int root)
    {
        var children = rows.ToLookup(r => r.Parent);
        var own = rows.GroupBy(r => r.Pid).ToDictionary(g => g.Key, g => g.First().ResidentBytes);
        long total = 0;
        var seen = new HashSet<int>();
        var stack = new Stack<int>([root]);
        while (stack.Count > 0)
        {
            int pid = stack.Pop();
            if (!seen.Add(pid)) continue;
            total += own.GetValueOrDefault(pid);
            foreach (var c in children[pid]) stack.Push(c.Pid);
        }
        return total;
    }

    private static List<Row> ReadProc()
    {
        var rows = new List<Row>();
        long page = Environment.SystemPageSize;
        foreach (string dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            try
            {
                if (ParseProcStat(pid, File.ReadAllText(Path.Combine(dir, "stat")),
                                  File.ReadAllText(Path.Combine(dir, "statm")), page) is { } row)
                    rows.Add(row);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // it exited
        }
        return rows;
    }
}

/// <summary>How a run's memory estimate sits against the machine.</summary>
public enum Em3dMemoryLevel { Fits, Warning, Severe }

/// <summary>One remedy a warning offers, and the estimate it would give.</summary>
public sealed record Em3dMemoryRemedy(string What, long? EstimateBytes);

/// <summary>R-em3d21-2b — the verdict, and the sentence a warning or a confirmation shows.</summary>
public sealed record Em3dMemoryVerdict(Em3dMemoryLevel Level, long? EstimateBytes, long PhysicalBytes, string? Text)
{
    /// <summary>Above this share of physical memory a run is warned about.</summary>
    public const double WarnFraction = 0.75;

    /// <summary>Above this share a run must be confirmed (the panel asks; the CLI needs --force).</summary>
    public const double SevereFraction = 1.5;

    /// <summary>During a run, the share of physical memory the process tree may reach before a note.</summary>
    public const double InUseNoteFraction = 0.9;

    public static readonly Em3dMemoryVerdict Unknown = new(Em3dMemoryLevel.Fits, null, 0, null);

    /// <summary>
    /// The verdict for <paramref name="estimate"/> on a machine with <paramref name="physical"/> bytes.
    /// <paramref name="remedies"/> are listed in the order given — the order of their effect.
    /// </summary>
    /// <param name="basis">A sentence put first, saying what the estimate rests on.</param>
    /// <param name="scope">Whose memory <paramref name="physical"/> is — brief-em3d-26: the Linux subsystem's,
    /// with the setting that raises it. Null is this machine's.</param>
    public static Em3dMemoryVerdict Evaluate(long? estimate, long physical, IReadOnlyList<Em3dMemoryRemedy> remedies,
                                             string? basis = null, Em3dMemoryScope? scope = null)
    {
        if (estimate is not { } e || physical <= 0) return new(Em3dMemoryLevel.Fits, estimate, physical, null);
        double share = (double)e / physical;
        if (share <= WarnFraction) return new(Em3dMemoryLevel.Fits, e, physical, null);

        var level = share > SevereFraction ? Em3dMemoryLevel.Severe : Em3dMemoryLevel.Warning;
        string pct = (share * 100).ToString("0", CultureInfo.InvariantCulture);
        string head = (basis is null ? "" : basis + " ") + $"Palace's memory for this run is estimated at about {MachineMemory.Format(e)}, {pct} % of " +
                      $"{scope?.Owner ?? "this machine's"} {MachineMemory.Format(physical)}. " +
                      (level == Em3dMemoryLevel.Severe
                          ? "At that size the run will very likely swap or be killed by the operating system. "
                          : "The estimate errs high by construction (it uses the highest memory per unknown circuitRF has " +
                            "measured), but a run this size may slow the machine or swap. ");
        var useful = remedies.Where(r => r.EstimateBytes is { } b && b < e).ToList();
        string tail = useful.Count == 0 ? ""
            : "In order of effect: " + string.Join("; ", useful.Select(r =>
                  $"{r.What} — about {MachineMemory.Format(r.EstimateBytes!.Value)}")) + ".";
        string raise = scope?.Remedy is { } r ? " " + r : "";
        return new(level, e, physical, (head + tail).TrimEnd() + raise);
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
/// <param name="Remedy">The sentence naming the setting that raises it.</param>
public sealed record Em3dMemoryScope(long Bytes, string Owner, string? Remedy);
