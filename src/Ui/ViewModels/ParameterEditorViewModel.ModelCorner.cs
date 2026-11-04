using System;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// A placed library part's Corner choice (brief vendorlib-13 R-vl13-2d): <c>nominal</c> plus the corners the model's
/// <c>.cnl</c> declares, read ONCE when the part is selected — never on a redraw. The choice is written as the instance's
/// <c>Corner</c> row, a selector the elaborator applies as the model's defaults, so a value the user set on the same
/// instance still wins; such a row is marked "overrides corner …".
/// </summary>
public sealed partial class ParameterEditorViewModel
{
    /// <summary><c>nominal</c>, then the model's corners; empty for a part whose model declares none.</summary>
    public ObservableCollection<string> VendorPartCorners { get; } = [];

    [ObservableProperty] private string? _vendorPartCorner;

    /// <summary>The model's top cell as its netlist was read at selection; null when not read.</summary>
    private Cell? _vendorModelTop;

    /// <summary>The part the corners were last read for.</summary>
    private (CircuitRF.Design.Schematic.EditableComponent?, string?) _vendorCornersKey;

    public bool HasVendorPartCorners => VendorPartCorners.Count > 1;

    /// <summary>Reads the selected part's corners from its model's <c>.cnl</c>, and marks the rows that beat the corner.</summary>
    private void RefreshVendorModelCorners()
    {
        bool was = _isRefreshing;
        _isRefreshing = true;
        // Read once per part selected: a refresh after an edit to the same part reuses what was read.
        var key = (_target, _target?.CellRef);
        bool reread = key != _vendorCornersKey;
        _vendorCornersKey = key;
        if (reread) { VendorPartCorners.Clear(); _vendorModelTop = null; }
        if (reread && _target?.CellRef is { } cellRef
            && VendorModelRefs.Resolve(cellRef, _schematicVm?.EditModel.SchematicDirectory, _schematicVm?.KitWorkspaceRoot, out _) is { } r
            && ModelNetlist.Read(r.ModelDir, out _) is { } lib
            && lib.Find(r.Entry.Model!.Cell) is { } top)
        {
            _vendorModelTop = top;
            if (top.Corners.Count > 0)
            {
                VendorPartCorners.Add(CellCorner.Nominal);
                foreach (var c in top.Corners) VendorPartCorners.Add(c.Name);
            }
        }
        string chosen = _target is null ? "" : CellCorner.SelectedBy(_target.Parameters.Select(p => new ParameterAssignment(p.Name, p.Expression))) ?? CellCorner.Nominal;
        // A selection the model no longer has (re-exported with the corner renamed or removed) stays on the list, so the
        // picker shows what the run will refuse and choosing nominal writes a change, rather than showing nominal already.
        if (!chosen.Equals(CellCorner.Nominal, StringComparison.OrdinalIgnoreCase) && chosen.Length > 0
            && !VendorPartCorners.Any(c => c.Equals(chosen, StringComparison.OrdinalIgnoreCase)))
        {
            if (VendorPartCorners.Count == 0) VendorPartCorners.Add(CellCorner.Nominal);
            VendorPartCorners.Add(chosen);
        }
        VendorPartCorner = VendorPartCorners.FirstOrDefault(c => c.Equals(chosen, StringComparison.OrdinalIgnoreCase))
                        ?? VendorPartCorners.FirstOrDefault();
        _isRefreshing = was;
        OnPropertyChanged(nameof(HasVendorPartCorners));
        MarkCornerOverrides();
    }

    partial void OnVendorPartCornerChanged(string? value)
    {
        if (_isRefreshing || _target is null || _schematicVm is null || value is null) return;
        string current = CellCorner.SelectedBy(_target.Parameters.Select(p => new ParameterAssignment(p.Name, p.Expression))) ?? CellCorner.Nominal;
        if (current.Equals(value, StringComparison.OrdinalIgnoreCase)) return;
        SetOrAddParameter(CellCorner.SelectorParameter, value);
        MarkCornerOverrides();
    }

    /// <summary>Each row the chosen corner binds and the user has set to something other than the model's default reads
    /// "overrides corner &lt;name&gt;"; every other row reads nothing.</summary>
    private void MarkCornerOverrides()
    {
        var corner = _vendorModelTop?.Corners.FirstOrDefault(c => c.Name.Equals(VendorPartCorner, StringComparison.OrdinalIgnoreCase));
        foreach (var row in Rows)
        {
            string note = "";
            if (corner is not null && corner.Bindings.Any(b => b.Name == row.Name)
                && _vendorModelTop!.Parameters.FirstOrDefault(p => p.Name == row.Name) is { } decl
                && _target?.Parameters.FirstOrDefault(p => p.Name == row.Name) is { } set
                && !string.IsNullOrWhiteSpace(set.Expression) && !RepeatsDefault(set, decl))
                note = $"overrides corner {corner.Name}";
            row.CornerNote = note;
        }
    }

    /// <summary>The extractor's own test (NetExtractor's vendor-model emission): a row is not an override when it repeats
    /// the model's default in both expression and unit — 10 nF against a 10 pF default IS one, and beats the corner.</summary>
    private static bool RepeatsDefault(EditableParameter set, ParameterDeclaration decl)
    {
        string unit = UnitNormalizer.ToEngineUnit(set.Unit);
        return set.Expression.Trim() == decl.DefaultExpression.Trim()
               && (unit.Length > 0 ? unit : null) == (string.IsNullOrEmpty(decl.Unit) ? null : UnitNormalizer.ToEngineUnit(decl.Unit));
    }
}
