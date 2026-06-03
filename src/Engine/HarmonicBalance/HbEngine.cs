using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Resolved HB analysis parameters (expression strings evaluated against ResolvedGlobals).
/// </summary>
public sealed record HbAnalysisParams(
    double   ToneHz,
    int      MaxHarmonic,
    int      FFTOverSample,
    double   Tol,
    DcBiasSteppingMode DriveStepping,
    int      GuardHarmonic,
    string?  SweepVarName,
    double   SweepStart,
    double   SweepStop,
    double   SweepStep)
{
    public bool HasSweep => SweepVarName is not null;

    /// <summary>Enumerate the sweep values (or a single 0 for no sweep).</summary>
    public IEnumerable<double> SweepValues()
    {
        if (!HasSweep) { yield return 0; yield break; }
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
    /// </summary>
    public Complex[][,] INl { get; }
    /// <summary>Interface node indices (circuit node numbers, 1-based).</summary>
    public int[] InterfaceNodes { get; }
    /// <summary>Node names at the interface (e.g. "n_gate", "n_drain").</summary>
    public string[] InterfaceNodeNames { get; }
    /// <summary>Per-sweep-point convergence trace.</summary>
    public HbConvergenceTrace Trace { get; }

    internal HbResult(double[] sweepValues, Complex[][,] V, Complex[][,] iNl,
        int[] interfaceNodes, string[] names, HbConvergenceTrace trace)
    {
        SweepValues       = sweepValues;
        this.V            = V;
        INl               = iNl;
        InterfaceNodes    = interfaceNodes;
        InterfaceNodeNames = names;
        Trace             = trace;
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

        double tone    = Num(hba.ToneExpr, 1e9);
        int    maxH    = (int)Num(hba.MaxHarmonicExpr, 7);
        int    osamp   = Math.Max(1, (int)Num(hba.FFTOverSampleExpr, 1));
        double tol     = Num(hba.TolExpr, 1e-6);
        int    guard   = (int)Num(hba.GuardHarmonicExpr, 0);

        var driveStepping = DcBiasSteppingMode.IfNecessary;
        var dsStr = hba.DriveSteppingExpr.Trim();
        if (dsStr.Equals("Always", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Always;
        else if (dsStr.Equals("Never", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Never;

        double sweepStart = 0, sweepStop = 0, sweepStep = 1;
        string? sweepVar = hba.SweepVarName;
        if (sweepVar is not null)
        {
            sweepStart = Num(hba.SweepStartExpr ?? "0", 0);
            sweepStop  = Num(hba.SweepStopExpr  ?? "0", 0);
            sweepStep  = Num(hba.SweepStepExpr  ?? "1", 1);
        }

        return new HbAnalysisParams(tone, maxH, osamp, tol, driveStepping, guard,
            sweepVar, sweepStart, sweepStop, sweepStep);
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

    public HbResult Run(HbAnalysisParams p)
    {
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
            Console.Error.WriteLine("[HB] Warning: DC operating point did not converge; proceeding with best available.");

        // ── Sweep loop ───────────────────────────────────────────────────────
        var sweepVals   = new List<double>();
        var sweepVArr   = new List<Complex[,]>();
        var sweepINlArr = new List<Complex[,]>();
        var trace       = new HbConvergenceTrace();

        Complex[,]? prevV = null;  // for warm-start continuation

        foreach (double sweepVal in p.SweepValues())
        {
            sweepVals.Add(sweepVal);

            // Update sweep variable and re-evaluate any sweep-dependent expressions.
            if (p.SweepVarName is not null)
                UpdateSweepPoint(p.SweepVarName, sweepVal);

            // Initial guess: warm-start from previous point, or cold-start from DC seed.
            var V = InitialGuess(prevV, dcResult, N, K, ifNodes);

            // Re-extract source excitation (changes with the drive amplitude at each Pin step).
            // Y_{N×N} is topology-based (constant); only I_src changes with Pin.
            for (int k = 1; k <= K; k++)
            {
                double omegaK = k * omega0;
                var (_, s) = extractor.Extract(omegaK);
                iSrc[k] = s;
            }

            // ── Newton solve ─────────────────────────────────────────────────
            var solveResult = HbNewton.Solve(V, yNN, iSrc, f0, K, N,
                _netlist, ifNodes, gridN, _settings, p.Tol);

            trace.AddStep(new HbConvergenceTrace.StepRecord(
                sweepVal, solveResult.Iterations, solveResult.Converged,
                solveResult.IterTrace));

            if (!solveResult.Converged)
            {
                Console.Error.WriteLine(
                    $"[HB] Non-convergence at {p.SweepVarName}={sweepVal}: " +
                    $"‖F‖={solveResult.IterTrace.LastOrDefault()?.ResidualNorm:E3} " +
                    $"after {solveResult.Iterations} iterations. " +
                    $"Storing best-available result.");
            }

            // DC diagnostic: report V[n,0] and I_nl[n,0] at each interface node.
            // I_nl[drain,0] should rise with Pin (self-biasing) — key sanity check after DC fix.
            Console.Error.Write($"[HB-DC] {p.SweepVarName}={sweepVal:F1}:");
            for (int n = 0; n < N; n++)
            {
                string nm = n < nodeNames.Length ? nodeNames[n] : $"if[{n}]";
                Console.Error.Write(
                    $"  {nm} V={V[n,0].Real:F3}V I_nl={solveResult.INl[n,0].Real*1e3:F2}mA");
            }
            Console.Error.WriteLine();

            sweepVArr.Add(V);
            sweepINlArr.Add(solveResult.INl);
            prevV = V;
        }

        // Print convergence summary to stderr (primary diagnostic).
        trace.Print();

        return new HbResult(
            sweepVals.ToArray(),
            sweepVArr.ToArray(),
            sweepINlArr.ToArray(),
            ifNodes,
            nodeNames,
            trace);
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
                V[n, k] = new Complex(1e-3, 0);   // small harmonic seed (deck slide 28)
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

    // ── Commensurability check (harmonic-balance.md §3.1) ────────────────────

    private void CheckCommensurability(double f0, int K)
    {
        const double Tol = 1.0;  // 1 Hz tolerance on frequency matching

        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is not ToneSourceModel tsm) continue;
            // The ToneSourceModel stores resolved FreqHz values per tone.
            // Re-check each declared frequency lands on the grid.
            // (ToneSourceModel doesn't expose FreqHz publicly; we read via parameter dict.)
            if (!ec.Parameters.TryGetValue("Freq", out var fv) || fv.Kind != ValueKind.Real)
                continue;
            double freqHz = fv.AsReal();
            if (freqHz == 0) continue;  // DC-only source, no tone

            // Check: freqHz = m * f0 for some integer m in 1..K.
            double ratio = freqHz / f0;
            double nearestK = Math.Round(ratio);
            if (Math.Abs(ratio - nearestK) * f0 > Tol || nearestK < 1 || nearestK > K)
                throw new InvalidOperationException(
                    $"Commensurability check failed: source '{ec.InstancePath}' " +
                    $"Freq={freqHz:G6} Hz is not on the HB tone grid " +
                    $"{{f0={f0:G6} Hz, MaxHarm={K}}}");
        }
    }
}
