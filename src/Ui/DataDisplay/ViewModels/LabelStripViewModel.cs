using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// Wraps one trace for a Y-axis label strip binding.
/// StripWidth and Theme are kept in sync by PlotContainerViewModel
/// when zoom level or theme changes, updating the bindings in place
/// so the ItemsControl does not need to rebuild its items.
/// </summary>
public partial class LabelStripViewModel : ObservableObject
{
    public Trace Trace      { get; }
    public bool  IsRightSide { get; }

    [ObservableProperty] private double      _stripWidth;
    [ObservableProperty] private RenderTheme _theme;
    [ObservableProperty] private int         _appearanceRevision;

    /// <summary>
    /// When non-null, overrides the trace description with a user-defined label.
    /// AxisLabelControl renders this text in the theme colour instead of the trace colour.
    /// </summary>
    [ObservableProperty] private string? _customLabel;

    /// <summary>
    /// When true (default), the label includes the source filename prefix.
    /// Set to false when the plot contains only one data source so the prefix
    /// adds no information — mirrors <see cref="Trace.ShortDescription"/>.
    /// </summary>
    [ObservableProperty] private bool _showFilePrefix = true;

    public LabelStripViewModel(Trace trace, bool isRightSide, double stripWidth, RenderTheme theme)
    {
        Trace       = trace;
        IsRightSide = isRightSide;
        _stripWidth = stripWidth;
        _theme      = theme;
    }
}
