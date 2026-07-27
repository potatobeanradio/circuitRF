namespace CircuitRF.Ui.ViewModels;

/// <summary>A non-editable group-header row in the L1j vertex list — "Outer (12)" / "Hole 1 (8)",
/// outer ring first (docs/sonnet-briefs/brief-L1j-properties-inspector.md §3.1a). Plain (not
/// <c>ObservableObject</c>): its text is fixed for the lifetime of the row's ring-structure — see
/// <c>LayoutShapePropertiesViewModel</c>'s "structure changed" rebuild rule.</summary>
public sealed class RingHeaderRow
{
    public string Text { get; }
    public RingHeaderRow(string text) => Text = text;
}
