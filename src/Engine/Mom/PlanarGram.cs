// CL1 — THE GRAM MATRIX ⟨f_m, f_n⟩ OF THE ROOFTOP BASIS.
//
// The surface-impedance boundary condition adds exactly one Galerkin term to the EFIE,
//
//     Z[m,n] += Z_s(ω, layer) · ⟨f_m , f_n⟩ ,      ⟨f_m, f_n⟩ = ∫ f_m·f_n dS
//
// and this file is the second factor. See PlanarSurfaceImpedance's header for the first.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// IT IS REAL, FREQUENCY-INDEPENDENT AND SPARSE, AND ALL THREE MATTER
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// REAL and FREQUENCY-INDEPENDENT: f is a geometric weight, so this is built ONCE per mesh beside the
// D6 cores and scaled by one complex scalar per frequency. If the conductor-loss term is ever
// MEASURABLE against an O(N²) fill and an O(N³) factorisation, this is being rebuilt per frequency,
// which it must not be — PlanarSurfaceImpedanceTests gates the cost as a COUNTER rather than a wall
// clock, on this area's own rule about timing tests.
//
// SPARSE, and the shape is exact rather than approximate:
//
//   • f_m·f_n ≡ 0 for a mixed X/Y pair (L8c's D5 — a rooftop is purely x̂ or purely ŷ), so the Gram
//     is block-diagonal by direction exactly as the vector block is.
//   • f_m is zero outside its own two cells, so ⟨f_m, f_n⟩ is zero unless m and n SHARE a cell. A
//     cell carries at most one X rooftop reaching left and one reaching right, so a basis has at
//     most two same-direction partners and the Gram has ~3N nonzeros — measured and reported as a
//     multiple of N rather than assumed.
//   • Two cells on different LEVELS are different cells, so a cross-layer pair contributes nothing
//     and no level pairing has to be resolved. Every entry's shared cells are on ONE level, which is
//     the level whose Z_s multiplies it.
//   • EM-TFR — a rooftop can straddle a film boundary, so its self term has one half on the film and
//     one on the metal. A same-direction pair shares at most two cells (a basis with itself both,
//     neighbours one), so one slot holds at most TWO (sheet class, value) contributions. The second
//     pair of arrays exists only when some slot uses it, so a mesh with no film has exactly today's.
//   • A VERTICAL (via) basis is deliberately NOT here. Its current crosses a footprint and returns
//     through a barrel WALL, which is a series impedance in ohms rather than an ohms-per-square
//     times a Gram entry — PlanarSurfaceImpedance.Barrel, and the via arm of the fill.
//   • PMS-5 — on a LOCAL-GRID mesh a rooftop across a refinement step has a STRIP of a coarse block as its half, and
//     two rooftops on one block can have different strips that overlap (or a strip and the whole block). Those pairs
//     share metal without sharing a support, so the Gram of a support mesh is taken on its BlockSource, per physical
//     block, over each pair's intersection — PlanarLocalGridFill.GramEntries.
//
// SYMMETRIC: ⟨f_m, f_n⟩ = ⟨f_n, f_m⟩ identically, so only j ≥ i is stored and R-fil-2's
// "computed on m ≤ n and mirrored" is untouched, as is P7's complex-symmetric factorisation.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE INTEGRAL, PER SHARED CELL
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// On one cell both weights are NON-NEGATIVE — the sign that distinguishes a rooftop's two halves
// belongs to the DIVERGENCE, not to the current (PlanarBasisFunctions) — so there is no sign
// bookkeeping here at all and the diagonal is strictly positive.
//
// WHOLE RECTANGLE (every Manhattan cell, i.e. nearly all of them). With Δ the cell's extent along
// the flow direction, L its transverse extent and A = Δ·L, the weight is |u − e|/A with e the
// half's own OUTER edge, so
//
//     ∫ w_m w_n dS = (L/A²)·∫₀^Δ ξ_m ξ_n du  =  Δ/(3L)   when the two halves share an outer edge
//                                               Δ/(6L)   when they do not (one ramp rising, one
//                                                         falling — the only other case, because a
//                                                         cell carries at most one rooftop per side)
//
// Both are elementary: ∫₀^Δ u² du = Δ³/3 and ∫₀^Δ u(Δ−u) du = Δ³/6.
//
// CUT CELL (PlanarCellRegion != null). The weight is the cell's own piecewise-affine profile
// (RooftopSupport — a list of STRIPS carrying w = (αx + βy + γ)/Area), so the product of two weights
// is a QUADRATIC over each strip and needs the plain integral, both first moments and the three
// SECOND moments. The first three already existed (PolygonIntegrals.Area/AreaMoment); the second
// moments are PolygonIntegrals.AreaSecondMoments, the one new closed form CL1 needed.
//
// THE TWO SUPPORTS' STRIPS ARE THE SAME STRIPS, AND THAT IS WHY THEY CAN BE PAIRED BY INDEX.
// RooftopSupport.Build takes its breakpoints from the REGION and the flow DIRECTION alone — not from
// which side the shared face is on — so two same-direction bases meeting on one cut cell get rings
// that are equal element for element and differ only in (α, β, γ). Pairing them by index is
// therefore exact rather than an alignment assumption, and it is asserted rather than trusted.
//
// The moments are taken about the cell's own CENTRE rather than about the origin: a cell 60 mm down
// a taper has coordinates ~1e-1 and extents ~1e-5, and ∫u² about the origin would be a difference of
// numbers 1e8 times the answer.

using System.Runtime.CompilerServices;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>CL1 — ⟨f_m, f_n⟩ over the rooftop basis</b>, as a symmetric sparse matrix in CSR over the
/// UPPER triangle. See the file header for the shape and for the two closed forms.
/// </summary>
public sealed class PlanarGram
{
    /// <summary>Row starts, length N + 1. Row <c>i</c> holds only columns <c>j ≥ i</c>.</summary>
    internal readonly int[] RowPtr;
    /// <summary>Columns, ascending within a row.</summary>
    internal readonly int[] ColIdx;
    /// <summary>The entries themselves — real, and positive on the diagonal.</summary>
    internal readonly double[] Value;
    /// <summary>The conductor LEVEL each entry's shared cells sit on. One per entry, because two
    /// bases that share a cell share that cell's level.</summary>
    internal readonly int[] Layer;
    /// <summary>EM-TFR — the sheet class of a slot's second contribution, or null on a Gram where no
    /// slot has one, which is every mesh with no film boundary.</summary>
    internal readonly int[]? Layer2;
    /// <summary>EM-TFR — that contribution's value; 0 on a slot whose cells agree.</summary>
    internal readonly double[]? Value2;

    /// <summary>N — the basis count the Gram is indexed by, vertical bases included (as empty rows).</summary>
    public int UnknownCount => RowPtr.Length - 1;

    /// <summary>Stored entries, i.e. the upper triangle's nonzeros including the diagonal.</summary>
    public long NonZeros => ColIdx.LongLength;

    /// <summary>Stored entries per unknown — the number the O(N) claim is made of. Single digits, or
    /// something is wrong with the PAIRING rather than with the integral.</summary>
    public double NonZerosPerUnknown => UnknownCount > 0 ? (double)NonZeros / UnknownCount : 0;

    /// <summary>Bytes held. O(N), and reported beside the cores' own for the same P1 reason.</summary>
    public long Bytes => 4L * RowPtr.Length + 4L * ColIdx.Length + 8L * Value.Length + 4L * Layer.Length
                       + 4L * (Layer2?.Length ?? 0) + 8L * (Value2?.Length ?? 0);

    private PlanarGram(int[] rowPtr, int[] colIdx, double[] value, int[] layer, int[]? layer2, double[]? value2)
    {
        RowPtr = rowPtr; ColIdx = colIdx; Value = value; Layer = layer; Layer2 = layer2; Value2 = value2;
    }

    /// <summary>
    /// <c>⟨f_i, f_j⟩</c>, or 0 for a pair that shares no cell. Symmetric: the stored triangle is
    /// consulted whichever way round the arguments come.
    /// </summary>
    public double At(int i, int j)
    {
        int a = Math.Min(i, j), b = Math.Max(i, j);
        int k = Slot(a, b);
        if (k < 0) return 0.0;
        return Value2 is { } second ? Value[k] + second[k] : Value[k];
    }

    /// <summary>The stored slot of <c>(a, b)</c> with <c>a ≤ b</c>, or −1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Slot(int a, int b)
    {
        int lo = RowPtr[a], hi = RowPtr[a + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int c = ColIdx[mid];
            if (c == b) return mid;
            if (c < b) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>
    /// The Gram matrix of one mesh. O(N) in time and memory: the pass is over CELLS, and each cell
    /// contributes only the pairs among the (at most a handful of) same-direction rooftops on it.
    /// </summary>
    public static PlanarGram Build(PlanarMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var source = mesh.BlockSource;
        if (source is null && mesh.HasBlocks) source = mesh;
        if (source is not null) mesh = source;      // the bases are the same list, in the same order
        int n = mesh.Bases.Count;
        int m = mesh.Cells.Count;

        // Per cell: which horizontal bases touch it, and through which half. A vertical basis is not
        // collected at all — see the header.
        var perCellHead = new int[m];
        Array.Fill(perCellHead, -1);
        var next   = new List<int>();
        var basisOf = new List<int>();
        var halfOf  = new List<int>();          // 0 = the A half, 1 = the B half

        for (int i = 0; i < n; i++)
        {
            var b = mesh.Bases[i];
            if (b.Direction == PlanarBasisDirection.Z) continue;
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, b);
            Attach(i, 0, ha.CellIndex);
            Attach(i, 1, hb.CellIndex);

            void Attach(int basis, int which, int c)
            {
                if ((uint)c >= (uint)m) return;
                basisOf.Add(basis); halfOf.Add(which); next.Add(perCellHead[c]);
                perCellHead[c] = next.Count - 1;
            }
        }

        // The supports, once per (basis, half). RooftopSupport.Build is the same call the fill's own
        // weights come from, so the Gram integrates the basis the fill integrates.
        var supports = new RooftopSupport[2 * n];
        var built    = new bool[2 * n];

        RooftopSupport SupportOf(int basis, int which)
        {
            int slot = 2 * basis + which;
            if (!built[slot])
            {
                var (sa, sb) = PlanarBasisFunctions.Supports(mesh, mesh.Bases[basis]);
                supports[2 * basis] = sa; supports[2 * basis + 1] = sb;
                built[2 * basis] = built[2 * basis + 1] = true;
            }
            return supports[slot];
        }

        // (i, j) with i ≤ j → the accumulated integral, plus the sheet class it was accumulated on, and
        // the second class's when the pair's shared cells disagree (EM-TFR, the file header).
        var acc = new Dictionary<(int I, int J), (double V, int Layer, double V2, int Layer2)>();
        bool anySecond = false;

        // PMS-5 (R-pms5-5) — a SUPPORT mesh's cells are support rectangles, and two of them can be one block's metal:
        // the Gram is taken on the block mesh it came from, per physical block, over the supports' intersection.
        if (source is not null)
        {
            foreach (var (bi, bj, g, cell) in PlanarLocalGridFill.GramEntries(source, (cell, p, q) =>
                         CellIntegral(cell, SupportOf(p.Basis, p.Half), SupportOf(q.Basis, q.Half),
                                      source.Bases[p.Basis].Direction == PlanarBasisDirection.X)))
                Add(bi, bj, g, cell);
        }
        else
        for (int c = 0; c < m; c++)
        {
            var cell = mesh.Cells[c];
            for (int p = perCellHead[c]; p >= 0; p = next[p])
            for (int q = perCellHead[c]; q >= 0; q = next[q])
            {
                int bi = basisOf[p], bj = basisOf[q];
                if (bi > bj) continue;                                   // upper triangle only
                var di = mesh.Bases[bi].Direction;
                if (di != mesh.Bases[bj].Direction) continue;            // D5
                // A basis normally has its two halves on two DIFFERENT cells, so bi == bj here means
                // p == q and the term is ∫w². The ordered double loop is deliberately not collapsed:
                // should a merged cell ever carry both halves of one rooftop, the cross term ∫w_A w_B
                // is visited twice — which is exactly the factor of two (w_A + w_B)² needs.
                double g = CellIntegral(cell, SupportOf(bi, halfOf[p]), SupportOf(bj, halfOf[q]),
                                        di == PlanarBasisDirection.X);
                if (g == 0) continue;
                Add(bi, bj, g, cell);
            }
        }

        void Add(int bi, int bj, double g, PlanarCell cell)
        {
            var key = (bi, bj);
            int sheetClass = mesh.SheetClassOf(cell);
            if (!acc.TryGetValue(key, out var prev)) acc[key] = (g, sheetClass, 0.0, -1);
            else if (prev.Layer == sheetClass) acc[key] = (prev.V + g, prev.Layer, prev.V2, prev.Layer2);
            else if (prev.Layer2 < 0 || prev.Layer2 == sheetClass)
            {
                acc[key] = (prev.V, prev.Layer, prev.V2 + g, sheetClass);
                anySecond = true;
            }
            else
                throw new InvalidOperationException(
                    $"Rooftops {bi} and {bj} share cells of three different sheet classes. A " +
                    "same-direction pair shares at most two cells, so this mesh has produced a " +
                    "support the two-contribution Gram slot does not cover.");
        }

        // ── pack to CSR, rows ascending, columns ascending within a row ───────────────────────
        var counts = new int[n];
        foreach (var k in acc.Keys) counts[k.I]++;

        var rowPtr = new int[n + 1];
        for (int i = 0; i < n; i++) rowPtr[i + 1] = rowPtr[i] + counts[i];
        int nnz = rowPtr[n];

        var colIdx = new int[nnz];
        var value  = new double[nnz];
        var layer  = new int[nnz];
        var layer2 = anySecond ? new int[nnz] : null;
        var value2 = anySecond ? new double[nnz] : null;
        var cursor = (int[])rowPtr.Clone();

        // Sorted so the packing is a function of the mesh alone — a dictionary's iteration order is
        // not, and R-fil-11's determinism rule applies to anything the fill reads.
        var keys = new List<(int I, int J)>(acc.Keys);
        keys.Sort(static (a, b) => a.I != b.I ? a.I.CompareTo(b.I) : a.J.CompareTo(b.J));
        foreach (var k in keys)
        {
            int at = cursor[k.I]++;
            colIdx[at] = k.J;
            value[at]  = acc[k].V;
            layer[at]  = acc[k].Layer;
            if (anySecond)
            {
                layer2![at] = Math.Max(acc[k].Layer2, 0);   // a 0 value makes the index immaterial
                value2![at] = acc[k].V2;
            }
        }

        return new PlanarGram(rowPtr, colIdx, value, layer, layer2, value2);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The two closed forms
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>∫ w_m w_n dS over ONE cell the two halves share. See the header.</summary>
    private static double CellIntegral(PlanarCell cell, RooftopSupport sm, RooftopSupport sn,
                                       bool alongX)
    {
        double area = cell.Area;
        if (!(area > 0)) return 0;

        if (sm.IsWholeRectangle && sn.IsWholeRectangle)
        {
            double delta = alongX ? cell.Width  : cell.Height;
            double cross = alongX ? cell.Height : cell.Width;
            if (!(delta > 0) || !(cross > 0)) return 0;

            // The whole-rectangle support carries exactly one strip whose gradient along the flow
            // direction is ±1: +1 for the half whose shared face is on the HIGH side (its ramp rises
            // from the low edge), −1 for the other. Equal gradients ⇒ the same outer edge ⇒ ∫ξ² ;
            // opposite ⇒ one rising against one falling ⇒ ∫ξ(Δ−ξ).
            double gm = alongX ? sm.Strips[0].Alpha : sm.Strips[0].Beta;
            double gn = alongX ? sn.Strips[0].Alpha : sn.Strips[0].Beta;
            return gm * gn > 0 ? delta / (3.0 * cross) : delta / (6.0 * cross);
        }

        if (sm.Strips.Count != sn.Strips.Count)
            throw new InvalidOperationException(
                $"Two same-direction rooftops on one cut cell produced {sm.Strips.Count} and " +
                $"{sn.Strips.Count} weight strips. RooftopSupport.Build takes its breakpoints from " +
                "the region and the flow direction alone, so they must agree — a mismatch means the " +
                "two supports are of different cells or different directions, and pairing them by " +
                "index would integrate one basis against another basis's domain.");

        double cx = cell.CenterX, cy = cell.CenterY;
        double sum = 0;
        for (int k = 0; k < sm.Strips.Count; k++)
        {
            var a = sm.Strips[k];
            var b = sn.Strips[k];
            var ring = a.Ring;

            double a0 = a.At(cx, cy), b0 = b.At(cx, cy);   // the affines' values at the new origin

            double plain = PolygonIntegrals.Area(ring, cx, cy);
            if (plain == 0) continue;
            double sign = plain < 0 ? -1.0 : 1.0;          // the ring's winding, see the header

            double mu = PolygonIntegrals.AreaMoment(ring, cx, cy, alongX: true);
            double mv = PolygonIntegrals.AreaMoment(ring, cx, cy, alongX: false);
            var (uu, uv, vv) = PolygonIntegrals.AreaSecondMoments(ring, cx, cy);

            sum += sign * (a.Alpha * b.Alpha * uu
                         + (a.Alpha * b.Beta + a.Beta * b.Alpha) * uv
                         + a.Beta * b.Beta * vv
                         + (a.Alpha * b0 + a0 * b.Alpha) * mu
                         + (a.Beta  * b0 + a0 * b.Beta)  * mv
                         + a0 * b0 * plain);
        }
        return sum / (area * area);
    }
}
