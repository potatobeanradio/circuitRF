// brief-img-14-sparameter-plots-to-touchstone.md §4 — `circuitrf recognize` on plot pictures, run as a PROCESS:
// a Smith trace with no frequency exits 1 naming --freq; and src/Cli names nothing of the plot
// reader but its entry points, their options and their results (R-im14-15, AS-7's source-scan rule).

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PlotCliVerbTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        var result = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], args);
        output.WriteLine($"$ {string.Join(' ', args)} -> {result.ExitCode}\n{result.StdOut}\n{result.StdErr}");
        return result;
    }

    [Fact]
    public void ASmithTraceWithoutFrequency_Exits1_NamingFreq()
    {
        string target = Path.Combine(pictures.Dir, "cli", "nofreq.s1p");
        var (exit, _, stderr) = Cli("recognize", FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "smith-z.png")), "-o", target);
        Assert.Equal(1, exit);
        Assert.Contains("--freq T1=", stderr);
        Assert.False(File.Exists(target));
    }

    [NonLinuxFact("Types the right axis from two labels the reader found, and on Linux's FreeType raster it finds one.")]
    public void Y2Axis_CalibratesTheRightAxis_AndAxisRight_MovesATrace()
    {
        // brief-pdg-1-two-y-axes.md §3 (6): the right axis typed from two of its own labels' pixels, and the left trace
        // moved onto it.
        string png = pictures.Picture("two-y");
        var read = PlotPictureSet.Read(null, png);
        var y2 = read.Plots[0].Rect!.Y2Labels;
        string left = read.Traces.First(t => t.Axis == PlotSide.Left).Name;
        string typed = FormattableString.Invariant($"{y2[0].Pixel}px={y2[0].Value},{y2[^1].Pixel}px={y2[^1].Value}");
        var (exit, stdout, stderr) = Cli("recognize", png, "--y2-axis", typed, "--trace", $"{left}=S21:db,axis=right",
                                         "--csv", Path.Combine(pictures.Dir, "cli", "two-y.csv"), "--json");
        Assert.True(exit == 0, stderr);

        var plots = System.Text.Json.Nodes.JsonNode.Parse(stdout)!["result"]!["recognize"]!["plots"]!;
        Assert.True((bool)plots["plots"]![0]!["y2"]!["manual"]!);
        var moved = plots["traces"]!.AsArray().Single(t => (string?)t!["name"] == left)!;
        Assert.Equal(("right", "user"), ((string?)moved["axis"], (string?)moved["axisEvidence"]));
    }

    [Fact]
    public void Fit_PutsAFitInTheJson_AndAYFitColumnInTheCsv()
    {
        // brief-pdg-2-curve-fit.md §3 (9).
        string png = pictures.Picture("s21db");
        string csv = Path.Combine(pictures.Dir, "cli", "fit.csv");
        var (exit, stdout, stderr) = Cli("recognize", png, "--trace", "T1=S21:db", "--fit", "T1=poly:2", "--csv", csv, "--json");
        Assert.True(exit == 0, stderr);

        var fit = System.Text.Json.Nodes.JsonNode.Parse(stdout)!["result"]!["recognize"]!["plots"]!["traces"]!.AsArray()
                      .Single(t => (string?)t!["name"] == "T1")!["fit"]!;
        Assert.Equal(("polynomial", 2, "GHz"), ((string?)fit["model"], (int?)fit["degree"], (string?)fit["xUnit"]));
        Assert.Equal(3, fit["coefficients"]!.AsArray().Count);
        Assert.False(string.IsNullOrEmpty((string?)fit["expression"]));

        var lines = File.ReadAllLines(csv).Where(l => !l.StartsWith('#')).ToList();
        Assert.Equal("y_fit", lines[0].Split(',')[^1]);
        Assert.All(lines.Skip(1).Where(l => l.StartsWith("T1,", StringComparison.Ordinal)), l => Assert.NotEmpty(l.Split(',')[^1]));
    }

    [Fact]
    public void SrcCli_NamesNoPlotReadingStep()
    {
        string repo = PalaceBackendTests.RepoRoot();
        // The entry points, their options and results, and the wire grammar of an assignment (PlotQuantity).
        string[] allowed =
        [
            "PlotReading", "PlotReadingOptions", "PlotPictureInput", "PlotAxisOverride", "PlotTraceOverride", "PlotFrequencyOverride",
            "PlotSession", "PlotPictureRead", "PlotRead", "PlotTraceRead", "PlotSample", "PlotFinding", "PlotMarker", "TraceFrequency",
            "TouchstoneAssembly", "TouchstoneAssemblyOptions", "TouchstoneAssemblyResult", "AssemblyRefusal", "AssemblyGrid",
            "MatrixCell", "PhaseRule", "MissingPhase", "FillRule", "PlotCsv", "PlotKind", "PlotType", "PlotQuantity", "PlotQuantityKind",
            "PlotAssignment", "AxisClicks", "AxisScale", "AxisCalibration", "AxisLabel", "RectCalibration", "SmithCalibration",
            "PolarCalibration", "LegendEntry", "TraceCurve", "TraceStyle", "TraceHints", "PlotFrame", "PlotAxis", "PlotSide",
            // brief-pdg-2's fits: the option a flag spells, the entry point the overlay and the CSV take them from, the result.
            "PlotFitOverride", "TraceFitSpec", "TraceFitModel", "TraceFit", "TraceFitResult",
            // brief-pdg-3's probe: the entry point --probe reads through, and what it returns.
            "TraceProbe", "TraceProbeHit", "TraceProbeText", "TraceProbeReading",
            // brief-pdg-4's plot read by hand: the records its flags spell, and Find curve's entry point and result.
            "ManualPlot", "ManualQuad", "ManualAxis", "ManualCurve", "ManualSampleSpec", "ManualSampleMethod", "ManualCurves", "ManualFollowResult",
        ];
        var declared = Directory.GetFiles(Path.Combine(repo, "src", "Design", "Imaging", "Plots"), "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f),
                @"^(?:public|internal)\s+(?:(?:static|sealed|readonly|abstract|partial)\s+)*(?:record\s+struct|record|class|enum|struct|interface)\s+(\w+)",
                RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.Contains("PlotTraces", declared);   // not vacuous
        Assert.Contains("MinimumPhase", declared);
        var forbidden = declared.Except(allowed).ToList();

        var hits = Directory.GetFiles(Path.Combine(repo, "src", "Cli"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f =>
            {
                string code = Regex.Replace(Regex.Replace(Regex.Replace(File.ReadAllText(f), @"/\*.*?\*/", "", RegexOptions.Singleline),
                                                          @"//[^\n]*", ""), "\"(?:[^\"\\\\\\n]|\\\\.)*\"", "\"\"");
                // A member access (MissingPhase.MinimumPhase) is not a type named; a qualified type (Plots.Foo) is.
                return forbidden.Where(t => Regex.IsMatch(code, $@"(?<![\w.]){t}\b|Plots\.{t}\b")).Select(t => $"{Path.GetFileName(f)}: {t}");
            }).ToList();
        Assert.Empty(hits);
    }
}
