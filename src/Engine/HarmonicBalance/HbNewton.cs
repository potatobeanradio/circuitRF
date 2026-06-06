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
        double             tol,
        double             lambda         = 1.0,   // B2: Newton step size λ ∈ (0,1]
        int                guardHarmonic  = 0)      // B3: guard harmonic index (0=off)
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
            var J = BuildJ(yNN, G, C, N, K, omega0, guardHarmonic);

            // ── 4. Dense solve J·ΔV = −F ─────────────────────────────────────
            var negF = new double[unknowns];
            for (int r = 0; r < unknowns; r++) negF[r] = -F[r];
            double[]? dV = SolveGaussian(J, negF, unknowns);
            if (dV is null)
            {
                Console.Error.WriteLine($"[HB] Jacobian singular at iter {iter}, ‖F‖={fN:E3}");
                return new SolveResult(false, iter + 1, trace, iNlLast);
            }

            // ── 5. Update V[k=0..K] += λ·ΔV ─────────────────────────────────
            ApplyUpdate(V, dV, N, K, lambda);
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

    public static double[] BuildF(Complex[,] V, Complex[][,] yNN, Complex[][] iSrc,
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

    public static double[] BuildJ(Complex[][,] yNN, Complex[,,] G, Complex[,,] C,
        int N, int K, double omega0, int guardHarmonic = 0)
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

            // ── Amplitude-convention per-term weights ─────────────────────────────
            // HbFft: v(t)=V_DC+Σ Re{Vₖ e^{jkωt}}, so ∂v/∂Re(Vᵢ)=cos(iωt).
            // Maas uses half-amplitude (∂v/∂Re(Vᵢ)=2·cos(iωt)) → each AC term is 2× large.
            // DC bin (j=0) is normalized ÷N; AC bins normalized ÷(N/2) → DC-bin term in an
            // AC (k≥1) row contributes weight 1, not 0.5. So weights are:
            //   G[j=0] in k≥1 row: weight 1.0   (DC bin in AC row: no extra halving)
            //   G[j≠0] in k≥1 row: weight 0.5   (half-amplitude correction)
            //   Any G[j]  in k=0 row: × 0.5 additionally (DC row uses ÷N, not ÷(N/2))
            double wKmi = ConversionWeight(k, k - i);
            double wKpi = ConversionWeight(k, k + i);

            Complex Gkmi = SafeGet(G, n, m, k - i);
            Complex Gkpi = SafeGet(G, n, m, k + i);
            double a00 =  wKmi *  Gkmi.Real      + wKpi *  Gkpi.Real;
            double a01 = -wKmi *  Gkmi.Imaginary + wKpi *  Gkpi.Imaginary;
            double a10 =  wKmi *  Gkmi.Imaginary + wKpi *  Gkpi.Imaginary;
            double a11 =  wKmi *  Gkmi.Real      - wKpi *  Gkpi.Real;

            Complex Ckmi = SafeGet(C, n, m, k - i);
            Complex Ckpi = SafeGet(C, n, m, k + i);
            double cb00 =  wKmi *  Ckmi.Real      + wKpi *  Ckpi.Real;
            double cb01 = -wKmi *  Ckmi.Imaginary + wKpi *  Ckpi.Imaginary;
            double cb10 =  wKmi *  Ckmi.Imaginary + wKpi *  Ckpi.Imaginary;
            double cb11 =  wKmi *  Ckmi.Real      - wKpi *  Ckpi.Real;
            double kw   =  k * omega0;
            a00 += -kw * cb10;  a01 += -kw * cb11;
            a10 +=  kw * cb00;  a11 +=  kw * cb01;

            // ── B3: Guard harmonic — hard cutoff above guardHarmonic index ────────
            // Applied to G/C only (not Y_NN) and only to J, never to F.
            // Attenuates the Newton update for high harmonics, improving convergence
            // of stiff Class-F/F⁻¹ circuits (per harmonic-balance.md §12.1).
            if (guardHarmonic > 0 && (k > guardHarmonic || i > guardHarmonic))
                a00 = a01 = a10 = a11 = 0.0;

            // ── Linear interface admittance (frequency-domain — no convention scaling) ──
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

    // ── Finite-difference Jacobian comparison (PASS A diagnostic) ─────────────

    /// <summary>One entry in the top-discrepancy list.</summary>
    public record JacobianElement(
        int Row, int Col,
        int RowNode, int RowHarm, bool RowIsIm,
        int ColNode, int ColHarm, bool ColIsIm,
        string BlockDesc,
        double AnalyticVal, double FdVal, double AbsError, double RelError);

    /// <summary>Result of the analytic-vs-FD Jacobian comparison.</summary>
    public record JacobianComparisonResult(
        double MaxAbsError,
        double MaxRelError,
        int MaxAbsRow, int MaxAbsCol,
        int MaxRelRow, int MaxRelCol,   // location of max relative error
        int Dof, int N, int K,
        IReadOnlyList<JacobianElement> TopDiscrepancies,
        // DC Im dummy elements (Im-F[n,0] / Im-V[m,0]) are intentionally set to a00
        // per Maas §7.3 to prevent Jacobian singularity — FD gives 0, analytic gives a00.
        // These N×N entries are excluded from MaxAbsError / MaxRelError.
        int    DcDummyCount,
        double DcDummyMaxAbsError);

    /// <summary>
    /// Compare BuildJ (analytic) to a central-difference Jacobian of BuildF.
    /// The FD Jacobian is the trusted oracle (owner's MATLAB practice).
    /// </summary>
    public static JacobianComparisonResult CompareJacobianNumerical(
        Complex[,] V,
        Complex[][,] yNN,
        Complex[][] iSrc,
        double f0, int K, int N,
        ElaboratedNetlist netlist, int[] interfaceNodes, int gridN)
    {
        double omega0 = 2.0 * Math.PI * f0;
        int dof = 2 * N * (K + 1);

        // ── Analytic Jacobian at V ────────────────────────────────────────────
        var (iNl0, qNl0, G0, C0) = EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes);
        double[] analyticJ = BuildJ(yNN, G0, C0, N, K, omega0);

        // ── FD Jacobian: perturb each real DOF j ──────────────────────────────
        double[] fdJ = new double[dof * dof];
        for (int j = 0; j < dof; j++)
        {
            // Decode DOF j → (node, harmonic, isIm)
            bool jIsIm = (j & 1) == 1;
            int tmp   = j >> 1;
            int jNode = tmp / (K + 1);
            int jHarm = tmp % (K + 1);

            double nomVal = jIsIm ? V[jNode, jHarm].Imaginary : V[jNode, jHarm].Real;
            // ε choice: central-diff truncation is O(ε²·J'''); FP cancellation is O(ε_m·|F|/ε).
            // 1e-6 works well — the per-row domFloor (below) ensures near-zero elements in
            // high-Y rows don't corrupt the relative-error statistic.
            double eps    = 1e-6 * Math.Max(Math.Abs(nomVal), 1.0);

            // V+
            var Vp = (Complex[,])V.Clone();
            Vp[jNode, jHarm] += jIsIm ? new Complex(0, eps) : new Complex(eps, 0);
            var (iNlp, qNlp, _, _) = EvaluateNonlinear(Vp, N, K, gridN, netlist, interfaceNodes);
            double[] Fp = BuildF(Vp, yNN, iSrc, iNlp, qNlp, N, K, omega0);

            // V-
            var Vm = (Complex[,])V.Clone();
            Vm[jNode, jHarm] += jIsIm ? new Complex(0, -eps) : new Complex(-eps, 0);
            var (iNlm, qNlm, _, _) = EvaluateNonlinear(Vm, N, K, gridN, netlist, interfaceNodes);
            double[] Fm = BuildF(Vm, yNN, iSrc, iNlm, qNlm, N, K, omega0);

            for (int r = 0; r < dof; r++)
                fdJ[r * dof + j] = (Fp[r] - Fm[r]) / (2.0 * eps);
        }

        // ── Scale for relative error ──────────────────────────────────────────
        // Global floor = globalScale × 1e-8: elements below this are treated as zero.
        // This is intentionally coarse — near-zero elements in high-Y rows (k=2,5 with
        // ZLoad≈1µΩ → Y≈1e6 S) have dom clamped to ≥0.01, so their FD-truncation error
        // (≈2.8e-8 for J'''≈1.7e5) contributes ≤2.8e-6 relative — at the FD oracle limit.
        // The SDD model's large J''' (up to ~1e7) makes sub-ppm FD accuracy impossible
        // for near-zero elements; the 1e-5 assertion gate is set accordingly.
        double globalScale = 0;
        for (int i = 0; i < dof * dof; i++)
            globalScale = Math.Max(globalScale, Math.Max(Math.Abs(analyticJ[i]), Math.Abs(fdJ[i])));

        // ── Comparison ────────────────────────────────────────────────────────
        double maxAbsErr = 0, maxRelErr = 0;
        int maxAbsRow = 0, maxAbsCol = 0;
        int maxRelRow = 0, maxRelCol = 0;
        int    dcDummyCount = 0;
        double dcDummyMaxAbsErr = 0;
        var discrepancies = new List<JacobianElement>();

        for (int r = 0; r < dof; r++)
        for (int c = 0; c < dof; c++)
        {
            double an     = analyticJ[r * dof + c];
            double fd     = fdJ[r * dof + c];
            double absErr = Math.Abs(an - fd);

            // DC Im dummy elements: Im-F[n,0] row AND Im-V[m,0] col.
            // Analytic sets a11=a00 per Maas §7.3 (prevents singularity); FD=0.
            // Excluded from the main comparison — reported separately.
            if (IsDcImDof(r, K) && IsDcImDof(c, K))
            {
                dcDummyCount++;
                if (absErr > dcDummyMaxAbsErr) dcDummyMaxAbsErr = absErr;
                continue;
            }

            double domFloor = Math.Max(globalScale * 1e-8, 1e-12);
            double dom    = Math.Max(Math.Max(Math.Abs(an), Math.Abs(fd)), domFloor);
            double relErr = absErr / dom;

            if (absErr > maxAbsErr) { maxAbsErr = absErr; maxAbsRow = r; maxAbsCol = c; }
            if (relErr > maxRelErr) { maxRelErr = relErr; maxRelRow = r; maxRelCol = c; }

            // Collect elements with meaningful relative error (>0.1%) above the noise floor.
            if (relErr > 1e-3 && dom > domFloor)
            {
                bool rIsIm = (r & 1) == 1; int rTmp = r >> 1;
                int rNode = rTmp / (K + 1); int rHarm = rTmp % (K + 1);
                bool cIsIm = (c & 1) == 1; int cTmp = c >> 1;
                int cNode = cTmp / (K + 1); int cHarm = cTmp % (K + 1);
                string block = DescribeBlock(rHarm, cHarm, K);
                discrepancies.Add(new JacobianElement(r, c, rNode, rHarm, rIsIm,
                    cNode, cHarm, cIsIm, block, an, fd, absErr, relErr));
            }
        }

        discrepancies.Sort((a, b) => b.AbsError.CompareTo(a.AbsError));
        if (discrepancies.Count > 20) discrepancies = discrepancies.GetRange(0, 20);

        return new JacobianComparisonResult(maxAbsErr, maxRelErr, maxAbsRow, maxAbsCol,
            maxRelRow, maxRelCol, dof, N, K, discrepancies, dcDummyCount, dcDummyMaxAbsErr);
    }

    // Per-term G/C weight: accounts for HbFft amplitude convention vs Maas.
    // w = (j==0 ? 1.0 : 0.5) × (kRow==0 ? 0.5 : 1.0).
    // Derivation: rawFFT_g[j] / (2·scaleK) where scaleK=N for DC rows, N/2 for AC rows,
    // and rawFFT_g[0]=G[0]·N vs rawFFT_g[j≥1]=G[j]·(N/2).
    private static double ConversionWeight(int kRow, int j)
        => (j == 0 ? 1.0 : 0.5) * (kRow == 0 ? 0.5 : 1.0);

    // DC Im DOF: Im(V[n,0]) or Im(F[n,0]) — odd index j with harm=0.
    private static bool IsDcImDof(int j, int K)
        => (j & 1) == 1 && (j >> 1) % (K + 1) == 0;

    private static string DescribeBlock(int k, int i, int K)
    {
        // k = row harmonic, i = col harmonic; maps onto the 2×2 sub-block structure.
        var sb = new System.Text.StringBuilder();
        int diff = k - i; int sum = k + i;
        sb.Append($"k={k} i={i} → G[{diff}]+G[{sum}]");
        if (k > 0) sb.Append($" +C×{k}ω");
        if (k == i) sb.Append(" +Y");
        if (k == 0) sb.Append(" [DC-row]");
        if (i == 0) sb.Append(" [DC-col]");
        if (k == 0 && i == 0) sb.Append(" [DC-diag]");
        return sb.ToString();
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

    private static void ApplyUpdate(Complex[,] V, double[] dV, int N, int K,
        double lambda = 1.0)
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
            V[n, k] += new Complex(lambda * dRe, lambda * dIm);
        }
    }

    // ── Gaussian elimination with partial pivoting ─────────────────────────────

    internal static double[]? SolveGaussian(double[] A, double[] b, int n)
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

    internal static double L2(double[] v) { double s = 0; foreach (double x in v) s += x*x; return Math.Sqrt(s); }
}
