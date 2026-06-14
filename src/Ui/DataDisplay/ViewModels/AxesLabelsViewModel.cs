using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class AxesLabelsViewModel : ViewModelBase
{
    private readonly Plot  _plot;
    private readonly Action _closeAction;

    public event EventHandler? PlotNeedsRedraw;

    [ObservableProperty] private bool   _titleOn;
    [ObservableProperty] private string _title    = "";
    [ObservableProperty] private bool   _xLabelOn;
    [ObservableProperty] private string _xLabel   = "";
    [ObservableProperty] private bool   _yLabelOn;
    [ObservableProperty] private string _yLabel   = "";
    [ObservableProperty] private bool   _y2LabelOn;
    [ObservableProperty] private string _y2Label  = "";

    public AxesLabelsViewModel(Plot plot, Action closeAction)
    {
        _plot        = plot;
        _closeAction = closeAction;

        _titleOn   = plot.CustomTitleOn;
        _title     = plot.CustomTitle;
        _xLabelOn  = plot.CustomXLabelOn;
        _xLabel    = plot.CustomXLabel;
        _yLabelOn  = plot.CustomYLabelOn;
        _yLabel    = plot.CustomYLabel;
        _y2LabelOn = plot.CustomY2LabelOn;
        _y2Label   = plot.CustomY2Label;
    }

    partial void OnTitleOnChanged(bool value)    => Sync();
    partial void OnTitleChanged(string value)    => Sync();
    partial void OnXLabelOnChanged(bool value)   => Sync();
    partial void OnXLabelChanged(string value)   => Sync();
    partial void OnYLabelOnChanged(bool value)   => Sync();
    partial void OnYLabelChanged(string value)   => Sync();
    partial void OnY2LabelOnChanged(bool value)  => Sync();
    partial void OnY2LabelChanged(string value)  => Sync();

    private void Sync()
    {
        _plot.CustomTitleOn   = TitleOn;
        _plot.CustomTitle     = Title;
        _plot.CustomXLabelOn  = XLabelOn;
        _plot.CustomXLabel    = XLabel;
        _plot.CustomYLabelOn  = YLabelOn;
        _plot.CustomYLabel    = YLabel;
        _plot.CustomY2LabelOn = Y2LabelOn;
        _plot.CustomY2Label   = Y2Label;
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => _closeAction();
}
