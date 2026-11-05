using System;
using System.Collections.Generic;
using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Design;

// ── Analysis hierarchy (data-model §4) ───────────────────────────────────────
// Typed shapes exist for Phase 2; the Phase-1 .cnl reader does NOT populate them —
// it fills RawDirective records instead. Types defined here so the design-layer
// shape is correct when the directive grammar is settled.

/// <summary>What an analysis's circuit is built from (brief-sdl-6 R-sdl6-1).</summary>
public enum AnalysisModel { Circuit, Em }

public abstract class Analysis(string name)
{
    public string Name    { get; } = name;
    /// <summary>When false, the analysis stays configured but is skipped at run time.</summary>
    public bool   Enabled { get; set; } = true;

    // ── What simulates it (brief-sdl-6 R-sdl6-1) ─────────────────────────────

    /// <summary>
    /// <c>model=</c>: what the circuit this analysis runs on is built from. <see cref="AnalysisModel.Circuit"/> (the
    /// default, and every analysis written before the key existed) is the circuit models; <see cref="AnalysisModel.Em"/>
    /// is the EM result of the schematic's own layout, at whatever values its variables have (SDL-6 Path A), and switches
    /// every EM-capable cell instance below it to EM too. The run host does the switching — the elaborator only ever sees
    /// the instances it produces.
    /// </summary>
    public AnalysisModel Model { get; set; } = AnalysisModel.Circuit;

    /// <summary><c>emSetup=</c>: the <c>.cem</c> that solves it when the layout has several, or null for the one there is.
    /// A path relative to the netlist's base, or a setup name. Meaningless with <see cref="AnalysisModel.Circuit"/>.</summary>
    public string? EmSetup { get; set; }

    /// <summary>The <c>model=</c> spelling of <paramref name="model"/>.</summary>
    public static string ModelToken(AnalysisModel model) => model == AnalysisModel.Em ? "em" : "circuit";

    /// <summary>Reads a <c>model=</c> value: <c>em</c> is EM, anything else the circuit models — a mistyped switch must not
    /// start EM solves nobody asked for.</summary>
    public static AnalysisModel ParseModel(string? token) =>
        token?.Trim().Trim('"').Equals("em", StringComparison.OrdinalIgnoreCase) == true ? AnalysisModel.Em : AnalysisModel.Circuit;

    // ── The WSProbe stability-margin threshold (WSP-9 R-wsp9-3) ──────────────

    /// <summary>The default <c>MarginThreshold=</c>, in dB — [M] §IV's own rule of thumb.</summary>
    public const string MarginThresholdDefault = "-15";

    /// <summary>The spelling that turns the margin diagnostic off.</summary>
    public const string MarginThresholdNone = "none";

    /// <summary>
    /// <c>MarginThreshold=</c> as the engine wants it: a threshold in dB, or <b>null</b> for
    /// <c>none</c> — the spelling that disables the report.
    ///
    /// <para>Deliberately a plain number rather than an expression: it is a fixed reporting
    /// threshold, not a swept quantity, and one that varied over a sweep would make the run's notes
    /// depend on which point produced them. Anything unreadable falls back to the default rather
    /// than failing the run, because a mistyped REPORTING threshold must never stop a simulation
    /// from producing its result.</para>
    /// </summary>
    // ── The NDF knob (brief-wsprobe-6 R-wsp6-2) ──────────────────────────────

    /// <summary>The default <c>NDF=</c> — off, so nothing about an existing analysis changes.</summary>
    public const string NdfDefault = "no";

    /// <summary>
    /// <c>NDF=</c> as a flag. <c>yes</c>/<c>true</c>/<c>on</c>/<c>1</c> turn the normalized
    /// determinant function on; anything else leaves it off, because a mistyped flag must not make a
    /// run do EXTRA work the user did not ask for — the failure direction that matters here is the
    /// one that produces an unexpected refusal on a design that was simulating happily.
    /// </summary>
    public static bool ParseNdf(string? expr)
    {
        string t = (expr ?? NdfDefault).Trim();
        return t.Equals("yes",  StringComparison.OrdinalIgnoreCase)
            || t.Equals("true", StringComparison.OrdinalIgnoreCase)
            || t.Equals("on",   StringComparison.OrdinalIgnoreCase)
            || t == "1";
    }

    public static double? ParseMarginThresholdDb(string? expr)
    {
        string t = (expr ?? MarginThresholdDefault).Trim();
        if (t.Length == 0) t = MarginThresholdDefault;
        if (t.Equals(MarginThresholdNone, StringComparison.OrdinalIgnoreCase) ||
            t.Equals("off",               StringComparison.OrdinalIgnoreCase))
            return null;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double db)
            ? db
            : double.Parse(MarginThresholdDefault, CultureInfo.InvariantCulture);
    }
}

public sealed class DcAnalysis(string name) : Analysis(name);

/// <summary>
/// brief-sar-9 R-sar9-3 — the field of an EM block in this circuit: the block's per-port fields (kept beside its <c>.sNp</c> by a
/// 3D run with <c>Sar.PerPortFields</c>) recombined with the incident waves this circuit delivers to each of its ports when
/// circuit port <see cref="DriveExpr"/> is driven. Gives SAR, B1± and their scalars at one frequency, normalised to the drive's
/// accepted or available power, with no new EM run. Every value is the directive's text, resolved at run time.
/// </summary>
public sealed class FieldsAnalysis(string name) : Analysis(name)
{
    /// <summary>The EM Field Data block's instance path (<c>EM1</c>, or <c>X1.EM1</c> inside a cell).</summary>
    public string Block { get; init; } = "";

    /// <summary>The frequency the fields are taken at: one the 3D run kept (its Sar.FrequenciesGHz).</summary>
    public string FreqExpr { get; init; } = "";

    /// <summary>The unit of <see cref="FreqExpr"/>.</summary>
    public string FreqUnit { get; init; } = "Hz";

    /// <summary>The circuit port number that drives the circuit; every other circuit port is terminated in its Z0. Several,
    /// comma-separated (<c>1,2</c>), drive at once (brief-sar-10 R-sar10-4), each with its <see cref="WeightsExpr"/>.</summary>
    public string DriveExpr { get; init; } = "1";

    /// <summary>brief-sar-10 R-sar10-4 — each driven port's incident-wave amplitude, comma-separated in <see cref="DriveExpr"/>'s
    /// order: complex expressions over the globals (<c>W1,W2</c>, or <c>1,polar(1,-90)</c>), so a channel's magnitude and phase
    /// are tunables of a complex VAR (<c>tune mag(W2)</c>, <c>tune phase(W2)</c>). Empty: 1 for every one.</summary>
    public string WeightsExpr { get; init; } = "";

    /// <summary><c>accepted</c> (the default) or <c>available</c>: the power at the drive every result is per watt of.</summary>
    public string Norm { get; init; } = NormAccepted;

    /// <summary><c>yes</c> (the default): the averaged SAR too. <c>no</c>: local SAR and B1 only, the fast path.</summary>
    public string AverageExpr { get; init; } = "yes";

    public const string NormAccepted = "accepted", NormAvailable = "available";

    /// <summary>Whether <see cref="Norm"/> asks for the available power; anything but <c>available</c> is accepted.</summary>
    public bool PerAvailable => Norm.Trim().Equals(NormAvailable, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the averaged SAR is asked for (<see cref="Analysis.ParseNdf"/>'s spellings of yes; empty is yes).</summary>
    public bool Average => AverageExpr.Trim().Length == 0 || ParseNdf(AverageExpr);
}

/// <summary>
/// S-parameter analysis. Carries one or more frequency-sweep segments; at engine time all
/// segments are unioned into a single sorted/deduped flat frequency array via <see cref="Expand"/>.
/// </summary>
public sealed class SParameterAnalysis : Analysis
{
    /// <summary>Ordered list of frequency-sweep segments. At least one segment is required.</summary>
    public IReadOnlyList<FrequencySpec> Sweeps { get; }

    // ── Single-segment convenience constructor (backward compat) ──────────────
    public SParameterAnalysis(string name, FrequencySpec freq) : base(name)
        => Sweeps = [freq];

    // ── Multi-segment constructor ─────────────────────────────────────────────
    public SParameterAnalysis(string name, IReadOnlyList<FrequencySpec> sweeps) : base(name)
    {
        if (sweeps.Count < 1)
            throw new ArgumentException("SParameterAnalysis requires at least one FrequencySpec.", nameof(sweeps));
        Sweeps = sweeps;
    }

    // ── Backward-compat: single-segment via the old Freq property ─────────────
    /// <summary>Returns the first segment. Use <see cref="Sweeps"/> when there may be multiple.</summary>
    public FrequencySpec Freq => Sweeps[0];

    /// <summary>
    /// <c>MarginThreshold=</c> from the directive, verbatim — the WSProbe stability margin in dB
    /// below which the run notes a probe as worth looking at, or <c>none</c> to disable the note
    /// (WSP-9 R-wsp9-3). Read through <see cref="Analysis.ParseMarginThresholdDb"/>.
    /// </summary>
    public string MarginThresholdExpr { get; init; } = MarginThresholdDefault;

    /// <summary>
    /// <c>NDF=</c> from the directive, verbatim — <c>yes</c> makes the engine emit the normalized
    /// determinant function over the sweep (brief-wsprobe-6). Read through
    /// <see cref="Analysis.ParseNdf"/>.
    /// </summary>
    public string NdfExpr { get; init; } = NdfDefault;

    /// <summary>
    /// <c>PassiveVars=</c> — globals the passive assembly re-elaborates at 0, the route by which a
    /// user-defined device (an SDD) declares which of its terms is the controlled source
    /// (R-wsp6-4). Verbatim; split by <c>NdfPassivation.ParseList</c>.
    /// </summary>
    public string PassiveVarsExpr { get; init; } = "";

    /// <summary>
    /// <c>PassiveParams=</c> — instance parameters (<c>X1.gmscale</c>) the passive assembly
    /// re-elaborates at 0, the only route into a compiled model. Verbatim; split by
    /// <c>NdfPassivation.ParseList</c>.
    /// </summary>
    public string PassiveParamsExpr { get; init; } = "";

    // ── Whole-analysis expand: union all segments into one sorted/deduped array ─
    /// <summary>
    /// Expands all <see cref="Sweeps"/> segments, unions their points, and returns a single
    /// sorted, deduplicated <c>double[]</c> — the flat frequency array the engine expects.
    /// </summary>
    public double[] Expand(IReadOnlyDictionary<string, Value>? globals = null,
                           IReadOnlyCollection<string>? globalsWithUnit = null)
    {
        var all = new SortedSet<double>();
        foreach (var seg in Sweeps)
            foreach (var f in seg.Expand(globals, globalsWithUnit))
                all.Add(f);
        return [.. all];
    }
}

/// <summary>
/// Circuit-envelope analysis directive (<c>type=env</c>). Every field is a raw expression/keyword string
/// resolved at engine time, like <see cref="HarmonicBalanceAnalysis"/>. The modulated stimulus comes
/// from the circuit's MOD_Source instances; the fundamental is the first source's centre frequency.
/// </summary>
public sealed class EnvelopeAnalysis(string name) : Analysis(name)
{
    /// <summary>Simulated time after the start point (s).</summary>
    public string StopExpr      { get; init; } = "100e-6";
    /// <summary>Envelope step (s) or <c>auto</c> (signal rate x the accuracy preset's oversampling).</summary>
    public string StepExpr      { get; init; } = "auto";
    /// <summary><c>draft</c> (1x), <c>standard</c> (4x, default), <c>fine</c> (8x) — oversampling of the signal rate.</summary>
    public string Accuracy      { get; init; } = "standard";
    public string MaxHarmExpr   { get; init; } = "5";
    /// <summary><c>auto</c> (default): carriers farther apart than 3x their bandwidth each get their own
    /// fundamental (a two-fundamental lattice); <c>single</c>: ONE envelope around the first source's Fc
    /// spans every carrier and their IM3 (slower, no lattice); <c>lattice</c>: as auto, stated explicitly.</summary>
    public string Envelope      { get; init; } = "auto";
    /// <summary>Mixing order kept when widely spaced carriers get their own fundamentals (|k1|+|k2| &lt;= order).</summary>
    public string MaxMixOrderExpr { get; init; } = "7";
    /// <summary>Retired (brief ENV-MEAS-3): a run records every labeled net's voltage and every IProbe's power. Kept only as
    /// an old file stated it, so the nodes it lists join the recorded voltages, with one note; no writer emits it.</summary>
    public string Probes        { get; init; } = "";
    /// <summary>Retired (brief ENV-MEAS-1): node powers are into 50 Ω. Kept only as an old file stated it, so a value other
    /// than 50 runs at 50 with a warning; no writer emits it.</summary>
    public string ZmeasExpr     { get; init; } = "50";
    /// <summary>Parallel-in-time segments: <c>auto</c> (one per core, or one when the circuit's memory is too long to warm up
    /// in parallel), or a count (1 = sequential).</summary>
    public string SegmentsExpr  { get; init; } = "auto";
    /// <summary>Warm-up before each segment (s) or <c>auto</c>: 5 us, or with <c>Segments</c> auto five of the circuit's slowest
    /// time constants when longer.</summary>
    public string WarmupExpr    { get; init; } = "auto";
    /// <summary>Signal-record time of t = 0 (s).</summary>
    public string StartExpr     { get; init; } = "0";
    public string TolExpr       { get; init; } = "1e-6";
    /// <summary>Keep the probe envelopes as a time cube (<c>yes</c>) or measurements only (<c>no</c>).</summary>
    public string SaveWaveforms { get; init; } = "yes";
}

/// <summary>
/// HB analysis directive (harmonic-balance.md §3.2).
/// Key=value fields store raw expression strings (resolved later via ResolvedGlobals).
///
/// Two spellings — one model (harmonic-balance.md §3.2):
///   Single-tone:  Tone=f0  MaxHarm=K
///   Multi-tone:   NumFreqs=N  Tone[1]=f1 … Tone[N]=fN  MaxMixOrder=M  MaxHarm=K
/// </summary>
public sealed class HarmonicBalanceAnalysis(string name) : Analysis(name)
{
    // Raw expression strings from the .cnl directive; resolved at engine time.

    // ── Single-tone spelling ───────────────────────────────────────────────────
    /// <summary>Single-tone fundamental raw expression (not baked to Hz). Ignored when NumFreqsExpr &gt; 1.</summary>
    public string ToneExpr          { get; init; } = "0";
    /// <summary>Unit for ToneExpr. Default "Hz" for back-compatibility (field unit × eval = Hz value).</summary>
    public string ToneUnit          { get; init; } = "Hz";

    // ── Multi-tone spelling ────────────────────────────────────────────────────
    /// <summary>Number of independent tones. "1" = single-tone (ToneExpr used). Default "1".</summary>
    public string NumFreqsExpr      { get; init; } = "1";
    /// <summary>
    /// Multi-tone frequencies: ToneExprs[i] is the raw expression for Tone[i+1] (0-based).
    /// Empty for single-tone (use ToneExpr instead).
    /// </summary>
    public string[] ToneExprs       { get; init; } = [];
    /// <summary>Units for ToneExprs (parallel). Missing/short entries default "Hz".</summary>
    public string[] ToneUnits       { get; init; } = [];
    /// <summary>Diamond mixing-order bound |k₁|+|k₂| ≤ MaxMixOrder (§6). Multi-tone only.</summary>
    public string MaxMixOrderExpr   { get; init; } = "5";

    // ── Common ─────────────────────────────────────────────────────────────────
    public string MaxHarmonicExpr   { get; init; } = "7";
    public string FFTOverSampleExpr { get; init; } = "1";
    public string TolExpr           { get; init; } = "1e-6";
    public string DriveSteppingExpr { get; init; } = "IfNecessary";
    public string GuardHarmonicExpr { get; init; } = "0";
    /// <summary>Newton step damping factor λ ∈ (0,1]. 1 = full Newton step (default). B2.</summary>
    public string LambdaExpr        { get; init; } = "1";
    /// <summary>Max Newton iterations per HB solve before continuation backoff. Default 100.</summary>
    public string MaxIterExpr       { get; init; } = "100";
    /// <summary>
    /// <c>MarginThreshold=</c>, as on a <c>sparam</c> directive — the WSProbe stability margin in dB
    /// below which the run notes a probe as worth looking at, or <c>none</c> to disable the note.
    /// It is read over the small-signal (<c>ssfreq</c>) sweep of a probed HB run, which is WSP-5's
    /// work; it is declared here so the key is spelled once and a document written today survives
    /// the round trip when that lands.
    /// </summary>
    public string MarginThresholdExpr { get; init; } = MarginThresholdDefault;

    // ── The small-signal (probe-tickle) sweep — WSP-5 R-wsp5-1 ────────────────
    //
    // Spelled like the S-parameter sweep and honouring its unit rules (cli.md §10A.3): SSStart /
    // SSStop with either SSStep or SSNpts, SSUnit applying to all three, SSLog for a log grid.
    // ABSENT means no small-signal solve at all and a run byte-identical to one from before WSP-5
    // (R-wsp5-9(a)) — which is why every field defaults to the empty string rather than to a
    // number: a default of "0.1" would silently give every existing HB document a sweep.

    /// <summary><c>SSStart=</c>. Empty ⇒ no small-signal sweep.</summary>
    public string SsStartExpr    { get; init; } = "";
    /// <summary><c>SSStop=</c>. Empty ⇒ no small-signal sweep.</summary>
    public string SsStopExpr     { get; init; } = "";
    /// <summary><c>SSStep=</c> — the step-size spelling. Mutually exclusive with SSNpts.</summary>
    public string SsStepExpr     { get; init; } = "";
    /// <summary><c>SSNpts=</c> — the point-count spelling. Mutually exclusive with SSStep.</summary>
    public string SsNptsExpr     { get; init; } = "";
    /// <summary><c>SSUnit=</c>, applied to SSStart/SSStop/SSStep alike (the AUT-8 lesson: one unit
    /// for the whole sweep, never a bare coefficient read as base SI).</summary>
    public string SsUnit         { get; init; } = "Hz";
    /// <summary><c>SSLog=</c> — <c>true</c>/<c>yes</c>/<c>1</c> for a logarithmic grid.</summary>
    public string SsLogExpr      { get; init; } = "";
    /// <summary><c>SSMaxHarm=</c> — the sideband order K_ss ≤ K. Empty ⇒ K.</summary>
    public string SsMaxHarmExpr  { get; init; } = "";

    /// <summary>True when the directive asked for a small-signal sweep at all.</summary>
    public bool HasSsSweep
        => SsStartExpr.Trim().Length > 0 && SsStopExpr.Trim().Length > 0;

    /// <summary>
    /// The small-signal sweep as the ordinary <see cref="FrequencySpec"/> every other frequency
    /// sweep in circuitRF is — so <c>SSUnit</c> resolves through <see cref="FreqUnit.ResolveHz"/>
    /// with the same var-unit-wins rule, and <c>SSLog</c> reaches the same log grid. Null when the
    /// directive declared none.
    /// </summary>
    public FrequencySpec? SsSweep()
    {
        if (!HasSsSweep) return null;
        var kind = ParseBool(SsLogExpr) ? SweepKind.Log : SweepKind.Linear;
        string npts = SsNptsExpr.Trim();
        if (npts.Length > 0 &&
            int.TryParse(npts, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
            return new FrequencySpec(SsStartExpr, SsStopExpr, n, kind, SsUnit, SsUnit);
        string step = SsStepExpr.Trim();
        if (step.Length == 0) return new FrequencySpec(SsStartExpr, SsStopExpr, 1, kind, SsUnit, SsUnit);
        return new FrequencySpec(SsStartExpr, SsStopExpr, step, kind, SsUnit, SsUnit, SsUnit);
    }

    /// <summary><c>true</c>/<c>yes</c>/<c>on</c>/<c>1</c>, case-insensitively; anything else false.</summary>
    public static bool ParseBool(string? s)
    {
        string t = (s ?? "").Trim();
        return t.Equals("true", StringComparison.OrdinalIgnoreCase)
            || t.Equals("yes",  StringComparison.OrdinalIgnoreCase)
            || t.Equals("on",   StringComparison.OrdinalIgnoreCase)
            || t == "1";
    }

    // Sweep fields — deprecated. Use ParametricSweepAnalysis to sweep HB.
    // Retained as init-only for .cnl back-compat read; the engine ignores them.
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepVarName  { get; init; }
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepStartExpr{ get; init; }
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepStopExpr { get; init; }
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepStepExpr { get; init; }
}

/// <summary>
/// Loadpull analysis directive (loadpull.md §2.1).
/// Key=value fields store raw expression strings (resolved at engine time).
/// </summary>
public sealed class LoadpullAnalysis(string name) : Analysis(name)
{
    // ── Required ───────────────────────────────────────────────────────────────
    public string ToneExpr        { get; init; } = "0";
    /// <summary>Unit for ToneExpr. Default "Hz" for back-compatibility (field unit × eval = Hz value).
    /// Resolved via the same var-unit-wins rule HB uses (FreqUnit.ResolveHz).</summary>
    public string ToneUnit        { get; init; } = "Hz";
    public string LoadTunerName   { get; init; } = "";   // instance name of the load Tuner
    public string SourceTunerName { get; init; } = "";   // instance name of the source Tuner
    public string GridPath        { get; init; } = "";   // path to .gam grid file (required)
    public string PinStartExpr    { get; init; } = "-20";
    public string PinMaxExpr      { get; init; } = "10"; // safety cap — always required

    // ── Optional with defaults ─────────────────────────────────────────────────
    public string MaxHarmonicExpr   { get; init; } = "5";
    public string SweepExpr         { get; init; } = "Load";   // "Load" or "Source"
    public string TuneHarmExpr      { get; init; } = "1";
    public string CompressionExpr   { get; init; } = "3";
    public string GainTypeExpr      { get; init; } = "Gt";     // "Gt" or "Gp"
    public string PinStepExpr       { get; init; } = "1";
    public string TickleExpr        { get; init; } = "-50";    // dBm; "off" to disable
    public string MaxIterExpr       { get; init; } = "100";
    public string FFTOverSampleExpr { get; init; } = "1";
    public string TolExpr           { get; init; } = "1e-6";
    public string DriveSteppingExpr { get; init; } = "IfNecessary";
    public string GuardHarmonicExpr { get; init; } = "0";
    /// <summary>
    /// Round 11 — the drive-up continuity guard's margin, dB. A Pin rung whose Pout moves further than
    /// its own Pin step did, by more than this, is re-walked by bisection continuation instead of being
    /// taken in one leap (<c>DriveLadder</c>). <c>0</c> disables the guard. Default 3 dB.
    /// </summary>
    public string ContinuityMarginExpr { get; init; } = "3";

    // Optional: source directory for resolving relative Grid paths (set by reader).
    public string? SourceDirectory  { get; init; }
}

/// <summary>
/// Resolved directive for a loadpull_pursuit analysis (Phase 4b-2).
/// All loadpull keys except Grid, plus pursuit-specific keys.
/// loadpull_pursuit.md §3.
/// </summary>
public sealed class LoadpullPursuitAnalysis(string name) : Analysis(name)
{
    // ── Shared with LoadpullAnalysis (no Grid) ────────────────────────────────
    public string ToneExpr          { get; init; } = "0";
    /// <summary>Unit for ToneExpr. Default "Hz" for back-compatibility. Resolved via the same
    /// var-unit-wins rule HB uses (FreqUnit.ResolveHz).</summary>
    public string ToneUnit          { get; init; } = "Hz";
    public string LoadTunerName     { get; init; } = "";
    public string SourceTunerName   { get; init; } = "";
    public string PinStartExpr      { get; init; } = "-20";
    public string PinMaxExpr        { get; init; } = "10";
    public string MaxHarmonicExpr   { get; init; } = "5";
    public string SweepExpr         { get; init; } = "Load";
    public string TuneHarmExpr      { get; init; } = "1";
    public string CompressionExpr   { get; init; } = "3";
    public string GainTypeExpr      { get; init; } = "Gt";
    public string PinStepExpr       { get; init; } = "1";
    public string TickleExpr        { get; init; } = "-50";
    public string MaxIterExpr       { get; init; } = "100";
    public string FFTOverSampleExpr { get; init; } = "1";
    public string TolExpr           { get; init; } = "1e-6";
    public string DriveSteppingExpr { get; init; } = "IfNecessary";
    public string GuardHarmonicExpr { get; init; } = "0";
    /// <summary>Round 11 — see <see cref="LoadpullAnalysis.ContinuityMarginExpr"/>; the pursuit's own
    /// inner drive-up is the same one, so it takes the same key.</summary>
    public string ContinuityMarginExpr { get; init; } = "3";

    // ── Pursuit-specific keys ─────────────────────────────────────────────────
    public string EffTypeExpr                { get; init; } = "DE";            // "DE" or "PAE"
    public string ZsourceOBOExpr             { get; init; } = "5";             // dB backoff
    public string SearchMethodExpr           { get; init; } = "SteepestAscent"; // SearchMethod enum name
    public string? OutputGridPath            { get; init; }             // null = no file
    public string Vswr1Expr                  { get; init; } = "1.5";
    public string Vswr1ResolutionExpr        { get; init; } = "4";
    public string Vswr2Expr                  { get; init; } = "3";
    public string Vswr2ResolutionExpr        { get; init; } = "4";
    public string KeepNonconvergingExpr      { get; init; } = "false";
    public string NonconvergentVswrExpr      { get; init; } = "1.05";
    public string CreateLoadpullResultExpr   { get; init; } = "true";  // default on
    public string LoadpullResultZsourceExpr  { get; init; } = "MXE";   // MXE | MXP | None

    public string? SourceDirectory           { get; init; }
}

/// <summary>
/// Composable parametric sweep that wraps an inner analysis (or another parametric sweep).
/// Each nesting level prepends one named axis to every cube in the resulting DataSet.
/// </summary>
public sealed class ParametricSweepAnalysis : Analysis
{
    /// <summary>The global variable to override at each sweep point.</summary>
    public string    SweepVarName      { get; }
    /// <summary>Expanded sweep values (ordered, outer→inner). Always populated.</summary>
    public double[]  SweepValues       { get; }
    /// <summary>Name of the inner analysis (HarmonicBalanceAnalysis or another ParametricSweepAnalysis).</summary>
    public string    InnerAnalysisName { get; }
    /// <summary>
    /// Compact spec used to build <see cref="SweepValues"/>. Non-null only when the sweep was
    /// defined via Start/Stop/Step or Start/Stop/Npts (not an explicit values list). Preserved so
    /// the .cnl writer can re-emit the compact form on round-trip.
    /// </summary>
    public SweepSpec? Spec             { get; }

    /// <summary>Array constructor — used when values are specified as an explicit list.</summary>
    public ParametricSweepAnalysis(string name, string sweepVarName,
                                   double[] sweepValues, string innerAnalysisName)
        : base(name)
    {
        SweepVarName      = sweepVarName;
        SweepValues       = (double[])sweepValues.Clone();
        InnerAnalysisName = innerAnalysisName;
    }

    /// <summary>
    /// Spec constructor — used when the sweep is defined via Start/Stop/Step or Start/Stop/Npts.
    /// Expands <paramref name="spec"/> into <see cref="SweepValues"/> at construction time and
    /// retains the spec for round-trip .cnl emission.
    /// </summary>
    public ParametricSweepAnalysis(string name, string sweepVarName,
                                   SweepSpec spec, string innerAnalysisName)
        : base(name)
    {
        SweepVarName      = sweepVarName;
        Spec              = spec;
        // Apply unit multiplier so SweepValues are always in base units.
        // Start and Stop are always scaled; StepOrCount is scaled only in StepSize mode
        // (in PointCount mode the count is dimensionless and must not be scaled).
        double m = Units.Scale(spec.Unit) ?? 1.0;
        SweepValues       = SweepExpander.ExpandSweep(
                                spec.Start * m,
                                spec.Stop  * m,
                                spec.Mode == SweepAxisMode.StepSize ? spec.StepOrCount * m : spec.StepOrCount,
                                spec.Mode, spec.Kind);
        InnerAnalysisName = innerAnalysisName;
    }
}

// ── Supporting types ──────────────────────────────────────────────────────────

public enum SweepKind { Linear, Log }

/// <summary>Whether a <see cref="FrequencySpec"/> segment is defined by step size or point count.</summary>
public enum FreqSpecMode { StepSize, PointCount }

/// <summary>
/// Compact spec for a parametric sweep defined by Start/Stop/StepOrCount (not an explicit list).
/// Stored on <see cref="ParametricSweepAnalysis.Spec"/> so the .cnl writer can re-emit the
/// compact Start/Stop/Step or Start/Stop/Npts form on round-trip.
/// </summary>
public sealed class SweepSpec(double start, double stop, double stepOrCount,
                               SweepAxisMode mode, SweepKind kind = SweepKind.Linear,
                               string unit = "")
{
    public double        Start        { get; } = start;
    public double        Stop         { get; } = stop;
    /// <summary>Step size (StepSize mode) or point count (PointCount mode).</summary>
    public double        StepOrCount  { get; } = stepOrCount;
    /// <summary>StepSize or PointCount (never List — use explicit array for list sweeps).</summary>
    public SweepAxisMode Mode         { get; } = mode;
    public SweepKind     Kind         { get; } = kind;
    /// <summary>
    /// General unit for Start/Stop/Step coefficients (empty = base units, scale 1).
    /// Applied at materialization so <see cref="ParametricSweepAnalysis.SweepValues"/> are always in base units.
    /// </summary>
    public string        Unit         { get; } = unit;
}

/// <summary>
/// One frequency-sweep segment: Start/Stop expressed as raw expression strings (so
/// <c>stop = "2*f0"</c> works), plus either a step-size expression or a point count.
/// Call <see cref="Expand"/> to resolve expressions and produce the concrete double[] array.
/// </summary>
public sealed class FrequencySpec
{
    // ── Stored intent (what the user typed) ───────────────────────────────────
    public string       StartExpr  { get; }
    public string       StopExpr   { get; }
    /// <summary>Step-size raw expression. Non-empty in <see cref="FreqSpecMode.StepSize"/> mode only.</summary>
    public string       StepExpr   { get; }
    /// <summary>Number of points. Non-null in <see cref="FreqSpecMode.PointCount"/> mode only. ≥ 1.</summary>
    public int?         NumPoints  { get; }
    public FreqSpecMode Mode       { get; }
    public SweepKind    Kind       { get; }
    /// <summary>Unit for StartExpr. Default "Hz" for back-compatibility.</summary>
    public string       StartUnit  { get; }
    /// <summary>Unit for StopExpr. Default "Hz" for back-compatibility.</summary>
    public string       StopUnit   { get; }
    /// <summary>Unit for StepExpr. Default "Hz" for back-compatibility.</summary>
    public string       StepUnit   { get; }

    // ── StepSize constructor (expression strings) ─────────────────────────────
    public FrequencySpec(string startExpr, string stopExpr, string stepExpr,
                         SweepKind kind = SweepKind.Linear,
                         string startUnit = "Hz", string stopUnit = "Hz", string stepUnit = "Hz")
    {
        StartExpr = startExpr;
        StopExpr  = stopExpr;
        StepExpr  = stepExpr;
        Mode      = FreqSpecMode.StepSize;
        Kind      = kind;
        StartUnit = startUnit;
        StopUnit  = stopUnit;
        StepUnit  = stepUnit;
    }

    // ── PointCount constructor ────────────────────────────────────────────────
    public FrequencySpec(string startExpr, string stopExpr, int numPoints,
                         SweepKind kind = SweepKind.Linear,
                         string startUnit = "Hz", string stopUnit = "Hz")
    {
        if (numPoints < 1)
            throw new ArgumentOutOfRangeException(nameof(numPoints), "NumPoints must be ≥ 1");
        StartExpr = startExpr;
        StopExpr  = stopExpr;
        StepExpr  = "";
        NumPoints = numPoints;
        Mode      = FreqSpecMode.PointCount;
        Kind      = kind;
        StartUnit = startUnit;
        StopUnit  = stopUnit;
        StepUnit  = "Hz";
    }

    // ── Backward-compat (doubles → expression strings, StepSize) ─────────────
    public FrequencySpec(double start, double stop, double step,
                         SweepKind kind = SweepKind.Linear)
        : this(start.ToString("R", CultureInfo.InvariantCulture),
               stop.ToString("R", CultureInfo.InvariantCulture),
               step.ToString("R", CultureInfo.InvariantCulture),
               kind) { }   // units default "Hz": inputs are already in Hz

    // ── Expand: resolve expressions → concrete freq-point array ──────────────

    /// <summary>
    /// Resolves Start/Stop/Step expressions against <paramref name="globals"/> — applying
    /// each field's unit via <see cref="FreqUnit.ResolveHz"/> with the var-unit-wins rule —
    /// and returns the concrete double[] of frequency points this segment covers.
    /// </summary>
    public double[] Expand(IReadOnlyDictionary<string, Value>? globals = null,
                           IReadOnlyCollection<string>? globalsWithUnit = null)
    {
        double start = ResolveFreqHz(StartExpr, StartUnit, globals, globalsWithUnit);
        double stop  = ResolveFreqHz(StopExpr,  StopUnit,  globals, globalsWithUnit);

        if (Mode == FreqSpecMode.PointCount)
        {
            int n = NumPoints!.Value;
            return Kind == SweepKind.Log
                ? LogSpace(start, stop, n)
                : LinSpace(start, stop, n);
        }

        double step = ResolveFreqHz(StepExpr, StepUnit, globals, globalsWithUnit);
        return Kind == SweepKind.Log
            ? LogStepSpace(start, stop, step)
            : LinearStepSpace(start, stop, step);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Resolve a frequency expression to Hz using FreqUnit when globals are available.
    // Falls back to the legacy numeric path (× unit multiplier) when globals is null or throws.
    private static double ResolveFreqHz(string expr, string unit,
        IReadOnlyDictionary<string, Value>? globals, IReadOnlyCollection<string>? globalsWithUnit)
    {
        if (globals is not null)
        {
            try { return FreqUnit.ResolveHz(expr, unit, globals, globalsWithUnit); }
            catch { }
        }
        // Legacy / fallback: resolve numerically and apply the field unit multiplier.
        return ResolveExpr(expr, globals) * FreqUnit.Multiplier(unit);
    }

    private static double ResolveExpr(string expr, IReadOnlyDictionary<string, Value>? globals)
    {
        // Fast path: plain numeric literal (covers the common "1e9", "1000000000" cases)
        if (double.TryParse(expr,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var d))
            return d;

        // Full expression path: bind Real globals into a scope and evaluate
        var scope = new Scope("freq");
        if (globals is not null)
            foreach (var (k, v) in globals)
                if (v.Kind == ValueKind.Real)
                    scope.Bind(k, v.AsReal().ToString("R", CultureInfo.InvariantCulture));

        return new Evaluator().Eval(expr, scope).AsReal();
    }

    // N points linearly spaced [start, stop]
    private static double[] LinSpace(double start, double stop, int n)
    {
        if (n == 1) return [start];
        var pts = new double[n];
        for (int i = 0; i < n; i++)
            pts[i] = start + (stop - start) * i / (n - 1);
        return pts;
    }

    // N points log-spaced [start, stop]
    private static double[] LogSpace(double start, double stop, int n)
    {
        if (n == 1) return [start];
        var pts = new double[n];
        double logRatio = Math.Log10(stop / start);
        for (int i = 0; i < n; i++)
            pts[i] = start * Math.Pow(10, logRatio * i / (n - 1));
        return pts;
    }

    // Linear additive step sweep
    private static double[] LinearStepSpace(double start, double stop, double step)
    {
        if (step <= 0) step = (stop - start) / 100.0;
        var list = new List<double>();
        for (double f = start; f <= stop + step * 1e-9; f += step)
            list.Add(f);
        return [.. list];
    }

    // Log sweep: step = multiplicative ratio per step (> 1). If ≤ 1, falls back to 100 log-pts.
    private static double[] LogStepSpace(double start, double stop, double step)
    {
        if (step <= 1.0) return LogSpace(start, stop, 100);
        var list = new List<double>();
        for (double f = start; f <= stop * (1.0 + 1e-9); f *= step)
            list.Add(f);
        return [.. list];
    }
}

// ── Measurement (declared on TestBench) ──────────────────────────────────────

/// <summary>
/// A performance expression evaluated against result cubes after simulation.
/// The grammar for measurement expressions is Phase 2+; stored as a raw string here.
/// </summary>
public sealed class Measurement(string name, string expression, string? unit = null)
{
    public string  Name       { get; } = name;
    public string  Expression { get; } = expression;
    public string? Unit       { get; } = unit;
}
