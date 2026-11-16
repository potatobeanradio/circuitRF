// ================================================================
//  TransientTypes.cs  —  what a transient run is asked for and what it returns
//  (docs/design/transient.md §3, §4; brief-trn-1)
// ================================================================

namespace CircuitRF.Engine.Transient;

/// <summary>The integration method of note §4.2. Both take backward-Euler steps at the start and after every breakpoint.</summary>
public enum TransientMethod { Trap, Gear2 }

/// <summary>Where a run starts (note §4.5).</summary>
public enum TransientStart { Op, Zero }

/// <summary>How each step's Newton is solved (note §4.4). <see cref="Auto"/> picks by the interface size.</summary>
public enum TransientNewtonPath { Auto, Interface, Full }

/// <summary>
/// A time-domain drive added to the right-hand side at every step, with the corners it reports to the step control. The
/// waveform sources (brief TRN-2) arrive here; a test may add one directly.
/// </summary>
public interface ITransientDrive
{
    /// <summary>The corners of the drive's waveform in (<paramref name="t0"/>, <paramref name="t1"/>].</summary>
    IEnumerable<double> Breakpoints(double t0, double t1);

    /// <summary>Adds the drive's contribution at time <paramref name="t"/> to <paramref name="rhs"/> (the MNA right-hand
    /// side: node rows first, then branch rows).</summary>
    void AddTo(double t, double[] rhs);
}

/// <summary>A resolved transient analysis: every value in base SI.</summary>
public sealed record TransientParams
{
    public double StopS { get; init; }
    /// <summary>Output grid spacing; 0 = Stop/1000.</summary>
    public double StepS { get; init; }
    public double StartS { get; init; }
    /// <summary>Largest accepted step; 0 = automatic (note §3).</summary>
    public double MaxStepS { get; init; }
    public TransientMethod Method { get; init; } = TransientMethod.Trap;
    public double RelTol { get; init; } = 1e-3;
    public double VTol { get; init; } = 1e-6;
    public double ITol { get; init; } = 1e-12;
    public TransientStart Start { get; init; } = TransientStart.Op;
    /// <summary>Net voltages held through the operating-point solve and released at t = 0⁺ (with <see cref="TransientStart.Zero"/>,
    /// the starting values of those nets).</summary>
    public IReadOnlyDictionary<string, double> IcNets { get; init; } = new Dictionary<string, double>();
    public bool SaveSteps { get; init; }
    public long MaxSteps { get; init; } = 10_000_000;
    public TransientNewtonPath NewtonPath { get; init; } = TransientNewtonPath.Auto;
    /// <summary>Drives added to the right-hand side (see <see cref="ITransientDrive"/>).</summary>
    public IReadOnlyList<ITransientDrive> Drives { get; init; } = [];
    /// <summary>The analysis, for the sentences a run writes.</summary>
    public string Name { get; init; } = "";
    /// <summary>brief TRN-7 — which solver runs it: <c>Solver=native</c> or <c>ngspice</c>.</summary>
    public TransientSolver Solver { get; init; } = TransientSolver.Native;
    /// <summary>brief TRN-7 — the directive these values were resolved from: the ngspice solver writes its deck from it.</summary>
    public CircuitRF.Core.Design.TransientAnalysis? Directive { get; init; }
}

/// <summary>brief TRN-7 — the solver a transient analysis runs on (D2).</summary>
public enum TransientSolver { Native, Ngspice }

/// <summary>One transient run, on the uniform output grid.</summary>
public sealed class TransientRunResult
{
    public required double[] Time { get; init; }
    public required string[] NodeNames { get; init; }
    /// <summary>[time][node] node voltage (V).</summary>
    public required double[][] V { get; init; }
    public required string[] ProbeNames { get; init; }
    /// <summary>[time][probe] the IProbe's current (A), flowing from its first node to its second.</summary>
    public required double[][] I { get; init; }
    /// <summary>[time][probe] the voltage at the probe's first node times its current (W).</summary>
    public required double[][] P { get; init; }
    /// <summary>[time][probe] the voltage at the probe's first node (V), what <see cref="P"/> multiplies the current by; with the
    /// current, a harmonic's power through <c>fft_power</c> (brief TRN-5).</summary>
    public double[][] VProbe { get; init; } = [];
    /// <summary>The starting state at every recorded node (V).</summary>
    public required double[] OpV { get; init; }
    /// <summary>The accepted steps' times, when asked for (SaveSteps).</summary>
    public double[]? StepTime { get; init; }
    /// <summary>[step][node], when asked for.</summary>
    public double[][]? StepV { get; init; }
    /// <summary>Each accepted step's method, when asked for: 'S' the start, 'E' backward Euler, 'T' trapezoidal, 'G' gear2.</summary>
    public char[]? StepMethod { get; init; }

    public long AcceptedSteps { get; init; }
    public long RejectedSteps { get; init; }
    public long NewtonIterations { get; init; }
    public long Factorizations { get; init; }
    /// <summary>Calls into the devices' Evaluate (one round trip each to a worker-hosted device).</summary>
    public long DeviceEvaluations { get; init; }
    /// <summary>The breakpoints a step landed on (the start and Stop included).</summary>
    public int BreakpointsLanded { get; init; }
    /// <summary>The distinct (method, ladder rung) pairs the run stepped with.</summary>
    public int MethodRungsVisited { get; init; }
    public double MaxStepS { get; init; }
    public string MaxStepReason { get; init; } = "";
    public TransientNewtonPath NewtonPathUsed { get; init; }
    public int InterfaceCount { get; init; }
    public List<string> Notes { get; } = [];
    /// <summary>brief-trn-4 — each frequency-defined element's wideband fit (R-trn4-4), and each line stepped as its delay
    /// (R-trn4-1/2), with the band they were realized over (DC to f_max = 1/(2 MaxStep)).</summary>
    public IReadOnlyList<TransientFitReport> Fits { get; init; } = [];
    public IReadOnlyList<TransientLineReport> Lines { get; init; } = [];
    public double FitBandHz { get; init; }
}

/// <summary>What a run would be, solved for nothing (<see cref="TransientEngine.Plan"/>).</summary>
public sealed record TransientPlan(double MaxStepS, string MaxStepReason, double EstimatedSteps, double? SlowestS, string? SlowestWhat,
                                   IReadOnlyList<string> Problems);

/// <summary>A transient run refused before it started, or stopped: the message names what and why.</summary>
public sealed class TransientRefusedException(string message) : InvalidOperationException(message);
