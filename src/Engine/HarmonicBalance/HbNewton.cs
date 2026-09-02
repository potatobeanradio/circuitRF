using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// FFT'd contribution from one (component, w≥2) bucket — pre-computed once per Newton iterate.
/// WNl[n,k] is the spectrum of Σ_ports I[p,w] summed over interface nodes.
/// Dw[n,m,k] is the corresponding Jacobian spectrum (used in the conversion matrix).
/// Model is kept to call Weight(W, k·ω₀) at Jacobian assembly time.
/// </summary>
public sealed record HigherWeightBucket(
    ComponentModel Model,
    int            W,
    Complex[,]     WNl,    // [N, K+1]
    Complex[,,]    Dw);    // [N, N, Kj+1]

/// <summary>
/// Per-device, per-port time-domain terminal contributions kept by one device pass, indexed by the
/// SDD weighting index w: w=0 is the conduction current I[p,0], w=1 the charge I[p,1], and w≥2 the
/// device's own weighted terms. The port current spectrum is
/// <c>I[p,k] = Σ_w H[w](k·ω₀)·FT{value_w[p](t)}_k</c> — the SAME sum the HB residual forms at the
/// node, just kept per named terminal.
///
/// <para><b>Why w=1 is carried and not dropped.</b> A branch whose only nonlinear content is charge
/// (a NonlinearC, or an SDD written with I[p,1] alone) has <c>res.I[p] ≡ 0</c> at every sample, so a
/// buffer holding only the conduction current reports that branch as exactly zero — which is what
/// the unified I cube used to publish.</para>
/// </summary>
public sealed class PortTermTimes
{
    public PortTermTimes(int portCount, int gridN)
    {
        PortCount = portCount;
        GridN     = gridN;
        W0        = new double[portCount, gridN];
        W1        = new double[portCount, gridN];
    }

    public int PortCount { get; }
    public int GridN     { get; }

    /// <summary>Conduction current I[p,0] over the time grid — H[0] = 1.</summary>
    public double[,] W0 { get; }

    /// <summary>Charge I[p,1] over the time grid — H[1] = jω.</summary>
    public double[,] W1 { get; }

    private Dictionary<int, double[,]>? _higher;

    /// <summary>The w≥2 buckets this device actually produced, or null when it produced none.</summary>
    public IReadOnlyDictionary<int, double[,]>? Higher => _higher;

    /// <summary>The w≥2 buffer for weighting index <paramref name="w"/>, allocated on first use.</summary>
    public double[,] HigherFor(int w)
    {
        _higher ??= [];
        if (!_higher.TryGetValue(w, out var buf))
            _higher[w] = buf = new double[PortCount, GridN];
        return buf;
    }
}

/// <summary>
/// Carries the linear extractor + per-harmonic source RHS into HbNewton.Solve so the per-iterate
/// _c_ref recompute can call SolveFullNetwork inside the Newton loop (brief #2).
/// Null for circuits with no SDD C[n] references — zero overhead on the common path.
/// </summary>
public sealed record ControlCurrentContext(
    HbLinearExtractor Extractor,
    Complex[][]       BSrc,   // [K+1][mnaSize] — per-harmonic source RHS (snapshotted)
    double            F0,
    int               K);

/// <summary>
/// Ingredients for the control-current Jacobian block J_cc (brief #3 §2), produced by the
/// pass-2 evaluation and consumed by BuildJ. J_cc = B·R·A where:
///   A = ∂iNl_total/∂V        (the main conversion blocks — built in BuildJ from G/C/buckets)
///   R = ∂_c_ref/∂iNl_total   (rRef sensitivity rows, harmonic-diagonal — built from Extractor)
///   B = ∂F/∂_c_ref           (conversion of the per-w control kernels with H[w] weighting)
/// </summary>
/// <param name="G">Number of (SDD, control) references.</param>
/// <param name="BranchIdx">[G] resolved MNA branch index of each referenced device.</param>
/// <param name="Models">[G] owning SDD model per control (for H[w] weights).</param>
/// <param name="Kernels">w → Khat[node, g, d]: FFT of ∂I[p,w]/∂_c summed to interface node.</param>
/// <param name="Kj">Conversion-grid harmonic reach of the kernels (= min(2K, gridN/2)).</param>
public sealed record ControlJacData(
    int                          G,
    int[]                        BranchIdx,
    ComponentModel[]             Models,
    Dictionary<int, Complex[,,]> Kernels,
    int                          Kj);

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
    /// <summary>
    /// How many time-domain device evaluations <see cref="EvaluateNonlinear"/> has performed ON THIS
    /// THREAD. Test-facing, in the same spirit as <c>HbEngine.LinearFactorizations</c>: the property
    /// HB-P3 M1 asserts is that the line search's ACCEPTED trial is also the next iteration's entry
    /// evaluation, so a converging solve with no backtracks evaluates exactly once per iteration and
    /// never twice at the same V. Per-thread so the test suite's parallel classes cannot see each
    /// other's counts; reset it with <see cref="ResetEvaluations"/> immediately before the solve
    /// under test.
    /// </summary>
    [ThreadStatic] private static int _evaluations;

    /// <inheritdoc cref="_evaluations"/>
    public static int Evaluations => _evaluations;

    /// <inheritdoc cref="_evaluations"/>
    public static void ResetEvaluations() => _evaluations = 0;

    /// <param name="INl">
    /// The TOTAL nonlinear injection spectrum per [node, harmonic] — <c>iNl + jkω₀·qNl + Σ_w
    /// H[w](kω₀)·WNl</c>, the same sum <see cref="BuildF"/> adds to the linear part of the residual.
    /// Caller reads <c>INl[n,0]</c> for the DC-current trend.
    ///
    /// <para><b>Why the total and not the conduction current alone.</b> This is what KCL at the
    /// interface node balances, so it is what the linear back-solve must inject to recover a branch
    /// current or a linear-interior node voltage. Carrying only the w=0 part reported every
    /// charge-carrying branch as its leakage current: a probe in series with a <c>NonlinearC</c>
    /// read <c>gmin·V</c> instead of <c>jωC·V</c>, and Zin/Pin at a gate with a Cgs term dropped the
    /// capacitive half. At k=0 the charge term vanishes, so DC readings are unchanged.</para>
    /// </param>
    /// <param name="PortTerms">
    /// Per-device, per-port terminal contributions over the time grid — <c>[deviceOrdinal]</c>
    /// indexes <c>netlist.NonlinearComponents</c> — as computed by the LAST device evaluation this
    /// solve performed, which is the one at the returned <c>V</c>. See <see cref="PortTermTimes"/>
    /// for why the charge row is carried alongside the current row.
    ///
    /// <para><b>Why it is returned rather than recomputed (HB-P2 M3).</b>
    /// <see cref="ComputeDevicePortCurrents"/> used to re-evaluate every device at every sample
    /// after convergence purely to re-house currents by named terminal — measured at ~150 us of a
    /// ~750 us warm Hero-2 <c>Run</c>, for numbers the final Newton pass had already computed and
    /// thrown away. Keeping them turns the post-solve step into one FFT per port per weighting
    /// index. Null when the caller asked for no buffer.</para>
    /// </param>
    public record SolveResult(bool Converged, int Iterations,
        IReadOnlyList<HbConvergenceTrace.IterRecord> IterTrace,
        Complex[,] INl,
        PortTermTimes[]? PortTerms = null);

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
        int                guardHarmonic  = 0,      // B3: guard harmonic index (0=off)
        ControlCurrentContext? cc         = null,   // brief #2: per-iterate _c_ref context
        bool               useControlJacobian = true)  // brief #3: J_cc on (quadratic) vs off (quasi-Newton)
    {
        double omega0  = 2.0 * Math.PI * f0;
        int    unknowns = 2 * N * (K + 1);  // real-split: all harmonics k=0..K
        var    trace   = new List<HbConvergenceTrace.IterRecord>();
        int    maxIter = settings.HbMaxIter;

        // The last computed CONDUCTION spectrum, and only that: it is the frozen w=0 seed pass 1 of
        // the control-current evaluation is documented to take (see EvaluateNonlinear). What the
        // solve REPORTS is the total injection, assembled at each return by TotalInjection.
        Complex[,] iNlLast  = new Complex[N, K + 1];

        // One buffer for the whole solve, overwritten by each device pass, so the pass that
        // happens to be the last one leaves its per-port terminal contributions behind (M3).
        // Allocated once — a per-iteration allocation would trade one cost for another.
        var portITime = AllocPortTerms(netlist, gridN);

        // Scratch the line search steps FROM — the accepted iterate every trial restarts at.
        var V0 = new Complex[N, K + 1];

        // ── 0. One evaluation before the loop ─────────────────────────────────
        // From here the loop's ENTRY state is always the last accepted trial's evaluation, so
        // EvaluateNonlinear runs exactly once per accepted step plus once per rejected trial —
        // never twice at the same V (HB-P3 M1; HbLineSearchTests counts it).
        var (iNl, qNl, G, C, higherBuckets) =
            EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes, cc, iNlLast, out var ctrlJac,
                              portITime);
        iNlLast   = iNl;
        var    F  = BuildF(V, yNN, iSrc, iNl, qNl, N, K, omega0, higherBuckets);
        double fN = L2(F);

        for (int iter = 0; iter < maxIter; iter++)
        {
            // ── 1. Convergence test on ‖F‖ (absolute, in amperes — design §12.2) ──
            if (fN < tol)
            {
                trace.Add(new HbConvergenceTrace.IterRecord(iter, fN));
                return new SolveResult(true, iter + 1, trace,
                    TotalInjection(iNl, qNl, higherBuckets, N, K, omega0), portITime);
            }

            // ── 2. Jacobian J (real-split 2×2 blocks, §7.2 + Maas §7.3) ───────
            var J = BuildJ(yNN, G, C, N, K, omega0, guardHarmonic, higherBuckets,
                useControlJacobian ? ctrlJac : null, cc);

            // ── 3. Dense solve J·ΔV = −F ─────────────────────────────────────
            var negF = new double[unknowns];
            for (int r = 0; r < unknowns; r++) negF[r] = -F[r];
            double[]? dV = SolveGaussian(J, negF, unknowns);
            if (dV is null)
            {
                trace.Add(new HbConvergenceTrace.IterRecord(iter, fN));
                Console.Error.WriteLine($"[HB] Jacobian singular at iter {iter}, ‖F‖={fN:E3}");
                return new SolveResult(false, iter + 1, trace,
                    TotalInjection(iNl, qNl, higherBuckets, N, K, omega0), portITime);
            }

            // ── 4. Update V[k=0..K] += λ·ΔV, λ from the backtracking line search ──
            double fEntry = fN;
            Array.Copy(V, V0, V.Length);
            var step = Backtrack(fEntry, lambda, lam =>
            {
                Array.Copy(V0, V, V.Length);
                ApplyUpdate(V, dV, N, K, lam);
                (iNl, qNl, G, C, higherBuckets) =
                    EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes, cc, iNlLast, out ctrlJac,
                                      portITime);
                F = BuildF(V, yNN, iSrc, iNl, qNl, N, K, omega0, higherBuckets);
                return L2(F);
            });
            iNlLast = iNl;
            fN      = step.Residual;
            trace.Add(new HbConvergenceTrace.IterRecord(
                iter, fEntry, step.Lambda, step.Backtracks, step.Stalled));
        }

        // Max iterations. ‖F‖ at the final iterate is already known — the last accepted trial
        // computed it — so the tail costs no evaluation of its own.
        trace.Add(new HbConvergenceTrace.IterRecord(settings.HbMaxIter, fN));
        return new SolveResult(false, settings.HbMaxIter, trace,
            TotalInjection(iNl, qNl, higherBuckets, N, K, omega0), portITime);
    }

    // ── Backtracking line search (HB-P3 M1) ──────────────────────────────────

    /// <summary>
    /// How many times the line search may halve λ before it accepts the smallest step it tried
    /// rather than standing still. 8 halvings, so λ bottoms out at 1/256.
    ///
    /// <para>A constant rather than a user setting on purpose: it bounds a failure mode, it does not
    /// tune an answer. A step this short that still does not reduce ‖F‖ is not a step-length problem
    /// — that is what <c>DriveStepping</c>'s ramp is for.</para>
    /// </summary>
    public const int MaxBacktracks = 8;

    /// <summary>
    /// The Armijo sufficient-decrease coefficient c in <c>‖F(V+λ·ΔV)‖ ≤ (1 − c·λ)·‖F(V)‖</c>.
    /// Small enough (1e-4) that a full Newton step from a good iterate is accepted on the first
    /// trial, which is what keeps a converging solve byte-identical to the undamped loop.
    /// </summary>
    public const double ArmijoC = 1e-4;

    /// <summary>The outcome of one line search: the residual reached, and how it got there.</summary>
    public readonly record struct LineSearchStep(
        double Residual, double Lambda, int Backtracks, bool Stalled);

    /// <summary>
    /// The backtracking line search the three HB Newton loops share.
    ///
    /// <para><b>Why the loops need it.</b> The HB Jacobian is exact and the fixed point is right; what
    /// failed at a compressed drive was the STEP. A full Newton step from a DC seed overshoots into
    /// the region where an SDD's <c>tanh</c>/<c>exp</c> terms saturate, and the iterate then wanders
    /// until <c>HbMaxIter</c> — 100 iterations returning non-converged on the shipped Hero-2 fixture
    /// at 16 and 20 dBm, before this existed.</para>
    ///
    /// <para><paramref name="trialAt"/> applies λ·ΔV to the caller's iterate, evaluates there and
    /// returns ‖F‖, leaving the caller's own state (spectra, Jacobian ingredients, residual vector)
    /// set to that trial. It is called at most <see cref="MaxBacktracks"/>+1 times and the ACCEPTED
    /// trial is always the last one called — which is what lets a caller keep per-evaluation
    /// by-products (the port-current buffer) without snapshotting them per trial.</para>
    ///
    /// <para>A NaN residual never satisfies the test, so it backtracks like any other rejection.
    /// Exhausting the halvings keeps the smallest step rather than standing still — standing still
    /// would repeat the same Jacobian and the same rejected step forever — and flags
    /// <see cref="LineSearchStep.Stalled"/> so the trace says so.</para>
    /// </summary>
    /// <param name="f0">‖F‖ at the iterate being stepped from.</param>
    /// <param name="lambdaStart">
    /// The first λ tried — the user's fixed <c>Lambda</c> damping (B2), default 1. Its meaning is
    /// unchanged: λ &lt; 1 still damps every step, it is now the STARTING point of the search
    /// instead of the only value.
    /// </param>
    public static LineSearchStep Backtrack(double f0, double lambdaStart, Func<double, double> trialAt)
    {
        ArgumentNullException.ThrowIfNull(trialAt);

        double lam = lambdaStart;
        for (int rejected = 0; ; rejected++)
        {
            double fT = trialAt(lam);
            if (fT <= (1.0 - ArmijoC * lam) * f0)
                return new LineSearchStep(fT, lam, rejected, false);
            if (rejected == MaxBacktracks)
                return new LineSearchStep(fT, lam, rejected, true);
            lam *= 0.5;
        }
    }

    /// <summary>
    /// A per-device <see cref="PortTermTimes"/> buffer sized for this netlist's nonlinear devices,
    /// or null when there are none. The ordinal is the position in
    /// <c>netlist.NonlinearComponents</c> — the order every device pass and
    /// <see cref="ComputeDevicePortCurrents"/> both iterate in.
    /// </summary>
    internal static PortTermTimes[]? AllocPortTerms(ElaboratedNetlist netlist, int gridN)
    {
        int count = netlist.NonlinearComponents.Count;
        if (count == 0) return null;
        var buf = new PortTermTimes[count];
        for (int i = 0; i < count; i++)
            buf[i] = new PortTermTimes(
                netlist.Components[netlist.NonlinearComponents[i]].Model.PortCount, gridN);
        return buf;
    }

    /// <summary>
    /// The TOTAL nonlinear injection spectrum at the interface — <c>iNl[n,k] + jkω₀·qNl[n,k] +
    /// Σ_w H[w](kω₀)·WNl[n,k]</c>. This is exactly the nonlinear part <see cref="BuildF"/> adds to
    /// the residual, kept as one array so every consumer of the converged solve (the linear
    /// back-solve, the INl cube, the loadpull FOMs) reads the same quantity KCL balanced.
    /// </summary>
    internal static Complex[,] TotalInjection(
        Complex[,] iNl, Complex[,] qNl, IReadOnlyList<HigherWeightBucket>? higherBuckets,
        int N, int K, double omega0)
    {
        var tot = new Complex[N, K + 1];
        for (int n = 0; n < N; n++)
        for (int k = 0; k <= K; k++)
        {
            Complex f = iNl[n, k];
            if (k > 0) f += new Complex(0, k * omega0) * qNl[n, k];
            if (higherBuckets is { Count: > 0 })
                foreach (var buc in higherBuckets)
                    f += buc.Model.Weight(buc.W, k * omega0) * buc.WNl[n, k];
            tot[n, k] = f;
        }
        return tot;
    }

    // ── Time-domain nonlinear evaluation ─────────────────────────────────────

    /// <summary>Convenience overload — discards the control-Jacobian ingredients (brief #3).</summary>
    public static (Complex[,] iNl, Complex[,] qNl, Complex[,,] G, Complex[,,] C,
                   IReadOnlyList<HigherWeightBucket> higherBuckets)
        EvaluateNonlinear(Complex[,] V, int N, int K, int gridN,
            ElaboratedNetlist netlist, int[] interfaceNodes,
            ControlCurrentContext? cc = null, Complex[,]? iNlPrev = null)
        => EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes, cc, iNlPrev, out _);

    /// <summary>
    /// Time-domain evaluation of all nonlinear devices, returning the residual spectra
    /// (iNl, qNl) and conversion-matrix spectra (G, C, higherBuckets).
    ///
    /// Control currents (brief #3 §0) — TWO-PASS self-consistent <c>_c_ref(V)</c>:
    ///   Pass 1 evaluates the SDDs with <c>_c_ref</c> frozen at the entry seed (from
    ///   <paramref name="iNlPrev"/>) → <c>iNl(V)</c> for the CURRENT V. Then
    ///   <c>_c_ref(V)</c> is computed from that <c>iNl(V)</c> via SolveFullNetwork per
    ///   harmonic (injecting the TOTAL nonlinear current: iNl + jωq + ΣH·WNl). Pass 2
    ///   re-evaluates with the self-consistent <c>_c_ref(V)</c> → the returned spectra,
    ///   so the residual the FD oracle differentiates has <c>∂_c_ref/∂V ≠ 0</c>.
    /// The inner map is a SINGLE linearization step (no inner iteration) — the analytic
    /// <c>J_cc</c> (BuildJ) is the exact derivative of exactly this one-step map.
    /// When <paramref name="cc"/> is null (no control SDDs), this is byte-identical to the
    /// pre-brief single-pass evaluation.
    /// </summary>
    public static (Complex[,] iNl, Complex[,] qNl, Complex[,,] G, Complex[,,] C,
                   IReadOnlyList<HigherWeightBucket> higherBuckets)
        EvaluateNonlinear(Complex[,] V, int N, int K, int gridN,
            ElaboratedNetlist netlist, int[] interfaceNodes,
            ControlCurrentContext? cc, Complex[,]? iNlPrev, out ControlJacData? ctrlJac,
            PortTermTimes[]? portITime = null)
    {
        _evaluations++;
        ctrlJac = null;

        var vTime = new double[N][];
        for (int n = 0; n < N; n++)
        {
            vTime[n] = new double[gridN];
            var Xn = new Complex[K + 1];
            for (int k = 0; k <= K; k++) Xn[k] = V[n, k];
            HbFft.Inverse(Xn, K, vTime[n]);
        }

        int Kj = Math.Min(2 * K, gridN / 2);

        // Control-reference table: one entry per (SDD, local control index).
        var controlEntries = BuildControlEntries(netlist);
        bool hasControl = cc is not null && controlEntries.Count > 0;

        Dictionary<int, double[,]>? cRefByDevice = null;
        if (hasControl)
        {
            double omega0 = 2.0 * Math.PI * cc!.F0;

            // Pass 1: frozen _c seed from iNlPrev (w=0) → iNl(V) for the current V.
            var cRefSeed = ComputeControlRefTimes(netlist, cc, N, K, gridN,
                (n, k) => iNlPrev is not null ? iNlPrev[n, k] : Complex.Zero);
            var (iNl1, qNl1, _, _, buckets1, _) =
                RunDevicePass(vTime, N, K, Kj, gridN, netlist, interfaceNodes, cRefSeed, null);

            // Total nonlinear injection spectrum from pass 1 (iNl + jωq + ΣH·WNl).
            cRefByDevice = ComputeControlRefTimes(netlist, cc, N, K, gridN, (n, k) =>
            {
                Complex tot = iNl1[n, k];
                if (k > 0) tot += new Complex(0, k * omega0) * qNl1[n, k];
                foreach (var b in buckets1)
                    tot += b.Model.Weight(b.W, k * omega0) * b.WNl[n, k];
                return tot;
            });
        }

        // Main pass (pass 2 if control present; the only pass otherwise).
        var (iNl, qNl, G, C, higherBuckets, sens) =
            RunDevicePass(vTime, N, K, Kj, gridN, netlist, interfaceNodes, cRefByDevice,
                hasControl ? controlEntries : null, portITime);

        if (hasControl && sens is not null)
            ctrlJac = BuildCtrlJacData(sens, controlEntries, N, K, Kj, gridN);

        return (iNl, qNl, G, C, higherBuckets);
    }

    /// <summary>
    /// The whole time grid of port-voltage vectors for one device, in sample order — exactly what
    /// the scalar loop computes one sample at a time, gathered so a batched model can be asked once.
    /// </summary>
    private static double[][] GatherPortVoltages(
        double[][] vTime, int gridN, int portCount, int[] portPlusIdx, int[] portMinusIdx)
    {
        var points = new double[gridN][];
        for (int t = 0; t < gridN; t++)
        {
            var pv = new double[portCount];
            for (int p = 0; p < portCount; p++)
            {
                double vp = portPlusIdx[p]  >= 0 ? vTime[portPlusIdx[p]][t]  : 0.0;
                double vm = portMinusIdx[p] >= 0 ? vTime[portMinusIdx[p]][t] : 0.0;
                pv[p] = vp - vm;
            }
            points[t] = pv;
        }
        return points;
    }

    /// <summary>
    /// One full time-domain evaluation pass over all nonlinear devices.
    /// <paramref name="cRefByDevice"/> maps a control-SDD's component index to its
    /// <c>_c_ref(t)</c> array [localControl, gridN]; null devices get no control currents.
    /// When <paramref name="controlEntries"/> is non-null, per-w control sensitivities
    /// (∂I[p,w]/∂_c) are accumulated into <c>sens</c> time buffers [node, globalControl, t].
    /// </summary>
    private static (Complex[,] iNl, Complex[,] qNl, Complex[,,] G, Complex[,,] C,
                    List<HigherWeightBucket> buckets, Dictionary<int, double[,,]>? sens)
        RunDevicePass(double[][] vTime, int N, int K, int Kj, int gridN,
            ElaboratedNetlist netlist, int[] interfaceNodes,
            Dictionary<int, double[,]>? cRefByDevice,
            IReadOnlyList<(int nlIdx, int ci, int branchIdx, ComponentModel model)>? controlEntries,
            PortTermTimes[]? portITime = null)
    {
        var iTime  = new double[N, gridN];
        var qTime  = new double[N, gridN];
        var dgTime = new double[N, N, gridN];
        var dcTime = new double[N, N, gridN];

        // Per-(nlIdx, W) time-domain buffers for w≥2 buckets.
        var bucketBuffers = new Dictionary<(int nlIdx, int w),
            (ComponentModel model, double[,] wTime, double[,,] dwTime)>();

        // Control-sensitivity time buffers: w → ∂I[p,w]/∂_c at [node, globalControl, t].
        Dictionary<int, double[,,]>? sens = null;
        Dictionary<(int nlIdx, int ci), int>? gLookup = null;
        int gCount = 0;
        if (controlEntries is not null)
        {
            sens    = new Dictionary<int, double[,,]>();
            gLookup = new Dictionary<(int, int), int>();
            for (int g = 0; g < controlEntries.Count; g++)
                gLookup[(controlEntries[g].nlIdx, controlEntries[g].ci)] = g;
            gCount = controlEntries.Count;
        }

        for (int devOrd = 0; devOrd < netlist.NonlinearComponents.Count; devOrd++)
        {
            int nlIdx     = netlist.NonlinearComponents[devOrd];
            var ec        = netlist.Components[nlIdx];
            int portCount = ec.Model.PortCount;
            var portV     = new double[portCount];
            var portPlusIdx  = new int[portCount];
            var portMinusIdx = new int[portCount];

            // Per-port terminal contributions for THIS device, when the caller asked for them (M3).
            // Written alongside the accumulation into iTime/qTime, from the same res — the post-solve
            // extraction re-derived exactly these by evaluating every device all over again.
            var devPort = portITime is not null && devOrd < portITime.Length ? portITime[devOrd] : null;

            for (int p = 0; p < portCount; p++)
            {
                int np = ec.Nodes.Length > 2*p   ? ec.Nodes[2*p]   : 0;
                int nm = ec.Nodes.Length > 2*p+1 ? ec.Nodes[2*p+1] : 0;
                portPlusIdx[p]  = Array.IndexOf(interfaceNodes, np);
                portMinusIdx[p] = Array.IndexOf(interfaceNodes, nm);
            }

            double[,]? cRefTime = null;
            cRefByDevice?.TryGetValue(nlIdx, out cRefTime);
            bool collectSens = sens is not null && cRefTime is not null;

            // One round trip for the whole time grid, when the model says a round trip is what an
            // evaluation costs. Built-in models leave this null and take the scalar path below
            // unchanged — the control-current form has no batched shape and is always scalar.
            IReadOnlyList<NonlinearResult>? batch =
                ec.PrefersBatchEvaluate && cRefTime is null
                    ? ec.EvaluateBatch(GatherPortVoltages(vTime, gridN, portCount, portPlusIdx, portMinusIdx))
                    : null;

            // HB-P4 M2 — and one walk of the compiled program for the whole grid, when the model
            // says its cost is arithmetic rather than transport. Same rule as the batch above: the
            // control-current form has no grid shape here and stays scalar.
            GridResult? grid = null;
            HbGridSampler? sampler = null;
            if (batch is null && ec.PrefersGridEvaluate && cRefTime is null)
            {
                var pv = HbGridBuffers.PortVBuffer(gridN, portCount);
                for (int p = 0; p < portCount; p++)
                {
                    int ip = portPlusIdx[p], im = portMinusIdx[p];
                    int bse = p * gridN;
                    for (int t = 0; t < gridN; t++)
                        pv[bse + t] = (ip >= 0 ? vTime[ip][t] : 0.0) - (im >= 0 ? vTime[im][t] : 0.0);
                }
                grid = HbGridBuffers.Result(devOrd);
                ec.EvaluateGrid(pv.AsSpan(0, portCount * gridN), [], gridN, grid);
                sampler = HbGridBuffers.Sampler();
            }

            for (int t = 0; t < gridN; t++)
            {
                NonlinearResult res;
                if (batch is not null)
                    res = batch[t];
                else if (grid is not null)
                    res = sampler!.Sample(grid, t);
                else
                {
                    for (int p = 0; p < portCount; p++)
                    {
                        double vp = portPlusIdx[p]  >= 0 ? vTime[portPlusIdx[p]][t]  : 0.0;
                        double vm = portMinusIdx[p] >= 0 ? vTime[portMinusIdx[p]][t] : 0.0;
                        portV[p] = vp - vm;
                    }
                    if (cRefTime is not null)
                    {
                        int m = ((SddModel)ec.Model).ControlRefs.Length;
                        var cVals = new double[m];
                        for (int ci = 0; ci < m; ci++) cVals[ci] = cRefTime[ci, t];
                        res = ec.Evaluate(new PortVoltages(portV), new ControlCurrents(cVals));
                    }
                    else
                        res = ec.Evaluate(new PortVoltages(portV));
                }

                for (int p = 0; p < portCount; p++)
                {
                    if (devPort is not null)
                    {
                        devPort.W0[p, t] = res.I[p];
                        devPort.W1[p, t] = res.Q[p];
                    }
                    PortAdd(iTime, portPlusIdx[p], portMinusIdx[p], t, res.I[p]);
                    PortAdd(qTime, portPlusIdx[p], portMinusIdx[p], t, res.Q[p]);
                    for (int q = 0; q < portCount; q++)
                    {
                        PortAdd4(dgTime, portPlusIdx[p], portMinusIdx[p],
                                 portPlusIdx[q], portMinusIdx[q], t, res.Dg[p, q]);
                        PortAdd4(dcTime, portPlusIdx[p], portMinusIdx[p],
                                 portPlusIdx[q], portMinusIdx[q], t, res.Dc[p, q]);
                    }
                }

                // Accumulate w≥2 bucket time-domain values.
                foreach (var term in res.Terms)
                {
                    var key = (nlIdx, term.W);
                    if (!bucketBuffers.ContainsKey(key))
                        bucketBuffers[key] = (ec.Model,
                            new double[N, gridN], new double[N, N, gridN]);
                    var buf = bucketBuffers[key];
                    var devW = devPort?.HigherFor(term.W);
                    for (int p = 0; p < portCount; p++)
                    {
                        if (devW is not null) devW[p, t] = term.Value[p];
                        PortAdd(buf.wTime, portPlusIdx[p], portMinusIdx[p], t, term.Value[p]);
                        for (int q = 0; q < portCount; q++)
                            PortAdd4(buf.dwTime, portPlusIdx[p], portMinusIdx[p],
                                     portPlusIdx[q], portMinusIdx[q], t, term.Jac[p, q]);
                    }
                }

                // Brief #3 §2: accumulate per-w control sensitivities ∂I[p,w]/∂_c.
                if (collectSens)
                {
                    int m = res.DControl?.GetLength(1) ?? 0;
                    for (int p = 0; p < portCount; p++)
                    {
                        for (int ci = 0; ci < m; ci++)
                        {
                            int g = gLookup![(nlIdx, ci)];
                            if (res.DControl is not null)
                                SensAddPort(sens!, 0, N, gCount, gridN,
                                            portPlusIdx[p], portMinusIdx[p], g, t, res.DControl[p, ci]);
                            if (res.DControlCharge is not null)
                                SensAddPort(sens!, 1, N, gCount, gridN,
                                            portPlusIdx[p], portMinusIdx[p], g, t, res.DControlCharge[p, ci]);
                        }
                    }
                    foreach (var term in res.Terms)
                    {
                        if (term.JacCtrl is null) continue;
                        for (int p = 0; p < portCount; p++)
                        {
                            for (int ci = 0; ci < term.JacCtrl.GetLength(1); ci++)
                            {
                                int g = gLookup![(nlIdx, ci)];
                                SensAddPort(sens!, term.W, N, gCount, gridN,
                                            portPlusIdx[p], portMinusIdx[p], g, t, term.JacCtrl[p, ci]);
                            }
                        }
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

        // FFT each w≥2 bucket's time buffers → WNl and Dw.
        var higherBuckets = new List<HigherWeightBucket>(bucketBuffers.Count);
        foreach (var ((_, w), (model, wTime, dwTime)) in bucketBuffers)
        {
            var WNl = new Complex[N, K + 1];
            var Dw  = new Complex[N, N, Kj + 1];

            for (int n = 0; n < N; n++)
            {
                var wX = new double[gridN];
                for (int t = 0; t < gridN; t++) wX[t] = wTime[n, t];
                HbFft.Forward(wX, K, out var wAmpl, out _);
                for (int k = 0; k <= K; k++) WNl[n, k] = wAmpl[k];
            }
            for (int n = 0; n < N; n++)
            for (int m = 0; m < N; m++)
            {
                var dwX = new double[gridN];
                for (int t = 0; t < gridN; t++) dwX[t] = dwTime[n, m, t];
                HbFft.Forward(dwX, Kj, out var dwAmpl, out _);
                for (int k = 0; k <= Kj; k++) Dw[n, m, k] = dwAmpl[k];
            }

            higherBuckets.Add(new HigherWeightBucket(model, w, WNl, Dw));
        }

        return (iNl, qNl, G, C, higherBuckets, sens);
    }

    private static void SensAdd(Dictionary<int, double[,,]> sens, int w,
        int N, int gCount, int gridN, int node, int g, int t, double val)
    {
        if (!sens.TryGetValue(w, out var buf))
            sens[w] = buf = new double[N, gCount, gridN];
        buf[node, g, t] += val;
    }

    // ── Port → interface-node accumulation (KCL) ─────────────────────────────
    //
    // A device port spans TWO nets, and its current leaves at the − net exactly as it enters at
    // the + net. Accumulating only at + is KCL-correct ONLY when the − net is ground (or otherwise
    // outside the interface), which is where every built-in hero circuit happens to sit: an SDD
    // written `SDD:M1 n_gate 0 n_drain 0` has both port references grounded. A FLOATING port — a
    // ring-quad diode across two live nets — is the case that exposes it, and the standalone DC
    // engine (NonlinearDcEngine.BuildResidualAndJacobian) has always done both signs. These
    // helpers make HB agree with it.
    //
    // The port voltage is V(+) − V(−), so a port-pair derivative dg[p,q] stamps at four corners
    // with signs (+,−,−,+) — the same 4-way stamp NonlinearDcEngine.StampDg does.

    private static void PortAdd(double[,] buf, int iPlus, int iMinus, int t, double val)
    {
        if (val == 0.0) return;
        if (iPlus  >= 0) buf[iPlus,  t] += val;
        if (iMinus >= 0) buf[iMinus, t] -= val;
    }

    private static void PortAdd4(double[,,] buf, int iPlus, int iMinus, int jPlus, int jMinus,
                                 int t, double val)
    {
        if (val == 0.0) return;
        if (iPlus  >= 0 && jPlus  >= 0) buf[iPlus,  jPlus,  t] += val;
        if (iPlus  >= 0 && jMinus >= 0) buf[iPlus,  jMinus, t] -= val;
        if (iMinus >= 0 && jPlus  >= 0) buf[iMinus, jPlus,  t] -= val;
        if (iMinus >= 0 && jMinus >= 0) buf[iMinus, jMinus, t] += val;
    }

    private static void SensAddPort(Dictionary<int, double[,,]> sens, int w,
        int N, int gCount, int gridN, int iPlus, int iMinus, int g, int t, double val)
    {
        if (val == 0.0) return;
        if (iPlus  >= 0) SensAdd(sens, w, N, gCount, gridN, iPlus,  g, t, +val);
        if (iMinus >= 0) SensAdd(sens, w, N, gCount, gridN, iMinus, g, t, -val);
    }

    /// <summary>Flat list of (SDD component idx, local control idx, resolved branch idx, model).</summary>
    private static List<(int nlIdx, int ci, int branchIdx, ComponentModel model)>
        BuildControlEntries(ElaboratedNetlist netlist)
    {
        var entries = new List<(int, int, int, ComponentModel)>();
        foreach (var nlIdx in netlist.NonlinearComponents)
        {
            var ec = netlist.Components[nlIdx];
            if (ec.Model is not SddModel sdd || sdd.ControlRefs.Length == 0) continue;
            for (int ci = 0; ci < sdd.ControlRefs.Length; ci++)
                entries.Add((nlIdx, ci, sdd.ControlBranchIndices[ci], sdd));
        }
        return entries;
    }

    /// <summary>
    /// Per control-SDD, compute <c>_c_ref(t)</c> [localControl, gridN] from a given
    /// interface-current source spectrum via SolveFullNetwork per harmonic (the same
    /// back-solve the residual uses). <paramref name="source"/>(n,k) is the injected
    /// nonlinear current at interface node n, harmonic k.
    /// </summary>
    private static Dictionary<int, double[,]> ComputeControlRefTimes(
        ElaboratedNetlist netlist, ControlCurrentContext cc, int N, int K, int gridN,
        Func<int, int, Complex> source)
    {
        var dict = new Dictionary<int, double[,]>();
        var iNlK = new Complex[N];
        foreach (var nlIdx in netlist.NonlinearComponents)
        {
            var ec = netlist.Components[nlIdx];
            if (ec.Model is not SddModel sdd || sdd.ControlRefs.Length == 0) continue;

            int m = sdd.ControlRefs.Length;
            var spec = new Complex[m, K + 1];
            for (int k = 0; k <= K; k++)
            {
                for (int n = 0; n < N; n++) iNlK[n] = source(n, k);
                double omegaK = k == 0 ? 0.0 : k * 2.0 * Math.PI * cc.F0;
                var xK = cc.Extractor.SolveFullNetwork(omegaK, iNlK, cc.BSrc[k]);
                for (int ci = 0; ci < m; ci++)
                {
                    int br = sdd.ControlBranchIndices[ci];
                    spec[ci, k] = br >= 0 && br < xK.Length ? xK[br] : Complex.Zero;
                }
            }

            var cRefTime = new double[m, gridN];
            var sbuf = new Complex[K + 1];
            var tbuf = new double[gridN];
            for (int ci = 0; ci < m; ci++)
            {
                for (int k = 0; k <= K; k++) sbuf[k] = spec[ci, k];
                HbFft.Inverse(sbuf, K, tbuf);
                for (int t = 0; t < gridN; t++) cRefTime[ci, t] = tbuf[t];
            }
            dict[nlIdx] = cRefTime;
        }
        return dict;
    }

    /// <summary>FFT the per-w control-sensitivity time buffers into the J_cc kernel spectra.</summary>
    private static ControlJacData BuildCtrlJacData(
        Dictionary<int, double[,,]> sens,
        IReadOnlyList<(int nlIdx, int ci, int branchIdx, ComponentModel model)> controlEntries,
        int N, int K, int Kj, int gridN)
    {
        int Gc = controlEntries.Count;
        var kernels = new Dictionary<int, Complex[,,]>();
        foreach (var (w, buf) in sens)
        {
            var khat = new Complex[N, Gc, Kj + 1];
            for (int n = 0; n < N; n++)
            for (int g = 0; g < Gc; g++)
            {
                var x = new double[gridN];
                for (int t = 0; t < gridN; t++) x[t] = buf[n, g, t];
                HbFft.Forward(x, Kj, out var ampl, out _);
                for (int d = 0; d <= Kj; d++) khat[n, g, d] = ampl[d];
            }
            kernels[w] = khat;
        }

        var branchIdx = new int[Gc];
        var models    = new ComponentModel[Gc];
        for (int g = 0; g < Gc; g++) { branchIdx[g] = controlEntries[g].branchIdx; models[g] = controlEntries[g].model; }

        return new ControlJacData(Gc, branchIdx, models, kernels, Kj);
    }

    // ── Residual F[n, k=0..K] (real-split) ───────────────────────────────────

    public static double[] BuildF(Complex[,] V, Complex[][,] yNN, Complex[][] iSrc,
        Complex[,] iNl, Complex[,] qNl, int N, int K, double omega0,
        IReadOnlyList<HigherWeightBucket>? higherBuckets = null)
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
            // w≥2 buckets: H[w](kω₀) · WNl[n,k]; w=0/1 are the fast path above.
            if (higherBuckets is { Count: > 0 })
                foreach (var buc in higherBuckets)
                    f += buc.Model.Weight(buc.W, k * omega0) * buc.WNl[n, k];

            F[rRe] = f.Real;
            // DC (k=0): Im part of F is always 0 (real signal — Maas §7.3).
            if (k > 0) F[rIm] = f.Imaginary;
        }
        return F;
    }

    // ── Jacobian (real 2×2 blocks, §7.2 + Maas §7.3) ─────────────────────────

    public static double[] BuildJ(Complex[][,] yNN, Complex[,,] G, Complex[,,] C,
        int N, int K, double omega0, int guardHarmonic = 0,
        IReadOnlyList<HigherWeightBucket>? higherBuckets = null,
        ControlJacData? ctrlJac = null, ControlCurrentContext? cc = null)
    {
        int dof = 2 * N * (K + 1);
        var J = new double[dof * dof];

        // Pre-compute H[w](k·ω₀) cache for each bucket — constant per solve.
        Complex[,]? hkCache = null;
        if (higherBuckets is { Count: > 0 })
        {
            hkCache = new Complex[higherBuckets.Count, K + 1];
            for (int bi = 0; bi < higherBuckets.Count; bi++)
            {
                var buc = higherBuckets[bi];
                for (int k = 0; k <= K; k++)
                    hkCache[bi, k] = buc.Model.Weight(buc.W, k * omega0);
            }
        }

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

            // ── w≥2 bucket contribution (§2, BuildJ) ─────────────────────────────
            // Each bucket contributes: H[w](k·ω₀) complex-multiplied by its
            // conversion block, using the same SafeGet / ConversionWeight convention.
            if (hkCache is not null)
            {
                for (int bi = 0; bi < higherBuckets!.Count; bi++)
                {
                    Complex Dkmi = SafeGet(higherBuckets[bi].Dw, n, m, k - i);
                    Complex Dkpi = SafeGet(higherBuckets[bi].Dw, n, m, k + i);
                    double d00 =  wKmi *  Dkmi.Real      + wKpi *  Dkpi.Real;
                    double d01 = -wKmi *  Dkmi.Imaginary + wKpi *  Dkpi.Imaginary;
                    double d10 =  wKmi *  Dkmi.Imaginary + wKpi *  Dkpi.Imaginary;
                    double d11 =  wKmi *  Dkmi.Real      - wKpi *  Dkpi.Real;
                    double ha  = hkCache[bi, k].Real;
                    double hb  = hkCache[bi, k].Imaginary;
                    a00 += ha * d00 - hb * d10;
                    a01 += ha * d01 - hb * d11;
                    a10 += hb * d00 + ha * d10;
                    a11 += hb * d01 + ha * d11;
                }
            }

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

        // ── Control-current Jacobian block J_cc (brief #3 §2) ─────────────────
        if (ctrlJac is not null && cc is not null && ctrlJac.G > 0)
            AddControlJacobian(J, G, C, higherBuckets, hkCache, ctrlJac, cc, N, K, omega0);

        return J;
    }

    // ── Control-current Jacobian assembly: J_cc = B·R·A (brief #3 §2) ─────────

    /// <summary>
    /// 2×2 real-split conversion block for output (n,k) ← input (m,i) from a single complex
    /// spectrum D[a,b,d] (conjugate for negative d), scaled by complex weight <paramref name="h"/>.
    /// Identical machinery to the G/C/bucket blocks in the main loop — NO Y_NN, Maas, or guard.
    /// </summary>
    private static (double a00, double a01, double a10, double a11) SpectrumBlock(
        Complex[,,] D, int a, int b, int k, int i, Complex h)
    {
        double wKmi = ConversionWeight(k, k - i);
        double wKpi = ConversionWeight(k, k + i);
        Complex Dkmi = SafeGet(D, a, b, k - i);
        Complex Dkpi = SafeGet(D, a, b, k + i);
        double d00 =  wKmi * Dkmi.Real      + wKpi * Dkpi.Real;
        double d01 = -wKmi * Dkmi.Imaginary + wKpi * Dkpi.Imaginary;
        double d10 =  wKmi * Dkmi.Imaginary + wKpi * Dkpi.Imaginary;
        double d11 =  wKmi * Dkmi.Real      - wKpi * Dkpi.Real;
        double ha = h.Real, hb = h.Imaginary;
        return (ha * d00 - hb * d10, ha * d01 - hb * d11,
                hb * d00 + ha * d10, hb * d01 + ha * d11);
    }

    private static void AddControlJacobian(double[] J, Complex[,,] G, Complex[,,] C,
        IReadOnlyList<HigherWeightBucket>? buckets, Complex[,]? hkCache,
        ControlJacData cj, ControlCurrentContext cc, int N, int K, double omega0)
    {
        int dofN = 2 * N * (K + 1);
        int Gc   = cj.G;
        int dofG = 2 * Gc * (K + 1);

        // A: ∂iNl_total/∂V  [dofN × dofN] — the main conversion (G + jω·C + ΣH·Dw), no Y/Maas.
        var A = new double[dofN * dofN];
        for (int n = 0; n < N; n++)
        for (int k = 0; k <= K; k++)
        for (int m = 0; m < N; m++)
        for (int i = 0; i <= K; i++)
        {
            var (a00, a01, a10, a11) = SpectrumBlock(G, n, m, k, i, Complex.One);
            var (c00, c01, c10, c11) = SpectrumBlock(C, n, m, k, i, new Complex(0, k * omega0));
            a00 += c00; a01 += c01; a10 += c10; a11 += c11;
            if (buckets is not null && hkCache is not null)
                for (int bi = 0; bi < buckets.Count; bi++)
                {
                    var (b00, b01, b10, b11) = SpectrumBlock(buckets[bi].Dw, n, m, k, i, hkCache[bi, k]);
                    a00 += b00; a01 += b01; a10 += b10; a11 += b11;
                }
            WriteBlock(A, dofN, n, k, m, i, K, a00, a01, a10, a11);
        }
        ZeroDcIm(A, dofN, dofN, K);  // iNl_total DC bin is real (HbFft forces Im=0)

        // B: ∂F/∂_c_ref  [dofN × dofG] — conversion of the per-w control kernels, H[w]-weighted.
        var B = new double[dofN * dofG];
        for (int n = 0; n < N; n++)
        for (int k = 0; k <= K; k++)
        for (int g = 0; g < Gc; g++)
        for (int kap = 0; kap <= K; kap++)
        {
            double s00 = 0, s01 = 0, s10 = 0, s11 = 0;
            foreach (var (w, khat) in cj.Kernels)
            {
                Complex h = w switch
                {
                    0 => Complex.One,
                    1 => new Complex(0, k * omega0),
                    _ => cj.Models[g].Weight(w, k * omega0)
                };
                var (q00, q01, q10, q11) = SpectrumBlock(khat, n, g, k, kap, h);
                s00 += q00; s01 += q01; s10 += q10; s11 += q11;
            }
            WriteBlockRect(B, dofG, Idx(n, k, false, N, K), Idx(n, k, true, N, K),
                Idx(g, kap, false, Gc, K), Idx(g, kap, true, Gc, K), s00, s01, s10, s11);
        }

        // R: ∂_c_ref/∂iNl_total  [dofG × dofN] — rRef rows, harmonic-diagonal.
        var R = new double[dofG * dofN];
        for (int g = 0; g < Gc; g++)
        for (int kap = 0; kap <= K; kap++)
        {
            double omegaKap = kap == 0 ? 0.0 : kap * omega0;
            var rRef = cc.Extractor.ControlSensitivityRow(omegaKap, cj.BranchIdx[g]);
            int rRe = Idx(g, kap, false, Gc, K);
            int rIm = Idx(g, kap, true,  Gc, K);
            for (int j = 0; j < N; j++)
            {
                Complex coeff = rRef[j];   // = ∂_c_ref/∂iNl[j] (sign baked into ControlSensitivityRow)
                int cRe = Idx(j, kap, false, N, K);
                int cIm = Idx(j, kap, true,  N, K);
                R[rRe * dofN + cRe] += coeff.Real;
                R[rRe * dofN + cIm] += -coeff.Imaginary;
                R[rIm * dofN + cRe] += coeff.Imaginary;
                R[rIm * dofN + cIm] += coeff.Real;
            }
        }

        // J_cc = B · R · A, with DC-Im rows/cols zeroed (match Maas's fictitious-DOF handling).
        var RA  = MatMul(R, dofG, dofN, A, dofN, dofN);   // [dofG × dofN]
        var BRA = MatMul(B, dofN, dofG, RA, dofG, dofN);  // [dofN × dofN]
        for (int r = 0; r < dofN; r++)
        {
            if (IsDcImDof(r, K)) continue;
            for (int c = 0; c < dofN; c++)
            {
                if (IsDcImDof(c, K)) continue;
                J[r * dofN + c] += BRA[r * dofN + c];
            }
        }
    }

    // Write a 2×2 real-split block into a square dof×dof matrix at output (n,k), input (m,i).
    private static void WriteBlock(double[] M, int dof, int n, int k, int m, int i, int K,
        double a00, double a01, double a10, double a11)
    {
        int r0 = Idx(n, k, false, 0, K), r1 = Idx(n, k, true, 0, K);
        int c0 = Idx(m, i, false, 0, K), c1 = Idx(m, i, true, 0, K);
        WriteBlockRect(M, dof, r0, r1, c0, c1, a00, a01, a10, a11);
    }

    private static void WriteBlockRect(double[] M, int cols, int r0, int r1, int c0, int c1,
        double a00, double a01, double a10, double a11)
    {
        if (r0 >= 0 && c0 >= 0) M[r0 * cols + c0] += a00;
        if (r0 >= 0 && c1 >= 0) M[r0 * cols + c1] += a01;
        if (r1 >= 0 && c0 >= 0) M[r1 * cols + c0] += a10;
        if (r1 >= 0 && c1 >= 0) M[r1 * cols + c1] += a11;
    }

    // Zero every entry whose row or column is a DC-imaginary (fictitious) DOF.
    private static void ZeroDcIm(double[] M, int rows, int cols, int K)
    {
        for (int r = 0; r < rows; r++)
        {
            bool rDc = IsDcImDof(r, K);
            for (int c = 0; c < cols; c++)
                if (rDc || IsDcImDof(c, K)) M[r * cols + c] = 0.0;
        }
    }

    // Dense real matrix multiply: (xr×xc)·(yr×yc) = (xr×yc), requires xc == yr.
    private static double[] MatMul(double[] X, int xr, int xc, double[] Y, int yr, int yc)
    {
        var R = new double[xr * yc];
        for (int r = 0; r < xr; r++)
        for (int kk = 0; kk < xc; kk++)
        {
            double xv = X[r * xc + kk];
            if (xv == 0.0) continue;
            int yrow = kk * yc;
            int rrow = r * yc;
            for (int c = 0; c < yc; c++)
                R[rrow + c] += xv * Y[yrow + c];
        }
        return R;
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
        ElaboratedNetlist netlist, int[] interfaceNodes, int gridN,
        ControlCurrentContext? cc = null,
        bool useControlJacobian = true)
    {
        double omega0 = 2.0 * Math.PI * f0;
        int dof = 2 * N * (K + 1);

        // Frozen control-current seed (brief #3 §0): the same iNlPrev is used for the
        // analytic Jacobian AND every FD evaluation, so pass-1's _c_ref seed is constant
        // across the perturbation and the two-pass map is differentiated self-consistently.
        Complex[,]? iNlSeed = null;
        if (cc is not null)
            iNlSeed = EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes, cc, null).iNl;

        // ── Analytic Jacobian at V ────────────────────────────────────────────
        var (iNl0, qNl0, G0, C0, buckets0) =
            EvaluateNonlinear(V, N, K, gridN, netlist, interfaceNodes, cc, iNlSeed, out var ctrlJac0);
        double[] analyticJ = BuildJ(yNN, G0, C0, N, K, omega0,
            higherBuckets: buckets0, ctrlJac: useControlJacobian ? ctrlJac0 : null, cc: cc);

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
            var (iNlp, qNlp, _, _, bucketsP) = EvaluateNonlinear(Vp, N, K, gridN, netlist, interfaceNodes, cc, iNlSeed);
            double[] Fp = BuildF(Vp, yNN, iSrc, iNlp, qNlp, N, K, omega0, bucketsP);

            // V-
            var Vm = (Complex[,])V.Clone();
            Vm[jNode, jHarm] += jIsIm ? new Complex(0, -eps) : new Complex(-eps, 0);
            var (iNlm, qNlm, _, _, bucketsM) = EvaluateNonlinear(Vm, N, K, gridN, netlist, interfaceNodes, cc, iNlSeed);
            double[] Fm = BuildF(Vm, yNN, iSrc, iNlm, qNlm, N, K, omega0, bucketsM);

            for (int r = 0; r < dof; r++)
                fdJ[r * dof + j] = (Fp[r] - Fm[r]) / (2.0 * eps);
        }

        // ── Scale for relative error ──────────────────────────────────────────
        // Global floor = globalScale × 1e-7: elements below this are treated as zero
        // (their value is unresolvable noise, not a derivative). This reflects a hard
        // FD limit, not a fudge: at eps=1e-6 the central difference of two F values whose
        // matrix peak is `globalScale` carries cancellation noise of ~globalScale·1e-12
        // absolute. An entry that is a *structural zero* (e.g. the diagonal of a purely
        // reactive H=jω self-block, where the whole magnitude rotates into the off-diagonal
        // Re↔Im block) therefore reads as ~1e-12·peak of pure roundoff. An eps-sweep makes
        // this unambiguous — such entries scale ~1/eps (roundoff) while real entries are
        // eps-flat. Flooring at 1e-8·peak claimed FD could resolve 8 orders below peak; it
        // cannot. 1e-7·peak keeps every genuinely-resolvable entry honest while not flagging
        // structural zeros as Jacobian errors. Real entries are unaffected (dom = |entry|).
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

            double domFloor = Math.Max(globalScale * 1e-7, 1e-12);
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

    // ── Dense linear solve: LU with partial pivoting, Gauss-Jordan below the crossover ────
    //
    // The solve runs once per Newton iteration on EVERY HB path (single-tone, two-tone and
    // T-tone), on a matrix that is structurally dense — every node-pair block is a Toeplitz+Hankel
    // conversion matrix and Y_NN fills the rest — so there is no sparsity to exploit and n^3/3 is
    // the right cost. Gauss-Jordan pays ~n^3 for the same answer, because it reduces every row
    // above AND below the pivot at every column; it also copies the matrix into an n×(n+1)
    // augmented buffer first, which doubles the transient for nothing.
    //
    // MEASURED (Release, Apple M4, single thread; Gauss-Jordan time / this LU's, best of ~20):
    //     n     8    16    24    32    48    64   124   172   256   378   512   756
    //   ratio 1.13  1.53  1.70  1.78  1.90  2.68  2.81  2.80  2.81  2.86  2.83  2.88
    // The LU is ahead from n ≈ 8 up and the margin widens to ~2.9× at the 6-tone/order-3 size
    // (n = 756: 84.6 ms → 29.4 ms). Below n = 8 Gauss-Jordan is marginally faster — both are well
    // under a microsecond there — which is what SolveCrossover records.
    //
    // NumFlat's Mat<double>.Lu() was measured on the same matrices and is deliberately NOT used:
    // it is not blocked (39.5 ms at n = 756 against this kernel's 29.4 ms), it is erratic at
    // power-of-two sizes (at n = 256 and n = 512 it falls back to roughly Gauss-Jordan's time), it
    // is slower than Gauss-Jordan below n ≈ 40, and it needs a copy into and out of Mat<double>
    // that this kernel does not. HbDenseSolveTests gates this implementation against it anyway.
    //
    // A blocked LU (panel factorisation + trailing GEMM) was written and measured too, and bought
    // nothing at these sizes — the trailing update is bound by issue rate, not cache traffic — so
    // the simpler right-looking form is what ships.

    /// <summary>
    /// Below this order the augmented Gauss-Jordan sweep is (marginally) faster than the LU
    /// factorisation; at and above it the LU wins and the margin grows. Measured — see the table
    /// in the source above this constant.
    /// </summary>
    public const int SolveCrossover = 8;

    /// <summary>
    /// Solves <c>A·x = b</c> for the dense real Newton system, returning <c>null</c> when the
    /// matrix is singular (pivot magnitude below 1e-30) — every caller branches on that and
    /// reports the singular Jacobian rather than throwing.
    ///
    /// <para>Public rather than internal so <c>HbDenseSolveTests</c> can drive both branches and
    /// compare them against each other and against NumFlat; <c>CircuitRF.Engine</c> exposes no
    /// internals to its test project.</para>
    /// </summary>
    public static double[]? SolveGaussian(double[] A, double[] b, int n)
        => n < SolveCrossover ? SolveGaussJordan(A, b, n) : SolveLu(A, b, n);

    /// <summary>
    /// In-place right-looking LU with partial pivoting on a private copy of <paramref name="A"/>,
    /// followed by the two triangular solves. The rank-1 trailing update is the whole cost and is
    /// explicitly vectorised; the row swap is full-width, so the multipliers already stored in the
    /// strict lower triangle travel with their row.
    /// </summary>
    public static double[]? SolveLu(double[] A, double[] b, int n)
    {
        var a = new double[n * n];
        Array.Copy(A, a, n * n);
        var piv = new int[n];
        int w = System.Numerics.Vector<double>.Count;

        for (int k = 0; k < n; k++)
        {
            int p = k;
            double best = Math.Abs(a[k * n + k]);
            for (int r = k + 1; r < n; r++)
            {
                double v = Math.Abs(a[r * n + k]);
                if (v > best) { best = v; p = r; }
            }
            if (best < 1e-30) return null;                 // same singularity test as Gauss-Jordan

            piv[k] = p;
            if (p != k)
                for (int j = 0; j < n; j++)
                    (a[k * n + j], a[p * n + j]) = (a[p * n + j], a[k * n + j]);

            double inv = 1.0 / a[k * n + k];
            int len = n - k - 1;
            if (len <= 0) continue;
            var rowK = a.AsSpan(k * n + k + 1, len);

            for (int r = k + 1; r < n; r++)
            {
                double f = a[r * n + k] * inv;
                a[r * n + k] = f;                          // multiplier, in place of the zero
                if (f == 0.0) continue;
                var rowR = a.AsSpan(r * n + k + 1, len);
                var vf   = new System.Numerics.Vector<double>(f);
                int j = 0;
                for (; j <= len - w; j += w)
                {
                    var acc = new System.Numerics.Vector<double>(rowR.Slice(j, w))
                            - vf * new System.Numerics.Vector<double>(rowK.Slice(j, w));
                    acc.CopyTo(rowR.Slice(j, w));
                }
                for (; j < len; j++) rowR[j] -= f * rowK[j];
            }
        }

        var x = new double[n];
        Array.Copy(b, x, n);
        for (int k = 0; k < n; k++) { int p = piv[k]; if (p != k) (x[k], x[p]) = (x[p], x[k]); }
        for (int i = 1; i < n; i++)                        // L·y = P·b (L has a unit diagonal)
        {
            double s = x[i];
            int rb = i * n;
            for (int j = 0; j < i; j++) s -= a[rb + j] * x[j];
            x[i] = s;
        }
        for (int i = n - 1; i >= 0; i--)                   // U·x = y
        {
            double s = x[i];
            int rb = i * n;
            for (int j = i + 1; j < n; j++) s -= a[rb + j] * x[j];
            x[i] = s / a[rb + i];
        }
        return x;
    }

    /// <summary>
    /// The original augmented Gauss-Jordan sweep, kept for the sub-<see cref="SolveCrossover"/>
    /// case (where it is faster) and as the reference <c>HbDenseSolveTests</c> compares against.
    /// </summary>
    public static double[]? SolveGaussJordan(double[] A, double[] b, int n)
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

    // ── Post-convergence port current extraction (C2) ────────────────────────
    //
    // Called ONCE after Newton convergence per sweep point — NOT in the Newton hot path.
    // FFTs each device's per-port terminal current to recover the complex harmonic spectrum.
    //
    // Returns dict keyed by "instancePath:terminalName" → Complex[K+1] spectrum.
    // Sign: current INTO the device at port p (passive sign convention), matching
    // INl[portPlusIdx[p], k] when that node belongs exclusively to this device.
    // Values are therefore numerically identical to INl — just re-housed by named branch.
    //
    // A TERMINAL CURRENT IS A SUM OVER WEIGHTING INDICES, not res.I alone:
    //   I[p,k] = Σ_w H[w](k·ω₀) · FT{ I[p,w](t) }_k
    // with H[0]=1 (conduction), H[1]=jω (charge) and H[w≥2] the device's own weight function —
    // the same sum the residual forms at the node. Taking only w=0 published every charge-carrying
    // port as exactly zero (a NonlinearC's res.I[p] is identically 0 at every sample), and dropped
    // the displacement half of a FET terminal current.
    //
    // WHERE THE TIME SERIES COMES FROM (HB-P2 M3). Preferably from `portITime` — the buffer the
    // last Newton device pass filled, which is by construction an evaluation at the same converged
    // V this method is handed. Re-deriving it here meant evaluating every device at every sample a
    // second time for numbers that were already in hand (~150 us of a ~750 us warm Hero-2 Run).
    //
    // The re-evaluation remains, and is taken in the two cases where the buffer is not the right
    // answer or not available:
    //   • cc != null — control currents. The post-solve currents must be evaluated at the CONVERGED
    //     _c_ref (cRefTimePost below, back-solved from the converged INl), which the last Newton
    //     pass did not use: its own _c_ref seed lags by one iterate. That is a different evaluation
    //     of the same device, not a cheaper route to the same one.
    //   • no buffer, or one whose shape does not match this (device count, port count, gridN) —
    //     a caller that did not ask for one, or asked at a different grid size.

    public static Dictionary<string, Complex[]> ComputeDevicePortCurrents(
        Complex[,]        V,
        int               N,
        int               K,
        int               gridN,
        double            f0,
        ElaboratedNetlist netlist,
        int[]             interfaceNodes,
        ControlCurrentContext? cc           = null,
        Complex[,]?            iNlConverged = null,
        PortTermTimes[]?       portITime    = null)
    {
        double omega0     = 2.0 * Math.PI * f0;
        bool   useLastPass = cc is null && MatchesShape(portITime, netlist, gridN);

        // IFFT V to time domain — needed only when a device is actually re-evaluated.
        double[][]? vTime = null;
        if (!useLastPass)
        {
            vTime = new double[N][];
            for (int n = 0; n < N; n++)
            {
                vTime[n] = new double[gridN];
                var Xn = new Complex[K + 1];
                for (int k = 0; k <= K; k++) Xn[k] = V[n, k];
                HbFft.Inverse(Xn, K, vTime[n]);
            }
        }

        var result = new Dictionary<string, Complex[]>(StringComparer.Ordinal);

        for (int devOrd = 0; devOrd < netlist.NonlinearComponents.Count; devOrd++)
        {
            int    nlIdx     = netlist.NonlinearComponents[devOrd];
            var    ec        = netlist.Components[nlIdx];
            int    portCount = ec.Model.PortCount;
            string[] terms   = ec.Model.TerminalNames;

            if (useLastPass)
            {
                Emit(result, ec, terms, portCount, gridN, K, omega0, portITime![devOrd]);
                continue;
            }

            var portPlusIdx  = new int[portCount];
            var portMinusIdx = new int[portCount];
            for (int p = 0; p < portCount; p++)
            {
                int np = ec.Nodes.Length > 2*p   ? ec.Nodes[2*p]   : 0;
                int nm = ec.Nodes.Length > 2*p+1 ? ec.Nodes[2*p+1] : 0;
                portPlusIdx[p]  = Array.IndexOf(interfaceNodes, np);
                portMinusIdx[p] = Array.IndexOf(interfaceNodes, nm);
            }

            // Control currents at the converged state (same approach as EvaluateNonlinear).
            double[,]? cRefTimePost = null;
            if (cc is not null && ec.Model is SddModel sddPostCtrl && sddPostCtrl.ControlRefs.Length > 0)
            {
                int m = sddPostCtrl.ControlRefs.Length;
                var specByCtrl = new Complex[m, K + 1];
                var iNlK = new Complex[N];
                for (int k = 0; k <= K; k++)
                {
                    for (int n = 0; n < N; n++)
                        iNlK[n] = iNlConverged is not null ? iNlConverged[n, k] : Complex.Zero;
                    double omegaK = k == 0 ? 0.0 : k * 2.0 * Math.PI * cc.F0;
                    var xK = cc.Extractor.SolveFullNetwork(omegaK, iNlK, cc.BSrc[k]);
                    for (int ci = 0; ci < m; ci++)
                    {
                        int brIdx = sddPostCtrl.ControlBranchIndices[ci];
                        specByCtrl[ci, k] = brIdx >= 0 && brIdx < xK.Length
                            ? xK[brIdx] : Complex.Zero;
                    }
                }
                cRefTimePost = new double[m, gridN];
                var specBuf = new Complex[K + 1];
                var timeBuf = new double[gridN];
                for (int ci = 0; ci < m; ci++)
                {
                    for (int k = 0; k <= K; k++) specBuf[k] = specByCtrl[ci, k];
                    HbFft.Inverse(specBuf, K, timeBuf);
                    for (int t = 0; t < gridN; t++) cRefTimePost[ci, t] = timeBuf[t];
                }
            }

            var portTermsLocal = new PortTermTimes(portCount, gridN);

            // Same rule as the Newton pass: one round trip for the grid where an evaluation costs
            // one, and the untouched scalar path everywhere else.
            IReadOnlyList<NonlinearResult>? batch =
                ec.PrefersBatchEvaluate && cRefTimePost is null
                    ? ec.EvaluateBatch(GatherPortVoltages(vTime!, gridN, portCount, portPlusIdx, portMinusIdx))
                    : null;

            GridResult? grid = null;
            HbGridSampler? sampler = null;
            if (batch is null && ec.PrefersGridEvaluate && cRefTimePost is null)
            {
                var pv = HbGridBuffers.PortVBuffer(gridN, portCount);
                for (int p = 0; p < portCount; p++)
                {
                    int ip = portPlusIdx[p], im = portMinusIdx[p];
                    int bse = p * gridN;
                    for (int t = 0; t < gridN; t++)
                        pv[bse + t] = (ip >= 0 ? vTime![ip][t] : 0.0) - (im >= 0 ? vTime![im][t] : 0.0);
                }
                grid = HbGridBuffers.Result(devOrd);
                ec.EvaluateGrid(pv.AsSpan(0, portCount * gridN), [], gridN, grid);
                sampler = HbGridBuffers.Sampler();
            }

            for (int t = 0; t < gridN; t++)
            {
                NonlinearResult res;
                if (batch is not null)
                    res = batch[t];
                else if (grid is not null)
                    res = sampler!.Sample(grid, t);
                else
                {
                    var portV = new double[portCount];
                    for (int p = 0; p < portCount; p++)
                    {
                        double vp = portPlusIdx[p]  >= 0 ? vTime![portPlusIdx[p]][t]  : 0.0;
                        double vm = portMinusIdx[p] >= 0 ? vTime![portMinusIdx[p]][t] : 0.0;
                        portV[p] = vp - vm;
                    }
                    if (cRefTimePost is not null)
                    {
                        int m = ((SddModel)ec.Model).ControlRefs.Length;
                        var cVals = new double[m];
                        for (int ci = 0; ci < m; ci++) cVals[ci] = cRefTimePost[ci, t];
                        res = ec.Evaluate(new PortVoltages(portV), new ControlCurrents(cVals));
                    }
                    else
                        res = ec.Evaluate(new PortVoltages(portV));
                }
                for (int p = 0; p < portCount; p++)
                {
                    portTermsLocal.W0[p, t] = res.I[p];
                    portTermsLocal.W1[p, t] = res.Q[p];
                }
                foreach (var term in res.Terms)
                {
                    var devW = portTermsLocal.HigherFor(term.W);
                    for (int p = 0; p < portCount; p++) devW[p, t] = term.Value[p];
                }
            }

            Emit(result, ec, terms, portCount, gridN, K, omega0, portTermsLocal);
        }

        return result;
    }

    /// <summary>FFT each port's per-weighting-index time series, weight them by H[w](k·ω₀) and sum →
    /// the terminal current spectrum, emitted under both the terminal name and a 0-based port-index
    /// alias ("M1:0", "M1:1", …) so a generic SDD can be addressed by port number regardless of
    /// terminal names.</summary>
    private static void Emit(Dictionary<string, Complex[]> result, ElaboratedComponent ec,
        string[] terms, int portCount, int gridN, int K, double omega0, PortTermTimes pt)
    {
        var iX = new double[gridN];
        for (int p = 0; p < portCount; p++)
        {
            string term = p < terms.Length ? terms[p] : (p + 1).ToString();
            string key  = $"{ec.InstancePath}:{term}";

            for (int t = 0; t < gridN; t++) iX[t] = pt.W0[p, t];
            HbFft.Forward(iX, K, out var iAmpl, out _);

            for (int t = 0; t < gridN; t++) iX[t] = pt.W1[p, t];
            HbFft.Forward(iX, K, out var qAmpl, out _);
            for (int k = 1; k <= K; k++)
                iAmpl[k] += ec.Model.Weight(1, k * omega0) * qAmpl[k];

            if (pt.Higher is { Count: > 0 })
                foreach (var (w, wTime) in pt.Higher)
                {
                    for (int t = 0; t < gridN; t++) iX[t] = wTime[p, t];
                    HbFft.Forward(iX, K, out var wAmpl, out _);
                    for (int k = 0; k <= K; k++)
                        iAmpl[k] += ec.Model.Weight(w, k * omega0) * wAmpl[k];
                }

            result[key] = iAmpl;

            string numKey = $"{ec.InstancePath}:{p}";
            if (numKey != key) result[numKey] = iAmpl;
        }
    }

    /// <summary>
    /// Whether a last-pass buffer describes THIS netlist at THIS grid size — device count, per-device
    /// port count and sample count all checked. A mismatch is a caller error rather than a data
    /// error, so it falls back to re-evaluation instead of throwing.
    /// </summary>
    private static bool MatchesShape(PortTermTimes[]? buf, ElaboratedNetlist netlist, int gridN)
    {
        if (buf is null || buf.Length != netlist.NonlinearComponents.Count) return false;
        for (int i = 0; i < buf.Length; i++)
        {
            var ec = netlist.Components[netlist.NonlinearComponents[i]];
            if (buf[i].PortCount != ec.Model.PortCount || buf[i].GridN != gridN) return false;
        }
        return true;
    }
}
