using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Drc;

/// <summary>
/// Code-behind for the DRC violations panel. Two gestures only — run a check, and bring the selected
/// violation on screen. Both call into the active layout's own view model; nothing about a rule, a
/// waiver or a marker is decided here.
/// </summary>
public partial class DrcToolView : UserControl
{
    public DrcToolView() => InitializeComponent();

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DrcTool { EditorVm: { } vm }) vm.RunDrc();
    }

    /// <summary>
    /// §9A.1's click-to-zoom. Double-click rather than single: a single click selects a row (which
    /// already highlights that violation's own marker), and yanking the viewport on every arrow-key
    /// walk down the list would make the list unusable.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DrcTool { EditorVm: { } vm }) vm.ZoomToSelectedViolationCommand.Execute(null);
    }
}
