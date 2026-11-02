using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Optimization;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Tests.Optimization;
using RfCore.Data;
using RfCore.Export;
using SkiaSharp;
using SampleStatistics = CircuitRF.Core.Expressions.SampleStatistics;
using FreqUnit = CircuitRF.Render.DataDisplay.FreqUnit;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-yield-9's fixture: a ten-trial result over five frequencies, trials 3, 6 and 9 failing.</summary>
internal static class TrialFixtures
{
    public static readonly int[] Fails = [3, 6, 9];

    /// <summary>The value trial <paramref name="t"/> reads at frequency index <paramref name="k"/> — spread so that
    /// neither the minimum nor the maximum trial is the same one at every frequency.</summary>
    public static double Value(int t, int k) => Math.Sin(1.3 * t + 0.7 * k) + 0.1 * k;

    public static double Stat(int t) => 1000 + 10 * t + 3 * Math.Cos(t);

    public static DataSet Result(int trials = 10)
    {
        var trial = new Axis("trial", [.. Enumerable.Range(1, trials).Select(i => (double)i)]);
        var freq = new Axis("freq", [1e9, 2e9, 3e9, 4e9, 5e9], "Hz");
        var ds = new DataSet();
        ds.AddToGroup("SP1", "G", new DataCube([trial, freq],
            [.. Enumerable.Range(1, trials).SelectMany(t => Enumerable.Range(0, 5).Select(k => Value(t, k)))]));
        ds.AddToGroup("trials", "stat:R1.R", new DataCube([trial], [.. Enumerable.Range(1, trials).Select(Stat)]) { Unit = "Ohm" });
        // The worst value is linear in R1 with a known wobble, so a fit has a meaningful R².
        ds.AddToGroup("trials", "goal:G:worst", new DataCube([trial], [.. Enumerable.Range(1, trials).Select(t => 0.02 * Stat(t) + Math.Sin(3.1 * t))]));
        ds.AddToGroup("trials", "pass", new DataCube([trial], [.. Enumerable.Range(1, trials).Select(t => Fails.Contains(t) ? 0.0 : 1.0)]));
        ds.AddToGroup("trials", "status", new DataCube([trial], new double[trials]));
        ds.AddToGroup("nominal", "SP1.G", new DataCube([freq], [.. Enumerable.Range(0, 5).Select(k => Value(0, k))]));
        return ds;
    }

    /// <summary>The family over trials, as the picker authors one, coloured by pass.</summary>
    public static Trace Family(string? colorBy = Trace.ColorByPass)
    {
        var t = DisplayFixtures.CubeTrace("SP1.G", new AxisSlice("trial", AxisRole.FamilyIterate, 0),
                                                   new AxisSlice("freq", AxisRole.KeepAsX, 0));
        t.ColorBy = colorBy;
        return t;
    }
}

/// <summary>R-ya9-1/2: ten members with three fails draw seven passes then three fails, in two colours, the nominal last.</summary>
public sealed class FamilyColourByTests
{
    [Fact]
    public void TenMembersWithThreeFails_DrawSevenThenThree_InTwoColours_NominalLast()
    {
        var t = TrialFixtures.Family();
        DisplayFixtures.Resolved(TrialFixtures.Result(), t);
        Assert.Equal(new TrialCounts(7, 3, 0), t.Counts);
        Assert.EndsWith(" — 7 pass · 3 fail", t.RectYLabel("G", false));

        var theme = RenderTheme.Light;
        var plan = TrialRenderer.FamilyPlan(t, theme);
        Assert.Equal(11, plan.Count);
        var passColor = theme.ToSKColor(theme.PassColor, t.Properties.LineOpacity * TrialRenderer.PassOpacity);
        var failColor = theme.ToSKColor(theme.FailColor, t.Properties.LineOpacity);
        int Trial(TrialStroke s) => (int)t.FamilyCurves[s.Member].AxisValue;

        Assert.All(plan.Take(7), s => { Assert.False(TrialFixtures.Fails.Contains(Trial(s))); Assert.Equal(passColor, s.Color); });
        Assert.Equal(TrialFixtures.Fails, plan.Skip(7).Take(3).Select(Trial));
        Assert.All(plan.Skip(7).Take(3), s => Assert.Equal(failColor, s.Color));
        Assert.NotEqual(passColor.WithAlpha(255), failColor.WithAlpha(255));

        var nominal = plan[^1];
        Assert.Equal(TrialStrokeKind.Nominal, nominal.Kind);
        Assert.Equal(theme.ToSKColor(t.Properties.LineColor, 1.0), nominal.Color);
        Assert.Equal(TrialFixtures.Value(0, 2), t.NominalPoints[2].Y, 6);

        // In the .cdd: a trace with no trial view writes none of the fields; a coloured one reads back coloured.
        var plain = new TraceConfig { CubeName = "x" };
        plain.SetTrialViews(TrialFixtures.Family(colorBy: null));
        string json = JsonSerializer.Serialize(plain, DataDisplayJson.Options);
        foreach (var key in new[] { "ColorBy", "Nominal", "Envelope", "Curves", "FitLine" })
            Assert.DoesNotContain($"\"{key}\"", json);
        var coloured = new TraceConfig();
        coloured.SetTrialViews(t);
        var back = JsonSerializer.Deserialize<TraceConfig>(JsonSerializer.Serialize(coloured, DataDisplayJson.Options), DataDisplayJson.Options)!;
        var reloaded = TrialFixtures.Family(colorBy: null);
        back.ApplyTrialViews(reloaded);
        Assert.Equal(Trace.ColorByPass, reloaded.ColorBy);
    }
}

/// <summary>R-ya9-3: the min–max band is the pointwise min and max; a percentile band is that percentile; Smith refuses.</summary>
public sealed class EnvelopeTests
{
    [Fact]
    public void MinMaxAndPercentileBands_AreThePointwiseStatistics_AndASmithPlotRefuses()
    {
        var ds = TrialFixtures.Result();
        double[] Column(int k) => [.. Enumerable.Range(1, 10).Select(t => TrialFixtures.Value(t, k))];

        var t = TrialFixtures.Family(colorBy: null);
        t.Envelope = new TrialEnvelope(EnvelopeKind.MinMax, 0);
        DisplayFixtures.Resolved(ds, t);
        var band = t.Band!;
        Assert.Equal([1, 2, 3, 4, 5], band.X);                               // GHz, as the curves are drawn
        for (int k = 0; k < 5; k++)
        {
            Assert.Equal(Column(k).Min(), band.Lo[k], 12);
            Assert.Equal(Column(k).Max(), band.Hi[k], 12);
            Assert.Equal(SampleStatistics.Median(Column(k)), band.Centre[k], 12);
        }

        Assert.True(TrialEnvelope.TryParse("p:5", out var p5, out _));
        t.Envelope = p5;
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        for (int k = 0; k < 5; k++)
        {
            Assert.Equal(SampleStatistics.Percentile(Column(k), 5), t.Band!.Lo[k], 12);
            Assert.Equal(SampleStatistics.Percentile(Column(k), 95), t.Band.Hi[k], 12);
        }

        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Smith, FreqUnit.GHz);
        Assert.Null(t.Band);
        Assert.Equal(TrialResolve.ComplexPlaneRefusal, t.EnvelopeRefusal);
        Assert.Equal(TrialResolve.ComplexPlaneRefusal, TrialViews.EnvelopeRefusal(t, PlotType.Smith));
    }
}

/// <summary>R-ya9-4: Scatter vs writes the Versus spec and a points style; colours follow pass; the fit's R² is r².</summary>
public sealed class ScatterTests
{
    [Fact]
    public void ScatterVs_WritesTheVersusSpec_ColoursFollowPass_AndTheFitsRSquaredIsRSquared()
    {
        var ds = TrialFixtures.Result();
        var t = DisplayFixtures.CubeTrace("trials.goal:G:worst", new AxisSlice("trial", AxisRole.KeepAsX, 0));
        Assert.Contains("trials.stat:R1.R", TrialViews.ScatterCandidates(t, ds));

        TrialViews.ApplyScatter(t, ds, "trials.stat:R1.R");
        Assert.Equal("trials.stat:R1.R", t.XSpec);
        Assert.EndsWith(" vs trials.stat:R1.R", t.Expression);
        Assert.False(t.Properties.LineEnabled);
        Assert.True(t.Properties.MarkerEnabled);
        Assert.Equal(Trace.ColorByPass, t.ColorBy);

        t.ShowFitLine = true;
        DisplayFixtures.Resolved(ds, t);
        Assert.Equal(TrialElements.Points, t.ElementKind);
        for (int p = 0; p < t.Points.Count; p++)
        {
            int trial = t.ElementTrials![p][0];
            Assert.Equal(TrialFixtures.Stat(trial), t.Points[p].X, 1e-3);   // a point is a float
            Assert.Equal(TrialFixtures.Fails.Contains(trial) ? TrialCategory.Fail : TrialCategory.Pass, t.MemberCategories![p]);
        }

        // r² from the definition, over the result's own doubles.
        var x = Enumerable.Range(1, 10).Select(TrialFixtures.Stat).ToArray();
        var y = ds["trials.goal:G:worst"].RealValues;
        double mx = x.Average(), my = y.Average();
        double sxy = x.Zip(y).Sum(v => (v.First - mx) * (v.Second - my));
        double r2 = sxy * sxy / (x.Sum(v => (v - mx) * (v - mx)) * y.Sum(v => (v - my) * (v - my)));
        Assert.Equal(r2, t.Fit!.RSquared, 5);
        Assert.Contains($"R² = {r2:0.###}", t.RectYLabel("G", false));
    }
}

/// <summary>R-ya9-6: a pick in one display selects the trial in another bound to the same source; a bar picks its
/// bin; Send to Tuning hands on the trial's values.</summary>
public sealed class TrialSelectionTests
{
    [Fact]
    public async Task APickInOneDisplay_HighlightsTheTrialInAnother_ABarPicksItsBin_AndSendToTuningHandsOnItsValues()
    {
        string dir = OptCli.Dir();
        string path = Path.Combine(dir, "lpf.yield.npy");
        var ds = TrialFixtures.Result();
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        try
        {
            async Task<(DataSourceLibraryViewModel Lib, Plot Plot)> Display(Trace trace)
            {
                var vm = new DataDisplayDocumentViewModel();
                var lib = vm.Window.DataSourceLibrary;
                lib.ResultsRootProvider = () => dir;
                await lib.LoadFileAsync(path);
                var plot = vm.Window.DataDisplay!.Plots[0].PlotVM.Plot;
                plot.SetPlotType(PlotType.Rect);
                trace.SourcePath = path;
                plot.Traces.Add(trace);
                PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Rect, FreqUnit.GHz);
                plot.Autoscale(force: true);
                return (lib, plot);
            }

            var scatter = DisplayFixtures.CubeTrace("trials.goal:G:worst", new AxisSlice("trial", AxisRole.KeepAsX, 0));
            TrialViews.ApplyScatter(scatter, ds, "trials.stat:R1.R");
            var (libA, plotA) = await Display(scatter);
            var family = TrialFixtures.Family();
            var (_, plotB) = await Display(family);

            // Click trial 4's point on A.
            var tf = PlotRenderer.BuildTransforms(plotA, (500, 300));
            int p4 = Enumerable.Range(0, scatter.Points.Count).Single(p => scatter.ElementTrials![p][0] == 4);
            var at = tf.ToCanvas(scatter.Points[p4].X, scatter.Points[p4].Y, false);
            var hit = TrialPick.At(plotA, tf, new SKPoint(at.X + 2, at.Y - 1))!;
            Assert.Equal([4], hit.Trials);
            TrialSelection.Shared.Select(hit.Trace.SourcePath!, hit.Trials);

            Assert.Equal([4], family.SelectedTrials!);                        // display B, through the shared store
            var last = TrialRenderer.FamilyPlan(family, RenderTheme.Light)[^1];
            Assert.Equal((TrialStrokeKind.Selected, 4), (last.Kind, (int)family.FamilyCurves[last.Member].AxisValue));
            Assert.Equal((path, 4), TrialActions.SingleSelected(plotB));

            // A histogram's bar picks every trial in its bin.
            var hist = DisplayFixtures.ExpressionTrace("histogram(trials.goal:G:worst, 3)");
            hist.Properties.DrawStyle = TraceDrawStyle.Bars;
            var histPlot = DisplayFixtures.Resolved(ds, hist);
            var htf = PlotRenderer.BuildTransforms(histPlot, (500, 300));
            var bar = StatisticsRenderer.BarRects(hist, htf, FreqUnit.GHz)[0][1];
            var barHit = TrialPick.At(histPlot, htf, new SKPoint(bar.MidX, bar.Bottom - 1))!;
            var worst = ds["trials.goal:G:worst"].RealValues;
            double lo = worst.Min(), w = (worst.Max() - lo) / 3;
            Assert.Equal(Enumerable.Range(1, 10).Where(t => worst[t - 1] >= lo + w && worst[t - 1] < lo + 2 * w), barHit.Trials);
            Assert.Equal(hist.Points[1].Y, barHit.Trials.Count);

            // Send to Tuning hands the trial's own drawn values to the workspace's hook.
            IReadOnlyDictionary<string, string>? sent = null;
            libA.SendTrialToTuning = (values, _, _) => sent = values;
            Assert.True(TrialActions.SendToTuning(libA, path, 4));
            Assert.Equal(TrialFixtures.Stat(4), TunableValue.InUnit(sent!["R1.R"], "Ohm")!.Value, 9);
        }
        finally
        {
            TrialSelection.Shared.Clear(path);
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>R-ya9-7: <c>plot … colorby=pass,envelope=p:1</c> and the in-process composer write the same SVG bytes.</summary>
public sealed class TrialPlotParityTests
{
    [Fact]
    public void APassFailFamilyWithAP1P99Envelope_IsTheSameBytesFromTheCliAndInProcess()
    {
        string dir = OptCli.Dir();
        string npy = Path.Combine(dir, "lpf.yield.npy");
        DataSetExporter.Export(TrialFixtures.Result(), npy, ExportFormat.Npy);

        string svg = Path.Combine(dir, "f.svg"), cdd = Path.Combine(dir, "f.cdd");
        var (exit, _, err) = OptCli.Run("plot", npy, "-o", svg, "--write-cdd", cdd,
                                        "--trace", "cube=SP1.G,colorby=pass,envelope=p:1");
        Assert.True(exit == 0, err);

        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
        var pc = config.Tabs[0].Plots[0];
        Assert.Equal(("pass", "p:1"), (pc.Traces[0].ColorBy, pc.Traces[0].Envelope));
        var (sources, refusal) = CddSources.Bind(npy, config, config.Tabs, [0], [npy]);
        Assert.Null(refusal);
        var plot = PlotConfigLoader.LoadPlot(pc, sources!);
        Assert.Equal(new TrialCounts(7, 3, 0), plot.Traces[0].Counts);
        Assert.NotNull(plot.Traces[0].Band);

        var placed = RenderDataDisplay.Place(plot, pc.Left, pc.Top, pc.Width, pc.Height, pc.FreqUnit,
                                             sources!.HasMultipleSources, t => sources.AliasFor(t.EffectiveSourcePath ?? ""));
        var current = AppSettings.Current;
        var settings = new AppSettings
        {
            ExportTheme                    = current.ExportTheme,
            ExportTransparentBackground    = current.ExportTransparentBackground,
            MarkerBoxTransparentBackground = current.MarkerBoxTransparentBackground,
            AlwaysDisplayDataSourcePrefix  = current.AlwaysDisplayDataSourcePrefix,
        };
        string mine = PlotDocumentWriter.BuildSvgString(
            canvas => PlotComposer.Render(canvas, [placed], RenderTheme.Light, settings, PagePlacement.Letter),
            PagePlacement.Letter);
        // Byte for byte but for Skia's clipPath ids, which come from a counter in the PROCESS (RailZMapTests'
        // WithoutSkiaIds): a second SVG rendered in one test process writes cl_9 where the CLI's fresh process wrote cl_3.
        static string WithoutSkiaIds(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\bcl_[0-9a-fA-F]+\b", "cl_");
        Assert.Equal(WithoutSkiaIds(mine), WithoutSkiaIds(File.ReadAllText(svg, new System.Text.UTF8Encoding(false))));
    }
}

/// <summary>R-ya9-5: Contributions ranks the goal once, keeps it in the source, and the Pareto reads its contributors.</summary>
public sealed class ContributionParetoTests
{
    [Fact]
    public void TheParetoStoresTheRankingOnce_AndLabelsItsAxisWithTheContributors()
    {
        var ds = DisplayFixtures.YieldResult();
        var (pc, why) = ContributionParetoPreset.Build(ds, "Vout", "r.yield.npy", out bool stored);
        Assert.True(why is null, why);
        Assert.True(stored);
        ContributionParetoPreset.Build(ds, "Vout", "r.yield.npy", out bool again);
        Assert.False(again);                                                   // computed once

        var bars = DisplayFixtures.ExpressionTrace(pc!.Traces[0].Expression!);
        bars.Properties.DrawStyle = TraceDrawStyle.Bars;
        var cumulative = DisplayFixtures.ExpressionTrace(pc.Traces[1].Expression!);
        var plot = DisplayFixtures.Resolved(ds, bars, cumulative);
        Assert.Equal(["R1.R", "R2.R"], plot.XCategoryLabels()!.Order());
        Assert.Equal(100, cumulative.Points[^1].Y, 6);
        Assert.True(bars.Points[0].Y >= bars.Points[1].Y);                     // largest share first
    }
}

/// <summary>A page pan redraws a trial plot unchanged every frame; its members come from one picture after the first
/// frame, at the pixels a direct draw gives, while a change to the plot draws them directly again.</summary>
public sealed class TrialLayerCacheTests
{
    private const int W = 400, H = 300;

    private static byte[] Draw(Plot plot, TrialLayerCache? cache, int dx = 0, int dy = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W + 20, H + 20, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.White);
        c.Translate(dx, dy);
        PlotRenderer.Draw(c, (W, H), plot, PlotDetail.Full, RenderTheme.Light, trialCache: cache);
        using var img = surface.Snapshot();
        using var px = img.PeekPixels();
        return px.GetPixelSpan().ToArray();
    }

    [Fact]
    public void TheSameMembersTwice_DrawFromOnePicture_AtTheDirectPixels_AndThroughAPan()
    {
        var plot   = DisplayFixtures.Resolved(TrialFixtures.Result(), TrialFixtures.Family());
        var direct = Draw(plot, null);
        var cache  = new TrialLayerCache();

        Assert.Equal(direct, Draw(plot, cache));                 // first sight: drawn directly
        Assert.Equal(0, cache.Builds);
        var cached = Draw(plot, cache);
        Assert.Equal(1, cache.Builds);
        Assert.True(direct.Zip(cached).Max(p => Math.Abs(p.First - p.Second)) <= 2, "the picture's pixels differ from a direct draw");

        Draw(plot, cache, dx: 13, dy: 7);                         // the page moved by whole pixels: the same picture
        Assert.Equal(1, cache.Builds);

        plot.Axes.Window = plot.Axes.Window with { X = plot.Axes.Window.X + 0.5 };   // the plot's own pan
        Assert.Equal(Draw(plot, null), Draw(plot, cache));        // drawn directly, nothing made
        Assert.Equal(1, cache.Builds);
    }
}
