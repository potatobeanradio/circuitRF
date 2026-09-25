using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-21 §7 — a Palace run you can watch.
//
//  Gates 1-5, 7 and 10 need nothing installed: the parser reads F0's committed Palace 0.18.1 logs
//  (testdata/em3d/f0/*/palace*/palace.log), and the memory check takes the machine's memory as an
//  argument. Gates 8 and 9 run the smallest existing gate case through Palace, so they skip without
//  it and are Category=Benchmark. Gate 6 is tests/Engine.Tests/Em3d/PhysicalCoresTests.cs.
//
//  The committed stage sequences are testdata/em3d/progress/*.txt. To rewrite them after a
//  DELIBERATE change to the stage wording, run gate 1 with CRF_WRITE_PROGRESS_GOLDENS=1 and review
//  the diff; nothing rewrites them otherwise.
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PalaceProgressTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-progress-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. The parser on F0's logs ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A-bondwire/palace-round-amr", 2, "A-round-amr")]
    [InlineData("B-via/palace", 0, "B-palace")]
    public void Gate1_F0sLogs_GiveTheCommittedStageSequence(string run, int maxPasses, string golden)
    {
        var (tracker, seen) = Feed(F0Log(run), maxPasses, 1e-4);
        string text = Render(seen, tracker);

        string path = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "progress", golden + ".txt");
        if (Environment.GetEnvironmentVariable("CRF_WRITE_PROGRESS_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        output.WriteLine(text);
        Assert.Equal(File.ReadAllText(path), text);

        // The golden's figures, checked against the log's own words rather than against the golden.
        var s = tracker.Summary;
        Assert.True(s.LogRecognised);
        if (golden == "A-round-amr")
        {
            Assert.Equal(146_769, s.InitialElements);
            Assert.Equal(153_510, s.FinalElements);            // F0 §4's table
            Assert.Equal(987_180, s.Unknowns);
            Assert.Equal(2, s.RefinementPasses);
            Assert.Equal(6, s.SweepSamples);
            Assert.Equal(11.9e9, s.PeakMemoryBytes!.Value, 1);
            Assert.Equal(415.4, s.PalaceSeconds!.Value, 0.1);
            Assert.Equal(["Solving: refinement pass 1 of 3", "Solving: refinement pass 2 of 3", "Solving: refinement pass 3 of 3"],
                         Passes(seen));
            Assert.Contains(seen, p => p.Stage.Contains("error 9.36e-3", StringComparison.Ordinal));   // greedy 1, pass 1
            Assert.Contains(seen, p => p.Stage.Contains("error 1.88e-6", StringComparison.Ordinal));   // greedy 4, pass 1
        }
        else
        {
            Assert.Equal(87_467, s.InitialElements);
            Assert.Equal(593_548, s.Unknowns);
            Assert.Equal(0, s.RefinementPasses);
            Assert.Equal(10, s.SweepSamples);
            Assert.Equal(7.0e9, s.PeakMemoryBytes!.Value, 1);
            Assert.Equal(["Solving: pass 1 of 1 (no refinement)"], Passes(seen));
            var online = seen.Where(p => p.Stage == PalaceStageTracker.EvaluatingLabel).ToList();
            Assert.Equal(200, online[^1].StageCompleted);
            Assert.All(online, p => Assert.Equal(200, p.StageTotal));
        }
    }

    // ── 2. Pass numbering ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AdaptiveMaxIterations = 2 is up to three solves. F0's two-pass log, cut after its second solve
    /// and closed the way Palace closes a refinement that converged there, reports the convergence and
    /// never shows "3 of 3".
    /// </summary>
    [Fact]
    public void Gate2_TwoPasses_ReadAsOfThree_AndAnEarlyConvergence_NeverShowsThreeOfThree()
    {
        var lines = File.ReadAllLines(F0Log("A-bondwire/palace-round-amr")).ToList();
        int second = lines.FindIndex(l => l.StartsWith("Adaptive mesh refinement (AMR) iteration 2:", StringComparison.Ordinal));
        Assert.True(second > 0);
        var converged = lines.Take(second).Concat(
        [
            "",
            "Completed 1 iteration of adaptive mesh refinement (AMR):",
            " Indicator norm = 5.000e-03, global unknowns = 958324",
            " Max. iterations = 2, tol. = 1.000e-02, max. size = 1500000",
        ]).ToList();

        var (tracker, seen) = FeedLines(converged, 2, 1e-4);
        Assert.Equal(["Solving: refinement pass 1 of 3", "Solving: refinement pass 2 of 3"], Passes(seen));
        Assert.DoesNotContain(seen, p => p.Stage.Contains("3 of 3", StringComparison.Ordinal));
        Assert.Contains(tracker.Notes, n => n.Contains("converged after pass 2 of up to 3", StringComparison.Ordinal));
        Assert.Equal(1, tracker.Summary.RefinementPasses);
    }

    // ── 3. The convergence fraction ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_TheSamplingBar_IsTheLogScaleConvergence_ClampedAtOne()
    {
        // log(e1/e) / log(e1/tol): 1e-2 -> 1e-4 is two decades; 1e-3 is half of them.
        Assert.Equal(0, PalaceStageTracker.ConvergenceFraction(1e-2, 1e-2, 1e-4), 12);
        Assert.Equal(0.5, PalaceStageTracker.ConvergenceFraction(1e-2, 1e-3, 1e-4), 12);
        Assert.Equal(1, PalaceStageTracker.ConvergenceFraction(1e-2, 1e-6, 1e-4), 12);   // past the tolerance: clamped
        Assert.Equal(0, PalaceStageTracker.ConvergenceFraction(1e-2, 5e-2, 1e-4), 12);   // worse than the first: clamped

        var log = new List<string> { " elements    1    1    1    100" };
        foreach (var (k, e) in new[] { (1, 1e-2), (2, 1e-3), (3, 3e-3), (4, 1e-5) })
            log.Add($"Greedy iteration {k} (n = {2 * k + 2}): ω* = 1.000e+01 GHz (1.000e+00), error = {e.ToString("0.000e+00", CultureInfo.InvariantCulture)}, memory = 0/2");
        var (_, seen) = FeedLines(log, 0, 1e-4);
        var bar = seen.Where(p => p.StageUnit == PalaceStageTracker.ConvergenceUnit).ToList();
        Assert.All(bar, p => Assert.Equal(PalaceStageTracker.ConvergenceSteps, p.StageTotal));
        Assert.Equal([0L, 500, 500, 1000], bar.GroupBy(p => p.Stage).Select(g => g.Last().StageCompleted));
        // The label shows Palace's current error even when it rose; the bar keeps its best.
        Assert.Contains(bar, p => p.Stage.Contains("error 3.0e-3", StringComparison.Ordinal) && p.StageCompleted == 500);

        // circuitRF excites every port, and Palace samples and sweeps each excitation on its own (its
        // v0.18.1 source: "Adding excitation index k (i/n):" and "Sweeping excitation index k (i/n):",
        // printed only when n > 1). Each one's bar starts again from its own first error.
        var two = new List<string> { " elements    1    1    1    100", "Beginning PROM construction offline phase:" };
        foreach (int x in new[] { 1, 2 })
        {
            two.Add($"Adding excitation index {x} ({x}/2):");
            two.Add("Greedy iteration 1 (n = 4): ω* = 1.000e+01 GHz (1.000e+00), error = 1.000e-02, memory = 0/2");
            two.Add("Greedy iteration 2 (n = 6): ω* = 1.000e+01 GHz (1.000e+00), error = 1.000e-06, memory = 1/2");
            two.Add("Adaptive sampling converged with 3 frequency samples:");
        }
        two.Add("Beginning fast frequency sweep online phase");
        foreach (int x in new[] { 1, 2 })
        {
            two.Add($"Sweeping excitation index {x} ({x}/2):");
            two.AddRange(Enumerable.Range(1, 3).Select(k => $"It {k}/3: ω/2π = {k}.000e+00 GHz (total elapsed time = 1.00e+00 s)"));
        }
        var (both, steps) = FeedLines(two, 0, 1e-4);
        Assert.True(both.Summary.LogRecognised);
        Assert.Equal([0L, 1000], steps.Where(p => p.Stage.StartsWith("Sweep: sampling, excitation 2 of 2", StringComparison.Ordinal))
                                      .GroupBy(p => p.Stage).Select(g => g.Last().StageCompleted));
        Assert.Equal(3, steps.Last(p => p.Stage == PalaceStageTracker.EvaluatingLabel + ", excitation 1 of 2").StageCompleted);
        Assert.Equal(3, steps.Last(p => p.Stage == PalaceStageTracker.EvaluatingLabel + ", excitation 2 of 2").StageCompleted);
    }

    // ── 4. Unknown wording ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("all")]
    [InlineData("greedy")]
    public void Gate4_RewordedLines_FallBackToIndeterminate_WithTheNote_AndNoFraction(string which)
    {
        var lines = File.ReadAllLines(F0Log("A-bondwire/palace-round-amr")).Select(l => which == "all"
            ? l.Replace("Greedy iteration", "Sampling step").Replace(" elements ", " tetrahedra ")
               .Replace("ND (p", "Nedelec (p").Replace("It ", "Step ").Replace("Adaptive mesh refinement", "Mesh adaptation")
            : l.StartsWith("Greedy iteration", StringComparison.Ordinal) ? l.Replace(", error = ", ", residual = ") : l).ToList();

        var (tracker, seen) = FeedLines(lines, 2, 1e-4);
        Assert.False(tracker.Summary.LogRecognised);
        Assert.Contains(tracker.Notes, n => n.Contains("not in the wording circuitRF reads", StringComparison.Ordinal));
        Assert.All(seen, p => Assert.Equal(0, p.StageTotal));
        Assert.Equal(PalaceStageTracker.UnrecognisedLabel, seen[^1].Stage);
        Assert.Equal(lines.Count, seen[^1].StageCompleted);          // the line count, to the last line
    }

    // ── 5. The memory check ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// F0 case A's mesh, as F0's own Gmsh log reports it (149,252 tetrahedra), at Standard: past 75 % of
    /// 16 GB, naming the Draft estimate; nothing at 64 GB. The volume estimate made before meshing is
    /// pinned beside it, because it is why this check exists: on a bond wire the refinement IS the mesh,
    /// and the volumes alone give a few hundred tetrahedra.
    /// </summary>
    [Fact]
    public void Gate5_CaseA_WarnsOnSixteenGigabytes_NamingTheDraftEstimate_AndNotOnSixtyFour()
    {
        var gmsh = new GmshLogProgress(null);
        foreach (string line in File.ReadLines(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0",
                                                            "A-bondwire", "palace-round", "gmsh.log")))
            gmsh.Line(line);
        long tets = gmsh.Tetrahedra!.Value;
        Assert.Equal(149_252, tets);

        var at16 = Em3dRunService.MeshMemoryVerdict(tets, PalaceSettings.Default, 16_000_000_000);
        output.WriteLine(at16.Text);
        Assert.Equal(Em3dMemoryLevel.Warning, at16.Level);
        long draft = Em3dSizeEstimate.PalaceMemoryBytesForMesh(tets, 1, 0);
        Assert.Contains($"the Draft preset (Palace.Quality: Draft) — about {MachineMemory.Format(draft)}", at16.Text);
        Assert.StartsWith("Gmsh made 149,252 tetrahedra.", at16.Text);

        var at64 = Em3dRunService.MeshMemoryVerdict(tets, PalaceSettings.Default, 64_000_000_000);
        Assert.Equal(Em3dMemoryLevel.Fits, at64.Level);
        Assert.Null(at64.Text);

        var (setup, source) = CaseA(Path.Combine(_root, "a"));
        var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(generated.Ok, generated.Refusal);
        var before = Em3dRunService.EstimatePalace(generated.Problem!, PalaceSettings.Resolve(setup.Palace))!;
        output.WriteLine($"before meshing: {before.Tetrahedra:N0} tetrahedra, {before.MemoryBytes / 1e9:0.00} GB");
        Assert.True(before.Tetrahedra * 10 < tets, $"{before.Tetrahedra} tetrahedra from the volumes");
        Assert.Equal(Em3dMemoryLevel.Fits,
                     Em3dRunService.PalaceMemoryVerdict(generated.Problem!, setup, source, PalaceSettings.Default, 16_000_000_000).Level);
    }

    [Fact]
    public void Gate5b_PastOneAndAHalfTimesMemory_IsSevere_AndARankCountPastThePhysicalOneIsRefused()
    {
        var severe = Em3dMemoryVerdict.Evaluate(40_000_000_000, 16_000_000_000, [new("the Draft preset", 9_000_000_000)]);
        Assert.Equal(Em3dMemoryLevel.Severe, severe.Level);
        Assert.Contains("very likely swap or be killed", severe.Text);
        Assert.Contains("the Draft preset — about 9.0 GB", severe.Text);

        string? refusal = Em3dRunService.RankRefusal(12, new PhysicalCoreReading(8, true, "sysctl hw.physicalcpu"));
        Assert.Contains("8 physical cores", refusal);
        Assert.Null(Em3dRunService.RankRefusal(8, new PhysicalCoreReading(8, true, "sysctl hw.physicalcpu")));
        Assert.Null(Em3dRunService.RankRefusal(null, new PhysicalCoreReading(8, true, "sysctl hw.physicalcpu")));
    }

    // ── 7. Presets ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_StandardIsTheDefaults_AndAnExplicitFieldBeatsThePreset()
    {
        Assert.Equal(PalaceSettings.Default, PalaceSettings.Resolve(null));
        Assert.Equal(PalaceSettings.Default, PalaceSettings.Resolve(new CemPalace()));
        Assert.Equal(PalaceSettings.Default, PalaceSettings.Resolve(new CemPalace { Quality = PalaceQuality.Standard }));
        Assert.Equal(PalaceSettings.Default, PalaceSettings.Preset(PalaceQuality.Standard));

        var draft = PalaceSettings.Resolve(new CemPalace { Quality = PalaceQuality.Draft });
        Assert.Equal((1, 0, 1e-3), (draft.ElementOrder, draft.AdaptiveMaxIterations, draft.SweepAdaptiveTol));
        var accurate = PalaceSettings.Resolve(new CemPalace { Quality = PalaceQuality.Accurate });
        Assert.Equal((2, 3, 0.005, 1e-5), (accurate.ElementOrder, accurate.AdaptiveMaxIterations, accurate.AdaptiveTol,
                                          accurate.SweepAdaptiveTol));

        var overridden = PalaceSettings.Resolve(new CemPalace { Quality = PalaceQuality.Draft, ElementOrder = 2 });
        Assert.Equal(2, overridden.ElementOrder);
        Assert.Equal(0, overridden.AdaptiveMaxIterations);            // the rest is still Draft's

        // The .cem round trip: a preset is a string, and Standard-with-nothing-else is not an empty section.
        var setup = new EmSetup { Name = "q", Solver3D = Em3dSolver.Palace, Palace = new CemPalace { Quality = PalaceQuality.Accurate } };
        string cem = Path.Combine(_root, "q.cem");
        Directory.CreateDirectory(_root);
        EmSetupPersistence.SaveToFile(cem, setup);
        Assert.Contains("\"Quality\": \"Accurate\"", File.ReadAllText(cem));
        Assert.Equal(PalaceQuality.Accurate, EmSetupPersistence.LoadFromFile(cem).Palace!.Quality);
        Assert.False(new CemPalace { Quality = PalaceQuality.Standard }.IsEmpty);
    }

    // ── 8. Live stages ──────────────────────────────────────────────────────────────────────────

    /// <summary>The smallest existing gate case through Palace: its stage changes, in order —
    /// meshing, pass 1, sampling, evaluating, reading. A counter of stage changes, never a timing.</summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate8_ARealRun_EmitsMeshingPassSamplingEvaluatingReading_InOrder()
    {
        string cem = PalaceBackendTests.SmallWorkspace(_root, "live");
        var setup = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(cem, setup.LayoutRef, Path.Combine(Path.GetDirectoryName(cem)!, ".cws"),
                                               new TechnologyCache());
        var seen = new Recorder();
        var result = EmRunService.Run(setup, resolved.Source, Path.Combine(Path.GetDirectoryName(cem)!, "results"),
                                      control: new RunControl { Progress = seen });
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);

        var stages = new List<string>();
        foreach (var p in seen.All)
            if (stages.Count == 0 || stages[^1] != p.Stage) stages.Add(p.Stage);
        foreach (string s in stages) output.WriteLine(s);
        foreach (string n in result.Notes ?? []) output.WriteLine("note: " + n);

        string[] order = [GmshLogProgress.MeshingLabel, "Solving: pass 1", PalaceStageTracker.SamplingLabel,
                          PalaceStageTracker.EvaluatingLabel, Em3dRunService.ReadingLabel];
        int at = -1;
        foreach (string want in order)
        {
            int next = stages.FindIndex(at + 1, s => s.StartsWith(want, StringComparison.Ordinal));
            Assert.True(next > at, $"'{want}' does not follow in order: {string.Join(" | ", stages)}");
            at = next;
        }
        Assert.StartsWith("Palace done in ", result.Notes![^1]);
        Assert.Contains("preset Standard", result.Notes![^1]);
        Assert.Contains(File.ReadAllLines(result.SnpPath!), l => l.Contains(Em3dRunService.RunProvenancePrefix + "preset Standard"));
    }

    // ── 9. The CLI's split ──────────────────────────────────────────────────────────────────────

    /// <summary>Stages go to stderr; stdout is exactly the lines the verb printed before this brief.</summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate9_EmPrintsItsStagesOnStderr_AndItsStdoutIsUnchanged()
    {
        string cem = PalaceBackendTests.SmallWorkspace(_root, "cli");
        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "em", cem);
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.Equal(0, code);

        Assert.Contains("Meshing (Gmsh)", stderr);
        Assert.Contains("Solving: pass 1 of 1", stderr);
        Assert.Contains(Em3dRunService.ReadingLabel, stderr);
        Assert.Contains("note: Palace done in ", stderr);

        string results = Path.Combine(Path.GetDirectoryName(cem)!, "results");
        var lines = stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal("EM setup:  ms", lines[0]);
        Assert.StartsWith("Solver:    Palace ", lines[1]);
        Assert.StartsWith("Points:    ", lines[2]);
        Assert.Equal($"Wrote {Path.Combine(results, "ms.palace.s2p")}", lines[3]);
        Assert.Equal($"Wrote {Path.Combine(results, "ms.palace_em.npy")}", lines[4]);
    }

    // ── 10. --solver on a planar setup ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_SolverOnAPlanarCem_IsRefusedWithExitOne_AndNothingRuns()
    {
        string from = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", "B-via", "planar", "ws");
        string ws = Path.Combine(_root, "planar");
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(ws, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        string cem = Path.Combine(ws, "via", "em", "via.cem");
        Assert.False(EmSetupPersistence.LoadFromFile(cem).Is3D);

        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "em", cem, "--solver", "palace");
        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Contains("is a planar setup", stderr);
        Assert.Contains("Solver3D", stderr);
        Assert.False(Directory.Exists(Path.Combine(ws, "results")), "a refused run wrote results");
    }

    // ══ fixtures ════════════════════════════════════════════════════════════════════════════════

    private sealed class Recorder : IProgress<RunProgress>
    {
        public List<RunProgress> All { get; } = [];
        public void Report(RunProgress value) { lock (All) All.Add(value); }
    }

    private static string F0Log(string run)
        => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", run, "palace.log");

    private static (PalaceStageTracker, List<RunProgress>) Feed(string log, int maxPasses, double tol)
        => FeedLines(File.ReadAllLines(log), maxPasses, tol);

    private static (PalaceStageTracker, List<RunProgress>) FeedLines(IEnumerable<string> lines, int maxPasses, double tol)
    {
        var rec = new Recorder();
        var tracker = new PalaceStageTracker(maxPasses, tol, new RunControl { Progress = rec, MinReportIntervalMs = 0 });
        tracker.Begin();
        foreach (string l in lines) tracker.Line(l);
        return (tracker, rec.All);
    }

    /// <summary>The pass stages, without their unknown counts, in the order they began.</summary>
    private static List<string> Passes(IEnumerable<RunProgress> seen)
        => [.. seen.Select(p => p.Stage).Where(s => s.StartsWith("Solving:", StringComparison.Ordinal))
                   .Select(s => s.Split(" · ")[0]).Distinct()];

    /// <summary>Every stage change (a new label), with its counter where it has one, then the summary.</summary>
    private static string Render(List<RunProgress> seen, PalaceStageTracker tracker)
    {
        var sb = new StringBuilder();
        string? last = null;
        RunProgress? prev = null;
        void Flush()
        {
            if (prev is null) return;
            sb.Append(prev.Stage);
            if (prev.StageTotal > 0) sb.Append($"  [{prev.StageCompleted}/{prev.StageTotal} {prev.StageUnit}]");
            sb.Append('\n');
        }
        foreach (var p in seen)
        {
            if (p.Stage != last) { Flush(); last = p.Stage; }
            prev = p;
        }
        Flush();
        var s = tracker.Summary;
        sb.Append($"summary: initial {s.InitialElements}, final {s.FinalElements}, unknowns {s.Unknowns}, passes " +
                  $"{s.RefinementPasses}, samples {s.SweepSamples}, peak {s.PeakMemoryBytes?.ToString("R", CultureInfo.InvariantCulture)}, " +
                  $"seconds {s.PalaceSeconds?.ToString("R", CultureInfo.InvariantCulture)}\n");
        foreach (string n in tracker.Notes) sb.Append("note: ").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// F0's case A as circuitRF builds it (brief 4's hexagon with its foot, the wBond file F0's kernel W
    /// row used), in F0's box: 3 × 2 mm and 1.1 mm high, perfect conductor on every face. The layout
    /// and its paired .wBond are written under <paramref name="dir"/> so a run resolves the wires the
    /// way the application does.
    /// </summary>
    internal static (EmSetup, EmLayoutSource) CaseA(string dir)
    {
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": -550000, "Y1": -50000, "X2": -450000, "Y2": 50000 },
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 450000, "Y1": -50000, "X2": 550000, "Y2": 50000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": -550000, "Y": 0, "Text": "1", "Height": 20000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 550000, "Y": 0, "Text": "2", "Height": 20000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        Directory.CreateDirectory(dir);
        string clayPath = Path.Combine(dir, "caseA.clay");
        File.WriteAllText(clayPath, clay);
        File.Copy(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", "A-bondwire", "kernelw", "case.wBond"),
                  Path.Combine(dir, "caseA.wBond"), overwrite: true);
        var view = LayoutPersistence.LoadFromFile(clayPath);
        var tech = new Technology
        {
            Name = "F0 case A",
            DefaultFlattenTolDbu = 100,
            Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Pad" }],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Pads", ThicknessDbu = 0, SigmaSm = 4.1e7,
                                       DrawingLayers = [new LayerKey(1, 0)] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Substrate", ThicknessDbu = 100_000, Epsr = 9.8 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Ground", ThicknessDbu = 0, SigmaSm = 5.8e7,
                                       IsGroundReference = true },
                ],
            },
        };
        // F0's box: x ±1500, y ±1000 around pads spanning x ±550, y ±50; the top at 1100 µm, over a
        // wire whose top is about 275 µm up.
        var side = new EmAirBoxFace(950, Em3dBoundaryKind.Pec);
        var setup = new EmSetup
        {
            Name = "caseA", LayoutRef = "caseA.clay",
            Frequency = new FrequencySpec("1", "40", 40, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.Palace, OperatingTempC = 20,
            AirBox = new EmAirBox(side, side, side, side, null, new EmAirBoxFace(825, Em3dBoundaryKind.Pec)),
        };
        return (setup, new EmLayoutSource(clayPath, view, tech, view.DbuPerMicron));
    }
}
