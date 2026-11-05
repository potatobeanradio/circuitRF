using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Analysis;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Wraps one <see cref="Analysis"/> for the <see cref="AnalysesListViewModel"/> row.
/// Enabled toggle routes through the SchematicViewModel's undo stack so the schematic
/// becomes dirty and the change is undoable.
/// </summary>
public sealed partial class AnalysisRowViewModel : ObservableObject
{
    private readonly SchematicViewModel _schematicVm;

    public Core.Design.Analysis Analysis { get; }

    /// <summary>
    /// The list this row belongs to, so the card's own context menu can bind its commands against the
    /// row's DataContext. A ContextMenu lives in a popup, outside the panel's visual tree, so an
    /// ancestor-relative binding to the list VM does not resolve from inside one — this backlink is
    /// what makes the menu's Command bindings ordinary. Null in tests that build a row on its own.
    /// </summary>
    public AnalysesListViewModel? Owner { get; init; }

    // ── Displayed fields ──────────────────────────────────────────────────────

    public bool Enabled
    {
        get => Analysis.Enabled;
        set
        {
            if (Analysis.Enabled == value) return;
            var cmd = new EnableAnalysisCommand(_schematicVm.EditModel, Analysis, value);
            // Through the list when there is one, so this checkbox is undoable from the panel for the
            // same reason every other edit here is (see AnalysesListViewModel.ExecuteEdit).
            if (Owner is not null) Owner.ExecuteEdit(cmd);
            else                   _schematicVm.Execute(cmd);
            OnPropertyChanged();
        }
    }

    public bool   IsSweep   => Analysis is ParametricSweepAnalysis;
    public string Name      => Analysis is ParametricSweepAnalysis psa ? psa.SweepVarName : Analysis.Name;
    public string TypeLabel => Analysis switch
    {
        DcAnalysis               => "DC",
        SParameterAnalysis       => "SP",
        HarmonicBalanceAnalysis  => "HB",
        EnvelopeAnalysis         => "ENV",
        LoadpullPursuitAnalysis  => "LPP",
        LoadpullAnalysis         => "LP",
        ParametricSweepAnalysis  => "SW",
        FieldsAnalysis           => "FLD",
        _                        => "?",
    };
    public string Summary   => ComputeSummary(Analysis, _schematicVm.EditModel);

    /// <summary>
    /// How the card menu's items name this card — "DC1", or "DC1 Vgs Sweep" for a sweep. A menu item
    /// naming what it will act on is worth the words here: the cards of one chain sit directly under
    /// each other and differ only by an indent, so a bare "Remove" on the wrong one looks exactly
    /// like a Remove on the right one.
    ///
    /// <para>A sweep is named by the analysis it ultimately wraps plus its own variable — NOT by its
    /// own <c>Name</c>, which is generated (<c>DC1_sweep_Vgs</c>, and <c>DC1_sweep_Vgs_sweep_Vds</c>
    /// once nested) and appears nowhere on screen.</para>
    /// </summary>
    public string MenuName => Analysis is ParametricSweepAnalysis psa
        ? $"{BaseAnalysisName(psa, _schematicVm.EditModel)} {psa.SweepVarName} Sweep"
        : Analysis.Name;

    /// <summary>
    /// False for a sweep card whose base analysis is disabled. A disabled base makes the whole chain
    /// inert — none of its sweeps run, whatever their own checkboxes say — so the card greys out to
    /// show it. Always true for a base card, whose own checkbox already says whether it runs. Rows are
    /// rebuilt on every model change, so toggling the base re-evaluates this without a notification.
    /// </summary>
    public bool ChainRuns => Analysis is not ParametricSweepAnalysis psa
        || BaseAnalysis(psa, _schematicVm.EditModel) is not { Enabled: false };

    /// <summary>The greyed-out card's tooltip; null (no tooltip) while the chain runs.</summary>
    public string? NotRunReason => ChainRuns
        ? null
        : $"Not run: {BaseAnalysisName((ParametricSweepAnalysis)Analysis, _schematicVm.EditModel)} is disabled";

    /// <summary>What the card menu's Remove item says. "Remove", not "Delete", is the verb the rest
    /// of circuitRF's menus use for taking something out of a document.</summary>
    public string RemoveLabel => $"Remove {MenuName}";

    /// <summary>What the card menu's Run item says — the one card's own name rather than "This
    /// Analysis", so the item reads as the same card the pointer is over.</summary>
    public string RunLabel => $"Run {MenuName}";

    /// <summary>
    /// The non-sweep analysis at the bottom of <paramref name="psa"/>'s chain. Walks
    /// <c>InnerAnalysisName</c> regardless of <c>Enabled</c> — this is a LABEL, so it must name the
    /// card the user can see rather than the one a run would collapse to. Depth-guarded, and falls
    /// back to the sweep's own name if the chain dangles.
    /// </summary>
    private static string BaseAnalysisName(ParametricSweepAnalysis psa, SchematicEditModel model) =>
        BaseAnalysis(psa, model)?.Name ?? psa.Name;

    /// <summary>The non-sweep analysis at the bottom of the chain, or null if the chain dangles.</summary>
    private static Core.Design.Analysis? BaseAnalysis(ParametricSweepAnalysis psa, SchematicEditModel model)
    {
        Core.Design.Analysis? a = psa;
        for (int guard = 0; a is ParametricSweepAnalysis p && guard < 64; guard++)
            a = model.Analyses.FirstOrDefault(
                x => string.Equals(x.Name, p.InnerAnalysisName, System.StringComparison.OrdinalIgnoreCase));

        return a is not null and not ParametricSweepAnalysis ? a : null;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public AnalysisRowViewModel(Core.Design.Analysis analysis, SchematicViewModel schematicVm)
    {
        Analysis     = analysis;
        _schematicVm = schematicVm;
    }

    // ── Summary formatting ────────────────────────────────────────────────────

    private static string ComputeSummary(Core.Design.Analysis a, SchematicEditModel model) => a switch
    {
        DcAnalysis                 => "Operating point",
        SParameterAnalysis sp      => FormatSpSummary(sp, model),
        HarmonicBalanceAnalysis hb => FormatHbSummary(hb, model),
        EnvelopeAnalysis env       => FormatEnvSummary(env),
        LoadpullPursuitAnalysis lpp => FormatLppSummary(lpp),
        LoadpullAnalysis lp        => FormatLpSummary(lp),
        ParametricSweepAnalysis ps => FormatSweepSummary(ps),
        FieldsAnalysis fa          => $"{fa.Block} at {fa.FreqExpr} {fa.FreqUnit}, port {fa.DriveExpr} driven" + (fa.Average ? "" : ", local only"),
        _                          => "",
    };

    private static string FormatLppSummary(LoadpullPursuitAnalysis lpp)
    {
        string followOn = lpp.CreateLoadpullResultExpr.Trim()
            .Equals("false", System.StringComparison.OrdinalIgnoreCase) ? "" : ", +loadpull";
        return $"Pursuit · {lpp.SearchMethodExpr} · {lpp.EffTypeExpr}{followOn}";
    }

    private static string FormatLpSummary(LoadpullAnalysis lp)
    {
        string tuners = $"{lp.LoadTunerName}/{lp.SourceTunerName}";
        string grid   = string.IsNullOrEmpty(lp.GridPath)
            ? "no grid"
            : System.IO.Path.GetFileName(lp.GridPath);
        return $"Loadpull · {tuners} · {lp.CompressionExpr} dB, grid {grid}";
    }

    private static string FormatSpSummary(SParameterAnalysis sp, SchematicEditModel model)
    {
        int n = sp.Sweeps.Count;
        string range = AnalysisPreviewHelper.ComputeFreqRangeSummary(sp.Sweeps, model) ?? UnresolvedRange(sp.Sweeps[0], model);
        return n == 1 ? range : $"{range}, {n} segments";
    }

    private static string UnresolvedRange(FrequencySpec first, SchematicEditModel model)
        => AnalysisPreviewHelper.ComputeFreqSummary(first.StartExpr, first.StartUnit, model) + "–"
           + AnalysisPreviewHelper.ComputeFreqSummary(first.StopExpr, first.StopUnit, model);

    /// <summary>
    /// The tone half of the HB card. A multi-tone analysis carries its fundamentals in
    /// <c>ToneExprs</c> and only MIRRORS tone 1 into the scalar <c>ToneExpr</c>, so reading the
    /// scalar alone reported an N-tone analysis as a single "f₀" and hid every other tone.
    /// The multi-tone test is HbEngine's own (<c>NumFreqs &gt; 1</c> and enough entries to satisfy
    /// it, first <c>NumFreqs</c> taken) so the card can never name a tone set the run would not use.
    /// </summary>
    private static string FormatEnvSummary(EnvelopeAnalysis env)
        => env.StopExpr + " s · " + env.Accuracy + " · segments " + env.SegmentsExpr
           + (string.IsNullOrWhiteSpace(env.Probes) ? "" : " · " + env.Probes);

    private static string FormatHbSummary(HarmonicBalanceAnalysis hb, SchematicEditModel model)
    {
        int numFreqs = int.TryParse(hb.NumFreqsExpr.Trim(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out int n) ? n : 1;

        if (numFreqs > 1 && hb.ToneExprs.Length >= numFreqs)
        {
            var tones = new string[numFreqs];
            for (int i = 0; i < numFreqs; i++)
            {
                string unit = i < hb.ToneUnits.Length ? hb.ToneUnits[i] : "Hz";
                tones[i] = $"f{Subscript(i + 1)}={FormatTone(hb.ToneExprs[i], unit, model)}";
            }
            return $"{string.Join(", ", tones)}, {hb.MaxHarmonicExpr} harmonics";
        }

        return $"f₀={FormatTone(hb.ToneExpr, hb.ToneUnit, model)}, {hb.MaxHarmonicExpr} harmonics";
    }

    /// <summary>An unset tone reads "?" rather than "0 Hz", which would look like a real setting.</summary>
    private static string FormatTone(string expr, string unit, SchematicEditModel model) =>
        expr == "0" ? "?" : AnalysisPreviewHelper.ComputeFreqSummary(expr, unit, model);

    /// <summary>Subscript digits for the tone index (HbMaxTones is 6, but any index formats).</summary>
    private static string Subscript(int i)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in i.ToString(CultureInfo.InvariantCulture))
            sb.Append(c is >= '0' and <= '9' ? (char)('\u2080' + (c - '0')) : c);
        return sb.ToString();
    }

    private static string FormatSweepSummary(ParametricSweepAnalysis psa)
    {
        var v = psa.SweepValues;
        if (v.Length == 0) return "(empty)";
        if (v.Length == 1) return $"1 pt: {FmtNum(v[0])}";
        return $"{v.Length} pts: {FmtNum(v[0])}…{FmtNum(v[^1])}";
    }

    private static string FmtNum(double v) =>
        v.ToString(System.Math.Abs(v) >= 1e6 || (System.Math.Abs(v) > 0 && System.Math.Abs(v) < 0.01)
            ? "G4" : "G6", CultureInfo.InvariantCulture);
}
