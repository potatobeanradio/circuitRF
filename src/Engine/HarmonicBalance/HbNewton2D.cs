using System.Numerics;
using FftFlat;
using CircuitRF.Core;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Two-tone HB Newton blocks — the 2-D mixing-lattice generalization of <see cref="HbNewton"/>
/// (harmonic-balance.md §6–§7). This is the SAME engine: the scalar harmonic axis k=0..K becomes
/// the tone-pair mixIndex axis (k₁,k₂) enumerated by <see cref="MixingGrid"/>, the 1-D FFT becomes
/// the separable 2-D FFT (<see cref="HbFft2D"/>), and the k−i / k+i difference/sum frequencies
/// become the VECTOR (k₁−i₁,k₂−i₂) / (k₁+i₁,k₂+i₂), looked up in the rectangular FFT spectrum.
///
/// The real 2×2 block structure and its (post-convergence-fix) phasor-convention scaling are
/// UNCHANGED from <see cref="HbNewton"/> — the per-axis weight is <see cref="HbFft2D.ConversionWeight2D"/>,
/// which reduces to the single-tone <c>ConversionWeight</c> when k₂≡0. The FD-Jacobian test
/// (<c>CompareJacobianNumerical2D</c>) is the oracle that the index/frequency arithmetic is right.
///
/// Single-tone is the NumFreqs=1 case and stays on <see cref="HbNewton"/> (its golden is frozen);
/// this class is the multi-tone path and is exercised only when MixingGrid is 2-D.
///
/// Unknowns: V[n, mix] for n=0..N-1 nonlinear-facing nodes, mix=0..M-1 retained mixing products,
/// real-split → 2·N·M DOF. mix=0 is the (0,0) DC index (real-only, Maas §7.3 special cases).
/// </summary>
public static class HbNewton2D
{
    /// <param name="PortITime">
    /// Per-device, per-port terminal current over the 2-D time grid — <c>[deviceOrdinal][port][t1, t2]</c>
    /// — from the LAST device evaluation of this solve, which is the one at the returned <c>V</c>.
    /// <see cref="ComputeDevicePortCurrents2D"/> takes it instead of re-evaluating every device at
    /// every sample for numbers already in hand (HB-P2 M3). This path has no control-current form,
    /// so there is no case where the re-evaluation is the different — and correct — answer.
    /// </param>
    public record SolveResult(bool Converged, int Iterations,
        IReadOnlyList<HbConvergenceTrace.IterRecord> IterTrace,
        Complex[,] INl,
        double[][][,]? PortITime = null);  // I_nl[node, mixIdx] at the returned point

    // ── Newton loop ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Two-tone HB Newton loop — the 2-D analogue of <see cref="HbNewton.Solve"/>. Iterates the
    /// real-split system J·ΔV = −F over the mixIndex axis until ‖F‖₂ &lt; tol or HbMaxIter.
    /// V is modified in place ([N, M]). yNN[mix]/iSrc[mix] are the per-mixing-product linear
    /// interface and Norton source (mix=0 = (0,0) DC uses the real DC admittance/source).
    /// </summary>
    public static SolveResult Solve(
        Complex[,]         V,                 // [N, M] — initial guess in, converged out
        Complex[][,]       yNN,              // [M][N,N]
        Complex[][]        iSrc,             // [M][N]
        MixingGrid         grid,
        double             f1,
        double             f2,
        int                N,
        int                N1,
        int                N2,
        ElaboratedNetlist  netlist,
        int[]              interfaceNodes,
        AnalysisSettings   settings,
        double             tol,
        double             lambda     = 1.0,
        int                guardOrder = 0)
    {
        int M        = grid.MixCount;
        int unknowns = 2 * N * M;
        int maxIter  = settings.HbMaxIter;
        var trace    = new List<HbConvergenceTrace.IterRecord>();
        Complex[,] iNlLast = new Complex[N, M];

        // One buffer for the whole solve, overwritten per pass, so the last pass leaves its
        // per-port terminal currents behind for the post-solve extraction (M3).
        var portITime = AllocPortITime(netlist, N1, N2);

        // The line search's scratch iterate, and the one evaluation that precedes the loop — the
        // identical transcription of HbNewton.Solve's structure (HB-P3 M1); see HbNewton.Backtrack
        // for why the three loops need it and why a converging solve is unchanged by it.
        var V0 = new Complex[N, M];

        var (iNl, qNl, G, C) = EvaluateNonlinear2D(V, grid, N, N1, N2, netlist, interfaceNodes, portITime);
        iNlLast   = iNl;
        var    F  = BuildF2D(V, yNN, iSrc, iNl, qNl, grid, N, f1, f2);
        double fN = HbNewton.L2(F);

        for (int iter = 0; iter < maxIter; iter++)
        {
            if (fN < tol)
            {
                trace.Add(new HbConvergenceTrace.IterRecord(iter, fN));
                return new SolveResult(true, iter + 1, trace, iNlLast, portITime);
            }

            var J = BuildJ2D(yNN, G, C, grid, N, f1, f2, guardOrder);

            var negF = new double[unknowns];
            for (int r = 0; r < unknowns; r++) negF[r] = -F[r];
            double[]? dV = HbNewton.SolveGaussian(J, negF, unknowns);
            if (dV is null)
            {
                trace.Add(new HbConvergenceTrace.IterRecord(iter, fN));
                Console.Error.WriteLine($"[HB2D] Jacobian singular at iter {iter}, ‖F‖={fN:E3}");
                return new SolveResult(false, iter + 1, trace, iNlLast, portITime);
            }

            double fEntry = fN;
            Array.Copy(V, V0, V.Length);
            var step = HbNewton.Backtrack(fEntry, lambda, lam =>
            {
                Array.Copy(V0, V, V.Length);
                ApplyUpdate2D(V, dV, N, M, lam);
                (iNl, qNl, G, C) = EvaluateNonlinear2D(V, grid, N, N1, N2, netlist, interfaceNodes, portITime);
                F = BuildF2D(V, yNN, iSrc, iNl, qNl, grid, N, f1, f2);
                return HbNewton.L2(F);
            });
            iNlLast = iNl;
            fN      = step.Residual;
            trace.Add(new HbConvergenceTrace.IterRecord(
                iter, fEntry, step.Lambda, step.Backtracks, step.Stalled));
        }

        trace.Add(new HbConvergenceTrace.IterRecord(maxIter, fN));
        return new SolveResult(false, maxIter, trace, iNlLast, portITime);
    }

    private static void ApplyUpdate2D(Complex[,] V, double[] dV, int N, int M, double lambda)
    {
        int len = dV.Length;
        for (int n = 0; n < N; n++)
        for (int mix = 0; mix < M; mix++)
        {
            int rRe = Idx(n, mix, false, M);
            int rIm = Idx(n, mix, true,  M);
            double dRe = rRe < len ? dV[rRe] : 0.0;
            // (0,0) DC Im is fictitious (real signal); no update there.
            double dIm = (mix != 0 && rIm < len) ? dV[rIm] : 0.0;
            V[n, mix] += new Complex(lambda * dRe, lambda * dIm);
        }
    }

    // ── Time-domain nonlinear evaluation (2-D) ───────────────────────────────────

    /// <summary>
    /// Evaluate the nonlinear devices on the rectangular N₁×N₂ grid and return:
    ///   iNl[n,mix], qNl[n,mix]   — nonlinear current/charge phasors at the retained mixing products
    ///   G[n·N+m], C[n·N+m]       — the FULL (non-folded) rectangular convention spectra of the
    ///                              time-domain dg/dc waveforms, indexed [N₁, N₂/2+1] and read via
    ///                              <see cref="HbFft2D.SpecGet"/> at arbitrary (dk₁,dk₂).
    ///
    /// The G/C spectra are NOT folded (unlike <see cref="HbFft2D.Forward2D"/>): the k₂=0 negative-k₁
    /// conjugate bins are retained so SpecGet can reconstruct every difference/sum lookup the
    /// Jacobian needs. On the retained half-plane this convention agrees bin-for-bin with Forward2D.
    /// </summary>
    public static (Complex[,] iNl, Complex[,] qNl, Complex[][,] G, Complex[][,] C)
        EvaluateNonlinear2D(Complex[,] V, MixingGrid grid, int N, int N1, int N2,
            ElaboratedNetlist netlist, int[] interfaceNodes, double[][][,]? portITime = null)
    {
        int M = grid.MixCount;

        // 1. V[n,mix] → real time-domain v(φ₁,φ₂) per node (DC forced real, as HbFft.Inverse does).
        var vTime = new double[N][,];
        var diamond = new Complex[M];
        for (int n = 0; n < N; n++)
        {
            for (int m = 0; m < M; m++)
                diamond[m] = (m == 0) ? new Complex(V[n, 0].Real, 0) : V[n, m];
            vTime[n] = new double[N1, N2];
            HbFft2D.Inverse2D(grid, diamond, N1, N2, vTime[n]);
        }

        var iTime  = new double[N][,];
        var qTime  = new double[N][,];
        for (int n = 0; n < N; n++) { iTime[n] = new double[N1, N2]; qTime[n] = new double[N1, N2]; }
        var dgTime = new double[N * N][,];
        var dcTime = new double[N * N][,];
        for (int idx = 0; idx < N * N; idx++) { dgTime[idx] = new double[N1, N2]; dcTime[idx] = new double[N1, N2]; }

        for (int devOrd = 0; devOrd < netlist.NonlinearComponents.Count; devOrd++)
        {
            int nlIdx     = netlist.NonlinearComponents[devOrd];
            var ec        = netlist.Components[nlIdx];
            int portCount = ec.Model.PortCount;
            var portV     = new double[portCount];
            var portPlusIdx  = new int[portCount];
            var portMinusIdx = new int[portCount];

            var devPortI = portITime is not null && devOrd < portITime.Length ? portITime[devOrd] : null;

            for (int p = 0; p < portCount; p++)
            {
                int np = ec.Nodes.Length > 2*p   ? ec.Nodes[2*p]   : 0;
                int nm = ec.Nodes.Length > 2*p+1 ? ec.Nodes[2*p+1] : 0;
                portPlusIdx[p]  = Array.IndexOf(interfaceNodes, np);
                portMinusIdx[p] = Array.IndexOf(interfaceNodes, nm);
            }

            // HB-P4 M2 — the two-tone grid is N1·N2 samples (1,024 at the shipping mesh), which is
            // where the per-sample device evaluation dominates a Newton iteration most heavily.
            int S2 = N1 * N2;
            GridResult? devGrid = null;
            HbGridSampler? sampler = null;
            if (ec.PrefersGridEvaluate)
            {
                var pv = HbGridBuffers.PortVBuffer(S2, portCount);
                for (int p = 0; p < portCount; p++)
                {
                    int ip = portPlusIdx[p], im = portMinusIdx[p];
                    int bse = p * S2;
                    for (int t1 = 0; t1 < N1; t1++)
                    for (int t2 = 0; t2 < N2; t2++)
                        pv[bse + t1 * N2 + t2] =
                            (ip >= 0 ? vTime[ip][t1, t2] : 0.0) - (im >= 0 ? vTime[im][t1, t2] : 0.0);
                }
                devGrid = HbGridBuffers.Result(devOrd);
                ec.EvaluateGrid(pv.AsSpan(0, portCount * S2), [], S2, devGrid);
                sampler = HbGridBuffers.Sampler();
            }

            for (int t1 = 0; t1 < N1; t1++)
            for (int t2 = 0; t2 < N2; t2++)
            {
                NonlinearResult res;
                if (devGrid is not null)
                    res = sampler!.Sample(devGrid, t1 * N2 + t2);
                else
                {
                    for (int p = 0; p < portCount; p++)
                    {
                        double vp = portPlusIdx[p]  >= 0 ? vTime[portPlusIdx[p]][t1, t2]  : 0.0;
                        double vm = portMinusIdx[p] >= 0 ? vTime[portMinusIdx[p]][t1, t2] : 0.0;
                        portV[p] = vp - vm;
                    }
                    res = ec.Evaluate(new PortVoltages(portV));
                }

                for (int p = 0; p < portCount; p++)
                {
                    if (devPortI is not null) devPortI[p][t1, t2] = res.I[p];
                    PortAdd(iTime, portPlusIdx[p], portMinusIdx[p], t1, t2, res.I[p]);
                    PortAdd(qTime, portPlusIdx[p], portMinusIdx[p], t1, t2, res.Q[p]);
                    for (int q = 0; q < portCount; q++)
                    {
                        PortAdd4(dgTime, N, portPlusIdx[p], portMinusIdx[p],
                                 portPlusIdx[q], portMinusIdx[q], t1, t2, res.Dg[p, q]);
                        PortAdd4(dcTime, N, portPlusIdx[p], portMinusIdx[p],
                                 portPlusIdx[q], portMinusIdx[q], t1, t2, res.Dc[p, q]);
                    }
                }
            }
        }

        // 2. Forward-transform to spectra. iNl/qNl: extract retained reps via SpecGet. G/C: keep full.
        var iNl = new Complex[N, M];
        var qNl = new Complex[N, M];
        for (int n = 0; n < N; n++)
        {
            var iSpec = ForwardConv2D(iTime[n], N1, N2);
            var qSpec = ForwardConv2D(qTime[n], N1, N2);
            for (int m = 0; m < M; m++)
            {
                var (k1, k2) = grid.ToneOf(m);
                iNl[n, m] = HbFft2D.SpecGet(iSpec, k1, k2);
                qNl[n, m] = HbFft2D.SpecGet(qSpec, k1, k2);
            }
        }

        var G = new Complex[N * N][,];
        var C = new Complex[N * N][,];
        for (int idx = 0; idx < N * N; idx++)
        {
            G[idx] = ForwardConv2D(dgTime[idx], N1, N2);
            C[idx] = ForwardConv2D(dcTime[idx], N1, N2);
        }
        return (iNl, qNl, G, C);
    }

    /// <summary>
    /// Non-folded 2-D forward (full-amplitude convention) used for the G/C and i/q spectra.
    /// Same row-real-FFT + column-complex-FFT composition as <see cref="HbFft2D.Forward2D"/> and the
    /// same divisor (N₁N₂ at the global DC bin, else N₁N₂/2), but WITHOUT folding out the k₂=0
    /// negative-k₁ conjugates — those are needed for the Jacobian's difference/sum lookups.
    /// </summary>
    private static Complex[,] ForwardConv2D(double[,] x, int N1, int N2)
    {
        int kMax2 = N2 / 2;
        var partial = new Complex[N1, kMax2 + 1];

        var rft2   = new RealFourierTransform(N2);
        var rowBuf = new double[N2 + 2];
        for (int i1 = 0; i1 < N1; i1++)
        {
            for (int i2 = 0; i2 < N2; i2++) rowBuf[i2] = x[i1, i2];
            for (int i2 = N2; i2 < rowBuf.Length; i2++) rowBuf[i2] = 0;
            var raw2 = rft2.Forward(rowBuf);
            for (int k2 = 0; k2 <= kMax2; k2++) partial[i1, k2] = raw2[k2];
        }

        var spec = new Complex[N1, kMax2 + 1];
        var cft1 = new FastFourierTransform(N1);
        var col  = new Complex[N1];
        for (int k2 = 0; k2 <= kMax2; k2++)
        {
            for (int i1 = 0; i1 < N1; i1++) col[i1] = partial[i1, k2];
            cft1.Forward(col);
            for (int k1 = 0; k1 < N1; k1++) spec[k1, k2] = col[k1];
        }

        for (int k1 = 0; k1 < N1; k1++)
            for (int k2 = 0; k2 <= kMax2; k2++)
                spec[k1, k2] /= Divisor(k1, k2, N1, N2);

        return spec;  // NOT folded — k₂=0 negative-k₁ conjugates preserved for SpecGet.
    }

    private static double Divisor(int k1, int k2, int N1, int N2)
        => (k1 == 0 && k2 == 0) ? (double)N1 * N2 : (double)N1 * N2 / 2.0;

    // ── Residual F[n, mix] (real-split) ──────────────────────────────────────────

    /// <summary>
    /// Two-tone residual: F[n,mix] = iSrc[mix][n] + Σ_m yNN[mix][n,m]·V[m,mix] + iNl[n,mix]
    ///                              + j·ω(mix)·qNl[n,mix].
    /// ω(mix) = 2π(k₁f₁ + k₂f₂) (signed). DC (mix=0 = (0,0)): Im part of F is forced to 0.
    /// </summary>
    public static double[] BuildF2D(Complex[,] V, Complex[][,] yNN, Complex[][] iSrc,
        Complex[,] iNl, Complex[,] qNl, MixingGrid grid, int N, double f1, double f2)
    {
        int M   = grid.MixCount;
        int dof = 2 * N * M;
        var F   = new double[dof];

        for (int n = 0; n < N; n++)
        for (int mix = 0; mix < M; mix++)
        {
            int rRe = Idx(n, mix, false, M);
            int rIm = Idx(n, mix, true,  M);

            Complex f  = iSrc[mix][n];
            var     yk = yNN[mix];
            for (int m = 0; m < N; m++) f += yk[n, m] * V[m, mix];
            f += iNl[n, mix];

            double omegaMix = OmegaOf(grid, mix, f1, f2);
            if (mix != 0) f += new Complex(0, omegaMix) * qNl[n, mix];

            F[rRe] = f.Real;
            // DC (mix=0 = (0,0)): Im part of F is always 0 (real signal — Maas §7.3).
            if (mix != 0) F[rIm] = f.Imaginary;
        }
        return F;
    }

    // ── Jacobian (real 2×2 blocks over the 2-D lattice, §7.2/§7.4 + Maas §7.3) ─────

    /// <summary>
    /// Two-tone analytic Jacobian. For each (row node n, row mix (k₁,k₂)) × (col node m, col mix
    /// (i₁,i₂)) the conductive block is wD·G[(k₁−i₁,k₂−i₂)] + wS·G[(k₁+i₁,k₂+i₂)] with per-axis
    /// weights from <see cref="HbFft2D.ConversionWeight2D"/>; the charge block adds the same
    /// structure rotated by j·ω(row mix). Y_NN sits on the mix==mix diagonal; the (0,0) DC index
    /// gets the Maas §7.3 real-only special cases.
    /// </summary>
    public static double[] BuildJ2D(Complex[][,] yNN, Complex[][,] G, Complex[][,] C,
        MixingGrid grid, int N, double f1, double f2, int guardOrder = 0)
    {
        int M   = grid.MixCount;
        int dof = 2 * N * M;
        var J   = new double[dof * dof];

        for (int n = 0; n < N; n++)
        for (int kMix = 0; kMix < M; kMix++)
        {
            var (k1, k2) = grid.ToneOf(kMix);
            double omegaK = OmegaOf(grid, kMix, f1, f2);

            for (int m = 0; m < N; m++)
            for (int iMix = 0; iMix < M; iMix++)
            {
                var (i1, i2) = grid.ToneOf(iMix);

                int rk0 = Idx(n, kMix, false, M);
                int rk1 = Idx(n, kMix, true,  M);
                int ci0 = Idx(m, iMix, false, M);
                int ci1 = Idx(m, iMix, true,  M);

                // Vector difference/sum mixing indices.
                int d1 = k1 - i1, d2 = k2 - i2;
                int s1 = k1 + i1, s2 = k2 + i2;

                double wD = HbFft2D.ConversionWeight2D(k1, k2, d1, d2);
                double wS = HbFft2D.ConversionWeight2D(k1, k2, s1, s2);

                var gnm = G[n * N + m];
                Complex Gd = HbFft2D.SpecGet(gnm, d1, d2);
                Complex Gs = HbFft2D.SpecGet(gnm, s1, s2);
                double a00 =  wD * Gd.Real      + wS * Gs.Real;
                double a01 = -wD * Gd.Imaginary + wS * Gs.Imaginary;
                double a10 =  wD * Gd.Imaginary + wS * Gs.Imaginary;
                double a11 =  wD * Gd.Real      - wS * Gs.Real;

                var cnm = C[n * N + m];
                Complex Cd = HbFft2D.SpecGet(cnm, d1, d2);
                Complex Cs = HbFft2D.SpecGet(cnm, s1, s2);
                double cb00 =  wD * Cd.Real      + wS * Cs.Real;
                double cb01 = -wD * Cd.Imaginary + wS * Cs.Imaginary;
                double cb10 =  wD * Cd.Imaginary + wS * Cs.Imaginary;
                double cb11 =  wD * Cd.Real      - wS * Cs.Real;
                a00 += -omegaK * cb10;  a01 += -omegaK * cb11;
                a10 +=  omegaK * cb00;  a11 +=  omegaK * cb01;

                // Guard: hard cutoff above total order guardOrder (J only). Applies to G/C, not Y_NN.
                if (guardOrder > 0 &&
                    (Math.Abs(k1) + Math.Abs(k2) > guardOrder ||
                     Math.Abs(i1) + Math.Abs(i2) > guardOrder))
                    a00 = a01 = a10 = a11 = 0.0;

                // Linear interface admittance on the diagonal (no convention scaling).
                if (kMix == iMix)
                {
                    Complex y = yNN[kMix][n, m];
                    a00 +=  y.Real;       a01 += -y.Imaginary;
                    a10 +=  y.Imaginary;  a11 +=  y.Real;
                }

                // Maas DC special cases (§7.3) at the (0,0) index (mixIdx 0).
                if (iMix == 0) { a01 = 0; a11 = (kMix == 0 ? a00 : 0); }
                if (kMix == 0) { a10 = 0; a11 = (iMix == 0 ? a00 : 0); }

                if (rk0 >= 0 && ci0 >= 0) J[rk0*dof+ci0] += a00;
                if (rk0 >= 0 && ci1 >= 0) J[rk0*dof+ci1] += a01;
                if (rk1 >= 0 && ci0 >= 0) J[rk1*dof+ci0] += a10;
                if (rk1 >= 0 && ci1 >= 0) J[rk1*dof+ci1] += a11;
            }
        }
        return J;
    }

    // ── Finite-difference Jacobian comparison (two-tone oracle) ───────────────────

    public record JacobianElement2D(
        int Row, int Col,
        int RowNode, int RowK1, int RowK2, bool RowIsIm,
        int ColNode, int ColI1, int ColI2, bool ColIsIm,
        double AnalyticVal, double FdVal, double AbsError, double RelError);

    public record JacobianComparison2DResult(
        double MaxAbsError,
        double MaxRelError,
        int MaxRelRow, int MaxRelCol,
        int Dof, int N, int M,
        IReadOnlyList<JacobianElement2D> TopDiscrepancies,
        int DcDummyCount, double DcDummyMaxAbsError);

    /// <summary>
    /// Compare <see cref="BuildJ2D"/> (analytic) against a central-difference Jacobian of
    /// <see cref="BuildF2D"/> — the trusted oracle for the 2-D index/frequency arithmetic.
    /// DC Im-dummy DOFs (Im of the (0,0) index, set to a00 per Maas §7.3) are reported separately.
    /// </summary>
    public static JacobianComparison2DResult CompareJacobianNumerical2D(
        Complex[,] V, Complex[][,] yNN, Complex[][] iSrc,
        MixingGrid grid, int N, int N1, int N2, double f1, double f2,
        ElaboratedNetlist netlist, int[] interfaceNodes)
    {
        int M   = grid.MixCount;
        int dof = 2 * N * M;

        var (iNl0, qNl0, G0, C0) = EvaluateNonlinear2D(V, grid, N, N1, N2, netlist, interfaceNodes);
        double[] analyticJ = BuildJ2D(yNN, G0, C0, grid, N, f1, f2);

        double[] fdJ = new double[dof * dof];
        for (int j = 0; j < dof; j++)
        {
            bool jIsIm = (j & 1) == 1;
            int  tmp   = j >> 1;
            int  jNode = tmp / M;
            int  jMix  = tmp % M;

            double nomVal = jIsIm ? V[jNode, jMix].Imaginary : V[jNode, jMix].Real;
            double eps    = 1e-6 * Math.Max(Math.Abs(nomVal), 1.0);

            var Vp = (Complex[,])V.Clone();
            Vp[jNode, jMix] += jIsIm ? new Complex(0, eps) : new Complex(eps, 0);
            var (iNlp, qNlp, _, _) = EvaluateNonlinear2D(Vp, grid, N, N1, N2, netlist, interfaceNodes);
            double[] Fp = BuildF2D(Vp, yNN, iSrc, iNlp, qNlp, grid, N, f1, f2);

            var Vm = (Complex[,])V.Clone();
            Vm[jNode, jMix] += jIsIm ? new Complex(0, -eps) : new Complex(-eps, 0);
            var (iNlm, qNlm, _, _) = EvaluateNonlinear2D(Vm, grid, N, N1, N2, netlist, interfaceNodes);
            double[] Fm = BuildF2D(Vm, yNN, iSrc, iNlm, qNlm, grid, N, f1, f2);

            for (int r = 0; r < dof; r++)
                fdJ[r * dof + j] = (Fp[r] - Fm[r]) / (2.0 * eps);
        }

        double globalScale = 0;
        for (int i = 0; i < dof * dof; i++)
            globalScale = Math.Max(globalScale, Math.Max(Math.Abs(analyticJ[i]), Math.Abs(fdJ[i])));

        double maxAbsErr = 0, maxRelErr = 0;
        int maxRelRow = 0, maxRelCol = 0;
        int dcDummyCount = 0; double dcDummyMaxAbsErr = 0;
        var discrepancies = new List<JacobianElement2D>();

        for (int r = 0; r < dof; r++)
        for (int c = 0; c < dof; c++)
        {
            double an     = analyticJ[r * dof + c];
            double fd     = fdJ[r * dof + c];
            double absErr = Math.Abs(an - fd);

            if (IsDcImDof(r, M) && IsDcImDof(c, M))
            {
                dcDummyCount++;
                if (absErr > dcDummyMaxAbsErr) dcDummyMaxAbsErr = absErr;
                continue;
            }

            double domFloor = Math.Max(globalScale * 1e-8, 1e-12);
            double dom      = Math.Max(Math.Max(Math.Abs(an), Math.Abs(fd)), domFloor);
            double relErr   = absErr / dom;

            if (absErr > maxAbsErr) maxAbsErr = absErr;
            if (relErr > maxRelErr) { maxRelErr = relErr; maxRelRow = r; maxRelCol = c; }

            if (relErr > 1e-3 && dom > domFloor)
            {
                var (rNode, rMix, rIsIm) = Decode(r, M);
                var (cNode, cMix, cIsIm) = Decode(c, M);
                var (rk1, rk2) = grid.ToneOf(rMix);
                var (ci1, ci2) = grid.ToneOf(cMix);
                discrepancies.Add(new JacobianElement2D(r, c,
                    rNode, rk1, rk2, rIsIm, cNode, ci1, ci2, cIsIm,
                    an, fd, absErr, relErr));
            }
        }

        discrepancies.Sort((a, b) => b.AbsError.CompareTo(a.AbsError));
        if (discrepancies.Count > 20) discrepancies = discrepancies.GetRange(0, 20);

        return new JacobianComparison2DResult(maxAbsErr, maxRelErr, maxRelRow, maxRelCol,
            dof, N, M, discrepancies, dcDummyCount, dcDummyMaxAbsErr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Signed angular frequency 2π(k₁f₁ + k₂f₂) of a retained mixing product.</summary>
    private static double OmegaOf(MixingGrid grid, int mix, double f1, double f2)
    {
        var (k1, k2) = grid.ToneOf(mix);
        return 2.0 * Math.PI * (k1 * f1 + k2 * f2);
    }

    /// <summary>
    /// Computes per-port current spectra for each nonlinear device over the two-tone mixing lattice.
    /// Returns a dict keyed "instancePath:terminalName" → Complex[M] (one per mixing product).
    /// Values are numerically identical to INl at nodes belonging exclusively to this device
    /// (both follow the passive-sign convention: positive = current INTO the device port = FROM node).
    /// </summary>
    public static Dictionary<string, Complex[]> ComputeDevicePortCurrents2D(
        Complex[,]        V,
        MixingGrid        grid,
        int               N,
        int               N1,
        int               N2,
        ElaboratedNetlist netlist,
        int[]             interfaceNodes,
        double[][][,]?    portITime = null)
    {
        int M = grid.MixCount;

        // Prefer the buffer the last Newton device pass filled — it is an evaluation at the same
        // converged V, so re-deriving it here costs a full device sweep for numbers already in hand
        // (HB-P2 M3). There is no control-current form on this path, so the re-evaluation below is
        // only ever a fallback for a caller that supplied no buffer (or one of the wrong shape).
        bool useLastPass = MatchesShape(portITime, netlist, N1, N2);

        // IFFT V to time domain (mirrors EvaluateNonlinear2D step 1)
        double[][,]? vTime = null;
        if (!useLastPass)
        {
            var diamond = new Complex[M];
            vTime = new double[N][,];
            for (int n = 0; n < N; n++)
            {
                for (int m = 0; m < M; m++)
                    diamond[m] = (m == 0) ? new Complex(V[n, 0].Real, 0) : V[n, m];
                vTime[n] = new double[N1, N2];
                HbFft2D.Inverse2D(grid, diamond, N1, N2, vTime[n]);
            }
        }

        var result = new Dictionary<string, Complex[]>(StringComparer.Ordinal);

        for (int devOrd = 0; devOrd < netlist.NonlinearComponents.Count; devOrd++)
        {
            int    nlIdx     = netlist.NonlinearComponents[devOrd];
            var    ec        = netlist.Components[nlIdx];
            int    portCount = ec.Model.PortCount;
            string[] terms   = ec.Model.TerminalNames;

            double[][,] portI;
            if (useLastPass)
            {
                portI = portITime![devOrd];
            }
            else
            {
                var portPlusIdx  = new int[portCount];
                var portMinusIdx = new int[portCount];
                for (int p = 0; p < portCount; p++)
                {
                    int np = ec.Nodes.Length > 2*p   ? ec.Nodes[2*p]   : 0;
                    int nm = ec.Nodes.Length > 2*p+1 ? ec.Nodes[2*p+1] : 0;
                    portPlusIdx[p]  = Array.IndexOf(interfaceNodes, np);
                    portMinusIdx[p] = Array.IndexOf(interfaceNodes, nm);
                }

                portI = new double[portCount][,];
                for (int p = 0; p < portCount; p++) portI[p] = new double[N1, N2];

                var portV = new double[portCount];
                for (int t1 = 0; t1 < N1; t1++)
                for (int t2 = 0; t2 < N2; t2++)
                {
                    for (int p = 0; p < portCount; p++)
                    {
                        double vp = portPlusIdx[p]  >= 0 ? vTime![portPlusIdx[p]][t1, t2]  : 0.0;
                        double vm = portMinusIdx[p] >= 0 ? vTime![portMinusIdx[p]][t1, t2] : 0.0;
                        portV[p] = vp - vm;
                    }
                    var res = ec.Evaluate(new PortVoltages(portV));
                    for (int p = 0; p < portCount; p++)
                        portI[p][t1, t2] = res.I[p];
                }
            }

            // FFT each port → spectrum, extract retained mixing products
            for (int p = 0; p < portCount; p++)
            {
                string term = p < terms.Length ? terms[p] : (p + 1).ToString();
                string key  = $"{ec.InstancePath}:{term}";

                var iSpec = ForwardConv2D(portI[p], N1, N2);
                var iAmpl = new Complex[M];
                for (int m = 0; m < M; m++)
                {
                    var (k1, k2) = grid.ToneOf(m);
                    iAmpl[m] = HbFft2D.SpecGet(iSpec, k1, k2);
                }
                result[key] = iAmpl;

                // Also emit a 0-based port-index alias ("M1:0", "M1:1", …) so generic SDDs
                // (not necessarily FETs) can be accessed by port number regardless of terminal names.
                string numKey = $"{ec.InstancePath}:{p}";
                if (numKey != key) result[numKey] = iAmpl;
            }
        }

        return result;
    }

    /// <summary>A <c>[deviceOrdinal][port][t1, t2]</c> buffer sized for this netlist's nonlinear
    /// devices, or null when there are none.</summary>
    internal static double[][][,]? AllocPortITime(ElaboratedNetlist netlist, int N1, int N2)
    {
        int count = netlist.NonlinearComponents.Count;
        if (count == 0) return null;
        var buf = new double[count][][,];
        for (int i = 0; i < count; i++)
        {
            int pc = netlist.Components[netlist.NonlinearComponents[i]].Model.PortCount;
            buf[i] = new double[pc][,];
            for (int p = 0; p < pc; p++) buf[i][p] = new double[N1, N2];
        }
        return buf;
    }

    /// <summary>Whether a last-pass buffer describes THIS netlist at THIS grid size.</summary>
    private static bool MatchesShape(double[][][,]? buf, ElaboratedNetlist netlist, int N1, int N2)
    {
        if (buf is null || buf.Length != netlist.NonlinearComponents.Count) return false;
        for (int i = 0; i < buf.Length; i++)
        {
            var ec = netlist.Components[netlist.NonlinearComponents[i]];
            if (buf[i].Length != ec.Model.PortCount) return false;
            foreach (var g in buf[i])
                if (g.GetLength(0) != N1 || g.GetLength(1) != N2) return false;
        }
        return true;
    }

    // Real-split DOF index for (node n, mixIdx, Re/Im). mix=0..M-1, all included.
    private static int Idx(int n, int mix, bool isIm, int M)
        => 2 * (n * M + mix) + (isIm ? 1 : 0);

    private static (int node, int mix, bool isIm) Decode(int j, int M)
    {
        bool isIm = (j & 1) == 1;
        int tmp = j >> 1;
        return (tmp / M, tmp % M, isIm);
    }

    // DC Im DOF: Im of the (0,0) mixing index (mixIdx 0).
    private static bool IsDcImDof(int j, int M)
        => (j & 1) == 1 && (j >> 1) % M == 0;

    // ── Port → interface-node accumulation (KCL) ─────────────────────────────
    // The two-tone twin of HbNewton.PortAdd/PortAdd4 — same rule, lattice-shaped buffers.
    // See HbNewton for why both signs are required and which circuits hide the omission.

    private static void PortAdd(double[][,] buf, int iPlus, int iMinus, int t1, int t2, double val)
    {
        if (val == 0.0) return;
        if (iPlus  >= 0) buf[iPlus] [t1, t2] += val;
        if (iMinus >= 0) buf[iMinus][t1, t2] -= val;
    }

    private static void PortAdd4(double[][,] buf, int N, int iPlus, int iMinus,
                                 int jPlus, int jMinus, int t1, int t2, double val)
    {
        if (val == 0.0) return;
        if (iPlus  >= 0 && jPlus  >= 0) buf[iPlus  * N + jPlus] [t1, t2] += val;
        if (iPlus  >= 0 && jMinus >= 0) buf[iPlus  * N + jMinus][t1, t2] -= val;
        if (iMinus >= 0 && jPlus  >= 0) buf[iMinus * N + jPlus] [t1, t2] -= val;
        if (iMinus >= 0 && jMinus >= 0) buf[iMinus * N + jMinus][t1, t2] += val;
    }
}
