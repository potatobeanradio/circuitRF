namespace CircuitRF.Core.Netlist;

/// <summary>
/// One key an <c>analysis</c> directive may carry: what it is called in a <c>.cnl</c>, whether the
/// directive is incomplete without it, and what the reader falls back to when it is absent.
///
/// <para><b>The default is stated here as documentation, not as behaviour</b> — the reader's own
/// <c>GetValueOrDefault</c> calls remain the thing that applies it. Two copies of a default would
/// be a second place to change, and this table would go stale in exactly the way it exists to
/// prevent. What this table IS authoritative about is the key's NAME and whether it is required.</para>
/// </summary>
/// <param name="Name">The canonical <c>.cnl</c> spelling, as the reader reads it and the writer writes it.</param>
/// <param name="Required">
/// True when the directive cannot run without it. A required key absent is refused at PARSE time,
/// which is the whole point: the engine already refuses a missing <c>LoadTuner=</c>, but it does so
/// one key per run, after a process launch and an elaboration — see AUT-8 R-aut8-1.
/// </param>
/// <param name="Default">The reader's fallback when the key is absent, for the generated reference. Null when required.</param>
/// <param name="Summary">One line, for the generated reference page (AUT-10 R-aut10-1).</param>
/// <param name="Indexed">
/// True for a key written <c>Name[i]</c> — HB's per-tone <c>Tone[1]</c>/<c>ToneUnit[1]</c>. The base
/// name alone is then not a legal key unless it is separately listed (HB lists <c>Tone</c> too,
/// because the single-tone spelling is the ordinary one).
/// </param>
public sealed record AnalysisDirectiveKey(
    string  Name,
    bool    Required = false,
    string? Default  = null,
    string  Summary  = "",
    bool    Indexed  = false);

/// <summary>
/// One <c>analysis type=</c> token and the complete set of keys its directive may carry.
///
/// <para><b>This is the registry the reader validates against, not a document about it.</b>
/// <see cref="CnlReader"/> refuses an unknown type token and an unknown key by consulting this
/// table, so a key that is not here is refused rather than accepted-and-discarded, and a key that
/// is here but that no parser reads is caught by <c>NetlistContractTests</c>. There is no third
/// category — which is the failure mode AUT-8 exists to remove.</para>
/// </summary>
/// <param name="Type">The canonical token, exactly as <c>type=</c> must be written for the reader to be unambiguous.</param>
/// <param name="TypeAliases">
/// Other spellings accepted for <c>type=</c>. These are the <c>.csch</c> serialisation's own tags
/// ("sp", "hb", "sweep", "lp", "lpp") — the two serialisations of one concept diverging is what
/// cost an out-of-process client eight guesses to find <c>loadpull_pursuit</c> (AUT-8 R-aut8-2).
/// </param>
/// <param name="Keys">Every key legal on the directive, in the order the reference page should list them.</param>
/// <param name="BareWords">Bare keywords (no <c>=</c>) the directive accepts, beyond a trailing unit token.</param>
/// <param name="RequiredOneOf">
/// Groups where exactly one member must be present. HB needs a tone, but a multi-tone HB spells it
/// <c>Tone[1]</c> and never writes a bare <c>Tone=</c>; a parametric sweep needs either an explicit
/// <c>Values=</c> list or a <c>Start=</c>/<c>Stop=</c> range. Neither is expressible as a per-key flag.
/// </param>
public sealed record AnalysisDirectiveSpec(
    string                              Type,
    IReadOnlyList<string>               TypeAliases,
    IReadOnlyList<AnalysisDirectiveKey> Keys,
    IReadOnlyList<string>               BareWords     = null!,
    IReadOnlyList<string[]>             RequiredOneOf = null!)
{
    public IReadOnlyList<string>   BareWords     { get; init; } = BareWords     ?? [];
    public IReadOnlyList<string[]> RequiredOneOf { get; init; } = RequiredOneOf ?? [];
}

/// <summary>
/// What the <c>.cnl</c> reader understands on an <c>analysis</c> line — every legal <c>type=</c>
/// token and, for each, every legal key.
///
/// <para><b>Why this exists (AUT-8 §R-aut8-1/2/3).</b> The reader used to collect a directive's
/// <c>key=value</c> pairs into a dictionary and read the ones it knew. A key it did not know was
/// accepted and discarded in silence: <c>check</c> was clean, <c>explain</c> was clean, and the run
/// then failed one missing key at a time — or, worse, did not fail. <c>Unit=GHz</c> on a
/// <c>sparam</c> directive was discarded and the sweep ran in Hz, producing a completely plausible
/// flat S11 nine orders of magnitude from the band that was asked for. Refusing what the reader does
/// not understand converts each of those into one line of output.</para>
///
/// <para><b>Aliases are computed, not tabulated</b> — see <see cref="Canonicalise"/>. The schematic's
/// own key names are these names with an <c>Lp</c>/<c>Lpp</c>/<c>Psa</c> prefix and an
/// <c>Expr</c>/<c>Name</c>/<c>Path</c> suffix, so the rule that maps one to the other is three
/// string operations and cannot fall out of step the way a hand-written table of ~60 pairs would.</para>
/// </summary>
public static partial class AnalysisDirectiveSchema
{
    /// <summary>Keys legal on every directive regardless of type.</summary>
    private static readonly AnalysisDirectiveKey[] _universal =
    [
        new("type",    Required: true,  Summary: "The analysis kind. One of the tokens this page lists."),
        new("enabled", Default: "true", Summary: "false skips the analysis at run time without deleting it."),
        new("model",   Default: "circuit", Summary: "circuit runs on the circuit models; em runs on the EM result of the schematic's own layout at the variables' values, and switches every EM-capable cell instance to EM."),
        new("emSetup", Summary: "The .cem that solves a model=em analysis when the layout has several: a path relative to the netlist, or a setup name."),
    ];

    private static readonly AnalysisDirectiveSpec[] _specs =
    [
        new("dc", ["operating_point", "op"],
        [
            // DcAnalysis carries nothing but its name — the bias point is a property of the circuit,
            // not of the directive. Listing no keys is the honest statement of that, and it is what
            // makes `type=dc Start=0` a refusal rather than a silently ignored line.
        ]),

        new("sparam", ["sp", "s_param", "sparameter", "s_parameters"],
        [
            new("start",     Required: true,  Summary: "First frequency. Bare number in Hz unless a unit follows or Unit=/startUnit= is given."),
            new("stop",      Required: true,  Summary: "Last frequency, same spelling rules as start."),
            new("step",      Default: "1e8",  Summary: "Step size. Mutually exclusive with npts; npts wins when both are given."),
            new("npts",                       Summary: "Point count. Selects the point-count sweep mode."),
            new("Unit",                       Summary: "Sets startUnit, stopUnit and stepUnit at once. Any one of those given individually overrides it."),
            new("startUnit", Default: "Hz",   Summary: "Unit for start, when it is not written inline."),
            new("stopUnit",  Default: "Hz",   Summary: "Unit for stop."),
            new("stepUnit",  Default: "Hz",   Summary: "Unit for step."),
            new("log",       Default: "false",Summary: "Logarithmic spacing. Also accepted as the bare word 'log'."),
            new("MarginThreshold", Default: "-15", Summary: "WSProbe stability margin, in dB, below which the run reports a probe as worth looking at. 'none' disables the report."),
            new("NDF", Default: "no", Summary: "yes emits the normalized determinant function over the sweep, and with it the right-half-plane pole count of the whole network. Refuses, naming the instance, on any device circuitRF cannot render passive."),
            new("PassiveVars", Summary: "Global variables the passive (Delta-0) assembly re-elaborates at 0 — how an SDD declares which of its terms is the controlled source. Comma- or space-separated."),
            new("PassiveParams", Summary: "Instance parameters, spelled <instance>.<parameter>, that the passive assembly re-elaborates at 0 — the only route into a compiled or external model."),
        ], BareWords: ["log"]),

        new("env", ["envelope", "circuit_envelope"],
        [
            new("Stop",      Default: "100e-6",  Summary: "Simulated time after the start point (s)."),
            new("Step",      Default: "auto",    Summary: "Envelope time step (s), or auto: the signal rate times the Accuracy oversampling."),
            new("Accuracy",  Default: "standard",Summary: "draft (1x the signal rate), standard (4x) or fine (8x). 1x reads ACPR2 ~0.4 dB and bias-node memory poorly; 4x is converged on measured cases."),
            new("MaxHarm",   Default: "5",       Summary: "Highest harmonic of the fundamental carried by every envelope step."),
            new("Envelope",  Default: "auto",    Summary: "auto: carriers farther apart than 3x their bandwidth each get their own fundamental (two-fundamental lattice). single: one envelope around Fc spanning every carrier and their IM3 - the step shrinks with the spacing. lattice: as auto."),
            new("MaxMixOrder", Default: "7",     Summary: "Mixing order kept when widely spaced carriers get their own fundamentals (|k1|+|k2| <= order). 7 converges IM5; at 5, IM5 sits on the truncation edge (measured 0.5-1 dB off)."),
            new("Probes",    Default: "",        Summary: "DEPRECATED. A run records every labeled net's voltage and every IProbe's power; the nodes listed here join the recorded voltages, with a note."),
            new("Zmeas",     Default: "50",      Summary: "DEPRECATED. A run reports voltages at labeled nets and power at IProbes; any value other than 50 earns a warning. Place an IProbe in series with a load to measure the power into it."),
            new("Segments",  Default: "auto",    Summary: "Parallel-in-time segments: auto (one per core, none shorter than 4x the warm-up; one when the circuit's memory is too long to warm up in parallel) or a count; 1 runs sequentially."),
            new("Warmup",    Default: "auto",    Summary: "Warm-up simulated and discarded before each segment (s), or auto: 5 us, or with Segments auto five of the circuit's slowest time constants (a trap, thermal pole or the linear network's slowest pole) when longer."),
            new("Start",     Default: "0",       Summary: "Signal-record time of t = 0 (s)."),
            new("Tol",       Default: "1e-6",    Summary: "Newton residual tolerance per envelope step (A)."),
            new("SaveWaveforms", Default: "yes", Summary: "yes keeps the probe envelopes as a time cube; no keeps the measurements only."),
        ]),

        new("hb", ["harmonic_balance", "harmonicbalance"],
        [
            new("Tone",                       Summary: "Fundamental frequency expression. Single-tone spelling; a multi-tone run writes Tone[1], Tone[2], …"),
            new("ToneUnit",   Default: "Hz",  Summary: "Unit for Tone."),
            new("Tone",       Indexed: true,  Summary: "Tone[i] — the i-th tone of a multi-tone run, 1-based and contiguous."),
            new("ToneUnit",   Indexed: true,  Summary: "ToneUnit[i] — unit for Tone[i]. Default Hz."),
            new("NumFreqs",   Default: "1",   Summary: "Number of independent tones."),
            new("MaxMixOrder",Default: "5",   Summary: "Highest mixing order retained in a multi-tone spectrum."),
            new("MaxHarm",    Default: "7",   Summary: "Highest harmonic of each tone."),
            new("FFTOverSample", Default: "1",Summary: "Oversampling factor for the time-domain transform."),
            new("Tol",        Default: "1e-6",Summary: "Newton convergence tolerance."),
            new("DriveStepping", Default: "IfNecessary", Summary: "Drive-ladder policy: Always | IfNecessary | Never."),
            new("GuardHarmonic", Default: "0",Summary: "Extra harmonics carried above MaxHarm as an aliasing guard."),
            new("Lambda",     Default: "1",   Summary: "Newton damping factor."),
            new("MaxIter",    Default: "100", Summary: "Newton iteration cap."),
            new("Sweep",                      Summary: "DEPRECATED and ignored by the engine. Wrap the analysis in a parametric_sweep instead."),
            new("MarginThreshold", Default: "-15", Summary: "WSProbe stability margin, in dB, below which the run reports a probe as worth looking at. 'none' disables the report. Read over the small-signal sweep of a probed HB run."),
            new("SSStart",                    Summary: "First probe (tickle) frequency of the small-signal sweep around the converged HB operating point. Absent means no small-signal solve at all."),
            new("SSStop",                     Summary: "Last probe frequency, same spelling rules as SSStart."),
            new("SSStep",                     Summary: "Probe-frequency step size. Mutually exclusive with SSNpts; SSNpts wins when both are given."),
            new("SSNpts",                     Summary: "Probe-frequency point count. Selects the point-count sweep mode."),
            new("SSUnit",     Default: "Hz",  Summary: "Unit for SSStart, SSStop and SSStep alike."),
            new("SSLog",      Default: "false", Summary: "Logarithmic probe-frequency spacing."),
            new("SSMaxHarm",                  Summary: "Sideband order K_ss retained in the conversion matrix. Defaults to MaxHarm; a lower value is reported in the run summary."),
        ], RequiredOneOf: [["Tone", "Tone[1]"]]),

        new("loadpull", ["lp"],
        [
            new("Tone",       Required: true, Summary: "Fundamental frequency expression."),
            new("ToneUnit",   Default: "Hz",  Summary: "Unit for Tone."),
            new("LoadTuner",  Required: true, Summary: "Instance name of the load Tuner in this design."),
            new("SourceTuner",Required: true, Summary: "Instance name of the source Tuner in this design."),
            new("Grid",       Required: true, Summary: "Path to the .gam termination grid, relative to the netlist."),
            new("MaxHarm",    Default: "5",   Summary: "Highest harmonic."),
            new("Sweep",      Default: "Load",Summary: "Which tuner the grid sweeps: Load | Source."),
            new("TuneHarm",   Default: "1",   Summary: "Harmonic the grid tunes."),
            new("Compression",Default: "3",   Summary: "Gain compression in dB that defines the reported operating point."),
            new("GainType",   Default: "Gt",  Summary: "Gt (transducer) or Gp (power)."),
            new("PinStart",   Default: "-20", Summary: "First drive level, dBm."),
            new("PinStep",    Default: "1",   Summary: "Drive ladder step, dB."),
            new("PinMax",     Default: "10",  Summary: "Highest drive level, dBm."),
            new("Tickle",     Default: "-50", Summary: "Small-signal probe level, dBm, or 'off'."),
            new("MaxIter",    Default: "100", Summary: "Newton iteration cap."),
            new("FFTOverSample", Default: "1",Summary: "Oversampling factor."),
            new("Tol",        Default: "1e-6",Summary: "Newton convergence tolerance."),
            new("DriveStepping", Default: "IfNecessary", Summary: "Drive-ladder policy."),
            new("GuardHarmonic", Default: "0",Summary: "Aliasing guard harmonics."),
            new("ContinuityMargin", Default: "3", Summary: "dB margin the continuity gate allows between adjacent grid points."),
        ]),

        new("loadpull_pursuit", ["lpp", "loadpullpursuit", "pursuit"],
        [
            new("Tone",       Required: true, Summary: "Fundamental frequency expression."),
            new("ToneUnit",   Default: "Hz",  Summary: "Unit for Tone."),
            new("LoadTuner",  Required: true, Summary: "Instance name of the load Tuner in this design."),
            new("SourceTuner",Required: true, Summary: "Instance name of the source Tuner in this design."),
            new("MaxHarm",    Default: "5",   Summary: "Highest harmonic."),
            new("Sweep",      Default: "Load",Summary: "Which tuner the pursuit moves: Load | Source."),
            new("TuneHarm",   Default: "1",   Summary: "Harmonic the pursuit tunes."),
            new("Compression",Default: "3",   Summary: "Gain compression in dB defining the operating point."),
            new("GainType",   Default: "Gt",  Summary: "Gt (transducer) or Gp (power)."),
            new("PinStart",   Default: "-20", Summary: "First drive level, dBm."),
            new("PinStep",    Default: "1",   Summary: "Drive ladder step, dB."),
            new("PinMax",     Default: "10",  Summary: "Highest drive level, dBm."),
            new("Tickle",     Default: "-50", Summary: "Small-signal probe level, dBm, or 'off'."),
            new("MaxIter",    Default: "100", Summary: "Newton iteration cap."),
            new("FFTOverSample", Default: "1",Summary: "Oversampling factor."),
            new("Tol",        Default: "1e-6",Summary: "Newton convergence tolerance."),
            new("DriveStepping", Default: "IfNecessary", Summary: "Drive-ladder policy."),
            new("GuardHarmonic", Default: "0",Summary: "Aliasing guard harmonics."),
            new("ContinuityMargin", Default: "3", Summary: "dB margin the continuity gate allows."),
            new("EffType",    Default: "DE",  Summary: "Efficiency the pursuit maximises: DE | PAE."),
            new("ZsourceOBO", Default: "5",   Summary: "Output back-off, dB, at which the source impedance is recommended."),
            new("SearchMethod", Default: "SteepestAscent", Summary: "SteepestAscent | IteratedQuadratic."),
            new("OutputGrid",                 Summary: "Path to write the recommended-termination .gam. Absent = write no file; the pursuit still runs."),
            new("VSWR1",      Default: "1.5", Summary: "First recommended-termination VSWR circle."),
            new("VSWR1_resolution", Default: "4", Summary: "Points on the VSWR1 circle."),
            new("VSWR2",      Default: "3",   Summary: "Second VSWR circle."),
            new("VSWR2_resolution", Default: "4", Summary: "Points on the VSWR2 circle."),
            new("keepNonconvergingPoints", Default: "false", Summary: "Keep points the HB solve did not converge on."),
            new("nonconvergentVSWR", Default: "1.05", Summary: "VSWR circle used to replace a non-converging point."),
            new("CreateLoadpullResult", Default: "true", Summary: "Also produce the loadpull result cubes the Data Display reads."),
            new("LoadpullResultZsource", Default: "MXE", Summary: "Source impedance the loadpull result is computed at: MXE | MXP | None."),
        ]),

        new("parametric_sweep", ["sweep", "parametricsweep", "param_sweep"],
        [
            new("Var",   Required: true, Summary: "Name of the global variable this sweep drives."),
            new("Inner", Required: true, Summary: "Name of the analysis this sweep wraps."),
            new("Values",                Summary: "Explicit comma-separated list. Mutually exclusive with Start/Stop."),
            new("Start",                 Summary: "First value of the swept range."),
            new("Stop",                  Summary: "Last value of the swept range."),
            new("Step",                  Summary: "Step size. Mutually exclusive with Npts; Npts wins when both are given."),
            new("Npts",                  Summary: "Point count."),
            new("Unit",  Default: "",    Summary: "Unit the Start/Stop/Step numbers, or the Values list, are written in. Absent = base SI."),
            new("log",   Default: "false", Summary: "Logarithmic spacing. Also accepted as the bare word 'log'."),
        ], BareWords: ["log"], RequiredOneOf: [["Values", "Start"], ["Values", "Stop"]]),

        // brief-sar-9 R-sar9-3 — an EM block's per-port fields recombined by this circuit's port waves
        new("fields", ["field_cosim", "cosim"],
        [
            new("block",    Required: true,  Summary: "Instance path of the S-parameter block whose .sNp was written by a 3D run with Sar.PerPortFields."),
            new("freq",     Required: true,  Summary: "The frequency the fields are recombined at: one the 3D run kept (its Sar.FrequenciesGHz). Bare number in Hz unless freqUnit= or Unit= says otherwise."),
            new("freqUnit", Default: "Hz",   Summary: "Unit for freq."),
            new("Unit",                      Summary: "Same as freqUnit."),
            new("drive",    Default: "1",    Summary: "The circuit port number that drives the circuit; every other circuit port is terminated in its Z0. Several, comma-separated (1,2), drive at once."),
            new("weights",                   Summary: "Each driven port's incident-wave amplitude, comma-separated in drive='s order: complex expressions over the globals (W1,W2 or 1,polar(1,-90)), so tune mag(W2)/phase(W2) steer a channel. Empty: 1 each. Results are per watt of the drives' total."),
            new("norm",     Default: "accepted", Summary: "accepted | available: the drive power every SAR and B1 result is per watt of."),
            new("average",  Default: "yes",  Summary: "yes: averaged SAR too (the setup's masses). no: local SAR and B1 only — the fast path."),
        ]),
    ];

    /// <summary>Every directive spec, in the order the reference page should list them.</summary>
    public static IReadOnlyList<AnalysisDirectiveSpec> Specs => _specs;

    /// <summary>The canonical <c>type=</c> tokens, for a refusal message and for the generated page.</summary>
    public static IReadOnlyList<string> TypeTokens { get; } = [.. _specs.Select(s => s.Type)];

    /// <summary>Keys legal on every directive whatever its type.</summary>
    public static IReadOnlyList<AnalysisDirectiveKey> UniversalKeys => _universal;

    private static readonly Dictionary<string, AnalysisDirectiveSpec> _byToken = BuildTokenIndex();

    private static Dictionary<string, AnalysisDirectiveSpec> BuildTokenIndex()
    {
        var map = new Dictionary<string, AnalysisDirectiveSpec>(StringComparer.Ordinal);
        foreach (var spec in _specs)
        {
            map[NormaliseTypeToken(spec.Type)] = spec;
            foreach (var alias in spec.TypeAliases)
                map[NormaliseTypeToken(alias)] = spec;
        }
        return map;
    }

    /// <summary>
    /// Lower-cases and drops separators, so <c>loadpull_pursuit</c>, <c>loadpull-pursuit</c> and
    /// <c>LoadpullPursuit</c> are one token rather than three near-misses.
    /// </summary>
    private static string NormaliseTypeToken(string token)
    {
        Span<char> buf = stackalloc char[token.Length];
        int n = 0;
        foreach (char c in token)
            if (c is not ('_' or '-' or ' '))
                buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    /// <summary>
    /// The directive spec for a <c>type=</c> token, or null when nothing here understands it.
    /// Null is what makes R-aut8-2 a refusal: the reader used to fall through to a raw directive and
    /// then report "the document declares no analysis", which named neither the token nor the problem.
    /// </summary>
    public static AnalysisDirectiveSpec? Find(string typeToken)
        => _byToken.GetValueOrDefault(NormaliseTypeToken(typeToken));

    // ── Key canonicalisation ─────────────────────────────────────────────────

    /// <summary>
    /// Reduces a key to the form both serialisations agree on: case-folded, underscores dropped, an
    /// <c>Lp</c>/<c>Lpp</c>/<c>Psa</c> prefix removed and an <c>Expr</c>/<c>Name</c>/<c>Path</c>
    /// suffix removed.
    ///
    /// <para><b>This IS the alias table.</b> The schematic serialises the same concepts under
    /// <c>LpLoadTunerName</c>, <c>LpToneExpr</c>, <c>LppOutputGridPath</c> and about twenty more; the
    /// <c>.cnl</c> spelling is each of those with the prefix and the suffix taken off. An out-of-process
    /// client that copied key names out of a schematic's own analysis block had every one of them
    /// accepted and discarded (AUT-8 R-aut8-1). Deriving the mapping rather than tabulating ~60 pairs
    /// is what keeps it true after the next key is added.</para>
    ///
    /// <para>Applied to the legal names too, so the comparison is canonical-to-canonical. On a legal
    /// name it is a no-op beyond case and underscores — no <c>.cnl</c> key carries any of the three
    /// prefixes or the three suffixes, which <c>NetlistContractTests</c> asserts so that adding one
    /// that does is a failing test rather than a collision.</para>
    /// </summary>
    public static string Canonicalise(string key)
    {
        string s = key.Replace("_", "", StringComparison.Ordinal);

        foreach (var prefix in _prefixes)
            if (s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            { s = s[prefix.Length..]; break; }

        foreach (var suffix in _suffixes)
            if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            { s = s[..^suffix.Length]; break; }

        return s.ToLowerInvariant();
    }

    // Longest first: "Lpp" must win over "Lp", or LppEffType canonicalises to "peff".
    private static readonly string[] _prefixes = ["Lpp", "Psa", "Lp"];
    private static readonly string[] _suffixes = ["Expr", "Name", "Path"];

    /// <summary>
    /// Every legal key of one directive, canonical spelling → the key. Indexed keys appear under
    /// their base name; <see cref="AnalysisDirectiveKey.Indexed"/> says whether the <c>[i]</c> form
    /// is the legal one.
    /// </summary>
    public static IReadOnlyDictionary<string, AnalysisDirectiveKey> LegalKeys(AnalysisDirectiveSpec spec)
        => _legalKeyCache.TryGetValue(spec.Type, out var m) ? m : BuildLegalKeys(spec);

    // Concurrent: netlists are read on several threads at once (brief-tuneopt-2 R-to2-5), and a plain
    // Dictionary written by two of them can corrupt itself.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, AnalysisDirectiveKey>> _legalKeyCache = new();

    private static IReadOnlyDictionary<string, AnalysisDirectiveKey> BuildLegalKeys(AnalysisDirectiveSpec spec)
    {
        var map = new Dictionary<string, AnalysisDirectiveKey>(StringComparer.Ordinal);
        foreach (var k in _universal.Concat(spec.Keys))
        {
            // HB lists Tone twice — plain and indexed. The plain entry is the one a bare "Tone="
            // must resolve to, so it wins; the indexed entry is found through IndexedKeys.
            var c = Canonicalise(k.Name);
            if (!map.TryGetValue(c, out var existing) || (existing.Indexed && !k.Indexed))
                map[c] = k;
        }
        _legalKeyCache[spec.Type] = map;
        return map;
    }

    /// <summary>Whether <paramref name="baseName"/> may legally be written <c>Name[i]</c> on this directive.</summary>
    public static bool AllowsIndexed(AnalysisDirectiveSpec spec, string baseName)
    {
        var c = Canonicalise(baseName);
        return spec.Keys.Any(k => k.Indexed && Canonicalise(k.Name) == c);
    }

    /// <summary>Whether <paramref name="key"/> may legally be written plainly (no <c>[i]</c>) on this directive.</summary>
    public static bool AllowsPlain(AnalysisDirectiveSpec spec, string key)
    {
        var c = Canonicalise(key);
        return _universal.Any(k => Canonicalise(k.Name) == c)
            || spec.Keys.Any(k => !k.Indexed && Canonicalise(k.Name) == c);
    }

    /// <summary>
    /// The canonical <c>.cnl</c> spelling of a key the caller may have written in the schematic's
    /// serialisation, or null when this directive has no such key. <c>"LpLoadTunerName"</c> →
    /// <c>"LoadTuner"</c>; <c>"Tone[2]"</c> → <c>"Tone[2]"</c>; <c>"nonsense"</c> → null.
    /// </summary>
    public static string? ResolveKey(AnalysisDirectiveSpec spec, string writtenKey)
    {
        if (TrySplitIndexed(writtenKey, out var baseName, out int index))
        {
            if (!AllowsIndexed(spec, baseName)) return null;
            var canon = LegalKeys(spec).GetValueOrDefault(Canonicalise(baseName));
            var name  = spec.Keys.FirstOrDefault(k => k.Indexed && Canonicalise(k.Name) == Canonicalise(baseName))?.Name
                        ?? canon?.Name ?? baseName;
            return $"{name}[{index}]";
        }

        if (!AllowsPlain(spec, writtenKey)) return null;
        var plain = _universal.FirstOrDefault(k => Canonicalise(k.Name) == Canonicalise(writtenKey))
                 ?? spec.Keys.First(k => !k.Indexed && Canonicalise(k.Name) == Canonicalise(writtenKey));
        return plain.Name;
    }

    /// <summary>
    /// The legal key names of a directive, in declaration order, for a refusal message. Indexed keys
    /// are shown in the form they are written, so a reader of the message can copy them.
    /// </summary>
    public static IReadOnlyList<string> KeyNamesForMessage(AnalysisDirectiveSpec spec)
        => [.. _universal.Select(k => k.Name)
                .Concat(spec.Keys.Select(k => k.Indexed ? k.Name + "[i]" : k.Name))
                .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Splits <c>Tone[2]</c> into ("Tone", 2). Returns false for a plain key. An index that is not a
    /// positive integer is NOT silently treated as a plain key — <c>Tone[x]</c> comes back as base
    /// "Tone[x]" with index 0, which fails the key lookup and is refused by name.
    /// </summary>
    public static bool TrySplitIndexed(string key, out string baseName, out int index)
    {
        baseName = key; index = 0;
        int open = key.IndexOf('[');
        if (open <= 0 || key[^1] != ']') return false;
        var inner = key[(open + 1)..^1];
        if (!int.TryParse(inner, System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out int n) || n < 1)
            return false;
        baseName = key[..open];
        index    = n;
        return true;
    }
}
