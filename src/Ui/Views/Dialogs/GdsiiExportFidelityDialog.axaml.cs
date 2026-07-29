using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-L4a-3: "The export dialog states what will change before writing" — curve-flatten count, hole
/// keyhole count, and skipped-bitmap count, computed by <see cref="GdsiiExport.Analyze"/> via the same
/// write path the real export uses (a dry run), so this can never disagree with what actually happens.
/// A coordinate overflow (gate 8) lists every offending shape by name and disables Export outright —
/// never a warning the user can click through.
/// </summary>
public partial class GdsiiExportFidelityDialog : Window
{
    public GdsiiExportFidelityDialog() => InitializeComponent();

    public GdsiiExportFidelityDialog(GdsiiExport.ExportPlan plan) : this()
    {
        CurveLine.Text = $"• {plan.CurvedShapesFlattened} curved shape(s) will flatten to polygons.";
        CurveLine.IsVisible = plan.CurvedShapesFlattened > 0;

        HoleLine.Text = $"• {plan.HolesKeyholed} shape(s) with holes will be keyholed into a single contour.";
        HoleLine.IsVisible = plan.HolesKeyholed > 0;

        BitmapLine.Text = $"• {plan.BitmapsSkipped} bitmap(s) will be skipped (never exported to GDSII).";
        BitmapLine.IsVisible = plan.BitmapsSkipped > 0;

        // R-via-9: a via with no landing layer configured exports its barrel only.
        ViaPadSkippedLine.Text = $"• {plan.ViaPadsSkipped} via(s) have no landing layer set — pad not exported.";
        ViaPadSkippedLine.IsVisible = plan.ViaPadsSkipped > 0;

        // R-via-10: GDSII carries no drill table — never a manufacturable PCB deliverable.
        ViaFabricationNoteLine.IsVisible = plan.HasVias;

        NoChangesLine.IsVisible = plan.HasNothingToReport;

        if (plan.UnresolvedInstanceReferences.Count > 0)
        {
            UnresolvedHeader.IsVisible = true;
            UnresolvedScroll.IsVisible = true;
            UnresolvedList.ItemsSource = plan.UnresolvedInstanceReferences;
        }

        if (!plan.CanWrite)
        {
            OverflowHeader.IsVisible = true;
            OverflowScroll.IsVisible = true;
            OverflowList.ItemsSource = plan.CoordinateOverflowOffenders;
            ExportButton.IsEnabled = false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnExportClick(object? sender, RoutedEventArgs e) => Close(true);
}
