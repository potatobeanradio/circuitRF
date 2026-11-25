using System;
using System.Collections.Generic;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed class MarkerTableViewModel : CanvasBoxViewModel
{
    public Plot Plot => Container.PlotVM.Plot;
    public RenderTheme Theme => Container.Theme;
    public IReadOnlyList<MarkerTableRow> Rows => MarkerTable.Rows(Plot,
        AppSettingsViewModel.Instance.EffectiveShowFilePrefix(_parent.Library?.HasMultipleSources ?? false));
    public double ScrollOffset { get; private set; }
    public double HorizontalScrollOffset { get; private set; }
    public int NeedsRedraw { get; private set; }

    public MarkerTableViewModel(PlotContainerViewModel container, DataDisplayViewModel parent, double left, double top)
        : base(container, parent, left, top) => Refresh();

    protected override void CommitPosition(double left, double top) => Plot.MarkerTablePos = new PlotPoint(left, top);

    public void Refresh()
    {
        var rows = Rows;
        var (width, height) = MarkerTableRenderer.Measure(rows);
        ScrollOffset = Math.Clamp(ScrollOffset, 0, MarkerTableRenderer.MaxScroll(rows));
        HorizontalScrollOffset = Math.Clamp(HorizontalScrollOffset, 0, MarkerTableRenderer.MaxHorizontalScroll(rows));
        SetLogicalSize(width, height);
        OnPropertyChanged(nameof(NeedsRedraw));
    }

    public void Scroll(double delta)
    {
        ScrollOffset = Math.Clamp(ScrollOffset + delta, 0, MarkerTableRenderer.MaxScroll(Rows));
        OnPropertyChanged(nameof(NeedsRedraw));
    }

    public void ScrollHorizontal(double delta)
    {
        HorizontalScrollOffset = Math.Clamp(HorizontalScrollOffset + delta, 0, MarkerTableRenderer.MaxHorizontalScroll(Rows));
        OnPropertyChanged(nameof(NeedsRedraw));
    }

    public void ScrollHorizontalTo(double position)
    {
        var rows = Rows;
        double width = MarkerTableRenderer.Measure(rows).W;
        double track = width - MarkerTableRenderer.FixedWidth;
        double thumb = Math.Max(20, track * track / (MarkerTableRenderer.FullWidth(rows) - MarkerTableRenderer.FixedWidth));
        double fraction = Math.Clamp((position - MarkerTableRenderer.FixedWidth - thumb / 2) / (track - thumb), 0, 1);
        HorizontalScrollOffset = fraction * MarkerTableRenderer.MaxHorizontalScroll(rows);
        OnPropertyChanged(nameof(NeedsRedraw));
    }

    public void SelectMarker(MarkerTableRow row) => _parent.SelectMarkerFromTable(Container, row.Trace, row.Marker);
    public MarkerInfoBoxViewModel MarkerEditor(MarkerTableRow row) => _parent.GetOrCreateInfoBoxVm(row.Marker, row.Trace, Container);
    public void ToggleBoxes() => _parent.SetMarkerTableBoxes(Container, !Plot.MarkerTableShowInfoBoxes);
    public void Close() => _parent.SetMarkerTableVisible(Container, false);
}