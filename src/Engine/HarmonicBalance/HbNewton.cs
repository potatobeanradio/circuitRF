using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// HB Newton loop — solves ALL harmonics k = 0..K simultaneously (harmonic-balance.md §4, §7, §8).
///
/// DC (k=0) is a FULL Newton participant (not frozen):
///   - Nonlinear devices mix harmonics to DC (even-order: cos²(ωt) = ½ + ½cos2ωt → DC shift).
///   - Freezing k=0 suppresses self-biasing and contaminates the harmonic solution.
///   - The nonlinear-DC solve provides only the INITIAL GUESS; the full Newton iterates.
///
/// Y_{N×N}(0) and I_src(0) for k=0 are the REAL DC admittance and Norton source
/// extracted by HbLinearExtractor.ExtractDC() (Maas 3.10–3.14, ω=0).
/// See HbLinearExtractor.ExtractDC for the singularity handling.
///
/// Unknowns: V[n][k=0..K], real-split form — 2*N*(K+1) total DOF.
/// Maas DC special cases (harmonic-balance.md §7.3, Maas 2003 p.145):
///   i=0 column: Im parts zeroed; at (k=0,i=0) J[1,1] = J[0,0].
///   k=0 row: Im residual is always 0.
/// </summary>
public static class HbNewton
{
    public record SolveResult(bool Converged, int Iterations,
        IReadOnlyList<HbConvergenceTrace.IterRecord> IterTrace,
        Complex[,] INl); // I_nl per [node, harmonic] — caller reads I_nl[n,0] for DC-current trend

    /// <summary>
    /// Run Newton loop. V is modified in-place ([N, K+1] complex).
    /// yNN[k][n,m] for k=0..K; iSrc[k][n] for k=0..K.
    /// k=0 entries are the real DC admittance/Norton source from HbLinearExtractor.ExtractDC.
    /// </summary>
    public static SolveResult Solve(
        Complex[,]         V,              // [N, K+1] — initial guess in, converged out
        Complex[][,]       yNN,            // [K+1][N,N] — k=0 uses virtual admittance
        Complex[][]        iSrc,           // [K+1][N]   — k=0 uses virtual Norton current
        double             f0,
        int                K,
        int                N,
        ElaboratedNetlist  netlist,
        int[]              interfaceNodes,
        int                gridN,
        AnalysisSettings   settings,
        double             tol)
    {
        double omega0  = 2.0 * Math.PI * f0;
        int    unknowns = 2 * N * (K + 1);  // real-split: all harmonics k=0..K
        var    trace   = new List<HbConvergenceTrace.IterRecord>();
        int    maxIter = settings.HbMaxIter;

        Complex[,] iNlLast  = new Complex[N, K + 1]; // last computed I_nl (for reporting)

        for (int iter = 0; iter < maxIter; iter++)
        {
            // ── 1. Time-domain evaluation ─────────────────────────────────────
            var (iNl, qNl, G, C) = EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes);
            iNlLast = iNl;

            // ── 2. Residual F[n, k=0..K] ──────────────────────────────────────
            var F     = BuildF(V, yNN, iSrc, iNl, qNl, N, K, omega0);
            double fN = L2(F);
            trace.Add(new HbConvergenceTrace.IterRecord(iter, fN));

            if (fN < tol)
                return new SolveResult(true, iter + 1, trace, iNlLast);

            // ── 3. Jacobian J (real-split 2×2 blocks, §7.2 + Maas §7.3) ───────
            var J = BuildJ(yNN, G, C, N, K, omega0);

            // ── 4. Dense solve J·ΔV = −F ─────────────────────────────────────
            var negF = new double[unknowns];
            for (int r = 0; r < unknowns; r++) negF[r] = -F[r];
            double[]? dV = SolveGaussian(J, negF, unknowns);
            if (dV is null)
            {
                Console.Error.WriteLine($"[HB] Jacobian singular at iter {iter}, ‖F‖={fN:E3}");
                return new SolveResult(false, iter + 1, trace, iNlLast);
            }

            // ── 5. Update V[k=0..K] += λ·ΔV  (λ=1) ──────────────────────────
            ApplyUpdate(V, dV, N, K);
        }

        // Max iterations.
        var (iNlF, qNlF, _, _) = EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes);
        iNlLast = iNlF;
        var FF = BuildF(V, yNN, iSrc, iNlF, qNlF, N, K, omega0);
        trace.Add(new HbConvergenceTrace.IterRecord(settings.HbMaxIter, L2(FF)));
        return new SolveResult(false, settings.HbMaxIter, trace, iNlLast);
    }

    // ── Time-domain nonlinear evaluation ─────────────────────────────────────

    public static (Complex[,] iNl, Complex[,] qNl, Complex[,,] G, Complex[,,] C)
        EvaluateNonlinear(Complex[,] V, int N, int K, int gridN,
            ElaboratedNetlist netlist, int[] interfaceNodes)
    {
        var vTime = new double[N][];
        for (int n = 0; n < N; n++)
        {
            vTime[n] = new double[gridN];
            var Xn = new Complex[K + 1];
            for (int k = 0; k <= K; k++) Xn[k] = V[n, k];
            HbFft.Inverse(Xn, K, vTime[n]);
        }

        var iTime  = new double[N, gridN];
        var qTime  = new double[N, gridN];
        int Kj     = Math.Min(2 * K, gridN / 2);
        var dgTime = new double[N, N, gridN];
        var dcTime = new double[N, N, gridN];

        foreach (var nlIdx in netlist.NonlinearComponents)
        {
            var ec        = netlist.Components[nlIdx];
            int portCount = ec.Model.PortCount;
            var portV     = new double[portCount];
            var portPlusIdx  = new int[portCount];
            var portMinusIdx = new int[portCount];

            for (int p = 0; p < portCount; p++)
            {
                int np = ec.Nodes.Length > 2*p   ? ec.Nodes[2*p]   : 0;
                int nm = ec.Nodes.Length > 2*p+1 ? ec.Nodes[2*p+1] : 0;
                portPlusIdx[p]  = Array.IndexOf(interfaceNodes, np);
                portMinusIdx[p] = Array.IndexOf(interfaceNodes, nm);
            }

            for (int t = 0; t < gridN; t++)
            {
                for (int p = 0; p < portCount; p++)
                {
                    double vp = portPlusIdx[p]  >= 0 ? vTime[portPlusIdx[p]][t]  : 0.0;
                    double vm = portMinusIdx[p] >= 0 ? vTime[portMinusIdx[p]][t] : 0.0;
                    portV[p] = vp - vm;
                }
                var res = ec.Model.Evaluate(new PortVoltages(portV));

                for (int p = 0; p < portCount; p++)
                {
                    int iPlus = portPlusIdx[p];
                    if (iPlus < 0) continue;
                    iTime[iPlus, t] += res.I[p];
                    qTime[iPlus, t] += res.Q[p];
                    for (int q = 0; q < portCount; q++)
                    {
                        int jPlus = portPlusIdx[q];
                        if (jPlus < 0) continue;
                        dgTime[iPlus, jPlus, t] += res.Dg[p, q];
                        dcTime[iPlus, jPlus, t] += res.Dc[p, q];
                    }
                }
            }
        }

        var iNl = new Complex[N, K + 1];
        var qNl = new Complex[N, K + 1];
        var G   = new Complex[N, N, Kj + 1];
        var C   = new Complex[N, N, Kj + 1];

        for (int n = 0; n < N; n++)
        {
            var iX = new double[gridN]; for (int t=0;t<gridN;t++) iX[t]=iTime[n,t];
            HbFft.Forward(iX, K, out var iAmpl, out _);
            for (int k=0;k<=K;k++) iNl[n,k] = iAmpl[k];

            var qX = new double[gridN]; for (int t=0;t<gridN;t++) qX[t]=qTime[n,t];
            HbFft.Forward(qX, K, out var qAmpl, out _);
            for (int k=0;k<=K;k++) qNl[n,k] = qAmpl[k];
        }
        for (int n = 0; n < N; n++)
        for (int m = 0; m < N; m++)
        {
            var dgX = new double[gridN]; for (int t=0;t<gridN;t++) dgX[t]=dgTime[n,m,t];
            HbFft.Forward(dgX, Kj, out var gAmpl, out _);
            for (int k=0;k<=Kj;k++) G[n,m,k] = gAmpl[k];

            var dcX = new double[gridN]; for (int t=0;t<gridN;t++) dcX[t]=dcTime[n,m,t];
            HbFft.Forward(dcX, Kj, out var cAmpl, out _);
            for (int k=0;k<=Kj;k++) C[n,m,k] = cAmpl[k];
        }
        return (iNl, qNl, G, C);
    }

    // ── Residual F[n, k=0..K] (real-split) ───────────────────────────────────

    private static double[] BuildF(Complex[,] V, Complex[][,] yNN, Complex[][] iSrc,
        Complex[,] iNl, Complex[,] qNl, int N, int K, double omega0)
    {
        int dof = 2 * N * (K + 1);
        var F = new double[dof];

        for (int n = 0; n < N; n++)
        for (int k = 0; k <= K; k++)
        {
            int rRe = Idx(n, k, false, N, K);
            int rIm = Idx(n, k, true,  N, K);

            Complex f = iSrc[k][n];
            var     yk = yNN[k];
            for (int m = 0; m < N; m++) f += yk[n, m] * V[m, k];
            f += iNl[n, k];
            if (k > 0) f += new Complex(0, k * omega0) * qNl[n, k];

            F[rRe] = f.Real;
            // DC (k=0): Im part of F is always 0 (real signal — Maas §7.3).
            if (k > 0) F[rIm] = f.Imaginary;
        }
        return F;
    }

    // ── Jacobian (real 2×2 blocks, §7.2 + Maas §7.3) ─────────────────────────

    private static double[] BuildJ(Complex[][,] yNN, Complex[,,] G, Complex[,,] C,
        int N, int K, double omega0)
    {
        int dof = 2 * N * (K + 1);
        var J = new double[dof * dof];

        for (int n = 0; n < N; n++)
        for (int k = 0; k <= K; k++)
        for (int m = 0; m < N; m++)
        for (int i = 0; i <= K; i++)
        {
            int rk0 = Idx(n, k, false, N, K);
            int rk1 = Idx(n, k, true,  N, K);
            int ci0 = Idx(m, i, false, N, K);
            int ci1 = Idx(m, i, true,  N, K);

            Complex Gkmi = SafeGet(G, n, m, k - i);
            Complex Gkpi = SafeGet(G, n, m, k + i);
            double a00 =  Gkmi.Real + Gkpi.Real;
            double a01 = -Gkmi.Imaginary + Gkpi.Imaginary;
            double a10 =  Gkmi.Imaginary + Gkpi.Imaginary;
            double a11 =  Gkmi.Real - Gkpi.Real;

            Complex Ckmi = SafeGet(C, n, m, k - i);
            Complex Ckpi = SafeGet(C, n, m, k + i);
            double cb00 =  Ckmi.Real + Ckpi.Real;
            double cb01 = -Ckmi.Imaginary + Ckpi.Imaginary;
            double cb10 =  Ckmi.Imaginary + Ckpi.Imaginary;
            double cb11 =  Ckmi.Real - Ckpi.Real;
            double kw   =  k * omega0;
            a00 += -kw * cb10;  a01 += -kw * cb11;
            a10 +=  kw * cb00;  a11 +=  kw * cb01;

            if (k == i && n < yNN[k].GetLength(0) && m < yNN[k].GetLength(1))
            {
                Complex y = yNN[k][n, m];
                a00 +=  y.Real;       a01 += -y.Imaginary;
                a10 +=  y.Imaginary;  a11 +=  y.Real;
            }

            // ── Maas DC special cases (§7.3) ─────────────────────────────────
            // i=0 column: Im DOF of V[m,0] is fictitious (DC is real-only).
            if (i == 0) { a01 = 0; a11 = (k == 0 ? a00 : 0); }
            // k=0 row: Im part of the DC residual is always 0.
            if (k == 0) { a10 = 0; a11 = (i == 0 ? a00 : 0); }

            if (rk0 >= 0 && ci0 >= 0 && rk0 < dof && ci0 < dof)
                J[rk0*dof+ci0] += a00;
            if (rk0 >= 0 && ci1 >= 0 && rk0 < dof && ci1 < dof)
                J[rk0*dof+ci1] += a01;
            if (rk1 >= 0 && ci0 >= 0 && rk1 < dof && ci0 < dof)
                J[rk1*dof+ci0] += a10;
            if (rk1 >= 0 && ci1 >= 0 && rk1 < dof && ci1 < dof)
                J[rk1*dof+ci1] += a11;
        }
        return J;
    }

    // ── Index helpers ─────────────────────────────────────────────────────────

    // Real-split index for (node n, harmonic k, Re/Im). k=0..K, all included.
    private static int Idx(int n, int k, bool isIm, int N, int K)
        => (k < 0 || k > K) ? -1 : 2 * (n * (K + 1) + k) + (isIm ? 1 : 0);

    private static Complex SafeGet(Complex[,,] arr, int n, int m, int kk)
    {
        bool conj = kk < 0;
        if (conj) kk = -kk;
        int maxK = arr.GetLength(2) - 1;
        if (kk > maxK) return Complex.Zero;
        var v = arr[n, m, kk];
        return conj ? Complex.Conjugate(v) : v;
    }

    private static void ApplyUpdate(Complex[,] V, double[] dV, int N, int K)
    {
        int len = dV.Length;
        for (int n = 0; n < N; n++)
        for (int k = 0; k <= K; k++)
        {
            int rRe = Idx(n, k, false, N, K);
            int rIm = Idx(n, k, true,  N, K);
            double dRe = rRe >= 0 && rRe < len ? dV[rRe] : 0.0;
            // DC Im is always zero (real signal); Newton update at Im is meaningless.
            double dIm = (k > 0 && rIm >= 0 && rIm < len) ? dV[rIm] : 0.0;
            V[n, k] += new Complex(dRe, dIm);
        }
    }

    // ── Gaussian elimination with partial pivoting ─────────────────────────────

    private static double[]? SolveGaussian(double[] A, double[] b, int n)
    {
        var aug = new double[n * (n + 1)];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++) aug[r*(n+1)+c] = A[r*n+c];
            aug[r*(n+1)+n] = b[r];
        }
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Math.Abs(aug[col*(n+1)+col]);
            for (int row = col+1; row < n; row++)
            {
                double v = Math.Abs(aug[row*(n+1)+col]);
                if (v > best) { best = v; pivot = row; }
            }
            if (best < 1e-30) return null;
            if (pivot != col)
                for (int j = 0; j <= n; j++)
                    (aug[col*(n+1)+j], aug[pivot*(n+1)+j]) = (aug[pivot*(n+1)+j], aug[col*(n+1)+j]);
            double diag = aug[col*(n+1)+col];
            for (int j = col; j <= n; j++) aug[col*(n+1)+j] /= diag;
            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double factor = aug[row*(n+1)+col];
                for (int j = col; j <= n; j++) aug[row*(n+1)+j] -= factor * aug[col*(n+1)+j];
            }
        }
        var x = new double[n];
        for (int r = 0; r < n; r++) x[r] = aug[r*(n+1)+n];
        return x;
    }

    private static double L2(double[] v) { double s = 0; foreach (double x in v) s += x*x; return Math.Sqrt(s); }
}
