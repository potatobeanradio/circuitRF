// P12 (brief-em-p12-aim-bordered-vias) — MULTI-LEVEL AND VIAS UNDER AIM, AS A BORDERED SYSTEM.
//
// ── What was refused, and what was actually the obstacle ──────────────────────────────────────────
//
// Until P12 `PlanarAimGeometry.Build` refused any mesh carrying a ẑ basis and `PlanarSolveContext`
// refused the general (multi-level) kernel under `Aim`, so every real PCB with a ground via was
// capped at the DENSE 5,000-unknown ceiling however much of it was ordinary horizontal metal. The
// refusal's stated reason — "a projection with a derivative in it" — is true of PROJECTING the ẑ
// bases. It was never a reason to project them.
//
// R-via-5 already orders the unknowns horizontal-first, so the matrix is
//
//     Z = [ Z_hh  Z_hz ]        N_h = horizontal rooftops (thousands)
//         [ Z_zh  Z_zz ]        N_z = via footprints      (tens to a few hundred)
//
// and the three blocks have completely different economics:
//
//   • Z_hh is the operator AIM already accelerates, ONE PAIRING AT A TIME. Every level shares the
//     SAME auxiliary grid — the grid is in-plane and the levels differ only in z — so a second level
//     costs one more grid kernel table and one more FFT hat pair per component, `L(L+1)/2` of them
//     for L levels. The scatter and the gather become per level, and nothing else about the
//     projection changes: the stencils, the moments and the near set are in-plane objects and are
//     the SAME OBJECTS the single-level accelerator builds.
//
//   • Z_hz, Z_zh and Z_zz are filled DENSELY, by `PlanarFill`'s own via arms (`MixedEntry`,
//     `SingularPrismPart`, the cell-pulse potential) through the P12 seam — the same arithmetic the
//     dense multi-level fill runs, not a second reading of it. That is `N_h × N_z + N_z²` entries,
//     which is cheap exactly while `N_z ≪ N_h`, and the brief is explicit that if `N_z` ever makes
//     the border the cost, that is a later brief with its own measurement.
//
// ── THE PRECONDITIONER IS THE PART THAT NEEDED A DECISION ────────────────────────────────────────
//
// §11's finding — GMRES's iteration count is FLAT in N when the near field's own sparse LU is the
// preconditioner — is about the horizontal operator. Handing GMRES a preconditioner that ignores the
// border would steer it with a matrix that has nothing at all in `N_z` rows, and the via unknowns
// are not weakly coupled: a ground via is a short circuit.
//
// So the preconditioner is the near-field LU with the border folded in EXACTLY, by block
// elimination:
//
//     M = [ N_hh  Z_hz ]   M⁻¹ r  =  y_z = S⁻¹ (r_z − Z_zh N_hh⁻¹ r_h),  S = Z_zz − Z_zh N_hh⁻¹ Z_hz
//         [ Z_zh  Z_zz ]            y_h = N_hh⁻¹ (r_h − Z_hz y_z)
//
// `S` is `N_z × N_z` and dense; building it is `N_z` sparse triangular solves, which is milliseconds
// at the footprint counts a real board has. **`N_hh⁻¹ Z_hz` is NOT stored**: it is `16·N_h·N_z`
// bytes — 38 MB at N_h = 12,000 and N_z = 200, on a working set whose whole point is to be small —
// so `S` is built one column at a time and discarded, and the apply pays a SECOND sparse solve
// instead. Two sparse substitutions per GMRES iteration against one is a fraction of a percent of an
// iteration that already runs three FFTs over the padded grid.
//
// ── WHAT IS STILL REFUSED, AND WHY IT IS A DIFFERENT KIND OF LIMIT ───────────────────────────────
//
// `Dcim.ValidatedRhoOverLambdaAtHeights` = 0.1 on G_A^zz still governs, and
// `PlanarSolve.VerticalRangeVerdict` still asks it. That is a statement about the KERNEL — how far
// apart two via footprints may be before the interior fit stops being validated — and it is exactly
// as true of an accelerated solve as of a dense one. Nothing here widens it, and the phase gate
// asserts the refusal still fires with `Aim` set.

using System.Numerics;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using CSparse.Ordering;
using CSparse.Storage;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>What a bordered build cost and how big it is — <see cref="PlanarAimReport"/>'s
/// counterpart for the multi-level path, with the two numbers that are new: how many height
/// pairings the grid carries, and how much the dense border weighs.</summary>
/// <param name="HorizontalCount">N_h — the accelerated block's size.</param>
/// <param name="VerticalCount">N_z — the dense border's width.</param>
/// <param name="LevelPairings">How many (level, level) grid kernel tables were built. One per
/// unordered pairing that the mesh's horizontal bases actually reach, never <c>L²</c>.</param>
/// <param name="SheetCount">How many distinct LEVELS carry horizontal bases — the number of grid
/// "sheets" the scatter and the gather run over, and the multiplier on the product's own scratch.</param>
/// <param name="BorderBytes">The border's own storage: <c>Z_hz</c> at <c>16·N_h·N_z</c> plus
/// <c>Z_zz</c> and the Schur complement at <c>16·N_z²</c> each. <c>Z_zh</c> is not stored — Z is
/// complex-symmetric and the transpose is read, not held.</param>
public sealed record PlanarBorderedAimReport(
    int    HorizontalCount,
    int    VerticalCount,
    int    LevelPairings,
    int    SheetCount,
    int    GridNodesX,
    int    GridNodesY,
    double GridPitchM,
    int    ProjectionOrder,
    double NearRadiusM,
    long   NearEntries,
    long   PaddedGridNodes,
    double GridKernelMs,
    double NearFillMs,
    double BorderMs,
    double PreconditionerMs,
    long   FactorNonZeros,
    long   BorderBytes,
    long   GeometryBytes)
{
    /// <summary>N — what the solve's vectors are the length of.</summary>
    public int UnknownCount => HorizontalCount + VerticalCount;

    /// <inheritdoc cref="PlanarAimReport.NearEntriesPerRow"/>
    public double NearEntriesPerRow => (double)NearEntries / Math.Max(1, HorizontalCount);

    /// <inheritdoc cref="PlanarAimReport.NearFillFraction"/>
    public double NearFillFraction =>
        (double)NearEntries / Math.Max(1, HorizontalCount) / Math.Max(1, HorizontalCount);

    /// <summary>
    /// Bytes held once built, on <see cref="PlanarAimReport.ResidentBytes"/>' own terms plus the
    /// border. The grid arrays are counted per PAIRING, which is the term that grows with the level
    /// count and is the one a reader of the single-level number would not expect.
    /// </summary>
    public long ResidentBytes =>
        16L * NearEntries
      + 16L * GridNodesX * GridNodesY * 2 * LevelPairings      // the absolute-offset kernel tables
      + 16L * PaddedGridNodes * 2 * LevelPairings              // their FFT hats
      + 16L * PaddedGridNodes * (3L * SheetCount + 3)          // the product's scatter/accumulate scratch
      + 20L * FactorNonZeros + 8L * (HorizontalCount + 1)
      + BorderBytes
      + GeometryBytes;

    /// <summary>The dense border as a fraction of what a dense solve of the WHOLE problem would
    /// hold — the number that says whether "cheap while N_z ≪ N_h" is being honoured on this mesh.</summary>
    public double BorderFractionOfDense =>
        (double)BorderBytes / Math.Max(1L, 16L * UnknownCount * UnknownCount);
}

/// <summary>
/// <b>P12 — the accelerated operator for a MULTI-LEVEL and/or VIA-BEARING mesh.</b> The horizontal
/// prefix is AIM-accelerated per (level, level) pairing over one shared auxiliary grid; the vertical
/// tail is a dense border. See the file header for the block structure, for why the ẑ bases are not
/// projected, and for the block-elimination preconditioner.
///
/// <para>Reduces to <see cref="PlanarAimOperator"/>'s operator on a one-level mesh with no vias —
/// same grid, same stencils, same near set, one pairing — which is what the P12 reduction gate
/// measures.</para>
///
/// <para><b>Not thread-safe for concurrent products</b>, on exactly the terms
/// <see cref="PlanarAimOperator"/> states: the FFT plans and the grid scratch are per-operator, and
/// one operator belongs to one mesh at one frequency.</para>
/// </summary>
public sealed class PlanarBorderedAimOperator : IPlanarOperator
{
    private readonly PlanarAimGeometry _g;
    private readonly PlanarAimSettings _st;
    private readonly PlanarMesh _mesh;

    private readonly int _nh, _nz, _n;
    private readonly int _side, _nx, _ny, _px, _py;
    private readonly AimStencil[] _stencils;

    // Which auxiliary-grid "sheet" each horizontal basis scatters onto, and how many there are.
    private readonly int[] _sheetOfBasis;                     // horizontal basis -> sheet
    private readonly int[] _sheetLayer;                       // sheet -> layer index
    private readonly int[] _layerSheet;                       // layer index -> sheet, or -1
    private readonly int _sheets;

    // Per unordered sheet pairing: the absolute-offset grid kernel tables and their FFT hats. Null
    // where the mesh has no horizontal pair spanning those two levels.
    private readonly Complex[]?[,] _ga, _gq, _hatA, _hatQ;
    private readonly AimGridFft _fft;

    // Scratch: one padded field per (component, sheet) for the sources, three for the observer being
    // accumulated. See the file header for why the observer side is not also per sheet.
    private readonly Complex[][] _srcX, _srcY, _srcQ;
    private readonly Complex[] _accX, _accY, _accQ;

    // The near field over the HORIZONTAL block only — the geometry's own CSR.
    private readonly int[] _rowPtr, _colIdx;
    private readonly Complex[] _nearCorrection;
    private Complex[]? _nearExact;
    private readonly SparseLU? _preconditioner;

    // The dense border. Z_zh is Z_hz transposed and is not stored: Z is complex-symmetric.
    private readonly Complex[] _hz;                            // [i * nz + k]
    private readonly Complex[] _zz;                            // [k * nz + l]
    private readonly LuDecompositionComplex? _schur;

    private readonly Complex _scalarScale, _vectorScale;

    public int Size => _n;

    /// <summary>The per-mesh geometry this reads.</summary>
    public PlanarAimGeometry Geometry => _g;

    /// <summary>What it cost and how big it is.</summary>
    public PlanarBorderedAimReport Report { get; }

    /// <inheritdoc cref="PlanarAimOperator.LastIterations"/>
    public int LastIterations { get; private set; }

    /// <inheritdoc cref="PlanarAimOperator.LastResidual"/>
    public double LastResidual { get; private set; }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Build
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The operator for one mesh at one frequency, over a <see cref="PlanarAimGeometry"/> built once
    /// per mesh (P6's shape, unchanged).
    /// </summary>
    /// <param name="set">The kernel set at this frequency, already viewed for these cores
    /// (<c>set.For(cores)</c>) exactly as <see cref="PlanarSystem.BuildMultiLevel"/> takes it.</param>
    /// <param name="levels">The z of every conductor level, and the ground plane's own z.</param>
    public static PlanarBorderedAimOperator Build(PlanarAimGeometry geometry, PlanarKernelSet set,
                                                  PlanarLevels levels, double omega,
                                                  PlanarFillDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(levels);
        return new PlanarBorderedAimOperator(geometry, set, levels, omega, diagnostics);
    }

    private PlanarBorderedAimOperator(PlanarAimGeometry g, PlanarKernelSet set, PlanarLevels levels,
                                      double omega, PlanarFillDiagnostics? diagnostics)
    {
        var cores = g.Cores;
        var fillSt = cores.Settings;
        _g    = g;
        _st   = g.Settings;
        _mesh = cores.Mesh;
        _nh   = g.HorizontalCount;
        _nz   = g.VerticalCount;
        _n    = _nh + _nz;
        _side = g.Side;
        _nx   = g.Nx;
        _ny   = g.Ny;
        _px   = g.Px;
        _py   = g.Py;
        _stencils = g.Stencils;
        _rowPtr = g.RowPtr;
        _colIdx = g.ColIdx;

        _scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        _vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;

        // P3's own resolution, verbatim: every per-pairing kernel object the dense multi-level fill
        // reads, resolved once, serially, before any row loop. The bordered operator reads the SAME
        // table — which is what makes its border the dense fill's arithmetic rather than a copy of it.
        var pr = PlanarFill.MultiLevelPairings.Resolve(cores, set, levels, fillSt);

        int layers = pr.TermsQ.GetLength(0);

        // ── which levels the HORIZONTAL bases live on ─────────────────────────────────────────
        _layerSheet = new int[layers];
        Array.Fill(_layerSheet, -1);
        var sheetLayers = new List<int>();
        _sheetOfBasis = new int[_nh];
        for (int i = 0; i < _nh; i++)
        {
            int l = _mesh.Bases[i].LayerIndex;
            if (_layerSheet[l] < 0) { _layerSheet[l] = sheetLayers.Count; sheetLayers.Add(l); }
            _sheetOfBasis[i] = _layerSheet[l];
        }
        _sheetLayer = [.. sheetLayers];
        _sheets = _sheetLayer.Length;

        // ── the grid kernels, one table pair per SHEET PAIRING ────────────────────────────────
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _fft  = new AimGridFft(_nx, _ny, _px, _py);
        _ga   = new Complex[]?[_sheets, _sheets];
        _gq   = new Complex[]?[_sheets, _sheets];
        _hatA = new Complex[]?[_sheets, _sheets];
        _hatQ = new Complex[]?[_sheets, _sheets];
        double selfRho = _st.SelfKernelFactor * g.H;
        int pairings = 0;

        for (int a = 0; a < _sheets; a++)
            for (int b = a; b < _sheets; b++)
            {
                int la = _sheetLayer[a], lb = _sheetLayer[b];
                var tq = pr.TermsQ[la, lb];
                var ta = pr.TermsA[la, lb];
                if (tq is null) continue;                      // no cell pairing across these levels

                var ka = new Complex[(long)_nx * _ny];
                var kq = new Complex[(long)_nx * _ny];
                for (int dp = 0; dp < _nx; dp++)
                    for (int dq = 0; dq < _ny; dq++)
                    {
                        double rho = g.H * Math.Sqrt((double)dp * dp + (double)dq * dq);
                        double at  = dp == 0 && dq == 0 ? selfRho : rho;
                        kq[dp * _ny + dq] = tq.Evaluate(at);
                        if (ta is not null) ka[dp * _ny + dq] = ta.Evaluate(at);
                    }

                _gq[a, b] = _gq[b, a] = kq;
                _hatQ[a, b] = _hatQ[b, a] = _fft.EmbedAndTransform(kq);
                if (ta is not null)
                {
                    _ga[a, b] = _ga[b, a] = ka;
                    _hatA[a, b] = _hatA[b, a] = _fft.EmbedAndTransform(ka);
                }
                pairings++;
            }

        _srcX = new Complex[_sheets][];
        _srcY = new Complex[_sheets][];
        _srcQ = new Complex[_sheets][];
        for (int a = 0; a < _sheets; a++)
        {
            _srcX[a] = new Complex[(long)_px * _py];
            _srcY[a] = new Complex[(long)_px * _py];
            _srcQ[a] = new Complex[(long)_px * _py];
        }
        _accX = new Complex[(long)_px * _py];
        _accY = new Complex[(long)_px * _py];
        _accQ = new Complex[(long)_px * _py];
        double gridMs = sw.Elapsed.TotalMilliseconds;

        // ── the scalar block, per CELL-LEVEL pairing, and the horizontal entry fill over it ───
        //
        // One PlanarPulsePotential per (layer, layer) — shared by the horizontal near field and by
        // the border, so the two halves cannot hold separate remainder memos of the same function.
        var pulse = new PlanarPulsePotential?[layers, layers];
        for (int la = 0; la < layers; la++)
            for (int lb = la; lb < layers; lb++)
                if (pr.TermsQ[la, lb] is { } t)
                    pulse[la, lb] = pulse[lb, la] =
                        new PlanarPulsePotential(g.EntryCores, t, pr.RemQ[la, lb]);

        var hFill = new PlanarEntryFill?[layers, layers];
        for (int la = 0; la < layers; la++)
            for (int lb = la; lb < layers; lb++)
                if (pr.TermsA[la, lb] is { } t && pulse[la, lb] is { } p)
                    hFill[la, lb] = hFill[lb, la] = new PlanarEntryFill(g.EntryCores, t, p, omega);

        // ── the near set's exact entries, then the AIM correction, then the lower copy ────────
        sw.Restart();
        var nearExact = new Complex[_colIdx.Length];
        _nearExact = nearExact;
        _nearCorrection = new Complex[_colIdx.Length];

        PlanarFill.ForRowsOf(fillSt, _nh, i =>
        {
            int li = _mesh.Bases[i].LayerIndex;
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j < i) continue;
                int lj = _mesh.Bases[j].LayerIndex;
                // hFill is null only for a (level, level) pairing that no SAME-DIRECTION horizontal
                // pair spans — x̂ on one level and ŷ on the other, say. D5 makes the vector block
                // identically zero there, so the entry is the scalar half alone, which is exactly
                // what PlanarEntryFill.At would have returned had it existed.
                nearExact[k] = hFill[li, lj] is { } f
                    ? f.At(i, j)
                    : _scalarScale * ScalarSum(g.EntryCores.DivHalves, pulse, i, j);
            }
        });

        PlanarFill.ForRowsOf(fillSt, _nh, i =>
        {
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            {
                int j = _colIdx[k];
                if (j < i) continue;
                _nearCorrection[k] = nearExact[k] - AimEntry(i, j);
            }
        });

        PlanarFill.ForRowsOf(fillSt, _nh, i =>
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
        double nearMs = sw.Elapsed.TotalMilliseconds;

        // ── the dense border ──────────────────────────────────────────────────────────────────
        sw.Restart();
        _hz = new Complex[(long)_nh * _nz];
        _zz = new Complex[(long)_nz * _nz];
        FillBorder(pr, pulse, levels, diagnostics);
        double borderMs = sw.Elapsed.TotalMilliseconds;

        // ── the preconditioner: the near LU, with the border folded in exactly ────────────────
        sw.Restart();
        var (lu, factorNnz) = FactorNear(nearExact);
        _preconditioner = lu;
        _schur = BuildSchur();
        double precondMs = sw.Elapsed.TotalMilliseconds;

        // P1's release point, unchanged: the exact entries have served the factorisation and the
        // product reads the correction.
        if (!_st.KeepNearExact) _nearExact = null;

        Report = new PlanarBorderedAimReport(
            HorizontalCount: _nh, VerticalCount: _nz, LevelPairings: pairings, SheetCount: _sheets,
            GridNodesX: _nx, GridNodesY: _ny, GridPitchM: g.H, ProjectionOrder: g.M,
            NearRadiusM: g.NearRadiusM, NearEntries: _colIdx.LongLength,
            PaddedGridNodes: (long)_px * _py,
            GridKernelMs: gridMs, NearFillMs: nearMs, BorderMs: borderMs,
            PreconditionerMs: precondMs, FactorNonZeros: factorNnz,
            BorderBytes: 16L * _hz.LongLength + 32L * _zz.LongLength,
            GeometryBytes: g.Bytes);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The border — PlanarFill's own via arms, per entry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Z_hz</c> and <c>Z_zz</c>, entry by entry, through the P12 seam onto
    /// <c>PlanarFill</c>'s <c>MixedEntry</c>, <c>SingularPrismPart</c> and cell-pulse potential.
    ///
    /// <para><b>The loop bounds are the dense fill's own.</b> <c>MultiLevelPairings.Resolve</c>
    /// populates its ẑẑ table on the ORDERED span pairs an <c>i ≤ j</c> scan over the mesh's bases
    /// meets, so this scans in exactly that order — a differently-ordered scan would ask for a span
    /// pairing that was never resolved and read a null.</para>
    /// </summary>
    private void FillBorder(PlanarFill.MultiLevelPairings pr, PlanarPulsePotential?[,] pulse,
                            PlanarLevels levels, PlanarFillDiagnostics? diagnostics)
    {
        var st = _g.Cores.Settings;
        double rhoFloor = _g.Cores.RhoFloorM;
        var div = _g.EntryCores.DivHalves;

        // The ẑẑ block's own scalar potential, per ordered span pair — the z-averaged terms carry a
        // different kernel from any (layer, layer) pairing, so it cannot share `pulse`.
        int spans = pr.Spans.Length;
        var zzPulse = new PlanarPulsePotential?[spans, spans];
        for (int si = 0; si < spans; si++)
            for (int sj = 0; sj < spans; sj++)
                if (pr.Zz[si, sj] is { } z)
                    zzPulse[si, sj] = new PlanarPulsePotential(_g.EntryCores, z.T, z.R);

        // ── Z_hz: one horizontal row, every via column ────────────────────────────────────────
        PlanarFill.ForRowsOf(st, _nh, i =>
        {
            var bi = _mesh.Bases[i];
            for (int k = 0; k < _nz; k++)
            {
                int j = _nh + k;
                var bj = _mesh.Bases[j];
                Complex s = ScalarSum(div, pulse, i, j);

                int sv = pr.SpanOfBasis[j];
                Complex v = pr.Spans[sv].Length
                          * PlanarFill.MixedEntryOf(_mesh, bj, bi, pr.Halves[i],
                                                    pr.Mixed[sv, bi.LayerIndex]!, rhoFloor, st);

                _hz[(long)i * _nz + k] = _scalarScale * s + _vectorScale * v;
            }
        });

        // ── Z_zz: the via block, upper triangle then mirrored ─────────────────────────────────
        PlanarFill.ForRowsOf(st, _nz, k =>
        {
            int i = _nh + k;
            var bi = _mesh.Bases[i];
            int si = pr.SpanOfBasis[i];
            for (int l = k; l < _nz; l++)
            {
                int j = _nh + l;
                var bj = _mesh.Bases[j];

                Complex s = ScalarSum(div, pulse, i, j);

                // R-zz-1's Tier 1 instrument, on the accelerated path too: the widest LATERAL
                // separation this arm — the only consumer of G_A^zz anywhere — actually asks about.
                diagnostics?.ObserveVerticalPair(
                    PlanarFill.CellPairSpanOf(_mesh.Cells[bi.CellA], _mesh.Cells[bj.CellA]));

                int sj = pr.SpanOfBasis[j];
                var spanI = pr.Spans[si];
                var spanJ = pr.Spans[sj];
                var zz = pr.Zz[si, sj]!.Value;
                Complex core = zzPulse[si, sj]!.At(bi.CellA, bj.CellA);
                core += PlanarFill.SingularPrismPartOf(_mesh, zz.Asym, bi.CellA, bj.CellA,
                                                       spanI, spanJ, st);

                Complex v = _scalarScale * s
                          + _vectorScale * spanI.Length * spanJ.Length * core;
                _zz[(long)k * _nz + l] = v;
                _zz[(long)l * _nz + k] = v;
            }
        });

        _ = levels;
    }

    /// <summary>D4's cell-pulse potential at the pairing the two cells' own LEVELS name.</summary>
    private Complex P(PlanarPulsePotential?[,] pulse, int cellA, int cellB)
        => pulse[_mesh.Cells[cellA].LayerIndex, _mesh.Cells[cellB].LayerIndex]!.At(cellA, cellB);

    /// <summary>D4's signed sum of four cell-pair potentials — the scalar half of any entry,
    /// whichever families the two bases belong to.</summary>
    private Complex ScalarSum((RooftopHalf A, RooftopHalf B)[] div, PlanarPulsePotential?[,] pulse,
                              int i, int j)
    {
        var (ma, mb) = div[i];
        var (na, nb) = div[j];
        return ma.Sign * na.Sign * P(pulse, ma.CellIndex, na.CellIndex)
             + ma.Sign * nb.Sign * P(pulse, ma.CellIndex, nb.CellIndex)
             + mb.Sign * na.Sign * P(pulse, mb.CellIndex, na.CellIndex)
             + mb.Sign * nb.Sign * P(pulse, mb.CellIndex, nb.CellIndex);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The accelerated product
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the grid product claims for one HORIZONTAL pair — the quantity the near-field
    /// correction removes before adding the exact entry. Identical to
    /// <c>PlanarAimOperator</c>'s, with the kernel table chosen by the pair's two sheets.</summary>
    private Complex AimEntry(int i, int j)
    {
        var a = _stencils[i];
        var b = _stencils[j];
        int s = _side;
        int sa = _sheetOfBasis[i], sb = _sheetOfBasis[j];
        var gq = _gq[sa, sb]!;
        var ga = _ga[sa, sb];
        bool sameDir = a.Direction == b.Direction && ga is not null;

        Complex q = Complex.Zero, v = Complex.Zero;

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
                        q += ca * gq[idx] * b.Charge[mm * s + nn];
                        if (sameDir) v += ja * ga![idx] * b.Current[mm * s + nn];
                    }
            }

        return _scalarScale * q + (sameDir ? _vectorScale * v : Complex.Zero);
    }

    /// <summary><c>y = Z x</c> — the accelerated horizontal block plus the dense border.</summary>
    public Complex[] Multiply(Complex[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length != _n)
            throw new ArgumentException($"Expected a vector of length {_n}, got {x.Length}.", nameof(x));

        int s = _side;
        var y = new Complex[_n];

        // ── scatter, per sheet ───────────────────────────────────────────────────────────────
        for (int a = 0; a < _sheets; a++)
        {
            Array.Clear(_srcX[a]); Array.Clear(_srcY[a]); Array.Clear(_srcQ[a]);
        }
        for (int i = 0; i < _nh; i++)
        {
            Complex xi = x[i];
            if (xi == Complex.Zero) continue;
            var st = _stencils[i];
            int sh = _sheetOfBasis[i];
            var cur = st.Direction == PlanarBasisDirection.X ? _srcX[sh] : _srcY[sh];
            var chg = _srcQ[sh];
            for (int k = 0; k < s; k++)
            {
                long row = (long)(st.P0 + k) * _py + st.Q0;
                for (int l = 0; l < s; l++)
                {
                    cur[row + l] += xi * st.Current[k * s + l];
                    chg[row + l] += xi * st.Charge[k * s + l];
                }
            }
        }

        // ── one forward transform per (component, sheet) ─────────────────────────────────────
        for (int a = 0; a < _sheets; a++)
        {
            _fft.Transform2(_srcX[a], forward: true);
            _fft.Transform2(_srcY[a], forward: true);
            _fft.Transform2(_srcQ[a], forward: true);
        }

        // ── accumulate every source sheet into one observer sheet, then invert and gather ────
        long m = (long)_px * _py;
        for (int a = 0; a < _sheets; a++)
        {
            Array.Clear(_accX); Array.Clear(_accY); Array.Clear(_accQ);
            for (int b = 0; b < _sheets; b++)
            {
                var hq = _hatQ[a, b];
                if (hq is null) continue;
                var sq = _srcQ[b];
                for (long t = 0; t < m; t++) _accQ[t] += hq[t] * sq[t];

                var ha = _hatA[a, b];
                if (ha is null) continue;
                var sx = _srcX[b];
                var sy = _srcY[b];
                for (long t = 0; t < m; t++)
                {
                    _accX[t] += ha[t] * sx[t];
                    _accY[t] += ha[t] * sy[t];
                }
            }

            _fft.Transform2(_accX, forward: false);
            _fft.Transform2(_accY, forward: false);
            _fft.Transform2(_accQ, forward: false);

            for (int i = 0; i < _nh; i++)
            {
                if (_sheetOfBasis[i] != a) continue;
                var st = _stencils[i];
                var cur = st.Direction == PlanarBasisDirection.X ? _accX : _accY;
                Complex vec = Complex.Zero, sca = Complex.Zero;
                for (int k = 0; k < s; k++)
                {
                    long row = (long)(st.P0 + k) * _py + st.Q0;
                    for (int l = 0; l < s; l++)
                    {
                        vec += st.Current[k * s + l] * cur[row + l];
                        sca += st.Charge[k * s + l]  * _accQ[row + l];
                    }
                }
                y[i] = _vectorScale * vec + _scalarScale * sca;
            }
        }

        // ── the exact near field, minus what the grid product claimed for it ─────────────────
        for (int i = 0; i < _nh; i++)
        {
            Complex acc = Complex.Zero;
            for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++) acc += _nearCorrection[k] * x[_colIdx[k]];
            y[i] += acc;
        }

        // ── the border: y_h += Z_hz x_z, y_z = Z_hzᵀ x_h + Z_zz x_z ─────────────────────────
        if (_nz > 0)
        {
            for (int i = 0; i < _nh; i++)
            {
                Complex acc = Complex.Zero;
                long o = (long)i * _nz;
                for (int k = 0; k < _nz; k++) acc += _hz[o + k] * x[_nh + k];
                y[i] += acc;
            }

            for (int k = 0; k < _nz; k++)
            {
                Complex acc = Complex.Zero;
                for (int i = 0; i < _nh; i++) acc += _hz[(long)i * _nz + k] * x[i];
                long o = (long)k * _nz;
                for (int l = 0; l < _nz; l++) acc += _zz[o + l] * x[_nh + l];
                y[_nh + k] = acc;
            }
        }

        return y;
    }

    /// <inheritdoc cref="PlanarAimOperator.Multiply(Vec{Complex})"/>
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
    // The preconditioner
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <inheritdoc cref="PlanarAimOperator"/>
    private (SparseLU? Lu, long FactorNnz) FactorNear(Complex[] exact)
    {
        var tri = new CoordinateStorage<Complex>(_nh, _nh, Math.Max(1, _colIdx.Length));
        for (int i = 0; i < _nh; i++)
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
            return (null, 0);
        }
    }

    /// <summary>
    /// <c>S = Z_zz − Z_zh N_hh⁻¹ Z_hz</c>, one column at a time. <b><c>N_hh⁻¹ Z_hz</c> is
    /// deliberately not kept</b> — see the file header for the megabytes and for what the apply pays
    /// instead. Null when the near factorisation failed (GMRES then runs unpreconditioned, exactly as
    /// on the single-level path) or when the mesh carries no vias at all.
    /// </summary>
    private LuDecompositionComplex? BuildSchur()
    {
        if (_nz == 0 || _preconditioner is null) return null;

        var s = new Mat<Complex>(_nz, _nz);
        var rhs = new Complex[_nh];
        var col = new Complex[_nh];
        for (int k = 0; k < _nz; k++)
        {
            for (int i = 0; i < _nh; i++) rhs[i] = _hz[(long)i * _nz + k];
            _preconditioner.Solve(rhs, col);
            for (int l = 0; l < _nz; l++)
            {
                Complex acc = Complex.Zero;
                for (int i = 0; i < _nh; i++) acc += _hz[(long)i * _nz + l] * col[i];
                s[l, k] = _zz[(long)l * _nz + k] - acc;
            }
        }

        try { return s.Lu(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private Complex[] ApplyPreconditioner(Complex[] v)
    {
        if (_preconditioner is null) return v;

        var rh = new Complex[_nh];
        Array.Copy(v, rh, _nh);
        var u = new Complex[_nh];
        _preconditioner.Solve(rh, u);

        if (_nz == 0 || _schur is null)
        {
            if (_nz == 0) return u;
            // No Schur factor: the border is left out of the preconditioner rather than applied
            // half-way. That is a weaker preconditioner, not a wrong one — GMRES's residual is the
            // true one either way (right preconditioning).
            var partial = new Complex[_n];
            Array.Copy(u, partial, _nh);
            Array.Copy(v, _nh, partial, _nh, _nz);
            return partial;
        }

        // t = r_z − Z_zh u
        var t = new Vec<Complex>(_nz);
        for (int k = 0; k < _nz; k++)
        {
            Complex acc = Complex.Zero;
            for (int i = 0; i < _nh; i++) acc += _hz[(long)i * _nz + k] * u[i];
            t[k] = v[_nh + k] - acc;
        }
        var yz = _schur.Solve(t);

        // y_h = N_hh⁻¹ (r_h − Z_hz y_z)
        for (int i = 0; i < _nh; i++)
        {
            Complex acc = Complex.Zero;
            long o = (long)i * _nz;
            for (int k = 0; k < _nz; k++) acc += _hz[o + k] * yz[k];
            rh[i] = v[i] - acc;
        }
        var yh = new Complex[_nh];
        _preconditioner.Solve(rh, yh);

        var r = new Complex[_n];
        Array.Copy(yh, r, _nh);
        for (int k = 0; k < _nz; k++) r[_nh + k] = yz[k];
        return r;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <inheritdoc cref="PlanarAimOperator.Solve(Vec{Complex})"/>
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
                $"The accelerated multi-level solve did not converge: {iterations} iteration(s) " +
                $"reached a relative residual of {residual:E2} against a tolerance of " +
                $"{_st.Tolerance:E2}. A half-converged current distribution produces a smooth, " +
                "plausible, WRONG s-parameter, so this refuses rather than returning one. Widen the " +
                "near field (NearRadiusFactor), raise ProjectionOrder, or clear PlanarFillSettings." +
                "Aim to solve this mesh densely.");

        var r = new Vec<Complex>(_n);
        for (int i = 0; i < _n; i++) r[i] = x[i];
        return r;
    }

    /// <summary>
    /// The exact entry held for a HORIZONTAL near pair, or a BORDER entry for any pair involving a
    /// via. A diagnostic, on <see cref="PlanarAimOperator.NearExactAt"/>'s own terms — the near
    /// entries need <see cref="PlanarAimSettings.KeepNearExact"/>; the border is always held,
    /// because the product reads it.
    /// </summary>
    public Complex ExactAt(int i, int j)
    {
        if (i >= _nh && j >= _nh) return _zz[(long)(i - _nh) * _nz + (j - _nh)];
        if (i >= _nh) return _hz[(long)j * _nz + (i - _nh)];
        if (j >= _nh) return _hz[(long)i * _nz + (j - _nh)];

        var exact = _nearExact ?? throw new InvalidOperationException(
            "The exact near-field entries were released after the preconditioner was factored from " +
            "them. Build with PlanarAimSettings { KeepNearExact = true } to read them.");
        for (int k = _rowPtr[i]; k < _rowPtr[i + 1]; k++)
            if (_colIdx[k] == j) return exact[k];
        return Complex.Zero;
    }
}
