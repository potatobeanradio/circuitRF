namespace CircuitRF.WBond.Tests;

/// <summary>
/// A direct numerical evaluation of the Neumann double integral — the independent oracle for
/// <see cref="Grover"/> at general angle.
///
/// <code>
/// M = (μ₀/4π) ∫₀ˡ ∫₀ᵐ (dl₁ · dl₂)/R = (μ₀/4π)·cos ε·∫₀ˡ ∫₀ᵐ dt ds / R(t,s)
/// </code>
///
/// <para><b>Why this exists rather than "the closed form agrees with itself".</b> The skew formula's
/// Ω term can be misplaced relative to the <c>2·cos ε</c> factor in a way that is <b>invisible as
/// ε → 0</b> — both placements agree to 9 digits there. Only an oracle that is independent of the
/// closed form, evaluated at a genuinely skew angle, separates them. That mistake was made and this
/// oracle is what caught it (see <see cref="GroverOracleTests"/> tier 1b).</para>
///
/// <para>Gauss–Legendre, because the integrand is smooth and non-singular whenever the filaments do
/// not touch. At n = 64 per dimension it is exact to ~1e-12 for the separations used here, at 4,096
/// evaluations per call — fast enough for the routine test tier.</para>
/// </summary>
internal static class NeumannOracle
{
    public static double Mutual(in Filament p, in Filament q, int n = 64)
    {
        double cosEps = p.Ux * q.Ux + p.Uy * q.Uy + p.Uz * q.Uz;
        if (cosEps == 0.0) return 0.0;

        var (nodes, weights) = GaussLegendre(n);

        // Map the canonical [-1,1] nodes onto [0,l] and [0,m].
        double halfL = p.Length / 2.0, halfM = q.Length / 2.0;

        double acc = 0.0;
        for (int i = 0; i < n; i++)
        {
            double t = halfL * (nodes[i] + 1.0);
            double px = p.Ax + t * p.Ux;
            double py = p.Ay + t * p.Uy;
            double pz = p.Az + t * p.Uz;
            double wi = weights[i];

            for (int j = 0; j < n; j++)
            {
                double s = halfM * (nodes[j] + 1.0);
                double dx = px - (q.Ax + s * q.Ux);
                double dy = py - (q.Ay + s * q.Uy);
                double dz = pz - (q.Az + s * q.Uz);
                acc += wi * weights[j] / Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        return Grover.Mu0Over4Pi * cosEps * acc * halfL * halfM;
    }

    /// <summary>Gauss–Legendre nodes and weights on [-1, 1], by Newton iteration on Pₙ.</summary>
    private static (double[] Nodes, double[] Weights) GaussLegendre(int n)
    {
        var nodes = new double[n];
        var weights = new double[n];

        for (int i = 0; i < n; i++)
        {
            // Chebyshev starting guess, then Newton on the Legendre polynomial.
            double x = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));

            for (int iter = 0; iter < 100; iter++)
            {
                // Three-term recurrence for P_n(x) and its derivative.
                double p0 = 1.0, p1 = 0.0;
                for (int k = 0; k < n; k++)
                {
                    double p2 = p1;
                    p1 = p0;
                    p0 = ((2.0 * k + 1.0) * x * p1 - k * p2) / (k + 1.0);
                }

                double dp = n * (x * p0 - p1) / (x * x - 1.0);
                double dx = -p0 / dp;
                x += dx;
                if (Math.Abs(dx) < 1e-16) break;
            }

            // Recompute P_n and P_{n-1} at the converged root for the weight.
            double q0 = 1.0, q1 = 0.0;
            for (int k = 0; k < n; k++)
            {
                double q2 = q1;
                q1 = q0;
                q0 = ((2.0 * k + 1.0) * x * q1 - k * q2) / (k + 1.0);
            }
            double derivative = n * (x * q0 - q1) / (x * x - 1.0);

            nodes[i] = x;
            weights[i] = 2.0 / ((1.0 - x * x) * derivative * derivative);
        }

        return (nodes, weights);
    }
}
