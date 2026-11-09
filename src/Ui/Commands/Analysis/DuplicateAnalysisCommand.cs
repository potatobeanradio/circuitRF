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

    internal static Core.Design.Analysis CloneAnalysis(
        Core.Design.Analysis a, string newName, string? newInnerName = null)
    {
        // brief-sdl-6: what the analysis is simulated by travels with the copy.
        var copy = CloneAnalysisCore(a, newName, newInnerName);
        copy.Model   = a.Model;
        copy.EmSetup = a.EmSetup;
        return copy;
    }

    private static Core.Design.Analysis CloneAnalysisCore(
        Core.Design.Analysis a, string newName, string? newInnerName) => a switch
    {
        DcAnalysis =>
            new DcAnalysis(newName) { Enabled = a.Enabled },

        FieldsAnalysis f =>
            new FieldsAnalysis(newName)
            {
                Enabled = a.Enabled, Block = f.Block, FreqExpr = f.FreqExpr, FreqUnit = f.FreqUnit, DriveExpr = f.DriveExpr,
                WeightsExpr = f.WeightsExpr, Norm = f.Norm, AverageExpr = f.AverageExpr,
            },

        // A copy keeps its fixture paths as written; an empty one follows the copy's own name, so two copies left at the
        // defaults write different files.
        TrlAnalysis t =>
            new TrlAnalysis(newName)
            {
                Enabled = a.Enabled, Thru = t.Thru, Reflect = t.Reflect, Line = t.Line, ReflectType = t.ReflectType,
                FixtureA = t.FixtureA, FixtureB = t.FixtureB,
            },

        OscillatorAnalysis o =>
            new OscillatorAnalysis(newName)
            {
                Enabled = a.Enabled, Probe = o.Probe, FguessExpr = o.FguessExpr, FstartExpr = o.FstartExpr, FstopExpr = o.FstopExpr,
                Unit = o.Unit, MaxHarmonicExpr = o.MaxHarmonicExpr, FFTOverSampleExpr = o.FFTOverSampleExpr, TolExpr = o.TolExpr,
                AguessExpr = o.AguessExpr, PhaseNoise = o.PhaseNoise, PnStartExpr = o.PnStartExpr, PnStopExpr = o.PnStopExpr,
                PnNptsExpr = o.PnNptsExpr, PnLog = o.PnLog, PnPortExpr = o.PnPortExpr,
            },

        SParameterAnalysis sp => sp.WithSweeps(newName, sp.Sweeps),

        EnvelopeAnalysis env =>
            new EnvelopeAnalysis(newName)
            {
                Enabled = env.Enabled, StopExpr = env.StopExpr, StepExpr = env.StepExpr, Accuracy = env.Accuracy,
                MaxHarmExpr = env.MaxHarmExpr, MaxMixOrderExpr = env.MaxMixOrderExpr, Probes = env.Probes,
                ZmeasExpr = env.ZmeasExpr, SegmentsExpr = env.SegmentsExpr, WarmupExpr = env.WarmupExpr,
                StartExpr = env.StartExpr, TolExpr = env.TolExpr, SaveWaveforms = env.SaveWaveforms, Envelope = env.Envelope,
                NoiseExpr = env.NoiseExpr, SeedExpr = env.SeedExpr, SeedPerPointExpr = env.SeedPerPointExpr,
                EvmSplitExpr = env.EvmSplitExpr,
            },

        HarmonicBalanceAnalysis hb =>
            new HarmonicBalanceAnalysis(newName)
            {
                Enabled           = hb.Enabled,
                ToneExpr          = hb.ToneExpr,
                ToneUnit          = hb.ToneUnit,
                NumFreqsExpr      = hb.NumFreqsExpr,
                ToneExprs         = hb.ToneExprs,
                ToneUnits         = hb.ToneUnits,
                MaxMixOrderExpr   = hb.MaxMixOrderExpr,
                MaxHarmonicExpr   = hb.MaxHarmonicExpr,
                FFTOverSampleExpr = hb.FFTOverSampleExpr,
                TolExpr           = hb.TolExpr,
                DriveSteppingExpr = hb.DriveSteppingExpr,
                GuardHarmonicExpr = hb.GuardHarmonicExpr,
                LambdaExpr        = hb.LambdaExpr,
                MaxIterExpr       = hb.MaxIterExpr,
#pragma warning disable CS0618
                SweepVarName      = hb.SweepVarName,
                SweepStartExpr    = hb.SweepStartExpr,
                SweepStopExpr     = hb.SweepStopExpr,
                SweepStepExpr     = hb.SweepStepExpr,
#pragma warning restore CS0618
            },

        LoadpullAnalysis lp =>
            new LoadpullAnalysis(newName)
            {
                Enabled           = lp.Enabled,
                ToneExpr          = lp.ToneExpr,
                ToneUnit          = lp.ToneUnit,
                LoadTunerName     = lp.LoadTunerName,
                SourceTunerName   = lp.SourceTunerName,
                GridPath          = lp.GridPath,
                PinStartExpr      = lp.PinStartExpr,
                PinMaxExpr        = lp.PinMaxExpr,
                MaxHarmonicExpr   = lp.MaxHarmonicExpr,
                SweepExpr         = lp.SweepExpr,
                TuneHarmExpr      = lp.TuneHarmExpr,
                CompressionExpr   = lp.CompressionExpr,
                GainTypeExpr      = lp.GainTypeExpr,
                PinStepExpr       = lp.PinStepExpr,
                TickleExpr        = lp.TickleExpr,
                MaxIterExpr       = lp.MaxIterExpr,
                FFTOverSampleExpr = lp.FFTOverSampleExpr,
                TolExpr           = lp.TolExpr,
                DriveSteppingExpr = lp.DriveSteppingExpr,
                GuardHarmonicExpr = lp.GuardHarmonicExpr,
                NoiseExpr         = lp.NoiseExpr,
                NoiseFreqExpr     = lp.NoiseFreqExpr,
                NoiseFreqUnit     = lp.NoiseFreqUnit,
                NoiseInExpr       = lp.NoiseInExpr,
                NoiseOutExpr      = lp.NoiseOutExpr,
                NoiseSidebandExpr = lp.NoiseSidebandExpr,
                SourceDirectory   = lp.SourceDirectory,
            },

        LoadpullPursuitAnalysis lpp =>
            new LoadpullPursuitAnalysis(newName)
            {
                Enabled                   = lpp.Enabled,
                ToneExpr                  = lpp.ToneExpr,
                ToneUnit                  = lpp.ToneUnit,
                LoadTunerName             = lpp.LoadTunerName,
                SourceTunerName           = lpp.SourceTunerName,
                PinStartExpr              = lpp.PinStartExpr,
                PinMaxExpr                = lpp.PinMaxExpr,
                MaxHarmonicExpr           = lpp.MaxHarmonicExpr,
                SweepExpr                 = lpp.SweepExpr,
                TuneHarmExpr              = lpp.TuneHarmExpr,
                CompressionExpr           = lpp.CompressionExpr,
                GainTypeExpr              = lpp.GainTypeExpr,
                PinStepExpr               = lpp.PinStepExpr,
                TickleExpr                = lpp.TickleExpr,
                MaxIterExpr               = lpp.MaxIterExpr,
                FFTOverSampleExpr         = lpp.FFTOverSampleExpr,
                TolExpr                   = lpp.TolExpr,
                DriveSteppingExpr         = lpp.DriveSteppingExpr,
                GuardHarmonicExpr         = lpp.GuardHarmonicExpr,
                NoiseExpr         = lpp.NoiseExpr,
                NoiseFreqExpr     = lpp.NoiseFreqExpr,
                NoiseFreqUnit     = lpp.NoiseFreqUnit,
                NoiseInExpr       = lpp.NoiseInExpr,
                NoiseOutExpr      = lpp.NoiseOutExpr,
                NoiseSidebandExpr = lpp.NoiseSidebandExpr,
                EffTypeExpr               = lpp.EffTypeExpr,
                ZsourceOBOExpr            = lpp.ZsourceOBOExpr,
                SearchMethodExpr          = lpp.SearchMethodExpr,
                OutputGridPath            = lpp.OutputGridPath,
                Vswr1Expr                 = lpp.Vswr1Expr,
                Vswr1ResolutionExpr       = lpp.Vswr1ResolutionExpr,
                Vswr2Expr                 = lpp.Vswr2Expr,
                Vswr2ResolutionExpr       = lpp.Vswr2ResolutionExpr,
                KeepNonconvergingExpr     = lpp.KeepNonconvergingExpr,
                NonconvergentVswrExpr     = lpp.NonconvergentVswrExpr,
                CreateLoadpullResultExpr  = lpp.CreateLoadpullResultExpr,
                LoadpullResultZsourceExpr = lpp.LoadpullResultZsourceExpr,
                SourceDirectory           = lpp.SourceDirectory,
            },

        ParametricSweepAnalysis psa =>
            CloneSweep(psa, newName, newInnerName ?? psa.InnerAnalysisName),

        _ => throw new NotSupportedException($"Cannot clone analysis type {a.GetType().Name}"),
    };

    private static ParametricSweepAnalysis CloneSweep(ParametricSweepAnalysis psa, string name, string inner)
        => psa.Spec is { } spec
            ? new ParametricSweepAnalysis(name, psa.SweepVarName, spec, inner)        { Enabled = psa.Enabled }
            : new ParametricSweepAnalysis(name, psa.SweepVarName, psa.SweepValues, inner) { Enabled = psa.Enabled };
}
