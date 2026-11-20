using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-12 §Gates — EMerge's memory lines on 3.0.10 and its iterative solver (CORVID). Gates 1, 2 and 4 need nothing
//  installed: the recorded measurements, a stand-in driver that answers as the real one does, and the format. Gate 3 runs a
//  real EMerge (Category=Benchmark, Skipped without one).
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class EmergeIterativeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-iterative-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── Gate 1. The memory lines hold every measurement ─────────────────────────────────────────

    /// <summary>Each solver's line is at or above every run measured on it and at most its <see cref="EmergeMemory.Ceiling"/>
    /// times the larger run of each problem: 1.3 for CORVID and the eigensolver. A SPARTA run, CPU or GPU, is judged from SPARTA's
    /// own analysis of its first system, and that estimate holds every run within 1.3; its unknowns-only line, which explain quotes,
    /// cannot (EmergeMemory says why). Every solver a run can use has measurements at three sizes or more, from about 50,000
    /// unknowns, each run at least twice.</summary>
    [Fact]
    public void EachLine_HoldsEveryMeasurement_AtOrAbove_AndWithin1Point3()
    {
        foreach (var group in EmergeMemory.Measurements.GroupBy(m => m.Solve))
        {
            Assert.True(group.Count() >= 3, $"{group.Key}: {group.Count()} sizes");
            Assert.InRange(group.Min(p => p.Unknowns), 50_000, 60_000);
            Assert.True(EmergeMemory.Ceiling(group.Key) is >= 1.3 and < 1.5);
            Assert.Equal(1.3, EmergeMemory.AnalysisCeiling);
            foreach (var p in group)
            {
                Assert.True(p.PeakMegabytes.Count >= 2, $"{group.Key} {p.Problem}: run once");
                long estimate = EmergeMemory.PeakBytes(p.Unknowns, group.Key);
                double ratio = (double)estimate / p.LargestBytes;
                output.WriteLine($"{group.Key} {p.Problem} {p.Unknowns:N0}: measured {string.Join(" / ", p.PeakMegabytes)} MB, " +
                                 $"estimate {MachineMemory.Format(estimate)} ({ratio:0.00}x)");
                Assert.True(estimate >= p.LargestBytes, $"{group.Key} {p.Problem}: estimate below a measurement");
                Assert.True(ratio <= EmergeMemory.Ceiling(group.Key), $"{group.Key} {p.Problem}: {ratio:0.000}x the largest");
                // what a SPARTA run is judged on: SPARTA's own analysis of its first system, within 1.3
                Assert.Equal(EmergeMemory.PricedFromAnalysis(group.Key), p.AnalysisMegabytes is not null);
                if (p.AnalysisMegabytes is not { } analysisMb) continue;
                long fromAnalysis = EmergeMemory.PeakBytesFromAnalysis(p.Unknowns, analysisMb * 1_000_000L, group.Key);
                double r2 = (double)fromAnalysis / p.LargestBytes;
                output.WriteLine($"    from SPARTA's analysis ({analysisMb} MB of factor and fronts): {MachineMemory.Format(fromAnalysis)} ({r2:0.00}x)");
                Assert.True(fromAnalysis >= p.LargestBytes, $"{group.Key} {p.Problem}: the analysis estimate is below a measurement");
                Assert.True(r2 <= EmergeMemory.AnalysisCeiling, $"{group.Key} {p.Problem}: {r2:0.000}x from the analysis");
            }
        }
        Assert.Equal(Enum.GetValues<EmergeLinearSolve>().Length, EmergeMemory.Measurements.Select(m => m.Solve).Distinct().Count());
        Assert.Equal(1.3, EmergeMemory.Ceiling(EmergeLinearSolve.Corvid));
        Assert.Equal(1.3, EmergeMemory.Ceiling(EmergeLinearSolve.SuperLuEigenmode));
    }

    // ── Gate 2. The remedy, offered and taken ───────────────────────────────────────────────────

    /// <summary>A mesh of 1.4 million unknowns on a 20 GB machine: SPARTA's line is past it and CORVID's is not, so the question
    /// offers CORVID first; answered with it, the waiting driver is told "iterative" and the tolerance, its run.json says
    /// CORVID, and the run's Notes say it was chosen and what its estimate was. The driver is a stand-in that answers as
    /// circuitrf_emerge.py does.</summary>
    [NonWindowsFact("the stand-in driver is a shell script")]
    public void ADirectSolveThatDoesNotFit_IsOfferedCorvid_AndARunStartedOnItReportsCorvid()
    {
        const long physical = 20_000_000_000;
        var mesh = new EmergeMeshCounts(100_000, 300_000, 400_000);
        Em3dMemoryVerdict? asked = null;
        var gate = new Em3dRunService.MemoryGate(v =>
        {
            asked = v;
            v.Choice.Remedy = v.Applicable[0];
            return true;
        });
        var log = new Em3dRunService.RunLog();
        var check = new Em3dRunService.EmergeMeshCheck(gate, log, EmergeSettings.Default, EmergeSpeedTier.Fast, eigenmode: false,
                                                       scattering: false, gpuFrom: null, iterativeAvailable: true, physical);
        var ran = RunStandIn(mesh, check);

        Assert.NotNull(asked);
        Assert.True(EmergeMemory.PeakBytes(1_400_000, EmergeLinearSolve.Sparta) > physical);
        Assert.True(asked!.Applicable[0].Iterative);
        Assert.Equal(EmergeMemory.PeakBytes(1_400_000, EmergeLinearSolve.Corvid), asked.Applicable[0].EstimateBytes);
        Assert.Contains("EMerge.LinearSolver: Iterative", asked.Text, StringComparison.Ordinal);
        Assert.Equal(0, ran.ExitCode);
        Assert.Equal("iterative 1E-06", File.ReadAllText(Path.Combine(_root, "answer.txt")).Trim());

        var facts = EmergeResults.ReadFacts(_root);
        Assert.True(facts.Iterative);
        Assert.Equal(new EmergeIterations(4, 9, 7.5), facts.Iterations);
        Assert.NotNull(check.TookIterative);
        output.WriteLine(check.TookIterative);
        Assert.StartsWith("EMerge solved this run with its iterative solver (CORVID), as chosen when its memory check asked: about " +
                          MachineMemory.Format(EmergeMemory.PeakBytes(1_400_000, EmergeLinearSolve.Corvid)), check.TookIterative);
        Assert.Contains("in at most 9 iterations", Em3dRunService.IterativeNote(facts), StringComparison.Ordinal);
        Assert.Contains(log.Warnings, w => w.Contains("started with the iterative linear solver, as chosen", StringComparison.Ordinal));
    }

    /// <summary>A mesh that fits is not asked about, and the driver is told to solve as its setup says; an eigenmode run, whose
    /// eigensolver does not use CORVID, is never offered it.</summary>
    [NonWindowsFact("the stand-in driver is a shell script")]
    public void AMeshThatFits_IsAnsweredSolve_AndAnEigenmodeRunIsNeverOfferedCorvid()
    {
        var gate = new Em3dRunService.MemoryGate(_ => throw new InvalidOperationException("asked"));
        var check = new Em3dRunService.EmergeMeshCheck(gate, new Em3dRunService.RunLog(), EmergeSettings.Default, EmergeSpeedTier.Fast,
                                                       false, false, null, true, 64_000_000_000);
        Assert.Equal(0, RunStandIn(new EmergeMeshCounts(10_000, 20_000, 30_000), check).ExitCode);
        Assert.Equal("solve", File.ReadAllText(Path.Combine(_root, "answer.txt")).Trim());
        Assert.Null(check.TookIterative);

        var eigen = EmergeMemory.Verdict(new EmergeMeshCounts(100_000, 300_000, 400_000), EmergeSettings.Default, EmergeLinearSolve.SuperLuEigenmode,
                                         20_000_000_000, iterativeAvailable: true, eigenmode: true);
        Assert.DoesNotContain(eigen.Remedies, r => r.Iterative);
    }

    /// <summary>
    /// A SPARTA run is judged at SPARTA's analysis, not at the mesh: the stand-in is told "solve analyze", reports an analysis, and is
    /// answered there. An analysis predicting a factor too large for the machine is offered CORVID and the answer carries it; one
    /// that fits is told to solve, and its unknowns-only estimate (which would have asked) is not what decided it.
    /// </summary>
    [NonWindowsFact("the stand-in driver is a shell script")]
    public void ASpartaRun_IsJudgedAtItsAnalysis_AndOfferedCorvidThere()
    {
        const long physical = 20_000_000_000;
        var mesh = new EmergeMeshCounts(100_000, 300_000, 400_000);
        Em3dMemoryVerdict? asked = null;
        var gate = new Em3dRunService.MemoryGate(v => { asked = v; v.Choice.Remedy = v.Applicable[0]; return true; });
        var check = new Em3dRunService.EmergeMeshCheck(gate, new Em3dRunService.RunLog(), EmergeSettings.Default, EmergeSpeedTier.Fast,
                                                       false, false, null, true, physical, analysisAvailable: true);
        Assert.Equal(0, RunStandIn(mesh, check, (13_000_000_000, 13_000_000_000)).ExitCode);
        Assert.Equal(["solve analyze", "iterative 1E-06"], File.ReadAllLines(Path.Combine(_root, "answer.txt")));
        Assert.NotNull(asked);
        Assert.Contains("whose analysis predicts a 13.0 GB factor and 13.0 GB of fronts", asked!.Text, StringComparison.Ordinal);
        Assert.True(asked.Applicable[0].Iterative);
        Assert.True(EmergeResults.ReadFacts(_root).Iterative);

        // the same mesh with a small analysis: no question, though the unknowns-only line puts it past this machine
        Assert.True(EmergeMemory.PeakBytes(1_400_000, EmergeLinearSolve.Sparta) > physical);
        var quiet = new Em3dRunService.EmergeMeshCheck(new Em3dRunService.MemoryGate(_ => throw new InvalidOperationException("asked")),
                                                       new Em3dRunService.RunLog(), EmergeSettings.Default, EmergeSpeedTier.Fast,
                                                       false, false, null, true, physical, analysisAvailable: true);
        Assert.Equal(0, RunStandIn(mesh, quiet, (2_000_000_000, 2_000_000_000)).ExitCode);
        Assert.Equal(["solve analyze", "solve"], File.ReadAllLines(Path.Combine(_root, "answer.txt")));
    }

    /// <summary>The stand-in: prints the driver's mesh line, waits for its answer as the driver does (and, told "solve analyze", prints
    /// an analysis and waits again), then writes run.json.</summary>
    private PalaceRun.ProcessRun RunStandIn(EmergeMeshCounts mesh, Em3dRunService.EmergeMeshCheck check, (long Factor, long Front)? analysis = null)
    {
        Directory.CreateDirectory(_root);
        string script = Path.Combine(_root, "driver.sh");
        string analyzed = analysis is { } a
            ? $"echo '{{\"driver\": 12, \"factorBytes\": {a.Factor}, \"frontBytes\": {a.Front}, \"n\": 1300000, \"stage\": \"analyzed\"}}'\n"
            : "";
        File.WriteAllText(script, "#!/bin/sh\n" +
            $"echo '{{\"driver\": 12, \"edges\": {mesh.Edges}, \"stage\": \"meshed\", \"tets\": {mesh.Tetrahedra}, \"triangles\": {mesh.Triangles}}}'\n" +
            "read answer\necho \"$answer\" > answer.txt\n" +
            "if [ \"$answer\" = \"solve analyze\" ]; then\n" + analyzed + "read answer\necho \"$answer\" >> answer.txt\nfi\n" +
            "case \"$answer\" in iterative*) s=CORVID;; stop) exit 3;; *) s=SPARTA;; esac\n" +
            "printf '{\"sparseSolver\": \"%s\", \"iterations\": {\"solves\": 4, \"most\": 9, \"mean\": 7.5}}' \"$s\" > run.json\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        EmergeStageTracker tracker = null!;
        tracker = new EmergeStageTracker(null, eigenmode: false)
        {
            OnMeshed = m => check.Admit(m, tracker),
            OnAnalyzed = x => check.AdmitAnalysis(x, tracker),
        };
        var psi = EmergeRun.StartInfo(script, _root, new Dictionary<string, string>(), ask: true);
        Assert.Equal(EmergeRun.AskArgument, psi.ArgumentList[^1]);
        return EmergeRun.Solve(psi, null, CancellationToken.None, tracker, 0);
    }

    // ── An EMerge the driver is not written for ─────────────────────────────────────────────────

    /// <summary>EMerge before 3.0.8 runs nothing, and the refusal says how to upgrade: circuitRF's own install from Settings or the
    /// CLI, a Python of the user's own with pip. Versions order numerically, a pre-release before its release.</summary>
    [Fact]
    public void AnEMergeOlderThan308_IsRefused_WithHowToUpgrade()
    {
        Assert.True(EmergeSolver.IsBefore("3.0.0b2", "3.0.0"));
        Assert.True(EmergeSolver.IsBefore("3.0.0", "3.0.8"));
        Assert.True(EmergeSolver.IsBefore("3.0.8", "3.0.10"));
        Assert.False(EmergeSolver.IsBefore("3.0.10", "3.0.8"));
        Assert.False(EmergeSolver.IsBefore("3.0.8", "3.0.8"));

        SolverInstallation At(string version, SolverHowFound how)
            => new(SolverTool.Emerge, "/x/env/bin/python", version, null, "", how, "", version);
        Assert.Null(EmergeSolver.TooOldRefusal(At("3.0.10", SolverHowFound.Installed)));
        Assert.Null(EmergeSolver.TooOldRefusal(At("3.0.8", SolverHowFound.Settings)));

        string own = EmergeSolver.TooOldRefusal(At("3.0.0", SolverHowFound.Installed))!;
        output.WriteLine(own);
        Assert.StartsWith("EMerge 3.0.0 is older than 3.0.8", own);
        Assert.Contains("Settings ▸ Solvers, where the EMerge row offers Update to EMerge 3.0.10…", own, StringComparison.Ordinal);
        Assert.Contains("`circuitrf solver install emerge --yes`", own, StringComparison.Ordinal);

        string theirs = EmergeSolver.TooOldRefusal(At("3.0.0b2", SolverHowFound.Settings))!;
        output.WriteLine(theirs);
        Assert.Contains("`\"/x/env/bin/python\" -m pip install --upgrade \"emerge[corvid]==3.0.10\"`", theirs, StringComparison.Ordinal);
        Assert.Contains("Install EMerge…", theirs, StringComparison.Ordinal);
    }

    // ── Gate 4. The setting round-trips, and explain prices both solvers ────────────────────────

    [Fact]
    public void TheLinearSolver_RoundTripsThroughTheCemAndTheC3d_AndExplainPricesBothSolvers()
    {
        var set = new EmSetup { Name = "S", Solver3D = EmergeSolver.Id, Emerge = new CemEmerge { LinearSolver = PalaceLinearSolver.Iterative } };
        string written = EmSetupPersistence.Serialize(set);
        Assert.Contains("\"LinearSolver\": \"Iterative\"", written, StringComparison.Ordinal);
        var read = EmSetupPersistence.Deserialize(written);
        Assert.True(EmergeSettings.Resolve(read.Emerge).Iterative);
        Assert.Equal(written, EmSetupPersistence.Serialize(read));
        var embedded = EmSetupPersistence.FromEmbedded(EmSetupPersistence.ToEmbedded(read));
        Assert.Equal(PalaceLinearSolver.Iterative, embedded.Emerge!.LinearSolver);
        // Direct is the default, and an omitted field
        Assert.False(EmergeSettings.Default.Iterative);
        Assert.True(new CemEmerge { LinearSolver = null }.IsEmpty);

        // problem.json carries it, and its tolerance, only on an iterative run
        var problem = Line();
        Assert.DoesNotContain("linearSolver", EmergeScriptWriter.Write(problem, EmergeSettings.Default).ProblemJson, StringComparison.Ordinal);
        string iterative = EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(read.Emerge)).ProblemJson!;
        Assert.Contains("\"linearSolver\": \"iterative\"", iterative, StringComparison.Ordinal);
        Assert.Contains("\"iterativeTolerance\": 1E-06", iterative, StringComparison.Ordinal);

        foreach (var (setup, first) in new[] { (set, "Iterative (this setup's), CORVID"), (new EmSetup { Name = "D", Solver3D = EmergeSolver.Id }, "Direct (this setup's), SPARTA") })
        {
            var row = ExplainEm3d.EmergeSize(problem, setup);
            output.WriteLine(row.Note);
            Assert.Equal("per-unknown", row.Kind);
            Assert.Contains(first, row.Note, StringComparison.Ordinal);
            Assert.Contains("SPARTA, about", row.Note, StringComparison.Ordinal);
            Assert.Contains("CORVID, about", row.Note, StringComparison.Ordinal);
        }
    }

    private static Em3dProblem Line()
    {
        const double Um = 1e-6;
        var pec = Em3dBoundaryKind.Pec;
        Point2[] outline = [new(0, 0), new(300 * Um, 0), new(300 * Um, 5 * Um), new(0, 5 * Um)];
        Em3dPort Port(int n, double x, double inward) => new(n, $"P{n}", "strip", "airbox/zmin", new Point3(x, 0, 0), new Point3(x, 5 * Um, 10 * Um),
            new Point3(0, 0, 1), 50, new Em3dReferencePlane(new Point3(x, 2.5 * Um, 5 * Um), new Point3(inward, 0, 0), 0));
        return new Em3dProblem([], [new Em3dSheet("strip", "aluminium", outline, [], 10 * Um, 1 * Um, 0)],
            [new Em3dMaterial("aluminium", 1, null, 0, 1, 3.77e7)], [Port(1, 0, 1), Port(2, 300 * Um, -1)],
            new Em3dAirBox(new Point3(-20 * Um, -60 * Um, 0), new Point3(320 * Um, 65 * Um, 71 * Um), new Em3dFaces(pec, pec, pec, pec, pec, pec)),
            new Em3dFrequency(0.1e9, 40e9, 5, Em3dSweepKind.Log), 20);
    }

    // ── Gate 3. CORVID against SPARTA, a real EMerge ────────────────────────────────────────────

    /// <summary>The 3D Wave Ports Launch (a lumped and a wave port) and Coupled Microstrip (two terminal groups) on one mesh, a
    /// point sweep, each solved by SPARTA and by CORVID: S within 1e-6 everywhere.</summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void Corvid_MeetsSparta_OnTheLaunchAndTheCoupledMicrostrip()
    {
        foreach (string example in new[] { "Launch", "Coupled Microstrip" })
        {
            var direct = Run(example, PalaceLinearSolver.Direct);
            var iterative = Run(example, PalaceLinearSolver.Iterative);
            Assert.Contains(iterative.Notes!, n => n.StartsWith("EMerge's iterative solver (CORVID) solved every system", StringComparison.Ordinal));
            var (a, b) = (direct.Data!["S"], iterative.Data!["S"]);
            Assert.Equal(a.ComplexValues.Length, b.ComplexValues.Length);
            double worst = a.ComplexValues.Zip(b.ComplexValues, (x, y) => (x - y).Magnitude).Max();
            output.WriteLine($"{example}: max |ΔS| {worst:E2} between SPARTA and CORVID");
            Assert.True(worst <= 1e-6, $"{example}: max |ΔS| {worst:E2}");
        }
    }

    private EmRunResult Run(string example, PalaceLinearSolver solver)
    {
        string ws = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N")[..6]);
        EmergeRunTests.CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Wave Ports"), ws);
        string path = Path.Combine(ws, example, "3d", example + ".c3d");
        var cell = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var setup = cell["Setups"]!.AsArray().Single(x => (string)x!["Name"]! == "Palace")!.DeepClone().AsObject();
        setup["Name"] = "EMerge";
        setup["Solver3D"] = "Emerge";
        setup.Remove("Palace");
        setup["Frequency"]!["NumPoints"] = 5;
        setup["EMerge"] = JsonNode.Parse($$"""{"AdaptiveSweep": false, "SaveFieldsGHz": [], "LinearSolver": "{{solver}}"}""");
        cell["Setups"]!.AsArray().Add(setup);
        File.WriteAllText(path, cell.ToJsonString());
        var doc = C3dPersistence.LoadFromFile(path);
        var em = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == "EMerge").Setup!, path);
        var run = EmRunService.RunThreeDView(em, doc, path, Path.Combine(ws, ".cws"), Path.Combine(ws, "results"), emergeDevice: EmergeDevice.Cpu);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        return run;
    }
}
