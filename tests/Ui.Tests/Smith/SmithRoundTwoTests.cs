using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The second round of owner items over the finished tool (<c>docs/design/smith-chart.md</c> §9.2).
/// <b>One test per claim</b>, and only the claims whose failure would be silent.
/// </summary>
/// <remarks>
/// The three items with no test here are the ones whose evidence is a pixel or a dialog — the square
/// toolbar buttons, the component glyphs on the menu rows, and the Save picker's suggested name,
/// which is built inside a <c>SaveFilePickerAsync</c> call that cannot run headless. Each is one
/// expression, and each fails visibly the moment the window is opened.
/// </remarks>
public sealed class SmithRoundTwoTests
{
    private const double DesignHz = 2.0e9;

    private static SmithDesign Design(params SmithElement[] elements)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = 50.0;
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 12.0, -8.5));
        foreach (var e in elements) d.Elements.Add(e);
        return d;
    }

    private static SmithElement SeriesL(double h) => new()
    {
        Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L1",
        Values = { LHenry = h },
    };

    // ═════════════════════════════════════════════════════════════════════════
    //  1. A slider's range does not move when the value reaches its end
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Dragging the thumb to the top leaves the top where it was.</b>
    /// </summary>
    /// <remarks>
    /// This is the runaway, stated as the property that rules it out rather than as the symptom. The
    /// old rule derived the range from the current value, so each write moved the ceiling up and the
    /// slider never ran out of travel — an owner-reported series L reached 9999999 H. Twenty writes
    /// is far more than it took to get there.
    /// </remarks>
    [Fact]
    public void DraggingASliderToItsMaximumDoesNotRaiseTheMaximum()
    {
        var vm = new SmithChartViewModel(Design(SeriesL(3.3e-9)));
        vm.SelectElement(0);

        var row = Assert.Single(vm.SliderRows);
        double ceiling = row.Range.Max;

        for (int i = 0; i < 20; i++)
        {
            row.Position = row.Maximum;          // what a thumb at the right end writes
            Assert.Equal(ceiling, row.Range.Max, 15);
        }

        // …and the value it reached is that ceiling, not something ten decades past it.
        Assert.Equal(ceiling, row.Value, 15);
    }

    /// <summary>
    /// <b>An inductor opens on 0 … 10 nH and a series capacitor on 0.1 pF … 1000 pF</b> — the owner's
    /// own defaults, quoted for the 2 GHz design frequency this tool opens on.
    /// </summary>
    /// <remarks>
    /// The second half of each row is the axis, which is a consequence rather than a separate
    /// setting: a range that reaches zero cannot be logarithmic, and one spanning four decades has to
    /// be. That is why <c>IsLogarithmic</c> reads the range and not the parameter.
    /// </remarks>
    [Theory]
    [InlineData(SmithElementKind.L, SmithPlacement.Series, 0.0,      10e-9,   false)]
    [InlineData(SmithElementKind.L, SmithPlacement.Shunt,  0.0,      10e-9,   false)]
    [InlineData(SmithElementKind.C, SmithPlacement.Shunt,  0.0,      10e-12,  false)]
    [InlineData(SmithElementKind.C, SmithPlacement.Series, 0.1e-12,  1000e-12, true)]
    public void AParametersRangeIsTheOneQuotedForTheDesignFrequency(
        SmithElementKind kind, SmithPlacement placement, double min, double max, bool log)
    {
        var element = new SmithElement { Kind = kind, Placement = placement, Name = "E1" };
        if (kind == SmithElementKind.L) element.Values.LHenry = 2e-9;
        else                            element.Values.CFarad = 1e-12;

        var vm = new SmithChartViewModel(Design(element));
        vm.SelectElement(0);

        var row = Assert.Single(vm.SliderRows);
        Assert.Equal(min, row.Range.Min, 15);
        Assert.Equal(max, row.Range.Max, 15);
        Assert.Equal(log, row.IsLogarithmic);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. The admittance grid is the document's, and survives the round trip
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The flag reaches the plot from the design, comes back through the capture, and survives a
    /// <c>.csmith</c> round trip.</b>
    /// </summary>
    /// <remarks>
    /// All three halves matter and each fails silently on its own: a flag that never reached the plot
    /// draws nothing, a plot toggled from its context menu with nothing harvesting it is lost on
    /// save, and a field absent from <c>SmithDesignIo</c> is lost on reopen. The toggle itself is
    /// <c>PlotControl</c>'s menu row, which needs a window; what it writes is
    /// <c>Plot.ShowSmithAdmittanceGrid</c>, which is what is set here.
    /// </remarks>
    [Fact]
    public void TheAdmittanceGridReachesThePlotAndSurvivesASave()
    {
        var vm = new SmithChartViewModel(Design(SeriesL(3.3e-9)));
        Assert.False(vm.ChartPlot.ShowSmithAdmittanceGrid);

        // What the context menu does, and then what PlotChanged does with it.
        vm.ChartPlot.ShowSmithAdmittanceGrid = true;
        vm.CaptureChartWindow();

        string text  = SmithDesignIo.SerializeUnvalidated(vm.Design);
        var    back  = SmithDesignIo.Deserialize(text);
        Assert.True(back.Chart.ShowAdmittanceGrid);

        // …and reopening it puts the flag back on the plot, which is the half a Fill must do.
        var reopened = new SmithChartViewModel(back);
        Assert.True(reopened.ChartPlot.ShowSmithAdmittanceGrid);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. Markers are placed freely
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A freely-placed marker resolves to where it was put, whatever trace it is stored on.</b>
    /// </summary>
    /// <remarks>
    /// The point of the claim is the SECOND assertion: the same marker without the flag resolves
    /// against the trace instead, which is what every marker did before and what a Data Display
    /// marker must keep doing. If <c>GetMarkerDataLocation</c>'s new early return were unguarded, the
    /// two would be equal and the test would say so.
    /// </remarks>
    [Fact]
    public void AFreeMarkerResolvesToItsOwnPositionAndABoundOneDoesNot()
    {
        var vm    = new SmithChartViewModel(Design(SeriesL(3.3e-9)));
        var trace = vm.ChartPlot.Traces.First(t => t.Points.Count > 1);

        var at = new Vector2(0.37f, -0.21f);
        var m  = new Marker(trace, 0.0, false, false, 1) { FreePosition = true, PositionStatic = at };

        Assert.Equal(at, trace.GetMarkerDataLocation(m));

        m.FreePosition = false;
        Assert.NotEqual(at, trace.GetMarkerDataLocation(m));
    }

    /// <summary>
    /// <b>A <c>.csmith</c> keeps a free marker free and keeps it where it was.</b>
    /// </summary>
    /// <remarks>
    /// A dropped <c>FreePosition</c> would not lose the marker — it would silently re-bind it to the
    /// curve it happens to be stored on, which moves it somewhere plausible. That is the failure mode
    /// worth a test.
    /// </remarks>
    [Fact]
    public void AFreeMarkerSurvivesTheCsmithRoundTrip()
    {
        var design = Design(SeriesL(3.3e-9));
        design.Markers.Add(new SmithMarker
        {
            TraceName = "L1", Name = "m1", Index = 1,
            FreePosition = true, PositionStaticX = 0.37f, PositionStaticY = -0.21f,
        });

        var back = SmithDesignIo.Deserialize(SmithDesignIo.SerializeUnvalidated(design));
        var m    = Assert.Single(back.Markers);

        Assert.True(m.FreePosition);
        Assert.Equal(0.37f, m.PositionStaticX, 5);
        Assert.Equal(-0.21f, m.PositionStaticY, 5);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. One frequency draws no load-point label
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A chart with one generator row carries no frequency label; adding a second row brings the
    /// labels back.</b>
    /// </summary>
    /// <remarks>
    /// Rendered rather than asserted on a flag, because the label is drawn by
    /// <see cref="SmithChartChrome"/> and the whole claim is about what reaches the picture. The
    /// second half is what keeps the first from passing for the wrong reason — a labelling path
    /// broken outright would also produce an SVG with no frequency in it.
    /// </remarks>
    [Fact]
    public void OneFrequencyDrawsNoLoadPointLabelAndTwoDo()
    {
        string f = SmithPlotBuilder.FrequencyLabel(DesignHz);

        var one = new SmithChartViewModel(Design(SeriesL(3.3e-9)));
        one.ChartContainer.Overlay = one.ChartOverlay;
        Assert.DoesNotContain(f, PlotExporter.BuildSvgStringForContainers(
            [one.ChartContainer], RenderTheme.Light), StringComparison.Ordinal);

        var twoRows = Design(SeriesL(3.3e-9));
        twoRows.Generator.Rows.Add(new SmithGeneratorRow(DesignHz * 1.1, 11.0, -9.4));
        var two = new SmithChartViewModel(twoRows);
        two.ChartContainer.Overlay = two.ChartOverlay;
        Assert.Contains(f, PlotExporter.BuildSvgStringForContainers(
            [two.ChartContainer], RenderTheme.Light), StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. The constant-Q value is part of the picture
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The Q value is drawn on the chart, so it is in an exported one.</b>
    /// </summary>
    /// <remarks>
    /// This is the owner's instruction stated as the thing that would otherwise be lost: a value
    /// rendered by the window's panel is absent from every copy, export and headless render, and the
    /// picture still looks correct without it. Drawing it in <see cref="SmithChartChrome"/> — below
    /// the firewall — is what puts it in the bytes.
    /// </remarks>
    [Fact]
    public void TheConstantQValueIsDrawnIntoTheExportedPicture()
    {
        var design = Design(SeriesL(3.3e-9));
        design.ConstantQ.Enabled = true;
        design.ConstantQ.Q       = 1.75;

        var vm = new SmithChartViewModel(design);
        vm.ChartContainer.Overlay = vm.ChartOverlay;

        // THE WHOLE LABEL, not the number on its own. A bare "1.75" is also SVG path data — an
        // exported chart is full of "Q381.753 343.596" quadratic segments — so the number alone
        // passes on a picture that draws no label at all, which is exactly the failure this test is
        // for. Caught by writing the negative half first. The equals sign is the owner's, 2026-09-19.
        string svg = PlotExporter.BuildSvgStringForContainers([vm.ChartContainer], RenderTheme.Light);
        Assert.Contains("Q=1.75", svg, StringComparison.Ordinal);

        design.ConstantQ.Enabled = false;
        vm.RebuildChart();
        Assert.DoesNotContain("Q=1.75", PlotExporter.BuildSvgStringForContainers(
            [vm.ChartContainer], RenderTheme.Light), StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. A refill does not disturb the window
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Refilling the plot's traces with the autoscale suppressed leaves the window exactly where
    /// it was.</b>
    /// </summary>
    /// <remarks>
    /// This is the drag glitch, as the property that rules it out. <c>Fill</c>'s
    /// <c>autoscale: false</c> used to guard only the window assignment at the bottom of the method,
    /// while the Clear-and-refill above it raised <c>CollectionChanged</c> once per trace and each of
    /// those autoscaled — including the one on the empty plot the Clear leaves behind. So a drag
    /// re-fitted the user's own framing underneath the hand holding it, occasionally and invisibly.
    /// </remarks>
    [Fact]
    public void ARefillWithTheAutoscaleSuppressedLeavesTheWindowAlone()
    {
        var design = Design(SeriesL(3.3e-9));
        var vm     = new SmithChartViewModel(design);

        var window = new PlotRect(-0.4, -0.3, 0.8, 0.6);
        vm.ChartPlot.Axes.Window = window;

        var scene = SmithPlotBuilder.BuildScene(design, null, (600.0, 600.0), window);
        SmithPlotBuilder.Fill(vm.ChartPlot, scene, design, autoscale: false);

        Assert.Equal(window.X,      vm.ChartPlot.Axes.Window.X,      12);
        Assert.Equal(window.Y,      vm.ChartPlot.Axes.Window.Y,      12);
        Assert.Equal(window.Width,  vm.ChartPlot.Axes.Window.Width,  12);
        Assert.Equal(window.Height, vm.ChartPlot.Axes.Window.Height, 12);
    }
}
