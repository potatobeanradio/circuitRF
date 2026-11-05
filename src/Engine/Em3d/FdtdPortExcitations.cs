// brief-cosim-pattern-3 — how to add a frequency's openEMS runs into one excitation of each port. A run's other ports are not exactly terminated in their Z0 as their probes
// see them, so a run's field holds their reflected waves as well as its own port's. FdtdPortTransform takes them out of S by inverting the incident-wave matrix U + Z0·I, and
// this is the same inverse applied to anything else a run produced, so a field added up with these weights is the field S describes.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Em3d;

/// <summary>
/// <see cref="Weights"/>[k, c] is what run k contributes to port c's excitation, which has an incident wave at port c alone and 1 V across it, and
/// <see cref="Current"/>[c] is the current into port c there.
/// </summary>
public sealed record FdtdPortExcitations(Complex[,] Weights, Complex[] Current)
{
    /// <summary>
    /// The excitations from one frequency's U and I, [port, run], in the reference impedances S was formed in; null with <paramref name="weakPort"/> when a port's voltage is
    /// zero in its own excitation, leaving nothing to refer it to. <see cref="FdtdPortTransform.Solve"/> has already refused a singular matrix.
    /// </summary>
    public static FdtdPortExcitations? From(Complex[,] u, Complex[,] i, IReadOnlyList<Complex> z0, out int weakPort)
    {
        weakPort = 0;
        int n = z0.Count;
        var a = new Mat<Complex>(n, n);
        for (int p = 0; p < n; p++)
            for (int k = 0; k < n; k++) a[p, k] = u[p, k] + z0[p] * i[p, k];
        var inverse = a.Inverse();

        var weights = new Complex[n, n];
        var current = new Complex[n];
        for (int c = 0; c < n; c++)
        {
            Complex v = Complex.Zero, j = Complex.Zero;
            for (int k = 0; k < n; k++)
            {
                v += u[c, k] * inverse[k, c];
                j += i[c, k] * inverse[k, c];
            }
            if (!(v.Magnitude > 0)) { weakPort = c; return null; }
            for (int k = 0; k < n; k++) weights[k, c] = inverse[k, c] / v;
            current[c] = j / v;
        }
        return new FdtdPortExcitations(weights, current);
    }
}
