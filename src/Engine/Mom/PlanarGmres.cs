// M5 — the iterative solver an accelerated operator needs, because it has no matrix to factor.
//
// This is the production form of the harness `MomIterativeSolverDecisionTests` used to answer gate 11,
// kept deliberately close to it so the numbers recorded in `src/Engine/Mom/CLAUDE.md` §11 describe the
// code that ships rather than a rewrite of it. Two choices carried over verbatim:
//
//   RIGHT preconditioning, so the Arnoldi residual IS the true ‖b − Ax‖ and the tolerance means what
//   it says. Left preconditioning reports ‖M⁻¹(b − Ax)‖, which flatters a strong preconditioner for
//   free and would let a bad near field look converged.
//
//   FULL GMRES by default. §11 measured 3 → 6 iterations to 1e-6 over 6.7× N with an 8-cell near
//   field; there is nothing to restart at those counts, and restarting can only converge more slowly.
//   The restart length is a knob for the case a future structure needs one, not a default.
//
// The complex Givens rotation is the one place this is easy to get subtly wrong. For [a; b] → [d; 0]
// with the matrix [c s; −conj(s) conj(c)], the choice is c = conj(a)/d and s = conj(b)/d; the
// right-hand side update must read the OLD g[j] before overwriting it. A slip here does not fail — it
// converges to a slightly different vector, which is the expensive kind of bug.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Right-preconditioned GMRES over an arbitrary matrix-vector product. Knows nothing about the planar
/// kernel — it takes two delegates and a right-hand side.
/// </summary>
public static class PlanarGmres
{
    /// <summary>
    /// Solves <c>A x = b</c> to a relative residual of <paramref name="tolerance"/>.
    /// </summary>
    /// <param name="multiply">The operator. Called once per iteration.</param>
    /// <param name="preconditioner">
    /// <c>M⁻¹</c>, applied on the RIGHT. Pass the identity for none — §11 measured what that costs
    /// (129 → 341 iterations over the same span), so "no preconditioner" is a supported, measured
    /// configuration rather than a broken one.
    /// </param>
    /// <param name="restart">Restart length; 0 or negative is full GMRES.</param>
    /// <param name="iterations">Total operator applications performed, across all restart cycles.</param>
    /// <param name="residual">The final TRUE relative residual — recomputed from <c>b − Ax</c> at the
    /// end of each cycle rather than taken from the Arnoldi recurrence, because the two drift apart
    /// once the Krylov basis loses orthogonality and the recurrence is the optimistic one.</param>
    public static Complex[] Solve(Func<Complex[], Complex[]> multiply,
                                  Func<Complex[], Complex[]> preconditioner,
                                  Complex[] b, double tolerance, int maxIterations, int restart,
                                  out int iterations, out double residual)
    {
        ArgumentNullException.ThrowIfNull(multiply);
        ArgumentNullException.ThrowIfNull(preconditioner);
        ArgumentNullException.ThrowIfNull(b);

        int n = b.Length;
        var x = new Complex[n];
        double bNorm = Norm(b);

        iterations = 0;
        if (bNorm == 0) { residual = 0.0; return x; }

        int cycle = restart > 0 ? Math.Min(restart, maxIterations) : maxIterations;
        residual = 1.0;

        while (iterations < maxIterations && residual > tolerance)
        {
            // r = b − A x, and the cycle's own Krylov space is built on it.
            var ax = iterations == 0 ? new Complex[n] : multiply(x);
            var r  = new Complex[n];
            for (int i = 0; i < n; i++) r[i] = b[i] - ax[i];

            double beta = Norm(r);
            residual = beta / bNorm;
            if (residual <= tolerance) break;

            int budget = Math.Min(cycle, maxIterations - iterations);
            if (budget <= 0) break;

            var v  = new List<Complex[]> { Scale(r, 1.0 / beta) };
            var z  = new List<Complex[]>();                       // the preconditioned basis vectors
            var h  = new Complex[budget + 2, budget + 1];
            var cs = new Complex[budget + 1];
            var sn = new Complex[budget + 1];
            var g  = new Complex[budget + 2];
            g[0] = beta;

            int k = 0;
            for (int j = 0; j < budget; j++)
            {
                var zj = preconditioner(v[j]);
                z.Add(zj);
                var w = multiply(zj);
                iterations++;

                for (int i = 0; i <= j; i++)                      // modified Gram-Schmidt
                {
                    h[i, j] = Dot(v[i], w);
                    Axpy(w, v[i], -h[i, j]);
                }
                double hNext = Norm(w);
                h[j + 1, j] = hNext;

                for (int i = 0; i < j; i++)                       // previous rotations
                {
                    Complex t = cs[i] * h[i, j] + sn[i] * h[i + 1, j];
                    h[i + 1, j] = -Complex.Conjugate(sn[i]) * h[i, j]
                                + Complex.Conjugate(cs[i]) * h[i + 1, j];
                    h[i, j] = t;
                }

                Complex aa = h[j, j], bb = h[j + 1, j];
                double d = Math.Sqrt(aa.Magnitude * aa.Magnitude + bb.Magnitude * bb.Magnitude);
                if (d == 0) { k = j; break; }
                cs[j] = Complex.Conjugate(aa) / d;
                sn[j] = Complex.Conjugate(bb) / d;
                h[j, j]     = cs[j] * aa + sn[j] * bb;
                h[j + 1, j] = Complex.Zero;

                g[j + 1] = -Complex.Conjugate(sn[j]) * g[j];      // from the OLD g[j] — order matters
                g[j]     =  cs[j] * g[j];

                k = j + 1;
                double est = g[j + 1].Magnitude / bNorm;
                if (est <= tolerance || est <= 1e-15) break;
                if (hNext <= 1e-300) break;                        // lucky breakdown: the space is exact
                v.Add(Scale(w, 1.0 / hNext));
            }

            if (k == 0) break;

            // Back-substitute the triangular least-squares problem and update x through M⁻¹.
            var yy = new Complex[k];
            for (int i = k - 1; i >= 0; i--)
            {
                Complex acc = g[i];
                for (int jj = i + 1; jj < k; jj++) acc -= h[i, jj] * yy[jj];
                yy[i] = h[i, i] == Complex.Zero ? Complex.Zero : acc / h[i, i];
            }
            for (int i = 0; i < k; i++) Axpy(x, z[i], yy[i]);

            var ax2 = multiply(x);
            double rr = 0;
            for (int i = 0; i < n; i++)
            {
                Complex e = b[i] - ax2[i];
                rr += e.Real * e.Real + e.Imaginary * e.Imaginary;
            }
            residual = Math.Sqrt(rr) / bNorm;
        }

        return x;
    }

    /// <summary>The identity preconditioner — "none", spelled so a caller does not have to write a
    /// lambda that looks like it might be doing something.</summary>
    public static Complex[] NoPreconditioner(Complex[] v) => v;

    private static Complex Dot(Complex[] a, Complex[] b)
    {
        double re = 0, im = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var t = a[i]; var u = b[i];
            re += t.Real * u.Real + t.Imaginary * u.Imaginary;      // conj(a) · b
            im += t.Real * u.Imaginary - t.Imaginary * u.Real;
        }
        return new Complex(re, im);
    }

    private static double Norm(Complex[] a)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i].Real * a[i].Real + a[i].Imaginary * a[i].Imaginary;
        return Math.Sqrt(s);
    }

    private static Complex[] Scale(Complex[] a, double f)
    {
        var r = new Complex[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] * f;
        return r;
    }

    private static void Axpy(Complex[] y, Complex[] x, Complex alpha)
    {
        for (int i = 0; i < y.Length; i++) y[i] += alpha * x[i];
    }
}
