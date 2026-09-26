// ================================================================
//  Em3dExampleTests.cs — the shipped 3D EM example, checked and run.
//
//  brief-em3d-30-showcase.md §3. The example is what the owner shows people, so every number its
//  README prints is a claim a reader will check. ONE source holds them: `expected-numbers.json`
//  beside the README. The README cites each value by its exact text (each value's `Readme` string
//  must appear in it verbatim) and gate 5 reproduces each value from a live run, so the page, the
//  file and the solver cannot drift apart without a failure naming which.
//
//  Gates 2-4 and 6 and the kernel W gate need no solver. Gate 5 runs Palace on every setup and skips
//  without one; it takes about eight minutes, so it is Category=Benchmark.
//
//  Gate 1 (index and disk agree, no home directory named) is ExampleWorkspacesTests', which picks the
//  new folder up by itself.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class Em3dExampleTests(ITestOutputHelper output) : IDisposable
{
    private const long SixteenGiB = 16L << 30;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-em3d30-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── 2. check ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>check</c> on the workspace finds no error, and its only warnings are DRC's note that the port
    /// labels carry no area — which the README explains. <b>A workspace <c>check</c> does not open the
    /// <c>.cem</c> files under a cell's <c>em/</c> folder</b>, so each setup is checked by name too: the
    /// 3D ones are clean, and the planar via setup is ONE error whose text is the README's quote —
    /// the refusal is a <c>check</c> error as well as a run refusal, not a run refusal alone.
    /// </summary>
    [Fact]
    public void Gate2_CheckIsClean_AndThePlanarViaIsItsOneError()
    {
        var numbers = Numbers();

        var ws = Check(Root());
        Assert.Equal(0, ws.Errors);
        Assert.All(ws.Warnings, w => Assert.Contains("carry no manufacturable area", w));

        foreach (string cem in Directory.EnumerateFiles(Root(), "*.cem", SearchOption.AllDirectories))
        {
            var r = Check(cem);
            string rel = Path.GetRelativePath(Root(), cem);
            output.WriteLine($"{rel}: {r.Errors} error(s), {r.Warnings.Count} warning(s)");
            Assert.Empty(r.Warnings);
            if (rel == numbers.PlanarVia.Setup)
                Assert.Equal([numbers.PlanarVia.Refusal], r.ErrorMessages.Select(Unprefixed));
            else
                Assert.Equal(0, r.Errors);
        }
    }

    // ── 3. sizes ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No user on a 16 GB machine meets the memory warning. The check Simulate makes before anything
    /// runs is evaluated against 16 GB for every driven setup; <b>and</b>, because that estimate is from
    /// volumes and says almost nothing where refinement around small metal is the mesh (brief 21's
    /// finding — it prices the via at 0.1 GB against a real 4.8 GB), every peak the README states is
    /// also under the same 75 %. Gate 5 then holds the run's own post-mesh check.
    /// </summary>
    [Fact]
    public void Gate3_EverySetupFitsUnderThe16GbWarning()
    {
        foreach (var (rel, setup, source, generated) in Setups3D())
        {
            var verdict = Em3dRunService.PalaceMemoryVerdict(generated.Problem!, setup, source,
                                                             PalaceSettings.Resolve(setup.Palace), SixteenGiB);
            output.WriteLine($"{rel}: {verdict.Level}, estimate {verdict.EstimateBytes?.ToString() ?? "(after meshing)"} B");
            Assert.Equal(Em3dMemoryLevel.Fits, verdict.Level);
        }
        foreach (var run in Numbers().Runs)
            Assert.True(run.PeakGB * (1L << 30) < Em3dMemoryVerdict.WarnFraction * SixteenGiB,
                        $"{run.Setup} peaks at {run.PeakGB} GB");
    }

    // ── 4. the writers, with no solver ──────────────────────────────────────────────────────────

    /// <summary>Every 3D setup lowers to a Gmsh script and a Palace configuration with no refusal. Nothing
    /// here finds or starts a program.</summary>
    [Fact]
    public void Gate4_EverySetupLowersToGmshAndPalace_WithNoSolver()
    {
        long gmsh = PalaceRun.GmshInvocations;
        int count = 0;
        foreach (var (rel, setup, _, generated) in Setups3D())
        {
            var settings = PalaceSettings.Resolve(setup.Palace);
            Assert.Empty(settings.Problems());
            var low = GmshGeoWriter.Write(generated.Problem!, settings);
            Assert.True(low.Ok, $"{rel}: {low.Refusal}");
            var cfg = PalaceConfigWriter.Write(generated.Problem!, low.Groups, settings);
            Assert.True(cfg.Ok, $"{rel}: {cfg.Refusal}");
            count++;
        }
        Assert.Equal(5, count);
        Assert.Equal(gmsh, PalaceRun.GmshInvocations);
    }

    // ── 5. the README's numbers, run ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every Palace setup in <c>expected-numbers.json</c>, run as Simulate runs it, reproduces each of
    /// its values within the tolerance the file states — and the run's own after-meshing memory check,
    /// on this machine, raises no warning.
    /// </summary>
    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate5_EveryPalaceRun_ReproducesTheReadmesNumbers()
    {
        var copy = CopyOfExample();
        foreach (var run in Numbers().Runs.Where(r => r.Solver == "Palace"))
        {
            var (setup, source) = Load(Path.Combine(copy, run.Setup));
            var wall = Stopwatch.StartNew();
            var result = EmRunService.Run(setup, source, Path.Combine(copy, "results"), confirmMemory: _ => true);
            output.WriteLine($"{run.Setup}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s — {result.Notes?.LastOrDefault()}");
            Assert.True(result.Status == EmRunStatus.Ok, $"{run.Setup}: {result.Error}");
            Assert.DoesNotContain(result.Warnings, w => w.Contains("Palace's memory for this run is estimated", StringComparison.Ordinal));

            foreach (var v in run.Values)
            {
                double got = Measure(v, result);
                output.WriteLine($"  {v.Quantity} {v.Label}: {got:G6} (README {v.Expected} ± {v.Tolerance})");
                Assert.True(Math.Abs(got - v.Expected) <= v.Tolerance,
                            $"{run.Setup} {v.Quantity} {v.Label}: {got:G6}, README says {v.Expected} ± {v.Tolerance}");
            }
        }
    }

    // ── kernel W ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The comparison row: kernel W on the cell's own <c>.wBond</c>, through the export the
    /// wBond window's Export Touchstone runs, gives the README's numbers.</summary>
    [Fact]
    public void KernelW_OnTheExamplesWire_GivesTheReadmesNumbers()
    {
        var run = Numbers().Runs.Single(r => r.Solver == "kernel W");
        var design = WBondIo.ReadFile(Path.Combine(Root(), run.Setup));
        var options = new WBondTouchstoneExport.Options(StartHz: 1e9, StopHz: 40e9, Points: 40,
                                                        Model: WBondNetworkModel.Distributed, SegmentsPerWire: 24);
        var freqs = WBondTouchstoneExport.BuildFrequencies(1e9, 40e9, 40, logarithmic: false);
        var snp = WBondTouchstoneExport.BuildNetwork(design, freqs, options);
        foreach (var v in run.Values)
        {
            double got = FromS(v, snp);
            output.WriteLine($"{v.Quantity} {v.Label}: {got:G6}");
            Assert.True(Math.Abs(got - v.Expected) <= v.Tolerance, $"kernel W {v.Quantity} {v.Label}: {got:G6} vs {v.Expected}");
        }
    }

    // ── 6. the planar refusal ───────────────────────────────────────────────────────────────────

    /// <summary>The planar setup on the via is refused when run, with exactly the text the README quotes.</summary>
    [Fact]
    public void Gate6_ThePlanarViaSetupRefuses_WithTheReadmesText()
    {
        var planar = Numbers().PlanarVia;
        var (setup, source) = Load(Path.Combine(Root(), planar.Setup));
        Assert.False(setup.Is3D);
        var result = EmRunService.Run(setup, source, Path.Combine(_tmp, "results"));
        Assert.Equal(EmRunStatus.Refused, result.Status);
        Assert.Equal(planar.Refusal, result.Error);
        Assert.False(Directory.Exists(Path.Combine(_tmp, "results")) &&
                     Directory.EnumerateFiles(Path.Combine(_tmp, "results"), "*", SearchOption.AllDirectories).Any());
    }

    /// <summary>One source: every value's text, and the refusal, appear verbatim in the README and in the
    /// user guide's walk-through of the example, which quote the same numbers.</summary>
    [Theory]
    [InlineData("examples/3D EM/README.md")]
    [InlineData("docs/user/src/reference/em-3d.md")]
    public void TheReadmeAndTheGuideCiteEveryNumberInTheFile(string page)
    {
        var numbers = Numbers();
        string text = File.ReadAllText(Path.Combine(RepoRoot(), page));
        Assert.Contains(numbers.PlanarVia.Refusal, text);
        foreach (var run in numbers.Runs)
        {
            foreach (string s in run.Readme) Assert.Contains(s, text);
            foreach (var v in run.Values) Assert.Contains(v.Readme, text);
        }
    }

    // ── measuring ───────────────────────────────────────────────────────────────────────────────

    private static double Measure(ExpectedValue v, EmRunResult result) => v.Quantity switch
    {
        "C_fF"     => Matrix(result, Em3dStaticResult.CapacitanceCube, v) * 1e15,
        "L_nH"     => Matrix(result, Em3dStaticResult.InductanceCube, v) * 1e9,
        "Mode_GHz" => Mode(result, Em3dEigenResult.FrequencyCube, v) / 1e9,
        "Mode_Q"   => Mode(result, Em3dEigenResult.QCube, v),
        _          => FromS(v, TouchstoneIO.ReadFile(result.SnpPath!)),
    };

    private static double Matrix(EmRunResult result, string cube, ExpectedValue v)
    {
        var c = result.Data![cube];
        var labels = c.Axis(Em3dStaticResult.AxisI).Labels!;
        int i = Array.IndexOf(labels, v.Row), j = Array.IndexOf(labels, v.Column);
        Assert.True(i >= 0 && j >= 0, $"{cube} has no [{v.Row}, {v.Column}]");
        return c.RealValues[i * labels.Length + j];
    }

    private static double Mode(EmRunResult result, string cube, ExpectedValue v)
        => result.Data![cube].RealValues[v.Mode!.Value - 1];

    /// <summary>|S21| in dB, ∠S21 in degrees, or the π-model's series inductance −Im(1/Y21)/ω in pH (F0
    /// Q1's reading), at <see cref="ExpectedValue.AtGHz"/>.</summary>
    private static double FromS(ExpectedValue v, SNP snp)
    {
        int k = Array.FindIndex(snp.Frequencies, f => Math.Abs(f - v.AtGHz!.Value * 1e9) < 1);
        Assert.True(k >= 0, $"no point at {v.AtGHz} GHz");
        var s = snp.Matrices[k];
        Complex s11 = s[0, 0], s21 = s[1, 0], s12 = s[0, 1], s22 = s[1, 1];
        double w = 2 * Math.PI * snp.Frequencies[k];
        Complex y21 = -2 * s21 / ((1 + s11) * (1 + s22) - s12 * s21) / 50.0;
        return v.Quantity switch
        {
            "S21_dB"     => 20 * Math.Log10(s21.Magnitude),
            "S21_deg"    => s21.Phase * 180 / Math.PI,
            "Lseries_pH" => (-1 / y21).Imaginary / w * 1e12,
            _            => throw new InvalidOperationException($"unknown quantity '{v.Quantity}'"),
        };
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string Root()
        => Path.Combine(ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/"), "3D EM");

    private IEnumerable<(string Rel, EmSetup Setup, EmLayoutSource Source, Em3dGenerationResult Generated)> Setups3D()
    {
        foreach (string cem in Directory.EnumerateFiles(Root(), "*.cem", SearchOption.AllDirectories).Order())
        {
            var (setup, source) = Load(cem);
            if (!setup.Is3D) continue;
            var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
            string rel = Path.GetRelativePath(Root(), cem);
            Assert.True(generated.Ok, $"{rel}: {generated.Refusal}");
            yield return (rel, setup, source, generated);
        }
    }

    private static (EmSetup, EmLayoutSource) Load(string cem)
    {
        var setup = EmSetupPersistence.LoadFromFile(cem);
        var resolution = EmSetupResolver.Resolve(cem, setup.LayoutRef,
                                                 WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(cem)),
                                                 new TechnologyCache());
        Assert.True(resolution.Source is { Technology: not null }, $"{cem}: {string.Join(" ", resolution.Diagnostics)}");
        return (setup, resolution.Source!);
    }

    private string CopyOfExample()
    {
        string to = Path.Combine(_tmp, "3D EM");
        foreach (string file in Directory.EnumerateFiles(Root(), "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}results{Path.DirectorySeparatorChar}")) continue;
            string dest = Path.Combine(to, Path.GetRelativePath(Root(), file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return to;
    }

    private sealed record CheckRun(int Errors, List<string> Warnings, List<string> ErrorMessages);

    /// <summary><c>check --json</c> as a process: the verb a user runs, not a transcription of it.</summary>
    private static CheckRun Check(string path)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
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
        var errors = Of("error");
        return new CheckRun(errors.Count, Of("warning"), errors);
    }

    /// <summary>A check message is prefixed with the document's path; the refusal is what follows it.</summary>
    private static string Unprefixed(string message)
    {
        int at = message.IndexOf(": ", StringComparison.Ordinal);
        return at >= 0 && message[..at].EndsWith(".cem", StringComparison.Ordinal) ? message[(at + 2)..] : message;
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Em3dExampleTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    // ── expected-numbers.json ───────────────────────────────────────────────────────────────────

    private sealed record ExpectedValue(string Quantity, string Label, double Expected, double Tolerance, string Readme,
                                        double? AtGHz = null, string? Row = null, string? Column = null, int? Mode = null);

    private sealed record ExpectedRun(string Setup, string Solver, double PeakGB, List<string> Readme, List<ExpectedValue> Values);

    private sealed record PlanarRefusal(string Setup, string Refusal);

    private sealed record ExpectedNumbers(PlanarRefusal PlanarVia, List<ExpectedRun> Runs);

    private static ExpectedNumbers Numbers()
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(), "expected-numbers.json")))!;
}
