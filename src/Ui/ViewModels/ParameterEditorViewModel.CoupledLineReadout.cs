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
        if (kind == SymbolKind.Msps) return SchiffmanReadout();
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
                SymbolKind.Mclc => CouplerReadout(c, z0, f, s),
                SymbolKind.Midc => LangePlan.Build(c, z0, f, s, Spec("Fingers", LangePlan.Fingers) ?? LangePlan.Fingers) is var d
                    ? $"{d.Fingers} fingers: " + Geometry(d.WMeters, d.SMeters, d.LengthMeters, d.Modes) + "\n"
                    + "Adjacent pair " + Modes(d.Modes.Ze, d.Modes.Zo) + "\n"
                    + "Coupler (Ou) " + Modes(d.Ze, d.Zo) + (d.Fingers == 4
                        ? "\nA jumper from each pin k to Jk joins the fingers"
                        : $"\nJumpers join J(4m+k) to pin k's conductor, J1-J{2 * (d.Fingers - 2)}")
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

    /// <summary>MCLC: the plain pair as always; with an option, what that option adds.</summary>
    private string CouplerReadout(double c, double z0, double f, MicrostripSubstrate s)
    {
        var comp = Enum.TryParse(_target?.Parameters.FirstOrDefault(p => p.Name == "Compensation")?.Expression?.Trim(),
            ignoreCase: true, out CouplerCompensation cc) && Enum.IsDefined(cc) ? cc : CouplerCompensation.None;
        double broadside = Spec("Broadside", 0.0) ?? 0.0;
        BroadsideStack? stack = null;
        if (broadside != 0.0)
            stack = CircuitRF.Design.Layout.PCells.SubstrateResolver.ResolveBroadsideStack(
                _schematicVm is { } svm ? SchematicTechnology.Of(svm.EditModel) : null, NonDefaultLayerChoice("SignalLayer")).Stack;
        var d = CoupledLineCouplerPlan.Build(c, z0, f, s, Spec("Sections", 1.0) ?? 1.0,
            Spec("Band", CoupledLineCouplerPlan.DefaultBand) ?? CoupledLineCouplerPlan.DefaultBand, Spec("Tandem", 0.0) ?? 0.0,
            comp, broadside, stack);
        if (d.Broadside is { } b && d.BroadsideModes is { } bm)
            return $"{(b.Stripline ? "BCLIN" : "MBCLIN")} on {b.LayerA}/{b.LayerB}: W {Mm(d.WMeters)}, O {Mm(d.OffsetMeters)}, "
                 + $"L {Mm(d.LengthMeters)} (λg/4 at F, εeff {F3(bm.EeffE)} even / {F3(bm.EeffO)} odd)\n" + Modes(bm.Ze, bm.Zo);
        if (d.Sections.Count > 1)
            return string.Join("\n", d.Sections.Select((x, k) => $"Section {k + 1}: "
                     + Geometry(x.Pair.WMeters, x.Pair.SMeters, x.Pair.QuarterWaveMeters, x.Pair.Modes)
                     + ", " + Modes(x.Ze, x.Zo)))
                 + $"\nCoupling {F3(c)} ± {F3(d.RippleDb)} dB over the band";
        string text = Geometry(d.WMeters, d.SMeters, d.LengthMeters, d.Modes) + "\n" + Modes(d.Modes.Ze, d.Modes.Zo);
        if (d.SubCouplingDb > 0)
            text = $"Each coupler {F3(d.SubCouplingDb)} dB: " + text + "\nA jumper on J1-J2 and on J3-J4 closes the crossovers";
        return d.Compensation switch
        {
            CouplerCompensation.Capacitive => text + $"\nCapacitor {F3(d.CompensationFarads * 1e12)} pF on 1-3 and on 2-4",
            CouplerCompensation.Inductive => text + $"\nSeries {Ohm(d.CompensationOhms)} at each end: a line W "
                + $"{Mm(d.InductiveSection.W, "0.#####")}, L {Mm(d.InductiveSection.L)}",
            CouplerCompensation.Wiggly => text + $"\nWiggly edges: odd path {F3(d.OddLengthFactor)}× the length (approximate "
                + "in the circuit; EM is the judge)",
            _ => text,
        };
    }

    /// <summary>MSPS: the C-section's pair and ρ, the reference line, and the phase's ripple over the band.</summary>
    private (string Text, string Note) SchiffmanReadout()
    {
        if (Spec("Z0") is not { } z0 || Spec("F") is not { } f || Spec("Phase") is not { } phase || Spec("Band") is not { } band)
            return ("", "A spec is an expression, so the part's dimensions are known only at run time.");
        var (h, t, er) = ResolveMlinSubstrate();
        var reference = Enum.TryParse(_target?.Parameters.FirstOrDefault(p => p.Name == "Reference")?.Expression?.Trim(),
            ignoreCase: true, out SchiffmanReference r) && Enum.IsDefined(r) ? r : SchiffmanReference.Straight;
        try
        {
            var d = SchiffmanPlan.Build(z0, f, phase * 180.0 / Math.PI, band, new MicrostripSubstrate(h, t, er, 0.0, 0.0), reference);
            return ($"C-section: " + Geometry(d.WMeters, d.SMeters, d.LengthMeters, d.Modes) + "\n"
                  + $"ρ = Ze/Zo {F3(d.Rho)}: " + Modes(d.Ze, d.Zo) + "\n"
                  + $"Reference W {Mm(d.W0Meters, "0.#####")} (Z0), {F3(d.ReferenceTurns * 90.0)}° at F, {Mm(d.ReferenceMeters)}\n"
                  + $"Phase {F3(d.PhaseDeg)}° ± {F3(d.RippleDeg)}° over the band (TEM)", "");
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
