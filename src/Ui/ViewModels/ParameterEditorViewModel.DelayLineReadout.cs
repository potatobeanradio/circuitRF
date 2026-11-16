using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.ViewModels;

// The delay line's readout (MDLY): its width, θ as a straight length, the copper's length along its centre line and its
// extent, and each run as DelayLinePlan lays it on the substrate a run injects, through the divider readout's own rows
// (ParameterEditorViewModel.DividerReadout.cs).
public partial class ParameterEditorViewModel
{
    private (string Text, string Note) DelayLineReadout()
    {
        if (Spec("Z0") is not { } z0 || Spec("F") is not { } f || Spec("Theta") is not { } theta
            || Spec("Turns", 2.0) is not { } turns || Spec("Pitch", 0.0) is not { } pitch || Spec("Side", 1.0) is not { } side
            || Spec("Coupled", 0.0) is not { } coupled)
            return ("", "A spec or a layout choice is an expression, so the line's dimensions are known only at run time.");
        var (h, t, er) = ResolveMlinSubstrate();
        var style = Enum.TryParse(_target?.Parameters.FirstOrDefault(p => p.Name == "Style")?.Expression?.Trim(),
            ignoreCase: true, out PlanRouteStyle st) && Enum.IsDefined(st) ? st : PlanRouteStyle.Meander;
        try
        {
            var d = DelayLinePlan.Build(z0, f, theta, new MicrostripSubstrate(h, t, er, 0.0, 0.0), style, Corner(), turns,
                pitch, side, coupled != 0.0);
            var r = d.Route;
            string Run(PlanSection piece) => piece switch
            {
                PlanLine l => $"line {Mm(l.L)}",
                PlanCurve c => $"{(Math.Abs(c.AngleDeg) == 180.0 ? "half-turn" : "curve")} r {Mm(c.Radius)}",
                PlanBend b => b.M is null ? "square corner" : "mitred corner",
                PlanCoupledStrips m => $"{m.W.Count} coupled legs of {Mm(m.L)}, {Mm(m.S[0])} apart",
                _ => piece.GetType().Name,
            };
            return ($"W {Mm(d.WMeters, "0.#####")} ({Ohm(z0)}), εeff {Eeff(d.Eeff)}\n"
                  + $"θ {(theta * 180.0 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture)}° at F: "
                  + $"{Mm(d.LineMeters)} of straight line\n"
                  + $"Drawn {Mm(d.CentreLineMeters)} along its centre line, {Mm(r.MaxX - r.MinX)} × {Mm(r.MaxY - r.MinY)}"
                  + (d.PitchMeters > 0.0 ? $", legs {Mm(d.PitchMeters)} apart" : "") + "\n"
                  + "Runs: " + string.Join(", ", r.Sections.Select(Run)), "");
        }
        catch (ParameterRefusalException ex)
        {
            return ("", ex.Message);
        }
    }
}
