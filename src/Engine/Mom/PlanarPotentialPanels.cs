// CAP-3 — the scalar block's cell potential P, formed in ROWS for a panel of basis columns at a time.
//
// Until CAP-3 every dense fill built D4's P as one m×m complex matrix (16·m² bytes, about 4.2·N² for
// a rooftop mesh's m ≈ N/1.95) and released it before the factorisation. Once CAP-2 halved the matrix
// to its lower triangle (≈ 8·N²), that transient sat on top of the triangle and SET the live peak of
// a frequency point. It is now formed as the rows a panel of the triangle's columns reads, under a byte
// budget (PotentialBudgetBytes), and dropped as soon as no later column reads it.
//
// WHAT A COLUMN READS. Column i of the triangle takes rows j ≥ i, i.e. P[cells of i, cells of j ≥ i].
// So a row of P for a cell of i is needed only from the smallest cell any basis j ≥ i touches — the
// SUFFIX MINIMUM over horizontal bases, which on the mesher's row-major numbering is the owner cell of
// i itself. Via bases sit after every horizontal one (R-via-5's contract) and their foot cells can be
// anywhere, so they are carried as a separate short segment per row (P against the via cells only),
// rather than letting one low via cell stretch every row back to cell 0.
//
// EACH UNORDERED PAIR IS EVALUATED ONCE where it can be. A row is first formed for b ≥ max(a, lo) —
// its upper part — and its lower part (lo ≤ b < a) is copied from row b while row b is still held,
// since P is symmetric and both rows mean P(min, max). A pair is evaluated twice only when the row it
// would be copied from has already been dropped or evicted. Which row an entry came from never
// changes its bits: every entry is P(min(a, b), max(a, b)) by the one expression, exactly as the full
// matrix computed it on a ≤ b and mirrored it. The scalar block's own four-term sum is untouched, so
// the triangle is bit-identical to the full-matrix fill (PlanarPotentialPanelTests).
//
// THE BUDGET IS A CAP, NOT A HINT. Rows a later panel will read are carried while they fit; past half
// the budget the ones read latest are evicted and re-formed when needed. So the transient never
// exceeds PotentialBudgetBytes plus one panel's smallest step, whatever the artwork, and that cap is
// what PlanarSystem.ResidentBytes charges.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace CircuitRF.Engine.Mom;

public static partial class PlanarFill
{
    /// <summary>
    /// <b>CAP-3 — the floor of the bytes a dense fill may hold of the cell potential P at once.</b> Below
    /// it the whole of P fits for every mesh up to ~2,000 cells, which is every calibration standard and
    /// every interactive mesh.
    /// </summary>
    public const long PotentialBudgetFloorBytes = 64L << 20;

    /// <summary>
    /// <b>CAP-3 — the cap on the cell-potential rows a dense fill of <paramref name="n"/> unknowns holds
    /// at once</b>: <see cref="PotentialBudgetFloorBytes"/>, or 1/32 of the stored triangle when that is
    /// larger (≈ 3 % of a frequency point). Large enough that a row-major mesh's window (one grid row of
    /// cells above the panel) is carried rather than re-formed; small enough that it is not what a
    /// frequency point's peak is made of. Changes no answer — only how many panels there are.
    /// </summary>
    public static long PotentialBudgetBytes(int n)
        => Math.Max(PotentialBudgetFloorBytes, BlockLowerMatrix.Bytes(n) / 32);

    /// <summary>One entry of D4's P for an UNORDERED cell pair, asked as (a ≤ b) — the value the
    /// full matrix held at both [a, b] and [b, a].</summary>
    internal interface IPotentialEntry
    {
        Complex At(int a, int b);
    }

    /// <summary>The one-level class path — <see cref="ScalarPotentialMatrix(PlanarFillCores, PlanarKernelTerms)"/>'s
    /// own expression for a ≤ b.</summary>
    internal readonly struct ClassPotential : IPotentialEntry
    {
        private readonly PlanarFillCores _cores;
        private readonly PlanarMesh _mesh;
        private readonly PlanarKernelTerms _terms;
        private readonly Func<double, Complex> _rem;
        private readonly Complex[] _classRem;
        private readonly int[] _band;
        private readonly double[] _table;
        private readonly CutRows? _cut;
        private readonly int _kernels, _stride;
        private readonly PlanarFillSettings _st;

        public ClassPotential(PlanarFillCores cores, PlanarKernelTerms terms, Func<double, Complex> rem,
                              Complex[] classRem)
        {
            _cores = cores; _mesh = cores.Mesh; _terms = terms; _rem = rem; _classRem = classRem;
            _band = cores.BandClass!; _table = cores.ClassCores!; _cut = cores.CutScalar;
            _kernels = cores.Kernels; _stride = cores.ClassStride; _st = cores.Settings;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Complex At(int a, int b)
        {
            var terms = _terms;
            int c = _band[_cores.BandIndex(a, b)];
            Complex v;
            if (c >= 0)
            {
                long o = (long)(c >> 1) * _stride;
                v = terms.Inverse * _table[o] + terms.Log * _table[o + 1];
                if (terms.ExtractsConstant) v += terms.Constant;
                if (terms.ExtractsLinear && _kernels > 2) v += terms.Linear * _table[o + 2];
                v += _classRem[c >> 1];
            }
            else
            {
                var t = _cut!.Get(a, b);
                v = terms.Inverse * t.Inverse + terms.Log * t.Log;
                if (terms.ExtractsConstant) v += terms.Constant;
                if (terms.ExtractsLinear) v += terms.Linear * t.Radius;
                v += PairRemainder(_mesh, Pulse(_mesh, a), Pulse(_mesh, b), PlanarBasisDirection.X, _rem, _st);
            }
            return v;
        }
    }

    /// <summary>A P already held whole — the reference builders' triangle-layout path only.</summary>
    internal readonly struct MatrixPotential(NumFlat.Mat<Complex> p) : IPotentialEntry
    {
        public Complex At(int a, int b) => p[b, a];
    }

    /// <summary>The multi-level path — the private
    /// <c>ScalarPotentialMatrix(PlanarFillCores, MultiLevelPairings, int[])</c>'s expression for a ≤ b.</summary>
    internal readonly struct LevelPotential : IPotentialEntry
    {
        private readonly PlanarFillCores _cores;
        private readonly PlanarMesh _mesh;
        private readonly MultiLevelPairings _pr;
        private readonly int[] _cellLayer;
        private readonly PlanarFillSettings _st;

        public LevelPotential(PlanarFillCores cores, MultiLevelPairings pr, int[] cellLayer)
        {
            _cores = cores; _mesh = cores.Mesh; _pr = pr; _cellLayer = cellLayer; _st = cores.Settings;
        }

        public Complex At(int a, int b) => LevelPotentialEntry(_cores, _mesh, _pr, _cellLayer, _st, a, b);
    }

    /// <summary>
    /// <b>CAP-3 — the scalar block written column panel by column panel</b>, from rows of P formed
    /// under <paramref name="budgetBytes"/> (see this file's header). Every entry is assigned exactly
    /// as the full-matrix block assigned it: <c>scalarScale · Σ sign·sign·P</c> in the same order.
    /// </summary>
    internal static void ScalarBlockInPanels<T>(BlockLowerMatrix z, (RooftopHalf A, RooftopHalf B)[] halves,
                                               PlanarMesh mesh, Complex scalarScale, T pot,
                                               PlanarFillSettings st, long budgetBytes)
        where T : struct, IPotentialEntry
    {
        var plan = new PotentialRowPlan(mesh, halves);
        int n = halves.Length, m = plan.M, nh = plan.HorizontalCount, nv = plan.Via.Length;
        long budget = budgetBytes > 0 ? budgetBytes : PotentialBudgetBytes(z.Size);

        var main = new Complex[]?[m];
        var lo   = new int[m];
        var seg  = nv > 0 ? new Complex[]?[m] : null;
        var alive = new List<int>();
        var isNew = new bool[m];
        long held = 0;                                          // bytes of the rows held, by capacity

        // Row arrays are pooled for the length of the fill: a dropped row's array serves the next row
        // formed, and every later row is no longer (its start only moves up). Without it every row was a
        // fresh large-object allocation and the dropped ones waited for a gen-2 collection, so the
        // process held them anyway.
        var freeRows = new Stack<Complex[]>();
        var freeSegs = new Stack<Complex[]>();
        Complex[] RentRow(int len)
        {
            while (freeRows.Count > 0) { var r = freeRows.Pop(); if (r.Length >= len) return r; }
            return new Complex[len];
        }
        long RowBytes(int start) => 16L * (m - start) + 16L * nv;
        long HeldBytes(int a) => 16L * main[a]!.Length + 16L * nv;
        void Drop(int a)
        {
            held -= HeldBytes(a);
            freeRows.Push(main[a]!); main[a] = null;
            if (seg is not null) { freeSegs.Push(seg[a]!); seg[a] = null; }
        }

        for (int i0 = 0; i0 < n;)
        {
            // ── drop what no later column reads ─────────────────────────────────────────────
            int loHere = i0 < nh ? plan.SuffixMin[i0] : m;
            for (int k = alive.Count - 1; k >= 0; k--)
            {
                int a = alive[k];
                if (plan.LastUse[a] < i0)
                {
                    Drop(a);
                    alive[k] = alive[^1]; alive.RemoveAt(alive.Count - 1);
                }
            }

            // ── past half the budget, evict the rows read latest; they are re-formed when read ─
            if (held > budget / 2)
            {
                alive.Sort((p, q) => plan.NextUse(q, i0).CompareTo(plan.NextUse(p, i0)));
                int k = 0;
                while (held > budget / 2 && k < alive.Count)
                {
                    int a = alive[k];
                    if (plan.NextUse(a, i0) == i0) break;              // read by the very next column
                    Drop(a);
                    k++;
                }
                alive.RemoveRange(0, k);
            }

            // ── this panel: columns while the rows they need, and do not hold, fit ──────────
            var fresh = new List<int>();
            long freshBytes = 0;
            int i1 = i0;
            while (i1 < n)
            {
                var (ha, hb) = halves[i1];
                long add = 0;
                int ca = ha.CellIndex, cb = hb.CellIndex;
                if (main[ca] is null && !isNew[ca]) add += RowBytes(loHere);
                if (cb != ca && main[cb] is null && !isNew[cb]) add += RowBytes(loHere);
                if (i1 > i0 && held + freshBytes + add > budget) break;
                if (main[ca] is null && !isNew[ca]) { isNew[ca] = true; fresh.Add(ca); }
                if (cb != ca && main[cb] is null && !isNew[cb]) { isNew[cb] = true; fresh.Add(cb); }
                freshBytes += add;
                i1++;
            }

            // ── form the fresh rows: the upper part (b ≥ a) first, then the lower part copied
            //    from a held row where there is one — the same P(min, max) either way ──────────
            foreach (int a in fresh)
            {
                lo[a] = loHere;
                main[a] = RentRow(m - loHere);
                if (seg is not null) seg[a] = freeSegs.Count > 0 ? freeSegs.Pop() : new Complex[nv];
            }
            var via = plan.Via;
            ForRows(st, fresh.Count, r =>
            {
                int a = fresh[r];
                var row = main[a]!;
                int l = lo[a];
                for (int b = Math.Max(a, l); b < m; b++) row[b - l] = pot.At(a, b);
                if (seg is not null)
                {
                    var s = seg[a]!;
                    for (int k = 0; k < nv; k++)
                    {
                        int v = via[k];
                        if (v < a) continue;
                        s[k] = v >= l ? row[v - l] : pot.At(a, v);
                    }
                }
            });
            ForRows(st, fresh.Count, r =>
            {
                int a = fresh[r];
                var row = main[a]!;
                int l = lo[a];
                for (int b = l; b < a && b < m; b++)
                {
                    var rb = main[b];
                    // Row b holds b's upper part whether it is fresh or carried, wherever it reaches a.
                    // (A row formed for the vertical tail alone starts at m and covers no index.)
                    row[b - l] = rb is not null && lo[b] <= a ? rb[a - lo[b]] : pot.At(b, a);
                }
                if (seg is not null)
                {
                    var s = seg[a]!;
                    for (int k = 0; k < nv; k++)
                    {
                        int v = via[k];
                        if (v >= a) break;
                        s[k] = v >= l ? row[v - l]
                             : main[v] is { } rv && lo[v] <= a ? rv[a - lo[v]]
                             : pot.At(v, a);
                    }
                }
            });
            foreach (int a in fresh) { isNew[a] = false; alive.Add(a); held += HeldBytes(a); }

            // ── the columns ─────────────────────────────────────────────────────────────────
            int c0 = i0, width = i1 - i0;
            ForRows(st, width, ii =>
            {
                int i = c0 + ii;
                var (ma, mb) = halves[i];
                var col = z.Column(i);                          // the contiguous triangle (P3)
                var ra = main[ma.CellIndex]!; int la = lo[ma.CellIndex];
                var rb = main[mb.CellIndex]!; int lb = lo[mb.CellIndex];
                int jh = Math.Min(nh, n);
                for (int j = i; j < jh; j++)
                {
                    var (na, nb) = halves[j];
                    Complex s = ma.Sign * na.Sign * ra[na.CellIndex - la]
                              + ma.Sign * nb.Sign * ra[nb.CellIndex - la]
                              + mb.Sign * na.Sign * rb[na.CellIndex - lb]
                              + mb.Sign * nb.Sign * rb[nb.CellIndex - lb];
                    col[j - i] = scalarScale * s;
                }
                if (seg is not null)
                {
                    var sa = seg[ma.CellIndex]!;
                    var sb = seg[mb.CellIndex]!;
                    var pos = plan.ViaPos;
                    for (int j = Math.Max(i, nh); j < n; j++)
                    {
                        var (na, nb) = halves[j];
                        Complex s = ma.Sign * na.Sign * sa[pos[na.CellIndex]]
                                  + ma.Sign * nb.Sign * sa[pos[nb.CellIndex]]
                                  + mb.Sign * na.Sign * sb[pos[na.CellIndex]]
                                  + mb.Sign * nb.Sign * sb[pos[nb.CellIndex]];
                        col[j - i] = scalarScale * s;
                    }
                }
            });
            i0 = i1;
        }
    }

    /// <summary>
    /// <b>Which basis columns read which cell's row</b>, and how far back a row has to reach. O(N + m),
    /// built once per fill.
    /// </summary>
    internal sealed class PotentialRowPlan
    {
        public readonly int M;
        /// <summary>The horizontal prefix's length: every basis from here on is vertical (R-via-5).
        /// The basis count when the tail is not all vertical, so no ordering is ever assumed.</summary>
        public readonly int HorizontalCount;
        /// <summary>Per horizontal column i (and one past the prefix, = M): the smallest cell any
        /// horizontal basis j ≥ i touches.</summary>
        public readonly int[] SuffixMin;
        /// <summary>The cells the vertical tail touches, ascending, and each cell's position in it (−1).</summary>
        public readonly int[] Via, ViaPos;
        /// <summary>Per cell: the last basis column that reads its row (−1 when none does).</summary>
        public readonly int[] LastUse;
        private readonly int[] _useStart, _uses;

        public PotentialRowPlan(PlanarMesh mesh, (RooftopHalf A, RooftopHalf B)[] halves)
        {
            int n = halves.Length;
            M = mesh.Cells.Count;
            int nh = n;
            while (nh > 0 && mesh.Bases[nh - 1].Direction == PlanarBasisDirection.Z) nh--;
            for (int i = 0; i < nh; i++)
                if (mesh.Bases[i].Direction == PlanarBasisDirection.Z) { nh = n; break; }
            HorizontalCount = nh;

            SuffixMin = new int[nh + 1];
            SuffixMin[nh] = M;
            for (int i = nh - 1; i >= 0; i--)
                SuffixMin[i] = Math.Min(SuffixMin[i + 1], Math.Min(halves[i].A.CellIndex, halves[i].B.CellIndex));

            ViaPos = new int[M];
            Array.Fill(ViaPos, -1);
            var via = new SortedSet<int>();
            for (int i = nh; i < n; i++) { via.Add(halves[i].A.CellIndex); via.Add(halves[i].B.CellIndex); }
            Via = via.ToArray();
            for (int k = 0; k < Via.Length; k++) ViaPos[Via[k]] = k;

            // Per cell, the ascending columns that read it (CSR).
            var count = new int[M + 1];
            for (int i = 0; i < n; i++)
            {
                count[halves[i].A.CellIndex + 1]++;
                if (halves[i].B.CellIndex != halves[i].A.CellIndex) count[halves[i].B.CellIndex + 1]++;
            }
            for (int c = 0; c < M; c++) count[c + 1] += count[c];
            _useStart = (int[])count.Clone();
            _uses = new int[count[M]];
            var fill = (int[])count.Clone();
            for (int i = 0; i < n; i++)
            {
                _uses[fill[halves[i].A.CellIndex]++] = i;
                if (halves[i].B.CellIndex != halves[i].A.CellIndex) _uses[fill[halves[i].B.CellIndex]++] = i;
            }
            LastUse = new int[M];
            for (int c = 0; c < M; c++) LastUse[c] = _useStart[c + 1] > _useStart[c] ? _uses[_useStart[c + 1] - 1] : -1;
        }

        /// <summary>The first column at or after <paramref name="i0"/> that reads cell <paramref name="c"/>'s row.</summary>
        public int NextUse(int c, int i0)
        {
            for (int k = _useStart[c]; k < _useStart[c + 1]; k++) if (_uses[k] >= i0) return _uses[k];
            return int.MaxValue;
        }
    }
}
