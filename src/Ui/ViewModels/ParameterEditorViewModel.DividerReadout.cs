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
    /// <summary>True for a planned passive with a readout: MWPD, MBLC, MRRC.</summary>
    public bool IsDividerReadoutTarget => _target?.Symbol is SymbolKind.Mwpd or SymbolKind.Mblc or SymbolKind.Mrrc;

    /// <summary>The derived numbers, one per line, or empty.</summary>
    public string DividerReadoutText { get; private set; } = "";

    /// <summary>Why no numbers are shown. Empty whenever there are some.</summary>
    public string DividerReadoutNote { get; private set; } = "";

    public bool HasDividerReadoutNote => DividerReadoutNote.Length > 0;

    private void RefreshDividerReadout()
    {
        string text = "", note = "";
        if (_target?.Symbol is SymbolKind.Mblc or SymbolKind.Mrrc)
            (text, note) = HybridReadout(_target.Symbol);
        else if (IsDividerReadoutTarget)
        {
            if (Spec("Z0") is not { } z0 || Spec("F") is not { } f || Spec("Gap") is not { } gap)
                note = "Z0, F or Gap is an expression, so the divider's dimensions are known only at run time.";
            else
            {
                var (h, t, er) = ResolveMlinSubstrate();
                try
                {
                    var d = WilkinsonPlan.Build(z0, f, gap, new MicrostripSubstrate(h, t, er, 0.0, 0.0));
                    // Widths to the plan's own 2 nm grid, so a line drawn to meet a pin can be typed exactly: a
                    // rounded width leaves a sliver step at the joint that DRC reports.
                    text = $"W {Mm(d.W0Meters, "0.#####")} (Z0), arms {Mm(d.ArmWMeters, "0.#####")} ({Ohm(d.ArmZ0)})\n"
                         + $"L {Mm(d.QuarterWaveMeters)} (λg/4 at F, εeff {d.ArmEeff.ToString("0.###", CultureInfo.InvariantCulture)})\n"
                         + $"Ring radius {Mm(d.RingRadiusMeters)}\n"
                         + $"Isolation R = 2·Z0 = {Ohm(d.IsolationOhms)}, across R2-R3";
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

    /// <summary>A spec row's SI value when it is a plain number.</summary>
    private double? Spec(string name)
        => _target?.Parameters.FirstOrDefault(p => p.Name == name) is { } row
           && NumericText.TryParseDouble(row.Expression, out double raw)
            ? raw * (Units.Scale(UnitNormalizer.ToEngineUnit(row.Unit)) ?? 1.0)
            : null;

    private static string Mm(double m, string format = "0.###") => (m * 1e3).ToString(format, CultureInfo.InvariantCulture) + " mm";
    private static string Ohm(double r) => r.ToString("0.#", CultureInfo.InvariantCulture) + " Ω";
}
