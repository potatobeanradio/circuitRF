using System;
using System.Globalization;
using System.Linq;
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
    /// <summary>Writes a flat TestBench (no cell definitions).</summary>
    public static string Write(TestBench tb, string? header = null)
        => Write(tb, null, header);

    /// <summary>
    /// Writes <paramref name="tb"/> plus the cell definitions in <paramref name="library"/> as
    /// <c>define … end</c> blocks (emitted before the top-level content, leaf-first as supplied).
    /// </summary>
    public static string Write(TestBench tb, Library? library, string? header = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(header))
        {
            sb.AppendLine($"; {header}");
            sb.AppendLine();
        }

        // The netlist's own technology, before anything that binds to it (brief-artsch-6 R-as6-1).
        // Nothing at all when it states none, so every netlist written before it existed is unchanged.
        if (tb.Technology is { Length: > 0 } technology)
        {
            sb.AppendLine($"technology \"{technology.Replace('\\', '/')}\"");
            sb.AppendLine();
        }

        // Cell definitions first (define-before-use; reader is order-independent)
        if (library is { Cells.Count: > 0 })
        {
            foreach (var cell in library.Cells)
            {
                AppendCell(sb, cell);
                sb.AppendLine();
            }
        }

        // User-defined expression functions: name(a, b, ...) = expr
        // These MUST be written. A kit netlist brings its own functions along with the cells that
        // call them, and a cell copied out of it references them by bare name — so dropping them
        // here leaves a file whose own definitions do not resolve, and the failure surfaces much
        // later as "Unknown function '<name>'" from the elaborator, pointing at nothing.
        foreach (var f in tb.Functions)
            sb.AppendLine($"{f.Name}({string.Join(", ", f.Parameters)}) = {f.Body}");

        if (tb.Functions.Count > 0 && (tb.GlobalVariables.Count > 0 || HasContent(tb)))
            sb.AppendLine();

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
        {
            var text = FormatAnalysis(analysis);
            if (!analysis.Enabled)
                // Append to every \n-separated sub-line (S-param emits one line per segment).
                text = string.Join("\n", text.Split('\n').Select(l => l + " enabled=false"));
            // brief-sdl-6 R-sdl6-1: only when not the default, so every netlist written before the key existed is unchanged.
            if (analysis.Model == AnalysisModel.Em || analysis.EmSetup is { Length: > 0 })
            {
                string suffix = (analysis.Model == AnalysisModel.Em ? " model=em" : "") +
                                (analysis.EmSetup is { Length: > 0 } es ? " emSetup=" + (es.Contains(' ') ? $"\"{es}\"" : es) : "");
                text = string.Join("\n", text.Split('\n').Select(l => l + suffix));
            }
            sb.AppendLine(text);
        }

        // Measurements: measure Name = expr
        foreach (var m in tb.Measurements)
        {
            if (!string.IsNullOrEmpty(m.Unit))
                sb.AppendLine($"measure {m.Name} = {m.Expression} {m.Unit}");
            else
                sb.AppendLine($"measure {m.Name} = {m.Expression}");
        }

        // Tuning and optimization: tune, preset, goal, optimize (TuningDirectiveText). Nothing at all
        // when the testbench has no setup, so a netlist written before these existed is unchanged.
        if (tb.Tuning is { IsEmpty: false } tuning)
            foreach (var line in TuningDirectiveText.Write(tuning))
                sb.AppendLine(line);

        // Raw directives — verbatim
        foreach (var raw in tb.RawDirectives)
            sb.AppendLine($"{raw.Kind} {raw.RawLine}");

        // Net-label provenance: which nets came from user-placed schematic labels.
        // Round-trips tb.LabeledNets so the node-picker filter survives schematic→.cnl→reader.
        if (tb.LabeledNets.Count > 0)
            sb.AppendLine($"labelednets {string.Join(" ", tb.LabeledNets.OrderBy(n => n, System.StringComparer.Ordinal))}");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasContent(TestBench tb)
        => tb.Instances.Count > 0 || HasDirectives(tb);

    private static bool HasDirectives(TestBench tb)
        => tb.Analyses.Count > 0 || tb.Measurements.Count > 0 || tb.RawDirectives.Count > 0
        || tb.Tuning is { IsEmpty: false };

    // ── Cell-block emission ───────────────────────────────────────────────────

    private static void AppendCell(StringBuilder sb, Cell cell)
    {
        sb.Append("define ").Append(cell.Name)
          .Append(" (").Append(string.Join(' ', cell.Ports)).Append(')').AppendLine();

        // Quoted when it holds a space: a model folder, and so its part number, may be named "BFP 740".
        if (cell.Part is { } part)
            sb.Append("  part ").AppendLine(part.Any(char.IsWhiteSpace) ? $"\"{part}\"" : part);

        if (cell.Parameters.Count > 0)
            sb.Append("  parameters ")
              .AppendLine(string.Join("  ", cell.Parameters.Select(FormatParamDecl)));

        // Named parameter sets (brief vendorlib-13 R-vl13-2a): `corner <name> <param>=<value> [unit] …`.
        foreach (var corner in cell.Corners)
        {
            sb.Append("  corner ").Append(corner.Name);
            foreach (var b in corner.Bindings)
                sb.Append(' ').Append(string.IsNullOrEmpty(b.Unit) ? $"{b.Name}={b.Expression}" : $"{b.Name}={b.Expression} {b.Unit}");
            sb.AppendLine();
        }

        // Cell-local VAR definitions (CnlReader routes in-block assignments to Cell.Variables).
        foreach (var v in cell.Variables)
            sb.Append("  ").AppendLine(FormatVariable(v));

        foreach (var inst in cell.Instances)
            sb.Append("  ").AppendLine(FormatInstance(inst));

        sb.Append("end ").AppendLine(cell.Name);
    }

    private static string FormatParamDecl(ParameterDeclaration pd)
        => string.IsNullOrEmpty(pd.Unit)
            ? $"{pd.Name}={pd.DefaultExpression}"
            : $"{pd.Name}={pd.DefaultExpression} {pd.Unit}";

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
    /// An instance's type as the line writes it: quoted when it holds whitespace, which a cell's name may (<c>Filter Board</c>).
    /// Unquoted, the reader took <c>Filter</c> for half a statement and skipped the line, so such a cell placed in a schematic
    /// dropped out of every simulation of it with only a warning.
    /// </summary>
    internal static string TypeToken(string type) => type.Any(char.IsWhiteSpace) ? $"\"{type}\"" : type;

    /// <summary>
    /// Standard instances: R, L, C, Port, V, V_1Tone, SnP, … and anything not handled specially.
    /// Emits: Type:Name  net1 net2 …  [refnet]  param=val [unit] …
    /// For SnP (N-or-N+1 rule): RefNetBinding is appended after signal nets when non-null.
    /// </summary>
    private static string FormatStandardInstance(Instance inst)
    {
        var sb = new StringBuilder();
        sb.Append($"{TypeToken(inst.Reference)}:{inst.InstanceName}");

        foreach (var net in inst.NetBindings)
            sb.Append($"  {net}");

        // N-or-N+1 rule: append floating reference net for SnP and similar frequency-domain blocks.
        if (inst.RefNetBinding is not null)
            sb.Append($"  {inst.RefNetBinding}");

        foreach (var ov in inst.Overrides)
            sb.Append($"  {FormatParam(ov, inst.Reference)}");

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
        sb.Append($"{TypeToken(inst.Reference)}:{inst.InstanceName}");

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
    /// Z_Port:Name  net1+  net1−  net2+  net2−  …  Z[i,j]=expr  …
    /// 2N nets in ± pair order; RefNetBinding is always null for Z_Port.
    /// NumPorts is not emitted — implicit from the Z[i,j] matrix size.
    /// </summary>
    private static string FormatZPortInstance(Instance inst)
    {
        var sb = new StringBuilder();
        sb.Append($"{TypeToken(inst.Reference)}:{inst.InstanceName}");

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
        sb.Append($"{TypeToken(inst.Reference)}:{inst.InstanceName}");

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

    private static string FormatParam(ParameterAssignment ov, string? reference = null)
    {
        // An SnP's File must be QUOTED. CnlReader resolves a relative Touchstone path against the
        // .cnl's own directory ONLY inside its `pexpr[0] == '"'` branch — so an unquoted relative
        // path reaches the model verbatim and is then looked for relative to the process working
        // directory, which is nowhere. hero1.cnl's own `File="…s2p"` is the canonical form.
        //
        // This surfaced at L7b: EM back-annotation is the first thing to write a RELATIVE SnP path
        // from the schematic side (R-cpl-13 stores workspace-relative), where the Browse… picker had
        // always produced an absolute one that happened not to need the resolution branch.
        if (reference is not null
            && (reference.Equals("SnP", StringComparison.OrdinalIgnoreCase)
                || reference.Equals(Devices.EmFieldDataRecord.TypeName, StringComparison.OrdinalIgnoreCase)
                || reference.Equals(Devices.ComponentModelFactory.DeembedTypeName, StringComparison.OrdinalIgnoreCase))
            && ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase)
            && !IsQuoted(ov.Expression))
            return $"{ov.Name}=\"{ov.Expression}\"";

        // An embedded JSON DESIGN payload is base64'd on its way into a .cnl, and only into a .cnl.
        //
        // Owner-reported, 2026-08-20: "the .csch is showing a Match component instance with Expression
        // all crazy text. Recall that all circuitRF file formats are supposed to be human readable."
        // It was base64 because ONE of the two formats a design has to survive cannot hold it any other
        // way: this one. A .cnl is whitespace-delimited and its only string escape is a pair of quotes
        // with no way to escape a quote inside one, so a JSON payload — which is all quotes — is not
        // expressible as a token here. A .csch is JSON and has no such problem.
        //
        // So the constraint is discharged where it exists. MatchEmbedding.Encode now writes readable
        // JSON, the .csch stores that, and this line turns it back into a bare token for the one
        // format that needs one. Both spellings decode (MatchEmbedding.TryDecode / WBondEmbedding.
        // TryDecode branch on a leading '{'), so a file written either way still loads and a
        // hand-authored .cnl may still carry base64.
        //
        // Keyed on the PARAMETER NAME, not the component type: "Design" is the embedded-payload
        // parameter for Match and for wBond alike (MatchEmbedding.DesignParameter ==
        // WBondEmbedding.DesignParameter == "Design"), and Core cannot see the wBond assembly to ask
        // it anything. Nothing else in the netlist writes a brace-leading expression — an expression
        // language token never starts with '{'.
        if (ov.Name.Equals(Matching.MatchEmbedding.DesignParameter, StringComparison.OrdinalIgnoreCase)
            && ov.Expression.TrimStart().StartsWith('{'))
            return $"{ov.Name}={Matching.MatchEmbedding.ToToken(ov.Expression)}";

        // A `Footprint` is a reference, not a value (ArtworkParameters) — `smt:0402@N`, or a
        // relative path to a .clay. The built-in spelling is a single token and needs nothing; a
        // PATH may well have a space in it, and a .cnl is whitespace-delimited, so an unquoted one
        // would make the reader treat its tail as a unit and refuse the whole line. Quoted only
        // when it has to be, so the common case still reads as itself.
        if (ArtworkParameters.IsArtworkOnly(ov.Name)
            && !IsQuoted(ov.Expression)
            && ov.Expression.AsSpan().ContainsAny(' ', '\t'))
            return $"{ov.Name}=\"{ov.Expression}\"";

        // A .cnl is whitespace-delimited, so `L=Lser + dL` would read its tail as a unit and refuse the line. Quoting would make it
        // a string, and no expression needs a space between two tokens, so the spaces go. A string literal keeps its own.
        string expression = IsQuoted(ov.Expression) || ov.Expression.Contains('"')
            ? ov.Expression
            : string.Concat(ov.Expression.Where(c => !char.IsWhiteSpace(c)));
        if (!string.IsNullOrEmpty(ov.Unit))
            return $"{ov.Name}={expression} {ov.Unit}";
        return $"{ov.Name}={expression}";
    }

    private static bool IsQuoted(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"';

    // ── Analysis emission ─────────────────────────────────────────────────────

    private static string FormatAnalysis(Analysis analysis) => analysis switch
    {
        HarmonicBalanceAnalysis hb    => FormatHbAnalysis(hb),
        EnvelopeAnalysis env          => $"analysis {env.Name} type=env Stop={env.StopExpr} Step={env.StepExpr} Accuracy={env.Accuracy} " +
                                         $"MaxHarm={env.MaxHarmExpr} MaxMixOrder={env.MaxMixOrderExpr} Segments={env.SegmentsExpr} " +
                                         $"Warmup={env.WarmupExpr} Start={env.StartExpr} Tol={env.TolExpr} SaveWaveforms={env.SaveWaveforms}" +
                                         (env.Envelope.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : $" Envelope={env.Envelope}") +
                                         EnvelopeNoiseKeys(env),
        LoadpullAnalysis lp           => FormatLoadpullAnalysis(lp),
        LoadpullPursuitAnalysis lpp   => FormatLoadpullPursuitAnalysis(lpp),
        ParametricSweepAnalysis ps    => FormatParametricSweepAnalysis(ps),
        SParameterAnalysis sp         => FormatSParameterAnalysis(sp),
        DcAnalysis dc                 => $"analysis {dc.Name} type=dc",
        FieldsAnalysis fa             => $"analysis {fa.Name} type=fields block={fa.Block} freq=\"{fa.FreqExpr}\" freqUnit={fa.FreqUnit} " +
                                         $"drive={fa.DriveExpr} {(fa.WeightsExpr.Length > 0 ? $"weights=\"{fa.WeightsExpr}\" " : "")}norm={fa.Norm} average={fa.AverageExpr}",
        OscillatorAnalysis osc        => $"analysis {osc.Name} type=osc" +
                                         (osc.Probe.Length > 0 ? $" Probe={osc.Probe}" : "") +
                                         $" Fguess=\"{osc.FguessExpr}\"" +
                                         (osc.FstartExpr.Length > 0 ? $" Fstart=\"{osc.FstartExpr}\"" : "") +
                                         (osc.FstopExpr.Length > 0 ? $" Fstop=\"{osc.FstopExpr}\"" : "") +
                                         $" Unit={osc.Unit} MaxHarm=\"{osc.MaxHarmonicExpr}\" FFTOverSample=\"{osc.FFTOverSampleExpr}\" Tol=\"{osc.TolExpr}\"" +
                                         (osc.AguessExpr.Length > 0 ? $" Aguess=\"{osc.AguessExpr}\"" : "") +
                                         // Brief NZ-10: written only when asked for, so an oscillator without it writes what it did before.
                                         (osc.PhaseNoise
                                             ? $" PhaseNoise=yes PnStart=\"{osc.PnStartExpr}\" PnStop=\"{osc.PnStopExpr}\" PnNpts=\"{osc.PnNptsExpr}\"" +
                                               $" PnLog={(osc.PnLog ? "yes" : "no")}" + (osc.PnPortExpr.Length > 0 ? $" PnPort=\"{osc.PnPortExpr}\"" : "")
                                             : ""),
        TrlAnalysis trl               => $"analysis {trl.Name} type=trl thru={trl.Thru} reflect={trl.Reflect} line={trl.Line} reflectType={trl.ReflectType}" +
                                         (trl.FixtureA.Length > 0 ? $" fixtureA=\"{trl.FixtureA}\"" : "") +
                                         (trl.FixtureB.Length > 0 ? $" fixtureB=\"{trl.FixtureB}\"" : ""),
        _                             => $"analysis {analysis.Name}",
    };

    private static string FormatHbAnalysis(HarmonicBalanceAnalysis hb)
    {
        var sb = new StringBuilder($"analysis {hb.Name} type=hb");
        sb.Append($" Tone=\"{hb.ToneExpr}\" ToneUnit={hb.ToneUnit}");

        // Multi-tone fields only when NumFreqs > 1 or ToneExprs is populated.
        if (hb.ToneExprs.Length > 0)
        {
            sb.Append($" NumFreqs={hb.NumFreqsExpr}");
            sb.Append($" MaxMixOrder={hb.MaxMixOrderExpr}");
            for (int i = 0; i < hb.ToneExprs.Length; i++)
            {
                string unit = i < hb.ToneUnits.Length ? hb.ToneUnits[i] : "Hz";
                sb.Append($" Tone[{i + 1}]=\"{hb.ToneExprs[i]}\" ToneUnit[{i + 1}]={unit}");
            }
        }

        sb.Append($" MaxHarm={hb.MaxHarmonicExpr}");
        sb.Append($" FFTOverSample={hb.FFTOverSampleExpr}");
        sb.Append($" Tol={hb.TolExpr}");
        sb.Append($" DriveStepping={hb.DriveSteppingExpr}");
        sb.Append($" GuardHarmonic={hb.GuardHarmonicExpr}");
        sb.Append($" Lambda={hb.LambdaExpr}");
        sb.Append($" MaxIter={hb.MaxIterExpr}");
        if (hb.MarginThresholdExpr != Analysis.MarginThresholdDefault)
            sb.Append($" MarginThreshold={hb.MarginThresholdExpr}");

        // The small-signal (probe-tickle) sweep — WSP-5 R-wsp5-1. Written only when the directive
        // declared one, so an HB line with no SS* keys round-trips to the same bytes it always did
        // (R-wsp5-9(a) is about the run; this is the same promise about the file).
        if (hb.HasSsSweep)
        {
            sb.Append($" SSStart=\"{hb.SsStartExpr}\" SSStop=\"{hb.SsStopExpr}\"");
            if (hb.SsStepExpr.Trim().Length > 0) sb.Append($" SSStep=\"{hb.SsStepExpr}\"");
            if (hb.SsNptsExpr.Trim().Length > 0) sb.Append($" SSNpts={hb.SsNptsExpr}");
            sb.Append($" SSUnit={hb.SsUnit}");
            if (hb.SsLogExpr.Trim().Length     > 0) sb.Append($" SSLog={hb.SsLogExpr}");
            if (hb.SsMaxHarmExpr.Trim().Length > 0) sb.Append($" SSMaxHarm={hb.SsMaxHarmExpr}");
        }

        // Noise on the conversion matrix (brief NZ-5): each key only when it differs from its default, so a line
        // without them round-trips to the bytes it always did.
        if (hb.NoiseExpr != Analysis.NoiseDefault) sb.Append($" Noise={hb.NoiseExpr}");
        if (hb.NoiseInExpr != HarmonicBalanceAnalysis.NoiseInDefault) sb.Append($" NoiseIn={hb.NoiseInExpr}");
        if (hb.NoiseOutExpr != HarmonicBalanceAnalysis.NoiseOutDefault) sb.Append($" NoiseOut={hb.NoiseOutExpr}");
        if (hb.NoiseSidebandExpr != HarmonicBalanceAnalysis.NoiseSidebandDefault)
            sb.Append($" NoiseSideband=\"{hb.NoiseSidebandExpr}\"");
        if (hb.NoiseContribExpr != Analysis.NoiseContribDefault) sb.Append($" NoiseContrib={hb.NoiseContribExpr}");

#pragma warning disable CS0618
        if (hb.SweepVarName is not null)
            sb.Append($" Sweep=\"{hb.SweepVarName}: {hb.SweepStartExpr} .. {hb.SweepStopExpr} step {hb.SweepStepExpr}\"");
#pragma warning restore CS0618

        return sb.ToString();
    }

    private static string FormatLoadpullAnalysis(LoadpullAnalysis lp)
    {
        var sb = new StringBuilder($"analysis {lp.Name} type=loadpull");
        sb.Append($" Tone=\"{lp.ToneExpr}\" ToneUnit={lp.ToneUnit}");
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
        AppendLoadpullNoise(sb, lp);
        return sb.ToString();
    }

    private static string FormatLoadpullPursuitAnalysis(LoadpullPursuitAnalysis lpp)
    {
        var sb = new StringBuilder($"analysis {lpp.Name} type=loadpull_pursuit");
        sb.Append($" Tone=\"{lpp.ToneExpr}\" ToneUnit={lpp.ToneUnit}");
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
        AppendLoadpullNoise(sb, lpp);
        return sb.ToString();
    }

    /// <summary>The envelope noise keys (brief NZ-7), each only when it differs from its default, so a line without them
    /// round-trips to the bytes it always did.</summary>
    private static string EnvelopeNoiseKeys(EnvelopeAnalysis env) =>
        (env.NoiseExpr != Analysis.NoiseDefault ? $" Noise={env.NoiseExpr}" : "") +
        (env.SeedExpr != EnvelopeAnalysis.SeedDefault ? $" Seed={env.SeedExpr}" : "") +
        (env.SeedPerPointExpr != Analysis.NoiseDefault ? $" SeedPerPoint={env.SeedPerPointExpr}" : "") +
        (env.EvmSplitExpr != Analysis.NoiseDefault ? $" EvmSplit={env.EvmSplitExpr}" : "");

    /// <summary>The loadpull noise keys (brief NZ-6), each only when it differs from its default, so a line without them
    /// round-trips to the bytes it always did.</summary>
    private static void AppendLoadpullNoise(StringBuilder sb, ILoadpullNoiseKeys n)
    {
        if (n.NoiseExpr != Analysis.NoiseDefault) sb.Append($" Noise={n.NoiseExpr}");
        if (n.NoiseFreqExpr.Trim().Length > 0) sb.Append($" NoiseFreq=\"{n.NoiseFreqExpr}\"");
        if (n.NoiseFreqUnit != "Hz") sb.Append($" NoiseFreqUnit={n.NoiseFreqUnit}");
        if (n.NoiseInExpr.Trim().Length > 0) sb.Append($" NoiseIn={n.NoiseInExpr}");
        if (n.NoiseOutExpr.Trim().Length > 0) sb.Append($" NoiseOut={n.NoiseOutExpr}");
        if (n.NoiseSidebandExpr != HarmonicBalanceAnalysis.NoiseSidebandDefault)
            sb.Append($" NoiseSideband=\"{n.NoiseSidebandExpr}\"");
    }

    private static string FormatParametricSweepAnalysis(ParametricSweepAnalysis ps)
    {
        if (ps.Spec is { } spec)
        {
            // Compact Start/Stop/Step|Npts form — preserves the user's original intent.
            var sb = new StringBuilder($"analysis {ps.Name} type=parametric_sweep Var={ps.SweepVarName}");
            if (spec.Kind == SweepKind.Log) sb.Append(" log");
            sb.Append($" Start={spec.Start.ToString(CultureInfo.InvariantCulture)}");
            sb.Append($" Stop={spec.Stop.ToString(CultureInfo.InvariantCulture)}");
            if (spec.Mode == SweepAxisMode.PointCount)
                sb.Append($" Npts={(int)Math.Round(spec.StepOrCount)}");
            else
                sb.Append($" Step={spec.StepOrCount.ToString(CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrEmpty(spec.Unit))
                sb.Append($" Unit={spec.Unit}");
            sb.Append($" Inner={ps.InnerAnalysisName}");
            return sb.ToString();
        }

        // Explicit list form.
        var values = string.Join(",", ps.SweepValues.Select(
            v => v.ToString(CultureInfo.InvariantCulture)));
        return $"analysis {ps.Name} type=parametric_sweep Var={ps.SweepVarName} Values={values} Inner={ps.InnerAnalysisName}";
    }

    private static string FormatSParameterAnalysis(SParameterAnalysis sp)
    {
        // Emit one "analysis" line per segment, all with the same name.
        // A single-segment analysis emits exactly one line (the common case).
        var lines = new StringBuilder();
        foreach (var f in sp.Sweeps)
        {
            var line = new StringBuilder($"analysis {sp.Name} type=sparam");
            if (f.Kind == SweepKind.Log) line.Append(" log");
            line.Append($" start=\"{f.StartExpr}\" startUnit={f.StartUnit} stop=\"{f.StopExpr}\" stopUnit={f.StopUnit}");
            if (f.Mode == FreqSpecMode.PointCount)
                line.Append($" npts={f.NumPoints}");
            else
                line.Append($" step=\"{f.StepExpr}\" stepUnit={f.StepUnit}");
            // MarginThreshold belongs to the ANALYSIS, not to a segment, so it is written on the
            // first line only — which is also what the reader's segment merge keeps (WSP-9).
            if (lines.Length == 0 && sp.MarginThresholdExpr != Analysis.MarginThresholdDefault)
                line.Append($" MarginThreshold={sp.MarginThresholdExpr}");
            // The NDF knob and its two passivation lists ride the same first line, for the same
            // reason (brief-wsprobe-6 R-wsp6-2). Quoted, because a list may carry commas.
            if (lines.Length == 0 && sp.NdfExpr != Analysis.NdfDefault)
                line.Append($" NDF={sp.NdfExpr}");
            if (lines.Length == 0 && sp.PassiveVarsExpr.Length > 0)
                line.Append($" PassiveVars=\"{sp.PassiveVarsExpr}\"");
            if (lines.Length == 0 && sp.PassiveParamsExpr.Length > 0)
                line.Append($" PassiveParams=\"{sp.PassiveParamsExpr}\"");
            if (lines.Length == 0 && sp.NoiseExpr != Analysis.NoiseDefault)
                line.Append($" Noise={sp.NoiseExpr}");
            if (lines.Length == 0 && sp.NoiseContribExpr != Analysis.NoiseContribDefault)
                line.Append($" NoiseContrib={sp.NoiseContribExpr}");
            if (lines.Length > 0) lines.Append('\n');
            lines.Append(line);
        }
        return lines.ToString();
    }
}
