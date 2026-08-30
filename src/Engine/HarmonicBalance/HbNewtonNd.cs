using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// T-tone HB Newton blocks — the general-tone-count generalization of <see cref="HbNewton2D"/>
/// (harmonic-balance.md §6.5). Same engine, three substitutions:
/// <list type="bullet">
///   <item>the tone-pair mixIndex axis becomes the T-vector lattice <see cref="MixingLattice"/>,</item>
///   <item>the separable 2-D FFT becomes the <see cref="HbApft"/> transform pair, and</item>
///   <item>the Jacobian's difference/sum-frequency convolution
///         (<c>G[k−i] , G[k+i]</c>) becomes the triple product <c>A·diag(dg)·Γ</c>.</item>
/// </list>
///
/// <para><b>Why the Jacobian changes shape and why that is exact.</b> The residual's nonlinear
/// term is literally <c>i_nl = A·i(Γ·V)</c>, so by the chain rule its derivative is
/// <c>A·diag(∂i/∂v)·Γ</c> — the EXACT derivative of what is computed, not an approximation of a
/// convolution. That removes the need for the derivative waveform's SPECTRUM at difference and
/// sum indices, which is the only reason the two-tone FFT grid had to reach 2·MaxMixOrder
/// (hence its 4·order per-axis rule). The finite-difference oracle
/// <see cref="CompareJacobianNumericalNd"/> is what pins it, exactly as
/// <c>CompareJacobianNumerical2D</c> pins the two-tone form.</para>
///
/// <para>Everything downstream of the block is UNCHANGED from <see cref="HbNewton2D"/> and is
/// deliberately transcribed rather than re-derived: Y_NN on the mix diagonal, the per-row jω
/// charge rotation, the guard-order cutoff, and the Maas §7.3 DC row/column special cases.</para>
///
/// <para><b>This class is the T ≥ 2 path.</b> Single tone stays on <see cref="HbNewton"/>. Two
/// tones came here on 2026-08-30, when <c>AnalysisSettings.HbTwoToneOnLattice</c> was defaulted to
/// true — it is a measured 3.5× there, since the rectangular grid evaluates the device on 1,024
/// time samples to solve what the lattice solves on ~250. <see cref="HbNewton2D"/> and
/// <see cref="HbFft2D"/> are not dead: clearing that setting routes two tones back to them, and
/// they remain the independent second implementation this one is gated against.</para>
///
/// <para>That gate is <c>HbNewtonNdVs2DTests</c>, which drives BOTH formulations at T = 2 on the
/// same circuit and requires them to agree — the strongest available evidence the T-tone
/// formulation is right, since at T ≥ 3 there is no second implementation to compare with. Its
/// value did not change when the default flipped; what changed is that the FFT path it compares
/// against is now reached only through the setting.</para>
///
/// <para>Unknowns: V[n, mix] for n = 0..N−1 nonlinear-facing nodes and mix = 0..M−1 retained
/// products, real-split → 2·N·M DOF. mix = 0 is the DC index (real-only, Maas §7.3).</para>
/// </summary>
public static class HbNewtonNd
{
    /// <param name="PortITime">
    /// Per-device, per-port terminal current over the APFT sample set — <c>[deviceOrdinal][port][s]</c>
    /// — from the LAST device evaluation of this solve, which is the one at the returned <c>V</c>.
    /// <see cref="ComputeDevicePortCurrentsNd"/> takes it instead of re-evaluating every device at
    /// every sample for numbers already in hand (HB-P2 M3). This path has no control-current form.
    /// </param>
    public record SolveResult(bool Converged, int Iterations,
        IReadOnlyList<HbConvergenceTrace.IterRecord> IterTrace,
        Complex[,] INl,   // I_nl[node, mixIdx] at the returned point
        double[][][]? PortITime = null);

    // ── Newton loop ──────────────────────────────────────────────────────────────

    /// <summary>
    /// T-tone HB Newton loop — the general-T analogue of <see cref="HbNewton2D.Solve"/>. Iterates
    /// the real-split system J·ΔV = −F over the mixIndex axis until ‖F‖₂ &lt; tol or HbMaxIter.
    /// V is modified in place ([N, M]). yNN[mix]/iSrc[mix] are the per-product linear interface
    /// and Norton source (mix = 0 = DC uses the real DC admittance/source).
    /// </summary>
    public static SolveResult Solve(
        Complex[,]         V,                // [N, M] — initial guess in, converged out
        Complex[][,]       yNN,              // [M][N,N]
        Complex[][]        iSrc,             // [M][N]
        HbApft             apft,
        double[]           toneFreqsHz,
        int                N,
        ElaboratedNetlist  netlist,
        int[]              interfaceNodes,
        AnalysisSettings   settings,
        double             tol,
        double             lambda     = 1.0,
        int                guardOrder = 0)
    {
        var lattice  = apft.Lattice;
        int M        = lattice.MixCount;
        int unknowns = 2 * N * M;
        int maxIter  = settings.HbMaxIter;
        var trace    = new List<HbConvergenceTrace.IterRecord>();
        Complex[,] iNlLast = new Complex[N, M];

        // One buffer for the whole solve, overwritten per pass, so the last pass leaves its
        // per-port terminal currents behind for the post-solve extraction (M3).
        var portITime = AllocPortITime(netlist, apft.SampleCount);

        for (int iter = 0; iter < maxIter; iter++)
        {
            var (iNl, qNl, dg, dc) = EvaluateNonlinearNd(V, apft, N, netlist, interfaceNodes, portITime);
            iNlLast = iNl;

            var F  = BuildFNd(V, yNN, iSrc, iNl, qNl, lattice, N, toneFreqsHz);
            double fN = HbNewton.L2(F);
            trace.Add(new HbConvergenceTrace.IterRecord(iter, fN));

            if (fN < tol)
                return new SolveResult(true, iter + 1, trace, iNlLast, portITime);

            var J = BuildJNd(yNN, dg, dc, apft, N, toneFreqsHz, guardOrder);

            var negF = new double[unknowns];
            for (int r = 0; r < unknowns; r++) negF[r] = -F[r];
            double[]? dV = HbNewton.SolveGaussian(J, negF, unknowns);
            if (dV is null)
            {
                Console.Error.WriteLine($"[HBnD] Jacobian singular at iter {iter}, ‖F‖={fN:E3}");
                return new SolveResult(false, iter + 1, trace, iNlLast, portITime);
            }

            ApplyUpdateNd(V, dV, N, M, lambda);
        }

        var (iNlF, qNlF, _, _) = EvaluateNonlinearNd(V, apft, N, netlist, interfaceNodes, portITime);
        iNlLast = iNlF;
        var FF = BuildFNd(V, yNN, iSrc, iNlF, qNlF, lattice, N, toneFreqsHz);
        trace.Add(new HbConvergenceTrace.IterRecord(maxIter, HbNewton.L2(FF)));
        return new SolveResult(false, maxIter, trace, iNlLast, portITime);
    }

    private static void ApplyUpdateNd(Complex[,] V, double[] dV, int N, int M, double lambda)
    {
        int len = dV.Length;
        for (int n = 0; n < N; n++)
        for (int mix = 0; mix < M; mix++)
        {
            int rRe = Idx(n, mix, false, M);
            int rIm = Idx(n, mix, true,  M);
            double dRe = rRe < len ? dV[rRe] : 0.0;
            // DC Im is fictitious (real signal); no update there.
            double dIm = (mix != 0 && rIm < len) ? dV[rIm] : 0.0;
            V[n, mix] += new Complex(lambda * dRe, lambda * dIm);
        }
    }

    // ── Time-domain nonlinear evaluation (T-tone) ────────────────────────────────

    /// <summary>
    /// Evaluate the nonlinear devices at the APFT's S torus samples and return:
    ///   iNl[n,mix], qNl[n,mix]  — nonlinear current/charge phasors at the retained products
    ///   dg[n·N+m], dc[n·N+m]    — the derivative waveforms as raw TIME SAMPLES (length S)
    ///
    /// <para>The derivative waveforms are deliberately NOT transformed: <see cref="BuildJNd"/>
    /// consumes them as the diagonal of the triple product. This is the structural difference
    /// from <see cref="HbNewton2D.EvaluateNonlinear2D"/>, which must produce full non-folded
    /// G/C SPECTRA so its convolution can look them up at difference and sum indices.</para>
    /// </summary>
    public static (Complex[,] iNl, Complex[,] qNl, double[][] dg, double[][] dc)
        EvaluateNonlinearNd(Complex[,] V, HbApft apft, int N,
            ElaboratedNetlist netlist, int[] interfaceNodes, double[][][]? portITime = null)
    {
        int M = apft.MixCount;
        int S = apft.SampleCount;

        // 1. V[n,mix] → real time samples v(φ_s) per node (DC forced real, as the 2-D path does;
        //    the APFT's DC-Im column is zero, so this is explicit intent rather than a correction).
        var vTime   = new double[N][];
        var diamond = new Complex[M];
        for (int n = 0; n < N; n++)
        {
            for (int m = 0; m < M; m++)
                diamond[m] = (m == 0) ? new Complex(V[n, 0].Real, 0) : V[n, m];
            vTime[n] = new double[S];
            apft.Synthesize(diamond, vTime[n]);
        }

        var iTime = new double[N][];
        var qTime = new double[N][];
        for (int n = 0; n < N; n++) { iTime[n] = new double[S]; qTime[n] = new double[S]; }
        var dgTime = new double[N * N][];
        var dcTime = new double[N * N][];
        for (int idx = 0; idx < N * N; idx++) { dgTime[idx] = new double[S]; dcTime[idx] = new double[S]; }

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

            for (int s = 0; s < S; s++)
            {
                for (int p = 0; p < portCount; p++)
                {
                    double vp = portPlusIdx[p]  >= 0 ? vTime[portPlusIdx[p]][s]  : 0.0;
                    double vm = portMinusIdx[p] >= 0 ? vTime[portMinusIdx[p]][s] : 0.0;
                    portV[p] = vp - vm;
                }
                var res = ec.Evaluate(new PortVoltages(portV));

                for (int p = 0; p < portCount; p++)
                {
                    if (devPortI is not null) devPortI[p][s] = res.I[p];
                    PortAdd(iTime, portPlusIdx[p], portMinusIdx[p], s, res.I[p]);
                    PortAdd(qTime, portPlusIdx[p], portMinusIdx[p], s, res.Q[p]);
                    for (int q = 0; q < portCount; q++)
                    {
                        PortAdd4(dgTime, N, portPlusIdx[p], portMinusIdx[p],
                                 portPlusIdx[q], portMinusIdx[q], s, res.Dg[p, q]);
                        PortAdd4(dcTime, N, portPlusIdx[p], portMinusIdx[p],
                                 portPlusIdx[q], portMinusIdx[q], s, res.Dc[p, q]);
                    }
                }
            }
        }

        // 2. Project the current/charge waveforms back onto the retained lattice.
        var iNl = new Complex[N, M];
        var qNl = new Complex[N, M];
        var buf = new Complex[M];
        for (int n = 0; n < N; n++)
        {
            apft.Analyze(iTime[n], buf);
            for (int m = 0; m < M; m++) iNl[n, m] = buf[m];
            apft.Analyze(qTime[n], buf);
            for (int m = 0; m < M; m++) qNl[n, m] = buf[m];
        }

        return (iNl, qNl, dgTime, dcTime);
    }

    // ── Residual F[n, mix] (real-split) ──────────────────────────────────────────

    /// <summary>
    /// T-tone residual: F[n,mix] = iSrc[mix][n] + Σ_m yNN[mix][n,m]·V[m,mix] + iNl[n,mix]
    ///                            + j·ω(mix)·qNl[n,mix],  ω(mix) = 2π·Σ_t k_t·f_t (signed).
    /// DC (mix = 0): the Im part of F is forced to 0 (real signal — Maas §7.3).
    /// Identical in form to <see cref="HbNewton2D.BuildF2D"/>.
    /// </summary>
    public static double[] BuildFNd(Complex[,] V, Complex[][,] yNN, Complex[][] iSrc,
        Complex[,] iNl, Complex[,] qNl, MixingLattice lattice, int N, double[] toneFreqsHz)
    {
        int M   = lattice.MixCount;
        int dof = 2 * N * M;
        var F   = new double[dof];
        var omega = Omegas(lattice, toneFreqsHz);

        for (int n = 0; n < N; n++)
        for (int mix = 0; mix < M; mix++)
        {
            int rRe = Idx(n, mix, false, M);
            int rIm = Idx(n, mix, true,  M);

            Complex f  = iSrc[mix][n];
            var     yk = yNN[mix];
            for (int m = 0; m < N; m++) f += yk[n, m] * V[m, mix];
            f += iNl[n, mix];

            if (mix != 0) f += new Complex(0, omega[mix]) * qNl[n, mix];

            F[rRe] = f.Real;
            if (mix != 0) F[rIm] = f.Imaginary;
        }
        return F;
    }

    // ── Jacobian (real 2×2 blocks over the T-tone lattice) ───────────────────────

    /// <summary>
    /// T-tone analytic Jacobian. Per node pair (n, m) the D×D block (D = 2M) is
    /// <code>
    ///   A·diag(dg)·Γ  +  R(ω_row)·[ A·diag(dc)·Γ ]
    /// </code>
    /// where R(ω_row) is the per-row-product real form of multiplying by jω (row 2k takes
    /// −ω_k × row 2k+1, row 2k+1 takes +ω_k × row 2k) — the same rotation
    /// <see cref="HbNewton2D.BuildJ2D"/> applies to its 2×2 charge block. Y_NN is then added on
    /// the mix diagonal and the Maas §7.3 DC special cases are applied last, both transcribed
    /// from the two-tone path.
    /// </summary>
    public static double[] BuildJNd(Complex[][,] yNN, double[][] dg, double[][] dc,
        HbApft apft, int N, double[] toneFreqsHz, int guardOrder = 0)
    {
        var lattice = apft.Lattice;
        int M   = lattice.MixCount;
        int D   = 2 * M;
        int dof = N * D;
        var J   = new double[dof * dof];
        var omega = Omegas(lattice, toneFreqsHz);

        // Guard: which products the G/C contribution is allowed to touch (J only, never Y_NN).
        bool[]? guarded = null;
        if (guardOrder > 0)
        {
            guarded = new bool[M];
            for (int m = 0; m < M; m++) guarded[m] = lattice.OrderOf(m) > guardOrder;
        }

        var block  = new double[D * D];
        var blockC = new double[D * D];

        for (int n = 0; n < N; n++)
        for (int m = 0; m < N; m++)
        {
            Array.Clear(block);

            // Both derivative waveforms go in ONE call: they share A and Γ, so the kernel walks a
            // column panel of Γ once for the pair instead of once each. A node pair with no device
            // across it has an identically-zero waveform, and passing null for it costs nothing —
            // the AllZero shortcut is preserved, it has just moved into the argument.
            var dgNm = dg[n * N + m];
            var dcNm = dc[n * N + m];
            bool hasG = !AllZero(dgNm);
            bool hasC = !AllZero(dcNm);

            if (hasC) Array.Clear(blockC);
            if (hasG || hasC)
                apft.AccumulateTripleProducts(hasG ? dgNm : null, hasC ? dcNm : null, block, blockC);

            if (hasC)
            {
                for (int k = 0; k < M; k++)
                {
                    double w = omega[k];
                    if (w == 0.0) continue;
                    int r0 = (2 * k) * D, r1 = (2 * k + 1) * D;
                    for (int c = 0; c < D; c++)
                    {
                        block[r0 + c] += -w * blockC[r1 + c];
                        block[r1 + c] +=  w * blockC[r0 + c];
                    }
                }
            }

            // Guard cutoff — applies to the G/C contribution only, before Y_NN is added.
            if (guarded is not null)
            {
                for (int k = 0; k < M; k++)
                {
                    if (!guarded[k]) continue;
                    Array.Clear(block, (2 * k) * D, D);          // row pair
                    Array.Clear(block, (2 * k + 1) * D, D);
                    for (int r = 0; r < D; r++)                  // column pair
                    { block[r * D + 2 * k] = 0.0; block[r * D + 2 * k + 1] = 0.0; }
                }
            }

            // Linear interface admittance on the mix diagonal (no convention scaling).
            for (int k = 0; k < M; k++)
            {
                Complex y = yNN[k][n, m];
                int r0 = (2 * k) * D, r1 = (2 * k + 1) * D;
                block[r0 + 2 * k]     +=  y.Real;
                block[r0 + 2 * k + 1] += -y.Imaginary;
                block[r1 + 2 * k]     +=  y.Imaginary;
                block[r1 + 2 * k + 1] +=  y.Real;
            }

            // Maas DC special cases (§7.3) at the DC index (local Re = 0, Im = 1): the DC row and
            // DC column of the imaginary DOF are zeroed and its diagonal mirrors the real one, so
            // the fictitious DOF stays decoupled and the block stays invertible.
            for (int r = 0; r < D; r++) block[r * D + 1] = 0.0;
            Array.Clear(block, 1 * D, D);
            block[1 * D + 1] = block[0];

            // Scatter into the global Jacobian.
            for (int r = 0; r < D; r++)
            {
                int src = r * D;
                int dst = (n * D + r) * dof + m * D;
                for (int c = 0; c < D; c++) J[dst + c] += block[src + c];
            }
        }

        return J;
    }

    // ── Finite-difference Jacobian comparison (the T-tone oracle) ────────────────

    public record JacobianElementNd(
        int Row, int Col,
        int RowNode, int[] RowK, bool RowIsIm,
        int ColNode, int[] ColK, bool ColIsIm,
        double AnalyticVal, double FdVal, double AbsError, double RelError);

    public record JacobianComparisonNdResult(
        double MaxAbsError,
        double MaxRelError,
        int MaxRelRow, int MaxRelCol,
        int Dof, int N, int M,
        IReadOnlyList<JacobianElementNd> TopDiscrepancies,
        int DcDummyCount, double DcDummyMaxAbsError);

    /// <summary>
    /// Compare <see cref="BuildJNd"/> (analytic) against a central-difference Jacobian of
    /// <see cref="BuildFNd"/> — the trusted oracle for the T-vector index and frequency
    /// arithmetic, and for the triple-product form itself. DC Im-dummy DOFs are reported
    /// separately, exactly as <c>CompareJacobianNumerical2D</c> does.
    /// </summary>
    public static JacobianComparisonNdResult CompareJacobianNumericalNd(
        Complex[,] V, Complex[][,] yNN, Complex[][] iSrc,
        HbApft apft, int N, double[] toneFreqsHz,
        ElaboratedNetlist netlist, int[] interfaceNodes)
    {
        var lattice = apft.Lattice;
        int M   = lattice.MixCount;
        int dof = 2 * N * M;

        var (_, _, dg0, dc0) = EvaluateNonlinearNd(V, apft, N, netlist, interfaceNodes);
        double[] analyticJ = BuildJNd(yNN, dg0, dc0, apft, N, toneFreqsHz);

        double[] fdJ = new double[dof * dof];
        for (int j = 0; j < dof; j++)
        {
            var (jNode, jMix, jIsIm) = Decode(j, M);

            double nomVal = jIsIm ? V[jNode, jMix].Imaginary : V[jNode, jMix].Real;
            double eps    = 1e-6 * Math.Max(Math.Abs(nomVal), 1.0);

            var Vp = (Complex[,])V.Clone();
            Vp[jNode, jMix] += jIsIm ? new Complex(0, eps) : new Complex(eps, 0);
            var (iNlp, qNlp, _, _) = EvaluateNonlinearNd(Vp, apft, N, netlist, interfaceNodes);
            double[] Fp = BuildFNd(Vp, yNN, iSrc, iNlp, qNlp, lattice, N, toneFreqsHz);

            var Vm = (Complex[,])V.Clone();
            Vm[jNode, jMix] += jIsIm ? new Complex(0, -eps) : new Complex(-eps, 0);
            var (iNlm, qNlm, _, _) = EvaluateNonlinearNd(Vm, apft, N, netlist, interfaceNodes);
            double[] Fm = BuildFNd(Vm, yNN, iSrc, iNlm, qNlm, lattice, N, toneFreqsHz);

            for (int r = 0; r < dof; r++)
                fdJ[r * dof + j] = (Fp[r] - Fm[r]) / (2.0 * eps);
        }

        double globalScale = 0;
        for (int i = 0; i < dof * dof; i++)
            globalScale = Math.Max(globalScale, Math.Max(Math.Abs(analyticJ[i]), Math.Abs(fdJ[i])));

        double maxAbsErr = 0, maxRelErr = 0;
        int maxRelRow = 0, maxRelCol = 0;
        int dcDummyCount = 0; double dcDummyMaxAbsErr = 0;
        var discrepancies = new List<JacobianElementNd>();

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
                discrepancies.Add(new JacobianElementNd(r, c,
                    rNode, lattice.ToneOf(rMix), rIsIm, cNode, lattice.ToneOf(cMix), cIsIm,
                    an, fd, absErr, relErr));
            }
        }

        discrepancies.Sort((a, b) => b.AbsError.CompareTo(a.AbsError));
        if (discrepancies.Count > 20) discrepancies = discrepancies.GetRange(0, 20);

        return new JacobianComparisonNdResult(maxAbsErr, maxRelErr, maxRelRow, maxRelCol,
            dof, N, M, discrepancies, dcDummyCount, dcDummyMaxAbsErr);
    }

    // ── Device port currents ─────────────────────────────────────────────────────

    /// <summary>
    /// Per-port current spectra for each nonlinear device over the T-tone lattice, keyed
    /// "instancePath:terminalName" (plus a 0-based "instancePath:portIndex" alias) →
    /// Complex[M]. The T-tone twin of <see cref="HbNewton2D.ComputeDevicePortCurrents2D"/>,
    /// including its passive-sign convention.
    /// </summary>
    public static Dictionary<string, Complex[]> ComputeDevicePortCurrentsNd(
        Complex[,]        V,
        HbApft            apft,
        int               N,
        ElaboratedNetlist netlist,
        int[]             interfaceNodes,
        double[][][]?     portITime = null)
    {
        int M = apft.MixCount;
        int S = apft.SampleCount;

        // Prefer the buffer the last Newton device pass filled — an evaluation at the same converged
        // V, so re-deriving it here costs a full device sweep for numbers already in hand (M3).
        bool useLastPass = MatchesShape(portITime, netlist, S);

        double[][]? vTime = null;
        if (!useLastPass)
        {
            var diamond = new Complex[M];
            vTime = new double[N][];
            for (int n = 0; n < N; n++)
            {
                for (int m = 0; m < M; m++)
                    diamond[m] = (m == 0) ? new Complex(V[n, 0].Real, 0) : V[n, m];
                vTime[n] = new double[S];
                apft.Synthesize(diamond, vTime[n]);
            }
        }

        var result = new Dictionary<string, Complex[]>(StringComparer.Ordinal);

        for (int devOrd = 0; devOrd < netlist.NonlinearComponents.Count; devOrd++)
        {
            int      nlIdx     = netlist.NonlinearComponents[devOrd];
            var      ec        = netlist.Components[nlIdx];
            int      portCount = ec.Model.PortCount;
            string[] terms     = ec.Model.TerminalNames;

            double[][] portI;
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

                portI = new double[portCount][];
                for (int p = 0; p < portCount; p++) portI[p] = new double[S];

                var portV = new double[portCount];
                for (int s = 0; s < S; s++)
                {
                    for (int p = 0; p < portCount; p++)
                    {
                        double vp = portPlusIdx[p]  >= 0 ? vTime![portPlusIdx[p]][s]  : 0.0;
                        double vm = portMinusIdx[p] >= 0 ? vTime![portMinusIdx[p]][s] : 0.0;
                        portV[p] = vp - vm;
                    }
                    var res = ec.Evaluate(new PortVoltages(portV));
                    for (int p = 0; p < portCount; p++) portI[p][s] = res.I[p];
                }
            }

            for (int p = 0; p < portCount; p++)
            {
                string term = p < terms.Length ? terms[p] : (p + 1).ToString();
                string key  = $"{ec.InstancePath}:{term}";

                var iAmpl = new Complex[M];
                apft.Analyze(portI[p], iAmpl);
                result[key] = iAmpl;

                string numKey = $"{ec.InstancePath}:{p}";
                if (numKey != key) result[numKey] = iAmpl;
            }
        }

        return result;
    }

    /// <summary>A <c>[deviceOrdinal][port][s]</c> buffer sized for this netlist's nonlinear devices,
    /// or null when there are none.</summary>
    internal static double[][][]? AllocPortITime(ElaboratedNetlist netlist, int sampleCount)
    {
        int count = netlist.NonlinearComponents.Count;
        if (count == 0) return null;
        var buf = new double[count][][];
        for (int i = 0; i < count; i++)
        {
            int pc = netlist.Components[netlist.NonlinearComponents[i]].Model.PortCount;
            buf[i] = new double[pc][];
            for (int p = 0; p < pc; p++) buf[i][p] = new double[sampleCount];
        }
        return buf;
    }

    /// <summary>Whether a last-pass buffer describes THIS netlist at THIS sample count.</summary>
    private static bool MatchesShape(double[][][]? buf, ElaboratedNetlist netlist, int sampleCount)
    {
        if (buf is null || buf.Length != netlist.NonlinearComponents.Count) return false;
        for (int i = 0; i < buf.Length; i++)
        {
            var ec = netlist.Components[netlist.NonlinearComponents[i]];
            if (buf[i].Length != ec.Model.PortCount) return false;
            foreach (var g in buf[i]) if (g.Length != sampleCount) return false;
        }
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Signed angular frequency 2π·Σ_t k_t·f_t per retained product.</summary>
    private static double[] Omegas(MixingLattice lattice, double[] toneFreqsHz)
    {
        var w = new double[lattice.MixCount];
        for (int m = 0; m < w.Length; m++)
            w[m] = 2.0 * Math.PI * lattice.FrequencyOf(m, toneFreqsHz);
        return w;
    }

    private static bool AllZero(double[] a)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != 0.0) return false;
        return true;
    }

    // Real-split DOF index for (node n, mixIdx, Re/Im) — identical layout to HbNewton2D, so the
    // per-node block n·(2M)…(n+1)·(2M) is contiguous and the Jacobian scatters block-wise.
    private static int Idx(int n, int mix, bool isIm, int M)
        => 2 * (n * M + mix) + (isIm ? 1 : 0);

    private static (int node, int mix, bool isIm) Decode(int j, int M)
    {
        bool isIm = (j & 1) == 1;
        int tmp = j >> 1;
        return (tmp / M, tmp % M, isIm);
    }

    // DC Im DOF: Im of the all-zero mixing index (mixIdx 0).
    private static bool IsDcImDof(int j, int M)
        => (j & 1) == 1 && (j >> 1) % M == 0;

    // ── Port → interface-node accumulation (KCL) ─────────────────────────────────
    // The T-tone twin of HbNewton.PortAdd/PortAdd4 — same rule, flat per-sample buffers.
    // See HbNewton for why both signs are required and which circuits hide the omission.

    private static void PortAdd(double[][] buf, int iPlus, int iMinus, int s, double val)
    {
        if (val == 0.0) return;
        if (iPlus  >= 0) buf[iPlus] [s] += val;
        if (iMinus >= 0) buf[iMinus][s] -= val;
    }

    private static void PortAdd4(double[][] buf, int N, int iPlus, int iMinus,
                                 int jPlus, int jMinus, int s, double val)
    {
        if (val == 0.0) return;
        if (iPlus  >= 0 && jPlus  >= 0) buf[iPlus  * N + jPlus] [s] += val;
        if (iPlus  >= 0 && jMinus >= 0) buf[iPlus  * N + jMinus][s] -= val;
        if (iMinus >= 0 && jPlus  >= 0) buf[iMinus * N + jPlus] [s] -= val;
        if (iMinus >= 0 && jMinus >= 0) buf[iMinus * N + jMinus][s] += val;
    }
}
