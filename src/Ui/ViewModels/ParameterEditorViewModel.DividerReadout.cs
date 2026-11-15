using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.ViewModels;

// ──────────────────────────────────────────────────────────────────────────────
//  What a planned passive's specs come out to — SHOWN, not entered (docs/design/rf-passives.md).
//
//  The part stores its specs (Z0, F) and the resistor's pin span (Gap); the widths, the quarter-wave
//  length and the ring radius are the plan's, built by the very function the model and the PCell call
//  (WilkinsonPlan.Build) on the substrate a run injects (ResolveMlinSubstrate), so the numbers here are
//  the ones simulated and drawn. A closed form plus two bisections: computed on the UI thread on every
//  refresh, as the MMIC readout is. A note replaces the numbers when a spec is an expression (known
//  only at run time) or the plan refuses it.
// ──────────────────────────────────────────────────────────────────────────────

public partial class ParameterEditorViewModel
{
    /// <summary>True for a planned passive with a readout: MWPD.</summary>
    public bool IsDividerReadoutTarget => _target?.Symbol == SymbolKind.Mwpd;

    /// <summary>The derived numbers, one per line, or empty.</summary>
    public string DividerReadoutText { get; private set; } = "";

    /// <summary>Why no numbers are shown. Empty whenever there are some.</summary>
    public string DividerReadoutNote { get; private set; } = "";

    public bool HasDividerReadoutNote => DividerReadoutNote.Length > 0;

    private void RefreshDividerReadout()
    {
        string text = "", note = "";
        if (IsDividerReadoutTarget)
        {
            if (Spec("Z0") is not { } z0 || Spec("F") is not { } f || Spec("Gap") is not { } gap
                || Spec("K", 1.0) is not { } k || Spec("N", 1.0) is not { } n || Spec("Band", WilkinsonPlan.DefaultBand) is not { } band)
                note = "Z0, F, Gap, K, N or Band is an expression, so the divider's dimensions are known only at run time.";
            else
            {
                var (h, t, er) = ResolveMlinSubstrate();
                try
                {
                    var d = WilkinsonPlan.Build(z0, f, gap, new MicrostripSubstrate(h, t, er, 0.0, 0.0), k, n, band);
                    // Widths to the plan's own 2 nm grid, so a line drawn to meet a pin can be typed exactly: a
                    // rounded width leaves a sliver step at the joint that DRC reports.
                    if (d.K == 1.0 && d.Sections == 1)
                        text = $"W {Mm(d.W0Meters, "0.#####")} (Z0), arms {Mm(d.ArmWMeters, "0.#####")} ({Ohm(d.ArmZ0)})\n"
                             + $"L {Mm(d.QuarterWaveMeters)} (λg/4 at F, εeff {d.ArmEeff.ToString("0.###", CultureInfo.InvariantCulture)})\n"
                             + $"Ring radius {Mm(d.RingRadiusMeters)}\n"
                             + $"Isolation R = 2·Z0 = {Ohm(d.IsolationOhms)}, across R2-R3";
                    else
                        // A variant: every quarter-wave line (λg/4 at F) and every resistor, as the plan has them.
                        text = $"W {Mm(d.W0Meters, "0.#####")} (Z0)\n"
                             + string.Concat(d.Lines.Select(l =>
                                   $"{l.Name}: {Ohm(l.Z)}, W {Mm(l.WMeters, "0.#####")}, L {Mm(l.QuarterWaveMeters)}\n"))
                             + $"Ring radius {Mm(d.RingRadiusMeters)}\n"
                             + string.Join("\n", d.Resistors.Select((r, i) =>
                                   $"R{(d.Sections > 1 ? (i + 1).ToString(CultureInfo.InvariantCulture) : "")} = {Ohm(r.Ohms)}, across {r.PinA}-{r.PinB}"));
                }
                catch (ParameterRefusalException ex)
                {
                    note = ex.Message;
                }
            }
        }
        DividerReadoutText = text;
        DividerReadoutNote = note;
        OnPropertyChanged(nameof(IsDividerReadoutTarget));
        OnPropertyChanged(nameof(DividerReadoutText));
        OnPropertyChanged(nameof(DividerReadoutNote));
        OnPropertyChanged(nameof(HasDividerReadoutNote));
    }

    /// <summary>A spec row's SI value when it is a plain number; <paramref name="absent"/> when the part has no such row
    /// (a divider placed before the row existed).</summary>
    private double? Spec(string name, double? absent = null)
        => _target?.Parameters.FirstOrDefault(p => p.Name == name) is not { } row ? absent
         : NumericText.TryParseDouble(row.Expression, out double raw)
            ? raw * (Units.Scale(UnitNormalizer.ToEngineUnit(row.Unit)) ?? 1.0)
            : null;

    private static string Mm(double m, string format = "0.###") => (m * 1e3).ToString(format, CultureInfo.InvariantCulture) + " mm";
    private static string Ohm(double r) => r.ToString("0.#", CultureInfo.InvariantCulture) + " Ω";
}
