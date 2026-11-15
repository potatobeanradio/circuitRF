using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.ViewModels;

// ──────────────────────────────────────────────────────────────────────────────
//  The coupled-line planned passives' readout (MCLC, MIDC, MCBL): what Coupling/ZL, Z0 and F come out to
//  on the substrate a run injects — W, S, L and the modes — from the plan functions the model and the
//  PCell call, so the numbers shown are the ones simulated and drawn. Shown in the divider readout's
//  slot (ParameterEditorViewModel.DividerReadout.cs).
// ──────────────────────────────────────────────────────────────────────────────

public partial class ParameterEditorViewModel
{
    private (string Text, string Note) CoupledLineReadout(SymbolKind kind)
    {
        double? spec = kind == SymbolKind.Mcbl ? Spec("ZL") : Spec("Coupling");
        if (spec is not { } c || Spec("Z0") is not { } z0 || Spec("F") is not { } f
            || (kind == SymbolKind.Mcbl ? Spec("Gap") : 0.0) is not { } gap)
            return ("", "A spec is an expression, so the part's dimensions are known only at run time.");
        var (h, t, er) = ResolveMlinSubstrate();
        var s = new MicrostripSubstrate(h, t, er, 0.0, 0.0);
        try
        {
            return (kind switch
            {
                SymbolKind.Mclc => CoupledLineCouplerPlan.Build(c, z0, f, s) is var d
                    ? Geometry(d.WMeters, d.SMeters, d.LengthMeters, d.Modes) + "\n" + Modes(d.Modes.Ze, d.Modes.Zo)
                    : "",
                SymbolKind.Midc => LangePlan.Build(c, z0, f, s) is var d
                    ? $"{d.Fingers} fingers: " + Geometry(d.WMeters, d.SMeters, d.LengthMeters, d.Modes) + "\n"
                    + "Adjacent pair " + Modes(d.Modes.Ze, d.Modes.Zo) + "\n"
                    + "Coupler (Ou) " + Modes(d.Ze, d.Zo) + "\nA jumper from each pin k to Jk joins the fingers"
                    : "",
                _ => MarchandBalunPlan.Build(z0, c, f, gap, s) is var d
                    ? "Each section: " + Geometry(d.WMeters, d.SMeters, d.LengthMeters, d.Modes) + "\n"
                    + Modes(d.Modes.Ze, d.Modes.Zo) + "\nA via to ground on G2 and on G3"
                    : "",
            }, "");
        }
        catch (ParameterRefusalException ex)
        {
            return ("", ex.Message);
        }
    }

    private static string Geometry(double w, double gap, double l, KirschningJansenCoupled.Modes m)
        => $"W {Mm(w)}, S {Mm(gap)}, L {Mm(l)} (λg/4 at F, εeff {F3(m.EeffE)} even / {F3(m.EeffO)} odd)";

    private static string Modes(double ze, double zo)
        => $"Ze {Ohm(ze)}, Zo {Ohm(zo)}, coupling {F3(-20.0 * Math.Log10(CoupledLineSynthesis.Coupling(ze, zo)))} dB";

    private static string F3(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
