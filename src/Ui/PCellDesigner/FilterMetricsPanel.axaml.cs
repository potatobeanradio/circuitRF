// Owner: PCD-12 (brief-pcd-12 R-pcd12-3, D8).

using Avalonia;
using Avalonia.Controls;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.Filters;

namespace CircuitRF.Ui.PCellDesigner;

/// <summary>What <see cref="FilterMetricsPanel"/> shows: the note and the Filter Designer's two tables of rows.</summary>
public sealed record FilterMetricsView(string Note, IReadOnlyList<FilterMetricRow> SpecRows, IReadOnlyList<FilterMetricRow> MatrixRows)
{
    /// <summary>The view of <paramref name="m"/>: its source and how many checks fail, or why there are none; nothing for null.</summary>
    public static FilterMetricsView Of(PCellFilterMetrics? m) => m switch
    {
        null => new("", [], []),
        { Filter: { } x } => new(FilterMetricRow.Note(m.Source, x), FilterMetricRow.SpecRows(x, null), FilterMetricRow.MatrixRows(x)),
        _ => new($"{m.Source}: {m.Refusal}", [], []),
    };
}

/// <summary>
/// A designed filter cell's metrics (<see cref="PCellFilter.CircuitMetrics"/>, or <see cref="PCellFilter.Metrics"/> on an
/// EM result), shown as the Filter Designer shows its own. Bind <see cref="Metrics"/>; nothing else.
/// </summary>
public partial class FilterMetricsPanel : UserControl
{
    public static readonly StyledProperty<PCellFilterMetrics?> MetricsProperty =
        AvaloniaProperty.Register<FilterMetricsPanel, PCellFilterMetrics?>(nameof(Metrics));

    /// <summary>The metrics to show; null shows nothing.</summary>
    public PCellFilterMetrics? Metrics
    {
        get => GetValue(MetricsProperty);
        set => SetValue(MetricsProperty, value);
    }

    public FilterMetricsPanel()
    {
        InitializeComponent();
        Root.DataContext = FilterMetricsView.Of(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MetricsProperty) Root.DataContext = FilterMetricsView.Of(Metrics);
    }
}
