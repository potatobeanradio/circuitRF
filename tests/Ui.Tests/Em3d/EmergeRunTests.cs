using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-5 §6 — EMerge's run: the log reader, the result reader, the eigenmode filter, the tree kill and the memory
//  estimate need nothing installed. The last gate runs EMerge and reports Skipped without one.
//
//  Fixture: testdata/em3d/emerge-run/driven/ holds three rows (8, 9.955 and 12 GHz) of the Cavity's driven run on EMerge
//  3.0.0b2 as the driver wrote them, and its run.json. Golden: cavity.s2p beside them, the Touchstone written from them. To
//  rewrite it after a DELIBERATE change to the writer, run with CRF_WRITE_EMERGE_GOLDENS=1.
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class EmergeRunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-run-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Fixture => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "emerge-run", "driven");

    // ── 1. The log reader ──────────────────────────────────────────────────────────────────────

    /// <summary>EMerge 3.0.0b2's own lines, abridged from the Cavity's driven run: the driver's mesh line, the mesh, each
    /// adaptive iteration's error against its tolerance, and the convergence.</summary>
    private const string CannedLog = """
        2026-10-21 23:31:48.804 | INFO     | emerge:<module>:60 - EMerge v3.0.0b2
        0:00:07.785246  INFO   : Calling GMSH mesher
        0:00:07.997412  INFO   : GMSH Meshing complete! (◮15124, ▲32966)
        {"driver": 1, "edges": 21817, "stage": "meshed", "tets": 15124, "triangles": 32966}
        0:00:07.997980  INFO   : Adaptive sweep: solving at ['8.00 GHz', '9.00 GHz', '10.00 GHz', '11.00 GHz', '12.00 GHz']
        0:00:08.262481  INFO   : Starting serial solve of 5 jobs.
        0:00:08.282039  INFO   : [p00/t00-j001] Calling SuperLU Solver.
        0:00:10.547162  INFO   : Adaptive sweep: 5 samples, estimated error 1.35e+00 (tol 1.0e-03)
        0:00:10.547207  INFO   : Adaptive sweep: solving at ['9.66 GHz']
        0:00:12.514647  INFO   : Adaptive sweep: 10 samples, estimated error 6.45e-05 (tol 1.0e-03)
        0:00:12.903853  INFO   : Post Processing simulation frequency = 11.625 GHz
        0:00:12.906087  INFO   : Adaptive sweep: 11 samples, estimated error 2.85e-04 (tol 1.0e-03)
        0:00:12.906109  INFO   : Adaptive sweep converged with 11 samples.
        """;

    [Fact]
    public void TheLogReader_ReadsTheStagesTheAdaptiveIterationsAndTheTetrahedra()
    {
        var stages = new List<RunProgress>();
        var control = new RunControl { Progress = new Sync(stages.Add), MinReportIntervalMs = 0 };
        EmergeMeshCounts? meshed = null;
        var tracker = new EmergeStageTracker(control, eigenmode: false) { OnMeshed = c => meshed = c };
        tracker.Begin();
        foreach (string line in CannedLog.Split('\n')) tracker.Line(line);

        Assert.Equal(new EmergeMeshCounts(15124, 21817, 32966), meshed);
        Assert.Equal(15124, tracker.Tetrahedra);
        Assert.Equal(11, tracker.Samples);
        Assert.Equal((2.85e-4, 1e-3), tracker.SweepError);
        Assert.True(tracker.Converged);
        var names = stages.Select(s => s.Stage).Distinct().ToList();
        foreach (string n in names) output.WriteLine(n);
        Assert.Contains(EmergeStageTracker.MeshingLabel, names);
        Assert.Contains(names, n => n.StartsWith(EmergeStageTracker.SolvingLabel + ": 9.66 GHz", StringComparison.Ordinal) &&
                                    n.Contains("model change 1.35e+0 (done below 1.0e-3)", StringComparison.Ordinal));

        // A sweep stopped at its limit reads as not converged, with its error.
        Assert.Equal(new EmergeSweepDone(100, false, 2.1e-3),
                     EmergeLog.Read("0:01:00.000000  WARNING: Adaptive sweep stopped at max_samples=100 with estimated error 2.10e-03."));
        Assert.Null(EmergeLog.Read("0:00:07.780342  INFO   : Adding PEC to surface tags [58, 59](dim = 2)"));
    }

    // ── 2. The result reader ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheResultReader_BuildsTheDataSet_AndTheTouchstone_ByteForByteAgainstItsGolden()
    {
        var facts = EmergeResults.ReadFacts(Fixture);
        Assert.Equal(("3.0.0b2", 15124L, 109566L, 11, true, 2.85e-4, true), (facts.Version, facts.Tetrahedra, facts.Unknowns, facts.Samples,
                     facts.Adaptive, facts.Error, facts.Converged));
        Assert.Empty(facts.Warnings);
        var sweep = EmergeResults.ReadDriven(Fixture, 2, out string? error);
        Assert.True(sweep is not null, error);
        Assert.Equal([8e9, 9.955e9, 12e9], sweep.FrequenciesHz);
        Assert.Equal(0.22312475, sweep.S[1][1, 0].Magnitude, 6);

        var (problem, setup, lowering) = Cavity();
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        sweep = EmergeResults.AtPortReferences(sweep, ports, out bool renormalised);
        Assert.False(renormalised);
        var data = EmergeResults.BuildDataSet(sweep, ports, facts);
        var s = data["S"];
        Assert.Equal(sweep.S[1][1, 0], At(s, 1, 1, 0));
        Assert.Equal(109566, data.CubesIn(EmergeResults.Group)["DegreesOfFreedom"].RealValues[0]);

        string snpBase = Path.Combine(_root, "cavity");
        Assert.Null(EmergeResults.WriteSnp(data, snpBase, setup, problem, lowering, EmergeSettings.Resolve(setup.Emerge), facts, "3.0.0b2",
                                           TimeSpan.FromSeconds(6), 2_590_000_000, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)));
        string written = File.ReadAllText(snpBase + ".s2p");
        string golden = Path.Combine(Fixture, "cavity.s2p");
        if (Environment.GetEnvironmentVariable("CRF_WRITE_EMERGE_GOLDENS") == "1") File.WriteAllText(golden, written);
        Assert.Equal(File.ReadAllText(golden), written);
    }

    /// <summary>Designer round 28 — a run whose sparse solver failed without raising wrote S = −I at every frequency and was
    /// reported solved. A zero field reads exactly that; a real sweep has no such frequency.</summary>
    [Fact]
    public void ASweepWithNoField_IsFound_AndARealSweepIsNot()
    {
        var real = EmergeResults.ReadDriven(Fixture, 2, out string? error);
        Assert.True(real is not null, error);
        Assert.Empty(EmergeResults.NoFieldFrequencies(real));

        Complex[,] MinusI(int n) { var m = new Complex[n, n]; for (int i = 0; i < n; i++) m[i, i] = -1; return m; }
        var empty = real with { S = [MinusI(2), real.S[1], MinusI(2)] };
        Assert.Equal([0, 2], EmergeResults.NoFieldFrequencies(empty));
    }

    /// <summary>brief-dfr28-3 R-dfr28-3-1 — the driver's refusal of a solve that is not finite names its cause, so the run is
    /// forwarded as a solve's refusal; a terminal face's names none.</summary>
    [Fact]
    public void ADriverRefusal_ReadsItsCause()
    {
        Assert.Equal("one element", EmergeLog.DriverRefusal(
            ["""{"cause": "non-finite-solve", "driver": 7, "error": "one element", "refused": true}"""], out string? cause));
        Assert.Equal(EmergeLog.NonFiniteSolveCause, cause);
        Assert.Equal("a face", EmergeLog.DriverRefusal(["""{"driver": 7, "error": "a face", "refused": true}"""], out cause));
        Assert.Null(cause);
    }

    /// <summary>
    /// A lossy sheet on a refined mesh: EMerge 3.0.0b2 split the sheet's unknowns only on the first mesh, and its second pass stopped
    /// with a TypeError. The driver splits each new basis, as 3.0.0b3 does.
    /// </summary>
    [EmergeFact]
    public void ALossySheet_SolvesOnARefinedMesh()
    {
        string ws = Path.Combine(_root, "ws");
        CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag"), ws);
        string cem = Path.Combine(ws, "thru", "em", "sheets.cem");
        var setup = EmSetupPersistence.LoadFromFile(cem);
        setup.Solver3D = EmergeSolver.Id;
        setup.Frequency = new CircuitRF.Core.Design.FrequencySpec("1e9", "1e9", 1);
        setup.AdditionalFrequencies = [new CircuitRF.Core.Design.FrequencySpec("2e9", "2e9", 1)];
        setup.Emerge = new CemEmerge { Quality = PalaceQuality.Standard, EdgeRefinement = 0.05, AdaptiveMesh = 1 };
        var source = EmSetupResolver.Resolve(cem, setup.LayoutRef, Path.Combine(ws, ".cws"), new CircuitRF.Design.Layout.TechnologyCache()).Source;
        var r = EmRunService.Run(setup, source, Path.Combine(ws, "results"), emergeDevice: EmergeDevice.Cpu);
        Assert.True(r.Status == EmRunStatus.Ok, r.Error);
    }

    /// <summary>
    /// brief-dfr28-3 R-dfr28-3-1 — the driver's check on EMerge's solves, with NaN injected: a matrix holding a non-finite
    /// entry is refused before it is solved, naming the one flat tetrahedron that owns those unknowns, where it is and what
    /// it lies in; a finite matrix whose solver returns NaN is refused after. On the TP149 board EMerge solved the first and
    /// wrote S = −I.
    /// </summary>
    [EmergeFact]
    public void ANonFiniteSolve_IsRefused_NamingItsElement()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, EmergeScriptWriter.DriverFile), EmergeScriptWriter.Driver);
        File.WriteAllText(Path.Combine(_root, "inject.py"), """
            import json, sys, types
            import numpy as np, scipy.sparse as sp
            sys.path.insert(0, sys.argv[1])
            import circuitrf_emerge as d
            try:
                from emerge._emerge.solveroutine import SolveRoutine
            except ImportError:
                from emerge._emerge.solver import SolveRoutine
            # One tetrahedron, flat (all four corners at z = 0), its 20 unknowns the matrix's.
            nodes = np.array([[0.1, 0.101, 0.1, 0.1005], [0.2, 0.2, 0.201, 0.2005], [0.0, 0.0, 0.0, 0.0]])
            mesh = types.SimpleNamespace(nodes=nodes, tets=np.arange(4).reshape(4, 1), n_tets=1, vtag_to_tet={7: [0]})
            sim = types.SimpleNamespace(mesh=mesh, mw=types.SimpleNamespace(basis=types.SimpleNamespace(tet_to_field=np.arange(20).reshape(20, 1))))
            model = {"names": ["background", "Dielectric 1"], "volumes": {7: (1, 0)}}
            heard = d.Heard()
            b, ids = np.ones((20, 1), complex), np.arange(20)
            out = []
            def attempt(A):
                try:
                    SolveRoutine.solve(None, A, b, ids)
                    out.append(None)
                except d.Refused as e:
                    out.append({"cause": e.cause, "error": str(e)})
            assert d.guard_solves(sim, model, heard) is None
            attempt(sp.csc_matrix(np.full((20, 20), np.nan, complex)))
            SolveRoutine.solve = lambda self, A, b, ids, *a, **k: (np.full(b.shape, np.nan, complex), None)
            d.guard_solves(sim, model, heard)
            attempt(sp.identity(20, dtype=complex, format="csc"))
            print(json.dumps(out))
            """);
        string python = SolverDiscovery.ReadinessFor(EmergeSolver.Id).Single(r => r.Tool == SolverTool.Emerge).Installation!.Path;
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
        foreach (string a in new[] { "-I", "inject.py", _root }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
        output.WriteLine(stdout);
        using var doc = JsonDocument.Parse(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
        var (before, after) = (doc.RootElement[0], doc.RootElement[1]);
        Assert.Equal(EmergeLog.NonFiniteSolveCause, before.GetProperty("cause").GetString());
        string where = before.GetProperty("error").GetString()!;
        Assert.Contains("1 degenerate element: a tetrahedron at (100.3750, 200.3750, 0.0000) mm, in 'Dielectric 1'", where);
        Assert.Contains("its volume is 0 um^3", where);
        Assert.Contains("stopped before solving", where);
        Assert.Equal(EmergeLog.NonFiniteSolveCause, after.GetProperty("cause").GetString());
        Assert.Contains("returned a solution that is not a number from a matrix that is finite", after.GetProperty("error").GetString());
    }

    // ── 3. The eigenmode filter ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEigenFilter_DropsNullSpaceAndBelowStartSolutions_CountsThem_AndNumbersTheRestUpward()
    {
        Complex[] f = [new(0.05e9, 0.12e9), new(11.47e9, 2.79e9), new(9.957e9, 0.0079e9), new(0.051e9, 0.13e9), new(8.06e9, 2.6e9), new(8.7e9, 0.1e9)];
        double[] q = [0.2, 2.06, 633, 0.2, 1.55, 40];
        var modes = EmergeResults.FilterModes(f, q, 9e9);
        Assert.Equal(3, modes.Dropped);                 // the two near 0 Hz and 8.06 GHz; 8.7 GHz is within 5 % of the start
        Assert.Equal(9e9 * 0.95, modes.FloorHz);
        Assert.Equal([new Em3dMode(1, 8.7e9, 40), new Em3dMode(2, 9.957e9, 633), new Em3dMode(3, 11.47e9, 2.06)], modes.Kept);
    }

    // ── 4. The tree kill ───────────────────────────────────────────────────────────────────────

    /// <summary>A stand-in interpreter that starts a grandchild and waits on it: cancelling the run kills both.</summary>
    [NonWindowsFact("the stand-in interpreter is a shell script")]
    public void Cancelling_KillsTheWholeProcessTree_GrandchildIncluded()
    {
        Directory.CreateDirectory(_root);
        string python = Path.Combine(_root, "python");
        File.WriteAllText(python, "#!/bin/sh\nsleep 300 &\necho $! > grandchild.pid\necho '0:00:00.100000  INFO   : Calling GMSH mesher'\nwait\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cts = new CancellationTokenSource();
        var psi = EmergeRun.StartInfo(python, _root, new Dictionary<string, string>());
        var run = Task.Run(() => EmergeRun.Solve(psi, null, cts.Token, new EmergeStageTracker(null, false), 0));

        string pidFile = Path.Combine(_root, "grandchild.pid");
        var waited = Stopwatch.StartNew();
        while (!(File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0) && waited.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
        int grandchild = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Alive(grandchild));
        cts.Cancel();
        Assert.True(run.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(run.Result.Cancelled);
        waited.Restart();
        while (Alive(grandchild) && waited.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(20);
        Assert.False(Alive(grandchild), $"the grandchild {grandchild} outlived the cancelled run");
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ── 5. The memory estimate ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMemoryEstimate_IsAPureFunctionOfTheUnknowns_AndTheGateReadsIt()
    {
        Assert.Equal(109566, EmergeMemory.Unknowns(21817, 32966, 2));
        Assert.Equal(21817, EmergeMemory.Unknowns(21817, 32966, 1));
        // The Cavity on the fast solver peaked at 2.59 GB: the estimate is above that, and not wildly so.
        long fast = EmergeMemory.PeakBytes(109566, EmergeSpeedTier.Fast, eigenmode: false);
        Assert.InRange(fast, 2_590_000_000, 3_200_000_000);
        Assert.True(EmergeMemory.PeakBytes(109566, EmergeSpeedTier.Baseline, false) > fast);
        Assert.Equal(EmergeMemory.PeakBytes(109566, EmergeSpeedTier.Baseline, false), EmergeMemory.PeakBytes(109566, EmergeSpeedTier.Fast, true));
        Assert.Equal(EmergeMemory.PeakBytes(109566, EmergeSpeedTier.Baseline, false), EmergeMemory.PeakBytes(109566, EmergeSpeedTier.Unknown, false));

        var mesh = new EmergeMeshCounts(15124, 21817, 32966);
        Assert.Equal(Em3dMemoryLevel.Fits, EmergeMemory.Verdict(mesh, EmergeSettings.Default, EmergeSpeedTier.Fast, false, 16L << 30).Level);
        var severe = EmergeMemory.Verdict(mesh, EmergeSettings.Default, EmergeSpeedTier.Fast, false, 1L << 30);
        Assert.Equal(Em3dMemoryLevel.Severe, severe.Level);
        Assert.StartsWith("EMerge made 15,124 tetrahedra, 109,566 unknowns. EMerge may need about", severe.Text);
        Assert.Contains("EMerge.ElementOrder: 1", severe.Text);
    }

    // ── 6. With EMerge installed ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The 3D Eigenmode example's Cavity, driven, on EMerge: its S21 notch on the 5 MHz grid is at 9.955 GHz, where Palace and
    /// openEMS put it; and the CLI and the 3D editor's Simulate write the same .s2p: every header line byte for byte but the two
    /// that differ between any two runs (the write stamp and what the run cost), the frequency column exactly, and S to 1e-9.
    /// <para>Why not S byte for byte: EMerge's multi-threaded solve is not bit-reproducible. Two runs of one input through the
    /// same path differed by up to 2e-12 in S, which printed to ten digits moves the last digit of a few rows (137 of 801 here);
    /// on one thread two runs were identical. The CLI and Simulate both run on every core (Automatic). Under full-suite load one
    /// printed value moved by 1.01e-9, hence 1e-8: a different mesh or setup moves S by orders of magnitude more.</para>
    /// </summary>
    [EmergeFact]
    public void TheCavity_Driven_OnEMerge_NotchesAt9955MHz_AndTheCliWritesTheGuisBytes()
    {
        string ws = Path.Combine(_root, "ws");
        CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Eigenmode"), ws);
        string path = Path.Combine(ws, "Cavity", "3d", "Cavity.c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == "Driven").Setup!, path);
        setup.Solver3D = EmergeSolver.Id;
        string results = Path.Combine(ws, "results");

        var gui = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), results);
        foreach (string n in gui.Notes ?? []) output.WriteLine("note: " + n);
        foreach (string w in gui.Warnings ?? []) output.WriteLine("warning: " + w);
        Assert.True(gui.Status == EmRunStatus.Ok, gui.Error);
        Assert.Contains(gui.Notes!, n => n.StartsWith("EMerge done in", StringComparison.Ordinal));
        var s = gui.Data!["S"];
        var f = s.Axes[0].Values;
        int notch = Enumerable.Range(0, f.Length).MinBy(k => At(s, k, 1, 0).Magnitude);
        output.WriteLine($"notch {f[notch] / 1e9:F3} GHz, {20 * Math.Log10(At(s, notch, 1, 0).Magnitude):F2} dB");
        Assert.Equal(9.955e9, f[notch], 1e3);
        string snp = gui.SnpPath!;
        var guiLines = File.ReadAllLines(snp);
        File.Delete(snp);

        var (code, stdout, stderr) = RunCli(ws, "em", path, "--setup", "Driven", "--solver", "emerge");
        output.WriteLine(stderr);
        Assert.True(code == 0, stdout + stderr);
        var cliLines = File.ReadAllLines(snp);
        Assert.Equal(Header(guiLines), Header(cliLines));
        var (guiRows, cliRows) = (Rows(guiLines), Rows(cliLines));
        Assert.Equal(guiRows.Count, cliRows.Count);
        for (int k = 0; k < guiRows.Count; k++)
        {
            Assert.Equal(guiRows[k][0], cliRows[k][0]);
            for (int c = 1; c < guiRows[k].Length; c++) Assert.Equal(guiRows[k][c], cliRows[k][c], 1e-8);
        }
    }

    private static string[] Header(IEnumerable<string> lines)
        => [.. lines.Where(l => (l.StartsWith('!') || l.StartsWith('#')) &&
                                !l.Contains("circuitRF-EM written:", StringComparison.Ordinal) &&
                                !l.Contains(EmProvenanceStamp.StampPrefix, StringComparison.Ordinal) &&
                                !l.Contains(Em3dRunService.CostProvenancePrefix, StringComparison.Ordinal))];

    private static List<double[]> Rows(IEnumerable<string> lines)
        => [.. lines.Where(l => l.Length > 0 && !l.StartsWith('!') && !l.StartsWith('#'))
                    .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray())];

    /// <summary>S[f, i, j] of a two-port's [f, i, j] cube.</summary>
    private static Complex At(RfCore.Data.DataCube s, int k, int i, int j) => s.ComplexValues[(k * 2 + i) * 2 + j];

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The 3D Eigenmode example's Cavity, its Driven setup on EMerge, lowered.</summary>
    private static (Em3dProblem Problem, EmSetup Setup, EmergeLowering Lowering) Cavity()
    {
        string path = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Eigenmode", "Cavity", "3d", "Cavity.c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == "Driven").Setup!, path);
        setup.Solver3D = EmergeSolver.Id;
        var assembled = C3dProblemAssembly.Assemble(setup, doc, path, WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path)));
        Assert.True(assembled.Ok, assembled.Refusal);
        var lowering = EmergeScriptWriter.Write(assembled.Problem!, EmergeSettings.Resolve(setup.Emerge));
        Assert.True(lowering.Ok, lowering.Refusal);
        return (assembled.Problem!, setup, lowering);
    }

    internal static void CopyDirectory(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>The built CLI as a process (EmCliVerbTests' pattern: the BUILT dll, both pipes drained concurrently).</summary>
    internal static (int ExitCode, string StdOut, string StdErr) RunCli(string cwd, params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(EmergeRunTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private sealed class Sync(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
