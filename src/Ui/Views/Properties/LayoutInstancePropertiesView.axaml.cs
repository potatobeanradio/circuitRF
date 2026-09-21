using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>Code-behind for <see cref="LayoutInstancePropertiesView"/> — see that file's own doc
/// comment for why this exists as a standalone control. Handlers moved verbatim from
/// <c>LayoutShapePropertiesView.axaml.cs</c>, unchanged, exactly as
/// <see cref="PCellParameterListView"/>'s were: the generic Tag-keyed trio stays on the shape panel
/// too, because the panel's other sections still use it.</summary>
public partial class LayoutInstancePropertiesView : UserControl
{
    public LayoutInstancePropertiesView() => InitializeComponent();

    private LayoutShapePropertiesViewModel? Vm => DataContext as LayoutShapePropertiesViewModel;

    // ── Static fields (Tag = field key, e.g. "InstanceX", "InstanceRotation") ──────────────────

    private void OnFieldGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string key }) Vm?.SetFocusedField(key);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        Vm?.SetFocusedField(null);
        Vm?.CommitField(key, tb.Text ?? "");
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        if (e.Key is Key.Enter or Key.Return) Vm?.CommitField(key, tb.Text ?? "");
        else if (e.Key == Key.Escape) Vm?.RevertField(key);
    }

    /// <summary>A cardinal-angle preset (R-L3d-10). Routes through the SAME commit path a typed angle
    /// takes, so there is one place a placement angle is set and not two that can disagree.</summary>
    private void OnInstanceRotationPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string degrees }) Vm?.CommitInstanceRotationText(degrees);
    }

    /// <summary>brief-footprint-4b R-fp4b-6b — Reset to AUTO, which is back to derived rather than
    /// back to a remembered number. Applies to every selected placement, not only this one.</summary>
    private void OnResetDesignatorPositionClick(object? sender, RoutedEventArgs e)
        => Vm?.ResetInstanceDesignatorPosition();

    // ── Instance: Re-target… (UI firewall — the cell-picker dialog lives in code-behind, never the
    // VM; mirrors LayoutEditorView.axaml.cs's own OnInstanceTool exactly, minus the placement-arming
    // step at the end — this button retargets the ALREADY-PLACED selected instance in place) ─────────

    private async void OnInstanceRetargetClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.EditorVm is not { } editorVm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        // No "Reference Cell…" here, deliberately: re-targeting is an edit on one existing instance,
        // and taking a cell in from another workspace is a change to the WORKSPACE. Offering it from a
        // property row would bury a workspace-level act inside a field edit. The picker falls back to
        // its plain Browse… for the same reason it always did.
        var dialog = new InstanceCellPickerDialog(editorVm.WorkspaceRootDir, editorVm.InstanceBaseDir, editorVm.CurrentCellDir);
        var pick = await dialog.ShowDialog<CellPickResult?>(owner);
        if (pick is null || pick.ReferenceRequested || pick.CellRef.Length == 0) return;

        editorVm.RetargetSelectedInstance(pick.CellRef);
    }
}
