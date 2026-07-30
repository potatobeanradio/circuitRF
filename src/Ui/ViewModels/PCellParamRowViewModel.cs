using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of the L5-followups §5 Properties Inspector parameter list for a selected PCell
/// instance (docs/sonnet-briefs/brief-L5-followups.md §5/R-L5f-8) — mirrors <see cref="VertexRowViewModel"/>'s
/// own shape exactly (thin, bindable, all reading/writing delegated to the owner, which alone knows
/// the resolved cell and the layout's display unit).</summary>
public sealed partial class PCellParamRowViewModel : ObservableObject
{
    private readonly LayoutShapePropertiesViewModel _owner;

    /// <summary>The PCell parameter name (e.g. "W", "Z1", "GammaMax") — also the "#"-column label.</summary>
    public string Name { get; }

    /// <summary>The parameter's declared unit (from <c>ComponentTypeRegistry.DefaultParameters</c>) —
    /// "mm" for a length (displayed/parsed in the LAYOUT's own display unit, R-L5f-8), "deg" for an
    /// angle, "Ω" for a resistance, or "" for dimensionless. Display-only; not itself editable.</summary>
    public string Unit { get; }

    /// <summary>Stable key for the focus-tracking guard (mirrors <c>VertexRowViewModel.FieldKeyX</c>) —
    /// computed once, not re-derived per refresh.</summary>
    public string FieldKey { get; }

    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string? _error;
    public bool HasError => Error is not null;

    internal PCellParamRowViewModel(LayoutShapePropertiesViewModel owner, string name, string unit)
    {
        _owner = owner;
        Name = name;
        Unit = unit;
        FieldKey = $"PCellParam:{name}";
        RefreshFromInstance();
    }

    /// <summary>Re-reads this parameter's current value from the owner's selected instance's resolved
    /// cell and pushes it into <see cref="ValueText"/> — unless this field currently has focus, in
    /// which case it is left alone (same focus guard <c>VertexRowViewModel.RefreshFromShape</c> uses).
    /// Called by the owner for every already-realized row on refresh — never rebuilds the row itself.</summary>
    internal void RefreshFromInstance() => _owner.PopulatePCellParamRow(this);

    /// <summary>R-L5f-9: commits (LostFocus/Enter) — copy-on-write, via
    /// <see cref="LayoutEditorViewModel.EditInstancePCellParameters"/>.</summary>
    public void Commit(string text) => _owner.CommitPCellParamField(this, text);
}
