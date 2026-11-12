using System.Diagnostics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-7 — a Hertzon design run on circuitRF's EMerge driver. Everything except the last test needs no solver: the plan,
/// where the answer lands, what the run does not use, and the problem the run path assembles from the translated document.
/// The last test solves a 6-second line, and is skipped without an EMerge.
/// </summary>
public sealed class HznEmergeRunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn7-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    /// <summary>A copy of a fixture in this test's own folder: a run writes beside the project.</summary>
    private string Copy(string name, string? as_ = null)
    {
        Directory.CreateDirectory(_root);
        string to = Path.Combine(_root, as_ ?? name);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(Fixture(name), to);
        return to;
    }

    [Fact]
    public void ThePlan_ChoosesTheSetup_NamesTheRun_AndKeepsItsResultsOutOfHertzonsFolder()
    {
        string hzn = Copy("mapping.hzn");
        var (plan, refusal) = HznEmergeRun.Plan(hzn, null, null);
        Assert.True(plan is not null, refusal?.Render());
        Assert.Equal(("Mapping", "Setup1", "Setup1/Sweep", "Mapping Setup1"), (plan.Design, plan.C3dSetup, plan.HznSetup, plan.Setup.Name));
        Assert.Equal(Path.Combine(_root, "mapping.circuitrf", "results"), plan.ResultsRoot);
        Assert.DoesNotContain(".hznresults", plan.ResultsRoot);
        Assert.Same(plan.Translation.Document, plan.Document);

        // A Hertzon setup's own name stands for its one sweep; one that does not exist lists what does.
        Assert.Equal("Setup1", HznEmergeRun.Plan(hzn, "Mapping", "Setup1").Plan!.C3dSetup);
        var missing = HznEmergeRun.Plan(hzn, "Mapping", "Nope").Refusal!.Render();
        Assert.Contains("no setup named 'Nope'", missing);
        Assert.Contains("'Setup1'", missing);

        // Several designs and none named is a refusal listing them.
        string two = Copy("two-designs.hzn");
        var which = HznEmergeRun.Plan(two, null, null).Refusal;
        Assert.NotNull(which);
        Assert.Contains("--design", which.Render());
    }

    [Fact]
    public void TheUnusedSettingsNote_NamesEverySettingTheRunDoesNotUse_WithItsValue()
    {
        var (plan, refusal) = HznEmergeRun.Plan(Fixture("microstrip-line.hzn"), null, null);
        Assert.True(plan is not null, refusal?.Render());
        Assert.Equal(
            "Hertzon settings not used by circuitRF's run (9): " +
            "port 1: IntegrationLine = 0,0,0;0,0,h; port 2: IntegrationLine = 0,Lsub,0;0,Lsub,h; " +
            "boundary Rad1: Order = First order; boundary Rad1: FarFieldReference = False; " +
            "setup Setup1: MaximumDeltaS = 0.02; setup Setup1: RefinementMethod = Local (Delaunay); setup Setup1: ErrorInclusion = 50; " +
            "sweep Setup1/Sweep: SaveFields = False; setup Setup1: adaptive frequency = 5GHz.",
            HznEmergeRun.UnusedNote(plan));
        Assert.Contains(HznEmergeRun.UnusedNote(plan)!, HznEmergeRun.RunNotes(plan));
    }

    [Fact]
    public void TheRunPath_AssemblesTheTranslatedDocument_WithTheProjectsOwnMaterials_AndTheSetupsSweep()
    {
        var (plan, refusal) = HznEmergeRun.Plan(Fixture("microstrip-line.hzn"), null, null);
        Assert.True(plan is not null, refusal?.Render());

        // The seam: the elaborator carries the project's materials, because the document names no technology file.
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        var problem = made.Problem!;
        Assert.Equal(3.55, problem.Materials.Single(m => m.Name == "sub").Epsr);
        Assert.Equal([1, 2], problem.Ports.Select(p => p.Number).Order());
        Assert.All(problem.Ports, p => Assert.Equal(Em3dPortKind.Lumped, p.Kind));
        Assert.Equal((4e9, 6e9, 3), (problem.Frequency.StartHz, problem.Frequency.StopHz, problem.Frequency.Points));

        // A wave port on the end faces of a shielded line is a wave port of the problem, and the run path takes it as it takes a
        // native one: both conductors found, the ground negative.
        var (wavePlan, waveRefusal) = HznEmergeRun.Plan(Fixture("microstrip-wave-ports.hzn"), null, null);
        Assert.True(wavePlan is not null, waveRefusal?.Render());
        var wave = C3dProblemAssembly.Assemble(wavePlan.Setup, wavePlan.Document, wavePlan.DocumentPath, null, HznEmergeRun.Elaborator(wavePlan));
        Assert.True(wave.Ok, wave.Refusal);
        Assert.All(wave.Problem!.Ports, p => Assert.Equal(Em3dPortKind.Wave, p.Kind));

        // Without it the same document names a material nothing resolves.
        var bare = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, new C3dElaborator());
        Assert.False(bare.Ok);
    }

    [Fact]
    public void TheCli_RefusesAnotherSolver_AndExplainNamesTheSetupThatWouldRun()
    {
        string hzn = Copy("microstrip-line.hzn");
        var palace = RunCli("em", hzn, "--solver", "palace");
        Assert.Equal(1, palace.ExitCode);
        Assert.Contains("runs on EMerge here", palace.StdErr);
        Assert.False(Directory.Exists(Path.Combine(_root, "microstrip-line.circuitrf")));

        var explain = RunCli("explain", hzn);
        Assert.True(explain.ExitCode == 0, explain.StdErr + explain.StdOut);
        Assert.Contains("Setup1/Sweep", explain.StdOut);
        Assert.Contains("em runs this setup on EMerge", explain.StdOut);
    }

    [EmergeFact]
    public void TheCliAsAProcess_WritesTheFilesTheInProcessRunWrites_ByteForByte_ApartFromItsStamps()
    {
        string viaCli = Copy("microstrip-line.hzn", "cli/line.hzn");
        string inProcess = Copy("microstrip-line.hzn", "proc/line.hzn");

        var run = RunCli("em", viaCli);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);

        var (plan, refusal) = HznEmergeRun.Plan(inProcess, null, null);
        Assert.True(plan is not null, refusal?.Render());
        var result = HznEmergeRun.Run(plan);
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        Assert.Contains(result.Notes!, n => n.StartsWith("Hertzon settings not used by circuitRF's run (9)", StringComparison.Ordinal));
        Assert.Contains("Hertzon settings not used by circuitRF's run (9)", run.StdErr);

        string a = Path.Combine(plan.ResultsRoot, "Line Setup1.emerge.s2p");
        string b = Path.Combine(Path.GetDirectoryName(viaCli)!, "line.circuitrf", "results", "Line Setup1.emerge.s2p");
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal(Normalised(File.ReadAllText(b)), Normalised(File.ReadAllText(a)));
        Assert.Equal(result.SnpPath, a);
        Assert.False(Directory.Exists(Path.Combine(_root, "cli", "line.hznresults")));
        output.WriteLine(File.ReadAllText(a));
    }

    /// <summary>The Touchstone less the lines a run stamps with its own time, id and cost.</summary>
    private static string Normalised(string s) => string.Join("\n", s.Split('\n').Where(l =>
        !l.StartsWith("! circuitRF-EM written", StringComparison.Ordinal) && !l.StartsWith("! circuitRF-EM stamp", StringComparison.Ordinal) &&
        !l.StartsWith("! circuitRF-EM 3D run cost", StringComparison.Ordinal)));

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HznEmergeRunTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}
