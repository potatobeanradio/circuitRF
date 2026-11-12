using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.HarmonicBalance;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Loadpull;

/// <summary>
/// Resolved parameters for a loadpull analysis (analogous to HbAnalysisParams).
/// </summary>
public sealed record LoadpullAnalysisParams(
    double             ToneHz,
    int                MaxHarmonic,
    int                FFTOverSample,
    double             Tol,
    DcBiasSteppingMode DriveStepping,
    int                GuardHarmonic,
    int                MaxIter,
    string             LoadTunerName,
    string             SourceTunerName,
    bool               SweepLoad,        // true=loadpull; false=sourcepull
    int                TuneHarm,
    GamReader.GamGrid  Grid,
    double             Compression,      // gain compression target (dB)
    bool               UseGt,            // true=Gt; false=Gp
    double             PinStartDbm,
    double             PinStepDb,
    double             PinMaxDbm,
    double?            TickleDbm,        // null = disabled
    /// <summary>Round 11 — <see cref="DriveLadder"/>'s continuity margin, dB. 0 disables the guard.
    /// Defaulted so every existing positional construction keeps compiling and keeps the guard on.</summary>
    double             ContinuityMarginDb = DriveLadder.DefaultContinuityMarginDb,
    /// <summary>Brief NZ-6: the noise read at every converged rung (<c>Noise=yes</c>), or null — and a null run is the one
    /// from before. Its ports are the tuners unless the directive named others.</summary>
    HbNoiseRequest?    Noise = null,
    /// <summary>Brief NZ-6: the output frequency the noise is read at, Hz (<c>NoiseFreq=</c>; the tone when absent).</summary>
    double             NoiseFreqHz = 0,
    /// <summary>LP-1: <c>Workers=</c> — loads solved at once; 0 defers to <see cref="AnalysisSettings.MaxParallelism"/>
    /// (itself 0 = one per core), 1 is the serial loop.</summary>
    int                Workers = 0);

/// <summary>
/// The core loadpull engine — outer Γ/Z grid × inner adaptive Pin sweep (loadpull.md §2–§5).
///
/// Orchestrates HB single-point solves via HbEngine.RunSinglePoint.
/// TunerModels are mutated between solves (SetHarmonicOverride per grid point;
/// SetSourceDrive per Pin step). InductanceRegularization=Always is forced for all solves
/// (Tuner bias-tee always triggers the voltage-pinned DC interface; loadpull.md §2.1).
/// </summary>
public sealed class LoadpullEngine
{
    private readonly ElaboratedNetlist _netlist;
    private readonly TestBench         _tb;
    private readonly HbEngine          _hbEngine;
    private readonly AnalysisSettings  _lpSettings;

    // brief-wbond-wire-temperature R-wbt-4a — true only inside Run(), the one caller that turns each step's wBond currents
    // into WireTemp. A pursuit's search calls RunOneTermination directly and reads none of them, so it must not pay K+1
    // back-solves per Pin step for them (its follow-on loadpull is a Run(), and reports WireTemp there).
    private bool _collectWBondCurrents;

    // Brief NZ-6 — the frequency the noise is read at, set only inside Run() when the directive asks for noise. A pursuit's
    // search calls RunOneTermination directly and reads no noise, so it pays nothing for it; its follow-on loadpull is a
    // Run(), and reports NF there. 0 = no noise.
    private double _noiseHz;

    // LP-1 — what a parallel grid needs to elaborate each worker's own copy of the netlist (null pins Run to the serial
    // loop), the settings each worker's engine is built from, and where this engine's progress lines go: Console.Error,
    // or a parallel load's own buffer, flushed in grid order.
    private readonly Library?          _lib;
    private readonly string?           _baseDir;
    private readonly AnalysisSettings? _settings;
    private TextWriter?                _log;
    private TextWriter Log => _log ?? Console.Error;

    /// <summary>How many workers the last <see cref="Run"/> on this thread solved its loads with. Test-facing: the claim is
    /// that the loads ran side by side, which a timing cannot hold steadily and a count can.</summary>
    [ThreadStatic] internal static int LastLoadpullDegree;

    /// <param name="lib">The library <paramref name="netlist"/> was elaborated from, and the base directory that
    /// elaboration used — needed ONLY to elaborate the per-worker copies of a parallel grid (LP-1). Null pins
    /// <see cref="Run"/> to the serial loop, which is what every caller had before.</param>
    public LoadpullEngine(ElaboratedNetlist netlist, TestBench tb, AnalysisSettings? settings = null,
                          Library? lib = null, string? baseDirectory = null)
    {
        _netlist  = netlist;
        _tb       = tb;
        _settings = settings;
        _lib      = lib;
        _baseDir  = baseDirectory;

        var base_ = settings ?? AnalysisSettings.Default;
        // Force InductanceRegularization=Always (loadpull.md §2.1).
        _lpSettings = new AnalysisSettings
        {
            HbMaxIter                 = base_.HbMaxIter,
            Gmin                      = base_.Gmin,
            ConductanceRegularization = base_.ConductanceRegularization,
            InductanceRegularization  = RegularizationMode.Always,
            InductanceRegR            = base_.InductanceRegR,
            Gmax                      = base_.Gmax,
            DcBiasStepping            = base_.DcBiasStepping,
            DriveStepping             = base_.DriveStepping,
            NonlinearAbsTol           = base_.NonlinearAbsTol,
            MaxParallelism            = base_.MaxParallelism,
        };
        _hbEngine = new HbEngine(netlist, tb, _lpSettings);
    }

    // ── Directive resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Resolve a LoadpullAnalysis directive against the elaborated globals.
    /// Pass <paramref name="globalsWithUnit"/> (from <c>ElaboratedNetlist.GlobalsWithExplicitUnit</c>)
    /// to enable the var-unit-wins rule for the tone frequency — identical to HbEngine.Resolve.
    /// </summary>
    public static LoadpullAnalysisParams Resolve(
        LoadpullAnalysis lpa,
        IReadOnlyDictionary<string, Value> globals,
        IReadOnlyCollection<string>? globalsWithUnit = null)
    {
        double Num(string expr, double def)
        {
            try
            {
                var scope = new Scope("lp-resolve");
                var ev    = new Evaluator();
                foreach (var kv in globals)
                {
                    scope.Bind(kv.Key, kv.Value.ToString()!);
                    ev.InjectResolved("lp-resolve", kv.Key, kv.Value);
                }
                return ev.EvalExpr(Parser.Parse(expr), scope) is { Kind: ValueKind.Real } v
                    ? v.AsReal()
                    : ev.EvalExpr(Parser.Parse(expr), scope).AsComplex().Real;
            }
            catch { return def; }
        }

        // Tone is the only frequency-unit-sensitive field; resolve it with HB's var-unit-wins rule.
        // (Pin/Compression/etc. are dBm/dB/counts, not frequencies — they stay on Num().)
        double tone;
        // Refused, not defaulted — see HbEngine.Resolve's ToneHz for why a substituted 1 GHz is
        // worse than no answer: every later message then describes a grid nobody asked for.
        try   { tone = FreqUnit.ResolveHz(lpa.ToneExpr, lpa.ToneUnit, globals, globalsWithUnit); }
        catch (ExpressionException ex)
        {
            throw new InvalidOperationException(
                "Loadpull " + FreqUnit.UnresolvedFieldMessage("Tone", lpa.Name, lpa.ToneExpr, ex), ex);
        }
        int    maxH    = (int)Num(lpa.MaxHarmonicExpr,   5);
        int    osamp   = Math.Max(1, (int)Num(lpa.FFTOverSampleExpr, 1));
        double tol     = Num(lpa.TolExpr,            1e-6);
        int    maxIter = Math.Max(1, (int)Num(lpa.MaxIterExpr, 100));
        int    guard   = (int)Num(lpa.GuardHarmonicExpr, 0);

        var driveStepping = DcBiasSteppingMode.IfNecessary;
        if (lpa.DriveSteppingExpr.Trim().Equals("Always", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Always;
        else if (lpa.DriveSteppingExpr.Trim().Equals("Never", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Never;

        bool   sweepLoad = !lpa.SweepExpr.Trim().Equals("Source", StringComparison.OrdinalIgnoreCase);
        int    tuneHarm  = (int)Num(lpa.TuneHarmExpr, 1);
        double compress  = Num(lpa.CompressionExpr, 3.0);
        bool   useGt     = !lpa.GainTypeExpr.Trim().Equals("Gp", StringComparison.OrdinalIgnoreCase);
        double pinStart  = Num(lpa.PinStartExpr, -20.0);
        double pinStep   = Num(lpa.PinStepExpr,    1.0);
        double pinMax    = Num(lpa.PinMaxExpr,     10.0);
        double contMargin = Num(lpa.ContinuityMarginExpr, DriveLadder.DefaultContinuityMarginDb);
        int    workers    = Math.Max(0, (int)Num(lpa.WorkersExpr, 0));

        double? tickle = null;
        var ts = lpa.TickleExpr.Trim();
        if (!ts.Equals("off", StringComparison.OrdinalIgnoreCase) &&
            !ts.Equals("false", StringComparison.OrdinalIgnoreCase))
            tickle = Num(ts, -50.0);

        if (string.IsNullOrEmpty(lpa.GridPath))
            throw new InvalidOperationException($"LoadpullAnalysis '{lpa.Name}': Grid= path is required.");
        if (!File.Exists(lpa.GridPath))
            throw new FileNotFoundException($"LoadpullAnalysis '{lpa.Name}': Grid file not found: '{lpa.GridPath}'");

        // Select the grid for this tone: a freq-less .gam applies at any frequency; a freq-tagged .gam
        // (Layer C) returns the block nearest the resolved tone — so a freq-swept loadpull uses each
        // frequency's own terminations.
        var grid = GamReader.ReadFileForFreq(lpa.GridPath, tone);

        var (noise, noiseHz) = ResolveNoise(lpa, lpa.Name, lpa.LoadTunerName, lpa.SourceTunerName, tone, globals, globalsWithUnit, Num);
        return new LoadpullAnalysisParams(
            tone, maxH, osamp, tol, driveStepping, guard, maxIter,
            lpa.LoadTunerName, lpa.SourceTunerName,
            sweepLoad, tuneHarm, grid,
            compress, useGt, pinStart, pinStep, pinMax, tickle, contMargin, noise, noiseHz, workers);
    }

    /// <summary>
    /// The loadpull noise keys (brief NZ-6) of a loadpull or a pursuit, or (null, 0) without <c>Noise=yes</c>. The frequency is
    /// 0 when <c>NoiseFreq</c> is absent, which <see cref="NoiseReadHz"/> reads as the default. <c>NoiseIn</c>
    /// and <c>NoiseOut</c> default to the source and load tuners; a number is a port's <c>Num</c>, as on an <c>hb</c>
    /// analysis, and anything else a Tuner's instance name. Every malformed key is a refusal naming it, as on HB.
    /// </summary>
    internal static (HbNoiseRequest? Request, double FreqHz) ResolveNoise(
        ILoadpullNoiseKeys keys, string analysis, string loadTuner, string sourceTuner, double toneHz,
        IReadOnlyDictionary<string, Value> globals, IReadOnlyCollection<string>? globalsWithUnit,
        Func<string, double, double> num)
    {
        if (!keys.Noise) return (null, 0);
        double hz = 0;
        if (keys.NoiseFreqExpr.Trim().Length > 0)
        {
            try { hz = FreqUnit.ResolveHz(keys.NoiseFreqExpr, keys.NoiseFreqUnit, globals, globalsWithUnit); }
            catch (ExpressionException ex)
            {
                throw new InvalidOperationException(
                    "Loadpull " + FreqUnit.UnresolvedFieldMessage("NoiseFreq", analysis, keys.NoiseFreqExpr, ex), ex);
            }
            if (!(hz > 0) || !double.IsFinite(hz))
                throw new InvalidOperationException(Diagnostic.Create("loadpull.noise.freq-not-positive", DiagnosticSeverity.Error,
                    "Loadpull '{analysis}': NoiseFreq={expr} is not a positive frequency.",
                    ("analysis", analysis), ("expr", keys.NoiseFreqExpr)).Render());
        }
        var k = HarmonicBalanceAnalysis.ParseSideband(keys.NoiseSidebandExpr);
        if (k is not { Length: 1 })
            throw new InvalidOperationException(HbNoiseDiagnostics.BadSideband(analysis, keys.NoiseSidebandExpr, 1).Render());

        // Each end: blank is the tuner; an integer is a port number; anything else is a tuner's name.
        (int Port, string? Tuner) End(string expr, string tuner)
        {
            string t = expr.Trim();
            if (t.Length == 0) return (0, tuner);
            if (int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                if (n < 1) throw new InvalidOperationException(HbNoiseDiagnostics.BadPort(analysis, "NoiseIn/NoiseOut", t).Render());
                return (n, null);
            }
            return (0, t);
        }
        var (inPort, inTuner)   = End(keys.NoiseInExpr,  sourceTuner);
        var (outPort, outTuner) = End(keys.NoiseOutExpr, loadTuner);
        return (new HbNoiseRequest(inPort, outPort, k, Contrib: false) { InTuner = inTuner, OutTuner = outTuner }, hz);
    }

    /// <summary>
    /// Where the noise is read when <c>NoiseFreq</c> is absent (brief NZ-6), relative to the tone: 1 % above it. The brief's
    /// default is "the tone", the LNA under drive, but the tone itself is degenerate (below) and a reading just beside it is
    /// not one number: under drive the conversion matrix carries baseband noise onto the output at the offset's own
    /// frequency, so a reading 1 kHz off is dominated by flicker and by the bias network's high impedance at 1 kHz — on
    /// brief NZ-5's BJT LNA, 3.6 → 8.2 → 17.4 dB from −50 to −30 dBm against 2.54 → 2.62 → 3.29 dB at 10 MHz. 1 % is the
    /// noise a small signal near the drive sees, which is what the default is for (owner's choice, 2026-11-09).
    /// </summary>
    public const double DefaultNoiseOffset = 0.01;

    /// <summary>
    /// The relative step a stated noise frequency on a degenerate point is moved by (brief NZ-6). At the tone or any
    /// half-multiple of it, a sideband and the negative of another are one frequency, and brief NZ-5's conversion matrix
    /// cannot split the noise between them (it reports NaN there). One part in a million of the tone separates the
    /// sidebands cleanly, and a displayed frequency does not change.
    /// </summary>
    public const double DegenerateNoiseOffset = 1e-6;

    /// <summary>The frequency the noise is actually read at: the tone plus <see cref="DefaultNoiseOffset"/> of it when
    /// <paramref name="requestedHz"/> is 0 (<c>NoiseFreq</c> absent); <paramref name="requestedHz"/> plus
    /// <see cref="DegenerateNoiseOffset"/> of the tone when it lands on a degenerate point (<paramref name="moved"/>);
    /// otherwise <paramref name="requestedHz"/>.</summary>
    public static double NoiseReadHz(double requestedHz, double toneHz, out bool moved)
    {
        moved = false;
        if (requestedHz <= 0) return toneHz * (1 + DefaultNoiseOffset);
        double r = 2.0 * requestedHz / toneHz;
        moved = Math.Abs(r - Math.Round(r)) <= 1e-9 * Math.Max(1.0, Math.Abs(r));
        return moved ? requestedHz + DegenerateNoiseOffset * toneHz : requestedHz;
    }

    // ── Setup context (shared between full sweep and per-termination queries) ───

    /// <summary>
    /// Pre-resolved setup for a loadpull or loadpull_pursuit run.
    /// Created once by PrepareContext; shared across RunOneTermination calls.
    /// </summary>
    public sealed class PursuitContext
    {
        public TunerModel      LoadModel      { get; }
        public TunerModel      SrcModel       { get; }
        public TunerModel      SweptModel     { get; }
        public int             LoadIfIdx      { get; }
        public int             SrcIfIdx       { get; }
        public int             K              { get; }
        public int[]           InterfaceNodes { get; }
        public string[]        NodeNames      { get; }
        public HbAnalysisParams  HbParams     { get; }
        public AnalysisSettings  SolveSettings{ get; }
        /// <summary>No nonlinear device touches the load tuner's DUT net (a composed FET, whose terminals sit
        /// behind its own linear network): Pout and the drain bias current come from the tuner's own branch
        /// currents, since the interface current there is zero.</summary>
        public bool              LoadOffDevice { get; }
        /// <summary>The same for the source tuner's DUT net: the gate bias current comes from its branches.</summary>
        public bool              SrcOffDevice  { get; }

        public PursuitContext(TunerModel load, TunerModel src, TunerModel swept,
            int loadIfIdx, int srcIfIdx, int k, int[] ifNodes, string[] nodeNames,
            HbAnalysisParams hbParams, AnalysisSettings solveSettings,
            bool loadOffDevice = false, bool srcOffDevice = false)
        {
            LoadOffDevice  = loadOffDevice;
            SrcOffDevice   = srcOffDevice;
            LoadModel      = load;
            SrcModel       = src;
            SweptModel     = swept;
            LoadIfIdx      = loadIfIdx;
            SrcIfIdx       = srcIfIdx;
            K              = k;
            InterfaceNodes = ifNodes;
            NodeNames      = nodeNames;
            HbParams       = hbParams;
            SolveSettings  = solveSettings;
        }
    }

    /// <summary>
    /// Validates, locates tuners, assigns roles, extracts interface nodes, and builds HB params.
    /// Call once; pass the resulting context to RunOneTermination.
    /// </summary>
    public PursuitContext PrepareContext(LoadpullAnalysisParams p)
    {
        // brief-wsprobe-5 §6: a probed loadpull is not refused, and it is not silently probe-less
        // either. The small-signal sweep belongs to the `hb` analysis; running it at every grid
        // point would multiply the cost by the ssfreq count, and the document's own replacement for
        // a stability loadpull is the rank-1 re-termination of ONE wsp (brief-wsprobe-3 §6).
        int probeCount = _netlist.WspProbes.Count;
        if (probeCount > 0)
            _netlist.AddNoteOnce("wsprobe.loadpull-no-ss-sweep",
                $"{probeCount} WSProbe{(probeCount == 1 ? "" : "s")} present; the small-signal " +
                "(SS*) sweep applies to the hb analysis only — at every termination of a loadpull " +
                "grid it would multiply the run by the ssfreq count. Run the hb analysis with " +
                "SSStart/SSStop to get the probes' large-signal transfer functions, then sweep the " +
                "terminations with wsp_terminate / wsp_loadpull_margin, which re-terminate that one " +
                "wsp for no further HB solves.");

        if (string.IsNullOrEmpty(p.LoadTunerName))
            throw new InvalidOperationException("LoadpullAnalysis: LoadTuner= is required.");
        if (string.IsNullOrEmpty(p.SourceTunerName))
            throw new InvalidOperationException("LoadpullAnalysis: SourceTuner= is required.");

        var (loadEc, loadModel) = FindTuner(p.LoadTunerName, "LoadTuner");
        var (srcEc,  srcModel)  = FindTuner(p.SourceTunerName, "SourceTuner");

        loadModel.SetRole(TunerRole.Load);
        srcModel.SetRole(TunerRole.Source);
        loadModel.SetTone(p.ToneHz);

        // Both tuners now declare [DUT, ref]: the DUT-facing net is Nodes[0] for either role
        // (the SourceTuner's internal RF-drive node is minted as Nodes[4] — see TunerModel).
        int loadDutNode = loadEc.Nodes.Length > 0 ? loadEc.Nodes[0] : 0;
        int srcDutNode  = srcEc.Nodes.Length  > 0 ? srcEc.Nodes[0]  : 0;

        // A tuner on a net no nonlinear device touches — a ComposedFET's terminals always sit behind its own
        // linear network — joins the HB interface as an extra node: V there is then solved and reported
        // like any interface node, its nonlinear current is zero, and Pout and the bias currents are read
        // from the tuner's own branches instead (ComputeLoadCurrent, ComputeSourceInputCurrent). A tuner
        // on a device terminal, the canonical case, takes the interface-current path exactly as before.
        var nonlinear     = _netlist.NonlinearNodes.ToHashSet();
        bool loadOff      = loadDutNode > 0 && !nonlinear.Contains(loadDutNode);
        bool srcOff       = srcDutNode  > 0 && !nonlinear.Contains(srcDutNode);
        int[] extra       = [.. new[] { (loadOff, loadDutNode), (srcOff, srcDutNode) }.Where(x => x.Item1).Select(x => x.Item2)];
        _hbEngine.ExtraInterfaceNodes = extra;
        var tempExtractor = new HbLinearExtractor(_netlist, _lpSettings, extra.Length > 0 ? extra : null);
        int[] ifNodes     = tempExtractor.InterfaceNodes;
        var nodeNames     = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();
        int loadIfIdx     = Array.IndexOf(ifNodes, loadDutNode);
        int srcIfIdx      = Array.IndexOf(ifNodes, srcDutNode);

        if (loadIfIdx < 0)
            throw new InvalidOperationException(
                $"LoadTuner '{p.LoadTunerName}' is connected to ground on its DUT side; it terminates nothing.");
        if (srcIfIdx < 0)
            throw new InvalidOperationException(
                $"SourceTuner '{p.SourceTunerName}' is connected to ground on its DUT side; it drives nothing.");

        int K = p.MaxHarmonic;
        var hbParams = new HbAnalysisParams(
            ToneFreqsHz:   [p.ToneHz],
            MaxHarmonic:   K,
            MaxMixOrder:   5,      // single-tone loadpull — MaxMixOrder ignored
            FFTOverSample: p.FFTOverSample,
            Tol:           p.Tol,
            DriveStepping: p.DriveStepping,
            GuardHarmonic: p.GuardHarmonic,
            SweepVarName:  null,
            SweepStart:    0, SweepStop: 0, SweepStep: 1,
            MaxIter:       p.MaxIter);

        var solveSettings = new AnalysisSettings
        {
            HbMaxIter                 = p.MaxIter,
            Gmin                      = _lpSettings.Gmin,
            ConductanceRegularization = _lpSettings.ConductanceRegularization,
            InductanceRegularization  = RegularizationMode.Always,
            InductanceRegR            = _lpSettings.InductanceRegR,
            Gmax                      = _lpSettings.Gmax,
            DcBiasStepping            = _lpSettings.DcBiasStepping,
            DriveStepping             = p.DriveStepping,
            NonlinearAbsTol           = _lpSettings.NonlinearAbsTol,
        };

        var sweptModel = p.SweepLoad ? loadModel : srcModel;
        return new PursuitContext(loadModel, srcModel, sweptModel,
            loadIfIdx, srcIfIdx, K, ifNodes, nodeNames, hbParams, solveSettings, loadOff, srcOff);
    }

    // ── Engine entry point ───────────────────────────────────────────────────

    /// <param name="control">
    /// Optional cancellation + progress. Checked once per GRID TERMINATION — a whole Pin drive-up
    /// runs inside one, and the adaptive ladder has no stable step count to subdivide progress by.
    /// </param>
    public DataSet Run(LoadpullAnalysisParams p, RunControl? control = null)
    {
        var ctx           = PrepareContext(p);
        var gridPoints    = new List<GridPointResult>();
        var gridPointList = p.Grid.Points;
        var firstV        = new Complex[,]?[gridPointList.Count];
        int degree        = PlanDegree(gridPointList.Count, p.Workers);
        LastLoadpullDegree = degree;
        _collectWBondCurrents = true;
        bool moved = false;
        if (p.Noise is { } req) HbNoise.ValidateEnds(_netlist, req);
        _noiseHz = p.Noise is null ? 0 : NoiseReadHz(p.NoiseFreqHz, p.ToneHz, out moved);
        if (moved)
            _netlist.AddNoteOnce("loadpull.noise-frequency-offset",
                $"Loadpull noise is read at {_noiseHz / 1e9:G9} GHz, {DegenerateNoiseOffset:G2} of the tone above the " +
                $"requested {p.NoiseFreqHz / 1e9:G9} GHz: at a half-multiple of the tone a sideband and the negative of " +
                "another are one frequency, and the noise there cannot be split between them.");
        else if (p.Noise is not null && p.NoiseFreqHz <= 0)
            _netlist.AddNoteOnce("loadpull.noise-frequency-default",
                $"Loadpull noise is read at {_noiseHz / 1e9:G9} GHz, 1 % above the tone (NoiseFreq is absent): the noise a " +
                "small signal beside the drive sees. Set NoiseFreq to read it elsewhere.");
        try
        {
            if (degree > 1) gridPoints.AddRange(RunParallel(p, ctx, degree, control));
            else for (int gi = 0; gi < gridPointList.Count; gi++)
            {
                control?.Tick();

                var gp    = gridPointList[gi];
                var gamma = gp.Gamma;
                var z     = gp.Z;

                GridLine(Log, gi, gridPointList.Count, gp);

                Complex[,]? gridSeed = FindNearestSeed(gi, gamma, firstV, gridPointList);
                var gpr = RunOneTermination(p, ctx, z, gi, gridSeed);

                // The neighbour seed is this load's FIRST converged rung — the tickle, or PinStart without one — because that is
                // the drive the next load starts at. Its LAST rung is the compressed state, tens of dB above, and seeding a
                // −50 dBm tickle from it asked one Newton solve to walk the whole drive-up back down: a device whose network,
                // thermal and trap nodes all sit in the nonlinear partition (an exported Verilog-A model) failed it at 48 of
                // 49 loads where the same model as a ComposedFET did not (brief MC-30 R-mc30-5; src/Engine/RESOLVED.md).
                firstV[gi] = gpr.PinSteps.FirstOrDefault(s => s.Converged)?.V;

                gridPoints.Add(gpr);
                StopLine(Log, gpr);
            }
        }
        finally { _collectWBondCurrents = false; _noiseHz = 0; }

        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        var ds = BuildLoadpullDataSet(gridPoints, p, ctx);
        AddWireTemperature(ds, gridPoints, p);
        return ds;
    }

    private static void GridLine(TextWriter w, int gi, int count, GamReader.GamPoint gp)
    {
        Complex gamma = gp.Gamma, z = gp.Z;
        w.WriteLine(
            $"[LP] Grid {gi+1}/{count}: " +
            $"Γ={gamma.Real:F4}{(gamma.Imaginary >= 0 ? "+" : "")}{gamma.Imaginary:F4}j  " +
            $"Z={z.Real:F2}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary:F2}j Ω");
    }

    private static void StopLine(TextWriter w, GridPointResult gpr)
        => w.WriteLine(
            $"[LP]   Stop={gpr.StopReason}  ({gpr.PinSteps.Count} Pin steps, " +
            $"{gpr.PinSteps.Count(s => s.Converged)} converged)");

    // ── LP-1: the grid's loads in parallel, with the serial loop's results ──────

    /// <summary>
    /// How many workers <see cref="Run"/> will solve <paramref name="gridCount"/> loads with. Public to the tests, because
    /// "did it take the serial path?" is otherwise only answerable by timing. 1 — the serial loop — without a
    /// <see cref="Library"/> to elaborate the copies from, at <see cref="AnalysisSettings.MaxParallelism"/> = 1, for fewer
    /// than two loads, and for a netlist that may not be elaborated twice (an external device is a slot in ONE worker
    /// process, whose channel serialises every call anyway; <see cref="SParameterEngine.CanElaborateInParallel"/>).
    /// Otherwise the cap — <paramref name="workers"/> (the directive's <c>Workers=</c>) when it is set, else
    /// <see cref="AnalysisSettings.MaxParallelism"/>, 0 meaning <see cref="Environment.ProcessorCount"/> — and never more
    /// workers than loads.
    /// </summary>
    public int PlanDegree(int gridCount, int workers = 0)
    {
        int requested = workers > 0 ? workers : _lpSettings.MaxParallelism;
        if (requested == 1 || _lib is null || gridCount < 2) return 1;
        if (!SParameterEngine.CanElaborateInParallel(_netlist)) return 1;
        return Math.Min(requested > 1 ? requested : Environment.ProcessorCount, gridCount);
    }

    /// <summary>
    /// The grid on <paramref name="degree"/> workers, each with its own elaborated copy of the netlist and its own engine —
    /// a model writes state while it stamps, so the copies are the whole thread-safety story, as in the S-parameter sweep
    /// (SP-P3). <b>Bit-identical to the serial loop at any degree</b>, because nothing a load computes depends on another
    /// load except its warm start, and that is reproduced exactly:
    ///
    /// <list type="bullet">
    /// <item>A load's seed is the first converged rung of the VSWR-nearest EARLIER load (<see cref="FindNearestSeed"/>,
    /// ties to the lower index). A ladder stops at its first failed rung, so that rung is always the load's FIRST solve:
    /// each load publishes it the moment it is solved (<see cref="RunOneTermination"/>'s <c>onFirstRung</c>), and load
    /// <c>i</c> waits only until loads <c>0 … i−1</c> have published. The first solves follow one another in grid order;
    /// everything after them — the drive-up, its continuations, the noise — runs side by side.</item>
    /// <item>Each worker's tuners carry the caller's harmonic overrides (a pursuit's follow-on Zsource), and every solve
    /// is a pure function of the tuner state, the drive and the seed — a reused factorisation is reused only when the
    /// re-stamped matrix is bit-identical (HB-P2), so which worker solved which load earlier does not matter.</item>
    /// <item>Diagnostics are taken per load (<see cref="ElaboratedNetlist.TakeRunDiagnostics"/>) and replayed into the
    /// caller's netlist in grid order; progress lines are buffered per load and printed in grid order as the completed
    /// prefix grows. A failure is the lowest failing load's, raised after the loads running beside it finish, with the
    /// findings of the loads before it — what the serial loop would have stopped with.</item>
    /// </list>
    /// </summary>
    private GridPointResult[] RunParallel(LoadpullAnalysisParams p, PursuitContext ctx, int degree, RunControl? control)
    {
        var grid      = p.Grid.Points;
        int n         = grid.Count;
        var results   = new GridPointResult?[n];
        var firstV    = new Complex[,]?[n];
        var published = new bool[n];
        var logs      = new string?[n];
        var diags     = new (IReadOnlyList<(string Key, string Message)> Warnings,
                             IReadOnlyList<(string Key, string Message)> Notes)?[n];
        var faults    = new Exception?[n];
        Exception? setupFault = null;
        int next = -1, prefix = 0, flushed = 0, abort = 0;
        object gate = new(), elaborating = new();

        void Publish(int gi, Complex[,]? v)
        {
            lock (gate)
            {
                if (published[gi]) return;
                published[gi] = true;
                firstV[gi]    = v;
                while (prefix < n && published[prefix]) prefix++;
                Monitor.PulseAll(gate);
            }
        }

        Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, _ =>
        {
            ElaboratedNetlist? copy = null;
            try
            {
                // One elaboration at a time: the Elaborator reads the TestBench, which nothing else may be changing then.
                lock (elaborating) copy = new Elaborator(_lib!) { BaseDirectory = _baseDir }.Elaborate(_tb);
                var eng  = new LoadpullEngine(copy, _tb, _settings) { _collectWBondCurrents = _collectWBondCurrents, _noiseHz = _noiseHz };
                var wctx = eng.PrepareContext(p);
                Mirror(ctx.SrcModel, wctx.SrcModel);
                Mirror(ctx.LoadModel, wctx.LoadModel);
                copy.TakeRunDiagnostics();   // its elaboration's and setup's: the caller's netlist holds its own

                while (Volatile.Read(ref abort) == 0)
                {
                    int gi = Interlocked.Increment(ref next);
                    if (gi >= n) break;
                    var log = new StringWriter();
                    eng._log = log;
                    try
                    {
                        control?.Tick();
                        Complex[,]? seed;
                        lock (gate)
                        {
                            while (prefix < gi) Monitor.Wait(gate);
                            seed = FindNearestSeed(gi, grid[gi].Gamma, firstV, grid);
                        }
                        GridLine(log, gi, n, grid[gi]);
                        var gpr = eng.RunOneTermination(p, wctx, grid[gi].Z, gi, seed, v => Publish(gi, v));
                        StopLine(log, gpr);
                        results[gi] = gpr;
                    }
                    catch (Exception ex)
                    {
                        faults[gi] = ex;
                        Volatile.Write(ref abort, 1);
                    }
                    finally
                    {
                        Publish(gi, null);   // a load that failed, or solved nothing, must not leave a later one waiting
                        diags[gi] = copy.TakeRunDiagnostics();
                        lock (gate)
                        {
                            logs[gi] = log.ToString();
                            while (flushed < n && logs[flushed] is { } text) { Console.Error.Write(text); flushed++; }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate) setupFault ??= ex;
                Volatile.Write(ref abort, 1);
            }
            finally { copy?.Dispose(); }
        });

        int failed = Array.FindIndex(faults, f => f is not null);
        for (int gi = 0; gi < n && (failed < 0 || gi <= failed); gi++)
        {
            if (diags[gi] is not { } d) continue;
            foreach (var (key, message) in d.Warnings) _netlist.AddWarningOnce(key, message);
            foreach (var (key, message) in d.Notes)    _netlist.AddNoteOnce(key, message);
        }
        if (failed >= 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(faults[failed]!).Throw();
        if (results.Any(r => r is null))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                setupFault ?? new InvalidOperationException("Loadpull: a parallel grid left a load unsolved.")).Throw();
        return results!;
    }

    private static void Mirror(TunerModel from, TunerModel to)
    {
        if (from.HarmonicOverride is { } o) to.SetHarmonicOverride(o.Harmonic, o.Z);
        else to.ClearHarmonicOverride();
    }

    /// <summary>
    /// brief-wbond-wire-temperature R-wbt-4a — every solved-temperature wBond's array currents at one converged step, read from
    /// the step's back-solver at each harmonic (as <c>HbPinCurrents</c> reads an SnP's rows). Null when none solves.
    /// </summary>
    private IReadOnlyDictionary<string, WBondArrayCurrents>? WBondCurrentsOf(HbEngine.SinglePointResult sr, PursuitContext ctx)
    {
        if (!_collectWBondCurrents) return null;
        var wbonds = WireTemperatureCubes.Instances(_netlist);
        if (!WireTemperatureCubes.AnySolved(wbonds) || sr.BackSolver is not { } back) return null;
        double[] freqs = [.. Enumerable.Range(0, ctx.K + 1).Select(k => k * ctx.HbParams.ToneHz)];
        return wbonds.Where(w => w.Model.ThermalSpec.Solved)
                     .ToDictionary(w => w.Path, w => WireTemperatureCubes.FromBranchRows(w.Model, freqs, k => back.GetSolution(k, 0)),
                                   StringComparer.Ordinal);
    }

    /// <summary>
    /// R-wbt-4d — <c>WireTemp</c> and its state as <c>[gridPoint, pinStep, wire array]</c>, the layout the FOM cubes have plus
    /// the array axis. Solved along each load point's Pin ladder in order, so each step warm-starts from the one below and a
    /// runaway warning names the step it happened at and the last that converged. A step the ladder never reached, or that did
    /// not converge, is state 3 (NaN) — a fixed-temperature instance still reports its Temp there.
    /// </summary>
    private void AddWireTemperature(DataSet ds, List<GridPointResult> gridPoints, LoadpullAnalysisParams p)
    {
        var wbonds = WireTemperatureCubes.Instances(_netlist);
        if (wbonds.Count == 0) return;
        var pinSeq = BuildPinSequence(p).ToList();
        var gridAxis = ds["Pout"].Axes[0];
        var pinAxis = ds["Pout"].Axes[1];
        string? outer = _netlist.WireThermal?.Drive;
        string[]? labels = null;
        double[] temps = [], states = [];
        for (int gi = 0; gi < gridPoints.Count; gi++)
        {
            var gpr = gridPoints[gi];
            var session = new CircuitRF.WBond.Thermal.WireThermalSession();
            for (int pi = 0; pi < pinSeq.Count; pi++)
            {
                var step = pi < gpr.PinSteps.Count ? gpr.PinSteps[pi] : null;
                session.Drive = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Pin = {pinSeq[pi].PavlDbm:0.##} dBm at load {gridAxis.Labels?[gi] ?? gi.ToString(System.Globalization.CultureInfo.InvariantCulture)} Ω")
                    + (outer is null ? "" : $" ({outer})");
                var point = WireTemperatureCubes.Compute(_netlist, wbonds,
                    (path, model) => step?.WBondCurrents?.GetValueOrDefault(path) ?? WBondArrayCurrents.Dc(new double[model.ArrayCount]),
                    step is { Converged: true, WBondCurrents: not null }, "at this drive", session);
                if (labels is null)
                {
                    labels = point.Labels;
                    temps = new double[gridPoints.Count * pinSeq.Count * labels.Length];
                    states = new double[temps.Length];
                }
                int at = (gi * pinSeq.Count + pi) * labels.Length;
                Array.Copy(point.TempC, 0, temps, at, labels.Length);
                Array.Copy(point.State, 0, states, at, labels.Length);
            }
        }
        if (labels is null) return;
        ds.Add(WireTemperatureCubes.Cube,
               new DataCube([gridAxis, pinAxis, WireTemperatureCubes.ArrayAxis(labels)], temps) { Unit = "°C" });
        ds.Add(WireTemperatureCubes.StateCube, new DataCube([gridAxis, pinAxis, WireTemperatureCubes.ArrayAxis(labels)], states));
    }

    // ── DataSet builder ──────────────────────────────────────────────────────

    private static DataSet BuildLoadpullDataSet(
        List<GridPointResult> gridPoints,
        LoadpullAnalysisParams p,
        PursuitContext ctx)
    {
        var pinSeq = BuildPinSequence(p).ToList();
        int nG  = gridPoints.Count;
        int nP  = pinSeq.Count;
        int nN  = ctx.NodeNames.Length;
        int nH  = ctx.K + 1;   // harmonics 0..K

        // Axes
        var gridAxis = new Axis("gridPoint",
            Enumerable.Range(0, nG).Select(i => (double)i).ToArray(),
            labels: gridPoints.Select(gp =>
                $"{gp.Z.Real:G6}{(gp.Z.Imaginary >= 0 ? "+" : "")}{gp.Z.Imaginary:G6}j").ToArray());

        var pinAxis = new Axis("pinStep",
            pinSeq.Select(ps => ps.PavlDbm).ToArray(),
            labels: pinSeq.Select(ps => $"{ps.PavlDbm:G4}").ToArray());

        var nodeAxis = new Axis("node",
            Enumerable.Range(0, nN).Select(i => (double)i).ToArray(),
            labels: ctx.NodeNames);

        var harmAxis = new Axis("harmonic",
            Enumerable.Range(0, nH).Select(i => (double)i).ToArray());

        // FOM buffers {gridPoint, pinStep} — row-major: gi*nP + pi
        int fomLen = nG * nP;
        var converged  = new double[fomLen];
        var isTickle   = new double[fomLen];
        var pavlDbmArr = new double[fomLen];
        var pout       = new double[fomLen];
        var gt         = new double[fomLen];
        var gp2        = new double[fomLen];
        var de         = new double[fomLen];
        var pae        = new double[fomLen];
        var pdc        = new double[fomLen];
        var biasVLoad  = new double[fomLen];
        var biasILoad  = new double[fomLen];
        var biasVSrc   = new double[fomLen];
        var biasISrc   = new double[fomLen];

        // Grid-point buffers {gridPoint}
        var zLoad     = new Complex[nG];
        var gammaLoad = new Complex[nG];
        var stopCode  = new double[nG];
        var zSource   = new Complex[nG];   // source impedance presented at the fundamental (IRL reference)

        // Spectra {gridPoint, pinStep, node, harmonic}
        int specLen = nG * nP * nN * nH;
        var vData   = new Complex[specLen];
        var inlData = new Complex[specLen];

        // Source-delivered input current {gridPoint, pinStep, harmonic} — the true current into the
        // DUT input node (see PinStepResult.ISrcIn). Zin/Γin are derived from V[src]/Iin downstream.
        int iinLen  = nG * nP * nH;
        var iinData = new Complex[iinLen];

        for (int gi = 0; gi < nG; gi++)
        {
            var gpr = gridPoints[gi];
            zLoad[gi]     = gpr.Z;
            gammaLoad[gi] = gpr.Gamma;
            zSource[gi]   = gpr.SourceZFund;
            stopCode[gi]  = gpr.StopReason switch
            {
                "PinMax"          => 0,
                "Compression"     => 1,
                "NonConvergence"  => 2,
                "NoConvergedSeed" => 3,
                _                 => 0,
            };

            for (int pi = 0; pi < nP; pi++)
            {
                int fomIdx = gi * nP + pi;
                pavlDbmArr[fomIdx] = pinSeq[pi].PavlDbm;   // always fill dBm axis value

                if (pi < gpr.PinSteps.Count)
                {
                    var step = gpr.PinSteps[pi];
                    converged[fomIdx]  = step.Converged ? 1.0 : 0.0;
                    isTickle[fomIdx]   = step.IsTickle  ? 1.0 : 0.0;
                    pout[fomIdx]       = step.PoutW;
                    gt[fomIdx]         = step.GtDb;
                    gp2[fomIdx]        = step.GpDb;
                    de[fomIdx]         = step.De;
                    pae[fomIdx]        = step.Pae;
                    pdc[fomIdx]        = step.PdcW;
                    biasVLoad[fomIdx]  = step.BiasVoltageLoadV;
                    biasILoad[fomIdx]  = step.BiasCurrentLoadA;
                    biasVSrc[fomIdx]   = step.BiasVoltageSrcV;
                    biasISrc[fomIdx]   = step.BiasCurrentSrcA;

                    if (step.Converged && step.V is not null)
                    {
                        int nVNodes = step.V.GetLength(0);
                        int nVHarm  = step.V.GetLength(1);
                        for (int ni = 0; ni < nN && ni < nVNodes; ni++)
                        for (int hi = 0; hi < nH && hi < nVHarm; hi++)
                        {
                            int sIdx = ((gi * nP + pi) * nN + ni) * nH + hi;
                            vData[sIdx]   = step.V[ni, hi];
                            inlData[sIdx] = step.INl[ni, hi];
                        }
                        if (step.ISrcIn is not null)
                            for (int hi = 0; hi < nH && hi < step.ISrcIn.Length; hi++)
                                iinData[(gi * nP + pi) * nH + hi] = step.ISrcIn[hi];
                    }
                }
                // else: pi >= PinSteps.Count — padded with 0 (default)
            }
        }

        var ds = new DataSet();
        ds.Add("Converged",  new DataCube(new[] { gridAxis, pinAxis }, converged));
        ds.Add("IsTickle",   new DataCube(new[] { gridAxis, pinAxis }, isTickle));
        ds.Add("PavlDbm",    new DataCube(new[] { gridAxis, pinAxis }, pavlDbmArr));
        ds.Add("Pout",       new DataCube(new[] { gridAxis, pinAxis }, pout));
        ds.Add("Gt",         new DataCube(new[] { gridAxis, pinAxis }, gt));
        ds.Add("Gp",         new DataCube(new[] { gridAxis, pinAxis }, gp2));
        ds.Add("DE",         new DataCube(new[] { gridAxis, pinAxis }, de));
        ds.Add("PAE",        new DataCube(new[] { gridAxis, pinAxis }, pae));
        ds.Add("Pdc",        new DataCube(new[] { gridAxis, pinAxis }, pdc));
        ds.Add("BiasVLoad",  new DataCube(new[] { gridAxis, pinAxis }, biasVLoad));
        ds.Add("BiasILoad",  new DataCube(new[] { gridAxis, pinAxis }, biasILoad));
        ds.Add("BiasVSrc",   new DataCube(new[] { gridAxis, pinAxis }, biasVSrc));
        ds.Add("BiasISrc",   new DataCube(new[] { gridAxis, pinAxis }, biasISrc));
        // Brief NZ-6 — NF beside the FOMs, only when asked for (a run without Noise=yes writes exactly what it always did).
        // NaN where a rung did not converge or the ladder never reached it: a padded 0 dB would read as a noiseless device.
        if (p.Noise is not null)
        {
            var nf = new double[fomLen];
            for (int gi = 0; gi < nG; gi++)
                for (int pi = 0; pi < nP; pi++)
                    nf[gi * nP + pi] = pi < gridPoints[gi].PinSteps.Count ? gridPoints[gi].PinSteps[pi].NfDb : double.NaN;
            ds.Add("NF", new DataCube(new[] { gridAxis, pinAxis }, nf) { Unit = "dB" });
        }
        ds.Add("ZLoad",      new DataCube(new[] { gridAxis }, zLoad));
        ds.Add("GammaLoad",  new DataCube(new[] { gridAxis }, gammaLoad));
        ds.Add("StopCode",   new DataCube(new[] { gridAxis }, stopCode));
        if (nN > 0 && nH > 0 && nG > 0 && nP > 0)
        {
            ds.Add("V",   new DataCube(new[] { gridAxis, pinAxis, nodeAxis, harmAxis }, vData));
            ds.Add("INl",   new DataCube(new[] { gridAxis, pinAxis, nodeAxis, harmAxis }, inlData));
            ds.Add("Iin",   new DataCube(new[] { gridAxis, pinAxis, harmAxis }, iinData));
        }

        // Node-identity provenance (loadpull-postprocessor.md §2): rank-0 metadata cubes naming the
        // DUT input (source-side) and output (load-side) node-axis indices, so a DataSet-only consumer
        // (the post-processor / export) can compute Zin/Γin/AM-PM without re-deriving topology.
        // __-prefixed → hidden from pickers, passed through StackSweepAxis unstacked. −1 = unknown.
        ds.Add("__SrcNodeIdx",  new DataCube(Array.Empty<Axis>(), new[] { (double)ctx.SrcIfIdx }));
        ds.Add("__LoadNodeIdx", new DataCube(Array.Empty<Axis>(), new[] { (double)ctx.LoadIfIdx }));

        // Source impedance presented at the fundamental, per grid point — the reference plane for input
        // return loss (NOT 50 Ω). `__`-prefixed so it is hidden from pickers and passed through sweep
        // stacking unstacked; distinct from the importers' rank-1 {freq} "ZSource" (no name collision).
        ds.Add("__SrcZ", new DataCube(new[] { gridAxis }, zSource));

        // A sourcepull: ZLoad/GammaLoad carry the swept termination, which is the SOURCE's; say so, with the load held
        // fixed, so an exported .spl/.lpcwave files the swept Γ on the source plane (LoadpullExportModel). A loadpull
        // writes neither, exactly as before.
        if (!p.SweepLoad)
        {
            ds.Add(RfCore.Loadpull.LoadpullExportModel.SweptSourceCube, new DataCube(Array.Empty<Axis>(), new[] { 1.0 }));
            ds.Add(RfCore.Loadpull.LoadpullExportModel.FixedLoadZCube,
                   new DataCube(Array.Empty<Axis>(), new[] { ctx.LoadModel.GetDeclaredZ(1) }));
        }

        // Single-frequency carrier so the summary surface reports the real tone frequency (not 0) for
        // these freq-axis-less FOM cubes — same convention the .spl/.lpcwave readers use (__Freq).
        ds.Add("__Freq", new DataCube(new[] { new Axis("freq", new[] { p.ToneHz }, "Hz") },
                                      new[] { p.ToneHz }));
        return ds;
    }

    /// <summary>
    /// Runs the inner adaptive Pin drive-up at a single termination Z.
    /// Used by both Run() (outer grid loop) and LoadpullPursuitEngine (one query per call).
    ///
    /// Gamma is derived from Z and the grid's Z0 (RfCore convention). Callers need not supply it.
    ///
    /// <paramref name="warmStart"/> — optional warm-start V from a nearby converged solve.
    ///
    /// <paramref name="onFirstRung"/> — called once, as soon as the ladder's first rung is recorded, with that rung's V if
    /// it converged and null if it did not: what a later load seeds from, which a parallel grid hands on without waiting
    /// for the rest of this load's drive-up. Not called when the Pin sequence is empty.
    /// </summary>
    public GridPointResult RunOneTermination(
        LoadpullAnalysisParams p,
        PursuitContext         ctx,
        Complex                z,
        int                    gridIndex,
        Complex[,]?            warmStart = null,
        Action<Complex[,]?>?   onFirstRung = null)
    {
        // B7: compute Γ from Z using the grid's actual Z0 (not hardcoded 50 Ω).
        double z0    = p.Grid.Z0 > 0 ? p.Grid.Z0 : 50.0;
        Complex gamma = RfHelpers.Z2G(z / z0);
        ctx.SweptModel.SetHarmonicOverride(p.TuneHarm, z);

        var pinSteps  = new List<PinStepResult>();
        void Record(PinStepResult s)
        {
            pinSteps.Add(s);
            if (pinSteps.Count == 1) onFirstRung?.Invoke(s.Converged ? s.V : null);
        }
        // Brief NZ-6 — each converged solve, by the step it made, so the noise is read after the ladder on the rungs it
        // kept (a continuation's discarded probes cost nothing). Null when no noise was asked for.
        var noisePoints = p.Noise is not null && _noiseHz > 0
            ? new Dictionary<PinStepResult, HbEngine.SinglePointResult>(ReferenceEqualityComparer.Instance) : null;
        string stopReason = "PinMax";
        double gMax   = double.NegativeInfinity;
        // Pout at the rung gMax was measured on — what a compression claim is measured AGAINST.
        double poutAtGMaxDbm = double.NaN;
        Complex[,]? innerSeed = warmStart;
        int continuations = 0, retries = 0;
        // The reason the LAST failed solve gave. A ladder that stops reports WHY it stopped: the
        // solver's own sentence says far more than the word "non-convergence" (a vacuous tolerance,
        // for one, is a setup fault and not a hard solve — see HbNewton.IsToleranceVacuous).
        string? lastFailReason = null;
        // Whether that failure was the vacuous-tolerance refusal: a setup fault, which the continuation from the tickle must
        // not walk around — smaller drive steps would "converge" against the same meaningless tolerance.
        bool lastFailVacuous = false;

        // One solve at an EXPLICIT warm start, packaged as the step this ladder records. Returns null
        // for non-convergence, which is what DriveLadder's continuation reads as "abandon this depth".
        // It does NOT touch innerSeed: a continuation probe is a candidate, not yet an accepted rung.
        PinStepResult? SolveAt(double pavlDbm, bool isTickle, Complex[,]? warm)
        {
            double pavlW = DbmToWatts(pavlDbm);
            ctx.SrcModel.SetSourceDrive(p.ToneHz, pavlW);

            var sr = _hbEngine.RunSinglePoint(ctx.HbParams, warm, ctx.SolveSettings);

            // True current the source delivers into the DUT input node (includes any passives the user
            // wired at the gate, not just INl[gate]). Drives Zin / Zsource / Pin_delivered.
            Complex[] iSrcIn = ComputeSourceInputCurrent(sr, ctx);
            Complex[] iLoadIn = ComputeLoadCurrent(sr, ctx);

            var foms  = ComputeFoms(sr.V, iSrcIn, iLoadIn, ctx.LoadIfIdx, ctx.SrcIfIdx, pavlW, ctx.K);

            double vLoad = ctx.LoadIfIdx >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.LoadIfIdx, 0].Real : 0;
            double iLoad = iLoadIn.Length > 0 ? iLoadIn[0].Real : 0;
            double vSrc  = ctx.SrcIfIdx  >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.SrcIfIdx,  0].Real : 0;
            double iSrc2 = BiasCurrentSrc(sr, ctx, iSrcIn);

            var step = new PinStepResult(
                pavlDbm, isTickle,
                sr.V, sr.INl, iSrcIn,
                foms.PavlW, foms.PinDeliveredW, foms.PoutW, foms.GtDb, foms.GpDb,
                vLoad, iLoad, vSrc, iSrc2,
                sr.Converged, sr.Iterations, sr.FailReason)
            {
                WBondCurrents = sr.Converged ? WBondCurrentsOf(sr, ctx) : null,
            };

            if (!sr.Converged && sr.FailReason is not null) lastFailReason = sr.FailReason;
            lastFailVacuous = sr.ToleranceVacuous;
            if (noisePoints is not null && sr.Converged) noisePoints[step] = sr;

            return sr.Converged ? step : null;
        }

        // The previous NON-TICKLE rung — what continuity is measured against. The tickle is deliberately
        // excluded: it sits tens of dB below PinStart, so it is not a ladder step and the jump from it
        // says nothing about a branch.
        PinStepResult? prevRung = null;
        // The converged tickle, which the FIRST rung continues from when it fails: the jump from the tickle to PinStart is
        // tens of dB, and on a high-gain stage behind a sharply matched source that is a leap one Newton solve does not
        // make — the Model Composer example's GaN at 2 GHz failed its first rung at every load from −10 dBm and walked
        // cleanly to compression from −30. Only a failed solve continues from it; continuity is never judged against it.
        PinStepResult? tickleRung = null;

        foreach (var (pavlDbm, isTickle) in BuildPinSequence(p))
        {
            var step = SolveAt(pavlDbm, isTickle, innerSeed);

            // Round 11 — ONE guard for the ladder's two ways of taking too big a drive step, because
            // they are the same defect wearing two faces: the Newton either fails outright, or succeeds
            // onto a DIFFERENT root (a rung whose Pout moved further than its own Pin step did — see
            // DriveLadder for the measurement). Both are answered by re-walking that one step as a
            // continuation from the previous rung instead of taking it in a single leap.
            var from = prevRung ?? (step is null && !lastFailVacuous ? tickleRung : null);
            if (!isTickle && from is not null &&
                (step is null || DriveLadder.IsDiscontinuous(
                     from.PavlDbm, DriveLadder.PoutDbm(from.PoutW),
                     pavlDbm,      DriveLadder.PoutDbm(step.PoutW), p.ContinuityMarginDb)))
            {
                var refined = DriveLadder.ContinueThroughJump(
                    from.PavlDbm, DriveLadder.PoutDbm(from.PoutW), from.V, pavlDbm,
                    (pin, warm) => SolveAt(pin, isTickle: false, warm),
                    st => DriveLadder.PoutDbm(st.PoutW),
                    st => st.V,
                    p.ContinuityMarginDb);

                if (refined is not null) { step = refined; continuations++; }
                else if (step is null)
                {
                    // Last resort: the cold seed. A step this far from its predecessor may simply not
                    // be reachable by continuation at all.
                    step = SolveAt(pavlDbm, isTickle, warm: null);
                    if (step is not null) retries++;
                }
            }

            if (step is null)
            {
                // Record the failed attempt so the caller can still see WHERE it stopped, exactly as
                // this loop always did — SolveAt returns null on non-convergence, so the recorded step
                // is re-made here rather than carried through the guard as a half-valid one.
                Record(NonConvergedStep(p, ctx, pavlDbm, isTickle, innerSeed));
                stopReason = "NonConvergence";
                Log.WriteLine(
                    $"[LP]   Pin={pavlDbm:F1}: non-convergence. Stopping." +
                    (lastFailReason is null ? "" : $"  {lastFailReason}"));
                // Once per run, not once per grid point: a setup fault repeats at every termination,
                // and 44 copies of the same paragraph buries it rather than reporting it.
                if (lastFailReason is not null)
                    _netlist.AddNoteOnce("loadpull.solve-failed", lastFailReason);
                break;
            }

            Record(step);
            innerSeed = step.V;
            if (!isTickle) prevRung = step;
            else tickleRung = step;

            double gain    = p.UseGt ? step.GtDb : step.GpDb;
            string gainTag = p.UseGt ? "Gt" : "Gp";

            if (!isTickle)
            {
                if (gain > gMax) { gMax = gain; poutAtGMaxDbm = WattsToDbm(step.PoutW); }
                double compression = gMax - gain;
                Log.WriteLine(
                    $"[LP]   Pin={pavlDbm:F1} dBm  Pout={WattsToDbm(step.PoutW):F2} dBm  " +
                    $"{gainTag}={gain:F2} dB  compr={compression:F2} dB");

                // Stop when we have driven at least 0.1 dB past the compression target.
                // Every step stays on the regular Pin grid (PinStart + n·PinStep).
                // The step just below this one and this step bracket P-xdB for ExtractCriterion.
                if (compression >= p.Compression + 0.1 &&
                    DriveLadder.IsCompressionCredible(poutAtGMaxDbm, WattsToDbm(step.PoutW)))
                {
                    stopReason = "Compression";
                    break;
                }

                if (pavlDbm >= p.PinMaxDbm)
                {
                    stopReason = "PinMax";
                    break;
                }
            }
        }

        // Brief NZ-6 — NF at every converged rung, on that rung's own large-signal waveform. This grid point's termination is
        // still the one in force, which is what the sideband partitions are stamped from; the drive is the right-hand side
        // only, so whichever rung the ladder solved last does not matter.
        if (noisePoints is not null)
            foreach (var st in pinSteps)
                if (noisePoints.TryGetValue(st, out var sr)
                    && _hbEngine.NoiseAt(sr, p.Noise!, _noiseHz) is { } nds)
                    st.NfDb = nds["NF"].RealValues[0];

        // Round 11 — the energy tripwire, and it is SKIPPED against an active termination. See
        // HasActiveTermination / WarnIfEnergyViolating for why that is physics and not a concession.
        bool active = HasActiveTermination(p, ctx, z);
        if (!active) WarnIfEnergyViolating(pinSteps, gamma);

        // The source impedance the DUT input is actually driven from at the fundamental (grid Z for
        // source-pull; declared Z[1] or a pursuit Zsource override otherwise). Input return loss is
        // referenced to this, captured after the grid point's harmonic override is in effect.
        Complex srcZFund = ctx.SrcModel.FundamentalZ(p.ToneHz);

        return new GridPointResult(gridIndex, gamma, z, pinSteps, stopReason, srcZFund,
                                   continuations, retries, active);
    }

    /// <summary>
    /// Whether ANY termination this grid point presents — either tuner, any harmonic 1…K, the swept one
    /// included — has <c>Re(Z) &lt; 0</c> and is therefore ACTIVE: a power SOURCE, not a load.
    ///
    /// <para><b>Negative-real terminations are a supported, deliberate research capability</b>, not an
    /// input error. They are how a PA study probes regenerative and negative-resistance behaviour, the
    /// engine stamps them (a negative conductance, warn-and-continue per src/Engine/CLAUDE.md), and a
    /// <c>.gam</c> grid may legitimately carry <c>|Γ| &gt; 1</c>. Nothing here refuses one.</para>
    ///
    /// <para><b>One pre-existing edge stays as it is and is worth knowing about:</b> an active SOURCE
    /// FUNDAMENTAL leaves available power undefined, and <c>TunerModel.SetSourceDrive</c> already
    /// answers that by stamping 0 V of drive (<c>reZ1 &gt; 0</c> guard) — the same case harmonicaRF
    /// refuses by name as <c>InverseFailure.ActiveSourceFundamental</c>. That is not this method's
    /// business; it only decides whether an energy BALANCE can be checked.</para>
    /// </summary>
    private static bool HasActiveTermination(LoadpullAnalysisParams p, PursuitContext ctx, Complex sweptZ)
    {
        if (sweptZ.Real < 0) return true;

        for (int k = 1; k <= p.MaxHarmonic; k++)
        {
            // The swept harmonic's declared value is overwritten per grid point, so it says nothing —
            // sweptZ above is the one that counts for it.
            if (ctx.LoadModel.GetDeclaredZ(k).Real < 0 && !(ReferenceEquals(ctx.SweptModel, ctx.LoadModel) && k == p.TuneHarm))
                return true;
            if (ctx.SrcModel.GetDeclaredZ(k).Real < 0 && !(ReferenceEquals(ctx.SweptModel, ctx.SrcModel) && k == p.TuneHarm))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The non-converged step this ladder records before it stops. Re-solved rather than threaded
    /// through the continuity guard: <c>SolveAt</c> deliberately returns null for a failed solve so the
    /// continuation can read null as "abandon this depth", and a half-valid step travelling through
    /// that path would be the sort of thing a later reader trusts by accident.
    /// </summary>
    private PinStepResult NonConvergedStep(
        LoadpullAnalysisParams p, PursuitContext ctx, double pavlDbm, bool isTickle, Complex[,]? warm)
    {
        double pavlW = DbmToWatts(pavlDbm);
        ctx.SrcModel.SetSourceDrive(p.ToneHz, pavlW);

        var sr = _hbEngine.RunSinglePoint(ctx.HbParams, warm, ctx.SolveSettings);
        Complex[] iSrcIn = ComputeSourceInputCurrent(sr, ctx);
        Complex[] iLoadIn = ComputeLoadCurrent(sr, ctx);
        var foms = ComputeFoms(sr.V, iSrcIn, iLoadIn, ctx.LoadIfIdx, ctx.SrcIfIdx, pavlW, ctx.K);

        double vLoad = ctx.LoadIfIdx >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.LoadIfIdx, 0].Real : 0;
        double iLoad = iLoadIn.Length > 0 ? iLoadIn[0].Real : 0;
        double vSrc  = ctx.SrcIfIdx  >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.SrcIfIdx,  0].Real : 0;
        double iSrc2 = BiasCurrentSrc(sr, ctx, iSrcIn);

        return new PinStepResult(
            pavlDbm, isTickle, sr.V, sr.INl, iSrcIn,
            foms.PavlW, foms.PinDeliveredW, foms.PoutW, foms.GtDb, foms.GpDb,
            vLoad, iLoad, vSrc, iSrc2,
            sr.Converged, sr.Iterations, sr.FailReason);
    }

    /// <summary>
    /// Round 11's independent tripwire, and <b>the invariant is PAE, not DE</b>. Routed through the
    /// engine diagnostics channel (<c>ElaboratedNetlist.AddWarningOnce</c>, src/Engine/CLAUDE.md) rather
    /// than stderr, so it reaches the Messages pane like every other engine warning. Once per run.
    ///
    /// <para><b>Why PAE.</b> At steady state, power out cannot exceed power in:
    /// <c>Pout ≤ Pdc + Pin_delivered + P_active</c>. With every termination passive, <c>P_active = 0</c>
    /// and that rearranges to exactly <c>PAE = (Pout − Pin_delivered)/Pdc ≤ 1</c>.
    /// <c>DE = Pout/Pdc ≤ 1</c> does <b>NOT</b> follow and is a real false positive: a low-gain stage
    /// driven hard can legitimately put out more than its DC input, with the difference supplied by the
    /// RF drive. This screen originally tested DE and would have accused such a stage of impossible
    /// physics.</para>
    ///
    /// <para><b>Skipped entirely when any termination is active</b> (<see cref="HasActiveTermination"/>).
    /// A negative-real termination is a power SOURCE, so <c>P_active &gt; 0</c> and PAE above 100% is
    /// then perfectly physical — that is much of the point of setting one. The engine does not compute
    /// <c>P_active</c>, so there is no bound left to test, and asserting one anyway would fire on exactly
    /// the research case the capability exists for. Silence here is a refusal to guess, not an oversight.</para>
    ///
    /// <para><b>Necessary, not sufficient</b>, and deliberately recorded as such: of the four
    /// nonphysical grid points that motivated this work, this caught two — the other two reported
    /// Pout 82.5 dBm at DE 51%, which is just as impossible and invisible to any efficiency test. It is
    /// here because it is nearly free and catches a real class, not because it is a complete screen.</para>
    /// </summary>
    private void WarnIfEnergyViolating(IReadOnlyList<PinStepResult> steps, Complex gamma)
    {
        foreach (var st in steps)
        {
            if (!st.Converged || st.IsTickle || st.PdcW <= 1e-9 || st.Pae <= 1.0) continue;

            _netlist.AddWarningOnce(
                "loadpull-energy-violation",
                $"Loadpull: a converged Pin step reports PAE {st.Pae:P1} — more RF output than the DC " +
                $"supply and the RF drive together can provide, which no passive termination can make up. " +
                $"The harmonic-balance solve has landed on a nonphysical root. First seen at " +
                $"Γ = {gamma.Real:F3}{(gamma.Imaginary >= 0 ? "+" : "")}{gamma.Imaginary:F3}j, " +
                $"Pin = {st.PavlDbm:F1} dBm. Try a smaller PinStep.");
            return;
        }
    }

    // ── Pin sequence ─────────────────────────────────────────────────────────

    private static IEnumerable<(double PavlDbm, bool IsTickle)> BuildPinSequence(
        LoadpullAnalysisParams p)
    {
        if (p.TickleDbm.HasValue)
            yield return (p.TickleDbm.Value, true);

        double pin = p.PinStartDbm;
        while (pin <= p.PinMaxDbm + 1e-9)
        {
            yield return (pin, false);
            pin += p.PinStepDb;
        }
    }

    // ── Γ-grid warm-start ────────────────────────────────────────────────────

    /// <summary>
    /// The seed of load <paramref name="currentIdx"/>: the first converged rung of the VSWR-nearest EARLIER load that has
    /// one, ties to the lower index. Never a later load, whatever has been solved — that is what keeps a parallel grid's
    /// seeds, and so its results, the serial loop's (LP-1).
    /// </summary>
    private static Complex[,]? FindNearestSeed(
        int currentIdx, Complex currentGamma,
        IReadOnlyList<Complex[,]?> firstV,
        IReadOnlyList<GamReader.GamPoint> grid)
    {
        double     bestVswr = double.MaxValue;
        Complex[,]? bestV   = null;

        for (int j = 0; j < currentIdx; j++)
        {
            if (firstV[j] is not { } v) continue;
            double vswr = GammaDeltaVswr(currentGamma, grid[j].Gamma);
            if (vswr < bestVswr)
            {
                bestVswr = vswr;
                bestV    = v;
            }
        }
        return bestV;
    }

    /// <summary>
    /// "Distance" between two Γ points measured as VSWR of the bilinear Möbius delta.
    /// VSWR → 1 as Γ_a → Γ_b; VSWR is minimal (nearest) neighbor warm-start criterion.
    /// (loadpull.md §3.3: "nearest = RFNetwork.VSWR between two Γ/Z points closest to 1".)
    /// </summary>
    private static double GammaDeltaVswr(Complex ga, Complex gb)
    {
        // Bilinear (Möbius) reflection between two terminations:
        //   Γ_delta = (Γ_a − Γ_b) / (1 − Γ_a · conj(Γ_b))
        var num   = ga - gb;
        var denom = Complex.One - ga * Complex.Conjugate(gb);
        double d  = denom.Magnitude < 1e-15 ? 1.0 : num.Magnitude / denom.Magnitude;
        d = Math.Min(d, 0.9999);
        return (1.0 + d) / (1.0 - d);
    }

    // ── Live FOMs (loadpull.md §4) ───────────────────────────────────────────

    /// <summary>
    /// The four fundamental-frequency figures of merit, in one place so there is exactly one
    /// definition of each. Made public for harmonicaRF, which drives its own Pin search and must not
    /// re-derive a single FOM (brief-harmonicarf-h0-h3 §0.3 item 5) — it is a pure function of its
    /// arguments, so no existing caller's result moves.
    /// </summary>
    public readonly record struct FomResult(
        double PavlW, double PinDeliveredW, double PoutW, double GtDb, double GpDb);

    /// <summary>
    /// Computes live FOMs from converged V and I_nl at fundamental (k=1).
    ///
    /// Sign convention (verified against Hero-2 golden data at Pavl=−20 dBm):
    ///   Pout          = −½·Re(V[loadIdx, 1] · conj(I_nl[loadIdx, 1]))  → positive for PA
    ///   Pin_delivered = +½·Re(V[srcIdx,  1] · conj(I_nl[srcIdx,  1]))  → positive for PA
    /// </summary>
    public static FomResult ComputeFoms(
        Complex[,] v, Complex[] iSrcIn, Complex[,] iNl,
        int loadIdx, int srcIdx,
        double pavlW, int K)
    {
        // By KCL at a load net carrying only the tuner and the device, the current into the load is −INl.
        var iLoadIn = new Complex[iNl.GetLength(1)];
        if (loadIdx >= 0)
            for (int k = 0; k < iLoadIn.Length; k++) iLoadIn[k] = -iNl[loadIdx, k];
        return ComputeFoms(v, iSrcIn, iLoadIn, loadIdx, srcIdx, pavlW, K);
    }

    /// <summary>
    /// The same FOMs from the current flowing INTO the load (<paramref name="iLoadIn"/>, per harmonic) —
    /// what the engine passes when it reads that current from the load tuner's own branches.
    /// </summary>
    public static FomResult ComputeFoms(
        Complex[,] v, Complex[] iSrcIn, Complex[] iLoadIn,
        int loadIdx, int srcIdx,
        double pavlW, int K)
    {
        if (K < 1 || v.GetLength(1) < 2)
            return new FomResult(pavlW, 0, 0, double.NegativeInfinity, double.NegativeInfinity);

        var vLoad = loadIdx >= 0 ? v[loadIdx,   1] : Complex.Zero;
        var iLoad = loadIdx >= 0 && iLoadIn.Length > 1 ? iLoadIn[1] : Complex.Zero;
        var vSrc  = srcIdx  >= 0 ? v[srcIdx,    1] : Complex.Zero;
        // Pin_delivered uses the true current INTO the DUT input node (source-delivered), not the
        // device's INl[gate] — they differ when passives are wired at the gate. ISrcIn[1] reduces to
        // INl[src,1] in the canonical case, so Hero references are unchanged.
        var iSrc  = iSrcIn.Length > 1 ? iSrcIn[1] : Complex.Zero;

        double pout         = 0.5 * (vLoad * Complex.Conjugate(iLoad)).Real;
        double pinDelivered =  0.5 * (vSrc  * Complex.Conjugate(iSrc)).Real;

        double gtDb = pavlW > 1e-30         ? RatioToDb(pout / pavlW)         : double.NegativeInfinity;
        double gpDb = pinDelivered > 1e-30  ? RatioToDb(pout / pinDelivered)  : double.NegativeInfinity;

        return new FomResult(pavlW, pinDelivered, pout, gtDb, gpDb);
    }

    /// <summary>
    /// The current the source tuner delivers INTO the DUT input node, per harmonic. By KCL at n_dut
    /// the source's net delivery equals (source Z_Port branch current) − (choke branch current); both
    /// are well-conditioned branch unknowns recovered from the HB linear back-solver. This is exactly
    /// the current flowing into everything on the gate node EXCEPT the source tuner — the FET plus any
    /// user-wired input passives. In the canonical loadpull case (only the source tuner + FET on the
    /// gate) it reduces to INl[src], so Zin/Zsource/Pin and the Hero references are unchanged.
    ///
    /// Falls back to the INl[src] column when the back-solver is unavailable (singular DC extraction)
    /// or the source tuner's branches weren't captured (e.g. S-param mode), preserving prior behavior.
    /// </summary>
    private static Complex[] ComputeSourceInputCurrent(HbEngine.SinglePointResult sr, PursuitContext ctx)
    {
        int K   = sr.V.GetLength(1) - 1;
        var iin = new Complex[K + 1];

        int zb = ctx.SrcModel.SourceZPortBranchIndex;   // source RF-drive Z_Port branch
        int cb = ctx.SrcModel.ChokeBranchIndex;         // bias-tee choke branch
        if (sr.BackSolver is not null && zb >= 0 && cb >= 0)
        {
            for (int k = 0; k <= K; k++)
            {
                var x = sr.BackSolver.GetSolution(k, 0);
                Complex iZ = zb < x.Length ? x[zb] : Complex.Zero;
                Complex iL = cb < x.Length ? x[cb] : Complex.Zero;
                iin[k] = iZ - iL;
            }
            return iin;
        }

        // Fallback: canonical KCL assumption I_into_DUT = INl[src].
        if (ctx.SrcIfIdx >= 0)
            for (int k = 0; k <= K && k < sr.INl.GetLength(1); k++)
                iin[k] = sr.INl[ctx.SrcIfIdx, k];
        return iin;
    }

    /// <summary>
    /// The current flowing from the load tuner's DUT net INTO the load, per harmonic. Canonically (a device
    /// terminal on that net) it is −INl[load] by KCL — the shipped definition, unchanged. With no nonlinear
    /// device on the net (<see cref="PursuitContext.LoadOffDevice"/>) INl there is zero, and the current is
    /// the load tuner's own: its Z_Port branch (through the DC block) plus its choke, both flowing away from
    /// n_dut, recovered from the HB linear back-solver. Pout read here includes whatever linear network sits
    /// between the device and the tuner — for a composed FET, its own extrinsic network, which is part of
    /// the model.
    /// </summary>
    private static Complex[] ComputeLoadCurrent(HbEngine.SinglePointResult sr, PursuitContext ctx)
    {
        int K    = sr.V.GetLength(1) - 1;
        var iout = new Complex[K + 1];
        int zb = ctx.LoadModel.LoadZPortBranchIndex, cb = ctx.LoadModel.ChokeBranchIndex;
        if (ctx.LoadOffDevice)
        {
            if (sr.BackSolver is null || zb < 0 || cb < 0) return iout;
            for (int k = 0; k <= K; k++)
            {
                var x = sr.BackSolver.GetSolution(k, 0);
                iout[k] = (zb < x.Length ? x[zb] : Complex.Zero) + (cb < x.Length ? x[cb] : Complex.Zero);
            }
            return iout;
        }
        if (ctx.LoadIfIdx >= 0)
            for (int k = 0; k <= K && k < sr.INl.GetLength(1); k++)
                iout[k] = -sr.INl[ctx.LoadIfIdx, k];
        return iout;
    }

    /// <summary>
    /// <see cref="PinStepResult.BiasCurrentSrcA"/>: −INl[src, 0] canonically; with no device on the source
    /// net, minus the DC current the source tuner delivers into it (its choke current, by
    /// <see cref="ComputeSourceInputCurrent"/>'s KCL).
    /// </summary>
    private static double BiasCurrentSrc(HbEngine.SinglePointResult sr, PursuitContext ctx, Complex[] iSrcIn)
    {
        if (ctx.SrcOffDevice) return iSrcIn.Length > 0 ? -iSrcIn[0].Real : 0;
        return ctx.SrcIfIdx >= 0 && sr.INl.GetLength(1) > 0 ? -sr.INl[ctx.SrcIfIdx, 0].Real : 0;
    }

    // ── Tuner lookup ─────────────────────────────────────────────────────────

    private (ElaboratedComponent Ec, TunerModel Model) FindTuner(string instanceName, string role)
    {
        foreach (var ec in _netlist.Components)
        {
            if (!ec.ComponentType.Equals("Tuner", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ec.InstancePath.Equals(instanceName, StringComparison.OrdinalIgnoreCase)) continue;
            if (ec.Model is TunerModel tm) return (ec, tm);
            throw new InvalidOperationException(
                $"{role}={instanceName}: expected TunerModel, got {ec.Model.GetType().Name}.");
        }

        var avail = string.Join(", ", _netlist.Components
            .Where(e => e.ComponentType.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.InstancePath));
        throw new InvalidOperationException(
            $"{role}={instanceName}: no Tuner named '{instanceName}'. Available: [{avail}]");
    }

    // ── Unit helpers ─────────────────────────────────────────────────────────

    /// <summary>10·log10 of a dimensionless power ratio (gain, efficiency). Never call with watts.</summary>
    private static double RatioToDb(double ratio)
        => ratio > 1e-30 ? 10.0 * Math.Log10(ratio) : -300.0;

    internal static double WattsToDbm(double w)
        => w > 1e-30 ? 10.0 * Math.Log10(w) + 30.0 : -300.0;

    private static double DbmToWatts(double dbm)
        => Math.Pow(10.0, (dbm - 30.0) / 10.0);
}
