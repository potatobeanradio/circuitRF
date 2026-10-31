// Designer report, round 20 — a four-layer set whose pours drew as stripes, a three-layer set whose vias had to be
// added to the stackup by hand, and an E series on an optimizer row that the values kept did not respect. One test
// per claim:
//   * a pour painted with abutting scanlines at a pitch just under the pen is one region, and traces stay strokes;
//   * a set numbering its copper Signal_01…03 with a Via_1_2_3 file imports three conductors and a via layer
//     spanning them, and a via file's name states a blind span;
//   * a file the layer-mapping table answers "vias" is a plated via layer through the board;
//   * a derivative-free algorithm searches an E-series row on its series, a gradient one between rungs;
//   * a run over an E-series row that is STOPPED still ends on the series;
//   * while paused with an E-series row between rungs the best cannot be pushed, and Stop puts it on a rung.

using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.Tests.OptimizerPanel;

namespace CircuitRF.Ui.Tests;

public sealed class DesignerRound20Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-r20-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The field board's pour: a 10-mil pen at a 9.1-mil pitch, thirty lines and the outline stroked round
    /// them — 1.1× coverage and fewer than fifty strokes, so neither earlier test called it painted.</summary>
    [Fact]
    public void AScanlinePourAtAPitchJustUnderThePen_IsOneRegion_AndTracesStayStrokes()
    {
        const double pen = 254, pitch = 231.14, width = 8_000;
        var pour = Enumerable.Range(0, 30).Select(i => Stroke(0, i * pitch, width, i * pitch, pen)).ToList();
        double top = 29 * pitch;
        pour.Add(new PathShape
        {
            Layer = new LayerKey(2, 0), Width = Um(pen), End = PathEndStyle.Round,
            Xy = [0, 0, Um(width), 0, Um(width), Um(top), 0, Um(top), 0, 0],
        });
        // Ten traces meeting end to end in a comb beside it: a network, one pen wide.
        var traces = Enumerable.Range(0, 10).Select(i => Stroke(12_000 + i * 1_000, 0, 12_000 + i * 1_000, 3_000, pen))
            .Append(Stroke(12_000, 0, 21_000, 0, pen)).ToList();
        var candidates = pour.Concat(traces).Select(s => new LayoutRasterFillCoalesce.Candidate(s, "k", "Top")).ToList();

        var plan = LayoutRasterFillCoalesce.Build(candidates, 100, perRegion: true);

        var replacement = Assert.Single(plan.Replacements);
        var region = Assert.IsType<PolygonShape>(Assert.Single(replacement.Regions));
        Assert.Null(region.Holes);
        Assert.Equal(pour.Count, replacement.Replaced.Count);
        Assert.DoesNotContain(replacement.Replaced, traces.Contains);
    }

    [Fact]
    public void NumberedSignalLayersAndAViaFile_ImportThreeConductorsAndAViaSpanningThem()
    {
        string dir = Folder("signal");
        foreach (string name in (string[])["Signal_01.gbr", "Signal_02.gbr", "Signal_03.gbr", "Via_1_2_3.gbr"])
            File.WriteAllText(Path.Combine(dir, name), Flashes(0.6));
        File.WriteAllText(Path.Combine(dir, "SP_Top.gbr"), Flashes(0.3));

        var result = GerberImport.Import(FilesIn(dir), _root, "signal", null, 1000);

        string LayerOf(string file) => result.Layers.Single(l => l.FileName == file).LayerName;
        Assert.Equal(["Top Copper", "Inner 1", "Bottom Copper", "Drill", "Paste Top"],
                     ((string[])["Signal_01.gbr", "Signal_02.gbr", "Signal_03.gbr", "Via_1_2_3.gbr", "SP_Top.gbr"]).Select(LayerOf));
        var tech = Assert.IsType<Technology>(result.Technology);
        Assert.Equal(3, tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor));
        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Equal(("Top Copper", "Bottom Copper", true), (via.SpanFromLayer, via.SpanToLayer, via.Plated != false));
    }

    /// <summary>A via file named so nothing identifies it, answered "vias" in the layer-mapping table.</summary>
    [Fact]
    public void AFileAnsweredVias_IsAPlatedViaLayerThroughTheBoard()
    {
        string dir = Folder("answered");
        File.WriteAllText(Path.Combine(dir, "top copper.gbr"), Flashes(0.6));
        File.WriteAllText(Path.Combine(dir, "bottom copper.gbr"), Flashes(0.6));
        File.WriteAllText(Path.Combine(dir, "plated.art"), Flashes(0.3));

        var result = GerberImport.Import(FilesIn(dir), _root, "answered", null, 1000,
            resolveMapping: request => new ImportMappingAnswer(
                [.. request.Rows.Select(r => r.StackupConductors is null ? r : r with { Stackup = LayerStackupChoice.Vias })],
                request.Target));

        var tech = Assert.IsType<Technology>(result.Technology);
        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Equal("plated", tech.Layers.Single(l => via.DrawingLayers.Contains(l.Key)).Name);
        Assert.Equal(("Top Copper", "Bottom Copper", true), (via.SpanFromLayer, via.SpanToLayer, via.Plated != false));
        Assert.DoesNotContain(result.Messages, m => m.StartsWith("No drill data was read", StringComparison.Ordinal));
    }

    [Fact]
    public void AViaFilesName_StatesItsSpan()
    {
        Assert.Equal((1, 2), GerberLayerCascade.SpanFromName("Via_1_2.gbr"));
        Assert.Equal((2, 3), GerberLayerCascade.SpanFromName("board-via-2-3.gbr"));
        Assert.Null(GerberLayerCascade.SpanFromName("Vias.gbr"));
    }

    private const string LSection = """
        Port:P1 in  0 Num=1 Z=50 Ohm
        L:L1    in  out L=5 nH
        C:C1    out 0   C=0.5 pF
        Port:P2 out 0 Num=2 Z=200 Ohm
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        tune L1.L min=1 nH max=50 nH opt=1 discrete=e12
        tune C1.C min=0.1 pF max=10 pF opt=1 discrete=e12
        goal Match = mag(SP1.S(1,1)) analysis=SP1 le 0.0001
        optimize algorithm=de maxiter=200 seed=1
        """;

    [Fact]
    public void AStoppedRun_OverAnESeriesRow_EndsOnTheSeries()
    {
        OptimizationRun? run = null;
        run = OptimizationRun.Create(OptCircuits.Prepare(LSection), new OptimizationOptions
        {
            Progress = p => { if (p.Iteration >= 2) run!.Stop(); },
        });

        var r = run.Run();

        Assert.Contains(OptimizationStages.Snap, r.Stages);
        var e12 = PreferredValues.Series(PreferredValues.E12, -13, -7);
        foreach (string key in (string[])["L1.L", "C1.C"])
        {
            double v = OptCircuits.Si(r, key);
            Assert.True(e12.Any(rung => Math.Abs(rung - v) <= rung * 1e-9), $"{key} = {v:G15} is not an E12 value");
        }
    }

    /// <summary>Differential evolution simulates only E12 values; Levenberg–Marquardt, which needs derivatives a rung
    /// does not have, still searches between them and snaps.</summary>
    [Fact]
    public void ADerivativeFreeAlgorithm_SimulatesOnlySeriesValues_AGradientOneStillSnaps()
    {
        // A decoded quantity is in the row's display unit (4.7 for 4.7 nH), so the rungs span every decade.
        var e12 = PreferredValues.Series(PreferredValues.E12, -13, 3);
        bool OnSeries(EvaluationRecord e) => ((string[])["L1.L", "C1.C"]).All(key =>
            e.Decoded.Quantities[key].Real is var v && e12.Any(rung => Math.Abs(rung - v) <= rung * 1e-9));

        var de = OptimizationRun.Create(OptCircuits.Prepare(LSection), new OptimizationOptions()).Run();
        var lm = OptimizationRun.Create(OptCircuits.Prepare(LSection.Replace("algorithm=de", "algorithm=lm")),
                                        new OptimizationOptions()).Run();

        Assert.All(de.Log, e => Assert.True(OnSeries(e)));
        Assert.Contains(lm.Log, e => !OnSeries(e));
        Assert.Contains(OptimizationStages.Snap, lm.Stages);
    }

    /// <summary>Two goals that cannot both be met keep the run going, and their optimum (37.5) lies between E12
    /// rungs; a gradient algorithm searches continuously, so the paused best is off the series.</summary>
    [Fact]
    public async Task PausedBetweenRungs_TheBestCannotBePushed_UntilStopPutsItOnARung()
    {
        var f = OptimizerPanelFixture.OneVariable("1", "100", "bfgsb", 200,
            OptimizerPanelFixture.Goal("Lo", "A", GoalType.Le, "37"),
            OptimizerPanelFixture.Goal("Hi", "A", GoalType.Ge, "38"));
        f.Row("A").SetSeriesCommand.Execute("E12");
        f.Panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(f.Panel.Iteration) && f.Panel.Iteration == 2 && f.Panel.IsRunActive && !f.Panel.IsPaused)
                f.Panel.PauseResumeCommand.Execute(null);
        };

        f.Panel.RunCommand.Execute(null);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (f.Panel.StatusText != "Paused" && DateTime.UtcNow < until) await Task.Delay(5);

        Assert.Equal("Paused", f.Panel.StatusText);
        Assert.False(f.Panel.PushCommand.CanExecute(null));
        Assert.True(f.Panel.SnapAndPolishCommand.CanExecute(null));

        f.Panel.StopCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(f.Panel.PushCommand.CanExecute(null));
        double best = f.Row("A").Best;
        Assert.Contains(PreferredValues.Series(PreferredValues.E12, 0, 2), rung => Math.Abs(rung - best) <= rung * 1e-9);
    }

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static PathShape Stroke(double x0, double y0, double x1, double y1, double widthUm) => new()
    {
        Layer = new LayerKey(2, 0), Xy = [Um(x0), Um(y0), Um(x1), Um(y1)], Width = Um(widthUm), End = PathEndStyle.Round,
    };

    /// <summary>Flashes on a 2 mm grid, the same in every file so a via file's holes land on the copper's pads;
    /// no <c>%TF.FileFunction</c>, so the name is the only evidence.</summary>
    private static string Flashes(double diameterMm) =>
        $"%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,{diameterMm:0.000}*%\nD10*\n" +
        string.Concat(Enumerable.Range(0, 4).Select(k => $"X{k * 2_000_000}Y1000000D03*\n")) +
        "M02*\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IReadOnlyList<string> FilesIn(string dir) =>
        [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];
}
