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

    public string Name      => Analysis.Name;
    public string TypeLabel => Analysis switch
    {
        DcAnalysis               => "DC",
        SParameterAnalysis       => "SP",
        HarmonicBalanceAnalysis  => "HB",
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
        DcAnalysis                => "Operating point",
        SParameterAnalysis sp     => FormatSpSummary(sp),
        HarmonicBalanceAnalysis hb => FormatHbSummary(hb),
        _                         => "",
    };

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
