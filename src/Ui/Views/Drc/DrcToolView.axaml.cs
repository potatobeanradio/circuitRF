using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;
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

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DrcTool { EditorVm: { } vm }) return;

        vm.RunDrc();

        // A design with bond wires and no assembly rules is the ONE moment a `.wasm` is worth asking
        // about — the user has just asked a question the workspace cannot answer. A new workspace
        // deliberately ships no rule file, so this is where one comes from; declining is remembered
        // for the session. See WorkspaceViewModel.PromptForAssemblyRulesAsync.
        if (vm.WireDesign is null || vm.AssemblyRules?.Rules is not null) return;

        var workspace = ResolveWorkspace();
        if (workspace is null) return;

        if (await workspace.PromptForAssemblyRulesAsync(vm, TopLevel.GetTopLevel(this) as Window))
            vm.RunDrc();
    }

    /// <summary>
    /// This view's DataContext is a <see cref="DrcTool"/>, not the workspace, so the workspace is
    /// found by walking the application's own windows — the same mechanism <c>TornOffFileMenuView</c>
    /// already uses, for the same reason.
    /// </summary>
    private static WorkspaceViewModel? ResolveWorkspace() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows
            .Select(w => w.DataContext)
            .OfType<WorkspaceViewModel>()
            .FirstOrDefault();

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
