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
// R-fil-11 — DETERMINISM. Parallelism is over ROWS of each packed triangle; every entry is written
// exactly once by exactly one thread, and the accumulation inside an entry is an ordinary sequential
// loop over a fixed node order. There is no shared accumulator and no dictionary iteration anywhere
// on this path.

using System.Collections.Concurrent;
using System.Numerics;
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
    bool                  Parallel                = true)
{
    public static readonly PlanarFillSettings Default = new();

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
/// D6's frequency-independent core: the four purely geometric integrals, for every cell pair (the
/// scalar half) and every same-direction basis pair (the vector half). Built once per mesh; every
/// frequency of a sweep reuses it.
/// </summary>
public sealed class PlanarFillCores
{
    public PlanarMesh          Mesh     { get; }
    public PlanarFillSettings  Settings { get; }

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

    /// <summary>How many cell-pair core integrals were evaluated — the cost number Tier 8 reports.</summary>
    public long ScalarPairs { get; }
    /// <summary>…and how many same-direction basis-pair core integrals.</summary>
    public long VectorPairs { get; }

    // Packed upper triangles. The scalar cores are AREA-NORMALISED (so the constant core is exactly
    // 1 and is not stored); the vector cores carry the rooftop weights ξ/Area and are summed over the
    // pair's four cell-pair combinations at build time, because the ω-dependent coefficients multiply
    // the whole sum.
    internal readonly double[] S0, SLog;
    internal readonly double[]? SRad;

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
    internal readonly double[] VX0, VXLog, VY0, VYLog;
    internal readonly double[]? VXRad, VYRad;
    internal readonly double[] VXArea, VYArea;      // ∫w_m · ∫w_n, per pair — trivial but wanted per entry

    internal PlanarFillCores(PlanarMesh mesh, PlanarFillSettings settings,
                             double minCellEdge, double extent, double rhoFloor,
                             double[] s0, double[] sLog, double[]? sRad,
                             int[] xBases, int[] yBases, int[] dirPos,
                             double[] vx0, double[] vxLog, double[]? vxRad, double[] vxArea,
                             double[] vy0, double[] vyLog, double[]? vyRad, double[] vyArea,
                             long scalarPairs, long vectorPairs)
    {
        Mesh = mesh; Settings = settings;
        MinCellEdgeM = minCellEdge; ExtentM = extent; RhoFloorM = rhoFloor;
        S0 = s0; SLog = sLog; SRad = sRad;
        XBases = xBases; YBases = yBases; DirPos = dirPos;
        VX0 = vx0; VXLog = vxLog; VXRad = vxRad; VXArea = vxArea;
        VY0 = vy0; VYLog = vyLog; VYRad = vyRad; VYArea = vyArea;
        ScalarPairs = scalarPairs; VectorPairs = vectorPairs;
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
        int a = Math.Min(cellA, cellB), b = Math.Max(cellA, cellB);
        long k = (long)a * CellCount - (long)a * (a - 1) / 2 + (b - a);
        return (S0[k], SLog[k], SRad is null ? 0.0 : SRad[k]);
    }

    /// <summary>Bytes held by the cached cores — Tier 8 reports this beside the matrix's own.</summary>
    public long CoreBytes =>
        8L * (S0.Length + SLog.Length + (SRad?.Length ?? 0)
            + VX0.Length + VXLog.Length + (VXRad?.Length ?? 0) + VXArea.Length
            + VY0.Length + VYLog.Length + (VYRad?.Length ?? 0) + VYArea.Length);
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
    /// </summary>
    public static PlanarFillCores BuildCores(PlanarMesh mesh, PlanarFillSettings? settings = null)
    {
                // Every fill in the engine passes through here, so this is the one place the settings
        // have to be sound — see PlanarFillSettings.Validate for why each check exists.
        (settings ?? PlanarFillSettings.Default).Validate();
ArgumentNullException.ThrowIfNull(mesh);
        var st = settings ?? PlanarFillSettings.Default;

        int n = mesh.Bases.Count;
        GuardCeiling(n);

        int m = mesh.Cells.Count;
        double minEdge = double.PositiveInfinity;
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var c in mesh.Cells)
        {
            minEdge = Math.Min(minEdge, Math.Min(c.Width, c.Height));
            x0 = Math.Min(x0, c.XMin); y0 = Math.Min(y0, c.YMin);
            x1 = Math.Max(x1, c.XMax); y1 = Math.Max(y1, c.YMax);
        }
        if (m == 0) { minEdge = 0; x0 = y0 = x1 = y1 = 0; }
        double extent   = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        double rhoFloor = st.RhoFloorFraction * minEdge;

        bool wantRad = st.Order >= PlanarExtractionOrder.Linear;

        // ── the scalar half: one entry per unordered CELL pair (D4) ───────────────────────────
        long sCount = (long)m * (m + 1) / 2;
        var s0   = new double[sCount];
        var sLog = new double[sCount];
        var sRad = wantRad ? new double[sCount] : null;

        ForRows(st, m, a =>
        {
            var wa = Pulse(a);
            for (int b = a; b < m; b++)
            {
                long k = Packed(a, b, m);
                var (c0, cl, cr) = PairCores(mesh, wa, Pulse(b), PlanarBasisDirection.X, wantRad, st);
                s0[k] = c0; sLog[k] = cl;
                if (sRad is not null) sRad[k] = cr;
            }
        });

        // ── the vector half: one entry per unordered SAME-DIRECTION basis pair (D5) ───────────
        var xb = new List<int>();
        var yb = new List<int>();
        for (int i = 0; i < n; i++)
            (mesh.Bases[i].Direction == PlanarBasisDirection.X ? xb : yb).Add(i);

        var (vx0, vxLog, vxRad, vxArea) =
            BuildDirectionCores(mesh, [.. xb], PlanarBasisDirection.X, wantRad, st);
        var (vy0, vyLog, vyRad, vyArea) =
            BuildDirectionCores(mesh, [.. yb], PlanarBasisDirection.Y, wantRad, st);

        long vCount = (long)xb.Count * (xb.Count + 1) / 2 + (long)yb.Count * (yb.Count + 1) / 2;

        var dirPos = new int[n];
        for (int i = 0; i < xb.Count; i++) dirPos[xb[i]] = i;
        for (int i = 0; i < yb.Count; i++) dirPos[yb[i]] = i;

        return new PlanarFillCores(mesh, st, minEdge, extent, rhoFloor,
                                   s0, sLog, sRad, [.. xb], [.. yb], dirPos,
                                   vx0, vxLog, vxRad, vxArea,
                                   vy0, vyLog, vyRad, vyArea,
                                   sCount, vCount);
    }

    private static (double[] C0, double[] CLog, double[]? CRad, double[] CArea) BuildDirectionCores(
        PlanarMesh mesh, int[] idx, PlanarBasisDirection dir, bool wantRad, PlanarFillSettings st)
    {
        int k = idx.Length;
        long count = (long)k * (k + 1) / 2;
        var c0    = new double[count];
        var cLog  = new double[count];
        var cRad  = wantRad ? new double[count] : null;
        var cArea = new double[count];

        // Each rooftop's two halves, resolved once — the inner loop is hot and Halves() would
        // otherwise be re-derived O(N²) times.
        var halves = new (CellWeight A, CellWeight B, double Moment)[k];
        for (int i = 0; i < k; i++)
        {
            var basis = mesh.Bases[idx[i]];
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, basis);
            var ca = mesh.Cells[ha.CellIndex];
            var cb = mesh.Cells[hb.CellIndex];
            // ∫ w dS over a half is (its extent along the flow direction)/2 — see the file header.
            double moment = 0.5 * (Extent(ca, dir) + Extent(cb, dir));
            halves[i] = (new CellWeight(ha.CellIndex, +1.0, ha.OuterEdge, true),
                         new CellWeight(hb.CellIndex, -1.0, hb.OuterEdge, true),
                         moment);
        }

        ForRows(st, k, i =>
        {
            var (ma, mb, mMom) = halves[i];
            for (int j = i; j < k; j++)
            {
                var (na, nb, nMom) = halves[j];
                long p = Packed(i, j, k);

                // Unrolled rather than looped over a temporary array: this is the O(N²) inner
                // statement of the whole slice, and an allocation here shows up as GC pressure at
                // the R17 ceiling.
                var (t00, l00, r00) = PairCores(mesh, ma, na, dir, wantRad, st);
                var (t01, l01, r01) = PairCores(mesh, ma, nb, dir, wantRad, st);
                var (t10, l10, r10) = PairCores(mesh, mb, na, dir, wantRad, st);
                var (t11, l11, r11) = PairCores(mesh, mb, nb, dir, wantRad, st);
                double a0 = t00 + t01 + t10 + t11;
                double aLog = l00 + l01 + l10 + l11;
                double aRad = r00 + r01 + r10 + r11;

                c0[p] = a0; cLog[p] = aLog; cArea[p] = mMom * nMom;
                if (cRad is not null) cRad[p] = aRad;
            }
        });

        return (c0, cLog, cRad, cArea);
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

        var mesh = cores.Mesh;
        int m = mesh.Cells.Count;
        var st = cores.Settings;
        var terms = termsQ.With(st.Order, cores.RhoFloorM);
        var rem = Remainder(terms, cores);

        var p = new Mat<Complex>(m, m);
        ForRows(st, m, a =>
        {
            var wa = Pulse(a);
            for (int b = a; b < m; b++)
            {
                long k = Packed(a, b, m);
                Complex v = terms.Inverse * cores.S0[k] + terms.Log * cores.SLog[k];
                if (terms.ExtractsConstant) v += terms.Constant;               // area-normalised ⇒ core = 1
                if (terms.ExtractsLinear && cores.SRad is not null) v += terms.Linear * cores.SRad[k];
                v += PairRemainder(mesh, wa, Pulse(b), PlanarBasisDirection.X, rem, st);
                p[a, b] = v;
                p[b, a] = v;                                                    // R-fil-2, structurally
            }
        });
        return p;
    }

    /// <summary>
    /// The full Galerkin matrix at one angular frequency. <b>R-fil-2: computed on <c>m ≤ n</c> and
    /// mirrored, so <c>Z[m,n]</c> and <c>Z[n,m]</c> are bit-identical by construction</b> rather than
    /// by the Green's function's reciprocity happening to come out — that is a different question and
    /// gets its own test.
    /// </summary>
    public static Mat<Complex> Fill(PlanarFillCores cores, PlanarKernelTerms termsA,
                                  PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        var mesh = cores.Mesh;
        int n = mesh.Bases.Count;
        GuardCeiling(n);

        var p = ScalarPotentialMatrix(cores, termsQ);
        var z = new Mat<Complex>(n, n);

        // ── the scalar block, assembled from P by signed differences (D4) ─────────────────────
        Complex scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        var halves = new (RooftopHalf A, RooftopHalf B)[n];
        for (int i = 0; i < n; i++) halves[i] = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);

        var st = cores.Settings;
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
                z[i, j] = scalarScale * s;
            }
        });

        // ── the vector block, same direction only (D5) ────────────────────────────────────────
        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
        var termsAr = termsA.With(st.Order, cores.RhoFloorM);
        var remA = Remainder(termsAr, cores);

        AddDirectionBlock(z, cores, cores.XBases, PlanarBasisDirection.X,
                          cores.VX0, cores.VXLog, cores.VXRad, cores.VXArea, termsAr, remA, vectorScale);
        AddDirectionBlock(z, cores, cores.YBases, PlanarBasisDirection.Y,
                          cores.VY0, cores.VYLog, cores.VYRad, cores.VYArea, termsAr, remA, vectorScale);

        // ── R-fil-2: mirror, bit-identically ─────────────────────────────────────────────────
        // Row-wise, so each row is written by exactly one iteration — R-fil-11's shape, and the
        // assignment is a copy rather than a recomputation, so the two triangles cannot differ in
        // their last bit the way a "compute both and trust reciprocity" fill would.
        ForRows(st, n, j =>
        {
            for (int i = 0; i < j; i++) z[j, i] = z[i, j];
        });

        return z;
    }

    private static void AddDirectionBlock(Mat<Complex> z, PlanarFillCores cores, int[] idx,
                                          PlanarBasisDirection dir,
                                          double[] c0, double[] cLog, double[]? cRad, double[] cArea,
                                          PlanarKernelTerms terms, Func<double, Complex> rem,
                                          Complex scale)
    {
        var mesh = cores.Mesh;
        var st   = cores.Settings;
        int k    = idx.Length;

        var halves = new (CellWeight A, CellWeight B)[k];
        for (int i = 0; i < k; i++)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[idx[i]]);
            halves[i] = (new CellWeight(ha.CellIndex, +1.0, ha.OuterEdge, true),
                         new CellWeight(hb.CellIndex, -1.0, hb.OuterEdge, true));
        }

        ForRows(st, k, i =>
        {
            var (ma, mb) = halves[i];
            for (int j = i; j < k; j++)
            {
                var (na, nb) = halves[j];
                long q = Packed(i, j, k);

                Complex v = terms.Inverse * c0[q] + terms.Log * cLog[q];
                if (terms.ExtractsConstant) v += terms.Constant * cArea[q];
                if (terms.ExtractsLinear && cRad is not null) v += terms.Linear * cRad[q];

                Complex r = PairRemainder(mesh, ma, na, dir, rem, st)
                          + PairRemainder(mesh, ma, nb, dir, rem, st)
                          + PairRemainder(mesh, mb, na, dir, rem, st)
                          + PairRemainder(mesh, mb, nb, dir, rem, st);

                z[idx[i], idx[j]] += scale * (v + r);
            }
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
    /// The Galerkin matrix of a MULTI-LEVEL problem, with vertical (via) bases. Reduces to
    /// <see cref="Fill(PlanarFillCores, PlanarKernelTerms, PlanarKernelTerms, double)"/>'s answer on a
    /// one-level mesh with no vias, which is what <c>PlanarFillTests</c> gates it against.
    /// </summary>
    public static Mat<Complex> FillMultiLevel(PlanarFillCores cores, PlanarKernelSet set,
                                              PlanarLevels levels, double omega,
                                              PlanarFillDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(cores);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(levels);

        var mesh = cores.Mesh;
        int n = mesh.Bases.Count;
        GuardCeiling(n);
        var st = cores.Settings;

        // ── the scalar half: P over CELLS, kernel chosen per (level, level) ───────────────────
        int m = mesh.Cells.Count;
        var p = new Mat<Complex>(m, m);
        var remCache = new Dictionary<(GreensKernel, double, double), Func<double, Complex>>();

        Func<double, Complex> RemFor(GreensKernel k, double za, double zb)
        {
            double lo = Math.Min(za, zb), hi = Math.Max(za, zb);
            lock (remCache)
            {
                if (remCache.TryGetValue((k, lo, hi), out var hit)) return hit;
                var f = Remainder(set.Get(k, lo, hi), cores);
                remCache[(k, lo, hi)] = f;
                return f;
            }
        }

        ForRows(st, m, a =>
        {
            var wa = Pulse(a);
            double za = levels.Of(mesh.Cells[a].LayerIndex);
            for (int b = a; b < m; b++)
            {
                double zb = levels.Of(mesh.Cells[b].LayerIndex);
                var terms = set.Get(GreensKernel.ScalarPotential, za, zb).With(st.Order, cores.RhoFloorM);
                long k = Packed(a, b, m);

                Complex v = terms.Inverse * cores.S0[k] + terms.Log * cores.SLog[k];
                if (terms.ExtractsConstant) v += terms.Constant;
                if (terms.ExtractsLinear && cores.SRad is not null) v += terms.Linear * cores.SRad[k];
                v += PairRemainder(mesh, wa, Pulse(b), PlanarBasisDirection.X,
                                   RemFor(GreensKernel.ScalarPotential, za, zb), st);
                p[a, b] = v;
                p[b, a] = v;
            }
        });

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
                z[i, j] = scalarScale * s;
            }
        });

        // ── the vector half ───────────────────────────────────────────────────────────────────
        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;

        // The via z-integral's per-SPAN objects (ViaZIntegral). Both are keyed on the z spans alone,
        // never on the cell pair, so a mesh whose vias all join the same two levels — which is every
        // via of one drawn layer — builds each exactly once however many vertical unknowns it carries.
        var zzTerms  = new Dictionary<(double, double, double, double), (PlanarKernelTerms T, Func<double, Complex> R)>();
        var mixedDer = new Dictionary<(double, double, double), Func<double, Complex>>();

        (PlanarKernelTerms, Func<double, Complex>) ZzTermsFor(ViaZIntegral.Span si, ViaZIntegral.Span sj)
        {
            var key = (si.Lo, si.Hi, sj.Lo, sj.Hi);
            lock (zzTerms)
            {
                if (zzTerms.TryGetValue(key, out var hit)) return hit;
                // M2 (R-zz-3) — the ẑẑ block ALONE may take its kernel from direct Sommerfeld
                // integration rather than from the DCIM fit. Reachable as a setting, exactly like
                // UseRadialTable = false, because M1 measured the fit as the failure and measured
                // every DcimSettings knob as unable to fix it. Nothing else in the fill changes:
                // the singular half is closed form in z and was never fitted, and the horizontal
                // and scalar blocks are untouched.
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
                var made = (t, r);
                zzTerms[key] = made;
                return made;
            }
        }

        Func<double, Complex> MixedDerivativeFor(ViaZIntegral.Span sv, double zh)
        {
            var key = (sv.Lo, sv.Hi, zh);
            lock (mixedDer)
            {
                if (mixedDer.TryGetValue(key, out var hit)) return hit;
                var raw = ViaZIntegral.AveragedMixedDerivative(set, sv, zh, st.ViaZNodes);
                Func<double, Complex> made = raw;
                if (st.UseRadialTable)
                {
                    double spacing = st.TableCellFraction * cores.MinCellEdgeM;
                    made = RadialRemainderTable.BuildFrom(
                        raw, Math.Max(cores.ExtentM, spacing * 8), spacing, st.MaxTableSamples).Evaluate;
                }
                mixedDer[key] = made;
                return made;
            }
        }

        ForRows(st, n, i =>
        {
            var bi = mesh.Bases[i];
            for (int j = i; j < n; j++)
            {
                var bj = mesh.Bases[j];
                bool zi = bi.Direction == PlanarBasisDirection.Z;
                bool zj = bj.Direction == PlanarBasisDirection.Z;

                if (!zi && !zj)
                {
                    // L8c's D5 is untouched: an X-rooftop and a Y-rooftop are pointwise orthogonal
                    // and the formulation-C vector kernel has no xy component.
                    if (bi.Direction != bj.Direction) continue;
                    double za = levels.Of(bi.LayerIndex), zb = levels.Of(bj.LayerIndex);
                    var t = set.Get(GreensKernel.VectorPotential, za, zb).With(st.Order, cores.RhoFloorM);
                    z[i, j] += vectorScale * HorizontalVectorEntry(
                        mesh, cores, i, j, bi, bj, t, RemFor(GreensKernel.VectorPotential, za, zb), st);
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

                    var si = SpanOf(levels, bi);
                    var sj = SpanOf(levels, bj);
                    var (t, rem) = ZzTermsFor(si, sj);
                    Complex core = CellPairPotential(mesh, cores, bi.CellA, bj.CellA, t, rem, st);

                    // SINGULAR half: the two extracted asymptotes, whose coefficients do not depend on
                    // the heights and whose depths are exactly Δ and Σ_b, integrated over the two
                    // prisms in CLOSED FORM in z. This is the piece a Gauss rule cannot carry — see
                    // ViaZIntegral's header — and it is where the 0.673·(ℓ/w) went.
                    core += SingularPrismPart(mesh, set, bi.CellA, bj.CellA, si, sj, st);

                    z[i, j] += vectorScale * si.Length * sj.Length * core;
                }
                else
                {
                    var vertical   = zi ? bi : bj;
                    var horizontal = zi ? bj : bi;
                    var sv = SpanOf(levels, vertical);
                    double zh = levels.Of(horizontal.LayerIndex);
                    // R-viz-5: ONE z-integral, and it is folded into the radial derivative the block
                    // already consumes — so MixedEntry is called exactly as often as it was.
                    z[i, j] += vectorScale * sv.Length
                             * MixedEntry(mesh, vertical, horizontal,
                                          MixedDerivativeFor(sv, zh), cores.RhoFloorM, st);
                }
            }
        });

        ForRows(st, n, j =>
        {
            for (int i = 0; i < j; i++) z[j, i] = z[i, j];
        });
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
    /// </summary>
    private static Complex SingularPrismPart(PlanarMesh mesh, PlanarKernelSet set,
                                             int cellA, int cellB,
                                             ViaZIntegral.Span si, ViaZIntegral.Span sj,
                                             PlanarFillSettings st)
    {
        var asym = set.Asymptote(GreensKernel.VerticalVectorPotential, si.Mid, sj.Mid);
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
    /// own height pairing's terms.</summary>
    private static Complex HorizontalVectorEntry(PlanarMesh mesh, PlanarFillCores cores,
                                                 int basisI, int basisJ,
                                                 PlanarBasis bi, PlanarBasis bj,
                                                 PlanarKernelTerms terms, Func<double, Complex> rem,
                                                 PlanarFillSettings st)
    {
        var dir = bi.Direction;
        var (ia, ib) = PlanarBasisFunctions.Halves(mesh, bi);
        var (ja, jb) = PlanarBasisFunctions.Halves(mesh, bj);
        var ma = new CellWeight(ia.CellIndex, +1.0, ia.OuterEdge, true);
        var mb = new CellWeight(ib.CellIndex, -1.0, ib.OuterEdge, true);
        var na = new CellWeight(ja.CellIndex, +1.0, ja.OuterEdge, true);
        var nb = new CellWeight(jb.CellIndex, -1.0, jb.OuterEdge, true);

        // ── L9d: D6's cached geometric cores, which are the SAME numbers this used to re-integrate.
        //
        // The height pairing enters only through the coefficients, so a same-direction pair's four
        // panel quadratures are exactly BuildDirectionCores' own already-summed entry. Reusing it
        // also puts this expression on L8c's own associativity (one coefficient times the summed
        // core, rather than the sum of four coefficient-times-core products), which is why the
        // one-level reduction against PlanarFill.Fill gets tighter rather than looser.
        int di = cores.DirPos[basisI], dj = cores.DirPos[basisJ];
        int k = dir == PlanarBasisDirection.X ? cores.XBases.Length : cores.YBases.Length;
        long q = Packed(Math.Min(di, dj), Math.Max(di, dj), k);
        var (vc0, vcLog, vcRad, vcArea) = dir == PlanarBasisDirection.X
            ? (cores.VX0, cores.VXLog, cores.VXRad, cores.VXArea)
            : (cores.VY0, cores.VYLog, cores.VYRad, cores.VYArea);

        Complex v = terms.Inverse * vc0[q] + terms.Log * vcLog[q];
        if (terms.ExtractsConstant) v += terms.Constant * vcArea[q];
        if (terms.ExtractsLinear && vcRad is not null) v += terms.Linear * vcRad[q];

        foreach (var (wa, wb) in new[] { (ma, na), (ma, nb), (mb, na), (mb, nb) })
            v += PairRemainder(mesh, wa, wb, dir, rem, st);

        return v;
    }

    /// <summary>The area-averaged potential coefficient between two CELLS at one kernel — the same
    /// object <c>P</c> is built from, exposed here because the ẑẑ block is exactly it.</summary>
    private static Complex CellPairPotential(PlanarMesh mesh, PlanarFillCores cores, int cellA, int cellB,
                                             PlanarKernelTerms terms, Func<double, Complex> rem,
                                             PlanarFillSettings st)
    {
        int a = Math.Min(cellA, cellB), b = Math.Max(cellA, cellB);
        long k = Packed(a, b, mesh.Cells.Count);
        Complex v = terms.Inverse * cores.S0[k] + terms.Log * cores.SLog[k];
        if (terms.ExtractsConstant) v += terms.Constant;
        if (terms.ExtractsLinear && cores.SRad is not null) v += terms.Linear * cores.SRad[k];
        v += PairRemainder(mesh, Pulse(a), Pulse(b), PlanarBasisDirection.X, rem, st);
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
    /// </summary>
    private static Complex MixedEntry(PlanarMesh mesh, PlanarBasis vertical, PlanarBasis horizontal,
                                      Func<double, Complex> dG,
                                      double rhoFloor, PlanarFillSettings st)
    {
        var v = mesh.Cells[vertical.CellA];
        var (ha, hb) = PlanarBasisFunctions.Halves(mesh, horizontal);
        bool alongX = horizontal.Direction == PlanarBasisDirection.X;
        double floor = Math.Max(rhoFloor, 1e-30);

        Complex total = Complex.Zero;
        // BOTH halves add with the SAME sign, and that is worth stating because the divergence's do
        // not: a rooftop's current flows one way through both of its cells and the ± distinction
        // belongs to ∇·f. Getting it wrong here cancels the block instead of assembling it.
        foreach (var half in new[] { ha, hb })
        {
            var c = mesh.Cells[half.CellIndex];
            double tau = SeparationRatio(v, c);
            int nodes = tau < st.NearRatio ? st.NearNodes
                      : tau < st.FarRatio  ? st.MidNodes : st.FarNodes;
            int panels = tau < st.NearRatio ? st.TouchPanels : 1;
            var (gx, gw) = Legendre.Nodes(nodes);
            var t = PanelEdges(panels);

            double invAv = 1.0 / v.Area, invAc = 1.0 / c.Area;
            Complex sum = Complex.Zero;

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

                    for (int qx = 0; qx < panels; qx++)
                    for (int qy = 0; qy < panels; qy++)
                    {
                        double xa2 = c.XMin + t[qx] * c.Width,  xb2 = c.XMin + t[qx + 1] * c.Width;
                        double ya2 = c.YMin + t[qy] * c.Height, yb2 = c.YMin + t[qy + 1] * c.Height;
                        double dx = 0.5 * (xb2 - xa2), dy = 0.5 * (yb2 - ya2);
                        double mx = 0.5 * (xa2 + xb2), my = 0.5 * (ya2 + yb2);

                        for (int a = 0; a < nodes; a++)
                        for (int b = 0; b < nodes; b++)
                        {
                            double xp = mx + dx * gx[a], yp = my + dy * gx[b];
                            double rho = Math.Sqrt((x - xp) * (x - xp) + (y - yp) * (y - yp));
                            if (rho <= floor) continue;         // the integrand is ODD; the limit is 0
                            double weight = Math.Abs((alongX ? xp : yp) - half.OuterEdge) * invAc;
                            double du = (alongX ? x - xp : y - yp) / rho;
                            sum += gw[a] * gw[b] * dx * dy * wq * weight
                                 * Complex.ImaginaryOne * dG(rho) * du;
                        }
                    }
                }
            }
            total += sum;
        }
        return total;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The cell-pair integrals
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One cell of one basis's support, as the quadrature needs it: the cell, the sign that
    /// makes <c>ξ = Sigma·(coord − Edge)</c> non-negative, and whether the weight is the rooftop's
    /// linear ramp or the divergence pulse.</summary>
    internal readonly record struct CellWeight(int CellIndex, double Sigma, double Edge, bool Ramp);

    private static CellWeight Pulse(int cellIndex) => new(cellIndex, 1.0, 0.0, false);

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

    /// <summary>
    /// The smooth remainder over one ordered cell pair — plain double quadrature, because after the
    /// extraction the integrand is bounded and has no singularity at all. This is the ONLY part of
    /// the entry that has to be recomputed per frequency (D6).
    /// </summary>
    private static Complex PairRemainder(PlanarMesh mesh, CellWeight wa, CellWeight wb,
                                         PlanarBasisDirection dir, Func<double, Complex> rem,
                                         PlanarFillSettings st)
    {
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
        double dx = a.CenterX - b.CenterX, dy = a.CenterY - b.CenterY;
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

    private static void GuardCeiling(int n)
    {
        if (n > SurfaceMesher.UnknownCeiling)
            throw new InvalidOperationException(
                $"This mesh has {n:N0} unknowns, which is past the {SurfaceMesher.UnknownCeiling:N0}-unknown " +
                $"ceiling this kernel is built for ({(double)n * n * 16.0 / (1024 * 1024):N0} MB of dense " +
                $"complex matrix, against {(double)SurfaceMesher.UnknownCeiling * SurfaceMesher.UnknownCeiling * 16.0 / (1024 * 1024):N0} MB " +
                "at the ceiling). Lower Cells per wavelength, turn the edge mesh off, or analyse a " +
                "smaller region — full-wave analysis of a structure this size needs matrix " +
                "compression, which is not built.");
    }

    /// <summary>Packed upper-triangle index for <c>i ≤ j</c> in an <c>n×n</c> symmetric array.</summary>
    private static long Packed(int i, int j, int n) => (long)i * n - (long)i * (i - 1) / 2 + (j - i);

    /// <summary>
    /// R-fil-11's parallelism: over ROWS, each written exactly once. Never over a shared accumulator,
    /// so the answer does not depend on how the scheduler happened to interleave.
    /// </summary>
    private static void ForRows(PlanarFillSettings st, int count, Action<int> row)
    {
        if (st.Parallel && count > 8) System.Threading.Tasks.Parallel.For(0, count, row);
        else for (int i = 0; i < count; i++) row(i);
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
