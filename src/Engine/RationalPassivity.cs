// ================================================================
//  RationalPassivity.cs  —  is a rational S matrix passive everywhere, and the
//  smallest change of its residues that makes it so (docs/design/transient.md
//  §5.4; brief-trn-4 R-trn4-4)
//
//  A fitted S(s) = D + Σ R_p/(s − a_p) can exceed σmax = 1 between or beyond the
//  samples it was fitted to. An envelope run reads it around harmonics only; a
//  transient run reads it from DC up, and a block that makes energy anywhere grows
//  without bound. So σmax(S(jω)) is evaluated on a grid ten times denser than the
//  samples from DC to the top of the band, a logarithmic tail above it, every
//  pole's own frequency and s → ∞ (where S = D).
//
//  Where σmax > 1 the residues and D are moved, the poles kept. To first order a
//  change ΔS moves σmax by Re(uᴴ ΔS v), u and v its singular vectors, so each local
//  peak above 1 is one linear equation in the residues. Among all the changes that
//  bring every peak to just under 1, the one taken is the smallest change of the
//  RESPONSE over the fitted samples (not of the coefficients): a weighted
//  least-norm problem whose weight is the basis functions' Gram matrix there.
//  Linearisation is not exact, so the check and the step repeat until the grid is
//  passive or the iterations run out.
// ================================================================

using System.Numerics;
using CircuitRF.Engine.Modeling;

namespace CircuitRF.Engine;

/// <summary>What <see cref="RationalPassivity.Enforce"/> did: σmax on the check grid before and after, the change as a
/// relative RMS of S over the fitted samples (0 when nothing was moved), and whether the result is passive.</summary>
public sealed record PassivityReport(double SigmaBefore, double SigmaAfter, double Perturbation, int Iterations, bool Passive, double WorstHz);

public static class RationalPassivity
{
    /// <summary>σmax above 1 by more than this is a violation (round-off in the singular value is ~1e-15).</summary>
    public const double Slack = 1e-9;

    /// <summary>Each peak is brought to 1 − this, so the next linearisation starts inside.</summary>
    private const double Margin = 1e-6;

    private const int MaxIterations = 40;

    /// <summary>Constraints spread across one violating band, beside its peak.</summary>
    private const int BandPoints = 24;

    /// <summary>
    /// The frequencies σmax is checked at for samples <paramref name="sampleHz"/> and a band up to <paramref name="topHz"/>:
    /// a uniform grid from DC to the top at least ten times denser than the samples (at least 2,001 points), a logarithmic
    /// tail to 100× the top, and each pole's own frequency.
    /// </summary>
    public static double[] Grid(RationalModel m, IReadOnlyList<double> sampleHz, double topHz)
    {
        double top = Math.Max(topHz, sampleHz.Count > 0 ? sampleHz.Max() : 0);
        int n = Math.Clamp(10 * sampleHz.Count, 2001, 40001);
        var g = new List<double>(n + 250);
        for (int i = 0; i < n; i++) g.Add(top * i / (n - 1));
        for (int i = 1; i <= 200; i++) g.Add(top * Math.Pow(100, i / 200.0));
        foreach (var p in m.Poles) if (p.Imaginary > 0) g.Add(p.Imaginary / (2 * Math.PI));
        g.Sort();
        return [.. g];
    }

    /// <summary>σmax of the <paramref name="ports"/>-port model on <paramref name="grid"/> and at s → ∞, with where it is largest
    /// (+∞ for s → ∞).</summary>
    public static (double Sigma, double AtHz) SigmaMax(RationalModel m, int ports, IReadOnlyList<double> grid)
    {
        double worst = Sigma(Infinity(m, ports)).S, at = double.PositiveInfinity;
        foreach (double hz in grid)
        {
            double s = Sigma(At(m, ports, hz)).S;
            if (s > worst) { worst = s; at = hz; }
        }
        return (worst, at);
    }

    /// <summary>
    /// <paramref name="m"/> made passive on <paramref name="grid"/> by the smallest change of its response over
    /// <paramref name="sampleHz"/>: the model unchanged when it is already passive.
    /// </summary>
    public static (RationalModel Model, PassivityReport Report) Enforce(RationalModel m, int ports, IReadOnlyList<double> sampleHz,
                                                                        IReadOnlyList<double> grid)
    {
        var (before, beforeAt) = SigmaMax(m, ports, grid);
        if (before <= 1 + Slack) return (m, new PassivityReport(before, before, 0, 0, true, beforeAt));

        var basis = new Basis(m);
        int K = basis.Count, R = ports * ports;
        // The Gram matrix of the basis over the samples: ‖ΔS_m‖² = pᵀ M p for each response's parameters p.
        var M = new double[K, K];
        foreach (double hz in sampleHz)
        {
            var phi = basis.At(new Complex(0, 2 * Math.PI * hz));
            for (int a = 0; a < K; a++)
                for (int b = 0; b < K; b++) M[a, b] += (Complex.Conjugate(phi[a]) * phi[b]).Real;
        }
        double tr = 0;
        for (int a = 0; a < K; a++) tr += M[a, a];
        for (int a = 0; a < K; a++) M[a, a] += 1e-12 * tr / K + 1e-300;
        // a basis the samples cannot tell apart leaves nothing to move: reported as not passive, which the caller refuses
        if (Invert(M) is not { } Minv) return (m, new PassivityReport(before, before, 0, 0, false, beforeAt));

        var cur = m;
        int it = 0;
        double sigma = before, at = beforeAt;
        for (; it < MaxIterations && sigma > 1 + Slack; it++)
        {
            // Each run of grid points above 1 − Margin/2 is a violating band: its peak, and points spread evenly across it (a
            // plateau held at one peak only would rise beside it), are the constraints; s → ∞ too when D is not inside.
            var peaks = new List<(Complex S, Complex[] U, Complex[] V, double Sigma)>();
            var vals = grid.Select(hz => Sigma(At(cur, ports, hz))).ToArray();
            for (int i = 0; i < vals.Length; i++)
            {
                if (vals[i].S <= 1 - Margin / 2) continue;
                int j = i;
                while (j + 1 < vals.Length && vals[j + 1].S > 1 - Margin / 2) j++;
                int top = i;
                for (int k = i; k <= j; k++) if (vals[k].S > vals[top].S) top = k;
                var take = new SortedSet<int> { top };
                int span = j - i + 1, spread = Math.Min(span, BandPoints);
                for (int q = 0; q < spread; q++) take.Add(i + (int)Math.Round((span - 1) * (spread == 1 ? 0.5 : q / (double)(spread - 1))));
                foreach (int k in take) peaks.Add((new Complex(0, 2 * Math.PI * grid[k]), vals[k].U, vals[k].V, vals[k].S));
                i = j;
            }
            var inf = Sigma(Infinity(cur, ports));
            bool infRow = inf.S > 1 - Margin / 2;
            int C = peaks.Count + (infRow ? 1 : 0);
            if (C == 0) break;
            // A[c, r, k]: the first-order change of peak c's σ per unit of response r's parameter k
            var A = new double[C, R, K];
            var b = new double[C];
            for (int c = 0; c < C; c++)
            {
                bool isInf = c == peaks.Count;
                var (u, v, s) = isInf ? (inf.U, inf.V, inf.S) : (peaks[c].U, peaks[c].V, peaks[c].Sigma);
                var phi = isInf ? basis.AtInfinity() : basis.At(peaks[c].S);
                for (int i = 0; i < ports; i++)
                    for (int j = 0; j < ports; j++)
                    {
                        var w = Complex.Conjugate(u[i]) * v[j];
                        for (int k = 0; k < K; k++) A[c, i * ports + j, k] = (w * phi[k]).Real;
                    }
                b[c] = (1 - Margin) - s;
            }
            // p_r = Minv A_rᵀ λ, with (Σ_r A_r Minv A_rᵀ) λ = b
            var G = new double[C, C];
            var MA = new double[R, K, C];     // Minv A_rᵀ
            for (int r = 0; r < R; r++)
                for (int k = 0; k < K; k++)
                    for (int c = 0; c < C; c++)
                    {
                        double sum = 0;
                        for (int l = 0; l < K; l++) sum += Minv[k, l] * A[c, r, l];
                        MA[r, k, c] = sum;
                    }
            for (int c1 = 0; c1 < C; c1++)
                for (int c2 = 0; c2 < C; c2++)
                {
                    double sum = 0;
                    for (int r = 0; r < R; r++)
                        for (int k = 0; k < K; k++) sum += A[c1, r, k] * MA[r, k, c2];
                    G[c1, c2] = sum;
                }
            for (int c = 0; c < C; c++) G[c, c] += 1e-14 * Math.Max(1e-300, G[c, c]);
            var lambda = Solve(G, b);
            if (lambda is null) break;
            var p = new double[R][];
            for (int r = 0; r < R; r++)
            {
                p[r] = new double[K];
                for (int k = 0; k < K; k++)
                {
                    double sum = 0;
                    for (int c = 0; c < C; c++) sum += MA[r, k, c] * lambda[c];
                    p[r][k] = sum;
                }
            }
            cur = basis.Apply(cur, p);
            (sigma, at) = SigmaMax(cur, ports, grid);
        }

        // the change, relative RMS of S over the samples
        double d2 = 0, s2 = 0;
        foreach (double hz in sampleHz)
            for (int r = 0; r < R; r++)
            {
                var a0 = m.EvaluateAtHz(r, hz);
                var a1 = cur.EvaluateAtHz(r, hz);
                d2 += (a1 - a0).Magnitude * (a1 - a0).Magnitude;
                s2 += a0.Magnitude * a0.Magnitude;
            }
        double pert = s2 > 0 ? Math.Sqrt(d2 / s2) : Math.Sqrt(d2);
        return (cur, new PassivityReport(before, sigma, pert, it, sigma <= 1 + Slack, at));
    }

    // ── the S matrix and its largest singular value ─────────────────────

    private static Complex[,] At(RationalModel m, int n, double hz)
    {
        var s = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) s[i, j] = m.EvaluateAtHz(i * n + j, hz);
        return s;
    }

    private static Complex[,] Infinity(RationalModel m, int n)
    {
        var s = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) s[i, j] = m.D[i * n + j];
        return s;
    }

    /// <summary>σmax with its singular vectors (S v = σ u): the dominant eigenvector of SᴴS by power iteration.</summary>
    internal static (double S, Complex[] U, Complex[] V) Sigma(Complex[,] s)
    {
        int n = s.GetLength(0);
        if (n == 1)
        {
            double a = s[0, 0].Magnitude;
            return (a, [a > 0 ? s[0, 0] / a : Complex.One], [Complex.One]);
        }
        if (n == 2)
        {
            // SᴴS = [[p, q], [q*, r]]: its larger eigenvalue and eigenvector in closed form
            double p = (s[0, 0] * Complex.Conjugate(s[0, 0]) + s[1, 0] * Complex.Conjugate(s[1, 0])).Real;
            double r = (s[0, 1] * Complex.Conjugate(s[0, 1]) + s[1, 1] * Complex.Conjugate(s[1, 1])).Real;
            Complex q = Complex.Conjugate(s[0, 0]) * s[0, 1] + Complex.Conjugate(s[1, 0]) * s[1, 1];
            double ev = 0.5 * (p + r) + Math.Sqrt(0.25 * (p - r) * (p - r) + q.Magnitude * q.Magnitude);
            Complex v0, v1;
            if (q.Magnitude > 1e-300 * (p + r + 1e-300)) { v0 = q; v1 = ev - p; }
            else if (p >= r) { v0 = 1; v1 = 0; }
            else { v0 = 0; v1 = 1; }
            double nv = Math.Sqrt(v0.Magnitude * v0.Magnitude + v1.Magnitude * v1.Magnitude);
            v0 /= nv; v1 /= nv;
            Complex u0 = s[0, 0] * v0 + s[0, 1] * v1, u1 = s[1, 0] * v0 + s[1, 1] * v1;
            double sg = Math.Sqrt(u0.Magnitude * u0.Magnitude + u1.Magnitude * u1.Magnitude);
            if (sg > 0) { u0 /= sg; u1 /= sg; } else u0 = 1;
            return (sg, [u0, u1], [v0, v1]);
        }
        var h = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex sum = 0;
                for (int k = 0; k < n; k++) sum += Complex.Conjugate(s[k, i]) * s[k, j];
                h[i, j] = sum;
            }
        var v = new Complex[n];
        // start from the column of SᴴS with the largest norm, so a dominant direction is never orthogonal to the start
        int best = 0; double bn = -1;
        for (int j = 0; j < n; j++) { double c = 0; for (int i = 0; i < n; i++) c += h[i, j].Magnitude; if (c > bn) { bn = c; best = j; } }
        for (int i = 0; i < n; i++) v[i] = h[i, best] + (i == best ? 1e-3 : 0);
        double lam = 0;
        for (int it = 0; it < 500; it++)
        {
            var w = new Complex[n];
            for (int i = 0; i < n; i++) { Complex sum = 0; for (int j = 0; j < n; j++) sum += h[i, j] * v[j]; w[i] = sum; }
            double norm = Math.Sqrt(w.Sum(z => z.Magnitude * z.Magnitude));
            if (!(norm > 0)) break;
            for (int i = 0; i < n; i++) w[i] /= norm;
            double change = 0;
            for (int i = 0; i < n; i++) change = Math.Max(change, (w[i] - v[i]).Magnitude);
            v = w;
            if (Math.Abs(norm - lam) <= 1e-15 * norm && change < 1e-12) { lam = norm; break; }
            lam = norm;
        }
        var u = new Complex[n];
        for (int i = 0; i < n; i++) { Complex sum = 0; for (int j = 0; j < n; j++) sum += s[i, j] * v[j]; u[i] = sum; }
        double sig = Math.Sqrt(u.Sum(z => z.Magnitude * z.Magnitude));
        if (sig > 0) for (int i = 0; i < n; i++) u[i] /= sig;
        else u[0] = 1;
        return (sig, u, v);
    }

    // ── the residues as real parameters ─────────────────────────────────

    /// <summary>A response's real parameters: one per real pole (its residue), two per conjugate pair (the real and imaginary
    /// parts of the upper member's residue), then D.</summary>
    private sealed class Basis
    {
        private readonly List<(int Pole, bool Pair)> _groups = [];
        private readonly Complex[] _poles;
        public Basis(RationalModel m)
        {
            _poles = m.Poles;
            for (int k = 0; k < m.Poles.Length; k++)
            {
                bool pair = m.Poles[k].Imaginary != 0;
                _groups.Add((k, pair));
                if (pair) k++;
            }
            Count = _groups.Sum(g => g.Pair ? 2 : 1) + 1;
        }

        public int Count { get; }

        public Complex[] At(Complex s)
        {
            var phi = new Complex[Count];
            int k = 0;
            foreach (var (p, pair) in _groups)
            {
                var a = _poles[p];
                if (!pair) { phi[k++] = 1 / (s - a); continue; }
                var z1 = 1 / (s - a); var z2 = 1 / (s - Complex.Conjugate(a));
                phi[k++] = z1 + z2;
                phi[k++] = Complex.ImaginaryOne * (z1 - z2);
            }
            phi[k] = 1;
            return phi;
        }

        public Complex[] AtInfinity()
        {
            var phi = new Complex[Count];
            phi[^1] = 1;
            return phi;
        }

        public RationalModel Apply(RationalModel m, double[][] p)
        {
            var res = m.Residues.Select(r => (Complex[])r.Clone()).ToArray();
            var d = (double[])m.D.Clone();
            for (int r = 0; r < res.Length; r++)
            {
                int k = 0;
                foreach (var (pole, pair) in _groups)
                {
                    if (!pair) { res[r][pole] += p[r][k++]; continue; }
                    var dr = new Complex(p[r][k], p[r][k + 1]);
                    k += 2;
                    res[r][pole] += dr;
                    res[r][pole + 1] += Complex.Conjugate(dr);
                }
                d[r] += p[r][k];
            }
            return new RationalModel { Poles = m.Poles, Residues = res, D = d, E = m.E };
        }
    }

    // ── small dense linear algebra ──────────────────────────────────────

    private static double[,]? Invert(double[,] a)
    {
        int n = a.GetLength(0);
        var inv = new double[n, n];
        for (int j = 0; j < n; j++)
        {
            var e = new double[n]; e[j] = 1;
            var x = Solve(a, e);
            if (x is null) return null;
            for (int i = 0; i < n; i++) inv[i, j] = x[i];
        }
        return inv;
    }

    private static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c; double best = Math.Abs(m[c, c]);
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > best) { best = Math.Abs(m[r, c]); p = r; }
            if (best == 0 || !double.IsFinite(best)) return null;
            if (p != c)
            {
                for (int j = 0; j < n; j++) (m[c, j], m[p, j]) = (m[p, j], m[c, j]);
                (x[c], x[p]) = (x[p], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                if (f == 0) continue;
                for (int j = c; j < n; j++) m[r, j] -= f * m[c, j];
                x[r] -= f * x[c];
            }
        }
        for (int c = n - 1; c >= 0; c--)
        {
            double s = x[c];
            for (int j = c + 1; j < n; j++) s -= m[c, j] * x[j];
            x[c] = s / m[c, c];
        }
        return x;
    }
}
