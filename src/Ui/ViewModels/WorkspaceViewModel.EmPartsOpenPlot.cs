// The automatic plot of an EM result with component ports reads the board with every part's port open (EmPartsOpen.Group), not
// the run's raw S, in which every part's port is terminated in 50 ohms. That is the board the 3D view and the layout draw, and the
// S21 a designer expects across a series part that is not fitted. The raw S stays in the result for a trace that asks for it.

using System;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>
    /// Points the inspector's last trace at <see cref="EmPartsOpen.Group"/>'s transmission (its second port from its first, or its
    /// one port's reflection) when <paramref name="data"/> holds that group. False, with the trace as it was, otherwise.
    /// </summary>
    internal static bool PointAtPartsOpen(PlotInspectorViewModel inspector, DataSet? data)
    {
        string spec = $"{EmPartsOpen.Group}.{NetworkMetrics.SCubeName}";
        if (data is null || !data.Contains(spec) || inspector.Traces.LastOrDefault() is not { } row) return false;
        string? group = row.AvailableGroups.FirstOrDefault(g => g == EmPartsOpen.Group
                                                             || g.EndsWith(".." + EmPartsOpen.Group, StringComparison.Ordinal));
        if (group is null) return false;

        var ports = data[spec].Axes[1];
        int Number(int k) => PortSlices.IsPortNumberAxis(ports) ? (int)Math.Round(ports.Values[k]) : k + 1;
        string label = ports.Length >= 2 ? $"S({Number(1)},{Number(0)})" : $"S({Number(0)},{Number(0)})";

        row.SelectedGroup = group;
        if (row.AvailableSignals.FirstOrDefault(x => x.Label == label && x.IsEnabled) is not { } item) return false;
        row.SelectedSignal = item;
        return true;
    }
}
