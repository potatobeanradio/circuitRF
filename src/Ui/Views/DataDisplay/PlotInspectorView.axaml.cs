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

    /// <summary>True when this view is a flyout's content, which shows the ✕ in its corner. The view model is shared
    /// with the Properties panel, so this is a fact about the VIEW, set by the host that made it.</summary>
    public bool IsFlyout
    {
        get => FlyoutCloseButton.IsVisible;
        set { FlyoutCloseButton.IsVisible = value; UpdateCloseRoom(); }
    }

    /// <summary>The ✕ shares the top line with the centred plot-type row. A plot of fixed type hides that row, and
    /// the next one has buttons at its right end, so the rows step down by the button's height.</summary>
    /// <remarks>A flyout is sized to its content, and the Labels and Axes sections are narrower than Traces: switching
    /// to one shrank the panel under the pointer and brought the ✕ down onto the plot-type row's Help button. As a flyout
    /// the rows are therefore never narrower than the Traces section (<see cref="FlyoutMinWidth"/>), nor than the
    /// widest they have been while it is open (<see cref="OnRowsSizeChanged"/>): the panel keeps its width across
    /// sections, and the ✕ its room.</remarks>
    private void UpdateCloseRoom()
    {
        if (InspectorRows is null || FlyoutCloseButton is null) return;   // before InitializeComponent
        InspectorRows.Margin = IsFlyout && _vm is { CanChangePlotType: false }
            ? new Thickness(0, FlyoutCloseButton.Height + 2, 0, 0)
            : default;
        InspectorRows.MinWidth = IsFlyout ? Math.Max(InspectorRows.MinWidth, FlyoutMinWidth) : 0;
        InspectorRows.SizeChanged -= OnRowsSizeChanged;
        if (IsFlyout) InspectorRows.SizeChanged += OnRowsSizeChanged;
    }

    /// <summary>The Traces section's width as a flyout (a trace card's fields and the Freq/legend row), DIPs.</summary>
    private const double FlyoutMinWidth = 277;

    private void OnRowsSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > InspectorRows.MinWidth) InspectorRows.MinWidth = e.NewSize.Width;
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
        UpdateCloseRoom();
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
                if (tb.IsVisible && ReferenceEquals(tb.DataContext, targetVm) && tb.Name != "TraceLabelEdit")
                {
                    tb.Focus();
                    tb.SelectAll();
                    return;
                }
            }
        }, DispatcherPriority.Render);
    }

    /// <summary>Focuses trace <paramref name="traceIndex"/>'s Label box with its text selected — the legend
    /// entry's Rename… (DFR27-3).</summary>
    public void FocusTraceLabel(int traceIndex)
    {
        if (traceIndex < 0 || _vm is null) return;
        ScrollToTrace(traceIndex);

        Dispatcher.UIThread.Post(() =>
        {
            var targetVm = _vm.Traces.ElementAtOrDefault(traceIndex);
            if (targetVm is null) return;
            foreach (var tb in this.GetVisualDescendants().OfType<TextBox>())
            {
                if (tb.Name == "TraceLabelEdit" && tb.IsVisible && ReferenceEquals(tb.DataContext, targetVm))
                {
                    tb.Focus();
                    tb.SelectAll();
                    return;
                }
            }
        }, DispatcherPriority.Render);
    }

    // ---- Spec editor event handlers (#4) ------------------------------------

    // The spec TextBox is OneWay-bound to SpecShorthand. A focused TextBox does NOT pick up a model
    // change made via the transform combo, so committing its (stale) text on LostFocus would overwrite
    // the combo's change. Track the value at focus time and commit ONLY if the user actually edited it;
    // otherwise re-sync the box to the current model (which the combo may have moved).
    private string _specPristine = "";

    /// <summary>The trace card's Statistics menu (brief-yield-8 R-ya8-2), built from the card's view model on each
    /// click — its entries depend on the trace and its source.</summary>
    private void OnStatisticsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || anchor.DataContext is not TraceRowViewModel vm) return;
        var entries = vm.StatisticsMenu();
        if (entries.Count == 0) return;
        var menu = new ContextMenu();
        foreach (var entry in entries) menu.Items.Add(MenuItemOf(entry));
        menu.Open(anchor);

        static MenuItem MenuItemOf(StatisticsMenuEntry entry)
        {
            var item = new MenuItem
            {
                Header = entry.Header,
                Icon   = entry.Checked
                    ? new Material.Icons.Avalonia.MaterialIcon { Kind = Material.Icons.MaterialIconKind.Check, Width = 12, Height = 12 }
                    : null,
            };
            if (entry.Children is { } children)
                foreach (var c in children) item.Items.Add(MenuItemOf(c));
            else if (entry.Run is { } run)
                item.Click += (_, _) => run();
            else
                item.IsEnabled = false;
            if (entry.Tooltip is { } tip)
            {
                // The reason an entry is refused (brief-yield-9: an envelope on a Smith or Polar plot) — readable on
                // the greyed item itself.
                ToolTip.SetTip(item, tip);
                ToolTip.SetShowOnDisabled(item, true);
            }
            return item;
        }
    }

    private void OnSpecEditGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) _specPristine = tb.Text ?? "";
    }

    private void OnSpecEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TraceRowViewModel vm) return;
        if ((tb.Text ?? "") != _specPristine)
            vm.CommitSpec(tb.Text ?? "");        // user edited the text → commit it
        else
            tb.Text = vm.SpecShorthand;          // unedited → re-sync to model (the combo may have changed it)
    }

    private void OnSpecEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.DataContext is TraceRowViewModel vm)
        {
            vm.CommitSpec(tb.Text ?? "");
            _specPristine = vm.SpecShorthand;    // committed — this is the new pristine baseline
            e.Handled = true;
        }
    }

    // ---- Label box (DFR27-3): committed on Enter or focus loss, one undo step per commit ----

    private void OnTraceLabelLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TraceRowViewModel vm) CommitTraceLabel(tb, vm);
    }

    private void OnTraceLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.DataContext is TraceRowViewModel vm)
        {
            CommitTraceLabel(tb, vm);
            e.Handled = true;
        }
    }

    private static void CommitTraceLabel(TextBox tb, TraceRowViewModel vm)
    {
        vm.CommitTraceLabel(tb.Text);
        tb.Text = vm.TraceLabel;   // the committed spelling — trimmed, or empty for the generated name
    }
}
