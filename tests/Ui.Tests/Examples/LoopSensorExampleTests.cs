// ================================================================
//  LoopSensorExampleTests.cs — the shipped Loop Sensor example (brief-cxp-5): a printed split-ring loop sensor fed through
//  a real-size edge-launch SMA, whose P1 is a coaxial port at the connector's back face.
//
//  `expected-numbers.json` beside the README is the one source of every number the README prints, and of every shipped
//  run's |S11| at every frequency of its sweep. The routine tier checks that the README quotes only recorded values, that
//  each quoted dip is its run's recorded minimum and that the coax's closed form is the drawn SMA's. The solves are
//  Category=Benchmark, one test per solver, each Skipped when its solver is absent: the regression net for the coaxial
//  port and the loop feed on all three solvers.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class LoopSensorExampleTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-cxp5-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── routine ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The README quotes every number the file records, each in the words the file gives.</summary>
    [Fact]
    public void TheReadmeCitesEveryNumberInTheFile()
    {
        var n = Numbers();
        string readme = File.ReadAllText(Path.Combine(Root(), "README.md"));
        foreach (string s in n.References.SelectMany(r => r.Readme)
                              .Concat(n.Runs.SelectMany(r => r.Readme))
                              .Concat(n.Alternatives.SelectMany(a => a.Readme)))
            Assert.Contains(s, readme);
    }

    /// <summary>Each run's quoted dip is its recorded minimum: the frequency to the MHz and the depth to 0.01 dB.</summary>
    [Fact]
    public void EachQuotedDip_IsItsRunsRecordedMinimum()
    {
        foreach (var run in Numbers().Runs.Where(r => r.DipMHz is not null))
        {
            int k = Array.IndexOf(run.S11_dB, run.S11_dB.Min());
            Assert.Equal(run.DipMHz!.Value, Math.Round((run.StartGHz * 1e3 + k * run.StepMHz)), 6);
            Assert.Equal(run.DipDb!.Value, Math.Round(run.S11_dB[k], 2), 6);
            Assert.Contains($"{run.DipMHz:0} MHz", run.Readme);
            Assert.Contains($"−{Math.Abs(run.DipDb.Value):0.00} dB", run.Readme);
        }
    }

    /// <summary>The coax impedance the README states is η₀/(2π√εr)·ln(D/d) of the drawn SMA's bore and pin, with the technology's
    /// PTFE.</summary>
    [KernelFact]
    public void TheCoaxsClosedForm_IsTheDrawnSmas()
    {
        var coax = Numbers().References.Single(r => r.Name == "coax");
        string path = Path.Combine(Root(), "SMA", "3d", "SMA.c3d");
        var e = new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(path), path, Path.Combine(Root(), ".cws"));
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        double d = 2 * Assert.IsType<Em3dCylinder>(e.Solids.Single(s => s.Name == "bore").Primitive).Radius;
        Assert.Equal(coax.OuterMm, d * 1e3, 9);
        double eta0 = 376.730313668;
        double z = eta0 / (2 * Math.PI * Math.Sqrt(coax.Epsr)) * Math.Log(coax.OuterMm / coax.InnerMm);
        output.WriteLine($"coax {z:F3} Ω");
        Assert.Equal(coax.Expected, z, 2);
    }

    /// <summary><c>check</c> on the workspace, as a process: no error, and every warning is one the README names.</summary>
    [KernelFact]
    public void CheckIsClean_AndEveryWarningIsInTheReadme()
    {
        var (errors, warnings) = Check(Root());
        Assert.Empty(errors);
        string readme = File.ReadAllText(Path.Combine(Root(), "README.md"));
        foreach (string w in warnings) output.WriteLine(w);
        string[] listed = Numbers().CheckWarnings;
        Assert.All(warnings, w => Assert.Contains(listed, l => w.Contains(l, StringComparison.Ordinal)));
        Assert.All(listed, l => Assert.Contains(l, readme));
    }

    /// <summary>The repository's vendor-name gate over every text file of the example.</summary>
    [Fact]
    public void NoVendorNameIsInTheExample()
    {
        foreach (string file in Directory.EnumerateFiles(Root(), "*", SearchOption.AllDirectories))
        {
            var words = Regex.Matches(File.ReadAllText(file).ToLowerInvariant(), @"[a-z][a-z0-9]*").Select(w => w.Value).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i]), Docs.WsProbeDocsTests.BannedDigests);
                if (i + 1 < words.Count)
                    Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i] + " " + words[i + 1]), Docs.WsProbeDocsTests.BannedDigests);
            }
        }
    }

    // ── the solves ──────────────────────────────────────────────────────────────────────────────

    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void EveryPalaceRun_ReproducesItsRecordedS11() => Reproduce("Palace");

    [CircuitRF.Ui.Tests.Em3d.EmergeFact]
    [Trait("Category", "Benchmark")]
    public void EveryEMergeRun_ReproducesItsRecordedS11() => Reproduce("EMerge");

    [CircuitRF.Ui.Tests.Em3d.OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void EveryOpenEmsRun_ReproducesItsRecordedS11() => Reproduce("openEMS");

    /// <summary>Every shipped run of <paramref name="setup"/> re-run, its |S11| held to the file's tolerance at every frequency.</summary>
    private void Reproduce(string setup)
    {
        var n = Numbers();
        var moved = new List<string>();
        foreach (var run in n.Runs.Where(r => r.Setup == setup))
        {
            string path = Path.Combine(Root(), run.Cell, "3d", run.Cell + ".c3d");
            var doc = C3dPersistence.LoadFromFile(path);
            var (embedded, why) = C3dSetups.Select(doc, run.Setup);
            Assert.True(embedded is not null, why);
            var wall = Stopwatch.StartNew();
            var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded!, path), doc, path, Path.Combine(Root(), ".cws"),
                                                    Path.Combine(_tmp, "results"), confirmMemory: _ => true);
            output.WriteLine($"{run.Cell}, {run.Setup}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s (recorded {run.Readme[0]})");
            Assert.True(result.Status == EmRunStatus.Ok, $"{run.Cell} {run.Setup}: {result.Error}");
            var snp = TouchstoneIO.ReadFile(result.SnpPath!);
            Assert.Equal(run.S11_dB.Length, snp.Frequencies.Length);
            for (int k = 0; k < run.S11_dB.Length; k++)
            {
                double ghz = run.StartGHz + k * run.StepMHz * 1e-3;
                Assert.True(Math.Abs(snp.Frequencies[k] - ghz * 1e9) < 1, $"{run.Cell} {run.Setup}: point {k} is at {snp.Frequencies[k]} Hz");
                double got = 20 * Math.Log10(snp.Matrices[k][0, 0].Magnitude);
                if (Math.Abs(got - run.S11_dB[k]) > n.RegressionToleranceDb)
                    moved.Add($"{run.Cell}, {run.Setup}: |S11| at {ghz:0.000} GHz is {got:F3} dB, recorded {run.S11_dB[k]:F3} dB");
            }
        }
        Assert.True(moved.Count == 0, $"{moved.Count} recorded values moved:\n" + string.Join("\n", moved.Take(40)));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string Root()
        => Path.Combine(ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/"), "Loop Sensor");

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    /// <summary><c>check --json</c> as a process: the verb a user runs.</summary>
    private static (List<string> Errors, List<string> Warnings) Check(string path)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in new[] { "check", path, "--json" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        using var doc = JsonDocument.Parse(outTask.GetAwaiter().GetResult());
        var diags = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        List<string> Of(string severity) => [.. diags.Where(d => d.GetProperty("severity").GetString() == severity)
                                                     .Select(d => d.GetProperty("message").GetString()!)];
        return (Of("error"), Of("warning"));
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(LoopSensorExampleTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    // ── expected-numbers.json ───────────────────────────────────────────────────────────────────

    /// <summary>A shipped run: its cell and setup, what the README quotes of it (cost first), its dip, and its |S11| at every
    /// frequency of the sweep from <see cref="StartGHz"/> in steps of <see cref="StepMHz"/>.</summary>
    private sealed record ShippedRun(string Cell, string Setup, List<string> Readme, double? DipMHz, double? DipDb,
                                     double StartGHz, double StepMHz, double[] S11_dB);

    /// <summary>A run made once to answer a question (a variant, a decomposition): quoted, not held.</summary>
    private sealed record Alternative(string Name, List<string> Readme);

    private sealed record Reference(string Name, double Expected, List<string> Readme, double Epsr, double InnerMm, double OuterMm);

    private sealed record ExpectedNumbers(List<Reference> References, List<ShippedRun> Runs, List<Alternative> Alternatives,
                                          double RegressionToleranceDb, string[] CheckWarnings);

    private static ExpectedNumbers Numbers()
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(), "expected-numbers.json")))!;
}
