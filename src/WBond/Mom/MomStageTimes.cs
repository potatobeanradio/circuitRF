using System.Diagnostics;

namespace CircuitRF.WBond.Mom;

/// <summary>
/// An opt-in stopwatch accumulator for the kernel's own stage boundaries — the object WM-3's cost
/// table is measured with, and the object <see cref="WireMomCost"/>'s constants were fitted from.
///
/// <h3>Why this exists rather than a benchmark that re-implements the stages</h3>
/// <para>A cost table measured by a test that replicates <see cref="MomAssembly"/>'s steps outside it
/// measures the replica, and goes stale silently the first time the real assembly is restructured —
/// which is exactly what WM-3's M1 does to it. Threading one nullable accumulator through the real
/// code path costs a null check per stage and cannot drift.</para>
///
/// <h3>Single-threaded paths only</h3>
/// <para>Nothing here is synchronised. Pass one to <see cref="WireMomSolver.Create(WireMomMesh,MomStageTimes)"/>
/// or to a single <see cref="WireMomSolver.PortImpedance(double,bool,MomStageTimes)"/>; do <b>not</b>
/// pass one into the frequency-parallel sweep, where several points would add to the same fields at
/// once. <see cref="WireMomSolver.Solve"/> deliberately takes no accumulator for that reason.</para>
/// </summary>
public sealed class MomStageTimes
{
    /// <summary><b>L</b>, the segment-basis inductance fill.</summary>
    public double InductanceFillMs { get; set; }

    /// <summary><b>P</b>, the node-basis potential fill.</summary>
    public double PotentialFillMs { get; set; }

    /// <summary>Cholesky of <b>P</b>, its inverse, and the scatter-add into <b>G</b>.</summary>
    public double ReduceToGMs { get; set; }

    /// <summary>Cholesky of <b>G</b>, its inverse, and the fill of <b>K̃</b>, <b>W</b> and <b>H</b>.</summary>
    public double AssembleKwhMs { get; set; }

    /// <summary>Per-frequency: forming <c>M̃(ω) = −ω²L + K̃ + jωD(ω)</c>.</summary>
    public double MTildeAssembleMs { get; set; }

    /// <summary>Per-frequency: the factorisation of <c>M̃</c>.</summary>
    public double FactorMs { get; set; }

    /// <summary>Per-frequency: the T right-hand-side solves and the <c>Z_port</c> reduction.</summary>
    public double PortSolveMs { get; set; }

    /// <summary>Everything frequency-independent.</summary>
    public double SetupMs => InductanceFillMs + PotentialFillMs + ReduceToGMs + AssembleKwhMs;

    /// <summary>Everything one frequency point costs.</summary>
    public double PerPointMs => MTildeAssembleMs + FactorMs + PortSolveMs;

    /// <summary>Times <paramref name="work"/> and adds the elapsed milliseconds through <paramref name="add"/>.</summary>
    internal static void Time(MomStageTimes? times, Action<MomStageTimes, double> add, Action work)
    {
        if (times is null) { work(); return; }

        var sw = Stopwatch.StartNew();
        work();
        add(times, sw.Elapsed.TotalMilliseconds);
    }

    public override string ToString() =>
        $"L {InductanceFillMs:F1} ms, P {PotentialFillMs:F1} ms, G {ReduceToGMs:F1} ms, " +
        $"K~/W/H {AssembleKwhMs:F1} ms (setup {SetupMs:F1} ms); " +
        $"per point: M~ {MTildeAssembleMs:F2} ms, factor {FactorMs:F2} ms, solves {PortSolveMs:F2} ms " +
        $"({PerPointMs:F2} ms).";
}
