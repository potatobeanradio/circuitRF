using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of an SnP's symbol menu: what <c>Symbol</c> is set to (empty for the SnP's own box), its label,
/// and the kind its picture is drawn as.</summary>
public sealed record SnpSymbolRow(string Value, string Label, SymbolKind Kind, int PortCount);

// An SnP's symbol picker (brief vendorlib-11 R-vl11-2d), in the shape of the Verilog-A one (brief 7): the current glyph
// as one picture, and a click opens the menu of the glyphs an SnP of this many ports can be drawn as. What may be
// listed and what a value draws are SnpGlyph's, in src/Design.
public partial class ParameterEditorViewModel
{
    public ObservableCollection<SnpSymbolRow> SnpSymbols { get; } = [];

    /// <summary>The kind the current symbol's picture is drawn as — the glyph, or SnP for its own box.</summary>
    [ObservableProperty] private SymbolKind _snpSymbolKind = SymbolKind.Snp;
    [ObservableProperty] private int _snpSymbolPortCount = 2;
    [ObservableProperty] private string _snpSymbolLabel = "";

    private void RefreshSnpSymbolPicker()
    {
        SnpSymbols.Clear();
        if (_target?.Symbol != SymbolKind.Snp) return;
        int ports = _target.PortCount;
        SnpSymbols.Add(new SnpSymbolRow("", "Box", SymbolKind.Snp, ports));
        foreach (var kind in SnpGlyph.Choices(ports))
            SnpSymbols.Add(new SnpSymbolRow(kind.ToString(), ComponentTypeRegistry.EngineReference(kind), kind, ports));

        var glyph = SnpGlyph.Of(_target);
        // A glyph written by hand that the menu does not list is still the instance's, and the menu shows it.
        if (glyph is { } g && SnpSymbols.All(r => r.Kind != g))
            SnpSymbols.Add(new SnpSymbolRow(g.ToString(), ComponentTypeRegistry.EngineReference(g), g, ports));
        SnpSymbolKind = glyph ?? SymbolKind.Snp;
        SnpSymbolPortCount = ports;
        SnpSymbolLabel = glyph is { } k ? ComponentTypeRegistry.EngineReference(k) : "Box";
    }

    /// <summary>A menu row: sets <c>Symbol</c>, one undo entry. The pins move to the glyph's; wires stay where drawn.</summary>
    [RelayCommand]
    private void ChooseSnpSymbol(SnpSymbolRow? row)
    {
        if (row is null || _target is null || _schematicVm is null || _target.Symbol != SymbolKind.Snp) return;
        var updated  = _target.Parameters.Select(p => p.Clone()).ToList();
        var existing = updated.FirstOrDefault(p => p.Name.Equals(SnpGlyph.SymbolParameter, StringComparison.Ordinal));
        if ((existing?.Expression ?? "").Trim() == row.Value) return;
        if (existing is not null) existing.Expression = row.Value;
        else updated.Add(new EditableParameter { Name = SnpGlyph.SymbolParameter, Expression = row.Value, ShowOnSchematic = false });
        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, updated));
    }
}
