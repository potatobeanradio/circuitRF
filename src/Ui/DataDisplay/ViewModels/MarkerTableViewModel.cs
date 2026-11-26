using System;
using System.Collections.Generic;
using System.Linq;
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

    public void RevealMarker(Marker marker, Trace trace)
    {
        var rows = Rows;
        float top = 0;
        var row = rows.FirstOrDefault(item => ReferenceEquals(item.Marker, marker));
        if (row is null) return;
        foreach (var item in rows)
        {
            if (ReferenceEquals(item, row)) break;
            top += MarkerTableRenderer.RowHeight(item);
        }
        float rowHeight = MarkerTableRenderer.RowHeight(row);
        var size = MarkerTableRenderer.Measure(rows);
        float viewportHeight = size.H - MarkerTableRenderer.HeaderHeightFor(rows) -
            (MarkerTableRenderer.MaxHorizontalScroll(rows) > 0 ? MarkerTableRenderer.ScrollbarHeight : 0);
        if (top < ScrollOffset || rowHeight > viewportHeight)
            ScrollOffset = top;
        else if (top + rowHeight > ScrollOffset + viewportHeight)
            ScrollOffset = top + rowHeight - viewportHeight;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, MarkerTableRenderer.MaxScroll(rows));

        var columns = MarkerTable.Columns(rows);
        int index = -1;
        for (int column = 0; column < columns.Count; column++)
            if (ReferenceEquals(columns[column].Trace, trace)) { index = column; break; }
        if (index >= 0)
        {
            float left = index * MarkerTableRenderer.ValueColumn;
            float viewportWidth = size.W - MarkerTableRenderer.FixedWidth;
            if (left < HorizontalScrollOffset)
                HorizontalScrollOffset = left;
            else if (left + MarkerTableRenderer.ValueColumn > HorizontalScrollOffset + viewportWidth)
                HorizontalScrollOffset = left + MarkerTableRenderer.ValueColumn - viewportWidth;
            HorizontalScrollOffset = Math.Clamp(HorizontalScrollOffset, 0, MarkerTableRenderer.MaxHorizontalScroll(rows));
        }
        OnPropertyChanged(nameof(NeedsRedraw));
    }

    public void SelectMarker(MarkerTableRow row) => _parent.SelectMarkerFromTable(Container, row.Trace, row.Marker);
    public MarkerInfoBoxViewModel MarkerEditor(MarkerTableRow row) => _parent.GetOrCreateInfoBoxVm(row.Marker, row.Trace, Container);
    public void ToggleBoxes() => _parent.SetMarkerTableBoxes(Container, !Plot.MarkerTableShowInfoBoxes);
    public override bool IsPositionLocked => Plot.MarkerTablePositionLocked;
    public void ToggleLocked() => _parent.SetMarkerTableLocked(Container, !Plot.MarkerTablePositionLocked);
    public void ToggleCard() => _parent.SetMarkerTableCard(Container, !Plot.MarkerTableOnCard);
    public void Close() => _parent.SetMarkerTableVisible(Container, false);
}