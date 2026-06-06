using System.Numerics;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Abstraction over the linear-network back-solver for lazy reconstruction of interior
/// node voltages and branch currents after HB convergence (Correction 1).
/// Defined in Core so MeasurementContext can reference it; implemented in Engine.
/// </summary>
public interface ILinearBackSolver
{
    /// <summary>
    /// Resolve a node name to its 1-based circuit node number.
    /// Returns false if not found or if the node is ground (node 0).
    /// </summary>
    bool TryGetNodeNumber(string name, out int circNode);

    /// <summary>
    /// Voltage at 1-based circuit node <paramref name="circNode"/> at harmonic k,
    /// sweep index si (lazy + cached; back-solves the full linear MNA on demand).
    /// </summary>
    Complex GetNodeVoltage(int circNode, int harmonicK, int sweepIdx);

    /// <summary>Number of sweep points (1 if no sweep was performed).</summary>
    int SweepCount { get; }

    /// <summary>
    /// Number of non-ground nodes. Node voltages occupy x[0..NonGroundCount-1] in the
    /// full solution vector returned by GetSolution.
    /// </summary>
    int NonGroundCount { get; }
}
