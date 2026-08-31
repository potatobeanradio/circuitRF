// P11 (brief-em-p11-accelerated-static-capacitance.md) — THE CALIBRATION STANDARDS' STATIC
// CAPACITANCE, WITHOUT A DENSE m×m SOLVE.
//
// ── What was dense, and why it mattered more than its share of the time ───────────────────────────
//
// D7 references every de-embedded s-parameter to the line's own Z_c = γ/(jωC_pul), and C_pul comes
// from DIFFERENCING two calibration standards' static capacitances. Each of those is
// `P q = ε₀·1` over CELLS, with `P` the area-averaged scalar-potential coefficient matrix at ω → 0
// (`PlanarFill.ScalarPotentialMatrix` with `PlanarKernelTerms.StaticScalar`). Until this phase that
// solve was ALWAYS a dense m×m complex LU, whatever the run's settings said — so the accelerator
// moved the DUT's ceiling and left this one where it was, and `PlanarSolve.Run` had to refuse a
// wide-port de-embedded run AT SETUP whose DUT would have solved comfortably
// (brief-em-deembed-ceiling-closeout.md; the owner's taper's wide-port standard meshed at
// N = 6,466 against a 5,000-unknown dense ceiling).
//
// ── The observation this phase rests on ──────────────────────────────────────────────────────────
//
// `P` is EXACTLY the scalar block M5 already projects. The accelerated operator's charge stencil is
// a ± pair of cell pulses per basis; a cell-pulse operator is the same stencil with ONE cell and
// `sign = +1`. So this is not a second accelerator — it is the same projection, the same near-set
// rule, the same grid FFT and the same sparse-LU-preconditioned GMRES, with:
//
//   • ONE grid kernel (the static scalar) rather than two, and no ω anywhere;
//   • unknowns over CELLS rather than over basis functions, so the near set, the stencils and the
//     preconditioner are all m-sized;
//   • the near set's exact entries taken from `PlanarPulsePotential`, which IS the dense path's
//     own `P[a, b]` — the same class-memoised singular cores, not a second formulation of them.
//
// The shared parts live in `AimProjection` and `AimGridFft` (see `PlanarAim.cs`), moved there rather
// than copied, so there is one moment match and one convolution in this repository.
//
// ── THE TOLERANCE IS TIGHTER THAN THE DUT'S, AND THE REASON IS THE DIFFERENCING ──────────────────
//
// `CapacitancePerMetre` reports `(C₂ − C₁)/Δℓ`. The two totals agree to several digits by
// construction — the standards are the same cross-section and differ only in the bulk cells in the
// middle — so an error in either is amplified by `C/(C₂ − C₁)` in the answer. The DUT's 1e-8
// residual is sized for a current vector that is read directly; this one is sized for a difference,
// and `PlanarAimSettings.StaticTolerance` carries the measurement rather than a guess. See
// `PlanarP11StaticAimTests` for the ladder it came from.
//
// ── THE TWO KNOBS ARE SIZED FROM TWO DIFFERENT LENGTHS, AND THE MEASUREMENT SAYS WHY ────────────
//
// Every number below is the relative error in C_pul against the DENSE solve on the same meshes: the
// FR-4 hero's own 30.8 mm / 90.9 mm standards (m = 279 / 738) and the GaAs hero's 1.14 mm / 3.02 mm
// (m = 117 / 198), at the shipping mesh, one knob moved at a time from the shipped defaults.
//
// **The PITCH is sized from the largest SOURCE SUPPORT** — one CELL here, one BASIS (two cells) in
// M5. That is the same rule stated once, not a different rule: the stencil spans M pitches and has
// to be about half again the support it replaces, or the moment match is extrapolating outside the
// region the source actually occupies. `GridSpacingFactor` × cell span, FR-4 / GaAs:
// `0.125 -> 1.19e-7 / 2.00e-9`, `0.25 -> 1.11e-7 / 2.00e-9`, **`0.5 -> 1.04e-7 / 2.02e-9`**,
// `0.75 -> 1.60e-7 / 2.11e-9`, `1.0 -> 3.32e-7 / 2.56e-9`. **Flat below 0.5 and degrading above it**,
// so the shipped 0.5 is the cheapest point that is not paying for the coarsening — and a finer grid
// here buys nothing, unlike M5's own N-ladder where it bought two decades. (It does buy something
// when the near field is narrow: at a radius of 3 supports the same ladder reads
// `0.5 -> 2.69e-7`, `0.25 -> 1.31e-6`, `0.125 -> 2.45e-6`, i.e. the opposite sign. What that says is
// that the pitch and the radius are not independent when the radius is too small to hold the
// coupling — which is P8's finding in a different coordinate.)
//
// **The RADIUS is sized from the largest BASIS SUPPORT — M5's own quantity and M5's own number** —
// because the near/far boundary is a property of the KERNEL, not of what is being projected: it is
// the range over which the coupling is long-ranged, which is why P8 put a floor of 2h under it. Two
// accelerators on one mesh should draw that boundary in the same place. `NearRadiusFactor` in basis
// supports: `3 -> 2.69e-7 / 8.88e-7`, `4 -> 3.69e-7 / 1.61e-7`, `5 -> 1.98e-7 / 2.89e-8`,
// **`6 -> 1.04e-7 / 2.02e-9`**, `8 -> 3.39e-8 / 7.07e-15`, at 99 / 132 / 164 / 196 / 256 near entries
// per row. **This is the knob that costs and the knob that pays.** Reading the same factor in CELL
// spans instead — the obvious thing to do for a cell-pulse operator — would have halved M5's radius
// silently and left GaAs at 8.88e-7, inside the 1e-6 gate by 12%.
//
// **The PROJECTION ORDER is inert here, and that is worth writing down.** M = 2 / 3 / 4 / 5 give
// `1.05e-7 / 1.04e-7 / 1.08e-7 / 1.08e-7` on FR-4 and `2.15e-9 / 2.02e-9 / 2.00e-9 / 2.00e-9` on
// GaAs. Raising it is not a remedy for this operator; widening the near field is. The refusal in
// `TotalCapacitance` names both because the near field is the one that acts.

using System.Numerics;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using CSparse.Ordering;
using CSparse.Storage;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>What the accelerated static solve cost and how big it turned out to be — the numbers
/// P11's gates read, taken from the object rather than re-derived by a test.</summary>
/// <param name="CellCount">m — the unknown count of the static system, which is CELLS.</param>
/// <param name="NearCellClasses">How many distinct translation classes the near field's pulse
/// potentials integrated, plus any per-pair entries for a pair with a cut cell.</param>
/// <param name="FactorNonZeros">The sparse LU's L and U together, or 0 when the factorisation failed
/// and GMRES ran unpreconditioned.</param>
public sealed record PlanarStaticAimReport(
    int    CellCount,
    int    GridNodesX,
    int    GridNodesY,
    double GridPitchM,
    int    ProjectionOrder,
    double NearRadiusM,
    double NearRadiusFromSpanM,
    double NearRadiusFloorM,
    long   NearEntries,
    int    NearCellClasses,
    long   PaddedGridNodes,
    long   FactorNonZeros,
    double ProjectionMs,
    double NearSetMs,
    double NearFillMs,
    double CorrectionMs,
    double GridKernelMs,
    double PreconditionerMs,
    double SolveMs,
    int    Iterations,
    double Residual)
{
    /// <summary>Near entries as a fraction of the dense m×m — the number that says whether this
    /// near field is genuinely O(m) or merely a smaller O(m²).</summary>
    public double NearFillFraction => (double)NearEntries / CellCount / CellCount;

    /// <inheritdoc cref="PlanarAimReport.NearEntriesPerRow"/>
    public double NearEntriesPerRow => (double)NearEntries / Math.Max(1, CellCount);

    /// <summary>
    /// Bytes this holds once built, against <c>3·16·m²</c> for the dense solve it replaces (P, and
    /// the L and U a general LU builds beside it — the working set
    /// <see cref="PlanarDeembed.StaticCapacitance"/>'s own ceiling guard quotes).
    /// </summary>
    public long ResidentBytes =>
        16L * NearEntries                                  // the near correction
      + 4L * NearEntries + 4L * (CellCount + 1)            // the CSR index
      + 8L * (long)((ProjectionOrder + 1) * (ProjectionOrder + 1)) * CellCount   // the stencils
      + 16L * GridNodesX * GridNodesY                      // the grid kernel table
      + 16L * PaddedGridNodes * 2                          // its FFT hat, and the one scratch field
      + 20L * FactorNonZeros + 8L * (CellCount + 1)        // the sparse LU
      + 8L * CellCount;                                    // AMD permutation + its inverse

    /// <summary>What the dense solve this replaced would have held — the three m×m complex matrices
    /// <see cref="PlanarDeembed.StaticCapacitance"/>'s ceiling guard names.</summary>
    public long DenseBytes => 3L * 16L * CellCount * CellCount;
}

/// <summary>
/// <b>P11 — the static capacitance solve, accelerated.</b> Holds no m×m anything: a uniform-grid
/// static kernel, one cell-pulse stencil per cell, and the exact <c>P</c> restricted to the near set.
/// <see cref="TotalCapacitance"/> solves <c>P q = ε₀·1</c> by GMRES against the accelerated product,
/// with the near field's own sparse factorisation as the preconditioner, and sums the charges.
///
/// <para><b>One instance per solve.</b> The FFT plan and its scratch buffer are per-instance, exactly
/// as <see cref="PlanarAimOperator"/>'s are.</para>
/// </summary>
public sealed class PlanarStaticAim
{
    private readonly int _m, _side, _order, _nx, _ny, _px, _py;
    private readonly int[] _p0, _q0;
    private readonly double[] _lambda;            // [cell * side*side], row-major over the stencil
    private readonly Complex[] _g;                // grid kernel, by ABSOLUTE offset: [|dp| * _ny + |dq|]
    private readonly Complex[] _hat, _buf;
    private readonly AimGridFft _fft;
    private readonly int[] _rowPtr, _colIdx;
    private readonly Complex[] _nearCorrection;
    private readonly SparseLU? _preconditioner;
    private readonly PlanarAimSettings _st;
    private readonly PlanarPulsePotential _pulse;

    private readonly double _projectionMs, _nearSetMs, _nearFillMs, _correctionMs,
                            _gridKernelMs, _precondMs, _radiusFromSpanM, _radiusFloorM, _radiusM,
                            _pitchM;
    private readonly long _factorNnz;

    /// <summary>m — the static system's unknown count, which is CELLS.</summary>
    public int Size => _m;

    /// <summary>Iterations the last <see cref="TotalCapacitance"/> took, and the residual it reached.</summary>
    public int LastIterations { get; private set; }

    /// <inheritdoc cref="LastIterations"/>
    public double LastResidual { get; private set; }

    /// <summary>What it cost and how big it is. Valid after construction; the solve fields are filled
    /// by <see cref="TotalCapacitance"/>.</summary>
    public PlanarStaticAimReport Report { get; private set; }

    /// <summary>
    /// Builds the accelerated static operator for one mesh.
    /// </summary>
    /// <param name="cores">
    /// The mesh's cores. <b>Geometry-only is what this is for</b> — <see cref="PlanarFill.BuildGeometryOnlyCores"/>'
    /// O(N) shape is all it reads, and handing it full pair cores would mean the O(m²) build this
    /// phase exists to remove had already happened.
    /// </param>
    /// <param name="staticScalar">The ω → 0 scalar kernel — <see cref="PlanarKernelTerms.StaticScalar"/>.</param>
    /// <param name="slabHeightM">P8's near-radius floor is 2h and h is not derivable from a mesh, so
    /// it is required here for the same reason <see cref="PlanarAimGeometry.Build"/> requires it.</param>
    /// <param name="entryCores">An already-built core store for this same mesh, when the caller has
    /// one; null builds a private one. The store's insertions are idempotent, so sharing costs
    /// nothing and saves whichever singular cores the other consumer already warmed.</param>
    public static PlanarStaticAim Build(PlanarFillCores cores, PlanarKernelTerms staticScalar,
                                        double slabHeightM, PlanarAimSettings? settings = null,
                                        PlanarEntryCores? entryCores = null)
    {
        ArgumentNullException.ThrowIfNull(cores);
        ArgumentNullException.ThrowIfNull(staticScalar);
        var st = settings ?? PlanarAimSettings.Default;
        st.Validate();

        if (!(slabHeightM > 0))
            throw new ArgumentOutOfRangeException(nameof(slabHeightM), slabHeightM,
                "The near radius has a floor of 2h (PlanarAimSettings.NearRadiusMinM) and h is the " +
                "slab height, so it has to be handed in. Pass the problem's own Slab.HeightM; to run " +
                "without the floor, pass the height anyway and set NearRadiusMinM: 0.");

        // The kernel handed in is evaluated at ONE height pair, so a mesh whose cells sit on more
        // than one level is not a problem this operator models — whichever kernel built it. A
        // calibration standard is always a single-level uniform line (D3) and never reaches this.
        //
        // MIM-4 narrowed what this refusal is ABOUT rather than deleting it. It used to say a
        // multi-level mesh "needs a static Green's function at interior heights, which this
        // repository does not have"; InteriorStaticImages is that function, and
        // PlanarKernelTerms.StaticScalarAt hands one to this method for any single level at any
        // height. What is still missing is a static kernel for a CROSS-LEVEL cell pair — a
        // per-pairing set, the way PlanarKernelSet carries the full-wave one — which is a different
        // object from the one this argument takes.
        foreach (var c in cores.Mesh.Cells)
            if (c.LayerIndex != cores.Mesh.Cells[0].LayerIndex)
                throw new NotSupportedException(
                    "The accelerated static capacitance solve models a SINGLE conductor level, because " +
                    "the kernel it is handed is one function of ρ, evaluated at ONE height pair — " +
                    "PlanarKernelTerms.StaticScalar on the slab top, or StaticScalarAt at any interior " +
                    "height (MIM-4). A multi-level mesh needs a static kernel PER LEVEL PAIRING, which " +
                    "is a set rather than a function and which nothing builds yet. Solve this mesh's " +
                    "static system densely.");

        return new PlanarStaticAim(cores, staticScalar, slabHeightM, st,
                                   entryCores ?? new PlanarEntryCores(cores));
    }

    private PlanarStaticAim(PlanarFillCores cores, PlanarKernelTerms staticScalar, double slabHeightM,
                            PlanarAimSettings st, PlanarEntryCores entryCores)
    {
        var mesh = cores.Mesh;
        _st    = st;
        _m     = mesh.Cells.Count;
        _order = st.ProjectionOrder;
        _side  = _order + 1;
        _pulse = new PlanarPulsePotential(entryCores, staticScalar);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── the auxiliary grid ────────────────────────────────────────────────────────────────
        var centres = new (double X, double Y)[_m];
        var spans   = new double[_m];
        double maxSpan = 0;
        for (int i = 0; i < _m; i++)
        {
            var c = mesh.Cells[i];
            centres[i] = (c.CenterX, c.CenterY);
            spans[i]   = Math.Max(c.Width, c.Height);
            maxSpan    = Math.Max(maxSpan, spans[i]);
        }
        if (!(maxSpan > 0)) maxSpan = cores.MinCellEdgeM > 0 ? cores.MinCellEdgeM : 1.0;

        // The RADIUS's own length: the largest BASIS support, which is what PlanarAimGeometry sizes
        // its radius from — see the file header for the measurement that decided this is not the
        // cell span. A mesh with no bases (a single isolated cell) falls back to the cell span.
        double maxBasisSpan = 0;
        foreach (var b in mesh.Bases)
        {
            var ca = mesh.Cells[b.CellA];
            var cb = mesh.Cells[b.CellB];
            maxBasisSpan = Math.Max(maxBasisSpan,
                Math.Max(Math.Max(ca.XMax, cb.XMax) - Math.Min(ca.XMin, cb.XMin),
                         Math.Max(ca.YMax, cb.YMax) - Math.Min(ca.YMin, cb.YMin)));
        }
        if (!(maxBasisSpan > 0)) maxBasisSpan = maxSpan;

        double h = st.GridSpacingFactor * maxSpan;
        _pitchM  = h;

        // P8's floor, on the slab rather than on the metal's own dicing — see the file header.
        _radiusFromSpanM = st.NearRadiusFactor * maxBasisSpan;
        _radiusFloorM    = st.NearRadiusFloorFor(slabHeightM);
        _radiusM         = Math.Max(_radiusFromSpanM, _radiusFloorM);

        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var c in mesh.Cells)
        {
            x0 = Math.Min(x0, c.XMin); y0 = Math.Min(y0, c.YMin);
            x1 = Math.Max(x1, c.XMax); y1 = Math.Max(y1, c.YMax);
        }

        double pad = (_order + 1) * h;
        double gx0 = x0 - pad, gy0 = y0 - pad;
        _nx = (int)Math.Ceiling((x1 + pad - gx0) / h) + 1;
        _ny = (int)Math.Ceiling((y1 + pad - gy0) / h) + 1;
        _px = PlanarAimGeometry.NextPow2(2 * _nx);
        _py = PlanarAimGeometry.NextPow2(2 * _ny);

        // ── the projection: one cell pulse per cell ───────────────────────────────────────────
        var vInv = AimProjection.InverseVandermonde(_order, h);
        _p0 = new int[_m];
        _q0 = new int[_m];
        _lambda = new double[(long)_m * _side * _side];
        int nodes = 2 * _order + 6;
        PlanarFill.ForRowsOf(cores.Settings, _m, i =>
        {
            int p = (int)Math.Round((centres[i].X - gx0) / h - 0.5 * _order);
            int q = (int)Math.Round((centres[i].Y - gy0) / h - 0.5 * _order);
            p = Math.Clamp(p, 0, _nx - 1 - _order);
            q = Math.Clamp(q, 0, _ny - 1 - _order);
            _p0[i] = p;
            _q0[i] = q;

            double xs = gx0 + (p + 0.5 * _order) * h;
            double ys = gy0 + (q + 0.5 * _order) * h;

            var moments = new double[_side, _side];
            AimProjection.Accumulate(_side, moments, mesh.Cells[i], PlanarFill.PulseAt(mesh, i),
                                     PlanarBasisDirection.X, 1.0, xs, ys, nodes);
            var lam = AimProjection.Coefficients(_side, moments, vInv);
            lam.CopyTo(_lambda, (long)i * _side * _side);
        });
        _projectionMs = sw.Elapsed.TotalMilliseconds;

        // ── the near set, over CELLS ──────────────────────────────────────────────────────────
        sw.Restart();
        (_rowPtr, _colIdx) = AimProjection.NearSet(_m, _order, centres, spans, _p0, _q0, _radiusM, h);
        _nearSetMs = sw.Elapsed.TotalMilliseconds;

        // ── the grid kernel, and its circulant embedding ──────────────────────────────────────
        //
        // The SAME re-floored terms the near entries are assembled from — read off the pulse
        // potential rather than floored a second time here, so the two cannot drift.
        sw.Restart();
        var terms = _pulse.Terms;
        double selfRho = st.SelfKernelFactor * h;
        _g = new Complex[(long)_nx * _ny];
        for (int dp = 0; dp < _nx; dp++)
            for (int dq = 0; dq < _ny; dq++)
            {
                double rho = h * Math.Sqrt((double)dp * dp + (double)dq * dq);
                _g[dp * _ny + dq] = terms.Evaluate(dp == 0 && dq == 0 ? selfRho : rho);
            }
        _fft = new AimGridFft(_nx, _ny, _px, _py);
        _hat = _fft.EmbedAndTransform(_g);
        _buf = new Complex[(long)_px * _py];
        _gridKernelMs = sw.Elapsed.TotalMilliseconds;

        // ── the near set's exact entries: the dense path's own P[a, b] ────────────────────────
        sw.Restart();
        var exact = new Complex[_colIdx.Length];
        PlanarFill.ForRowsOf(cores.Settings, _m, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j < i) continue;
                _pulse.PrepareCells(i, j);
                exact[k] = _pulse.At(i, j);
            }
        });
        _nearFillMs = sw.Elapsed.TotalMilliseconds;

        // ── the correction: exact − what the grid product claims for the pair ─────────────────
        sw.Restart();
        _nearCorrection = new Complex[_colIdx.Length];
        PlanarFill.ForRowsOf(cores.Settings, _m, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j < i) continue;
                _nearCorrection[k] = exact[k] - AimEntry(i, j);
            }
        });

        // Both nearness criteria are symmetric, so the near set is, and the lower triangle is COPIED
        // rather than recomputed — which is also what keeps P[i,j] and P[j,i] bit-identical here for
        // the same reason the dense fill mirrors instead of computing both.
        PlanarFill.ForRowsOf(cores.Settings, _m, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j >= i) continue;
                int t = TransposePosition(i, j);
                exact[k]           = exact[t];
                _nearCorrection[k] = _nearCorrection[t];
            }
        });
        _correctionMs = sw.Elapsed.TotalMilliseconds;

        // ── the preconditioner: the near field's own sparse LU ────────────────────────────────
        sw.Restart();
        (_preconditioner, _factorNnz) = FactorNear(exact);
        _precondMs = sw.Elapsed.TotalMilliseconds;

        Report = BuildReport(0, 0, double.NaN);
    }

    private PlanarStaticAimReport BuildReport(double solveMs, int iterations, double residual) =>
        new(CellCount: _m, GridNodesX: _nx, GridNodesY: _ny, GridPitchM: _pitchM,
            ProjectionOrder: _order, NearRadiusM: _radiusM,
            NearRadiusFromSpanM: _radiusFromSpanM, NearRadiusFloorM: _radiusFloorM,
            NearEntries: _colIdx.LongLength, NearCellClasses: _pulse.CellPairCount,
            PaddedGridNodes: (long)_px * _py, FactorNonZeros: _factorNnz,
            ProjectionMs: _projectionMs, NearSetMs: _nearSetMs, NearFillMs: _nearFillMs,
            CorrectionMs: _correctionMs, GridKernelMs: _gridKernelMs,
            PreconditionerMs: _precondMs, SolveMs: solveMs,
            Iterations: iterations, Residual: residual);

    /// <summary>Whether the near-radius floor is what set the radius on this mesh.</summary>
    public bool NearRadiusIsFloored => _radiusFloorM > _radiusFromSpanM;

    /// <summary>True when <c>(a, b)</c> is in the near set — what the near-set completeness gate asks.</summary>
    public bool IsNear(int a, int b)
    {
        for (int k = _rowPtr[a]; k < _rowPtr[a + 1]; k++) if (_colIdx[k] == b) return true;
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Σ q</c> where <c>P q = ε₀·1</c> — the whole meshed sheet's static capacitance to ground,
    /// exactly the quantity <see cref="PlanarDeembed.StaticCapacitance"/>'s dense path returns.
    ///
    /// <para>The right-hand side is <c>ε₀·1</c> rather than <c>1</c> against a scaled <c>P</c>, for
    /// the reason P2/M2 recorded on the dense path: the scaling the RHS carries for free is one
    /// rounding per entry that this form does not do at all.</para>
    /// </summary>
    public double TotalCapacitance()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var b = new Complex[_m];
        for (int i = 0; i < _m; i++) b[i] = EmConstants.Eps0;

        var q = PlanarGmres.Solve(Multiply, ApplyPreconditioner, b, _st.StaticTolerance,
                                  _st.MaxIterations, _st.Restart,
                                  out int iterations, out double residual);
        LastIterations = iterations;
        LastResidual   = residual;
        Report = BuildReport(sw.Elapsed.TotalMilliseconds, iterations, residual);

        if (residual > _st.StaticTolerance)
            throw new InvalidOperationException(
                $"The accelerated static capacitance solve did not converge: {iterations} iteration(s) " +
                $"reached a relative residual of {residual:E2} against a tolerance of " +
                $"{_st.StaticTolerance:E2}. This is D7's reference impedance — C_pul DIFFERENCES two " +
                "of these, so a half-converged charge vector renormalises every published " +
                "s-parameter by a smooth, plausible, wrong Z_c rather than failing visibly. Widen the " +
                "near field (NearRadiusFactor), raise ProjectionOrder, or clear PlanarFillSettings.Aim " +
                "to solve this standard's static system densely.");

        Complex total = Complex.Zero;
        for (int i = 0; i < _m; i++) total += q[i];
        return total.Real;
    }

    /// <summary><c>y = P x</c>, accelerated: the sparse near-field correction plus one FFT
    /// convolution on the auxiliary grid.</summary>
    public Complex[] Multiply(Complex[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length != _m)
            throw new ArgumentException($"Expected a vector of length {_m}, got {x.Length}.", nameof(x));

        int s = _side;
        Array.Clear(_buf);

        // scatter: cell charges → grid density
        for (int i = 0; i < _m; i++)
        {
            Complex xi = x[i];
            if (xi == Complex.Zero) continue;
            long lam = (long)i * s * s;
            for (int k = 0; k < s; k++)
            {
                long row = (long)(_p0[i] + k) * _py + _q0[i];
                for (int l = 0; l < s; l++) _buf[row + l] += xi * _lambda[lam + k * s + l];
            }
        }

        _fft.Convolve(_buf, _hat);

        // gather: grid potential → cell reactions
        var y = new Complex[_m];
        for (int i = 0; i < _m; i++)
        {
            long lam = (long)i * s * s;
            Complex acc = Complex.Zero;
            for (int k = 0; k < s; k++)
            {
                long row = (long)(_p0[i] + k) * _py + _q0[i];
                for (int l = 0; l < s; l++) acc += _lambda[lam + k * s + l] * _buf[row + l];
            }
            y[i] = acc;
        }

        // the exact near field, minus what the grid product just claimed for it
        for (int i = 0; i < _m; i++)
        {
            Complex acc = Complex.Zero;
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++) acc += _nearCorrection[k] * x[_colIdx[k]];
            y[i] += acc;
        }

        return y;
    }

    /// <summary>What the accelerated product produces for one cell pair — i.e. what the near-field
    /// correction has to remove before adding the exact entry.</summary>
    private Complex AimEntry(int a, int bIdx)
    {
        int s = _side;
        long la = (long)a * s * s, lb = (long)bIdx * s * s;
        Complex v = Complex.Zero;

        for (int k = 0; k < s; k++)
            for (int l = 0; l < s; l++)
            {
                double ca = _lambda[la + k * s + l];
                if (ca == 0.0) continue;
                int p = _p0[a] + k, q = _q0[a] + l;

                for (int mm = 0; mm < s; mm++)
                    for (int nn = 0; nn < s; nn++)
                    {
                        int dp = Math.Abs(p - (_p0[bIdx] + mm));
                        int dq = Math.Abs(q - (_q0[bIdx] + nn));
                        v += ca * _g[(long)dp * _ny + dq] * _lambda[lb + mm * s + nn];
                    }
            }
        return v;
    }

    /// <inheritdoc cref="PlanarAimGeometry.TransposePosition"/>
    private int TransposePosition(int i, int j)
    {
        int lo = _rowPtr[j], hi = _rowPtr[j + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_colIdx[mid] == i) return mid;
            if (_colIdx[mid] < i) lo = mid + 1; else hi = mid - 1;
        }
        throw new InvalidOperationException(
            $"The near set is not symmetric: ({i}, {j}) is in it and ({j}, {i}) is not. " +
            "Both nearness criteria are symmetric, so this is a bug in the near-set construction " +
            "rather than a configuration.");
    }

    private (SparseLU? Lu, long FactorNnz) FactorNear(Complex[] exact)
    {
        var tri = new CoordinateStorage<Complex>(_m, _m, Math.Max(1, _colIdx.Length));
        for (int i = 0; i < _m; i++)
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
                tri.At(i, _colIdx[k], exact[k]);

        var csc = SparseMatrix.OfIndexed(tri);
        try
        {
            var perm = AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtPlusA);
            var lu = SparseLU.Create(csc, perm, 1.0);
            return (lu, lu.NonZerosCount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed near-field factorisation is not fatal — GMRES still runs unpreconditioned, and
            // the iteration cap is what stops it. Reporting it as "no preconditioner" beats refusing.
            return (null, 0);
        }
    }

    private Complex[] ApplyPreconditioner(Complex[] v)
    {
        if (_preconditioner is null) return v;
        var r = new Complex[_m];
        _preconditioner.Solve(v, r);
        return r;
    }

    /// <summary><see cref="Multiply(Complex[])"/> in NumFlat's own vector type.</summary>
    public Vec<Complex> Multiply(Vec<Complex> x)
    {
        var a = new Complex[_m];
        for (int i = 0; i < _m; i++) a[i] = x[i];
        var b = Multiply(a);
        var r = new Vec<Complex>(_m);
        for (int i = 0; i < _m; i++) r[i] = b[i];
        return r;
    }
}
