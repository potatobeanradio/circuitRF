// ================================================================
//  UndoCommands.cs  —  CircuitRF-specific IUndoableCommand implementations.
//
//  Each command is a self-contained snapshot of a single user action.
//  Commands call internal helper methods on DataDisplayViewModel rather
//  than manipulating private state directly, keeping the logic central
//  and testable.
//
//  ADDING A NEW COMMAND
//  1. Implement IUndoableCommand (Execute performs; Undo reverses).
//  2. Add any required internal helper methods to DataDisplayViewModel.
//  3. Push/Do the command at the call site (view-model or code-behind).
// ================================================================

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

// ---- Plot add / remove -----------------------------------------------

/// <summary>Undo: remove the added container.  Redo: add it back.</summary>
internal sealed class AddPlotCommand : IUndoableCommand
{
    private readonly PlotContainerViewModel _container;
    private readonly DataDisplayViewModel   _display;

    public AddPlotCommand(PlotContainerViewModel container, DataDisplayViewModel display)
    {
        _container = container;
        _display   = display;
    }

    public void Execute() => _display.InternalAddContainer(_container, selectIt: true);
    public void Undo()    => _display.InternalRemoveContainer(_container);
}

/// <summary>
/// Undo: re-add every container that was removed.
/// Redo: remove them again.
/// Handles single and multi-selection removes (Delete key, Delete Plot menu).
/// </summary>
internal sealed class RemovePlotsCommand : IUndoableCommand
{
    private readonly IReadOnlyList<PlotContainerViewModel> _containers;
    private readonly DataDisplayViewModel                  _display;

    public RemovePlotsCommand(
        IReadOnlyList<PlotContainerViewModel> containers,
        DataDisplayViewModel                  display)
    {
        _containers = containers;
        _display    = display;
    }

    public void Execute()
    {
        foreach (var c in _containers)
            _display.InternalRemoveContainer(c);
    }

    public void Undo()
    {
        foreach (var c in _containers)
            _display.InternalAddContainer(c, selectIt: false);

        // Re-select all restored containers so the user can see what came back.
        foreach (var c in _containers)
            c.IsSelected = true;
        _display.RefreshSelection();
    }
}

// ---- Marker add / remove ---------------------------------------------

/// <summary>Undo: remove the added marker.  Redo: add it back.</summary>
internal sealed class AddMarkerCommand : IUndoableCommand
{
    private readonly Marker                  _marker;
    private readonly Trace                   _trace;
    private readonly PlotContainerViewModel  _container;
    private readonly DataDisplayViewModel    _display;

    public AddMarkerCommand(
        Marker                 marker,
        Trace                  trace,
        PlotContainerViewModel container,
        DataDisplayViewModel   display)
    {
        _marker    = marker;
        _trace     = trace;
        _container = container;
        _display   = display;
    }

    public void Execute() => _display.InternalAddMarker(_marker, _trace, _container);
    public void Undo()    => _display.InternalRemoveMarker(_marker, _trace, _container);
}

/// <summary>Undo: restore the removed marker.  Redo: remove it again.</summary>
internal sealed class RemoveMarkerCommand : IUndoableCommand
{
    private readonly Marker                  _marker;
    private readonly Trace                   _trace;
    private readonly PlotContainerViewModel  _container;
    private readonly DataDisplayViewModel    _display;

    public RemoveMarkerCommand(
        Marker                 marker,
        Trace                  trace,
        PlotContainerViewModel container,
        DataDisplayViewModel   display)
    {
        _marker    = marker;
        _trace     = trace;
        _container = container;
        _display   = display;
    }

    public void Execute() => _display.InternalRemoveMarker(_marker, _trace, _container);
    public void Undo()    => _display.InternalAddMarker(_marker, _trace, _container);
}

// ---- Move plots / InfoBoxes ------------------------------------------

/// <summary>
/// Snapshot of a single plot container's position before and after a drag.
/// </summary>
internal readonly record struct PlotMoveSnapshot(
    PlotContainerViewModel Vm,
    double StartLeft, double StartTop,
    double EndLeft,   double EndTop);

/// <summary>
/// Snapshot of a single marker info-box position before and after a drag.
/// </summary>
internal readonly record struct InfoBoxMoveSnapshot(
    MarkerInfoBoxViewModel Vm,
    double StartLogLeft, double StartLogTop,
    double EndLogLeft,   double EndLogTop);

/// <summary>
/// Records the start and end positions of all plots and InfoBoxes moved
/// in a single drag gesture.
/// Execute() re-applies the end positions (Redo).
/// Undo()    restores the start positions.
/// </summary>
internal sealed class MovePlotsCommand : IUndoableCommand
{
    private readonly IReadOnlyList<PlotMoveSnapshot>    _plots;
    private readonly IReadOnlyList<InfoBoxMoveSnapshot> _infoBoxes;

    public MovePlotsCommand(
        IReadOnlyList<PlotMoveSnapshot>    plots,
        IReadOnlyList<InfoBoxMoveSnapshot> infoBoxes)
    {
        _plots     = plots;
        _infoBoxes = infoBoxes;
    }

    public void Execute()
    {
        foreach (var s in _plots)
        {
            s.Vm.Left = s.EndLeft;
            s.Vm.Top  = s.EndTop;
        }
        foreach (var s in _infoBoxes)
            s.Vm.SetLogicalPosition(s.EndLogLeft, s.EndLogTop);
    }

    public void Undo()
    {
        foreach (var s in _plots)
        {
            s.Vm.Left = s.StartLeft;
            s.Vm.Top  = s.StartTop;
        }
        foreach (var s in _infoBoxes)
            s.Vm.SetLogicalPosition(s.StartLogLeft, s.StartLogTop);
    }
}

// ---- Resize plot -------------------------------------------------------

/// <summary>
/// Records the width and height of a plot container before and after a
/// resize drag.  Only the drag start and drag end are stored — intermediate
/// sizes during the drag are NOT on the stack.
/// </summary>
internal sealed class ResizePlotCommand : IUndoableCommand
{
    private readonly PlotContainerViewModel _vm;
    private readonly double _oldW, _oldH;
    private readonly double _newW, _newH;

    public ResizePlotCommand(
        PlotContainerViewModel vm,
        double oldW, double oldH,
        double newW, double newH)
    {
        _vm   = vm;
        _oldW = oldW; _oldH = oldH;
        _newW = newW; _newH = newH;
    }

    public void Execute() { _vm.Width = _newW; _vm.Height = _newH; }
    public void Undo()    { _vm.Width = _oldW; _vm.Height = _oldH; }
}

// ---- Tab add / remove -----------------------------------------------

/// <summary>
/// Records a New Tab action.
/// Execute() (Redo): appends the tab to the end and makes it active.
/// Undo(): removes the tab, activating the nearest remaining tab.
/// </summary>
internal sealed class AddTabCommand : IUndoableCommand
{
    private readonly TabViewModel           _tab;
    private readonly DisplayWindowViewModel _window;

    public AddTabCommand(TabViewModel tab, DisplayWindowViewModel window)
    {
        _tab    = tab;
        _window = window;
    }

    public void Execute() => _window.InternalAddTab(_tab, _window.Tabs.Count, makeActive: true);
    public void Undo()    => _window.InternalRemoveTab(_tab);
}

/// <summary>
/// Records a tab close/remove action.
/// Execute() (Redo): removes the tab, activating the nearest remaining tab.
/// Undo(): re-inserts the tab at its original index and restores active state.
/// </summary>
internal sealed class RemoveTabCommand : IUndoableCommand
{
    private readonly TabViewModel           _tab;
    private readonly DisplayWindowViewModel _window;
    private readonly int                    _index;      // index at time of removal
    private readonly bool                   _wasActive;

    public RemoveTabCommand(
        TabViewModel           tab,
        DisplayWindowViewModel window,
        int                    index,
        bool                   wasActive)
    {
        _tab       = tab;
        _window    = window;
        _index     = index;
        _wasActive = wasActive;
    }

    public void Execute() => _window.InternalRemoveTab(_tab);
    public void Undo()    => _window.InternalAddTab(_tab, _index, makeActive: _wasActive);
}

// ---- Paste -----------------------------------------------------------

/// <summary>
/// Tracks the set of plot containers added by a Paste operation.
/// Undo removes them; Redo adds them back.
/// </summary>
internal sealed class PasteCommand : IUndoableCommand
{
    private readonly IReadOnlyList<PlotContainerViewModel> _pasted;
    private readonly DataDisplayViewModel                  _display;

    public PasteCommand(
        IReadOnlyList<PlotContainerViewModel> pasted,
        DataDisplayViewModel                  display)
    {
        _pasted  = pasted;
        _display = display;
    }

    public void Execute()
    {
        foreach (var c in _pasted)
            _display.InternalAddContainer(c, selectIt: false);

        // Re-select all pasted containers.
        foreach (var c in _pasted)
            c.IsSelected = true;
        _display.RefreshSelection();
    }

    public void Undo()
    {
        foreach (var c in _pasted)
            _display.InternalRemoveContainer(c);
    }
}
