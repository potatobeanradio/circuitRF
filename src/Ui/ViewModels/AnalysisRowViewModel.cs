using System.Globalization;
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

    // ── Displayed fields ──────────────────────────────────────────────────────

    public bool Enabled
    {
        get => Analysis.Enabled;
        set
        {
            if (Analysis.Enabled == value) return;
            _schematicVm.Execute(new EnableAnalysisCommand(_schematicVm.EditModel, Analysis, value));
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
        LoadpullPursuitAnalysis  => "LPP",
        LoadpullAnalysis         => "LP",
        ParametricSweepAnalysis  => "SW",
        _                        => "?",
    };
    public string Summary   => ComputeSummary(Analysis, _schematicVm.EditModel);

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
        LoadpullPursuitAnalysis lpp => FormatLppSummary(lpp),
        LoadpullAnalysis lp        => FormatLpSummary(lp),
        ParametricSweepAnalysis ps => FormatSweepSummary(ps),
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
        var first = sp.Sweeps[0];
        string start = AnalysisPreviewHelper.ComputeFreqSummary(first.StartExpr, first.StartUnit, model);
        string stop  = AnalysisPreviewHelper.ComputeFreqSummary(first.StopExpr,  first.StopUnit,  model);
        string range = $"{start}–{stop}";
        return n == 1 ? range : $"{range}, {n} segments";
    }

    /// <summary>
    /// The tone half of the HB card. A multi-tone analysis carries its fundamentals in
    /// <c>ToneExprs</c> and only MIRRORS tone 1 into the scalar <c>ToneExpr</c>, so reading the
    /// scalar alone reported an N-tone analysis as a single "f₀" and hid every other tone.
    /// The multi-tone test is HbEngine's own (<c>NumFreqs &gt; 1</c> and enough entries to satisfy
    /// it, first <c>NumFreqs</c> taken) so the card can never name a tone set the run would not use.
    /// </summary>
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
