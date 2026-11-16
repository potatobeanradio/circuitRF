using System.Globalization;
using CommunityToolkit.Mvvm.Input;
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
    /// <summary>True for a planned passive with a readout: MWPD, the hybrids and the coupled-line parts.</summary>
    public bool IsDividerReadoutTarget => _target?.Symbol is SymbolKind.Mwpd or SymbolKind.Mblc or SymbolKind.Mrrc
                                          or SymbolKind.Mclc or SymbolKind.Midc or SymbolKind.Mcbl;

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
        else if (_target?.Symbol is SymbolKind.Mclc or SymbolKind.Midc or SymbolKind.Mcbl)
            (text, note) = CoupledLineReadout(_target.Symbol);
        else if (IsDividerReadoutTarget)
        {
            if (Spec("Z0") is not { } z0 || Spec("F") is not { } f || SiteGap(ref note) is not { } gap
                || Spec("K", 1.0) is not { } k || Spec("N", 1.0) is not { } n || Spec("Band", WilkinsonPlan.DefaultBand) is not { } band)
                note = "Z0, F, Gap, K, N or Band is an expression, so the divider's dimensions are known only at run time.";
            else
            {
                var (h, t, er) = ResolveMlinSubstrate();
                try
                {
                    var d = WilkinsonPlan.Build(z0, f, gap, new MicrostripSubstrate(h, t, er, 0.0, 0.0), k, n, band, Corner());
                    // Widths to the plan's own 2 nm grid, so a line drawn to meet a pin can be typed exactly: a
                    // rounded width leaves a sliver step at the joint that DRC reports.
                    if (d.K == 1.0 && d.Sections == 1)
                        text = $"W {Mm(d.W0Meters, "0.#####")} (Z0), arms {Mm(d.ArmWMeters, "0.#####")} ({Ohm(d.ArmZ0)})\n"
                             + $"L {Mm(d.QuarterWaveMeters)} (λg/4 at F, εeff {d.ArmEeff.ToString("0.###", CultureInfo.InvariantCulture)})\n"
                             + Ring(d)
                             + $"Isolation R = 2·Z0 = {Ohm(d.IsolationOhms)}, across R2-R3";
                    else
                        // A variant: every quarter-wave line (λg/4 at F) and every resistor, as the plan has them.
                        text = $"W {Mm(d.W0Meters, "0.#####")} (Z0)\n"
                             + string.Concat(d.Lines.Select(l =>
                                   $"{l.Name}: {Ohm(l.Z)}, W {Mm(l.WMeters, "0.#####")}, L {Mm(l.QuarterWaveMeters)}\n"))
                             + Ring(d)
                             + string.Join("\n", d.Resistors.Select((r, i) =>
                                   $"R{(d.Sections > 1 ? (i + 1).ToString(CultureInfo.InvariantCulture) : "")} = {Ohm(r.Ohms)}, across {r.PinA}-{r.PinB}"));
                    text += Sites(d.Plan);
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
        OnPropertyChanged(nameof(HasSites));
        OnPropertyChanged(nameof(DividerReadoutText));
        OnPropertyChanged(nameof(DividerReadoutNote));
        OnPropertyChanged(nameof(HasDividerReadoutNote));
    }

    /// <summary>A planned passive with sites, so the readout offers Place Site Parts.</summary>
    public bool HasSites => SchematicViewModel.HasSites(_target);

    /// <summary>Place Site Parts, the context menu's own command (<see cref="SchematicViewModel.PlaceSiteParts"/>).</summary>
    [RelayCommand]
    private void PlaceSiteParts()
    {
        if (_target is not null) _schematicVm?.PlaceSiteParts(_target.Id);
    }

    /// <summary>The site span the plan is built with, as the run and the layout resolve it: the Footprint's land span when
    /// the part states one (<see cref="MicrostripSubstrateInjection.SiteSpanMeters"/>), else Gap. A footprint that does not
    /// resolve leaves Gap and says why in <paramref name="note"/>.</summary>
    private double? SiteGap(ref string note)
    {
        if (_target is null || PassiveSites.FootprintOf(_target) is not { } footprint) return Spec("Gap");
        var model = _schematicVm?.EditModel;
        if (MicrostripSubstrateInjection.SiteSpanMeters(footprint, model?.SchematicDirectory,
                model is null ? null : SchematicTechnology.Of(model), out string? refusal) is { } span)
            return span;
        note = $"Footprint: {refusal}; Gap is used as stated.";
        return Spec("Gap");
    }

    /// <summary>The plan's sites, a line each: what goes there, the value the design wants and, where the plan sets it,
    /// the span between the pins (rf-passives.md, "Sites"). Place Site Parts fills the empty ones.</summary>
    private static string Sites(PassivePlan plan)
        => string.Concat(plan.Sites.Select(site =>
            $"\nSite {site.Name}: " + (site.Role switch
            {
                PlanSiteRole.Via => "a via to ground",
                PlanSiteRole.Jumper => "a jumper (bond wire or air bridge)",
                PlanSiteRole.Load => "the load",
                PlanSiteRole.L => "L",
                PlanSiteRole.C => "C",
                _ => "R",
            })
            + (site.Value is { } v ? " " + (site.Role switch
            {
                PlanSiteRole.L => (v * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " nH",
                PlanSiteRole.C => (v * 1e12).ToString("0.###", CultureInfo.InvariantCulture) + " pF",
                _ => Ohm(v),
            }) : "")
            + (site.SpanMeters is { } span ? $", span {Mm(span, "0.####")}" : "")));

    /// <summary>A spec row's SI value when it is a plain number; <paramref name="absent"/> when the part has no such row
    /// (a divider placed before the row existed).</summary>
    private double? Spec(string name, double? absent = null)
        => _target?.Parameters.FirstOrDefault(p => p.Name == name) is not { } row ? absent
         : NumericText.TryParseDouble(row.Expression, out double raw)
            ? raw * (Units.Scale(UnitNormalizer.ToEngineUnit(row.Unit)) ?? 1.0)
            : null;

    /// <summary>A ring's <c>Corner</c>, a name; Curve when the row is absent or names none (the picker offers only the names).</summary>
    private PlanCorner Corner()
        => Enum.TryParse(_target?.Parameters.FirstOrDefault(p => p.Name == "Corner")?.Expression?.Trim(), ignoreCase: true,
               out PlanCorner c) && Enum.IsDefined(c) ? c : PlanCorner.Curve;

    /// <summary>The ring's radius, or with corners each arm's straight runs: shortened by the corners' own length at F.</summary>
    private static string Ring(WilkinsonDesign d)
        => d.Corner == PlanCorner.Curve
            ? $"Ring radius {Mm(d.RingRadiusMeters)}\n"
            : string.Concat(d.Arms.Select(a =>
                $"{(d.Arms.Count > 1 || d.Sections > 1 ? a.Name : "Each arm")}: {Mm(a.AcrossMeters)} across, "
              + $"{Mm(a.RiseMeters)} up and down; each {(d.Corner == PlanCorner.Miter ? "mitred" : "square")} corner stands for "
              + $"{Mm(a.CornerMeters)} of it at F\n"));

    private static string Mm(double m, string format = "0.###") => (m * 1e3).ToString(format, CultureInfo.InvariantCulture) + " mm";
    private static string Ohm(double r) => r.ToString("0.#", CultureInfo.InvariantCulture) + " Ω";
}
