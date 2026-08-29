// L8c — the matrix fill: the per-CELL potential matrix, the per-BASIS vector block, and the
// quadrature rules that produce them.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE FORMULATION, DERIVED RATHER THAN TRANSCRIBED
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// MPIE on a PEC sheet. E^scat = −jωA − ∇φ with A = µ₀∫G_A J dS′ and φ = (1/ε₀)∫G_q q dS′, and
// continuity q = −∇·J/(jω). Expanding J = Σ Iₙ fₙ, testing with f_m (Galerkin, D1) and integrating
// ∫f_m·∇φ by parts — the boundary term vanishes because f·n̂ = 0 on the rooftop's own rim, which is
// D2's other payoff — gives
//
//     Z[m,n] = jωµ₀ ∫∫ f_m·f_n G_A dS′dS  +  (1/(jωε₀)) ∫∫ (∇·f_m)(∇·f_n) G_q dS′dS
//
// with the repository's own normalisation for both kernels (free-space value 1/4πR; see
// LayeredMedium and SpectralGreens). There is no excitation vector here and no port: D8. The matrix
// is still fully gateable — see the four rungs in PlanarFillTests / PlanarStaticLimitTests.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D4 — THE SCALAR BLOCK IS ASSEMBLED FROM A PER-CELL MATRIX, AND THAT IS EXACT, NOT AN APPROXIMATION
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// ∇·f is a PULSE of height ±1/Area on each of the rooftop's two cells (PlanarBasisFunctions), so
//
//     Z^φ[m,n] = (1/(jωε₀)) Σ_{a∈m} Σ_{b∈n} s_a s_b P[a,b],
//     P[a,b]   = (1/(A_a A_b)) ∫_a ∫_b G_q dS′ dS
//
// P is built ONCE over cells — about N/2 of them, so roughly a 4× reduction in scalar integrals —
// and is exactly the electrostatic potential-coefficient matrix, which is what makes Tier 5's ω → 0
// capacitance gate reachable at all.
//
// A structural consequence worth stating because it is easy to "fix": **s_A + s_B = 0, so any part of
// G_q that does not depend on ρ contributes exactly ZERO to the scalar block.** That is the rooftop's
// charge neutrality showing through. The extracted constant therefore cancels in Z^φ and survives
// only in Z^A — which is a correctness check, not an optimisation to be pursued.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D5 — THE VECTOR BLOCK IS BLOCK-DIAGONAL BY DIRECTION
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// In Michalski-Zheng formulation C the vector kernel is a single scalar G_A with no xy component
// (L8a's R-lgf-1), and a rooftop is purely x̂ or purely ŷ. So f_m·f_n ≡ 0 for a mixed pair and an
// X-rooftop couples to a Y-rooftop through the SCALAR term alone. Half the vector fill disappears,
// and Tier 4 tests it as a formulation fact rather than as an optimisation.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D6 — THE FREQUENCY-INDEPENDENT CORE, AND WHAT IT ACTUALLY IS
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The Green's function is per-frequency (L8a's R-lgf-5) so the fill is too. But the EXTRACTED
// singular parts factor into a frequency-dependent COEFFICIENT times a purely geometric INTEGRAL:
//
//     ∫∫ w_a w_b G  =  C₁(ω)·∫∫ w_a w_b /R  +  C_log(ω)·∫∫ w_a w_b ln r
//                    + C_const(ω)·∫∫ w_a w_b  +  C_lin(ω)·∫∫ w_a w_b r  +  ∫∫ w_a w_b G_rem(ω)
//
// The four integrals on the right are geometry alone (RectangleIntegrals) and are computed ONCE per
// mesh. Only the last term — a bounded, smooth integrand — is redone per frequency, and it needs a
// far lower quadrature order than the singular cores do. R-mom-11's own lesson applies: this is
// enforced by <see cref="PlanarSweepResult.CoreFillCount"/>, asserted at exactly 1 for a 3-point AND
// a 101-point sweep, **not by a comment**.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// P4 — THE FOUR RAMP COMBINATIONS OF A CELL PAIR ARE ONE PASS, NOT FOUR
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// A cell carries two ramps along the flow coordinate u — the RISING one, w_A = (u − u₀)/A, which a
// rooftop uses on its lower cell (RooftopHalf A, OuterEdge = u₀), and the FALLING one,
// w_B = (u₁ − u)/A, on its upper cell (half B, OuterEdge = u₁) — and one pulse, p = 1/A. With
// Δ = u₁ − u₀ the cell's extent along u,
//
//     w_B = Δ·p − w_A                                                                     (P4.1)
//
// so every (half, half) integral over an ORDERED cell pair (a outer, c inner) is a linear
// combination of four primitives per kernel K ∈ {1/R, ln r, r} and per flow direction:
//
//     Q00 = ⟨p_a, p_c⟩     Q10 = ⟨w_A,a, p_c⟩     Q01 = ⟨p_a, w_A,c⟩     Q11 = ⟨w_A,a, w_A,c⟩
//
// where ⟨f, g⟩ = ∫_a f ∫_c g K dS′ dS is the outer Gauss rule times the closed-form inner integral
// PairCores already evaluates. Q00 is direction-free — it is the scalar block's own S0 — so a pair
// has 1 + 3 + 3 = 7 primitives per kernel. Substituting (P4.1) on either side:
//
//     ⟨A, A⟩ = Q11
//     ⟨A, B⟩ = Δ_c·Q10 − Q11
//     ⟨B, A⟩ = Δ_a·Q01 − Q11                                                              (P4.2)
//     ⟨B, B⟩ = Δ_a·Δ_c·Q00 − Δ_a·Q01 − Δ_c·Q10 + Q11
//
// In CellWeight's terms: half A is Sigma = +1 with Edge = u₀, so the inner ramp σ(u′ − Edge)/A is
// w_A; half B is Sigma = −1 with Edge = u₁, so σ(u′ − Edge)/A = (u₁ − u′)/A = w_B. Both are
// non-negative on the cell, which is what the outer |u − Edge| encodes. The map is in
// <see cref="PlanarFill.Combine"/> — ONE function, used by the dense build and by the per-entry fill.
//
// ORIENTATION IS PRESERVED, NOT NORMALISED. The outer rule and the inner closed form are not
// interchangeable to 1e-12 — swapping which cell is integrated numerically moves a touching pair by
// its quadrature error (~1e-6) — and the row loops integrate the LOWER-INDEXED BASIS's cells as the
// outer domain. In the ŷ block that puts the same cell pair on BOTH orientations (the basis one row
// down and to the left has the lower index), so the primitives are computed per ordered pair
// (a, c), for every c from the smallest inner cell any same-direction pair with a as outer can ask
// for (<see cref="RampTopology.MinInner"/>). That band is c ≥ a − n_x rather than c ≥ a, and it is
// what keeps the assembled matrix at 1e-12 rather than at the quadrature's own tolerance.
//
// THE PASS IS OVER OUTER CELLS, AND EVERY SLOT HAS ONE WRITER. A basis pair (i, j) draws on two
// outer cells — i's A cell and its B cell — so a cell-parallel pass would have two threads adding
// into one entry. Instead the A cell's contributions go to the entry itself and the B cell's to a
// transient second triangle, and a row pass adds the two (R-fil-11 holds: each slot is written by
// exactly one thread, over the inner cells in ascending order). The per-frequency remainder takes
// the same shape with the same seven sums of rem(ρ), and the ONE pass serves both flow directions.
// Bit-identity with the four-call path is not available — four combinations of seven primitives
// are not the same arithmetic as four quadratures — so the gate is 1e-12 on the assembled matrix
// (PlanarP4MomentCacheTests). S0/SLog ARE bit-identical, because Q00 is accumulated with the pulse
// path's own expressions. A pair with a CUT half (Strips != null) has a ramp affine in BOTH
// coordinates, for which (P4.1) does not hold; every pair touching one takes the four-call path,
// unchanged.
//
// R-fil-11 — DETERMINISM. Parallelism is over ROWS of each packed triangle; every entry is written
// exactly once by exactly one thread, and the accumulation inside an entry is an ordinary sequential
// loop over a fixed node order. There is no shared accumulator and no dictionary iteration anywhere
// on this path.

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// R-fil-5 — <b>the quadrature rules, stated rather than hidden.</b> A fill whose accuracy depends on
/// a magic number nobody wrote down is a fill nobody can debug. Every number here is swept by
/// PlanarFillTests' Tier 6 and reported in <c>src/Engine/Mom/CLAUDE.md</c>.
/// </summary>
/// <param name="Order">D3's extraction order. <b>The default is Constant (1), and that is a
/// measurement rather than a preference</b> — exactly as L8a recorded a table for its own
/// branch-point orders instead of a taste. Order 2 buys nothing: on a fixed mesh all three orders
/// agree with the converged answer to 5e-9, and on the case where the fill IS stressed (FR-4 at
/// 20 GHz, where the surface-wave residue is large) order 0 and order 2 land within 1e-4 of each
/// other while both are 5% from the truth — it was the remainder QUADRATURE that mattered there, not
/// the extraction order. Order 2 additionally costs the ∫∫r and ∫∫u·r cores in both time and memory,
/// so it is reachable and not default. Order 0 is kept reachable because it is the cheapest thing
/// that is still correct.</param>
/// <param name="SelfPanels">Outer-cell subdivision (per axis) for the SELF term. The inner integral
/// is exact there, but the outer integrand's gradient is log-divergent on the source cell's own
/// boundary — which for a self term is the outer domain's boundary too. That is a quadrature-order
/// question rather than a special case, and this is the knob that answers it. <b>Measured</b> against
/// the mean-reciprocal-distance constant of a unit square (2.9732096): at 12 nodes the error is
/// 2.8e-5 with 1 panel, 3.1e-6 with 3, 7.8e-7 with 6 and 2.0e-7 with 12 — so 6 buys three decades
/// over the naive rule for 36× the points on a band that is 0.4% of the pairs.</param>
/// <param name="TouchPanels">…and for a NEAR-but-not-self pair. <b>This, not the self term, is what
/// limits the fill's accuracy</b> — measured on the small irregular fixture against the correlation
/// oracle, the self entry is right to 4.8e-8 while a face-touching neighbour is 9.7e-6 at 2 panels,
/// 8.2e-7 at 4 and 1.0e-7 at 6. The self term's singular line is spread over all four sides of the
/// outer cell and is well handled by the clustering; a touching pair concentrates it on ONE side,
/// where half the clustered panels are wasted at the far edge.</param>
/// <param name="NearNodes">Gauss nodes per axis per panel for a near pair.</param>
/// <param name="MidNodes">…for an intermediate pair.</param>
/// <param name="FarNodes">…for a well-separated pair.</param>
/// <param name="NearRatio">Separation ÷ cell size below which a pair counts as near.</param>
/// <param name="FarRatio">…above which it counts as far.</param>
/// <param name="RemainderNodesNear">Gauss nodes per axis, both inner AND outer, for the remainder of
/// a NEAR pair. <b>"Smooth" is relative and this is where that bites</b> — the extraction leaves an
/// O(ρ²) remainder, but ρ = |r − r′| is not a smooth function of the separation VECTOR, so odd powers
/// and the surface wave's own ρ²ln ρ leave a weak conical kink on the diagonal r = r′. Measured on
/// FR-4 at 20 GHz, where the surface-wave residue is large: the self entry converges only like
/// n^-2.2 (165.15 / 159.59 / 157.68 / 157.04 / 156.84 at n = 3/5/8/12/16 against 156.64), so a
/// 3-point rule that is ample for the free-space kernel is 5% wrong for the layered one. This is the
/// single number that decides the fill's accuracy on FR-4 at the top of the band.</param>
/// <param name="RemainderNodesMid">…for an intermediate pair, where ρ stays bounded away from zero
/// and the kink is outside the domain.</param>
/// <param name="RemainderNodesFar">…for a far pair, where the remainder is genuinely smooth.</param>
/// <param name="UseRadialTable">Interpolate the remainder from a per-frequency radial table rather
/// than evaluating the Green's function at every quadrature point. Off is the reference path.</param>
/// <param name="TableCellFraction">Table spacing as a fraction of the smallest cell edge — i.e. 20
/// samples across the finest cell at the default. Set from the CELL rather than from the wavelength
/// because the cell is the scale on which the fill asks questions of the remainder. <b>Measured</b>
/// on the small irregular fixture against a directly-evaluated fill: 6.8e-5 relative at ¼ cell,
/// 3.7e-7 at 1/10, 9.4e-8 at 1/20 and <b>8.1e-9 at 1/50</b> — so the default sits two decades below
/// the fill's own quadrature error and five below the kernel's.</param>
/// <param name="RhoFloorFraction">The remainder's evaluation floor, as a fraction of the smallest
/// cell edge. See SingularExtraction's header for the error bound this buys.</param>
/// <param name="MaxTableSamples">A ceiling on the radial table, so a pathologically fine mesh cannot
/// make the per-frequency table cost more than the fill it serves. Neither starter hero comes near
/// it (the FR-4 hero wants ~12,000 samples, the GaAs one ~19,000).</param>
/// <param name="ViaZNodes">R-viz-3 — <b>Gauss nodes per via for the z-integral's BOUNDED half</b>
/// (the asymptotes' wave correction, the surface-wave poles and the fitted images). The SINGULAR half
/// is closed form in z and does not use this at all, which is why a small number is enough.
///
/// <para><b>The default is 2, and that is a measurement rather than a preference</b> —
/// <c>ViaPhysicsTests.T3_1c</c> sweeps n_z ∈ {1, 2, 4, 8} twice. Against the exact bar integral at
/// εᵣ = 1 every setting lands inside 0.024% at ℓ/w up to 5; on the MMIC two-level stack, where the
/// surface-wave residues and the fitted image depths genuinely do move with the heights and the via
/// spans its whole region, the vertical blocks agree with n_z = 8 to <b>5.6e-8</b> even at n_z = 1.
/// So the honest reading is that this rule is not where the accuracy comes from, and L8c's own
/// precedent applies — its extraction order is 1 rather than 2 because "order 2 buys nothing". 2 is
/// the smallest setting that is a genuine QUADRATURE (1 is a midpoint rule, and reading the setting
/// as "midpoint" is the mistake this whole change exists to undo), and it costs 3 fits per via span
/// against 10 at n_z = 4 — 0.28% of a de-embedded point instead of 1.05%.</para></param>
/// <summary>
/// <b>R-zz-1's Tier 1 instrument.</b> What the fill actually ASKED the kernel about, as opposed to
/// what a reading of the code says it asks about — which is the difference the whole scoping change
/// turns on, and the reason this exists rather than a comment.
///
/// <para>Optional and defaulted-null everywhere, so no existing caller changes and the fill pays
/// nothing when it is absent. Thread-safe because <c>ForRows</c> may run the ẑẑ arm in parallel.</para>
/// </summary>
public sealed class PlanarFillDiagnostics
{
    private readonly object _gate = new();
    private double _maxVerticalPairRho;

    /// <summary>The largest in-plane separation any <c>G_A^zz</c> query reached — i.e. the widest ρ
    /// the ONE kernel <see cref="Dcim.ValidatedRhoOverLambdaAtHeights"/> governs was evaluated at.
    /// Zero when the mesh carries no vertical basis.</summary>
    public double MaxVerticalPairRhoM
    {
        get { lock (_gate) return _maxVerticalPairRho; }
    }

    internal void ObserveVerticalPair(double rhoM)
    {
        lock (_gate) if (rhoM > _maxVerticalPairRho) _maxVerticalPairRho = rhoM;
    }
}

/// <param name="ViaZStaticNodes">Gauss nodes per PANEL of the singular half's one-dimensional
/// t-integral (the panels are the trapezoidal density's own knots plus the kink at t = 0). Its
/// integrand is a piecewise-smooth product of a linear density and a bounded mean, so a modest rule
/// converges; the convergence is reported rather than asserted.</param>
/// <param name="Parallel">Fill rows concurrently. Does not change the answer — R-fil-11.</param>
/// <param name="MaxDegreeOfParallelism">
/// <b>M1 (brief-em-sweep-performance) — the ONE parallelism cap in this engine.</b> Null is
/// unbounded, i.e. exactly what every fill did before that brief. There is deliberately no second
/// cap anywhere: when <see cref="PlanarSolve"/> fans out the DUT and its calibration standards at
/// one frequency (M2), it does NOT add an outer cap that a reader would have to multiply by this
/// one — it materialises this same number as a shared <see cref="PlanarFillSettings.Budget"/>, which
/// every fill-row worker draws a permit from. However many solves are in flight, the number of
/// threads doing fill arithmetic at any instant is this number.
///
/// <para><b>It cannot change an answer</b> — R-fil-11: the parallelism is over ROWS of the packed
/// upper triangle, every entry is written exactly once, and nothing accumulates into shared state.
/// R-emp-8 asserts that as BIT-IDENTITY at caps 1, 2 and unbounded rather than leaving it as a
/// claim, and it is why the core count is kept out of every provenance hash (R-emp-7).</para>
/// </param>
public sealed record PlanarFillSettings(
    PlanarExtractionOrder Order                   = PlanarExtractionOrder.Constant,
    int                   SelfPanels              = 4,
    int                   TouchPanels             = 3,
    int                   NearNodes               = 10,
    int                   MidNodes                = 5,
    int                   FarNodes                = 3,
    double                NearRatio               = 1.6,
    double                FarRatio                = 4.0,
    int                   RemainderNodesNear      = 8,
    int                   RemainderNodesMid       = 4,
    int                   RemainderNodesFar       = 2,
    bool                  UseRadialTable          = true,
    double                TableCellFraction       = 0.02,
    double                RhoFloorFraction        = 1e-8,
    int                   MaxTableSamples         = 1 << 15,
    bool                  DirectVerticalKernel    = false,
    int                   VerticalTableSamples    = 256,
    int                   ViaZNodes               = 2,
    int                   ViaZStaticNodes         = 10,
    bool                  Parallel                = true,
    int?                  MaxDegreeOfParallelism  = null)
{
    public static readonly PlanarFillSettings Default = new();

    /// <summary>
    /// <b>M2 — the shared meter <see cref="MaxDegreeOfParallelism"/> is spent through when more than
    /// one solve is in flight.</b> This is NOT a second cap: it carries the same number, and it
    /// exists because a cap on an outer <c>Parallel.ForEach</c> does not bound the inner
    /// <c>Parallel.For</c> over rows. Non-null only on a run that fans out; null everywhere else, in
    /// which case <see cref="MaxDegreeOfParallelism"/> is applied directly as a
    /// <c>ParallelOptions</c> cap and a null cap reproduces today's unbounded <c>Parallel.For</c>
    /// exactly.
    ///
    /// <para>Deliberately outside the positional list: it is a shared mutable object for the
    /// duration of one run, not a setting a caller chooses, and it must not appear in the record's
    /// own parameter list where someone would set it by hand.</para>
    /// </summary>
    public PlanarParallelBudget? Budget { get; init; }

    /// <summary>
    /// <b>M5 — non-null turns the AIM accelerator on for this mesh.</b> Null is the dense path, byte
    /// for byte: <see cref="PlanarSolveContext"/> builds the full O(N²) cores, fills, factors and
    /// back-substitutes exactly as L8c/L8d wrote it, and nothing on this object is read.
    ///
    /// <para>Deliberately outside the positional list, like <see cref="Budget"/>: it selects a
    /// SOLVER, not a quantity, and it must not silently become part of a record's structural equality
    /// where a comparison of two fill settings would start meaning "were these solved the same way".
    /// It enters no provenance hash for the same reason the core cap does not (R-emp-7) — with the
    /// accelerator's own accuracy gates passed, it changes how the answer is computed and not what it
    /// is.</para>
    /// </summary>
    public PlanarAimSettings? Aim { get; init; }

    /// <summary>
    /// <b>P2/M3 — the meter that says how many times the geometric cores were actually BUILT.</b>
    /// Null everywhere except on a run that wants to count them.
    ///
    /// <para>Deliberately an INSTANCE handed through the settings, exactly as <see cref="Budget"/> is,
    /// and for the reason <c>PlanarSweepResult.CoreFillCount</c>'s own note gives: a static counter is
    /// the obvious implementation and it makes every test that reads it flaky the moment two fill
    /// tests run concurrently, which xUnit does by default across classes. One run's settings object
    /// reaches the DUT and every calibration standard (<c>PlanarSolve.Run</c> builds exactly one
    /// <c>fillSt</c>), so one counter sees every build in that run and no build from any other.</para>
    ///
    /// <para>Outside the positional list for the same reason as the other two: it is a shared mutable
    /// meter for the duration of one run, not a quantity a caller chooses.</para>
    /// </summary>
    public PlanarCoreBuildCounter? CoreBuilds { get; init; }

    /// <summary>
    /// <b>P4 — the per-fill quadrature meter.</b> Counts the outer×inner remainder passes one
    /// <see cref="PlanarFill.Fill"/> runs, so "the vector remainder is one pass per cell pair, not
    /// four per basis pair" is a COUNTER a routine test asserts rather than a wall clock. Null by
    /// default; outside the positional list for the same reason <see cref="CoreBuilds"/> is.
    /// </summary>
    public PlanarFillCounters? Counters { get; init; }

    /// <summary>
    /// <b>P7 — which dense factorisation the planar system uses.</b> True (the default) is the
    /// in-place blocked complex-symmetric LDLᵀ, <see cref="SymmetricFactorization"/>. False falls
    /// back to NumFlat's general LU, which is what every published number in this area was produced
    /// by up to P6 and is kept reachable for exactly that reason — the same way
    /// <see cref="UseRadialTable"/> keeps the directly-evaluated remainder reachable as its own
    /// oracle.
    ///
    /// <para>The two do not agree bit for bit and cannot: they are different factorisations of the
    /// same matrix. What they agree to is measured — <c>PlanarP7SymmetricFactorTests</c> carries the
    /// numbers, and <c>PlanarP7FactorCostTests</c> the time and memory.</para>
    ///
    /// <para>Outside the positional list for the reason <see cref="Aim"/> is: it selects a SOLVER,
    /// not a quantity, and two settings objects that differ only in how their matrix was factored
    /// are not two different fills.</para>
    /// </summary>
    public bool UseSymmetricFactorization { get; init; } = true;

    /// <summary>
    /// <b>P7 — keep a copy of Z so every solve can report ‖Zx − b‖/‖b‖.</b> Off by default, and
    /// deliberately: the factorisation is IN PLACE, so this hands back a whole N×N matrix — 381 MB
    /// at the ceiling — which is the memory the phase exists to recover. It is a diagnostic for a
    /// gate or a support question, not a safety net.
    ///
    /// <para>The matrix-free instruments are always on and cost nothing:
    /// <see cref="SymmetricFactorization.GrowthFactor"/> and
    /// <see cref="SymmetricFactorization.SmallestPivotRatio"/> are computed during the
    /// factorisation and survive the matrix.</para>
    /// </summary>
    public bool TrackFactorizationResidual { get; init; }

    /// <summary>
    /// <b>Refuse a setting that would silently produce a WRONG answer rather than an exception.</b>
    ///
    /// <para>These are not defensive checks against nonsense for its own sake — each one guards a
    /// value whose bad case is a complete, smooth, plausible result. <c>ViaZNodes = 0</c> is the
    /// clearest: <see cref="ViaZIntegral.Nodes"/> returns empty arrays, the z-average sums nothing,
    /// and the ẑẑ block comes out ZERO — i.e. the vias stop conducting and nothing anywhere looks
    /// wrong. A quadrature count that silently means "skip the integral" is exactly the failure this
    /// area keeps finding, so it is refused at the one place every fill passes through.</para>
    /// </summary>
    public void Validate()
    {
        static void AtLeast(int v, int min, string name, string why)
        {
            if (v < min)
                throw new ArgumentOutOfRangeException(name, v,
                    $"{name} must be at least {min}. {why}");
        }

        AtLeast(ViaZNodes, 1, nameof(ViaZNodes),
            "At 0 the z-quadrature has no nodes, so the ẑẑ block integrates to zero and the vias " +
            "silently stop carrying current. R-viz-3 measured 2 as the default.");
        AtLeast(ViaZStaticNodes, 1, nameof(ViaZStaticNodes),
            "This is the singular half's t-rule; at 0 the via's closed-form z-integral contributes " +
            "nothing and its inductance collapses.");
        AtLeast(SelfPanels, 1, nameof(SelfPanels), "The self term needs at least one panel.");
        AtLeast(TouchPanels, 1, nameof(TouchPanels), "A touching pair needs at least one panel.");
        AtLeast(NearNodes,  1, nameof(NearNodes),  "A Gauss rule needs at least one node.");
        AtLeast(MidNodes,   1, nameof(MidNodes),   "A Gauss rule needs at least one node.");
        AtLeast(FarNodes,   1, nameof(FarNodes),   "A Gauss rule needs at least one node.");
        AtLeast(RemainderNodesNear, 1, nameof(RemainderNodesNear),
            "R-fil-8 measured 8 here because a fitted image can sit closer to the metal than a cell " +
            "is wide; at 0 the remainder is dropped entirely.");
        AtLeast(RemainderNodesMid,  1, nameof(RemainderNodesMid),  "A Gauss rule needs at least one node.");
        AtLeast(RemainderNodesFar,  1, nameof(RemainderNodesFar),  "A Gauss rule needs at least one node.");
        AtLeast(MaxTableSamples,      8, nameof(MaxTableSamples),
            "A radial table below 8 samples cannot carry its own interpolation stencil.");
        AtLeast(VerticalTableSamples, 8, nameof(VerticalTableSamples),
            "M2 measured the assembled ẑẑ block still moving 2.2e-3 at 32 samples and converged at 128.");

        if (!(TableCellFraction > 0))
            throw new ArgumentOutOfRangeException(nameof(TableCellFraction), TableCellFraction,
                "The radial table's spacing is this fraction of the smallest cell edge; at 0 it has " +
                "no spacing to sample on.");
        if (RhoFloorFraction < 0)
            throw new ArgumentOutOfRangeException(nameof(RhoFloorFraction), RhoFloorFraction,
                "The ρ floor is a non-negative fraction of the smallest cell edge.");

        // M1. Zero or negative is the one value that would run NOTHING rather than run slowly, and
        // `Parallel.For` would throw a framework exception with no mention of a core count in it.
        // Null is how "no cap" is spelled.
        if (MaxDegreeOfParallelism is { } dop && dop < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), dop,
                "A core cap of zero or fewer would run no fill rows at all. Use null for unbounded, " +
                "which is the default and is what every fill did before the core-count control existed.");
    }

    /// <summary>A deliberately finer setting, for the "refine and it must converge" gate (Tier 6).</summary>
    public PlanarFillSettings Finer(int factor) => this with
    {
        SelfPanels         = SelfPanels * factor,
        TouchPanels        = TouchPanels * factor,
        NearNodes          = NearNodes + 4 * factor,
        MidNodes           = MidNodes + 3 * factor,
        FarNodes           = FarNodes + 2 * factor,
        RemainderNodesNear = RemainderNodesNear + 4 * factor,
        RemainderNodesMid  = RemainderNodesMid + 2 * factor,
        RemainderNodesFar  = RemainderNodesFar + factor,
    };
}

/// <summary>
/// <b>P2/M3/M4 — how many times the frequency-independent cores were built, and for which meshes.</b>
///
/// <para>D6's whole claim is that they are built ONCE per mesh and reused at every frequency, and
/// R-fil-9 already enforces that for a single-mesh sweep. A DE-EMBEDDED sweep touches several meshes,
/// and its counter (<c>PlanarSolveResult.CoreFillCount</c>) is derived from the number of standard
/// MESHES rather than from the number of builds — so it could not have seen either of the two things
/// P2 changed: <c>PlanarDeembed.StaticCapacitance</c> re-coring a mesh whose cores already existed
/// (M3), or a standard being cored though no frequency ever selected it (M4). This counts the builds
/// themselves.</para>
///
/// <para>Thread-safe; a de-embedded run cores its standards on whatever worker first asks for them.</para>
/// </summary>
public sealed class PlanarCoreBuildCounter
{
    private sealed class ByReference : IEqualityComparer<PlanarMesh>
    {
        public bool Equals(PlanarMesh? a, PlanarMesh? b) => ReferenceEquals(a, b);
        public int GetHashCode(PlanarMesh m) => RuntimeHelpers.GetHashCode(m);
    }

    private readonly ConcurrentDictionary<PlanarMesh, StrongBox<int>> _pair = new(new ByReference());
    private int _total, _pairTotal;

    /// <summary>Every core build, of either kind.</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>Just <see cref="PlanarFill.BuildCores"/> — the O(m²) one. The geometry-only build the
    /// accelerator uses is O(N) and is counted in <see cref="Total"/> alone.</summary>
    public int PairCoreTotal => Volatile.Read(ref _pairTotal);

    /// <summary>How many DISTINCT meshes have had their pair cores built.</summary>
    public int PairCoreMeshCount => _pair.Count;

    /// <summary>Pair-core builds for one mesh — 0 if it was never cored, which is M4's own gate.</summary>
    public int PairCoreBuildsFor(PlanarMesh mesh) =>
        _pair.TryGetValue(mesh, out var b) ? Volatile.Read(ref b.Value) : 0;

    /// <summary>The worst offender: 1 means every cored mesh was cored exactly once.</summary>
    public int MaxPairCoreBuildsPerMesh
    {
        get
        {
            int worst = 0;
            foreach (var b in _pair.Values) worst = Math.Max(worst, Volatile.Read(ref b.Value));
            return worst;
        }
    }

    internal void Observe(PlanarMesh mesh, bool pairCores)
    {
        Interlocked.Increment(ref _total);
        if (!pairCores) return;
        Interlocked.Increment(ref _pairTotal);
        Interlocked.Increment(ref _pair.GetOrAdd(mesh, static _ => new StrongBox<int>()).Value);
    }

    private readonly ConcurrentDictionary<PlanarMesh, StrongBox<int>> _aim = new(new ByReference());
    private int _aimTotal;

    /// <summary><b>P6</b> — every <see cref="PlanarAimGeometry"/> build. The accelerator's
    /// frequency-independent state — stencils, near set, and the near pairs' singular cores — is
    /// built once per mesh, so over a sweep of any length this is the number of accelerated meshes,
    /// exactly as <see cref="PairCoreTotal"/> is for the dense path's cores.</summary>
    public int AimGeometryTotal => Volatile.Read(ref _aimTotal);

    /// <summary>P6 — geometry builds for one mesh; 1 is the gate.</summary>
    public int AimGeometryBuildsFor(PlanarMesh mesh) =>
        _aim.TryGetValue(mesh, out var b) ? Volatile.Read(ref b.Value) : 0;

    internal void ObserveAimGeometry(PlanarMesh mesh)
    {
        Interlocked.Increment(ref _aimTotal);
        Interlocked.Increment(ref _aim.GetOrAdd(mesh, static _ => new StrongBox<int>()).Value);
    }
}

/// <summary>P4's fill meter — see <see cref="PlanarFillSettings.Counters"/>. Thread-safe; each
/// parallel row adds its own tally once.</summary>
public sealed class PlanarFillCounters
{
    private long _remainderPasses;

    /// <summary>Outer×inner remainder quadratures run by the vector block, over every
    /// <see cref="PlanarFill.Fill"/> that saw this object. A cell-pair pass (seven sums, both
    /// directions) counts 1; a four-call fallback on a cut pair counts 4.</summary>
    public long RemainderPasses => Volatile.Read(ref _remainderPasses);

    internal void AddRemainderPasses(long n) => Interlocked.Add(ref _remainderPasses, n);
}

/// <summary>
/// <b>P5 — how the frequency-independent cores are held.</b> <see cref="Classes"/> is the
/// production layout; <see cref="Triangles"/> survives only under the two retained reference
/// builders (<see cref="PlanarFill.BuildCoresByPairs"/>, P4's, and
/// <see cref="PlanarFill.BuildCoresByHalves"/>, L8c's) so their gates stay runnable.
/// </summary>
public enum PlanarCoreLayout
{
    /// <summary>One <c>int</c> per ordered cell pair in P4's band naming its translation class,
    /// plus a table of the seven P4 primitives per class (PlanarPairClasses.cs). Pairs with a cut
    /// cell keep their own rows.</summary>
    Classes,
    /// <summary>P2's layout: packed upper triangles of the scalar cores over cell pairs and of the
    /// summed vector cores over same-direction basis pairs.</summary>
    Triangles,
}

/// <summary>
/// <b>Rows of per-pair cores for the indices a memo cannot serve</b> — cut cells in the scalar
/// family, bases with a cut half in a vector family. One row per such index, every column, so a
/// mesh with a handful of cut cells pays a handful of rows rather than a whole triangle. The value
/// at (i, j) is the one the packed triangle held — computed with the LOWER index outer — and it is
/// stored in whichever of the two rows exists (both, when both are cut).
/// </summary>
internal sealed class CutRows
{
    /// <summary>Per index: its row, or −1.</summary>
    public readonly int[]    RowOf;
    public readonly int      Columns, Kernels, RowCount;
    public readonly double[] Values;

    public CutRows(int count, bool[] cut, int kernels)
    {
        RowOf = new int[count];
        int rows = 0;
        for (int i = 0; i < count; i++) RowOf[i] = cut[i] ? rows++ : -1;
        RowCount = rows; Columns = count; Kernels = kernels;
        Values = new double[(long)rows * count * kernels];
    }

    public long Bytes => 4L * RowOf.Length + 8L * Values.Length;

    public void Set(int row, int col, PlanarFill.CoreTriple v)
    {
        long o = ((long)row * Columns + col) * Kernels;
        Values[o] = v.Inverse; Values[o + 1] = v.Log;
        if (Kernels > 2) Values[o + 2] = v.Radius;
    }

    public PlanarFill.CoreTriple Get(int i, int j)
    {
        int row = RowOf[i], col = j;
        if (row < 0) { row = RowOf[j]; col = i; }
        long o = ((long)row * Columns + col) * Kernels;
        return new(Values[o], Values[o + 1], Kernels > 2 ? Values[o + 2] : 0.0);
    }
}

/// <summary>
/// D6's frequency-independent core: the purely geometric integrals, for every cell pair (the
/// scalar half) and every same-direction basis pair (the vector half). Built once per mesh; every
/// frequency of a sweep reuses it.
///
/// <para><b>P5:</b> in the <see cref="PlanarCoreLayout.Classes"/> layout neither half is stored per
/// pair. An ordered cell pair in P4's band holds a 4-byte class index (<see cref="BandClass"/>),
/// the seven primitives live once per class (<see cref="ClassCores"/>), and a vector entry is
/// assembled from its four cell pairs' classes at the point of use — the same
/// <see cref="PlanarFill.Combine"/> arithmetic P4 scattered at build time, now pulled per basis
/// pair. See PlanarPairClasses.cs.</para>
/// </summary>
public sealed class PlanarFillCores
{
    public PlanarMesh          Mesh     { get; }
    public PlanarFillSettings  Settings { get; }
    public PlanarCoreLayout    Layout   { get; }

    /// <summary>N — the matrix dimension, i.e. the basis count (R-msh-6).</summary>
    public int UnknownCount => Mesh.Bases.Count;
    public int CellCount    => Mesh.Cells.Count;

    /// <summary>The smallest cell edge anywhere — what the ρ floor and the table spacing derive from.</summary>
    public double MinCellEdgeM { get; }
    /// <summary>The mesh's own diagonal — how far apart two cells can be, i.e. how long the radial
    /// table has to be.</summary>
    public double ExtentM { get; }
    /// <summary>The ρ floor actually used, reported per R-fil-5.</summary>
    public double RhoFloorM { get; }

    /// <summary>How many cell-pair core integrals a TRIANGLE layout evaluated — the cost number
    /// Tier 8 reports. In the class layout, the number of unordered cell pairs the scalar half
    /// covers, for the same reports.</summary>
    public long ScalarPairs { get; }
    /// <summary>…and how many same-direction basis-pair core integrals (the packed count, either layout).</summary>
    public long VectorPairs { get; }

    // ── Triangles layout (the two reference builders only) ────────────────────────────────────
    // Packed upper triangles. The scalar cores are AREA-NORMALISED (so the constant core is exactly
    // 1 and is not stored); the vector cores carry the rooftop weights ξ/Area and are summed over the
    // pair's four cell-pair combinations at build time, because the ω-dependent coefficients multiply
    // the whole sum.
    internal readonly double[]? S0, SLog, SRad;
    internal readonly double[]? VX0, VXLog, VY0, VYLog, VXRad, VYRad;

    // ── Classes layout ────────────────────────────────────────────────────────────────────────

    /// <summary>P5 — the per-mesh translation-class key, O(n_x² + n_y²). On BOTH core builders,
    /// because AIM's per-entry fill classifies its near pairs on demand.</summary>
    internal readonly PairClassifier Classifier;

    /// <summary>Per basis: both halves are whole rectangles the classifier can name, so every pair
    /// it is in is served from the class table. False for a cut basis (P4's four-call path) and for
    /// a basis on a cell whose rectangle is not its grid spacing (which the mesher does not produce).</summary>
    internal readonly bool[] Memoised;

    /// <summary>Row starts of P4's ordered-pair band, length m + 1: outer cell <c>a</c>'s inner cells
    /// run from <c>Topology.MinInner[a]</c> to <c>m − 1</c>, at <c>BandStart[a] + (c − MinInner[a])</c>.</summary>
    internal readonly long[]? BandStart;
    /// <summary>Per ordered band pair: <c>(class &lt;&lt; 1) | rotated</c>, or −1 when either cell
    /// is not classifiable (its scalar core is then in <see cref="CutScalar"/>).</summary>
    internal readonly int[]? BandClass;
    /// <summary>Per class: its key (PairClassifier), from which the representative pair and the
    /// rule are re-derived per frequency for the remainder pass.</summary>
    internal readonly long[]? ClassKey;
    /// <summary>Per class, <see cref="ClassStride"/> doubles: the seven P4 primitives — pulse, then
    /// X10 X01 X11, then Y10 Y01 Y11 — each as (Inverse, Log[, Radius]).</summary>
    internal readonly double[]? ClassCores;
    /// <summary>2 at the shipped extraction order, 3 when the ∫∫r cores are wanted.</summary>
    internal readonly int Kernels;
    internal int ClassStride => 7 * Kernels;

    /// <summary>Scalar cores of every pair with a cell the memo cannot serve — one row per such cell.</summary>
    internal readonly CutRows? CutScalar;
    /// <summary>Vector cores of every same-direction pair with a basis the memo cannot serve, per direction.</summary>
    internal readonly CutRows? CutX, CutY;

    // ── both layouts ──────────────────────────────────────────────────────────────────────────

    internal readonly int[]    XBases, YBases;

    /// <summary>
    /// <b>L9d — basis index → its position inside <see cref="XBases"/>/<see cref="YBases"/></b>, so the
    /// multi-level fill can index the cached direction cores instead of re-integrating them.
    ///
    /// <para>D6's cores are purely GEOMETRIC — in-plane integrals of 1/r, ln r and 1 — so they do not
    /// depend on the height pairing at all; L9c's own note says exactly that ("the geometric cores are
    /// reused verbatim") for the SCALAR block, and it is equally true of the same-direction vector
    /// block. Before this map, <c>FillMultiLevel</c> re-ran four panel quadratures per entry for every
    /// horizontal pair — correct, and the reason a calibration standard (which is always single-level,
    /// so every one of its pairs takes this branch) cost several times what the shipped fill costs.</para>
    /// </summary>
    internal readonly int[]    DirPos;

    /// <summary>
    /// <b>P2/M1 — ∫w dS for every basis, indexed by BASIS index.</b> The extracted CONSTANT term's
    /// vector core is ∫w_m · ∫w_n, an outer product of this O(N) vector, and until P2 it was stored
    /// as two packed O(N²) triangles (<c>VXArea</c>/<c>VYArea</c>) beside the two that genuinely need
    /// to be — the 1/R and ln r integrals, which do not factor. Multiplying the two moments at the
    /// point of use is one <c>double</c> multiply per entry and gives the same bits: the stored value
    /// WAS that product, computed with the same two operands.
    ///
    /// <para>Per BASIS rather than per direction-position because both readers already have a basis
    /// index to hand (<c>AddDirectionBlock</c> has <c>idx[i]</c>, <c>HorizontalVectorEntry</c> has the
    /// basis directly), and one length-N array is the same N doubles as two per-direction ones.</para>
    /// </summary>
    internal readonly double[] VMoment;

    /// <summary>P4 — which bases each cell belongs to, and which of them are cut. O(N); both core
    /// builders carry it because the dense fill and the per-entry fill read the same one.</summary>
    internal readonly RampTopology Topology;

    /// <summary>
    /// <b>P4 — how many outer-quadrature passes the core build ran.</b> A cell-pair moment pass
    /// (seven primitives, both directions, all kernels) counts 1; a four-call fallback on a pair
    /// with a cut half counts 4; a conformal scalar core counts 1. Before P4 this was
    /// <c>ScalarPairs + 4·VectorPairs</c> ≈ 4.5 m²; P4 made it ≈ 0.6 m²; <b>P5 makes it one pass per
    /// translation CLASS</b> — 0.04 m² on the 60 mm taper — and <c>PlanarP5TranslationClassTests</c>
    /// asserts the class count as a counter rather than a wall clock.
    /// </summary>
    public long QuadraturePasses { get; }

    /// <summary>P5 — how many translation classes the band's classifiable pairs fell into (0 in the
    /// triangle layout and on a geometry-only core, which classifies on demand).</summary>
    public int ClassCount => ClassKey?.Length ?? 0;
    /// <summary>P5 — how many ordered cell pairs P4's band holds, classifiable or not.</summary>
    public long BandPairs => BandClass?.LongLength ?? 0;
    /// <summary>P5 — distinct spacing classes per axis, at 1e-12 relative.</summary>
    public (int X, int Y) SpacingClasses => (Classifier.X.ClassCount, Classifier.Y.ClassCount);
    /// <summary>P5 — distinct spacings per axis under exact equality, for the record of why the
    /// classifier quantises.</summary>
    public (int X, int Y) ExactlyDistinctSpacings => (Classifier.X.ExactlyDistinct, Classifier.Y.ExactlyDistinct);
    /// <summary>P5 — what the classifier itself holds, so a geometry-only core can state that it
    /// holds nothing per cell pair.</summary>
    public long ClassifierBytes => Classifier.Bytes + Memoised.Length;

    /// <summary>P4 — true when either half of the basis carries strips (a cut cell whose ramp is
    /// affine in both coordinates), so every pair it is in takes the four-call path unchanged.</summary>
    public bool IsCutBasis(int basis) => Topology.Cut[basis];

    /// <summary>
    /// <b>M5 — false when the O(N²) pair arrays were deliberately not built.</b> The AIM accelerator
    /// touches a vanishing fraction of the pairs and computes those on demand
    /// (<see cref="PlanarEntryFill"/>), so building the cached triangles would be the very O(N²) cost
    /// it exists to remove. Everything else on this object — the mesh, the settings, the ρ floor, the
    /// extent, the per-direction index maps — is O(N) and is present either way.
    ///
    /// <para><see cref="PlanarFill.Fill"/> and <see cref="PlanarFill.FillMultiLevel"/> refuse a
    /// geometry-only core by name rather than reading past the end of an empty array.</para>
    /// </summary>
    public bool HasPairCores { get; }

    /// <summary>The class layout (production, and the geometry-only core).</summary>
    internal PlanarFillCores(PlanarMesh mesh, PlanarFillSettings settings,
                             double minCellEdge, double extent, double rhoFloor,
                             PairClassifier classifier, bool[] memoised, int kernels,
                             long[]? bandStart, int[]? bandClass, long[]? classKey, double[]? classCores,
                             CutRows? cutScalar, CutRows? cutX, CutRows? cutY,
                             RampTopology topology, double[] vMoment,
                             long scalarPairs, long vectorPairs, long quadraturePasses, bool hasPairCores)
    {
        Layout = PlanarCoreLayout.Classes;
        Mesh = mesh; Settings = settings;
        MinCellEdgeM = minCellEdge; ExtentM = extent; RhoFloorM = rhoFloor;
        Classifier = classifier; Memoised = memoised; Kernels = kernels;
        BandStart = bandStart; BandClass = bandClass; ClassKey = classKey; ClassCores = classCores;
        CutScalar = cutScalar; CutX = cutX; CutY = cutY;
        Topology = topology; VMoment = vMoment;
        XBases = topology.X.Idx; YBases = topology.Y.Idx; DirPos = topology.DirPos;
        ScalarPairs = scalarPairs; VectorPairs = vectorPairs; QuadraturePasses = quadraturePasses;
        HasPairCores = hasPairCores;
    }

    /// <summary>The triangle layout (the two reference builders).</summary>
    internal PlanarFillCores(PlanarMesh mesh, PlanarFillSettings settings,
                             double minCellEdge, double extent, double rhoFloor,
                             double[] s0, double[] sLog, double[]? sRad,
                             double[] vx0, double[] vxLog, double[]? vxRad,
                             double[] vy0, double[] vyLog, double[]? vyRad,
                             double[] vMoment, RampTopology topology, PairClassifier classifier,
                             long scalarPairs, long vectorPairs, long quadraturePasses)
    {
        Layout = PlanarCoreLayout.Triangles;
        HasPairCores = true;
        Topology = topology; Classifier = classifier;
        Memoised = new bool[mesh.Bases.Count];
        for (int i = 0; i < Memoised.Length; i++) Memoised[i] = !topology.Cut[i];
        Kernels = sRad is null ? 2 : 3;
        QuadraturePasses = quadraturePasses;
        Mesh = mesh; Settings = settings;
        MinCellEdgeM = minCellEdge; ExtentM = extent; RhoFloorM = rhoFloor;
        S0 = s0; SLog = sLog; SRad = sRad;
        XBases = topology.X.Idx; YBases = topology.Y.Idx; DirPos = topology.DirPos;
        VX0 = vx0; VXLog = vxLog; VXRad = vxRad;
        VY0 = vy0; VYLog = vyLog; VYRad = vyRad;
        VMoment = vMoment;
        ScalarPairs = scalarPairs; VectorPairs = vectorPairs;
    }

    /// <summary>Index of the ordered pair (outer, inner) in the band; the inner cell must be at or
    /// above <c>Topology.MinInner[outer]</c>, which every pair the fill asks for is by construction.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal long BandIndex(int outer, int inner) => BandStart![outer] + (inner - Topology.MinInner[outer]);

    /// <summary>The seven primitives of one class, as the P4 struct.</summary>
    internal PlanarFill.CellPairMoments ClassMoments(int cls)
    {
        var t = ClassCores!;
        long o = (long)cls * ClassStride;
        int k = Kernels;
        PlanarFill.CoreTriple Read(int slot)
        {
            long p = o + (long)slot * k;
            return new(t[p], t[p + 1], k > 2 ? t[p + 2] : 0.0);
        }
        return new(Read(0), Read(1), Read(2), Read(3), Read(4), Read(5), Read(6));
    }

    /// <summary>The scalar (pulse×pulse) core of one unordered cell pair, either layout.</summary>
    internal PlanarFill.CoreTriple ScalarCoreOf(int cellA, int cellB)
    {
        int a = Math.Min(cellA, cellB), b = Math.Max(cellA, cellB);
        if (Layout == PlanarCoreLayout.Triangles)
        {
            long k = (long)a * CellCount - (long)a * (a - 1) / 2 + (b - a);
            return new(S0![k], SLog![k], SRad is null ? 0.0 : SRad[k]);
        }
        int v = BandClass![BandIndex(a, b)];
        if (v < 0) return CutScalar!.Get(a, b);
        long o = (long)(v >> 1) * ClassStride;
        return new(ClassCores![o], ClassCores[o + 1], Kernels > 2 ? ClassCores[o + 2] : 0.0);
    }

    /// <summary>The summed vector core of one same-direction basis pair, by DIRECTION POSITION,
    /// either layout — the number the packed triangle held. In the class layout a whole pair is
    /// assembled with P4's association (A half's inner cells ascending, then B's, then A + B).</summary>
    internal PlanarFill.CoreTriple VectorCoreOf(PlanarBasisDirection dir, int posI, int posJ)
    {
        int i = Math.Min(posI, posJ), j = Math.Max(posI, posJ);
        bool alongX = dir == PlanarBasisDirection.X;
        if (Layout == PlanarCoreLayout.Triangles)
        {
            int k = alongX ? XBases.Length : YBases.Length;
            long q = (long)i * k - (long)i * (i - 1) / 2 + (j - i);
            return alongX
                ? new(VX0![q], VXLog![q], VXRad is null ? 0.0 : VXRad[q])
                : new(VY0![q], VYLog![q], VYRad is null ? 0.0 : VYRad[q]);
        }
        var d  = Topology.Of(dir);
        int bi = d.Idx[i], bj = d.Idx[j];
        if (!Memoised[bi] || !Memoised[bj]) return (alongX ? CutX : CutY)!.Get(i, j);
        return PlanarFill.WholeVectorCore(new PlanarFill.BandSource(this), Mesh,
                                          Topology.Halves[bi], Topology.Halves[bj], alongX);
    }

    /// <summary>
    /// D6's geometric cores for one CELL pair, area-normalised: <c>∫∫dS′dS/R</c>, <c>∫∫ln r</c> and
    /// <c>∫∫r</c> divided by <c>A_a·A_b</c>. Exposed because they are the frequency-independent part
    /// of the answer and are therefore what a convergence or a hand check wants to look at — the
    /// self core of a unit square is the mean reciprocal distance 2.9732096, which can be obtained
    /// with nothing from this repository involved.
    /// </summary>
    public (double Inverse, double Log, double Radius) ScalarCore(int cellA, int cellB)
    {
        if (!HasPairCores)
            throw new InvalidOperationException(
                "These cores were built geometry-only (M5's accelerator path), so the cached cell-pair " +
                "cores do not exist. Ask PlanarEntryFill for the pair you want, or build the cores " +
                "with PlanarFill.BuildCores.");
        var t = ScalarCoreOf(cellA, cellB);
        return (t.Inverse, t.Log, t.Radius);
    }

    /// <summary>Bytes held by the cached cores — Tier 8 reports this beside the matrix's own.
    /// <see cref="VMoment"/> is O(N) and is counted here anyway, because P1's whole point is that this
    /// number is what the object HOLDS rather than what is interesting about it. P5: the classifier's
    /// own O(n_x² + n_y²) tables are counted for the same reason.</summary>
    public long CoreBytes =>
        8L * ((S0?.Length ?? 0) + (SLog?.Length ?? 0) + (SRad?.Length ?? 0)
            + (VX0?.Length ?? 0) + (VXLog?.Length ?? 0) + (VXRad?.Length ?? 0)
            + (VY0?.Length ?? 0) + (VYLog?.Length ?? 0) + (VYRad?.Length ?? 0)
            + VMoment.Length
            + (BandStart?.Length ?? 0) + (ClassKey?.Length ?? 0) + (ClassCores?.Length ?? 0))
        + 4L * (BandClass?.Length ?? 0)
        + (CutScalar?.Bytes ?? 0) + (CutX?.Bytes ?? 0) + (CutY?.Bytes ?? 0)
        + ClassifierBytes;
}

/// <summary>
/// The Galerkin matrix fill for the planar full-wave kernel. See the file header for the
/// formulation, for D4/D5/D6, and for what is deliberately NOT here (ports, excitation,
/// s-parameters — all L8d).
/// </summary>
public static class PlanarFill
{
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D6's core build
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the frequency-independent geometric cores. <b>R-fil-10: refuses above R17's ceiling
    /// before allocating anything of that size</b>, using <see cref="SurfaceMesher"/>'s own wording.
    ///
    /// <para><b>P5 — one seven-primitive pass per translation CLASS, not per cell pair.</b> Every
    /// ordered pair in P4's band is classified (PlanarPairClasses.cs — an integer key on grid
    /// indices, spacing classes and the τ band), the distinct keys are sorted so the class ids are a
    /// function of the mesh alone (R-fil-11 — no scheduler order reaches the result), and the seven
    /// primitives are computed once per class on the class's synthetic representative. What is held
    /// is the 4-byte class index per band pair, the table, and rows for the pairs a class cannot
    /// serve; the per-basis-pair triangles P4 scattered into are not built at all — a vector entry
    /// is assembled from its four cell pairs' classes at the point of use.</para>
    /// </summary>
    public static PlanarFillCores BuildCores(PlanarMesh mesh, PlanarFillSettings? settings = null)
    {
        // Every fill in the engine passes through here, so this is the one place the settings
        // have to be sound — see PlanarFillSettings.Validate for why each check exists.
        (settings ?? PlanarFillSettings.Default).Validate();
        ArgumentNullException.ThrowIfNull(mesh);
        var st = settings ?? PlanarFillSettings.Default;

        int n = mesh.Bases.Count;
        int m = mesh.Cells.Count;
        GuardCeiling(n, m);

        var (minEdge, extent, rhoFloor) = MeshGeometry(mesh, st);
        bool wantRad = st.Order >= PlanarExtractionOrder.Linear;
        int  kernels = wantRad ? 3 : 2;

        var topo       = RampTopology.Build(mesh);
        var classifier = PairClassifier.Build(mesh);
        var memo       = MemoisedBases(mesh, topo, classifier);
        var ok         = classifier.Classifiable;

        // ── the band: P4's ordered pairs, c ≥ MinInner[a] ────────────────────────────────────
        var bandStart = new long[m + 1];
        for (int a = 0; a < m; a++) bandStart[a + 1] = bandStart[a] + (m - topo.MinInner[a]);
        long bandCount = bandStart[m];

        // ── classify: keys per pair (transient), the distinct keys sorted, ids by rank ────────
        var keys = new long[bandCount];
        ForRows(st, m, a =>
        {
            var ca = mesh.Cells[a];
            long at = bandStart[a];
            for (int c = topo.MinInner[a]; c < m; c++, at++)
                keys[at] = ok[a] && ok[c] ? classifier.Key(ca, mesh.Cells[c], st, out _) : -1L;
        });
        var distinct = new HashSet<long>();
        foreach (long k in keys) if (k >= 0) distinct.Add(k);
        var classKey = distinct.ToArray();
        Array.Sort(classKey);

        var bandClass = new int[bandCount];
        ForRows(st, m, a =>
        {
            var ca = mesh.Cells[a];
            long at = bandStart[a];
            for (int c = topo.MinInner[a]; c < m; c++, at++)
            {
                long k = keys[at];
                if (k < 0) { bandClass[at] = -1; continue; }
                classifier.Key(ca, mesh.Cells[c], st, out bool rot);
                bandClass[at] = (Array.BinarySearch(classKey, k) << 1) | (rot ? 1 : 0);
            }
        });
        keys = null!;

        // ── the class table: the seven primitives on each class's representative ─────────────
        int classCount = classKey.Length;
        int stride = 7 * kernels;
        var classCores = new double[(long)classCount * stride];
        ForRows(st, classCount, cls =>
        {
            long key = classKey[cls];
            var (outer, inner) = classifier.Representative(key);
            var (nodes, panels) = PairClassifier.CoreRule(key, st);
            var q = CellPairCores(outer, inner, nodes, panels, wantRad);
            long o = (long)cls * stride;
            void Put(int slot, CoreTriple t)
            {
                long p = o + (long)slot * kernels;
                classCores[p] = t.Inverse; classCores[p + 1] = t.Log;
                if (kernels > 2) classCores[p + 2] = t.Radius;
            }
            Put(0, q.Pulse);
            Put(1, q.X10); Put(2, q.X01); Put(3, q.X11);
            Put(4, q.Y10); Put(5, q.Y01); Put(6, q.Y11);
        });
        long passes = classCount;

        // ── rows for what no class can serve: cut (or otherwise unclassifiable) cells' scalar
        //    cores, and cut bases' summed vector cores by L8c's four calls, unchanged ──────────
        var unclassifiable = new bool[m];
        int cutCells = 0;
        for (int c = 0; c < m; c++) if (!ok[c]) { unclassifiable[c] = true; cutCells++; }
        CutRows? cutScalar = null;
        if (cutCells > 0)
        {
            cutScalar = new CutRows(m, unclassifiable, kernels);
            long local = 0;
            var rows = new List<int>();
            for (int c = 0; c < m; c++) if (unclassifiable[c]) rows.Add(c);
            ForRows(st, rows.Count, r =>
            {
                int a = rows[r];
                var pa = Pulse(mesh, a);
                for (int b = 0; b < m; b++)
                {
                    int lo = Math.Min(a, b), hi = Math.Max(a, b);
                    // the same call, the same orientation (lower index outer) the triangle held
                    var (c0, cl, cr) = lo == a
                        ? PairCores(mesh, pa, Pulse(mesh, hi), PlanarBasisDirection.X, wantRad, st)
                        : PairCores(mesh, Pulse(mesh, lo), pa, PlanarBasisDirection.X, wantRad, st);
                    cutScalar.Set(cutScalar.RowOf[a], b, new(c0, cl, cr));
                }
                Interlocked.Add(ref local, m);
            });
            passes += local;
        }

        CutRows? cutX = CutBasisRows(mesh, topo, topo.X, PlanarBasisDirection.X, memo, wantRad, kernels, st, ref passes);
        CutRows? cutY = CutBasisRows(mesh, topo, topo.Y, PlanarBasisDirection.Y, memo, wantRad, kernels, st, ref passes);

        st.CoreBuilds?.Observe(mesh, pairCores: true);

        long sCount = (long)m * (m + 1) / 2;
        long xCount = (long)topo.X.Count * (topo.X.Count + 1) / 2;
        long yCount = (long)topo.Y.Count * (topo.Y.Count + 1) / 2;
        return new PlanarFillCores(mesh, st, minEdge, extent, rhoFloor,
                                   classifier, memo, kernels,
                                   bandStart, bandClass, classKey, classCores,
                                   cutScalar, cutX, cutY,
                                   topo, BasisMoments(mesh),
                                   sCount, xCount + yCount, passes, hasPairCores: true);
    }

    /// <summary>Per basis: served from the class table — an uncut rooftop on two cells the
    /// classifier can name.</summary>
    private static bool[] MemoisedBases(PlanarMesh mesh, RampTopology topo, PairClassifier classifier)
    {
        int n = mesh.Bases.Count;
        var memo = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var (ha, hb) = topo.Halves[i];
            memo[i] = !topo.Cut[i] && classifier.Classifiable[ha.CellIndex] && classifier.Classifiable[hb.CellIndex];
        }
        return memo;
    }

    /// <summary>One direction's rows of four-call vector cores for every basis pair with a half
    /// the memo cannot serve — L8c's own statement, the lower position outer, as the triangle held it.</summary>
    private static CutRows? CutBasisRows(PlanarMesh mesh, RampTopology topo, RampTopology.DirectionMap d,
                                         PlanarBasisDirection dir, bool[] memo, bool wantRad, int kernels,
                                         PlanarFillSettings st, ref long passes)
    {
        int k = d.Count;
        var cut = new bool[k];
        var rows = new List<int>();
        for (int p = 0; p < k; p++) if (!memo[d.Idx[p]]) { cut[p] = true; rows.Add(p); }
        if (rows.Count == 0) return null;

        var table = new CutRows(k, cut, kernels);
        long local = 0;
        ForRows(st, rows.Count, r =>
        {
            int i = rows[r];
            for (int j = 0; j < k; j++)
            {
                int lo = Math.Min(i, j), hi = Math.Max(i, j);
                var (ma, mb) = topo.Halves[d.Idx[lo]];
                var (na, nb) = topo.Halves[d.Idx[hi]];
                var (t00, l00, r00) = PairCores(mesh, ma, na, dir, wantRad, st);
                var (t01, l01, r01) = PairCores(mesh, ma, nb, dir, wantRad, st);
                var (t10, l10, r10) = PairCores(mesh, mb, na, dir, wantRad, st);
                var (t11, l11, r11) = PairCores(mesh, mb, nb, dir, wantRad, st);
                table.Set(table.RowOf[i], j,
                          new(t00 + t01 + t10 + t11, l00 + l01 + l10 + l11, r00 + r01 + r10 + r11));
            }
            Interlocked.Add(ref local, 4L * k);
        });
        passes += local;
        return table;
    }

    /// <summary>
    /// <b>P5's REFERENCE — P4's core build: one seven-primitive pass per ORDERED cell pair in the
    /// band, scattered into P2's packed triangles.</b> Not a production path since P5: it exists so
    /// the 1e-12 agreement of <see cref="BuildCores"/>' class table with the per-pair arithmetic is a
    /// gate that stays runnable (<c>PlanarP5TranslationClassTests</c>), exactly as
    /// <see cref="BuildCoresByHalves"/> does for P4 against L8c. Produces the
    /// <see cref="PlanarCoreLayout.Triangles"/> layout; pair it with <see cref="FillByPairs"/>.
    /// </summary>
    public static PlanarFillCores BuildCoresByPairs(PlanarMesh mesh, PlanarFillSettings? settings = null)
    {
        (settings ?? PlanarFillSettings.Default).Validate();
        ArgumentNullException.ThrowIfNull(mesh);
        var st = settings ?? PlanarFillSettings.Default;

        int n = mesh.Bases.Count;
        int m = mesh.Cells.Count;
        GuardCeiling(n, m);

        var (minEdge, extent, rhoFloor) = MeshGeometry(mesh, st);

        bool wantRad = st.Order >= PlanarExtractionOrder.Linear;

        var topo = RampTopology.Build(mesh);
        var tx = topo.X;
        var ty = topo.Y;

        // ── the scalar half: one entry per unordered CELL pair (D4) ───────────────────────────
        long sCount = (long)m * (m + 1) / 2;
        var s0   = new double[sCount];
        var sLog = new double[sCount];
        var sRad = wantRad ? new double[sCount] : null;

        // ── the vector half: one entry per unordered SAME-DIRECTION basis pair (D5) ───────────
        // P4: the A-half contributions accumulate in the entry itself and the B-half ones in a
        // transient second triangle (see the header) — each written by exactly one outer cell.
        long xCount = (long)tx.Count * (tx.Count + 1) / 2;
        long yCount = (long)ty.Count * (ty.Count + 1) / 2;
        var vx0 = new double[xCount]; var vxLog = new double[xCount]; var vxRad = wantRad ? new double[xCount] : null;
        var vy0 = new double[yCount]; var vyLog = new double[yCount]; var vyRad = wantRad ? new double[yCount] : null;
        var bx0 = new double[xCount]; var bxLog = new double[xCount]; var bxRad = wantRad ? new double[xCount] : null;
        var by0 = new double[yCount]; var byLog = new double[yCount]; var byRad = wantRad ? new double[yCount] : null;

        long passes = 0;

        // ── P4: ONE pass per ordered cell pair, over outer cells ──────────────────────────────
        ForRows(st, m, a =>
        {
            long local = 0;
            var  ca    = mesh.Cells[a];
            bool aRect = !ca.IsCut;                 // the pulse path's rectangle (D4, bit-identical)
            bool aRamp = topo.HasWholeRamp[a];      // some rooftop on a takes the rectangle ramp
            var  pa    = Pulse(mesh, a);

            for (int c = topo.MinInner[a]; c < m; c++)
            {
                var  cc     = mesh.Cells[c];
                bool scalar = c >= a;
                bool rect   = aRect && !cc.IsCut;
                bool ramp   = aRamp && topo.HasWholeRamp[c];

                if (scalar && !rect)
                {
                    // R-cut-2: a scalar core with a cut cell takes the conformal path, as before.
                    long k = Packed(a, c, m);
                    var (c0, cl, cr) = PairCores(mesh, pa, Pulse(mesh, c), PlanarBasisDirection.X, wantRad, st);
                    s0[k] = c0; sLog[k] = cl;
                    if (sRad is not null) sRad[k] = cr;
                    local++;
                }
                if (!ramp && !(scalar && rect)) continue;

                var q = CellPairCores(mesh, a, c, wantRad, st);
                local++;

                if (scalar && rect)
                {
                    long k = Packed(a, c, m);
                    s0[k] = q.Pulse.Inverse; sLog[k] = q.Pulse.Log;
                    if (sRad is not null) sRad[k] = q.Pulse.Radius;
                }
                if (ramp)
                {
                    Scatter(tx, topo.Cut, a, c, ca.Width,  cc.Width,  q.Pulse, q.X10, q.X01, q.X11,
                            vx0, vxLog, vxRad, bx0, bxLog, bxRad);
                    Scatter(ty, topo.Cut, a, c, ca.Height, cc.Height, q.Pulse, q.Y10, q.Y01, q.Y11,
                            vy0, vyLog, vyRad, by0, byLog, byRad);
                }
            }
            Interlocked.Add(ref passes, local);
        });

        // ── the row pass: add the B triangle in, and take the four-call path on cut pairs ─────
        passes += FinishDirectionCores(mesh, topo, tx, PlanarBasisDirection.X, wantRad, st,
                                       vx0, vxLog, vxRad, bx0, bxLog, bxRad);
        passes += FinishDirectionCores(mesh, topo, ty, PlanarBasisDirection.Y, wantRad, st,
                                       vy0, vyLog, vyRad, by0, byLog, byRad);

        st.CoreBuilds?.Observe(mesh, pairCores: true);

        return new PlanarFillCores(mesh, st, minEdge, extent, rhoFloor,
                                   s0, sLog, sRad,
                                   vx0, vxLog, vxRad,
                                   vy0, vyLog, vyRad,
                                   BasisMoments(mesh), topo, PairClassifier.Build(mesh),
                                   sCount, xCount + yCount, passes);
    }

    /// <summary>
    /// <b>M5 — everything <see cref="BuildCores"/> produces EXCEPT the per-pair band and the class table.</b>
    /// The ρ floor, the extent, the smallest cell edge and the per-direction index maps are all O(N)
    /// and every consumer of them (the kernel's re-floor, the radial remainder table, the direction
    /// split) works unchanged; what is absent is precisely the cached pair arithmetic that the AIM
    /// accelerator never asks for, because it touches O(N) pairs and evaluates those on demand.
    ///
    /// <para>Building the full cores and then ignoring them would leave M5's whole cost claim resting
    /// on an O(N²) build — which is the thing being removed. <see cref="PlanarFillCores.HasPairCores"/>
    /// is false on the result and the dense fills refuse it by name.</para>
    /// </summary>
    public static PlanarFillCores BuildGeometryOnlyCores(PlanarMesh mesh, PlanarFillSettings? settings = null)
    {
        (settings ?? PlanarFillSettings.Default).Validate();
        ArgumentNullException.ThrowIfNull(mesh);
        var st = settings ?? PlanarFillSettings.Default;

        int n = mesh.Bases.Count;
        var (minEdge, extent, rhoFloor) = MeshGeometry(mesh, st);

        var topo = RampTopology.Build(mesh);
        var classifier = PairClassifier.Build(mesh);

        st.CoreBuilds?.Observe(mesh, pairCores: false);

        return new PlanarFillCores(mesh, st, minEdge, extent, rhoFloor,
                                   classifier, MemoisedBases(mesh, topo, classifier),
                                   st.Order >= PlanarExtractionOrder.Linear ? 3 : 2,
                                   null, null, null, null, null, null, null,
                                   topo, BasisMoments(mesh),
                                   0, 0, 0, hasPairCores: false);
    }

    /// <summary>The three mesh-wide scalars both core builders derive, in one place so the AIM path's
    /// ρ floor and radial-table span are literally the dense path's rather than a second derivation
    /// that can drift from it.</summary>
    private static (double MinEdge, double Extent, double RhoFloor) MeshGeometry(
        PlanarMesh mesh, PlanarFillSettings st)
    {
        double minEdge = double.PositiveInfinity;
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var c in mesh.Cells)
        {
            minEdge = Math.Min(minEdge, Math.Min(c.Width, c.Height));
            x0 = Math.Min(x0, c.XMin); y0 = Math.Min(y0, c.YMin);
            x1 = Math.Max(x1, c.XMax); y1 = Math.Max(y1, c.YMax);
        }
        if (mesh.Cells.Count == 0) { minEdge = 0; x0 = y0 = x1 = y1 = 0; }
        return (minEdge,
                Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)),
                st.RhoFloorFraction * minEdge);
    }

    /// <summary>
    /// <b>P2/M1 — ∫w dS for every basis in the mesh</b>, the vector block's extracted CONSTANT core in
    /// its factored form. O(N), so both core builders carry it, including the geometry-only one that
    /// the accelerator uses (<see cref="PlanarEntryFill"/> derived exactly this array for itself
    /// before P2 and now reads this one, so there is a single derivation of it).
    /// </summary>
    private static double[] BasisMoments(PlanarMesh mesh)
    {
        int n = mesh.Bases.Count;
        var moments = new double[n];
        for (int i = 0; i < n; i++)
        {
            var basis    = mesh.Bases[i];
            var (wa, wb) = RampHalves(mesh, basis);
            var ca  = mesh.Cells[wa.CellIndex];
            var cb  = mesh.Cells[wb.CellIndex];
            var dir = basis.Direction;
            // ∫ w dS over a half is (its extent along the flow direction)/2 — see the file header.
            // On a CUT cell that is no longer the rectangle's own extent, because the ramp is measured
            // from the metal's boundary: the honest quantity is the first moment of the weight itself,
            // and it is taken from the strips rather than from Width/Height. The whole-rectangle
            // expression is left LITERALLY as L8c wrote it, so R-cut-2's bit-identity survives an
            // association that would otherwise move the last bit.
            moments[i] = !ca.IsCut && !cb.IsCut
                ? 0.5 * (Extent(ca, dir) + Extent(cb, dir))
                : WeightMoment(ca, wa, dir) + WeightMoment(cb, wb, dir);
        }
        return moments;
    }

    /// <summary>
    /// <b>P4's REFERENCE — the pre-P4 core build, every same-direction basis pair by four (half, half)
    /// quadratures.</b> Not a production path: it exists so the 1e-12 agreement of
    /// <see cref="BuildCores"/> with the four-call arithmetic is a gate that stays runnable
    /// (<c>PlanarP4MomentCacheTests</c>) rather than a digest printed once against a tree that no
    /// longer exists. Its scalar half is <see cref="BuildCores"/>' own (bit-identical); only the
    /// vector half differs, and only in association.
    /// </summary>
    public static PlanarFillCores BuildCoresByHalves(PlanarMesh mesh, PlanarFillSettings? settings = null)
    {
        (settings ?? PlanarFillSettings.Default).Validate();
        ArgumentNullException.ThrowIfNull(mesh);
        var st = settings ?? PlanarFillSettings.Default;

        int m = mesh.Cells.Count;
        GuardCeiling(mesh.Bases.Count, m);
        var (minEdge, extent, rhoFloor) = MeshGeometry(mesh, st);
        bool wantRad = st.Order >= PlanarExtractionOrder.Linear;
        var topo = RampTopology.Build(mesh);

        long sCount = (long)m * (m + 1) / 2;
        var s0   = new double[sCount];
        var sLog = new double[sCount];
        var sRad = wantRad ? new double[sCount] : null;
        ForRows(st, m, a =>
        {
            var wa = Pulse(mesh, a);
            for (int b = a; b < m; b++)
            {
                long k = Packed(a, b, m);
                var (c0, cl, cr) = PairCores(mesh, wa, Pulse(mesh, b), PlanarBasisDirection.X, wantRad, st);
                s0[k] = c0; sLog[k] = cl;
                if (sRad is not null) sRad[k] = cr;
            }
        });

        var (vx0, vxLog, vxRad) = DirectionCoresByHalves(mesh, topo, topo.X, PlanarBasisDirection.X, wantRad, st);
        var (vy0, vyLog, vyRad) = DirectionCoresByHalves(mesh, topo, topo.Y, PlanarBasisDirection.Y, wantRad, st);
        long vCount = vx0.LongLength + vy0.LongLength;

        st.CoreBuilds?.Observe(mesh, pairCores: true);
        return new PlanarFillCores(mesh, st, minEdge, extent, rhoFloor,
                                   s0, sLog, sRad,
                                   vx0, vxLog, vxRad, vy0, vyLog, vyRad,
                                   BasisMoments(mesh), topo, PairClassifier.Build(mesh),
                                   sCount, vCount, sCount + 4 * vCount);
    }

    private static (double[] C0, double[] CLog, double[]? CRad) DirectionCoresByHalves(
        PlanarMesh mesh, RampTopology topo, RampTopology.DirectionMap d, PlanarBasisDirection dir,
        bool wantRad, PlanarFillSettings st)
    {
        int k = d.Count;
        long count = (long)k * (k + 1) / 2;
        var c0   = new double[count];
        var cLog = new double[count];
        var cRad = wantRad ? new double[count] : null;
        ForRows(st, k, i =>
        {
            var (ma, mb) = topo.Halves[d.Idx[i]];
            for (int j = i; j < k; j++)
            {
                var (na, nb) = topo.Halves[d.Idx[j]];
                long p = Packed(i, j, k);
                var (t00, l00, r00) = PairCores(mesh, ma, na, dir, wantRad, st);
                var (t01, l01, r01) = PairCores(mesh, ma, nb, dir, wantRad, st);
                var (t10, l10, r10) = PairCores(mesh, mb, na, dir, wantRad, st);
                var (t11, l11, r11) = PairCores(mesh, mb, nb, dir, wantRad, st);
                c0[p] = t00 + t01 + t10 + t11;
                cLog[p] = l00 + l01 + l10 + l11;
                if (cRad is not null) cRad[p] = r00 + r01 + r10 + r11;
            }
        });
        return (c0, cLog, cRad);
    }

    /// <summary>
    /// P4's row pass over one direction's packed triangle: adds the transient B-half triangle into
    /// the entry (A-half + B-half, in that order — the association the per-entry fill reproduces),
    /// and computes any pair with a CUT half by L8c's four-call path exactly as before. Returns the
    /// number of quadrature passes the fallback ran.
    /// </summary>
    private static long FinishDirectionCores(PlanarMesh mesh, RampTopology topo, RampTopology.DirectionMap d,
                                             PlanarBasisDirection dir, bool wantRad, PlanarFillSettings st,
                                             double[] c0, double[] cLog, double[]? cRad,
                                             double[] b0, double[] bLog, double[]? bRad)
    {
        int k = d.Count;
        long passes = 0;
        ForRows(st, k, i =>
        {
            long local = 0;
            int  bi    = d.Idx[i];
            var (ma, mb) = topo.Halves[bi];
            for (int j = i; j < k; j++)
            {
                int  bj = d.Idx[j];
                long p  = Packed(i, j, k);
                if (topo.Cut[bi] || topo.Cut[bj])
                {
                    var (na, nb) = topo.Halves[bj];
                    // Unrolled rather than looped over a temporary array: an allocation here shows
                    // up as GC pressure at the ceiling, and this is L8c's own statement verbatim.
                    var (t00, l00, r00) = PairCores(mesh, ma, na, dir, wantRad, st);
                    var (t01, l01, r01) = PairCores(mesh, ma, nb, dir, wantRad, st);
                    var (t10, l10, r10) = PairCores(mesh, mb, na, dir, wantRad, st);
                    var (t11, l11, r11) = PairCores(mesh, mb, nb, dir, wantRad, st);
                    c0[p] = t00 + t01 + t10 + t11;
                    cLog[p] = l00 + l01 + l10 + l11;
                    if (cRad is not null) cRad[p] = r00 + r01 + r10 + r11;
                    local += 4;
                }
                else
                {
                    c0[p] += b0[p];
                    cLog[p] += bLog[p];
                    if (cRad is not null) cRad[p] += bRad![p];
                }
            }
            Interlocked.Add(ref passes, local);
        });
        return passes;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P4 — the cell-pair primitives, the linear map, and the scatter into basis-pair slots
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The three geometric kernels' values of one primitive — <c>1/R</c>, <c>ln r</c> and
    /// (when <c>Order ≥ Linear</c>) <c>r</c>; zero otherwise.</summary>
    internal readonly record struct CoreTriple(double Inverse, double Log, double Radius)
    {
        public static CoreTriple operator +(CoreTriple a, CoreTriple b)
            => new(a.Inverse + b.Inverse, a.Log + b.Log, a.Radius + b.Radius);
        public static CoreTriple operator -(CoreTriple a, CoreTriple b)
            => new(a.Inverse - b.Inverse, a.Log - b.Log, a.Radius - b.Radius);
        public static CoreTriple operator *(double s, CoreTriple a)
            => new(s * a.Inverse, s * a.Log, s * a.Radius);
    }

    /// <summary>The seven frequency-independent primitives of one ORDERED cell pair — see the P4
    /// header. <see cref="Pulse"/> is direction-free; the other six are per flow direction.</summary>
    internal readonly record struct CellPairMoments(CoreTriple Pulse,
                                                    CoreTriple X10, CoreTriple X01, CoreTriple X11,
                                                    CoreTriple Y10, CoreTriple Y01, CoreTriple Y11);

    /// <summary>The same seven sums of the smooth remainder <c>rem(ρ)</c>, per frequency.</summary>
    internal readonly record struct CellPairRemainders(Complex Pulse,
                                                       Complex X10, Complex X01, Complex X11,
                                                       Complex Y10, Complex Y01, Complex Y11);

    /// <summary>(P4.2) — one (half, half) combination from the four primitives of a flow direction.
    /// <paramref name="da"/>/<paramref name="dc"/> are the outer/inner cells' extents along it.</summary>
    internal static CoreTriple Combine(bool outerB, bool innerB, double da, double dc,
                                       CoreTriple q00, CoreTriple q10, CoreTriple q01, CoreTriple q11)
        => (outerB, innerB) switch
        {
            (false, false) => q11,
            (false, true)  => dc * q10 - q11,
            (true,  false) => da * q01 - q11,
            _              => (da * dc) * q00 - da * q01 - dc * q10 + q11,
        };

    /// <inheritdoc cref="Combine(bool, bool, double, double, CoreTriple, CoreTriple, CoreTriple, CoreTriple)"/>
    internal static Complex Combine(bool outerB, bool innerB, double da, double dc,
                                    Complex q00, Complex q10, Complex q01, Complex q11)
        => (outerB, innerB) switch
        {
            (false, false) => q11,
            (false, true)  => dc * q10 - q11,
            (true,  false) => da * q01 - q11,
            _              => (da * dc) * q00 - da * q01 - dc * q10 + q11,
        };

    /// <summary>
    /// <b>P5 — where a whole ordered cell pair's primitives come from.</b> The dense fill reads the
    /// band and the class table (<see cref="BandSource"/>); AIM's per-entry fill reads its on-demand
    /// class cache. A struct constraint rather than a delegate, so the JIT specialises
    /// <see cref="WholeVectorEntry{T}"/> per source and the assembly is ONE function — which is what
    /// makes <see cref="PlanarEntryFill.At"/> bit-identical to <see cref="Fill"/> by construction.
    /// </summary>
    internal interface IPairSource
    {
        /// <summary>The class primitives of the ordered pair, and whether the pair is the class
        /// representative's 180° rotation (in which case its A and B halves read swapped).</summary>
        void Get(int outer, int inner, out CellPairMoments cores, out bool rotated);
        /// <summary>The class's seven remainder sums at the current frequency.</summary>
        CellPairRemainders Remainder(int outer, int inner);
    }

    /// <summary>The dense path's source: the band index, the class table, and the per-frequency
    /// per-class remainder table the fill hands it.</summary>
    internal readonly struct BandSource(PlanarFillCores cores, CellPairRemainders[]? remainders = null) : IPairSource
    {
        private readonly PlanarFillCores       _cores = cores;
        private readonly CellPairRemainders[]? _rem   = remainders;

        public void Get(int outer, int inner, out CellPairMoments cores, out bool rotated)
        {
            int v = _cores.BandClass![_cores.BandIndex(outer, inner)];
            rotated = (v & 1) != 0;
            cores = _cores.ClassMoments(v >> 1);
        }

        public CellPairRemainders Remainder(int outer, int inner)
            => _rem![_cores.BandClass![_cores.BandIndex(outer, inner)] >> 1];
    }

    /// <summary>
    /// The summed geometric core of one whole same-direction basis pair from its four ordered cell
    /// pairs — P4's slot association: A half's inner cells ascending, then B half's, then A + B. A
    /// rotated member swaps its (A, B) reading of the representative: one xor on each Combine.
    /// </summary>
    internal static CoreTriple WholeVectorCore<T>(T src, PlanarMesh mesh,
                                                  (CellWeight A, CellWeight B) hi, (CellWeight A, CellWeight B) hj,
                                                  bool alongX) where T : struct, IPairSource
    {
        var (ra, rb) = hi;
        var (sa, sb) = hj;
        var (cLo, loB, cHi, hiB) = sa.CellIndex <= sb.CellIndex
            ? (sa.CellIndex, false, sb.CellIndex, true)
            : (sb.CellIndex, true, sa.CellIndex, false);
        double dA  = ExtentAlong(mesh, ra.CellIndex, alongX), dB = ExtentAlong(mesh, rb.CellIndex, alongX);
        double dLo = ExtentAlong(mesh, cLo, alongX),          dHi = ExtentAlong(mesh, cHi, alongX);

        src.Get(ra.CellIndex, cLo, out var pALo, out bool rALo);
        src.Get(ra.CellIndex, cHi, out var pAHi, out bool rAHi);
        src.Get(rb.CellIndex, cLo, out var pBLo, out bool rBLo);
        src.Get(rb.CellIndex, cHi, out var pBHi, out bool rBHi);

        var coreA = CombineCores(rALo, loB ^ rALo, dA, dLo, in pALo, alongX)
                  + CombineCores(rAHi, hiB ^ rAHi, dA, dHi, in pAHi, alongX);
        var coreB = CombineCores(!rBLo, loB ^ rBLo, dB, dLo, in pBLo, alongX)
                  + CombineCores(!rBHi, hiB ^ rBHi, dB, dHi, in pBHi, alongX);
        return coreA + coreB;
    }

    /// <summary>
    /// One whole same-direction basis pair's vector entry before the jωµ₀ scale: the coefficient
    /// times the summed core (<see cref="WholeVectorCore{T}"/>), the extracted constant's outer
    /// product, and the remainder assembled from the four cell pairs' class sums in the same
    /// association. Used by the dense fill and by <see cref="PlanarEntryFill.At"/>.
    /// </summary>
    internal static Complex WholeVectorEntry<T>(T src, PlanarMesh mesh,
                                                (CellWeight A, CellWeight B) hi, (CellWeight A, CellWeight B) hj,
                                                bool alongX, PlanarKernelTerms terms, double momI, double momJ)
        where T : struct, IPairSource
    {
        var (ra, rb) = hi;
        var (sa, sb) = hj;
        var (cLo, loB, cHi, hiB) = sa.CellIndex <= sb.CellIndex
            ? (sa.CellIndex, false, sb.CellIndex, true)
            : (sb.CellIndex, true, sa.CellIndex, false);
        double dA  = ExtentAlong(mesh, ra.CellIndex, alongX), dB = ExtentAlong(mesh, rb.CellIndex, alongX);
        double dLo = ExtentAlong(mesh, cLo, alongX),          dHi = ExtentAlong(mesh, cHi, alongX);

        var core = WholeVectorCore(src, mesh, hi, hj, alongX);

        // the rotation flags again, for the remainder — the same four lookups' answers
        src.Get(ra.CellIndex, cLo, out _, out bool rALo);
        src.Get(ra.CellIndex, cHi, out _, out bool rAHi);
        src.Get(rb.CellIndex, cLo, out _, out bool rBLo);
        src.Get(rb.CellIndex, cHi, out _, out bool rBHi);
        var qALo = src.Remainder(ra.CellIndex, cLo); var qAHi = src.Remainder(ra.CellIndex, cHi);
        var qBLo = src.Remainder(rb.CellIndex, cLo); var qBHi = src.Remainder(rb.CellIndex, cHi);

        Complex remA = CombineRem(rALo, loB ^ rALo, dA, dLo, in qALo, alongX)
                     + CombineRem(rAHi, hiB ^ rAHi, dA, dHi, in qAHi, alongX);
        Complex remB = CombineRem(!rBLo, loB ^ rBLo, dB, dLo, in qBLo, alongX)
                     + CombineRem(!rBHi, hiB ^ rBHi, dB, dHi, in qBHi, alongX);

        Complex v = terms.Inverse * core.Inverse + terms.Log * core.Log;
        if (terms.ExtractsConstant) v += terms.Constant * (momI * momJ);
        if (terms.ExtractsLinear)   v += terms.Linear   * core.Radius;

        Complex r = remA + remB;
        return v + r;
    }

    private static double ExtentAlong(PlanarMesh mesh, int cell, bool alongX)
        => alongX ? mesh.Cells[cell].Width : mesh.Cells[cell].Height;

    internal static CoreTriple CombineCores(bool outerB, bool innerB, double da, double dc,
                                            in CellPairMoments q, bool alongX)
        => alongX ? Combine(outerB, innerB, da, dc, q.Pulse, q.X10, q.X01, q.X11)
                  : Combine(outerB, innerB, da, dc, q.Pulse, q.Y10, q.Y01, q.Y11);

    internal static Complex CombineRem(bool outerB, bool innerB, double da, double dc,
                                       in CellPairRemainders q, bool alongX)
        => alongX ? Combine(outerB, innerB, da, dc, q.Pulse, q.X10, q.X01, q.X11)
                  : Combine(outerB, innerB, da, dc, q.Pulse, q.Y10, q.Y01, q.Y11);

    /// <summary>
    /// The seven primitives of one ordered WHOLE-rectangle cell pair, in one outer pass — L8c's own
    /// rule (the same panels, nodes and nesting order as <see cref="PairCores"/>), with every inner
    /// closed form evaluated once per node and used by all seven. The pulse×pulse accumulation is
    /// the pulse path's own expressions in the pulse path's own order, so S0/SLog are bit-identical
    /// to the pre-P4 build; the ⟨A, A⟩ accumulation likewise reproduces the ramp path's <c>t00</c>.
    /// </summary>
    internal static CellPairMoments CellPairCores(PlanarMesh mesh, int outer, int inner, bool wantRad,
                                                  PlanarFillSettings st)
    {
        var a = mesh.Cells[outer];
        var b = mesh.Cells[inner];
        var (nodes, panels) = RuleFor(a, b, st);
        return CellPairCores(a, b, nodes, panels, wantRad);
    }

    /// <summary>P5 — the same pass on two cells with the rule given, which is how a translation
    /// class's representative is integrated: the rule is the class's (it is in the key), never
    /// re-derived from the representative's own floats.</summary>
    internal static CellPairMoments CellPairCores(PlanarCell a, PlanarCell b, int nodes, int panels, bool wantRad)
    {
        var (gx, gw) = Legendre.Nodes(nodes);
        double invAb = 1.0 / b.Area, invAa = 1.0 / a.Area;
        var t = PanelEdges(panels);

        double p0 = 0, pl = 0, pr = 0;
        double x10i = 0, x10l = 0, x10r = 0, x01i = 0, x01l = 0, x01r = 0, x11i = 0, x11l = 0, x11r = 0;
        double y10i = 0, y10l = 0, y10r = 0, y01i = 0, y01l = 0, y01r = 0, y11i = 0, y11l = 0, y11r = 0;

        for (int qx = 0; qx < panels; qx++)
            for (int qy = 0; qy < panels; qy++)
            {
                double xa = a.XMin + t[qx] * a.Width,  xb = a.XMin + t[qx + 1] * a.Width;
                double ya = a.YMin + t[qy] * a.Height, yb = a.YMin + t[qy + 1] * a.Height;
                double cx = 0.5 * (xa + xb), hx = 0.5 * (xb - xa);
                double cy = 0.5 * (ya + yb), hy = 0.5 * (yb - ya);

                for (int i = 0; i < nodes; i++)
                {
                    double x = cx + hx * gx[i];
                    for (int j = 0; j < nodes; j++)
                    {
                        double y  = cy + hy * gx[j];
                        double wq = gw[i] * gw[j] * hx * hy;

                        double x1 = b.XMin - x, x2 = b.XMax - x;
                        double y1 = b.YMin - y, y2 = b.YMax - y;

                        double inv = RectangleIntegrals.Inverse(x1, x2, y1, y2);
                        double lg  = RectangleIntegrals.Log(x1, x2, y1, y2);
                        double rad = wantRad ? RectangleIntegrals.Radius(x1, x2, y1, y2) : 0.0;

                        // pulse × pulse — D4's own arithmetic (i0 = Inverse·invAb; w = wq·invAa)
                        double i0 = inv * invAb, il = lg * invAb, ir = 0;
                        if (wantRad) ir = rad * invAb;
                        double wp = wq * invAa;
                        p0 += wp * i0;
                        pl += wp * il;
                        if (wantRad) pr += wp * ir;

                        // inner rising ramps: ξ_c = u′ − u₀,c in the frame centred on the node
                        double cxu = x - b.XMin, cyu = y - b.YMin;
                        double i0x = invAb * (RectangleIntegrals.InverseMomentU(x1, x2, y1, y2) + cxu * inv);
                        double ilx = invAb * (RectangleIntegrals.LogMomentU(x1, x2, y1, y2) + cxu * lg);
                        double i0y = invAb * (RectangleIntegrals.InverseMomentV(x1, x2, y1, y2) + cyu * inv);
                        double ily = invAb * (RectangleIntegrals.LogMomentV(x1, x2, y1, y2) + cyu * lg);
                        double irx = 0, iry = 0;
                        if (wantRad)
                        {
                            irx = invAb * (RectangleIntegrals.RadiusMomentU(x1, x2, y1, y2) + cxu * rad);
                            iry = invAb * (RectangleIntegrals.RadiusMomentV(x1, x2, y1, y2) + cyu * rad);
                        }

                        // pulse outer × ramp inner
                        x01i += wp * i0x; x01l += wp * ilx; if (wantRad) x01r += wp * irx;
                        y01i += wp * i0y; y01l += wp * ily; if (wantRad) y01r += wp * iry;

                        // rising ramp outer — the ramp path's own weight, |u − u₀|·invAa, and its
                        // own "skip a node of zero weight"
                        double wx = wq * (Math.Abs(x - a.XMin) * invAa);
                        if (wx != 0)
                        {
                            x10i += wx * i0;  x10l += wx * il;  if (wantRad) x10r += wx * ir;
                            x11i += wx * i0x; x11l += wx * ilx; if (wantRad) x11r += wx * irx;
                        }
                        double wy = wq * (Math.Abs(y - a.YMin) * invAa);
                        if (wy != 0)
                        {
                            y10i += wy * i0;  y10l += wy * il;  if (wantRad) y10r += wy * ir;
                            y11i += wy * i0y; y11l += wy * ily; if (wantRad) y11r += wy * iry;
                        }
                    }
                }
            }

        return new CellPairMoments(new(p0, pl, pr),
                                   new(x10i, x10l, x10r), new(x01i, x01l, x01r), new(x11i, x11l, x11r),
                                   new(y10i, y10l, y10r), new(y01i, y01l, y01r), new(y11i, y11l, y11r));
    }

    /// <summary>
    /// The seven remainder sums of one ordered whole-rectangle cell pair at one frequency —
    /// <see cref="PairRemainder"/>'s own rule and node order, with <c>rem(ρ)</c> evaluated once per
    /// (outer, inner) node and weighted seven ways. This is the per-frequency cost P4 exists to cut.
    /// </summary>
    internal static CellPairRemainders CellPairRemainder(PlanarMesh mesh, int outer, int inner,
                                                         Func<double, Complex> rem, PlanarFillSettings st)
    {
        var a = mesh.Cells[outer];
        var b = mesh.Cells[inner];
        double tau = SeparationRatio(a, b);
        int nodes = tau < st.NearRatio ? st.RemainderNodesNear
                  : tau < st.FarRatio  ? st.RemainderNodesMid
                                       : st.RemainderNodesFar;
        return CellPairRemainder(a, b, nodes, rem);
    }

    /// <summary>P5 — the pulse×pulse remainder alone, for the scalar block's per-class pass: the
    /// pulse sum of <see cref="CellPairRemainder(PlanarCell, PlanarCell, int, Func{double, Complex})"/>
    /// in the pulse path's own expressions, without the six ramp sums it has no use for.</summary>
    internal static Complex CellPairPulseRemainder(PlanarCell a, PlanarCell b, int nodes, Func<double, Complex> rem)
    {
        var (gx, gw) = Legendre.Nodes(nodes);
        double invAa = 1.0 / a.Area, invAb = 1.0 / b.Area;
        double hax = 0.5 * a.Width, hay = 0.5 * a.Height;
        double hbx = 0.5 * b.Width, hby = 0.5 * b.Height;

        Complex p = Complex.Zero;
        for (int i = 0; i < nodes; i++)
        {
            double x = a.CenterX + hax * gx[i];
            for (int j = 0; j < nodes; j++)
            {
                double y  = a.CenterY + hay * gx[j];
                double wp = gw[i] * gw[j] * hax * hay * invAa;
                Complex sp = Complex.Zero;
                for (int k = 0; k < nodes; k++)
                {
                    double xp = b.CenterX + hbx * gx[k];
                    for (int l = 0; l < nodes; l++)
                    {
                        double yp  = b.CenterY + hby * gx[l];
                        double dx  = x - xp, dy = y - yp;
                        sp += gw[k] * gw[l] * invAb * rem(Math.Sqrt(dx * dx + dy * dy));
                    }
                }
                p += wp * sp * hbx * hby;
            }
        }
        return p;
    }

    /// <inheritdoc cref="CellPairRemainder(PlanarMesh, int, int, Func{double, Complex}, PlanarFillSettings)"/>
    internal static CellPairRemainders CellPairRemainder(PlanarCell a, PlanarCell b, int nodes,
                                                         Func<double, Complex> rem)
    {
        var (gx, gw) = Legendre.Nodes(nodes);

        double invAa = 1.0 / a.Area, invAb = 1.0 / b.Area;
        double hax = 0.5 * a.Width, hay = 0.5 * a.Height;
        double hbx = 0.5 * b.Width, hby = 0.5 * b.Height;

        Complex p = Complex.Zero;
        Complex x10 = Complex.Zero, x01 = Complex.Zero, x11 = Complex.Zero;
        Complex y10 = Complex.Zero, y01 = Complex.Zero, y11 = Complex.Zero;

        for (int i = 0; i < nodes; i++)
        {
            double x = a.CenterX + hax * gx[i];
            for (int j = 0; j < nodes; j++)
            {
                double y  = a.CenterY + hay * gx[j];
                double wp = gw[i] * gw[j] * hax * hay * invAa;
                double wx = gw[i] * gw[j] * hax * hay * (Math.Abs(x - a.XMin) * invAa);
                double wy = gw[i] * gw[j] * hax * hay * (Math.Abs(y - a.YMin) * invAa);

                Complex sp = Complex.Zero, sx = Complex.Zero, sy = Complex.Zero;
                for (int k = 0; k < nodes; k++)
                {
                    double xp = b.CenterX + hbx * gx[k];
                    for (int l = 0; l < nodes; l++)
                    {
                        double yp  = b.CenterY + hby * gx[l];
                        double dx  = x - xp, dy = y - yp;
                        Complex r  = rem(Math.Sqrt(dx * dx + dy * dy));
                        double gkl = gw[k] * gw[l];
                        sp += gkl * invAb * r;
                        double wbx = Math.Abs(xp - b.XMin) * invAb;
                        if (wbx != 0) sx += gkl * wbx * r;
                        double wby = Math.Abs(yp - b.YMin) * invAb;
                        if (wby != 0) sy += gkl * wby * r;
                    }
                }
                p   += wp * sp * hbx * hby;
                x01 += wp * sx * hbx * hby;
                y01 += wp * sy * hbx * hby;
                if (wx != 0) { x10 += wx * sp * hbx * hby; x11 += wx * sx * hbx * hby; }
                if (wy != 0) { y10 += wy * sp * hbx * hby; y11 += wy * sy * hbx * hby; }
            }
        }
        return new CellPairRemainders(p, x10, x01, x11, y10, y01, y11);
    }

    /// <summary>Adds one ordered cell pair's contributions to every same-direction basis pair
    /// (i ≤ j) it serves: outer cell <paramref name="a"/> as i's A or B half, inner cell
    /// <paramref name="c"/> as j's. A-half contributions go to the entry, B-half ones to the
    /// transient triangle; pairs with a cut basis are left for the row pass.</summary>
    private static void Scatter(RampTopology.DirectionMap d, bool[] cut, int a, int c, double da, double dc,
                                CoreTriple q00, CoreTriple q10, CoreTriple q01, CoreTriple q11,
                                double[] a0, double[] aLog, double[]? aRad,
                                double[] b0, double[] bLog, double[]? bRad)
    {
        int k = d.Count;
        for (int ea = d.CellStart[a]; ea < d.CellStart[a + 1]; ea++)
        {
            int pi = d.Pos[ea];
            if (cut[d.Idx[pi]]) continue;
            bool outerB = d.IsB[ea];
            for (int ec = d.CellStart[c]; ec < d.CellStart[c + 1]; ec++)
            {
                int pj = d.Pos[ec];
                if (pj < pi || cut[d.Idx[pj]]) continue;
                var  v = Combine(outerB, d.IsB[ec], da, dc, q00, q10, q01, q11);
                long p = Packed(pi, pj, k);
                if (outerB) { b0[p] += v.Inverse; bLog[p] += v.Log; if (bRad is not null) bRad[p] += v.Radius; }
                else        { a0[p] += v.Inverse; aLog[p] += v.Log; if (aRad is not null) aRad[p] += v.Radius; }
            }
        }
    }

    /// <inheritdoc cref="Scatter(RampTopology.DirectionMap, bool[], int, int, double, double, CoreTriple, CoreTriple, CoreTriple, CoreTriple, double[], double[], double[], double[], double[], double[])"/>
    private static void Scatter(RampTopology.DirectionMap d, bool[] cut, int a, int c, double da, double dc,
                                Complex q00, Complex q10, Complex q01, Complex q11,
                                Complex[] slotA, Complex[] slotB)
    {
        int k = d.Count;
        for (int ea = d.CellStart[a]; ea < d.CellStart[a + 1]; ea++)
        {
            int pi = d.Pos[ea];
            if (cut[d.Idx[pi]]) continue;
            bool outerB = d.IsB[ea];
            for (int ec = d.CellStart[c]; ec < d.CellStart[c + 1]; ec++)
            {
                int pj = d.Pos[ec];
                if (pj < pi || cut[d.Idx[pj]]) continue;
                var  v = Combine(outerB, d.IsB[ec], da, dc, q00, q10, q01, q11);
                long p = Packed(pi, pj, k);
                if (outerB) slotB[p] += v; else slotA[p] += v;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The per-frequency assembly
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// D4's <c>P</c> — the area-averaged scalar-potential coefficient matrix over CELLS, in the
    /// repository's <c>φ = (1/ε₀)∫G_q q dS′</c> normalisation. Symmetric by construction (computed on
    /// <c>a ≤ b</c> and mirrored), and it <b>is</b> the electrostatic potential-coefficient matrix in
    /// the ω → 0 limit — which is what Tier 5's capacitance harness uses.
    /// </summary>
    public static Mat<Complex> ScalarPotentialMatrix(PlanarFillCores cores, PlanarKernelTerms termsQ)
    {
        ArgumentNullException.ThrowIfNull(cores);
        ArgumentNullException.ThrowIfNull(termsQ);
        RequirePairCores(cores);

        var mesh = cores.Mesh;
        int m = mesh.Cells.Count;
        var st = cores.Settings;
        var terms = termsQ.With(st.Order, cores.RhoFloorM);
        var rem = Remainder(terms, cores);

        var p = new Mat<Complex>(m, m);
        if (cores.Layout == PlanarCoreLayout.Triangles)
        {
            ForRows(st, m, a =>
            {
                var wa = Pulse(mesh, a);
                for (int b = a; b < m; b++)
                {
                    long k = Packed(a, b, m);
                    Complex v = terms.Inverse * cores.S0![k] + terms.Log * cores.SLog![k];
                    if (terms.ExtractsConstant) v += terms.Constant;           // area-normalised ⇒ core = 1
                    if (terms.ExtractsLinear && cores.SRad is not null) v += terms.Linear * cores.SRad[k];
                    v += PairRemainder(mesh, wa, Pulse(mesh, b), PlanarBasisDirection.X, rem, st);
                    p[b, a] = v;                                // the contiguous triangle (P3)
                }
            });
            MirrorLowerToUpper(st, p);                          // R-fil-2, structurally
            return p;
        }

        // P5: the pulse remainder once per class, then one lookup per pair. A pair with an
        // unclassifiable cell keeps L8c's own per-pair call, exactly as its core kept its own row.
        var classRem = ClassPulseRemainders(cores, rem);
        var band     = cores.BandClass!;
        var cut      = cores.CutScalar;
        var ok       = cores.Classifier.Classifiable;
        int kernels  = cores.Kernels, stride = cores.ClassStride;
        var table    = cores.ClassCores!;
        ForRows(st, m, a =>
        {
            CellWeight? wa = ok[a] ? null : Pulse(mesh, a);
            for (int b = a; b < m; b++)
            {
                int c = band[cores.BandIndex(a, b)];
                Complex v;
                if (c >= 0)
                {
                    long o = (long)(c >> 1) * stride;
                    v = terms.Inverse * table[o] + terms.Log * table[o + 1];
                    if (terms.ExtractsConstant) v += terms.Constant;
                    if (terms.ExtractsLinear && kernels > 2) v += terms.Linear * table[o + 2];
                    v += classRem[c >> 1];
                }
                else
                {
                    var t = cut!.Get(a, b);
                    v = terms.Inverse * t.Inverse + terms.Log * t.Log;
                    if (terms.ExtractsConstant) v += terms.Constant;
                    if (terms.ExtractsLinear) v += terms.Linear * t.Radius;
                    v += PairRemainder(mesh, wa ?? Pulse(mesh, a), Pulse(mesh, b), PlanarBasisDirection.X, rem, st);
                }
                p[b, a] = v;
            }
        });
        MirrorLowerToUpper(st, p);
        return p;
    }

    /// <summary>P5 — the pulse×pulse remainder of every class at this frequency, on the class's
    /// representative with the class's rule.</summary>
    private static Complex[] ClassPulseRemainders(PlanarFillCores cores, Func<double, Complex> rem)
    {
        var st   = cores.Settings;
        var keys = cores.ClassKey!;
        var outv = new Complex[keys.Length];
        ForRows(st, keys.Length, c =>
        {
            var (outer, inner) = cores.Classifier.Representative(keys[c]);
            outv[c] = CellPairPulseRemainder(outer, inner, PairClassifier.RemainderNodes(keys[c], st), rem);
        });
        return outv;
    }

    /// <summary>P5 — the seven remainder sums of every class at this frequency.</summary>
    private static CellPairRemainders[] ClassRemainders(PlanarFillCores cores, Func<double, Complex> rem)
    {
        var st   = cores.Settings;
        var keys = cores.ClassKey!;
        var outv = new CellPairRemainders[keys.Length];
        ForRows(st, keys.Length, c =>
        {
            var (outer, inner) = cores.Classifier.Representative(keys[c]);
            outv[c] = CellPairRemainder(outer, inner, PairClassifier.RemainderNodes(keys[c], st), rem);
        });
        return outv;
    }

    /// <summary>
    /// The full Galerkin matrix at one angular frequency. <b>R-fil-2: computed on <c>m ≤ n</c> and
    /// mirrored, so <c>Z[m,n]</c> and <c>Z[n,m]</c> are bit-identical by construction</b> rather than
    /// by the Green's function's reciprocity happening to come out — that is a different question and
    /// gets its own test.
    ///
    /// <para><b>P5:</b> the per-frequency remainder is evaluated once per translation class (the
    /// vector block's seven sums and the scalar block's pulse sum), and every entry is assembled from
    /// its cell pairs' class values — no per-pair quadrature and no transient per-basis-pair
    /// triangles. The per-pair arithmetic survives as <see cref="FillByPairs"/>, the reference.</para>
    /// </summary>
    public static Mat<Complex> Fill(PlanarFillCores cores, PlanarKernelTerms termsA,
                                  PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        RequirePairCores(cores);
        RequireLayout(cores, PlanarCoreLayout.Classes, nameof(Fill));
        var mesh = cores.Mesh;
        int n = mesh.Bases.Count;
        GuardCeiling(n, mesh.Cells.Count);

        var z = new Mat<Complex>(n, n);
        var st = cores.Settings;

        ScalarBlock(z, cores, termsQ, omega);

        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
        var termsAr = termsA.With(st.Order, cores.RhoFloorM);
        var remA = Remainder(termsAr, cores);
        var classRem = ClassRemainders(cores, remA);
        st.Counters?.AddRemainderPasses(classRem.Length);

        var src = new BandSource(cores, classRem);
        AddDirectionBlockFromClasses(z, cores, src, cores.Topology.X, PlanarBasisDirection.X, cores.CutX, termsAr, remA, vectorScale);
        AddDirectionBlockFromClasses(z, cores, src, cores.Topology.Y, PlanarBasisDirection.Y, cores.CutY, termsAr, remA, vectorScale);

        MirrorLowerToUpper(st, z);
        return z;
    }

    /// <summary>P5 — one direction's vector block, every whole pair pulled from the class table
    /// through <see cref="WholeVectorEntry{T}"/>, every pair with an unserved basis by L8c's four
    /// calls as before.</summary>
    private static void AddDirectionBlockFromClasses(Mat<Complex> z, PlanarFillCores cores, BandSource src,
                                                     RampTopology.DirectionMap d, PlanarBasisDirection dir,
                                                     CutRows? cut, PlanarKernelTerms terms,
                                                     Func<double, Complex> rem, Complex scale)
    {
        var mesh = cores.Mesh;
        var st   = cores.Settings;
        var topo = cores.Topology;
        var memo = cores.Memoised;
        var idx  = d.Idx;
        int k    = d.Count;
        var mom  = cores.VMoment;
        bool alongX = dir == PlanarBasisDirection.X;
        var counters = st.Counters;

        ForRows(st, k, i =>
        {
            long local = 0;
            int  bi = idx[i];
            var  hi = topo.Halves[bi];
            for (int j = i; j < k; j++)
            {
                int bj = idx[j];
                Complex e;
                if (memo[bi] && memo[bj])
                    e = WholeVectorEntry(src, mesh, hi, topo.Halves[bj], alongX, terms, mom[bi], mom[bj]);
                else
                {
                    var (ma, mb) = hi;
                    var (na, nb) = topo.Halves[bj];
                    var t = cut!.Get(i, j);
                    Complex v = terms.Inverse * t.Inverse + terms.Log * t.Log;
                    if (terms.ExtractsConstant) v += terms.Constant * (mom[bi] * mom[bj]);
                    if (terms.ExtractsLinear)   v += terms.Linear   * t.Radius;
                    Complex r = PairRemainder(mesh, ma, na, dir, rem, st)
                              + PairRemainder(mesh, ma, nb, dir, rem, st)
                              + PairRemainder(mesh, mb, na, dir, rem, st)
                              + PairRemainder(mesh, mb, nb, dir, rem, st);
                    local += 4;
                    e = v + r;
                }
                z[bj, bi] += scale * e;                          // idx ascending ⇒ lower triangle
            }
            if (local != 0) counters?.AddRemainderPasses(local);
        });
    }

    /// <summary>
    /// <b>P5's REFERENCE — P4's per-frequency assembly</b>: one remainder pass per ordered cell pair
    /// in the band, scattered into transient per-basis-pair slots. Requires the
    /// <see cref="PlanarCoreLayout.Triangles"/> cores of <see cref="BuildCoresByPairs"/>. Not a
    /// production path; see <c>PlanarP5TranslationClassTests</c>.
    /// </summary>
    public static Mat<Complex> FillByPairs(PlanarFillCores cores, PlanarKernelTerms termsA,
                                           PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        RequirePairCores(cores);
        RequireLayout(cores, PlanarCoreLayout.Triangles, nameof(FillByPairs));
        var mesh = cores.Mesh;
        int n = mesh.Bases.Count;
        GuardCeiling(n, mesh.Cells.Count);

        var z = new Mat<Complex>(n, n);
        var st = cores.Settings;

        // ── the scalar block, assembled from P by signed differences (D4) ─────────────────────
        // In its own method so the transient m×m P is unreachable before the vector block's own
        // transient triangles are allocated (P1's accounting: neither is the point's peak).
        ScalarBlock(z, cores, termsQ, omega);

        // ── the vector block, same direction only (D5) ────────────────────────────────────────
        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
        var termsAr = termsA.With(st.Order, cores.RhoFloorM);
        var remA = Remainder(termsAr, cores);
        VectorBlock(z, cores, termsAr, remA, vectorScale);

        // ── R-fil-2: mirror, bit-identically ─────────────────────────────────────────────────
        // Column-wise, so each column is written by exactly one iteration — R-fil-11's shape, and
        // the assignment is a copy rather than a recomputation, so the two triangles cannot differ
        // in their last bit the way a "compute both and trust reciprocity" fill would. P3 moved the
        // computed triangle from upper to LOWER, because Mat<T> is column-major and z[j, i] with j
        // innermost is the contiguous write; the copy runs the same way.
        MirrorLowerToUpper(st, z);

        return z;
    }

    /// <summary>
    /// <b>P4's REFERENCE — the pre-P4 per-frequency assembly</b>: the same scalar block, and every
    /// same-direction basis pair's remainder as four (half, half) quadratures summed in L8c's order.
    /// Pair it with <see cref="BuildCoresByHalves"/> for the whole pre-P4 arithmetic. Not a
    /// production path; see <c>PlanarP4MomentCacheTests</c>.
    /// </summary>
    public static Mat<Complex> FillByHalves(PlanarFillCores cores, PlanarKernelTerms termsA,
                                            PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        RequirePairCores(cores);
        RequireLayout(cores, PlanarCoreLayout.Triangles, nameof(FillByHalves));
        var mesh = cores.Mesh;
        int n = mesh.Bases.Count;
        var st = cores.Settings;
        var topo = cores.Topology;
        var z = new Mat<Complex>(n, n);
        ScalarBlock(z, cores, termsQ, omega);

        Complex scale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
        var terms = termsA.With(st.Order, cores.RhoFloorM);
        var rem = Remainder(terms, cores);
        var mom = cores.VMoment;

        foreach (var (d, dir, c0, cLog, cRad) in new[]
                 {
                     (topo.X, PlanarBasisDirection.X, cores.VX0!, cores.VXLog!, cores.VXRad),
                     (topo.Y, PlanarBasisDirection.Y, cores.VY0!, cores.VYLog!, cores.VYRad),
                 })
        {
            var idx = d.Idx;
            int k = d.Count;
            ForRows(st, k, i =>
            {
                var (ma, mb) = topo.Halves[idx[i]];
                for (int j = i; j < k; j++)
                {
                    var (na, nb) = topo.Halves[idx[j]];
                    long q = Packed(i, j, k);
                    Complex v = terms.Inverse * c0[q] + terms.Log * cLog[q];
                    if (terms.ExtractsConstant) v += terms.Constant * (mom[idx[i]] * mom[idx[j]]);
                    if (terms.ExtractsLinear && cRad is not null) v += terms.Linear * cRad[q];
                    Complex r = PairRemainder(mesh, ma, na, dir, rem, st)
                              + PairRemainder(mesh, ma, nb, dir, rem, st)
                              + PairRemainder(mesh, mb, na, dir, rem, st)
                              + PairRemainder(mesh, mb, nb, dir, rem, st);
                    z[idx[j], idx[i]] += scale * (v + r);
                }
            });
        }
        MirrorLowerToUpper(st, z);
        return z;
    }

    private static void ScalarBlock(Mat<Complex> z, PlanarFillCores cores, PlanarKernelTerms termsQ,
                                    double omega)
    {
        var mesh = cores.Mesh;
        int n = mesh.Bases.Count;
        var p = ScalarPotentialMatrix(cores, termsQ);

        Complex scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        var halves = new (RooftopHalf A, RooftopHalf B)[n];
        for (int i = 0; i < n; i++) halves[i] = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);

        ForRows(cores.Settings, n, i =>
        {
            var (ma, mb) = halves[i];
            for (int j = i; j < n; j++)
            {
                var (na, nb) = halves[j];
                Complex s = ma.Sign * na.Sign * p[ma.CellIndex, na.CellIndex]
                          + ma.Sign * nb.Sign * p[ma.CellIndex, nb.CellIndex]
                          + mb.Sign * na.Sign * p[mb.CellIndex, na.CellIndex]
                          + mb.Sign * nb.Sign * p[mb.CellIndex, nb.CellIndex];
                z[j, i] = scalarScale * s;                      // the contiguous triangle (P3)
            }
        });
    }

    /// <summary>
    /// P4 — the per-frequency vector block: ONE remainder pass per ordered whole cell pair, serving
    /// both flow directions, scattered into per-basis-pair slots (A half to one triangle, B half
    /// to another — one writer each), then a row pass per direction that assembles
    /// <c>scale·(v + (r_A + r_B))</c>. Pairs with a cut basis take the four-call path in the row pass.
    /// </summary>
    private static void VectorBlock(Mat<Complex> z, PlanarFillCores cores, PlanarKernelTerms terms,
                                    Func<double, Complex> rem, Complex scale)
    {
        var mesh = cores.Mesh;
        var st   = cores.Settings;
        var topo = cores.Topology;
        var tx   = topo.X;
        var ty   = topo.Y;
        int m    = mesh.Cells.Count;

        long xCount = (long)tx.Count * (tx.Count + 1) / 2;
        long yCount = (long)ty.Count * (ty.Count + 1) / 2;
        var rxA = new Complex[xCount]; var rxB = new Complex[xCount];
        var ryA = new Complex[yCount]; var ryB = new Complex[yCount];

        var counters = st.Counters;
        ForRows(st, m, a =>
        {
            if (!topo.HasWholeRamp[a]) return;
            long local = 0;
            var  ca    = mesh.Cells[a];
            for (int c = topo.MinInner[a]; c < m; c++)
            {
                if (!topo.HasWholeRamp[c]) continue;
                var cc = mesh.Cells[c];
                var r  = CellPairRemainder(mesh, a, c, rem, st);
                local++;
                Scatter(tx, topo.Cut, a, c, ca.Width,  cc.Width,  r.Pulse, r.X10, r.X01, r.X11, rxA, rxB);
                Scatter(ty, topo.Cut, a, c, ca.Height, cc.Height, r.Pulse, r.Y10, r.Y01, r.Y11, ryA, ryB);
            }
            counters?.AddRemainderPasses(local);
        });

        AddDirectionBlock(z, cores, tx, PlanarBasisDirection.X, cores.VX0!, cores.VXLog!, cores.VXRad,
                          rxA, rxB, terms, rem, scale);
        AddDirectionBlock(z, cores, ty, PlanarBasisDirection.Y, cores.VY0!, cores.VYLog!, cores.VYRad,
                          ryA, ryB, terms, rem, scale);
    }

    private static void AddDirectionBlock(Mat<Complex> z, PlanarFillCores cores, RampTopology.DirectionMap d,
                                          PlanarBasisDirection dir,
                                          double[] c0, double[] cLog, double[]? cRad,
                                          Complex[] rA, Complex[] rB,
                                          PlanarKernelTerms terms, Func<double, Complex> rem,
                                          Complex scale)
    {
        var mesh = cores.Mesh;
        var st   = cores.Settings;
        var topo = cores.Topology;
        var idx  = d.Idx;
        int k    = d.Count;
        var mom  = cores.VMoment;
        var counters = st.Counters;

        ForRows(st, k, i =>
        {
            long local = 0;
            var (ma, mb) = topo.Halves[idx[i]];
            for (int j = i; j < k; j++)
            {
                long q = Packed(i, j, k);

                Complex v = terms.Inverse * c0[q] + terms.Log * cLog[q];
                // P2/M1: the same product the packed VXArea/VYArea triangle used to hold, formed from
                // the same two operands at the point of use — one multiply per entry instead of an
                // O(N²) array of an outer product.
                if (terms.ExtractsConstant) v += terms.Constant * (mom[idx[i]] * mom[idx[j]]);
                if (terms.ExtractsLinear && cRad is not null) v += terms.Linear * cRad[q];

                Complex r;
                if (topo.Cut[idx[i]] || topo.Cut[idx[j]])
                {
                    var (na, nb) = topo.Halves[idx[j]];
                    r = PairRemainder(mesh, ma, na, dir, rem, st)
                      + PairRemainder(mesh, ma, nb, dir, rem, st)
                      + PairRemainder(mesh, mb, na, dir, rem, st)
                      + PairRemainder(mesh, mb, nb, dir, rem, st);
                    local += 4;
                }
                else
                    r = rA[q] + rB[q];

                z[idx[j], idx[i]] += scale * (v + r);           // idx ascending ⇒ lower triangle
            }
            if (local != 0) counters?.AddRemainderPasses(local);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // L9c / M5 — THE MULTI-LEVEL FILL
    //
    // Three blocks are new and only one of them needed new machinery, which is the payoff of D2's
    // basis choice and L8b's shared grid:
    //
    //   SCALAR, generalised.  ∇·f is the same ±1/Area pulse whether the basis is horizontal or
    //     vertical, so L8c's D4 is unchanged — the scalar block is still a signed sum of per-CELL
    //     entries. What changes is that a cell pair can now straddle two levels, so the kernel is
    //     picked per (level, level). **The GEOMETRIC cores are reused verbatim**: they are in-plane
    //     integrals of 1/r, ln r and 1, and the height pair enters only through the coefficients.
    //     A cross-level pair has NO 1/ρ (its direct term sits at Δ = |z−z′| > 0 and is bounded at
    //     ρ = 0) but it still has the surface wave's ln ρ — see FromDcimAtHeights.
    //
    //   ẑẑ.  A via's in-plane weight is a PULSE over its footprint — the same pulse the scalar block
    //     integrates — so the vertical vector block is the scalar block's own cell-pair integral with
    //     G_A^zz in place of G_q and a factor ℓ_mℓ_n from the two z-integrals. **No new core, no new
    //     quadrature, no new closed form.**
    //
    //   ẑx̂ / ẑŷ.  This one is genuinely new. The dyadic entry is j ∂G/∂x, not a value, so it is the
    //     only block that integrates a DERIVATIVE and the only one whose integrand is ODD in x − x′.
    //     Its transpose is equal rather than opposite (G_A^uz = −G_A^zu with the heights swapped, and
    //     the odd factor supplies the second sign), so R-fil-2's "compute m ≤ n and mirror" still
    //     produces a symmetric Z. It is done by direct graded quadrature rather than by extraction —
    //     stated as a limit, with its convergence measured, in PlanarFillTests.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>P3 — every per-PAIRING object the multi-level fill reads, resolved ONCE before any row
    /// loop runs.</b>
    ///
    /// <para>Before P3, <see cref="PlanarFill.FillMultiLevel"/> asked <see cref="PlanarKernelSet.Get"/>
    /// for its terms per CELL pair and per BASIS pair — a lock on the set's dictionary in the O(N²)
    /// inner loop, plus a fresh <see cref="PlanarKernelTerms"/> from <c>.With</c> per pair — and took
    /// three more locks per entry for the remainder, ẑẑ and mixed caches. The single-level
    /// <see cref="PlanarFill.Fill"/> hoists all of that; this is the same hoist for a mesh whose
    /// pairings are indexed by LAYER rather than being one pair for the whole problem.</para>
    ///
    /// <para><b>Exactly the pairings the loops visit are resolved, and no others</b> — a pairing that
    /// was never asked for is a fit that was never paid for, and <c>PlanarKernelSet.FitCount</c> is
    /// asserted by three tests. So the horizontal vector table is built per DIRECTION from the layers
    /// that carry that direction (a layer with only x̂ rooftops and one with only ŷ never pair), the
    /// ẑẑ table is keyed on the ORDERED span pair the <c>i ≤ j</c> loop produces, and the mixed table
    /// covers every (span, horizontal layer) the mesh contains. Resolution is serial and in index
    /// order, which is deterministic; the fits themselves are memoised pure functions of their key,
    /// so the order cannot move a bit — R-emp-8 on this path is <c>P3_3</c>.</para>
    /// </summary>
    internal sealed class MultiLevelPairings
    {
        /// <summary>The scalar kernel's terms and remainder per (layer, layer); null where the mesh
        /// has no such cell pairing.</summary>
        public readonly PlanarKernelTerms?[,]      TermsQ;
        public readonly Func<double, Complex>?[,]  RemQ;
        /// <summary>The horizontal vector kernel's, per (layer, layer) of same-direction rooftops.</summary>
        public readonly PlanarKernelTerms?[,]      TermsA;
        public readonly Func<double, Complex>?[,]  RemA;

        /// <summary>The distinct z spans vertical bases occupy, in first-seen basis order.</summary>
        public readonly ViaZIntegral.Span[] Spans;
        /// <summary>Basis index → index into <see cref="Spans"/>, or −1 for a horizontal basis.</summary>
        public readonly int[] SpanOfBasis;
        /// <summary>The ẑẑ block's z-averaged terms, remainder and the pair's asymptote, per ORDERED
        /// span pair as the <c>i ≤ j</c> loop meets them.</summary>
        public readonly (PlanarKernelTerms T, Func<double, Complex> R,
                         LayeredSpectralGreens.InteriorAsymptote Asym)?[,] Zz;
        /// <summary>The mixed block's z-averaged radial derivative per (span, horizontal layer).</summary>
        public readonly Func<double, Complex>?[,] Mixed;

        /// <summary>Each HORIZONTAL rooftop's two halves as the quadrature wants them, per basis —
        /// what <see cref="AddDirectionBlock"/> caches as <c>halves</c>. Default for a vertical basis.</summary>
        public readonly (CellWeight A, CellWeight B)[] Halves;
        /// <summary>The divergence pulse per CELL — <see cref="Pulse"/> resolved once rather than per
        /// pair, which on a conformal mesh is a tile build per call.</summary>
        public readonly CellWeight[] Pulses;

        private MultiLevelPairings(int layers, int spans, int n, int m)
        {
            TermsQ = new PlanarKernelTerms?[layers, layers];
            RemQ   = new Func<double, Complex>?[layers, layers];
            TermsA = new PlanarKernelTerms?[layers, layers];
            RemA   = new Func<double, Complex>?[layers, layers];
            Zz     = new (PlanarKernelTerms, Func<double, Complex>, LayeredSpectralGreens.InteriorAsymptote)?[spans, spans];
            Mixed  = new Func<double, Complex>?[spans, layers];
            Halves = new (CellWeight, CellWeight)[n];
            Pulses = new CellWeight[m];
            Spans  = new ViaZIntegral.Span[spans];
            SpanOfBasis = new int[n];
        }

        public static MultiLevelPairings Resolve(PlanarFillCores cores, PlanarKernelSet set,
                                                 PlanarLevels levels, PlanarFillSettings st)
        {
            var mesh = cores.Mesh;
            int n = mesh.Bases.Count, m = mesh.Cells.Count;

            // Sized from what the mesh NAMES rather than from the level list, so a layer index the
            // mesh never uses costs nothing and one the levels cannot answer fails where it always
            // did — in levels.Of, when that pairing is resolved.
            int layers = 0;
            foreach (var c in mesh.Cells) layers = Math.Max(layers, c.LayerIndex + 1);
            foreach (var b in mesh.Bases) layers = Math.Max(layers, b.LayerIndex + 2);

            // ── the spans, in first-seen order over the vertical bases ────────────────────────
            var spanList  = new List<ViaZIntegral.Span>();
            var spanIndex = new int[n];
            for (int i = 0; i < n; i++)
            {
                var b = mesh.Bases[i];
                if (b.Direction != PlanarBasisDirection.Z) { spanIndex[i] = -1; continue; }
                var s = SpanOf(levels, b);
                int at = spanList.IndexOf(s);
                if (at < 0) { at = spanList.Count; spanList.Add(s); }
                spanIndex[i] = at;
            }

            var r = new MultiLevelPairings(layers, spanList.Count, n, m);
            spanList.CopyTo(r.Spans);
            Array.Copy(spanIndex, r.SpanOfBasis, n);

            // ── the scalar block: every (layer, layer) that has a cell on both ─────────────────
            var cellLayers = new bool[layers];
            foreach (var c in mesh.Cells) cellLayers[c.LayerIndex] = true;
            for (int la = 0; la < layers; la++)
            {
                if (!cellLayers[la]) continue;
                for (int lb = la; lb < layers; lb++)
                {
                    if (!cellLayers[lb]) continue;
                    double za = levels.Of(la), zb = levels.Of(lb);
                    var t = set.Get(GreensKernel.ScalarPotential, za, zb).With(st.Order, cores.RhoFloorM);
                    var f = Remainder(set.Get(GreensKernel.ScalarPotential, za, zb), cores);
                    r.TermsQ[la, lb] = r.TermsQ[lb, la] = t;
                    r.RemQ[la, lb]   = r.RemQ[lb, la]   = f;
                }
            }

            // ── the horizontal vector block: per DIRECTION, the layers that carry it ───────────
            foreach (var dir in new[] { PlanarBasisDirection.X, PlanarBasisDirection.Y })
            {
                var dirLayers = new bool[layers];
                foreach (var b in mesh.Bases)
                    if (b.Direction == dir) dirLayers[b.LayerIndex] = true;
                for (int la = 0; la < layers; la++)
                {
                    if (!dirLayers[la]) continue;
                    for (int lb = la; lb < layers; lb++)
                    {
                        if (!dirLayers[lb] || r.TermsA[la, lb] is not null) continue;
                        double za = levels.Of(la), zb = levels.Of(lb);
                        var t = set.Get(GreensKernel.VectorPotential, za, zb).With(st.Order, cores.RhoFloorM);
                        var f = Remainder(set.Get(GreensKernel.VectorPotential, za, zb), cores);
                        r.TermsA[la, lb] = r.TermsA[lb, la] = t;
                        r.RemA[la, lb]   = r.RemA[lb, la]   = f;
                    }
                }
            }

            // ── the ẑẑ block: the ORDERED span pairs the i ≤ j loop meets ─────────────────────
            for (int i = 0; i < n; i++)
            {
                int si = spanIndex[i];
                if (si < 0) continue;
                for (int j = i; j < n; j++)
                {
                    int sj = spanIndex[j];
                    if (sj < 0 || r.Zz[si, sj] is not null) continue;
                    r.Zz[si, sj] = ZzTerms(cores, set, r.Spans[si], r.Spans[sj], st);
                }
            }

            // ── the mixed block: every (span, horizontal layer) ───────────────────────────────
            var horizontalLayers = new bool[layers];
            foreach (var b in mesh.Bases)
                if (b.Direction != PlanarBasisDirection.Z) horizontalLayers[b.LayerIndex] = true;
            for (int i = 0; i < n; i++)
            {
                int sv = spanIndex[i];
                if (sv < 0) continue;
                for (int l = 0; l < layers; l++)
                {
                    if (!horizontalLayers[l] || r.Mixed[sv, l] is not null) continue;
                    r.Mixed[sv, l] = MixedDerivative(cores, set, r.Spans[sv], levels.Of(l), st);
                }
            }

            // ── per-basis and per-cell geometry ───────────────────────────────────────────────
            for (int i = 0; i < n; i++)
                if (spanIndex[i] < 0) r.Halves[i] = RampHalves(mesh, mesh.Bases[i]);
            for (int a = 0; a < m; a++) r.Pulses[a] = Pulse(mesh, a);

            return r;
        }

        /// <summary>The via z-integral's per-SPAN-PAIR objects (ViaZIntegral), keyed on the z spans
        /// alone, never on the cell pair — so a mesh whose vias all join the same two levels, which
        /// is every via of one drawn layer, builds each exactly once however many vertical unknowns
        /// it carries.</summary>
        private static (PlanarKernelTerms, Func<double, Complex>, LayeredSpectralGreens.InteriorAsymptote)
            ZzTerms(PlanarFillCores cores, PlanarKernelSet set,
                    ViaZIntegral.Span si, ViaZIntegral.Span sj, PlanarFillSettings st)
        {
            // M2 (R-zz-3) — the ẑẑ block ALONE may take its kernel from direct Sommerfeld integration
            // rather than from the DCIM fit. Reachable as a setting, exactly like UseRadialTable =
            // false, because M1 measured the fit as the failure and measured every DcimSettings knob
            // as unable to fix it. Nothing else in the fill changes: the singular half is closed form
            // in z and was never fitted, and the horizontal and scalar blocks are untouched.
            PlanarKernelTerms t;
            Func<double, Complex> r;
            if (st.DirectVerticalKernel)
            {
                double rhoMax = Math.Max(cores.ExtentM, cores.MinCellEdgeM * 8);
                t = ViaZIntegral.AveragedTermsDirect(
                        set, GreensKernel.VerticalVectorPotential, si, sj, st.ViaZNodes,
                        st.Order, cores.RhoFloorM, rhoMax, st.VerticalTableSamples);
                // Already a table; re-tabulating would interpolate an interpolation.
                r = t.Remainder;
            }
            else
            {
                t = ViaZIntegral.AveragedTerms(set, GreensKernel.VerticalVectorPotential,
                                              si, sj, st.ViaZNodes, st.Order, cores.RhoFloorM);
                r = Remainder(t, cores);
            }
            // The pair's k_ρ → ∞ asymptote, whose two coefficients do not depend on the heights at
            // all — asked once per span pair rather than once per ẑẑ entry.
            var asym = set.Asymptote(GreensKernel.VerticalVectorPotential, si.Mid, sj.Mid);
            return (t, r, asym);
        }

        private static Func<double, Complex> MixedDerivative(PlanarFillCores cores, PlanarKernelSet set,
                                                             ViaZIntegral.Span sv, double zh,
                                                             PlanarFillSettings st)
        {
            var raw = ViaZIntegral.AveragedMixedDerivative(set, sv, zh, st.ViaZNodes);
            if (!st.UseRadialTable) return raw;
            double spacing = st.TableCellFraction * cores.MinCellEdgeM;
            return RadialRemainderTable.BuildFrom(
                raw, Math.Max(cores.ExtentM, spacing * 8), spacing, st.MaxTableSamples).Evaluate;
        }
    }

    /// <summary>
    /// The Galerkin matrix of a MULTI-LEVEL problem, with vertical (via) bases. Reduces to
    /// <see cref="Fill(PlanarFillCores, PlanarKernelTerms, PlanarKernelTerms, double)"/>'s answer on a
    /// one-level mesh with no vias, which is what <c>PlanarFillTests</c> gates it against.
    ///
    /// <para><b>P3 — the row loops read tables and nothing else.</b> Every kernel-terms object,
    /// remainder evaluator, z-averaged via term, mixed derivative, rooftop half and divergence pulse
    /// is resolved once, serially, in <see cref="MultiLevelPairings.Resolve"/>; inside
    /// <see cref="ForRows"/> there is no lock, no dictionary and no allocation, exactly as in
    /// <see cref="Fill(PlanarFillCores, PlanarKernelTerms, PlanarKernelTerms, double)"/>. The
    /// arithmetic per entry is unchanged and the result is bit-identical
    /// (<c>PlanarP3MultiLevelFillTests</c>); what changed is when each object is looked up.</para>
    ///
    /// <para>The lower triangle is written (column-major storage makes <c>z[j, i]</c> with <c>j</c>
    /// innermost the contiguous one) and mirrored to the upper afterwards — R-fil-2's "computed once,
    /// copied" is intact, the triangle merely swapped.</para>
    /// </summary>
    public static Mat<Complex> FillMultiLevel(PlanarFillCores cores, PlanarKernelSet set,
                                              PlanarLevels levels, double omega,
                                              PlanarFillDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(cores);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(levels);
        RequirePairCores(cores);

        var mesh = cores.Mesh;
        int n = mesh.Bases.Count;
        GuardCeiling(n, mesh.Cells.Count);
        var st = cores.Settings;
        int m = mesh.Cells.Count;

        var pr = MultiLevelPairings.Resolve(cores, set, levels, st);
        var cellLayer = new int[m];
        for (int a = 0; a < m; a++) cellLayer[a] = mesh.Cells[a].LayerIndex;

        // ── the scalar half: P over CELLS, kernel chosen per (level, level) ───────────────────
        var p = new Mat<Complex>(m, m);
        ForRows(st, m, a =>
        {
            var wa = pr.Pulses[a];
            int la = cellLayer[a];
            for (int b = a; b < m; b++)
            {
                int lb = cellLayer[b];
                var terms = pr.TermsQ[la, lb]!;
                var core = cores.ScalarCoreOf(a, b);

                Complex v = terms.Inverse * core.Inverse + terms.Log * core.Log;
                if (terms.ExtractsConstant) v += terms.Constant;
                if (terms.ExtractsLinear) v += terms.Linear * core.Radius;
                v += PairRemainder(mesh, wa, pr.Pulses[b], PlanarBasisDirection.X, pr.RemQ[la, lb]!, st);
                p[b, a] = v;
            }
        });
        MirrorLowerToUpper(st, p);

        var z = new Mat<Complex>(n, n);
        Complex scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        var halves = new (RooftopHalf A, RooftopHalf B)[n];
        for (int i = 0; i < n; i++) halves[i] = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);

        ForRows(st, n, i =>
        {
            var (ma, mb) = halves[i];
            for (int j = i; j < n; j++)
            {
                var (na, nb) = halves[j];
                Complex s = ma.Sign * na.Sign * p[ma.CellIndex, na.CellIndex]
                          + ma.Sign * nb.Sign * p[ma.CellIndex, nb.CellIndex]
                          + mb.Sign * na.Sign * p[mb.CellIndex, na.CellIndex]
                          + mb.Sign * nb.Sign * p[mb.CellIndex, nb.CellIndex];
                z[j, i] = scalarScale * s;
            }
        });

        // ── the vector half ───────────────────────────────────────────────────────────────────
        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;

        ForRows(st, n, i =>
        {
            var bi = mesh.Bases[i];
            bool zi = bi.Direction == PlanarBasisDirection.Z;
            int  li = bi.LayerIndex;
            int  si = pr.SpanOfBasis[i];
            for (int j = i; j < n; j++)
            {
                var bj = mesh.Bases[j];
                bool zj = bj.Direction == PlanarBasisDirection.Z;

                if (!zi && !zj)
                {
                    // L8c's D5 is untouched: an X-rooftop and a Y-rooftop are pointwise orthogonal
                    // and the formulation-C vector kernel has no xy component.
                    if (bi.Direction != bj.Direction) continue;
                    int lj = bj.LayerIndex;
                    z[j, i] += vectorScale * HorizontalVectorEntry(
                        mesh, cores, i, j, bi.Direction, pr.Halves[i], pr.Halves[j],
                        pr.TermsA[li, lj]!, pr.RemA[li, lj]!, st);
                }
                else if (zi && zj)
                {
                    // ── the ẑẑ block, with the z-integral RESOLVED rather than replaced ──────
                    //
                    // BOUNDED half: the z-averaged terms, which cost n_z² fits and ZERO extra
                    // cell-pair quadratures — the entry is linear in the kernel, so averaging the
                    // TERMS is the same thing as averaging the entries and the fill's own O(N²) work
                    // is untouched.
                    // R-zz-1's Tier 1 instrument: record the largest LATERAL separation this arm —
                    // the only consumer of G_A^zz anywhere — actually asks about, so the refusal's
                    // own scoping is asserted against what the fill does rather than against a
                    // reading of the code.
                    diagnostics?.ObserveVerticalPair(
                        CellPairSpan(mesh.Cells[bi.CellA], mesh.Cells[bj.CellA]));

                    int sj = pr.SpanOfBasis[j];
                    var spanI = pr.Spans[si];
                    var spanJ = pr.Spans[sj];
                    var (t, rem, asym) = pr.Zz[si, sj]!.Value;
                    Complex core = CellPairPotential(mesh, cores, pr.Pulses, bi.CellA, bj.CellA, t, rem, st);

                    // SINGULAR half: the two extracted asymptotes, whose coefficients do not depend on
                    // the heights and whose depths are exactly Δ and Σ_b, integrated over the two
                    // prisms in CLOSED FORM in z. This is the piece a Gauss rule cannot carry — see
                    // ViaZIntegral's header — and it is where the 0.673·(ℓ/w) went.
                    core += SingularPrismPart(mesh, asym, bi.CellA, bj.CellA, spanI, spanJ, st);

                    z[j, i] += vectorScale * spanI.Length * spanJ.Length * core;
                }
                else
                {
                    var vertical   = zi ? bi : bj;
                    var horizontal = zi ? bj : bi;
                    int sv         = zi ? si : pr.SpanOfBasis[j];
                    int hIndex     = zi ? j  : i;
                    // R-viz-5: ONE z-integral, and it is folded into the radial derivative the block
                    // already consumes — so MixedEntry is called exactly as often as it was.
                    z[j, i] += vectorScale * pr.Spans[sv].Length
                             * MixedEntry(mesh, vertical, horizontal, pr.Halves[hIndex],
                                          pr.Mixed[sv, horizontal.LayerIndex]!, cores.RhoFloorM, st);
                }
            }
        });

        MirrorLowerToUpper(st, z);
        return z;
    }

    /// <summary>The largest in-plane distance between any point of one cell and any point of the
    /// other — the widest ρ a cell-pair integral over the two can reach.</summary>
    private static double CellPairSpan(PlanarCell a, PlanarCell b)
    {
        double dx = Math.Max(a.XMax, b.XMax) - Math.Min(a.XMin, b.XMin);
        double dy = Math.Max(a.YMax, b.YMax) - Math.Min(a.YMin, b.YMin);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The z extent a vertical basis occupies. An ordinary via spans its two feet's levels; a
    /// GROUND ATTACHMENT spans the plane up to its one meshed level, and for that basis alone
    /// <c>LayerIndex</c> names the meshed level rather than the lower of two.
    /// </summary>
    private static ViaZIntegral.Span SpanOf(PlanarLevels levels, PlanarBasis b) =>
        b.AttachesToGround
            ? new(levels.GroundZ, levels.Of(b.LayerIndex))
            : new(levels.Of(b.LayerIndex), levels.Of(b.LayerIndex + 1));

    /// <summary>
    /// <b>The ẑẑ entry's SINGULAR half: the two extracted asymptotes, z-integrated in closed form.</b>
    ///
    /// <para>Their coefficients are the k_ρ → ∞ limits of the cascade — the source region's own
    /// Fresnel coefficients — so they come off one probe rather than one per z node, and cost no fit
    /// at all. A CROSS-REGION pair has neither (every term crosses a full region and decays), and the
    /// method returns zero for it: nothing was extracted, so nothing has to be put back.</para>
    ///
    /// <para>P3: the probe is the span pair's, resolved once in <see cref="MultiLevelPairings"/>
    /// rather than asked of the kernel set per entry.</para>
    /// </summary>
    private static Complex SingularPrismPart(PlanarMesh mesh, LayeredSpectralGreens.InteriorAsymptote asym,
                                             int cellA, int cellB,
                                             ViaZIntegral.Span si, ViaZIntegral.Span sj,
                                             PlanarFillSettings st)
    {
        if (asym.IsMixedForm) return Complex.Zero;

        Complex v = Complex.Zero;
        const double FourPi = 4.0 * Math.PI;

        if (asym.DirectCoefficient != Complex.Zero)
            v += asym.DirectCoefficient / FourPi
               * ViaZIntegral.PrismCore(mesh, cellA, cellB, si, sj,
                                        sumFamily: false, floorZ: 0.0, st, st.ViaZStaticNodes);

        if (asym.ImageCoefficient != Complex.Zero)
        {
            // Σ_b = z + z′ − 2z_b, so the region floor is recoverable from the probe rather than
            // needing its own plumbing down from the stack.
            double floorZ = 0.5 * (si.Mid + sj.Mid - asym.ImageDepth);
            v += asym.ImageCoefficient / FourPi
               * ViaZIntegral.PrismCore(mesh, cellA, cellB, si, sj,
                                        sumFamily: true, floorZ, st, st.ViaZStaticNodes);
        }

        return v;
    }

    /// <summary>One same-direction horizontal pair's vector entry — L8c's own expression, lifted out
    /// of <see cref="AddDirectionBlock"/> so the multi-level assembly can call it per pair with its
    /// own height pairing's terms. P3: the halves arrive cached per basis and the four remainders
    /// are added in the same order they always were, without the array that used to carry them.</summary>
    private static Complex HorizontalVectorEntry(PlanarMesh mesh, PlanarFillCores cores,
                                                 int basisI, int basisJ, PlanarBasisDirection dir,
                                                 (CellWeight A, CellWeight B) hi,
                                                 (CellWeight A, CellWeight B) hj,
                                                 PlanarKernelTerms terms, Func<double, Complex> rem,
                                                 PlanarFillSettings st)
    {
        var (ma, mb) = hi;
        var (na, nb) = hj;

        // ── L9d: D6's cached geometric cores, which are the SAME numbers this used to re-integrate.
        //
        // The height pairing enters only through the coefficients, so a same-direction pair's four
        // panel quadratures are exactly BuildDirectionCores' own already-summed entry. Reusing it
        // also puts this expression on L8c's own associativity (one coefficient times the summed
        // core, rather than the sum of four coefficient-times-core products), which is why the
        // one-level reduction against PlanarFill.Fill gets tighter rather than looser.
        // P5: the same summed core, read through the layout — a packed triangle under the
        // reference builders, an assembly from four translation classes in production.
        var vc = cores.VectorCoreOf(dir, cores.DirPos[basisI], cores.DirPos[basisJ]);

        Complex v = terms.Inverse * vc.Inverse + terms.Log * vc.Log;
        if (terms.ExtractsConstant)
            v += terms.Constant * (cores.VMoment[basisI] * cores.VMoment[basisJ]);
        if (terms.ExtractsLinear) v += terms.Linear * vc.Radius;

        // Sequential, in this order — the multi-level fill's own association, which differs from
        // AddDirectionBlock's (r = Σ four, then v + r) and is what the pinned digests were taken on.
        v += PairRemainder(mesh, ma, na, dir, rem, st);
        v += PairRemainder(mesh, ma, nb, dir, rem, st);
        v += PairRemainder(mesh, mb, na, dir, rem, st);
        v += PairRemainder(mesh, mb, nb, dir, rem, st);

        return v;
    }

    /// <summary>The area-averaged potential coefficient between two CELLS at one kernel — the same
    /// object <c>P</c> is built from, exposed here because the ẑẑ block is exactly it.</summary>
    private static Complex CellPairPotential(PlanarMesh mesh, PlanarFillCores cores, CellWeight[] pulses,
                                             int cellA, int cellB,
                                             PlanarKernelTerms terms, Func<double, Complex> rem,
                                             PlanarFillSettings st)
    {
        int a = Math.Min(cellA, cellB), b = Math.Max(cellA, cellB);
        var core = cores.ScalarCoreOf(a, b);
        Complex v = terms.Inverse * core.Inverse + terms.Log * core.Log;
        if (terms.ExtractsConstant) v += terms.Constant;
        if (terms.ExtractsLinear) v += terms.Linear * core.Radius;
        v += PairRemainder(mesh, pulses[a], pulses[b], PlanarBasisDirection.X, rem, st);
        return v;
    }

    /// <summary>
    /// <b>The ẑû mixed entry, and it is the one block that integrates a DERIVATIVE.</b>
    ///
    /// <para><c>∫∫ (1/A_v) · j G′(ρ)(u − u′)/ρ · w_h(r′) dS′ dS</c>, with u the VIA's coordinate.
    /// Direct graded quadrature on both integrals rather than an extraction: the singular part is
    /// <c>G′ ~ −C/ρ</c> (the mixed kernel's own asymptote is a logarithm, not a 1/ρ), so the integrand
    /// behaves as <c>(u−u′)/ρ²</c> — integrable in two dimensions, unlike the 1/ρ³ a value-kernel
    /// dipole would give. <b>That is why this block does not need its own closed form and the
    /// horizontal blocks do.</b> Its accuracy is a quadrature question and is measured rather than
    /// asserted; the ρ floor is the fill's own.</para>
    ///
    /// <para>P3: on a whole-rectangle half the horizontal cell's nodes are enumerated inline — the
    /// same nodes, the same weights, the same nesting order as <see cref="OuterNodes"/> — rather than
    /// through an iterator allocated per VIA node. This is the one arm whose cost is not O(N²) but
    /// which dominated a via-bearing fill anyway, because a via cell is large enough that most
    /// horizontal cells count as near or intermediate to it and take the full graded rule. A cut half
    /// still goes through the shared enumerator.</para>
    /// </summary>
    private static Complex MixedEntry(PlanarMesh mesh, PlanarBasis vertical, PlanarBasis horizontal,
                                      (CellWeight A, CellWeight B) horizontalHalves,
                                      Func<double, Complex> dG,
                                      double rhoFloor, PlanarFillSettings st)
    {
        var v = mesh.Cells[vertical.CellA];
        bool alongX = horizontal.Direction == PlanarBasisDirection.X;
        double floor = Math.Max(rhoFloor, 1e-30);

        // BOTH halves add with the SAME sign, and that is worth stating because the divergence's do
        // not: a rooftop's current flows one way through both of its cells and the ± distinction
        // belongs to ∇·f. Getting it wrong here cancels the block instead of assembling it.
        return MixedHalf(mesh, v, horizontalHalves.A, horizontal.Direction, alongX, dG, floor, st)
             + MixedHalf(mesh, v, horizontalHalves.B, horizontal.Direction, alongX, dG, floor, st);
    }

    private static Complex MixedHalf(PlanarMesh mesh, PlanarCell v, CellWeight half,
                                     PlanarBasisDirection dir, bool alongX,
                                     Func<double, Complex> dG, double floor, PlanarFillSettings st)
    {
        var c = mesh.Cells[half.CellIndex];
        double tau = SeparationRatio(v, c);
        int nodes = tau < st.NearRatio ? st.NearNodes
                  : tau < st.FarRatio  ? st.MidNodes : st.FarNodes;
        int panels = tau < st.NearRatio ? st.TouchPanels : 1;
        var (gx, gw) = Legendre.Nodes(nodes);
        var t = PanelEdges(panels);

        double invAv = 1.0 / v.Area;
        Complex sum = Complex.Zero;

        // The VIA cell's footprint is Manhattan by construction (L9c), so its own quadrature stays
        // the rectangle's; only the HORIZONTAL half can be cut, and it takes the shared node
        // enumerator — which is what keeps a conformal mesh with a via from silently integrating
        // the rooftop over metal that is not there.
        bool whole = half.Strips is null;
        double invAc = 1.0 / c.Area;

        for (int px = 0; px < panels; px++)
        for (int py = 0; py < panels; py++)
        {
            double xa = v.XMin + t[px] * v.Width,  xb = v.XMin + t[px + 1] * v.Width;
            double ya = v.YMin + t[py] * v.Height, yb = v.YMin + t[py + 1] * v.Height;
            double cx = 0.5 * (xa + xb), hx = 0.5 * (xb - xa);
            double cy = 0.5 * (ya + yb), hy = 0.5 * (yb - ya);

            for (int i = 0; i < nodes; i++)
            for (int j = 0; j < nodes; j++)
            {
                double x = cx + hx * gx[i], y = cy + hy * gx[j];
                double wq = gw[i] * gw[j] * hx * hy * invAv;

                if (whole)
                {
                    // OuterNodes' whole-rectangle branch, inline: panel (qx, qy), node (i′, j′).
                    for (int qx = 0; qx < panels; qx++)
                    for (int qy = 0; qy < panels; qy++)
                    {
                        double xpa = c.XMin + t[qx] * c.Width,  xpb = c.XMin + t[qx + 1] * c.Width;
                        double ypa = c.YMin + t[qy] * c.Height, ypb = c.YMin + t[qy + 1] * c.Height;
                        double cpx = 0.5 * (xpa + xpb), hpx = 0.5 * (xpb - xpa);
                        double cpy = 0.5 * (ypa + ypb), hpy = 0.5 * (ypb - ypa);

                        for (int ip = 0; ip < nodes; ip++)
                        for (int jp = 0; jp < nodes; jp++)
                        {
                            double xp = cpx + hpx * gx[ip], yp = cpy + hpy * gx[jp];
                            double weight = half.Ramp
                                ? Math.Abs((alongX ? xp : yp) - half.Edge) * invAc
                                : invAc;
                            if (weight == 0) continue;
                            double wc = gw[ip] * gw[jp] * hpx * hpy * weight;

                            double rho = Math.Sqrt((x - xp) * (x - xp) + (y - yp) * (y - yp));
                            if (rho <= floor) continue;         // the integrand is ODD; the limit is 0
                            double du = (alongX ? x - xp : y - yp) / rho;
                            sum += wc * wq * Complex.ImaginaryOne * dG(rho) * du;
                        }
                    }
                }
                else
                {
                    foreach (var (xp, yp, wc) in OuterNodes(c, half, dir, panels, nodes))
                    {
                        double rho = Math.Sqrt((x - xp) * (x - xp) + (y - yp) * (y - yp));
                        if (rho <= floor) continue;         // the integrand is ODD; the limit is 0
                        double du = (alongX ? x - xp : y - yp) / rho;
                        sum += wc * wq * Complex.ImaginaryOne * dG(rho) * du;
                    }
                }
            }
        }
        return sum;
    }

    /// <summary>
    /// <b>P3 — R-fil-2's mirror, in the cache-friendly direction.</b> <see cref="Mat{T}"/> is
    /// column-major, so the fills write the LOWER triangle — <c>z[j, i]</c> with <c>j</c> innermost
    /// is contiguous — and this copies it to the upper one column at a time, each column written by
    /// exactly one row-loop iteration (R-fil-11's shape). The assignment is a copy rather than a
    /// recomputation, so the two triangles cannot differ in their last bit.
    /// </summary>
    private static void MirrorLowerToUpper(PlanarFillSettings st, Mat<Complex> z)
    {
        int n = z.RowCount;
        ForRows(st, n, j =>
        {
            for (int i = 0; i < j; i++) z[i, j] = z[j, i];
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The cell-pair integrals
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One cell of one basis's support, as the quadrature needs it: the cell, the sign that
    /// makes <c>ξ = Sigma·(coord − Edge)</c> non-negative, and whether the weight is the rooftop's
    /// linear ramp or the divergence pulse.
    ///
    /// <para><b><see cref="Strips"/> is the conformal generalisation and NULL is the whole
    /// rectangle.</b> On a cut cell neither the domain nor the weight is expressible as a rectangle
    /// plus one edge coordinate — see <see cref="RooftopSupport"/> — so the strips carry both, and
    /// <c>Sigma</c>/<c>Edge</c>/<c>Ramp</c> are then unused. Keeping the rectangle fields rather than
    /// replacing them is what lets the fill run L8c's own expressions, unchanged, whenever both cells
    /// of a pair are whole (R-cut-2).</para></summary>
    internal readonly record struct CellWeight(int CellIndex, double Sigma, double Edge, bool Ramp,
                                               IReadOnlyList<WeightStrip>? Strips = null);

    private static CellWeight Pulse(PlanarMesh mesh, int cellIndex) =>
        new(cellIndex, 1.0, 0.0, false, RooftopSupport.Tiles(mesh.Cells[cellIndex]));

    /// <summary>
    /// A rooftop's two halves as the quadrature wants them. <b>A pair of whole rectangles comes back
    /// with no strips at all</b>, which is what puts it on L8c's own code path; only a half that is
    /// actually cut carries the piecewise ramp, so a MIXED pair pays for one side and not both.
    /// </summary>
    private static (CellWeight A, CellWeight B) RampHalves(PlanarMesh mesh, PlanarBasis basis)
    {
        var (ha, hb) = PlanarBasisFunctions.Halves(mesh, basis);
        var ca = mesh.Cells[ha.CellIndex];
        var cb = mesh.Cells[hb.CellIndex];

        if (!ca.IsCut && !cb.IsCut)
            return (new CellWeight(ha.CellIndex, +1.0, ha.OuterEdge, true),
                    new CellWeight(hb.CellIndex, -1.0, hb.OuterEdge, true));

        var (sa, sb) = PlanarBasisFunctions.Supports(mesh, basis);
        return (new CellWeight(ha.CellIndex, +1.0, ha.OuterEdge, true,
                               sa.IsWholeRectangle ? null : sa.Strips),
                new CellWeight(hb.CellIndex, -1.0, hb.OuterEdge, true,
                               sb.IsWholeRectangle ? null : sb.Strips));
    }

    /// <summary>
    /// <c>∫ w dS / Area</c> over one half — the "area" core D6 factors the CONSTANT extracted term
    /// against. For a whole rectangle it is the cell's extent along the flow direction over two, which
    /// is what L8c wrote; for a cut cell the ramp is measured from the metal's own boundary and the
    /// two are different numbers, so it is integrated from the strips rather than assumed.
    /// </summary>
    private static double WeightMoment(PlanarCell cell, CellWeight w, PlanarBasisDirection dir)
    {
        if (w.Strips is null) return 0.5 * Extent(cell, dir);

        // ∫∫(αx + βy + γ) dS over a polygon, from the area and its two first moments about any point.
        double total = 0;
        foreach (var strip in w.Strips)
        {
            double area = PolygonIntegrals.Area(strip.Ring, 0, 0);
            total += strip.Alpha * PolygonIntegrals.AreaMoment(strip.Ring, 0, 0, true)
                   + strip.Beta  * PolygonIntegrals.AreaMoment(strip.Ring, 0, 0, false)
                   + strip.Gamma * area;
        }
        return total / cell.Area;
    }

    private static double Extent(PlanarCell c, PlanarBasisDirection d) =>
        d == PlanarBasisDirection.X ? c.Width : c.Height;

    /// <summary>
    /// The three geometric cores for one ordered cell pair: <c>∫∫ w_a w_b /R</c>,
    /// <c>∫∫ w_a w_b ln r</c> and (when asked) <c>∫∫ w_a w_b r</c>.
    ///
    /// <para><b>The inner integral is CLOSED FORM and the outer one is a Gauss rule.</b> That is the
    /// whole reason a rectangular mesh is affordable: the classic near-singular difficulty comes from
    /// doing both numerically, and here only one of them is.</para>
    /// </summary>
    private static (double C0, double CLog, double CRad) PairCores(
        PlanarMesh mesh, CellWeight wa, CellWeight wb, PlanarBasisDirection dir,
        bool wantRad, PlanarFillSettings st)
    {
        // R-cut-2: a pair of whole rectangles takes L8c's own expressions in L8c's own order, so every
        // pre-conformal number in this repository is reproduced bit for bit rather than to a tolerance.
        if (wa.Strips is not null || wb.Strips is not null)
            return PairCoresConformal(mesh, wa, wb, dir, wantRad, st);

        var a = mesh.Cells[wa.CellIndex];
        var b = mesh.Cells[wb.CellIndex];
        var (nodes, panels) = RuleFor(a, b, st);
        var (gx, gw) = Legendre.Nodes(nodes);

        bool alongX = dir == PlanarBasisDirection.X;
        double invAb = 1.0 / b.Area, invAa = 1.0 / a.Area;

        double s0 = 0, sl = 0, sr = 0;
        var t = PanelEdges(panels);

        for (int qx = 0; qx < panels; qx++)
            for (int qy = 0; qy < panels; qy++)
            {
                double xa = a.XMin + t[qx] * a.Width,  xb = a.XMin + t[qx + 1] * a.Width;
                double ya = a.YMin + t[qy] * a.Height, yb = a.YMin + t[qy + 1] * a.Height;
                double cx = 0.5 * (xa + xb), hx = 0.5 * (xb - xa);
                double cy = 0.5 * (ya + yb), hy = 0.5 * (yb - ya);

                for (int i = 0; i < nodes; i++)
                {
                    double x = cx + hx * gx[i];
                    for (int j = 0; j < nodes; j++)
                    {
                        double y  = cy + hy * gx[j];
                        double wq = gw[i] * gw[j] * hx * hy;

                        double weightA = wa.Ramp
                            ? Math.Abs((alongX ? x : y) - wa.Edge) * invAa
                            : invAa;
                        if (weightA == 0) continue;

                        double x1 = b.XMin - x, x2 = b.XMax - x;
                        double y1 = b.YMin - y, y2 = b.YMax - y;

                        double i0, il, ir = 0;
                        if (!wb.Ramp)
                        {
                            i0 = RectangleIntegrals.Inverse(x1, x2, y1, y2) * invAb;
                            il = RectangleIntegrals.Log(x1, x2, y1, y2) * invAb;
                            if (wantRad) ir = RectangleIntegrals.Radius(x1, x2, y1, y2) * invAb;
                        }
                        else
                        {
                            // ξ_b = σ(u + c) in the frame centred on the observation point.
                            double c = (alongX ? x : y) - wb.Edge;
                            double sg = wb.Sigma * invAb;
                            i0 = sg * ((alongX ? RectangleIntegrals.InverseMomentU(x1, x2, y1, y2)
                                               : RectangleIntegrals.InverseMomentV(x1, x2, y1, y2))
                                       + c * RectangleIntegrals.Inverse(x1, x2, y1, y2));
                            il = sg * ((alongX ? RectangleIntegrals.LogMomentU(x1, x2, y1, y2)
                                               : RectangleIntegrals.LogMomentV(x1, x2, y1, y2))
                                       + c * RectangleIntegrals.Log(x1, x2, y1, y2));
                            if (wantRad)
                                ir = sg * ((alongX ? RectangleIntegrals.RadiusMomentU(x1, x2, y1, y2)
                                                   : RectangleIntegrals.RadiusMomentV(x1, x2, y1, y2))
                                           + c * RectangleIntegrals.Radius(x1, x2, y1, y2));
                        }

                        double w = wq * weightA;
                        s0 += w * i0;
                        sl += w * il;
                        if (wantRad) sr += w * ir;
                    }
                }
            }

        return (s0, sl, sr);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // CONFORMAL (CUT) CELLS — the same two integrals over a polygon rather than over a rectangle
    //
    // §3 named three routes and asked for the measurement that chooses. This is (a), and M2 is what
    // forces it: a cut cell's ramp vanishes on the METAL's own outer boundary, so it is affine in
    // BOTH coordinates rather than linear in one, and route (c) — the rectangle's closed form scaled
    // by the area fraction — cannot express it even in principle. Route (b), a numerical inner
    // integral, is measured against this one rather than shipped; see the phase note.
    //
    // THE INNER INTEGRAL STAYS CLOSED FORM, which is the whole point. PolygonIntegrals gives the same
    // six over an arbitrary polygon, and an affine weight α·x + β·y + γ resolves into
    // α·(moment in u) + β·(moment in v) + (αx+βy+γ)·(plain), i.e. exactly the cores it returns. So
    // L8c's own statement — "the classic near-singular difficulty comes from doing BOTH integrals
    // numerically, and here only one of them is" — survives the cut untouched.
    //
    // THE OUTER INTEGRAL keeps its clustered panels. Each strip is a convex quadrilateral, and the
    // bilinear map from the unit square carries the Chebyshev clustering onto it unchanged — which
    // matters for exactly the reason L8c measured it: the outer integrand's gradient is log-divergent
    // on ∂b, and for a self or touching pair that line lies on the outer domain's own boundary.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static (double C0, double CLog, double CRad) PairCoresConformal(
        PlanarMesh mesh, CellWeight wa, CellWeight wb, PlanarBasisDirection dir,
        bool wantRad, PlanarFillSettings st)
    {
        var a = mesh.Cells[wa.CellIndex];
        var b = mesh.Cells[wb.CellIndex];
        var (nodes, panels) = RuleFor(a, b, st);

        double s0 = 0, sl = 0, sr = 0;
        foreach (var (x, y, w) in OuterNodes(a, wa, dir, panels, nodes))
        {
            var (i0, il, ir) = InnerCores(b, wb, dir, x, y, wantRad);
            s0 += w * i0;
            sl += w * il;
            if (wantRad) sr += w * ir;
        }
        return (s0, sl, sr);
    }

    /// <summary>
    /// The inner integral over cell <paramref name="b"/>'s own domain — the rectangle's closed form
    /// when it is whole, the polygon's when it is cut. Both are closed forms; nothing here is a
    /// quadrature.
    /// </summary>
    private static (double I0, double ILog, double IRad) InnerCores(
        PlanarCell b, CellWeight wb, PlanarBasisDirection dir, double x, double y, bool wantRad)
    {
        bool alongX = dir == PlanarBasisDirection.X;
        double invAb = 1.0 / b.Area;

        if (wb.Strips is null)
        {
            double x1 = b.XMin - x, x2 = b.XMax - x;
            double y1 = b.YMin - y, y2 = b.YMax - y;
            if (!wb.Ramp)
                return (RectangleIntegrals.Inverse(x1, x2, y1, y2) * invAb,
                        RectangleIntegrals.Log(x1, x2, y1, y2) * invAb,
                        wantRad ? RectangleIntegrals.Radius(x1, x2, y1, y2) * invAb : 0.0);

            double c  = (alongX ? x : y) - wb.Edge;
            double sg = wb.Sigma * invAb;
            return (sg * ((alongX ? RectangleIntegrals.InverseMomentU(x1, x2, y1, y2)
                                  : RectangleIntegrals.InverseMomentV(x1, x2, y1, y2))
                          + c * RectangleIntegrals.Inverse(x1, x2, y1, y2)),
                    sg * ((alongX ? RectangleIntegrals.LogMomentU(x1, x2, y1, y2)
                                  : RectangleIntegrals.LogMomentV(x1, x2, y1, y2))
                          + c * RectangleIntegrals.Log(x1, x2, y1, y2)),
                    wantRad
                        ? sg * ((alongX ? RectangleIntegrals.RadiusMomentU(x1, x2, y1, y2)
                                        : RectangleIntegrals.RadiusMomentV(x1, x2, y1, y2))
                                + c * RectangleIntegrals.Radius(x1, x2, y1, y2))
                        : 0.0);
        }

        double i0 = 0, il = 0, ir = 0;
        foreach (var strip in wb.Strips)
        {
            var c = PolygonIntegrals.CoresXY(strip.Ring, x, y, wantRad);
            // w(r′) = α·u + β·v + (α·x + β·y + γ), with (u, v) measured from the observation point.
            double g = strip.Alpha * x + strip.Beta * y + strip.Gamma;
            i0 += strip.Alpha * c.InverseU + strip.Beta * c.InverseV + g * c.Inverse;
            il += strip.Alpha * c.LogU     + strip.Beta * c.LogV     + g * c.Log;
            if (wantRad) ir += strip.Alpha * c.RadiusU + strip.Beta * c.RadiusV + g * c.Radius;
        }
        return (i0 * invAb, il * invAb, wantRad ? ir * invAb : 0.0);
    }

    /// <summary>
    /// The OUTER quadrature nodes over one cell's own domain, each already carrying the basis weight
    /// and the 1/Area normalisation. A whole rectangle takes the tensor rule over clustered panels;
    /// a cut cell takes the same rule over each strip through the bilinear quadrilateral map.
    /// </summary>
    private static IEnumerable<(double X, double Y, double W)> OuterNodes(
        PlanarCell a, CellWeight wa, PlanarBasisDirection dir, int panels, int nodes)
    {
        bool alongX = dir == PlanarBasisDirection.X;
        double invAa = 1.0 / a.Area;
        var (gx, gw) = Legendre.Nodes(nodes);
        var t = PanelEdges(panels);

        if (wa.Strips is null)
        {
            for (int qx = 0; qx < panels; qx++)
                for (int qy = 0; qy < panels; qy++)
                {
                    double xa = a.XMin + t[qx] * a.Width,  xb = a.XMin + t[qx + 1] * a.Width;
                    double ya = a.YMin + t[qy] * a.Height, yb = a.YMin + t[qy + 1] * a.Height;
                    double cx = 0.5 * (xa + xb), hx = 0.5 * (xb - xa);
                    double cy = 0.5 * (ya + yb), hy = 0.5 * (yb - ya);

                    for (int i = 0; i < nodes; i++)
                        for (int j = 0; j < nodes; j++)
                        {
                            double x = cx + hx * gx[i], y = cy + hy * gx[j];
                            double weight = wa.Ramp ? Math.Abs((alongX ? x : y) - wa.Edge) * invAa : invAa;
                            if (weight == 0) continue;
                            yield return (x, y, gw[i] * gw[j] * hx * hy * weight);
                        }
                }
            yield break;
        }

        foreach (var strip in wa.Strips)
        {
            var q = strip.Ring;
            for (int px = 0; px < panels; px++)
                for (int py = 0; py < panels; py++)
                    for (int i = 0; i < nodes; i++)
                        for (int j = 0; j < nodes; j++)
                        {
                            double xi = t[px] + 0.5 * (t[px + 1] - t[px]) * (1.0 + gx[i]);
                            double et = t[py] + 0.5 * (t[py + 1] - t[py]) * (1.0 + gx[j]);
                            double jw = 0.25 * (t[px + 1] - t[px]) * (t[py + 1] - t[py]) * gw[i] * gw[j];

                            // The bilinear map of the unit square onto the (convex, possibly
                            // degenerate) quadrilateral, and its Jacobian.
                            double n0 = (1 - xi) * (1 - et), n1 = xi * (1 - et);
                            double n2 = xi * et,             n3 = (1 - xi) * et;
                            double x = n0 * q[0].X + n1 * q[1].X + n2 * q[2].X + n3 * q[3].X;
                            double y = n0 * q[0].Y + n1 * q[1].Y + n2 * q[2].Y + n3 * q[3].Y;

                            double dxu = (1 - et) * (q[1].X - q[0].X) + et * (q[2].X - q[3].X);
                            double dyu = (1 - et) * (q[1].Y - q[0].Y) + et * (q[2].Y - q[3].Y);
                            double dxv = (1 - xi) * (q[3].X - q[0].X) + xi * (q[2].X - q[1].X);
                            double dyv = (1 - xi) * (q[3].Y - q[0].Y) + xi * (q[2].Y - q[1].Y);
                            double jac = dxu * dyv - dyu * dxv;
                            if (jac == 0) continue;

                            double weight = strip.At(x, y) * invAa;
                            if (weight == 0) continue;
                            yield return (x, y, jw * Math.Abs(jac) * weight);
                        }
        }
    }

    /// <summary>
    /// The smooth remainder over one ordered cell pair — plain double quadrature, because after the
    /// extraction the integrand is bounded and has no singularity at all. This is the ONLY part of
    /// the entry that has to be recomputed per frequency (D6).
    /// </summary>
    private static Complex PairRemainder(PlanarMesh mesh, CellWeight wa, CellWeight wb,
                                         PlanarBasisDirection dir, Func<double, Complex> rem,
                                         PlanarFillSettings st)
    {
        if (wa.Strips is not null || wb.Strips is not null)
            return PairRemainderConformal(mesh, wa, wb, dir, rem, st);

        var a = mesh.Cells[wa.CellIndex];
        var b = mesh.Cells[wb.CellIndex];
        double tau = SeparationRatio(a, b);
        int nodes = tau < st.NearRatio ? st.RemainderNodesNear
                  : tau < st.FarRatio  ? st.RemainderNodesMid
                                       : st.RemainderNodesFar;
        var (gx, gw) = Legendre.Nodes(nodes);

        bool alongX = dir == PlanarBasisDirection.X;
        double invAa = 1.0 / a.Area, invAb = 1.0 / b.Area;
        double hax = 0.5 * a.Width, hay = 0.5 * a.Height;
        double hbx = 0.5 * b.Width, hby = 0.5 * b.Height;

        Complex total = Complex.Zero;
        for (int i = 0; i < nodes; i++)
        {
            double x = a.CenterX + hax * gx[i];
            for (int j = 0; j < nodes; j++)
            {
                double y = a.CenterY + hay * gx[j];
                double weightA = wa.Ramp ? Math.Abs((alongX ? x : y) - wa.Edge) * invAa : invAa;
                if (weightA == 0) continue;
                double wOuter = gw[i] * gw[j] * hax * hay * weightA;

                Complex inner = Complex.Zero;
                for (int k = 0; k < nodes; k++)
                {
                    double xp = b.CenterX + hbx * gx[k];
                    for (int l = 0; l < nodes; l++)
                    {
                        double yp = b.CenterY + hby * gx[l];
                        double weightB = wb.Ramp
                            ? Math.Abs((alongX ? xp : yp) - wb.Edge) * invAb
                            : invAb;
                        if (weightB == 0) continue;
                        double dx = x - xp, dy = y - yp;
                        inner += gw[k] * gw[l] * weightB * rem(Math.Sqrt(dx * dx + dy * dy));
                    }
                }
                total += wOuter * inner * hbx * hby;
            }
        }
        return total;
    }

    /// <summary>The remainder over a pair with at least one cut cell — the same rule, over the
    /// strips. No extraction is involved, so the only thing the cut changes is the domain.</summary>
    private static Complex PairRemainderConformal(PlanarMesh mesh, CellWeight wa, CellWeight wb,
                                                  PlanarBasisDirection dir, Func<double, Complex> rem,
                                                  PlanarFillSettings st)
    {
        var a = mesh.Cells[wa.CellIndex];
        var b = mesh.Cells[wb.CellIndex];
        double tau = SeparationRatio(a, b);
        int nodes = tau < st.NearRatio ? st.RemainderNodesNear
                  : tau < st.FarRatio  ? st.RemainderNodesMid
                                       : st.RemainderNodesFar;

        Complex total = Complex.Zero;
        foreach (var (x, y, wOuter) in OuterNodes(a, wa, dir, 1, nodes))
        {
            Complex inner = Complex.Zero;
            foreach (var (xp, yp, wInner) in OuterNodes(b, wb, dir, 1, nodes))
            {
                double dx = x - xp, dy = y - yp;
                inner += wInner * rem(Math.Sqrt(dx * dx + dy * dy));
            }
            total += wOuter * inner;
        }
        return total;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Rule selection, the radial table, and the plumbing
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Normalised outer-panel edges on [0, 1], <b>clustered toward both ends</b> by the Chebyshev
    /// rule <c>t_k = ½(1 − cos(πk/p))</c>.
    ///
    /// <para><b>Why clustered and not uniform.</b> The outer integrand is <c>∫_b dS′/R</c> evaluated
    /// over cell a; its GRADIENT is log-divergent on ∂b, and for a self or touching pair that line
    /// lies on ∂a — i.e. on the outer domain's own boundary, where a Gauss rule is weakest. Clustering
    /// makes the end panel O(1/p²) wide instead of O(1/p), which converts a slow algebraic
    /// convergence into a fast one at no extra cost. It is parameter-free, symmetric, and handles the
    /// self case (singular on all four sides) and the touching case (singular on one) identically,
    /// which is why no "which side is it on" logic is needed.</para>
    /// </summary>
    private static double[] PanelEdges(int p) => PanelCache.GetOrAdd(p, static q =>
    {
        var t = new double[q + 1];
        for (int k = 0; k <= q; k++) t[k] = 0.5 * (1.0 - Math.Cos(Math.PI * k / q));
        t[0] = 0.0; t[q] = 1.0;
        return t;
    });

    private static readonly ConcurrentDictionary<int, double[]> PanelCache = new();

    /// <summary><see cref="RuleFor"/> and <see cref="PanelEdges"/>, reachable from
    /// <see cref="ViaZIntegral"/> so the prism core's in-plane rule is literally the same one the
    /// planar cores use rather than a second copy that can drift from it.</summary>
    internal static (int Nodes, int Panels) RuleForCells(PlanarCell a, PlanarCell b, PlanarFillSettings st)
        => RuleFor(a, b, st);

    /// <inheritdoc cref="RuleForCells"/>
    internal static double[] PanelEdgesFor(int panels) => PanelEdges(panels);

    private static (int Nodes, int Panels) RuleFor(PlanarCell a, PlanarCell b, PlanarFillSettings st)
    {
        double tau = SeparationRatio(a, b);
        if (tau == 0)           return (st.NearNodes, st.SelfPanels);   // the same cell
        if (tau < st.NearRatio) return (st.NearNodes, st.TouchPanels);
        if (tau < st.FarRatio)  return (st.MidNodes, 1);
        return (st.FarNodes, 1);
    }

    /// <summary>Centroid separation ÷ the larger cell diagonal — R-fil-5's "separation-to-size
    /// ratio", the one number every rule below keys off.</summary>
    private static double SeparationRatio(PlanarCell a, PlanarCell b)
    {
        // The METAL's centroid, which is the cell's own centre until a cut moves it. Identical for
        // every uncut cell, so no rule selection anywhere in the shipped path changes.
        double dx = a.CentroidX - b.CentroidX, dy = a.CentroidY - b.CentroidY;
        double d  = Math.Sqrt(dx * dx + dy * dy);
        double s  = Math.Max(Math.Sqrt(a.Width * a.Width + a.Height * a.Height),
                             Math.Sqrt(b.Width * b.Width + b.Height * b.Height));
        return s > 0 ? d / s : 0.0;
    }

    /// <summary>The remainder evaluator: a per-frequency radial table, or the kernel itself.</summary>
    private static Func<double, Complex> Remainder(PlanarKernelTerms terms, PlanarFillCores cores)
    {
        var st = cores.Settings;
        if (!st.UseRadialTable) return terms.Remainder;

        double spacing = st.TableCellFraction * cores.MinCellEdgeM;
        var table = RadialRemainderTable.Build(terms, Math.Max(cores.ExtentM, spacing * 8), spacing,
                                               st.MaxTableSamples);
        return table.Evaluate;
    }

    /// <summary>R-fil-10's own copy of R17. <b>P1: the megabytes come from
    /// <see cref="PlanarSystem.ResidentPhrase"/>, the one function all three refusals share</b>, so
    /// the three cannot drift apart again — they had all three quoted 16·N², which is about a third
    /// of what the machine holds.</summary>
    private static void GuardCeiling(int n, int cellCount = 0)
    {
        if (n > SurfaceMesher.UnknownCeiling)
            throw new InvalidOperationException(
                $"This mesh has {n:N0} unknowns, which is past the {SurfaceMesher.UnknownCeiling:N0}-unknown " +
                $"ceiling this kernel is built for ({PlanarSystem.ResidentPhrase(n, cellCount)}). " +
                "Lower Cells per wavelength, turn the edge mesh off, or analyse a " +
                "smaller region — full-wave analysis of a structure this size needs matrix " +
                "compression, which is not built.");
    }

    /// <summary>Packed upper-triangle index for <c>i ≤ j</c> in an <c>n×n</c> symmetric array.</summary>
    private static long Packed(int i, int j, int n) => (long)i * n - (long)i * (i - 1) / 2 + (j - i);

    private static void RequireLayout(PlanarFillCores cores, PlanarCoreLayout layout, string caller)
    {
        if (cores.Layout == layout) return;
        throw new InvalidOperationException(
            $"{caller} needs cores in the {layout} layout and these are {cores.Layout}. " +
            "PlanarFill.BuildCores produces the class layout for Fill; BuildCoresByPairs and " +
            "BuildCoresByHalves produce the triangle layout for the reference fills FillByPairs and " +
            "FillByHalves.");
    }

    private static void RequirePairCores(PlanarFillCores cores)
    {
        if (cores.HasPairCores) return;
        throw new InvalidOperationException(
            "This fill needs the cached cell-pair and basis-pair cores, and these were built " +
            "geometry-only by PlanarFill.BuildGeometryOnlyCores — the O(N) shape M5's accelerator " +
            "uses. A DENSE fill of a geometry-only core would be silently building the O(N²) " +
            "arithmetic that was deliberately skipped. Build the cores with PlanarFill.BuildCores if " +
            "a dense matrix is what is wanted.");
    }

    // ── M5's per-entry seam ───────────────────────────────────────────────────────────────────
    // PlanarEntryFill (below) is the only caller. These are wrappers rather than a widening of the
    // helpers' own visibility, so the dense path's own call sites read exactly as L8c wrote them.

    internal static CellWeight PulseAt(PlanarMesh mesh, int cellIndex) => Pulse(mesh, cellIndex);

    internal static (CellWeight A, CellWeight B) RampHalvesOf(PlanarMesh mesh, PlanarBasis basis)
        => RampHalves(mesh, basis);

    internal static double WeightMomentOf(PlanarCell cell, CellWeight w, PlanarBasisDirection dir)
        => WeightMoment(cell, w, dir);

    internal static double ExtentOf(PlanarCell c, PlanarBasisDirection d) => Extent(c, d);

    internal static (double C0, double CLog, double CRad) PairCoresOf(
        PlanarMesh mesh, CellWeight wa, CellWeight wb, PlanarBasisDirection dir,
        bool wantRad, PlanarFillSettings st) => PairCores(mesh, wa, wb, dir, wantRad, st);

    internal static Complex PairRemainderOf(
        PlanarMesh mesh, CellWeight wa, CellWeight wb, PlanarBasisDirection dir,
        Func<double, Complex> rem, PlanarFillSettings st)
        => PairRemainder(mesh, wa, wb, dir, rem, st);

    internal static Func<double, Complex> RemainderOf(PlanarKernelTerms terms, PlanarFillCores cores)
        => Remainder(terms, cores);

    // ── P12's per-entry seam onto the VIA arms ────────────────────────────────────────────────
    // PlanarBorderedAimOperator is the only caller. Wrappers rather than a widening of the helpers'
    // own visibility, for the same reason M5's are: FillMultiLevel's call sites read as L9 wrote
    // them, and the bordered operator's border is THE SAME ARITHMETIC rather than a second reading
    // of it — which is what the |ΔI| gates against the dense via solve are actually measuring.

    /// <inheritdoc cref="SingularPrismPart"/>
    internal static Complex SingularPrismPartOf(PlanarMesh mesh,
                                                LayeredSpectralGreens.InteriorAsymptote asym,
                                                int cellA, int cellB,
                                                ViaZIntegral.Span si, ViaZIntegral.Span sj,
                                                PlanarFillSettings st)
        => SingularPrismPart(mesh, asym, cellA, cellB, si, sj, st);

    /// <inheritdoc cref="MixedEntry"/>
    internal static Complex MixedEntryOf(PlanarMesh mesh, PlanarBasis vertical, PlanarBasis horizontal,
                                         (CellWeight A, CellWeight B) horizontalHalves,
                                         Func<double, Complex> dG, double rhoFloor,
                                         PlanarFillSettings st)
        => MixedEntry(mesh, vertical, horizontal, horizontalHalves, dG, rhoFloor, st);

    /// <inheritdoc cref="CellPairSpan"/>
    internal static double CellPairSpanOf(PlanarCell a, PlanarCell b) => CellPairSpan(a, b);

    /// <summary>
    /// <b>M5's projection reads the basis through the FILL'S OWN weight evaluation</b>, cut cells and
    /// all, rather than re-deriving the rooftop from its geometry. A projection built on a second
    /// reading of what the basis is would be an approximation of a different operator, and the
    /// difference would show up as an accuracy floor nothing could explain.
    /// </summary>
    /// <summary>M5's near-field fill is row-parallel for exactly R-fil-11's reason — each row is
    /// written by one iteration and nothing accumulates into shared state — so it goes through the
    /// same budget-aware loop the dense fill does rather than a second <c>Parallel.For</c> that M1's
    /// one cap would not bound.</summary>
    internal static void ForRowsOf(PlanarFillSettings st, int count, Action<int> row)
        => ForRows(st, count, row);

    internal static IEnumerable<(double X, double Y, double W)> WeightNodes(
        PlanarCell a, CellWeight wa, PlanarBasisDirection dir, int panels, int nodes)
        => OuterNodes(a, wa, dir, panels, nodes);

    /// <summary>
    /// R-fil-11's parallelism: over ROWS, each written exactly once. Never over a shared accumulator,
    /// so the answer does not depend on how the scheduler happened to interleave.
    ///
    /// <para><b>M1/M2 — three shapes, and the first is the one every pre-brief caller still takes.</b>
    /// No cap and no budget is L8c's own <c>Parallel.For</c>, unchanged. A plain cap is that plus a
    /// <c>ParallelOptions</c>. A BUDGET is what a fanned-out run gets: each worker that joins the
    /// loop takes one permit for as long as it participates and releases it when the loop ends, so
    /// the cap bounds fill threads ACROSS every concurrent solve rather than within one. A solve
    /// that has finished filling and gone into its single-threaded LU is holding no permits, which
    /// is exactly the overlap M2 exists to buy — see <see cref="PlanarParallelBudget"/>.</para>
    /// </summary>
    private static void ForRows(PlanarFillSettings st, int count, Action<int> row)
    {
        if (!st.Parallel || count <= 8)
        {
            for (int i = 0; i < count; i++) row(i);
            return;
        }

        if (st.Budget is { } budget)
        {
            System.Threading.Tasks.Parallel.For(
                0, count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = budget.Cap },
                () => { budget.Enter(); return true; },
                (i, _, held) => { row(i); return held; },
                _ => budget.Exit());
            return;
        }

        if (st.MaxDegreeOfParallelism is { } cap)
            System.Threading.Tasks.Parallel.For(
                0, count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = cap },
                row);
        else
            System.Threading.Tasks.Parallel.For(0, count, row);
    }
}

/// <summary>
/// <b>P4 — which rooftops each cell belongs to, per flow direction, and which of them are cut.</b>
/// O(N), built by both core builders, read by the cell-pair passes in <see cref="PlanarFill"/> and
/// by <see cref="PlanarEntryFill"/>. Every array is read-only after construction.
/// </summary>
internal sealed class RampTopology
{
    /// <summary>One direction's bases and, per cell, the (position, half) pairs it appears in.</summary>
    internal sealed class DirectionMap
    {
        /// <summary>Direction position → basis index (the same array the cores' <c>XBases</c>/<c>YBases</c> hold).</summary>
        public readonly int[] Idx;
        /// <summary>CSR row starts over CELLS, length m + 1.</summary>
        public readonly int[] CellStart;
        /// <summary>CSR payload: the direction POSITION of a basis the cell belongs to…</summary>
        public readonly int[] Pos;
        /// <summary>…and whether the cell is that basis's B (upper, falling-ramp) half.</summary>
        public readonly bool[] IsB;
        public int Count => Idx.Length;

        internal DirectionMap(int[] idx, int[] cellStart, int[] pos, bool[] isB)
        {
            Idx = idx; CellStart = cellStart; Pos = pos; IsB = isB;
        }
    }

    /// <summary>Each rooftop's two halves as the quadrature wants them — <see cref="PlanarFill.RampHalves"/>, once.</summary>
    public readonly (PlanarFill.CellWeight A, PlanarFill.CellWeight B)[] Halves;
    /// <summary>Per basis: either half carries strips, i.e. its ramp is affine in both coordinates
    /// and (P4.1) does not hold. Such a basis takes the four-call path on every pair.</summary>
    public readonly bool[] Cut;
    /// <summary>Per basis: its position inside its direction's <see cref="DirectionMap.Idx"/>.</summary>
    public readonly int[] DirPos;
    public readonly DirectionMap X, Y;
    /// <summary>Per cell: the smallest inner cell index any same-direction basis pair with this cell
    /// as OUTER can ask for — the lower edge of the ordered-pair band (see the P4 header).</summary>
    public readonly int[] MinInner;
    /// <summary>Per cell: at least one uncut rooftop uses it, so the rectangle primitives are wanted.</summary>
    public readonly bool[] HasWholeRamp;

    private RampTopology((PlanarFill.CellWeight, PlanarFill.CellWeight)[] halves, bool[] cut, int[] dirPos,
                         DirectionMap x, DirectionMap y, int[] minInner, bool[] hasWholeRamp)
    {
        Halves = halves; Cut = cut; DirPos = dirPos; X = x; Y = y; MinInner = minInner; HasWholeRamp = hasWholeRamp;
    }

    public DirectionMap Of(PlanarBasisDirection d) => d == PlanarBasisDirection.X ? X : Y;

    public static RampTopology Build(PlanarMesh mesh)
    {
        int n = mesh.Bases.Count, m = mesh.Cells.Count;
        var halves = new (PlanarFill.CellWeight, PlanarFill.CellWeight)[n];
        var cut    = new bool[n];
        var xb = new List<int>();
        var yb = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var basis = mesh.Bases[i];
            halves[i] = PlanarFill.RampHalvesOf(mesh, basis);
            cut[i]    = halves[i].Item1.Strips is not null || halves[i].Item2.Strips is not null;
            if (basis.Direction == PlanarBasisDirection.X) xb.Add(i);
            else if (basis.Direction == PlanarBasisDirection.Y) yb.Add(i);
        }

        var dirPos = new int[n];
        for (int i = 0; i < xb.Count; i++) dirPos[xb[i]] = i;
        for (int i = 0; i < yb.Count; i++) dirPos[yb[i]] = i;

        var minInner     = new int[m];
        var hasWholeRamp = new bool[m];
        for (int c = 0; c < m; c++) minInner[c] = c;

        DirectionMap Map(List<int> list)
        {
            var idx = list.ToArray();
            int k = idx.Length;
            var count = new int[m + 1];
            foreach (int b in idx)
            {
                count[halves[b].Item1.CellIndex + 1]++;
                count[halves[b].Item2.CellIndex + 1]++;
            }
            for (int c = 0; c < m; c++) count[c + 1] += count[c];
            var start = (int[])count.Clone();
            var fill  = (int[])count.Clone();
            var pos   = new int[2 * k];
            var isB   = new bool[2 * k];
            for (int p = 0; p < k; p++)
            {
                var (ha, hb) = halves[idx[p]];
                pos[fill[ha.CellIndex]] = p; isB[fill[ha.CellIndex]++] = false;
                pos[fill[hb.CellIndex]] = p; isB[fill[hb.CellIndex]++] = true;
            }

            // The smallest cell any basis at position ≥ p touches; a pair (i, j ≥ i) with cell a as
            // i's outer half needs inner cells down to this.
            var suffixMin = new int[k + 1];
            suffixMin[k] = int.MaxValue;
            for (int p = k - 1; p >= 0; p--)
            {
                var (ha, hb) = halves[idx[p]];
                suffixMin[p] = Math.Min(suffixMin[p + 1], Math.Min(ha.CellIndex, hb.CellIndex));
            }
            for (int c = 0; c < m; c++)
                for (int e = start[c]; e < start[c + 1]; e++)
                {
                    minInner[c] = Math.Min(minInner[c], suffixMin[pos[e]]);
                    if (!cut[idx[pos[e]]]) hasWholeRamp[c] = true;
                }
            return new DirectionMap(idx, start, pos, isB);
        }

        var x = Map(xb);
        var y = Map(yb);
        return new RampTopology(halves, cut, dirPos, x, y, minInner, hasWholeRamp);
    }
}

/// <summary>
/// <b>P6 — the frequency-independent half of <see cref="PlanarEntryFill"/>, built once per mesh.</b>
/// Everything the per-entry fill reads that does not carry ω: the basis halves, the per-basis
/// moments, the class table, and — the part that costs — the singular cores of every cell pair the
/// near field asks for, memoised by translation class (P5) or, for a pair with a cut cell or a cut
/// basis, per pair.
///
/// <para>Until P6 all of this lived on <see cref="PlanarEntryFill"/>, which
/// <see cref="PlanarAimOperator"/> constructed afresh at every frequency — so the clustered-panel
/// singular quadrature behind every near entry ran once per FREQUENCY, while the dense path ran it
/// once per MESH (D6, <c>CoreFillCount == 1</c>). <see cref="PlanarAimGeometry"/> owns one of these
/// and warms it over the whole near set when the geometry is built; the per-frequency fill then
/// finds every core it needs already here, and <see cref="CorePasses"/> is the counter that says
/// so (<c>PlanarP6AimGeometryTests</c>).</para>
///
/// <para><b>The class cores live in a flat array, not in dictionary nodes.</b> The index is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> from class key to slot; the seven primitives sit
/// in chunked <see cref="PlanarFill.CellPairMoments"/> arrays those slots address. A dictionary
/// holding the values themselves would keep 168 B per class inside nodes that no memory walk
/// counts — P1's own gate (<c>P1_3</c>) adds up the arrays an operator holds, and a store it cannot
/// see is a store the accounting cannot be checked against.</para>
///
/// <para>Thread-safe: the value factories are pure functions of the key, so a race to insert
/// produces the same bits either way; slot allocation is the one write that serialises, and it is
/// a counter increment.</para>
/// </summary>
public sealed class PlanarEntryCores
{
    private const int ChunkShift = 9;                // 512 classes per chunk
    private const int ChunkSize  = 1 << ChunkShift;

    internal readonly PlanarMesh         Mesh;
    internal readonly PlanarFillSettings Settings;
    internal readonly bool               WantRad;

    internal readonly (RooftopHalf A, RooftopHalf B)[]                        DivHalves;
    internal readonly (PlanarFill.CellWeight A, PlanarFill.CellWeight B)[]    RampHalves;
    internal readonly double[]       Moments;
    internal readonly PairClassifier Classifier;
    internal readonly bool[]         Memoised;

    private readonly ConcurrentDictionary<long, int> _classSlot = new();
    private readonly object _grow = new();
    private PlanarFill.CellPairMoments[][] _chunks = new PlanarFill.CellPairMoments[4][];
    private int _slots;

    private readonly ConcurrentDictionary<(int, int), (double C0, double CLog, double CRad)> _cutScalar = new();
    private readonly ConcurrentDictionary<(int, int), (double T, double L, double R)>        _cutVector = new();
    private long _passes;

    /// <summary>The geometry this was built from.</summary>
    public PlanarFillCores Cores { get; }

    /// <summary>How many distinct translation classes have been integrated.</summary>
    public int ClassCount => _classSlot.Count;

    /// <summary>Per-pair scalar cores held for pairs the class table cannot serve (a cut cell).</summary>
    public int CutScalarPairs => _cutScalar.Count;

    /// <summary>Per-pair vector cores held for same-direction basis pairs with a cut half.</summary>
    public int CutVectorPairs => _cutVector.Count;

    /// <summary>
    /// <b>The P6 counter.</b> Outer-quadrature passes of the singular cores run through this object:
    /// a class counts 1, a cut scalar pair 1, a cut vector pair 4 (its four-call path). Over a sweep
    /// of any length on one geometry this must stop growing after the geometry is built —
    /// <c>PlanarP6AimGeometryTests</c> asserts exactly that, the way <c>CoreFillCount</c> asserts
    /// D6's once-per-mesh core build.
    /// </summary>
    public long CorePasses => Volatile.Read(ref _passes);

    /// <summary>Bytes this holds: the class store at 168 B per class plus ~40 B of index node, the
    /// cut caches at ~72 B per entry, and the two per-basis half arrays.</summary>
    public long Bytes =>
        (long)_classSlot.Count * (Unsafe.SizeOf<PlanarFill.CellPairMoments>() + 40)
      + (long)(_cutScalar.Count + _cutVector.Count) * 72
      + (long)DivHalves.Length * Unsafe.SizeOf<(RooftopHalf, RooftopHalf)>()
      + (long)Moments.Length * 0;                    // Moments and RampHalves are the cores' own arrays

    public PlanarEntryCores(PlanarFillCores cores)
    {
        ArgumentNullException.ThrowIfNull(cores);
        Cores      = cores;
        Mesh       = cores.Mesh;
        Settings   = cores.Settings;
        WantRad    = Settings.Order >= PlanarExtractionOrder.Linear;
        Moments    = cores.VMoment;
        Classifier = cores.Classifier;
        Memoised   = cores.Memoised;
        // P6: read straight off the topology rather than copied — it is the same array of the same
        // structs, and a copy per frequency was N × 2 × sizeof(CellWeight) of pure duplication.
        RampHalves = cores.Topology.Halves;

        int n = Mesh.Bases.Count;
        DivHalves = new (RooftopHalf, RooftopHalf)[n];
        for (int i = 0; i < n; i++) DivHalves[i] = PlanarBasisFunctions.Halves(Mesh, Mesh.Bases[i]);
    }

    /// <summary>The seven primitives of a class, integrated on its synthetic representative the
    /// first time any pair of the class is asked for, and read from the store after.</summary>
    internal PlanarFill.CellPairMoments ClassCores(long key)
    {
        int slot = _classSlot.GetOrAdd(key, static (k, self) => self.Integrate(k), this);
        return _chunks[slot >> ChunkShift][slot & (ChunkSize - 1)];
    }

    private int Integrate(long key)
    {
        var (a, b) = Classifier.Representative(key);
        var (nodes, panels) = PairClassifier.CoreRule(key, Settings);
        var cores = PlanarFill.CellPairCores(a, b, nodes, panels, WantRad);
        Interlocked.Increment(ref _passes);

        lock (_grow)
        {
            int slot = _slots++;
            int chunk = slot >> ChunkShift;
            if (chunk >= _chunks.Length) Array.Resize(ref _chunks, _chunks.Length * 2);
            _chunks[chunk] ??= new PlanarFill.CellPairMoments[ChunkSize];
            _chunks[chunk][slot & (ChunkSize - 1)] = cores;
            return slot;
        }
    }

    /// <summary>D4's area-normalised pulse×pulse cores of a cell pair the class table cannot
    /// serve, per pair — the pulse path's own four-call arithmetic, unchanged since P4.</summary>
    internal (double C0, double CLog, double CRad) CutScalarCores(int cellA, int cellB)
        => _cutScalar.GetOrAdd((cellA, cellB), static (key, self) =>
        {
            var wa = PlanarFill.PulseAt(self.Mesh, key.Item1);
            var wb = PlanarFill.PulseAt(self.Mesh, key.Item2);
            var r  = PlanarFill.PairCoresOf(self.Mesh, wa, wb, PlanarBasisDirection.X, self.WantRad, self.Settings);
            Interlocked.Increment(ref self._passes);
            return r;
        }, this);

    /// <summary>The summed cores of a same-direction basis pair with a cut half — the four-call
    /// path's <c>t00 + t01 + t10 + t11</c> (and the log and radius sums), in that order, so the
    /// assembled entry is bit-for-bit what the per-frequency four-call path produced.</summary>
    internal (double T, double L, double R) CutVectorCores(int a, int b, PlanarBasisDirection dir)
        => _cutVector.GetOrAdd((a, b), static (key, self) =>
        {
            var (ra, rb) = self.RampHalves[key.Item1];
            var (sa, sb) = self.RampHalves[key.Item2];
            var dir = self.Mesh.Bases[key.Item1].Direction;
            var (t00, l00, r00) = PlanarFill.PairCoresOf(self.Mesh, ra, sa, dir, self.WantRad, self.Settings);
            var (t01, l01, r01) = PlanarFill.PairCoresOf(self.Mesh, ra, sb, dir, self.WantRad, self.Settings);
            var (t10, l10, r10) = PlanarFill.PairCoresOf(self.Mesh, rb, sa, dir, self.WantRad, self.Settings);
            var (t11, l11, r11) = PlanarFill.PairCoresOf(self.Mesh, rb, sb, dir, self.WantRad, self.Settings);
            Interlocked.Add(ref self._passes, 4);
            return (t00 + t01 + t10 + t11, l00 + l01 + l10 + l11, r00 + r01 + r10 + r11);
        }, this);

    /// <summary>The cores-only source <see cref="PlanarFill.WholeVectorCore{T}"/> reads while the
    /// geometry warms the store — its <c>Remainder</c> is never called there.</summary>
    internal readonly struct CoreSource(PlanarEntryCores owner) : PlanarFill.IPairSource
    {
        private readonly PlanarEntryCores _o = owner;

        public void Get(int outer, int inner, out PlanarFill.CellPairMoments cores, out bool rotated)
        {
            long key = _o.Classifier.Key(_o.Mesh.Cells[outer], _o.Mesh.Cells[inner], _o.Settings, out rotated);
            cores = _o.ClassCores(key);
        }

        public PlanarFill.CellPairRemainders Remainder(int outer, int inner)
            => throw new InvalidOperationException("The cores-only source carries no remainder.");
    }

    /// <summary>
    /// Every singular core <see cref="PlanarEntryFill.At"/> will need for <c>(i, j)</c>, computed
    /// now if it is not already held — the same lookups <c>At</c> makes, through the same functions,
    /// with the frequency-dependent remainders left out. <see cref="PlanarAimGeometry"/> runs this
    /// over its near set once, which is what makes the per-frequency near fill core-free.
    /// </summary>
    internal void Prepare(int i, int j)
    {
        int a = Math.Min(i, j), b = Math.Max(i, j);

        var (ma, mb) = DivHalves[a];
        var (na, nb) = DivHalves[b];
        PrepareScalar(ma.CellIndex, na.CellIndex);
        PrepareScalar(ma.CellIndex, nb.CellIndex);
        PrepareScalar(mb.CellIndex, na.CellIndex);
        PrepareScalar(mb.CellIndex, nb.CellIndex);

        var dirA = Mesh.Bases[a].Direction;
        if (dirA != Mesh.Bases[b].Direction) return;

        if (!Memoised[a] || !Memoised[b]) { _ = CutVectorCores(a, b, dirA); return; }
        _ = PlanarFill.WholeVectorCore(new CoreSource(this), Mesh, RampHalves[a], RampHalves[b],
                                       dirA == PlanarBasisDirection.X);
    }

    /// <summary>P11 — <see cref="PrepareScalar"/> for a near set built over CELLS. Same warm-up, same
    /// store; the basis-pair form calls it four times.</summary>
    internal void PrepareScalarPair(int cellA, int cellB) => PrepareScalar(cellA, cellB);

    private void PrepareScalar(int cellA, int cellB)
    {
        int a = Math.Min(cellA, cellB), b = Math.Max(cellA, cellB);
        var ok = Classifier.Classifiable;
        if (!ok[a] || !ok[b]) { _ = CutScalarCores(a, b); return; }
        _ = ClassCores(Classifier.Key(Mesh.Cells[a], Mesh.Cells[b], Settings, out _));
    }
}

/// <summary>
/// <b>M5 — ONE entry of the Galerkin matrix, computed on demand.</b> The dense fill's own arithmetic,
/// in the dense fill's own order, for a single <c>(i, j)</c>: <see cref="At"/> is asserted BIT-IDENTICAL
/// against <see cref="PlanarFill.Fill"/> entry by entry, which is what lets AIM's near-field correction
/// be "the exact matrix, sparsely" rather than a second formulation of it.
///
/// <para><b>Why it exists at all.</b> AIM computes exact entries only for pairs inside its near field —
/// O(N) of them, a few hundred per row — and <see cref="PlanarFill.BuildCores"/> is O(N²) in both time
/// and memory. Filling the whole triangle and then reading a thin band out of it would leave the
/// accelerator's cost claim resting on the very quadratic term it removes. So the cores are computed
/// per pair here, from <see cref="PlanarFill.BuildGeometryOnlyCores"/>' O(N) geometry.</para>
///
/// <para><b>P5: the caches are keyed by TRANSLATION CLASS, not by cell pair.</b> P4 memoised the
/// seven primitives per ordered cell pair; the same near-field row now asks the class table
/// (<see cref="PairClassifier"/>) and integrates a class once, on the class's synthetic
/// representative — the SAME representative the dense build uses, which is what keeps
/// <see cref="At"/> bit-identical to <see cref="PlanarFill.Fill"/> now that both read a class rather
/// than the pair itself, and what keeps R-fil-11 under a row-parallel near fill (a first-visitor
/// representative would depend on the scheduler; a pure function of the key does not). A pair
/// with a cut basis takes the four-call path exactly as the dense fill does, and a scalar pair with
/// a cut cell is memoised per pair as it was.</para>
///
/// <para><b>P6: this object is now the PER-FREQUENCY half only.</b> The cores — every singular
/// quadrature — live on a <see cref="PlanarEntryCores"/> that outlives it, built once per mesh; what
/// is constructed here per frequency is the two radial remainder tables, the ω scales, and the
/// per-class remainder memos. The four-argument constructor still exists for a caller with no
/// geometry to share (the gates), and builds a private <see cref="PlanarEntryCores"/>.</para>
///
/// <para>Thread-safe: every field is read-only after construction, the remainder evaluators are the
/// same shared radial tables the parallel dense fill already uses, and the caches are
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>s whose value factories are pure functions of the
/// key, so a race to insert produces the same bits either way.</para>
/// </summary>
public sealed class PlanarEntryFill
{
    private readonly PlanarEntryCores   _g;
    private readonly PlanarMesh         _mesh;
    private readonly PlanarFillSettings _st;
    private readonly PlanarKernelTerms  _termsA;
    private readonly Func<double, Complex> _remA;
    private readonly Complex _scalarScale, _vectorScale;

    private readonly ConcurrentDictionary<long, PlanarFill.CellPairRemainders> _remA7 = new();

    /// <summary><b>P11 — the scalar block's cell-pulse potential, as its own object</b>, because the
    /// accelerated static capacitance solve needs exactly this and nothing else of a fill. See
    /// <see cref="PlanarPulsePotential"/>; this class's <c>P</c> is that object's <c>At</c>.</summary>
    public PlanarPulsePotential Pulse => _pulse;
    private readonly PlanarPulsePotential _pulse;

    /// <summary>How many distinct translation CLASSES the scalar block has integrated (plus any
    /// per-pair entries for pairs with a cut cell) — the counter that says the near field really is
    /// O(N) and that the memo is doing what it is here for. Until P5 this counted cell pairs.</summary>
    public int CellPairCount => _pulse.CellPairCount;

    /// <summary>P5 — how many distinct translation classes the vector block has integrated (one
    /// seven-primitive core pass and one seven-sum remainder pass each).</summary>
    public int VectorPairCount => _remA7.Count;

    /// <summary>P6 — the frequency-independent cores this fill reads.</summary>
    public PlanarEntryCores Geometry => _g;

    public PlanarEntryFill(PlanarFillCores cores, PlanarKernelTerms termsA, PlanarKernelTerms termsQ,
                           double omega)
        : this(new PlanarEntryCores(cores), termsA, termsQ, omega) { }

    /// <summary>P6 — the per-frequency fill over a shared, already-built core store.</summary>
    public PlanarEntryFill(PlanarEntryCores geometry, PlanarKernelTerms termsA, PlanarKernelTerms termsQ,
                           double omega)
        : this(geometry, termsA, new PlanarPulsePotential(geometry, termsQ), omega) { }

    /// <summary>
    /// <b>P12 — the same, over a scalar block the caller already owns.</b> A multi-level accelerated
    /// operator holds one <see cref="PlanarPulsePotential"/> per (level, level) pairing and reads it
    /// from BOTH the horizontal near field and the via border; constructing a second one inside this
    /// would give the two halves separate remainder memos of the same function.
    /// </summary>
    public PlanarEntryFill(PlanarEntryCores geometry, PlanarKernelTerms termsA,
                           PlanarPulsePotential pulse, double omega)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(termsA);
        ArgumentNullException.ThrowIfNull(pulse);

        _g    = geometry;
        _mesh = geometry.Mesh;
        _st   = geometry.Settings;
        var cores = geometry.Cores;

        // The dense path re-floors the terms for this mesh in exactly these two places
        // (ScalarPotentialMatrix and Fill), so the entry evaluator does the same rather than trusting
        // the caller to have done it. The scalar half re-floors inside PlanarPulsePotential, which is
        // the same call on the same two arguments.
        _termsA = termsA.With(_st.Order, cores.RhoFloorM);
        _remA   = PlanarFill.RemainderOf(_termsA, cores);
        _pulse  = pulse;

        _scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        _vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
    }

    /// <summary>The on-demand class source <see cref="PlanarFill.WholeVectorEntry{T}"/> reads.</summary>
    private readonly struct ClassSource(PlanarEntryFill owner) : PlanarFill.IPairSource
    {
        private readonly PlanarEntryFill _o = owner;

        public void Get(int outer, int inner, out PlanarFill.CellPairMoments cores, out bool rotated)
        {
            long key = _o._g.Classifier.Key(_o._mesh.Cells[outer], _o._mesh.Cells[inner], _o._st, out rotated);
            cores = _o._g.ClassCores(key);
        }

        public PlanarFill.CellPairRemainders Remainder(int outer, int inner)
        {
            long key = _o._g.Classifier.Key(_o._mesh.Cells[outer], _o._mesh.Cells[inner], _o._st, out _);
            return _o._remA7.GetOrAdd(key, static (k, self) =>
            {
                var (a, b) = self._g.Classifier.Representative(k);
                return PlanarFill.CellPairRemainder(a, b, PairClassifier.RemainderNodes(k, self._st), self._remA);
            }, _o);
        }
    }

    /// <summary>
    /// <c>Z[i, j]</c>. Symmetric by construction, exactly as the dense fill is: the work is done on the
    /// ordered pair <c>min ≤ max</c> and the other triangle is the same number, not a second
    /// computation of it.
    /// </summary>
    public Complex At(int i, int j)
    {
        int a = Math.Min(i, j), b = Math.Max(i, j);

        // ── the scalar block: the same signed sum of four cell-pair potentials (D4) ───────────
        var (ma, mb) = _g.DivHalves[a];
        var (na, nb) = _g.DivHalves[b];
        Complex s = ma.Sign * na.Sign * P(ma.CellIndex, na.CellIndex)
                  + ma.Sign * nb.Sign * P(ma.CellIndex, nb.CellIndex)
                  + mb.Sign * na.Sign * P(mb.CellIndex, na.CellIndex)
                  + mb.Sign * nb.Sign * P(mb.CellIndex, nb.CellIndex);
        Complex z = _scalarScale * s;

        // ── the vector block: same direction only (D5) ───────────────────────────────────────
        var dirA = _mesh.Bases[a].Direction;
        if (dirA != _mesh.Bases[b].Direction) return z;

        var ramps = _g.RampHalves;
        var (ra, rb) = ramps[a];
        var (sa, sb) = ramps[b];

        if (!_g.Memoised[a] || !_g.Memoised[b])
        {
            // The four-call path, exactly as the dense row pass takes it on a pair with a cut half.
            // P6: the four cores are summed once, on the geometry; the remainders are per frequency.
            var (t, l, r) = _g.CutVectorCores(a, b, dirA);

            Complex vc = _termsA.Inverse * t + _termsA.Log * l;
            if (_termsA.ExtractsConstant) vc += _termsA.Constant * (_g.Moments[a] * _g.Moments[b]);
            if (_termsA.ExtractsLinear)   vc += _termsA.Linear   * r;

            Complex rc = PlanarFill.PairRemainderOf(_mesh, ra, sa, dirA, _remA, _st)
                       + PlanarFill.PairRemainderOf(_mesh, ra, sb, dirA, _remA, _st)
                       + PlanarFill.PairRemainderOf(_mesh, rb, sa, dirA, _remA, _st)
                       + PlanarFill.PairRemainderOf(_mesh, rb, sb, dirA, _remA, _st);

            return z + _vectorScale * (vc + rc);
        }

        // ── P5: the dense build's own assembly, from the class cache ─────────────────────────
        return z + _vectorScale * PlanarFill.WholeVectorEntry(
            new ClassSource(this), _mesh, ramps[a], ramps[b],
            dirA == PlanarBasisDirection.X, _termsA, _g.Moments[a], _g.Moments[b]);
    }

    /// <summary>D4's area-averaged scalar-potential coefficient for one CELL pair — the dense path's
    /// <c>P[a, b]</c>. <b>P11: one line, because the arithmetic moved to
    /// <see cref="PlanarPulsePotential"/> whole</b>, so the accelerated static solve and this share
    /// one implementation rather than agreeing by inspection.</summary>
    private Complex P(int cellA, int cellB) => _pulse.At(cellA, cellB);
}

/// <summary>
/// <b>P11 — D4's <c>P[a, b]</c>: the area-averaged scalar-potential coefficient of ONE CELL PAIR,
/// on demand.</b> Carved out of <see cref="PlanarEntryFill"/> unchanged, because
/// <see cref="PlanarStaticAim"/> needs exactly this operator and nothing else of a fill: the static
/// capacitance system is <c>P q = ε₀·1</c> over CELLS, with no vector block, no ω and no basis
/// functions in it at all.
///
/// <para>The memo shape is P5's, unchanged: keyed by TRANSLATION CLASS for a classifiable pair and
/// per pair for one with a cut cell, so a near field asks for a class once however many pairs are
/// its translates.</para>
///
/// <para>Thread-safe on the same terms <see cref="PlanarEntryFill"/> is: every field is read-only
/// after construction and both caches are <see cref="ConcurrentDictionary{TKey,TValue}"/>s whose
/// value factories are pure functions of the key.</para>
/// </summary>
public sealed class PlanarPulsePotential
{
    private readonly PlanarEntryCores   _g;
    private readonly PlanarMesh         _mesh;
    private readonly PlanarFillSettings _st;
    private readonly PlanarKernelTerms  _termsQ;
    private readonly Func<double, Complex> _remQ;

    private readonly ConcurrentDictionary<long, Complex>       _remQ1 = new();
    private readonly ConcurrentDictionary<(int, int), Complex> _pCut  = new();

    /// <summary>How many distinct translation CLASSES this has integrated, plus any per-pair entries
    /// for pairs with a cut cell.</summary>
    public int CellPairCount => _remQ1.Count + _pCut.Count;

    /// <summary>The frequency-independent cores this reads.</summary>
    public PlanarEntryCores Geometry => _g;

    /// <summary>The re-floored scalar terms this evaluates — read by the static accelerator, which
    /// has to build its grid kernel table from the SAME floored terms the near entries use.</summary>
    public PlanarKernelTerms Terms => _termsQ;

    /// <param name="remainder">
    /// <b>P12 — the remainder evaluator, when the caller already has the one the dense path built.</b>
    /// Null (the default) derives it here exactly as the dense path does. The ẑẑ block is the one
    /// caller that supplies its own: under <c>PlanarFillSettings.DirectVerticalKernel</c> its terms
    /// are ALREADY a radial table (<c>ViaZIntegral.AveragedTermsDirect</c>) and re-tabulating them
    /// would interpolate an interpolation — which is the reason the dense fill's <c>ZzTerms</c> makes
    /// the same distinction.
    /// </param>
    public PlanarPulsePotential(PlanarEntryCores geometry, PlanarKernelTerms termsQ,
                                Func<double, Complex>? remainder = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(termsQ);

        _g    = geometry;
        _mesh = geometry.Mesh;
        _st   = geometry.Settings;

        // The dense path re-floors the terms for this mesh inside ScalarPotentialMatrix, so this does
        // the same rather than trusting the caller to have done it.
        _termsQ = termsQ.With(_st.Order, geometry.Cores.RhoFloorM);
        _remQ   = remainder ?? PlanarFill.RemainderOf(_termsQ, geometry.Cores);
    }

    /// <summary><c>P[cellA, cellB]</c>, symmetric by construction.</summary>
    public Complex At(int cellA, int cellB)
    {
        int a = Math.Min(cellA, cellB), b = Math.Max(cellA, cellB);
        var ok = _g.Classifier.Classifiable;
        if (!ok[a] || !ok[b])
            return _pCut.GetOrAdd((a, b), static (key, self) => self.ComputeP(key.Item1, key.Item2), this);

        long key = _g.Classifier.Key(_mesh.Cells[a], _mesh.Cells[b], _st, out _);
        var core = _g.ClassCores(key).Pulse;
        Complex v = _termsQ.Inverse * core.Inverse + _termsQ.Log * core.Log;
        if (_termsQ.ExtractsConstant) v += _termsQ.Constant;          // area-normalised ⇒ core = 1
        if (_termsQ.ExtractsLinear)   v += _termsQ.Linear * core.Radius;
        v += _remQ1.GetOrAdd(key, static (k, self) =>
        {
            var (oa, ob) = self._g.Classifier.Representative(k);
            return PlanarFill.CellPairPulseRemainder(oa, ob, PairClassifier.RemainderNodes(k, self._st), self._remQ);
        }, this);
        return v;
    }

    /// <summary>Warms this pair's singular cores without evaluating the frequency-dependent
    /// remainder — the pulse half of <see cref="PlanarEntryCores.Prepare"/>, for a near set built
    /// over CELLS rather than over bases.</summary>
    internal void PrepareCells(int cellA, int cellB) => _g.PrepareScalarPair(cellA, cellB);

    private Complex ComputeP(int a, int b)
    {
        var wa = PlanarFill.PulseAt(_mesh, a);
        var wb = PlanarFill.PulseAt(_mesh, b);
        var (c0, cl, cr) = _g.CutScalarCores(a, b);

        Complex v = _termsQ.Inverse * c0 + _termsQ.Log * cl;
        if (_termsQ.ExtractsConstant) v += _termsQ.Constant;          // area-normalised ⇒ core = 1
        if (_termsQ.ExtractsLinear)   v += _termsQ.Linear * cr;
        v += PlanarFill.PairRemainderOf(_mesh, wa, wb, PlanarBasisDirection.X, _remQ, _st);
        return v;
    }
}

/// <summary>
/// Gauss-Legendre nodes and weights, <b>computed</b> by Newton on the Legendre recurrence rather than
/// tabulated — the same rule L8a followed for its own nodes and its Bessel functions, and for the
/// same recorded reason (§8.3: tables of constants are exactly what D4 forbids taking from memory,
/// and the recurrence is three lines). <see cref="SommerfeldIntegral"/> has its own private copy; it
/// is not widened here because the two have different lifetimes and the duplication is nine lines.
/// </summary>
internal static class Legendre
{
    // A ConcurrentDictionary, not a lock: Nodes() is called once per CELL PAIR, so a mutex here
    // serialises the entire parallel fill and the speed-up disappears.
    private static readonly ConcurrentDictionary<int, (double[] X, double[] W)> Cache = new();

    public static (double[] X, double[] W) Nodes(int n) => Cache.GetOrAdd(n, Compute);

    private static (double[] X, double[] W) Compute(int n)
    {
        {
            var x = new double[n];
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double z = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5)), pp = 0;
                for (int it = 0; it < 200; it++)
                {
                    double p0 = 1, p1 = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double p2 = p1;
                        p1 = p0;
                        p0 = ((2 * j + 1) * z * p1 - j * p2) / (j + 1);
                    }
                    pp = n * (z * p0 - p1) / (z * z - 1);
                    double dz = p0 / pp;
                    z -= dz;
                    if (Math.Abs(dz) < 1e-16) break;
                }
                x[i] = z;
                w[i] = 2.0 / ((1 - z * z) * pp * pp);
            }
            return (x, w);
        }
    }
}
