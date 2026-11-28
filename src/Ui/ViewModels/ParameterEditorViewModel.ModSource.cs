using System;
using System.Linq;
using CircuitRF.Design.Schematic;
using CommunityToolkit.Mvvm.Input;
using RfCore.Signals;

namespace CircuitRF.Ui.ViewModels;

public partial class ParameterEditorViewModel
{
    /// <summary>True for a MOD_Source: the inspector shows its signal summary and the Signal Designer button.</summary>
    public bool IsModSource => _target?.Symbol == SymbolKind.ModSource;

    /// <summary>One line per carrier, read from the Carrier[i] descriptions — what the source will actually play.</summary>
    public string ModSourceSummary
    {
        get
        {
            if (_target is not { Symbol: SymbolKind.ModSource } t) return "";
            var lines = t.Parameters
                .Where(p => p.Name.StartsWith("Carrier[", StringComparison.OrdinalIgnoreCase))
                .Select(p =>
                {
                    try
                    {
                        var c = SignalSpec.ParseCarrier(p.Expression.Trim().Trim('"'));
                        // the label is the family's one-line summary; Pavg means the pulse top or the packet where it is gated
                        if (c.Standard == CarrierStandard.Pcw) return $"{c.Label}, Pavg is the pulse-top power";
                        if (c.Standard == CarrierStandard.Wlan) return $"{c.Label}, Pavg is the packet power";
                        return c.OffsetHz == 0 ? c.Label : $"{c.Label} @ {c.OffsetHz / 1e6:+0.##;-0.##} MHz";
                    }
                    catch (FormatException ex) { return $"{p.Name}: {ex.Message}"; }
                })
                .ToList();
            return lines.Count == 0 ? "No carriers — the default LTE 20 MHz is used." : string.Join("\n", lines);
        }
    }

    /// <summary>The source's carriers include a pulsed-CW one, so its Pavg is the pulse-top power.</summary>
    internal static bool IsPulsedSource(EditableComponent comp)
        => comp.Parameters.Any(p => p.Name.StartsWith("Carrier[", StringComparison.OrdinalIgnoreCase)
                                    && p.Expression.Trim().Trim('"').StartsWith("PCW", StringComparison.OrdinalIgnoreCase));

    /// <summary>Raised by <see cref="OpenSignalDesignerCommand"/>; the view opens the window (it knows the owner).</summary>
    public event Action<EditableComponent>? OpenSignalDesignerRequested;

    [RelayCommand]
    private void OpenSignalDesigner()
    {
        if (_target is { Symbol: SymbolKind.ModSource }) OpenSignalDesignerRequested?.Invoke(_target);
    }

    private void NotifyModSourceState()
    {
        OnPropertyChanged(nameof(IsModSource));
        OnPropertyChanged(nameof(ModSourceSummary));
        OnPropertyChanged(nameof(CanOpenPhasedArrayTool));
    }

    /// <summary>True for a Phased Antenna Array in a schematic its workspace can netlist: the inspector shows the Phased Array button.</summary>
    public bool CanOpenPhasedArrayTool => _target?.Symbol == SymbolKind.PhasedArray && SchematicVm?.PreparePhasedArrayCircuit is not null;

    /// <summary>Raised by <see cref="OpenPhasedArrayToolCommand"/>; the view opens the window (it knows the owner).</summary>
    public event Action<EditableComponent>? OpenPhasedArrayToolRequested;

    [RelayCommand]
    private void OpenPhasedArrayTool()
    {
        if (_target is { Symbol: SymbolKind.PhasedArray }) OpenPhasedArrayToolRequested?.Invoke(_target);
    }
}
