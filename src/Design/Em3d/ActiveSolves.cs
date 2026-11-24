// The solver and mesher trees running on this machine, across every circuitRF process — the application, a CLI run, and
// each `serve` an agent drives. Each tree's ceiling (Em3dProcessLimits) is held against the machine's memory as if it were
// the only one, and the size check before a run is too, so solves started side by side — seven EMerge solves from one
// optimizer's batch, or two agents' runs at once — each stayed under the machine's memory while together they were several
// times it, and the machine swapped until it stopped responding.
//
// Each tree watched by the one process loop (PalaceRun.RunProcess) writes one small file here and rewrites it every second
// with its footprint; the file is deleted when the tree ends. A file not rewritten for FreshFor is a tree whose host ended
// without deleting it, and is ignored and removed — no process id is trusted, since ids are reused. The directory is the
// user's temp directory, which every circuitRF process of one user shares.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CircuitRF.Design.Em3d;

/// <summary>The solver and mesher trees running on this machine, in every circuitRF process of this user.</summary>
public static class ActiveSolves
{
    /// <summary>One running tree.</summary>
    /// <param name="Program">The program the tree was started as ("python3", "palace", "gmsh").</param>
    /// <param name="Directory">The directory it works in.</param>
    /// <param name="Started">When it started.</param>
    /// <param name="FootprintBytes">Its footprint at its last rewrite.</param>
    public sealed record Entry(string Program, string Directory, DateTimeOffset Started, long FootprintBytes);

    /// <summary>A file older than this belongs to a tree whose host ended without removing it.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(15);

    /// <summary>Where the files are kept; tests point it at a scratch directory.</summary>
    internal static string Root { get; set; } = Path.Combine(Path.GetTempPath(), "circuitRF-solves");

    /// <summary>One tree's file, rewritten by <see cref="Update"/> and removed on dispose.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly string _path;
        private readonly Entry _start;
        private bool _gone;

        internal Registration(string path, Entry start) { _path = path; _start = start; Write(start); }

        /// <summary>When the tree started: trees started before it are the ones it yields to.</summary>
        public DateTimeOffset Started => _start.Started;

        internal string FileName => Path.GetFileName(_path);

        /// <summary>Records the tree's footprint now (and with it, that the tree is alive).</summary>
        public void Update(long footprintBytes)
        {
            if (!_gone) Write(_start with { FootprintBytes = footprintBytes });
        }

        private void Write(Entry e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(e));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            _gone = true;
            try { File.Delete(_path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Registers the tree <paramref name="process"/> heads, started as <paramref name="program"/> in
    /// <paramref name="directory"/>.</summary>
    public static Registration Register(Process process, string program, string directory)
    {
        string name = $"{Environment.ProcessId}-{process.Id}-{Guid.NewGuid():N}.json";
        return new Registration(Path.Combine(Root, name),
                                new Entry(Path.GetFileNameWithoutExtension(program), directory, DateTimeOffset.UtcNow, 0));
    }

    /// <summary>Every live tree but <paramref name="self"/>; files no longer rewritten are removed.</summary>
    public static IReadOnlyList<Entry> Others(Registration? self = null)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(Root)) return list;
        string[] files;
        try { files = Directory.GetFiles(Root, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (string f in files)
        {
            if (self is not null && Path.GetFileName(f) == self.FileName) continue;
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > FreshFor) { File.Delete(f); continue; }
                if (JsonSerializer.Deserialize<Entry>(File.ReadAllText(f)) is { } e) list.Add(e);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return list;
    }

    /// <summary>What the trees started before <paramref name="self"/> hold now: the memory it has to leave them.</summary>
    public static long OlderFootprint(Registration self)
        => Others(self).Where(e => e.Started < self.Started).Sum(e => e.FootprintBytes);

    /// <summary>
    /// The warning a run gets before it starts when other solves are already running here, or null when none are. The size
    /// check counts the whole machine for each run, so it is the only place the caller learns of the others before one of them
    /// is stopped.
    /// </summary>
    public static string? RunningWarning(long physicalBytes)
    {
        var others = Others();
        if (others.Count == 0) return null;
        long held = others.Sum(e => e.FootprintBytes);
        string each = string.Join("; ", others.Select(e =>
            $"{e.Program} in {e.Directory}, {MachineMemory.Format(e.FootprintBytes)}, for {Took(DateTimeOffset.UtcNow - e.Started)}"));
        string count = others.Count == 1 ? "Another EM solve is" : $"{others.Count.ToString(CultureInfo.InvariantCulture)} other EM solves are";
        return $"{count} already running on this machine ({each}), holding {MachineMemory.Format(held)}" +
               (physicalBytes > 0 ? $" of its {MachineMemory.Format(physicalBytes)}" : "") +
               ". Each solve uses every core and most of the memory it can; this one is stopped if together they reach the " +
               "machine's memory. Run one EM solve at a time.";
    }

    private static string Took(TimeSpan t)
        => t.TotalMinutes >= 1 ? $"{Math.Floor(t.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min"
                               : $"{Math.Max(1, (int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s";
}
