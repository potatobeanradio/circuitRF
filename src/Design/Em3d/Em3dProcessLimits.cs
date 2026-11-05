// brief-emi-10 — a memory ceiling and a free-disk floor for every solver and mesher process circuitRF starts.
//
// The size check before a run (MemoryGate, with each solver's Em3dMemoryVerdict) is an ESTIMATE, taken once; it cannot see a
// mesher that runs away on one curve, which grew a 1D mesh by about 8 GB in half a second, swapped the machine and filled its
// startup disk until it crashed. What stops that is here: the one process loop (PalaceRun.RunProcess) reads the tree's
// FOOTPRINT every 100 ms and kills the tree at the ceiling, and reads the free space of the volume swap grows on and of the
// run's own volume every second and kills the tree below the floor. Both write no result, as a cancelled run writes none.
//
//   * The ceiling defaults to this machine's physical memory: past it the operating system swaps. A run the user forced past
//     the size check, which asks once the estimate is past that memory, keeps no ceiling (owner decision O2) — it chose swap
//     knowingly — but keeps the floor.
//   * The floor is 10 GB, or 5 % of the volume when that is smaller (O3). It is not a setting, and forcing does not lift it:
//     it is the guard against the failure that happened.
//   * On Windows the Job Object holds the ceiling itself (Em3dProcessJob), so a burst between two polls cannot overshoot it.

using System.Globalization;

namespace CircuitRF.Design.Em3d;

/// <summary>Why a process tree was stopped, or not started.</summary>
public enum ProcessStopReason { Memory, Disk }

/// <summary>
/// brief-emi-10 — what stopped a process tree: its footprint reaching the ceiling, or a volume's free space falling below the
/// floor. <paramref name="BeforeStart"/>: the volume was already below the floor, and nothing was started.
/// </summary>
/// <param name="ReadingBytes">The footprint reached, or the free space left.</param>
/// <param name="LimitBytes">The ceiling, or the floor.</param>
/// <param name="Meshing">The tree was meshing (Gmsh, or EMerge before it reported its mesh) rather than solving.</param>
/// <param name="Volume">The volume, as its mount point or drive names it (a disk stop only).</param>
/// <param name="VolumeRole">What the volume is to the run (a disk stop only): "the startup volume, where the operating system
/// swaps".</param>
public sealed record ProcessStop(ProcessStopReason Reason, long ReadingBytes, long LimitBytes, bool Meshing,
                                 bool BeforeStart = false, string? Volume = null, string? VolumeRole = null)
{
    /// <summary>
    /// The sentence a run stopped this way shows, naming <paramref name="solver"/> and what it was doing. A memory stop while
    /// meshing says a runaway is the likely cause; while solving, that the problem is larger than this machine, with
    /// <paramref name="remedies"/> (the solver's own, in their order of effect).
    /// </summary>
    /// <param name="ceilingOwner">Whose ceiling it was: "this machine's memory limit for a solver".</param>
    /// <param name="logPath">The run's log, which a runaway's sentence points at.</param>
    public string Sentence(string solver, string ceilingOwner, IReadOnlyList<Em3dMemoryRemedy> remedies, string? logPath)
    {
        string doing = Meshing ? "meshing" : "solving";
        if (Reason == ProcessStopReason.Disk)
        {
            string head = BeforeStart ? $"{solver} was not started" : $"{solver} was stopped while {doing}";
            return $"{head}: {VolumeRole} ({Volume}) has {MachineMemory.Format(ReadingBytes)} free, below the " +
                   $"{MachineMemory.Format(LimitBytes)} circuitRF keeps free on it so the operating system can swap and the run can " +
                   $"write. {(BeforeStart ? "" : "Nothing was written. ")}Free some space on that volume, or write the results to another one.";
        }
        string reached = $"{solver} was stopped while {doing}: it reached {MachineMemory.Format(ReadingBytes)}, {ceilingOwner} " +
                         $"({MachineMemory.Format(LimitBytes)}). Nothing was written.";
        string listed = string.Join("; ", remedies.Select(r => r.What));
        if (Meshing)
            return reached + " A mesh this size usually means the mesher ran away on one curve; check the run's log for the object " +
                   $"it was meshing{(logPath is null ? "" : $" ({logPath})")}, or coarsen the mesh" +
                   (listed.Length > 0 ? $": {listed}." : ".");
        return reached + " The problem is larger than this machine can solve without swapping" +
               (listed.Length > 0 ? $"; in order of effect: {listed}." : "; coarsen the mesh, or make the problem smaller.");
    }
}

/// <summary>
/// brief-emi-10 R-emi10-2/4 — the ceiling one run's processes are held to, and whose it is. The disk floor is not here: it is
/// not a setting, and every watched process keeps it.
/// </summary>
/// <param name="CeilingBytes">The footprint at which the tree is stopped; null for none.</param>
/// <param name="CeilingOwner">Whose ceiling, as the stop sentence names it.</param>
public sealed record ProcessLimits(long? CeilingBytes, string CeilingOwner)
{
    /// <summary>The sentence's owner for the default ceiling.</summary>
    public const string MachineOwner = "this machine's memory limit for a solver";

    /// <summary>The sentence's owner for a limit the user set (Settings ▸ Solvers, or <c>--memory-limit</c>).</summary>
    public const string UserOwner = "the memory limit set for a solver";

    /// <summary>True once the user has forced the run past the size check: from then on there is no ceiling (owner decision
    /// O2). Read every tick, because EMerge's check is answered while its process runs.</summary>
    public Func<bool>? Lifted { get; init; }

    /// <summary>
    /// The ceiling for a machine with <paramref name="physicalBytes"/>, lowered to <paramref name="userLimitBytes"/> when
    /// that is given and below it — a limit can lower the ceiling, never raise it past physical memory.
    /// </summary>
    public static ProcessLimits For(long physicalBytes, long? userLimitBytes)
        => userLimitBytes is { } u && u > 0 && (physicalBytes <= 0 || u < physicalBytes)
            ? new(u, UserOwner)
            : new(physicalBytes > 0 ? physicalBytes : null, MachineOwner);

    /// <summary>A watch on a process working in <paramref name="runDirectory"/>, held to this ceiling and to the disk floor on
    /// the volume swap grows on and the run's own.</summary>
    public ProcessWatch Watch(Action<string>? onLine, long physicalBytes, string runDirectory)
        => new(onLine, physicalBytes)
        {
            CeilingBytes = CeilingBytes, CeilingLifted = Lifted, CeilingOwner = CeilingOwner,
            FloorVolumes = DiskFloor.Volumes(runDirectory),
        };
}

/// <summary>A volume the floor is kept on, and what it is to the run.</summary>
public sealed record FloorVolume(string Path, string Role);

/// <summary>A volume's free and total bytes.</summary>
public readonly record struct VolumeSpace(long FreeBytes, long TotalBytes);

/// <summary>brief-emi-10 R-emi10-3 — the free-disk floor: the volume swap grows on, and the run's own.</summary>
public static class DiskFloor
{
    /// <summary>The floor on a volume large enough: 10 GB.</summary>
    public const long FloorBytes = 10_000_000_000;

    /// <summary>On a smaller volume, this share of it.</summary>
    public const double FloorFraction = 0.05;

    /// <summary>The floor on a volume of <paramref name="totalBytes"/>: 10 GB, or 5 % of it when that is smaller.</summary>
    public static long FloorFor(long totalBytes)
        => totalBytes > 0 ? Math.Min(FloorBytes, (long)(FloorFraction * totalBytes)) : FloorBytes;

    /// <summary>
    /// Where swap grows: macOS's startup volume (<c>/System/Volumes/VM</c>, which shares its container); Windows's system
    /// drive, which holds the page file. Linux has none: its swap is a fixed partition or file, and it fails by its OOM
    /// killer, not by a full disk.
    /// </summary>
    public static string? SwapPath
        => OperatingSystem.IsMacOS() ? (Directory.Exists("/System/Volumes/VM") ? "/System/Volumes/VM" : "/")
         : OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory)
         : null;

    /// <summary>The volumes a run in <paramref name="runDirectory"/> keeps the floor on: the swap volume, then the run's own;
    /// one entry when they are the same volume.</summary>
    public static IReadOnlyList<FloorVolume> Volumes(string runDirectory)
    {
        const string Swap = "the startup volume, where the operating system swaps";
        const string Run = "the volume the run writes to";
        string? swap = SwapPath;
        if (swap is null) return [new(runDirectory, Run)];
        return SameVolume(swap, runDirectory)
            ? [new(swap, "the startup volume, where the operating system swaps and the run writes")]
            : [new(swap, Swap), new(runDirectory, Run)];
    }

    /// <summary>The free and total space of the volume holding <paramref name="path"/>; null where it cannot be read (a share
    /// on Windows, a path that does not exist yet and has no existing parent).</summary>
    public static VolumeSpace? Read(string path)
    {
        try
        {
            string? at = Existing(path);
            if (at is null) return null;
            var d = new DriveInfo(at);
            return new(d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// What starting a run anyway means for the disk, in the question before it: it keeps no memory ceiling, so it swaps what
    /// does not fit, and the floor is then what stops it. Null where swap is no volume's to give (Linux, the subsystem's memory).
    /// </summary>
    /// <param name="estimateBytes">What the run may need.</param>
    /// <param name="physicalBytes">The memory it is checked against.</param>
    /// <param name="space">The swap volume's free and total space, or null when it cannot be read.</param>
    public static string? SwapRoom(long estimateBytes, long physicalBytes, VolumeSpace? space)
    {
        if (space is not { } s || estimateBytes <= physicalBytes) return null;
        long swap = estimateBytes - physicalBytes, floor = FloorFor(s.TotalBytes), room = s.FreeBytes - floor;
        return $"Simulate Anyway keeps no memory limit, so the run may swap about {MachineMemory.Format(swap)}: the startup disk has " +
               $"{MachineMemory.Format(s.FreeBytes)} free, and the run is stopped when that falls to {MachineMemory.Format(floor)}." +
               (room < swap ? $" Free about {MachineMemory.Format(swap - room)} more first." : "");
    }

    /// <summary>The volume's name as a reader knows it: its drive, or its mount point.</summary>
    public static string Name(string path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        if (path == "/System/Volumes/VM") return "/";
        return MountOf(path) ?? path;
    }

    private static bool SameVolume(string a, string b)
        => string.Equals(Name(a), Name(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The longest mount point holding <paramref name="path"/>. On macOS a home directory is reached through a firmlink
    /// from <c>/</c>, so it reads as <c>/</c> — the volume of the same container, whose free space it shares.</summary>
    private static string? MountOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName)
                .Where(m => full == m || full.StartsWith(m.EndsWith('/') ? m : m + "/", StringComparison.Ordinal))
                .OrderByDescending(m => m.Length).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? Existing(string path)
    {
        string? at = Path.GetFullPath(path);
        while (at is not null && !Directory.Exists(at)) at = Path.GetDirectoryName(at);
        return at;
    }
}

/// <summary>
/// brief-emi-10 R-emi10-4 — the user's own memory limit for a solver: a per-MACHINE preference (Settings ▸ Solvers, stored as
/// <c>em_memory_limit_gb</c>), or <c>circuitrf em --memory-limit</c>. It lowers the ceiling; it cannot raise it past physical
/// memory, which is what forcing a run does.
/// </summary>
public static class Em3dMemoryLimit
{
    /// <summary>Bytes in the GB the limit is written in (the decimal one, as every figure the run shows).</summary>
    public const double BytesPerGb = 1e9;

    /// <summary>Null when <paramref name="gb"/> is a usable limit on a machine with <paramref name="physicalBytes"/>; otherwise
    /// the refusal, the same sentence from the panel and from the flag.</summary>
    public static string? Refusal(double gb, long physicalBytes)
    {
        if (double.IsNaN(gb) || gb <= 0)
            return "A memory limit for a solver is a positive number of GB.";
        if (physicalBytes > 0 && gb * BytesPerGb >= physicalBytes)
            return $"A memory limit for a solver must be below this machine's memory ({MachineMemory.Format(physicalBytes)}); " +
                   $"{gb.ToString("0.##", CultureInfo.InvariantCulture)} GB is not. Past physical memory the run swaps, and starting a " +
                   "run anyway when its size check asks is what allows that.";
        return null;
    }

    /// <summary>The limit in bytes, or null for <paramref name="gb"/> null (Automatic).</summary>
    public static long? Bytes(double? gb) => gb is { } g ? (long)(g * BytesPerGb) : null;

    /// <summary>
    /// What <c>explain</c> reports of a run's limits: the ceiling and where it came from, and the floor on each volume.
    /// </summary>
    public static IReadOnlyList<string> Describe(long physicalBytes, long? userLimitBytes, string runDirectory)
    {
        var limits = ProcessLimits.For(physicalBytes, userLimitBytes);
        var lines = new List<string>
        {
            limits.CeilingBytes is { } c
                ? $"Memory ceiling: {MachineMemory.Format(c)} ({(limits.CeilingOwner == ProcessLimits.UserOwner ? "the limit given" : "this machine's physical memory")}); " +
                  "a solver or mesher reaching it is stopped. A run started anyway when its size check asks has none."
                : "Memory ceiling: none (this machine's memory could not be read).",
        };
        foreach (var v in DiskFloor.Volumes(runDirectory))
        {
            var space = DiskFloor.Read(v.Path);
            lines.Add($"Disk floor: {MachineMemory.Format(DiskFloor.FloorFor(space?.TotalBytes ?? 0))} on {v.Role} ({DiskFloor.Name(v.Path)})" +
                      (space is { } s ? $", {MachineMemory.Format(s.FreeBytes)} free now." : "."));
        }
        return lines;
    }
}
