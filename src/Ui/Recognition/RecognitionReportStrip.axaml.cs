using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Recognition;

/// <summary>The report strip of the Create … from … dialog (R-as8-2, R-im5-1): the same control for every source.</summary>
public partial class RecognitionReportStrip : UserControl
{
    private RecognitionSessionViewModel? _vm;

    public RecognitionReportStrip() => InitializeComponent();

    /// <summary>The most the strip grows to before it scrolls.</summary>
    public double StripMaxHeight
    {
        get => Strip.MaxHeight;
        set => Strip.MaxHeight = value;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.ReportLines.CollectionChanged -= OnLinesChanged;
        _vm = DataContext as RecognitionSessionViewModel;
        if (_vm is not null) _vm.ReportLines.CollectionChanged += OnLinesChanged;
    }

    /// <summary>A new report arrives collapsed: let the strip fit it again.</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Strip.Height = double.NaN;

    /// <summary>
    /// Expanding a report line must not grow the strip: in its Auto row it would take the height from the parts table
    /// above and its top edge would climb until it reached its MaxHeight. Pinning it at its present height before the
    /// layout pass the toggle causes makes the expansion scroll inside it instead.
    /// </summary>
    private void OnExpandClick(object? sender, RoutedEventArgs e)
    {
        if (double.IsNaN(Strip.Height)) Strip.Height = Strip.Bounds.Height;
    }

    private void OnAnchorPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecognitionReportAnchorViewModel a) _vm?.SelectAnchor(a.Anchor);
    }
}
