using System.Numerics;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Where a Newton update was still too large to call the solve converged: interface node <paramref name="Node"/> (its
/// index in the solve's unknowns), harmonic or mixing product <paramref name="Column"/>, the update's size and the bound it
/// exceeded, both in volts.
/// </summary>
public sealed record HbUpdateExcess(int Node, int Column, double Size, double Bound);

/// <summary>
/// The HB Newton loops' voltage-update test (brief R-hbv-1; <see cref="AnalysisSettings.HbVRelTol"/> says why it exists).
/// One instance per solve, null when the settings leave it off, so the loops read <c>check?.Excess(…)</c> and a solve
/// without it runs exactly as it did.
///
/// <para>It compares the accepted iterate with the one the step started from, so it judges the update the line search
/// actually applied (λ·ΔV), over whatever columns the solve carries — harmonics, or mixing products. Cost: one pass over
/// N × columns phasors per iteration, nothing beside the factorisation.</para>
/// </summary>
internal sealed class HbUpdateCheck
{
    private readonly double _rel;
    private readonly double _abs;

    private HbUpdateCheck(double rel, double abs) { _rel = rel; _abs = abs; }

    /// <summary>The check <paramref name="settings"/> ask for, or null when they leave it off (both tolerances null or 0).</summary>
    internal static HbUpdateCheck? For(AnalysisSettings settings)
    {
        double rel = Math.Max(0, settings.HbVRelTol ?? 0), abs = Math.Max(0, settings.HbVAbsTol ?? 0);
        return rel > 0 || abs > 0 ? new HbUpdateCheck(rel, abs) : null;
    }

    /// <summary>The worst place the update from <paramref name="from"/> to <paramref name="to"/> exceeds its bound, by the
    /// ratio of size to bound; null when it is within the bound everywhere.</summary>
    internal HbUpdateExcess? Excess(Complex[,] to, Complex[,] from)
    {
        int n = to.GetLength(0), cols = to.GetLength(1);
        HbUpdateExcess? worst = null;
        double worstRatio = 1.0;
        for (int i = 0; i < n; i++)
        {
            double scale = 0;
            for (int k = 0; k < cols; k++) scale = Math.Max(scale, to[i, k].Magnitude);
            double bound = _rel * scale + _abs;
            for (int k = 0; k < cols; k++)
            {
                double d = (to[i, k] - from[i, k]).Magnitude;
                if (!(d > bound)) continue;
                double ratio = bound > 0 ? d / bound : double.PositiveInfinity;
                if (ratio > worstRatio || worst is null) { worstRatio = ratio; worst = new HbUpdateExcess(i, k, d, bound); }
            }
        }
        return worst;
    }
}
