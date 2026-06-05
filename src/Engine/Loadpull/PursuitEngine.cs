using System.Numerics;
using RfCore;

namespace CircuitRF.Engine.Loadpull;

/// <summary>
/// Selectable search method for <see cref="PursuitEngine"/>.
/// Default is <see cref="SteepestAscent"/> (the committed Baylis algorithm).
/// Open to future additions.
/// </summary>
public enum SearchMethod
{
    /// <summary>
    /// Baylis fixed-direction steepest-ascent line search (loadpull_pursuit.md §1.1, §1.1.1).
    /// Fits the gradient once per leg, ascends along that ray, shrinks on rejection.
    /// Default and backwards-compatible.
    /// </summary>
    SteepestAscent,

    /// <summary>
    /// Trust-region iterated-quadratic search (loadpull_pursuit.md §1.1.2).
    /// Re-fits curvature at every iterate; jumps toward the quadratic optimum where the
    /// Hessian is negative-definite, degrades to a gradient step otherwise.
    /// </summary>
    IteratedQuadratic,
}

/// <summary>
/// Baylis steepest-ascent search for MXP (max output power) or MXE (max efficiency)
/// terminations in the Z-plane (Re/Im in Ω).
///
/// Algorithm:
///   1. Tangent-plane stage: query 2 neighbours at Dn VSWR in the +Re(Z) and +Im(Z)
///      directions (step lengths computed via exact VswrFromZ root-find), fit
///      ΔC = m1·ΔRe(Z) + m2·ΔIm(Z), compute steepest-ascent unit direction in Z-space.
///   2. Ascend by Ds VSWR along that direction (exact step via VswrFromZ root-find);
///      accept if criterion improves; on failure shrink the VSWR-excess-over-unity by /3:
///        ds = 1 + (ds−1)/3       (so 1.3→1.1→1.033→1.011…, always VSWR ≥ 1)
///      Converge when ds &lt; ConvergenceThreshold (a VSWR just above 1).
///   3. Polynomial refinement in Z-space with 4 exact Dn-VSWR cardinal neighbours.
///      Fallback: if the polynomial can't find an improvement, step to the best-scored
///      cardinal that beats the current point.
///
/// Three bug-fixes from the 2026-06-04 diagnostic:
///   Fix 1 — ds stays VSWR ≥ 1 throughout (excess-over-unity shrink).
///            Old: ds /= 3 → 0.433 &lt; threshold 1.05, exited after 1 rejection.
///            New: ds = 1+(ds-1)/3 → converges over 3-4 shrink steps.
///   Fix 2 — exact VSWR step via FindStepLength bisection; VswrToDeltaGamma deleted.
///            Old: (vswr-1)/(vswr+1) approximation (0–3% error, grows with Γ, needs Z0).
///            New: exact bilinear VSWR from VswrFromZ, no Z0, no approximation.
///   Fix 3 — gradient and stepping in the raw Z-plane (Ω), no Γ, no Z0.
///            Old: internal Γ representation with Z0=50 baked in.
///            New: tangent-plane neighbours placed at exact Dn VSWR in ±Re/±Im Z directions.
/// </summary>
public sealed class PursuitEngine
{
    // ── Tunable parameters ────────────────────────────────────────────────────

    /// <summary>VSWR step for tangent-plane neighbours (Dn).</summary>
    public double Dn { get; init; } = 1.05;

    /// <summary>Initial ascent step size (Ds, VSWR ≥ 1).</summary>
    public double DsInitial { get; init; } = 1.3;

    /// <summary>
    /// Convergence threshold: when Ds (VSWR) falls below this the ascent terminates
    /// and polynomial refinement runs.  Must satisfy 1 &lt; threshold &lt; DsInitial.
    /// Default 1.02 ≈ negligible impedance step (&lt;1% VSWR excess).
    /// </summary>
    public double ConvergenceThreshold { get; init; } = 1.02;

    /// <summary>Maximum ascent iterations (safety cap).</summary>
    public int MaxAscentSteps { get; init; } = 40;

    /// <summary>
    /// Search method to use.  Default is <see cref="SearchMethod.SteepestAscent"/>;
    /// the committed, passing Baylis algorithm.  <see cref="SearchMethod.IteratedQuadratic"/>
    /// re-fits curvature at every iterate (loadpull_pursuit.md §1.1.2).
    /// </summary>
    public SearchMethod Method { get; init; } = SearchMethod.SteepestAscent;

    /// <summary>
    /// Optional diagnostic logger.  When set, the engine writes step-by-step diagnostics
    /// (prefix "[PE]" for SteepestAscent, "[IQ]" for IteratedQuadratic) to this writer.
    /// </summary>
    public TextWriter? Log { get; init; }

    // ── Result type ───────────────────────────────────────────────────────────

    public sealed class PursuitResult
    {
        public Complex OptimumZ     { get; }
        public double  OptimumValue { get; }
        public IReadOnlyList<(Complex Z, double? Value)> AllQueries { get; }
        public IReadOnlyList<Complex> UnscorableZ { get; }
        public bool    Converged    { get; }
        public string? AbortReason  { get; }

        public PursuitResult(Complex optimumZ, double optimumValue,
            IReadOnlyList<(Complex, double?)> allQueries,
            IReadOnlyList<Complex> unscorableZ,
            bool converged, string? abortReason = null)
        {
            OptimumZ     = optimumZ;
            OptimumValue = optimumValue;
            AllQueries   = allQueries;
            UnscorableZ  = unscorableZ;
            Converged    = converged;
            AbortReason  = abortReason;
        }
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Run the configured search method starting from <paramref name="startZ"/> (Ω).
    /// <paramref name="criterion"/> returns the scalar criterion at a candidate Z, or null
    /// if unscorable.  The criterion delegate is the ONLY way scores are obtained — never
    /// call the loadpull engine directly, so the VSWR-dedup cache applies automatically.
    /// </summary>
    public PursuitResult Run(Complex startZ, Func<Complex, double?> criterion) =>
        Method switch
        {
            SearchMethod.IteratedQuadratic => RunIteratedQuadratic(startZ, criterion),
            _                              => RunSteepestAscent(startZ, criterion),
        };

    // ── SteepestAscent (Baylis, the committed default) ────────────────────────

    private PursuitResult RunSteepestAscent(Complex startZ, Func<Complex, double?> criterion)
    {
        var queries    = new List<(Complex Z, double? Value)>();
        var unscorable = new List<Complex>();

        double? Score(Complex z)
        {
            if (z.Real <= 0) return null;   // physical guard: passive termination only
            var v = criterion(z);
            queries.Add((z, v));
            if (v is null) unscorable.Add(z);
            return v;
        }

        // ── 1. Tangent-plane stage in Z-space ─────────────────────────────────
        double? c0 = Score(startZ);
        Log?.WriteLine($"[PE] Tangent.Start: Z={FmtZ(startZ)} c0={c0:G6}");
        if (c0 is null)
            return Abort(startZ, queries, unscorable,
                $"Start point Z={startZ} is unscorable — DUT does not compress; " +
                "raise PinMax or check bias/load.");

        // Two neighbours at exactly Dn VSWR: +Re(Z) and +Im(Z) directions.
        // Fix 3: Z-plane directions; Fix 2: exact step via FindStepLength.
        double  len1 = FindStepLength(startZ, new Complex(1, 0), Dn);
        double  len2 = FindStepLength(startZ, new Complex(0, 1), Dn);
        Complex n1Z  = startZ + new Complex(len1, 0);
        Complex n2Z  = startZ + new Complex(0,    len2);

        double? c1 = Score(n1Z);
        Log?.WriteLine($"[PE] Tangent.N1: Z={FmtZ(n1Z)} VSWR={RfHelpers.VswrFromZ(startZ, n1Z):F4} c1={c1?.ToString("G6") ?? "null"}");
        double? c2 = Score(n2Z);
        Log?.WriteLine($"[PE] Tangent.N2: Z={FmtZ(n2Z)} VSWR={RfHelpers.VswrFromZ(startZ, n2Z):F4} c2={c2?.ToString("G6") ?? "null"}");

        if (c1 is null && c2 is null)
            return Abort(startZ, queries, unscorable,
                "Both tangent-plane neighbours are unscorable — cannot form a gradient; " +
                "try a different start point.");

        // Mirror unscorable neighbours through startZ (B6: no negative-R probes).
        if (c1 is null)
        {
            n1Z = 2 * startZ - n1Z;
            c1  = Score(n1Z);
            Log?.WriteLine($"[PE] Tangent.N1.Mirror: Z={FmtZ(n1Z)} c1={c1:G6}");
        }
        if (c2 is null)
        {
            n2Z = 2 * startZ - n2Z;
            c2  = Score(n2Z);
            Log?.WriteLine($"[PE] Tangent.N2.Mirror: Z={FmtZ(n2Z)} c2={c2:G6}");
        }

        // Fit ΔC = m1·ΔRe(Z) + m2·ΔIm(Z) in Z-space (Baylis Eq. 1).
        double dx1 = n1Z.Real - startZ.Real, dy1 = n1Z.Imaginary - startZ.Imaginary;
        double dx2 = n2Z.Real - startZ.Real, dy2 = n2Z.Imaginary - startZ.Imaginary;
        (double m1, double m2) = FitLinearPlane(
            dx1, dy1, (c1 ?? c0.Value) - c0.Value,
            dx2, dy2, (c2 ?? c0.Value) - c0.Value);

        // Steepest-ascent unit vector in Z-plane.
        double gradMag = Math.Sqrt(m1 * m1 + m2 * m2);
        Log?.WriteLine($"[PE] Gradient: m1={m1:G4} m2={m2:G4} gradMag={gradMag:G4}");
        if (gradMag < 1e-20)
        {
            Log?.WriteLine("[PE] Gradient: flat — returning start as optimum");
            return new PursuitResult(startZ, c0.Value, queries, unscorable, converged: true);
        }
        double ux = m1 / gradMag, uy = m2 / gradMag;
        Log?.WriteLine($"[PE] Gradient: ux={ux:G4} uy={uy:G4}  (unit vector in Z-plane, Ω)");

        // ── 2. Ascent loop ─────────────────────────────────────────────────────
        double  ds      = DsInitial;   // always VSWR ≥ 1; Fix 1: excess-over-unity shrink
        Complex curZ    = startZ;
        double  cCur    = c0.Value;
        var     history = new List<(Complex Z, double C)> { (startZ, c0.Value) };

        Log?.WriteLine($"[PE] Ascent.Loop: DsInitial={DsInitial} ConvergenceThreshold={ConvergenceThreshold} MaxSteps={MaxAscentSteps}");

        for (int step = 0; step < MaxAscentSteps; step++)
        {
            Log?.WriteLine($"[PE] Ascent.Check: step={step} ds={ds:G4} threshold={ConvergenceThreshold} → {(ds < ConvergenceThreshold ? "TERMINATE" : "continue")}");
            if (ds < ConvergenceThreshold)
            {
                Log?.WriteLine($"[PE] Ascent.Terminate: ds={ds:G4} < threshold={ConvergenceThreshold} after {step} iterations");
                break;
            }

            // Fix 2: exact VSWR step length via bisection.
            double  stepLen = FindStepLength(curZ, new Complex(ux, uy), ds);
            Complex candZ   = curZ + new Complex(ux, uy) * stepLen;

            if (candZ.Real <= 0)
            {
                // Gradient direction exits the passive half-plane — shrink and retry.
                double dsOld = ds;
                ds = 1.0 + (ds - 1.0) / 3.0;
                Log?.WriteLine($"[PE] Ascent.NonPhysical: step={step} candZ.Re={candZ.Real:F2} → shrink ds: {dsOld:G4} → {ds:G4}");
                continue;
            }

            Log?.WriteLine($"[PE] Ascent.Step: step={step} ds={ds:G4} stepLen={stepLen:F2}Ω curZ={FmtZ(curZ)} candZ={FmtZ(candZ)} trueVSWR={RfHelpers.VswrFromZ(curZ, candZ):F4}");
            double? cCand = Score(candZ);

            if (cCand is not null && cCand.Value > cCur)
            {
                Log?.WriteLine($"[PE] Ascent.Accept: step={step} crit={cCand:G6} > prev={cCur:G6} Δ={cCand.Value - cCur:G4} ds_unchanged={ds:G4}");
                history.Add((candZ, cCand.Value));
                curZ = candZ;
                cCur = cCand.Value;
            }
            else
            {
                // Fix 1: shrink VSWR-excess-over-unity; ds stays ≥ 1.
                double dsOld = ds;
                ds = 1.0 + (ds - 1.0) / 3.0;
                Log?.WriteLine($"[PE] Ascent.Reject: step={step} crit={cCand?.ToString("G6") ?? "null"} <= prev={cCur:G6}  ds: {dsOld:G4} → {ds:G4}");
            }
        }

        Log?.WriteLine($"[PE] Ascent.Done: curZ={FmtZ(curZ)} cCur={cCur:G6} ds_final={ds:G4} accepted_steps={history.Count - 1}");

        // ── 3. Polynomial refinement in Z-space (Baylis Eq. 4) ────────────────
        // Four exact Dn-VSWR cardinal neighbours in ±Re, ±Im directions.
        var refineDir = new[]
        {
            new Complex( 1,  0),   // +Re(Z)
            new Complex(-1,  0),   // −Re(Z)
            new Complex( 0,  1),   // +Im(Z)
            new Complex( 0, -1),   // −Im(Z)
        };

        Log?.WriteLine($"[PE] Refine.Start: curZ={FmtZ(curZ)}");
        var poly            = new List<(double Dx, double Dy, double Dc)> { (0, 0, 0) };
        var scoredCardinals = new List<(Complex Z, double C)>();

        foreach (var dir in refineDir)
        {
            double  rLen = FindStepLength(curZ, dir, Dn);
            Complex rZ   = curZ + dir * rLen;
            if (rZ.Real <= 0) continue;
            double? rc = Score(rZ);
            Log?.WriteLine($"[PE] Refine.Cardinal: Z={FmtZ(rZ)} VSWR={RfHelpers.VswrFromZ(curZ, rZ):F4} crit={rc?.ToString("G6") ?? "null"}");
            if (rc is null) continue;
            scoredCardinals.Add((rZ, rc.Value));
            poly.Add(((rZ - curZ).Real, (rZ - curZ).Imaginary, rc.Value - cCur));
        }

        Complex optimumZ  = curZ;
        double  cOptimum  = cCur;
        bool    polyMoved = false;

        if (poly.Count >= 5)   // origin + all 4 cardinals
        {
            var (mm1, mm2, mm11, mm12, mm22) = FitQuadraticSurface(poly);
            Complex delta  = SolveQuadraticOptimum(mm1, mm2, mm11, mm12, mm22);
            Complex candZ2 = curZ + delta;   // delta in Z-plane (Ω)
            if (delta != Complex.Zero &&
                candZ2.Real > 0 &&
                RfHelpers.VswrFromZ(candZ2, curZ) < 2.0 * Dn + 1.0)
            {
                optimumZ  = candZ2;
                polyMoved = true;
                Log?.WriteLine($"[PE] Refine.Result: poly_accepted Z={FmtZ(optimumZ)} VSWR_from_cur={RfHelpers.VswrFromZ(candZ2, curZ):G4}");
            }
            else
            {
                Log?.WriteLine($"[PE] Refine.Result: poly_rejected (delta={delta.Real:G4}+{delta.Imaginary:G4}j) → best-cardinal fallback");
            }
        }
        else
        {
            Log?.WriteLine($"[PE] Refine.Result: too_few_cardinals ({poly.Count}) → best-cardinal fallback");
        }

        // Fallback: if polynomial didn't move, step to the best-scored cardinal if it improves.
        if (!polyMoved)
        {
            bool improved = false;
            foreach (var (cZ, cC) in scoredCardinals)
            {
                if (cC > cOptimum)
                {
                    cOptimum = cC;
                    optimumZ = cZ;
                    improved = true;
                    Log?.WriteLine($"[PE] Refine.BestCardinal: Z={FmtZ(cZ)} crit={cC:G6} — beats curZ");
                }
            }
            if (!improved)
                Log?.WriteLine("[PE] Refine.BestCardinal: no cardinal improves on curZ — staying put");
        }

        Log?.WriteLine($"[PE] Final: optimumZ={FmtZ(optimumZ)} cOptimum={cOptimum:G6} total_queries={queries.Count}");
        return new PursuitResult(optimumZ, cOptimum, queries, unscorable, converged: true);
    }

    // ── IteratedQuadratic (trust-region iterated quadratic) ───────────────────

    /// <summary>
    /// Trust-region iterated-quadratic search (loadpull_pursuit.md §1.1.2).
    ///
    /// At each iterate: places 4 cardinal neighbours at the trust-region radius R (VSWR,
    /// exact via FindStepLength), fits the local quadratic surface, and jumps toward its
    /// analytic optimum if the Hessian is negative-definite and the optimum is within the
    /// trust region.  If not, falls back to a gradient step using the same fit's linear
    /// part — degrading gracefully to steepest ascent where curvature is unusable.
    ///
    /// The quadratic is fitted as a DECOUPLED model — ΔC = m1·Δx + m2·Δy + ½(m11·Δx² + m22·Δy²)
    /// — using separate 1-D fits per axis (Re and Im).  This avoids the singular AtA matrix
    /// that arises from the full 5-parameter fit when all cardinal probes are axis-aligned
    /// (the cross-term column ΔxΔy is identically zero for axis-aligned cardinals, making
    /// Solve5x5 return all-zeros and the gradient appear flat).  With m12=0 the Hessian is
    /// diagonal, and SolveQuadraticOptimum reduces to delta = (−m1/m11, −m2/m22).
    ///
    /// Scores obtained ONLY via the criterion delegate, so the VSWR-dedup cache in
    /// LoadpullPursuitEngine applies automatically.  Tracks and returns the best point
    /// seen across all iterations (including scored cardinals).
    /// </summary>
    private PursuitResult RunIteratedQuadratic(Complex startZ, Func<Complex, double?> criterion)
    {
        var queries    = new List<(Complex Z, double? Value)>();
        var unscorable = new List<Complex>();

        double? Score(Complex z)
        {
            if (z.Real <= 0) return null;   // physical guard: passive termination only
            var v = criterion(z);
            queries.Add((z, v));
            if (v is null) unscorable.Add(z);
            return v;
        }

        // 1. Seed at startZ.
        double? c0 = Score(startZ);
        Log?.WriteLine($"[IQ] Seed: Z={FmtZ(startZ)} c0={c0:G6}");
        if (c0 is null)
            return Abort(startZ, queries, unscorable,
                $"Start point Z={startZ} is unscorable — DUT does not compress; " +
                "raise PinMax or check bias/load.");

        Complex curZ  = startZ;
        double  cCur  = c0.Value;
        Complex bestZ = startZ;
        double  bestC = c0.Value;

        // 2. Trust-region radius R (VSWR ≥ 1), initialized to DsInitial.
        //    Shrink rule (same as SteepestAscent): R = 1 + (R-1)/3 on rejection.
        //    Grow rule on quadratic improvement: R = min(DsInitial, 1 + (R-1)*2).
        double R = DsInitial;

        // dirs[0]=+Re, [1]=−Re, [2]=+Im, [3]=−Im.
        // isRe[i]: dirs 0 and 1 are on the Re axis; dirs 2 and 3 are on the Im axis.
        var dirs  = new Complex[] { new Complex(1,0), new Complex(-1,0), new Complex(0,1), new Complex(0,-1) };
        var isRe  = new bool[]    { true,              true,              false,             false             };

        Log?.WriteLine($"[IQ] Loop: DsInitial={DsInitial} ConvergenceThreshold={ConvergenceThreshold} MaxSteps={MaxAscentSteps}");

        for (int step = 0; step < MaxAscentSteps; step++)
        {
            Log?.WriteLine($"[IQ] Check: step={step} R={R:G4} threshold={ConvergenceThreshold}  curZ={FmtZ(curZ)} cCur={cCur:G6}");
            if (R < ConvergenceThreshold)
            {
                Log?.WriteLine($"[IQ] Converged: R={R:G4} < threshold={ConvergenceThreshold} after {step} steps");
                break;
            }

            // 3a. Place 4 cardinal neighbours at R VSWR in ±Re(Z), ±Im(Z).
            //     Mirror any unscorable cardinal through curZ (no negative-R probes).
            //     Collect Re-axis and Im-axis points separately for the decoupled 1-D fit.
            var rePts = new List<(double Du, double Dc)>();   // (ΔRe, ΔC) from Re-axis cardinals
            var imPts = new List<(double Du, double Dc)>();   // (ΔIm, ΔC) from Im-axis cardinals
            int nScored = 0;

            for (int d = 0; d < dirs.Length; d++)
            {
                var dir = dirs[d];
                double  len   = FindStepLength(curZ, dir, R);
                Complex cardZ = curZ + dir * len;

                // Physical guard: mirror immediately if the placed point is non-physical.
                if (cardZ.Real <= 0)
                    cardZ = 2 * curZ - cardZ;

                if (cardZ.Real <= 0) continue;   // both endpoints non-physical — skip

                double? cardC = Score(cardZ);
                Log?.WriteLine($"[IQ] Cardinal: dir=({dir.Real:G2},{dir.Imaginary:G2}) Z={FmtZ(cardZ)} VSWR={RfHelpers.VswrFromZ(curZ, cardZ):F4} c={cardC?.ToString("G6") ?? "null"}");

                // If unscorable (non-convergent), mirror through curZ and retry.
                if (cardC is null)
                {
                    Complex mirZ = 2 * curZ - cardZ;
                    if (mirZ.Real > 0)
                    {
                        cardC = Score(mirZ);
                        if (cardC is not null) cardZ = mirZ;
                        Log?.WriteLine($"[IQ] Cardinal.Mirror: Z={FmtZ(mirZ)} c={cardC?.ToString("G6") ?? "null"}");
                    }
                }

                if (cardC is not null)
                {
                    nScored++;
                    // Classify by axis: compute the axial displacement from curZ.
                    double du = isRe[d]
                        ? (cardZ.Real      - curZ.Real)
                        : (cardZ.Imaginary - curZ.Imaginary);
                    if (isRe[d]) rePts.Add((du, cardC.Value - cCur));
                    else          imPts.Add((du, cardC.Value - cCur));
                    if (cardC.Value > bestC) { bestC = cardC.Value; bestZ = cardZ; }
                }
            }

            // 3b. Fit decoupled 1-D quadratics per axis.
            //     Full 5-parameter FitQuadraticSurface cannot be used here: with axis-aligned
            //     cardinals the ΔxΔy cross-term column is identically zero, making AtA singular
            //     and Solve5x5 return all-zeros (flat apparent gradient).  Instead fit each axis
            //     independently, setting m12=0 (unobservable from axis-aligned probes).
            if (nScored == 0)
            {
                // No scored cardinals — shrink R and retry.
                double Rold = R;
                R = 1.0 + (R - 1.0) / 3.0;
                Log?.WriteLine($"[IQ] TooFewPoints(0): shrink R {Rold:G4} → {R:G4}");
                continue;
            }

            var (m1, m11) = FitAxis1D(rePts);
            var (m2, m22) = FitAxis1D(imPts);

            // 3c. Decide jump direction: quadratic optimum (if Hessian negative-definite
            //     and within/near trust region) or gradient fallback (same fit, linear part).
            // With m12=0 (decoupled): Hessian det = m11*m22 > 0 iff both m11<0 and m22<0.
            // SolveQuadraticOptimum then gives delta = (−m1/m11, −m2/m22).
            Complex delta     = SolveQuadraticOptimum(m1, m2, m11, 0.0, m22);
            Complex? jumpZ    = null;
            bool     isQuad   = false;

            if (delta != Complex.Zero)
            {
                // Hessian is negative-definite: check containment in trust region.
                Complex candQ = curZ + delta;
                double  vswrQ = candQ.Real > 0
                    ? RfHelpers.VswrFromZ(curZ, candQ)
                    : double.MaxValue;

                // Accept the quadratic jump if within / just outside the trust region.
                // Clamp to the trust boundary if it lands just outside (vswrQ ≤ R*1.5).
                if (vswrQ <= R * 1.5 && candQ.Real > 0)
                {
                    if (vswrQ > R)
                    {
                        // Clamp: project the delta direction to exactly R VSWR.
                        double dLen = delta.Magnitude;
                        if (dLen > 1e-20)
                        {
                            Complex ddir = delta / dLen;
                            double  sl   = FindStepLength(curZ, ddir, R);
                            candQ = curZ + ddir * sl;
                        }
                    }
                    if (candQ.Real > 0)
                    {
                        jumpZ  = candQ;
                        isQuad = true;
                    }
                }
                // Else: optimum clearly outside trust region → fall through to gradient.
            }

            if (jumpZ is null)
            {
                // Gradient step: use the linear part (m1, m2) of the same quadratic fit
                // as the ascent direction — identical to SteepestAscent behavior.
                double gradMag = Math.Sqrt(m1 * m1 + m2 * m2);
                if (gradMag < 1e-20)
                {
                    // Flat criterion surface at this scale — shrink R and retry.
                    double Rold = R;
                    R = 1.0 + (R - 1.0) / 3.0;
                    Log?.WriteLine($"[IQ] FlatGradient: shrink R {Rold:G4} → {R:G4}");
                    continue;
                }
                double  ux = m1 / gradMag, uy = m2 / gradMag;
                double  sl = FindStepLength(curZ, new Complex(ux, uy), R);
                Complex gZ = curZ + new Complex(ux, uy) * sl;
                if (gZ.Real > 0) jumpZ = gZ;
            }

            if (jumpZ is null)
            {
                // No valid move in any direction (exits physical region) — shrink R.
                double Rold = R;
                R = 1.0 + (R - 1.0) / 3.0;
                Log?.WriteLine($"[IQ] NoValidJump: shrink R {Rold:G4} → {R:G4}");
                continue;
            }

            // Score the candidate.
            double? cJump = Score(jumpZ.Value);
            Log?.WriteLine($"[IQ] Jump({(isQuad ? "quadratic" : "gradient")}): Z={FmtZ(jumpZ.Value)} VSWR={RfHelpers.VswrFromZ(curZ, jumpZ.Value):F4} c={cJump?.ToString("G6") ?? "null"}");

            if (cJump is not null && cJump.Value > cCur)
            {
                curZ = jumpZ.Value;
                cCur = cJump.Value;
                if (cJump.Value > bestC) { bestC = cJump.Value; bestZ = curZ; }

                if (isQuad)
                {
                    // Quadratic model proved accurate: grow trust radius (cap at DsInitial).
                    double Rold = R;
                    R = Math.Min(DsInitial, 1.0 + (R - 1.0) * 2.0);
                    Log?.WriteLine($"[IQ] Accept.Quadratic: grow R {Rold:G4} → {R:G4}");
                }
                else
                {
                    Log?.WriteLine($"[IQ] Accept.Gradient: R unchanged={R:G4}");
                }
            }
            else
            {
                // Reject: shrink trust radius.
                double Rold = R;
                R = 1.0 + (R - 1.0) / 3.0;
                Log?.WriteLine($"[IQ] Reject(c={cJump?.ToString("G6") ?? "null"} ≤ cCur={cCur:G6}): shrink R {Rold:G4} → {R:G4}");
            }
        }

        Log?.WriteLine($"[IQ] Done: bestZ={FmtZ(bestZ)} bestC={bestC:G6} total_queries={queries.Count}");
        return new PursuitResult(bestZ, bestC, queries, unscorable, converged: true);
    }

    // ── Exact VSWR step-length solver (Fix 2) ─────────────────────────────────

    /// <summary>
    /// Returns scalar L (Ω) such that VswrFromZ(curZ, curZ + dir·L) == targetVswr.
    /// <paramref name="dir"/> is a unit vector in the Z-plane.
    /// Uses bisection (30 iterations, ~1 ppm precision).
    /// </summary>
    private static double FindStepLength(Complex curZ, Complex dir, double targetVswr)
    {
        if (targetVswr <= 1.0 + 1e-10) return 0.0;

        // Upper bound on L that keeps Re(Z) ≥ 1 Ω (for −Re directions).
        double maxL = dir.Real < -1e-10
            ? (curZ.Real - 1.0) / (-dir.Real)
            : 1e6;
        if (maxL <= 0.0) return 0.0;

        // Doubling search for hi where VSWR(hi) >= targetVswr.
        double hi = Math.Min(maxL, Math.Max(0.5, curZ.Real * 0.05));
        for (int i = 0; i < 60 && hi < maxL; i++)
        {
            Complex c = curZ + dir * hi;
            if (c.Real > 0.5 && RfHelpers.VswrFromZ(curZ, c) >= targetVswr) break;
            hi = Math.Min(hi * 2.0, maxL);
        }

        // Bisection (30 iterations).
        double lo = 0.0;
        for (int i = 0; i < 30; i++)
        {
            double  mid = 0.5 * (lo + hi);
            Complex cm  = curZ + dir * mid;
            if (cm.Real > 0.5 && RfHelpers.VswrFromZ(curZ, cm) < targetVswr)
                lo = mid;
            else
                hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    // ── Formatting helpers (used by Log) ──────────────────────────────────────

    private static string FmtZ(Complex z)
        => $"{z.Real:F2}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary:F2}j";

    // ── Curve-fitting helpers (unchanged) ────────────────────────────────────

    /// <summary>
    /// Fit a 1-D quadratic ΔC = m·Δu + ½·muu·Δu² through the origin and the supplied
    /// (Δu, ΔC) points (typically the two axis-aligned cardinals on one axis).
    ///
    /// Used by <see cref="RunIteratedQuadratic"/> to avoid the singular AtA matrix that
    /// arises when axis-aligned cardinals are fed to the full 5-parameter
    /// <see cref="FitQuadraticSurface"/> (the ΔxΔy cross-term column is identically zero
    /// for axis-aligned probes, making the system rank-deficient).
    ///
    /// With 0 points: returns (0, 0).
    /// With 1 point:  gradient-only estimate (muu = 0, m = ΔC/Δu).
    /// With 2 points: exact 2×2 solve.  (Origin is implicit — it is the reference point.)
    /// </summary>
    private static (double M, double Muu) FitAxis1D(List<(double Du, double Dc)> pts)
    {
        if (pts.Count == 0) return (0.0, 0.0);
        if (pts.Count == 1)
        {
            double du = pts[0].Du, dc = pts[0].Dc;
            return Math.Abs(du) > 1e-20 ? (dc / du, 0.0) : (0.0, 0.0);
        }
        // Two points: [du_a, du_a²/2] [m]   [dc_a]
        //             [du_b, du_b²/2] [muu] = [dc_b]
        double a = pts[0].Du, cA = pts[0].Dc;
        double b = pts[1].Du, cB = pts[1].Dc;
        double det = a * (b * b / 2.0) - b * (a * a / 2.0);   // = ab(b−a)/2
        if (Math.Abs(det) < 1e-30)
            return Math.Abs(a) > 1e-20 ? (cA / a, 0.0) : (0.0, 0.0);
        double m   = (cA * (b * b / 2.0) - cB * (a * a / 2.0)) / det;
        double muu = (a * cB - b * cA) / det;
        return (m, muu);
    }

    /// <summary>
    /// Fit linear plane ΔC = m1·x + m2·y through two data points.
    /// </summary>
    private static (double M1, double M2) FitLinearPlane(
        double x1, double y1, double dc1,
        double x2, double y2, double dc2)
    {
        double det = x1 * y2 - x2 * y1;
        if (Math.Abs(det) < 1e-30) return (dc1 / (Math.Abs(x1) + 1e-30), 0);
        double m1 = (dc1 * y2 - dc2 * y1) / det;
        double m2 = (x1 * dc2 - x2 * dc1) / det;
        return (m1, m2);
    }

    /// <summary>
    /// Fit 2nd-order surface ΔC = m1·x + m2·y + ½(m11·x² + 2·m12·x·y + m22·y²)
    /// to (Δx, Δy, ΔC) points via least-squares. Baylis Eq. 4.
    /// </summary>
    private static (double M1, double M2, double M11, double M12, double M22)
        FitQuadraticSurface(List<(double Dx, double Dy, double Dc)> pts)
    {
        int n = pts.Count;
        var A = new double[n, 5];
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            var (x, y, dc) = pts[i];
            A[i, 0] = x;
            A[i, 1] = y;
            A[i, 2] = 0.5 * x * x;
            A[i, 3] = x * y;
            A[i, 4] = 0.5 * y * y;
            b[i]    = dc;
        }
        var AtA = new double[5, 5];
        var Atb = new double[5];
        for (int j = 0; j < 5; j++)
        {
            for (int k = 0; k < 5; k++)
                for (int i = 0; i < n; i++)
                    AtA[j, k] += A[i, j] * A[i, k];
            for (int i = 0; i < n; i++)
                Atb[j] += A[i, j] * b[i];
        }
        double[] θ = Solve5x5(AtA, Atb);
        return (θ[0], θ[1], θ[2], θ[3], θ[4]);
    }

    /// <summary>
    /// Analytic optimum of ΔC = m1·x + m2·y + ½(m11·x² + 2·m12·x·y + m22·y²).
    /// Requires negative-definite Hessian (m11 &lt; 0, det &gt; 0).
    /// Returns Complex.Zero if Hessian is not negative-definite.
    /// </summary>
    private static Complex SolveQuadraticOptimum(
        double m1, double m2, double m11, double m12, double m22)
    {
        double det = m11 * m22 - m12 * m12;
        if (det < 1e-30 || m11 >= 0) return Complex.Zero;
        double dx = (m12 * m2 - m22 * m1) / det;
        double dy = (m12 * m1 - m11 * m2) / det;
        return new Complex(dx, dy);
    }

    /// <summary>Gaussian elimination with partial pivoting for a 5×5 system.</summary>
    private static double[] Solve5x5(double[,] A, double[] b)
    {
        const int N = 5;
        var a = new double[N, N];
        var r = new double[N];
        Array.Copy(A, a, N * N);
        Array.Copy(b, r, N);

        for (int col = 0; col < N; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < N; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            for (int k = 0; k < N; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            (r[col], r[pivot]) = (r[pivot], r[col]);

            if (Math.Abs(a[col, col]) < 1e-30) return new double[N];

            for (int row = col + 1; row < N; row++)
            {
                double fac = a[row, col] / a[col, col];
                for (int k = col; k < N; k++) a[row, k] -= fac * a[col, k];
                r[row] -= fac * r[col];
            }
        }
        var x = new double[N];
        for (int i = N - 1; i >= 0; i--)
        {
            x[i] = r[i];
            for (int j = i + 1; j < N; j++) x[i] -= a[i, j] * x[j];
            x[i] /= a[i, i];
        }
        return x;
    }

    // ── Abort helper ──────────────────────────────────────────────────────────

    private static PursuitResult Abort(Complex startZ,
        List<(Complex, double?)> queries,
        List<Complex> unscorable,
        string reason)
        => new PursuitResult(startZ, double.NegativeInfinity, queries, unscorable,
            converged: false, abortReason: reason);
}
