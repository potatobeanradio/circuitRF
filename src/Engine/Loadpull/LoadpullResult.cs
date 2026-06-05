using System.Numerics;

namespace CircuitRF.Engine.Loadpull;

/// <summary>
/// Full 2-D loadpull result: every (grid point × Pin step) retains V, I_nl, live FOMs,
/// and bias-supply data. The post-processor computes exact P-xdB, contours, etc. from this.
/// (loadpull.md §5 — capture everything; the post-processor analyzes.)
/// </summary>
public sealed class LoadpullResult
{
    public IReadOnlyList<GridPointResult> GridPoints { get; }
    public GamReader.GamGrid             Grid       { get; }
    public double                         ToneHz     { get; }
    public int                            MaxHarm    { get; }

    /// <summary>Interface node indices (circuit node numbers, 1-based).</summary>
    public int[]    InterfaceNodes     { get; }
    public string[] InterfaceNodeNames { get; }

    public LoadpullResult(
        IReadOnlyList<GridPointResult> gridPoints,
        GamReader.GamGrid              grid,
        double                          toneHz,
        int                             maxHarm,
        int[]                           interfaceNodes,
        string[]                        interfaceNodeNames)
    {
        GridPoints          = gridPoints;
        Grid                = grid;
        ToneHz              = toneHz;
        MaxHarm             = maxHarm;
        InterfaceNodes      = interfaceNodes;
        InterfaceNodeNames  = interfaceNodeNames;
    }
}

/// <summary>
/// Result for one grid point: its termination, the adaptive Pin sweep results, and the stop reason.
/// </summary>
public sealed class GridPointResult
{
    public int     GridIndex  { get; }
    public Complex Gamma      { get; }    // Γ at TuneHarm
    public Complex Z          { get; }    // Z at TuneHarm

    public IReadOnlyList<PinStepResult> PinSteps   { get; }
    public string                        StopReason { get; }  // "Compression", "PinMax", "NonConvergence", "NoConvergedSeed"

    public GridPointResult(int gridIndex, Complex gamma, Complex z,
        IReadOnlyList<PinStepResult> pinSteps, string stopReason)
    {
        GridIndex  = gridIndex;
        Gamma      = gamma;
        Z          = z;
        PinSteps   = pinSteps;
        StopReason = stopReason;
    }

    /// <summary>Returns the last converged Pin step, or null if none converged.</summary>
    public PinStepResult? LastConvergedStep => PinSteps.LastOrDefault(s => s.Converged);
}

/// <summary>
/// Result at one (grid point, Pin step): the full spectra and all live FOMs.
/// </summary>
public sealed class PinStepResult
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public double PavlDbm   { get; }
    public bool   IsTickle  { get; }   // true for the prepended very-low-Pin tickle point

    // ── Spectra (all harmonics 0..K, all interface nodes) ─────────────────────
    /// <summary>V[node_idx, harmonic_k] — complex interface voltages.</summary>
    public Complex[,] V   { get; }
    /// <summary>INl[node_idx, harmonic_k] — nonlinear device currents.</summary>
    public Complex[,] INl { get; }

    // ── Live FOMs (fundamental k=1 only; loadpull.md §4) ─────────────────────
    public double PavlW          { get; }   // available source power (W)
    public double PinDeliveredW  { get; }   // power delivered to DUT input  = +½Re(V_gate · I_nl_gate*)
    public double PoutW          { get; }   // power into load               = −½Re(V_drain · I_nl_drain*)
    public double GtDb           { get; }   // transducer gain Gt = Pout/Pavl (dB)
    public double GpDb           { get; }   // power gain Gp = Pout/Pin_delivered (dB)

    // ── Bias supply (for Pdc / efficiency) ───────────────────────────────────
    public double BiasVoltageLoadV   { get; }   // V(n_drain) ≈ Vdd (at DC, exact for ideal choke)
    public double BiasCurrentLoadA   { get; }   // = -INl[drain,0].Real; supply current = -BiasCurrentLoadA
    public double BiasVoltageSrcV    { get; }   // V(n_gate)  ≈ Vgg
    public double BiasCurrentSrcA    { get; }   // = -INl[gate,0].Real;  supply current = -BiasCurrentSrcA

    // ── Efficiency (4b-2) ─────────────────────────────────────────────────────
    // Pdc = Σ Vdc·Idc over Tuner bias supplies (loadpull_pursuit.md §2).
    // Supply current sign: I_supply = INl[node,0].Real = -BiasCurrentA (see LoadpullEngine).
    // For ideal choke/cap: V(n_dut) = Vbias exactly, so bias data is exact.
    public double PdcW { get; }    // total DC power drawn from all Tuner bias supplies (W)
    public double De   { get; }    // drain efficiency = Pout / Pdc (linear, not dB)
    public double Pae  { get; }    // PAE = (Pout − Pin_delivered) / Pdc (linear)

    // ── Convergence ──────────────────────────────────────────────────────────
    public bool    Converged   { get; }
    public int     Iterations  { get; }
    public string? FailReason  { get; }

    public PinStepResult(
        double pavlDbm, bool isTickle,
        Complex[,] v, Complex[,] iNl,
        double pavlW, double pinDeliveredW, double poutW, double gtDb, double gpDb,
        double biasVoltageLoadV, double biasCurrentLoadA,
        double biasVoltageSrcV,  double biasCurrentSrcA,
        bool converged, int iterations, string? failReason)
    {
        PavlDbm           = pavlDbm;
        IsTickle          = isTickle;
        V                 = v;
        INl               = iNl;
        PavlW             = pavlW;
        PinDeliveredW     = pinDeliveredW;
        PoutW             = poutW;
        GtDb              = gtDb;
        GpDb              = gpDb;
        BiasVoltageLoadV  = biasVoltageLoadV;
        BiasCurrentLoadA  = biasCurrentLoadA;
        BiasVoltageSrcV   = biasVoltageSrcV;
        BiasCurrentSrcA   = biasCurrentSrcA;
        Converged         = converged;
        Iterations        = iterations;
        FailReason        = failReason;

        // Pdc: for each Tuner, supply current = INl[node,0].Real = -BiasCurrentA.
        // Pdc = Vload·(-BiasILoad) + Vsrc·(-BiasISrc).
        PdcW = biasVoltageLoadV * (-biasCurrentLoadA) + biasVoltageSrcV * (-biasCurrentSrcA);
        De   = PdcW > 1e-9 ? poutW / PdcW : 0.0;
        Pae  = PdcW > 1e-9 ? (poutW - pinDeliveredW) / PdcW : 0.0;
    }
}
