// brief-em3d-7 R-em3d7-3, R-em3d7-4c-e — the two programs a Palace run starts, and what is read back.
//
// Stage, run, check, read. Each program is started from the ABSOLUTE path discovery resolved (brief 6),
// in the run directory, through Em3dProcessLauncher (the one door, which counts), with its output
// logged beside the files it made. Cancellation kills the whole process TREE — the Palace wrapper is
// a shell script that starts mpirun, which starts the ranks, and killing only the script would leave
// eight solvers running with nobody to report to.
//
// Two rules decide what a failure looks like:
//   * The EXIT CODE is the only signal (F0 Q5: a .geo syntax error still leaves Gmsh writing a mesh and
//     exiting 1; after some it segfaults). A mesh file's presence proves nothing, and nothing here
//     retries or falls back (R-em3d7-3d).
//   * A run that did not finish produces no result (R-em3d7-4e). Palace's output directory is emptied
//     before it starts, so a port-S.csv left by an earlier or a killed run is never read as this one's.

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;

namespace CircuitRF.Design.Em3d;

/// <summary>How one step ended. <see cref="Refused"/> is the entity check's verdict — the geometry,
/// not a program, is at fault; <see cref="Message"/> otherwise carries the program's own words.</summary>
public sealed record PalaceStep(bool Ok, bool Cancelled, bool Refused, string? Message, bool Reused = false)
{
    public static PalaceStep Done(bool reused = false) => new(true, false, false, null, reused);

    /// <summary>brief-em3d-21 R-em3d21-2c — the process tree's highest resident memory seen, bytes,
    /// where this OS lets it be read.</summary>
    public long? PeakResidentBytes { get; init; }

    /// <summary>The one-time note a tree reaching 90 % of physical memory gets.</summary>
    public string? MemoryNote { get; init; }

    /// <summary>A mesh step's tetrahedra, as Gmsh reported creating them — from this run's log, or from
    /// the log the reused mesh was made with. Null when Gmsh printed no count.</summary>
    public long? Tetrahedra { get; init; }
}

/// <summary>
/// brief-em3d-21 — what a running program's output and memory are watched with: a line handler, fed
/// on the RUN's thread (so a cancellation it throws can kill the tree), and the machine's memory for
/// the once-a-second resident sample. Null handler: the stage ticks once per line, as before.
/// </summary>
public sealed record ProcessWatch(Action<string>? OnLine, long PhysicalBytes);

/// <summary>What Palace said about the run it made (<c>postpro/palace.json</c>): the mesh it started
/// from and the one it finished on, and how many adaptive passes lay between.</summary>
public sealed record PalaceRunFacts(long? InitialElements, long? FinalElements, long? DegreesOfFreedom,
                                    int? AdaptiveIterations, string? GitTag);

/// <summary>S read back from <c>port-S.csv</c>: frequencies in Hz, and S[f][i, j] in the order of the
/// port numbers asked for.</summary>
public sealed record PalacePortS(double[] FrequenciesHz, Complex[][,] S);

/// <summary>
/// brief-em3d-23 R-em3d23-4c — one mode of an eigenmode solve, from <c>eig.csv</c>: its complex frequency
/// (Hz), Palace's Q, and the eigensolver's backward and absolute errors.
/// </summary>
public sealed record PalaceMode(int Index, double FrequencyHz, double ImagFrequencyHz, double Q, double BackwardError,
                                double AbsoluteError);

/// <summary>brief-em3d-23 — a wave port's mode as Palace reported it in its log: <c>Port k, mode m: kₙ = a+bi m⁻¹</c>.</summary>
public sealed record PalaceWaveMode(int Port, int Mode, Complex Kn)
{
    /// <summary>
    /// Propagating: kₙ's decay is under a tenth of its phase, so the mode loses under 5.5 dB per wavelength —
    /// a guided mode, whose only decay is its materials' loss (a tanδ/2 of it). An evanescent mode's kₙ is
    /// almost purely imaginary. Between the two is a LEAKY mode of a port face with open (absorbing) edges:
    /// on the 50 Ω microstrip at 10 GHz with an 8-height region Palace's mode 2 was 188.8 − 173.6i m⁻¹,
    /// decay nearly equal to phase — gone within a few millimetres, and not a mode the port can launch.
    /// </summary>
    public bool Propagating => Kn.Real > 0 && Math.Abs(Kn.Imaginary) <= 0.1 * Kn.Real;

    /// <summary>The distance over which the mode's amplitude falls by 1/e, metres (∞ for a lossless one).</summary>
    public double DecayLengthM => Kn.Imaginary == 0 ? double.PositiveInfinity : 1 / Math.Abs(Kn.Imaginary);
}

public static class PalaceRun
{
    public const string MeshHashFile = "model.geo.sha256";
    public const string GmshLogFile  = "gmsh.log";
    public const string PalaceLogFile = "palace.log";
    public const string PortSFile    = "port-S.csv";

    /// <summary>Names the MPI launcher for one process, as <c>CIRCUITRF_PALACE</c> names Palace.</summary>
    public const string MpiLauncherVariable = "CIRCUITRF_MPIRUN";

    /// <summary>The stage a run whose geometry is unchanged shows instead of meshing.</summary>
    public const string MeshReusedLabel = "Mesh reused (the geometry script is unchanged)";

    private static long _gmsh, _palace;

    /// <summary>How many times this process has started Gmsh — gate 5's counter (R-em3d7-3c).</summary>
    public static long GmshInvocations => Interlocked.Read(ref _gmsh);

    /// <summary>How many times this process has started Palace.</summary>
    public static long PalaceInvocations => Interlocked.Read(ref _palace);

    /// <summary>Every mesher and solver process, as it starts — so a test can look for it in the
    /// process table after a cancellation (gate 10).</summary>
    internal static event Action<Process>? ProcessStarted;

    // ── Gmsh ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the script and its groups into <paramref name="runDir"/>, meshes it unless the same
    /// script already has a mesh there (R-em3d7-3c), and checks the entity table against the groups
    /// (R-em3d7-3b) — on a reused mesh too, since the check is the thing that makes a mesh usable.
    /// </summary>
    public static PalaceStep Mesh(string runDir, GmshLowering lowering, string gmsh, RunControl? control,
                                  CancellationToken ct, long physicalBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(lowering);
        if (!lowering.Ok) return new(false, false, true, lowering.Refusal);
        Directory.CreateDirectory(runDir);

        string geoPath  = Path.Combine(runDir, GmshGeoWriter.GeoFile);
        string mshPath  = Path.Combine(runDir, GmshGeoWriter.MeshFile);
        string entPath  = Path.Combine(runDir, GmshGeoWriter.EntitiesFile);
        string hashPath = Path.Combine(runDir, MeshHashFile);
        string logPath  = Path.Combine(runDir, GmshLogFile);
        GmshLogProgress? gmshLog = null;

        WriteText(Path.Combine(runDir, GmshGeoWriter.GroupsFile), lowering.GroupsJson!);
        string hash = Sha256(lowering.Geo!);

        bool reuse = File.Exists(hashPath) && File.Exists(mshPath) && File.Exists(entPath) &&
                     File.ReadAllText(hashPath).Trim() == hash &&
                     File.Exists(geoPath) && File.ReadAllText(geoPath) == lowering.Geo;
        if (!reuse)
        {
            foreach (string stale in new[] { hashPath, mshPath, entPath })
                if (File.Exists(stale)) File.Delete(stale);
            WriteText(geoPath, lowering.Geo!);

            // brief-em3d-21 R-em3d21-1e — Gmsh's own stage lines, not a count of them.
            gmshLog = new GmshLogProgress(control);
            gmshLog.Begin();
            Interlocked.Increment(ref _gmsh);
            var run = RunProcess(gmsh, [GmshGeoWriter.GeoFile, "-3", "-o", GmshGeoWriter.MeshFile], runDir, logPath,
                                 Em3dProcessKind.Mesher, env: null, control, ct, new ProcessWatch(gmshLog.Line, physicalBytes));
            if (run.Cancelled) return new(false, true, false, null);
            if (run.StartFailure is { } why)
                return new(false, false, false, $"Gmsh could not be started ({why}).");
            if (run.ExitCode != 0)
                return new(false, false, false,
                    $"Gmsh failed (exit code {run.ExitCode}). Its own words: {Quote(run.Tail, "Error")} " +
                    $"The full log is {logPath}.");
        }

        var table = File.Exists(entPath) ? GmshGeoWriter.ReadEntities(File.ReadAllText(entPath)) : null;
        if (GmshGeoWriter.CheckEntities(lowering.Groups, table) is { } refusal)
            return new(false, false, true, refusal + $" The script and its entity table are in {runDir}.");

        if (!reuse) WriteText(hashPath, hash + "\n");
        // R-em3d21-1e — a reused mesh is said, not shown as a meshing stage. Its size is the log it was
        // made with, read by the same reader.
        if (reuse)
        {
            control?.BeginStage(MeshReusedLabel);
            gmshLog = new GmshLogProgress(null);
            if (File.Exists(logPath)) foreach (string line in File.ReadLines(logPath)) gmshLog.Line(line);
        }
        return PalaceStep.Done(reuse) with { Tetrahedra = gmshLog?.Tetrahedra };
    }

    // ── Palace ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the configuration and runs Palace on <paramref name="processes"/> processes (the setup's
    /// existing core setting, so one knob means one thing — R-em3d7-4c). With more than one it goes
    /// through MPI; <paramref name="note"/> says so when no launcher was found and it ran as one.
    /// </summary>
    /// <param name="tracker">Reads Palace's log into stages (brief-em3d-21). Null: the stage ticks
    /// once per line, as it did before.</param>
    public static PalaceStep Solve(string runDir, string configJson, string palace, int processes,
                                   RunControl? control, CancellationToken ct, out string? note,
                                   PalaceStageTracker? tracker = null, long physicalBytes = 0)
    {
        note = null;
        WriteText(Path.Combine(runDir, PalaceConfigWriter.ConfigFile), configJson);

        // R-em3d7-4e — nothing from an earlier run may be read as this one's.
        string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        if (Directory.Exists(post)) Directory.Delete(post, recursive: true);

        string? mpirun = null;
        if (processes > 1)
        {
            var launcher = FindMpiLauncher(palace);
            mpirun = launcher.Path;
            if (mpirun is null)
                note = $"Palace ran as one process: {processes} were asked for, and {launcher.How}.";
        }

        var (exe, args) = PalaceCommand(palace, processes, mpirun);

        if (tracker is not null) tracker.Begin();
        else control?.BeginStage("solving (Palace)", 0, "lines");
        Interlocked.Increment(ref _palace);
        var run = RunProcess(exe, args, runDir, Path.Combine(runDir, PalaceLogFile), Em3dProcessKind.Solver,
                             env: new Dictionary<string, string> { ["OMP_NUM_THREADS"] = "1" }, control, ct,
                             new ProcessWatch(tracker is null ? null : tracker.Line, physicalBytes));
        if (run.Cancelled) return new(false, true, false, null);
        if (run.StartFailure is { } why) return new(false, false, false, $"Palace could not be started ({why}).");
        if (run.ExitCode != 0)
            return new(false, false, false,
                $"Palace failed (exit code {run.ExitCode}). Its own words: {Quote(run.Tail, "rror")} " +
                $"The full log is {Path.Combine(runDir, PalaceLogFile)}.");
        return PalaceStep.Done() with { PeakResidentBytes = run.PeakResident, MemoryNote = run.MemoryNote };
    }

    /// <summary>
    /// The program and arguments that run Palace on <paramref name="processes"/> ranks: the <c>palace</c>
    /// wrapper takes <c>-np</c> and <c>--launcher</c>; a bare <c>palace-&lt;arch&gt;.bin</c> is started by
    /// <c>mpirun -n</c>; with no launcher, the wrapper runs <c>--serial</c>. Shared with the Linux subsystem
    /// route (brief-em3d-26), which runs the same command inside a distribution.
    /// </summary>
    internal static (string Exe, List<string> Args) PalaceCommand(string palace, int processes, string? mpirun)
    {
        bool bareBinary = palace.EndsWith(".bin", StringComparison.Ordinal);
        string exe = palace;
        var args = new List<string>();
        if (mpirun is not null && bareBinary)
        {
            exe = mpirun;
            args.AddRange(["-n", processes.ToString(CultureInfo.InvariantCulture), palace]);
        }
        else if (mpirun is not null)
            args.AddRange(["-np", processes.ToString(CultureInfo.InvariantCulture), "--launcher", mpirun]);
        else if (!bareBinary)
            args.Add("--serial");
        args.Add(PalaceConfigWriter.ConfigFile);
        return (exe, args);
    }

    /// <summary>The <c>mpirun</c> named in Settings ▸ 3D EM, or null. Installed by <c>src/Ui</c>
    /// (<c>Em3dSolverPathInstaller</c>), read when needed — the seam <see cref="SolverDiscovery.PreferredCommand"/> is.</summary>
    public static Func<string?>? PreferredMpiLauncher { get; set; }

    /// <summary>An MPI launcher, or null <see cref="Path"/> with <see cref="How"/> saying what was tried.</summary>
    public sealed record MpiLauncher(string? Path, string How);

    /// <summary>
    /// The MPI launcher Palace runs under. In order: the one named in Settings, then
    /// <see cref="MpiLauncherVariable"/> — a NAMED launcher that is missing is reported, never silently
    /// replaced, as <see cref="SolverDiscovery.Find"/> treats a named program — then an <c>mpirun</c>
    /// beside Palace (a Spack view puts it there), then <b>the MPI this Palace was built against</b>,
    /// from the Spack database (<see cref="SpackInstalls.MpiLauncherFor"/>; Palace's own recipe has no
    /// view, so this is where its <c>mpirun</c> is), then <c>PATH</c>, then <c>~/.local/bin</c>,
    /// <c>/opt/homebrew/bin</c>, <c>/usr/local/bin</c> — a Finder-launched application's <c>PATH</c>
    /// holds none of them. The Spack match outranks <c>PATH</c> because a different MPI's launcher can
    /// start the ranks and then fail to connect them.
    /// </summary>
    public static MpiLauncher FindMpiLauncher(string palace, IReadOnlyList<string>? spackRoots = null)
    {
        if (PreferredMpiLauncher?.Invoke()?.Trim() is { Length: > 0 } preferred)
            return File.Exists(preferred)
                ? new(System.IO.Path.GetFullPath(preferred), "set in Settings")
                : new(null, $"the MPI launcher set in Settings ('{preferred}') does not exist");
        if (Environment.GetEnvironmentVariable(MpiLauncherVariable)?.Trim() is { Length: > 0 } named)
            return File.Exists(named)
                ? new(System.IO.Path.GetFullPath(named), $"named by {MpiLauncherVariable}")
                : new(null, $"the MPI launcher named by {MpiLauncherVariable} ('{named}') does not exist");

        if (System.IO.Path.GetDirectoryName(palace) is { } dir && System.IO.Path.Combine(dir, "mpirun") is var beside
            && File.Exists(beside))
            return new(beside, "found beside Palace");
        // brief-em3d-24 — a Palace the install assistant built lives in a Spack tree of its own inside its home,
        // which no default root names; its install record does.
        if (InstalledSpackTree(palace) is { } tree && SpackInstalls.MpiLauncherFor(palace, [tree]) is { } own)
            return new(own, "the MPI Palace was built with, from the Spack tree circuitRF installed it in");
        if (SpackInstalls.MpiLauncherFor(palace, spackRoots) is { } linked)
            return new(linked, "the MPI Palace was built with, from its Spack installation");

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
        if (!OperatingSystem.IsWindows())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0) dirs.Add(System.IO.Path.Combine(home, ".local", "bin"));
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        }
        foreach (string d in dirs)
        {
            string candidate;
            try { candidate = System.IO.Path.Combine(d, "mpirun"); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate)) return new(candidate, "found on PATH or in a default directory");
        }
        return new(null, $"no MPI launcher (mpirun) was found in Settings, {MpiLauncherVariable}, beside Palace, " +
                         "in Palace's Spack installation, on PATH or in the default directories");
    }

    /// <summary>The Spack install tree of the circuitRF-installed home <paramref name="palace"/> lives in, or null.</summary>
    private static string? InstalledSpackTree(string palace)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(palace); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException) { return null; }
        return Install.SolverHomes.Published(SolverTool.Palace, Install.SolverHomes.DefaultRoots)
            .FirstOrDefault(r => r.SpackInstallTree is not null
                                 && full.StartsWith(r.Home.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            ?.SpackInstallTree;
    }

    // ── Reading back ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>R-em3d7-4d — by column NAME, never by position.</b> Palace writes <c>|S[i][j]| (dB)</c> and
    /// <c>arg(S[i][j]) (deg.)</c> per port pair, with <c>j</c> the excitation, which PalaceConfigWriter
    /// makes the port's own number. A missing column is an error naming it.
    /// </summary>
    public static PalacePortS ReadPortS(string csvPath, IReadOnlyList<int> ports, out string? error)
    {
        error = null;
        var empty = new PalacePortS([], []);
        if (!File.Exists(csvPath))
        {
            error = $"Palace finished but wrote no {PortSFile} ({csvPath}).";
            return empty;
        }
        var lines = File.ReadAllLines(csvPath).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2)
        {
            error = $"Palace's {PortSFile} holds no frequency ({csvPath}).";
            return empty;
        }
        var header = lines[0].Split(',').Select(h => h.Trim()).ToList();
        string? missing = null;
        int Col(string name)
        {
            int k = header.IndexOf(name);
            if (k < 0) missing ??= $"Palace's {PortSFile} has no column '{name}' ({csvPath}).";
            return k;
        }
        int fCol = Col("f (GHz)");
        int n = ports.Count;
        var mag = new int[n, n];
        var arg = new int[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                mag[i, j] = Col($"|S[{ports[i]}][{ports[j]}]| (dB)");
                arg[i, j] = Col($"arg(S[{ports[i]}][{ports[j]}]) (deg.)");
            }
        if (missing is not null)
        {
            error = missing;
            return empty;
        }

        var freqs = new List<double>();
        var mats  = new List<Complex[,]>();
        for (int r = 1; r < lines.Count; r++)
        {
            var cells = lines[r].Split(',');
            double Cell(int k) => k < cells.Length &&
                double.TryParse(cells[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
            double f = Cell(fCol);
            var s = new Complex[n, n];
            bool ok = !double.IsNaN(f);
            for (int i = 0; i < n && ok; i++)
                for (int j = 0; j < n && ok; j++)
                {
                    double db = Cell(mag[i, j]), deg = Cell(arg[i, j]);
                    ok = !double.IsNaN(db) && !double.IsNaN(deg);
                    s[i, j] = Complex.FromPolarCoordinates(Math.Pow(10, db / 20), deg * Math.PI / 180);
                }
            if (!ok)
            {
                error = $"Row {r + 1} of Palace's {PortSFile} is not all numbers ({csvPath}).";
                return empty;
            }
            freqs.Add(f * 1e9);
            mats.Add(s);
        }
        return new PalacePortS([.. freqs], [.. mats]);
    }

    /// <summary>brief-em3d-22 — the electrostatic solve's Maxwell capacitance matrix, farads.</summary>
    public const string CapacitanceFile = "terminal-C.csv";

    /// <summary>The electrostatic solve's mutual (lumped-circuit) capacitance matrix, farads.</summary>
    public const string MutualCapacitanceFile = "terminal-Cm.csv";

    /// <summary>The magnetostatic solve's inductance matrix, henries.</summary>
    public const string InductanceFile = "terminal-M.csv";

    /// <summary>The magnetostatic solve's current-difference form of it, henries.</summary>
    public const string MutualInductanceFile = "terminal-Mm.csv";

    /// <summary>
    /// <b>R-em3d22-3b — a terminal matrix, by column NAME.</b> Palace 0.18.1 writes a column
    /// <c>i</c> (the row's terminal index) and one column per terminal, headed
    /// <c>&lt;symbol&gt;[i][&lt;index&gt;] (&lt;unit&gt;)</c> — <c>C[i][2] (F)</c>, <c>C_m[i][2] (F)</c>,
    /// <c>M[i][2] (H)</c> (read from its source, electrostaticsolver.cpp / magnetostaticsolver.cpp, and
    /// from the runs committed under testdata/em3d/static/). Rows are matched by their <c>i</c> value,
    /// never by position. <paramref name="indices"/> gives the matrix's order.
    /// </summary>
    public static double[,]? ReadTerminalMatrix(string csvPath, string symbol, string unit, IReadOnlyList<int> indices,
                                                out string? error)
    {
        error = null;
        string file = Path.GetFileName(csvPath);
        if (!File.Exists(csvPath)) { error = $"Palace finished but wrote no {file} ({csvPath})."; return null; }
        var lines = File.ReadAllLines(csvPath).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) { error = $"Palace's {file} holds no row ({csvPath})."; return null; }
        var header = lines[0].Split(',').Select(h => h.Trim()).ToList();
        int iCol = header.IndexOf("i");
        if (iCol < 0) { error = $"Palace's {file} has no column 'i' ({csvPath})."; return null; }
        int n = indices.Count;
        var cols = new int[n];
        for (int j = 0; j < n; j++)
        {
            string name = $"{symbol}[i][{indices[j]}] {unit}";
            cols[j] = header.IndexOf(name);
            if (cols[j] < 0) { error = $"Palace's {file} has no column '{name}' ({csvPath})."; return null; }
        }
        var rowOf = new Dictionary<int, string[]>();
        foreach (string line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (iCol < cells.Length &&
                double.TryParse(cells[iCol].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double iv))
                rowOf[(int)Math.Round(iv)] = cells;
        }
        var m = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            if (!rowOf.TryGetValue(indices[i], out var cells))
            {
                error = $"Palace's {file} has no row for terminal {indices[i]} ({csvPath}).";
                return null;
            }
            for (int j = 0; j < n; j++)
            {
                if (cols[j] >= cells.Length ||
                    !double.TryParse(cells[cols[j]].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    error = $"Palace's {file} row for terminal {indices[i]} is not all numbers ({csvPath}).";
                    return null;
                }
                m[i, j] = v;
            }
        }
        return m;
    }

    // ── brief-em3d-23: wave ports and eigenmodes ───────────────────────────────────────────────

    /// <summary>A wave port's mode impedance, Z_PV, per frequency (written because each port states a voltage path).</summary>
    public const string PortZFile = "port-Z.csv";

    /// <summary>An eigenmode solve's modes.</summary>
    public const string EigFile = "eig.csv";

    /// <summary>An eigenmode solve's external Q per lumped port (a port with a resistance).</summary>
    public const string PortQFile = "port-Q.csv";

    /// <summary>An eigenmode solve's energy per postprocessing domain, and each domain's participation.</summary>
    public const string DomainEnergyFile = "domain-E.csv";

    /// <summary>
    /// <b>R-em3d23-2d — a wave port's mode impedance, by column NAME.</b> Palace 0.18.1 writes
    /// <c>f (GHz)</c>, <c>Re{Z_PV[i]} (Ohm)</c> and <c>Im{Z_PV[i]} (Ohm)</c> per wave port that states a voltage
    /// path (postoperatorcsv.cpp, InitializePortZ; its own regression reference coaxial/lumped_wave/port-Z.csv).
    /// Z_PV = |V|²/P over the unit-power mode — the impedance the power-normalised modal S is referred to.
    /// Returns Z[f][k] in <paramref name="ports"/>' order.
    /// </summary>
    public static Complex[][]? ReadPortZ(string csvPath, IReadOnlyList<int> ports, out double[] frequenciesHz, out string? error)
    {
        frequenciesHz = [];
        var table = ReadTable(csvPath, out error);
        if (table is null) return null;
        var (header, rows) = table.Value;
        int fCol = header.IndexOf("f (GHz)");
        if (fCol < 0) { error = $"Palace's {PortZFile} has no column 'f (GHz)' ({csvPath})."; return null; }
        var re = new int[ports.Count];
        var im = new int[ports.Count];
        for (int k = 0; k < ports.Count; k++)
        {
            re[k] = header.IndexOf($"Re{{Z_PV[{ports[k]}]}} (Ohm)");
            im[k] = header.IndexOf($"Im{{Z_PV[{ports[k]}]}} (Ohm)");
            if (re[k] < 0 || im[k] < 0)
            {
                error = $"Palace's {PortZFile} has no column 'Re{{Z_PV[{ports[k]}]}} (Ohm)' ({csvPath}).";
                return null;
            }
        }
        var f = new List<double>();
        var z = new List<Complex[]>();
        foreach (var cells in rows)
        {
            double Cell(int c) => c < cells.Length && double.TryParse(cells[c].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
            var row = new Complex[ports.Count];
            for (int k = 0; k < ports.Count; k++) row[k] = new Complex(Cell(re[k]), Cell(im[k]));
            if (double.IsNaN(Cell(fCol)) || row.Any(c => double.IsNaN(c.Real) || double.IsNaN(c.Imaginary)))
            {
                error = $"A row of Palace's {PortZFile} is not all numbers ({csvPath}).";
                return null;
            }
            f.Add(Cell(fCol) * 1e9);
            z.Add(row);
        }
        frequenciesHz = [.. f];
        return [.. z];
    }

    /// <summary>
    /// <b>R-em3d23-4c — the modes, by column NAME.</b> Palace 0.18.1 writes <c>m</c>, <c>Re{f} (GHz)</c>,
    /// <c>Im{f} (GHz)</c>, <c>Q</c>, <c>Error (Bkwd.)</c>, <c>Error (Abs.)</c> (postoperatorcsv.cpp,
    /// InitializeEig; the run committed under testdata/em3d/eigen/).
    /// </summary>
    public static IReadOnlyList<PalaceMode>? ReadModes(string csvPath, out string? error)
    {
        var table = ReadTable(csvPath, out error);
        if (table is null) return null;
        var (header, rows) = table.Value;
        string[] names = ["m", "Re{f} (GHz)", "Im{f} (GHz)", "Q", "Error (Bkwd.)", "Error (Abs.)"];
        var cols = names.Select(n => header.IndexOf(n)).ToArray();
        for (int k = 0; k < names.Length; k++)
            if (cols[k] < 0) { error = $"Palace's {EigFile} has no column '{names[k]}' ({csvPath})."; return null; }
        var modes = new List<PalaceMode>();
        foreach (var cells in rows)
        {
            var v = cols.Select(c => c < cells.Length &&
                double.TryParse(cells[c].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : double.NaN).ToArray();
            if (v.Take(3).Any(double.IsNaN)) { error = $"A row of Palace's {EigFile} is not all numbers ({csvPath})."; return null; }
            modes.Add(new PalaceMode((int)Math.Round(v[0]), v[1] * 1e9, v[2] * 1e9, v[3], v[4], v[5]));
        }
        return modes;
    }

    /// <summary>
    /// Columns of a per-mode table (<c>port-Q.csv</c>'s <c>Q_ext[i]</c>, <c>domain-E.csv</c>'s <c>p_elec[k]</c>)
    /// by NAME, rows matched to modes by their <c>m</c> value. Null (with no error) when the file is absent —
    /// Palace writes neither when there is nothing to report.
    /// </summary>
    public static double[,]? ReadModeColumns(string csvPath, IReadOnlyList<string> columns, IReadOnlyList<int> modes, out string? error)
    {
        error = null;
        if (!File.Exists(csvPath)) return null;
        var table = ReadTable(csvPath, out error);
        if (table is null) return null;
        var (header, rows) = table.Value;
        int mCol = header.IndexOf("m");
        var cols = columns.Select(c => header.IndexOf(c)).ToArray();
        if (mCol < 0 || cols.Any(c => c < 0))
        {
            error = $"Palace's {Path.GetFileName(csvPath)} has no column '{(mCol < 0 ? "m" : columns[Array.IndexOf(cols, -1)])}' ({csvPath}).";
            return null;
        }
        var byMode = new Dictionary<int, string[]>();
        foreach (var cells in rows)
            if (mCol < cells.Length && double.TryParse(cells[mCol].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m))
                byMode[(int)Math.Round(m)] = cells;
        var result = new double[modes.Count, columns.Count];
        for (int i = 0; i < modes.Count; i++)
            for (int j = 0; j < columns.Count; j++)
                result[i, j] = byMode.TryGetValue(modes[i], out var cells) && cols[j] < cells.Length &&
                               double.TryParse(cells[cols[j]].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    ? v : double.NaN;
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex WaveModeLine = new(
        @"^\s*Port (\d+), mode (\d+): k\S* = ([-+]?\d+(?:\.\d+)?(?:e[-+]\d+)?)([-+]\d+(?:\.\d+)?(?:e[-+]\d+)?)i m", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// brief-em3d-23 — a wave port's mode line (waveportoperator.cpp, 0.18.1):
    /// <c> Port 1, mode 1: kₙ = 1.234e+02+5.000e-03i m⁻¹</c>, with <c>, Z_PV = …</c> after it when the port
    /// states a voltage path. Null for any other line.
    /// </summary>
    public static PalaceWaveMode? ParseWaveMode(string line)
    {
        var m = WaveModeLine.Match(line);
        if (!m.Success) return null;
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new PalaceWaveMode(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                  int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                                  new Complex(D(m.Groups[3].Value), D(m.Groups[4].Value)));
    }

    /// <summary>
    /// <b>R-em3d23-3 — is each wave port's SECOND mode propagating at the top of the band?</b> Palace 0.18.1
    /// reports only the mode a port is set to (its log's <c>Port k, mode m: kₙ</c> line; the mode solver's
    /// own print level is fixed at 0), so the only way to hear about mode 2 from Palace itself is to ask for
    /// it: this runs Palace on the same mesh with every wave port set to mode 2 at the sweep's top frequency,
    /// and stops it — killing the tree — as soon as every port has printed that line, which Palace does
    /// while it sets up the ports, before the 3D solve. Null when it could not be asked (the reason is in
    /// <paramref name="note"/>); nothing here fails the run.
    /// </summary>
    public static IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, string palace, double topHz,
                                                            IReadOnlyList<int> wavePorts, CancellationToken ct, out string? note)
        => SecondModes(runDir, configJson, topHz, wavePorts, out note, (dir, watch) =>
        {
            bool bareBinary = palace.EndsWith(".bin", StringComparison.Ordinal);
            Interlocked.Increment(ref _palace);
            return RunProcess(palace, bareBinary ? [PalaceConfigWriter.ConfigFile] : ["--serial", PalaceConfigWriter.ConfigFile],
                              dir, Path.Combine(dir, PalaceLogFile), Em3dProcessKind.Solver,
                              env: new Dictionary<string, string> { ["OMP_NUM_THREADS"] = "1" }, null, ct, watch);
        });

    /// <summary>The directory, under the run directory, the second-mode check runs in.</summary>
    public const string SecondModeDirectory = "mode2";

    /// <summary>
    /// The check itself, with the process run supplied: <paramref name="run"/> is given the Windows-side
    /// <c>mode2</c> directory (the configuration is already written there) and the watcher that stops it.
    /// brief-em3d-26 runs the same check inside a Linux subsystem distribution through this.
    /// </summary>
    internal static IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, double topHz,
                                                              IReadOnlyList<int> wavePorts, out string? note,
                                                              Func<string, ProcessWatch, ProcessRun> run)
    {
        note = null;
        string dir = Path.Combine(runDir, SecondModeDirectory);
        try
        {
            Directory.CreateDirectory(dir);
            var root = System.Text.Json.Nodes.JsonNode.Parse(configJson)!.AsObject();
            root["Problem"]!["Output"] = "postpro";
            root["Model"]!["Mesh"] = "../" + GmshGeoWriter.MeshFile;
            root["Model"]!.AsObject().Remove("Refinement");
            foreach (var wp in root["Boundaries"]!["WavePort"]!.AsArray()) wp!["Mode"] = 2;
            var driven = root["Solver"]!["Driven"]!.AsObject();
            driven["Samples"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["Type"] = "Point",
                ["Freq"] = new System.Text.Json.Nodes.JsonArray(topHz / 1e9),
            });
            driven.Remove("AdaptiveTol");
            WriteText(Path.Combine(dir, PalaceConfigWriter.ConfigFile), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException)
        {
            note = $"the second-mode check could not be staged ({e.Message})";
            return null;
        }

        var seen = new Dictionary<int, PalaceWaveMode>();
        void OnLine(string line)
        {
            if (ParseWaveMode(line) is { Mode: 2 } m) seen[m.Port] = m;
            if (wavePorts.All(seen.ContainsKey)) throw new OperationCanceledException();
        }
        var result = run(dir, new ProcessWatch(OnLine, 0));
        if (wavePorts.All(seen.ContainsKey)) return [.. wavePorts.Select(p => seen[p])];
        note = result.Cancelled ? "the second-mode check was cancelled"
             : result.StartFailure is { } why ? $"the second-mode check could not start Palace ({why})"
             : $"Palace printed no mode-2 line for every wave port (exit {result.ExitCode}; its log is {Path.Combine(dir, PalaceLogFile)})";
        return null;
    }

    /// <summary>Counts one Palace start made outside this class (the Linux subsystem route).</summary>
    internal static void CountPalace() => Interlocked.Increment(ref _palace);

    /// <summary>A CSV's trimmed header and its non-empty rows' cells, or null with an error naming the file.</summary>
    private static (List<string> Header, List<string[]> Rows)? ReadTable(string csvPath, out string? error)
    {
        error = null;
        string file = Path.GetFileName(csvPath);
        if (!File.Exists(csvPath)) { error = $"Palace finished but wrote no {file} ({csvPath})."; return null; }
        var lines = File.ReadAllLines(csvPath).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) { error = $"Palace's {file} holds no row ({csvPath})."; return null; }
        return (lines[0].Split(',').Select(h => h.Trim()).ToList(), lines.Skip(1).Select(l => l.Split(',')).ToList());
    }

    /// <summary>What <c>postpro/palace.json</c> records about the mesh and the adaptive passes. The
    /// initial mesh is the lowest-numbered iteration archive's, when Palace made any.</summary>
    public static PalaceRunFacts ReadFacts(string postDir)
    {
        (long? Elements, long? Dofs, int? Iteration, string? Tag) Read(string file)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                string? tag = root.TryGetProperty("GitTag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (!root.TryGetProperty("Problem", out var p)) return (null, null, null, tag);
                long? Num(string k) => p.TryGetProperty(k, out var v) && v.TryGetInt64(out long x) ? x : null;
                return (Num("MeshElements"), Num("DegreesOfFreedom"), (int?)Num("Iteration"), tag);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return (null, null, null, null);
            }
        }

        string top = Path.Combine(postDir, "palace.json");
        if (!File.Exists(top)) return new(null, null, null, null, null);
        var final = Read(top);
        long? initial = final.Elements;
        if (Directory.Exists(postDir))
        {
            var first = Directory.EnumerateDirectories(postDir, "iteration*")
                .Select(d => (Dir: d, N: int.TryParse(Path.GetFileName(d)["iteration".Length..], out int k) ? k : int.MaxValue))
                .OrderBy(x => x.N).FirstOrDefault();
            if (first.Dir is not null && File.Exists(Path.Combine(first.Dir, "palace.json")))
                initial = Read(Path.Combine(first.Dir, "palace.json")).Elements ?? initial;
        }
        return new(initial, final.Elements, final.Dofs, final.Iteration is { } it ? Math.Max(0, it - 1) : null, final.Tag);
    }

    // ── one child process ────────────────────────────────────────────────────────────────────

    internal sealed record ProcessRun(int ExitCode, bool Cancelled, string? StartFailure, IReadOnlyList<string> Tail,
                                      long? PeakResident = null, string? MemoryNote = null);

    private static ProcessRun RunProcess(string exe, IReadOnlyList<string> args, string cwd, string logPath,
                                         Em3dProcessKind kind, IReadOnlyDictionary<string, string>? env,
                                         RunControl? control, CancellationToken ct, ProcessWatch? watch = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        return RunProcess(psi, logPath, kind, control, ct, watch);
    }

    /// <summary>
    /// Starts <paramref name="psi"/>, logs and relays its lines, and stops it on cancellation.
    /// <paramref name="kill"/> replaces the tree kill — brief-em3d-26: killing <c>wsl.exe</c> does not kill
    /// the Linux processes it started, so a subsystem run stops its own process group first.
    /// <paramref name="sampleMemory"/> false skips the resident-memory sample, which on a subsystem run would
    /// read <c>wsl.exe</c>'s own few megabytes, not Palace's.
    /// </summary>
    internal static ProcessRun RunProcess(ProcessStartInfo psi, string logPath, Em3dProcessKind kind, RunControl? control,
                                          CancellationToken ct, ProcessWatch? watch, Action<Process>? kill = null,
                                          bool sampleMemory = true)
    {
        if (ct.IsCancellationRequested) return new(-1, true, null, []);
        string exe = psi.FileName;
        void Stop(Process p)
        {
            if (kill is not null) kill(p);
            Kill(p);
        }

        var gate = new Lock();
        var tail = new Queue<string>();
        var pending = new Queue<string>();       // lines not yet handed to the watcher (the run's thread drains it)
        long lines = 0;
        using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        void Line(string? text)
        {
            if (text is null) return;
            lock (gate)
            {
                log.WriteLine(text);
                tail.Enqueue(text);
                if (tail.Count > 60) tail.Dequeue();
                if (watch?.OnLine is not null) pending.Enqueue(text);
                lines++;
            }
        }

        Process? p;
        try { p = Em3dProcessLauncher.Start(psi, kind); }
        catch (Exception e) { return new(-1, false, e.Message, []); }
        if (p is null) return new(-1, false, "the operating system started nothing", []);

        using (p)
        {
            ProcessStarted?.Invoke(p);
            if (psi.RedirectStandardInput) p.StandardInput.Close();
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived  += (_, e) => Line(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            long reported = 0;
            long? peak = null;
            string? memoryNote = null;
            var sampled = Stopwatch.StartNew();
            // Hands every line read so far to the watcher, or ticks the stage once per line without one.
            // On the run's thread, so a cancellation thrown by the control lands here.
            void Drain()
            {
                if (watch?.OnLine is { } onLine)
                {
                    while (true)
                    {
                        string next;
                        lock (gate) { if (pending.Count == 0) break; next = pending.Dequeue(); }
                        onLine(next);
                    }
                    return;
                }
                long now;
                lock (gate) now = lines;
                if (now > reported && control is not null)
                {
                    control.TickStage(now - reported);
                    reported = now;
                }
            }
            // R-em3d21-2c — the whole tree's resident memory, once a second, beside the stage.
            void Sample()
            {
                if (watch is null || !sampleMemory || sampled.ElapsedMilliseconds < 1000) return;
                sampled.Restart();
                if (ProcessTreeMemory.ResidentBytes(p.Id) is not { } rss || rss <= 0) return;
                peak = Math.Max(peak ?? 0, rss);
                string detail = $"{MachineMemory.Format(rss)} in use";
                if (memoryNote is null && watch.PhysicalBytes > 0 && rss >= Em3dMemoryVerdict.InUseNoteFraction * watch.PhysicalBytes)
                    memoryNote = $"{Path.GetFileName(exe)} and the processes it started reached {MachineMemory.Format(rss)} of resident " +
                                 $"memory, {(100.0 * rss / watch.PhysicalBytes).ToString("0", CultureInfo.InvariantCulture)} % of " +
                                 $"this machine's {MachineMemory.Format(watch.PhysicalBytes)}. Past this the operating system " +
                                 "swaps, and the run slows sharply; it was not stopped.";
                if (memoryNote is not null) detail += " — near this machine's memory";
                control?.SetStageDetail(detail);
            }
            while (!p.WaitForExit(100))
            {
                if (ct.IsCancellationRequested)
                {
                    Stop(p);
                    return new(-1, true, null, []);
                }
                try { Drain(); }
                catch (OperationCanceledException) { Stop(p); return new(-1, true, null, []); }
                Sample();
            }
            p.WaitForExit();                  // drains the asynchronous readers
            if (ct.IsCancellationRequested) return new(-1, true, null, []);
            try { Drain(); }
            catch (OperationCanceledException) { return new(-1, true, null, []); }
            lock (gate) return new(p.ExitCode, false, null, [.. tail], peak, memoryNote);
        }
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { p.WaitForExit(10_000); } catch (InvalidOperationException) { }
    }

    /// <summary>The program's last lines worth quoting: those carrying <paramref name="marker"/> when
    /// there are any, else its last few.</summary>
    private static string Quote(IReadOnlyList<string> tail, string marker)
    {
        var lines = tail.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var marked = lines.Where(l => l.Contains(marker, StringComparison.Ordinal)).TakeLast(5).ToList();
        var chosen = marked.Count > 0 ? marked : lines.TakeLast(8).ToList();
        return chosen.Count == 0 ? "(it printed nothing)" : "“" + string.Join(" | ", chosen) + "”";
    }

    private static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    internal static string Sha256(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
