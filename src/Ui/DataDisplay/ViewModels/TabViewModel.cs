// ================================================================
//  TabViewModel.cs  —  One tab in a DisplayWindow.
//
//  Each tab owns an independent DataDisplayViewModel (plots, zoom,
//  marker info-boxes, selection state).  The tab also stores a
//  human-readable Name and an IsEditingName flag used by the tab
//  header view to switch between a TextBlock and an editable TextBox.
//
//  GetCanvasSizeFunc is injected by DataDisplayView when the view is
//  loaded, so that DisplayWindowViewModel can ask the active tab for
//  its current canvas pixel size (needed for FitAll).
// ================================================================

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class TabViewModel : ViewModelBase
{
    [ObservableProperty] private string _name         = "Tab 1";
    [ObservableProperty] private bool   _isEditingName = false;

    public DataDisplayViewModel DataDisplay { get; }

    // Set by DataDisplayView.OnLoaded so the window can query canvas size.
    internal Func<(double W, double H)>? GetCanvasSizeFunc { get; set; }

    public (double W, double H) GetCanvasSize()
        => GetCanvasSizeFunc?.Invoke() ?? (800.0, 600.0);

    // Fired when the user clicks the tab's close button.
    // DisplayWindowViewModel subscribes in CreateNewTab and calls RemoveTab.
    public event EventHandler? CloseRequested;

    public TabViewModel(SnpLibraryViewModel library, string name = "Tab 1", bool addEmptyPlot = true, bool selectEmptyPlot = false)
    {
        _name       = name;
        DataDisplay = new DataDisplayViewModel(library, addEmptyPlot, selectEmptyPlot);
    }

    [RelayCommand]
    private void CloseTab() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
