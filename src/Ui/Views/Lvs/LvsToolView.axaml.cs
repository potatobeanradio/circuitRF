using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.Lvs;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Lvs;

/// <summary>
/// Code-behind for the LVS results panel. Three gestures only — run a comparison, turn the parts it
/// found placed end for end, and bring the selected finding on screen. Both call into the active layout's own view model; nothing about a
/// finding, a waiver or a marker is decided here (R-lvs12-5a).
/// </summary>
public partial class LvsToolView : UserControl
{
    public LvsToolView() => InitializeComponent();

    /// <summary>
    /// R-lvs12-1c: one call to the view model's <c>RunLvs</c>, which is one call to
    /// <c>LvsRun.Run</c> — the same function <c>circuitrf lvs</c> calls, with the same arguments.
    /// </summary>
    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LvsTool { EditorVm: { } vm }) return;

        var result = vm.RunLvs();
        if (result is null) return;   // reported by the view model; no cell folder to compare

        // Reported, not merely run — the DRC panel's own rule. A comparison that filled a list and
        // said nothing in Messages leaves no trace of WHICH technology it read the artwork against,
        // which is the half a clean result cannot be trusted without.
        LvsRunReport.Post(ResolveWorkspace()?.Messages ?? vm.MessageSink, result);
    }

    /// <summary>
    /// Brief LVS 16 R-lvs16-3a: every checked part turned in one undoable step, then compared again
    /// — and the new result posted, on the Compare button's own terms.
    /// </summary>
    private void OnTurnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LvsTool { EditorVm: { } vm }) return;

        var result = vm.TurnInLayout(vm.SelectedLvsTurnedParts);
        if (result is null) return;   // the view model says why, beside the button

        LvsRunReport.Post(ResolveWorkspace()?.Messages ?? vm.MessageSink, result);
    }

    private static WorkspaceViewModel? ResolveWorkspace() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows
            .Select(w => w.DataContext)
            .OfType<WorkspaceViewModel>()
            .FirstOrDefault();

    /// <summary>
    /// Click-to-zoom, on the DRC panel's own terms: double-click rather than single, because a
    /// single click already selects the row (which highlights its marker and cross-probes it) and
    /// yanking the viewport on every arrow-key walk down the list would make the list unusable.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LvsTool { EditorVm: { } vm }) vm.ZoomToSelectedFindingCommand.Execute(null);
    }
}
