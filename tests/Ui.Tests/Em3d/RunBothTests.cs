using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-10 §6 — one setup through Palace and openEMS, and the comparison.
//  Gates 1, 2 and 6 (the arithmetic and the notes) are tests/Engine.Tests/Em3d/Em3dComparisonTests.cs.
//
//  Gate 5 needs nothing installed. Gates 4 and 8 need Palace (CIRCUITRF_PALACE and CIRCUITRF_MPIRUN on
//  the F0 machine, as PalaceBackendTests'); gate 3 needs Palace and openEMS. Gates 3, 4 and 7 run real
//  solvers and are Category=Benchmark (measured 25 s and 7 s on the F0 machine; gate 7 is minutes).
// ══════════════════════════════════════════════════════════════════════════════════════════════

// PalaceBackendTests' collection: gates 3 and 8 read process-wide run counters, and gate 8 narrows the
// shared openEMS discovery for its duration.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class RunBothTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-both-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 3. One generation ───────────────────────────────────────────────────────────────────────

    /// <summary>A run through both solvers generates its 3D problem once, writes both results and
    /// the comparison, and lists all five files.</summary>
    [BothFact]
    [Trait("Category", "Benchmark")]   // measured 25 s: both solvers run
    public void Gate3_ABothRun_GeneratesTheProblemOnce()
    {
        var (setup, source) = Small();
        setup.Solver3D = Em3dSolver.Both;
        string results = Path.Combine(_root, "results");

        long before = Em3dRunService.ProblemsGenerated;
        var result = EmRunService.Run(setup, source, results);
        Assert.Equal(1, Em3dRunService.ProblemsGenerated - before);

        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        foreach (string n in result.Notes ?? []) output.WriteLine(n);
        Assert.StartsWith("Palace against openEMS: S21 by at most", result.Notes![0]);
        Assert.Equal(
            [Path.Combine(results, "ms.palace.s2p"), Path.Combine(results, "ms.palace_em.npy"),
             Path.Combine(results, "ms.openems.s2p"), Path.Combine(results, "ms.openems_em.npy"),
             Path.Combine(results, "ms.compare_em.npy")],
            result.Outputs!.Select(o => o.Path));
        Assert.All(result.Outputs!, o => Assert.True(File.Exists(o.Path), o.Path));
    }

    // ── 4. One side fails ───────────────────────────────────────────────────────────────────────

    /// <summary>An openEMS that fails when run (a script that exits 1 with a message) leaves Palace's
    /// result written, writes no comparison, exits 1, and quotes the message.</summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]   // measured 7 s: a real Palace run
    public void Gate4_OpenEmsFailing_KeepsPalacesResult_AndExits1()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake openEMS is a shell script
        const string Words = "fake openEMS: the licence daemon is not answering";
        string fake = Path.Combine(_root, "fake-openEMS");
        Directory.CreateDirectory(_root);
        // Its --help is the validated build's banner, so discovery accepts it; any run fails.
        File.WriteAllText(fake, $"""
            #!/bin/sh
            if [ "$1" = "--help" ]; then
              echo ' | openEMS 64bit -- version 67d3784'
              echo '		CSXCAD -- Version: dcdb62b'
              exit 0
            fi
            echo '{Words}' >&2
            exit 1

            """);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string cem = Workspace("fails");
        string results = Path.Combine(Path.GetDirectoryName(cem)!, "results");
        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [("CIRCUITRF_OPENEMS", fake)],
                                                    "em", cem, "--solver", "both");
        output.WriteLine(stderr);
        Assert.Equal(1, code);
        Assert.Contains(Words, stderr);
        Assert.Contains("Palace's result was written", stderr);
        Assert.True(File.Exists(Path.Combine(results, "ms.palace.s2p")));
        Assert.Contains($"Wrote {Path.Combine(results, "ms.palace.s2p")}", stdout);
        Assert.False(File.Exists(Path.Combine(results, "ms.compare_em.npy")));
        Assert.False(File.Exists(Path.Combine(results, "ms.openems.s2p")));
    }

    // ── 5. Plottable ────────────────────────────────────────────────────────────────────────────

    /// <summary>The comparison, written by the writer the run uses, is drawn by <c>circuitrf plot</c>
    /// with no <c>.cdd</c>; and its notes survive the file.</summary>
    [Fact]
    public void Gate5_TheComparison_IsPlottedWithNoCdd()
    {
        var (setup, source) = Small();
        var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(generated.Ok, generated.Refusal);
        double[] f = [1e9, 4e9, 7e9, 10e9];
        var p = new Complex[f.Length * 4];
        var o = new Complex[f.Length * 4];
        for (int k = 0; k < f.Length; k++)
            for (int e = 0; e < 4; e++)
            {
                p[k * 4 + e] = Complex.FromPolarCoordinates(e is 1 or 2 ? 0.95 : 0.1, -k);
                o[k * 4 + e] = Complex.FromPolarCoordinates(e is 1 or 2 ? 0.94 : 0.12, -k - 0.02);
            }
        DataCube Cube(Complex[] s) => new([new Axis("freq", f, "Hz"), new Axis("i", [1, 2], "port"), new Axis("j", [1, 2], "port")], s);
        var cmp = Em3dComparison.Compare(Cube(p), Cube(o), generated.Problem!, new Em3dComparisonFacts(5.5e9, [], [], []));

        Directory.CreateDirectory(_root);
        string npy = Path.Combine(_root, "thru.compare_em.npy");
        DataSetExporter.Export(cmp.Data!, npy, ExportFormat.Npy);
        string svg = Path.Combine(_root, "d.svg");
        var (code, _, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "plot", npy, "-o", svg, "--trace", "cube=dMag_dB,i=2,j=1");
        Assert.True(code == 0, stderr);
        Assert.True(new FileInfo(svg).Length > 0);

        var back = DataSetImporter.Import(npy).DataSet.CubesIn(Em3dComparison.Group)[Em3dComparison.NotesCube];
        Assert.Equal(cmp.Summary, back.Axes[0].Labels![0]);
    }

    // ── 7. F0 case B through both ───────────────────────────────────────────────────────────────

    /// <summary>
    /// F0's case B, generated by circuitRF and run through both: the comparison's S21 maxima fall within
    /// the spread F0 Q1 measured between its two hand-built runs (docs/design/em-3d-f0-findings.md Q1:
    /// |S21| within 0.035 dB over 0.1–20 GHz, phase within 0.6 % of electrical length). The board is drawn
    /// on Edge.Cuts, as OpenEmsBackendTests' gate 8 draws it, so the dielectric stops where F0's did.
    /// </summary>
    [BothFact]
    [Trait("Category", "Benchmark")]
    public void Gate7_CaseB_ThroughBoth_AgreesWithinF0sSpread()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: false);
        var edge = new LayerKey(90, 0);
        source.Technology!.Layers.Add(new LayerDef
        {
            Key = edge, Name = "Board outline", Interchange = new InterchangeMapping(null, null, null, null, null, "Edge.Cuts"),
        });
        long um = source.View.DbuPerMicron;
        source.View.Shapes.Add(new RectShape { Layer = edge, X1 = -5000 * um, Y1 = -2500 * um, X2 = 5000 * um, Y2 = 2500 * um });
        setup.AirBox = PalaceBackendTests.Padded(1500);
        setup.Solver3D = Em3dSolver.Both;

        var result = EmRunService.Run(setup, source, Path.Combine(_root, "results"));
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        foreach (string n in result.Notes ?? []) output.WriteLine(n);
        foreach (string w in result.Warnings) output.WriteLine("WARNING " + w);

        var data = result.Data!;
        var f = data[Em3dComparison.DMagCube].Axes[0].Values;
        double[] mag = data[Em3dComparison.DMagCube].RealValues, phase = data[Em3dComparison.DPhaseCube].RealValues;
        var palace = data[Em3dComparison.PalaceCube].ComplexValues;
        double[] length = PalaceBackendTests.Unwrap([.. Enumerable.Range(0, f.Length).Select(k => palace[k * 4 + 2].Phase)]);
        double worstDb = 0, worstShare = 0;
        for (int k = 0; k < f.Length; k++)
        {
            worstDb = Math.Max(worstDb, Math.Abs(mag[k * 4 + 2]));
            worstShare = Math.Max(worstShare, Math.Abs(phase[k * 4 + 2]) / Math.Abs(length[k] * 180 / Math.PI));
        }
        output.WriteLine($"worst |S21| difference {worstDb:F4} dB, worst phase {100 * worstShare:F2} % of electrical length");
        Assert.True(worstDb <= 0.035, $"{worstDb} dB");
        Assert.True(worstShare <= 0.006, $"{100 * worstShare} %");
    }

    // ── 8. Refusal before work ──────────────────────────────────────────────────────────────────

    /// <summary>With openEMS missing, a both-run is refused before anything starts — no problem
    /// generated, no Gmsh, no Palace — and the refusal names <c>--solver palace</c>.</summary>
    [PalaceFact]
    public void Gate8_OpenEmsMissing_RefusesBeforePalaceStarts_NamingSolverPalace()
    {
        var (setup, source) = Small();
        setup.Solver3D = Em3dSolver.Both;
        var discovery = SolverDiscovery.OpenEms;
        var (commands, directories) = (discovery.CandidateCommands, discovery.SearchDirectories);
        long generated = Em3dRunService.ProblemsGenerated, gmsh = PalaceRun.GmshInvocations, palace = PalaceRun.PalaceInvocations;
        EmRunResult result;
        try
        {
            discovery.CandidateCommands = [];
            discovery.SearchDirectories = [];
            result = EmRunService.Run(setup, source, Path.Combine(_root, "results"));
        }
        finally
        {
            discovery.CandidateCommands = commands;
            discovery.SearchDirectories = directories;
        }
        output.WriteLine(result.Error);
        Assert.Equal(EmRunStatus.Refused, result.Status);
        Assert.Contains("openEMS was not found", result.Error);
        Assert.Contains("`circuitrf em --solver palace`", result.Error);
        Assert.Equal(generated, Em3dRunService.ProblemsGenerated);
        Assert.Equal(gmsh, PalaceRun.GmshInvocations);
        Assert.Equal(palace, PalaceRun.PalaceInvocations);
    }

    // ══ fixtures ════════════════════════════════════════════════════════════════════════════════

    /// <summary>PalaceBackendTests' small microstrip: 2 mm of air, order 1, no refinement passes.</summary>
    private static (EmSetup, EmLayoutSource) Small()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        var face = new EmAirBoxFace(2000, null);
        setup.AirBox = new EmAirBox(face, face, face, face, null, face);
        setup.Palace = new CemPalace { ElementOrder = 1, AdaptiveMaxIterations = 0 };
        setup.Name = "ms";
        setup.LayoutRef = Path.Combine("ms", "layout", "ms.clay");
        return (setup, source);
    }

    /// <summary>The small microstrip as a workspace on disk. Returns the .cem's path.</summary>
    private string Workspace(string name)
    {
        string ws = Path.Combine(_root, name);
        var (setup, source) = Small();
        Directory.CreateDirectory(Path.Combine(ws, "ms", "layout"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), source.Technology!);
        LayoutPersistence.SaveToFile(Path.Combine(ws, setup.LayoutRef), source.View);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string cem = Path.Combine(ws, "ms.cem");
        EmSetupPersistence.SaveToFile(cem, setup);
        return cem;
    }
}

/// <summary>Skips, naming what is missing, unless a run through both solvers would proceed.</summary>
public sealed class BothFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
        SolverDiscovery.ReadinessFor(Em3dSolver.Both).FirstOrDefault(r => !r.Proceeds) is { } r
            ? $"needs Palace, Gmsh and openEMS (set CIRCUITRF_PALACE on a machine with F0's installs): {r.Refusal}" : null);

    public BothFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}
