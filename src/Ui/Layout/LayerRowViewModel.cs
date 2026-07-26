using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// VM for one row in the .ctech editor's layer table. Every field is staged and committed
/// explicitly (mirrors <c>CellParameterRowViewModel</c>) — text/numeric fields commit from
/// code-behind (LostFocus / Enter), checkboxes and the color picker commit immediately.
/// After any commit, <see cref="TechEditorViewModel.CommitEdit"/> may replace
/// <see cref="TechEditorViewModel.Working"/> wholesale (undo/redo of a later edit), which orphans
/// this row — expected, a fresh row is rebuilt from the new <see cref="Layer"/> instance.
/// </summary>
public sealed partial class LayerRowViewModel : ObservableObject
{
    private readonly TechEditorViewModel _owner;
    private bool _isRefreshing;

    internal LayerDef Layer { get; }

    [ObservableProperty] private string _stagedName = "";
    [ObservableProperty] private string _stagedLayerNumber = "0";
    [ObservableProperty] private string _stagedDatatype = "0";
    [ObservableProperty] private Rgba   _color;
    [ObservableProperty] private string _stagedFillOpacity = "";
    [ObservableProperty] private string _stagedZOrder = "0";
    [ObservableProperty] private bool   _visible;
    [ObservableProperty] private bool   _selectable;
    [ObservableProperty] private string _stagedPurpose = "";

    public IRelayCommand RemoveCommand    { get; }
    public IRelayCommand DuplicateCommand { get; }
    public IRelayCommand MoveUpCommand    { get; }
    public IRelayCommand MoveDownCommand  { get; }
    public IAsyncRelayCommand<Window?> PickColorCommand { get; }

    public LayerRowViewModel(LayerDef layer, TechEditorViewModel owner)
    {
        Layer = layer;
        _owner = owner;

        RemoveCommand    = new RelayCommand(() => owner.RemoveLayer(this));
        DuplicateCommand = new RelayCommand(() => owner.DuplicateLayer(this));
        MoveUpCommand    = new RelayCommand(() => owner.MoveLayer(this, -1));
        MoveDownCommand  = new RelayCommand(() => owner.MoveLayer(this, +1));
        PickColorCommand = new AsyncRelayCommand<Window?>(PickColorAsync);

        RefreshFromModel();
    }

    /// <summary>Avalonia Color for swatch binding — the Avalonia-facing conversion lives here in
    /// the view model layer (not in the framework-free <see cref="Rgba"/> type itself).</summary>
    public Avalonia.Media.Color SwatchColor => new(Color.A, Color.R, Color.G, Color.B);
    partial void OnColorChanged(Rgba value) => OnPropertyChanged(nameof(SwatchColor));

    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedName        = Layer.Name;
        StagedLayerNumber  = Layer.Key.Layer.ToString();
        StagedDatatype     = Layer.Key.Datatype.ToString();
        Color              = Layer.Color;
        StagedFillOpacity  = Layer.FillOpacity.ToString("0.###");
        StagedZOrder       = Layer.ZOrder.ToString();
        Visible            = Layer.Visible;
        Selectable         = Layer.Selectable;
        StagedPurpose      = Layer.Purpose ?? "";
        _isRefreshing = false;
    }

    partial void OnVisibleChanged(bool value)
    {
        if (_isRefreshing || value == Layer.Visible) return;
        var before = _owner.SnapshotJson();
        Layer.Visible = value;
        _owner.CommitEdit(before, $"Toggle visible for {Layer.Name}");
    }

    partial void OnSelectableChanged(bool value)
    {
        if (_isRefreshing || value == Layer.Selectable) return;
        var before = _owner.SnapshotJson();
        Layer.Selectable = value;
        _owner.CommitEdit(before, $"Toggle selectable for {Layer.Name}");
    }

    public void CommitName()
    {
        var name = StagedName.Trim();
        if (name.Length == 0 || name == Layer.Name) { RefreshFromModel(); return; }
        var before = _owner.SnapshotJson();
        Layer.Name = name;
        _owner.CommitEdit(before, $"Rename layer to {name}");
    }

    public void CommitLayerNumber()
    {
        if (!int.TryParse(StagedLayerNumber, out var v) || v < 0) { RefreshFromModel(); return; }
        if (v == Layer.Key.Layer) return;
        var before = _owner.SnapshotJson();
        Layer.Key = Layer.Key with { Layer = v };
        _owner.CommitEdit(before, $"Set layer number of {Layer.Name}");
    }

    public void CommitDatatype()
    {
        if (!int.TryParse(StagedDatatype, out var v) || v < 0) { RefreshFromModel(); return; }
        if (v == Layer.Key.Datatype) return;
        var before = _owner.SnapshotJson();
        Layer.Key = Layer.Key with { Datatype = v };
        _owner.CommitEdit(before, $"Set datatype of {Layer.Name}");
    }

    public void CommitFillOpacity()
    {
        if (!double.TryParse(StagedFillOpacity, out var v)) { RefreshFromModel(); return; }
        v = System.Math.Clamp(v, 0.0, 1.0);
        if (System.Math.Abs(v - Layer.FillOpacity) < 1e-9) { StagedFillOpacity = v.ToString("0.###"); return; }
        var before = _owner.SnapshotJson();
        Layer.FillOpacity = v;
        _owner.CommitEdit(before, $"Set fill opacity of {Layer.Name}");
        StagedFillOpacity = v.ToString("0.###");
    }

    public void CommitZOrder()
    {
        if (!int.TryParse(StagedZOrder, out var v)) { RefreshFromModel(); return; }
        if (v == Layer.ZOrder) return;
        var before = _owner.SnapshotJson();
        Layer.ZOrder = v;
        _owner.CommitEdit(before, $"Set Z-order of {Layer.Name}");
    }

    public void CommitPurpose()
    {
        var purpose = StagedPurpose.Trim();
        var current = Layer.Purpose ?? "";
        if (purpose == current) return;
        var before = _owner.SnapshotJson();
        Layer.Purpose = purpose.Length == 0 ? null : purpose;
        _owner.CommitEdit(before, $"Set purpose of {Layer.Name}");
    }

    private async Task PickColorAsync(Window? owner)
    {
        if (owner is null) return;
        var result = await new ColorPickerDialog(Layer.Color).ShowDialog<Rgba?>(owner);
        if (result is not { } newColor || newColor == Layer.Color) return;
        var before = _owner.SnapshotJson();
        Layer.Color = newColor;
        _owner.CommitEdit(before, $"Change color of {Layer.Name}");
        Color = newColor;
    }
}
