using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of the L1j properties-panel vertex list (docs/sonnet-briefs/
/// brief-L1j-properties-inspector.md §3). Thin and bindable — all the geometry reading/writing lives
/// on the owning <see cref="LayoutShapePropertiesViewModel"/>, which alone knows the current effective
/// shape (R-L1j-1) and the display unit; this class only holds which vertex it addresses and its own
/// staged text/error state.</summary>
public sealed partial class VertexRowViewModel : ObservableObject
{
    private readonly LayoutShapePropertiesViewModel _owner;

    /// <summary>-1 = the outer ring; &gt;=0 = <c>Holes[Ring]</c>.</summary>
    public int Ring { get; }

    /// <summary>Index within its own ring — also the "#" column value.</summary>
    public int VertexIndex { get; }

    /// <summary>Stable key identifying this row's X/Y fields for the focus-tracking guard
    /// (R-L1j-3) — computed once, not re-derived per refresh.</summary>
    public string FieldKeyX { get; }
    public string FieldKeyY { get; }

    [ObservableProperty] private string _xText = "";
    [ObservableProperty] private string _yText = "";
    [ObservableProperty] private string _edgeText = "";
    [ObservableProperty] private string? _error;
    public bool HasError => Error is not null;

    internal VertexRowViewModel(LayoutShapePropertiesViewModel owner, int ring, int vertexIndex)
    {
        _owner = owner;
        Ring = ring;
        VertexIndex = vertexIndex;
        FieldKeyX = $"VtxX:{ring}:{vertexIndex}";
        FieldKeyY = $"VtxY:{ring}:{vertexIndex}";
        RefreshFromShape();
    }

    /// <summary>Re-reads this vertex's current position/edge kind from the owner's live effective
    /// shape and pushes it into <see cref="XText"/>/<see cref="YText"/>/<see cref="EdgeText"/> — unless
    /// this specific field currently has focus (R-L1j-3), in which case that one field is left alone.
    /// Called by the owner both for a full rebuild and, during a drag or an unrelated model change,
    /// for every already-realized row (R-L1j-6) — never rebuilds the row itself.</summary>
    internal void RefreshFromShape() => _owner.PopulateVertexRow(this);

    public void CommitX(string text) => _owner.CommitVertexField(this, isX: true, text);
    public void CommitY(string text) => _owner.CommitVertexField(this, isX: false, text);

    /// <summary>Escape — reverts this one field (X or Y) to its canonical value and clears its error,
    /// bypassing the focus guard deliberately (Escape IS the explicit revert action).</summary>
    public void Revert(bool isY) => _owner.RevertVertexField(this, isY);
}
