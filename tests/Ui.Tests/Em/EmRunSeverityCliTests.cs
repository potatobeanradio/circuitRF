// ================================================================
//  EmRunSeverityCliTests.cs — brief-em-run-severity-and-check.md, R-emsev-5 and R-emsev-1
//
//  The brief's own first gate, written the way it words it: the user's `.cem` as found — a MIM
//  capacitor drawn on the shipped MMIC technology with the plate level left out of the analysis —
//  and `circuitrf check` on it. Before EM-SEV that printed
//
//      1 document(s) checked: 0 error(s), 0 warning(s), 0 note(s).
//
//  for a setup whose run was about to publish an open circuit where the capacitor was.
//
//  The REAL verb as a separate process, not Check.Run in-process: what is being gated is the exit
//  code and what reaches a terminal, and both are properties of the program rather than of a method.
//  It launches the ALREADY-BUILT CircuitRF.Cli.dll for EmCliVerbTests' own reason (a nested
//  `dotnet run` inside `dotnet test` hangs on this repository's build locks) — see RunCli there.
//
//  NOT tagged Benchmark, and the timing assertion is the point: every finding here is produced
//  before the first frequency point is solved, so the whole of it is seconds where the solve the
//  user ran was eleven minutes.
// ================================================================

using System.Diagnostics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using System.Text.Json;
using CircuitRF.Ui.Layout;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmRunSeverityCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-emsev-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    private static LabelShape Port(LayerKey layer, double x, double y, string name) =>
        new() { Layer = layer, X = Um(x), Y = Um(y), Text = name, IsPort = true };

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        // Both pipes drained concurrently — see EmCliVerbTests.RunCli for why draining them in
        // sequence deadlocks on a verb that writes to both.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(EmRunSeverityCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    /// <summary>
    /// <b>The user's design, as found.</b> A series MIM capacitor on the shipped MMIC technology —
    /// a Metal1 feed carrying the bottom plate, a 'MIM Metal' top plate, the plate via, and a Metal2
    /// feed out — with the `.cem`'s analysis levels naming Metal1 and Metal2 and NOT the plate.
    /// Three separate mechanisms then remove the capacitor from the run.
    /// </summary>
    private string BuildWorkspace()
    {
        LayerKey Metal1 = new(1, 0), Metal2 = new(2, 0), MimMetal = new(9, 0), MimVia = new(10, 0);

        string cellLayoutDir = Path.Combine(_root, "Cap", "layout");
        Directory.CreateDirectory(cellLayoutDir);

        TechPersistence.SaveToFile(Path.Combine(_root, "mmic.ctech"), StarterTechnologies.MmicGaAs());

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(Metal1,   0, 20, 30, 30));
        view.Shapes.Add(Rect(MimMetal, 20, 20, 30, 30));
        view.Shapes.Add(Rect(MimVia,   22, 22, 28, 28));
        view.Shapes.Add(Rect(Metal2,   22, 22, 60, 28));
        view.Shapes.Add(Port(Metal1,   0, 25, "P1"));
        view.Shapes.Add(Port(Metal2,  60, 25, "P2"));
        LayoutPersistence.SaveToFile(Path.Combine(cellLayoutDir, "Cap.clay"), view);

        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "mmic.ctech" });

        var setup = new EmSetup
        {
            Name      = "cap",
            LayoutRef = Path.Combine("Cap", "layout", "Cap.clay"),
            Frequency = new FrequencySpec("1", "10", 5, SweepKind.Linear, "GHz", "GHz"),
        };
        setup.AnalysisLevelNames.Add("Metal1");
        setup.AnalysisLevelNames.Add("Metal2");

        string cemPath = Path.Combine(_root, "cap.cem");
        EmSetupPersistence.SaveToFile(cemPath, setup);
        return cemPath;
    }

    private static string[] TreeSnapshot(string root)
        => [.. Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    /// <summary>
    /// <b>R-emsev-5 — `circuitrf check` on a `.cem` runs the extraction and reports its warnings.</b>
    /// The brief's first gate, in one test because its four clauses are four properties of ONE run
    /// and splitting them would mean running it four times: the warnings name the level, the exit
    /// code is non-zero at the severity that admits warnings, nothing was written, and it took
    /// seconds rather than the eleven minutes the solve takes.
    /// </summary>
    [Fact]
    public void CheckOnACem_ReportsTheExtractionsWarnings_WritesNothing_AndDoesNotSolve()
    {
        string cemPath = BuildWorkspace();
        var before = TreeSnapshot(_root);

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = RunCli("check", cemPath, "--severity", "warning");
        sw.Stop();

        output.WriteLine($"exit {exitCode} in {sw.Elapsed.TotalSeconds:F1} s");
        output.WriteLine("stdout:\n" + stdout);
        output.WriteLine("stderr:\n" + stderr);

        // ≥ 2 warnings naming the level that was left out. There are three; asserting the brief's
        // own floor rather than the exact count leaves room for a fourth without a false failure.
        var warnings = stderr.Split('\n')
            .Where(l => l.StartsWith("warning:", StringComparison.Ordinal))
            .Where(l => l.Contains("MIM Metal", StringComparison.Ordinal))
            .ToList();
        Assert.True(warnings.Count >= 2,
            $"expected at least 2 warnings naming 'MIM Metal'; got {warnings.Count}:\n" + stderr);

        // Non-zero, at the severity that admits warnings. `check`'s DEFAULT threshold is `error` and
        // warnings exit 0 there — that is the verb's own documented rule, and the brief's phrasing
        // ("warnings exit non-zero at the default severity") states it the other way round. Changing
        // the default here to make one gate pass would change the exit code of every warning the
        // verb already produces, so the gate asks for the severity it means.
        Assert.Equal(1, exitCode);

        // …and at the default it is a reported warning rather than a failure, which is the rule.
        var (defaultExit, _, defaultErr) = RunCli("check", cemPath);
        Assert.Equal(0, defaultExit);
        Assert.Contains("MIM Metal", defaultErr, StringComparison.Ordinal);

        // It writes NOTHING. Not the `.sNp`, not the `.npy`, not a results/ folder — so it runs on a
        // read-only tree and on a workspace another process has open.
        Assert.Equal(before, TreeSnapshot(_root));

        // And it did not solve: the extract-and-mesh phase is the whole of it. The bound is
        // deliberately loose — this asserts "seconds, not minutes", which is the brief's own claim,
        // and not a machine-speed measurement.
        Assert.True(sw.Elapsed.TotalSeconds < 60,
            $"check took {sw.Elapsed.TotalSeconds:F1} s — that is solve time, not extract time.");
    }

    /// <summary>
    /// <b>R-emsev-1 — the `em` verb prints warnings AHEAD of notes.</b> Grouping is half the answer;
    /// a reader works down a terminal from the top, and on a run of this shape there are of order
    /// thirty-five lines to work down. Asserted on the real process's stderr, because the ordering
    /// is a property of what was printed and of nothing else.
    /// </summary>
    [Fact]
    public void TheEmVerb_PrintsEveryWarningBeforeEveryNote()
    {
        string cemPath = BuildWorkspace();

        // Every line asserted on is printed before the first frequency point is solved, so the run is
        // stopped there on purpose: a machine memory limit (`--memory-limit`, Settings ▸ Solvers headless)
        // too small for the DUT and its calibration standards, so the run is asked about its size and,
        // with no --force to say yes, declined before anything is filled. Solved, it took a Debug build
        // 17 minutes.
        var (_, _, stderr) = RunCli("em", cemPath, "--memory-limit", "0.01");
        output.WriteLine("stderr:\n" + (stderr.Length > 6000 ? stderr[..6000] + " …" : stderr));

        var lines = stderr.Split('\n');
        int lastWarning = -1, firstNote = int.MaxValue;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("warning:", StringComparison.Ordinal)) lastWarning = i;
            else if (lines[i].StartsWith("note:", StringComparison.Ordinal) && i < firstNote) firstNote = i;
        }

        Assert.True(lastWarning >= 0, "this run is supposed to produce warnings:\n" + stderr);
        Assert.True(firstNote < int.MaxValue, "this run is supposed to produce notes too:\n" + stderr);
        Assert.True(lastWarning < firstNote,
            $"a warning was printed at line {lastWarning}, after the first note at {firstNote}:\n" + stderr);
    }

    /// <summary>A plain Metal1 line on the shipped MMIC technology with a port at each end, and a
    /// full-wave planar <c>.cem</c> whose PlanarMesh states <paramref name="mesh"/>.</summary>
    private string BuildLine(PlanarMeshSettings mesh)
    {
        LayerKey Metal1 = new(1, 0);
        string cellLayoutDir = Path.Combine(_root, "Line", "layout");
        Directory.CreateDirectory(cellLayoutDir);
        TechPersistence.SaveToFile(Path.Combine(_root, "mmic.ctech"), StarterTechnologies.MmicGaAs());

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(Metal1, 0, 0, 400, 20));
        view.Shapes.Add(Port(Metal1,   0, 10, "P1"));
        view.Shapes.Add(Port(Metal1, 400, 10, "P2"));
        LayoutPersistence.SaveToFile(Path.Combine(cellLayoutDir, "Line.clay"), view);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "mmic.ctech" });

        var setup = new EmSetup
        {
            Name         = "line",
            AnalysisKind = EmAnalysisKind.Planar,   // a uniform line would otherwise go to the cross-section kernel
            LayoutRef    = Path.Combine("Line", "layout", "Line.clay"),
            Frequency    = new FrequencySpec("1", "10", 5, SweepKind.Linear, "GHz", "GHz"),
            PlanarMesh   = mesh,
        };
        string cemPath = Path.Combine(_root, "line.cem");
        EmSetupPersistence.SaveToFile(cemPath, setup);
        return cemPath;
    }

    /// <summary>
    /// With <c>Auto</c> on, a stated <c>EdgeMesh: false</c> is discarded (<c>PlanarMeshSettings.Resolved</c>
    /// is the contract) and the mesh notes report only the value used. <c>check</c> says so, naming the
    /// field and the switch that makes it take effect; otherwise the notes say "edge mesh on" and
    /// nothing explains why.
    /// </summary>
    [Fact]
    public void CheckOnACem_AFieldAutoDiscards_IsAWarningNamingIt()
    {
        string cemPath = BuildLine(PlanarMeshSettings.Default with { EdgeMesh = false });

        var (_, stdout, stderr) = RunCli("check", cemPath, "--json");
        output.WriteLine(stderr);

        var d = JsonDocument.Parse(stdout).RootElement.GetProperty("diagnostics").EnumerateArray()
                            .Single(x => x.GetProperty("id").GetString() == "check.em.mesh-auto-override");
        Assert.Equal("warning", d.GetProperty("severity").GetString());
        Assert.Equal("EdgeMesh", d.GetProperty("arguments").GetProperty("fields").GetString());
        Assert.Contains("\"Auto\": false", d.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The mesh budget survives <c>--summary</c>: the unknown count and the ceiling it is judged
    /// against are on the document's result row, not only in the notes <c>--summary</c> collapses.
    /// </summary>
    [Fact]
    public void CheckOnACem_TheMeshBudget_SurvivesSummary()
    {
        string cemPath = BuildLine(PlanarMeshSettings.Default);

        var (_, stdout, stderr) = RunCli("check", cemPath, "--json", "--summary");
        output.WriteLine(stderr);

        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.DoesNotContain(root.GetProperty("diagnostics").EnumerateArray(),
                              x => x.GetProperty("severity").GetString() == "info");   // notes collapsed
        var em = root.GetProperty("result").GetProperty("check").GetProperty("documents")[0].GetProperty("em");
        Assert.True(em.GetProperty("unknowns").GetInt32() > 0);
        Assert.True(em.GetProperty("ceiling").GetInt32() > 0);
        Assert.Equal("ok", em.GetProperty("verdict").GetString());
    }
}
