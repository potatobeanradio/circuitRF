using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Devices;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of a Verilog-A model's symbol menu: what <c>Symbol</c> is set to, its label, and the reference its
/// picture is drawn from.</summary>
public sealed record VerilogASymbolRow(string Value, string Label, string GlyphRef);

// The VerilogA panel's symbol picker (brief-vendorlib-7 R-vl7-3): the current symbol as one picture, and a click opens
// the menu of every symbol this model can take — the Model Composer's Export picker, in the same shape. The listing
// itself is VerilogASymbolProvider.Choices, in src/Design, so nothing here decides what a model may be drawn as.
public partial class ParameterEditorViewModel
{
    /// <summary>The menu's rows, rebuilt with the panel.</summary>
    public ObservableCollection<VerilogASymbolRow> VerilogASymbols { get; } = [];

    /// <summary>The reference the current symbol's picture is drawn from.</summary>
    [ObservableProperty] private string _verilogASymbolGlyphRef = "";

    /// <summary>What the current symbol is called, under its picture.</summary>
    [ObservableProperty] private string _verilogASymbolLabel = "";

    /// <summary>Picks a <c>.csym</c> for Browse…; supplied by the view, which owns the file dialog.</summary>
    public Func<Task<string?>>? PickVerilogASymbolFileAsync { get; set; }

    /// <summary>Rebuilds the picture and the menu from the instance — a readout, so it follows an undo too.</summary>
    private void RefreshVerilogASymbolPicker()
    {
        VerilogASymbols.Clear();
        if (_target?.Symbol != Design.Schematic.SymbolKind.VerilogA) return;

        int    pins      = _target.PortCount;
        string terminals = ParamValue(_target, VerilogASymbolProvider.TerminalsParameter);
        string? symbol   = _target.Parameters
                                  .FirstOrDefault(p => p.Name.Equals(VerilogASymbolProvider.SymbolParameter, StringComparison.Ordinal))
                                  ?.Expression?.Trim();

        foreach (var c in VerilogASymbolProvider.Choices(pins, terminals))
            VerilogASymbols.Add(new VerilogASymbolRow(c.Value, c.Label,
                                VerilogASymbolProvider.GlyphRefFor(pins, terminals, c.Value)));

        // No Symbol at all is an instance saved before the parameter existed: the box, and the menu says so.
        string current = symbol ?? VerilogASymbolProvider.BoxName;
        VerilogASymbolGlyphRef = VerilogASymbolProvider.GlyphRefFor(pins, terminals, current);
        VerilogASymbolLabel    = VerilogASymbols.FirstOrDefault(r => r.Value == current)?.Label
                               ?? Path.GetFileName(current.Trim('"'));
    }

    /// <summary>A menu row: sets <c>Symbol</c>, one undo entry (R-vl7-3c).</summary>
    [RelayCommand]
    private void ChooseVerilogASymbol(VerilogASymbolRow? row)
    {
        if (row is not null) SetVerilogASymbol(row.Value);
    }

    /// <summary>Browse… for a <c>.csym</c>, stored by <c>File</c>'s own rule so the model and its symbol travel
    /// together.</summary>
    [RelayCommand]
    private async Task BrowseVerilogASymbolAsync()
    {
        if (_schematicVm is null || PickVerilogASymbolFileAsync is null) return;
        if (await PickVerilogASymbolFileAsync() is not { } path) return;
        SetVerilogASymbol(SnpPathPolicy.ToStored(path, _schematicVm.WorkspaceRoot));
    }

    /// <summary>
    /// Writes <c>Symbol</c> by its EXACT name — a model's own <c>symbol</c> is a different parameter — adding it when
    /// the instance predates it. A symbol change moves the pins; wires stay where they were drawn.
    /// </summary>
    private void SetVerilogASymbol(string value)
    {
        if (_target is null || _schematicVm is null || _target.Symbol != Design.Schematic.SymbolKind.VerilogA) return;
        var updated  = _target.Parameters.Select(p => p.Clone()).ToList();
        var existing = updated.FirstOrDefault(p => p.Name.Equals(VerilogASymbolProvider.SymbolParameter, StringComparison.Ordinal));
        if (existing is not null && (existing.Expression ?? "").Trim() == value) return;
        if (existing is not null) existing.Expression = value;
        else updated.Add(new EditableParameter
             { Name = VerilogASymbolProvider.SymbolParameter, Expression = value, ShowOnSchematic = false });
        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, updated));
    }

    /// <summary>True for a VerilogA parameter the panel owns rather than a row: the symbol and the stored terminal
    /// names, which are artwork and are never typed.</summary>
    private static bool IsVerilogASymbolPanelParameter(string name)
        => name.Equals(ComponentModelFactory.VerilogASymbolParam,    StringComparison.Ordinal)
        || name.Equals(ComponentModelFactory.VerilogATerminalsParam, StringComparison.Ordinal);
}
