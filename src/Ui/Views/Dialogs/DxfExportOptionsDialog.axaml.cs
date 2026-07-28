using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// §2A's "the export dialog states what will change before writing," mirroring
/// <c>GdsiiExportFidelityDialog</c>'s own pattern exactly — the shown counts come from
/// <see cref="DxfExport.Preview"/>, the SAME write path (a dry run) the real export uses, so the
/// preview can never disagree with what actually happens. Adds the two DXF-specific choices §1.2/§2A.3
/// call for: flatten-curves-to-polyline and fit-to-extents/match-current-view — remembered for the
/// session by the caller (<paramref name="defaultFlattenSplines"/> etc.), never persisted to disk.
/// </summary>
public partial class DxfExportOptionsDialog : Window
{
    public bool FlattenSplines { get; private set; }
    public bool PathAsOutlinePolygon { get; private set; }
    public DxfViewMode ViewMode { get; private set; } = DxfViewMode.FitToExtents;
    public DxfAcadVersion AcadVersion { get; private set; } = DxfAcadVersion.R2018;

    public DxfExportOptionsDialog() => InitializeComponent();

    public DxfExportOptionsDialog(
        DxfExport.ExportPlan plan, DxfExportSummary preview,
        bool defaultFlattenSplines, bool defaultPathAsOutline, DxfViewMode defaultViewMode,
        DxfAcadVersion defaultAcadVersion = DxfAcadVersion.R2018) : this()
    {
        CurveLine.Text = $"• {preview.CurvedShapesWritten} curved shape(s) export natively (arc bulge / SPLINE) — never flattened.";
        CurveLine.IsVisible = preview.CurvedShapesWritten > 0;

        HoleLine.Text = $"• {preview.HolesAsHatch} shape(s) with holes will export as HATCH (holes preserved).";
        HoleLine.IsVisible = preview.HolesAsHatch > 0;

        BitmapLine.Text = $"• {preview.BitmapsSkipped} bitmap(s) will be skipped (never exported to DXF).";
        BitmapLine.IsVisible = preview.BitmapsSkipped > 0;

        MixedLine.Text = $"• {preview.MixedArcCubicApproximated} shape(s) mixing an arc and a cubic edge in one ring will approximate the cubic segment(s).";
        MixedLine.IsVisible = preview.MixedArcCubicApproximated > 0;

        PathLine.Text = $"• {preview.PathsFlattenedForCubic} path(s) with a cubic segment will export fully flattened.";
        PathLine.IsVisible = preview.PathsFlattenedForCubic > 0;

        // R-dxf-2 (brief-dxf-version-support.md): R2000 output has no native Unicode text, so any
        // non-ASCII layer/block name or label is `\U+XXXX`-escaped rather than written as a raw
        // code-page byte — reported here, never silent, exactly like the other fidelity lines above.
        EscapedTextLine.Text = $"• {preview.NonAsciiTextEscaped} non-ASCII text value(s) (layer/block name or label) will be escaped as \\U+XXXX.";
        EscapedTextLine.IsVisible = preview.NonAsciiTextEscaped > 0;

        NoChangesLine.IsVisible =
            preview.CurvedShapesWritten == 0 && preview.HolesAsHatch == 0 && preview.BitmapsSkipped == 0 &&
            preview.MixedArcCubicApproximated == 0 && preview.PathsFlattenedForCubic == 0 &&
            preview.NonAsciiTextEscaped == 0;

        if (plan.UnresolvedInstanceReferences.Count > 0)
        {
            UnresolvedHeader.IsVisible = true;
            UnresolvedScroll.IsVisible = true;
            UnresolvedList.ItemsSource = plan.UnresolvedInstanceReferences;
        }

        FlattenSplinesCheck.IsChecked = defaultFlattenSplines;
        PathOutlineCheck.IsChecked = defaultPathAsOutline;
        ViewModeCombo.SelectedIndex = defaultViewMode == DxfViewMode.MatchCurrentView ? 1 : 0;

        // R-col-1a: the version choice is remembered for the session by the caller (a static field on
        // LayoutEditorView, mirroring _lastFlattenSplines/_lastViewMode exactly) — this dialog only
        // reflects whatever it's handed and reports back whatever the user picked.
        AcadVersionCombo.SelectedIndex = defaultAcadVersion switch
        {
            DxfAcadVersion.R2000 => 0,
            DxfAcadVersion.R2004 => 1,
            _ => 2,
        };
        UpdateFormatVersionLine();
    }

    private void OnAcadVersionChanged(object? sender, SelectionChangedEventArgs e) => UpdateFormatVersionLine();

    private void UpdateFormatVersionLine() =>
        FormatVersionLine.Text = $"Writes DXF: {DxfWriter.FormatDescription(SelectedAcadVersion())}";

    private DxfAcadVersion SelectedAcadVersion() => AcadVersionCombo.SelectedIndex switch
    {
        0 => DxfAcadVersion.R2000,
        1 => DxfAcadVersion.R2004,
        _ => DxfAcadVersion.R2018,
    };

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        FlattenSplines = FlattenSplinesCheck.IsChecked == true;
        PathAsOutlinePolygon = PathOutlineCheck.IsChecked == true;
        ViewMode = ViewModeCombo.SelectedIndex == 1 ? DxfViewMode.MatchCurrentView : DxfViewMode.FitToExtents;
        AcadVersion = SelectedAcadVersion();
        Close(true);
    }
}
