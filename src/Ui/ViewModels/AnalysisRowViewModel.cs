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
    public string Summary   => ComputeSummary(Analysis);

    // ── Construction ──────────────────────────────────────────────────────────

    public AnalysisRowViewModel(Core.Design.Analysis analysis, SchematicViewModel schematicVm)
    {
        Analysis     = analysis;
        _schematicVm = schematicVm;
    }

    // ── Summary formatting ────────────────────────────────────────────────────

    private static string ComputeSummary(Core.Design.Analysis a) => a switch
    {
        DcAnalysis                 => "Operating point",
        SParameterAnalysis sp      => FormatSpSummary(sp),
        HarmonicBalanceAnalysis hb => FormatHbSummary(hb),
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

    private static string FormatSpSummary(SParameterAnalysis sp)
    {
        int n = sp.Sweeps.Count;
        var first = sp.Sweeps[0];
        string range = $"{FormatFreq(first.StartExpr)}–{FormatFreq(first.StopExpr)}";
        return n == 1 ? range : $"{range}, {n} segments";
    }

    private static string FormatHbSummary(HarmonicBalanceAnalysis hb)
    {
        string tone = hb.ToneExpr == "0" ? "?" : FormatFreq(hb.ToneExpr);
        return $"f₀={tone}, {hb.MaxHarmonicExpr} harmonics";
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

    // Best-effort plain-text frequency formatter: if the expression is a literal double,
    // render it with SI suffix (GHz/MHz/kHz/Hz); otherwise show the raw expression string.
    private static string FormatFreq(string expr)
    {
        if (double.TryParse(expr, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double hz))
        {
            if      (hz >= 1e9)  return $"{hz / 1e9:G3} GHz";
            else if (hz >= 1e6)  return $"{hz / 1e6:G3} MHz";
            else if (hz >= 1e3)  return $"{hz / 1e3:G3} kHz";
            else                 return $"{hz:G3} Hz";
        }
        return expr;
    }
}
