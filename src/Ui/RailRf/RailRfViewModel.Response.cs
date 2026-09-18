// The |Z| plot's host (railrf.md §11.1: "Plots are PlotControls in rectangular mode, fed from a
// DataSet — never a bespoke chart").

using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// The plot host — one <see cref="DataDisplayViewModel"/> with exactly one container in it, laid
    /// out by this window rather than by a canvas.
    /// </summary>
    /// <remarks>
    /// <b>A bare <c>PlotControl</c> is not enough, and the Match Designer already paid for finding
    /// that out</b>: a <c>PlotControl</c> asks its HOST for the marker index, the info-box view model,
    /// the selected markers and the container to export, and a host that is null answers "nothing" to
    /// all four — silently. So the markers, the info boxes, the theme and the clipboard here are the
    /// Data Display's own, and this window only decides where the box sits.
    ///
    /// <para><b>It is not a Data Display document.</b> Nothing is persisted, no datasource library is
    /// loaded into it, and the plot cannot be added to or deleted.</para>
    ///
    /// <para><b>Brief 12 fills it.</b> What is here is the host and the container; the |Z| trace, the
    /// mask shading and the aggressor lines are brief 12's, built from a <c>DataSet</c> — which is why
    /// there is no drawing code anywhere in this view model.</para>
    /// </remarks>
    public DataDisplayViewModel PlotHost { get; } =
        new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>The container holding <see cref="ImpedancePlot"/>.</summary>
    public PlotContainerViewModel ImpedanceContainer { get; private set; } = null!;

    /// <summary>|Z| against frequency, with its mask and the aggressor lines.</summary>
    public Plot ImpedancePlot => ImpedanceContainer.PlotVM.Plot;

    /// <summary>Seed logical width of the plot — the view re-sizes it to the pane.</summary>
    public const double PlotWidth = 320.0;

    /// <summary>Seed logical height, at the golden ratio the Data Display's own new plots open at.</summary>
    public const double PlotHeight = PlotWidth / 1.618;

    private void BuildPlotHost()
    {
        ImpedanceContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.MHz,
                                              left: 0, top: 0, width: PlotWidth, height: PlotHeight);

        // AXES PANNING STARTS LOCKED, for the Match Designer's own reason: this plot is a read-out of
        // a design being edited underneath it, every committed edit re-solves and autoscales, and a
        // user who had dragged the window off the trace would see an empty plot and read it as a
        // broken design. The menu item still toggles it.
        ImpedancePlot.Axes.LockedPanning = true;
        PlotHost.SelectOnly((PlotContainerViewModel?)null);
    }
}
