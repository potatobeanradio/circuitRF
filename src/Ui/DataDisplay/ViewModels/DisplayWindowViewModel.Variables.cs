// ================================================================
//  DisplayWindowViewModel.Variables.cs  —  the window's display variables
//  (brief-ddl-7, the seam)
//
//  The window holds one DisplayVariables over its live tabs — the API a
//  display-bound plugin session would be handed, never this view model.
//  A set through it is made the way the canvas makes the same change:
//  a control's through EditControlCommand (ControlContainerViewModel.
//  Record), a marker's through the plot edit recorder an arrow step or a
//  drag ends in. Either is one undo step, marks the document dirty, and
//  the links follow. Subscribers hear of every variable that moved after
//  each follow, whoever moved it.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DisplayWindowViewModel
{
    private DisplayVariables? _variables;

    /// <summary>The display's variables: its markers, input controls and the trial selection, listed, read, set and
    /// subscribed to below the firewall.</summary>
    public DisplayVariables Variables
        => _variables ??= new DisplayVariables(BuildLinkDocument, () => new LibraryDataSources(DataSourceLibrary),
                                               new VariableWriter(this));

    /// <summary>A set made as the window's own gestures make it.</summary>
    private sealed class VariableWriter(DisplayWindowViewModel window) : IDisplayVariableWriter
    {
        public bool WriteControl(DisplayControl control, Func<bool> change)
        {
            var vm = window.Tabs.SelectMany(t => t.DataDisplay.Controls).FirstOrDefault(c => ReferenceEquals(c.Model, control));
            if (vm is null) return change();
            var before = control.Clone();
            if (!change()) return false;
            vm.RequestRedraw();
            vm.Record(before, "value");   // one undo step when it moved; dirty; the links follow (Changed)
            return true;
        }

        public bool WriteMarker(LinkDocument.MarkerSite site, Func<bool> change)
        {
            var display = window.Tabs.Select(t => t.DataDisplay)
                                .FirstOrDefault(d => d.Plots.Any(c => ReferenceEquals(c.PlotVM.Plot, site.Plot)));
            if (display is null) return change();
            var container = display.Plots.First(c => ReferenceEquals(c.PlotVM.Plot, site.Plot));
            display.CheckpointEdits();   // whatever was edited before is its own step
            if (!change()) return false;
            // As an arrow step does: the box follows, the plot redraws — which the edit recorder takes as one undo step
            // and the window as a dirty mark — and whatever follows the marker follows it.
            container.FindMarkerInfoBoxVm(site.Marker)?.OnMarkerMoved();
            container.RequestPlotRedraw();
            display.RequestLinkFollow(released: true);
            return true;
        }
    }
}
