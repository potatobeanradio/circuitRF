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
        string note = "";
        if (spec is not { } c || Spec("Z0") is not { } z0 || Spec("F") is not { } f
            || (kind == SymbolKind.Mcbl ? SiteGap(ref note) : 0.0) is not { } gap)
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
                    + "Coupler (Ou) " + Modes(d.Ze, d.Zo) + Sites(d.Plan)
                    : "",
                _ => MarchandBalunPlan.Build(z0, c, f, gap, s) is var d
                    ? "Each section: " + Geometry(d.WMeters, d.SMeters, d.LengthMeters, d.Modes) + "\n"
                    + Modes(d.Modes.Ze, d.Modes.Zo) + Sites(d.Plan)
                    : "",
            }, note);
        }
        catch (ParameterRefusalException ex)
        {
            return ("", ex.Message);
        }
    }

    /// <summary>The coupled array (MCLA): its geometry is stored, so the readout is what the form makes of it — each
    /// strip's width and ends, the gaps, the taps — and the sites, a via or a C on each end pin.</summary>
    private (string Text, string Note) CoupledArrayReadout()
    {
        bool expression = false;
        double Real(string name, double fallback)
        {
            if (Spec(name, fallback) is { } v) return v;
            expression = true;
            return fallback;
        }
        try
        {
            var spec = CoupledArraySpec.Of(Real, name => _target?.Parameters.FirstOrDefault(p => p.Name == name)?.Expression);
            if (expression) return ("", "A dimension is an expression, so the array is known only at run time.");
            var (h, t, er) = ResolveMlinSubstrate();
            var d = CoupledArrayPlan.Build(spec, new MicrostripSubstrate(h, t, er, 0.0, 0.0));
            int pin = 0;
            string End(StripEnd e) => e switch
            {
                StripEnd.Short => $"short ({CoupledArrayPlan.EndPin(++pin)})",
                StripEnd.Site => $"C site ({CoupledArrayPlan.EndPin(++pin)})",
                StripEnd.Join => "joined",
                StripEnd.Port => "port",
                _ => "open",
            };
            return ($"{spec.N} strips, {spec.Form}, {spec.Feed} feed; L {Mm(spec.L)}\n"
                  + string.Concat(d.Ends.Select((e, k) =>
                        $"Strip {k + 1}: W {Mm(spec.W[k])}, near {End(e.Near)}, far {End(e.Far)}"
                      + (k < spec.S.Count ? $"; gap below {Mm(spec.S[k])}" : "") + "\n"))
                  + (spec.Feed == CoupledArrayFeed.Tapped
                        ? $"Taps {Mm(spec.Tap)} from the outer strips' grounded ends, lines {Mm(spec.Wf)} wide" : "Ports on the outer strips' ends")
                  + Sites(d.Plan), "");
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
