using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Ui.ViewModels;

// The ladder's readout (MLAD): what each section of its list is at F on the substrate a run injects — a line's or a stub's
// impedance and electrical length (a radial end's reactance with it), a taper's end impedances and length, a gap's
// capacitances, a site's part — from LadderPlan.Build, the function the model and the PCell call, shown in the divider
// readout's slot (ParameterEditorViewModel.DividerReadout.cs), with the sites' parts and spans after it. A list that does
// not parse shows the refusal, which names the section.
public partial class ParameterEditorViewModel
{
    private (string Text, string Note) LadderReadout()
    {
        if (Spec("Z0") is not { } z0 || Spec("F") is not { } f)
            return ("", "Z0 or F is an expression, so the sections' impedances are known only at run time.");
        var (h, t, er) = ResolveMlinSubstrate();
        string footprintNote = "";
        double gap = SiteGap(ref footprintNote) ?? LadderPlan.DefaultGap;
        try
        {
            var d = LadderPlan.Build(_target?.Parameters.FirstOrDefault(p => p.Name == "Sections")?.Expression, z0, f,
                new MicrostripSubstrate(h, t, er, 0.0, 0.0), gap);
            var lines = d.Sections.Select((s, i) => $"{i + 1} " + (s, d.AtF[i]) switch
            {
                (LadderLine, var a) => $"line: {Ohm(a.Z)}, {Deg(a.ThetaDeg)}",
                (LadderTaper, var a) => $"taper: {Ohm(a.Z)} to {Ohm(a.Z2)}, {Deg(a.ThetaDeg)}",
                (LadderGap, var a) => $"gap at W {Mm(a.WAt, "0.#####")}: Cg {Ff(a.SeriesF)}, Cp {Ff(a.ShuntF)} each end",
                (LadderStub b, var a) => $"stub, {b.End.ToString().ToLowerInvariant()}, "
                    + (b.Side == LadderStubSide.Both ? "both sides" : b.Side.ToString().ToLowerInvariant())
                    + $": {Ohm(a.Z)}, {Deg(a.ThetaDeg)}" + (b.Side == LadderStubSide.Both ? " each" : "")
                    + (b.End == LadderStubEnd.Open ? $" (+{Deg(a.OpenEndDeg)} open end)" : "")
                    + (b.End is LadderStubEnd.Radial or LadderStubEnd.Butterfly
                        ? $", then a sector of Ro {Mm(b.Ro)}, {Deg(b.AngleDeg)}: {(a.EndOhms < 0 ? "−" : "+")}j{Ohm(Math.Abs(a.EndOhms))}"
                        : ""),
                (LadderSite c, _) => $"site, {(c.Parallel ? "parallel" : c.Kind.ToString().ToLowerInvariant())}: {c.Role} {SiteValue(c.Role, c.Value)}",
                _ => "",
            });
            // The vias as they always read; the lumped parts' sites, with their spans, as every planned passive lists them.
            var vias = d.Plan.Sites.Where(x => x.Role == PlanSiteRole.Via).Select(x => x.PinA).ToList();
            string via = vias.Count == 0 ? ""
                : vias.SequenceEqual(Enumerable.Range(1, vias.Count).Select(k => $"G{k}"))
                    ? $"\nA via to ground on G1{(vias.Count > 1 ? $"-G{vias.Count}" : "")}"
                    : $"\nA via to ground on {string.Join(", ", vias)}";
            return ($"At F, ports' lines W {Mm(d.W0Meters, "0.#####")} (Z0)\n" + string.Join("\n", lines) + via
                  + Sites(d.Plan with { Sites = [.. d.Plan.Sites.Where(x => x.Role != PlanSiteRole.Via)] }), footprintNote);
        }
        catch (ParameterRefusalException ex)
        {
            return ("", ex.Message);
        }
    }

    private static string Deg(double d) => d.ToString("0.#", CultureInfo.InvariantCulture) + "°";
    private static string Ff(double c) => (c * 1e15).ToString("0.#", CultureInfo.InvariantCulture) + " fF";
}
