using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-22 §6 — package RLC: Palace's electrostatic and magnetostatic solves.
//
//  Gates 1, 6 and 8 and the fixture gates (the log parser and the CSV reader on the committed runs
//  in testdata/em3d/static/) need nothing installed. Gates 2-5 and 7 run Palace and skip without it.
//  Every closed form is computed HERE, from its formula — never from a circuitRF run.
//
//  Goldens: testdata/em3d/palace-goldens/static-{es,ms}/ (CRF_WRITE_PALACE_GOLDENS=1 rewrites them,
//  as for brief 7's). The Palace runs' own output is testdata/em3d/static/<case>/, written by gates
//  2-4 with CRF_WRITE_STATIC_FIXTURES=1; nothing rewrites it otherwise.
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PalaceStaticTests(ITestOutputHelper output) : IDisposable
{
    private const double Um = 1e-6;
    private const double Eps0 = 8.8541878128e-12;
    private const double Mu0 = 4e-7 * Math.PI;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-static-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. Writer goldens ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Em3dProblemType.Electrostatic, "static-es")]
    [InlineData(Em3dProblemType.Magnetostatic, "static-ms")]
    public void Gate1_TheTwoTerminalConfig_IsTheCommittedGolden_AndValidatesAgainstTheSchema(Em3dProblemType type, string name)
    {
        var (setup, source) = TwoLines(type);
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        var problem = r.Problem!;
        Assert.Empty(problem.Validate());
        Assert.Equal(["A", "B"], problem.Terminals.Select(t => t.Name));
        var settings = PalaceSettings.Resolve(setup.Palace);
        var low = GmshGeoWriter.Write(problem, settings);
        Assert.True(low.Ok, low.Refusal);
        var cfg = PalaceConfigWriter.Write(problem, low.Groups, settings);
        Assert.True(cfg.Ok, cfg.Refusal);

        string dir = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "palace-goldens", name);
        var files = new[] { (GmshGeoWriter.GeoFile, low.Geo!), (GmshGeoWriter.GroupsFile, low.GroupsJson!),
                            (PalaceConfigWriter.ConfigFile, cfg.Json!) };
        if (Environment.GetEnvironmentVariable("CRF_WRITE_PALACE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(dir);
            foreach (var (file, text) in files) File.WriteAllBytes(Path.Combine(dir, file), System.Text.Encoding.UTF8.GetBytes(text));
        }
        foreach (var (file, text) in files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, file)), System.Text.Encoding.UTF8.GetBytes(text));

        using var schemaDoc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "palace-schema", "0.18.1.json")));
        using var doc = JsonDocument.Parse(cfg.Json!);
        Assert.Empty(new DraftSevenSchema(schemaDoc.RootElement).Validate(doc.RootElement));

        // What each problem says, read off the configuration rather than restated.
        var b = doc.RootElement.GetProperty("Boundaries");
        Assert.Equal(type.ToString(), doc.RootElement.GetProperty("Problem").GetProperty("Type").GetString());
        Assert.False(b.TryGetProperty("LumpedPort", out _));
        if (type == Em3dProblemType.Electrostatic)
        {
            Assert.Equal(2, b.GetProperty("Terminal").GetArrayLength());
            Assert.True(b.TryGetProperty("Ground", out _));      // the PEC floor
            Assert.False(b.TryGetProperty("PEC", out _));
        }
        else
        {
            var sources = b.GetProperty("SurfaceCurrent");
            Assert.Equal(2, sources.GetArrayLength());
            Assert.All(sources.EnumerateArray(), s => Assert.Equal("+Z", s.GetProperty("Direction").GetString()));
        }
    }

    // ── 2. Parallel plates ──────────────────────────────────────────────────────────────────────

    /// <summary>A plate spanning the box wall to wall over the PEC floor, PMC sides: the field is
    /// uniform, so C = ε₀εᵣA/d with no fringing term.</summary>
    [PalaceFact]
    public void Gate2_ParallelPlates_WithPmcSides_GiveEps0EpsrAOverD()
    {
        double a = 1000 * Um, d = 100 * Um, t = 20 * Um, top = 300 * Um, er = 2;
        var faces = new Em3dFaces(Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc,
                                  Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pmc);
        var problem = new Em3dProblem(
            [
                new Em3dSolid("below", "fill", Em3dRole.Dielectric, new Em3dBox(new(0, 0, 0), new(a, a, d)), 1),
                new Em3dSolid("above", "fill", Em3dRole.Dielectric, new Em3dBox(new(0, 0, d + t), new(a, a, top)), 1),
                new Em3dSolid("plate", "metal", Em3dRole.Conductor, new Em3dBox(new(0, 0, d), new(a, a, d + t)), 2),
            ],
            [], Materials(er), [], new Em3dAirBox(new(0, 0, 0), new(a, a, top), faces), Hz, 20)
        {
            Type = Em3dProblemType.Electrostatic,
            Terminals = [new Em3dTerminal("plate", ["plate"])],
        };

        var (c, cm) = Solve(problem, "plates");
        double expected = Eps0 * er * a * a / d;
        output.WriteLine($"C = {c[0, 0]:G6} F, closed form {expected:G6} F, {100 * (c[0, 0] / expected - 1):F4} %");
        Assert.InRange(c[0, 0] / expected, 0.99, 1.01);
        Assert.NotNull(cm);
        AssertMaxwell(c);
    }

    // ── 3, 4. Coax ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A coaxial section whose end faces are natural (zero-charge) walls: the field is purely
    /// radial, so C = 2πε₀εᵣℓ / ln(b/a).</summary>
    [PalaceFact]
    public void Gate3_CoaxElectrostatic_Gives2PiEps0EpsrLOverLnBOverA()
    {
        var problem = Coax(Em3dProblemType.Electrostatic);
        var (c, _) = Solve(problem, "coax-es");
        double expected = 2 * Math.PI * Eps0 * CoaxEr * CoaxLength / Math.Log(CoaxB / CoaxA);
        output.WriteLine($"C = {c[0, 0]:G6} F, closed form {expected:G6} F, {100 * (c[0, 0] / expected - 1):F4} %");
        Assert.InRange(c[0, 0] / expected, 0.99, 1.01);
        AssertMaxwell(c);
    }

    /// <summary>The same section shorted at the far end (a PEC face) and driven through the annulus at
    /// the near end: L = μ₀ℓ/2π · ln(b/a). Conductors are voids, so this is the EXTERNAL inductance —
    /// which is exactly what the formula is.</summary>
    [PalaceFact]
    public void Gate4_CoaxMagnetostatic_GivesMu0LOver2PiLnBOverA()
    {
        var problem = Coax(Em3dProblemType.Magnetostatic);
        var (l, _) = Solve(problem, "coax-ms");
        double expected = Mu0 * CoaxLength / (2 * Math.PI) * Math.Log(CoaxB / CoaxA);
        output.WriteLine($"L = {l[0, 0]:G6} H, closed form {expected:G6} H, {100 * (l[0, 0] / expected - 1):F4} %");
        Assert.InRange(l[0, 0] / expected, 0.98, 1.02);
        AssertMaxwell(l, inductance: true);
    }

    // ── 5, 7. A two-terminal run: symmetric, signed, and no .sNp ─────────────────────────────────

    /// <summary>
    /// The two-line layout through the one door (EmRunService), electrostatic: a 2×2 Maxwell matrix
    /// that is symmetric with a positive diagonal and a non-positive off-diagonal (gate 5); the mutual
    /// form's diagonal is the row sum; the result is a .npy under <c>&lt;key&gt;.palace_es</c> and
    /// there is no Touchstone anywhere (gate 7).
    /// </summary>
    [PalaceFact]
    public void Gate5And7_TwoLines_GiveASignedSymmetricMatrix_InANpy_AndNoTouchstone()
    {
        var (setup, source) = TwoLines(Em3dProblemType.Electrostatic);
        string results = Path.Combine(_root, "results");
        var result = EmRunService.Run(setup, source, results);
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        foreach (string n in result.Notes ?? []) output.WriteLine("note: " + n);

        var c = Matrix(result.Data!, Em3dStaticResult.CapacitanceCube, out var names);
        Assert.Equal(["A", "B"], names);
        AssertMaxwell(c);
        var cm = Matrix(result.Data!, Em3dStaticResult.MutualCapacitanceCube, out _);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(c[i, 0] + c[i, 1], cm[i, i], 1e-6 * c[i, i]);
            Assert.Equal(-c[i, 1 - i], cm[i, 1 - i], 1e-12 * c[i, i]);
        }

        Assert.Null(result.SnpPath);
        Assert.Equal(Path.Combine(results, "two.palace_es"), Em3dRunService.RunDirectory(results, setup, Em3dSolver.Palace));
        Assert.Equal("two.palace_es", Em3dRunService.NpyKey(setup, Em3dSolver.Palace));
        Assert.EndsWith("two.palace_es.npy", result.NpyPath);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root, "*.s*p", SearchOption.AllDirectories),
                              f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"\.s\d+p$"));
    }

    // ── 6. Refusals before work ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_StaticOnOpenEmsOrBoth_ATerminalWithNoConductor_AndASourcelessMagnetostaticTerminal_AreRefusedBeforeGmsh()
    {
        long gmsh = PalaceRun.GmshInvocations;

        foreach (var solver in new[] { Em3dSolver.OpenEms, Em3dSolver.Both })
        {
            var (setup, source) = TwoLines(Em3dProblemType.Electrostatic);
            setup.Solver3D = solver;
            var run = EmRunService.Run(setup, source, Path.Combine(_root, "r-" + solver));
            Assert.Equal(EmRunStatus.Refused, run.Status);
            Assert.Contains("only Palace solves the static problems", run.Error);
        }

        // Named by the generator — the run's own step after discovery, before any lowering.
        var (noMetal, src1) = TwoLines(Em3dProblemType.Electrostatic);
        noMetal.Terminals3D = [new("A", "A"), new("VDD", "VDD")];
        var g1 = Em3dGenerator.Generate(noMetal, src1, src1.Technology!);
        Assert.False(g1.Ok);
        Assert.Contains("Terminal 'VDD' names net 'VDD', and no conductor", g1.Refusal);

        var (sourceless, src2) = TwoLines(Em3dProblemType.Magnetostatic);
        sourceless.Terminals3D = [new("A", "A", "1"), new("B", "B")];
        var g2 = Em3dGenerator.Generate(sourceless, src2, src2.Technology!);
        Assert.False(g2.Ok);
        Assert.Contains("Magnetostatic terminal 'B' names no source", g2.Refusal);

        // And through the run: refused, whatever discovery finds, with no mesher started.
        foreach (var (setup, source) in new[] { (noMetal, src1), (sourceless, src2) })
            Assert.Equal(EmRunStatus.Refused, EmRunService.Run(setup, source, Path.Combine(_root, "r-" + setup.Problem3D)).Status);

        // An electrostatic conductor in no terminal: Palace has no floating conductor, so it is named.
        var (floating, src3) = TwoLines(Em3dProblemType.Electrostatic);
        floating.Terminals3D = [new("A", "A")];
        var g3 = Em3dGenerator.Generate(floating, src3, src3.Technology!);
        Assert.True(g3.Ok, g3.Refusal);
        Assert.Equal(["Top Copper (1 oz)/B"], g3.Problem!.FloatingConductors());
        Assert.Contains("'Top Copper (1 oz)/B' is in no terminal", PalaceConfigWriter.StaticRefusal(g3.Problem));

        Assert.Equal(gmsh, PalaceRun.GmshInvocations);
    }

    // ── 8. Existing .cem files ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_EveryCemInTheRepo_WritesNoNewKey_AndAStaticSetupKeepsItsFields()
    {
        foreach (string cem in new[] { "examples", "testdata" }
                     .SelectMany(d => Directory.EnumerateFiles(Path.Combine(PalaceBackendTests.RepoRoot(), d), "*.cem",
                                                               SearchOption.AllDirectories)))
        {
            // Writer to writer, as brief 3's gate 6 does: not every .cem in the repo is in the writer's own
            // spelling, so the bytes on disk were never the fixed point. What this brief's fields could
            // change is what the writer EMITS for an existing file, and the only way is a key it did not state.
            string written = EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(File.ReadAllText(cem)));
            Assert.Equal(written, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(written)));
            foreach (string key in new[] { "\"Problem3D\"", "\"Terminals3D\"", "\"Ground3D\"" })
                Assert.DoesNotContain(key, written, StringComparison.Ordinal);
        }

        var (setup, _) = TwoLines(Em3dProblemType.Magnetostatic);
        setup.Ground3D = "GND";
        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("\"Problem3D\": \"Magnetostatic\"", json);
        var back = EmSetupPersistence.Deserialize(json);
        Assert.Equal(Em3dProblemType.Magnetostatic, back.Problem3D);
        Assert.Equal(setup.Terminals3D, back.Terminals3D);
        Assert.Equal("GND", back.Ground3D);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));
    }

    // ── The committed runs: the log parser and the CSV reader, with nothing installed ────────────

    /// <summary>R-em3d22-5a — each committed static log reads as "terminal k of N" stages and is
    /// recognised to its end; its unknown count is H1's for the electrostatic run and ND's otherwise.</summary>
    [Theory]
    [InlineData("coax-es", true)]
    [InlineData("coax-ms", false)]
    public void StaticLogs_ReadAsTerminalStages(string run, bool electrostatic)
    {
        string log = Path.Combine(StaticDir(run), PalaceRun.PalaceLogFile);
        var seen = new List<string>();
        var control = new CircuitRF.Engine.RunControl
        {
            Progress = new SyncProgress(p => seen.Add(p.Stage)), MinReportIntervalMs = 0,
        };
        var tracker = new PalaceStageTracker(0, 1e-4, control, electrostatic);
        foreach (string line in File.ReadLines(log)) tracker.Line(line);
        Assert.True(tracker.Summary.LogRecognised, string.Join(" | ", tracker.Notes));
        Assert.Contains(seen, s => s.EndsWith("· terminal 1 of 1", StringComparison.Ordinal));
        Assert.Empty(tracker.Warnings);
        string space = electrostatic ? "H1 (p = 2): " : "ND (p = 2): ";
        long printed = long.Parse(File.ReadLines(log).First(l => l.Contains(space, StringComparison.Ordinal))
                                      .Split(space)[1].Split(',')[0]);
        Assert.Equal(printed, tracker.Summary.Unknowns);
    }

    /// <summary>R-em3d22-3b — the matrices by column name; a missing column is named.</summary>
    [Fact]
    public void TheCommittedCsvs_ReadByColumnName()
    {
        var c = PalaceRun.ReadTerminalMatrix(Path.Combine(StaticDir("coax-es"), PalaceRun.CapacitanceFile), "C", "(F)", [1], out var e1);
        Assert.Null(e1);
        Assert.InRange(c![0, 0], 1e-13, 2e-13);
        var l = PalaceRun.ReadTerminalMatrix(Path.Combine(StaticDir("coax-ms"), PalaceRun.InductanceFile), "M", "(H)", [1], out var e2);
        Assert.Null(e2);
        Assert.InRange(l![0, 0], 1e-10, 2e-10);
        Assert.Null(PalaceRun.ReadTerminalMatrix(Path.Combine(StaticDir("coax-es"), PalaceRun.CapacitanceFile), "C", "(F)", [2], out var e3));
        Assert.Contains("'C[i][2] (F)'", e3);
    }

    // ══ fixtures ════════════════════════════════════════════════════════════════════════════════

    private static readonly Em3dFrequency Hz = new(1e9, 1e9, 1, Em3dSweepKind.Linear);

    private const double CoaxA = 100 * Um, CoaxB = 230 * Um, CoaxWall = 20 * Um, CoaxLength = 1000 * Um, CoaxEr = 2;

    private static IReadOnlyList<Em3dMaterial> Materials(double er) =>
    [
        new("fill", er, null, 0, 1, 0),
        new("metal", 1, null, 0, 1, 5.8e7),
        new("Air", 1, null, 0, 1, 0),
    ];

    /// <summary>
    /// A coaxial section along z, 0 to ℓ, filling the box's height: the outer conductor a tube (its
    /// metal cut by the higher-order dielectric), the inner a cylinder, air outside the tube. The ends
    /// are natural walls, except that the magnetostatic section is shorted by a PEC far face and driven
    /// through the dielectric's annulus at z = 0.
    /// </summary>
    private static Em3dProblem Coax(Em3dProblemType type)
    {
        bool ms = type == Em3dProblemType.Magnetostatic;
        double r = CoaxB + CoaxWall, half = 400 * Um;
        Em3dCylinder Cyl(double radius) => new(new(0, 0, 0), new(0, 0, CoaxLength), radius);
        var faces = new Em3dFaces(Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc,
                                  Em3dBoundaryKind.Pmc, ms ? Em3dBoundaryKind.Pec : Em3dBoundaryKind.Pmc);
        var port = new Em3dPort(1, "port/1", "inner", "outer", new(-CoaxB, -CoaxB, 0), new(CoaxB, CoaxB, 0),
                                new(0, 0, 0), 50, new Em3dReferencePlane(new(0, 0, 0), new(0, 0, 1), 0))
        {
            Annulus = new Em3dAnnulus(CoaxA, CoaxB, Outward: false),
        };
        return new Em3dProblem(
            [
                new Em3dSolid("outer", "metal", Em3dRole.Conductor, Cyl(r), 1),
                new Em3dSolid("dielectric", "fill", Em3dRole.Dielectric, Cyl(CoaxB), 2),
                new Em3dSolid("inner", "metal", Em3dRole.Conductor, Cyl(CoaxA), 3),
            ],
            [], Materials(CoaxEr), ms ? [port] : [],
            new Em3dAirBox(new(-half, -half, 0), new(half, half, CoaxLength), faces), Hz, 20)
        {
            Type = type,
            Terminals = [new Em3dTerminal("inner", ["inner"], ms ? "port/1" : null)],
            GroundObjects = ["outer"],
        };
    }

    /// <summary>
    /// Two 3 mm lines on nets A and B over the 2-layer board's undrawn ground (the PEC floor). The
    /// magnetostatic one is fed by an edge port at each line's near end and shorted to the floor by a
    /// via at its far end, so each current has a loop to flow round.
    /// </summary>
    internal static (EmSetup, EmLayoutSource) TwoLines(Em3dProblemType type)
    {
        bool ms = type == Em3dProblemType.Magnetostatic;
        string extra = !ms ? "" : """
            ,
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 0, "Y": 750000, "Text": "1", "Height": 200000,
                  "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 0, "Y": -750000, "Text": "2", "Height": 200000,
                  "IsPort": true, "PortDirection": "R0" },
                { "$type": "Via", "Layer": { "Layer": 7, "Datatype": 0 }, "X": 2800000, "Y": 750000, "PadSize": 400000, "DrillSize": 300000 },
                { "$type": "Via", "Layer": { "Layer": 7, "Datatype": 0 }, "X": 2800000, "Y": -750000, "PadSize": 400000, "DrillSize": 300000 }
            """;
        string clay = $$"""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "Net": "A", "X1": 0, "Y1": 500000, "X2": 3000000, "Y2": 1000000 },
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "Net": "B", "X1": 0, "Y1": -1000000, "X2": 3000000, "Y2": -500000 }{{extra}}
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var setup = new EmSetup
        {
            Name = "two", LayoutRef = "two/layout/two.clay",
            Solver3D = Em3dSolver.Palace,
            Problem3D = type,
            Terminals3D = ms ? [new("A", "A", "1"), new("B", "B", "2")] : [new("A", "A"), new("B", "B")],
            Palace = new CemPalace { AdaptiveMaxIterations = 0, EdgeRefinement = 0.5 },
        };
        return (setup, new EmLayoutSource("/nowhere/two.clay", view, tech, view.DbuPerMicron));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lowers <paramref name="problem"/>, meshes it and runs Palace serially at order 2 with no
    /// refinement passes, and reads the matrix and its other form back — the run service's steps
    /// without its layout. CRF_WRITE_STATIC_FIXTURES=1 copies the run's CSVs and log to
    /// testdata/em3d/static/<paramref name="name"/>.
    /// </summary>
    private (double[,] M, double[,]? Mutual) Solve(Em3dProblem problem, string name)
    {
        // Coarse and unrefined, so each gate is one fixed mesh and stays in the routine tier: 15 % of the
        // box's largest side, no conductor grading — a circle still gets twelve elements a turn.
        var settings = PalaceSettings.Default with { AdaptiveMaxIterations = 0, MaxElementWavelengths = 0.15, EdgeRefinement = 1 };
        Assert.Empty(problem.Validate());
        var low = GmshGeoWriter.Write(problem, settings);
        Assert.True(low.Ok, low.Refusal);
        var cfg = PalaceConfigWriter.Write(problem, low.Groups, settings);
        Assert.True(cfg.Ok, cfg.Refusal);

        string dir = Path.Combine(_root, name);
        var mesh = PalaceRun.Mesh(dir, low, SolverDiscovery.Gmsh.Find(out _)!.Path, null, default);
        Assert.True(mesh.Ok, mesh.Message);
        string palace = SolverDiscovery.ReadinessFor(Em3dSolver.Palace).Single(r => r.Tool == SolverTool.Palace).Installation!.Path;
        bool es = problem.Type == Em3dProblemType.Electrostatic;
        var tracker = new PalaceStageTracker(0, settings.SweepAdaptiveTol, null, es);
        var solved = PalaceRun.Solve(dir, cfg.Json!, palace, 1, null, default, out _, tracker);
        Assert.True(solved.Ok, solved.Message);
        Assert.True(tracker.Summary.LogRecognised, string.Join(" | ", tracker.Notes));
        Assert.Empty(tracker.Warnings);

        string post = Path.Combine(dir, PalaceConfigWriter.OutputDirectory);
        int[] indices = [.. Enumerable.Range(1, problem.Terminals.Count)];
        var (file, mutualFile, sym, unit) = es
            ? (PalaceRun.CapacitanceFile, PalaceRun.MutualCapacitanceFile, "C", "(F)")
            : (PalaceRun.InductanceFile, PalaceRun.MutualInductanceFile, "M", "(H)");
        var m = PalaceRun.ReadTerminalMatrix(Path.Combine(post, file), sym, unit, indices, out string? error);
        Assert.True(m is not null, error);
        var mutual = PalaceRun.ReadTerminalMatrix(Path.Combine(post, mutualFile), sym + "_m", unit, indices, out _);

        if (Environment.GetEnvironmentVariable("CRF_WRITE_STATIC_FIXTURES") == "1")
        {
            string keep = StaticDir(name);
            Directory.CreateDirectory(keep);
            foreach (string f in new[] { file, mutualFile }) File.Copy(Path.Combine(post, f), Path.Combine(keep, f), true);
            // The log's first line names the binary by its absolute path: kept to its file name, so no
            // machine's directory layout is committed.
            File.WriteAllText(Path.Combine(keep, PalaceRun.PalaceLogFile), System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(Path.Combine(dir, PalaceRun.PalaceLogFile)), @"(?m)^>> \S*/(palace[^/\s]*)", ">> $1"));
            File.Copy(Path.Combine(dir, PalaceConfigWriter.ConfigFile), Path.Combine(keep, PalaceConfigWriter.ConfigFile), true);
        }
        return (m!, mutual);
    }

    /// <summary>Gate 5: symmetric to 1e-6 relative, a positive diagonal, and (for a Maxwell
    /// capacitance matrix) a non-positive off-diagonal.</summary>
    private static void AssertMaxwell(double[,] m, bool inductance = false)
    {
        int n = m.GetLength(0);
        for (int i = 0; i < n; i++)
        {
            Assert.True(m[i, i] > 0, $"diagonal {i} is {m[i, i]}");
            for (int j = 0; j < n; j++)
            {
                double scale = Math.Sqrt(m[i, i] * m[j, j]);
                Assert.True(Math.Abs(m[i, j] - m[j, i]) <= 1e-6 * scale, $"[{i},{j}] {m[i, j]} vs [{j},{i}] {m[j, i]}");
                if (i != j && !inductance) Assert.True(m[i, j] <= 0, $"off-diagonal [{i},{j}] is {m[i, j]}");
            }
        }
    }

    private static double[,] Matrix(RfCore.Data.DataSet data, string cube, out string[] names)
    {
        var c = data[cube];
        names = c.Axes[0].Labels!;
        int n = names.Length;
        var v = c.RealValues;
        var m = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = v[i * n + j];
        return m;
    }

    private static string StaticDir(string run) => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "static", run);

    private sealed class SyncProgress(Action<CircuitRF.Engine.RunProgress> on) : IProgress<CircuitRF.Engine.RunProgress>
    {
        public void Report(CircuitRF.Engine.RunProgress value) => on(value);
    }
}
