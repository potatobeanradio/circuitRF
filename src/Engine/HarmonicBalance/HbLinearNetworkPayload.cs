using System;
using System.Numerics;
using RfCore.Export;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Implements <see cref="ILinearNetworkPayload"/> and <see cref="IBackSolverProvider"/>
/// by wrapping the retained <see cref="HbLinearBackSolver"/> (which in turn wraps
/// <see cref="HbLinearExtractor"/>).
///
/// This class lives in CircuitRF.Engine (same assembly as the solver/extractor)
/// so it can access their internal members.  The exporter in RfCore consumes it only
/// through the <see cref="ILinearNetworkPayload"/> interface, preventing a circular dep.
/// The <see cref="IBackSolverProvider"/> interface allows the exporter to call
/// <see cref="GetFullSolution"/> for eager linear-interior evaluation without re-doing a
/// sparse solve (the underlying back-solver caches the solution vector).
///
/// Data-export.md §4, §8.3.
/// </summary>
public sealed class HbLinearNetworkPayload : ILinearNetworkPayload, IBackSolverProvider
{
    private readonly HbLinearBackSolver _solver;

    public HbLinearNetworkPayload(HbLinearBackSolver solver)
    {
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
    }

    // ── Dimensions ──────────────────────────────────────────────────────────

    public int SweepCount     => _solver.SweepCount;
    public int HarmonicCount  => _solver.K + 1;          // K+1 (includes DC at index 0)
    public int MnaSize        => _solver.Extractor.MnaSize;
    public int NonGroundCount => _solver.NonGroundCount;
    public int InterfaceCount => _solver.Extractor.InterfaceCount;

    // ── Frequency mapping ────────────────────────────────────────────────────

    public double[] Omegas
    {
        get
        {
            int K1 = HarmonicCount;
            var omegas = new double[K1];
            for (int k = 1; k < K1; k++)
                omegas[k] = 2.0 * Math.PI * k * _solver.F0;
            // omegas[0] = 0.0 (DC)
            return omegas;
        }
    }

    // ── Index↔name maps ──────────────────────────────────────────────────────

    public int[]    InterfaceNodes => _solver.Extractor.InterfaceNodes;
    public string[] NodeNames      => _solver.Extractor.NodeNames;
    public string[] BranchNames    => _solver.Extractor.BranchNames;

    // ── Sparse G matrix ──────────────────────────────────────────────────────

    public (int[] Rows, int[] Cols, Complex[] Data) GetSparseG(int k)
    {
        double omega = k == 0 ? 0.0 : 2.0 * Math.PI * k * _solver.F0;

        // Ensure the cache entry exists — force a SolveFullNetwork call for DC (k=0)
        // if Extract() was never called for omega=0 (the back-solver lazy-populates it).
        if (k == 0)
            EnsureDcCached();

        return _solver.Extractor.GetSparseG(omega);
    }

    // ── Per-(harmonic, sweep) data ────────────────────────────────────────────

    public Complex GetBSrc(int sweepIdx, int k, int mnaIdx)
    {
        var bSrc = _solver.BSrcRaw;
        if (sweepIdx >= bSrc.Length || k >= bSrc[sweepIdx].Length) return Complex.Zero;
        var row = bSrc[sweepIdx][k];
        return mnaIdx < row.Length ? row[mnaIdx] : Complex.Zero;
    }

    public Complex GetINl(int sweepIdx, int interfaceNodeIdx, int k)
    {
        var iNl = _solver.INlRaw;
        if (sweepIdx >= iNl.Length) return Complex.Zero;
        var mat = iNl[sweepIdx];
        if (interfaceNodeIdx >= mat.GetLength(0) || k >= mat.GetLength(1)) return Complex.Zero;
        return mat[interfaceNodeIdx, k];
    }

    // ── IBackSolverProvider ─────────────────────────────────────────────────

    /// <summary>
    /// Full MNA solution vector for harmonic <paramref name="k"/> at sweep index
    /// <paramref name="si"/>, lazy-cached in the underlying <see cref="HbLinearBackSolver"/>.
    /// x[0..NonGroundCount-1] = node voltages; x[NonGroundCount..] = branch currents.
    /// </summary>
    public Complex[] GetFullSolution(int k, int si) => _solver.GetSolution(k, si);

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure the DC (k=0) LU/G entry is in the extractor's cache.
    /// The back-solver's lazy-cache path in SolveFullNetwork does this on first call;
    /// for export we trigger it proactively so GetSparseG(0) never returns empty arrays.
    /// </summary>
    private void EnsureDcCached()
    {
        // Calling GetSolution(0, 0) will populate the _luCache[omega=0] entry if absent.
        if (_solver.SweepCount > 0)
            _solver.GetSolution(0, 0);
    }
}
