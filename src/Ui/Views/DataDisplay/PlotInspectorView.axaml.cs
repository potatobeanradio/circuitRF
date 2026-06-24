using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class PlotInspectorView : UserControl
{
    private ScrollViewer? _traceScrollViewer;
    private PlotInspectorViewModel? _vm;

    public PlotInspectorView()
    {
        InitializeComponent();
        _traceScrollViewer = this.FindControl<ScrollViewer>("TraceScrollViewer");
    }

    // Plot-type Help: open the Reference Guide's Plot Types chapter at the current type.
    private void OnPlotTypeHelp(object? sender, RoutedEventArgs e)
    {
        string? anchor = DataContext is PlotInspectorViewModel vm
            ? (vm.IsSmithPlot ? "smith" : vm.IsPolarPlot ? "polar" : vm.IsTablePlot ? "table" : "rectangular")
            : null;
        DocLauncher.OpenPlotType(anchor);
    }

    /// <summary>
    /// Subscribe to the new ViewModel's Traces collection whenever the DataContext changes,
    /// and unsubscribe from the old one to avoid memory leaks.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
            _vm.Traces.CollectionChanged -= OnTracesCollectionChanged;

        _vm = DataContext as PlotInspectorViewModel;

        if (_vm is not null)
            _vm.Traces.CollectionChanged += OnTracesCollectionChanged;
    }

    /// <summary>
    /// Scrolls to the end when a new trace card is added so the user immediately
    /// sees the newly created trace without having to scroll manually.
    /// </summary>
    private void OnTracesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToEnd();
    }

    /// <summary>
    /// Scrolls the trace list so the card at <paramref name="traceIndex"/> is visible.
    /// Called from PlotControl after the flyout is shown, posted at Loaded priority
    /// so the ScrollViewer knows its extents before the offset is set.
    /// </summary>
    public void ScrollToTrace(int traceIndex)
    {
        if (traceIndex <= 0 || _traceScrollViewer is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            const double EstimatedCardHeight = 92.0;
            _traceScrollViewer.Offset = new Vector(0, traceIndex * EstimatedCardHeight);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Scrolls the trace list to the bottom of the list.
    /// </summary>
    public void ScrollToEnd()
    {
        if (_traceScrollViewer is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            _traceScrollViewer?.Offset = new Vector(0, _traceScrollViewer!.Extent.Height);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Opens the inspector scrolled to the given trace and focuses its spec TextBox.
    /// Interim implementation of the Table trace-header double-click (brief §5):
    /// routes to the inline spec editor instead of just scrolling to the card.
    /// </summary>
    public void FocusSpecTextBox(int traceIndex)
    {
        if (traceIndex < 0 || _vm is null) return;
        ScrollToTrace(traceIndex);

        Dispatcher.UIThread.Post(() =>
        {
            var targetVm = _vm.Traces.ElementAtOrDefault(traceIndex);
            if (targetVm is null) return;
            foreach (var tb in this.GetVisualDescendants().OfType<TextBox>())
            {
                if (tb.IsVisible && ReferenceEquals(tb.DataContext, targetVm))
                {
                    tb.Focus();
                    tb.SelectAll();
                    return;
                }
            }
        }, DispatcherPriority.Render);
    }

    // ---- Spec editor event handlers (#4) ------------------------------------

    private void OnSpecEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TraceRowViewModel vm)
            vm.CommitSpec(tb.Text ?? "");
    }

    private void OnSpecEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.DataContext is TraceRowViewModel vm)
        {
            vm.CommitSpec(tb.Text ?? "");
            e.Handled = true;
        }
    }
}
