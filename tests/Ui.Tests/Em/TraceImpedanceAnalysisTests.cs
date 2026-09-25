// Trace Impedance Analysis — every trace on a layout, end to end (round 8). One test per claim: a
// trace is found and answered with the probe's own number; a bend does not split it or flag it; a
// slot in the plane under it is flagged where it is; a cancelled run keeps the layers it finished;
// and the headless verb writes the report and decides its exit code by the verdicts.

using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests.Em;

[Collection(LvsCliConsoleCollection.Name)]
public class TraceImpedanceAnalysisTests
{
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static Technology Tech() => new()
    {
        Name = "analysis",
        Layers =
        [
            new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
            new LayerDef { Key = Gnd, Name = "Plane", Purpose = "conductor" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35),
                                   SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(500), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35),
                                   SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            ],
        },
    };

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2) };

    private static TraceImpedanceReport Analyze(IReadOnlyList<LayoutShape> shapes, double target = 50, double tol = 10,
                                                IReadOnlyList<LayerKey>? layers = null, RunControl? control = null) =>
        TraceImpedanceAnalysis.Analyze(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron,
            new TraceImpedanceOptions { TargetOhms = target, TolerancePercent = tol, Layers = layers ?? [Top] }, control);

    /// <summary>One straight microstrip: found as ONE trace, and its Z0 is the probe's, because both
    /// cut it through the same cross-section solve.</summary>
    [Fact]
    public void AStraightMicrostrip_IsOneTrace_AtTheProbesZ0()
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        var probe = TraceImpedanceProbe.Probe(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, 0, 0, Top);

        var report = Analyze(shapes);

        var trace = Assert.Single(Assert.Single(report.Layers).Traces);
        Assert.True(probe.Ok, probe.Refusal);
        Assert.Equal(probe.Z0Ohms, trace.Z0Mean!.Value, probe.Z0Ohms * 0.005);
        Assert.Equal(12000, trace.Length / LayoutUnits.DefaultDbuPerMicron, 12000 * 0.01);
        Assert.Equal("microstrip", Assert.Single(trace.Configurations).Name);
    }

    /// <summary>A trace with a 90° bend is one trace: the bend's corner is not cut, and not flagged.</summary>
    [Fact]
    public void ABentTrace_IsOneTrace_AndItsCornerIsNotFlagged()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, 500, 500),     // along x, ending in the corner square
            Rect(Top, -500, -500, 500, 6000),     // up y from the same square
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];
        var probe = TraceImpedanceProbe.Probe(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, Um(-3000), 0, Top);

        var report = Analyze(shapes, target: probe.Z0Ohms, tol: 5);

        var trace = Assert.Single(Assert.Single(report.Layers).Traces);
        Assert.Equal(2, trace.Pieces.Count);
        Assert.Empty(trace.Issues);
        Assert.Equal(TraceVerdict.Pass, trace.Verdict);
    }

    /// <summary>A window cut in the plane across the trace is a return-path break, placed at the window.</summary>
    [Fact]
    public void ASlotInThePlane_IsFlaggedWhereItIs()
    {
        var plane = new PolygonShape
        {
            Layer = Gnd,
            Xy = [Um(-8000), Um(-8000), Um(8000), Um(-8000), Um(8000), Um(8000), Um(-8000), Um(8000)],
            Holes = [[Um(2500), Um(-1500), Um(2500), Um(1500), Um(3500), Um(1500), Um(3500), Um(-1500)]],
        };
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), plane];

        var trace = Assert.Single(Assert.Single(Analyze(shapes).Layers).Traces);

        var broken = Assert.Single(trace.Issues, i => i.Kind == TraceIssueKind.ReturnBroken);
        Assert.InRange(broken.X, Um(2500), Um(3500));
        Assert.Equal(TraceVerdict.Fail, trace.Verdict);
    }

    /// <summary>Cancel stops the run between layers and the report keeps every layer that finished
    /// (owner, 2026-09-25) — and says it was cancelled.</summary>
    [Fact]
    public void ACancelledRun_KeepsTheLayersItFinished()
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token = cts.Token,
            MinReportIntervalMs = 0,
            Progress = new Inline(p => { if (p.Stage.Contains("(2 of 2)", StringComparison.Ordinal)) cts.Cancel(); }),
        };

        var report = Analyze(shapes, layers: [Top, Gnd], control: control);

        Assert.True(report.Cancelled);
        Assert.Equal("Top", Assert.Single(report.Layers).Name);
        Assert.Equal(["Top", "Plane"], report.LayersRequested);
    }

    private sealed class Inline(Action<RunProgress> a) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => a(value);
    }

    /// <summary>
    /// The verb on a layout on disk: it writes the PDF the editor writes (summary, then a map and a
    /// table per layer), exits 0 when every trace passes and 1 when one fails, and refuses a layer
    /// name that is not copper with the names that are.
    /// </summary>
    [Fact]
    public void TheVerb_WritesTheReport_AndExitsByTheVerdicts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-impedance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            var view = new LayoutView { TechRef = "board.ctech" };
            view.Shapes.Add(Rect(Top, -6000, -500, 6000, 500));
            view.Shapes.Add(Rect(Gnd, -8000, -8000, 8000, 8000));
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);
            string pdf = Path.Combine(dir, "report.pdf");
            double z0 = TraceImpedanceProbe.Probe(view.Shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, 0, 0, Top).Z0Ohms;

            Assert.Equal(0, InProcess("impedance", clay, "--layers", "Top",
                                      "--target", z0.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                                      "-o", pdf, "--json"));
            string text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
            Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, @"/Type /Page\b(?!s)").Count);

            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Top", "--target", "10", "--json"));
            var trace = JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedance")
                                    .GetProperty("layers")[0].GetProperty("traces")[0];
            Assert.Equal("fail", trace.GetProperty("verdict").GetString());

            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Silk", "--json"));
            Assert.Contains("impedance.layers.unknown", _last, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private string _last = "";

    private int InProcess(params string[] args)
    {
        var real = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            JsonRun.Reset();
            int exit = CliEntry.Run(args);
            _last = buffer.ToString();
            return exit;
        }
        finally { Console.SetOut(real); }
    }
}
