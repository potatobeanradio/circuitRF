using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// VM for one row in the .ctech editor's stackup list — an ordered top-to-bottom plain list
/// (no diagram; see docs/design/layout-view.md §10.4, which is L6). The detail pane shows only
/// the fields <see cref="Kind"/> actually uses (§2.4's rule): a dielectric never shows σ, a
/// conductor/via never shows εr/tanδ/µr. Thickness is a physical dimension, parsed/formatted via
/// <see cref="LayoutUnits"/> in the technology's <see cref="Technology.DefaultDisplayUnit"/> —
/// never a hand-rolled number parser. Drawing-layer selection is a closed set against the current
/// layer table (<see cref="DrawingLayerOptions"/>), not free text, so it is impossible in the UI
/// to name a layer that doesn't exist.
///
/// <b>Cardinality per §10.4 is NOT uniform across kinds.</b> A conductor is explicitly "bound to
/// one or more drawing layers" (e.g. a plane split/repeated across several drawn layer numbers) —
/// multi-select is correct there. A via is "bound to a drawing layer" (singular) and a dielectric
/// slab likewise corresponds to at most one outline/extent layer — for both, checking a new layer
/// in <see cref="SetDrawingLayerChecked"/> clears any previous selection instead of adding to it,
/// so the UI enforces the same one-drawing-layer invariant the model already implies.
/// </summary>
public sealed partial class StackupLayerRowViewModel : ObservableObject
{
    private readonly TechEditorViewModel _owner;
    private bool _isRefreshing;

    internal StackupLayer Layer { get; }

    public StackupKind Kind => Layer.Kind;
    public bool IsDielectric => Kind == StackupKind.Dielectric;
    public bool IsConductor  => Kind == StackupKind.Conductor;
    public bool IsVia        => Kind == StackupKind.Via;

    /// <summary>Only a conductor may bind more than one drawing layer (§10.4).</summary>
    public bool AllowMultipleDrawingLayers => Kind == StackupKind.Conductor;

    public string DrawingLayersLabel => AllowMultipleDrawingLayers ? "Drawing layers:" : "Drawing layer:";

    /// <summary>Subtle units reminder shown next to the Thickness field — the technology's own
    /// <see cref="Technology.DefaultDisplayUnit"/>, the same unit <see cref="StagedThicknessText"/>
    /// is parsed/formatted in.</summary>
    public string ThicknessUnitSuffix => LayoutUnits.Suffix(_owner.Working.DefaultDisplayUnit);

    [ObservableProperty] private string _stagedName = "";
    [ObservableProperty] private string _stagedThicknessText = "";
    [ObservableProperty] private string? _thicknessError;
    public bool HasThicknessError => ThicknessError is not null;
    partial void OnThicknessErrorChanged(string? value) => OnPropertyChanged(nameof(HasThicknessError));

    [ObservableProperty] private string _stagedEpsr = "";
    [ObservableProperty] private string _stagedTanD = "";
    [ObservableProperty] private string _stagedMur  = "";
    [ObservableProperty] private string _stagedSigmaSm = "";

    /// <summary>brief-technology-editor-units-and-layers.md R-tec-1: settable ONLY on conductor rows
    /// (meaningless on dielectric/via — <see cref="StackupLayer.IsGroundReference"/>'s own doc
    /// comment). Commits immediately on toggle, mirroring <c>LayerRowViewModel</c>'s own
    /// Visible/Selectable checkboxes rather than the staged-text convention used for numeric fields.</summary>
    [ObservableProperty] private bool _isGroundReference;

    public ObservableCollection<DrawingLayerCheckItem> DrawingLayerOptions { get; } = [];

    public IRelayCommand RemoveCommand   { get; }
    public IRelayCommand MoveUpCommand   { get; }
    public IRelayCommand MoveDownCommand { get; }

    public StackupLayerRowViewModel(StackupLayer layer, TechEditorViewModel owner)
    {
        Layer = layer;
        _owner = owner;

        RemoveCommand   = new RelayCommand(() => owner.RemoveStackupLayer(this));
        MoveUpCommand   = new RelayCommand(() => owner.MoveStackupLayer(this, -1));
        MoveDownCommand = new RelayCommand(() => owner.MoveStackupLayer(this, +1));

        RefreshFromModel();
    }

    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedName          = Layer.Name;
        StagedThicknessText  = LayoutUnits.Format(Layer.ThicknessDbu, _owner.Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron);
        ThicknessError       = null;
        StagedEpsr           = Layer.Epsr.ToString("0.####");
        StagedTanD           = Layer.TanD.ToString("0.######");
        StagedMur            = Layer.Mur.ToString("0.####");
        StagedSigmaSm        = Layer.SigmaSm.ToString("0.###e+0");
        IsGroundReference    = Layer.IsGroundReference;

        DrawingLayerOptions.Clear();
        foreach (var l in _owner.Working.Layers)
            DrawingLayerOptions.Add(new DrawingLayerCheckItem(l.Key, l.Name, Layer.DrawingLayers.Contains(l.Key), this));
        _isRefreshing = false;
    }

    public void CommitName()
    {
        var name = StagedName.Trim();
        if (name.Length == 0 || name == Layer.Name) { RefreshFromModel(); return; }
        var before = _owner.SnapshotJson();
        Layer.Name = name;
        _owner.CommitEdit(before, $"Rename stackup layer to {name}");
    }

    public void CommitThickness()
    {
        if (!LayoutUnits.TryParse(StagedThicknessText, _owner.Working.DefaultDisplayUnit,
                LayoutUnits.DefaultDbuPerMicron, out var dbu) || dbu <= 0)
        {
            ThicknessError = "Enter a positive length, e.g. 1.6mm, 35u, 100 um.";
            return;
        }
        ThicknessError = null;
        if (dbu == Layer.ThicknessDbu) return;
        var before = _owner.SnapshotJson();
        Layer.ThicknessDbu = dbu;
        _owner.CommitEdit(before, $"Set thickness of {Layer.Name}");
        RefreshFromModel();
    }

    public void CommitEpsr()
    {
        if (!double.TryParse(StagedEpsr, out var v)) { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.Epsr) < 1e-12) return;
        var before = _owner.SnapshotJson();
        Layer.Epsr = v;
        _owner.CommitEdit(before, $"Set εr of {Layer.Name}");
    }

    public void CommitTanD()
    {
        if (!double.TryParse(StagedTanD, out var v)) { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.TanD) < 1e-15) return;
        var before = _owner.SnapshotJson();
        Layer.TanD = v;
        _owner.CommitEdit(before, $"Set tanδ of {Layer.Name}");
    }

    public void CommitMur()
    {
        if (!double.TryParse(StagedMur, out var v)) { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.Mur) < 1e-12) return;
        var before = _owner.SnapshotJson();
        Layer.Mur = v;
        _owner.CommitEdit(before, $"Set µr of {Layer.Name}");
    }

    public void CommitSigmaSm()
    {
        if (!double.TryParse(StagedSigmaSm, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        { RefreshFromModel(); return; }
        if (System.Math.Abs(v - Layer.SigmaSm) < 1e-6) return;
        var before = _owner.SnapshotJson();
        Layer.SigmaSm = v;
        _owner.CommitEdit(before, $"Set σ of {Layer.Name}");
    }

    partial void OnIsGroundReferenceChanged(bool value)
    {
        if (_isRefreshing || value == Layer.IsGroundReference) return;
        var before = _owner.SnapshotJson();
        Layer.IsGroundReference = value;
        _owner.CommitEdit(before, $"Toggle ground reference for {Layer.Name}");
    }

    // Called by DrawingLayerCheckItem on toggle.
    internal void SetDrawingLayerChecked(LayerKey key, bool isChecked)
    {
        if (_isRefreshing) return;
        bool already = Layer.DrawingLayers.Contains(key);
        if (isChecked == already) return;
        var before = _owner.SnapshotJson();
        if (isChecked)
        {
            // Via/Dielectric: at most one drawing layer — checking a new one replaces, not adds.
            if (!AllowMultipleDrawingLayers) Layer.DrawingLayers.Clear();
            Layer.DrawingLayers.Add(key);
        }
        else
        {
            Layer.DrawingLayers.Remove(key);
        }
        _owner.CommitEdit(before, $"Set drawing layers of {Layer.Name}");
    }
}

/// <summary>One checkable row in a stackup layer's drawing-layer multi-select — a closed set
/// against the current layer table, never free text.</summary>
public sealed partial class DrawingLayerCheckItem : ObservableObject
{
    private readonly StackupLayerRowViewModel _owner;

    public LayerKey Key { get; }
    public string Name  { get; }

    [ObservableProperty] private bool _isChecked;

    public DrawingLayerCheckItem(LayerKey key, string name, bool isChecked, StackupLayerRowViewModel owner)
    {
        Key = key;
        Name = name;
        _owner = owner;
        _isChecked = isChecked;
    }

    partial void OnIsCheckedChanged(bool value) => _owner.SetDrawingLayerChecked(Key, value);
}
