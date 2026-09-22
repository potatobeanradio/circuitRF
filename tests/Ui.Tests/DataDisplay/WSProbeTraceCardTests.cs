// ================================================================
//  WSProbeTraceCardTests.cs — WSP-4 §2 at the CARD, not at the model.
//
//  The metric list appears where every other quantity appears (the item picker), the probe pickers
//  appear beside it, the gating is offered rather than hidden, and "Mark crossings" places exactly
//  the markers the readout reported. What the numbers ARE is gated in WSProbeTraceTests.
// ================================================================

using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class WSProbeTraceCardTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string p in _temp) { try { File.Delete(p); } catch { /* best effort */ } }
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }

    private static DataSet Run(string fixture)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(RepoRoot(), "testdata", "wsprobe", fixture));
        var nl    = new Elaborator(lib).Elaborate(tb);
        var freqs = tb.Analyses.OfType<CircuitRF.Core.Design.SParameterAnalysis>()
                      .First().Expand(nl.ResolvedGlobals);
        return SParameterEngine.Run(nl, freqs);
    }

    /// <summary>The inspector over a real run, opened on a probe metric — the state the card is in
    /// after a user picks one out of the item list.</summary>
    private async Task<(PlotInspectorViewModel Insp, TraceRowViewModel Row)> CardOn(
        string fixture, WspMetric metric, PlotType plotType = PlotType.Rect)
    {
        var ds = Run(fixture);
        string path = Path.Combine(Path.GetTempPath(), $"crf_wsp4_{Guid.NewGuid():N}.npy");
        _temp.Add(path);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);

        var library = new DataSourceLibraryViewModel();
        await library.LoadFileAsync(path);
        await library.SelectDataSourceAsync(path);

        var loaded = library.SelectedEntry!.Data!;
        string spec = WspSource.WspCubeSpec(WspSource.GroupsWithProbes(loaded).First());
        var probes  = WspSource.Probes(loaded, WspSource.GroupOf(spec));

        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex)
        {
            SourcePath = path,
            CubeName   = spec,
            Transform  = WspMetrics.DefaultTransform(metric, plotType),
            Wsp        = new WspTraceSpec { Probe = probes[0].Label, Metric = metric },
        };
        var leading = WspSource.LeadingAxes(loaded, spec)!;
        trace.Slice = TraceRowViewModel.BuildDefaultSlice(leading, TraceRowViewModel.DefaultXAxis(leading));

        var plot = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var insp = new PlotInspectorViewModel(plot, () => { }, library: library);
        insp.RebuildAndNotify();
        return (insp, insp.Traces.Single());
    }

    // ══ the section, and what it lists ═══════════════════════════════════════

    [Fact]
    public async Task TheSectionAppearsForAProbeMetric_AndListsTheRunsProbesWithTheirIdx()
    {
        var (_, row) = await CardOn("series_resonator.cnl", WspMetric.InvY0, PlotType.Polar);

        Assert.True(row.ShowWspSection);
        string item = Assert.Single(row.WspProbeItems);
        Assert.Contains("P", item);
        Assert.Contains("idx 1", item);       // the document's own 1-based index, shown beside it
        Assert.Equal(item, row.SelectedWspProbeItem);

        // The two pickers only some metrics read are hidden, not shown inert.
        Assert.False(row.ShowWspWith);
        Assert.False(row.ShowWspSet);
        Assert.False(row.ShowWspZ0);
    }

    [Fact]
    public async Task APairMetricShowsTheSecondPicker_AndAZ0Field()
    {
        var (_, row) = await CardOn("two_block.cnl", WspMetric.LGM, PlotType.Polar);
        Assert.True(row.ShowWspWith);
        Assert.True(row.ShowWspZ0);           // the pair blocks are scattered at it (Eq. 142/143)
        Assert.False(row.ShowWspSet);
        Assert.True(row.WspProbeItems.Count >= 2);
    }

    /// <summary>
    /// The metric list is offered in the ordinary item picker, in its own group, with every metric
    /// present — the ones that do not fit the plot type DISABLED WITH A REASON rather than absent.
    /// </summary>
    [Theory]
    [InlineData(PlotType.Rect)]
    [InlineData(PlotType.Polar)]
    [InlineData(PlotType.Smith)]
    public async Task EveryMetricIsInThePicker_DisabledWithAReasonWhereItDoesNotFit(PlotType plotType)
    {
        var (_, row) = await CardOn("series_resonator.cnl", WspMetric.SM_Y0, plotType);

        // Two groups mention the probe: the metric list, and the virtual reduced two-port that
        // R-wsp4-8 adds as an ordinary NETWORK source. The metric list is the one that ENDS in the
        // probe section's own name.
        string group = Assert.Single(
            row.AvailableGroups, g => g.EndsWith("\u25b8 WSProbe", StringComparison.Ordinal));
        row.SelectedGroup = group;
        output.WriteLine($"{plotType}: {row.AvailableSignals.Count} items in '{group}'");

        Assert.Equal(WspMetrics.All.Count, row.AvailableSignals.Count);
        foreach (var info in WspMetrics.All)
        {
            var item = Assert.Single(row.AvailableSignals, s => s.WspMetric == info.Metric);
            string? why = WspMetrics.DisabledReasonOn(info.Metric, plotType);
            Assert.Equal(why is null, item.IsEnabled);
            Assert.Equal(why, item.DisabledReason);
            if (why is not null) Assert.Equal(why, item.TooltipText);
        }
    }

    // ══ the readout and "Mark crossings" ═════════════════════════════════════

    /// <summary>
    /// R-wsp4-14f's second half: the button places exactly one marker per reported frequency, and
    /// only once — pressing it again on a trace that is already marked adds nothing, because the
    /// frequencies have not changed and a second marker at the same place is not a second reading.
    /// </summary>
    [Fact]
    public async Task MarkCrossings_PlacesOneMarkerPerReportedFrequency()
    {
        var (_, row) = await CardOn("series_resonator.cnl", WspMetric.InvY0, PlotType.Polar);

        output.WriteLine(row.WspReadoutText);
        Assert.True(row.ShowWspReadout);
        Assert.Contains("1.591", row.WspReadoutText);
        Assert.True(row.CanMarkWspCrossings);

        row.MarkWspCrossingsCommand.Execute(null);
        var m = Assert.Single(row.Trace.Markers);
        Assert.InRange(m.Freq, 1.585e9, 1.598e9);

        row.MarkWspCrossingsCommand.Execute(null);
        Assert.Single(row.Trace.Markers);
    }

    /// <summary>A reading of "none" is a real answer, and leaves the button with nothing to do —
    /// disabled, with a sentence saying so, rather than placing a marker at an invented
    /// frequency.</summary>
    [Fact]
    public async Task ARouteWithNoCrossings_LeavesTheButtonDisabled()
    {
        var (_, row) = await CardOn("series_resonator.cnl", WspMetric.InvH0, PlotType.Polar);

        Assert.Contains("none", row.WspReadoutText);
        Assert.False(row.CanMarkWspCrossings);
        Assert.Contains("Nothing was reported", row.MarkWspCrossingsTooltip);

        row.MarkWspCrossingsCommand.Execute(null);
        Assert.Empty(row.Trace.Markers);
    }

    // ══ the spec box ═════════════════════════════════════════════════════════

    /// <summary>
    /// The card names a probe trace the way a <c>measure</c> line would (overview D-5) — and
    /// re-committing that text unchanged, which a LostFocus does on every tab through the box, must
    /// leave the trace exactly as it was.
    /// </summary>
    [Fact]
    public async Task TheSpecBoxShowsTheAccessorForm_AndRecommittingItChangesNothing()
    {
        var (_, row) = await CardOn("series_resonator.cnl", WspMetric.SM_Y0);

        string shown = row.SpecShorthand;
        output.WriteLine(shown);
        Assert.Contains("wsp_SM_Y0(", shown);
        Assert.Contains("idx(\"P\")", shown);

        row.CommitSpec(shown);
        Assert.True(row.Trace.IsWspTrace);
        Assert.Equal(WspMetric.SM_Y0, row.Trace.Wsp!.Metric);
        Assert.Equal(shown, row.SpecShorthand);
    }

    /// <summary>A genuine edit gives the probe metric up and becomes what was typed — the card must
    /// not keep computing a probe quantity behind a spec that names something else.</summary>
    [Fact]
    public async Task TypingSomethingElseIntoTheSpecBox_GivesUpTheProbeMetric()
    {
        var (_, row) = await CardOn("series_resonator.cnl", WspMetric.SM_Y0);
        row.CommitSpec("mag(Y0:P)");

        Assert.False(row.Trace.IsWspTrace);
        Assert.Null(row.Trace.Wsp);
    }
}
