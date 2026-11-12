// Trace Impedance Analysis — every trace on a layout, end to end (round 8). One test per claim: a
// trace is found and answered with the probe's own number; a bend does not split it or flag it; a
// slot in the plane under it is flagged where it is; a cancelled run keeps the layers it finished;
// and the headless verb writes the report and decides its exit code by the verdicts. The warning tier
// (brief-impedance-1): a stretch inside the warning band warns, an electrically short one warns, a
// broken return never does, a reference step alone warns, and the verb exits 0 on warnings. The scope
// (brief-impedance-2): it saves solves, it never removes copper, the survey solves one cut per width
// class, the review round-trips in the .clay, and the verb applies the saved scope by default. Scope on
// the canvas (brief-impedance-4): a region selects the traces it touches, whole; a pick selects one
// trace, or every trace joined to it through a via; a net selects its traces and the netless ones are
// counted; a pick on bare board is kept and said; all three round-trip; and the verb refuses a bare
// --region coordinate. Accepted findings (brief-impedance-5): an accepted warning passes until the trace
// moves; a tolerance change keeps it; a worse finding shows again with both numbers; acceptances
// round-trip and are absent when none; and the verb's exit code counts un-accepted findings only.
// A large board (brief-impedance-8): a cut with ground copper standing on the image plane is solved;
// and a long solve says how far it has got and how long is left, on stderr only.

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
                                                IReadOnlyList<LayerKey>? layers = null, RunControl? control = null,
                                                double warn = 20, double? maxFrequencyHz = null, Technology? tech = null) =>
        TraceImpedanceAnalysis.Analyze(shapes, tech ?? Tech(), LayoutUnits.DefaultDbuPerMicron,
            new TraceImpedanceOptions
            {
                TargetOhms = target, TolerancePercent = tol, WarningPercent = warn, MaxFrequencyHz = maxFrequencyHz,
                Layers = layers ?? [Top],
            }, control);

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

    /// <summary>Two microstrips of different widths on one layer each read the probe's own Z0. The cut's
    /// key once held the width only in quanta of 1.5 % of itself, so every trace wider than ~67 µm
    /// keyed alike and a 300 µm line took a 1000 µm line's 47.7 Ω (the probe's is 85.0 Ω).</summary>
    [Fact]
    public void TwoWidthsOnOneLayer_EachReadTheirOwnZ0()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, 1500, 6000, 2500), Rect(Top, -6000, -2150, 6000, -1850),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var traces = Assert.Single(Analyze(shapes).Layers).Traces;

        Assert.Equal(2, traces.Count);
        foreach (var t in traces)
        {
            var probe = TraceImpedanceProbe.Probe(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron,
                                                  0, (t.StartY + t.EndY) / 2, Top);
            Assert.Equal(probe.Z0Ohms, t.Z0Mean!.Value, probe.Z0Ohms * 0.005);
        }
    }

    /// <summary>A layout that draws only its top copper and lets the technology's ground-flagged layer
    /// stand for the plane reads as if the plane were drawn — not against the stackup's bottom
    /// boundary a copper thickness deeper, which read a 50 Ω line as 52.3 Ω (round 8).</summary>
    [Fact]
    public void AnUndrawnGroundFlaggedLayer_IsTakenAsThePlane()
    {
        LayoutShape[] drawn = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        LayoutShape[] implied = [Rect(Top, -6000, -500, 6000, 500)];
        var tech = Tech();
        tech.Stackup.Layers[2].IsGroundReference = true;
        tech.Stackup.Bottom = BoundaryCondition.Ground;
        var options = new TraceImpedanceOptions { Layers = [Top] };

        var withPlane = Assert.Single(TraceImpedanceAnalysis.Analyze(drawn, tech, LayoutUnits.DefaultDbuPerMicron, options).Layers[0].Traces);
        var withFlag  = Assert.Single(TraceImpedanceAnalysis.Analyze(implied, tech, LayoutUnits.DefaultDbuPerMicron, options).Layers[0].Traces);

        Assert.Equal(withPlane.Z0Mean!.Value, withFlag.Z0Mean!.Value, withPlane.Z0Mean.Value * 0.002);
        Assert.Contains(withFlag.Notes, n => n.StartsWith("Nothing is drawn on 'Plane'", StringComparison.Ordinal));
    }

    /// <summary>A trace that runs onto a narrower component pad does not take the pad in as its last
    /// few mils: an end piece shorter than it is wide is the land, not the line (round 8's 0201 pads
    /// read 57.6 Ω at the end of a 50 Ω trace).</summary>
    [Fact]
    public void APadAtATracesEnd_IsNotPartOfTheTrace()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, 6000, 500),
            Rect(Top, 5800, -420, 6600, 420),      // the pad: narrower than the trace, overlapping its end
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var trace = Assert.Single(Assert.Single(Analyze(shapes).Layers).Traces);

        Assert.Equal(1000, trace.WidthMin / LayoutUnits.DefaultDbuPerMicron, 1.0);
    }

    /// <summary>A trace that runs into a square pad as wide as three of it is found, with its own length
    /// (brief-impedance-7). The pad was joined as a "width step" that the end-trim, looking for a
    /// piece SHORTER than it is wide, let through — so the chain took the pad's 3 mm as its width, and
    /// 9 mm of it was under four widths long, and the whole trace was dropped as short.</summary>
    [Fact]
    public void ATraceIntoASquarePad_IsFound_WithItsOwnLength()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -475, 0, 475),
            Rect(Top, 0, -1500, 3000, 1500),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];
        var probe = TraceImpedanceProbe.Probe(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, Um(-3000), 0, Top);

        var trace = Assert.Single(Assert.Single(Analyze(shapes).Layers).Traces);

        Assert.Equal(6000, trace.Length / LayoutUnits.DefaultDbuPerMicron, 950.0);   // ± one station
        Assert.Equal(950, trace.WidthMax / LayoutUnits.DefaultDbuPerMicron, 1.0);
        Assert.Equal(probe.Z0Ohms, trace.Z0Mean!.Value, probe.Z0Ohms * 0.01);
        Assert.Equal(["open end", "pad"], new[] { trace.StartsAt, trace.EndsAt }.Order());
    }

    /// <summary>
    /// A straight line whose two sides carry collinear vertices at DIFFERENT points — what a Gerber layer
    /// composited from strokes and flashes arrives as — is one trace of its full length. Its sides used to
    /// pair edge by edge, every pair overlapping by less than the half width a piece needs, so the line
    /// was not found at all (designer feedback round 11: the last 0.7 mm before each series part and a
    /// 1.6 mm section between two of them, drawn as plain copper on the map).
    /// </summary>
    [Fact]
    public void ALineWhoseSidesAreSplitAtDifferentPoints_IsOneTraceOfItsFullLength()
    {
        const double half = 381, step = 343, length = 6000;
        var xy = new List<long> { Um(0), Um(-half) };
        for (double x = 0.5 * step; x < length; x += step) xy.AddRange([Um(x), Um(-half)]);   // bottom, offset
        xy.AddRange([Um(length), Um(-half), Um(length), Um(half)]);
        for (double x = length - step; x > 0; x -= step) xy.AddRange([Um(x), Um(half)]);     // top
        xy.AddRange([Um(0), Um(half)]);
        LayoutShape[] shapes = [new PolygonShape { Layer = Top, Xy = [.. xy] }, Rect(Gnd, -2000, -8000, 8000, 8000)];

        var trace = Assert.Single(Assert.Single(Analyze(shapes).Layers).Traces);

        Assert.Equal(length, trace.Length / LayoutUnits.DefaultDbuPerMicron, 1.0);
        Assert.Equal(2 * half, trace.WidthMax / LayoutUnits.DefaultDbuPerMicron, 1.0);
    }

    /// <summary>A trace into a pad only a little wider than itself — 1.37× here, longer than it is wide —
    /// leaves the pad off the trace, as a wider pad always did (round-10 report: "through the pad, but
    /// not always"). With the step at 1.5 the pad stayed as the trace's last 2 mm and its width.</summary>
    [Fact]
    public void ATraceIntoAPadALittleWiderThanIt_LeavesThePadOff()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -475, 0, 475),
            Rect(Top, 0, -650, 2000, 650),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var trace = Assert.Single(Assert.Single(Analyze(shapes).Layers).Traces);

        Assert.Equal(950, trace.WidthMax / LayoutUnits.DefaultDbuPerMicron, 1.0);
        Assert.Contains("pad", new[] { trace.StartsAt, trace.EndsAt });
    }

    /// <summary>The probe reads the same whichever way a pad polygon was wound (brief-impedance-7 §2).
    /// Wound clockwise, the pad used to cancel the trace's last 200 µm where the two overlap.</summary>
    [Fact]
    public void APadPolygonsWinding_ChangesNoProbeAnswer()
    {
        long[] ccw = [Um(-200), Um(-1500), Um(2800), Um(-1500), Um(2800), Um(1500), Um(-200), Um(1500)];
        long[] cw = [Um(-200), Um(-1500), Um(-200), Um(1500), Um(2800), Um(1500), Um(2800), Um(-1500)];
        LayoutShape[] Board(long[] pad) =>
            [Rect(Top, -6000, -475, 0, 475), new PolygonShape { Layer = Top, Xy = pad }, Rect(Gnd, -8000, -8000, 8000, 8000)];

        foreach (double x in (double[])[-5000, -3000, -1200, -600, -100, 1000])
            foreach (double y in (double[])[0, 300])
            {
                var a = TraceImpedanceProbe.Probe(Board(ccw), Tech(), LayoutUnits.DefaultDbuPerMicron, Um(x), Um(y), Top);
                var b = TraceImpedanceProbe.Probe(Board(cw), Tech(), LayoutUnits.DefaultDbuPerMicron, Um(x), Um(y), Top);
                Assert.Equal(a.Refusal, b.Refusal);
                Assert.Equal(a.Z0Ohms, b.Z0Ohms);
            }
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

        // A broken return is never softened: not by a wide warning band, not by being electrically short.
        var soft = Assert.Single(Assert.Single(Analyze(shapes, warn: 90, maxFrequencyHz: 1e6).Layers).Traces);
        Assert.Equal(IssueSeverity.Fail, Assert.Single(soft.Issues, i => i.Kind == TraceIssueKind.ReturnBroken).Severity);
        Assert.Equal(TraceVerdict.Fail, soft.Verdict);
    }

    /// <summary>Z0 ~1 % above the pass band warns while the warning band holds it (board A's 55.4 Ω
    /// against a 55.0 Ω edge), and fails once the warning band is set below it.</summary>
    [Theory]
    [InlineData(20.0, IssueSeverity.Warning, TraceVerdict.Warning)]
    [InlineData(10.5, IssueSeverity.Fail, TraceVerdict.Fail)]
    public void JustOutsideThePassBand_WarnsInsideTheWarningBand(double warn, IssueSeverity severity, TraceVerdict verdict)
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        double z0 = Assert.Single(Analyze(shapes).Layers[0].Traces).Z0Min!.Value;
        double target = z0 / (1.01 * 1.10);     // the pass band's top edge 1 % below the trace

        var trace = Assert.Single(Analyze(shapes, target: target, tol: 10, warn: warn).Layers[0].Traces);

        var issue = Assert.Single(trace.Issues);
        Assert.Equal(TraceIssueKind.OutOfTolerance, issue.Kind);
        Assert.Equal(severity, issue.Severity);
        Assert.Equal(verdict, trace.Verdict);
    }

    /// <summary>A 2 mm neck in an in-band trace, far outside the warning band: a fail with no frequency,
    /// a warning at a frequency where it is under λ/20, and a fail again where it is not.</summary>
    [Theory]
    [InlineData(null, IssueSeverity.Fail)]
    [InlineData(1e9, IssueSeverity.Warning)]
    [InlineData(20e9, IssueSeverity.Fail)]
    public void AShortNeck_IsAWarningOnlyWhenElectricallyShort(double? maxFrequencyHz, IssueSeverity severity)
    {
        LayoutShape[] wide = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];
        double z0 = Assert.Single(Analyze(wide).Layers[0].Traces).Z0Min!.Value;
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, -1000, 500),
            Rect(Top, -1000, -150, 1000, 150),     // the neck: 300 µm wide, 2 mm long
            Rect(Top, 1000, -500, 6000, 500),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var trace = Assert.Single(Analyze(shapes, target: z0, maxFrequencyHz: maxFrequencyHz).Layers[0].Traces);

        var issue = Assert.Single(trace.Issues);
        Assert.Equal(TraceIssueKind.OutOfTolerance, issue.Kind);
        Assert.Equal(severity, issue.Severity);
        Assert.Equal(maxFrequencyHz is not null && severity == IssueSeverity.Warning,
                     issue.Text.Contains("electrically short", StringComparison.Ordinal));
    }

    /// <summary>The reference stepping from one layer to another along a trace, with nothing else
    /// wrong, is a warning: often a designed transition, and the reviewer decides.</summary>
    [Fact]
    public void AReferenceStepAlone_IsAWarning()
    {
        var l2 = new LayerKey(3, 0); var l3 = new LayerKey(4, 0); var l4 = new LayerKey(5, 0);
        StackupLayer Cu(string name, LayerKey key) => new()
            { Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [key] };
        StackupLayer Core() => new() { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(200), Epsr = 4.4 };
        var tech = new Technology
        {
            Name = "four",
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
                new LayerDef { Key = l2, Name = "L2", Purpose = "conductor" },
                new LayerDef { Key = l3, Name = "L3", Purpose = "conductor" },
                new LayerDef { Key = l4, Name = "L4", Purpose = "conductor" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Open,
                Layers = [Cu("Top", Top), Core(), Cu("L2", l2), Core(), Cu("L3", l3), Core(), Cu("L4", l4)],
            },
        };
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -500, 6000, 500),
            Rect(l2, -8000, 6000, 8000, 8000),      // L2 is cleared under the whole trace
            Rect(l3, -8000, -8000, 0, 8000),        // L3 under the first half only…
            Rect(l4, -8000, -8000, 8000, 8000),     // …and L4 under all of it
        ];

        var trace = Assert.Single(Analyze(shapes, target: 55, tol: 45, warn: 90, tech: tech).Layers[0].Traces);

        var step = Assert.Single(trace.Issues);
        Assert.Equal(TraceIssueKind.ReferenceStep, step.Kind);
        Assert.Equal(IssueSeverity.Warning, step.Severity);
        Assert.Equal(TraceVerdict.Warning, trace.Verdict);
    }

    /// <summary>A warning band no wider than the tolerance is refused, naming both numbers.</summary>
    [Fact]
    public void AWarningBandNoWiderThanTheTolerance_IsRefused()
    {
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];

        var report = Analyze(shapes, tol: 15, warn: 15);

        Assert.False(report.Ok);
        Assert.Contains("± 15 %", report.Refusal, StringComparison.Ordinal);
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

            // Only a warning: reported, exit 0 — and 1 when --severity warning says warnings count.
            string warnTarget = (z0 / 1.15).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0, InProcess("impedance", clay, "--layers", "Top", "--target", warnTarget));
            Assert.Contains(" WARN ", _last, StringComparison.Ordinal);
            Assert.Contains("0 pass, 1 warning, 0 fail", _last, StringComparison.Ordinal);
            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Top", "--target", warnTarget, "--severity", "warning"));

            // A frequency without its unit is refused.
            Assert.Equal(1, InProcess("impedance", clay, "--max-freq", "6", "--json"));
            Assert.Contains("impedance.args.bad-number", _last, StringComparison.Ordinal);

            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Top", "--target", "10", "--json"));
            var trace = JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedance")
                                    .GetProperty("layers")[0].GetProperty("traces")[0];
            Assert.Equal("fail", trace.GetProperty("verdict").GetString());

            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Silk", "--json"));
            Assert.Contains("impedance.layers.unknown", _last, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── the scope (brief-impedance-2) ───────────────────────────────────────────────────────────

    /// <summary>One 450 µm trace and five 100 µm traces on Top — the first 100 µm one 200 µm beside the
    /// 450 µm trace, so it is a coplanar neighbour of it — over a plane.</summary>
    private static List<LayoutShape> WidthBoard(bool withNeighbour = true)
    {
        var shapes = new List<LayoutShape> { Rect(Top, -6000, -225, 6000, 225), Rect(Gnd, -8000, -8000, 8000, 8000) };
        if (withNeighbour) shapes.Add(Rect(Top, -6000, 425, 6000, 525));
        foreach (double y in (double[])[2500, 3500, 4500, 5500])
            shapes.Add(Rect(Top, -6000, y, 6000, y + 100));
        return shapes;
    }

    private static TraceImpedanceScope Wide() => new() { Widths = [TraceWidthSelector.Around("Top", 450)] };

    private static TraceImpedanceReport AnalyzeScoped(IReadOnlyList<LayoutShape> shapes, TraceImpedanceScope? scope) =>
        TraceImpedanceAnalysis.Analyze(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron,
            new TraceImpedanceOptions { Layers = [Top], Scope = scope });

    /// <summary>The scope is applied before cutting, so it saves the SOLVES, not just the rows — counted,
    /// never timed: the scoped run solves no more cuts than the one trace it reports has stations, and
    /// fewer than the run over all six; the trace under review is T1 and the other five are counted.</summary>
    [Fact]
    public void AScopedRun_SolvesOnlyTheTracesInScope()
    {
        var all = AnalyzeScoped(WidthBoard(), null);
        var scoped = AnalyzeScoped(WidthBoard(), Wide());

        Assert.Equal(6, all.TraceCount);
        var trace = Assert.Single(Assert.Single(scoped.Layers).Traces);
        Assert.Equal("T1", trace.Id);
        Assert.Equal(5, scoped.Layers[0].OutOfScope);
        Assert.True(scoped.SolveCount <= trace.Stations.Count, $"{scoped.SolveCount} solves for {trace.Stations.Count} stations");
        Assert.True(scoped.SolveCount < all.SolveCount, $"scoped {scoped.SolveCount}, all {all.SolveCount}");
        Assert.Equal("Top at 450 µm (1 trace). 5 traces on Top are outside the scope and were not analysed.", scoped.ScopeText);
    }

    /// <summary>Scope selects TRACES, never COPPER: the 450 µm trace reads the same Z0 to the last digit
    /// with its 100 µm neighbour out of scope as in scope — and the neighbour does move it (the trace
    /// alone reads differently), so the equality is not vacuous.</summary>
    [Fact]
    public void AnOutOfScopeNeighbour_IsStillGroundedCopper()
    {
        double? Wide450(TraceImpedanceReport r) =>
            r.AllTraces.Single(t => Math.Abs(t.WidthMax / LayoutUnits.DefaultDbuPerMicron - 450) < 5).Z0Mean;

        double? unscoped = Wide450(AnalyzeScoped(WidthBoard(), null));
        double? scoped = Wide450(AnalyzeScoped(WidthBoard(), Wide()));
        double? alone = Wide450(AnalyzeScoped(WidthBoard(withNeighbour: false), Wide()));

        Assert.Equal(unscoped, scoped);
        Assert.NotEqual(alone!.Value, scoped!.Value, 3);
    }

    /// <summary>The survey groups the traces by width — 450 µm × 1 and 100 µm × 5, with their lengths —
    /// and solves exactly one cut per class.</summary>
    [Fact]
    public void TheSurvey_GroupsByWidth_WithOneSolvePerClass()
    {
        var survey = TraceImpedanceAnalysis.Survey(WidthBoard(), Tech(), LayoutUnits.DefaultDbuPerMicron,
                                                   new TraceImpedanceOptions { Layers = [Top] });

        var classes = Assert.Single(survey.Layers).Classes;
        Assert.Equal(2, classes.Count);
        Assert.Equal(100, classes[0].NominalMicrons, 1);
        Assert.Equal(5, classes[0].TraceCount);
        Assert.Equal(5 * 12000, classes[0].TotalLengthMicrons, 5 * 12000 * 0.01);
        Assert.Equal(450, classes[1].NominalMicrons, 1);
        Assert.Equal(1, classes[1].TraceCount);
        Assert.All(classes, c => Assert.NotNull(c.TypicalZ0));
        Assert.Equal(2, survey.SolveCount);
    }

    /// <summary>A <c>.clay</c> with no review writes no key for it, so every existing file round-trips
    /// byte-identical; one with a review round-trips its settings and scope.</summary>
    [Fact]
    public void TheReview_RoundTripsInTheClay_AndIsAbsentWhenNull()
    {
        var view = new LayoutView { TechRef = "board.ctech" };
        view.Shapes.Add(Rect(Top, -6000, -225, 6000, 225));
        string plain = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("ImpedanceReview", plain, StringComparison.Ordinal);
        Assert.Equal(plain, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(plain)));

        view.ImpedanceReview = new TraceImpedanceReview
        {
            TargetOhms = 55, TolerancePercent = 5, WarningPercent = 12, MaxFrequencyHz = 6e9, Layers = ["Top"], Scope = Wide(),
        };
        var back = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view)).ImpedanceReview;
        Assert.NotNull(back);
        Assert.True(back.SameAs(view.ImpedanceReview));
        Assert.Equal(Wide().Widths, back.Scope!.Widths);
    }

    /// <summary>The verb applies the scope saved on the layout by default, so a headless run reports what
    /// the editor reports; <c>--no-scope</c> reviews all six traces, <c>--width</c> replaces the saved
    /// classes for its layer, and <c>--survey</c> lists the widths.</summary>
    [Fact]
    public void TheVerb_AppliesTheSavedScope_UnlessToldNot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-impedance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            var view = new LayoutView { TechRef = "board.ctech", ImpedanceReview = new TraceImpedanceReview { Scope = Wide() } };
            view.Shapes.AddRange(WidthBoard());
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);

            int Traces(params string[] extra)
            {
                InProcess(["impedance", clay, "--layers", "Top", "--json", .. extra]);
                return JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedance")
                                   .GetProperty("traces").GetInt32();
            }
            Assert.Equal(1, Traces());
            Assert.Equal(6, Traces("--no-scope"));
            Assert.Equal(5, Traces("--width", "Top=100um"));

            Assert.Equal(0, InProcess("impedance", clay, "--survey", "--json"));
            var classes = JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedanceSurvey")
                                      .GetProperty("layers")[0].GetProperty("classes");
            Assert.Equal(2, classes.GetArrayLength());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── scope on the canvas (brief-impedance-4) ─────────────────────────────────────────────────

    /// <summary>Two 100 µm traces, 1 mm apart, over the plane.</summary>
    private static List<LayoutShape> TwinBoard(string? netOfFirst = null)
    {
        var first = Rect(Top, -6000, 2500, 6000, 2600);
        first.Net = netOfFirst;
        return [first, Rect(Top, -6000, 3500, 6000, 3600), Rect(Gnd, -8000, -8000, 8000, 8000)];
    }

    private static TraceScopeRegion Box(double x0, double y0, double x1, double y1) =>
        TraceScopeRegion.Rectangle(null, Um(x0), Um(y0), Um(x1), Um(y1));

    /// <summary>A region round one of two same-width traces reviews that one; a region clipping only its
    /// end still reviews it WHOLE — the same length as with no scope.</summary>
    [Fact]
    public void ARegion_SelectsTheTracesItTouches_Whole()
    {
        double fullLength = AnalyzeScoped(TwinBoard(), null).AllTraces.Single(t => t.StartY < Um(3000)).Length;

        var around = Assert.Single(AnalyzeScoped(TwinBoard(), new() { Regions = [Box(-7000, 2400, 7000, 2700)] }).AllTraces);
        Assert.True(around.StartY < Um(3000));

        var clipped = AnalyzeScoped(TwinBoard(), new() { Regions = [Box(5000, 2300, 7000, 2800)] });
        Assert.Equal(fullLength, Assert.Single(clipped.AllTraces).Length);
        Assert.Equal(1, clipped.Layers[0].OutOfScope);
    }

    /// <summary>A section three widths long — a wide line between two series parts — is a pad to a run
    /// nothing points with, and a trace to a region round it (field report: every section of the RF line
    /// but one read as a pad, so no selector could choose it).</summary>
    [Fact]
    public void AShortSection_IsATrace_OnlyWhereASelectorChoosesIt()
    {
        LayoutShape[] shapes = [Rect(Top, -1500, -500, 1500, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];

        Assert.Empty(AnalyzeScoped(shapes, null).AllTraces);
        var chosen = AnalyzeScoped(shapes, new() { Regions = [Box(-2000, -1000, 2000, 1000)] });
        Assert.Equal(3000, Assert.Single(chosen.AllTraces).Length / LayoutUnits.DefaultDbuPerMicron, 1.0);
        Assert.Equal(0, chosen.Layers[0].OutOfScope);
    }

    private static readonly LayerKey Mid = new(3, 0);
    private static readonly LayerKey Drill = new(4, 0);

    /// <summary>Top and Mid over the plane, a via joining them.</summary>
    private static Technology ViaTech() => new()
    {
        Name = "via",
        Layers =
        [
            new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
            new LayerDef { Key = Mid, Name = "Mid", Purpose = "conductor" },
            new LayerDef { Key = Gnd, Name = "Plane", Purpose = "conductor" },
            new LayerDef { Key = Drill, Name = "Via" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Via, Name = "V1", DrawingLayers = [Drill], SpanFromLayer = "Top", SpanToLayer = "Mid" },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core 1", ThicknessDbu = Um(300), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Mid", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Mid] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core 2", ThicknessDbu = Um(300), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            ],
        },
    };

    /// <summary>A trace on Top down a via to a trace on Mid, and a second, unconnected trace on Top.</summary>
    private static List<LayoutShape> ViaBoard() =>
    [
        Rect(Top, -6000, -150, 0, 150),
        new ViaShape { Layer = Drill, X = 0, Y = 0, DrillSize = Um(250) },
        Rect(Mid, 0, -150, 6000, 150),
        Rect(Top, -6000, 2850, 6000, 3150),
        Rect(Gnd, -8000, -8000, 8000, 8000),
    ];

    /// <summary>The plane cleared 450 µm round the via (its antipad) is not a broken return on the Mid
    /// trace into it while the via-transition distance covers it — the stretch is named in a note
    /// instead — and is flagged again at 0 (round-10 report: every layer change was flagged). The via
    /// has no pad on Mid, as every via of an imported Gerber board lands on the top layer only: its
    /// barrel is what the trace runs onto.</summary>
    [Fact]
    public void TheAntipadRoundAVia_IsNotFlagged_WithinTheViaTransition_AndIsNoted()
    {
        List<LayoutShape> board =
        [
            Rect(Top, -6000, -150, 0, 150),
            new ViaShape { Layer = Drill, X = 0, Y = 0, DrillSize = Um(250) },
            Rect(Mid, -150, -150, 6000, 150),
            Rect(Gnd, -8000, -8000, 8000, -450), Rect(Gnd, -8000, 450, 8000, 8000),
            Rect(Gnd, -8000, -450, -450, 450), Rect(Gnd, 450, -450, 8000, 450),
        ];
        TraceRun Run(double viaTransitionUm) => Assert.Single(TraceImpedanceAnalysis.Analyze(board, ViaTech(),
            LayoutUnits.DefaultDbuPerMicron, new TraceImpedanceOptions { Layers = [Mid], ViaTransitionMicrons = viaTransitionUm }).AllTraces);

        var skipped = Run(TraceImpedanceOptions.DefaultViaTransitionMicrons);
        var checkedAll = Run(0);

        Assert.DoesNotContain(skipped.Issues, i => i.Kind == TraceIssueKind.ReturnBroken);
        Assert.Contains(skipped.Notes, n => n.StartsWith("Not checked for", StringComparison.Ordinal));
        Assert.Contains(checkedAll.Issues, i => i.Kind == TraceIssueKind.ReturnBroken);
    }

    /// <summary>A Trace pick selects the one trace under it; a Connected pick at the same point also
    /// selects the trace on Mid the via joins it to — and never the unconnected trace beside it.</summary>
    [Fact]
    public void APick_SelectsItsTrace_OrEverythingJoinedThroughAVia()
    {
        TraceImpedanceReport Run(TracePickExtent extent) => TraceImpedanceAnalysis.Analyze(ViaBoard(), ViaTech(),
            LayoutUnits.DefaultDbuPerMicron, new TraceImpedanceOptions
            {
                Layers = [Top, Mid], Scope = new() { Picks = [new TracePick("Top", Um(-3000), 0, extent)] },
            });

        var one = Run(TracePickExtent.Trace);
        var trace = Assert.Single(one.AllTraces);
        Assert.True(Math.Abs(trace.StartY) < Um(200), $"picked the trace at y = {trace.StartY}");
        Assert.Empty(one.Layers.Single(l => l.Name == "Mid").Traces);

        var joined = Run(TracePickExtent.Connected);
        Assert.Equal(2, joined.TraceCount);
        Assert.Single(joined.Layers.Single(l => l.Name == "Top").Traces);
        Assert.Single(joined.Layers.Single(l => l.Name == "Mid").Traces);
        Assert.Empty(joined.PicksWithoutCopper);
    }

    /// <summary>A net selects the traces on copper carrying it; the trace on unnetted copper is counted
    /// as having no net.</summary>
    [Fact]
    public void ANet_SelectsItsTraces_AndTheNetlessAreCounted()
    {
        var report = AnalyzeScoped(TwinBoard(netOfFirst: "RF_OUT"), new() { Nets = ["RF_OUT"] });

        var trace = Assert.Single(report.AllTraces);
        Assert.True(trace.StartY < Um(3000));
        Assert.True(report.HasNets);
        Assert.Equal(1, report.NetlessCount);
        Assert.StartsWith("Selected by net RF_OUT. ", report.ScopeText, StringComparison.Ordinal);
    }

    /// <summary>A pick on bare board is kept, reported as finding no copper, and the run still succeeds.</summary>
    [Fact]
    public void APickOnBareBoard_IsKeptAndSaid_AndTheRunSucceeds()
    {
        var pick = new TracePick("Top", 0, Um(7500), TracePickExtent.Trace);
        var report = AnalyzeScoped(TwinBoard(), new() { Picks = [pick] });

        Assert.Null(report.Refusal);
        Assert.Equal(pick, Assert.Single(report.PicksWithoutCopper));
        Assert.Contains(report.Notes, n => n.Contains("no copper under it now", StringComparison.Ordinal));
        Assert.Equal(0, report.TraceCount);
    }

    /// <summary>A 1 mm line ending in a 1.6 × 5.6 mm section — 3.5 of its widths, wider than the line — is reviewed as the line
    /// alone, the section its land, with or without a scope: the review's end trim. Only artwork recognition's own option
    /// keeps the section on the trace.</summary>
    [Fact]
    public void AWideEndSection_IsTheLinesLand_ScopedOrNot_AndOnlyRecognitionKeepsIt()
    {
        LayoutShape[] shapes = [Rect(Top, 0, -500, 20_000, 500), Rect(Top, 20_000, -800, 25_600, 800), Rect(Gnd, -2000, -8000, 30_000, 8000)];
        var region = new TraceImpedanceScope { Regions = [TraceScopeRegion.Rectangle(null, Um(-1000), Um(-2000), Um(27_000), Um(2000))] };
        TraceRun Run(TraceImpedanceOptions o) =>
            Assert.Single(Assert.Single(TraceImpedanceAnalysis.Analyze(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron, o with { Layers = [Top] }).Layers).Traces);

        var plain = Run(new TraceImpedanceOptions());
        var scoped = Run(new TraceImpedanceOptions { Scope = region });
        Assert.Equal(Um(20_000), plain.Length, (double)Um(10));
        Assert.Equal((plain.Length, plain.WidthMax, plain.Z0Min, plain.Z0Max, plain.Stations.Count),
                     (scoped.Length, scoped.WidthMax, scoped.Z0Min, scoped.Z0Max, scoped.Stations.Count));

        var recognised = Run(new TraceImpedanceOptions { Scope = region, KeepLongEndSections = true });
        Assert.Equal(Um(25_600), recognised.Length, (double)Um(10));
    }

    /// <summary>A scope with every selector kind — a region on a layer, whole layers — round-trips
    /// through the .clay; one saved before regions had layers writes neither key, so it is read back
    /// as every-layer regions and written back unchanged (brief-impedance-6 gate 1).</summary>
    [Fact]
    public void EverySelectorKind_RoundTripsInTheClay()
    {
        var scope = new TraceImpedanceScope
        {
            Regions = [TraceScopeRegion.Rectangle("RF front end", 0, 0, 1000, 2000), new TraceScopeRegion(null, [0, 0, 500, 900, -300, 400])],
            Picks = [new TracePick("Top", 10, 20, TracePickExtent.Connected)],
            Nets = ["RF_OUT"],
        };
        var view = new LayoutView { TechRef = "board.ctech", ImpedanceReview = new TraceImpedanceReview { Scope = scope } };
        string before = LayoutPersistence.Serialize(view);
        var savedScope = JsonDocument.Parse(before).RootElement.GetProperty("ImpedanceReview").GetProperty("Scope");
        Assert.All(savedScope.GetProperty("Regions").EnumerateArray(), r => Assert.False(r.TryGetProperty("LayerName", out _)));
        Assert.False(savedScope.TryGetProperty("WholeLayers", out _));
        Assert.All(LayoutPersistence.Deserialize(before).ImpedanceReview!.Scope!.Regions, r => Assert.Null(r.LayerName));

        scope.Regions[1] = scope.Regions[1] with { LayerName = "Mid" };
        scope.WholeLayers = ["Top"];
        var back = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view)).ImpedanceReview!.Scope;
        Assert.True(TraceImpedanceScope.Same(scope, back));
        Assert.True(back!.HasSelectors);
    }

    /// <summary>brief-impedance-6: a region selects on its own layer only, and a layer nothing points at
    /// is not reviewed — the scope line says so; a region naming no layer (every region saved before)
    /// selects on every layer, as it did; and a whole layer keeps all of that layer beside a region.</summary>
    [Fact]
    public void ARegion_SelectsOnItsOwnLayer_AndAWholeLayerKeepsTheRest()
    {
        TraceImpedanceReport Run(TraceImpedanceScope scope) => TraceImpedanceAnalysis.Analyze(ViaBoard(), ViaTech(),
            LayoutUnits.DefaultDbuPerMicron, new TraceImpedanceOptions { Layers = [Top, Mid], Scope = scope });
        int On(TraceImpedanceReport r, string layer) => r.Layers.Single(l => l.Name == layer).Traces.Count;
        var box = Box(-7000, -500, 7000, 500);

        var mid = Run(new() { Regions = [box with { LayerName = "Mid" }] });
        Assert.Equal((0, 1), (On(mid, "Top"), On(mid, "Mid")));
        Assert.Contains("nothing selected on Top", mid.ScopeText, StringComparison.Ordinal);

        var every = Run(new() { Regions = [box] });
        Assert.Equal((1, 1), (On(every, "Top"), On(every, "Mid")));

        var whole = Run(new() { Regions = [box with { LayerName = "Mid" }], WholeLayers = ["Top"] });
        Assert.Equal((2, 1), (On(whole, "Top"), On(whole, "Mid")));
    }

    /// <summary>Field report: a region round a via-to-test-pad link reviewed nothing and gave no reason. Copper
    /// a region covers that is too short to be a trace — here 1.5 of its widths — is named in a run note with its
    /// size.</summary>
    [Fact]
    public void ARegionOverPadLengthCopper_SaysWhyNothingWasReviewed()
    {
        LayoutShape[] shapes = [Rect(Top, -750, -500, 750, 500), Rect(Gnd, -5000, -5000, 5000, 5000)];
        var report = TraceImpedanceAnalysis.Analyze(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron,
            new TraceImpedanceOptions { Layers = [Top], Scope = new() { Regions = [Box(-2000, -2000, 2000, 2000)] } });
        Assert.Empty(report.Layers.Single().Traces);
        var note = Assert.Single(report.Notes, n => n.Contains("a region covers copper", StringComparison.Ordinal));
        Assert.Contains("1500 µm long and 1000 µm wide", note, StringComparison.Ordinal);
    }

    /// <summary>The verb's --region takes a unit on every coordinate and refuses a bare number; with
    /// units it selects what the region in the first test selects.</summary>
    [Fact]
    public void TheVerb_Region_RequiresUnits()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-impedance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            var view = new LayoutView { TechRef = "board.ctech" };
            view.Shapes.AddRange(TwinBoard());
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);

            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Top", "--region", "-7000,2400,7000,2700", "--json"));
            Assert.Contains("impedance.args.coordinate-needs-unit", _last, StringComparison.Ordinal);

            InProcess("impedance", clay, "--layers", "Top", "--region", "-7mm,2.4mm,7mm,2.7mm", "--json");
            Assert.Equal(1, JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedance")
                                        .GetProperty("traces").GetInt32());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── accepted findings (brief-impedance-5) ───────────────────────────────────────────────────

    /// <summary>A 1 mm trace over the plane — <paramref name="halfWidth"/> and <paramref name="dx"/> in µm
    /// narrow it and move it — and a target that puts the 1 mm trace's Z0 1 % above the pass band's top
    /// edge: board A's 55.4 Ω against 55.0 Ω.</summary>
    private static LayoutShape[] AcceptBoard(double halfWidth = 500, double dx = 0) =>
        [Rect(Top, -6000 + dx, -halfWidth, 6000 + dx, halfWidth), Rect(Gnd, -8000, -8000, 8000, 8000)];

    private static readonly Lazy<double> AcceptTarget =
        new(() => Assert.Single(Analyze(AcceptBoard()).Layers[0].Traces).Z0Min!.Value / (1.01 * 1.10));

    private static TraceImpedanceAcceptance AcceptOnly(TraceImpedanceReport report) =>
        TraceImpedanceAcceptance.For(report, report.AllTraces.Single(), report.AllTraces.Single().Issues.Single(),
                                     "connector launch", new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>Accepting the warning makes the trace PASS with the finding still there, marked; the same
    /// trace moved 10 µm is not the trace that was reviewed, so it warns again and the acceptance is
    /// listed stale.</summary>
    [Fact]
    public void AnAcceptedWarning_Passes_UntilTheTraceMoves()
    {
        var report = Analyze(AcceptBoard(), target: AcceptTarget.Value);
        var accepted = AcceptOnly(report);

        var applied = TraceImpedanceAcceptance.Apply(report, [accepted]);
        var trace = applied.AllTraces.Single();
        Assert.Equal(TraceVerdict.Pass, trace.Verdict);
        Assert.Equal(1, trace.AcceptedCount);
        Assert.Same(accepted, trace.Issues.Single().Accepted);
        Assert.Empty(applied.StaleAcceptances);
        Assert.StartsWith("PASS — 1 trace reviewed, 1 pass (1 with an accepted finding), 0 warnings, 0 failures",
                          applied.VerdictSentence, StringComparison.Ordinal);

        var moved = TraceImpedanceAcceptance.Apply(Analyze(AcceptBoard(dx: 10), target: AcceptTarget.Value), [accepted]);
        Assert.Equal(TraceVerdict.Warning, moved.AllTraces.Single().Verdict);
        Assert.Same(accepted, Assert.Single(moved.StaleAcceptances));
    }

    /// <summary>The key is the trace, not the stretch: a trace that steps from 950 to 1000 µm, accepted at
    /// ± 10 % where only its narrow half is out, stays accepted at ± 5 %, where the finding's stretch has
    /// grown to cover the whole trace.</summary>
    [Fact]
    public void ChangingTheTolerance_KeepsTheAcceptance()
    {
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -475, 0, 475), Rect(Top, 0, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000),
        ];
        double target = Assert.Single(Analyze(shapes).Layers[0].Traces).Z0Max!.Value / (1.01 * 1.10);
        var wide = Analyze(shapes, target: target, tol: 10);
        var narrow = Analyze(shapes, target: target, tol: 5);
        var before = wide.AllTraces.Single().Issues.Single();
        var after = narrow.AllTraces.Single().Issues.Single();
        Assert.NotEqual((before.X0, before.X1), (after.X0, after.X1));    // the stretch did move

        var applied = TraceImpedanceAcceptance.Apply(narrow, [AcceptOnly(wide)]);

        Assert.Equal(TraceVerdict.Pass, applied.AllTraces.Single().Verdict);
        Assert.Empty(applied.StaleAcceptances);
    }

    /// <summary>An acceptance does not cover a WORSE finding: the same trace narrowed reads a higher Z0, and
    /// its finding is shown again, un-accepted, with the accepted and the new worst Z0 in its text.</summary>
    [Fact]
    public void AWorseFinding_IsShownAgain_WithBothNumbers()
    {
        var accepted = AcceptOnly(Analyze(AcceptBoard(), target: AcceptTarget.Value));

        var worse = TraceImpedanceAcceptance.Apply(Analyze(AcceptBoard(halfWidth: 470), target: AcceptTarget.Value), [accepted]);

        var trace = worse.AllTraces.Single();
        var issue = trace.Issues.Single();
        Assert.Null(issue.Accepted);
        Assert.NotEqual(TraceVerdict.Pass, trace.Verdict);
        Assert.EndsWith($"Accepted at {accepted.WorstOhms:0.0} Ω, now {trace.Z0Max:0.0} Ω.", issue.Text, StringComparison.Ordinal);
        Assert.True(trace.Z0Max > accepted.WorstOhms + 0.05, $"{trace.Z0Max} vs {accepted.WorstOhms}");
        Assert.Empty(worse.StaleAcceptances);
    }

    /// <summary>An acceptance round-trips through the <c>.clay</c>; a layout with none writes no key for
    /// them, so every existing file stays byte-identical.</summary>
    [Fact]
    public void Acceptances_RoundTripInTheClay_AndAreAbsentWhenNone()
    {
        var view = new LayoutView { TechRef = "board.ctech" };
        view.Shapes.AddRange(AcceptBoard());
        string plain = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("ImpedanceAcceptances", plain, StringComparison.Ordinal);
        Assert.Equal(plain, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(plain)));

        var accepted = AcceptOnly(Analyze(AcceptBoard(), target: AcceptTarget.Value));
        view.ImpedanceAcceptances.Add(accepted);
        var back = Assert.Single(LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view)).ImpedanceAcceptances);
        Assert.Equal((accepted.Key, accepted.Kind, accepted.Reason, accepted.AcceptedUtc, accepted.LayerName, accepted.Summary, accepted.WorstOhms),
                     (back.Key, back.Kind, back.Reason, back.AcceptedUtc, back.LayerName, back.Summary, back.WorstOhms));
    }

    /// <summary>The verb applies the acceptances saved on the layout: a layout whose only failure is
    /// accepted exits 0 and says why in <c>--json</c>; <c>--ignore-accepted</c> counts it, and exits 1.</summary>
    [Fact]
    public void TheVerb_ExitsOnUnacceptedFindingsOnly()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-impedance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            double target = AcceptTarget.Value / 1.2;     // far outside the warning band: a fail
            string targetText = target.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var report = Analyze(AcceptBoard(), target: double.Parse(targetText, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(TraceVerdict.Fail, report.AllTraces.Single().Verdict);

            var view = new LayoutView { TechRef = "board.ctech" };
            view.Shapes.AddRange(AcceptBoard());
            view.ImpedanceAcceptances.Add(AcceptOnly(report));
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);

            Assert.Equal(0, InProcess("impedance", clay, "--layers", "Top", "--target", targetText, "--json"));
            var issue = JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedance")
                                    .GetProperty("layers")[0].GetProperty("traces")[0].GetProperty("issues")[0];
            Assert.Equal("connector launch", issue.GetProperty("accepted").GetProperty("reason").GetString());
            Assert.Equal(1, InProcess("impedance", clay, "--layers", "Top", "--target", targetText, "--ignore-accepted"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── a large board (brief-impedance-8) ───────────────────────────────────────────────────────

    /// <summary>A stripline layer whose plane below is pulled back from under the trace, on a stackup
    /// whose bottom boundary is ground: the plane is that boundary, and the bottom copper beside the
    /// trace stands ON it. Meshed whole that copper's face on the plane was singular and the trace was
    /// unsolved — on a 10-layer board 762 stations, and ~90 % of a 29-minute run. It is a grounded bump
    /// on the plane, its face there is left out, and the trace has an answer.</summary>
    [Fact]
    public void GroundCopperOnTheImagePlane_IsSolved()
    {
        var mid = new LayerKey(3, 0);
        var tech = new Technology
        {
            Name = "stripline",
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
                new LayerDef { Key = mid, Name = "Mid", Purpose = "conductor" },
                new LayerDef { Key = Gnd, Name = "Bottom", Purpose = "conductor" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open,
                Bottom = BoundaryCondition.Ground,
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core 1", ThicknessDbu = Um(200), Epsr = 4.4 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Mid", ThicknessDbu = Um(18), SigmaSm = 5.8e7, DrawingLayers = [mid] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core 2", ThicknessDbu = Um(200), Epsr = 4.4 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
                ],
            },
        };
        LayoutShape[] shapes =
        [
            Rect(mid, -6000, -50, 6000, 50),
            Rect(Top, -8000, -8000, 8000, 8000),
            Rect(Gnd, -8000, 300, 8000, 8000), Rect(Gnd, -8000, -8000, 8000, -300),   // pulled back under the trace
        ];

        var report = Analyze(shapes, layers: [mid], tech: tech);

        var trace = Assert.Single(Assert.Single(report.Layers).Traces);
        Assert.NotEqual(TraceVerdict.Unsolved, trace.Verdict);
        Assert.InRange(trace.Z0Mean!.Value, 20, 80);
        Assert.DoesNotContain(trace.Issues, i => i.Text.Contains("solved to no capacitance", StringComparison.Ordinal));
    }

    /// <summary>A long solve repeats its line on stderr at most once per interval, with the count done,
    /// the time elapsed and — once the rate means something — the time left; and the verb's --json
    /// stdout is the same bytes however often that line is printed.</summary>
    [Fact]
    public void ALongSolve_SaysHowFarItHasGot_OnStderrOnly()
    {
        var err = new StringWriter();
        var now = TimeSpan.Zero;
        var lines = new CircuitRF.Cli.Impedance.ProgressLines(err, TimeSpan.FromSeconds(3), () => now);
        const string stage = "Inner 7 (8 of 10): solving";
        lines.Report(new RunProgress(stage, 7, 10, 0, 6368, "cross-sections"));
        now = TimeSpan.FromSeconds(1);
        lines.Report(new RunProgress(stage, 7, 10, 100, 6368, "cross-sections"));
        now = TimeSpan.FromSeconds(42);
        lines.Report(new RunProgress(stage, 7, 10, 3184, 6368, "cross-sections"));

        Assert.Equal(
            "[circuitRF] Inner 7 (8 of 10): solving (6368 cross-sections)" + Environment.NewLine +
            "[circuitRF] Inner 7 (8 of 10): solving: 3,184 of 6,368 cross-sections, 42 s elapsed, about 42 s left" + Environment.NewLine,
            err.ToString());

        string dir = Path.Combine(Path.GetTempPath(), "crf-impedance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var interval = CircuitRF.Cli.Impedance.ProgressInterval;
        try
        {
            TechPersistence.SaveToFile(Path.Combine(dir, "board.ctech"), Tech());
            var view = new LayoutView { TechRef = "board.ctech" };
            view.Shapes.Add(Rect(Top, -6000, -500, 6000, 500));
            view.Shapes.Add(Rect(Gnd, -8000, -8000, 8000, 8000));
            string clay = Path.Combine(dir, "board.clay");
            LayoutPersistence.SaveToFile(clay, view);

            CircuitRF.Cli.Impedance.ProgressInterval = TimeSpan.MaxValue;
            InProcess("impedance", clay, "--layers", "Top", "--json");
            string quiet = _last;
            CircuitRF.Cli.Impedance.ProgressInterval = TimeSpan.Zero;
            InProcess("impedance", clay, "--layers", "Top", "--json");
            Assert.Equal(quiet, _last);
        }
        finally
        {
            CircuitRF.Cli.Impedance.ProgressInterval = interval;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
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
