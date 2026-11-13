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

    /// <summary>brief-emi-10 — the tree's highest footprint (resident, compressed and swapped), bytes.</summary>
    public long? PeakFootprintBytes { get; init; }

    /// <summary>The one-time warning a tree reaching 90 % of its memory ceiling gets.</summary>
    public string? MemoryNote { get; init; }

    /// <summary>brief-emi-10 — the memory ceiling or the disk floor stopped the tree (or refused to start it). Not
    /// <see cref="PalaceStep.Ok"/>, and nothing was written.</summary>
    public ProcessStop? Stopped { get; init; }

    /// <summary>A mesh step's tetrahedra, as Gmsh reported creating them — from this run's log, or from
    /// the log the reused mesh was made with. Null when Gmsh printed no count.</summary>
    public long? Tetrahedra { get; init; }

    /// <summary>brief-emb-2 R-emb2-1 — what the run must say about the mesh step: that Gmsh's curving ran past its cap and the
    /// mesh was made again with straight-sided second-order elements. Null otherwise.</summary>
    public string? Note { get; init; }

    /// <summary>brief-dfr35-1 R-dfr35-1-8 — where the surfaces a failed mesh named are, when the script's table held them.</summary>
    public Em3dLayoutPlace? Place { get; init; }
}

/// <summary>
/// brief-em3d-21 — what a running program's output and memory are watched with: a line handler, fed
/// on the RUN's thread (so a cancellation it throws can kill the tree), and the machine's memory for
/// the memory sample. Null handler: the stage ticks once per line, as before.
/// brief-emi-10 — and what the tree is held to: a memory ceiling on its footprint, read every 100 ms, and a free-disk floor on
/// <see cref="FloorVolumes"/>, read every second. <see cref="ProcessLimits.Watch"/> fills both for a run.
/// </summary>
public sealed record ProcessWatch(Action<string>? OnLine, long PhysicalBytes)
{
    /// <summary>The footprint at which the tree is killed; null for none.</summary>
    public long? CeilingBytes { get; init; }

    /// <summary>True once the run has been forced past its size check: the ceiling no longer applies (owner decision O2).</summary>
    public Func<bool>? CeilingLifted { get; init; }

    /// <summary>Whose ceiling it is, as the sentence names it.</summary>
    public string CeilingOwner { get; init; } = ProcessLimits.MachineOwner;

    /// <summary>The volumes the free-disk floor is kept on.</summary>
    public IReadOnlyList<FloorVolume> FloorVolumes { get; init; } = [];

    /// <summary>Called on the run's thread every tick (100 ms), after the lines are handed over: openEMS's decay check. A
    /// cancellation it throws kills the tree.</summary>
    public Action? OnTick { get; init; }

    /// <summary>Whether the process is meshing rather than solving, for a solver that meshes itself (EMerge, until it reports
    /// its mesh). Null: a mesher's process meshes and a solver's solves.</summary>
    public Func<bool>? Meshing { get; init; }

    /// <summary>Every process this watch's loop starts, as it starts.</summary>
    public Action<System.Diagnostics.Process>? OnStarted { get; init; }

    /// <summary>Test seam: the tree's memory for a root pid, in place of the operating system's (a reader per run).</summary>
    internal Func<int, Func<TreeMemory?>>? ReadMemory { get; init; }

    /// <summary>Test seam: a volume's free space, in place of the operating system's.</summary>
    internal Func<string, VolumeSpace?>? ReadSpace { get; init; }
}

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
    /// <param name="straight">brief-emb-2 R-emb2-1 — the same problem lowered with straight-sided second-order elements, for a
    /// script that curves them: once Gmsh's high-order stage has run longer than <paramref name="curvingMinutes"/>, Gmsh is
    /// stopped and the mesh made again from this one, and the step's <see cref="PalaceStep.Note"/> says so. Null: no cap.</param>
    public static PalaceStep Mesh(string runDir, GmshLowering lowering, string gmsh, RunControl? control,
                                  CancellationToken ct, ProcessLimits? limits = null, long physicalBytes = 0,
                                  Func<GmshLowering>? straight = null, double curvingMinutes = CircuitRF.Design.Layout.Em.PalaceSettings.DefaultMeshCurvingMinutes)
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
        // brief-em3d-65 R-em3d65-2a — the kernel solids' B-reps the script imports, by hash: the same file for the same solid,
        // so an unchanged problem still reuses its mesh.
        foreach (var kernel in lowering.KernelFiles ?? []) kernel.WriteInto(runDir);
        string hash = Sha256(lowering.Geo!);

        bool reuse = HasMeshOf(runDir, lowering, straight, out bool straightSided);
        string? note = reuse && straightSided ? CurvingNote(curvingMinutes, reused: true) : null;
        if (reuse && HasDelaunaySurfaces(File.ReadAllText(geoPath))) note = Joined(note, FlatElementNote(reused: true));
        if (!reuse)
        {
            foreach (string stale in new[] { hashPath, mshPath, entPath })
                if (File.Exists(stale)) File.Delete(stale);
            WriteText(geoPath, lowering.Geo!);

            // Gmsh meshes in a local scratch folder and the mesh and entity table are moved in after. The
            // script writes the entity table one Printf at a time, each opening and closing the file, and in
            // a workspace on a network drive some of those opens failed ("Unable to open file
            // 'entities.txt'", field report): Gmsh then wrote the mesh, exited 1 and the run stopped.
            // The folder is the user's scratch folder (ScratchFolder), the system temp folder by default.
            if (ScratchFolder.Refusal() is { } noScratch) return new(false, false, true, noScratch);
            string stage = ScratchFolder.NewPath("crf-gmsh-");
            try
            {
                Directory.CreateDirectory(stage);
                WriteText(Path.Combine(stage, GmshGeoWriter.GeoFile), lowering.Geo!);
                foreach (var kernel in lowering.KernelFiles ?? []) kernel.WriteInto(stage);
                // brief-em3d-140 R-em3d140-3 — a geometry already built for these same statements: the script reads it and builds nothing.
                if (HasBuiltGeometry(runDir, lowering))
                    foreach (string ext in BuiltExtensions)
                        File.Copy(Path.Combine(runDir, lowering.Built + ext), Path.Combine(stage, lowering.Built + ext), overwrite: true);

                // brief-em3d-21 R-em3d21-1e — Gmsh's own stage lines, not a count of them.
                int cuts = System.Text.RegularExpressions.Regex.Count(lowering.Geo!, "BooleanDifference");
                gmshLog = new GmshLogProgress(control) { GeometryCuts = cuts };
                gmshLog.Begin();
                // brief-emb-2 R-emb2-1 — a script that curves its elements is watched: past the cap on its high-order stage
                // Gmsh is stopped (a cancellation thrown from the tick kills the tree) and the mesh is made straight-sided.
                bool capped = false;
                var watch = Watch(limits, gmshLog.Line, physicalBytes, stage);
                if (straight is not null && GmshGeoWriter.Curves(lowering))
                {
                    var watched = gmshLog;
                    var tick = watch.OnTick;
                    watch = watch with
                    {
                        OnTick = () =>
                        {
                            tick?.Invoke();
                            if (!watched.CurvingPast(curvingMinutes)) return;
                            capped = true;
                            throw new OperationCanceledException();
                        },
                    };
                }
                Interlocked.Increment(ref _gmsh);
                var run = RunProcess(gmsh, GmshArguments, stage, logPath, Em3dProcessKind.Mesher, env: null, control, ct, watch);
                if (run.Cancelled && capped && !ct.IsCancellationRequested && straight!() is { Ok: true } flat)
                {
                    note = CurvingNote(curvingMinutes, reused: false);
                    WriteText(geoPath, flat.Geo!);
                    WriteText(Path.Combine(stage, GmshGeoWriter.GeoFile), flat.Geo!);
                    foreach (string partial in new[] { GmshGeoWriter.MeshFile, GmshGeoWriter.EntitiesFile })
                        if (File.Exists(Path.Combine(stage, partial))) File.Delete(Path.Combine(stage, partial));
                    gmshLog = new GmshLogProgress(control) { GeometryCuts = cuts };
                    gmshLog.Begin();
                    Interlocked.Increment(ref _gmsh);
                    run = RunProcess(gmsh, GmshArguments, stage, logPath, Em3dProcessKind.Mesher, env: null, control, ct,
                                     Watch(limits, gmshLog.Line, physicalBytes, stage));
                }
                // brief-cxp-5 — curving that left a node with no position (NaN): Gmsh's high-order optimisation can, beside short
                // facets meeting a curved face, and Palace then fails reading the mesh with "Required mesh-end tag not found",
                // naming nothing. Straight-sided second-order elements carry no such node.
                if (!run.Cancelled && run.Stopped is null && run.StartFailure is null && run.ExitCode == 0 && !capped && straight is not null &&
                    GmshGeoWriter.Curves(lowering) && Em3dMeshCheck.NonFiniteNodes(Path.Combine(stage, GmshGeoWriter.MeshFile)) is > 0 and var lost &&
                    straight() is { Ok: true } flatter)
                {
                    note = Joined(note, NonFiniteCurvingNote(lost));
                    WriteText(geoPath, flatter.Geo!);
                    WriteText(Path.Combine(stage, GmshGeoWriter.GeoFile), flatter.Geo!);
                    foreach (string partial in new[] { GmshGeoWriter.MeshFile, GmshGeoWriter.EntitiesFile })
                        if (File.Exists(Path.Combine(stage, partial))) File.Delete(Path.Combine(stage, partial));
                    gmshLog = new GmshLogProgress(control) { GeometryCuts = cuts };
                    gmshLog.Begin();
                    Interlocked.Increment(ref _gmsh);
                    run = RunProcess(gmsh, GmshArguments, stage, logPath, Em3dProcessKind.Mesher, env: null, control, ct,
                                     Watch(limits, gmshLog.Line, physicalBytes, stage));
                }
                // A flat tetrahedron the mesher left: once more on the Delaunay surface mesher, from the geometry just saved. A mesh
                // still holding one is refused by the check the run makes next, with its place.
                if (!run.Cancelled && run.Stopped is null && run.StartFailure is null && run.ExitCode == 0 && lowering.Built is not null &&
                    Em3dMeshCheck.Check(Path.Combine(stage, GmshGeoWriter.MeshFile), lowering.Groups) is { ZeroVolume: > 0 })
                {
                    note = Joined(note, FlatElementNote(reused: false));
                    string delaunay = File.ReadAllText(Path.Combine(stage, GmshGeoWriter.GeoFile)) + GmshGeoWriter.DelaunaySurfacesLine + "\n";
                    WriteText(geoPath, delaunay);
                    WriteText(Path.Combine(stage, GmshGeoWriter.GeoFile), delaunay);
                    foreach (string partial in new[] { GmshGeoWriter.MeshFile, GmshGeoWriter.EntitiesFile })
                        File.Delete(Path.Combine(stage, partial));
                    gmshLog = new GmshLogProgress(control) { GeometryCuts = cuts };
                    gmshLog.Begin();
                    Interlocked.Increment(ref _gmsh);
                    run = RunProcess(gmsh, GmshArguments, stage, logPath, Em3dProcessKind.Mesher, env: null, control, ct,
                                     Watch(limits, gmshLog.Line, physicalBytes, stage));
                }
                if (run.Cancelled) return new(false, true, false, null);
                if (run.Stopped is { } stopped) return new(false, false, false, null) { Stopped = stopped, PeakFootprintBytes = run.PeakFootprint };
                if (run.StartFailure is { } why)
                    return new(false, false, false, $"Gmsh could not be started ({why}).");
                if (run.ExitCode != 0)
                {
                    // brief-dfr31-6 R-dfr31-6-4 — the surfaces it names, as the objects they belong to; the face table is
                    // in the stage folder, which goes when this returns.
                    string faces = Path.Combine(stage, GmshGeoWriter.FacesFile);
                    string? facesText = File.Exists(faces) ? File.ReadAllText(faces) : null;
                    var surfaceLines = GmshSurfaceLines(logPath);
                    string? named = facesText is null ? null : GmshGeoWriter.NameSurfaces(surfaceLines, facesText, lowering.Groups);
                    return new(false, false, false,
                        $"Gmsh failed (exit code {run.ExitCode}). Its own words: {GmshErrors(logPath, run.Tail)} " +
                        (named is null ? "" : named + " ") + $"The full log is {logPath}.")
                    { Place = facesText is null ? null : GmshGeoWriter.SurfacePlace(surfaceLines, facesText) };
                }
                foreach (string produced in new[] { GmshGeoWriter.MeshFile, GmshGeoWriter.EntitiesFile })
                    if (File.Exists(Path.Combine(stage, produced)))
                        MoveIn(Path.Combine(stage, produced), Path.Combine(runDir, produced));
            }
            finally
            {
                try { KeepBuiltGeometry(stage, runDir, lowering, logPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
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
        return PalaceStep.Done(reuse) with { Tetrahedra = gmshLog?.Tetrahedra, Note = note };
    }

    /// <summary>
    /// brief-dfr36-1 R-dfr36-1-3 — whether <paramref name="runDir"/> already holds the mesh of this script: the one test a run makes
    /// before meshing, and what a 3D view reads to say a mesh shown is no longer the model's.
    /// The script asked for may have been meshed straight-sided after its curving ran past the cap (brief-emb-2): its hash is then
    /// beside the straight script, and that mesh is the one to reuse rather than curving for another five minutes.
    /// </summary>
    public static bool HasMeshOf(string runDir, GmshLowering lowering, Func<GmshLowering>? straight, out bool straightSided)
    {
        straightSided = false;
        string geoPath = Path.Combine(runDir, GmshGeoWriter.GeoFile), hashPath = Path.Combine(runDir, MeshHashFile);
        if (!File.Exists(hashPath) || !File.Exists(Path.Combine(runDir, GmshGeoWriter.MeshFile)) ||
            !File.Exists(Path.Combine(runDir, GmshGeoWriter.EntitiesFile)) || !File.Exists(geoPath) ||
            File.ReadAllText(hashPath).Trim() != Sha256(lowering.Geo!))
            return false;
        string onDisk = File.ReadAllText(geoPath);
        // A mesh remade on the Delaunay surface mesher is this script's mesh too: the hash beside it is this script's.
        if (HasDelaunaySurfaces(onDisk)) onDisk = onDisk[..^(GmshGeoWriter.DelaunaySurfacesLine.Length + 1)];
        if (onDisk == lowering.Geo) return true;
        if (straight is null || !GmshGeoWriter.Curves(lowering) || straight() is not { Ok: true } flat || onDisk != flat.Geo) return false;
        straightSided = true;
        return true;
    }

    private static bool HasDelaunaySurfaces(string geo) => geo.EndsWith(GmshGeoWriter.DelaunaySurfacesLine + "\n", StringComparison.Ordinal);

    private static string Joined(string? first, string second) => first is null ? second : first + " " + second;

    /// <summary>What a run says when a flat element made it remake the mesh, or when it reuses that mesh.</summary>
    internal static string FlatElementNote(bool reused)
        => reused
            ? "The reused mesh was made on Gmsh's Delaunay surface mesher, because its usual one left a zero-volume element."
            : "Gmsh left a zero-volume element, so the mesh was made again on its Delaunay surface mesher, which adds about 5 % more elements.";

    private static readonly string[] BuiltExtensions = [GmshGeoWriter.BuiltBrepExtension, GmshGeoWriter.BuiltMapExtension];

    /// <summary>brief-em3d-140 R-em3d140-3 — whether <paramref name="dir"/> holds a whole built geometry for this script: the
    /// B-rep, and a map with its last line. A Gmsh stopped while writing them leaves a map without one.</summary>
    public static bool HasBuiltGeometry(string dir, GmshLowering lowering)
    {
        if (lowering.Built is not { } built) return false;
        string map = Path.Combine(dir, built + GmshGeoWriter.BuiltMapExtension);
        return File.Exists(Path.Combine(dir, built + GmshGeoWriter.BuiltBrepExtension)) && File.Exists(map)
            && File.ReadLines(map).LastOrDefault() == GmshGeoWriter.BuiltMapComplete;
    }

    /// <summary>
    /// brief-em3d-140 R-em3d140-3 — keeps what Gmsh built beside the script, whatever happened after: a mesh that failed or a run
    /// stopped while meshing starts from it next time. One geometry is kept, since a board's is as large as its mesh. A file
    /// Gmsh could not read is removed, or every later run would stop on it.
    /// </summary>
    private static void KeepBuiltGeometry(string stage, string runDir, GmshLowering lowering, string logPath)
    {
        if (lowering.Built is not { } built) return;
        bool unreadable = File.Exists(logPath) && File.ReadLines(logPath).Any(l =>
            l.StartsWith("Error", StringComparison.Ordinal) && l.Contains(built, StringComparison.Ordinal));
        if (unreadable)
        {
            foreach (string ext in BuiltExtensions) File.Delete(Path.Combine(runDir, built + ext));
            return;
        }
        if (HasBuiltGeometry(runDir, lowering) || !HasBuiltGeometry(stage, lowering)) return;
        foreach (string old in Directory.EnumerateFiles(runDir, GmshGeoWriter.BuiltPrefix + "*"))
            File.Delete(old);
        foreach (string ext in BuiltExtensions)
            MoveIn(Path.Combine(stage, built + ext), Path.Combine(runDir, built + ext));
    }

    /// <summary>
    /// Moves a file Gmsh made in the scratch folder into the run folder. On Windows a rename into a workspace folder that a
    /// cloud-sync client or a scanner watches can be refused with "Access to the path is denied" while writing a new file there
    /// is not (field report: the mesh was made and the run stopped, in a synced Documents folder). The rename is tried again for
    /// under a second, then the file is copied; the scratch folder is removed afterwards, so the copy leaves nothing behind.
    /// </summary>
    internal static void MoveIn(string from, string to)
    {
        try { CircuitRF.Design.Cells.AtomicFile.MoveOver(from, to); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            File.Copy(from, to, overwrite: true);
        }
    }

    /// <summary>brief-emb-2 R-emb2-6 — Gmsh's command line: at verbosity 5, which names every stage as it starts, the
    /// high-order optimisation and its node count included.</summary>
    internal static readonly string[] GmshArguments = [GmshGeoWriter.GeoFile, "-3", "-v", "5", "-o", GmshGeoWriter.MeshFile];

    /// <summary>brief-cxp-5 — the note when curving left nodes with no position and the mesh was made straight-sided.</summary>
    internal static string NonFiniteCurvingNote(int nodes)
        => $"Gmsh's curving of the second-order elements left {nodes.ToString("N0", CultureInfo.InvariantCulture)} node(s) with no " +
           "position, which the solver cannot read, so the mesh was made again with straight-sided second-order elements. A curved " +
           "surface is then faceted at its mesh size.";

    /// <summary>brief-emb-2 R-emb2-1 — what a run says when the curving cap stepped in (or its mesh is reused).</summary>
    internal static string CurvingNote(double minutes, bool reused)
        => reused
            ? "The reused mesh has straight-sided second-order elements: when it was made, Gmsh's curving either ran past " +
              $"{minutes.ToString("G4", CultureInfo.InvariantCulture)} min (Palace.MeshCurvingMinutes) or left nodes with no position. " +
              "A curved surface is faceted at its mesh size."
            : $"Gmsh's curving of the second-order elements ran past {minutes.ToString("G4", CultureInfo.InvariantCulture)} min " +
           "(Palace.MeshCurvingMinutes), so the mesh was made again with straight-sided second-order elements. A curved " +
           "surface is then faceted at its mesh size; raise MeshCurvingMinutes to let the curving finish.";

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
                                   PalaceStageTracker? tracker = null, long physicalBytes = 0, ProcessLimits? limits = null)
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
                             Watch(limits, tracker is null ? null : tracker.Line, physicalBytes, runDir));
        if (run.Cancelled) return new(false, true, false, null);
        if (run.Stopped is { } stopped)
            return new(false, false, false, null) { Stopped = stopped, PeakResidentBytes = run.PeakResident, PeakFootprintBytes = run.PeakFootprint };
        if (run.StartFailure is { } why) return new(false, false, false, $"Palace could not be started ({why}).");
        if (run.ExitCode != 0)
            return new(false, false, false,
                (KilledSentence(run.ExitCode, run.Tail, physicalBytes, wsl: false) is { } killed
                    ? killed + " "
                    : $"Palace failed (exit code {run.ExitCode}). Its own words: {Quote(run.Tail, "rror")} ") +
                $"The full log is {Path.Combine(runDir, PalaceLogFile)}.");
        return PalaceStep.Done() with { PeakResidentBytes = run.PeakResident, PeakFootprintBytes = run.PeakFootprint, MemoryNote = run.MemoryNote };
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

    /// <summary>The <c>mpirun</c> named in Settings ▸ Solvers, or null. Installed by <c>src/Ui</c>
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
    /// <paramref name="note"/>); nothing here fails the run. brief-em3d-115 R-em3d115-2c — a terminal face's own modes are
    /// its N terminals', so it is asked for mode N + 1 on its Active entry, and only that entry (the lowest of
    /// <paramref name="wavePorts"/> on the face) is in the result.
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
        var expected = new Dictionary<int, int>();
        try
        {
            Directory.CreateDirectory(dir);
            var root = System.Text.Json.Nodes.JsonNode.Parse(configJson)!.AsObject();
            root["Problem"]!["Output"] = "postpro";
            root["Model"]!["Mesh"] = "../" + GmshGeoWriter.MeshFile;
            root["Model"]!.AsObject().Remove("Refinement");
            // brief-em3d-115 R-em3d115-2c — on a terminal face (N entries on one attribute) modes 1…N are the terminals' own,
            // so the face's Active entry asks mode N + 1 instead, once per face; an entry alone on its face asks mode 2.
            foreach (var face in root["Boundaries"]!["WavePort"]!.AsArray().GroupBy(wp => (int)wp!["Attributes"]![0]!))
            {
                var entries = face.ToList();
                var leader = entries.FirstOrDefault(wp => wp!["Active"]?.GetValue<bool>() == true) ?? entries.MinBy(wp => (int)wp!["Index"]!);
                int mode = entries.Count + 1;
                leader!["Mode"] = mode;
                expected[(int)leader["Index"]!] = mode;
            }
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

        var asked = wavePorts.Where(expected.ContainsKey).ToList();
        var seen = new Dictionary<int, PalaceWaveMode>();
        void OnLine(string line)
        {
            if (ParseWaveMode(line) is { } m && expected.TryGetValue(m.Port, out int mode) && m.Mode == mode) seen[m.Port] = m;
            if (asked.All(seen.ContainsKey)) throw new OperationCanceledException();
        }
        var result = run(dir, Watch(null, OnLine, 0, dir));
        if (asked.All(seen.ContainsKey)) return [.. asked.Select(p => seen[p])];
        note = result.Cancelled ? "the second-mode check was cancelled"
             : result.StartFailure is { } why ? $"the second-mode check could not start Palace ({why})"
             : result.Stopped is { } stop ? $"the second-mode check was stopped: {stop.Sentence("Palace", ProcessLimits.MachineOwner, [], null)}"
             : $"Palace printed no line for the mode past each wave port face's own (exit {result.ExitCode}; its log is {Path.Combine(dir, PalaceLogFile)})";
        return null;
    }

    /// <summary>The directory, under the run directory, a face's mode solve runs in (brief-em3d-115).</summary>
    public const string FaceModeDirectory = "facemodes";

    private static readonly System.Text.RegularExpressions.Regex FaceModeLine = new(
        @"^\s*eig (\d+): kn = \S+, n_eff = ([-+]?\d+(?:\.\d+)?(?:e[-+]\d+)?)([-+]\d+(?:\.\d+)?(?:e[-+]\d+)?)i",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// <b>brief-em3d-115 R-em3d115-3 — a port face's own modes at one frequency, from Palace's 2D mode solve.</b> An adaptive
    /// sweep logs no kₙ (0.18.1 suppresses the port output while it builds its reduced model, and re-solves only the Active
    /// entry's mode afterwards, printing nothing), and the terminal transform needs every entry's. Palace's
    /// <c>BoundaryMode</c> problem extracts the face from the same mesh (<c>Solver.BoundaryMode.Attributes</c>) and solves its
    /// modes there, in seconds: on the 3D Wave Ports Pair's mesh, 3 s against 113 s for a driven start that is stopped at its
    /// port lines. Its log prints each mode's n_eff to seven figures (<c>eig 0: kn = …, n_eff = 1.691209e+00+1.4e-13i</c>);
    /// kₙ = n_eff·ω/c. The first <paramref name="count"/> modes in Palace's own order (decreasing Re kₙ), which is the order the
    /// driven run's <c>Mode</c> indices count; measured equal to the driven log's kₙ to its four figures on brief 124's
    /// microstrip pair. Null, with the reason, when it could not be asked.
    /// </summary>
    internal static IReadOnlyList<Complex>? FaceModes(string runDir, string configJson, int attribute, int count, double fHz,
                                                     out string? note, Func<string, ProcessRun> run)
    {
        note = null;
        string dir = Path.Combine(runDir, FaceModeDirectory);
        try
        {
            Directory.CreateDirectory(dir);
            var root = System.Text.Json.Nodes.JsonNode.Parse(configJson)!.AsObject();
            root["Problem"]!["Type"] = "BoundaryMode";
            root["Problem"]!["Output"] = "postpro";
            root["Problem"]!["OutputFormats"] = new System.Text.Json.Nodes.JsonObject { ["Paraview"] = false };
            root["Model"]!["Mesh"] = "../" + GmshGeoWriter.MeshFile;
            root["Model"]!.AsObject().Remove("Refinement");
            var solver = root["Solver"]!.AsObject();
            solver.Remove("Driven");
            solver["BoundaryMode"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Freq"] = fHz / 1e9,
                ["N"] = count,
                ["Attributes"] = new System.Text.Json.Nodes.JsonArray(attribute),
            };
            WriteText(Path.Combine(dir, PalaceConfigWriter.ConfigFile), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException)
        {
            note = $"the port face's mode solve could not be staged ({e.Message})";
            return null;
        }
        var result = run(dir);
        var modes = ParseFaceModes(File.Exists(Path.Combine(dir, PalaceLogFile)) ? File.ReadLines(Path.Combine(dir, PalaceLogFile)) : [], fHz);
        if (modes.Count >= count) return [.. modes.Take(count)];
        note = result.Cancelled ? "the port face's mode solve was cancelled"
             : result.StartFailure is { } why ? $"the port face's mode solve could not start Palace ({why})"
             : result.Stopped is { } stop ? $"the port face's mode solve was stopped: {stop.Sentence("Palace", ProcessLimits.MachineOwner, [], null)}"
             : $"Palace's mode solve of attribute {attribute} at {(fHz / 1e9).ToString("G6", CultureInfo.InvariantCulture)} GHz printed " +
               $"{modes.Count} of {count} modes (exit {result.ExitCode}; its log is {Path.Combine(dir, PalaceLogFile)})";
        return null;
    }

    /// <summary>The modes a <c>BoundaryMode</c> log prints, kₙ in m⁻¹ (n_eff·ω/c), in its own order.</summary>
    internal static IReadOnlyList<Complex> ParseFaceModes(IEnumerable<string> lines, double fHz)
    {
        const double c0 = 299_792_458.0;
        var byIndex = new SortedDictionary<int, Complex>();
        foreach (string line in lines)
        {
            var m = FaceModeLine.Match(line);
            if (!m.Success) continue;
            static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            byIndex[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] =
                new Complex(D(m.Groups[2].Value), D(m.Groups[3].Value)) * (2 * Math.PI * fHz / c0);
        }
        return [.. byIndex.Values];
    }

    /// <summary>The face's mode solve on this machine: one process, as the second-mode check runs.</summary>
    public static IReadOnlyList<Complex>? FaceModes(string runDir, string configJson, string palace, int attribute, int count, double fHz,
                                                   CancellationToken ct, out string? note)
        => FaceModes(runDir, configJson, attribute, count, fHz, out note, dir =>
        {
            bool bareBinary = palace.EndsWith(".bin", StringComparison.Ordinal);
            Interlocked.Increment(ref _palace);
            return RunProcess(palace, bareBinary ? [PalaceConfigWriter.ConfigFile] : ["--serial", PalaceConfigWriter.ConfigFile],
                              dir, Path.Combine(dir, PalaceLogFile), Em3dProcessKind.Solver,
                              env: new Dictionary<string, string> { ["OMP_NUM_THREADS"] = "1" }, null, ct, Watch(null, null, 0, dir));
        });

    /// <summary>brief-em3d-31 — Palace's far field, r·E in Cartesian components per (frequency, excitation, θ, φ).</summary>
    public const string FarFieldFile = "farfield-rE.csv";

    /// <summary>brief-em3d-31 — the ports' incident and total voltages per excitation.</summary>
    public const string PortVFile = "port-V.csv";

    /// <summary>
    /// <b>brief-em3d-31 — Palace's far field, by column NAME.</b> The pinned 0.18.1 writes <c>f (GHz)</c>,
    /// <c>exc</c>, <c>theta (deg.)</c>, <c>phi (deg.)</c> and <c>r*Re{E_x} (V)</c> / <c>r*Im{E_x} (V)</c> for
    /// x, y, z (read off a real run and its postoperatorcsv.cpp). It writes each pole ONCE, whatever azimuths
    /// were asked for there — a Cartesian vector is well defined at a pole where θ̂ and φ̂ are not — so the
    /// map is keyed by rounded (θ, φ) and a pole is looked up at θ alone. Keys: (frequency GHz as printed,
    /// excitation) → (θ, φ) → (E_x, E_y, E_z).
    /// </summary>
    public static Dictionary<(double FGHz, int Exc), Dictionary<(int Theta, int Phi), (Complex X, Complex Y, Complex Z)>>?
        ReadFarField(string csvPath, out string? error)
    {
        if (ReadTable(csvPath, out error) is not { } t) return null;
        var (h, rows) = t;
        string[] names = ["f (GHz)", "exc", "theta (deg.)", "phi (deg.)",
                          "r*Re{E_x} (V)", "r*Im{E_x} (V)", "r*Re{E_y} (V)", "r*Im{E_y} (V)", "r*Re{E_z} (V)", "r*Im{E_z} (V)"];
        var col = names.Select(n => h.IndexOf(n)).ToArray();
        if (Array.IndexOf(col, -1) is int miss and >= 0)
        {
            error = $"Palace's {FarFieldFile} has no column '{names[miss]}' ({csvPath}).";
            return null;
        }
        var map = new Dictionary<(double, int), Dictionary<(int, int), (Complex, Complex, Complex)>>();
        for (int r = 0; r < rows.Count; r++)
        {
            var c = rows[r];
            var v = new double[names.Length];
            for (int k = 0; k < names.Length; k++)
                if (col[k] >= c.Length || !double.TryParse(c[col[k]].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]))
                {
                    error = $"Row {r + 2} of Palace's {FarFieldFile} is not all numbers ({csvPath}).";
                    return null;
                }
            var key = (v[0], (int)Math.Round(v[1]));
            if (!map.TryGetValue(key, out var dirs)) map[key] = dirs = [];
            dirs[((int)Math.Round(v[2] * 1000), (int)Math.Round(v[3] * 1000))] =
                (new Complex(v[4], v[5]), new Complex(v[6], v[7]), new Complex(v[8], v[9]));
        }
        return map;
    }

    /// <summary>
    /// <b>brief-em3d-31 — each excitation's own port voltage, by column NAME</b>: <c>V_inc[k][k] (V)</c> and
    /// <c>Re{V[k][k]} (V)</c> / <c>Im{V[k][k]} (V)</c> for port k driven — the incident and the TOTAL voltage at
    /// the driven port (their difference over V_inc is S_kk exactly, checked against port-S.csv on a real run).
    /// With a single excitation the columns carry one index (<c>V_inc[k] (V)</c>). Rows in file order with
    /// their frequencies, GHz as printed.
    /// </summary>
    public static (double[] FrequenciesGHz, double[][] Incident, Complex[][] Total)? ReadPortV(string csvPath, IReadOnlyList<int> ports,
                                                                                                out string? error)
    {
        if (ReadTable(csvPath, out error) is not { } t) return null;
        var (h, rows) = t;
        int fc = h.IndexOf("f (GHz)");
        // With ONE excitation Palace drops the excitation index from every column (its ex_label is empty):
        // "V_inc[1] (V)", "Re{V[1]} (V)". With several it writes "V_inc[1][1] (V)".
        int Col(string two, string one) => h.IndexOf(two) is int k and >= 0 ? k : ports.Count == 1 ? h.IndexOf(one) : -1;
        var inc = ports.Select(k => Col($"V_inc[{k}][{k}] (V)", $"V_inc[{k}] (V)")).ToArray();
        var re = ports.Select(k => Col($"Re{{V[{k}][{k}]}} (V)", $"Re{{V[{k}]}} (V)")).ToArray();
        var im = ports.Select(k => Col($"Im{{V[{k}][{k}]}} (V)", $"Im{{V[{k}]}} (V)")).ToArray();
        if (fc < 0 || inc.Contains(-1) || re.Contains(-1) || im.Contains(-1))
        {
            error = $"Palace's {PortVFile} does not carry every driven port's incident and total voltage ({csvPath}).";
            return null;
        }
        var f = new double[rows.Count];
        var vi = new double[rows.Count][];
        var vt = new Complex[rows.Count][];
        for (int r = 0; r < rows.Count; r++)
        {
            double Cell(int k) => k < rows[r].Length && double.TryParse(rows[r][k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : double.NaN;
            f[r] = Cell(fc);
            vi[r] = [.. inc.Select(Cell)];
            vt[r] = [.. Enumerable.Range(0, ports.Count).Select(k => new Complex(Cell(re[k]), Cell(im[k])))];
            if (double.IsNaN(f[r]) || vi[r].Any(double.IsNaN) || vt[r].Any(z => double.IsNaN(z.Real) || double.IsNaN(z.Imaginary)))
            {
                error = $"Row {r + 2} of Palace's {PortVFile} is not all numbers ({csvPath}).";
                return null;
            }
        }
        return (f, vi, vt);
    }

    /// <summary>
    /// <b>brief-em3d-115 R-em3d115-4 — the wave ports' voltage matrix, by column NAME</b>: <c>Re{V_wp[i][j]} (V)</c> and
    /// <c>Im{V_wp[i][j]} (V)</c>, the TOTAL field's line integral along port i's voltage path with port j excited
    /// (postoperator.cpp ≈ 1743, 0.18.1). With one excitation the second index is dropped (<c>Re{V_wp[i]} (V)</c>), as
    /// <see cref="ReadPortV"/>'s columns are. Returns V[f][i, j] in <paramref name="ports"/>' order for both indices, and each
    /// row's frequency, GHz as printed.
    /// </summary>
    public static (double[] FrequenciesGHz, Complex[][,] V)? ReadWavePortV(string csvPath, IReadOnlyList<int> ports, out string? error)
    {
        if (ReadTable(csvPath, out error) is not { } t) return null;
        var (h, rows) = t;
        int n = ports.Count, fc = h.IndexOf("f (GHz)");
        var re = new int[n, n];
        var im = new int[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int Col(string part) => h.IndexOf($"{part}{{V_wp[{ports[i]}][{ports[j]}]}} (V)") is int k and >= 0 ? k
                                       : n == 1 ? h.IndexOf($"{part}{{V_wp[{ports[i]}]}} (V)") : -1;
                (re[i, j], im[i, j]) = (Col("Re"), Col("Im"));
                if (re[i, j] < 0 || im[i, j] < 0 || fc < 0)
                {
                    error = $"Palace's {PortVFile} has no column 'Re{{V_wp[{ports[i]}][{ports[j]}]}} (V)' ({csvPath}).";
                    return null;
                }
            }
        var f = new double[rows.Count];
        var v = new Complex[rows.Count][,];
        for (int r = 0; r < rows.Count; r++)
        {
            double Cell(int k) => k < rows[r].Length && double.TryParse(rows[r][k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                ? x : double.NaN;
            f[r] = Cell(fc);
            v[r] = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) v[r][i, j] = new Complex(Cell(re[i, j]), Cell(im[i, j]));
            if (double.IsNaN(f[r]) || Enumerable.Range(0, n * n).Any(k => double.IsNaN(v[r][k / n, k % n].Real) || double.IsNaN(v[r][k / n, k % n].Imaginary)))
            {
                error = $"Row {r + 2} of Palace's {PortVFile} is not all numbers ({csvPath}).";
                return null;
            }
        }
        return (f, v);
    }

    private static readonly System.Text.RegularExpressions.Regex BoundaryModesLine = new(
        @"^\s*Calculating boundary modes at wave ports for \S+ = ([-+]?\d+(?:\.\d+)?(?:e[-+]\d+)?) GHz",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// brief-em3d-115 R-em3d115-4 — every wave port's kₙ, from the log, by the frequency Palace set its ports up at: a
    /// Point sweep logs <c>Calculating boundary modes at wave ports for ω/2π = 2.000e+00 GHz (…)</c> and then one mode line
    /// per entry, once per frequency PER EXCITATION (0.18.1 loops the excitations outside the frequencies); the first block
    /// at each frequency is kept. An adaptive sweep logs no such line and no kₙ at all. kₙ is written nowhere else, and only
    /// to four figures; the frequency line too, so a block's frequency is matched to a row of port-S.csv at that precision.
    /// </summary>
    public static IReadOnlyList<PalaceWaveModeSample> ReadWaveModes(IEnumerable<string> logLines)
    {
        var blocks = new List<PalaceWaveModeSample>();
        Dictionary<int, PalaceWaveMode>? current = null;
        double fHz = double.NaN;
        void Close()
        {
            if (current is { Count: > 0 } && !blocks.Any(b => Math.Abs(b.FrequencyHz - fHz) <= 1e-9 * fHz))
                blocks.Add(new PalaceWaveModeSample(fHz, current));
            current = null;
        }
        foreach (string line in logLines)
        {
            var f = BoundaryModesLine.Match(line);
            if (f.Success)
            {
                Close();
                fHz = double.Parse(f.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture) * 1e9;
                current = [];
                continue;
            }
            if (current is not null && ParseWaveMode(line) is { } m) current[m.Port] = m;
        }
        Close();
        return blocks;
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

    /// <summary>
    /// Removes the FIELD files Palace saved for each adaptive pass before the last, keeping each pass's
    /// <c>palace.json</c> and CSVs.
    ///
    /// <para><b>Palace saves every pass by default</b> (<c>Model.Refinement.SaveAdaptIterations</c>, which
    /// this writer never sets): the pass's postprocessing goes to <c>postpro/iterationX/</c> and the final
    /// pass stays at the top level. Nothing in circuitRF reads a pass's fields, because the 3D view draws the
    /// top-level <c>postpro/paraview</c>. They were most of a run folder: on the 3D Connector example, 420 MB of
    /// 695 MB. What IS read is kept: <see cref="ReadFacts"/> takes the starting mesh's element count from the
    /// first pass's <c>palace.json</c>, and the per-pass CSVs are the convergence history. Turning the option
    /// off instead would lose both.</para>
    ///
    /// <para>Every subfolder of a pass is field output (a CSV or <c>palace.json</c> is never in one), so a
    /// pass loses its subfolders and keeps its files. Best effort: a folder that cannot be removed costs
    /// disk space, never the run.</para>
    /// </summary>
    public static void PruneIterationFields(string postDir)
    {
        try
        {
            if (!Directory.Exists(postDir)) return;
            foreach (string pass in Directory.EnumerateDirectories(postDir, "iteration*"))
                foreach (string sub in Directory.EnumerateDirectories(pass))
                {
                    try { Directory.Delete(sub, recursive: true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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

    /// <param name="Stopped">brief-emi-10 — the ceiling or the floor stopped the tree, or the floor refused to start it.</param>
    internal sealed record ProcessRun(int ExitCode, bool Cancelled, string? StartFailure, IReadOnlyList<string> Tail,
                                      long? PeakResident = null, string? MemoryNote = null, ProcessStop? Stopped = null,
                                      long? PeakFootprint = null)
    {
        /// <summary>The memory ceiling stopped the tree.</summary>
        public bool StoppedForMemory => Stopped?.Reason == ProcessStopReason.Memory;
    }

    /// <summary>The watch a step runs under: the run's limits, or — called with none, as a probe or a test is — this machine's
    /// ceiling, and the floor on the volume <paramref name="runDir"/> is on and the swap volume.</summary>
    internal static ProcessWatch Watch(ProcessLimits? limits, Action<string>? onLine, long physicalBytes, string runDir)
        => (limits ?? ProcessLimits.For(MachineMemory.PhysicalBytes, null)).Watch(onLine, physicalBytes, runDir);

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
    /// <paramref name="sampleMemory"/> false skips the memory sample, and with it the ceiling: on a subsystem run it
    /// would read <c>wsl.exe</c>'s own few megabytes, not Palace's, and the subsystem's own memory limit bounds the run.
    /// </summary>
    /// <remarks>brief-emi-10 — THE one loop every mesher and solver process runs in, so one ceiling and one floor hold for all
    /// of them: the tree's footprint every 100 ms against <see cref="ProcessWatch.CeilingBytes"/>, each floor volume's free
    /// space every second, and a volume already below the floor refuses the start, so a counter of processes started stays
    /// put.</remarks>
    internal static ProcessRun RunProcess(ProcessStartInfo psi, string logPath, Em3dProcessKind kind, RunControl? control,
                                          CancellationToken ct, ProcessWatch? watch, Action<Process>? kill = null,
                                          bool sampleMemory = true)
    {
        if (ct.IsCancellationRequested) return new(-1, true, null, []);
        string exe = psi.FileName;
        bool meshing = kind == Em3dProcessKind.Mesher;
        bool Meshing() => watch?.Meshing?.Invoke() ?? meshing;

        // R-emi10-3 — a volume already below the floor: nothing is started that would be stopped at once.
        var readSpace = watch?.ReadSpace ?? DiskFloor.Read;
        ProcessStop? BelowFloor(bool beforeStart)
        {
            foreach (var v in watch?.FloorVolumes ?? [])
                if (readSpace(v.Path) is { } space && space.FreeBytes < DiskFloor.FloorFor(space.TotalBytes))
                    return new(ProcessStopReason.Disk, space.FreeBytes, DiskFloor.FloorFor(space.TotalBytes), Meshing(), beforeStart,
                               DiskFloor.Name(v.Path), v.Role);
            return null;
        }
        if (BelowFloor(beforeStart: true) is { } refused) return new(-1, false, null, [], Stopped: refused);

        void Stop(Process p)
        {
            if (kill is not null) kill(p);
            Kill(p);
        }

        var gate = new Lock();
        var tail = new Queue<string>();
        var pending = new Queue<string>();       // lines not yet handed to the watcher (the run's thread drains it)
        long lines = 0;
        // Set under the gate before the writer is disposed: after a Kill the timed WaitForExit does not wait for the
        // redirected streams, so a late line can still arrive — and a handler that throws on a disposed writer is rethrown on
        // a thread-pool thread, which ends the process (the guard openEMS's own loop had).
        bool closed = false;
        using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        void Line(string? text)
        {
            if (text is null) return;
            lock (gate)
            {
                if (closed) return;
                log.WriteLine(text);
                tail.Enqueue(text);
                if (tail.Count > 60) tail.Dequeue();
                if (watch?.OnLine is not null) pending.Enqueue(text);
                lines++;
            }
        }
        // Designer round 36 — no solver stamps its log, so the log alone could not say when anything happened, or whether a last
        // line such as "setsid: child 1054 did not exit normally" was a crash or the Stop button. circuitRF's lines say; they go
        // to the file only, never to the tail or the progress parser.
        var running = Stopwatch.StartNew();
        void Stamp(string words)
        {
            lock (gate)
            {
                if (closed) return;
                log.WriteLine(LogStamp(DateTimeOffset.Now, words));
                log.Flush();
            }
        }

        Process? p;
        try { p = Em3dProcessLauncher.Start(psi, kind); }
        catch (Exception e) { return new(-1, false, e.Message, []); }
        if (p is null) return new(-1, false, "the operating system started nothing", []);
        Stamp($"{Path.GetFileName(exe)} started.");

        bool watchMemory = watch is not null && sampleMemory;
        long? Ceiling() => watch?.CeilingLifted?.Invoke() == true ? null : watch?.CeilingBytes;
        using var job = watchMemory ? Em3dProcessJob.Attach(p, Ceiling()) : null;
        try
        {
            using (p)
            {
                ProcessStarted?.Invoke(p);
                watch?.OnStarted?.Invoke(p);
                if (psi.RedirectStandardInput) p.StandardInput.Close();
                p.OutputDataReceived += (_, e) => Line(e.Data);
                p.ErrorDataReceived  += (_, e) => Line(e.Data);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                long reported = 0;
                long? peak = null, peakFootprint = null;
                string? memoryNote = null;
                var shown = Stopwatch.StartNew();
                var diskRead = Stopwatch.StartNew();
                var read = !watchMemory ? null
                         : watch!.ReadMemory?.Invoke(p.Id) ?? (job is not null ? job.Read : ProcessTreeMemory.Sampler(p.Id));
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
                // R-em3d21-2c, R-emi10-2 — the whole tree's memory every tick, against the ceiling; beside the stage once a second.
                ProcessStop? Sample()
                {
                    if (read is null) return null;
                    long? ceiling = Ceiling();
                    if (job is not null && job.Limit != ceiling) job.SetLimit(ceiling);    // lifted by a confirmation mid-run
                    if (job is not null && ceiling is { } held && job.LimitReached())
                        return new(ProcessStopReason.Memory, Math.Max(job.Peak, peakFootprint ?? 0), held, Meshing());
                    if (read() is not { } m || m.ResidentBytes <= 0 && m.FootprintBytes <= 0) return null;
                    peak = Math.Max(peak ?? 0, m.ResidentBytes);
                    peakFootprint = Math.Max(peakFootprint ?? 0, m.FootprintBytes);
                    if (ceiling is { } c && m.FootprintBytes >= c) return new(ProcessStopReason.Memory, m.FootprintBytes, c, Meshing());
                    long near = ceiling ?? watch!.PhysicalBytes;
                    if (memoryNote is null && near > 0 && m.FootprintBytes >= Em3dMemoryVerdict.InUseNoteFraction * near)
                        memoryNote = ceiling is { } limit
                            ? $"{Path.GetFileName(exe)} and the processes it started reached {MachineMemory.Format(m.FootprintBytes)}, " +
                              $"{(100.0 * m.FootprintBytes / limit).ToString("0", CultureInfo.InvariantCulture)} % of {watch!.CeilingOwner} " +
                              $"({MachineMemory.Format(limit)}); at the limit it is stopped."
                            : $"{Path.GetFileName(exe)} and the processes it started reached {MachineMemory.Format(m.FootprintBytes)}, " +
                              $"{(100.0 * m.FootprintBytes / near).ToString("0", CultureInfo.InvariantCulture)} % of this machine's " +
                              $"{MachineMemory.Format(near)}. Past this the operating system swaps, and the run slows sharply; it was " +
                              "started past its size check, so it is not stopped for memory.";
                    if (shown.ElapsedMilliseconds >= 1000)
                    {
                        shown.Restart();
                        control?.SetStageDetail(m.Describe() + (memoryNote is not null ? " — near the memory limit" : ""));
                    }
                    return null;
                }
                ProcessRun Stopped(ProcessStop why)
                {
                    Stamp($"circuitRF is stopping it after {Took(running.Elapsed)}: {(why.Reason == ProcessStopReason.Memory ? "the memory limit" : "the disk space floor")}. " +
                          "Any line after this one comes from stopping it.");
                    Stop(p);
                    lock (gate) return new(-1, false, null, [.. tail], peak, memoryNote, why, peakFootprint);
                }
                ProcessRun Cancelled()
                {
                    Stamp($"Stop was pressed after {Took(running.Elapsed)}. Any line after this one comes from stopping it.");
                    Stop(p);
                    return new(-1, true, null, []);
                }
                var marked = Stopwatch.StartNew();
                while (!p.WaitForExit(100))
                {
                    if (ct.IsCancellationRequested) return Cancelled();
                    try { Drain(); watch?.OnTick?.Invoke(); }
                    catch (OperationCanceledException) { return Cancelled(); }
                    if (Sample() is { } over) return Stopped(over);
                    if (marked.Elapsed >= LogStampInterval)
                    {
                        marked.Restart();
                        Stamp($"Still running after {Took(running.Elapsed)}.");
                    }
                    if (diskRead.ElapsedMilliseconds >= 1000)
                    {
                        diskRead.Restart();
                        // brief-emb-2 R-emb2-6 — the log on disk keeps up with the run (once a second), so a long stage can be
                        // read while it runs rather than only after the program exits.
                        lock (gate) if (!closed) log.Flush();
                        if (BelowFloor(beforeStart: false) is { } full) return Stopped(full);
                    }
                }
                p.WaitForExit();                  // drains the asynchronous readers
                if (ct.IsCancellationRequested) return new(-1, true, null, []);
                // On Windows the kernel refuses the allocation at the limit and the process fails on its own: it is still
                // a stop for memory, not the solver's failure.
                if (job is not null && Ceiling() is { } jobCeiling && job.LimitReached())
                    lock (gate) return new(-1, false, null, [.. tail], peak, memoryNote,
                                           new(ProcessStopReason.Memory, Math.Max(job.Peak, peakFootprint ?? 0), jobCeiling, Meshing()), peakFootprint);
                try { Drain(); }
                catch (OperationCanceledException) { return new(-1, true, null, []); }
                Stamp($"{Path.GetFileName(exe)} ended with exit code {p.ExitCode} after {Took(running.Elapsed)}.");
                lock (gate) return new(p.ExitCode, false, null, [.. tail], peak, memoryNote, PeakFootprint: peakFootprint);
            }
        }
        finally
        {
            lock (gate) closed = true;
        }
    }

    /// <summary>How often a running program's log gets a line saying the time.</summary>
    internal static TimeSpan LogStampInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>One of circuitRF's lines in a program's log: the wall clock with its offset, then what happened.</summary>
    internal static string LogStamp(DateTimeOffset at, string words)
        => $"[circuitRF {at.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}] {words}";

    /// <summary>A run's length for a log line: "42 s", "12 min 05 s", "3 h 07 min".</summary>
    internal static string Took(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min"
         : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s"
         : $"{(int)t.TotalSeconds} s";

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { p.WaitForExit(10_000); } catch (InvalidOperationException) { }
    }

    /// <summary>The program's last lines worth quoting: those carrying <paramref name="marker"/> when
    /// there are any, else its last few.</summary>
    /// <summary>
    /// A failed program's own words, from the last lines of its stdout AND stderr (both go into the tail): the lines
    /// carrying <paramref name="marker"/>, else the last eight. brief-em3d-136 R-em3d136-5b — a marked line that ends in
    /// a colon brings the line after it, because that is where a GPU error states its cause (the device library's
    /// message names the call that failed, then gives the reason — "out of memory" — on a line of its own).
    /// </summary>
    /// <summary>
    /// What a failed Gmsh run said: its first <c>Error</c> lines from the whole log. Gmsh logs an error where it
    /// happens and exits 1 only at the end, after its closing summary, so the tail held "0 errors" and the
    /// error itself was 29,000 lines earlier (field report). The tail is the fallback when the log has none.
    /// </summary>
    internal static string GmshErrors(string logPath, IReadOnlyList<string> tail)
    {
        var errors = GmshErrorLines(logPath);
        return errors.Count == 0 ? Quote(tail, "Error") : "“" + string.Join(" | ", errors) + "”";
    }

    /// <summary>The first distinct <c>Error</c> lines of a Gmsh log, or none when it cannot be read.</summary>
    internal static IReadOnlyList<string> GmshErrorLines(string logPath)
    {
        try
        {
            if (File.Exists(logPath))
                return [.. File.ReadLines(logPath).Select(l => l.Trim())
                           .Where(l => l.StartsWith("Error", StringComparison.Ordinal)).Distinct().Take(5)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [];
    }

    /// <summary>
    /// The lines to name objects from: the errors, and when they name no surface ("Could not recover boundary mesh: error 2"), the
    /// surfaces it left empty, spelled as an error spells them. An empty surface is what the 3D stage then failed on.
    /// </summary>
    internal static IReadOnlyList<string> GmshSurfaceLines(string logPath)
    {
        var errors = GmshErrorLines(logPath);
        if (errors.Any(l => l.Contains("surface ", StringComparison.Ordinal))) return errors;
        var empty = GmshWarningLines(logPath)
            .Where(l => l.Contains("No elements in surface ", StringComparison.Ordinal))
            .SelectMany(l => l[(l.IndexOf("surface ", StringComparison.Ordinal) + 8)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(tag => $"surface {tag}");
        return [.. errors, .. empty];
    }

    /// <summary>brief-dfr35-1 R-dfr35-1-5 — the distinct <c>Warning</c> lines of a Gmsh log, or none when it cannot be read.</summary>
    internal static IReadOnlyList<string> GmshWarningLines(string logPath)
    {
        try
        {
            if (File.Exists(logPath))
                return [.. File.ReadLines(logPath).Select(l => l.Trim())
                           .Where(l => l.StartsWith("Warning", StringComparison.Ordinal))
                           .Select(l => l[(l.IndexOf(':') + 1)..].Trim())
                           // Gmsh's closing summary repeats the count in a box of its own.
                           .TakeWhile(l => !l.StartsWith("Mesh generation error summary", StringComparison.Ordinal))
                           .Where(l => !l.StartsWith("---", StringComparison.Ordinal)).Distinct()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [];
    }

    /// <summary>
    /// Designer feedback round 34 — a Palace stopped by a kill signal (exit 137 = 128 + SIGKILL; −9 where the
    /// runtime reports the signal itself; or a wrapper's own "Killed" line) printed nothing about why, so the failure
    /// read as Palace's. On Linux and in the Linux subsystem that signal is almost always the kernel's out-of-memory
    /// killer. Null when the exit is not a kill. <paramref name="memoryBytes"/> is what the machine (or the
    /// subsystem's VM) has, when known; <paramref name="wsl"/> names <c>.wslconfig</c> as the remedy.
    /// </summary>
    internal static string? KilledSentence(int exitCode, IReadOnlyList<string> tail, long? memoryBytes, bool wsl)
    {
        bool killed = exitCode is 137 or -9 or 9
                      || tail.Any(l => l.Contains(" Killed ", StringComparison.Ordinal) || l.TrimEnd().EndsWith(" Killed", StringComparison.Ordinal));
        if (!killed) return null;
        string has = memoryBytes is > 0 ? $" ({MachineMemory.Format(memoryBytes.Value)} available to it)" : "";
        return $"The operating system stopped Palace with a kill signal (exit code {exitCode}), which almost always means it " +
               $"ran out of memory{has}. " +
               (wsl ? "Give the Linux subsystem more memory (memory= in .wslconfig; its default is half this computer's), " +
                      "or make the problem smaller: a coarser mesh, a smaller solve region or a narrower band."
                    : "Close other programs, or make the problem smaller: a coarser mesh, a smaller solve region or a narrower band.");
    }

    internal static string Quote(IReadOnlyList<string> tail, string marker)
    {
        var lines = tail.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var marked = new List<int>();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
            {
                marked.Add(i);
                if (lines[i].EndsWith(':') && i + 1 < lines.Count && !lines[i + 1].Contains(marker, StringComparison.Ordinal))
                    marked.Add(++i);
            }
        var chosen = marked.Count > 0 ? marked.TakeLast(5).Select(i => lines[i]).ToList() : lines.TakeLast(8).ToList();
        return chosen.Count == 0 ? "(it printed nothing)" : "“" + string.Join(" | ", chosen) + "”";
    }

    /// <summary>
    /// brief-em3d-136 R-em3d136-5a — whether a run asked to use a GPU solved on the CPU, from Palace's log: a build
    /// without GPU support prints a warning ending in <c>reverting to CPU!</c>, and every run's banner names the backends
    /// it used in its <c>Device configuration:</c> line, first the one that solved. A log with neither says nothing,
    /// so it reads as false — the check warns only on evidence.
    /// </summary>
    public static bool RanOnCpu(string logPath)
    {
        try { return File.Exists(logPath) && RanOnCpu(File.ReadLines(logPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <inheritdoc cref="RanOnCpu(string)"/>
    internal static bool RanOnCpu(IEnumerable<string> logLines)
    {
        const string Configuration = "Device configuration:";
        foreach (string raw in logLines)
        {
            if (raw.Contains("reverting to CPU", StringComparison.Ordinal)) return true;
            int at = raw.IndexOf(Configuration, StringComparison.Ordinal);
            if (at < 0) continue;
            string first = raw[(at + Configuration.Length)..].Split(',')[0].Trim();
            return string.Equals(first, "cpu", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    internal static string Sha256(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
