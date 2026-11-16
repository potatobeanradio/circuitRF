// ================================================================
//  DelayInterpolation.cs  —  the delayed wave between samples, shared by the
//  envelope's line delay and the transient's lines (docs/design/transient.md
//  §4.7, §5.3; brief-trn-4 R-trn4-1)
//
//  A line's delayed wave is read between the samples a run has stored. On a
//  uniform grid the envelope reads it with quadratic Lagrange on three
//  successive samples (passive: |L| <= 1 to Nyquist, measured; cubic is not).
//  The transient's accepted steps are not uniform, so it reads the same
//  quadratic through the three nodes' own times; on equal spacing the two are
//  the same polynomial.
// ================================================================

namespace CircuitRF.Engine;

public static class DelayInterpolation
{
    /// <summary>Quadratic Lagrange weights on nodes 0, 1, 2 (unit spacing) at <paramref name="d"/> (value) and their
    /// d/dx — the envelope's form, moved here unchanged.</summary>
    public static void Lagrange(double d, double[] l, double[] ld)
    {
        l[0] = (d - 1) * (d - 2) / 2; l[1] = -d * (d - 2); l[2] = d * (d - 1) / 2;
        ld[0] = (2 * d - 3) / 2;      ld[1] = -(2 * d - 2);  ld[2] = (2 * d - 1) / 2;
    }

    /// <summary>Quadratic Lagrange weights at <paramref name="t"/> on three distinct node times.</summary>
    public static (double W0, double W1, double W2) Quadratic(double t, double t0, double t1, double t2) =>
        ((t - t1) * (t - t2) / ((t0 - t1) * (t0 - t2)),
         (t - t0) * (t - t2) / ((t1 - t0) * (t1 - t2)),
         (t - t0) * (t - t1) / ((t2 - t0) * (t2 - t1)));
}
