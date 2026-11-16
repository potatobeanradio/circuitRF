using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.ViewModels;

// The hybrids' readout (MBLC, MRRC): each arm's width and length as BranchLinePlan and RatRacePlan derive them on the
// substrate a run injects, shown through the divider readout's own rows (ParameterEditorViewModel.DividerReadout.cs).
// Widths to the plan's 2 nm grid, as the divider's are, so a line drawn to meet a pin can be typed exactly.
public partial class ParameterEditorViewModel
{
    private (string Text, string Note) HybridReadout(SymbolKind kind)
    {
        if (Spec("Z0") is not { } z0 || Spec("F") is not { } f)
            return ("", "Z0 or F is an expression, so the hybrid's dimensions are known only at run time.");
        var (h, t, er) = ResolveMlinSubstrate();
        var s = new MicrostripSubstrate(h, t, er, 0.0, 0.0);
        try
        {
            if (kind == SymbolKind.Mrrc)
            {
                var r = RatRacePlan.Build(z0, f, s, Corner());
                return ($"W {Mm(r.W0Meters, "0.#####")} (Z0), ring {Mm(r.RingWMeters, "0.#####")} ({Ohm(r.RingZ0)})\n"
                      + $"L {Mm(r.QuarterWaveMeters)} (λg/4 at F, εeff {Eeff(r.RingEeff)}); the ring is 6 of them\n"
                      + (r.Corner == PlanCorner.Curve
                          ? $"Half-turn radius {Mm(r.RadiusMeters)}"
                          : $"Each end: a {Mm(r.SideMeters)} side between two {(r.Corner == PlanCorner.Miter ? "mitred" : "square")} "
                          + $"corners, each standing for {Mm(r.CornerMeters)} of ring at F"), "");
            }
            if (Spec("N") is not { } n)
                return ("", "N is an expression, so the coupler's dimensions are known only at run time.");
            var d = BranchLinePlan.Build(z0, f, n, s);
            string Arm(string name, BranchLineArm a) => $"{name} {Mm(a.WMeters, "0.#####")} ({Ohm(a.Z)}), L {Mm(a.LMeters)}";
            var lines = new List<string> { $"W {Mm(d.W0Meters, "0.#####")} (Z0 feeds)", Arm("Series arms", d.Series[0]) };
            lines.Add(Arm(d.Sections == 1 ? "Shunt arms" : "Outer shunt arms", d.Shunts[0]));
            if (d.Sections == 2) lines.Add(Arm("Middle shunt arm", d.Shunts[1]));
            // Three and four sections: each distinct shunt arm from the outside in, as the arms mirror about the middle.
            for (int i = 1; d.Sections > 2 && i <= d.Sections / 2; i++)
                lines.Add(Arm(i < d.Sections / 2 ? "Next shunt arms" : d.Sections % 2 == 0 ? "Middle shunt arm" : "Middle shunt arms",
                    d.Shunts[i]));
            lines.Add("L: between the junctions' reference planes, λg/4 at F");
            return (string.Join("\n", lines), "");
        }
        catch (ParameterRefusalException ex)
        {
            return ("", ex.Message);
        }
    }

    private static string Eeff(double e) => e.ToString("0.###", CultureInfo.InvariantCulture);
}
