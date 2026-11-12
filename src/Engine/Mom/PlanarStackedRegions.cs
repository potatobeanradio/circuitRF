// PMS-3 — a via-joined stacked-metal region is one conductor.
//
// A via group whose footprint covers a large area where both of its levels carry metal shorts the two sheets together
// everywhere inside it. Meshed as two levels plus one vertical basis per cell, the interior spends three unknowns per
// cell on what one carries. This finds those regions on the shared grid, after every level's cells are classified and
// before any rooftop is built, and says which cells to drop and which to give the stack's parallel sheet.
//
// THE LEVEL KEPT IS THE LOWEST, NOT THE TOP (the brief said top; RESOLVED.md §PMS-3 records the measurements). The kept
// sheet is where the region's charge is put, and the charge of shorted sheets sits on the face toward what is below:
// for a MIM Via window — MIM Metal joined to Metal2 over the Metal1 bottom plate 0.2 µm below — keeping Metal2 would
// move the capacitor's plate to 3 µm and divide its capacitance by about fifteen, and on A1 (a region over the ground
// plane) keeping the top was 18× further from the full solve in |ΔS| than keeping the lowest. Keeping the level the
// outside metal ARRIVES on was measured too: it is what the DC resistance prefers (current arriving on a dropped level
// must change level inside the rim) and it is the wrong trade.
//
// ORDERING (R-msh-2, R-via-5): everything here is integer grid scans in (chain, IY, IX) order. A list search stands in
// for a dictionary wherever one would be iterated; nothing on the path iterates a set.

namespace CircuitRF.Engine.Mom;

/// <summary>PMS-3 — what <see cref="PlanarStackedRegions.Find"/> decided, in the mesher's own cell indices.</summary>
internal sealed class PlanarStackedPlan
{
    /// <summary>Per cell index (before compaction): true when the cell is a dropped level of a region's interior.</summary>
    public required bool[] DropCell { get; init; }
    /// <summary>Per cell index: the stack the kept interior cell stands for, or −1.</summary>
    public required int[] StackOfCell { get; init; }
    /// <summary>The level sets the kept cells name, in first-found order.</summary>
    public required List<PlanarMeshStack> Stacks { get; init; }
    /// <summary>Per via group: the chain it belongs to, or −1 (a ground attachment, or no chain).</summary>
    public required int[] ChainOfVia { get; init; }
    /// <summary>Per grid position (iy·nx + ix): the chain whose region interior this is, or −1.</summary>
    public required int[] InteriorChainAt { get; init; }

    public int Regions { get; init; }
    public double AreaM2 { get; init; }
    public int DroppedCells { get; init; }
    public int SavedRooftops { get; init; }
    public int SavedVertical { get; init; }
    public int WhollyInsidePieces { get; init; }
    public int PortProtectedRegions { get; init; }

    public bool Any => Regions > 0;
    public int SavedUnknowns => SavedRooftops + SavedVertical;
}

/// <summary>PMS-3 — finds the stacked regions of one mesh. See the file header.</summary>
internal static class PlanarStackedRegions
{
    /// <summary>How many cells beyond a qualifying region's bounding box a port may sit and still keep the region whole.
    /// A port resolves on its own level's cells and an internal port cuts a whole gridline across its conductor, so a
    /// region a port touches is left exactly as today rather than reasoned about.</summary>
    private const int PortGuardCells = 2;

    public static PlanarStackedPlan? Find(
        PlanarProblem problem, double[] gx, double[] gy, IReadOnlyList<PlanarCell> cells, int[][] cellAtPerLayer,
        ViaFootprintPlan footprints, int minCells, int rimCells, IReadOnlyList<PlanarPort>? ports,
        double minSideM = PlanarMeshSettings.DefaultStackedRegionMinSideM)
    {
        var vias = problem.ViaList;
        int nx = gx.Length - 1, ny = gy.Length - 1;
        if (vias.Count == 0 || nx <= 0 || ny <= 0) return null;

        // ── Chains: groups with one footprint whose gaps run consecutively (PlanarExtractor's AddSpan) ────────────
        var chainOfVia = new int[vias.Count];
        Array.Fill(chainOfVia, -1);
        var chainLo = new List<int>();
        var chainHi = new List<int>();
        var chainGroups = new List<int>();
        for (int vi = 0; vi < vias.Count; vi++)
        {
            if (vias[vi].ToGround || chainOfVia[vi] >= 0) continue;
            // Every other group with this footprint, by lower level ascending (ties in list order).
            var same = new List<int> { vi };
            for (int vj = vi + 1; vj < vias.Count; vj++)
                if (!vias[vj].ToGround && chainOfVia[vj] < 0 && SameFootprint(vias[vi].Polygons, vias[vj].Polygons))
                    same.Add(vj);
            same.Sort((a, b) => vias[a].LowerLayerIndex != vias[b].LowerLayerIndex
                ? vias[a].LowerLayerIndex.CompareTo(vias[b].LowerLayerIndex) : a.CompareTo(b));

            // Split into runs of consecutive gaps; a repeated gap rides along with the run it repeats.
            int k = 0;
            while (k < same.Count)
            {
                int c = chainLo.Count;
                int lo = vias[same[k]].LowerLayerIndex, hi = vias[same[k]].UpperLayerIndex, groups = 0;
                while (k < same.Count)
                {
                    var v = vias[same[k]];
                    if (v.LowerLayerIndex > hi || v.UpperLayerIndex <= lo) break;
                    if (v.LowerLayerIndex < hi && v.UpperLayerIndex > hi) break;      // not a clean continuation
                    hi = Math.Max(hi, v.UpperLayerIndex);
                    chainOfVia[same[k]] = c;
                    groups++;
                    k++;
                }
                chainLo.Add(lo); chainHi.Add(hi); chainGroups.Add(groups);
            }
        }
        int chains = chainLo.Count;
        if (chains == 0) return null;

        // ── Who covers each position: one chain, or anything else (another chain, a ground via) ──────────────────
        //
        // A position another via group also covers is left exactly as today. Dropping a level there would drop that
        // via's foot with it: on fixture S a backside via inside the region lost its ground attachment that way, with
        // nothing failing but a count (RESOLVED.md §PMS-3).
        var rowMark = new int[nx];
        int stamp = 0;
        var coveredBy = new int[nx * ny];
        Array.Fill(coveredBy, -1);
        for (int vi = 0; vi < vias.Count; vi++)
        {
            int owner = chainOfVia[vi] >= 0 ? chainOfVia[vi] : -2;
            ForEachCovered(vi, pos => coveredBy[pos] = coveredBy[pos] == -1 || coveredBy[pos] == owner ? owner : -2);
        }

        // ── Candidate positions: covered by its chain alone, whole non-film metal on every level of the chain, and no
        //    metal on any level above it (the kept sheet may be a lower one) ──────────────────────────────────────────
        var candidate = new int[nx * ny];
        Array.Fill(candidate, -1);
        int levels = problem.Layers.Count;
        for (int pos = 0; pos < nx * ny; pos++)
        {
            int c = coveredBy[pos];
            if (c < 0) continue;
            if (!WholeMetalOnEvery(cells, cellAtPerLayer, pos, chainLo[c], chainHi[c])) continue;
            bool above = false;
            for (int l = chainHi[c] + 1; l < levels && !above; l++) above = cellAtPerLayer[l][pos] >= 0;
            if (!above) candidate[pos] = c;
        }

        void ForEachCovered(int vi, Action<int> each)
        {
            var via = vias[vi];
            var plan = footprints.Vias[vi];
            for (int iy = plan.Y0; iy <= plan.Y1; iy++)
            {
                double yc = 0.5 * (gy[iy] + gy[iy + 1]);
                int s = ++stamp;
                int rx0 = int.MaxValue, rx1 = -1;
                foreach (var box in plan.Boxes)
                {
                    if (iy < box.Y0 || iy > box.Y1) continue;
                    var poly = via.Polygons[box.Polygon];
                    for (int ix = box.X0; ix <= box.X1; ix++)
                    {
                        if (rowMark[ix] == s) continue;
                        if (!poly.Contains(0.5 * (gx[ix] + gx[ix + 1]), yc)) continue;
                        rowMark[ix] = s;
                        if (ix < rx0) rx0 = ix;
                        if (ix > rx1) rx1 = ix;
                    }
                }
                for (int ix = rx0; ix <= rx1; ix++)
                    if (rowMark[ix] == s) each(iy * nx + ix);
            }
        }

        // Port positions, as grid indices.
        var portCells = new List<(int IX, int IY)>();
        if (ports is not null)
            foreach (var p in ports)
                portCells.Add((IndexOf(gx, p.Location.X), IndexOf(gy, p.Location.Y)));

        // ── Components (4-connected), qualification, and the interior by erosion ────────────────────────────────
        var drop = new bool[cells.Count];
        var stackOf = new int[cells.Count];
        Array.Fill(stackOf, -1);
        var stacks = new List<PlanarMeshStack>();
        var interiorAt = new int[nx * ny];
        Array.Fill(interiorAt, -1);
        var compAt = new int[nx * ny];
        Array.Fill(compAt, -1);
        var inner = new bool[nx * ny];
        var queue = new List<int>();
        var remove = new List<int>();
        int comp = 0, regions = 0, portProtected = 0, dropped = 0, savedVertical = 0;
        double area = 0;

        for (int start = 0; start < nx * ny; start++)
        {
            int c = candidate[start];
            if (c < 0 || compAt[start] >= 0) continue;

            queue.Clear();
            queue.Add(start);
            compAt[start] = comp;
            int ix0 = int.MaxValue, ix1 = -1, iy0 = int.MaxValue, iy1 = -1;
            for (int q = 0; q < queue.Count; q++)
            {
                int pos = queue[q], iy = pos / nx, ix = pos - iy * nx;
                ix0 = Math.Min(ix0, ix); ix1 = Math.Max(ix1, ix); iy0 = Math.Min(iy0, iy); iy1 = Math.Max(iy1, iy);
                if (ix > 0)      Visit(pos - 1);
                if (ix + 1 < nx) Visit(pos + 1);
                if (iy > 0)      Visit(pos - nx);
                if (iy + 1 < ny) Visit(pos + nx);
            }
            int thisComp = comp++;

            if (ix1 - ix0 + 1 < minCells || iy1 - iy0 + 1 < minCells) continue;
            // PMS-7 — and a physical floor beside the cell count: a cell count measures the mesh, so on a fine enough
            // grid an ordinary post would reach it. Both must hold; the floor can only keep a region whole.
            if (gx[ix1 + 1] - gx[ix0] < minSideM || gy[iy1 + 1] - gy[iy0] < minSideM) continue;
            bool portNear = false;
            foreach (var (pix, piy) in portCells)
                if (pix >= ix0 - PortGuardCells && pix <= ix1 + PortGuardCells &&
                    piy >= iy0 - PortGuardCells && piy <= iy1 + PortGuardCells) { portNear = true; break; }
            if (portNear) { portProtected++; continue; }

            // Erode rimCells times over the 8-neighbourhood: a position stays interior only while every neighbour is
            // still an interior member, so the interior never shares an edge or a corner with anything outside.
            foreach (int pos in queue) inner[pos] = true;
            for (int r = 0; r < rimCells; r++)
            {
                remove.Clear();
                foreach (int pos in queue)
                {
                    if (!inner[pos]) continue;
                    int iy = pos / nx, ix = pos - iy * nx;
                    bool keep = true;
                    for (int dy = -1; dy <= 1 && keep; dy++)
                        for (int dx = -1; dx <= 1 && keep; dx++)
                        {
                            int jx = ix + dx, jy = iy + dy;
                            if (jx < 0 || jy < 0 || jx >= nx || jy >= ny) { keep = false; break; }
                            int j = jy * nx + jx;
                            keep = compAt[j] == thisComp && inner[j];
                        }
                    if (!keep) remove.Add(pos);
                }
                foreach (int pos in remove) inner[pos] = false;
            }

            int interior = 0;
            foreach (int pos in queue) if (inner[pos]) interior++;
            if (interior == 0) continue;     // erosion already cleared every flag

            int lo = chainLo[c], hi = chainHi[c];
            int kept = lo;
            int stack = stacks.FindIndex(st => st.LowerLayerIndex == lo && st.UpperLayerIndex == hi && st.KeptLayerIndex == kept);
            if (stack < 0) { stack = stacks.Count; stacks.Add(new PlanarMeshStack(lo, hi, kept)); }

            regions++;
            foreach (int pos in queue)
            {
                int iy = pos / nx, ix = pos - iy * nx;
                area += (gx[ix + 1] - gx[ix]) * (gy[iy + 1] - gy[iy]);
                if (!inner[pos]) continue;
                interiorAt[pos] = c;
                stackOf[cellAtPerLayer[kept][pos]] = stack;
                for (int l = lo; l <= hi; l++) if (l != kept) { drop[cellAtPerLayer[l][pos]] = true; dropped++; }
            }
            savedVertical += interior * chainGroups[c];
            foreach (int pos in queue) inner[pos] = false;

            void Visit(int j)
            {
                if (candidate[j] != c || compAt[j] >= 0) return;
                compAt[j] = comp;
                queue.Add(j);
            }
        }

        // ── What it saves: the rooftops the dropped cells would have carried, each pair counted once ────────────
        int savedRooftops = 0, whollyInside = 0;
        if (dropped > 0)
        {
            var visited = new bool[cells.Count];
            var stack = new List<int>();
            for (int l = 0; l < levels; l++)
            {
                var at = cellAtPerLayer[l];
                for (int pos = 0; pos < nx * ny; pos++)
                {
                    int a = at[pos];
                    if (a < 0 || !drop[a]) continue;
                    int iy = pos / nx, ix = pos - iy * nx;
                    // Right and up: any existing neighbour. Left and down: only a kept one (a dropped one counted it).
                    if (ix + 1 < nx && at[pos + 1] >= 0) savedRooftops++;
                    if (iy + 1 < ny && at[pos + nx] >= 0) savedRooftops++;
                    if (ix > 0 && at[pos - 1] >= 0 && !drop[at[pos - 1]]) savedRooftops++;
                    if (iy > 0 && at[pos - nx] >= 0 && !drop[at[pos - nx]]) savedRooftops++;

                    // A piece of metal on this level that lies wholly inside an interior goes with it.
                    if (visited[a]) continue;
                    bool all = true;
                    stack.Clear();
                    stack.Add(pos);
                    visited[a] = true;
                    while (stack.Count > 0)
                    {
                        int p = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        if (!drop[at[p]]) all = false;
                        int py = p / nx, px = p - py * nx;
                        if (px > 0)      Push(p - 1);
                        if (px + 1 < nx) Push(p + 1);
                        if (py > 0)      Push(p - nx);
                        if (py + 1 < ny) Push(p + nx);
                    }
                    if (all) whollyInside++;

                    void Push(int j)
                    {
                        int cj = at[j];
                        if (cj < 0 || visited[cj]) return;
                        visited[cj] = true;
                        stack.Add(j);
                    }
                }
            }
            // The region's own upper plates never count: their rim is kept.
        }

        return new PlanarStackedPlan
        {
            DropCell = drop, StackOfCell = stackOf, Stacks = stacks, ChainOfVia = chainOfVia,
            InteriorChainAt = interiorAt, Regions = regions, AreaM2 = area, DroppedCells = dropped,
            SavedRooftops = savedRooftops, SavedVertical = savedVertical, WhollyInsidePieces = whollyInside,
            PortProtectedRegions = portProtected,
        };
    }

    private static bool WholeMetalOnEvery(IReadOnlyList<PlanarCell> cells, int[][] cellAt, int pos, int lo, int hi)
    {
        for (int l = lo; l <= hi; l++)
        {
            int ci = cellAt[l][pos];
            if (ci < 0) return false;
            var cell = cells[ci];
            if (cell.Region is not null || cell.Film >= 0) return false;
        }
        return true;
    }

    /// <summary>The cell index along one axis whose span holds <paramref name="v"/>, clamped to the grid.</summary>
    private static int IndexOf(double[] lines, double v)
    {
        int i = Array.BinarySearch(lines, v);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, lines.Length - 2);
    }

    private static bool SameFootprint(IReadOnlyList<PlanarPolygon> a, IReadOnlyList<PlanarPolygon> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (ReferenceEquals(a[i], b[i])) continue;
            if (!SameRing(a[i].Outer, b[i].Outer)) return false;
            var ha = a[i].HoleRings; var hb = b[i].HoleRings;
            if (ha.Count != hb.Count) return false;
            for (int h = 0; h < ha.Count; h++) if (!SameRing(ha[h], hb[h])) return false;
        }
        return true;
    }

    private static bool SameRing(IReadOnlyList<EmPoint> a, IReadOnlyList<EmPoint> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].X != b[i].X || a[i].Y != b[i].Y) return false;
        return true;
    }
}
