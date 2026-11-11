// P5 — translation classes of cell pairs (brief-em-p5-translation-class-memo.md, 2026-08-29).
//
// The mesh is a tensor-product grid (PlanarMesh, D8) and every kernel is a function of separation
// alone, so the seven P4 primitives of an ORDERED cell pair depend only on
// (w_a, h_a, w_c, h_c, Δx, Δy) and on the quadrature rule the pair's separation selects. Two pairs
// with the same six numbers and the same rule get the same integrals, and until P5 the fill
// computed them again — 2.5× (a short line) to 30× (a long taper) over, counted on the shipping
// mesher and recorded in HISTORY.md §P5.
//
// THE KEY IS ON GRID INDICES, NOT ON DOUBLES. Along one axis the pair is described by the LIST of
// spacings read from the outer cell toward the inner one, inclusive — its first element is w_a,
// its last is w_c, and Δ is the sum of everything but the last. Two lists are the same class when
// they are the same sequence of SPACING CLASSES, where a spacing class is a set of gridline
// differences equal to 1e-12 relative. Exact `==` was the brief's first choice and it does not
// hold: the mesher writes a bulk gridline as `a + len·i/n`, and on the hero's x axis that gives
// 15 exactly-distinct spacings for what are 6 at 1e-12 (the in-class spread reaches 4e-13 on the
// GaAs line and the 60 mm taper). So the differences ARE quantised, at 1e-12 relative, and a class
// is represented by its smallest member. Nothing else in the key is a double: a list is
// hash-consed element by element into an integer id (PairClassifier.Axis.ListId), and a list read
// DOWNWARD in index gets the same id as the equal list read upward, which is what makes the two
// graded ends of a symmetric line one class rather than two.
//
// ORIENTATION IS PRESERVED; 180° ROTATION IS FOLDED. P4 learned that the outer (Gauss) and inner
// (closed-form) roles are not interchangeable to 1e-12, so a class never swaps them: the outer
// cell of every member is the outer cell of the representative. What IS folded is the rotation
// (Δx, Δy) → (−Δx, −Δy) about the outer cell, which the brief's "(Δx, Δy) ≥ 0 lexicographically"
// asks for: the outer rule's node set is mirror-symmetric, and under the rotation a cell's rising
// ramp becomes its falling one, so a rotated member reads the representative's (A, B) halves
// swapped — `Combine(outerB ^ rot, innerB ^ rot, …)`, one xor, no arithmetic (PlanarFill's P4.2).
//
// THE RULE IS IN THE KEY. A pair's quadrature rule is chosen from τ = separation ÷ diagonal
// against fixed thresholds, and a family of pairs sits EXACTLY on one of them: two equal cells
// offset by (4, 4) cells have τ = 4·√(w²+h²)/√(w²+h²) = 4 in exact arithmetic, and floating point
// decides per pair which side of FarRatio each lands on. A class that carried its representative's
// rule to such a member would move that entry by the rule change (~1e-6), so the τ band, computed
// from the member's own floats exactly as RuleFor computes it, is part of the key and a class is
// never asked to serve two rules.
//
// THE REPRESENTATIVE IS SYNTHETIC AND DETERMINISTIC. The class primitives are computed on a pair
// built from the key alone — outer cell [0, w_a] × [0, h_a], inner at the class's (Δx, Δy) — rather
// than on whichever member happened to be visited first. That is what makes the dense fill and
// AIM's on-demand near fill produce the SAME bits for the same class (PlanarEntryFill.At is gated
// bit-identical to PlanarFill.Fill), and what keeps R-fil-11 under AIM's row-parallel near fill,
// where a first-visitor representative would depend on the scheduler. It is also why the gate
// against P4 is 1e-12 and not bit-identity: every member's ρ is now computed from the
// representative's coordinates rather than its own, and the last bits move — measured at up to
// 5e-13 on the closed-form far core of two cells 0.25 m apart before a line of P5 was written.

using System.Runtime.CompilerServices;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The translation-class key of every ordered cell pair of one mesh — see the file header. Built
/// once per mesh by both core builders; O(n_x² + n_y²) memory, none of it per cell pair.
/// </summary>
internal sealed class PairClassifier
{
    /// <summary>Spacing classes are equal to this, relative — see the header for why exact
    /// equality was not available.</summary>
    public const double SpacingTolerance = 1e-12;

    /// <summary>One grid axis: its spacing classes and the hash-consed spacing LISTS.</summary>
    internal sealed class Axis
    {
        /// <summary>Cells along the axis (gridlines − 1).</summary>
        public readonly int Count;
        /// <summary>Spacing class of each cell index, 0 … <see cref="ClassCount"/> − 1, in
        /// ascending order of spacing.</summary>
        public readonly int[] Cls;
        /// <summary>Representative spacing per class — the smallest member, which for a mesher that
        /// wrote the spacings exactly equal is the spacing itself.</summary>
        public readonly double[] Rep;
        /// <summary>How many spacings were exactly distinct before quantisation — the report's
        /// evidence that quantisation was needed.</summary>
        public readonly int ExactlyDistinct;
        /// <summary><c>Count × Count</c>, row <c>from</c>: the canonical id of the spacing list read
        /// from cell <c>from</c> toward cell <c>to</c>, both ends inclusive.</summary>
        public readonly int[] ListId;
        /// <summary>Per list id: the spacing class of its first / last element.</summary>
        public readonly int[] First, Last;
        /// <summary>Per list id: Σ of every element but the last — the offset from the outer cell's
        /// near edge to the inner cell's near edge along the reading direction. Compensated
        /// (Neumaier) so a 2,000-cell list carries no accumulated rounding.</summary>
        public readonly double[] Delta;

        public int ClassCount => Rep.Length;
        public int ListCount  => First.Length;

        private Axis(int count, int[] cls, double[] rep, int exactlyDistinct, int[] listId,
                     int[] first, int[] last, double[] delta)
        {
            Count = count; Cls = cls; Rep = rep; ExactlyDistinct = exactlyDistinct;
            ListId = listId; First = first; Last = last; Delta = delta;
        }

        public long Bytes => 4L * (Cls.Length + ListId.Length + First.Length + Last.Length)
                           + 8L * (Rep.Length + Delta.Length);

        public static Axis Build(IReadOnlyList<double> grid)
        {
            int n = grid.Count - 1;
            var spacing = new double[n];
            for (int i = 0; i < n; i++) spacing[i] = grid[i + 1] - grid[i];

            // ── spacing classes: sort, then merge runs within the tolerance of the class's own
            //    smallest member ─────────────────────────────────────────────────────────────
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (p, q) => spacing[p].CompareTo(spacing[q]));

            var cls  = new int[n];
            var reps = new List<double>();
            int exact = 0;
            double prevExact = double.NaN;
            for (int k = 0; k < n; k++)
            {
                double v = spacing[order[k]];
                if (v != prevExact) { exact++; prevExact = v; }
                if (reps.Count == 0 || v - reps[^1] > SpacingTolerance * reps[^1]) reps.Add(v);
                cls[order[k]] = reps.Count - 1;
            }
            var rep = reps.ToArray();

            // ── lists, hash-consed: id(list + e) = dict[(id(list), class(e))] ─────────────────
            var dict  = new Dictionary<(int Prev, int Cls), int>();
            var first = new List<int>();
            var last  = new List<int>();
            var sum   = new List<double>();
            var comp  = new List<double>();

            int Extend(int prev, int c)
            {
                if (dict.TryGetValue((prev, c), out int id)) return id;
                id = first.Count;
                dict[(prev, c)] = id;
                if (prev < 0)
                {
                    first.Add(c); last.Add(c); sum.Add(0.0); comp.Add(0.0);
                }
                else
                {
                    // the previous last element stops being last, so it joins Δ
                    double v = rep[last[prev]];
                    double s = sum[prev], t = s + v;
                    double cmp = comp[prev] + (Math.Abs(s) >= Math.Abs(v) ? (s - t) + v : (v - t) + s);
                    first.Add(first[prev]); last.Add(c); sum.Add(t); comp.Add(cmp);
                }
                return id;
            }

            var listId = new int[(long)n * n];
            for (int from = 0; from < n; from++)
            {
                int id = Extend(-1, cls[from]);
                listId[(long)from * n + from] = id;
                int fwd = id;
                for (int to = from + 1; to < n; to++)
                {
                    fwd = Extend(fwd, cls[to]);
                    listId[(long)from * n + to] = fwd;
                }
                int bwd = id;
                for (int to = from - 1; to >= 0; to--)
                {
                    bwd = Extend(bwd, cls[to]);
                    listId[(long)from * n + to] = bwd;
                }
            }

            var delta = new double[sum.Count];
            for (int i = 0; i < delta.Length; i++) delta[i] = sum[i] + comp[i];
            return new Axis(n, cls, rep, exact, listId, first.ToArray(), last.ToArray(), delta);
        }
    }

    public readonly Axis X, Y;
    /// <summary>Per cell: a whole rectangle whose width and height ARE its grid spacings (a cut or
    /// merged cell is never memoised; neither is any cell whose rectangle disagrees with the grid,
    /// which the mesher does not produce but a hand-built mesh could). PMS-5: or a whole rectangle that
    /// IS a block of the grid's lines, which a support mesh's blocks and strips are.</summary>
    public readonly bool[] Classifiable;

    /// <summary>PMS-5 — the block axes, or null on a mesh whose classifiable cells are all elementary (every
    /// tensor-grid mesh), on which the key is exactly P5's.</summary>
    public readonly BlockAxis? BX, BY;

    private PairClassifier(Axis x, Axis y, bool[] classifiable, BlockAxis? bx, BlockAxis? by)
    {
        X = x; Y = y; Classifiable = classifiable; BX = bx; BY = by;
    }

    public long Bytes => X.Bytes + Y.Bytes + Classifiable.Length + (BX?.Bytes ?? 0) + (BY?.Bytes ?? 0);

    // ── PMS-5 — a pair with a block or a strip in it ──────────────────────────────────────────────
    //
    // R-pms5-3. P5's key reads a pair as the LIST of spacings from the outer cell to the inner one, which
    // presumes each cell is one spacing. A support mesh's cell is a run of them, and two supports can
    // overlap (a strip inside its block), so neither "from" nor "toward" is defined. Along one axis the
    // pair is instead its three edge positions measured from the outer support's low edge — the outer's
    // high edge, the inner's low and high edges — QUANTISED to 2⁻⁴⁰ of the axis's extent. That is the
    // same job P5's 1e-12-relative spacing classes do (the mesher writes a bulk line as a + len·i/n, and
    // equal spans differ in the last bits), done on positions rather than spacings so a coincident
    // gridline stays coincident in the representative: two edges on one line are the same difference
    // from the same origin and quantise identically, so a strip and its block still share three edges
    // exactly and OverlapPanels sees the same pair the member is. Same-size supports at the same offset
    // share a class wherever they are, which on a coarse region of uniform blocks is most of them.
    //
    // A pair of two ELEMENTARY cells keeps P5's key bit for bit, so a support mesh's tensor part is classed
    // exactly as the tensor grid's is. A block key carries sign pattern 7, which P5's patterns (0–4)
    // never use, and is never rotated: the 180° fold is P5's economy and not a correctness matter.

    /// <summary>The sign-pattern value that marks a PMS-5 block key.</summary>
    internal const int BlockPattern = 7;

    /// <summary>PMS-5 — one axis's support spans and the quantised geometry of every pair of them.</summary>
    internal sealed class BlockAxis
    {
        public readonly int Lines;          // gridlines
        public readonly int[] SpanAt;       // (lo · Lines + hi) → span id, or −1
        public readonly int Spans;
        public readonly int[] PairId;       // (outer span · Spans + inner span) → pair-geometry id
        public readonly long[] Hi, InnerLo, InnerHi;   // per pair-geometry id, in quanta from the outer's low edge
        public readonly double Quantum;

        private BlockAxis(int lines, int[] spanAt, int spans, int[] pairId, long[] hi, long[] lo2, long[] hi2, double q)
        {
            Lines = lines; SpanAt = spanAt; Spans = spans; PairId = pairId;
            Hi = hi; InnerLo = lo2; InnerHi = hi2; Quantum = q;
        }

        public long Bytes => 4L * (SpanAt.Length + PairId.Length) + 8L * (Hi.Length + InnerLo.Length + InnerHi.Length);

        public int Id(int outerLo, int outerHi, int innerLo, int innerHi)
            => PairId[SpanAt[outerLo * Lines + outerHi] * Spans + SpanAt[innerLo * Lines + innerHi]];

        public static BlockAxis Build(IReadOnlyList<double> grid, IEnumerable<(int Lo, int Hi)> used)
        {
            int lines = grid.Count;
            var spanAt = new int[(long)lines * lines];
            Array.Fill(spanAt, -1);
            var los = new List<int>(); var his = new List<int>();
            foreach (var (lo, hi) in used)
            {
                long k = (long)lo * lines + hi;
                if (spanAt[k] >= 0) continue;
                spanAt[k] = los.Count; los.Add(lo); his.Add(hi);
            }
            int sCount = los.Count;
            double quantum = Math.Max(grid[^1] - grid[0], double.Epsilon) * Math.Pow(2, -40);
            long Q(double v) => (long)Math.Round(v / quantum);

            (long, long, long) Geometry(int a, int c)
            {
                double o = grid[los[a]];
                return (Q(grid[his[a]] - o), Q(grid[los[c]] - o), Q(grid[his[c]] - o));
            }

            var distinct = new HashSet<(long, long, long)>();
            for (int a = 0; a < sCount; a++)
                for (int c = 0; c < sCount; c++) distinct.Add(Geometry(a, c));
            var sorted = distinct.ToArray();
            Array.Sort(sorted);                       // ids are a function of the mesh alone (R-fil-11)
            var idOf = new Dictionary<(long, long, long), int>(sorted.Length);
            for (int i = 0; i < sorted.Length; i++) idOf[sorted[i]] = i;
            if (sorted.Length > IdMask)
                throw new InvalidOperationException(
                    $"{sorted.Length:N0} distinct support-pair geometries on one axis exceed the class key's {IdBits} bits.");

            var pairId = new int[(long)sCount * sCount];
            for (int a = 0; a < sCount; a++)
                for (int c = 0; c < sCount; c++) pairId[(long)a * sCount + c] = idOf[Geometry(a, c)];
            var hiArr = new long[sorted.Length]; var lo2 = new long[sorted.Length]; var hi2 = new long[sorted.Length];
            for (int i = 0; i < sorted.Length; i++) (hiArr[i], lo2[i], hi2[i]) = sorted[i];
            return new BlockAxis(lines, spanAt, sCount, pairId, hiArr, lo2, hi2, quantum);
        }
    }

    public static PairClassifier Build(PlanarMesh mesh)
    {
        var x = Axis.Build(mesh.GridX);
        var y = Axis.Build(mesh.GridY);
        int m = mesh.Cells.Count;
        var ok = new bool[m];
        bool anyBlock = false;
        for (int c = 0; c < m; c++)
        {
            var cell = mesh.Cells[c];
            if (cell.IsCut) continue;
            if (cell.IX < 0 || cell.IX >= x.Count || cell.IY < 0 || cell.IY >= y.Count) continue;
            if (cell.IsBlock)
            {
                // PMS-5 — a support mesh's block or strip: its rectangle must BE its gridlines' rectangle.
                if (cell.IX1 > x.Count || cell.IY1 > y.Count) continue;
                ok[c] = cell.XMin == mesh.GridX[cell.IX] && cell.XMax == mesh.GridX[cell.IX1]
                     && cell.YMin == mesh.GridY[cell.IY] && cell.YMax == mesh.GridY[cell.IY1];
                anyBlock |= ok[c];
                continue;
            }
            double gw = mesh.GridX[cell.IX + 1] - mesh.GridX[cell.IX];
            double gh = mesh.GridY[cell.IY + 1] - mesh.GridY[cell.IY];
            ok[c] = Math.Abs(cell.Width - gw) <= SpacingTolerance * gw
                 && Math.Abs(cell.Height - gh) <= SpacingTolerance * gh
                 && cell.XMin == mesh.GridX[cell.IX] && cell.YMin == mesh.GridY[cell.IY];
        }

        BlockAxis? bx = null, by = null;
        if (anyBlock)
        {
            var sx = new List<(int, int)>(); var sy = new List<(int, int)>();
            for (int c = 0; c < m; c++)
            {
                if (!ok[c]) continue;
                var cell = mesh.Cells[c];
                sx.Add((cell.IX, cell.IX1)); sy.Add((cell.IY, cell.IY1));
            }
            bx = BlockAxis.Build(mesh.GridX, sx);
            by = BlockAxis.Build(mesh.GridY, sy);
        }
        return new PairClassifier(x, y, ok, bx, by);
    }

    // ── the key ───────────────────────────────────────────────────────────────────────────────
    //
    //   bits 0–1   τ band: 0 self, 1 near, 2 mid, 3 far — RuleFor's own thresholds
    //   bits 2–4   sign pattern after rotation: 0 (0,0) · 1 (0,+) · 2 (+,−) · 3 (+,0) · 4 (+,+)
    //   bits 5–33  x list id     bits 34–62  y list id

    private const int  IdBits = 29;
    private const long IdMask = (1L << IdBits) - 1;

    /// <summary>Both cells must be <see cref="Classifiable"/>; the caller checks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Key(PlanarCell outer, PlanarCell inner, PlanarFillSettings st, out bool rotated)
    {
        if (BX is not null && (outer.IsBlock || inner.IsBlock))
        {
            rotated = false;
            long bxId = BX.Id(outer.IX, outer.IX1, inner.IX, inner.IX1);
            long byId = BY!.Id(outer.IY, outer.IY1, inner.IY, inner.IY1);
            return (long)Band(outer, inner, st) | ((long)BlockPattern << 2) | (bxId << 5) | (byId << 34);
        }
        int sx = Math.Sign(inner.IX - outer.IX), sy = Math.Sign(inner.IY - outer.IY);
        rotated = sx < 0 || (sx == 0 && sy < 0);
        if (rotated) { sx = -sx; sy = -sy; }
        int pattern = sx == 0 ? (sy == 0 ? 0 : 1) : (sy < 0 ? 2 : sy == 0 ? 3 : 4);
        long xId = X.ListId[(long)outer.IX * X.Count + inner.IX];
        long yId = Y.ListId[(long)outer.IY * Y.Count + inner.IY];
        return (long)Band(outer, inner, st) | ((long)pattern << 2) | (xId << 5) | (yId << 34);
    }

    /// <summary>RuleFor's own selection, as a small integer — identical thresholds, identical
    /// arithmetic, so a pair sitting on a threshold lands where the per-pair rule put it.</summary>
    public static int Band(PlanarCell a, PlanarCell b, PlanarFillSettings st)
    {
        double dx = a.CentroidX - b.CentroidX, dy = a.CentroidY - b.CentroidY;
        double d  = Math.Sqrt(dx * dx + dy * dy);
        double s  = Math.Max(Math.Sqrt(a.Width * a.Width + a.Height * a.Height),
                             Math.Sqrt(b.Width * b.Width + b.Height * b.Height));
        double tau = s > 0 ? d / s : 0.0;
        if (tau == 0)           return 0;
        if (tau < st.NearRatio) return 1;
        if (tau < st.FarRatio)  return 2;
        return 3;
    }

    public static int BandOf(long key) => (int)(key & 3);

    /// <summary>The outer rule of a class — <see cref="PlanarFill.RuleForCells"/>'s answer for its band.</summary>
    public static (int Nodes, int Panels) CoreRule(long key, PlanarFillSettings st) => BandOf(key) switch
    {
        0 => (st.NearNodes, st.SelfPanels),
        1 => (st.NearNodes, st.TouchPanels),
        2 => (st.MidNodes, 1),
        _ => (st.FarNodes, 1),
    };

    /// <summary>The remainder rule of a class.</summary>
    public static int RemainderNodes(long key, PlanarFillSettings st) => BandOf(key) switch
    {
        0 or 1 => st.RemainderNodesNear,
        2      => st.RemainderNodesMid,
        _      => st.RemainderNodesFar,
    };

    /// <summary>
    /// The class's synthetic representative pair — outer cell at the origin, inner cell at the
    /// class's signed offset, every dimension the representative spacing of its class. A pure
    /// function of the key: see the header for why that, and not a first-visited member, is the
    /// representative.
    /// </summary>
    public (PlanarCell Outer, PlanarCell Inner) Representative(long key)
    {
        int pattern = (int)((key >> 2) & 7);
        int xId = (int)((key >> 5) & IdMask);
        int yId = (int)((key >> 34) & IdMask);

        if (pattern == BlockPattern)
        {
            // PMS-5 — outer at the origin, every edge at its quantised position, so coincident lines coincide.
            double qx = BX!.Quantum, qy = BY!.Quantum;
            // Both marked as blocks (IX1 = 2), so a pair keyed here is integrated as the member pair is — PlanarLocalGridFill.
            // OuterPanels' long-side rule reads whether either cell of a pair is a block.
            var o = new PlanarCell(0, 0, 0, 0.0, 0.0, BX.Hi[xId] * qx, BY.Hi[yId] * qy) { IX1 = 2, IY1 = 2 };
            var i = new PlanarCell(0, 0, 0, BX.InnerLo[xId] * qx, BY.InnerLo[yId] * qy,
                                   BX.InnerHi[xId] * qx, BY.InnerHi[yId] * qy) { IX1 = 2, IY1 = 2 };
            return (o, i);
        }

        double wA = X.Rep[X.First[xId]], wC = X.Rep[X.Last[xId]], dx = X.Delta[xId];
        double hA = Y.Rep[Y.First[yId]], hC = Y.Rep[Y.Last[yId]], dy = Y.Delta[yId];

        int sx = pattern >= 2 ? 1 : 0;
        int sy = pattern is 1 or 4 ? 1 : pattern == 2 ? -1 : 0;

        // Read from the outer cell toward the inner one, Δ is the sum of all elements but the
        // last. Along a positive axis that is the offset between the two near edges. Along a
        // NEGATIVE axis the inner cell's near edge sits Σ(all but the FIRST) below the outer's,
        // and Σ(all but first) = Δ + w_c − w_a.
        double xMin = sx > 0 ? dx : 0.0;
        double yMin = sy > 0 ? dy : sy < 0 ? -(dy + hC - hA) : 0.0;

        var outer = new PlanarCell(0, 0, 0, 0.0, 0.0, wA, hA);
        var inner = new PlanarCell(0, 0, 0, xMin, yMin, xMin + wC, yMin + hC);
        return (outer, inner);
    }
}
