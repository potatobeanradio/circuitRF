using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Resolved HB analysis parameters (expression strings evaluated against ResolvedGlobals).
///
/// Two modes (harmonic-balance.md §3.2):
///   Single-tone:  ToneFreqsHz.Length == 1.  MaxMixOrder is ignored.
///   Multi-tone:   ToneFreqsHz.Length >= 2.  MaxHarmonic provides per-axis reach; MaxMixOrder
///                 bounds the diamond |k₁|+|k₂| ≤ MaxMixOrder.
/// </summary>
public sealed record HbAnalysisParams(
    /// <summary>
    /// Tone frequencies in Hz.  Length 1 = single-tone; length 2+ = multi-tone.
    /// Use ToneHz as a convenience getter for the single-tone case.
    /// </summary>
    double[] ToneFreqsHz,
    int      MaxHarmonic,
    /// <summary>Diamond mixing-order bound for two-tone. Ignored when single-tone.</summary>
    int      MaxMixOrder,
    int      FFTOverSample,
    double   Tol,
    DcBiasSteppingMode DriveStepping,
    /// <summary>Guard harmonic index H.  0 = off.  See B3 in CLAUDE.md.</summary>
    int      GuardHarmonic,
    string?  SweepVarName,
    double   SweepStart,
    double   SweepStop,
    double   SweepStep,
    int      MaxIter = 100,
    /// <summary>
    /// Newton step damping factor λ ∈ (0,1].
    /// Default 1.0 = full Newton step (no damping).
    /// Owner can supply &lt;1 via the cnl Lambda= key or by constructing with Lambda: x.
    /// B2.
    /// </summary>
    double   Lambda  = 1.0)
{
    /// <summary>Convenience: fundamental frequency for single-tone runs.</summary>
    public double ToneHz      => ToneFreqsHz[0];
    /// <summary>True when two or more independent tones are declared.</summary>
    public bool   IsMultiTone => ToneFreqsHz.Length > 1;
    public bool   HasSweep    => SweepVarName is not null;

    /// <summary>
    /// Enumerate the sweep values.
    /// Returns the actual sweep points when HasSweep=true; empty when HasSweep=false.
    /// The engine's Run() always executes at least one Newton solve — no-sweep is handled
    /// by a separate single-iteration path, not by yielding a dummy value.
    /// </summary>
    public IEnumerable<double> SweepValues()
    {
        if (!HasSweep) yield break;
        for (double v = SweepStart; v <= SweepStop + 1e-10; v += SweepStep)
            yield return v;
    }
}

/// <summary>
/// HB engine result: the solved V and I_nl [node, harmonic] per sweep point.
/// </summary>
public sealed class HbResult
{
    /// <summary>Swept variable values (one entry per sweep point).</summary>
    public double[] SweepValues { get; }
    /// <summary>Converged interface voltages: [sweepIdx][nodeIdx, harmonicK].</summary>
    public Complex[][,] V { get; }
    /// <summary>
    /// Nonlinear device currents at the interface: [sweepIdx][nodeIdx, harmonicK].
    /// I_nl[n,0] = DC device current — changes with Pin (self-biasing; Task-1 fix).
    /// I_nl[n,k] = k-th harmonic of the device current at interface node n.
    /// I_nl current-direction convention —  current flowing FROM interface node n INTO the nonlinear device.
    /// Positive = current entering the nonlinear device (passive sign convention on the device ports).
    /// </summary>
    public Complex[][,] INl { get; }
    /// <summary>Interface node indices (circuit node numbers, 1-based).</summary>
    public int[] InterfaceNodes { get; }
    /// <summary>Node names at the interface (e.g. "n_gate", "n_drain").</summary>
    public string[] InterfaceNodeNames { get; }
    /// <summary>Per-sweep-point convergence trace.</summary>
    public HbConvergenceTrace Trace { get; }

    /// <summary>
    /// Two-tone only: the mixing lattice. When non-null, the second index of <see cref="V"/> /
    /// <see cref="INl"/> is the <c>mixIndex</c> (0..M-1) per <see cref="MixingGrid"/>, NOT the
    /// scalar harmonic k. Null for single-tone runs (index is harmonic k=0..K).
    /// </summary>
    public MixingGrid? Grid { get; }

    /// <summary>Tone frequencies (Hz). Length 1 single-tone; length 2+ multi-tone.</summary>
    public double[] ToneFreqsHz { get; }

    /// <summary>True when the result is on the 2-D mixing-lattice axis.</summary>
    public bool IsMultiTone => Grid is not null;

    internal HbResult(double[] sweepValues, Complex[][,] V, Complex[][,] iNl,
        int[] interfaceNodes, string[] names, HbConvergenceTrace trace,
        MixingGrid? grid = null, double[]? toneFreqsHz = null)
    {
        SweepValues       = sweepValues;
        this.V            = V;
        INl               = iNl;
        InterfaceNodes    = interfaceNodes;
        InterfaceNodeNames = names;
        Trace             = trace;
        Grid              = grid;
        ToneFreqsHz       = toneFreqsHz ?? [];
    }
}

/// <summary>
/// Single-tone harmonic-balance engine — Phase 4a (harmonic-balance.md §13).
///
/// Usage:
///   var engine = new HbEngine(netlist, testBench, settings);
///   var result = engine.Run(analysisParams);
///
/// See CLAUDE.md for FFT convention, sign convention, and DC-exclusion rationale.
/// </summary>
public sealed class HbEngine
{
    private readonly ElaboratedNetlist  _netlist;
    private readonly TestBench          _tb;
    private readonly AnalysisSettings   _settings;

    public HbEngine(ElaboratedNetlist netlist, TestBench tb, AnalysisSettings? settings = null)
    {
        _netlist  = netlist;
        _tb       = tb;
        _settings = settings ?? AnalysisSettings.Default;
    }

    // ── Directive resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Resolve a HarmonicBalanceAnalysis directive against the elaborated globals.
    /// </summary>
    public static HbAnalysisParams Resolve(
        HarmonicBalanceAnalysis hba,
        IReadOnlyDictionary<string, Value> globals)
    {
        double Num(string expr, double def)
        {
            try
            {
                var scope = BuildScope(globals);
                var ev    = BuildEvaluator(globals);
                var ast   = Parser.Parse(expr);
                var val   = ev.EvalExpr(ast, scope);
                return val.Kind == ValueKind.Real ? val.AsReal() : val.AsComplex().Real;
            }
            catch { return def; }
        }

        int    maxH    = (int)Num(hba.MaxHarmonicExpr, 7);
        int    osamp   = Math.Max(1, (int)Num(hba.FFTOverSampleExpr, 1));
        double tol     = Num(hba.TolExpr, 1e-6);
        int    guard   = (int)Num(hba.GuardHarmonicExpr, 0);
        double lambda  = Math.Clamp(Num(hba.LambdaExpr, 1.0), 1e-4, 1.0);  // B2
        int    maxIter = Math.Max(1, (int)Num(hba.MaxIterExpr, 100));

        // ── Tone frequencies: single-tone (scalar Tone=) or multi-tone (Tone[1..N]) ──
        int numFreqs = Math.Max(1, (int)Num(hba.NumFreqsExpr, 1));
        double[] toneFreqsHz;
        if (numFreqs > 1 && hba.ToneExprs.Length >= numFreqs)
        {
            toneFreqsHz = new double[numFreqs];
            for (int i = 0; i < numFreqs; i++)
                toneFreqsHz[i] = Num(hba.ToneExprs[i], 1e9);
        }
        else
        {
            // Single-tone: use scalar Tone=.
            toneFreqsHz = [Num(hba.ToneExpr, 1e9)];
        }

        int maxMixOrder = Math.Max(1, (int)Num(hba.MaxMixOrderExpr, 5));

        var driveStepping = DcBiasSteppingMode.IfNecessary;
        var dsStr = hba.DriveSteppingExpr.Trim();
        if (dsStr.Equals("Always", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Always;
        else if (dsStr.Equals("Never", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Never;

        // Read deprecated sweep fields for back-compat (HbAnalysisParams carries them so
        // callers can inspect p.HasSweep; the engine ignores them in Run()).
#pragma warning disable CS0618
        double sweepStart = 0, sweepStop = 0, sweepStep = 1;
        string? sweepVar = hba.SweepVarName;
        if (sweepVar is not null)
        {
            sweepStart = Num(hba.SweepStartExpr ?? "0", 0);
            sweepStop  = Num(hba.SweepStopExpr  ?? "0", 0);
            sweepStep  = Num(hba.SweepStepExpr  ?? "1", 1);
        }
#pragma warning restore CS0618

        return new HbAnalysisParams(toneFreqsHz, maxH, maxMixOrder, osamp, tol, driveStepping, guard,
            sweepVar, sweepStart, sweepStop, sweepStep,
            MaxIter: maxIter, Lambda: lambda);
    }

    private static Scope BuildScope(IReadOnlyDictionary<string, Value> globals)
    {
        var scope = new Scope("hb-resolve");
        foreach (var kv in globals)
            scope.Bind(kv.Key, kv.Value.ToString()!);
        return scope;
    }

    private static Evaluator BuildEvaluator(IReadOnlyDictionary<string, Value> globals)
    {
        var ev = new Evaluator();
        foreach (var kv in globals)
            ev.InjectResolved("hb-resolve", kv.Key, kv.Value);
        return ev;
    }

    // ── Engine entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Run the HB analysis.  Returns an <see cref="HbRunResult"/> that wraps the DataSet
    /// and, for single-tone runs, a lazy <see cref="HbLinearBackSolver"/> for recovering
    /// linear-interior node voltages (C1).  Implicit conversion to DataSet is available.
    /// </summary>
    public HbRunResult Run(HbAnalysisParams p)
    {
        if (p.IsMultiTone) return new HbRunResult(RunTwoTone(p));

        int    K      = p.MaxHarmonic;
        double f0     = p.ToneHz;
        int    gridN  = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * f0;

        // ── Setup: linear extractor (extracts Y_{N×N} and I_src at each harmonic) ──
        var extractor  = new HbLinearExtractor(_netlist, _settings);
        int N          = extractor.InterfaceCount;
        int[] ifNodes  = extractor.InterfaceNodes;

        // Node names for result labelling.
        var nodeNames = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();

        // ── Commensurability check (harmonic-balance.md §3.1) ────────────────
        CheckCommensurability(f0, K);

        // ── Configure P1Tone sources with tone context (must precede extraction) ──
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is not P1ToneModel p1) continue;
            double driveHz = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == ValueKind.Real
                ? fv.AsReal() : f0;
            p1.SetToneContext(fc: f0, driveFreqHz: driveHz);
        }

        // ── Extract Y_{N×N}(0) and I_src(0) for DC (k=0): real DC admittance + Norton source.
        // ExtractDC throws SingularMatrixException if any interface node is voltage-pinned
        // (ideal inductor + ideal voltage source = zero-impedance DC path).  Fix: add R to chokes.
        var yNN  = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();

        // ── Extract Y_{N×N} and I_src for k=1..K ─────────────────────────────
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;  // exact-harmonic guarantee: k*(2π*f0)
            var (y, s)    = extractor.Extract(omegaK);
            yNN[k]        = y;
            iSrc[k]       = s;
        }

        // ── DC operating point (Phase-3 NonlinearDcEngine) ─────────────────
        var dcResult = NonlinearDcEngine.Run(_netlist, _settings);
        if (!dcResult.Converged)
            _netlist.AddWarningOnce("hb-dc-nonconverge",
                "HB: DC operating point did not converge; proceeding with best available.");

        var trace = new HbConvergenceTrace();

        // C2: Per-branch current accumulator: "instancePath:terminalName" → spectra.
        var portCurrentsByBranch = new Dictionary<string, List<Complex[]>>(StringComparer.Ordinal);

        // C1: Source RHS snapshot for the linear back-solver.
        var bSrcThisPoint = new Complex[K + 1][];
        for (int k = 0; k <= K; k++)
        {
            double omegaSnap = k == 0 ? 0.0 : k * omega0;
            bSrcThisPoint[k] = extractor.BuildSourceRhs(omegaSnap);
        }

        var V = InitialGuess(null, dcResult, N, K, ifNodes);

        // Re-extract source excitation (drive amplitude baked into I_src).
        // Y_{N×N} is topology-based (constant); only I_src changes with drive level.
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;
            var (_, s) = extractor.Extract(omegaK);
            iSrc[k] = s;
        }

        // ── Newton solve ──────────────────────────────────────────────────────
        var effectiveSettings = p.MaxIter != _settings.HbMaxIter
            ? new AnalysisSettings
              {
                  HbMaxIter              = p.MaxIter,
                  Gmin                   = _settings.Gmin,
                  ConductanceRegularization = _settings.ConductanceRegularization,
                  InductanceRegularization  = _settings.InductanceRegularization,
                  InductanceRegR            = _settings.InductanceRegR,
                  Gmax                      = _settings.Gmax,
                  DcBiasStepping            = _settings.DcBiasStepping,
                  DriveStepping             = _settings.DriveStepping,
                  NonlinearAbsTol           = _settings.NonlinearAbsTol,
              }
            : _settings;
        var solveResult = HbNewton.Solve(V, yNN, iSrc, f0, K, N,
            _netlist, ifNodes, gridN, effectiveSettings, p.Tol,
            p.Lambda, p.GuardHarmonic);

        trace.AddStep(new HbConvergenceTrace.StepRecord(
            0.0, solveResult.Iterations, solveResult.Converged,
            solveResult.IterTrace));

        if (!solveResult.Converged)
        {
            double res = solveResult.IterTrace.LastOrDefault()?.ResidualNorm ?? 0.0;
            _netlist.AddWarning(
                $"HB did not converge (‖F‖={res:E3} after {solveResult.Iterations} iterations); " +
                $"stored best-available result.");
            Console.Error.WriteLine(
                $"[HB] Non-convergence: ‖F‖={res:E3} after {solveResult.Iterations} iterations.");
        }

        Console.Error.Write("[HB-DC]:");
        for (int n = 0; n < N; n++)
        {
            string nm = n < nodeNames.Length ? nodeNames[n] : $"if[{n}]";
            Console.Error.Write(
                $"  {nm} V={V[n,0].Real:F3}V I_nl={solveResult.INl[n,0].Real*1e3:F2}mA");
        }
        Console.Error.WriteLine();

        // C2: Post-convergence per-device port-current extraction (not in Newton hot path).
        var pointPortCurrents = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, _netlist, ifNodes);
        foreach (var (key, spec) in pointPortCurrents)
        {
            if (!portCurrentsByBranch.TryGetValue(key, out var lst))
                portCurrentsByBranch[key] = lst = [];
            lst.Add(spec);
        }

        trace.Print();

        // C1: Lazy linear back-solver — recovers linear-interior node voltages on demand.
        var backSolver = new HbLinearBackSolver(
            extractor, f0, K, [solveResult.INl], [bSrcThisPoint], _netlist);

        // Expand V/INl to all user-facing non-ground nodes (brief hb-linear-nodes-in-cube).
        // Interface nodes use the converged Newton solution directly; linear-only nodes
        // are recovered via HbLinearBackSolver.  __-prefixed internal mint nodes are excluded.
        var fullNodeIds = Enumerable.Range(1, _netlist.Nodes.Count - 1)
            .Where(c => !_netlist.Nodes.NameOf(c).StartsWith("__", StringComparison.Ordinal))
            .ToArray();

        var ifNodeToIdx = new Dictionary<int, int>(N);
        for (int n = 0; n < N; n++)
            ifNodeToIdx[ifNodes[n]] = n;

        int     Nfull     = fullNodeIds.Length;
        var     Vfull     = new Complex[Nfull, K + 1];
        var     INlfull   = new Complex[Nfull, K + 1];
        var     namesFull = new string[Nfull];

        for (int fi = 0; fi < Nfull; fi++)
        {
            int c = fullNodeIds[fi];
            namesFull[fi] = _netlist.Nodes.NameOf(c);
            if (ifNodeToIdx.TryGetValue(c, out int ifIdx))
            {
                for (int k = 0; k <= K; k++)
                {
                    Vfull  [fi, k] = V             [ifIdx, k];
                    INlfull[fi, k] = solveResult.INl[ifIdx, k];
                }
            }
            else
            {
                // Linear-only node: back-solve voltage; INl = 0 (no nonlinear current here).
                for (int k = 0; k <= K; k++)
                    Vfull[fi, k] = backSolver.GetNodeVoltage(c, k, 0);
            }
        }

        var ds = BuildSingleToneDataSet(
            Vfull, INlfull, namesFull, f0, K, trace, portCurrentsByBranch,
            _netlist.Nodes.LabeledNames);

        return new HbRunResult(ds, backSolver);
    }

    // ── Two-tone engine entry point (harmonic-balance.md §6) ─────────────────

    /// <summary>
    /// Multi-tone (two-tone) HB run. Generalizes <see cref="Run"/> from the scalar harmonic axis
    /// to the 2-D mixing lattice (<see cref="MixingGrid"/>): the linear interface and Norton source
    /// are extracted per retained mixing product at ω(k₁,k₂)=2π(k₁f₁+k₂f₂), the nonlinear blocks
    /// use the separable 2-D FFT, and the Newton solve is <see cref="HbNewton2D.Solve"/>.
    /// The returned <see cref="HbResult"/> carries the grid; V/INl are indexed [sweep][node, mixIdx].
    /// </summary>
    private DataSet RunTwoTone(HbAnalysisParams p)
    {
        double f1 = p.ToneFreqsHz[0];
        double f2 = p.ToneFreqsHz[1];
        double w1 = 2.0 * Math.PI * f1, w2 = 2.0 * Math.PI * f2;

        var grid     = new MixingGrid(p.MaxMixOrder);
        int M        = grid.MixCount;
        // Per-axis grid sizing reaches the diamond's single-axis extent (k=MaxMixOrder) and, via
        // the 4·order rule, the 2·order sum bins the Jacobian needs (harmonic-balance.md §5.2/§6.1).
        var (N1, N2) = HbFft2D.GridSizes(p.MaxMixOrder, p.MaxMixOrder, p.FFTOverSample);

        var extractor = new HbLinearExtractor(_netlist, _settings);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        var nodeNames = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();

        CheckCommensurabilityMultiTone(grid, f1, f2);

        // ── Configure P1Tone sources with tone context (must precede extraction) ──
        double fcTwoTone = (f1 + f2) / 2.0;
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is not P1ToneModel p1) continue;
            double driveHz = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == ValueKind.Real
                ? fv.AsReal() : f1;
            p1.SetToneContext(fc: fcTwoTone, driveFreqHz: driveHz);
        }

        // Extract the linear interface per mixing product. A retained rep may have NEGATIVE
        // frequency (e.g. (1,-1) = f1−f2); for a real network Y(−ω)=conj(Y(ω)). We extract at |ω|
        // and conjugate so the Z_Port's explicit complex Z(|f|) stays consistent with the L/C
        // elements (which conjugate naturally under ω→−ω). Sources sit only on positive carriers,
        // so the Norton vector at a negative-ω rep is zero (conj is a no-op there).
        (Complex[,] y, Complex[] s) ExtractMix(double omega)
        {
            if (omega >= 0) return extractor.Extract(omega);
            var (yp, sp) = extractor.Extract(-omega);
            return (ConjugateMatrix(yp), ConjugateVector(sp));
        }

        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int m = 1; m < M; m++)
            (yNN[m], iSrc[m]) = ExtractMix(grid.OmegaOf(m, w1, w2));

        var dcResult = NonlinearDcEngine.Run(_netlist, _settings);
        if (!dcResult.Converged)
            _netlist.AddWarningOnce("hb-dc-nonconverge",
                "HB: DC operating point did not converge; proceeding with best available.");

        var trace = new HbConvergenceTrace();
        var portCurrentsByBranch = new Dictionary<string, List<Complex[]>>(StringComparer.Ordinal);
        var effectiveSettings = EffectiveSettings(p);

        var V = InitialGuess2D(null, dcResult, N, M, ifNodes);

        // Re-extract source excitation (drive amplitude baked into I_src).
        for (int m = 1; m < M; m++)
            (_, iSrc[m]) = ExtractMix(grid.OmegaOf(m, w1, w2));

        var solveResult = HbNewton2D.Solve(V, yNN, iSrc, grid, f1, f2, N, N1, N2,
            _netlist, ifNodes, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic);

        trace.AddStep(new HbConvergenceTrace.StepRecord(
            0.0, solveResult.Iterations, solveResult.Converged, solveResult.IterTrace));

        if (!solveResult.Converged)
        {
            double res2d = solveResult.IterTrace.LastOrDefault()?.ResidualNorm ?? 0.0;
            _netlist.AddWarning(
                $"HB (two-tone) did not converge (‖F‖={res2d:E3} after {solveResult.Iterations} iters); " +
                $"stored best-available result.");
            Console.Error.WriteLine(
                $"[HB2D] Non-convergence: ‖F‖={res2d:E3} after {solveResult.Iterations} iters.");
        }

        Console.Error.Write("[HB2D-DC]:");
        for (int n = 0; n < N; n++)
            Console.Error.Write(
                $"  {nodeNames[n]} V={V[n,0].Real:F3}V I_nl={solveResult.INl[n,0].Real*1e3:F2}mA");
        Console.Error.WriteLine();

        // C2: post-convergence per-device port-current extraction over the mixing lattice.
        var pointPortCurrents = HbNewton2D.ComputeDevicePortCurrents2D(
            V, grid, N, N1, N2, _netlist, ifNodes);
        foreach (var (key, spec) in pointPortCurrents)
        {
            if (!portCurrentsByBranch.TryGetValue(key, out var lst))
                portCurrentsByBranch[key] = lst = [];
            lst.Add(spec);
        }

        trace.Print();

        return BuildTwoToneDataSet(
            V, solveResult.INl, nodeNames, grid, f1, f2, trace, portCurrentsByBranch,
            _netlist.Nodes.LabeledNames);
    }

    /// <summary>Effective settings with HbMaxIter overridden from the directive's MaxIter.</summary>
    private AnalysisSettings EffectiveSettings(HbAnalysisParams p)
        => p.MaxIter == _settings.HbMaxIter ? _settings : new AnalysisSettings
        {
            HbMaxIter                 = p.MaxIter,
            Gmin                      = _settings.Gmin,
            ConductanceRegularization = _settings.ConductanceRegularization,
            InductanceRegularization  = _settings.InductanceRegularization,
            InductanceRegR            = _settings.InductanceRegR,
            Gmax                      = _settings.Gmax,
            DcBiasStepping            = _settings.DcBiasStepping,
            DriveStepping             = _settings.DriveStepping,
            NonlinearAbsTol           = _settings.NonlinearAbsTol,
        };

    private static Complex[,] ConjugateMatrix(Complex[,] a)
    {
        int r = a.GetLength(0), c = a.GetLength(1);
        var b = new Complex[r, c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                b[i, j] = Complex.Conjugate(a[i, j]);
        return b;
    }

    private static Complex[] ConjugateVector(Complex[] a)
    {
        var b = new Complex[a.Length];
        for (int i = 0; i < a.Length; i++) b[i] = Complex.Conjugate(a[i]);
        return b;
    }

    private static Complex[,] InitialGuess2D(
        Complex[,]? prevV, NonlinearDcEngine.DcResult dcResult, int N, int M, int[] ifNodes)
    {
        var V = new Complex[N, M];
        if (prevV is not null)
        {
            for (int n = 0; n < N; n++)
            for (int m = 0; m < M; m++)
                V[n, m] = prevV[n, m];
            return V;
        }

        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            double vdc = circNode > 0 && circNode - 1 < dcResult.NodeVoltages.Length
                ? dcResult.NodeVoltages[circNode - 1] : 0.0;
            V[n, 0] = new Complex(vdc, 0);          // (0,0) DC from the operating point
            for (int m = 1; m < M; m++)
                V[n, m] = new Complex(1e-3, 1e-3);  // small AC seed
        }
        return V;
    }

    /// <summary>
    /// Multi-tone commensurability (harmonic-balance.md §3.1): every tone-source frequency must land
    /// on the {k₁f₁+k₂f₂} lattice of a retained mixing product. Errors naming any off-grid source.
    /// </summary>
    private void CheckCommensurabilityMultiTone(MixingGrid grid, double f1, double f2)
    {
        const double Tol = 1.0;  // 1 Hz
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is ToneSourceModel)
            {
                foreach (var (key, val) in ec.Parameters)
                {
                    if (val.Kind != ValueKind.Real) continue;
                    bool isFreqKey = key.Equals("Freq", StringComparison.OrdinalIgnoreCase)
                                  || key.StartsWith("Freq[", StringComparison.OrdinalIgnoreCase);
                    if (!isFreqKey) continue;
                    double fTone = val.AsReal();
                    if (Math.Abs(fTone) < Tol) continue;

                    bool onGrid = false;
                    foreach (var (k1, k2) in grid.All())
                        if (Math.Abs(k1 * f1 + k2 * f2 - fTone) <= Tol) { onGrid = true; break; }
                    if (!onGrid)
                        throw new InvalidOperationException(
                            $"Commensurability check failed: source '{ec.InstancePath}' {key}={fTone:G6} Hz " +
                            $"is not on the two-tone grid {{f1={f1:G6}, f2={f2:G6}, MaxMixOrder={grid.MaxMixOrder}}}");
                }
            }
            else if (ec.Model is P1ToneModel p1)
            {
                double fTone = p1.FreqHz;
                if (Math.Abs(fTone) < Tol) continue;

                bool onGrid = false;
                foreach (var (k1, k2) in grid.All())
                    if (Math.Abs(k1 * f1 + k2 * f2 - fTone) <= Tol) { onGrid = true; break; }
                if (!onGrid)
                    throw new InvalidOperationException(
                        $"Commensurability check failed: source '{ec.InstancePath}' Freq={fTone:G6} Hz " +
                        $"is not on the two-tone grid {{f1={f1:G6}, f2={f2:G6}, MaxMixOrder={grid.MaxMixOrder}}}");
            }
        }
    }

    // ── Single-point solve (used by the Loadpull engine) ─────────────────────

    /// <summary>
    /// Result of a single HB Newton solve at one operating point.
    /// </summary>
    /// <summary>
    /// INl[n,k] sign convention — stated once here, followed everywhere:
    ///
    /// INl[n,k] = current flowing FROM interface node n INTO the nonlinear device
    ///            (passive sign convention: positive = entering the device, leaving the node).
    ///
    /// Derivation: EvaluateNonlinear accumulates res.I[p] (SDD port current, passive convention)
    /// into iTime[nodeIdx] then FFTs.  The HB residual is F = Y_NN*(V−V_oc) + INl = 0.
    /// At DC, INl[drain,0] = +Idd (drain current leaving n_drain into FET) balances the
    /// Norton supply current Y_NN*(V_oc−V[drain]) = +Idd flowing into n_drain.  ✓
    ///
    /// Consequences for power and impedance formulas:
    ///   At RF (choke open ⟹ only FET + load at drain; only FET + source at gate):
    ///
    ///   KCL at n_drain: I_into_load = −INl[drain,k]   (load absorbs what FET drives out)
    ///   KCL at n_gate:  I_into_gate_from_source = INl[gate,k]  (source delivers what FET absorbs)
    ///
    ///   ⟹ Pout          = ½·Re(V[drain]·conj(−INl[drain,1])) = −½·Re(V[drain]·conj(INl[drain,1]))
    ///   ⟹ Pin_delivered = ½·Re(V[gate]·conj(INl[gate,1]))     = +½·Re(V[gate]·conj(INl[gate,1]))
    ///   ⟹ Zin (DUT input impedance seen from source)          = V[gate,1] / INl[gate,1]
    ///      (no negation — INl[gate,1] = I_from_source_into_gate)
    ///   ⟹ Zsource = conj(Zin)
    ///
    /// The sign ASYMMETRY between Pout and Pin is NOT ad-hoc; it follows directly from
    /// which side is "into load" vs "into device."
    /// </summary>
    public sealed record SinglePointResult(
        Complex[,] V,          // [N, K+1] — converged interface voltages (or best-available)
        Complex[,] INl,        // [N, K+1] — nonlinear device currents; see sign convention above
        bool       Converged,
        int        Iterations,
        string?    FailReason, // non-null only on non-convergence or errors
        IReadOnlyList<HbConvergenceTrace.IterRecord> IterTrace);

    /// <summary>
    /// Runs a single HB Newton solve at the current netlist operating point (no sweep loop).
    /// The caller (LoadpullEngine) manages the outer termination-grid and Pin loops,
    /// updating TunerModel state before each call.
    ///
    /// <paramref name="warmStart"/> — if provided, seeds V from this array instead of the DC point.
    /// Y_NN is re-extracted from the current netlist state (after any TunerModel overrides).
    ///
    /// Settings used: <paramref name="p"/>.MaxIter overrides HbMaxIter;
    /// InductanceRegularization is taken from the injected <paramref name="settingsOverride"/>
    /// (the loadpull engine passes Always).
    /// </summary>
    public SinglePointResult RunSinglePoint(
        HbAnalysisParams  p,
        Complex[,]?       warmStart        = null,
        AnalysisSettings? settingsOverride  = null)
    {
        int    K      = p.MaxHarmonic;
        double f0     = p.ToneHz;
        int    gridN  = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * f0;

        var settings = settingsOverride ?? _settings;
        // Honour the directive's MaxIter.
        if (p.MaxIter != settings.HbMaxIter)
            settings = new AnalysisSettings
            {
                HbMaxIter                 = p.MaxIter,
                Gmin                      = settings.Gmin,
                ConductanceRegularization = settings.ConductanceRegularization,
                InductanceRegularization  = settings.InductanceRegularization,
                InductanceRegR            = settings.InductanceRegR,
                Gmax                      = settings.Gmax,
                DcBiasStepping            = settings.DcBiasStepping,
                DriveStepping             = settings.DriveStepping,
                NonlinearAbsTol           = settings.NonlinearAbsTol,
            };

        var extractor = new HbLinearExtractor(_netlist, settings);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;

        // Extract Y_NN and I_src at every harmonic.
        var yNN  = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        try
        {
            (yNN[0], iSrc[0]) = extractor.ExtractDC();
        }
        catch (SingularMatrixException ex)
        {
            return new SinglePointResult(
                new Complex[N, K + 1], new Complex[N, K + 1],
                false, 0, $"DC extraction singular: {ex.Message}", []);
        }
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;
            var (y, s) = extractor.Extract(omegaK);
            yNN[k]  = y;
            iSrc[k] = s;
        }

        // Initial guess: warm-start if provided; else seed from DC operating point.
        Complex[,] V;
        if (warmStart is not null && warmStart.GetLength(0) == N && warmStart.GetLength(1) == K + 1)
        {
            V = new Complex[N, K + 1];
            for (int n = 0; n < N; n++)
            for (int k = 0; k <= K; k++)
                V[n, k] = warmStart[n, k];
        }
        else
        {
            var dcResult = NonlinearDcEngine.Run(_netlist, settings);
            V = InitialGuess(null, dcResult, N, K, ifNodes);
        }

        // Newton solve.
        var sr = HbNewton.Solve(V, yNN, iSrc, f0, K, N, _netlist, ifNodes, gridN, settings, p.Tol,
            p.Lambda, p.GuardHarmonic);

        string? failReason = null;
        if (!sr.Converged)
            failReason = $"Newton non-convergence: ‖F‖={sr.IterTrace.LastOrDefault()?.ResidualNorm:E3} after {sr.Iterations} iters";

        return new SinglePointResult(V, sr.INl, sr.Converged, sr.Iterations, failReason, sr.IterTrace);
    }

    // ── Initial guess ────────────────────────────────────────────────────────

    private static Complex[,] InitialGuess(
        Complex[,]?  prevV,
        NonlinearDcEngine.DcResult dcResult,
        int N, int K, int[] ifNodes)
    {
        var V = new Complex[N, K + 1];

        if (prevV is not null)
        {
            // Warm-start: copy previous converged spectrum.
            for (int n = 0; n < N; n++)
            for (int k = 0; k <= K; k++)
                V[n, k] = prevV[n, k];
            return V;
        }

        // Cold-start: DC operating point for k=0; small seed for k≥1.
        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            double vdc   = circNode > 0 && circNode - 1 < dcResult.NodeVoltages.Length
                ? dcResult.NodeVoltages[circNode - 1]
                : 0.0;
            V[n, 0] = new Complex(vdc, 0);
            for (int k = 1; k <= K; k++)
                V[n, k] = new Complex(1e-3, 1e-3);   // small harmonic seed (deck slide 28)
        }

        return V;
    }

    // ── Sweep-point update ────────────────────────────────────────────────────

    private void UpdateSweepPoint(string sweepVar, double newValue)
    {
        // Update the sweep variable in the resolved globals (for ToneSourceModel re-evaluation).
        // Find ToneSourceModel instances and re-evaluate their V/Vdc expressions.
        var updatedGlobals = new Dictionary<string, Value>(_netlist.ResolvedGlobals,
            StringComparer.Ordinal)
        {
            [sweepVar] = new Value(newValue)
        };

        // Re-evaluate all globals that might depend on the sweep variable.
        // We use the TestBench GlobalVariables (expression strings) to rebuild.
        var reEvaluated = ReEvaluateGlobals(_tb, sweepVar, newValue,
            _netlist.ResolvedGlobals);

        // Update ToneSourceModels with the new globals.
        foreach (var ec in _netlist.Components)
            if (ec.Model is ToneSourceModel tsm)
                tsm.ReevaluateFromGlobals(reEvaluated);
    }

    private static IReadOnlyDictionary<string, Value> ReEvaluateGlobals(
        TestBench tb, string sweepVar, double newValue,
        IReadOnlyDictionary<string, Value> baseGlobals)
    {
        var result = new Dictionary<string, Value>(baseGlobals, StringComparer.Ordinal)
        {
            [sweepVar] = new Value(newValue)
        };

        // Re-evaluate all GlobalVariables from their expressions with sweepVar overridden.
        var scope = new Scope("sweep-globals");
        foreach (var kv in result)
            scope.Bind(kv.Key, kv.Value.ToString()!);
        var ev = new Evaluator();
        foreach (var kv in result)
            ev.InjectResolved("sweep-globals", kv.Key, kv.Value);

        foreach (var v in tb.GlobalVariables)
        {
            if (v.Name == sweepVar) continue;
            try
            {
                var ast = Parser.Parse(v.Expression);
                var val = ev.EvalExpr(ast, scope);
                if (val.Kind is ValueKind.Real or ValueKind.Complex)
                {
                    result[v.Name] = val;
                    // Update scope for subsequent variables that depend on this one.
                    scope.Bind(v.Name, val.ToString()!);
                    ev.InjectResolved("sweep-globals", v.Name, val);
                }
            }
            catch { /* leave previous value */ }
        }

        return result;
    }

    // ── Jacobian diagnostic (PASS A) ─────────────────────────────────────────

    /// <summary>
    /// Compare the analytic Jacobian (BuildJ) to a central-difference Jacobian of BuildF
    /// at operating point <paramref name="Vstar"/>, with the sweep variable set to
    /// <paramref name="sweepVal"/>.
    ///
    /// Use after engine.Run() to get a converged V; pass result.V[sweepIdx] as Vstar.
    /// </summary>
    public HbNewton.JacobianComparisonResult RunJacobianDiagnostic(
        HbAnalysisParams p,
        Complex[,]       Vstar,
        double           sweepVal)
    {
        // Set sweep state so I_src(k) reflects the right drive level.
        if (p.SweepVarName is not null)
            UpdateSweepPoint(p.SweepVarName, sweepVal);

        int    K      = p.MaxHarmonic;
        double f0     = p.ToneHz;
        int    gridN  = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * f0;

        var extractor = new HbLinearExtractor(_netlist, _settings);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;

        var yNN  = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;
            var (y, s) = extractor.Extract(omegaK);
            yNN[k]  = y;
            iSrc[k] = s;
        }

        // Vstar may have been computed at a different N (e.g. different netlist state).
        // Validate dimensions.
        if (Vstar.GetLength(0) != N || Vstar.GetLength(1) != K + 1)
            throw new ArgumentException(
                $"Vstar dimensions [{Vstar.GetLength(0)},{Vstar.GetLength(1)}] " +
                $"do not match extractor N={N}, K={K}.");

        return HbNewton.CompareJacobianNumerical(Vstar, yNN, iSrc, f0, K, N, _netlist, ifNodes, gridN);
    }

    // ── DataSet builders (5-3) ────────────────────────────────────────────────

    /// <summary>
    /// Build the DataSet returned by single-tone Run().
    /// Always single-point: V/INl axes [node, harmonic]; Converged/Residual = scalar.
    /// I:* branch-current cubes have axis [harmonic].
    /// Node axis carries Labels = nodeNames for V("n_drain", …) lookups.
    /// Harmonic axis values = {0, f0, 2f0, …, K·f0} Hz.
    /// </summary>
    private static DataSet BuildSingleToneDataSet(
        Complex[,]  V,
        Complex[,]  iNl,
        string[]    nodeNames,
        double      f0,
        int         K,
        HbConvergenceTrace trace,
        Dictionary<string, List<Complex[]>> portCurrents,
        HashSet<string>?    labeledNames = null)
    {
        int N  = V.GetLength(0);
        int K1 = K + 1;

        var nodeVals = new double[N];
        for (int i = 0; i < N; i++) nodeVals[i] = i;
        var nodeAxis = new Axis("node", nodeVals, "", nodeNames);

        var harmVals = new double[K1];
        for (int k = 0; k < K1; k++) harmVals[k] = k * f0;
        var harmAxis = new Axis("harmonic", harmVals, "Hz");

        var vData   = new Complex[N * K1];
        var inlData = new Complex[N * K1];
        for (int n = 0; n < N; n++)
        for (int k = 0; k < K1; k++)
        {
            vData  [n * K1 + k] = V  [n, k];
            inlData[n * K1 + k] = iNl[n, k];
        }

        var step0   = trace.Steps.Count > 0 ? trace.Steps[0] : null;
        double conv = step0?.Converged == true ? 1.0 : 0.0;
        double res  = step0?.IterTrace.Count > 0 ? step0.IterTrace[^1].ResidualNorm : 0.0;

        var ds = new DataSet();
        ds.Add("V",         new DataCube([nodeAxis, harmAxis], vData));
        ds.Add("INl",       new DataCube([nodeAxis, harmAxis], inlData));
        ds.Add("Converged", DataCube.Scalar(conv));
        ds.Add("Residual",  DataCube.Scalar(res));

        // Provenance: which node-axis entries came from a user net label (for the node-picker filter).
        if (labeledNames is not null)
        {
            var labeled = nodeNames.Where(n => labeledNames.Contains(n)).Distinct().ToArray();
            if (labeled.Length > 0)
            {
                var lIdx = Enumerable.Range(0, labeled.Length).Select(i => (double)i).ToArray();
                ds.Add("__LabeledNodes", new DataCube(
                    [new Axis("label", lIdx, "", labeled)],
                    new double[labeled.Length]));
            }
        }

        foreach (var (branchKey, specList) in portCurrents)
        {
            if (specList.Count == 0) continue;
            var spec  = specList[0];
            var iData = new Complex[K1];
            for (int k = 0; k < K1 && k < spec.Length; k++) iData[k] = spec[k];
            ds.Add("I:" + branchKey, new DataCube([harmAxis], iData));
        }

        return ds;
    }

    /// <summary>
    /// Build the DataSet returned by two-tone Run().
    /// Always single-point: V/INl axes [node, mixIndex] (Complex); Converged/Residual = scalar.
    /// MixIndex axis values = {k1·f1+k2·f2} Hz per product, Labels = {"(k1,k2)"}.
    /// ToneFreqs cube has axis [tone] (Real): {f1, f2} Hz.
    /// MetaMixOrder cube has axis [1] (Real): {MaxMixOrder}.
    /// I:* branch-current cubes have axis [mixIndex].
    /// </summary>
    private static DataSet BuildTwoToneDataSet(
        Complex[,]  V,
        Complex[,]  iNl,
        string[]    nodeNames,
        MixingGrid  grid,
        double      f1,
        double      f2,
        HbConvergenceTrace trace,
        Dictionary<string, List<Complex[]>> portCurrentsByBranch,
        HashSet<string>?    labeledNames = null)
    {
        int N = V.GetLength(0);
        int M = grid.MixCount;

        var nodeVals = new double[N];
        for (int i = 0; i < N; i++) nodeVals[i] = i;
        var nodeAxis = new Axis("node", nodeVals, "", nodeNames);

        var mixVals   = new double[M];
        var mixLabels = new string[M];
        for (int m = 0; m < M; m++)
        {
            var (k1, k2) = grid.ToneOf(m);
            mixVals[m]   = k1 * f1 + k2 * f2;
            mixLabels[m] = $"({k1},{k2})";
        }
        var mixAxis = new Axis("mixIndex", mixVals, "Hz", mixLabels);

        var vData   = new Complex[N * M];
        var inlData = new Complex[N * M];
        for (int n = 0; n < N; n++)
        for (int m = 0; m < M; m++)
        {
            vData  [n * M + m] = V  [n, m];
            inlData[n * M + m] = iNl[n, m];
        }

        var step0   = trace.Steps.Count > 0 ? trace.Steps[0] : null;
        double conv = step0?.Converged == true ? 1.0 : 0.0;
        double res  = step0?.IterTrace.Count > 0 ? step0.IterTrace[^1].ResidualNorm : 0.0;

        var toneAxis  = new Axis("tone", [1.0, 2.0], "");
        var orderAxis = new Axis("order", [1.0], "");

        var ds = new DataSet();
        ds.Add("V",            new DataCube([nodeAxis, mixAxis], vData));
        ds.Add("INl",          new DataCube([nodeAxis, mixAxis], inlData));
        ds.Add("Converged",    DataCube.Scalar(conv));
        ds.Add("Residual",     DataCube.Scalar(res));
        ds.Add("ToneFreqs",    new DataCube([toneAxis], new double[] { f1, f2 }));
        ds.Add("MetaMixOrder", new DataCube([orderAxis], new double[] { grid.MaxMixOrder }));

        if (labeledNames is not null)
        {
            var labeled = nodeNames.Where(n => labeledNames.Contains(n)).Distinct().ToArray();
            if (labeled.Length > 0)
            {
                var lIdx = Enumerable.Range(0, labeled.Length).Select(i => (double)i).ToArray();
                ds.Add("__LabeledNodes", new DataCube(
                    [new Axis("label", lIdx, "", labeled)],
                    new double[labeled.Length]));
            }
        }

        foreach (var (branchKey, specList) in portCurrentsByBranch)
        {
            if (specList.Count == 0) continue;
            var spec  = specList[0];
            var iData = new Complex[M];
            for (int m = 0; m < M && m < spec.Length; m++) iData[m] = spec[m];
            ds.Add("I:" + branchKey, new DataCube([mixAxis], iData));
        }

        return ds;
    }

    // ── Commensurability check (harmonic-balance.md §3.1) ────────────────────

    private void CheckCommensurability(double f0, int K)
    {
        const double Tol = 1.0;  // 1 Hz tolerance on frequency matching

        foreach (var ec in _netlist.Components)
        {
            double freqHz = 0;
            if (ec.Model is ToneSourceModel)
            {
                if (!ec.Parameters.TryGetValue("Freq", out var fv) || fv.Kind != ValueKind.Real) continue;
                freqHz = fv.AsReal();
            }
            else if (ec.Model is P1ToneModel p1)
            {
                freqHz = p1.FreqHz;
            }
            else continue;

            if (freqHz == 0) continue;  // DC-only source, no tone

            // Check: freqHz = m * f0 for some integer m in 1..K.
            double ratio    = freqHz / f0;
            double nearestK = Math.Round(ratio);
            if (Math.Abs(ratio - nearestK) * f0 > Tol || nearestK < 1 || nearestK > K)
                throw new InvalidOperationException(
                    $"Commensurability check failed: source '{ec.InstancePath}' " +
                    $"Freq={freqHz:G6} Hz is not on the HB tone grid " +
                    $"{{f0={f0:G6} Hz, MaxHarm={K}}}");
        }
    }
}
