using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-L4c-7: "the export dialog states every conversion before anything is written" — Bézier edges
/// flattened, hierarchy flattened, labels converted to geometry, port labels omitted, bitmaps omitted,
/// paths emitted as regions rather than strokes, and the coordinate format chosen (only when it widened
/// past the plain default). Computed by <see cref="GerberExport.Analyze"/> via the same write path the
/// real export uses (a dry run into <c>Stream.Null</c>), so this can never disagree with what actually
/// gets written. By the time this dialog is shown, any cross-technology mapping confirmation has
/// already been resolved by the caller (the same <c>LayerMappingDialog</c> paste/retarget/flatten use) —
/// <see cref="GerberExport.ExportPlan.CanWrite"/> being false here means the hierarchy exceeded the
/// flatten ceiling or the resolution isn't representable exactly (R-L4c-1), and disables Export outright.
/// </summary>
public partial class GerberExportFidelityDialog : Window
{
    public GerberExportFidelityDialog() => InitializeComponent();

    public GerberExportFidelityDialog(GerberExport.ExportPlan plan) : this()
    {
        CubicLine.Text = $"• {plan.CubicEdgesFlattened} Bézier (cubic) edge(s) will flatten to line segments.";
        CubicLine.IsVisible = plan.CubicEdgesFlattened > 0;

        HierarchyLine.Text = $"• {plan.TopLevelInstancesFlattened} instance(s)/array(s) will flatten into {plan.ShapesContributedByFlatten} shape(s) — Gerber has no hierarchy.";
        HierarchyLine.IsVisible = plan.TopLevelInstancesFlattened > 0;

        LabelsLine.Text = $"• {plan.LabelsConvertedToGeometry} label(s) will convert to stroked-font geometry.";
        LabelsLine.IsVisible = plan.LabelsConvertedToGeometry > 0;

        PortLabelsLine.Text = $"• {plan.PortLabelsOmitted} port label(s) will be omitted (markers, not artwork).";
        PortLabelsLine.IsVisible = plan.PortLabelsOmitted > 0;

        BitmapLine.Text = $"• {plan.BitmapsOmitted} bitmap(s) will be omitted (never exported to Gerber).";
        BitmapLine.IsVisible = plan.BitmapsOmitted > 0;

        PathsRegionLine.Text = $"• {plan.PathsAsRegion} path(s) will export as filled region outlines instead of parametric strokes (non-round end style).";
        PathsRegionLine.IsVisible = plan.PathsAsRegion > 0;

        FormatLine.Text = $"• Coordinate format: %FSLAX{plan.Format.DigitPair}Y{plan.Format.DigitPair}*% ({plan.Format.IntegerDigits} integer + {plan.Format.DecimalDigits} decimal digits).";
        FormatLine.IsVisible = plan.FormatIsNonDefault;

        // R-via-5: a bare Circle on a drill layer still drills (never refused, never silent) but has
        // no matching pad — reported so the user can Convert to Via (R-via-6) for annular-ring checking.
        UnpairedDrillLine.Text = $"• {plan.UnpairedDrillCircles} circle(s) on a drill layer will produce unpaired holes — convert to Vias for annular-ring checking?";
        UnpairedDrillLine.IsVisible = plan.UnpairedDrillCircles > 0;

        NoChangesLine.IsVisible = plan.HasNothingToReport;

        if (plan.UnresolvedInstances.Count > 0)
        {
            UnresolvedHeader.IsVisible = true;
            UnresolvedScroll.IsVisible = true;
            UnresolvedList.ItemsSource = plan.UnresolvedInstances;
        }

        if (!plan.CanWrite)
        {
            BlockedHeader.IsVisible = true;
            BlockedScroll.IsVisible = true;
            BlockedList.ItemsSource = plan.Diagnostics.Count > 0 ? plan.Diagnostics : ["Export cannot proceed."];
            ExportButton.IsEnabled = false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnExportClick(object? sender, RoutedEventArgs e) => Close(true);
}
