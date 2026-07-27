using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// "Flatten to Polygon…" tolerance prompt (docs/design/layout-view.md §3.2 R9d,
/// docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md R-L1h-2) — the ONE surviving Flatten entry
/// point; the no-dialog variant is gone. Flattening is irreversible except by undo and its resolution
/// is the whole point of the operation, so the tolerance is always shown and confirmed, never
/// inferred. Returns the chosen tolerance in DBU via <c>ShowDialog&lt;long?&gt;</c>, or null on
/// cancel. <b>R-L1h-2a: this dialog never writes its value back to any shape</b> — every shape it
/// touches stops being curved (a flattened circle is a <c>PolygonShape</c> with no tolerance field at
/// all), so there is nothing left for a written-back value to govern; the caller applies the chosen
/// value directly via <see cref="LayoutEditorViewModel.FlattenSelectionToPolygon"/>.
/// </summary>
public partial class FlattenToPolygonDialog : Window
{
    private readonly LayoutEditorViewModel? _vm;
    private readonly IReadOnlyList<int> _selectedIndices = [];
    private readonly LayoutUnit _displayUnit;
    private readonly int _dbuPerMicron;

    public FlattenToPolygonDialog() => InitializeComponent();

    /// <param name="vm">The layout editor VM.</param>
    /// <param name="selectedIndices">The FULL current selection — not just the curved subset; the
    /// skip count (R-L1h-2) is computed as the difference between this and the curved subset.</param>
    public FlattenToPolygonDialog(LayoutEditorViewModel vm, IReadOnlyList<int> selectedIndices) : this()
    {
        _vm = vm;
        _selectedIndices = selectedIndices;
        _displayUnit = vm.DisplayUnit;
        _dbuPerMicron = vm.Model.DbuPerMicron;

        // R-lbl-4/R-lbl-5 (docs/sonnet-briefs/brief-layout-label-fix-and-text-flatten.md): names what
        // will happen — curves and labels are counted separately (glyph outlines are a different kind
        // of "becomes a polygon" than a curved primitive, worth saying explicitly), and a port label
        // (excluded from `curved` entirely — it can never be flattened) is called out by name rather
        // than folded into an undifferentiated "has no curvature" bucket.
        var curved = selectedIndices.Where(vm.HasCurvedGeometryAt).ToList();
        int curveCount = curved.Count(i => vm.Model.Shapes[i] is not LabelShape);
        int labelCount = curved.Count - curveCount;
        int portLabelCount = selectedIndices.Count(i => vm.Model.Shapes[i] is LabelShape { IsPort: true });
        int otherSkipped = selectedIndices.Count - curved.Count - portLabelCount;

        string what = (curveCount, labelCount) switch
        {
            (0, 0)          => "Nothing",
            (var c, 0)      => $"{c} curve{(c == 1 ? "" : "s")}",
            (0, var l)      => $"{l} label{(l == 1 ? "" : "s")}",
            (var c, var l)  => $"{c} curve{(c == 1 ? "" : "s")} and {l} label{(l == 1 ? "" : "s")}",
        };
        var notes = new List<string>();
        if (portLabelCount > 0) notes.Add($"{portLabelCount} port label{(portLabelCount == 1 ? "" : "s")} will be skipped");
        if (otherSkipped > 0) notes.Add($"{otherSkipped} have no curvature");

        SkipText.Text = notes.Count > 0
            ? $"{what} will become polygon(s); {string.Join("; ", notes)}."
            : $"{what} will become polygon(s).";
        TextBecomesGeometryNote.IsVisible = labelCount > 0;

        // R-L1h-2b: pre-fill from the FIRST curved shape's resolved tolerance, labelled by which of
        // ResolveTolDbu's two branches actually won — the shape's own explicit value, or the
        // technology default. A tolerance set in the properties panel is the tolerance this dialog
        // offers; the two surfaces agree by reading the same source, never by writing to each other.
        if (curved.Count > 0)
        {
            var repShape = vm.Model.Shapes[curved[0]];
            long defaultTol = LayoutFlattener.ResolveTolDbu(repShape, vm.Technology);
            ToleranceBox.Text = LayoutUnits.Format(defaultTol, _displayUnit, _dbuPerMicron);
            SourceText.Text = LayoutFlattener.OwnTolDbu(repShape) is not null
                ? "from this shape"
                : "from technology default";
        }

        UpdatePreview();
        Opened += (_, _) => { ToleranceBox.Focus(); ToleranceBox.SelectAll(); };
    }

    private void OnToleranceChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_vm is not null && TryReadTolerance(out var dbu))
        {
            var counts = _vm.PreviewFlattenVertexCounts(_selectedIndices, dbu);
            int total = counts.Sum(c => c.VertexCount);
            CountLabel.Text = counts.Count <= 1
                ? $"{total} vertices"
                : $"{total} vertices total across {counts.Count} shape(s)";
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
