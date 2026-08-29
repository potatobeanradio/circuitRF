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
    /// which the mesher does not produce but a hand-built mesh could).</summary>
    public readonly bool[] Classifiable;

    private PairClassifier(Axis x, Axis y, bool[] classifiable)
    {
        X = x; Y = y; Classifiable = classifiable;
    }

    public long Bytes => X.Bytes + Y.Bytes + Classifiable.Length;

    public static PairClassifier Build(PlanarMesh mesh)
    {
        var x = Axis.Build(mesh.GridX);
        var y = Axis.Build(mesh.GridY);
        int m = mesh.Cells.Count;
        var ok = new bool[m];
        for (int c = 0; c < m; c++)
        {
            var cell = mesh.Cells[c];
            if (cell.IsCut) continue;
            if (cell.IX < 0 || cell.IX >= x.Count || cell.IY < 0 || cell.IY >= y.Count) continue;
            double gw = mesh.GridX[cell.IX + 1] - mesh.GridX[cell.IX];
            double gh = mesh.GridY[cell.IY + 1] - mesh.GridY[cell.IY];
            ok[c] = Math.Abs(cell.Width - gw) <= SpacingTolerance * gw
                 && Math.Abs(cell.Height - gh) <= SpacingTolerance * gh
                 && cell.XMin == mesh.GridX[cell.IX] && cell.YMin == mesh.GridY[cell.IY];
        }
        return new PairClassifier(x, y, ok);
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
