// PMS-5 — the fill on a locally refined grid: the support mesh, the overlap rule and the Gram across a step.
//
// R-pms5-1 — THE PULSE DOMAINS ARE RECTANGLES, NOT CELLS. On a block mesh a rooftop across a refinement step has, on its
// coarse side, a half-support that is a STRIP of the coarse block (D-pms4-4): full depth along the current, the segment's
// width across it, pulse ±1/(strip area). Everything the fill integrates is therefore still a rectangle with a separable
// weight — a pulse, or a ramp along the flow over a constant across it — and RectangleIntegrals / SingularExtraction
// already take any rectangle. What changes is WHICH rectangles are paired.
//
// So the fill is not rewritten; it is handed a different mesh. The SUPPORT MESH has one cell per distinct support
// rectangle (a whole block, or one of its strips — many rooftops share a support, so they are deduplicated by their
// gridline indices), and the source's bases, in the source's order, each naming its two supports as CellA/CellB. Every
// path of the fill — the class table, the per-entry fill, the multi-level blocks, the via arms — then integrates exactly
// the supports, with no new code of its own: a support-pair potential IS a cell-pair potential on this mesh, and a
// ramp half is the support's ramp from its own outer edge. On a 1 × 1 mesh there is no support mesh at all (the source
// is the fill's mesh, HasBlocks is false), which is what makes the degenerate case bit-identical by construction rather
// than by tolerance.
//
// TWO THINGS ARE NOT TRUE OF A SUPPORT MESH THAT ARE TRUE OF A TENSOR ONE, AND EACH HAS ITS OWN ANSWER HERE:
//
//  (1) ITS CELLS OVERLAP. A strip lies inside its parent block, which is itself the support of the rooftop on the block's
//      other face, and the strips of two faces cross. A pair of overlapping supports is no longer "the same cell" (τ = 0,
//      the self rule) or "touching" (the near rule clustered at the outer cell's own edges): the inner rectangle's edges
//      run THROUGH the outer one, and the inner closed form's log-derivative singularity lies along a line the outer rule
//      does not cluster to. OuterPanels splits the outer domain at every inner edge that crosses it, so each piece meets
//      the inner rectangle only at its own boundary — the self or touching situation again — and integrates each piece on
//      the SELF panels. The weight stays the outer support's own; only the quadrature domain is cut. A pair that does not
//      overlap takes the one-piece path with exactly the expressions it always had.
//
//      A strip is also LONG: one elementary column across a block's whole depth reaches 194:1 on fixture S, where the
//      self rule on its own four clustered panels misses the fill's 5e-6 gate by a decade (R-pms5-2). In a near or
//      overlapping pair with a block or strip in it, a piece longer than MaxPieceAspect widths is cut into equal lengths
//      along its long side, each on the same panels. A pair of two elementary cells never is.
//
//  (2) ITS GRAM IS NOT PER CELL. CL1's ⟨f_m, f_n⟩ pairs bases that share a CELL; two bases whose supports are a block
//      and one of its strips are different cells here and the same metal there. The Gram is therefore built on the
//      SOURCE (PlanarGram.Build reads BlockSource), per physical block, over the intersection of every two same-direction
//      supports on it: both span the block's full depth along the current, so the overlap is the intersection of their
//      transverse spans and the integral is closed form (R-pms5-5).

using System.Runtime.CompilerServices;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>PMS-5 — how the fill reads a local-grid mesh.</b> See the file header: a block mesh is filled on its
/// <see cref="SupportMesh"/>, overlapping supports are integrated by <see cref="OuterPanels"/>, and the Gram across a
/// refinement step is <see cref="GramEntries"/>.
/// </summary>
public static class PlanarLocalGridFill
{
    private static readonly ConditionalWeakTable<PlanarMesh, PlanarMesh> Cache = new();

    /// <summary>
    /// <b>R-pms5-1 — the support mesh of a block mesh</b>: one cell per distinct support rectangle, the source's bases in
    /// the source's order naming their supports, and <see cref="PlanarMesh.BlockSource"/> pointing back. A mesh with no
    /// block and no step is returned as it is, and so is a support mesh. Cached per source mesh.
    /// </summary>
    public static PlanarMesh SupportMesh(PlanarMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.BlockSource is not null || !mesh.HasBlocks) return mesh;
        return Cache.GetValue(mesh, Build);
    }

    /// <summary>The fill's own mesh for <paramref name="mesh"/>: its support mesh when it carries blocks.</summary>
    internal static PlanarMesh ForFill(PlanarMesh mesh) => SupportMesh(mesh);

    private readonly record struct Span(int Cell, int X0, int X1, int Y0, int Y1);

    private static PlanarMesh Build(PlanarMesh src)
    {
        var gx = src.GridX; var gy = src.GridY;
        var index = new Dictionary<Span, int>();   // looked up, never iterated (R-msh-2)
        var spans = new List<Span>();
        int n = src.Bases.Count;
        var halfA = new int[n]; var halfB = new int[n];

        int Of(Span s)
        {
            if (!index.TryGetValue(s, out int k)) { k = spans.Count; index[s] = k; spans.Add(s); }
            return k;
        }
        Span Whole(int c) { var cell = src.Cells[c]; return new(c, cell.IX, cell.IX1, cell.IY, cell.IY1); }

        for (int i = 0; i < n; i++)
        {
            var b = src.Bases[i];
            if (b.Direction == PlanarBasisDirection.Z || !b.IsSegment)
            {
                halfA[i] = Of(Whole(b.CellA));
                halfB[i] = Of(Whole(b.CellB));
                continue;
            }
            var a = src.Cells[b.CellA]; var c = src.Cells[b.CellB];
            if (b.Direction == PlanarBasisDirection.X)
            {
                halfA[i] = Of(new(b.CellA, a.IX, a.IX1, b.SegmentLo, b.SegmentHi));
                halfB[i] = Of(new(b.CellB, c.IX, c.IX1, b.SegmentLo, b.SegmentHi));
            }
            else
            {
                halfA[i] = Of(new(b.CellA, b.SegmentLo, b.SegmentHi, a.IY, a.IY1));
                halfB[i] = Of(new(b.CellB, b.SegmentLo, b.SegmentHi, c.IY, c.IY1));
            }
        }

        // The order is (LayerIndex, IY, IX, IY1, IX1): integers, as R-msh-2 asks, and on a mesh's tensor part it is the
        // source's own (LayerIndex, IY, IX).
        var order = new int[spans.Count];
        for (int k = 0; k < order.Length; k++) order[k] = k;
        Array.Sort(order, (p, q) =>
        {
            var (sp, sq) = (spans[p], spans[q]);
            int lp = src.Cells[sp.Cell].LayerIndex, lq = src.Cells[sq.Cell].LayerIndex;
            if (lp != lq) return lp.CompareTo(lq);
            if (sp.Y0 != sq.Y0) return sp.Y0.CompareTo(sq.Y0);
            if (sp.X0 != sq.X0) return sp.X0.CompareTo(sq.X0);
            if (sp.Y1 != sq.Y1) return sp.Y1.CompareTo(sq.Y1);
            if (sp.X1 != sq.X1) return sp.X1.CompareTo(sq.X1);
            return sp.Cell.CompareTo(sq.Cell);
        });
        var rank = new int[spans.Count];
        var cells = new PlanarCell[spans.Count];
        for (int r = 0; r < order.Length; r++)
        {
            var s = spans[order[r]];
            rank[order[r]] = r;
            var c = src.Cells[s.Cell];
            if (s.X0 == c.IX && s.X1 == c.IX1 && s.Y0 == c.IY && s.Y1 == c.IY1) { cells[r] = c; continue; }
            if (c.IsCut)
                throw new InvalidOperationException(
                    $"Cell {s.Cell} is a cut cell and a rooftop across a refinement step asks for a strip of it. The local " +
                    "grid keeps every cut cell elementary, so a strip of one means the mesh is not one the local grid made.");
            cells[r] = c with
            {
                IX = s.X0, IY = s.Y0, IX1 = s.X1, IY1 = s.Y1,
                XMin = gx[s.X0], XMax = gx[s.X1], YMin = gy[s.Y0], YMax = gy[s.Y1],
            };
        }

        var bases = new PlanarBasis[n];
        for (int i = 0; i < n; i++)
            bases[i] = src.Bases[i] with
            {
                CellA = rank[halfA[i]], CellB = rank[halfB[i]], SegmentLo = -1, SegmentHi = -1,
            };

        return new PlanarMesh(cells, bases, src.LayerNames, src.GridX, src.GridY)
        {
            Films = src.Films,
            Stacks = src.Stacks,
            Merge = src.Merge,
            BlockSource = src,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // (1) Overlapping supports — the outer domain, cut at the inner rectangle's edges
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True when the interiors of <paramref name="a"/> and <paramref name="b"/> intersect and the two are not the same
    /// rectangle — a pair a tensor grid never produces (its cells are equal or disjoint).
    /// </summary>
    /// <summary>
    /// <b>R-pms5-2 — the longest piece, in widths, the outer rule integrates on one set of panels when a block or a strip
    /// is in a NEAR or overlapping pair.</b> Measured on the worst strip fixture S produces (194:1): the self rule on its own panels is
    /// 4.3e-5 from the oracle, past the fill's 5e-6 gate. A pair of two elementary cells never reads it, so every
    /// tensor-grid entry is untouched. RESOLVED.md §PMS-5 has the ladder.
    /// </summary>
    internal const double MaxPieceAspect = 16.0;

    /// <summary>How many equal pieces a w × h piece is cut into along its long side. A whole number of elementary cells
    /// often puts the aspect EXACTLY on a multiple of <see cref="MaxPieceAspect"/>, and a class representative's quantised
    /// edges sit within ~1e-10 of the member's — so the count is taken a billionth below, and a member and its class
    /// cannot land on different sides of a threshold (P5 keys its τ band for the same reason).</summary>
    private static int Pieces(double w, double h)
        => Math.Max(1, (int)Math.Ceiling((w >= h ? w / h : h / w) / MaxPieceAspect * (1 - 1e-9)));

    internal static bool Overlap(PlanarCell a, PlanarCell b)
        => a.XMin < b.XMax && b.XMin < a.XMax && a.YMin < b.YMax && b.YMin < a.YMax
        && !(a.XMin == b.XMin && a.XMax == b.XMax && a.YMin == b.YMin && a.YMax == b.YMax);

    /// <summary>
    /// <b>The outer quadrature's panels for the ordered pair (outer <paramref name="a"/>, inner <paramref name="b"/>)</b>, as
    /// rectangles in metres: the <paramref name="panels"/> × <paramref name="panels"/> Chebyshev-clustered panels of
    /// <paramref name="a"/> for a pair that does not overlap — the very boxes every caller computed inline, with the same
    /// expressions — and, for an overlapping pair, <paramref name="a"/> cut at every edge of <paramref name="b"/> that
    /// crosses its interior, each piece on <paramref name="overlapPanels"/> × <paramref name="overlapPanels"/>.
    /// </summary>
    internal static List<(double X0, double X1, double Y0, double Y1)> OuterPanels(
        PlanarCell a, PlanarCell b, int panels, int overlapPanels, bool longSide = true)
    {
        var boxes = new List<(double, double, double, double)>();
        // Only a near pair (clustered panels) needs the long side panelled; a far pair's integrand is smooth over the
        // whole strip, and panelling it would multiply the cost of every pair a long strip is in.
        bool stretch = longSide && (a.IsBlock || b.IsBlock) && panels > 1;
        if (!Overlap(a, b))
        {
            if (!stretch || Pieces(a.Width, a.Height) == 1)
            {
                var t = PlanarFill.PanelEdgesFor(panels);
                for (int qx = 0; qx < panels; qx++)
                    for (int qy = 0; qy < panels; qy++)
                        boxes.Add((a.XMin + t[qx] * a.Width,  a.XMin + t[qx + 1] * a.Width,
                                   a.YMin + t[qy] * a.Height, a.YMin + t[qy + 1] * a.Height));
                return boxes;
            }
            Piece(boxes, a.XMin, a.Width, a.YMin, a.Height, panels, true);
            return boxes;
        }

        var xs = Cuts(a.XMin, a.XMax, b.XMin, b.XMax);
        var ys = Cuts(a.YMin, a.YMax, b.YMin, b.YMax);
        for (int i = 0; i + 1 < xs.Count; i++)
            for (int j = 0; j + 1 < ys.Count; j++)
                Piece(boxes, xs[i], xs[i + 1] - xs[i], ys[j], ys[j + 1] - ys[j], overlapPanels, longSide);
        return boxes;

        // R-pms5-2 — a piece longer than MaxPieceAspect times its width is cut into equal lengths along its long side,
        // each on the clustered panels: the outer integrand's edge structure lives on the scale of the SHORT side, and
        // four clustered panels along a 200:1 strip put the first node a long way from the end it has to resolve.
        static void Piece(List<(double, double, double, double)> boxes, double x0, double w, double y0, double h, int p,
                          bool longSide)
        {
            int k = longSide ? Pieces(w, h) : 1;
            bool longX = w >= h;
            var e = PlanarFill.PanelEdgesFor(p);
            for (int s = 0; s < k; s++)
            {
                double px0 = longX ? x0 + w * s / k : x0, pw = longX ? w / k : w;
                double py0 = longX ? y0 : y0 + h * s / k, ph = longX ? h : h / k;
                for (int qx = 0; qx < p; qx++)
                    for (int qy = 0; qy < p; qy++)
                        boxes.Add((px0 + e[qx] * pw, px0 + e[qx + 1] * pw, py0 + e[qy] * ph, py0 + e[qy + 1] * ph));
            }
        }

        static List<double> Cuts(double lo, double hi, double c0, double c1)
        {
            var list = new List<double>(4) { lo };
            if (c0 > lo && c0 < hi) list.Add(c0);
            if (c1 > lo && c1 < hi) list.Add(c1);
            list.Add(hi);
            return list;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // (2) The Gram across a refinement step
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-pms5-5 — every nonzero ⟨f_m, f_n⟩ of a block mesh</b>, as (i ≤ j, value, the physical cell's sheet class),
    /// one contribution per physical cell the two share. Per physical cell, over every two same-direction horizontal
    /// halves on it: both supports span the cell's full depth Δ along the current, so they meet on the intersection of
    /// their transverse spans, of length L∩, and with transverse widths L_m, L_n
    /// <code>
    ///   ∫ w_m w_n dS = L∩ · ∫₀^Δ ξ_m ξ_n du / (Δ L_m · Δ L_n) = L∩·Δ / (3 L_m L_n)   same outer edge
    ///                                                       = L∩·Δ / (6 L_m L_n)   opposite outer edges
    /// </code>
    /// which is CL1's Δ/(3L) and Δ/(6L) when the two supports are the same rectangle. A cut cell is only ever a whole
    /// support (the local grid keeps it elementary) and takes CL1's own integral, which <paramref name="cutIntegral"/>
    /// supplies.
    /// </summary>
    internal static IEnumerable<(int I, int J, double Value, PlanarCell Cell)> GramEntries(
        PlanarMesh src, Func<PlanarCell, (int Basis, int Half), (int Basis, int Half), double> cutIntegral)
    {
        int n = src.Bases.Count, m = src.Cells.Count;
        var head = new int[m];
        Array.Fill(head, -1);
        var next = new List<int>(); var slot = new List<(int Basis, int Half)>();
        var supportOf = new List<PlanarStepSupport>();

        for (int i = 0; i < n; i++)
        {
            var b = src.Bases[i];
            if (b.Direction == PlanarBasisDirection.Z) continue;
            var (sa, sb) = PlanarLocalGrid.SupportsAnyCell(src, b);
            Attach(i, 0, sa); Attach(i, 1, sb);
        }

        void Attach(int basis, int half, PlanarStepSupport s)
        {
            slot.Add((basis, half)); supportOf.Add(s); next.Add(head[s.CellIndex]);
            head[s.CellIndex] = next.Count - 1;
        }

        for (int c = 0; c < m; c++)
        {
            var cell = src.Cells[c];
            for (int p = head[c]; p >= 0; p = next[p])
                for (int q = head[c]; q >= 0; q = next[q])
                {
                    int bi = slot[p].Basis, bj = slot[q].Basis;
                    if (bi > bj) continue;
                    var di = src.Bases[bi].Direction;
                    if (di != src.Bases[bj].Direction) continue;
                    double g = cell.IsCut
                        ? cutIntegral(cell, slot[p], slot[q])
                        : StripIntegral(supportOf[p], supportOf[q], di == PlanarBasisDirection.X);
                    if (g != 0) yield return (bi, bj, g, cell);
                }
        }
    }

    private static double StripIntegral(PlanarStepSupport sm, PlanarStepSupport sn, bool alongX)
    {
        double delta = alongX ? sm.XMax - sm.XMin : sm.YMax - sm.YMin;
        double lm = alongX ? sm.YMax - sm.YMin : sm.XMax - sm.XMin;
        double ln = alongX ? sn.YMax - sn.YMin : sn.XMax - sn.XMin;
        double lo = alongX ? Math.Max(sm.YMin, sn.YMin) : Math.Max(sm.XMin, sn.XMin);
        double hi = alongX ? Math.Min(sm.YMax, sn.YMax) : Math.Min(sm.XMax, sn.XMax);
        double cap = hi - lo;
        if (!(cap > 0) || !(delta > 0) || !(lm > 0) || !(ln > 0)) return 0;
        // A half's sign is +1 on the A side (shared face high, ramp rising from the low edge) and −1 on the B side: equal
        // signs share an outer edge.
        double k = sm.Sign * sn.Sign > 0 ? 3.0 : 6.0;
        return cap * delta / (k * lm * ln);
    }
}
