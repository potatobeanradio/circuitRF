// ================================================================
//  DataDisplayViewModel.Grid.cs  —  the canvas grid plots snap to
//
//  The symbol editor's snap button, for the Data Display canvas: one
//  toolbar button cycling a coarse grid, a fine grid and no grid. While a
//  grid is on, moving a plot puts its corner on it and resizing one puts
//  its far edge on it — so plots line up and come out the same size. The
//  grid itself is not drawn: it is where plots land, not something to look
//  at. A view setting, not part of the document.
// ================================================================

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>The canvas grid: the two spacings the toolbar button offers, and off.</summary>
public enum CanvasGrid { Coarse, Fine, Off }

public partial class DataDisplayViewModel
{
    /// <summary>The coarse grid's spacing, in the canvas's logical units.</summary>
    public const double CoarseGridStep = 40;

    /// <summary>The fine grid's spacing, in the canvas's logical units.</summary>
    public const double FineGridStep = 10;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(GridStep))]
    private CanvasGrid _canvasGrid = CanvasGrid.Off;

    /// <summary>The grid's spacing in logical units; 0 when it is off.</summary>
    public double GridStep => CanvasGrid switch
    {
        CanvasGrid.Coarse => CoarseGridStep,
        CanvasGrid.Fine   => FineGridStep,
        _                 => 0,
    };

    /// <summary>Puts <paramref name="v"/> on the grid, or returns it as it is when the grid is off.</summary>
    internal double SnapToGrid(double v) => GridStep > 0 ? Math.Round(v / GridStep) * GridStep : v;

    /// <summary>The pointer's travel since the move began, before any snapping.</summary>
    private double _moveRawDx, _moveRawDy;

    /// <summary>
    /// The move to apply this step when the grid is on: the dragged plot's top-left goes where the pointer's
    /// travel would put it, snapped, and every other selected item moves by the same amount.
    /// </summary>
    private (double dx, double dy) SnappedMoveStep(double dx, double dy, PlotContainerViewModel? anchor)
    {
        if (GridStep <= 0 || anchor is null || _moveStartPlots is null) return (dx, dy);
        int i = _moveStartPlots.FindIndex(s => ReferenceEquals(s.Vm, anchor));
        if (i < 0) return (dx, dy);
        _moveRawDx += dx;
        _moveRawDy += dy;
        var start = _moveStartPlots[i];
        return (SnapToGrid(start.Left + _moveRawDx) - anchor.Left,
                SnapToGrid(start.Top  + _moveRawDy) - anchor.Top);
    }
}
