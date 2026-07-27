using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// "Flatten to Polygon…" tolerance prompt (docs/design/layout-view.md §3.2 R9d) — shows the resulting
/// vertex count live as the tolerance changes. Returns the chosen tolerance in DBU via
/// <c>ShowDialog&lt;long?&gt;</c>, or null on cancel. The preview is computed against ONE representative
/// shape (the first curved shape in the selection this was opened for) — the tolerance the user picks
/// here is then applied uniformly to every curved shape in the selection.
/// </summary>
public partial class FlattenToPolygonDialog : Window
{
    private readonly LayoutEditorViewModel? _vm;
    private readonly int _previewShapeIndex;
    private readonly LayoutUnit _displayUnit;
    private readonly int _dbuPerMicron;

    public FlattenToPolygonDialog() => InitializeComponent();

    public FlattenToPolygonDialog(LayoutEditorViewModel vm, int previewShapeIndex, long defaultTolDbu) : this()
    {
        _vm = vm;
        _previewShapeIndex = previewShapeIndex;
        _displayUnit = vm.DisplayUnit;
        _dbuPerMicron = vm.Model.DbuPerMicron;

        ToleranceBox.Text = LayoutUnits.Format(defaultTolDbu, _displayUnit, _dbuPerMicron);
        UpdatePreview();
        Opened += (_, _) => { ToleranceBox.Focus(); ToleranceBox.SelectAll(); };
    }

    private void OnToleranceChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_vm is not null && TryReadTolerance(out var dbu))
        {
            int count = _vm.PreviewFlattenVertexCount(_previewShapeIndex, dbu);
            CountLabel.Text = $"{count} vertices";
        }
        else
        {
            CountLabel.Text = "—";
        }
    }

    private bool TryReadTolerance(out long dbu) =>
        LayoutUnits.TryParse(ToleranceBox.Text ?? "", _displayUnit, _dbuPerMicron, out dbu) && dbu > 0;

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { TryCommit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    private void TryCommit()
    {
        if (TryReadTolerance(out var dbu)) Close((long?)dbu);
    }
}
