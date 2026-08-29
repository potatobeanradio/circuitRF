// M5 (brief-em-sweep-performance) — THE ADAPTIVE INTEGRAL METHOD, on this kernel's own operator.
//
// ── What this is, and what the decision gate already settled ──────────────────────────────────────
//
// The brief's own correction is the starting point and it is worth repeating, because the usual
// framing is wrong here: "they use a uniform grid, so the matrix is block-Toeplitz and the product is
// an FFT" needs TRANSLATION INVARIANCE, and this mesher's grid is edge-graded and conformally cut. It
// is not uniform even in the interior. Building on raw Toeplitz would mean giving up edge grading and
// cut cells — i.e. giving up exactly what buys accuracy at low N (L8b: 4.437% -> 0.431% for 3.6x the
// unknowns, and the uniform ladder needs ~20x the unknowns to catch it).
//
// AIM projects the ARBITRARY basis functions onto a SEPARATE uniform auxiliary grid, does the far
// field by FFT on that grid, and keeps a sparse near-field correction computed EXACTLY. The mesh stays
// graded and conformal; only the auxiliary grid is uniform.
//
// `src/Engine/Mom/CLAUDE.md` §11 is the decision gate and it is RUN: with an 8-cell near-field
// preconditioner GMRES's iteration count is FLAT — 3 -> 6 to a 1e-6 residual over 6.7x N, on the
// shipping mesh and a 2-D conductor — the near field is genuinely O(N), and Jacobi is worthless. That
// answered the only question that could have stopped this. It did NOT establish the projection's own
// accuracy, which is R-emp-16, and which is what the gates on this file measure.
//
// ── THE OPERATOR BEING ACCELERATED (and why it needs THREE projections, not one) ───────────────────
//
// L8c's mixed-potential entry is two blocks with two different kernels:
//
//     Z[m,n] = jωµ₀ ⟨f_m , G_A f_n⟩          — same direction only (D5)
//            + 1/(jωε₀) ⟨∇·f_m , G_q ∇·f_n⟩  — every pair (D4)
//
// So the accelerator projects three densities per basis onto the same grid: the x̂ current, the ŷ
// current, and the CHARGE ∇·f (the ±1/Area pulse pair). The far field is then two FFT convolutions
// with G_A (one per current component, the same kernel) and one with G_q. Projecting the current
// without its divergence would accelerate half the operator and quietly leave the other half dense.
//
// ── THE MOMENT MATCH, and why the stencil is a TENSOR square ───────────────────────────────────────
//
// Each basis is replaced, for far-field purposes, by point sources on an (M+1)×(M+1) block of grid
// nodes carrying the SAME multipole moments up to tensor order M:
//
//     Σ_kl λ_kl ξ_k^a η_l^b = ∫ w(r) (x−x_s)^a (y−y_s)^b dS      for 0 ≤ a,b ≤ M
//
// The classic AIM matches a+b ≤ M on an (M+1)^d stencil, which is underdetermined and needs a
// minimum-norm solve. The full TENSOR set is square, is uniquely solvable by two (M+1)×(M+1)
// Vandermonde inversions, and matches strictly MORE moments for the same node count — so it is what is
// built. On a uniform grid every basis shares the same ξ, so the inverse is computed ONCE.
//
// **The moments are taken through the FILL'S OWN weight evaluation** (`PlanarFill.WeightNodes`), cut
// cells, strips and all. A projection built on a second reading of what a rooftop is would approximate
// a different operator, and the residual would look like an accuracy floor with no cause.
//
// ── THE ONE TRAP: G(0) IS ARBITRARY, AND THAT IS ONLY TRUE IF THE NEAR SET SAYS SO ────────────────
//
// The grid kernel needs a value at zero separation, where 1/ρ is infinite. Any finite value works —
// but ONLY because every pair whose two stencils OVERLAP is in the near set, where the AIM value is
// subtracted off exactly. Get the near set slightly wrong and the answer depends on a number that was
// picked arbitrarily, which is the worst kind of dependence: smooth, plausible and unattributable.
//
// So the near set is deliberately the UNION of two criteria — a radius AND stencil overlap — rather
// than a radius alone chosen to be "wide enough". `Gate_SelfKernelSentinelDoesNotMoveTheAnswer`
// asserts it by moving the sentinel and demanding the product not move.
//
// ── WHAT IS DELIBERATELY NOT HERE ─────────────────────────────────────────────────────────────────
//
// The MULTI-LEVEL / via path (L9c/L9d). A vertical basis's current is ẑ-directed, its kernel is
// G_A^zz plus a MIXED ẑx̂ component whose dyadic entry is a ∂/∂x rather than a value, and its sources
// sit at a different height — which is a different Toeplitz kernel per height pairing and a projection
// with a derivative in it. That is a second phase, not a widening; `PlanarAimGeometry.Build` refuses
// it by name rather than producing a plausible number for a structure it does not model.
//
// ── P6 (brief-em-p6-aim-frequency-independent-state.md, 2026-08-29): TWO OBJECTS, NOT ONE ────────
//
// `PlanarAimGeometry` is built ONCE per mesh: the auxiliary grid, every stencil, the near set as CSR,
// its mirror, and — through `PlanarEntryCores` — the singular cores of every near pair, warmed over the
// near set at build. `PlanarAimOperator` is built per FREQUENCY over it and holds only what carries ω:
// the grid kernel tables and their FFT hats, the remainders and assembly of the near entries, the AIM
// correction, and the sparse LU. Before P6 one object did both at every frequency, so the near
// field's clustered-panel singular quadrature ran once per frequency where the dense path runs it
// once per mesh (D6); `PlanarEntryCores.CorePasses` is the counter that says it no longer does.

using System.Collections.Concurrent;
using System.Numerics;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using CSparse.Ordering;
using CSparse.Storage;
using FftFlat;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// M5's knobs. <b>Every default here is measured rather than chosen</b> — see
/// <c>AimAccuracyTests</c> for the projection-order × near-radius table R-emp-17 asks for, and read it
/// before moving one.
/// </summary>
/// <param name="ProjectionOrder">
/// <b>M</b> — the stencil is <c>(M+1)×(M+1)</c> nodes and the match is every monomial
/// <c>x^a y^b</c> with <c>a, b ≤ M</c>. 0 is a monopole-only projection and is useless past a few
/// cells; the cost is <c>(M+1)^4</c> kernel lookups per near pair, so it grows fast.
/// </param>
/// <param name="GridSpacingFactor">
/// The auxiliary grid pitch, as a multiple of the LARGEST basis support. <b>Not of the median cell,
/// and the difference is the whole point on a graded mesh</b>: the shipping mesh's cell spread is
/// 8.3×, and a pitch set from the median would leave the bulk bases much larger than their own
/// stencils. Sized from the largest support, every basis fits inside its stencil by construction.
///
/// <para><b>THE DEFAULT IS 0.5 AND THE MEASUREMENT IS WHY.</b> R-emp-17 names the order and the
/// radius as the free parameters; the N ladder found a third, and it is the dominant one. The
/// stencil has to resolve the KERNEL across its own width, not merely enclose the basis — and at a
/// pitch of one whole support the stencil spans 0.235 λ_g, across which e^{−jk₀ρ} and every surface
/// wave turn appreciably. Measured on the 64 mm hero at 6 GHz, holding the near radius fixed IN
/// METRES so the cost is the control: <c>0.235 λ_g → 5.69e-4</c>, <c>0.117 λ_g → 5.52e-6</c>,
/// <c>0.059 λ_g → 8.72e-7</c> in the solved current vector, for the SAME near-field entry count and
/// the same build time. <b>A finer auxiliary grid is very nearly free here</b> — it costs grid nodes
/// and one FFT over them, and no near-field arithmetic at all.</para>
/// </param>
/// <param name="NearRadiusFactor">
/// The exact near field's radius, as a multiple of the LARGEST BASIS SUPPORT — deliberately the same
/// unit as the pitch rather than a multiple of the pitch itself, so that the two knobs are
/// INDEPENDENT. Expressed in pitches, halving the pitch would silently halve the near field and every
/// pitch measurement would be a radius measurement in disguise.
///
/// <para>The near set is the UNION of this radius and stencil overlap, so correctness does not depend
/// on it — accuracy and cost do, and it is the knob that costs.</para>
///
/// <para><b>P8 — it is a factor, not the radius. The radius is
/// <c>max(NearRadiusFactor·maxSpan, NearRadiusMinM)</c></b>, and that floor is the whole of P8; see
/// <see cref="NearRadiusMinM"/> for why a radius measured in supports alone breaks on a refined
/// mesh.</para>
/// </param>
/// <param name="NearRadiusMinM">
/// <b>P8 — a floor on the near radius IN METRES, because the physics has a length of its own and the
/// basis support is not it.</b> Null (the default) derives it as
/// <see cref="DerivedNearRadiusImageDepths"/>·h from the slab height handed to
/// <see cref="PlanarAimGeometry.Build"/>; a positive value overrides that; 0 disables the floor and
/// restores the pre-P8 behaviour, which is what the P8 ladder's "before" column runs.
///
/// <para><b>Why 2h, derived rather than picked.</b> The scalar (charge) kernel over a grounded slab
/// is <c>1/ρ − 1/√(ρ² + 4h²)</c> plus smooth terms: the conductor's charge and its image at depth
/// 2h very nearly cancel, and beyond ρ ≈ 2h the residue falls like <c>2h²/ρ³</c> rather than like
/// <c>1/ρ</c>. <b>2h is therefore where the coupling stops being long-ranged</b>, and a near field
/// narrower than it is missing the dominant coupling — both in the preconditioner GMRES is steered
/// by, which is what makes the iteration count climb, and in the OPERATOR, because the radius is
/// also the boundary between the entries computed exactly and the entries the projection
/// approximates. Measured against the dense solve on the identical mesh: <c>|ΔI|</c> is 4.90e-7 at
/// the shipping cells/λ = 20 and 5.93e-4 at cells/λ = 140, of which the floor recovers 17×.</para>
///
/// <para><b>And a radius measured in supports walks straight into it.</b> Refining the mesh at a
/// fixed footprint shrinks the largest basis support, so the shipped 6 supports shrink in metres too:
/// on the FR-4 hero cross-section it is 8.9h at the shipping cells/λ = 20 and 1.28h at cells/λ = 140.
/// Measured on a 16 mm line at 6 GHz, GMRES's iteration count over cells/λ 20…140 goes
/// <c>2, 4, 6, 12, 46, 144, 273</c> without the floor and <c>2, 4, 6, 12, 14, 10, 10</c> with it —
/// and the same ladder at 64 mm did not converge at all at its top rung
/// (<c>HISTORY.md</c>'s A1b table). Growing the geometry at fixed resolution never triggers it,
/// which is why the length ladder never saw this.</para>
/// </param>
/// <param name="Tolerance">GMRES's relative residual target. L8c puts the fill's own accuracy at
/// 5.0e-6 against an independent oracle and L8d measured de-embedding amplifying a raw-S error ~22×,
/// so a solve stopped at 1e-4 has thrown the fill away.</param>
/// <param name="StaticTolerance">
/// <b>P11 — GMRES's relative residual target for the STATIC capacitance solve</b>
/// (<see cref="PlanarStaticAim"/>), which is a different system with a different error budget and
/// therefore does not share <see cref="Tolerance"/>.
///
/// <para><b>Tighter, and the differencing is why.</b> D7's C_pul is
/// <c>(C₂ − C₁)/Δℓ</c>: the two standards are the same cross-section and differ only in the bulk
/// cells between the reference planes, so their totals agree to several digits and an error in
/// either is amplified by <c>C/(C₂ − C₁)</c> in the answer. The DUT's residual is sized for a
/// current vector read directly; this one is sized for a difference.</para>
///
/// <para><b>1e-10, and the ladder is why.</b> Against the dense solve on the FR-4 hero's own
/// 30.8 mm / 90.9 mm standards, the differenced C_pul error is <c>9.88e-8</c> at a tolerance of
/// 1e-6 and <c>1.04e-7</c> at 1e-8, 1e-10, 1e-12 and 1e-14 alike — <b>identical in every printed
/// digit from 1e-8 down</b>, because what is left at that point is the PROJECTION's error and not
/// the solve's. So the tolerance is not what limits this; it is set two decades under where the
/// answer stopped moving, so that a standard pair whose lengths are close (a larger
/// <c>C/(C₂ − C₁})</c> than the 1.5-1.9 those fixtures measure) still has the solve's own
/// contribution well under the projection's. It is not set tighter than that on purpose: a
/// tolerance GMRES cannot reach on an ill-conditioned system turns into a REFUSAL, and
/// 1e-12 was reachable on these fixtures in one extra iteration but is not a promise.</para>
/// </param>
/// <param name="MaxIterations">A cap, not a target. Reaching it throws rather than returning a
/// half-converged current distribution that would produce a smooth, plausible, wrong s-parameter.</param>
/// <param name="Restart">GMRES restart length; 0 is full GMRES. Full is what §11 measured, and at the
/// single-digit iteration counts it reports there is nothing to restart.</param>
/// <param name="SelfKernelFactor">
/// Where the grid kernel's value at zero separation is taken from, as a fraction of the pitch. <b>It
/// is arbitrary and it must stay arbitrary</b> — see the file header. Exposed only so the gate can
/// move it and assert the answer does not.
/// </param>
/// <param name="KeepNearExact">
/// <b>P1 — whether the near set's EXACT entries stay live after the preconditioner has been factored
/// from them.</b> They are read by exactly two things: <c>FactorNear</c>, which runs once at build
/// time, and the <see cref="PlanarAimOperator.NearExactAt"/> diagnostic. The accelerated PRODUCT uses
/// the correction (exact − AIM), never the exact, so holding both for the life of the operator was
/// 16 B per near entry of pure diagnostic weight — 27 MB at N = 12,894, on a working set the whole
/// point of which is to be small.
///
/// <para>Default false: the array is released the moment the factorisation is done. Set it true in a
/// test that reads <see cref="PlanarAimOperator.NearExactAt"/>; that method says so by name rather
/// than returning a silent zero. (Reading the entries back out of CSparse's CSC instead was the other
/// option and is worse: the CSC's own row index costs 4 B per entry MORE than the array it would
/// replace, for the same numbers.)</para>
/// </param>
public sealed record PlanarAimSettings(
    int     ProjectionOrder   = 3,
    double  GridSpacingFactor = 0.5,
    double  NearRadiusFactor  = 6.0,
    double  Tolerance         = 1e-8,
    int     MaxIterations     = 400,
    int     Restart           = 0,
    double  SelfKernelFactor  = 0.5,
    bool    KeepNearExact     = false,
    double? NearRadiusMinM    = null,
    double  StaticTolerance   = 1e-10)
{
    public static readonly PlanarAimSettings Default = new();

    /// <summary><b>P8 — the derived near-radius floor, in slab heights: 2, i.e. the image depth.</b>
    /// Named rather than written as a literal because the number is the depth of the ground plane's
    /// image and not a tuning constant — see <see cref="NearRadiusMinM"/> for the derivation and for
    /// the ladder that measured it.</summary>
    public const double DerivedNearRadiusImageDepths = 2.0;

    /// <summary>The floor this settings object asks for on a slab of height <paramref name="slabHeightM"/>
    /// — the explicit <see cref="NearRadiusMinM"/> when it is set, otherwise
    /// <see cref="DerivedNearRadiusImageDepths"/>·h.</summary>
    public double NearRadiusFloorFor(double slabHeightM) =>
        NearRadiusMinM ?? DerivedNearRadiusImageDepths * slabHeightM;

    /// <summary>Refuse a setting whose bad case is a complete, plausible, wrong answer — the same rule
    /// <see cref="PlanarFillSettings.Validate"/> is written to.</summary>
    public void Validate()
    {
        if (ProjectionOrder < 0 || ProjectionOrder > 6)
            throw new ArgumentOutOfRangeException(nameof(ProjectionOrder), ProjectionOrder,
                "The projection order is the stencil's tensor order; below 0 it has no stencil and " +
                "above ~6 the Vandermonde inversion is the error rather than the projection.");
        if (!(GridSpacingFactor > 0))
            throw new ArgumentOutOfRangeException(nameof(GridSpacingFactor), GridSpacingFactor,
                "The auxiliary grid needs a positive pitch.");
        if (!(NearRadiusFactor >= 0))
            throw new ArgumentOutOfRangeException(nameof(NearRadiusFactor), NearRadiusFactor,
                "A negative near radius would leave the near set to stencil overlap alone.");
        if (!(Tolerance > 0) || Tolerance >= 1)
            throw new ArgumentOutOfRangeException(nameof(Tolerance), Tolerance,
                "The GMRES tolerance is a relative residual in (0, 1).");
        if (!(StaticTolerance > 0) || StaticTolerance >= 1)
            throw new ArgumentOutOfRangeException(nameof(StaticTolerance), StaticTolerance,
                "The static solve's GMRES tolerance is a relative residual in (0, 1).");
        if (MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), MaxIterations,
                "An iteration cap below 1 runs no iterations and returns the zero vector.");
        if (Restart < 0)
            throw new ArgumentOutOfRangeException(nameof(Restart), Restart,
                "Use 0 for full (non-restarted) GMRES.");
        if (NearRadiusMinM is { } min && !(min >= 0))
            throw new ArgumentOutOfRangeException(nameof(NearRadiusMinM), min,
                "The near-radius floor is a length in metres; 0 disables it and null derives it from " +
                "the slab. A negative floor would silently mean the same as 0 rather than saying so.");
        if (!(SelfKernelFactor > 0))
            throw new ArgumentOutOfRangeException(nameof(SelfKernelFactor), SelfKernelFactor,
                "The self-kernel sentinel is evaluated at this fraction of the grid pitch and must be " +
                "strictly positive, or the kernel is asked for its own singularity.");
    }
}

/// <summary>What the accelerator cost and how big it turned out to be — the numbers R-emp-17's table
/// and the cost gate report, taken from the object rather than re-derived by a test.</summary>
/// <param name="PreconditionerNonZeros">
/// <b>The NEAR MATRIX's non-zero count, not the factor's</b> — <c>csc.NonZerosCount</c>, which (each
/// near pair being stored exactly once) is <see cref="NearEntries"/> a second time. The name predates
/// P1 and reads like the fill-in; it is kept because the existing R-emp-17 tables print it under this
/// name. <see cref="FactorNonZeros"/> is the fill-in.
/// </param>
/// <param name="FactorNonZeros">
/// <b>P1</b> — <c>SparseLU.NonZerosCount</c>, L and U together, or 0 when the factorisation failed and
/// GMRES is running unpreconditioned. At the accelerated ceiling this is nearly half of everything the
/// operator holds, and until P1 nothing counted it.
/// </param>
/// <param name="NearCellPairs">
/// <b>P5</b> — how many distinct translation CLASSES the near field's scalar block integrated (plus
/// any per-pair entries for pairs with a cut cell), i.e. what the on-demand memo actually computed.
/// Until P5 this counted distinct cell pairs; a class serves every pair that is its translate, so the
/// number is now a small fraction of those.
/// </param>
/// <param name="NearExactRetained">
/// <b>P1</b> — whether the near set's exact entries are still live
/// (<see cref="PlanarAimSettings.KeepNearExact"/>). They are released after the preconditioner is
/// factored from them unless a diagnostic asked for them, and <see cref="ResidentBytes"/> charges for
/// them exactly when they are held.
/// </param>
public sealed record PlanarAimReport(
    int    UnknownCount,
    int    GridNodesX,
    int    GridNodesY,
    double GridPitchM,
    int    ProjectionOrder,
    double NearRadiusM,
    long   NearEntries,
    int    NearCellPairs,
    long   PaddedGridNodes,
    double ProjectionMs,
    double GridKernelMs,
    double RemainderTableMs,
    double NearFillMs,
    double PreconditionerMs,
    long   PreconditionerNonZeros,
    long   FactorNonZeros = 0,
    bool   NearExactRetained = false,
    double NearRadiusFromSupportM = 0,
    double NearRadiusFloorM = 0,
    double GeometryMs = 0,
    double NearSetMs = 0,
    double NearCoreMs = 0,
    double NearRemainderMs = 0,
    double CorrectionMs = 0,
    double LowerCopyMs = 0,
    long   GeometryBytes = 0,
    int    NearCoreClasses = 0)
{
    /// <summary><b>P6 — what one FREQUENCY costs to build</b>, the geometry excluded: grid kernel,
    /// near remainders and assembly, AIM correction, and the sparse LU. The radial remainder tables
    /// are excluded for the reason <see cref="RemainderTableMs"/> gives.</summary>
    public double PerFrequencyMs => GridKernelMs + NearRemainderMs + CorrectionMs + LowerCopyMs + PreconditionerMs;

    /// <summary>Near entries as a fraction of the dense matrix — the number that says whether the
    /// near field is genuinely O(N) or merely a smaller O(N²).</summary>
    public double NearFillFraction => (double)NearEntries / UnknownCount / UnknownCount;

    /// <summary>Entries per row. §11's own near-8c rows hold this essentially constant (227 -> 273)
    /// while the FRACTION falls 72% -> 13%, and it is the per-row number that carries the claim.</summary>
    public double NearEntriesPerRow => (double)NearEntries / Math.Max(1, UnknownCount);

    /// <summary>
    /// <b>Bytes the accelerator holds once it is built</b>, against
    /// <see cref="PlanarSystem.ResidentBytes"/> for the dense path — which is the comparison M5 is
    /// actually won on, R17 being a memory ceiling rather than a time one.
    ///
    /// <para><b>Renamed from <c>ApproximateBytes</c> at P1 (2026-08-29), because it no longer
    /// approximates anything</b> — every term below is an array this class allocated, at its actual
    /// element size. It had omitted three things: the sparse LU's own factors (its fill-in is several
    /// times the near set it was built from — see <see cref="FactorNonZeros"/>), CSparse's CSC copy of
    /// the near matrix, and, until P1 freed it, <c>_nearExact</c>.</para>
    ///
    /// <list type="bullet">
    /// <item>the near set's values (exact, when retained, and correction) at 16 B;</item>
    /// <item>the two grid kernel tables, and the five padded FFT arrays (two transformed kernels and
    /// three scratch fields) — NOT negligible at a fine pitch, and exactly what a "the near field is
    /// only 10% of the matrix" reading forgets;</item>
    /// <item><b>P6: the geometry</b> (<see cref="GeometryBytes"/>) — the per-basis stencils at
    /// 16·(M+1)²·N (P6 stores them as <c>double</c>; they were 32 as <c>Complex</c>), the CSR column
    /// index at 4 B per near entry, the row pointer, and the near cores' store
    /// (168 B per translation class plus its index node) — held once per mesh, shared by every
    /// frequency's operator, and counted here because one operator's working set includes it;</item>
    /// <item>the preconditioner's L and U together (<see cref="FactorNonZeros"/> entries at 16 B of
    /// value plus 4 B of row index, plus two column pointers of 4·(N+1)), and the AMD permutation.</item>
    /// </list>
    ///
    /// <para>The CSC copy the factorisation is built FROM is transient — it is released with the
    /// factorisation's own scratch — so it is not counted here; <c>HISTORY.md</c>'s P1 table measures
    /// the build's peak, which does hold it.</para>
    /// </summary>
    public long ResidentBytes =>
        (NearExactRetained ? 32L : 16L) * NearEntries        // correction (+ exact, when retained)
      + 16L * GridNodesX * GridNodesY * 2
      + 16L * PaddedGridNodes * 5
      + 20L * FactorNonZeros + 8L * (UnknownCount + 1)       // the sparse LU's L and U
      + 8L * UnknownCount                                    // AMD permutation + its inverse
      + GeometryBytes;                                       // P6: stencils, CSR index, cores

    /// <summary>P6 — the per-frequency operator's own arrays alone: <see cref="ResidentBytes"/>
    /// less the geometry a sweep holds once for all of them.</summary>
    public long PerFrequencyBytes => ResidentBytes - GeometryBytes;

    /// <summary>Bytes the accelerator's BUILD peaks at — <see cref="ResidentBytes"/> plus the
    /// transient CSC copy of the near matrix that <c>FactorNear</c> hands to CSparse (values at 16 B,
    /// row index at 4 B, column pointer at 4·(N+1)).</summary>
    public long PeakBuildBytes =>
        ResidentBytes + 20L * NearEntries + 4L * (UnknownCount + 1);
}

/// <summary>
/// One basis's projection onto the auxiliary grid: where its stencil sits, and the coefficients that
/// reproduce its moments there. <b>P6: the coefficients are <c>double</c></b> — they always were real
/// (a moment match of a real weight against real monomials), and storing them as <c>Complex</c> was
/// 16 B per stencil node of zeros. The products they enter are bit-for-bit what the complex form
/// gave: <c>(a + 0i)·z</c> and <c>a·z</c> take the same two multiplies.
/// </summary>
internal sealed class AimStencil
{
    /// <summary>Lower-left grid node index of the <c>(M+1)×(M+1)</c> block.</summary>
    public required int P0 { get; init; }
    public required int Q0 { get; init; }

    /// <summary>Current-density coefficients, row-major over the stencil. The basis has exactly one
    /// flow direction, so there is one of these and the direction says which grid field it lands
    /// in.</summary>
    public required double[] Current { get; init; }

    /// <summary>Charge coefficients — <c>∇·f</c>'s moments, which are what the scalar block sees.</summary>
    public required double[] Charge { get; init; }

    public required PlanarBasisDirection Direction { get; init; }
}

/// <summary>
/// <b>P6 — everything the accelerator holds that does not depend on frequency, built once per
/// mesh.</b> The auxiliary grid (origin, pitch, extent), every basis's stencil, the near set as CSR,
/// the mirror index that makes the lower triangle a copy of the upper, and — through
/// <see cref="EntryCores"/> — the singular cores of every near pair, warmed over the near set here so
/// that no frequency ever runs a singular quadrature.
///
/// <para>Until P6 <see cref="PlanarAimOperator"/> rebuilt all of this at every frequency: the
/// projection, the near set, and (decisively) the near fill's clustered-panel singular cores, which
/// the dense path computes once per mesh (D6). A <see cref="PlanarSolveContext"/> with
/// <see cref="PlanarFillSettings.Aim"/> set now builds one of these beside its geometry-only cores
/// and hands it to every <see cref="PlanarAimOperator.Build(PlanarAimGeometry, PlanarKernelTerms,
/// PlanarKernelTerms, double)"/>.</para>
///
/// <para>Read-only after construction, so one geometry can serve concurrent operators; the core
/// store's own insertions are the one mutation and they are idempotent.</para>
/// </summary>
public sealed class PlanarAimGeometry
{
    internal readonly int N, M, Side, Nx, Ny, Px, Py;
    internal readonly int Nz;
    internal readonly double H;
    internal readonly AimStencil[] Stencils;
    internal readonly int[] RowPtr, ColIdx;

    public PlanarFillCores   Cores      { get; }
    public PlanarAimSettings Settings   { get; }

    /// <summary>The slab height this geometry's near-radius floor was derived from (P8).</summary>
    public double SlabHeightM { get; }

    /// <summary><b>The near field's radius, in metres, as actually applied</b> —
    /// <c>max(NearRadiusFromSupportM, NearRadiusFloorM)</c>.</summary>
    public double NearRadiusM { get; }

    /// <summary>What <see cref="PlanarAimSettings.NearRadiusFactor"/> alone asked for: 6 largest basis
    /// supports at the shipped default. Below <see cref="NearRadiusFloorM"/> exactly when the floor
    /// bound, which is the one number that says whether P8 changed this mesh at all.</summary>
    public double NearRadiusFromSupportM { get; }

    /// <summary><b>P8's floor</b> — <see cref="PlanarAimSettings.NearRadiusFloorFor"/> on this
    /// geometry's slab.</summary>
    public double NearRadiusFloorM { get; }

    /// <summary>Whether the floor is what set the radius on this mesh.</summary>
    public bool NearRadiusIsFloored => NearRadiusFloorM > NearRadiusFromSupportM;

    /// <summary>The singular cores of every near pair, and the counter that says they were
    /// computed once.</summary>
    public PlanarEntryCores  EntryCores { get; }

    public int    UnknownCount => N;

    /// <summary>
    /// <b>P12 — how many HORIZONTAL unknowns this geometry projects</b>, i.e. the size of the block
    /// AIM accelerates. Equal to <see cref="UnknownCount"/> by definition; named separately because
    /// on a via-bearing mesh it is a PREFIX of the mesh's unknowns rather than all of them, and every
    /// index arithmetic in <see cref="PlanarBorderedAimOperator"/> turns on that.
    /// </summary>
    public int HorizontalCount => N;

    /// <summary>
    /// <b>P12 — how many ẑ-directed (via) unknowns follow the horizontal prefix.</b> Zero on a
    /// single-level mesh, which is what <see cref="PlanarAimOperator"/> requires. These are NOT
    /// projected: they are the dense border of <see cref="PlanarBorderedAimOperator"/>'s system.
    /// </summary>
    public int VerticalCount => Nz;

    /// <summary>The mesh's own unknown count — <c>HorizontalCount + VerticalCount</c>.</summary>
    public int TotalUnknowns => N + Nz;
    public long   NearEntries  => ColIdx.LongLength;
    public double GridPitchM   => H;
    public int    GridNodesX   => Nx;
    public int    GridNodesY   => Ny;
    public long   PaddedGridNodes => (long)Px * Py;

    /// <summary>The three phases of the build, so a sweep can see what it paid once.</summary>
    public double ProjectionMs { get; }
    public double NearSetMs    { get; }
    public double NearCoreMs   { get; }
    public double TotalMs => ProjectionMs + NearSetMs + NearCoreMs;

    /// <summary>Bytes this holds: the stencils at <c>16·(M+1)²</c> per basis, the CSR column index
    /// at 4 B per near entry, the row pointer, and the core store.
    ///
    /// <para><b>The mirror index is deliberately NOT here</b>, although the brief listed it. It is
    /// 4 B per near entry — 18.2 MB at N = 11,959, which took the accelerated working set from
    /// 196.2 MB to 214 MB, over the "under 200 MB at the ceiling" line §8 states — to save a rebuild
    /// that is a binary search per lower-triangle entry and costs tens of milliseconds per frequency
    /// (measured with the near set: 89 ms at N = 11,959, against a 3.8 s point). The operator finds
    /// each transpose position inline instead (<see cref="PlanarAimOperator"/>'s lower-triangle copy).</para>
    /// </summary>
    public long Bytes =>
        16L * Side * Side * N
      + 4L * ColIdx.LongLength + 4L * (N + 1)
      + EntryCores.Bytes;

    /// <summary>
    /// Builds the geometry for one mesh. <paramref name="cores"/> may be — and for the cost claim to
    /// mean anything SHOULD be — <see cref="PlanarFill.BuildGeometryOnlyCores"/>' O(N) shape.
    ///
    /// <para><b>P12 — a via-bearing mesh is no longer refused here, and the reason it once was is
    /// worth keeping straight.</b> The refusal was about PROJECTING a ẑ basis, which is still not
    /// done: this projects the HORIZONTAL PREFIX only (R-via-5 guarantees it is a prefix), and the
    /// ẑ unknowns become <see cref="PlanarBorderedAimOperator"/>'s dense border. What IS asserted
    /// here is that ordering — a mesh whose vertical bases are interleaved would give a projection
    /// silently indexed onto the wrong basis.</para>
    /// </summary>
    public static PlanarAimGeometry Build(PlanarFillCores cores, double slabHeightM,
                                          PlanarAimSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(cores);
        var st = settings ?? PlanarAimSettings.Default;
        st.Validate();

        // P8 — the slab height is REQUIRED rather than defaulted, because the failure it guards
        // against is silent: a geometry built with no h would take the pre-P8 near radius and produce
        // a complete, plausible answer that GMRES merely takes 20x longer to reach (or does not reach
        // at all, on a refined mesh). A caller that genuinely wants no floor says so in the settings.
        if (!(slabHeightM > 0))
            throw new ArgumentOutOfRangeException(nameof(slabHeightM), slabHeightM,
                "The near radius has a floor of 2h (PlanarAimSettings.NearRadiusMinM) and h is the " +
                "slab height, so it has to be handed in. Pass the problem's own Slab.HeightM; to run " +
                "without the floor, pass the height anyway and set NearRadiusMinM: 0.");

        // R-via-5 — every horizontal basis before every vertical one. The mesher produces that
        // ordering and three tests already assert it; this is the accelerator's own check, because
        // what it costs to be wrong here is not an exception but a projection indexed onto a basis
        // that is not the one it was built for.
        var bases = cores.Mesh.Bases;
        int nh = 0;
        while (nh < bases.Count && bases[nh].Direction != PlanarBasisDirection.Z) nh++;
        for (int i = nh; i < bases.Count; i++)
            if (bases[i].Direction != PlanarBasisDirection.Z)
                throw new NotSupportedException(
                    $"Basis {i} is horizontal but follows the vertical basis at {nh}. The accelerator " +
                    "projects the HORIZONTAL PREFIX of the unknowns and borders the system with the " +
                    "vertical tail (P12), so the two families have to be contiguous — R-via-5's own " +
                    "ordering, which SurfaceMesher produces. An interleaved mesh would be projected " +
                    "onto the wrong bases and would produce a smooth, plausible, wrong answer.");

        cores.Settings.CoreBuilds?.ObserveAimGeometry(cores.Mesh);
        return new PlanarAimGeometry(cores, st, slabHeightM, nh);
    }

    private PlanarAimGeometry(PlanarFillCores cores, PlanarAimSettings st, double slabHeightM,
                              int horizontalCount)
    {
        var mesh = cores.Mesh;
        Cores    = cores;
        Settings = st;
        N    = horizontalCount;
        Nz   = mesh.Bases.Count - horizontalCount;
        M    = st.ProjectionOrder;
        Side = M + 1;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── the auxiliary grid ────────────────────────────────────────────────────────────────
        var (centres, spans) = SupportBoxes(mesh, N);
        double maxSpan = 0;
        foreach (var s in spans) maxSpan = Math.Max(maxSpan, s);
        if (!(maxSpan > 0)) maxSpan = cores.MinCellEdgeM > 0 ? cores.MinCellEdgeM : 1.0;

        H = st.GridSpacingFactor * maxSpan;

        // ── P8 — the near radius, and the floor under it ──────────────────────────────────────
        //
        // The PITCH stays sized from the largest support: it is the stencil's own resolution of the
        // kernel and has nothing to do with the slab. The RADIUS does not, because what the near
        // field has to span is the range over which the coupling is long-ranged, and on a grounded
        // slab that range is set by the image at depth 2h — not by how finely the metal happens to
        // be diced. See PlanarAimSettings.NearRadiusMinM for the derivation and the measurement.
        SlabHeightM            = slabHeightM;
        NearRadiusFromSupportM = st.NearRadiusFactor * maxSpan;
        NearRadiusFloorM       = st.NearRadiusFloorFor(slabHeightM);
        NearRadiusM            = Math.Max(NearRadiusFromSupportM, NearRadiusFloorM);

        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var c in mesh.Cells)
        {
            x0 = Math.Min(x0, c.XMin); y0 = Math.Min(y0, c.YMin);
            x1 = Math.Max(x1, c.XMax); y1 = Math.Max(y1, c.YMax);
        }

        // Padded by (M+1) pitches on every side, which is exactly what makes the stencil placement
        // below need no clamping and therefore no "what if it was clamped" accuracy caveat.
        double pad = (M + 1) * H;
        double gx0 = x0 - pad, gy0 = y0 - pad;
        Nx = (int)Math.Ceiling((x1 + pad - gx0) / H) + 1;
        Ny = (int)Math.Ceiling((y1 + pad - gy0) / H) + 1;
        Px = NextPow2(2 * Nx);
        Py = NextPow2(2 * Ny);

        // ── the projection ────────────────────────────────────────────────────────────────────
        var vInv = AimProjection.InverseVandermonde(M, H);
        Stencils = new AimStencil[N];
        for (int i = 0; i < N; i++)
            Stencils[i] = Project(mesh, i, centres[i], gx0, gy0, H, vInv);
        ProjectionMs = sw.Elapsed.TotalMilliseconds;

        // ── the near set, and its mirror ──────────────────────────────────────────────────────
        sw.Restart();
        var sp0 = new int[N];
        var sq0 = new int[N];
        for (int i = 0; i < N; i++) { sp0[i] = Stencils[i].P0; sq0[i] = Stencils[i].Q0; }
        (RowPtr, ColIdx) = AimProjection.NearSet(N, M, centres, spans, sp0, sq0, NearRadiusM, H);
        NearSetMs = sw.Elapsed.TotalMilliseconds;

        // ── the singular cores of every near pair, ONCE ───────────────────────────────────────
        // Row-parallel over the upper triangle, the same loop the per-frequency fill runs; every
        // core it will ask for is in the store when this returns, and PlanarEntryCores.CorePasses
        // stops moving here.
        sw.Restart();
        EntryCores = new PlanarEntryCores(cores);
        var entry = EntryCores;
        PlanarFill.ForRowsOf(cores.Settings, N, i =>
        {
            for (int k = RowPtr[i]; k < RowPtr[i + 1]; k++)
            {
                int j = ColIdx[k];
                if (j >= i) entry.Prepare(i, j);
            }
        });
        NearCoreMs = sw.Elapsed.TotalMilliseconds;
    }

    internal static int NextPow2(int n) { int p = 1; while (p < n) p <<= 1; return p; }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The projection
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Each basis's support bounding box: its centre, and its larger dimension. The box is the
    /// GRID rectangles of the two cells — a cut cell's metal is inside its rectangle, so this bounds
    /// the support in every case, which is what the stencil guard needs.</summary>
    private static ((double X, double Y)[] Centres, double[] Spans) SupportBoxes(PlanarMesh mesh, int n)
    {
        var c = new (double, double)[n];
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            var b  = mesh.Bases[i];
            var ca = mesh.Cells[b.CellA];
            var cb = mesh.Cells[b.CellB];
            double xa = Math.Min(ca.XMin, cb.XMin), xb = Math.Max(ca.XMax, cb.XMax);
            double ya = Math.Min(ca.YMin, cb.YMin), yb = Math.Max(ca.YMax, cb.YMax);
            c[i] = (0.5 * (xa + xb), 0.5 * (ya + yb));
            s[i] = Math.Max(xb - xa, yb - ya);
        }
        return (c, s);
    }

    private AimStencil Project(PlanarMesh mesh, int i, (double X, double Y) centre,
                               double gx0, double gy0, double h, double[,] vInv)
    {
        var basis = mesh.Bases[i];

        int p0 = (int)Math.Round((centre.X - gx0) / h - 0.5 * M);
        int q0 = (int)Math.Round((centre.Y - gy0) / h - 0.5 * M);
        p0 = Math.Clamp(p0, 0, Nx - 1 - M);
        q0 = Math.Clamp(q0, 0, Ny - 1 - M);

        double xs = gx0 + (p0 + 0.5 * M) * h;
        double ys = gy0 + (q0 + 0.5 * M) * h;

        var (mJ, mQ) = Moments(mesh, basis, xs, ys);

        return new AimStencil
        {
            P0 = p0, Q0 = q0,
            Current   = AimProjection.Coefficients(Side, mJ, vInv),
            Charge    = AimProjection.Coefficients(Side, mQ, vInv),
            Direction = basis.Direction,
        };
    }

    /// <summary>
    /// <c>∫ w (x−x_s)^a (y−y_s)^b dS</c> for the current weight and for the charge pulse, taken through
    /// the fill's own weight evaluation so the projected object is the operator's own basis.
    /// </summary>
    private (double[,] Current, double[,] Charge) Moments(PlanarMesh mesh, PlanarBasis basis,
                                                          double xs, double ys)
    {
        int s = Side;
        var mJ = new double[s, s];
        var mQ = new double[s, s];

        var (ra, rb) = PlanarFill.RampHalvesOf(mesh, basis);
        var (da, db) = PlanarBasisFunctions.Halves(mesh, basis);

        // Enough nodes that the rule is exact on the polynomial part: a whole rectangle's integrand is
        // degree (1 + a + b) ≤ 2M+1, and a strip's bilinear map roughly doubles that.
        int nodes = 2 * M + 6;

        int side = Side;
        AimProjection.Accumulate(side, mJ, mesh.Cells[ra.CellIndex], ra, basis.Direction, 1.0, xs, ys, nodes);
        AimProjection.Accumulate(side, mJ, mesh.Cells[rb.CellIndex], rb, basis.Direction, 1.0, xs, ys, nodes);

        AimProjection.Accumulate(side, mQ, mesh.Cells[da.CellIndex], PlanarFill.PulseAt(mesh, da.CellIndex),
                                 PlanarBasisDirection.X, da.Sign, xs, ys, nodes);
        AimProjection.Accumulate(side, mQ, mesh.Cells[db.CellIndex], PlanarFill.PulseAt(mesh, db.CellIndex),
                                 PlanarBasisDirection.X, db.Sign, xs, ys, nodes);

        return (mJ, mQ);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The near set
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The stored position of <c>(j, i)</c> for a stored <c>(i, j)</c>. The near set is symmetric
    /// because both of its criteria are, so this always exists — and the assertion says so rather
    /// than silently leaving a zero where an entry belongs. A binary search over row <c>j</c>, run
    /// per lower-triangle entry per frequency rather than tabulated (see <see cref="Bytes"/>).
    /// </summary>
    internal int TransposePosition(int i, int j)
    {
        int lo = RowPtr[j], hi = RowPtr[j + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (ColIdx[mid] == i) return mid;
            if (ColIdx[mid] < i) lo = mid + 1; else hi = mid - 1;
        }
        throw new InvalidOperationException(
            $"The near set is not symmetric: ({i}, {j}) is in it and ({j}, {i}) is not. " +
            "Both nearness criteria are symmetric, so this is a bug in the near-set " +
            "construction rather than a configuration.");
    }

    /// <summary>True when <c>(i, j)</c> is in the near set — what the near-set completeness gate asks.</summary>
    public bool IsNear(int i, int j)
    {
        for (int k = RowPtr[i]; k < RowPtr[i + 1]; k++) if (ColIdx[k] == j) return true;
        return false;
    }

    /// <summary>The two stencils' node index boxes — the overlap gate reads these rather than
    /// re-deriving them from the settings.</summary>
    public (int P0, int Q0) StencilOrigin(int i) => (Stencils[i].P0, Stencils[i].Q0);
}

/// <summary>
/// <b>M5's accelerated operator.</b> Holds no <c>N×N</c> anything: a uniform-grid kernel pair, one
/// stencil per basis, and the exact matrix restricted to the near set. <see cref="Multiply"/> is the
/// accelerated product; <see cref="Solve"/> runs right-preconditioned GMRES against it with the near
/// field's own sparse factorisation as the preconditioner — which §11 measured as the one that makes
/// the iteration count flat, and which AIM gets for free because it computes those entries anyway.
///
/// <para><b>P6: one of these per FREQUENCY, over a <see cref="PlanarAimGeometry"/> built once per
/// mesh.</b> What is built here is exactly what carries ω: the two grid kernel tables and their FFT
/// hats, the radial remainder tables, the near entries' remainders and assembly, the AIM correction,
/// and the sparse LU. The stencils, the near set, the mirror and every singular core are the
/// geometry's and are read, not rebuilt.</para>
///
/// <para><b>Not thread-safe for concurrent products</b> — the FFT plans and their scratch buffers are
/// per-operator, and one operator belongs to one mesh at one frequency. M2's fan-out gives each solve
/// its own, which is the shape it already has.</para>
/// </summary>
public sealed class PlanarAimOperator : IPlanarOperator
{
    private readonly PlanarAimGeometry _g;
    private readonly int _n;
    private readonly PlanarAimSettings _st;
    private readonly AimStencil[] _stencils;
    private readonly int _side;                               // m + 1
    private readonly int _nx, _ny;                            // auxiliary grid nodes

    // Grid kernels, indexed by ABSOLUTE offset — G depends only on |Δ|, so this is the whole table.
    private readonly Complex[] _ga, _gq;                      // [|dp| * _ny + |dq|]

    // The FFT'd circulant embeddings, and the scratch the product runs in.
    private readonly int _px, _py;
    private readonly Complex[] _hatA, _hatQ;
    private readonly Complex[] _bufX, _bufY, _bufQ;
    private readonly AimGridFft _fft;

    // The near field: CSR over the FULL matrix (both triangles), holding the exact entries and the
    // correction (exact − AIM). The correction is what the product adds; the exact is what the
    // preconditioner factors. The index arrays are the geometry's.
    private readonly int[]     _rowPtr;
    private readonly int[]     _colIdx;
    private readonly Complex[] _nearCorrection;

    // P1: LIVE ONLY UNTIL FactorNear HAS RUN, unless PlanarAimSettings.KeepNearExact asked for it.
    // The product reads _nearCorrection; nothing else in a solve reads this.
    private Complex[]? _nearExact;

    private readonly SparseLU? _preconditioner;

    // The two ω-dependent block scales, resolved once at construction.
    private readonly Complex _scalarScale, _vectorScale;

    public int Size => _n;

    /// <summary>The per-mesh state this operator reads.</summary>
    public PlanarAimGeometry Geometry => _g;

    /// <summary>What it cost and how big it is.</summary>
    public PlanarAimReport Report { get; }

    /// <summary>Iterations the last <see cref="Solve"/> took, and the residual it reached. Read by the
    /// gates; §11's whole finding is about the first of these.</summary>
    public int LastIterations { get; private set; }

    /// <inheritdoc cref="LastIterations"/>
    public double LastResidual { get; private set; }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Build
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the geometry AND the operator for one mesh at one frequency — the pre-P6 shape, kept
    /// for a caller with one frequency (the gates). A sweep builds the geometry once with
    /// <see cref="PlanarAimGeometry.Build"/> and calls the other overload.
    /// </summary>
    public static PlanarAimOperator Build(PlanarFillCores cores, PlanarKernelTerms termsA,
                                          PlanarKernelTerms termsQ, double omega,
                                          double slabHeightM,
                                          PlanarAimSettings? settings = null)
        => Build(PlanarAimGeometry.Build(cores, slabHeightM, settings), termsA, termsQ, omega);

    /// <summary>P6 — the per-frequency operator over a geometry built once per mesh.</summary>
    public static PlanarAimOperator Build(PlanarAimGeometry geometry, PlanarKernelTerms termsA,
                                          PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(termsA);
        ArgumentNullException.ThrowIfNull(termsQ);
        return new PlanarAimOperator(geometry, termsA, termsQ, omega);
    }

    private PlanarAimOperator(PlanarAimGeometry g, PlanarKernelTerms termsA, PlanarKernelTerms termsQ,
                              double omega)
    {
        // P12 — the ẑ family is not modelled HERE, and the sentence now names where it is. This
        // operator carries ONE grid kernel pair, which is a statement that every source and every
        // observer sits at one height; a via mesh is a bordered system with a kernel per height
        // pairing (PlanarBorderedAimOperator), not a wider version of this.
        if (g.VerticalCount > 0)
            throw new NotSupportedException(
                $"This mesh carries {g.VerticalCount} ẑ-directed (via) unknown(s). " +
                "PlanarAimOperator holds ONE grid kernel pair, i.e. one height pairing, and a via " +
                "basis needs its own G_A^zz plus a MIXED component whose dyadic entry is a ∂/∂x " +
                "rather than a value. Build PlanarBorderedAimOperator instead: it accelerates the " +
                "same horizontal block per (level, level) pairing and carries the vertical unknowns " +
                "as a DENSE BORDER, which is what P12 measured as cheap while N_z ≪ N_h.");

        var cores = g.Cores;
        var st    = g.Settings;
        _g        = g;
        _n        = g.N;
        _st       = st;
        _side     = g.Side;
        _nx       = g.Nx;
        _ny       = g.Ny;
        _stencils = g.Stencils;
        _rowPtr   = g.RowPtr;
        _colIdx   = g.ColIdx;
        double h  = g.H;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── the grid kernels, and their circulant embeddings ──────────────────────────────────
        var termsAr = termsA.With(cores.Settings.Order, cores.RhoFloorM);
        var termsQr = termsQ.With(cores.Settings.Order, cores.RhoFloorM);
        double selfRho = st.SelfKernelFactor * h;

        int nx = g.Nx, ny = g.Ny;
        _ga = new Complex[(long)nx * ny];
        _gq = new Complex[(long)nx * ny];
        for (int dp = 0; dp < nx; dp++)
            for (int dq = 0; dq < ny; dq++)
            {
                double rho = h * Math.Sqrt((double)dp * dp + (double)dq * dq);
                double at  = dp == 0 && dq == 0 ? selfRho : rho;
                _ga[dp * ny + dq] = termsAr.Evaluate(at);
                _gq[dp * ny + dq] = termsQr.Evaluate(at);
            }

        _px = g.Px;
        _py = g.Py;
        _fft  = new AimGridFft(_nx, _ny, _px, _py);
        _hatA = _fft.EmbedAndTransform(_ga);
        _hatQ = _fft.EmbedAndTransform(_gq);
        _bufX = new Complex[(long)_px * _py];
        _bufY = new Complex[(long)_px * _py];
        _bufQ = new Complex[(long)_px * _py];
        double gridMs = sw.Elapsed.TotalMilliseconds;

        // ── the radial remainder tables ───────────────────────────────────────────────────────
        // Timed apart from the near fill on purpose: constructing the entry filler builds the two
        // per-frequency radial remainder tables, and THE DENSE PATH BUILDS THE SAME TWO. Charging them
        // to the accelerator would make every cost comparison below flatter the dense path by a fixed
        // amount that has nothing to do with either.
        sw.Restart();
        var entry = new PlanarEntryFill(g.EntryCores, termsA, termsQ, omega);
        double tableMs = sw.Elapsed.TotalMilliseconds;

        // ── the near set's exact entries: remainders + assembly over the geometry's cores ─────
        sw.Restart();
        Complex scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
        _scalarScale = scalarScale;
        _vectorScale = vectorScale;

        var nearExact   = new Complex[_colIdx.Length];
        _nearExact      = nearExact;
        _nearCorrection = new Complex[_colIdx.Length];

        PlanarFill.ForRowsOf(cores.Settings, _n, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j < i) continue;
                nearExact[k] = entry.At(i, j);
            }
        });
        double remainderMs = sw.Elapsed.TotalMilliseconds;

        // ── the AIM correction: exact − what the grid product claims for the pair ────────────
        sw.Restart();
        PlanarFill.ForRowsOf(cores.Settings, _n, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j < i) continue;
                _nearCorrection[k] = nearExact[k] - AimEntry(i, j, scalarScale, vectorScale);
            }
        });

        double correctionMs = sw.Elapsed.TotalMilliseconds;

        // R-fil-2, one level down: BOTH criteria for nearness are symmetric, so the near set is, and
        // the lower triangle is COPIED from the upper rather than recomputed. Not a micro-optimisation
        // — it is half the build, and it is also what keeps Z[i,j] and Z[j,i] bit-identical here for
        // the same reason the dense fill mirrors instead of computing both. P6: the transpose
        // position is found by binary search here rather than read from a held index — see
        // PlanarAimGeometry.Bytes for the 18 MB that decided it.
        sw.Restart();
        PlanarFill.ForRowsOf(cores.Settings, _n, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j >= i) continue;
                int t = g.TransposePosition(i, j);
                nearExact[k]       = nearExact[t];
                _nearCorrection[k] = _nearCorrection[t];
            }
        });
        double copyMs = sw.Elapsed.TotalMilliseconds;

        // ── the preconditioner: the near field's own sparse LU ────────────────────────────────
        sw.Restart();
        var (lu, nnz, factorNnz) = FactorNear(nearExact);
        _preconditioner = lu;
        double precondMs = sw.Elapsed.TotalMilliseconds;

        // P1 — the exact entries have served their only non-diagnostic purpose. Dropping them here
        // rather than at the end of Build is deliberate: the CSC copy FactorNear made is still
        // collectable at this point too, so the operator's steady state is reached in one collection.
        if (!st.KeepNearExact) _nearExact = null;

        Report = new PlanarAimReport(
            UnknownCount: _n, GridNodesX: nx, GridNodesY: ny, GridPitchM: h,
            ProjectionOrder: g.M, NearRadiusM: g.NearRadiusM,
            NearEntries: _colIdx.LongLength, NearCellPairs: entry.CellPairCount,
            PaddedGridNodes: (long)_px * _py,
            ProjectionMs: g.ProjectionMs, GridKernelMs: gridMs, RemainderTableMs: tableMs,
            NearFillMs: remainderMs + correctionMs + copyMs,
            PreconditionerMs: precondMs, PreconditionerNonZeros: nnz,
            FactorNonZeros: factorNnz, NearExactRetained: st.KeepNearExact,
            NearRadiusFromSupportM: g.NearRadiusFromSupportM, NearRadiusFloorM: g.NearRadiusFloorM,
            GeometryMs: g.TotalMs, NearSetMs: g.NearSetMs, NearCoreMs: g.NearCoreMs,
            NearRemainderMs: remainderMs, CorrectionMs: correctionMs, LowerCopyMs: copyMs,
            GeometryBytes: g.Bytes, NearCoreClasses: g.EntryCores.ClassCount);
    }

    /// <summary>What the accelerated product produces for one pair — i.e. what the near-field
    /// correction has to remove before adding the exact entry.</summary>
    private Complex AimEntry(int i, int j, Complex scalarScale, Complex vectorScale)
    {
        var a = _stencils[i];
        var b = _stencils[j];
        int s = _side;

        Complex q = Complex.Zero, v = Complex.Zero;
        bool sameDir = a.Direction == b.Direction;

        for (int k = 0; k < s; k++)
            for (int l = 0; l < s; l++)
            {
                double ca = a.Charge[k * s + l];
                double ja = a.Current[k * s + l];
                if (ca == 0.0 && ja == 0.0) continue;
                int p = a.P0 + k, qq = a.Q0 + l;

                for (int mm = 0; mm < s; mm++)
                    for (int nn = 0; nn < s; nn++)
                    {
                        int dp = Math.Abs(p - (b.P0 + mm));
                        int dq = Math.Abs(qq - (b.Q0 + nn));
                        long idx = (long)dp * _ny + dq;
                        q += ca * _gq[idx] * b.Charge[mm * s + nn];
                        if (sameDir) v += ja * _ga[idx] * b.Current[mm * s + nn];
                    }
            }

        return scalarScale * q + (sameDir ? vectorScale * v : Complex.Zero);
    }

    /// <summary>
    /// The near field's own sparse LU. <b><c>Nnz</c> is the near matrix's — the CSC copy's — non-zero
    /// count, which is the near ENTRY count; <c>FactorNnz</c> is the FACTOR's, L and U together, and
    /// the two are not remotely the same number</b> (P1 measured the fill-in at several times the
    /// matrix it comes from). The report used to carry only the first, under a name that reads like
    /// the second.
    /// </summary>
    /// <param name="exact">The near set's exact entries, passed rather than read from
    /// <c>_nearExact</c> so that this cannot be called after P1 releases them — the field is nullable
    /// from here on and an argument is a cheaper guarantee than a null check with a sentence in it.</param>
    private (SparseLU? Lu, long Nnz, long FactorNnz) FactorNear(Complex[] exact)
    {
        var tri = new CoordinateStorage<Complex>(_n, _n, Math.Max(1, _colIdx.Length));
        for (int i = 0; i < _n; i++)
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
                tri.At(i, _colIdx[k], exact[k]);

        var csc = SparseMatrix.OfIndexed(tri);
        try
        {
            var perm = AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtPlusA);
            var lu = SparseLU.Create(csc, perm, 1.0);
            return (lu, csc.NonZerosCount, lu.NonZerosCount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed near-field factorisation is not fatal — GMRES still runs unpreconditioned, and
            // §11 measured what that costs (129 -> 341 iterations over the same span) rather than
            // leaving it as a guess. Reporting it as "no preconditioner" beats refusing the solve.
            return (null, csc.NonZerosCount, 0);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The accelerated product
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><c>y = Z x</c>, accelerated: the sparse near-field correction plus three FFT
    /// convolutions on the auxiliary grid.</summary>
    public Complex[] Multiply(Complex[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length != _n)
            throw new ArgumentException($"Expected a vector of length {_n}, got {x.Length}.", nameof(x));

        int s = _side;

        Array.Clear(_bufX); Array.Clear(_bufY); Array.Clear(_bufQ);

        // ── scatter: basis coefficients → grid densities ─────────────────────────────────────
        for (int i = 0; i < _n; i++)
        {
            Complex xi = x[i];
            if (xi == Complex.Zero) continue;
            var st = _stencils[i];
            var cur = st.Direction == PlanarBasisDirection.X ? _bufX : _bufY;
            for (int k = 0; k < s; k++)
            {
                long row = (long)(st.P0 + k) * _py + st.Q0;
                for (int l = 0; l < s; l++)
                {
                    cur[row + l]  += xi * st.Current[k * s + l];
                    _bufQ[row + l] += xi * st.Charge[k * s + l];
                }
            }
        }

        // ── convolve on the grid ─────────────────────────────────────────────────────────────
        _fft.Convolve(_bufX, _hatA);
        _fft.Convolve(_bufY, _hatA);
        _fft.Convolve(_bufQ, _hatQ);

        // ── gather: grid potentials → basis reactions ────────────────────────────────────────
        var y = new Complex[_n];
        for (int i = 0; i < _n; i++)
        {
            var st = _stencils[i];
            var cur = st.Direction == PlanarBasisDirection.X ? _bufX : _bufY;
            Complex vec = Complex.Zero, sca = Complex.Zero;
            for (int k = 0; k < s; k++)
            {
                long row = (long)(st.P0 + k) * _py + st.Q0;
                for (int l = 0; l < s; l++)
                {
                    vec += st.Current[k * s + l] * cur[row + l];
                    sca += st.Charge[k * s + l]  * _bufQ[row + l];
                }
            }
            y[i] = _vectorScale * vec + _scalarScale * sca;
        }

        // ── the exact near field, minus what the grid product just claimed for it ────────────
        for (int i = 0; i < _n; i++)
        {
            Complex acc = Complex.Zero;
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++) acc += _nearCorrection[k] * x[_colIdx[k]];
            y[i] += acc;
        }

        return y;
    }

    /// <summary><see cref="Multiply(Complex[])"/> in NumFlat's own vector type, which is what the
    /// excitation and the de-embedding speak.</summary>
    public Vec<Complex> Multiply(Vec<Complex> x)
    {
        var a = new Complex[_n];
        for (int i = 0; i < _n; i++) a[i] = x[i];
        var b = Multiply(a);
        var r = new Vec<Complex>(_n);
        for (int i = 0; i < _n; i++) r[i] = b[i];
        return r;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The grid FFT
    // ══════════════════════════════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Right-preconditioned GMRES against the accelerated product, with the near field's own sparse
    /// LU as the preconditioner.
    ///
    /// <para><b>Right preconditioning is not a detail.</b> It makes the Arnoldi residual the TRUE
    /// ‖b − Zx‖, so the tolerance means what it says; left preconditioning would report
    /// ‖M⁻¹(b − Zx)‖ and flatter a strong preconditioner for free. §11 measured on the same choice.</para>
    /// </summary>
    public Vec<Complex> Solve(Vec<Complex> rhs)
    {
        var b = new Complex[_n];
        for (int i = 0; i < _n; i++) b[i] = rhs[i];

        var x = PlanarGmres.Solve(Multiply, ApplyPreconditioner, b, _st.Tolerance,
                                  _st.MaxIterations, _st.Restart,
                                  out int iterations, out double residual);
        LastIterations = iterations;
        LastResidual   = residual;

        if (residual > _st.Tolerance)
            throw new InvalidOperationException(
                $"The accelerated solve did not converge: {iterations} iteration(s) reached a relative " +
                $"residual of {residual:E2} against a tolerance of {_st.Tolerance:E2}. A half-converged " +
                "current distribution produces a smooth, plausible, WRONG s-parameter, so this refuses " +
                "rather than returning one. Widen the near field (NearRadiusFactor), raise " +
                "ProjectionOrder, or solve this mesh densely.");

        var r = new Vec<Complex>(_n);
        for (int i = 0; i < _n; i++) r[i] = x[i];
        return r;
    }

    private Complex[] ApplyPreconditioner(Complex[] v)
    {
        if (_preconditioner is null) return v;
        var r = new Complex[_n];
        _preconditioner.Solve(v, r);
        return r;
    }

    /// <summary>The exact entry held for <c>(i, j)</c>, or zero when the pair is not near. A
    /// DIAGNOSTIC — the gates read it, and it is public for the same reason
    /// <see cref="PlanarFillDiagnostics"/> is: an instrument that cannot be reached from a test is not
    /// an instrument.
    ///
    /// <para>P1: the exact entries are released once the preconditioner is factored from them, so this
    /// needs <see cref="PlanarAimSettings.KeepNearExact"/>. It THROWS when they were not kept rather
    /// than returning zero — a silent zero here is indistinguishable from "not near", which is exactly
    /// the question a caller is asking.</para></summary>
    public Complex NearExactAt(int i, int j)
    {
        var exact = _nearExact ?? throw new InvalidOperationException(
            "The exact near-field entries were released after the preconditioner was factored from " +
            "them — the accelerated product reads the CORRECTION (exact − AIM), never the exact, so " +
            "holding both costs 16 bytes per near entry for a diagnostic nothing in a solve reads. " +
            "Build the operator with PlanarAimSettings { KeepNearExact = true } to read them.");
        for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            if (_colIdx[k] == j) return exact[k];
        return Complex.Zero;
    }

    /// <inheritdoc cref="PlanarAimGeometry.IsNear"/>
    public bool IsNear(int i, int j) => _g.IsNear(i, j);

    /// <inheritdoc cref="PlanarAimGeometry.StencilOrigin"/>
    public (int P0, int Q0) StencilOrigin(int i) => _g.StencilOrigin(i);
}

/// <summary>
/// <b>P11 — the parts of AIM that are not about WHAT is being projected</b>: the stencil's Vandermonde
/// inverse and moment match, the moment quadrature, and the near set. Extracted from
/// <see cref="PlanarAimGeometry"/> unchanged when the accelerated static capacitance solve
/// (<see cref="PlanarStaticAim"/>) needed the same three over CELL PULSES rather than over basis
/// functions — one projection, two things projected, rather than two implementations that agree by
/// inspection.
/// </summary>
internal static class AimProjection
{
    /// <summary>
    /// <c>V⁻¹</c> where <c>V[a,k] = ξ_k^a</c> and <c>ξ_k = (k − M/2)·h</c> — the stencil's own
    /// coordinates about its centre. Uniform grid ⇒ one inverse serves every basis, which is what makes
    /// the projection O(N) with a tiny constant instead of an <c>(M+1)³</c> solve per basis.
    /// </summary>
    internal static double[,] InverseVandermonde(int m, double h)
    {
        int s = m + 1;
        var v = new double[s, s];
        for (int k = 0; k < s; k++)
        {
            double xi = (k - 0.5 * m) * h;
            double p = 1.0;
            for (int a = 0; a < s; a++) { v[a, k] = p; p *= xi; }
        }

        // Gauss-Jordan with partial pivoting. s is 1..7, so this is a handful of flops and does not
        // want a library dependency.
        var inv = new double[s, s];
        for (int i = 0; i < s; i++) inv[i, i] = 1.0;

        for (int col = 0; col < s; col++)
        {
            int piv = col;
            for (int r = col + 1; r < s; r++)
                if (Math.Abs(v[r, col]) > Math.Abs(v[piv, col])) piv = r;
            if (Math.Abs(v[piv, col]) < 1e-300)
                throw new InvalidOperationException(
                    "The stencil's Vandermonde matrix is singular, which can only happen if the grid " +
                    "pitch collapsed to zero. That is a mesh with no extent, not a settings error.");
            if (piv != col)
                for (int c2 = 0; c2 < s; c2++)
                {
                    (v[col, c2], v[piv, c2]) = (v[piv, c2], v[col, c2]);
                    (inv[col, c2], inv[piv, c2]) = (inv[piv, c2], inv[col, c2]);
                }

            double d = v[col, col];
            for (int c2 = 0; c2 < s; c2++) { v[col, c2] /= d; inv[col, c2] /= d; }
            for (int r = 0; r < s; r++)
            {
                if (r == col) continue;
                double f = v[r, col];
                if (f == 0) continue;
                for (int c2 = 0; c2 < s; c2++) { v[r, c2] -= f * v[col, c2]; inv[r, c2] -= f * inv[col, c2]; }
            }
        }
        return inv;
    }

    /// <summary><c>λ = V⁻¹ m V⁻ᵀ</c>, flattened row-major over the stencil.</summary>
    internal static double[] Coefficients(int side, double[,] moments, double[,] vInv)
    {
        int s = side;
        var tmp = new double[s, s];
        for (int k = 0; k < s; k++)
            for (int b = 0; b < s; b++)
            {
                double acc = 0;
                for (int a = 0; a < s; a++) acc += vInv[k, a] * moments[a, b];
                tmp[k, b] = acc;
            }

        var lam = new double[s * s];
        for (int k = 0; k < s; k++)
            for (int l = 0; l < s; l++)
            {
                double acc = 0;
                for (int b = 0; b < s; b++) acc += tmp[k, b] * vInv[l, b];
                lam[k * s + l] = acc;
            }
        return lam;
    }

    /// <summary>
    /// <c>∫ w (x−x_s)^a (y−y_s)^b dS</c> accumulated over one cell, <b>through the FILL'S OWN weight
    /// evaluation</b> (<see cref="PlanarFill.WeightNodes"/>) — cut cells, strips and all. A projection
    /// built on a second reading of what a weight is would approximate a different operator, and the
    /// residual would look like an accuracy floor with no cause.
    /// </summary>
    internal static void Accumulate(int side, double[,] target, PlanarCell cell,
                                    PlanarFill.CellWeight weight,
                                    PlanarBasisDirection dir, double sign, double xs, double ys, int nodes)
    {
        int s = side;
        foreach (var (x, y, w) in PlanarFill.WeightNodes(cell, weight, dir, 1, nodes))
        {
            double dx = x - xs, dy = y - ys;
            double px = 1.0;
            for (int a = 0; a < s; a++)
            {
                double py = 1.0;
                for (int b = 0; b < s; b++)
                {
                    target[a, b] += sign * w * px * py;
                    py *= dy;
                }
                px *= dx;
            }
        }
    }

    /// <summary>
    /// Every pair that is either within <paramref name="radius"/> or whose stencils OVERLAP, as CSR
    /// over the full matrix. <b>The second criterion is not belt-and-braces</b>: it is what makes the
    /// grid kernel's value at zero separation cancel exactly, and therefore what makes it legitimate
    /// for that value to be arbitrary. See the file header.
    /// </summary>
    /// <param name="n">How many unknowns — basis functions for M5's operator, CELLS for P11's.</param>
    /// <param name="m">The projection order, i.e. the stencil is <c>(m+1)×(m+1)</c> nodes.</param>
    /// <param name="p0">Each unknown's stencil origin, x. <paramref name="q0"/> is the same in y.</param>
    internal static (int[] RowPtr, int[] ColIdx) NearSet(int n, int m,
                                                        (double X, double Y)[] centres, double[] spans,
                                                        int[] p0, int[] q0, double radius, double h)
    {
        // Aliased to the names the body was written in, so the arithmetic below reads exactly as it
        // did inside PlanarAimGeometry and a reader can diff the two by eye.
        int M = m, N = n;
        // A stencil spans M pitches; two stencils overlap only if their centres are within about
        // (M+1)·h on each axis, so this bound cannot miss one.
        double stencilReach = (M + 1.5) * h;
        double search = Math.Max(radius, stencilReach * 1.5);
        double maxSpan = 0;
        foreach (double s in spans) maxSpan = Math.Max(maxSpan, s);
        search = Math.Max(search, maxSpan);

        var buckets = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < N; i++)
        {
            var key = ((int)Math.Floor(centres[i].X / search), (int)Math.Floor(centres[i].Y / search));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(i);
        }

        var rows = new List<int>[N];
        double r2 = radius * radius;

        PlanarFill.ForRowsOf(PlanarFillSettings.Default, N, i =>
        {
            var mine = new List<int>();
            int bx = (int)Math.Floor(centres[i].X / search);
            int by = (int)Math.Floor(centres[i].Y / search);
            int siP0 = p0[i], siQ0 = q0[i];

            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (!buckets.TryGetValue((bx + ox, by + oy), out var list)) continue;
                    foreach (int j in list)
                    {
                        double dx = centres[i].X - centres[j].X;
                        double dy = centres[i].Y - centres[j].Y;
                        bool near = dx * dx + dy * dy <= r2;
                        if (!near)
                            near = Math.Abs(siP0 - p0[j]) <= M && Math.Abs(siQ0 - q0[j]) <= M;
                        if (near) mine.Add(j);
                    }
                }
            mine.Sort();
            rows[i] = mine;
        });

        var rowPtr = new int[N + 1];
        for (int i = 0; i < N; i++) rowPtr[i + 1] = rowPtr[i] + rows[i].Count;
        var colIdx = new int[rowPtr[N]];
        for (int i = 0; i < N; i++) rows[i].CopyTo(colIdx, rowPtr[i]);
        return (rowPtr, colIdx);
    }
}

/// <summary>
/// <b>P11 — the auxiliary grid's own FFT</b>: the circulant embedding of an absolute-offset kernel
/// table, and the cyclic convolution the accelerated product runs in. Extracted from
/// <see cref="PlanarAimOperator"/> unchanged, because <see cref="PlanarStaticAim"/> needs exactly the
/// same three methods on exactly the same padded grid with one kernel instead of two.
///
/// <para><b>Not thread-safe</b> — the FFT plans and the strided-column scratch are per-instance, and
/// one instance belongs to one operator. That is the constraint <see cref="PlanarAimOperator"/>
/// already documented for itself.</para>
/// </summary>
internal sealed class AimGridFft
{
    private readonly int _nx, _ny, _px, _py;
    private readonly FastFourierTransform _fftX, _fftY;
    private readonly Complex[] _rowScratch;

    public AimGridFft(int nx, int ny, int px, int py)
    {
        _nx = nx; _ny = ny; _px = px; _py = py;
        _fftX = new FastFourierTransform(px);
        _fftY = new FastFourierTransform(py);
        _rowScratch = new Complex[Math.Max(px, py)];
    }

    /// <summary>Wraps the absolute-offset kernel table into the <c>Px×Py</c> circulant and transforms
    /// it. Negative offsets land in the upper half of each axis, which is what makes the cyclic
    /// convolution agree with the linear one over the sub-block the grid actually occupies.</summary>
    public Complex[] EmbedAndTransform(Complex[] g)
    {
        var c = new Complex[(long)_px * _py];
        for (int u = 0; u < _px; u++)
        {
            int dp = u < _nx ? u : u - _px;
            if (Math.Abs(dp) >= _nx) continue;
            int adp = Math.Abs(dp);
            for (int v = 0; v < _py; v++)
            {
                int dq = v < _ny ? v : v - _py;
                if (Math.Abs(dq) >= _ny) continue;
                c[(long)u * _py + v] = g[(long)adp * _ny + Math.Abs(dq)];
            }
        }
        Transform2(c, forward: true);
        return c;
    }

    public void Convolve(Complex[] buf, Complex[] hat)
    {
        Transform2(buf, forward: true);
        for (long i = 0; i < buf.LongLength; i++) buf[i] *= hat[i];
        Transform2(buf, forward: false);
    }

    /// <summary>Separable 2-D transform over the <c>Px×Py</c> buffer, row-major. FftFlat's
    /// <c>Inverse</c> carries the 1/N per axis, so the pair round-trips without a rescale.</summary>
    public void Transform2(Complex[] buf, bool forward)
    {
        // rows (along y, contiguous)
        for (int u = 0; u < _px; u++)
        {
            var row = buf.AsSpan(u * _py, _py);
            if (forward) _fftY.Forward(row); else _fftY.Inverse(row);
        }
        // columns (along x, strided)
        var col = _rowScratch.AsSpan(0, _px);
        for (int v = 0; v < _py; v++)
        {
            for (int u = 0; u < _px; u++) col[u] = buf[(long)u * _py + v];
            if (forward) _fftX.Forward(col); else _fftX.Inverse(col);
            for (int u = 0; u < _px; u++) buf[(long)u * _py + v] = col[u];
        }
    }

}
