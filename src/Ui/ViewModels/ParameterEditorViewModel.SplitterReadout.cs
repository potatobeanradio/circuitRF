using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.ViewModels;

// The Gysel, Bagley and tee dividers' readout (MGPD, MBPD, MTPD): each line's impedance, width and length as GyselPlan,
// BagleyPlan and TeeDividerPlan derive them on the substrate a run injects, and the rings' turns, shown through the
// divider readout's own rows (ParameterEditorViewModel.DividerReadout.cs). Widths to the plan's 2 nm grid, as the
// divider's are, so a line drawn to meet a pin can be typed exactly.
public partial class ParameterEditorViewModel
{
    private (string Text, string Note) SplitterReadout(SymbolKind kind)
    {
        if (Spec("Z0") is not { } z0 || Spec("F") is not { } f
            || (kind == SymbolKind.Mbpd ? Spec("N", 3.0) : 0.0) is not { } n || (kind == SymbolKind.Mtpd ? Spec("K", 1.0) : 1.0) is not { } k)
            return ("", "A spec is an expression, so the divider's dimensions are known only at run time.");
        var (h, t, er) = ResolveMlinSubstrate();
        var s = new MicrostripSubstrate(h, t, er, 0.0, 0.0);
        static string Line(WilkinsonLine l, int quarters = 1)
            => $"{l.Name}: {Ohm(l.Z)}, W {Mm(l.WMeters, "0.#####")}, L {Mm(quarters * l.QuarterWaveMeters)}"
             + (quarters == 2 ? " (λg/2 at F)" : " (λg/4 at F)");
        try
        {
            switch (kind)
            {
                case SymbolKind.Mgpd:
                {
                    var d = GyselPlan.Build(z0, f, s, Corner());
                    return ($"W {Mm(d.W0Meters, "0.#####")} (Z0)\n{Line(d.Lines[0])}\n{Line(d.Lines[1])}\n{Line(d.Lines[2], 2)}\n"
                          + Turns(d.Ring) + $"\nR = {Ohm(d.IsolationOhms)} from each of R2 and R3 to ground", "");
                }
                case SymbolKind.Mbpd:
                {
                    var d = BagleyPlan.Build(z0, f, n, s, Corner());
                    return ($"W {Mm(d.W0Meters, "0.#####")} (Z0)\n{Line(d.Lines[0])}\n{Line(d.Lines[1], 2)}\n" + Turns(d.Ring)
                          + "\nOutputs 2, 4 … leave a quarter period behind the input, 3, 5 … half a period from them", "");
                }
                default:
                {
                    var d = TeeDividerPlan.Build(z0, f, s, k);
                    return ($"W {Mm(d.W0Meters, "0.#####")} (Z0)\n" + string.Join("\n", d.Lines.Select(l => Line(l))), "");
                }
            }
        }
        catch (ParameterRefusalException ex)
        {
            return ("", ex.Message);
        }
    }

    /// <summary>A ring's turns: the half-turns' radius, or each end's side between its corners.</summary>
    private static string Turns(RingShape r)
        => r.Corner == PlanCorner.Curve
            ? $"Half-turn radius {Mm(r.RadiusMeters)}"
            : $"Each end: a {Mm(r.SideMeters)} side between two {(r.Corner == PlanCorner.Miter ? "mitred" : "square")} corners, "
            + $"each standing for {Mm(r.CornerMeters)} of line at F";
}
