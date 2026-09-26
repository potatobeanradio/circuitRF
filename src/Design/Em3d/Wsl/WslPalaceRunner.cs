// brief-em3d-26 R-em3d26-2 — Palace, run inside the user's Linux subsystem distribution.
//
// The run is STAGED in the distribution's own Linux filesystem, never on the Windows drive it mounts
// (em-3d.md §7.4): file access across that boundary is slow, and /mnt/c is where the failed build report
// in Palace's tracker was building. The Windows run directory stays the one series 1 defined and holds
// everything — the mesh, the configuration, the log, and every file Palace wrote that circuitRF reads — so
// a result does not depend on where it was computed (§7.3).
//
//   Windows run dir ── model.msh, config.json ──▶ ~/.circuitrf/runs/<run key>/   (the \\wsl.localhost\ share)
//                                                 Palace runs there, its output relayed on stdout
//   Windows run dir ◀── postpro: CSVs, palace.json, requested field files ──
//
// The Linux run directory is circuitRF's scratch space and is removed when the leg ends.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>R-em3d26-2a — what one subsystem run copies, and where it runs.</summary>
/// <param name="LinuxRunDirectory">The staging directory inside the distribution.</param>
/// <param name="CopyIn">The run directory's files that go in, by name: the mesh and the configuration, only.</param>
/// <param name="PidFile">Where the process-group wrapper writes the group id.</param>
public sealed record WslStagingPlan(string LinuxRunDirectory, IReadOnlyList<string> CopyIn, string PidFile);

/// <summary>Palace in a Linux subsystem distribution (brief-em3d-26).</summary>
internal sealed class WslPalaceRunner(WslSession session, string linuxHome, string? mpirun, string mpiHow) : IPalaceRunner
{
    /// <summary>The runs directory under the distribution's home.</summary>
    public const string RunsDirectory = ".circuitrf/runs";

    /// <summary>The wrapper's pid file, in the staging directory.</summary>
    public const string PidFile = "circuitrf.pid";

    private readonly HashSet<string> _staged = new(StringComparer.Ordinal);

    /// <summary>The MPI launcher inside the distribution, and how it was found.</summary>
    public string? MpiLauncher => mpirun;

    public PhysicalCoreReading Cores => session.Cores();

    /// <summary>The run key: a hash of the Windows run directory, so two setups never share a staging directory.</summary>
    public static string RunKey(string windowsRunDir)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(windowsRunDir).TrimEnd('\\', '/'))))[..16];

    /// <summary>The staging plan for <paramref name="windowsRunDir"/>. Pure, which is what gate 4 reads.</summary>
    public static WslStagingPlan Plan(string linuxHome, string windowsRunDir)
    {
        string dir = WslPaths.Combine(linuxHome, RunsDirectory, RunKey(windowsRunDir));
        return new(dir, [GmshGeoWriter.MeshFile, PalaceConfigWriter.ConfigFile], WslPaths.Combine(dir, PidFile));
    }

    /// <summary>
    /// R-em3d26-2a step 4 — which of Palace's output files come back, by their path under <c>postpro/</c>:
    /// every CSV; <c>palace.json</c> (top level and each adaptive pass's), which the completion summary reads
    /// the element counts from; and whatever lies under a field directory the setup asked for (brief 29).
    /// Nothing else — a mesh archive per refinement pass is Palace's working state, not a result.
    /// </summary>
    public static IReadOnlyList<string> CopyOut(IEnumerable<string> postproFiles, IReadOnlyList<string> fieldDirectories)
        => postproFiles.Select(f => f.Replace('\\', '/'))
                       .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                                   || WslPaths.Name(f) == "palace.json"
                                   || fieldDirectories.Any(d => f.StartsWith(d.TrimEnd('/') + "/", StringComparison.Ordinal)))
                       .Order(StringComparer.Ordinal).ToList();

    /// <summary>The field directories under <c>postpro/</c> the setup asked Palace to write. None until
    /// brief 29 asks Palace for fields; the copy-out already honours the list.</summary>
    public IReadOnlyList<string> FieldDirectories { get; init; } = [];

    /// <summary>The grace period between TERM and KILL (a seam for the gate).</summary>
    internal TimeSpan Grace { get; init; } = WslProcessGroup.Grace;

    public PalaceStep Solve(string runDir, string configJson, string palace, int processes, RunControl? control,
                            CancellationToken ct, out string? note, PalaceStageTracker? tracker, long physicalBytes)
    {
        note = null;
        File.WriteAllText(Path.Combine(runDir, PalaceConfigWriter.ConfigFile), configJson, new UTF8Encoding(false));
        // R-em3d7-4e — nothing from an earlier run may be read as this one's.
        string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        if (Directory.Exists(post)) Directory.Delete(post, recursive: true);

        var plan = Plan(linuxHome, runDir);
        if (Stage(runDir, plan) is { } stageFailure) return new(false, false, false, stageFailure);

        if (processes > 1 && mpirun is null)
            note = $"Palace ran as one process: {processes} were asked for, and {mpiHow}.";
        var (exe, args) = PalaceRun.PalaceCommand(palace, processes, processes > 1 ? mpirun : null);

        if (tracker is not null) tracker.Begin();
        else control?.BeginStage("solving (Palace, in the Linux subsystem)", 0, "lines");
        var run = Start(plan, plan.LinuxRunDirectory, Path.Combine(runDir, PalaceRun.PalaceLogFile),
                        [exe, .. args], control, ct, new ProcessWatch(tracker is null ? null : tracker.Line, 0));
        if (run.Cancelled) return new(false, true, false, null);
        if (run.StartFailure is { } why) return new(false, false, false, $"Palace could not be started in the Linux subsystem ({why}).");
        if (run.ExitCode != 0)
            return new(false, false, false,
                $"Palace failed in the Linux subsystem distribution '{session.Distribution}' (exit code {run.ExitCode}). " +
                $"Its own words: {Quote(run.Tail)} The full log is {Path.Combine(runDir, PalaceRun.PalaceLogFile)}.");

        if (CopyBack(runDir, plan) is { } copyFailure) return new(false, false, false, copyFailure);
        return PalaceStep.Done();
    }

    public IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, string palace, double topHz,
                                                      IReadOnlyList<int> wavePorts, CancellationToken ct, out string? note)
    {
        var plan = Plan(linuxHome, runDir);
        return PalaceRun.SecondModes(runDir, configJson, topHz, wavePorts, out note, (dir, watch) =>
        {
            // The mode-2 configuration names ../model.msh, which the staging directory already holds.
            if (Stage(runDir, plan) is { } failure) return new PalaceRun.ProcessRun(-1, false, failure, []);
            string linuxDir = WslPaths.Combine(plan.LinuxRunDirectory, PalaceRun.SecondModeDirectory);
            var mkdir = session.Exec(["mkdir", "-p", linuxDir]);
            if (!mkdir.Ok) return new PalaceRun.ProcessRun(-1, false, mkdir.Failure ?? mkdir.Message, []);
            try
            {
                File.Copy(Path.Combine(dir, PalaceConfigWriter.ConfigFile),
                          session.ToWindows(WslPaths.Combine(linuxDir, PalaceConfigWriter.ConfigFile)), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new PalaceRun.ProcessRun(-1, false, e.Message, []);
            }
            bool bare = palace.EndsWith(".bin", StringComparison.Ordinal);
            return Start(plan, linuxDir, Path.Combine(dir, PalaceRun.PalaceLogFile),
                         bare ? [palace, PalaceConfigWriter.ConfigFile] : [palace, "--serial", PalaceConfigWriter.ConfigFile],
                         null, ct, watch);
        });
    }

    /// <summary>A fresh staging directory holding exactly the plan's copy-in files. Once per run directory
    /// per leg: the second-mode check reuses what the solve staged.</summary>
    private string? Stage(string runDir, WslStagingPlan plan)
    {
        if (_staged.Contains(plan.LinuxRunDirectory)) return null;
        session.RemoveTree(plan.LinuxRunDirectory);
        var mkdir = session.Exec(["mkdir", "-p", plan.LinuxRunDirectory]);
        if (!mkdir.Ok)
            return $"The run could not be staged in the Linux subsystem distribution '{session.Distribution}' " +
                   $"({mkdir.Failure ?? mkdir.Message}).";
        try
        {
            foreach (string file in plan.CopyIn)
                File.Copy(Path.Combine(runDir, file), session.ToWindows(WslPaths.Combine(plan.LinuxRunDirectory, file)), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The mesh and configuration could not be copied into the Linux subsystem distribution " +
                   $"'{session.Distribution}' ({e.Message}).";
        }
        _staged.Add(plan.LinuxRunDirectory);
        return null;
    }

    /// <summary>Palace's output files, copied back into the Windows run directory's <c>postpro/</c>.</summary>
    private string? CopyBack(string runDir, WslStagingPlan plan)
    {
        string from = session.ToWindows(WslPaths.Combine(plan.LinuxRunDirectory, PalaceConfigWriter.OutputDirectory));
        try
        {
            if (!Directory.Exists(from)) return null;   // ReadPortS then names the missing file, as natively
            var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                                 .Select(f => Path.GetRelativePath(from, f)).ToList();
            foreach (string rel in CopyOut(files, FieldDirectories))
            {
                string dest = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(from, rel.Replace('/', Path.DirectorySeparatorChar)), dest, overwrite: true);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Palace finished in the Linux subsystem, but its results could not be copied back ({e.Message}).";
        }
    }

    /// <summary>One Palace process inside the distribution, as a process-group leader, streamed through
    /// the run service's own reader; cancelling stops the group, then <c>wsl.exe</c>.</summary>
    private PalaceRun.ProcessRun Start(WslStagingPlan plan, string linuxCwd, string logPath, IReadOnlyList<string> argv,
                                       RunControl? control, CancellationToken ct, ProcessWatch watch)
    {
        string pidFile = WslPaths.Combine(linuxCwd, PidFile);
        var psi = session.Wsl.StartInfo(session.Arguments(
            WslProcessGroup.Wrap(pidFile, ["env", "OMP_NUM_THREADS=1", .. argv]), linuxCwd));
        PalaceRun.CountPalace();
        return PalaceRun.RunProcess(psi, logPath, Em3dProcessKind.Solver, control, ct, watch,
                                    kill: _ => WslProcessGroup.Stop(session, pidFile, Grace), sampleMemory: false);
    }

    private static string Quote(IReadOnlyList<string> tail)
    {
        var lines = tail.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var marked = lines.Where(l => l.Contains("rror", StringComparison.Ordinal)).TakeLast(5).ToList();
        var chosen = marked.Count > 0 ? marked : lines.TakeLast(8).ToList();
        return chosen.Count == 0 ? "(it printed nothing)" : "“" + string.Join(" | ", chosen) + "”";
    }

    /// <summary>The Linux run directories this leg staged are circuitRF's scratch space, and go with it.</summary>
    public void Dispose()
    {
        foreach (string dir in _staged)
            try { session.RemoveTree(dir); } catch (ArgumentException) { }
        _staged.Clear();
    }
}
