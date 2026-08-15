using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
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
    double             ContinuityMarginDb = DriveLadder.DefaultContinuityMarginDb);

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

    public LoadpullEngine(ElaboratedNetlist netlist, TestBench tb, AnalysisSettings? settings = null)
    {
        _netlist = netlist;
        _tb      = tb;

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
        try   { tone = FreqUnit.ResolveHz(lpa.ToneExpr, lpa.ToneUnit, globals, globalsWithUnit); }
        catch { tone = 1e9; }
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

        return new LoadpullAnalysisParams(
            tone, maxH, osamp, tol, driveStepping, guard, maxIter,
            lpa.LoadTunerName, lpa.SourceTunerName,
            sweepLoad, tuneHarm, grid,
            compress, useGt, pinStart, pinStep, pinMax, tickle, contMargin);
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

        public PursuitContext(TunerModel load, TunerModel src, TunerModel swept,
            int loadIfIdx, int srcIfIdx, int k, int[] ifNodes, string[] nodeNames,
            HbAnalysisParams hbParams, AnalysisSettings solveSettings)
        {
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

        var tempExtractor = new HbLinearExtractor(_netlist, _lpSettings);
        int[] ifNodes     = tempExtractor.InterfaceNodes;
        var nodeNames     = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();
        int loadIfIdx     = Array.IndexOf(ifNodes, loadDutNode);
        int srcIfIdx      = Array.IndexOf(ifNodes, srcDutNode);

        if (loadIfIdx < 0)
            throw new InvalidOperationException(
                $"LoadTuner '{p.LoadTunerName}' DUT node (node {loadDutNode}, net " +
                $"'{_netlist.Nodes.NameOf(loadDutNode)}') is not a nonlinear interface node.");
        if (srcIfIdx < 0)
            throw new InvalidOperationException(
                $"SourceTuner '{p.SourceTunerName}' DUT node (node {srcDutNode}, net " +
                $"'{_netlist.Nodes.NameOf(srcDutNode)}') is not a nonlinear interface node.");

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
            loadIfIdx, srcIfIdx, K, ifNodes, nodeNames, hbParams, solveSettings);
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
        var convergedV    = new Dictionary<int, Complex[,]>();
        var gridPointList = p.Grid.Points;

        for (int gi = 0; gi < gridPointList.Count; gi++)
        {
            control?.Tick();

            var gp    = gridPointList[gi];
            var gamma = gp.Gamma;
            var z     = gp.Z;

            Console.Error.WriteLine(
                $"[LP] Grid {gi+1}/{gridPointList.Count}: " +
                $"Γ={gamma.Real:F4}{(gamma.Imaginary >= 0 ? "+" : "")}{gamma.Imaginary:F4}j  " +
                $"Z={z.Real:F2}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary:F2}j Ω");

            Complex[,]? gridSeed = FindNearestSeed(gi, gamma, convergedV, gridPointList);
            var gpr = RunOneTermination(p, ctx, z, gi, gridSeed);

            var lastConv = gpr.PinSteps.LastOrDefault(s => s.Converged);
            if (lastConv is not null)
                convergedV[gi] = lastConv.V;

            gridPoints.Add(gpr);
            Console.Error.WriteLine(
                $"[LP]   Stop={gpr.StopReason}  ({gpr.PinSteps.Count} Pin steps, " +
                $"{gpr.PinSteps.Count(s => s.Converged)} converged)");
        }

        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        return BuildLoadpullDataSet(gridPoints, p, ctx);
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
    /// </summary>
    public GridPointResult RunOneTermination(
        LoadpullAnalysisParams p,
        PursuitContext         ctx,
        Complex                z,
        int                    gridIndex,
        Complex[,]?            warmStart = null)
    {
        // B7: compute Γ from Z using the grid's actual Z0 (not hardcoded 50 Ω).
        double z0    = p.Grid.Z0 > 0 ? p.Grid.Z0 : 50.0;
        Complex gamma = RfHelpers.Z2G(z / z0);
        ctx.SweptModel.SetHarmonicOverride(p.TuneHarm, z);

        var pinSteps  = new List<PinStepResult>();
        string stopReason = "PinMax";
        double gMax   = double.NegativeInfinity;
        Complex[,]? innerSeed = warmStart;
        int continuations = 0, retries = 0;

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

            var foms  = ComputeFoms(sr.V, iSrcIn, sr.INl, ctx.LoadIfIdx, ctx.SrcIfIdx, pavlW, ctx.K);

            double vLoad = ctx.LoadIfIdx >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.LoadIfIdx, 0].Real : 0;
            double iLoad = ctx.LoadIfIdx >= 0 && sr.INl.GetLength(1) > 0 ? -sr.INl[ctx.LoadIfIdx, 0].Real : 0;
            double vSrc  = ctx.SrcIfIdx  >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.SrcIfIdx,  0].Real : 0;
            double iSrc2 = ctx.SrcIfIdx  >= 0 && sr.INl.GetLength(1) > 0 ? -sr.INl[ctx.SrcIfIdx, 0].Real : 0;

            var step = new PinStepResult(
                pavlDbm, isTickle,
                sr.V, sr.INl, iSrcIn,
                foms.PavlW, foms.PinDeliveredW, foms.PoutW, foms.GtDb, foms.GpDb,
                vLoad, iLoad, vSrc, iSrc2,
                sr.Converged, sr.Iterations, sr.FailReason);

            return sr.Converged ? step : null;
        }

        // The previous NON-TICKLE rung — what continuity is measured against. The tickle is deliberately
        // excluded: it sits tens of dB below PinStart, so it is not a ladder step and the jump from it
        // says nothing about a branch.
        PinStepResult? prevRung = null;

        foreach (var (pavlDbm, isTickle) in BuildPinSequence(p))
        {
            var step = SolveAt(pavlDbm, isTickle, innerSeed);

            // Round 11 — ONE guard for the ladder's two ways of taking too big a drive step, because
            // they are the same defect wearing two faces: the Newton either fails outright, or succeeds
            // onto a DIFFERENT root (a rung whose Pout moved further than its own Pin step did — see
            // DriveLadder for the measurement). Both are answered by re-walking that one step as a
            // continuation from the previous rung instead of taking it in a single leap.
            if (!isTickle && prevRung is not null &&
                (step is null || DriveLadder.IsDiscontinuous(
                     prevRung.PavlDbm, DriveLadder.PoutDbm(prevRung.PoutW),
                     pavlDbm,          DriveLadder.PoutDbm(step.PoutW), p.ContinuityMarginDb)))
            {
                var refined = DriveLadder.ContinueThroughJump(
                    prevRung.PavlDbm, DriveLadder.PoutDbm(prevRung.PoutW), prevRung.V, pavlDbm,
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
                pinSteps.Add(NonConvergedStep(p, ctx, pavlDbm, isTickle, innerSeed));
                stopReason = "NonConvergence";
                Console.Error.WriteLine(
                    $"[LP]   Pin={pavlDbm:F1}: non-convergence. Stopping.");
                break;
            }

            pinSteps.Add(step);
            innerSeed = step.V;
            if (!isTickle) prevRung = step;

            double gain    = p.UseGt ? step.GtDb : step.GpDb;
            string gainTag = p.UseGt ? "Gt" : "Gp";

            if (!isTickle)
            {
                if (gain > gMax) gMax = gain;
                double compression = gMax - gain;
                Console.Error.WriteLine(
                    $"[LP]   Pin={pavlDbm:F1} dBm  Pout={WattsToDbm(step.PoutW):F2} dBm  " +
                    $"{gainTag}={gain:F2} dB  compr={compression:F2} dB");

                // Stop when we have driven at least 0.1 dB past the compression target.
                // Every step stays on the regular Pin grid (PinStart + n·PinStep).
                // The step just below this one and this step bracket P-xdB for ExtractCriterion.
                if (compression >= p.Compression + 0.1)
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
        var foms = ComputeFoms(sr.V, iSrcIn, sr.INl, ctx.LoadIfIdx, ctx.SrcIfIdx, pavlW, ctx.K);

        double vLoad = ctx.LoadIfIdx >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.LoadIfIdx, 0].Real : 0;
        double iLoad = ctx.LoadIfIdx >= 0 && sr.INl.GetLength(1) > 0 ? -sr.INl[ctx.LoadIfIdx, 0].Real : 0;
        double vSrc  = ctx.SrcIfIdx  >= 0 && sr.V.GetLength(1) > 0 ? sr.V[ctx.SrcIfIdx,  0].Real : 0;
        double iSrc2 = ctx.SrcIfIdx  >= 0 && sr.INl.GetLength(1) > 0 ? -sr.INl[ctx.SrcIfIdx, 0].Real : 0;

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

    private static Complex[,]? FindNearestSeed(
        int currentIdx, Complex currentGamma,
        Dictionary<int, Complex[,]> convergedV,
        IReadOnlyList<GamReader.GamPoint> grid)
    {
        if (convergedV.Count == 0) return null;

        double     bestVswr = double.MaxValue;
        Complex[,]? bestV   = null;

        foreach (var kv in convergedV)
        {
            double vswr = GammaDeltaVswr(currentGamma, grid[kv.Key].Gamma);
            if (vswr < bestVswr)
            {
                bestVswr = vswr;
                bestV    = kv.Value;
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
        if (K < 1 || v.GetLength(1) < 2)
            return new FomResult(pavlW, 0, 0, double.NegativeInfinity, double.NegativeInfinity);

        var vLoad = loadIdx >= 0 ? v[loadIdx,   1] : Complex.Zero;
        var iLoad = loadIdx >= 0 ? iNl[loadIdx, 1] : Complex.Zero;
        var vSrc  = srcIdx  >= 0 ? v[srcIdx,    1] : Complex.Zero;
        // Pin_delivered uses the true current INTO the DUT input node (source-delivered), not the
        // device's INl[gate] — they differ when passives are wired at the gate. ISrcIn[1] reduces to
        // INl[src,1] in the canonical case, so Hero references are unchanged.
        var iSrc  = iSrcIn.Length > 1 ? iSrcIn[1] : Complex.Zero;

        double pout         = -0.5 * (vLoad * Complex.Conjugate(iLoad)).Real;
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
