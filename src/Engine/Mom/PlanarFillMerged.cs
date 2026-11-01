// PM-2 Part 2 — the merged system Tᵀ Z T, filled in PANELS of the elemental matrix.
//
// The elemental Z is never held: at 15,236 elemental rooftops it is 3.7 GB, and the merged system it
// reduces to is a quarter of that. So the lower triangle of Z is filled a band of COLUMNS at a time,
// and each band is folded straight into the merged matrix:
//
//   Z = U + Uᵀ with U = L + D/2 (L the strict lower triangle, D the diagonal)
//   ⇒ Tᵀ Z T = X + Xᵀ with X = Tᵀ U T = Σ_panels (Tᵀ U[:, P]) · T[P, :]
//
// so only the triangle Fill already computes is computed, once. Halving the diagonal is exact in
// binary floating point, and so is every step of the product when T is the identity — which is why a
// mesh where nothing merges reproduces Fill's own matrix to the bit (gated).
//
// EVERY ELEMENTAL ENTRY IS FILL'S OWN EXPRESSION, IN FILL'S OWN ORDER: the scalar term assigned,
// the same-direction vector term added, the conductor's sheet term added. The cell-pair potential P
// is formed per panel, as rows for the cells the panel's rooftops touch, rather than as the m×m matrix
// Fill builds — the same class lookups, asked of (min, max) exactly as Fill's mirror reads them.
// What a panel costs over Fill is that a cell's row is formed for each panel that touches it (about
// twice) and in full rather than as a triangle — measured in RESOLVED.md §PM-2 Part 2.
//
// CAP-2 (2026-10-31): THE MERGED MATRIX IS HELD AS ITS LOWER TRIANGLE, and that cost exactly one thing.
// X = TᵀUT is not symmetric — only X + Xᵀ is — so holding X in full and folding it at the end needs
// the square this brief exists to remove. Instead each panel's product folds straight into the
// triangle: destination column c (owned by one iteration, R-fil-11's shape) takes this panel's X[a, c]
// for a ≥ c AND its X[c, a] for a > c. The diagonal takes only X[c, c] and is doubled at the end as
// before, so it is bit-identical; an OFF-diagonal entry now accumulates the two halves interleaved
// panel by panel where it used to sum each half over every panel and then add them. That is a change
// of summation order, at round-off (the gate is |ΔS| ≤ 1e-12 on this path, RESOLVED.md §CAP-2), and it
// leaves T = I bit-identical to Fill, since there every off-diagonal X[c, a] is an exact zero.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

public static partial class PlanarFill
{
    /// <summary>Bytes the panel workspace may hold — the elemental panel, its restriction and the
    /// potential rows. Small next to the merged matrix it feeds; it sets only how many panels there
    /// are, never an answer.</summary>
    public const long MergedPanelBudgetBytes = 64L << 20;

    /// <summary>Columns per panel for a mesh of <paramref name="n"/> elemental rooftops, <paramref name="nm"/>
    /// merged unknowns and <paramref name="m"/> cells.</summary>
    public static int MergedPanelWidth(int n, int nm, int m)
    {
        long per = 16L * (n + nm + 2L * m);
        return (int)Math.Clamp(MergedPanelBudgetBytes / Math.Max(1, per), 16, 512);
    }

    /// <summary>
    /// <b>PM-2 — the merged Galerkin matrix <c>Tᵀ Z T</c> at one angular frequency</b>, for a mesh
    /// carrying <see cref="PlanarMesh.Merge"/>. Full (both triangles), symmetric bit for bit, and of
    /// size <see cref="PlanarRooftopMerge.Count"/>. Single-level only, like <see cref="Fill"/>.
    /// CAP-2: <see cref="FillMergedLower"/>'s triangle, expanded — for small-N callers.
    /// </summary>
    /// <param name="panelWidth">Columns of the elemental triangle per panel; 0 sizes it from
    /// <see cref="MergedPanelBudgetBytes"/>. Changes no answer.</param>
    public static Mat<Complex> FillMerged(PlanarFillCores cores, PlanarKernelTerms termsA,
                                          PlanarKernelTerms termsQ, double omega, int panelWidth = 0)
        => FillMergedLower(cores, termsA, termsQ, omega, panelWidth).ToDense();

    /// <summary>
    /// <b>CAP-2 — <see cref="FillMerged"/>'s lower triangle</b>, folded straight into a
    /// <see cref="BlockLowerMatrix"/> panel by panel; no square is ever held. See this file's header
    /// for the one summation order this changed.
    /// </summary>
    public static BlockLowerMatrix FillMergedLower(PlanarFillCores cores, PlanarKernelTerms termsA,
                                                   PlanarKernelTerms termsQ, double omega, int panelWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(cores);
        ArgumentNullException.ThrowIfNull(termsA);
        ArgumentNullException.ThrowIfNull(termsQ);
        RequirePairCores(cores);
        RequireLayout(cores, PlanarCoreLayout.Classes, nameof(FillMerged));
        var mesh  = cores.Mesh;
        var merge = mesh.Merge ?? throw new InvalidOperationException(
            "FillMerged needs a mesh carrying a rooftop merge (PlanarMesh.Merge); this one has none. " +
            "Use Fill for the elemental system.");
        int n = mesh.Bases.Count, nm = merge.Count, m = mesh.Cells.Count;
        GuardCeiling(nm, m, cores.Settings.MemoryLimitBytes);
        var st = cores.Settings;
        var bases = mesh.Bases;

        // ── Fill's own per-frequency tables ─────────────────────────────────────────────────────
        var termsQr  = termsQ.With(st.Order, cores.RhoFloorM);
        var remQ     = Remainder(termsQr, cores);
        var classRemQ = ClassPulseRemainders(cores, remQ);
        Complex scalarScale = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);

        Complex vectorScale = Complex.ImaginaryOne * omega * EmConstants.Mu0;
        var termsAr  = termsA.With(st.Order, cores.RhoFloorM);
        var remA     = Remainder(termsAr, cores);
        var classRem = ClassRemainders(cores, remA);
        st.Counters?.AddRemainderPasses(classRem.Length);
        var src = new BandSource(cores, classRem);

        var band   = cores.BandClass!;
        var cutS   = cores.CutScalar;
        int kernels = cores.Kernels, stride = cores.ClassStride;
        var table  = cores.ClassCores!;
        var topo   = cores.Topology;
        var memo   = cores.Memoised;
        var mom    = cores.VMoment;
        var dirPos = cores.DirPos;

        var halves = new (RooftopHalf A, RooftopHalf B)[n];
        for (int i = 0; i < n; i++) halves[i] = PlanarBasisFunctions.Halves(mesh, bases[i]);

        PlanarGram? gram = null;
        Complex[]?  zs   = null;
        if (st.ConductorLoss is { } loss) { gram = cores.Gram; zs = loss.SheetTable(mesh, omega); }

        // ScalarPotentialMatrix's class path, for the pair (a ≤ b) its mirror reads.
        Complex PEntry(int a, int b)
        {
            int c = band[cores.BandIndex(a, b)];
            Complex v;
            if (c >= 0)
            {
                long o = (long)(c >> 1) * stride;
                v = termsQr.Inverse * table[o] + termsQr.Log * table[o + 1];
                if (termsQr.ExtractsConstant) v += termsQr.Constant;
                if (termsQr.ExtractsLinear && kernels > 2) v += termsQr.Linear * table[o + 2];
                v += classRemQ[c >> 1];
            }
            else
            {
                var t = cutS!.Get(a, b);
                v = termsQr.Inverse * t.Inverse + termsQr.Log * t.Log;
                if (termsQr.ExtractsConstant) v += termsQr.Constant;
                if (termsQr.ExtractsLinear) v += termsQr.Linear * t.Radius;
                v += PairRemainder(mesh, Pulse(mesh, a), Pulse(mesh, b), PlanarBasisDirection.X, remQ, st);
            }
            return v;
        }

        // AddDirectionBlockFromClasses' entry, for the same-direction pair (bi ≤ bj).
        Complex VectorEntry(int bi, int bj)
        {
            bool alongX = bases[bi].Direction == PlanarBasisDirection.X;
            if (memo[bi] && memo[bj])
                return WholeVectorEntry(src, mesh, topo.Halves[bi], topo.Halves[bj], alongX, termsAr,
                                        mom[bi], mom[bj]);
            var (ma, mb) = topo.Halves[bi];
            var (na, nb) = topo.Halves[bj];
            var dir = bases[bi].Direction;
            var t = (alongX ? cores.CutX : cores.CutY)!.Get(dirPos[bi], dirPos[bj]);
            Complex v = termsAr.Inverse * t.Inverse + termsAr.Log * t.Log;
            if (termsAr.ExtractsConstant) v += termsAr.Constant * (mom[bi] * mom[bj]);
            if (termsAr.ExtractsLinear)   v += termsAr.Linear   * t.Radius;
            Complex r = PairRemainder(mesh, ma, na, dir, remA, st)
                      + PairRemainder(mesh, ma, nb, dir, remA, st)
                      + PairRemainder(mesh, mb, na, dir, remA, st)
                      + PairRemainder(mesh, mb, nb, dir, remA, st);
            st.Counters?.AddRemainderPasses(4);
            return v + r;
        }

        int w = panelWidth > 0 ? panelWidth : MergedPanelWidth(n, nm, m);
        w = Math.Min(w, Math.Max(1, n));
        var x = new BlockLowerMatrix(nm);
        var u = new Mat<Complex>(n, w);
        var v = new Mat<Complex>(nm, w);
        var slot = new int[m];
        Array.Fill(slot, -1);
        var colPtr = merge.ColPtr; var colRow = merge.ColRow; var colVal = merge.ColVal;
        var rowPtr = merge.RowPtr; var rowCol = merge.RowCol; var rowVal = merge.RowVal;
        // Per macro column, the first T entry not yet folded: T's columns are sorted by elemental
        // row, and panels advance in row order, so each column's entries are consumed in order.
        var cursor = new int[nm];
        Array.Copy(colPtr, cursor, nm);

        for (int c0 = 0; c0 < n; c0 += w)
        {
            int c1 = Math.Min(n, c0 + w), width = c1 - c0;

            // The potential rows this panel's rooftops read.
            var cellList = new List<int>();
            for (int i = c0; i < c1; i++)
            {
                int a = halves[i].A.CellIndex, b = halves[i].B.CellIndex;
                if (slot[a] < 0) { slot[a] = cellList.Count; cellList.Add(a); }
                if (slot[b] < 0) { slot[b] = cellList.Count; cellList.Add(b); }
            }
            var prow = new Complex[(long)cellList.Count * m];
            ForRows(st, cellList.Count, r =>
            {
                int a = cellList[r];
                long o = (long)r * m;
                for (int b = 0; b < m; b++) prow[o + b] = a <= b ? PEntry(a, b) : PEntry(b, a);
            });

            u.Memory.Span.Clear();
            ForRows(st, width, ii =>
            {
                int i = c0 + ii;
                var col = u.Memory.Span.Slice(ii * u.Stride, n);
                var (ma, mb) = halves[i];
                long pa = (long)slot[ma.CellIndex] * m, pb = (long)slot[mb.CellIndex] * m;
                var dirI = bases[i].Direction;
                for (int j = i; j < n; j++)
                {
                    var (na, nb) = halves[j];
                    Complex s = ma.Sign * na.Sign * prow[pa + na.CellIndex]
                              + ma.Sign * nb.Sign * prow[pa + nb.CellIndex]
                              + mb.Sign * na.Sign * prow[pb + na.CellIndex]
                              + mb.Sign * nb.Sign * prow[pb + nb.CellIndex];
                    Complex z = scalarScale * s;
                    if (dirI != PlanarBasisDirection.Z && bases[j].Direction == dirI)
                        z += vectorScale * VectorEntry(i, j);
                    col[j] = z;
                }
                if (zs is not null)
                    for (int k = gram!.RowPtr[i]; k < gram.RowPtr[i + 1]; k++)
                        col[gram.ColIdx[k]] += SheetTerm(zs, gram, k);
                col[i] *= 0.5;                                  // U = L + D/2
            });
            foreach (int a in cellList) slot[a] = -1;

            // V = Tᵀ U[:, P]. A macro whose last elemental rooftop is above the column's diagonal
            // reads only the zero upper part of U.
            ForRows(st, width, ii =>
            {
                int i = c0 + ii;
                var uc = u.Memory.Span.Slice(ii * u.Stride, n);
                var vc = v.Memory.Span.Slice(ii * v.Stride, nm);
                for (int a = 0; a < nm; a++)
                {
                    int k0 = colPtr[a], k1 = colPtr[a + 1];
                    if (k1 == k0 || colRow[k1 - 1] < i) { vc[a] = Complex.Zero; continue; }
                    Complex sum = Complex.Zero;
                    for (int k = k0; k < k1; k++) sum += colVal[k] * uc[colRow[k]];
                    vc[a] = sum;
                }
            });

            // This panel's (a, ii, weight) for every T entry of its rows, by macro a: the transposed
            // half's sources, so a destination column c reads exactly its a > c tail.
            int nnzP = rowPtr[c1] - rowPtr[c0];
            var tri = new (int A, int Ii, double W)[nnzP];
            for (int i = c0, o = 0; i < c1; i++)
                for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++) tri[o++] = (rowCol[k], i - c0, rowVal[k]);
            Array.Sort(tri, (p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.Ii.CompareTo(q.Ii));
            var triA = new int[nnzP];
            for (int t = 0; t < nnzP; t++) triA[t] = tri[t].A;

            // X += V · T[P, :], folded into the triangle: destination column c is written by ONE
            // iteration — X[a, c] for a ≥ c (column c of the product), then X[c, a] for a > c (row c).
            int panelEnd = c1;
            ForRows(st, nm, c =>
            {
                var xc = x.Column(c);
                int len = nm - c;
                var vs = v.Memory.Span;
                int k = cursor[c], kEnd = colPtr[c + 1];
                for (; k < kEnd && colRow[k] < panelEnd; k++)
                {
                    double wt = colVal[k];
                    var vc = vs.Slice((colRow[k] - c0) * v.Stride + c, len);
                    for (int a = 0; a < len; a++) xc[a] += wt * vc[a];
                }
                cursor[c] = k;

                int t0 = Array.BinarySearch(triA, c + 1);
                if (t0 < 0) t0 = ~t0;
                else while (t0 > 0 && triA[t0 - 1] == c + 1) t0--;
                if (t0 == nnzP) return;

                // Row c of V, gathered once at a constant stride (which prefetches) rather than read
                // in the triples' order (which would not).
                Span<Complex> vr = width <= 512 ? stackalloc Complex[width] : new Complex[width];
                for (int ii = 0; ii < width; ii++) vr[ii] = vs[ii * v.Stride + c];
                for (int t = t0; t < nnzP; t++)
                {
                    var (a, ii, wt) = tri[t];
                    xc[a - c] += wt * vr[ii];
                }
            });
        }

        // Tᵀ Z T = X + Xᵀ: the off-diagonal halves were folded together above; the diagonal is
        // X[j, j] alone and is doubled here, exactly as before.
        for (int j = 0; j < nm; j++) x.Column(j)[0] *= 2.0;
        return x;
    }
}
