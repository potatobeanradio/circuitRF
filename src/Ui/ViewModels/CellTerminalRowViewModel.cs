using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// One row of the cell Properties panel's <b>Terminals</b> section: which layout pin is which
/// schematic port (<c>brief-lvs-1-terminal-map.md</c> R-lvs1-4c).
///
/// <para><b>Staged, and committed as the WHOLE block.</b> The map's defects are relational — a
/// duplicate port, one layout pin claimed twice — so a row that wrote itself the moment a character
/// was typed would leave the cell in a state the validator reports as broken while the user is still
/// typing the other half of the fix. The view commits on LostFocus/Enter, through
/// <see cref="CellParameterEditorViewModel.CommitTerminals"/>.</para>
/// </summary>
public sealed partial class CellTerminalRowViewModel : ObservableObject
{
    private readonly CellParameterEditorViewModel _editorVm;

    [ObservableProperty] private int    _port;
    [ObservableProperty] private string _name = "";

    /// <summary>The layout pins this terminal is, comma-separated. SEVERAL is the ordinary spelling
    /// for a bonded ground or a FET's two source pads, and a list is what the <c>.ccell</c> carries —
    /// so the field takes one, rather than making the several-pin case unreachable from the panel.</summary>
    [ObservableProperty] private string _layoutPins = "";

    /// <summary>True while this row comes from a DERIVATION rather than from the cell's own file.
    /// The section says so once, at the top; the row is shown read-only-looking rather than disabled,
    /// because editing one is exactly how a user turns a derivation into a declaration.</summary>
    [ObservableProperty] private bool _isDerived;

    public IRelayCommand RemoveCommand { get; }

    public CellTerminalRowViewModel(
        int port, string name, IReadOnlyList<string> layoutPins, bool isDerived,
        CellParameterEditorViewModel editorVm)
    {
        _port       = port;
        _name       = name;
        _layoutPins = string.Join(", ", layoutPins);
        _isDerived  = isDerived;
        _editorVm   = editorVm;

        RemoveCommand = new RelayCommand(() => _editorVm.RemoveTerminalRow(this));
    }

    /// <summary>The row as the file spells it. An empty field is an empty LIST, not a pin named "" —
    /// a port that is deliberately mapped to nothing is a legal, and reported, state.</summary>
    internal CcellTerminal ToTerminal() => new()
    {
        Port      = Port,
        Name      = Name ?? "",
        LayoutPin = [.. (LayoutPins ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)],
    };
}
