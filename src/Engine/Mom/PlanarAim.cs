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
// with a derivative in it. That is a second phase, not a widening; `PlanarAimOperator.Build` refuses
// it by name rather than producing a plausible number for a structure it does not model.

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
/// </param>
/// <param name="Tolerance">GMRES's relative residual target. L8c puts the fill's own accuracy at
/// 5.0e-6 against an independent oracle and L8d measured de-embedding amplifying a raw-S error ~22×,
/// so a solve stopped at 1e-4 has thrown the fill away.</param>
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
    int    ProjectionOrder   = 3,
    double GridSpacingFactor = 0.5,
    double NearRadiusFactor  = 6.0,
    double Tolerance         = 1e-8,
    int    MaxIterations     = 400,
    int    Restart           = 0,
    double SelfKernelFactor  = 0.5,
    bool   KeepNearExact     = false)
{
    public static readonly PlanarAimSettings Default = new();

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
        if (MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), MaxIterations,
                "An iteration cap below 1 runs no iterations and returns the zero vector.");
        if (Restart < 0)
            throw new ArgumentOutOfRangeException(nameof(Restart), Restart,
                "Use 0 for full (non-restarted) GMRES.");
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
    bool   NearExactRetained = false)
{
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
    /// <item>the near set's CSR: values (exact, when retained, and correction) at 16 B, the column
    /// index at 4 B, the row pointer at 4·(N+1);</item>
    /// <item>the two grid kernel tables, and the five padded FFT arrays (two transformed kernels and
    /// three scratch fields) — NOT negligible at a fine pitch, and exactly what a "the near field is
    /// only 10% of the matrix" reading forgets;</item>
    /// <item>the per-basis stencils, 32·(M+1)²·N;</item>
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
      + 4L * NearEntries + 4L * (UnknownCount + 1)           // the CSR index
      + 16L * GridNodesX * GridNodesY * 2
      + 16L * PaddedGridNodes * 5
      + 32L * (ProjectionOrder + 1) * (ProjectionOrder + 1) * UnknownCount
      + 20L * FactorNonZeros + 8L * (UnknownCount + 1)       // the sparse LU's L and U
      + 8L * UnknownCount;                                   // AMD permutation + its inverse

    /// <summary>Bytes the accelerator's BUILD peaks at — <see cref="ResidentBytes"/> plus the
    /// transient CSC copy of the near matrix that <c>FactorNear</c> hands to CSparse (values at 16 B,
    /// row index at 4 B, column pointer at 4·(N+1)).</summary>
    public long PeakBuildBytes =>
        ResidentBytes + 20L * NearEntries + 4L * (UnknownCount + 1);
}

/// <summary>
/// One basis's projection onto the auxiliary grid: where its stencil sits, and the coefficients that
/// reproduce its moments there.
/// </summary>
internal sealed class AimStencil
{
    /// <summary>Lower-left grid node index of the <c>(M+1)×(M+1)</c> block.</summary>
    public required int P0 { get; init; }
    public required int Q0 { get; init; }

    /// <summary>Current-density coefficients, row-major over the stencil. The basis has exactly one
    /// flow direction, so there is one of these and the direction says which grid field it lands
    /// in.</summary>
    public required Complex[] Current { get; init; }

    /// <summary>Charge coefficients — <c>∇·f</c>'s moments, which are what the scalar block sees.</summary>
    public required Complex[] Charge { get; init; }

    public required PlanarBasisDirection Direction { get; init; }
}

/// <summary>
/// <b>M5's accelerated operator.</b> Holds no <c>N×N</c> anything: a uniform-grid kernel pair, one
/// stencil per basis, and the exact matrix restricted to the near set. <see cref="Multiply"/> is the
/// accelerated product; <see cref="Solve"/> runs right-preconditioned GMRES against it with the near
/// field's own sparse factorisation as the preconditioner — which §11 measured as the one that makes
/// the iteration count flat, and which AIM gets for free because it computes those entries anyway.
///
/// <para><b>Not thread-safe for concurrent products</b> — the FFT plans and their scratch buffers are
/// per-operator, and one operator belongs to one mesh at one frequency. M2's fan-out gives each solve
/// its own, which is the shape it already has.</para>
/// </summary>
public sealed class PlanarAimOperator : IPlanarOperator
{
    private readonly int _n;
    private readonly PlanarAimSettings _st;
    private readonly AimStencil[] _stencils;
    private readonly int _m;                                  // projection order
    private readonly int _side;                               // m + 1
    private readonly int _nx, _ny;                            // auxiliary grid nodes

    // Grid kernels, indexed by ABSOLUTE offset — G depends only on |Δ|, so this is the whole table.
    private readonly Complex[] _ga, _gq;                      // [|dp| * _ny + |dq|]

    // The FFT'd circulant embeddings, and the scratch the product runs in.
    private readonly int _px, _py;
    private readonly Complex[] _hatA, _hatQ;
    private readonly Complex[] _bufX, _bufY, _bufQ;
    private readonly FastFourierTransform _fftX, _fftY;
    private readonly Complex[] _rowScratch;

    // The near field: CSR over the FULL matrix (both triangles), holding the exact entries and the
    // correction (exact − AIM). The correction is what the product adds; the exact is what the
    // preconditioner factors.
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
    /// Builds the accelerator for one mesh at one frequency. <paramref name="cores"/> may be — and for
    /// the cost claim to mean anything SHOULD be — <see cref="PlanarFill.BuildGeometryOnlyCores"/>'
    /// O(N) shape.
    /// </summary>
    public static PlanarAimOperator Build(PlanarFillCores cores, PlanarKernelTerms termsA,
                                          PlanarKernelTerms termsQ, double omega,
                                          PlanarAimSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(cores);
        ArgumentNullException.ThrowIfNull(termsA);
        ArgumentNullException.ThrowIfNull(termsQ);
        var st = settings ?? PlanarAimSettings.Default;
        st.Validate();

        foreach (var b in cores.Mesh.Bases)
            if (b.Direction == PlanarBasisDirection.Z)
                throw new NotSupportedException(
                    "The AIM accelerator models the horizontal (x̂/ŷ) basis family only. A ẑ-directed " +
                    "via basis carries G_A^zz plus a MIXED component whose dyadic entry is a ∂/∂x " +
                    "rather than a value, and its sources sit at a different height — a different " +
                    "grid kernel per height pairing and a projection with a derivative in it. That is " +
                    "its own phase, not a widening of this one. Solve a via-bearing mesh densely.");

        return new PlanarAimOperator(cores, termsA, termsQ, omega, st);
    }

    private PlanarAimOperator(PlanarFillCores cores, PlanarKernelTerms termsA,
                              PlanarKernelTerms termsQ, double omega, PlanarAimSettings st)
    {
        var mesh = cores.Mesh;
        _n  = mesh.Bases.Count;
        _st = st;
        _m  = st.ProjectionOrder;
        _side = _m + 1;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── the auxiliary grid ────────────────────────────────────────────────────────────────
        var (centres, spans) = SupportBoxes(mesh);
        double maxSpan = 0;
        foreach (var s in spans) maxSpan = Math.Max(maxSpan, s);
        if (!(maxSpan > 0)) maxSpan = cores.MinCellEdgeM > 0 ? cores.MinCellEdgeM : 1.0;

        double h        = st.GridSpacingFactor * maxSpan;
        double nearM    = st.NearRadiusFactor  * maxSpan;

        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var c in mesh.Cells)
        {
            x0 = Math.Min(x0, c.XMin); y0 = Math.Min(y0, c.YMin);
            x1 = Math.Max(x1, c.XMax); y1 = Math.Max(y1, c.YMax);
        }

        // Padded by (M+1) pitches on every side, which is exactly what makes the stencil placement
        // below need no clamping and therefore no "what if it was clamped" accuracy caveat.
        double pad = (_m + 1) * h;
        double gx0 = x0 - pad, gy0 = y0 - pad;
        _nx = (int)Math.Ceiling((x1 + pad - gx0) / h) + 1;
        _ny = (int)Math.Ceiling((y1 + pad - gy0) / h) + 1;

        // ── the projection ────────────────────────────────────────────────────────────────────
        var vInv = InverseVandermonde(_m, h);
        _stencils = new AimStencil[_n];
        for (int i = 0; i < _n; i++)
            _stencils[i] = Project(mesh, i, centres[i], gx0, gy0, h, vInv);
        double projectionMs = sw.Elapsed.TotalMilliseconds;

        // ── the grid kernels, and their circulant embeddings ──────────────────────────────────
        sw.Restart();
        var termsAr = termsA.With(cores.Settings.Order, cores.RhoFloorM);
        var termsQr = termsQ.With(cores.Settings.Order, cores.RhoFloorM);
        double selfRho = st.SelfKernelFactor * h;

        _ga = new Complex[(long)_nx * _ny];
        _gq = new Complex[(long)_nx * _ny];
        for (int dp = 0; dp < _nx; dp++)
            for (int dq = 0; dq < _ny; dq++)
            {
                double rho = h * Math.Sqrt((double)dp * dp + (double)dq * dq);
                double at  = dp == 0 && dq == 0 ? selfRho : rho;
                _ga[dp * _ny + dq] = termsAr.Evaluate(at);
                _gq[dp * _ny + dq] = termsQr.Evaluate(at);
            }

        _px = NextPow2(2 * _nx);
        _py = NextPow2(2 * _ny);
        _fftX = new FastFourierTransform(_px);
        _fftY = new FastFourierTransform(_py);
        _rowScratch = new Complex[Math.Max(_px, _py)];
        _hatA = EmbedAndTransform(_ga);
        _hatQ = EmbedAndTransform(_gq);
        _bufX = new Complex[(long)_px * _py];
        _bufY = new Complex[(long)_px * _py];
        _bufQ = new Complex[(long)_px * _py];
        double gridMs = sw.Elapsed.TotalMilliseconds;

        // ── the near set, and the exact entries in it ─────────────────────────────────────────
        sw.Restart();
        double radius = nearM;
        var (rowPtr, colIdx) = NearSet(centres, spans, radius, h);
        _rowPtr = rowPtr; _colIdx = colIdx;

        // Timed apart from the near fill on purpose: constructing the entry filler builds the two
        // per-frequency radial remainder tables, and THE DENSE PATH BUILDS THE SAME TWO. Charging them
        // to the accelerator would make every cost comparison below flatter the dense path by a fixed
        // amount that has nothing to do with either.
        var entry = new PlanarEntryFill(cores, termsA, termsQ, omega);
        double tableMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        Complex scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
        _scalarScale = scalarScale;
        _vectorScale = vectorScale;

        var nearExact   = new Complex[colIdx.Length];
        _nearExact      = nearExact;
        _nearCorrection = new Complex[colIdx.Length];

        // R-fil-2, one level down: BOTH criteria for nearness are symmetric, so the near set is, and
        // the lower triangle is COPIED from the upper rather than recomputed. Not a micro-optimisation
        // — it is half the build, and it is also what keeps Z[i,j] and Z[j,i] bit-identical here for
        // the same reason the dense fill mirrors instead of computing both.
        var mirror = MirrorIndex();

        PlanarFill.ForRowsOf(cores.Settings, _n, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j < i) continue;
                Complex exact = entry.At(i, j);
                nearExact[k]       = exact;
                _nearCorrection[k] = exact - AimEntry(i, j, scalarScale, vectorScale);
            }
        });

        PlanarFill.ForRowsOf(cores.Settings, _n, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j >= i) continue;
                int t = mirror[k];
                nearExact[k]       = nearExact[t];
                _nearCorrection[k] = _nearCorrection[t];
            }
        });
        double nearMs = sw.Elapsed.TotalMilliseconds;

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
            UnknownCount: _n, GridNodesX: _nx, GridNodesY: _ny, GridPitchM: h,
            ProjectionOrder: _m, NearRadiusM: radius,
            NearEntries: colIdx.LongLength, NearCellPairs: entry.CellPairCount,
            PaddedGridNodes: (long)_px * _py,
            ProjectionMs: projectionMs, GridKernelMs: gridMs, RemainderTableMs: tableMs,
            NearFillMs: nearMs,
            PreconditionerMs: precondMs, PreconditionerNonZeros: nnz,
            FactorNonZeros: factorNnz, NearExactRetained: st.KeepNearExact);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The projection
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Each basis's support bounding box: its centre, and its larger dimension. The box is the
    /// GRID rectangles of the two cells — a cut cell's metal is inside its rectangle, so this bounds
    /// the support in every case, which is what the stencil guard needs.</summary>
    private static ((double X, double Y)[] Centres, double[] Spans) SupportBoxes(PlanarMesh mesh)
    {
        int n = mesh.Bases.Count;
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

    /// <summary>
    /// <c>V⁻¹</c> where <c>V[a,k] = ξ_k^a</c> and <c>ξ_k = (k − M/2)·h</c> — the stencil's own
    /// coordinates about its centre. Uniform grid ⇒ one inverse serves every basis, which is what makes
    /// the projection O(N) with a tiny constant instead of an <c>(M+1)³</c> solve per basis.
    /// </summary>
    private static double[,] InverseVandermonde(int m, double h)
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

    private AimStencil Project(PlanarMesh mesh, int i, (double X, double Y) centre,
                               double gx0, double gy0, double h, double[,] vInv)
    {
        var basis = mesh.Bases[i];

        int p0 = (int)Math.Round((centre.X - gx0) / h - 0.5 * _m);
        int q0 = (int)Math.Round((centre.Y - gy0) / h - 0.5 * _m);
        p0 = Math.Clamp(p0, 0, _nx - 1 - _m);
        q0 = Math.Clamp(q0, 0, _ny - 1 - _m);

        double xs = gx0 + (p0 + 0.5 * _m) * h;
        double ys = gy0 + (q0 + 0.5 * _m) * h;

        var (mJ, mQ) = Moments(mesh, basis, xs, ys);

        return new AimStencil
        {
            P0 = p0, Q0 = q0,
            Current   = Coefficients(mJ, vInv),
            Charge    = Coefficients(mQ, vInv),
            Direction = basis.Direction,
        };
    }

    /// <summary><c>λ = V⁻¹ m V⁻ᵀ</c>, flattened row-major over the stencil.</summary>
    private Complex[] Coefficients(double[,] moments, double[,] vInv)
    {
        int s = _side;
        var tmp = new double[s, s];
        for (int k = 0; k < s; k++)
            for (int b = 0; b < s; b++)
            {
                double acc = 0;
                for (int a = 0; a < s; a++) acc += vInv[k, a] * moments[a, b];
                tmp[k, b] = acc;
            }

        var lam = new Complex[s * s];
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
    /// <c>∫ w (x−x_s)^a (y−y_s)^b dS</c> for the current weight and for the charge pulse, taken through
    /// the fill's own weight evaluation so the projected object is the operator's own basis.
    /// </summary>
    private (double[,] Current, double[,] Charge) Moments(PlanarMesh mesh, PlanarBasis basis,
                                                          double xs, double ys)
    {
        int s = _side;
        var mJ = new double[s, s];
        var mQ = new double[s, s];

        var (ra, rb) = PlanarFill.RampHalvesOf(mesh, basis);
        var (da, db) = PlanarBasisFunctions.Halves(mesh, basis);

        // Enough nodes that the rule is exact on the polynomial part: a whole rectangle's integrand is
        // degree (1 + a + b) ≤ 2M+1, and a strip's bilinear map roughly doubles that.
        int nodes = 2 * _m + 6;

        Accumulate(mJ, mesh.Cells[ra.CellIndex], ra, basis.Direction, 1.0, xs, ys, nodes);
        Accumulate(mJ, mesh.Cells[rb.CellIndex], rb, basis.Direction, 1.0, xs, ys, nodes);

        Accumulate(mQ, mesh.Cells[da.CellIndex], PlanarFill.PulseAt(mesh, da.CellIndex),
                   PlanarBasisDirection.X, da.Sign, xs, ys, nodes);
        Accumulate(mQ, mesh.Cells[db.CellIndex], PlanarFill.PulseAt(mesh, db.CellIndex),
                   PlanarBasisDirection.X, db.Sign, xs, ys, nodes);

        return (mJ, mQ);
    }

    private void Accumulate(double[,] target, PlanarCell cell, PlanarFill.CellWeight weight,
                            PlanarBasisDirection dir, double sign, double xs, double ys, int nodes)
    {
        int s = _side;
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

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The near set
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every pair that is either within <paramref name="radius"/> or whose stencils OVERLAP, as CSR
    /// over the full matrix. <b>The second criterion is not belt-and-braces</b>: it is what makes the
    /// grid kernel's value at zero separation cancel exactly, and therefore what makes it legitimate
    /// for that value to be arbitrary. See the file header.
    /// </summary>
    private (int[] RowPtr, int[] ColIdx) NearSet((double X, double Y)[] centres, double[] spans,
                                                 double radius, double h)
    {
        // A stencil spans M pitches; two stencils overlap only if their centres are within about
        // (M+1)·h on each axis, so this bound cannot miss one.
        double stencilReach = (_m + 1.5) * h;
        double search = Math.Max(radius, stencilReach * 1.5);
        double maxSpan = 0;
        foreach (double s in spans) maxSpan = Math.Max(maxSpan, s);
        search = Math.Max(search, maxSpan);

        var buckets = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < _n; i++)
        {
            var key = ((int)Math.Floor(centres[i].X / search), (int)Math.Floor(centres[i].Y / search));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(i);
        }

        var rows = new List<int>[_n];
        double r2 = radius * radius;

        PlanarFill.ForRowsOf(PlanarFillSettings.Default, _n, i =>
        {
            var mine = new List<int>();
            int bx = (int)Math.Floor(centres[i].X / search);
            int by = (int)Math.Floor(centres[i].Y / search);
            var si = _stencils[i];

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
                        {
                            var sj = _stencils[j];
                            near = Math.Abs(si.P0 - sj.P0) <= _m && Math.Abs(si.Q0 - sj.Q0) <= _m;
                        }
                        if (near) mine.Add(j);
                    }
                }
            mine.Sort();
            rows[i] = mine;
        });

        var rowPtr = new int[_n + 1];
        for (int i = 0; i < _n; i++) rowPtr[i + 1] = rowPtr[i] + rows[i].Count;
        var colIdx = new int[rowPtr[_n]];
        for (int i = 0; i < _n; i++) rows[i].CopyTo(colIdx, rowPtr[i]);
        return (rowPtr, colIdx);
    }

    /// <summary>
    /// For every stored position, the position holding its transpose. The near set is symmetric
    /// because both of its criteria are, so this always exists — and the assertion below says so
    /// rather than silently leaving a zero where an entry belongs.
    /// </summary>
    private int[] MirrorIndex()
    {
        var mirror = new int[_colIdx.Length];
        for (int i = 0; i < _n; i++)
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j >= i) { mirror[k] = k; continue; }
                int lo = _rowPtr[j], hi = _rowPtr[j + 1] - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_colIdx[mid] == i) { found = mid; break; }
                    if (_colIdx[mid] < i) lo = mid + 1; else hi = mid - 1;
                }
                if (found < 0)
                    throw new InvalidOperationException(
                        $"The near set is not symmetric: ({i}, {j}) is in it and ({j}, {i}) is not. " +
                        "Both nearness criteria are symmetric, so this is a bug in the near-set " +
                        "construction rather than a configuration.");
                mirror[k] = found;
            }
        return mirror;
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
                Complex ca = a.Charge[k * s + l];
                Complex ja = a.Current[k * s + l];
                if (ca == Complex.Zero && ja == Complex.Zero) continue;
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
        Convolve(_bufX, _hatA);
        Convolve(_bufY, _hatA);
        Convolve(_bufQ, _hatQ);

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

    private static int NextPow2(int n) { int p = 1; while (p < n) p <<= 1; return p; }

    /// <summary>Wraps the absolute-offset kernel table into the <c>Px×Py</c> circulant and transforms
    /// it. Negative offsets land in the upper half of each axis, which is what makes the cyclic
    /// convolution agree with the linear one over the sub-block the grid actually occupies.</summary>
    private Complex[] EmbedAndTransform(Complex[] g)
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

    private void Convolve(Complex[] buf, Complex[] hat)
    {
        Transform2(buf, forward: true);
        for (long i = 0; i < buf.LongLength; i++) buf[i] *= hat[i];
        Transform2(buf, forward: false);
    }

    /// <summary>Separable 2-D transform over the <c>Px×Py</c> buffer, row-major. FftFlat's
    /// <c>Inverse</c> carries the 1/N per axis, so the pair round-trips without a rescale.</summary>
    private void Transform2(Complex[] buf, bool forward)
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

    /// <summary>True when <c>(i, j)</c> is in the near set — what the near-set completeness gate asks.</summary>
    public bool IsNear(int i, int j)
    {
        for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++) if (_colIdx[k] == j) return true;
        return false;
    }

    /// <summary>The two stencils' node index boxes — the overlap gate reads these rather than
    /// re-deriving them from the settings.</summary>
    public (int P0, int Q0) StencilOrigin(int i) => (_stencils[i].P0, _stencils[i].Q0);
}
