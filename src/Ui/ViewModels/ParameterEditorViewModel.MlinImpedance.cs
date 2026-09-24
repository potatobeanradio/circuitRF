using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.ViewModels;

// ──────────────────────────────────────────────────────────────────────────────
//  MLIN: the Z0 its width gives, and the width a Z0 needs — ONE field, both ways.
//
//  Owner request after a round of outside-designer feedback (2026-09-24): an MLIN's parameters
//  should answer "what width do I need for this Z0" and "what Z0 does this width give", on the
//  workspace's own substrate. The history has no MLIN Z0 route to restore — the only entry-mode
//  switch ever built is MKlopf's Z1/Z2 ⇄ W1/W2.
//
//  W STAYS THE ONE STORED PARAMETER. A second, authoritative Z0 route (MKlopf's shape) would have to
//  be understood by everything else that reads an MLIN's W — the layout PCell and its W grips,
//  Schematic → Layout, LVS and the EM extractor — and none of them would. So Z0 is a DERIVED field:
//  it shows the static Hammerstad-Jensen Z0 of the current W, and typing a Z0 into it synthesises
//  the width (HammerstadJensen.SynthesizeWidth, which inverts that same Compute) and writes W, as
//  one undoable edit. There is no mode to toggle, because both directions are always available.
//
//  THE SUBSTRATE IS THE SIMULATED ONE: MicrostripSubstrateInjection.BuildOverrides with this
//  instance's own SignalLayer / GroundReference choices — the overrides NetExtractor injects — and
//  ComponentModelFactory's own fallback constants where nothing resolves, which is what a run then
//  simulates too.
// ──────────────────────────────────────────────────────────────────────────────

public partial class ParameterEditorViewModel
{
    /// <summary>True for an MLIN — the one component the Z0 field applies to.</summary>
    public bool IsMlinTarget => _target?.Symbol == SymbolKind.Mlin;

    private string _mlinZ0Text = "";

    /// <summary>The Z0 field, in ohms. Bound two-way; <see cref="CommitMlinZ0Command"/> applies it.</summary>
    public string MlinZ0Text
    {
        get => _mlinZ0Text;
        set { if (_mlinZ0Text == value) return; _mlinZ0Text = value; OnPropertyChanged(); }
    }

    /// <summary>The static effective permittivity at the current width, or empty.</summary>
    public string MlinEeffText { get; private set; } = "";

    /// <summary>Why no Z0 is shown, a refused entry, or a validity note. Empty when there is nothing
    /// to say.</summary>
    public string MlinImpedanceNote { get; private set; } = "";

    /// <summary>True while <see cref="MlinImpedanceNote"/> has something to say.</summary>
    public bool HasMlinImpedanceNote => MlinImpedanceNote.Length > 0;

    private IRelayCommand? _commitMlinZ0Command;

    /// <summary>Synthesises W from <see cref="MlinZ0Text"/> and writes it.</summary>
    public IRelayCommand CommitMlinZ0Command => _commitMlinZ0Command ??= new RelayCommand(CommitMlinZ0);

    /// <summary>
    /// H, T and εr as the run will see them: the injected overrides where a substrate resolves, the
    /// model's own defaults where one does not.
    /// </summary>
    private (double H, double T, double Er) ResolveMlinSubstrate()
    {
        var tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(_schematicVm?.EditModel.SchematicDirectory);
        var overrides = MicrostripSubstrateInjection.BuildOverrides(
            tech, out _, NonDefaultLayerChoice("SignalLayer"), NonDefaultLayerChoice("GroundReference"));

        double h  = ComponentModelFactory.DefaultSubstrateHMeters;
        double t  = ComponentModelFactory.DefaultSubstrateTMeters;
        double er = ComponentModelFactory.DefaultSubstrateEpsR;
        foreach (var o in overrides)
        {
            if (!NumericText.TryParseDouble(o.Expression, out double v)) continue;
            switch (o.Name)
            {
                case "H":  h  = v; break;
                case "T":  t  = v; break;
                case "Er": er = v; break;
            }
        }
        return (h, t, er);
    }

    /// <summary>The W row, when it is a plain number — an expression (a VAR, a formula) has no
    /// value here, and overwriting it with a number would silently unbind it.</summary>
    private bool TryReadMlinWidth(out double wMeters, out EditableParameter? row)
    {
        row = _target?.Parameters.FirstOrDefault(p => p.Name == "W");
        wMeters = 0;
        if (row is null || !NumericText.TryParseDouble(row.Expression, out double raw)) return false;
        wMeters = raw * (Units.Scale(UnitNormalizer.ToEngineUnit(row.Unit)) ?? 1.0);
        return wMeters > 0;
    }

    /// <summary>Re-reads the field from the current W. Called with the substrate readout, so it follows
    /// every model refresh and every technology change.</summary>
    private void RefreshMlinImpedance()
    {
        OnPropertyChanged(nameof(IsMlinTarget));
        string z0 = "", eeff = "", note = "";

        if (IsMlinTarget)
        {
            if (!TryReadMlinWidth(out double w, out var row))
            {
                note = row is null
                    ? "This MLIN has no W parameter."
                    : "W is an expression, so its Z0 is known only at run time. Enter a number in W to see it here.";
            }
            else
            {
                var (h, t, er) = ResolveMlinSubstrate();
                var reporter = new MicrostripValidityReporter(_target!.InstanceName);
                var (zc, ee) = HammerstadJensen.Compute(w, h, t, er, reporter);
                z0   = FormatOhm2(zc);
                eeff = "εeff " + ee.ToString("0.###", CultureInfo.InvariantCulture);
                note = string.Join(" ", reporter.Drain().Select(m => m.Message));
            }
        }

        MlinZ0Text        = z0;
        MlinEeffText      = eeff;
        MlinImpedanceNote = note;
        OnPropertyChanged(nameof(MlinEeffText));
        OnPropertyChanged(nameof(MlinImpedanceNote));
        OnPropertyChanged(nameof(HasMlinImpedanceNote));
    }

    /// <summary>
    /// Z0 → W: synthesises the width on the simulated substrate and writes it in W's own unit, as one
    /// undoable <see cref="SetParametersCommand"/>. A Z0 no width in the model's validity range can
    /// produce is refused with the model's own sentence rather than written as a clamped width.
    /// </summary>
    private void CommitMlinZ0()
    {
        if (_target is null || _schematicVm is null || !IsMlinTarget) return;

        string text = (MlinZ0Text ?? "").Trim();
        foreach (var suffix in new[] { "Ohms", "Ohm", "ohms", "ohm", "Ω" })
            if (text.EndsWith(suffix, StringComparison.Ordinal)) { text = text[..^suffix.Length].Trim(); break; }

        if (!TryReadMlinWidth(out double wCur, out var row) || row is null)
        {
            RefreshMlinImpedance();
            return;
        }

        if (!NumericText.TryParseDouble(text, out double target) || !(target > 0))
        {
            RefreshMlinImpedance();
            SetMlinNote($"'{MlinZ0TextOrBlank(text)}' is not an impedance in ohms.");
            return;
        }

        var (h, t, er) = ResolveMlinSubstrate();

        // The field shows Z0 ROUNDED to 0.01 Ω and commits on every focus loss, so leaving it untouched
        // used to synthesise W back from the rounded value — a slightly different width, written as an
        // edit, every time focus left the field. What is shown is not a request to change anything.
        if (FormatOhm2(target) == FormatOhm2(HammerstadJensen.Compute(wCur, h, t, er, new MicrostripValidityReporter(_target.InstanceName)).Z0))
        {
            RefreshMlinImpedance();
            return;
        }

        var reporter = new MicrostripValidityReporter(_target.InstanceName);
        double w = HammerstadJensen.SynthesizeWidth(target, h, t, er, reporter);
        var refusal = reporter.Drain();
        if (refusal.Count > 0)
        {
            // Out of range: SynthesizeWidth reports and returns the bound. Writing that would put a
            // width on the schematic that does not have the Z0 the user asked for.
            RefreshMlinImpedance();
            SetMlinNote(string.Join(" ", refusal.Select(m => m.Message)));
            return;
        }

        string unit = string.IsNullOrEmpty(row.Unit) ? "mm" : row.Unit;
        string expression = FormatLengthInUnit(w, unit);
        if (Math.Abs(w - wCur) <= 1e-12 || expression == row.Expression.Trim())
        {
            RefreshMlinImpedance();
            return;
        }

        var newParams = _target.Parameters.Select(p => p.Clone()).ToList();
        var wRow = newParams.First(p => p.Name == "W");
        wRow.Expression = expression;
        wRow.Unit = unit;
        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
        RefreshMlinImpedance();
    }

    private void SetMlinNote(string note)
    {
        MlinImpedanceNote = note;
        OnPropertyChanged(nameof(MlinImpedanceNote));
        OnPropertyChanged(nameof(HasMlinImpedanceNote));
    }

    private static string MlinZ0TextOrBlank(string text) => text.Length > 0 ? text : "(blank)";

    private static string FormatOhm2(double z) => Math.Round(z, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
