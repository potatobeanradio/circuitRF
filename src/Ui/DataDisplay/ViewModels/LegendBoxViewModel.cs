// ================================================================
//  LegendBoxViewModel.cs
//
//  A plot's FLOATING legend (LegendPlacement.Floating): the Inside box
//  dragged out of the plot onto the Data Display canvas, placed and
//  moved exactly as a marker info box is. One per container whose plot
//  floats its legend, owned by DataDisplayViewModel. The position is
//  written back to Plot.LegendPos.
// ================================================================

using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed partial class LegendBoxViewModel : CanvasBoxViewModel
{
    public Plot Plot => Container.PlotVM.Plot;

    public LegendBoxViewModel(PlotContainerViewModel container, DataDisplayViewModel parent,
                              double logicalLeft, double logicalTop)
        : base(container, parent, logicalLeft, logicalTop)
        => RefreshSize();

    protected override void CommitPosition(double left, double top) => Plot.LegendPos = new PlotPoint(left, top);

    /// <summary>The line-width unit of the plot's own canvas, in logical units — what the docked legend was measured at,
    /// so a legend lifted out of the plot keeps the size it had.</summary>
    public double Lw => LegendRenderer.LineWidthFor(Container.CanvasWidthLogical, Container.ViewHeight / ZoomOrOne);

    private double ZoomOrOne => _parent.ZoomLevel > 0 ? _parent.ZoomLevel : 1.0;

    public System.Func<Trace, string?>? AliasFor =>
        _parent.Library is { } lib ? t => lib.AliasFor(t.EffectiveSourcePath) : null;

    public bool AlwaysShowSource =>
        AppSettingsViewModel.Instance.EffectiveShowFilePrefix(_parent.Library?.HasMultipleSources ?? false);

    public RenderTheme Theme => Container.Theme;

    /// <summary>Re-measures the box — a trace added, removed or renamed changes it.</summary>
    public void RefreshSize()
    {
        var (w, h) = LegendRenderer.MeasureFloating(Plot, Lw, AliasFor, AlwaysShowSource);
        SetLogicalSize(w, h);
        OnPropertyChanged(nameof(NeedsRedraw));
    }

    /// <summary>Dummy property — PropertyChanged on this triggers InvalidateVisual in the view.</summary>
    public int NeedsRedraw { get; private set; }
}
