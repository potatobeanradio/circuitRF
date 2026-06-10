using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>
/// Clones an analysis and inserts it after the original.
/// Name-collision resolution: "{name} copy", then "{name} copy 2", etc.
/// Undo removes the clone.
/// </summary>
internal sealed class DuplicateAnalysisCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _source;
    private Core.Design.Analysis? _clone;

    public string Description => $"Duplicate analysis {_source.Name}";

    public DuplicateAnalysisCommand(SchematicEditModel model, Core.Design.Analysis source)
    {
        _model  = model;
        _source = source;
    }

    public void Execute()
    {
        string newName = ResolveName(_model.Analyses, _source.Name);
        _clone = CloneAnalysis(_source, newName);
        int insertAt = _model.Analyses.IndexOf(_source);
        _model.Analyses.Insert(insertAt >= 0 ? insertAt + 1 : _model.Analyses.Count, _clone);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        if (_clone is not null) _model.Analyses.Remove(_clone);
        _model.NotifyChanged();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static string ResolveName(IReadOnlyList<Core.Design.Analysis> existing, string baseName)
    {
        var names = existing.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate = baseName + " copy";
        if (!names.Contains(candidate)) return candidate;
        for (int n = 2; ; n++)
        {
            candidate = baseName + " copy " + n;
            if (!names.Contains(candidate)) return candidate;
        }
    }

    internal static Core.Design.Analysis CloneAnalysis(Core.Design.Analysis a, string newName) => a switch
    {
        DcAnalysis =>
            new DcAnalysis(newName) { Enabled = a.Enabled },

        SParameterAnalysis sp =>
            new SParameterAnalysis(newName, sp.Sweeps) { Enabled = a.Enabled },

        HarmonicBalanceAnalysis hb =>
            new HarmonicBalanceAnalysis(newName)
            {
                Enabled           = hb.Enabled,
                ToneExpr          = hb.ToneExpr,
                NumFreqsExpr      = hb.NumFreqsExpr,
                ToneExprs         = hb.ToneExprs,
                MaxMixOrderExpr   = hb.MaxMixOrderExpr,
                MaxHarmonicExpr   = hb.MaxHarmonicExpr,
                FFTOverSampleExpr = hb.FFTOverSampleExpr,
                TolExpr           = hb.TolExpr,
                DriveSteppingExpr = hb.DriveSteppingExpr,
                GuardHarmonicExpr = hb.GuardHarmonicExpr,
                LambdaExpr        = hb.LambdaExpr,
                MaxIterExpr       = hb.MaxIterExpr,
                SweepVarName      = hb.SweepVarName,
                SweepStartExpr    = hb.SweepStartExpr,
                SweepStopExpr     = hb.SweepStopExpr,
                SweepStepExpr     = hb.SweepStepExpr,
            },

        _ => throw new NotSupportedException($"Cannot clone analysis type {a.GetType().Name}"),
    };
}
