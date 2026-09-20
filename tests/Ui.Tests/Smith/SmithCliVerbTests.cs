// ================================================================
//  brief-smith-10-cli-verb.md §2 — the gate every verb in this repository has, for
//  `circuitrf smith`.
//
//  ── Why two ways of driving the verb, and which gate uses which ──────────────────────────────────
//
//  The byte-identity gates launch the real CLI DLL as a PROCESS, on the EmCliVerbTests /
//  ConvertCliVerbTests / RenderCliVerbTests standard, and compare what it wrote against the same
//  src/Design/Smith evaluation and the same CircuitRF.Render composition performed IN THIS PROCESS.
//  A same-process call could never show the failure that mattered when the renderers came below the
//  firewall — a second process with no Avalonia host under it drawing in a substituted typeface —
//  so the process is not an inconvenience, it is the measurement.
//
//  The cancellation gate is about what happens INSIDE one process: a RunControl cancelled before the
//  call, which is not observable across two processes that each answer one command and exit. That is
//  why CircuitRF.Cli grants InternalsVisibleTo to this assembly (see its .csproj).
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using CircuitRF.Cli;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// <c>circuitrf smith</c> (brief-smith-10-cli-verb.md §2). <b>One test per claim.</b>
///
/// <para><b>What the byte-identity gates actually prove.</b> Not that the verb agrees with itself —
/// that the verb's answer is the APPLICATION's. The in-process side of each comparison calls
/// <c>SmithReadings</c>/<c>SmithCascade</c> and <c>SmithPlotBuilder</c>/<c>SmithChartChrome</c>
/// directly, which are the functions <c>SmithChartViewModel</c> calls on every edit, and those are
/// gated against the engine and against the window by the rest of this folder. The chain is what
/// makes it an end-to-end claim.</para>
///
/// <para><b>Party to the typeface collection</b> (<see cref="SkiaFontsTypefaceCollection"/>), by its
/// second membership rule — <i>a class that compares rendered TEXT bytes against another process
/// belongs here</i> — which this class met from the day it was written and was not declared on. The
/// symptom is the one that type documents: a neighbour holds <c>SkiaFonts.TestOverrideTypeface</c>
/// for the length of one test, the in-process render of this gate lands in that window and comes
/// back in Helvetica, and the fresh CLI process has no override to read and comes back in IBM Plex
/// Sans. Reliably green alone, red beside its neighbours — and it only surfaced when an unrelated
/// change moved the schedule.</para>
/// </summary>
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class SmithCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-smith-cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const double DesignHz = 2.0e9;
    private const double ChartZ0  = 50.0;

    /// <summary>The chart box the verb composes at — <c>SmithPlotBuilder.NominalCanvas</c>, which is
    /// the window's own square plot. Stated here so a change to the verb's framing fails this test
    /// rather than quietly producing a differently-shaped picture.</summary>
    private const double Box = SmithPlotBuilder.NominalCanvas;

    // ── gate 1: byte identity against the in-process call ────────────────────

    /// <summary>
    /// The verb, as a process, writes the Γ that <c>SmithReadings</c> computes — byte for byte,
    /// with no exclusion at all.
    /// </summary>
    /// <remarks>
    /// <b>There is no timestamp to exclude, deliberately.</b> The verb writes its Touchstone with
    /// <c>includeDateComment</c> left off, so the same document written twice is the same bytes —
    /// which is worth more than the provenance line would have been: a caller can diff two revisions
    /// of a matching network and see only what changed about the network.
    /// </remarks>
    [Fact]
    public void WritingTheLoadAsTouchstone_IsTheEvaluatorsOwnAnswer()
    {
        string doc = WriteDesign("match.csmith", Demo());
        string outPath = Path.Combine(_root, "cli.s1p");

        var (exit, stdout, stderr) = RunCli("smith", doc, "-o", outPath);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        // The same answer, in process, from the functions the window reads its status strip from.
        var design  = SmithDesignIo.LoadFromFile(doc);
        var reading = SmithReadings.At(design, DesignHz, Path.GetDirectoryName(doc));

        var lines = File.ReadAllLines(outPath);
        var data  = lines.First(l => !l.StartsWith('!') && !l.StartsWith('#'))
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // TouchstoneIO writes at G10 — ten SIGNIFICANT digits, which is what the tolerances are for.
        // Tighter than the file's own precision would be a test of the writer's format string.
        Assert.Equal(reading.Gamma.Magnitude,
                     double.Parse(data[1], System.Globalization.CultureInfo.InvariantCulture), 8);
        Assert.Equal(reading.Gamma.Phase * 180.0 / Math.PI,
                     double.Parse(data[2], System.Globalization.CultureInfo.InvariantCulture), 6);

        // And the whole file is reproducible: a second run of the same document is the same bytes.
        string again = Path.Combine(_root, "cli-again.s1p");
        Assert.Equal(0, RunCli("smith", doc, "-o", again).ExitCode);
        Assert.Equal(File.ReadAllBytes(outPath), File.ReadAllBytes(again));
    }

    /// <summary>
    /// Gate 1, the half that matters most: the SVG the verb writes as a PROCESS is the SVG
    /// <c>SmithPlotBuilder</c> + <c>SmithChartChrome</c> + <c>PlotComposer</c> produce in this one.
    /// </summary>
    /// <remarks>
    /// <b>This is what proves R-smith10-3.</b> A second drawing path in the verb would produce a
    /// picture that is plausible, and a plausible picture is indistinguishable from a right one until
    /// somebody compares a screenshot with an export. Here they are compared.
    ///
    /// <para><b>The chrome is in the comparison</b>, not just the traces: the in-process side hands
    /// <c>PlacedPlot.Overlay</c> the same delegate the verb does, so a verb that dropped the
    /// arrowheads and the load-point labels — the silent-absence failure that field exists for —
    /// fails here rather than looking fine.</para>
    /// </remarks>
    [Fact]
    public void DrawingTheChartAsAProcess_WritesTheBytesTheRendererWrites()
    {
        string doc = WriteDesign("chart.csmith", Demo(sweep: true, constantQ: true));
        string outPath = Path.Combine(_root, "cli.svg");

        var (exit, stdout, stderr) = RunCli("smith", doc, "-o", outPath);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        AssertSameSvg(File.ReadAllText(outPath), InProcessChartSvg(doc), "chart");
    }

    /// <summary>
    /// The same gate on a document that carries an <b>overlay</b>.
    /// </summary>
    /// <remarks>
    /// <b>The overlay path is the one that changed</b> (brief 12): a `.csmith` now stores its
    /// reference material as the Data Display's own <c>TraceConfig</c>, and both the window and the
    /// verb restore one through <c>PlotConfigLoader.LoadTrace</c>. A verb that resolved it any other
    /// way would draw a curve that is plausible, which is indistinguishable from a right one until
    /// somebody compares a picture with an export. Here they are compared.
    /// </remarks>
    [Fact]
    public void DrawingAChartWithAnOverlay_WritesTheBytesTheRendererWrites()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ref.s1p"),
            "# HZ S RI R 75\n1.0e9 0.0 0.0\n2.0e9 0.1 0.0\n3.0e9 0.2 0.1\n");

        var design = Demo();
        design.Overlays.Add(SmithOverlays.Write(new TraceConfig
        {
            SourcePath           = "ref.s1p",
            YAxis                = DependentVarFormat.Complex,
            Z0                   = "50",
            Z0Override           = true,
            ExcludeFromAutoscale = true,
        }));

        string doc     = WriteDesign("overlay.csmith", design);
        string outPath = Path.Combine(_root, "cli-overlay.svg");

        var (exit, stdout, stderr) = RunCli("smith", doc, "-o", outPath);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        AssertSameSvg(File.ReadAllText(outPath), InProcessChartSvg(doc), "chart with overlay");
    }

    // ── gate 2: the verb holds no logic of its own (R-smith10-1) ─────────────

    /// <summary>
    /// A comment-stripped scan of <c>src/Cli/Smith.cs</c>: no second evaluator, no second
    /// <c>Plot</c>, no arithmetic on impedances.
    /// </summary>
    /// <remarks>
    /// <b>A rule stated in a header is not a rule the code follows</b> — <c>AuthoringCliVerbTests</c>'
    /// own finding, and the reason this scan exists rather than a comment. What it looks for is the
    /// SHAPE each duplicate would take: the VSWR quotient, the mismatch decibel, a conjugate of
    /// any kind, and a <c>Plot</c> constructed rather than asked for.
    /// </remarks>
    [Fact]
    public void TheVerbHoldsNoEvaluatorAndNoPlotOfItsOwn()
    {
        string code = StripComments(ReadRepoFile("src/Cli/Smith.cs"));

        foreach (string forbidden in new[]
        {
            "new Plot(",            // the plot is SmithPlotBuilder's (NewChartPlot)
            "Complex.Conjugate",    // every conjugate in this tool is SmithReadings' or the scene's
            "Math.Log10",           // ... and the mismatch decibel is too
            "1.0 - mag",            // ... as is the VSWR quotient, in either spelling
            "1.0 + mag",
            "SmithQArcs",           // the arcs are the scene's, never re-derived here
        })
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);

        // And it DOES call the ones that own those answers — the vacuity guard, without which the
        // assertions above would pass on an empty file.
        foreach (string required in new[]
        {
            "SmithReadings.",
            "SmithCascade.Evaluate",
            "SmithBand.Evaluate",
            "SmithPlotBuilder.NewChartPlot",
            "SmithPlotBuilder.Fill",
            "SmithChartChrome.Draw",
            "RenderDataDisplay.Emit",
        })
            Assert.Contains(required, code, StringComparison.Ordinal);
    }

    // ── gate 3: a refusal BY KIND ────────────────────────────────────────────

    /// <summary>
    /// A <c>.csch</c>, a <c>.clay</c> and a <c>.crail</c> each exit 1 naming what was FOUND.
    /// </summary>
    /// <remarks>
    /// <b>By kind, not "unreadable".</b> A caller who handed over a schematic very likely meant a run
    /// verb, and "that is a schematic" is the sentence that says so; "that file did not parse" sends
    /// them to look at the file.
    /// </remarks>
    [Theory]
    [InlineData("other.csch",  "schematic")]
    [InlineData("other.clay",  "layout")]
    [InlineData("other.crail", "rail")]
    public void AnyOtherDocumentKind_IsRefusedByKind(string name, string kind)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "{}");

        var (exit, _, stderr) = RunCli("smith", path);
        output.WriteLine(stderr);

        Assert.Equal(1, exit);
        Assert.Contains(kind, stderr, StringComparison.Ordinal);
        Assert.Contains(".csmith", stderr, StringComparison.Ordinal);
    }

    // ── gate 4: --at against the generator table's span (R-smith2-5) ─────────

    /// <summary>
    /// <c>--at</c> outside the table refuses <b>with the span in the sentence</b>, and a one-row
    /// table accepts any frequency at all.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because the exception is the interesting one.</b> One row is one impedance,
    /// flat, so every frequency is legal against it — and a verb that refused there would be stricter
    /// than the window, which is the same defect as being laxer.
    ///
    /// <para><b>The sentence is <c>SmithDesign.Refusal</c>'s, not the verb's</b> (R-smith10-4). That
    /// is why the assertion is on the span's numbers: they can only have come from the document's own
    /// rule.</para>
    /// </remarks>
    [Fact]
    public void AnAtOutsideTheGeneratorTable_RefusesWithTheSpan()
    {
        string doc = WriteDesign("span.csmith", Demo());

        var (exit, _, stderr) = RunCli("smith", doc, "--at", "5GHz");
        output.WriteLine(stderr);

        Assert.Equal(1, exit);
        Assert.Contains("1.8 GHz", stderr, StringComparison.Ordinal);
        Assert.Contains("2.2 GHz", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AOneRowGeneratorTable_AcceptsAnyFrequency()
    {
        var design = Demo();
        design.Generator.Rows.Clear();
        design.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 10.0, -10.0));

        string doc = WriteDesign("flat.csmith", design);

        var (exit, stdout, stderr) = RunCli("smith", doc, "--at", "17GHz");
        output.WriteLine(stdout + stderr);

        Assert.Equal(0, exit);
        Assert.Contains("17 GHz", stdout, StringComparison.Ordinal);
    }

    // ── gate 5: cancellation ─────────────────────────────────────────────────

    /// <summary>
    /// A run cancelled through <c>RunControl</c> exits 130 and leaves NO file.
    /// </summary>
    /// <remarks>
    /// Nothing is written until the bytes are complete — <c>VectorPage</c>'s own property, which
    /// makes "a cancelled run writes nothing" true by construction rather than by a guard each verb
    /// has to remember.
    /// </remarks>
    [Fact]
    public void ACancelledRun_Exits130AndWritesNothing()
    {
        string doc = WriteDesign("cancel.csmith", Demo());
        string outPath = Path.Combine(_root, "cancelled.svg");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using (RunHost.Install(cts.Token, observer: null))
            Assert.Equal(130, InProcessCli("smith", doc, "-o", outPath));

        Assert.False(File.Exists(outPath), "a cancelled run published a partial picture");
    }

    /// <summary>The vacuity guard: the same call with no cancellation writes the file, so the 130 is
    /// the cancellation and not a refusal that happens to share its shape.</summary>
    [Fact]
    public void TheSameRunWithNoCancellation_Succeeds()
    {
        string doc = WriteDesign("not-cancelled.csmith", Demo());
        string outPath = Path.Combine(_root, "not-cancelled.svg");

        Assert.Equal(0, InProcessCli("smith", doc, "-o", outPath));
        Assert.True(File.Exists(outPath));
    }

    // ── gate 6: --json and -o co-exist ───────────────────────────────────────

    /// <summary>
    /// The picture is written and stdout is still parseable JSON carrying both halves.
    /// </summary>
    /// <remarks>
    /// <b>There is no picture on stdout</b> — <c>render</c>'s own rule, and the reason <c>-o</c> is
    /// required for one. The document carries the <c>render</c> block (what was written) and the
    /// <c>smith</c> block (what was evaluated), because a caller that asked for both asked for both.
    /// </remarks>
    [Fact]
    public void JsonAndAPicture_CoExist()
    {
        string doc = WriteDesign("json.csmith", Demo(sweep: true));
        string outPath = Path.Combine(_root, "json.svg");

        var (exit, stdout, stderr) = RunCli("smith", doc, "-o", outPath, "--json");
        output.WriteLine(stderr);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(outPath));

        using var json = JsonDocument.Parse(stdout);
        var result = json.RootElement.GetProperty("result");

        Assert.True(result.TryGetProperty("render", out _), stdout);
        var smith = result.GetProperty("smith");

        Assert.Equal(ChartZ0, smith.GetProperty("z0Ohm").GetDouble(), 12);
        Assert.Equal(DesignHz, smith.GetProperty("frequencyHz").GetDouble(), 3);

        // The WALK is the field that makes this verb worth running on a build machine: one row per
        // node, generator first, load last.
        var nodes = smith.GetProperty("nodes");
        Assert.Equal(3, nodes.GetArrayLength());
        Assert.False(nodes[0].TryGetProperty("element", out _));   // node 0 is the generator
        Assert.Equal("L1", nodes[1].GetProperty("element").GetString());
        Assert.Equal("C1", nodes[2].GetProperty("element").GetString());

        Assert.True(smith.GetProperty("band").GetProperty("points").GetInt32() > 1);
    }

    // ── --set, which a .csmith has nothing for ───────────────────────────────

    /// <summary>
    /// <c>--set</c> is refused, naming what a <c>.csmith</c> actually holds.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than dropped.</b> <c>cli.md</c> §3.3's accepted-and-dropped defect is a run
    /// that answered a different question than the one asked, and it is what
    /// <c>rail</c>'s own <c>--set</c> refusal exists for one verb along. The brief's option table
    /// lists the flag; the document has no expression scope for it to land on, and saying so is the
    /// only honest answer.
    /// </remarks>
    [Fact]
    public void SetIsRefusedBecauseACsmithDeclaresNoVariables()
    {
        string doc = WriteDesign("set.csmith", Demo());

        var (exit, _, stderr) = RunCli("smith", doc, "--set", "x=1");
        output.WriteLine(stderr);

        Assert.Equal(1, exit);
        Assert.Contains("no variables", stderr, StringComparison.Ordinal);
        Assert.Contains("--at", stderr, StringComparison.Ordinal);
    }

    // ── the fixture ──────────────────────────────────────────────────────────

    /// <summary>
    /// A two-element L-match into a low, capacitive generator — three generator rows, so the span
    /// rule has something to say and the chart carries three load points.
    /// </summary>
    private static SmithDesign Demo(bool sweep = false, bool constantQ = false)
    {
        var d = new SmithDesign { Name = "L-match demo" };
        d.Chart.Z0Ohm             = ChartZ0;
        d.Chart.DesignFrequencyHz = DesignHz;

        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 10.0, -12.0));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 10.0, -10.0));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 11.0,  -8.0));

        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L1",
            Values = new SmithElementValues { LHenry = 2.2e-9 },
        });
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.C, Placement = SmithPlacement.Shunt, Name = "C1",
            Values = new SmithElementValues { CFarad = 1.2e-12 },
        });

        if (sweep)
        {
            d.Sweep.Enabled = true;
            d.Sweep.StartHz = 1.8e9;
            d.Sweep.StopHz  = 2.2e9;
            d.Sweep.Points  = 21;
        }
        if (constantQ) { d.ConstantQ.Enabled = true; d.ConstantQ.Q = 1.5; }

        return d;
    }

    private string WriteDesign(string name, SmithDesign design)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        SmithDesignIo.SaveToFile(path, design);
        return path;
    }

    // ── the in-process reference ─────────────────────────────────────────────

    /// <summary>
    /// The chart, composed here, by the calls the verb makes and the window makes.
    /// </summary>
    /// <remarks>
    /// <b>Written out rather than factored into the verb and called</b>: a comparison of the verb
    /// with a helper the verb itself uses would be a comparison of it with a copy of itself. What is
    /// stated here is the composition — the builder, the chrome, the placement and the page — and
    /// each of those is a name the verb also says, which is the claim.
    /// </remarks>
    private static string InProcessChartSvg(string documentPath)
    {
        var design = SmithDesignIo.LoadFromFile(documentPath);
        string dir = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;

        // The document's reference material, resolved the way the WINDOW resolves it: through the
        // `.cdd`'s own PlotConfigLoader.LoadTrace over an IPlotDataSources. Stated here rather than
        // taken from the verb, for the reason above — a comparison with the verb's own helper would
        // be a comparison of it with a copy of itself.
        var sources  = new SmithDocumentSources(dir);
        var overlays = design.Overlays
            .Select(SmithOverlays.Read)
            .Where(cfg => cfg is not null)
            .Select(cfg => (Cfg: cfg!, Trace: SmithOverlays.Load(cfg!, sources)))
            .Where(p => p.Trace is not null)
            .Select(p => new SmithOverlayTrace(SmithOverlays.Key(p.Cfg), p.Trace!))
            .ToList();

        var scene = SmithPlotBuilder.BuildScene(design, dir, (Box, Box), window: null);
        var plot  = SmithPlotBuilder.NewChartPlot();
        SmithPlotBuilder.Fill(plot, scene, design, autoscale: true, overlays);

        var placed = RenderDataDisplay.Place(
            plot, left: 0, top: 0, width: Box, height: Box, FreqUnit.GHz,
            hasMultipleSources: false, aliasFor: null,
            overlay: (canvas, tf, theme) =>
                SmithChartChrome.Draw(canvas, tf, theme, scene, design, SmithChromeState.None));

        var page     = PagePlacement.Letter;
        var current  = AppSettings.Current;
        var settings = new AppSettings
        {
            ExportTheme                    = current.ExportTheme,
            ExportTransparentBackground    = current.ExportTransparentBackground,
            MarkerBoxTransparentBackground = current.MarkerBoxTransparentBackground,
            AlwaysDisplayDataSourcePrefix  = current.AlwaysDisplayDataSourcePrefix,
        };

        return PlotDocumentWriter.BuildSvgString(
            canvas => PlotComposer.Render(canvas, [placed], RenderTheme.Light, settings, page),
            page);
    }

    // ── driving the verb ─────────────────────────────────────────────────────

    private static int InProcessCli(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        // Both pipes drained CONCURRENTLY: reading one to the end and only then the other deadlocks
        // the moment the child fills the pipe it is not being read from, and this verb writes a stage
        // line per phase to stderr.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(SmithCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    /// <summary>
    /// Byte identity, with the ONE exclusion RND-1 measured and named: Skia's SVG device numbers its
    /// <c>clipPath</c> elements from a counter it does not reset per canvas, so two renders in
    /// DIFFERENT processes carry different ids for the same clip. It is a property of neither code
    /// path. Applied only where the raw bytes actually differ, and reported when it is.
    /// </summary>
    private void AssertSameSvg(string fromCli, string inProcess, string what)
    {
        if (fromCli == inProcess) { output.WriteLine($"{what}: byte-identical"); return; }

        output.WriteLine($"{what}: differ before Skia-id normalisation "
                       + $"({fromCli.Length} vs {inProcess.Length} bytes)");
        output.WriteLine(FirstDifference(fromCli, inProcess) ?? "(lengths only)");
        Assert.Equal(StripSkiaIds(inProcess), StripSkiaIds(fromCli));
    }

    private static string StripSkiaIds(string svg)
        => Regex.Replace(svg, @"\b(cl|img|gr|fp)_[0-9a-z]+\b", "$1_N");

    private static string? FirstDifference(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return $"first difference at {i}: …{a.Substring(Math.Max(0, i - 60), Math.Min(120, a.Length - Math.Max(0, i - 60)))}…"
                     + $"\n                 vs …{b.Substring(Math.Max(0, i - 60), Math.Min(120, b.Length - Math.Max(0, i - 60)))}…";
        return a.Length == b.Length ? null : $"lengths differ: {a.Length} vs {b.Length}";
    }
}
