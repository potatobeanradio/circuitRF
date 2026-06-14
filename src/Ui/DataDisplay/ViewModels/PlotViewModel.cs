// ================================================================
//  PlotViewModel.cs  —  ViewModel wrapper for a single Plot
//
//  Responsibilities:
//    • Owns the Plot model
//    • Owns CanvasSize (pixel dimensions — a rendering concern, not a
//      model concern, so it does not belong on Plot)
//    • Exposes derived bindable state for the UI (axis labels, etc.)
//    • Handles PlotControl.PlotChanged and raises property-change
//      notifications so the UI stays in sync
//
//  Usage from AXAML:
//    <controls:PlotControl
//        Plot="{Binding ActivePlot.Plot}"
//        PlotTheme="{Binding CurrentTheme}"
//        PlotChanged="OnPlotChanged" />   ← or bind via command
//
//  Usage from code-behind / MainWindowViewModel:
//    ActivePlot = new PlotViewModel(new Plot(PlotType.Smith, FreqUnit.GHz));
//    ActivePlot.CanvasSize = (800, 600);
// ================================================================

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class PlotViewModel : ViewModelBase
{
    // ---- Model ------------------------------------------------------

    public Plot Plot { get; }

    // ---- Canvas size (owned here, NOT on Plot) ----------------------

    /// <summary>
    /// Canvas size in logical pixels.  Set this from the Avalonia
    /// control's SizeChanged / OnSizeChanged before rendering.
    /// Replaces the removed Plot.CanvasSize property.
    /// </summary>
    [ObservableProperty]
    private (double Width, double Height) _canvasSize;

    // ---- Derived bindable state for the UI -------------------------

    public string XAxisLabel => Plot.XLabel;

    public string Title => Plot.Title;

    public bool HasSecondaryAxis => Plot.NeedsSecondary;
    public bool AxesLockedPanning => Plot.Axes.LockedPanning;

    public string WindowText =>
        $"X: {Plot.Axes.Window.Left:G4}–{Plot.Axes.Window.Right:G4}  " +
        $"Y: {Plot.Axes.Window.Top:G4}–{Plot.Axes.Window.Bottom:G4}";

    // ---- Constructor ------------------------------------------------

    public PlotViewModel(Plot plot)
    {
        Plot = plot ?? throw new ArgumentNullException(nameof(plot));
    }

    // ---- PlotControl.PlotChanged handler ---------------------------

    /// <summary>
    /// Wire this to PlotControl.PlotChanged in code-behind or via an
    /// event-binding helper.  Refreshes all derived bindable state.
    /// </summary>
    public void OnPlotChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(XAxisLabel));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasSecondaryAxis));
        OnPropertyChanged(nameof(WindowText));
        OnPropertyChanged(nameof(AxesLockedPanning));
    }

    // OnCanvasSizeChanged: viewport for complex plots is now computed inside
    // PlotRenderer.BuildTransforms on every frame, so no manual update needed here.
}
