// ================================================================
//  The S-Parameter Viewer: its plots, its data source, its window's
//  view model and the Workspace rows that open it.
//
//  The preset is what the window shows, what `circuitrf snp` draws and
//  what Save as Data Display writes, so its claims are pinned here once:
//  the frequency axis is the file's own range, a log sweep gets a log
//  axis, the Smith chart carries the reflections only, phase is ±180°,
//  and a large file opens on a readable selection.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.SParameters;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views.SParameters;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.SParameters;

public sealed class SParameterViewerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-spv-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>An N-port written to disk: S(i,j) = (0.1·(i+1) + 0.01·(j+1)) at a phase that turns with frequency.</summary>
    private string Write(int ports, double[] freqs, string? name = null, MatrixType type = MatrixType.S)
    {
        var mats = freqs.Select((f, k) =>
        {
            var m = new Mat<Complex>(ports, ports);
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                    m[i, j] = Complex.FromPolarCoordinates(0.1 * (i + 1) + 0.01 * (j + 1), 0.1 * k);
            return m;
        }).ToArray();
        var snp = new SNP(freqs, mats, type, MatrixFormat.RI, new Complex(50, 0));
        string path = Path.Combine(_dir, name ?? $"part.s{ports}p");
        TouchstoneIO.WriteFile(snp, path);
        return path;
    }

    private static double[] Uniform(double start, double step, int n) => [.. Enumerable.Range(0, n).Select(i => start + i * step)];

    private static TouchstoneSource Read(string path)
    {
        var source = TouchstoneSource.Read(path, out var refusal);
        Assert.Null(refusal);
        return source!;
    }

    // ── the preset ───────────────────────────────────────────────────────────

    [Fact]
    public void TheSmithChartCarriesTheReflections_TheRectangularPlotsEveryTerm()
    {
        var source = Read(Write(2, Uniform(1e9, 0.1e9, 41)));
        var plots = SParameterViewerPreset.Compose(source, SParameterViewerPreset.DefaultSelection(2));

        Assert.Equal([SParameterViewerPreset.PlotKind.Smith, SParameterViewerPreset.PlotKind.Polar,
                      SParameterViewerPreset.PlotKind.Magnitude, SParameterViewerPreset.PlotKind.Phase],
                     plots.Select(p => p.Kind));
        var smith = plots[0].Config;
        Assert.Equal(PlotType.Smith, smith.PlotType);
        Assert.Equal([(0, 0), (1, 1)], smith.Traces.Select(t => (t.Row, t.Col)));
        Assert.All(smith.Traces, t => Assert.Equal(DependentVarFormat.Complex, t.YAxis));

        var polar = plots[1].Config;
        Assert.Equal(PlotType.Polar, polar.PlotType);
        Assert.Equal([(0, 1), (1, 0)], polar.Traces.Select(t => (t.Row, t.Col)));
        Assert.All(polar.Traces, t => Assert.Equal(DependentVarFormat.Complex, t.YAxis));

        Assert.Equal([(0, 0), (0, 1), (1, 0), (1, 1)], plots[2].Config.Traces.Select(t => (t.Row, t.Col)));
        Assert.All(plots[2].Config.Traces, t => Assert.Equal(DependentVarFormat.Db, t.YAxis));
        Assert.All(plots[3].Config.Traces, t => Assert.Equal(DependentVarFormat.Phase, t.YAxis));
        Assert.All(plots.SelectMany(p => p.Config.Traces), t => Assert.Equal(source.Path, t.SourcePath));
    }

    /// <summary>The axis is the file's: pinned to exactly its first and last point, never rounded out to nice numbers.</summary>
    [Fact]
    public void TheFrequencyAxisIsPinnedToTheFilesOwnRange()
    {
        var source = Read(Write(2, Uniform(1.23e9, 0.01e9, 101)));
        var plots = SParameterViewerPreset.Compose(source, SParameterViewerPreset.DefaultSelection(2));
        foreach (var rect in plots.Where(p => p.Config.PlotType == PlotType.Rect).Select(p => p.Config))
        {
            Assert.Equal(FreqUnit.GHz, rect.FreqUnit);
            Assert.False(rect.Axes!.AutoscaleX);
            Assert.Equal(1.23, rect.Axes.WindowX, 9);
            Assert.Equal(2.23 - 1.23, rect.Axes.WindowWidth, 9);
            Assert.Equal(AxisScale.Linear, rect.Axes.XScale);
        }
        var phase = plots.Single(p => p.Kind == SParameterViewerPreset.PlotKind.Phase).Config.Axes!;
        Assert.False(phase.AutoscaleY);
        Assert.Equal(-180, phase.WindowY);
        Assert.Equal(360, phase.WindowHeight);
        var magnitude = plots.Single(p => p.Kind == SParameterViewerPreset.PlotKind.Magnitude).Config;
        Assert.True(magnitude.Axes!.AutoscaleY);

        // And once loaded, the plot shows exactly that window and the file's points, one per frequency.
        var loaded = PlotConfigLoader.LoadPlot(magnitude, source);
        Assert.Equal(1.23, loaded.Axes.Window.X, 6);
        Assert.Equal(2.23, loaded.Axes.Window.X + loaded.Axes.Window.Width, 6);
        Assert.All(loaded.Traces, t => Assert.Equal(101, t.Points.Count));
    }

    [Fact]
    public void ALogSweepGetsALogAxis_AndTheUnitFollowsTheSpanNotTheOptionLine()
    {
        var source = Read(Write(1, [.. Enumerable.Range(0, 31).Select(i => 1e3 * Math.Pow(10, i / 10.0))]));
        Assert.Equal(FrequencyGrid.Logarithmic, source.Facts.Grid);
        var rect = SParameterViewerPreset.Compose(source, SParameterViewerPreset.DefaultSelection(1))
            .Single(p => p.Kind == SParameterViewerPreset.PlotKind.Magnitude).Config;
        Assert.Equal(AxisScale.Log, rect.Axes!.XScale);
        Assert.Equal(FreqUnit.MHz, rect.FreqUnit);
    }

    [Fact]
    public void OneTermHasOneColourOnAllThreePlots()
    {
        var source = Read(Write(2, Uniform(1e9, 0.1e9, 11)));
        var plots = SParameterViewerPreset.Compose(source, SParameterViewerPreset.DefaultSelection(2));
        int s22 = plots[0].Config.Traces.Single(t => t.Row == 1 && t.Col == 1).Properties.LineColorIndex;
        Assert.Equal(s22, plots[2].Config.Traces.Single(t => t.Row == 1 && t.Col == 1).Properties.LineColorIndex);
        Assert.Equal(s22, plots[3].Config.Traces.Single(t => t.Row == 1 && t.Col == 1).Properties.LineColorIndex);
        Assert.DoesNotContain(plots[1].Config.Traces, t => t.Row == t.Col);
        Assert.Equal(4, plots[2].Config.Traces.Select(t => t.Properties.LineColorIndex).Distinct().Count());
    }

    [Fact]
    public void UpToFourPortsEveryTermOpens_AboveThatTheReflectionsAndForwardNeighbours()
    {
        Assert.Equal(16, SParameterViewerPreset.DefaultSelection(4).Count);
        var six = SParameterViewerPreset.DefaultSelection(6);
        Assert.Equal(6 + 5, six.Count);
        Assert.All(six, t => Assert.True(t.Row == t.Col || t.Row == t.Col + 1));
    }

    [Theory]
    [InlineData("S11,S21", 2, "S11,S21")]
    [InlineData("s(2,1), S11", 2, "S11,S21")]
    [InlineData("reflections", 3, "S11,S22,S33")]
    [InlineData("S(10,1),S(1,1)", 12, "S(1,1),S(10,1)")]
    public void ASelectionReadsAsTyped_RowMajorAndOnce(string text, int ports, string expected)
    {
        var sel = SParameterViewerPreset.ParseSelection(text, ports, out var error);
        Assert.Null(error);
        Assert.Equal(expected, string.Join(",", sel!.Select(t => SParameterViewerPreset.Name(t, ports))));
    }

    [Theory]
    [InlineData("S31", 2)]
    [InlineData("S101", 12)]
    [InlineData("gain", 2)]
    public void ATermTheFileDoesNotHaveIsRefused(string text, int ports)
    {
        Assert.Null(SParameterViewerPreset.ParseSelection(text, ports, out var error));
        Assert.Equal("touchstone.no-such-parameter", error!.Id);
    }

    [Fact]
    public void TheFilePaneSpellsTheFileOnce()
    {
        var source = Read(Write(2, Uniform(1e9, 0.1e9, 41)));
        var rows = SParameterViewerPreset.FileFacts(source.Facts).ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("2", rows["Ports"]);
        Assert.Equal("41", rows["Points"]);
        Assert.Equal("1 GHz", rows["Start"]);
        Assert.Equal("5 GHz", rows["Stop"]);
        Assert.Equal("uniform, step 100 MHz", rows["Spacing"]);
        Assert.Equal("50 Ω", rows["Reference"]);
        Assert.Equal("No ⚠", rows["DC point"]);
    }

    [Fact]
    public void ASampleAtZeroHertzIsADcPoint()
    {
        var source = Read(Write(1, [0, 1e9, 2e9], "fromdc.s1p"));
        Assert.True(source.Facts.HasDcPoint);
        Assert.Equal("Yes", SParameterViewerPreset.FileFacts(source.Facts).Single(r => r.Label == "DC point").Value);
        var vm = new SParameterViewerViewModel();
        vm.Show(source.Path, source, null);
        Assert.False(vm.DcPointMissing);
        Assert.False(vm.Facts.Single(r => r.Label == "DC point").Warn);
    }

    [Fact]
    public void ASmithHoverNamesTheTermThenGammaThenTheImpedance()
    {
        Assert.Equal("Γ=21+j30 (36.62 ∠ 55.01°)", SParameterViewerPreset.Gamma(new Complex(21, 30)));
        Assert.Equal("Γ=21-j30 (36.62 ∠ -55.01°)", SParameterViewerPreset.Gamma(new Complex(21, -30)));
        Assert.Equal(
            "S(1,1) @ 2.4 GHz\nΓ=0.5+j0.1 (0.51 ∠ 11.31°)\nZ=Z0*(1+j0) Ω",
            SParameterViewerPreset.SmithHover("S(1,1)", 2.4e9, new Complex(0.5, 0.1), "impedance=Z0*(1+j0)"));
        Assert.Equal(
            "S(2,2) @ 1 GHz\nΓ=0+j0 (0.00 ∠ 0°)\nZ=50 Ω",
            SParameterViewerPreset.SmithHover("S(2,2)", 1e9, Complex.Zero, "impedance=50 Ω"));
    }

    // ── the source ───────────────────────────────────────────────────────────

    /// <summary>
    /// A network trace reads its source's matrices as S whatever the option line said, so a Y-parameter file
    /// handed over as read would draw admittances on a Smith chart. The source converts it first.
    /// </summary>
    [Fact]
    public void AYParameterFileIsPlottedAsS()
    {
        string path = Write(2, Uniform(1e9, 0.1e9, 5), "admittance.s2p", MatrixType.Y);
        var source = Read(path);
        Assert.Equal(MatrixType.Y, source.Facts.Type);
        Assert.Equal(MatrixType.S, source.SParameters.Type);
        var expected = RFNetwork.YToS(source.AsRead.Matrices[0], source.AsRead.Z0);
        Assert.Equal(expected[1, 0], source.NetworkFor(source.Path)!.Matrices[0][1, 0]);
    }

    [Fact]
    public void AFileThatCannotBeReadIsARefusalNotAnException()
    {
        string path = Path.Combine(_dir, "broken.s2p");
        File.WriteAllText(path, "# GHz S MA R 50\nthis is not data\n");
        Assert.Null(TouchstoneSource.Read(path, out var refusal));
        Assert.Equal("touchstone.unreadable", refusal!.Id);
    }

    // ── the window's view model ──────────────────────────────────────────────

    [Fact]
    public void ThePlotsAreCentred_StackedWithAGap_AndSplitWhenAllThreeAreOn()
    {
        var one = SParameterPlotLayout.SlotsFor(smith: false, polar: false, magnitude: true, phase: false, 800, 500);
        Assert.Equal(0, one.Smith.Width);
        Assert.InRange((one.Magnitude.Left + one.Magnitude.Right) / 2, 399, 401);
        Assert.True(one.Magnitude.Top > 1);
        Assert.True(one.Magnitude.Bottom < 499);

        var two = SParameterPlotLayout.SlotsFor(smith: true, polar: false, magnitude: true, phase: false, 800, 500);
        Assert.True(two.Magnitude.Top >= two.Smith.Bottom + SParameterPlotLayout.Gap - 0.1);
        Assert.Equal(800, two.Smith.Width);
        Assert.Equal(800, two.Magnitude.Width);
        Assert.Equal(0, two.Phase.Width);

        var pair = SParameterPlotLayout.SlotsFor(smith: false, polar: false, magnitude: true, phase: true, 800, 500);
        Assert.Equal(0, pair.Smith.Width);
        Assert.Equal(0, pair.Polar.Width);
        Assert.Equal(0, pair.Magnitude.Left);
        Assert.Equal(800, pair.Magnitude.Width);
        Assert.Equal(800, pair.Phase.Width);
        Assert.True(pair.Phase.Top >= pair.Magnitude.Bottom + SParameterPlotLayout.Gap - 0.1);
        Assert.Equal(pair.Magnitude.Height, pair.Phase.Height);

        var rounds = SParameterPlotLayout.SlotsFor(smith: true, polar: true, magnitude: false, phase: false, 800, 500);
        Assert.Equal(rounds.Smith.Top, rounds.Polar.Top);
        Assert.True(rounds.Polar.Left >= rounds.Smith.Right + SParameterPlotLayout.Gap - 0.1);

        var smithOnly = SParameterPlotLayout.SlotsFor(smith: true, polar: false, magnitude: false, phase: false, 800, 740);
        Assert.Equal(800, smithOnly.Smith.Width);
        Assert.True(smithOnly.Smith.Height < 740);
        Assert.True((800 - smithOnly.Smith.Height) / 2 >= 120 - 1);

        var four = SParameterPlotLayout.SlotsFor(smith: true, polar: true, magnitude: true, phase: true, 800, 500);
        Assert.Equal(four.Smith.Top, four.Polar.Top);
        Assert.Equal(four.Magnitude.Top, four.Phase.Top);
        Assert.True(four.Polar.Left >= four.Smith.Right + SParameterPlotLayout.Gap - 0.1);
        Assert.True(four.Magnitude.Top >= four.Smith.Bottom + SParameterPlotLayout.Gap - 0.1);
        Assert.True(four.Phase.Left >= four.Magnitude.Right + SParameterPlotLayout.Gap - 0.1);

        var three = SParameterPlotLayout.SlotsFor(smith: true, polar: false, magnitude: true, phase: true, 800, 500);
        Assert.Equal(0, three.Smith.Top);
        Assert.Equal(800, three.Smith.Width);
        Assert.True(three.Magnitude.Top >= three.Smith.Bottom + SParameterPlotLayout.Gap - 0.1);
        Assert.True(three.Phase.Left >= three.Magnitude.Right + SParameterPlotLayout.Gap - 0.1);
        Assert.Equal(three.Magnitude.Top, three.Phase.Top);
        Assert.Equal(three.Magnitude.Width, three.Phase.Width);
    }

    [Fact]
    public void TheWindowOpensOnSmithAndMagnitude_AndEveryPlotCanBeOff()
    {
        var vm = new SParameterViewerViewModel();
        Assert.True(vm.ShowSmith);
        Assert.True(vm.ShowMagnitude);
        Assert.False(vm.ShowPolar);
        Assert.False(vm.ShowPhase);

        vm.ShowSmith = false;
        vm.ShowMagnitude = false;
        Assert.False(vm.ShowSmith);
        Assert.False(vm.ShowMagnitude);
        Assert.False(vm.ShowPolar);
        Assert.False(vm.ShowPhase);
    }

    [Fact]
    public void TraceNamesSitBesideThePlot_AndTheSmithChartStaysInsideItsRow()
    {
        var vm = new SParameterViewerViewModel();
        string path = Write(2, Uniform(1e9, 0.1e9, 41));
        vm.Show(path, Read(path), null);

        Assert.False(vm.SmithPlot.CustomTitleOn);
        Assert.False(vm.MagnitudePlot.CustomTitleOn);
        Assert.Equal(LegendPlacement.Off, vm.SmithPlot.Legend);
        Assert.Equal(LegendPlacement.Off, vm.MagnitudePlot.Legend);
        Assert.Equal(LegendPlacement.Off, vm.PhasePlot.Legend);
        Assert.All(vm.SmithPlot.Traces, t => Assert.True(t.ExcludeFromAxisLabels));
        Assert.Equal(["S(1,1)", "S(2,2)"], vm.SmithLegend.Select(e => e.Label));
        Assert.Equal(4, vm.MagnitudeLegend.Count);
        Assert.Contains("S(1,1)", vm.MagnitudeLegend[0].Label);
        Assert.Equal(4, vm.PhaseLegend.Count);

        // A title used to push the disc down without shrinking it, so the bottom of a Smith chart
        // drawn into a fixed row landed past the row — over the window, and over the plots below.
        foreach (var (w, h) in new[] { (900.0, 700.0), (900.0, 280.0), (400.0, 700.0) })
        {
            var vp = PlotRenderer.BuildTransforms(vm.SmithPlot, (w, h)).Viewport;
            Assert.InRange(vp.Y, 0, 1);
            Assert.InRange(vp.X, 0, 1);
            Assert.True(vp.Y + vp.Height <= 1.001, $"disc bottom {vp.Y + vp.Height} at {w}×{h}");
            Assert.True(vp.X + vp.Width <= 1.001, $"disc right {vp.X + vp.Width} at {w}×{h}");
        }

        string wide = Write(3, Uniform(1e9, 0.1e9, 11), "wide.s3p");
        vm.Show(wide, Read(wide), null);
        Assert.Empty(vm.MagnitudeLegend);
        Assert.Equal(3, vm.SmithLegend.Count);
    }

    [Fact]
    public void ACommentCountCarriesTheLines()
    {
        string path = Path.Combine(_dir, "noted.s1p");
        File.WriteAllText(path, "! measured on the bench\n! fixture subtracted\n# GHz S RI R 50\n1 0.1 0.2\n2 0.1 0.3\n");
        var source = Read(path);
        Assert.Equal("2 lines", SParameterViewerPreset.FileFacts(source.Facts).Single(r => r.Label == "Comments").Value);
    }

    [Fact]
    public void TheViewModelShowsTheFile_ItsTermsAndThePlots()
    {
        var vm = new SParameterViewerViewModel();
        Assert.True(vm.IsEmpty);
        string path = Write(2, Uniform(1e9, 0.1e9, 41));
        vm.Show(path, Read(path), null);

        Assert.True(vm.HasFile);
        Assert.False(vm.IsEmpty);
        Assert.EndsWith("part.s2p", vm.Title);
        Assert.Contains(vm.Facts, r => r.Label == "Points" && r.Value == "41");
        var dc = Assert.Single(vm.Facts, r => r.Label == "DC point");
        Assert.Equal("No ⚠", dc.Value);
        Assert.True(dc.Warn);
        Assert.Equal("No", dc.Text);
        Assert.True(vm.DcPointMissing);
        Assert.Equal(["S11", "S12", "S21", "S22"], vm.Terms.Select(t => t.Name));
        Assert.All(vm.Terms, t => Assert.True(t.IsChecked));
        Assert.Equal(2, vm.SmithPlot.Traces.Count);
        Assert.Equal([(0, 1), (1, 0)], vm.PolarPlot.Traces.Select(t => (t.Row, t.Col)));
        Assert.Equal(4, vm.MagnitudePlot.Traces.Count);
        Assert.Equal(4, vm.PhasePlot.Traces.Count);
        Assert.False(vm.HasFindings);
    }

    [Fact]
    public void UntickingATermTakesItOffEveryPlot_AndTheBulkChoicesRedrawOnce()
    {
        var vm = new SParameterViewerViewModel();
        string path = Write(2, Uniform(1e9, 0.1e9, 41));
        vm.Show(path, Read(path), null);
        int redraws = 0;
        vm.PlotsUpdated += (_, _) => redraws++;

        vm.Terms.Single(t => t.Name == "S11").IsChecked = false;
        Assert.Equal(1, redraws);
        Assert.Single(vm.SmithPlot.Traces);
        Assert.Equal(3, vm.MagnitudePlot.Traces.Count);
        Assert.DoesNotContain(vm.PhasePlot.Traces, t => t.Row == 0 && t.Col == 0);

        vm.SelectReflectionsCommand.Execute(null);
        Assert.Equal(2, redraws);
        Assert.Equal([(0, 0), (1, 1)], vm.MagnitudePlot.Traces.Select(t => (t.Row, t.Col)));

        vm.SelectNoneCommand.Execute(null);
        Assert.Empty(vm.MagnitudePlot.Traces);
        Assert.Contains("No terms ticked", vm.Status);
    }

    [Fact]
    public void AMarkerStaysOnItsTermAcrossARedraw()
    {
        var vm = new SParameterViewerViewModel();
        string path = Write(2, Uniform(1e9, 0.1e9, 41));
        vm.Show(path, Read(path), null);
        var s21 = vm.MagnitudePlot.Traces.Single(t => t.Row == 1 && t.Col == 0);
        var marker = new Marker(s21, 2e9, false, false, 1);
        s21.Markers.Add(marker);

        vm.Redraw();

        var after = vm.MagnitudePlot.Traces.Single(t => t.Row == 1 && t.Col == 0);
        Assert.NotSame(s21, after);
        Assert.Contains(marker, after.Markers);
    }

    [Fact]
    public void AFileWithADefectStatesIt_AndAnUnreadableOneSaysWhy()
    {
        var vm = new SParameterViewerViewModel();
        string mislabelled = Write(2, Uniform(1e9, 0.1e9, 5), "mislabelled.s3p");
        vm.Show(Path.GetFullPath(mislabelled), Read(mislabelled), null);
        Assert.True(vm.HasFindings);
        Assert.Contains("3 ports", vm.Findings.Single());

        string broken = Path.Combine(_dir, "broken.s2p");
        File.WriteAllText(broken, "# GHz S MA R 50\nnot data\n");
        var source = TouchstoneSource.Read(broken, out var refusal);
        vm.Show(broken, source, refusal);
        Assert.True(vm.HasError);
        Assert.False(vm.HasFile);
        Assert.Contains("broken.s2p", vm.ErrorText);
        Assert.Empty(vm.MagnitudePlot.Traces);
    }

    [Fact]
    public void ReloadingTheSameFileKeepsTheTicks()
    {
        var vm = new SParameterViewerViewModel();
        string path = Path.GetFullPath(Write(2, Uniform(1e9, 0.1e9, 11)));
        vm.Show(path, Read(path), null);
        vm.SelectReflectionsCommand.Execute(null);
        vm.Show(path, Read(path), null);
        Assert.Equal(["S11", "S22"], vm.Terms.Where(t => t.IsChecked).Select(t => t.Name));
    }

    // ── the Workspace rows ───────────────────────────────────────────────────

    private static ProjectTreeNodeViewModel Node(NodeKind kind, string abs, string rel, string? warning = null) =>
        new(new ProjectTreeNode(kind, Path.GetFileName(abs), abs, rel, warningReason: warning), new ProjectTreeFilterState());

    [Theory]
    [InlineData("amp.s2p")]
    [InlineData("coupler.S4P")]
    [InlineData("bus.s16p")]
    public void TouchstoneKnownFilesAndInFolderFiles_OpenInTheViewer(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not read to decide");
        Assert.True(Node(NodeKind.KnownFile, path, name).IsTouchstoneFile);
        Assert.True(Node(NodeKind.OtherFile, path, name).IsTouchstoneFile);
    }

    [Fact]
    public void APicture_ABrokenKnownFile_AndAFileOutsideTheFolder_DoNot()
    {
        string png = Path.Combine(_dir, "plot.png");
        File.WriteAllText(png, "x");
        Assert.False(Node(NodeKind.KnownFile, png, "plot.png").IsTouchstoneFile);
        string gone = Path.Combine(_dir, "gone.s2p");
        Assert.False(Node(NodeKind.KnownFile, gone, "gone.s2p", warning: "Known File path not found").IsTouchstoneFile);
        string outside = Path.Combine(_dir, "elsewhere.s2p");
        File.WriteAllText(outside, "x");
        Assert.False(Node(NodeKind.OtherFile, outside, "../elsewhere.s2p").IsTouchstoneFile);
    }
}
