using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.HarmonicBalance;

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
    double?            TickleDbm);       // null = disabled

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

    public static LoadpullAnalysisParams Resolve(
        LoadpullAnalysis lpa,
        IReadOnlyDictionary<string, Value> globals)
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

        double tone    = Num(lpa.ToneExpr,           1e9);
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

        double? tickle = null;
        var ts = lpa.TickleExpr.Trim();
        if (!ts.Equals("off", StringComparison.OrdinalIgnoreCase) &&
            !ts.Equals("false", StringComparison.OrdinalIgnoreCase))
            tickle = Num(ts, -50.0);

        if (string.IsNullOrEmpty(lpa.GridPath))
            throw new InvalidOperationException($"LoadpullAnalysis '{lpa.Name}': Grid= path is required.");
        if (!File.Exists(lpa.GridPath))
            throw new FileNotFoundException($"LoadpullAnalysis '{lpa.Name}': Grid file not found: '{lpa.GridPath}'");

        var grid = GamReader.ReadFile(lpa.GridPath);

        return new LoadpullAnalysisParams(
            tone, maxH, osamp, tol, driveStepping, guard, maxIter,
            lpa.LoadTunerName, lpa.SourceTunerName,
            sweepLoad, tuneHarm, grid,
            compress, useGt, pinStart, pinStep, pinMax, tickle);
    }

    // ── Engine entry point ───────────────────────────────────────────────────

    public LoadpullResult Run(LoadpullAnalysisParams p)
    {
        // Validate required tuner names.
        if (string.IsNullOrEmpty(p.LoadTunerName))
            throw new InvalidOperationException("LoadpullAnalysis: LoadTuner= is required.");
        if (string.IsNullOrEmpty(p.SourceTunerName))
            throw new InvalidOperationException("LoadpullAnalysis: SourceTuner= is required.");

        // Locate tuner models.
        var (loadEc, loadModel) = FindTuner(p.LoadTunerName, "LoadTuner");
        var (srcEc,  srcModel)  = FindTuner(p.SourceTunerName, "SourceTuner");

        // Assign roles and tone.
        loadModel.SetRole(TunerRole.Load);
        srcModel.SetRole(TunerRole.Source);
        loadModel.SetTone(p.ToneHz);
        // SourceTuner tone set via SetSourceDrive at each Pin step.

        // DUT-facing nodes:
        //   LoadTuner: Nodes[0] is the DUT-facing net.
        //   SourceTuner: Nodes[1] is the DUT-facing net.
        int loadDutNode = loadEc.Nodes.Length > 0 ? loadEc.Nodes[0] : 0;
        int srcDutNode  = srcEc.Nodes.Length  > 1 ? srcEc.Nodes[1]  : 0;

        // Discover interface nodes and check DUT connectivity.
        var tempExtractor = new HbLinearExtractor(_netlist, _lpSettings);
        int[] ifNodes     = tempExtractor.InterfaceNodes;
        var nodeNames     = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();
        int loadIfIdx     = Array.IndexOf(ifNodes, loadDutNode);
        int srcIfIdx      = Array.IndexOf(ifNodes, srcDutNode);

        if (loadIfIdx < 0)
            throw new InvalidOperationException(
                $"LoadTuner '{p.LoadTunerName}' DUT node (node {loadDutNode}, net " +
                $"'{_netlist.Nodes.NameOf(loadDutNode)}') is not a nonlinear interface node. " +
                "The FET must be connected to the Tuner's first declared net.");
        if (srcIfIdx < 0)
            throw new InvalidOperationException(
                $"SourceTuner '{p.SourceTunerName}' DUT node (node {srcDutNode}, net " +
                $"'{_netlist.Nodes.NameOf(srcDutNode)}') is not a nonlinear interface node. " +
                "The FET must be connected to the Tuner's second declared net.");

        int N = ifNodes.Length;
        int K = p.MaxHarmonic;

        // HB analysis params (no sweep; loadpull engine owns the outer loops).
        var hbParams = new HbAnalysisParams(
            ToneHz:        p.ToneHz,
            MaxHarmonic:   K,
            FFTOverSample: p.FFTOverSample,
            Tol:           p.Tol,
            DriveStepping: p.DriveStepping,
            GuardHarmonic: p.GuardHarmonic,
            SweepVarName:  null,
            SweepStart:    0, SweepStop: 0, SweepStep: 1,
            MaxIter:       p.MaxIter);

        // Per-solve settings: InductanceRegularization=Always, MaxIter from directive.
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

        // The swept tuner (whose TuneHarm termination is overridden per grid point).
        var sweptModel = p.SweepLoad ? loadModel : srcModel;

        var gridPoints    = new List<GridPointResult>();
        var convergedV    = new Dictionary<int, Complex[,]>();  // gridIdx → last converged V
        var gridPointList = p.Grid.Points;

        // ── Outer grid loop ──────────────────────────────────────────────────
        for (int gi = 0; gi < gridPointList.Count; gi++)
        {
            var gp    = gridPointList[gi];
            var gamma = gp.Gamma;
            var z     = gp.Z;

            Console.Error.WriteLine(
                $"[LP] Grid {gi+1}/{gridPointList.Count}: " +
                $"Γ={gamma.Real:F4}{(gamma.Imaginary >= 0 ? "+" : "")}{gamma.Imaginary:F4}j  " +
                $"Z={z.Real:F2}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary:F2}j Ω");

            sweptModel.SetHarmonicOverride(p.TuneHarm, z);

            // Γ-grid warm-start: seed from nearest already-converged neighbor.
            Complex[,]? gridSeed = FindNearestSeed(gi, gamma, convergedV, gridPointList);

            var pinSteps   = new List<PinStepResult>();
            string stopReason = "PinMax";
            double gMax    = double.NegativeInfinity;
            bool   overshot = false;   // true after we drove one step past compression

            Complex[,]? innerSeed = gridSeed;   // warm-start within the Pin sweep

            foreach (var (pavlDbm, isTickle) in BuildPinSequence(p))
            {
                double pavlW = DbmToWatts(pavlDbm);

                // Update source drive amplitude for this Pavl.
                srcModel.SetSourceDrive(p.ToneHz, pavlW);

                var sr = _hbEngine.RunSinglePoint(hbParams, innerSeed, solveSettings);

                // Compute live FOMs.
                var foms = ComputeFoms(sr.V, sr.INl, loadIfIdx, srcIfIdx, pavlW, K);

                // Bias supply approximate readback (4b-2 will use branch current directly).
                double vLoad = loadIfIdx >= 0 && sr.V.GetLength(1) > 0 ? sr.V[loadIfIdx, 0].Real : 0;
                double iLoad = loadIfIdx >= 0 && sr.INl.GetLength(1) > 0 ? -sr.INl[loadIfIdx, 0].Real : 0;
                double vSrc  = srcIfIdx  >= 0 && sr.V.GetLength(1) > 0 ? sr.V[srcIfIdx,  0].Real : 0;
                double iSrc2 = srcIfIdx  >= 0 && sr.INl.GetLength(1) > 0 ? -sr.INl[srcIfIdx, 0].Real : 0;

                pinSteps.Add(new PinStepResult(
                    pavlDbm, isTickle,
                    sr.V, sr.INl,
                    foms.PavlW, foms.PinDeliveredW, foms.PoutW, foms.GtDb, foms.GpDb,
                    vLoad, iLoad, vSrc, iSrc2,
                    sr.Converged, sr.Iterations, sr.FailReason));

                if (sr.Converged)
                    innerSeed = sr.V;

                // ── Stop logic ────────────────────────────────────────────────
                if (!sr.Converged)
                {
                    stopReason = "NonConvergence";
                    Console.Error.WriteLine(
                        $"[LP]   Pin={pavlDbm:F1}: non-convergence ({sr.FailReason}). Stopping.");
                    break;
                }

                double gain = p.UseGt ? foms.GtDb : foms.GpDb;
                string gainTag = p.UseGt ? "Gt" : "Gp";

                if (!isTickle)
                {
                    if (gain > gMax) gMax = gain;
                    double compression = gMax - gain;
                    Console.Error.WriteLine(
                        $"[LP]   Pin={pavlDbm:F1} dBm  Pout={WattsToDbm(foms.PoutW):F2} dBm  " +
                        $"{gainTag}={gain:F2} dB  compr={compression:F2} dB");

                    if (overshot)
                    {
                        // One overshoot step done — stop.
                        stopReason = "Compression";
                        break;
                    }

                    if (compression >= p.Compression)
                    {
                        // Just reached P-xdB. One more step = "~0.1 dB overshoot" for bracket.
                        stopReason = "Compression";
                        overshot   = true;
                        // Don't break yet — the loop will do one more step then hit the overshot check.
                    }

                    if (pavlDbm >= p.PinMaxDbm)
                    {
                        if (!overshot) stopReason = "PinMax";
                        break;
                    }
                }
            }

            // Update converged seed map.
            var lastConv = pinSteps.LastOrDefault(s => s.Converged);
            if (lastConv is not null)
                convergedV[gi] = lastConv.V;

            gridPoints.Add(new GridPointResult(gi, gamma, z, pinSteps, stopReason));
            Console.Error.WriteLine(
                $"[LP]   Stop={stopReason}  ({pinSteps.Count} Pin steps, " +
                $"{pinSteps.Count(s => s.Converged)} converged)");
        }

        sweptModel.ClearHarmonicOverride();
        srcModel.SetTone(0);   // reset to S-param mode
        loadModel.SetTone(0);

        return new LoadpullResult(gridPoints, p.Grid, p.ToneHz, K, ifNodes, nodeNames);
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

    private readonly record struct FomResult(
        double PavlW, double PinDeliveredW, double PoutW, double GtDb, double GpDb);

    /// <summary>
    /// Computes live FOMs from converged V and I_nl at fundamental (k=1).
    ///
    /// Sign convention (verified against Hero-2 golden data at Pavl=−20 dBm):
    ///   Pout          = −½·Re(V[loadIdx, 1] · conj(I_nl[loadIdx, 1]))  → positive for PA
    ///   Pin_delivered = +½·Re(V[srcIdx,  1] · conj(I_nl[srcIdx,  1]))  → positive for PA
    /// </summary>
    private static FomResult ComputeFoms(
        Complex[,] v, Complex[,] iNl,
        int loadIdx, int srcIdx,
        double pavlW, int K)
    {
        if (K < 1 || v.GetLength(1) < 2)
            return new FomResult(pavlW, 0, 0, double.NegativeInfinity, double.NegativeInfinity);

        var vLoad = loadIdx >= 0 ? v[loadIdx,   1] : Complex.Zero;
        var iLoad = loadIdx >= 0 ? iNl[loadIdx, 1] : Complex.Zero;
        var vSrc  = srcIdx  >= 0 ? v[srcIdx,    1] : Complex.Zero;
        var iSrc  = srcIdx  >= 0 ? iNl[srcIdx,  1] : Complex.Zero;

        double pout         = -0.5 * (vLoad * Complex.Conjugate(iLoad)).Real;
        double pinDelivered =  0.5 * (vSrc  * Complex.Conjugate(iSrc)).Real;

        double gtDb = pavlW > 1e-30         ? WToDb(pout / pavlW)         : double.NegativeInfinity;
        double gpDb = pinDelivered > 1e-30  ? WToDb(pout / pinDelivered)  : double.NegativeInfinity;

        return new FomResult(pavlW, pinDelivered, pout, gtDb, gpDb);
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

    private static double WToDb(double ratio)
        => ratio > 1e-30 ? 10.0 * Math.Log10(ratio) : -300.0;

    private static double WattsToDbm(double w)
        => w > 1e-30 ? 10.0 * Math.Log10(w) + 30.0 : -300.0;

    private static double DbmToWatts(double dbm)
        => Math.Pow(10.0, (dbm - 30.0) / 10.0);
}
