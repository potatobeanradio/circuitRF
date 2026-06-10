using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// Emits a <see cref="TestBench"/> as <c>.cnl</c> text — the exact inverse of <see cref="CnlReader"/>.
/// Output is accepted by <see cref="CnlReader"/> and round-trips to an equivalent TestBench.
/// Framework-free: no Avalonia, no Skia.
/// </summary>
public static class CnlWriter
{
    /// <summary>
    /// Writes <paramref name="tb"/> to a <c>.cnl</c> string.
    /// </summary>
    /// <param name="tb">The TestBench to emit.</param>
    /// <param name="header">
    /// Optional provenance comment emitted as a <c>; header</c> line at the top.
    /// </param>
    public static string Write(TestBench tb, string? header = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(header))
        {
            sb.AppendLine($"; {header}");
            sb.AppendLine();
        }

        // Global variables: name = expr [unit]
        foreach (var v in tb.GlobalVariables)
            sb.AppendLine(FormatVariable(v));

        if (tb.GlobalVariables.Count > 0 && HasContent(tb))
            sb.AppendLine();

        // Instances
        foreach (var inst in tb.Instances)
            sb.AppendLine(FormatInstance(inst));

        if (tb.Instances.Count > 0 && HasDirectives(tb))
            sb.AppendLine();

        // Typed analyses
        foreach (var analysis in tb.Analyses)
            sb.AppendLine(FormatAnalysis(analysis));

        // Measurements: measure Name = expr
        foreach (var m in tb.Measurements)
        {
            if (!string.IsNullOrEmpty(m.Unit))
                sb.AppendLine($"measure {m.Name} = {m.Expression} {m.Unit}");
            else
                sb.AppendLine($"measure {m.Name} = {m.Expression}");
        }

        // Raw directives — verbatim
        foreach (var raw in tb.RawDirectives)
            sb.AppendLine($"{raw.Kind} {raw.RawLine}");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasContent(TestBench tb)
        => tb.Instances.Count > 0 || HasDirectives(tb);

    private static bool HasDirectives(TestBench tb)
        => tb.Analyses.Count > 0 || tb.Measurements.Count > 0 || tb.RawDirectives.Count > 0;

    // ── Variable emission ─────────────────────────────────────────────────────

    private static string FormatVariable(Variable v)
    {
        if (!string.IsNullOrEmpty(v.Unit))
            return $"{v.Name} = {v.Expression} {v.Unit}";
        return $"{v.Name} = {v.Expression}";
    }

    // ── Instance emission ─────────────────────────────────────────────────────

    private static string FormatInstance(Instance inst)
    {
        string type = inst.Reference;

        if (type.Equals("SDD", StringComparison.OrdinalIgnoreCase))
            return FormatSddInstance(inst);

        if (type.Equals("Z_Port", StringComparison.OrdinalIgnoreCase))
            return FormatZPortInstance(inst);

        if (type.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
            return FormatTunerInstance(inst);

        return FormatStandardInstance(inst);
    }

    /// <summary>
    /// Standard instances: R, L, C, Port, V, V_1Tone, SnP, … and anything not handled specially.
    /// Emits: Type:Name  net1 net2 …  [refnet]  param=val [unit] …
    /// For SnP (N-or-N+1 rule): RefNetBinding is appended after signal nets when non-null.
    /// </summary>
    private static string FormatStandardInstance(Instance inst)
    {
        var sb = new StringBuilder();
        sb.Append($"{inst.Reference}:{inst.InstanceName}");

        foreach (var net in inst.NetBindings)
            sb.Append($"  {net}");

        // N-or-N+1 rule: append floating reference net for SnP and similar frequency-domain blocks.
        if (inst.RefNetBinding is not null)
            sb.Append($"  {inst.RefNetBinding}");

        foreach (var ov in inst.Overrides)
            sb.Append($"  {FormatParam(ov)}");

        return sb.ToString();
    }

    /// <summary>
    /// SDD:Name  net1 net2 …  I[p,w]=expr  Q[p,w]=expr  …
    /// All overrides are equation assignments (name=expr, no unit, no space around '=').
    /// NumPorts is not emitted — it is implicit from the net count.
    /// </summary>
    private static string FormatSddInstance(Instance inst)
    {
        var sb = new StringBuilder();
        sb.Append($"{inst.Reference}:{inst.InstanceName}");

        foreach (var net in inst.NetBindings)
            sb.Append($"  {net}");

        foreach (var ov in inst.Overrides)
        {
            // NumPorts is implicit from net count; skip to avoid confusing the parser.
            if (ov.Name.Equals("NumPorts", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append($"  {ov.Name}={ov.Expression}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Z_Port:Name  net1 …  [refnet]  Z[i,j]=expr  …
    /// RefNetBinding appended after signal nets per the N-or-N+1 rule.
    /// NumPorts is not emitted — implicit from the Z[i,j] matrix size.
    /// </summary>
    private static string FormatZPortInstance(Instance inst)
    {
        var sb = new StringBuilder();
        sb.Append($"{inst.Reference}:{inst.InstanceName}");

        foreach (var net in inst.NetBindings)
            sb.Append($"  {net}");

        if (inst.RefNetBinding is not null)
            sb.Append($"  {inst.RefNetBinding}");

        foreach (var ov in inst.Overrides)
        {
            if (ov.Name.Equals("NumPorts", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append($"  {ov.Name}={ov.Expression}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tuner:Name  net0 net1  [simple_params]  Z[k]=expr  G[k]=expr  …
    /// TunerName is synthetic (injected by the reader from the instance name) — not emitted.
    /// </summary>
    private static string FormatTunerInstance(Instance inst)
    {
        var sb = new StringBuilder();
        sb.Append($"{inst.Reference}:{inst.InstanceName}");

        foreach (var net in inst.NetBindings)
            sb.Append($"  {net}");

        foreach (var ov in inst.Overrides)
        {
            if (ov.Name.Equals("TunerName", StringComparison.OrdinalIgnoreCase))
                continue; // synthetic; skip
            sb.Append($"  {ov.Name}={ov.Expression}");
        }

        return sb.ToString();
    }

    private static string FormatParam(ParameterAssignment ov)
    {
        if (!string.IsNullOrEmpty(ov.Unit))
            return $"{ov.Name}={ov.Expression} {ov.Unit}";
        return $"{ov.Name}={ov.Expression}";
    }

    // ── Analysis emission ─────────────────────────────────────────────────────

    private static string FormatAnalysis(Analysis analysis) => analysis switch
    {
        HarmonicBalanceAnalysis hb    => FormatHbAnalysis(hb),
        LoadpullAnalysis lp           => FormatLoadpullAnalysis(lp),
        LoadpullPursuitAnalysis lpp   => FormatLoadpullPursuitAnalysis(lpp),
        ParametricSweepAnalysis ps    => FormatParametricSweepAnalysis(ps),
        SParameterAnalysis sp         => FormatSParameterAnalysis(sp),
        DcAnalysis dc                 => $"analysis {dc.Name} type=dc",
        _                             => $"analysis {analysis.Name}",
    };

    private static string FormatHbAnalysis(HarmonicBalanceAnalysis hb)
    {
        var sb = new StringBuilder($"analysis {hb.Name} type=hb");
        sb.Append($" Tone={hb.ToneExpr}");

        // Multi-tone fields only when NumFreqs > 1 or ToneExprs is populated.
        if (hb.ToneExprs.Length > 0)
        {
            sb.Append($" NumFreqs={hb.NumFreqsExpr}");
            sb.Append($" MaxMixOrder={hb.MaxMixOrderExpr}");
            for (int i = 0; i < hb.ToneExprs.Length; i++)
                sb.Append($" Tone[{i + 1}]={hb.ToneExprs[i]}");
        }

        sb.Append($" MaxHarm={hb.MaxHarmonicExpr}");
        sb.Append($" FFTOverSample={hb.FFTOverSampleExpr}");
        sb.Append($" Tol={hb.TolExpr}");
        sb.Append($" DriveStepping={hb.DriveSteppingExpr}");
        sb.Append($" GuardHarmonic={hb.GuardHarmonicExpr}");
        sb.Append($" Lambda={hb.LambdaExpr}");
        sb.Append($" MaxIter={hb.MaxIterExpr}");

        if (hb.SweepVarName is not null)
            sb.Append($" Sweep=\"{hb.SweepVarName}: {hb.SweepStartExpr} .. {hb.SweepStopExpr} step {hb.SweepStepExpr}\"");

        return sb.ToString();
    }

    private static string FormatLoadpullAnalysis(LoadpullAnalysis lp)
    {
        var sb = new StringBuilder($"analysis {lp.Name} type=loadpull");
        sb.Append($" Tone={lp.ToneExpr}");
        sb.Append($" MaxHarm={lp.MaxHarmonicExpr}");
        sb.Append($" LoadTuner={lp.LoadTunerName}");
        sb.Append($" SourceTuner={lp.SourceTunerName}");
        if (!string.IsNullOrEmpty(lp.GridPath))
            sb.Append($" Grid=\"{lp.GridPath}\"");
        sb.Append($" Sweep={lp.SweepExpr}");
        sb.Append($" TuneHarm={lp.TuneHarmExpr}");
        sb.Append($" Compression={lp.CompressionExpr}");
        sb.Append($" GainType={lp.GainTypeExpr}");
        sb.Append($" PinStart={lp.PinStartExpr}");
        sb.Append($" PinStep={lp.PinStepExpr}");
        sb.Append($" PinMax={lp.PinMaxExpr}");
        sb.Append($" Tickle={lp.TickleExpr}");
        sb.Append($" MaxIter={lp.MaxIterExpr}");
        sb.Append($" FFTOverSample={lp.FFTOverSampleExpr}");
        sb.Append($" Tol={lp.TolExpr}");
        sb.Append($" DriveStepping={lp.DriveSteppingExpr}");
        sb.Append($" GuardHarmonic={lp.GuardHarmonicExpr}");
        return sb.ToString();
    }

    private static string FormatLoadpullPursuitAnalysis(LoadpullPursuitAnalysis lpp)
    {
        var sb = new StringBuilder($"analysis {lpp.Name} type=loadpull_pursuit");
        sb.Append($" Tone={lpp.ToneExpr}");
        sb.Append($" MaxHarm={lpp.MaxHarmonicExpr}");
        sb.Append($" LoadTuner={lpp.LoadTunerName}");
        sb.Append($" SourceTuner={lpp.SourceTunerName}");
        sb.Append($" Sweep={lpp.SweepExpr}");
        sb.Append($" TuneHarm={lpp.TuneHarmExpr}");
        sb.Append($" Compression={lpp.CompressionExpr}");
        sb.Append($" GainType={lpp.GainTypeExpr}");
        sb.Append($" PinStart={lpp.PinStartExpr}");
        sb.Append($" PinStep={lpp.PinStepExpr}");
        sb.Append($" PinMax={lpp.PinMaxExpr}");
        sb.Append($" Tickle={lpp.TickleExpr}");
        sb.Append($" MaxIter={lpp.MaxIterExpr}");
        sb.Append($" FFTOverSample={lpp.FFTOverSampleExpr}");
        sb.Append($" Tol={lpp.TolExpr}");
        sb.Append($" DriveStepping={lpp.DriveSteppingExpr}");
        sb.Append($" GuardHarmonic={lpp.GuardHarmonicExpr}");
        sb.Append($" EffType={lpp.EffTypeExpr}");
        sb.Append($" ZsourceOBO={lpp.ZsourceOBOExpr}");
        sb.Append($" SearchMethod={lpp.SearchMethodExpr}");
        if (lpp.OutputGridPath is not null)
            sb.Append($" OutputGrid=\"{lpp.OutputGridPath}\"");
        sb.Append($" VSWR1={lpp.Vswr1Expr}");
        sb.Append($" VSWR1_resolution={lpp.Vswr1ResolutionExpr}");
        sb.Append($" VSWR2={lpp.Vswr2Expr}");
        sb.Append($" VSWR2_resolution={lpp.Vswr2ResolutionExpr}");
        sb.Append($" keepNonconvergingPoints={lpp.KeepNonconvergingExpr}");
        sb.Append($" nonconvergentVSWR={lpp.NonconvergentVswrExpr}");
        sb.Append($" CreateLoadpullResult={lpp.CreateLoadpullResultExpr}");
        sb.Append($" LoadpullResultZsource={lpp.LoadpullResultZsourceExpr}");
        return sb.ToString();
    }

    private static string FormatParametricSweepAnalysis(ParametricSweepAnalysis ps)
    {
        var values = string.Join(",", ps.SweepValues.Select(
            v => v.ToString(CultureInfo.InvariantCulture)));
        return $"analysis {ps.Name} type=parametric_sweep Var={ps.SweepVarName} Values={values} Inner={ps.InnerAnalysisName}";
    }

    private static string FormatSParameterAnalysis(SParameterAnalysis sp)
    {
        // No typed parser in CnlReader for s-param yet; emit as a raw-compatible line.
        var f = sp.Freq;
        return $"analysis {sp.Name} type=sparam start={f.Start.ToString(CultureInfo.InvariantCulture)} stop={f.Stop.ToString(CultureInfo.InvariantCulture)} step={f.Step.ToString(CultureInfo.InvariantCulture)}";
    }
}
