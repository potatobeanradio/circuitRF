// PMS-5 — the fill on a locally refined grid (brief-pms-5-local-grid-fill.md).
//
// One test per claim, on a fixture small enough for the routine tier: a two-level block whose Metal1 plate the local
// grid coarsens while a post, a backside via and two narrow Metal2 feeds keep their own lines. The accuracy solves
// against the tensor grid on A1 and A2 (gate 2) and the cost on fixture S (gate 5) were taken with a scratch harness in
// Release and are in src/Engine/Mom/RESOLVED.md §PMS-5.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class Pms5LocalGridFillTests(ITestOutputHelper output)
{
    private static PlanarPolygon R(double x0, double y0, double x1, double y1)
        => PlanarLineFixtures.Rect(x0 * 1e-6, y0 * 1e-6, x1 * 1e-6, y1 * 1e-6);

    /// <summary>A 40 × 30 µm Metal1 plate grounded by a 2 µm backside via, a 4 µm Metal2 line joined to it by a 2 µm post,
    /// and a separate Metal2 stub — on the synthetic chip's two-level GaAs medium. The local grid coarsens the plate and
    /// keeps the post, the via and the two feeds at their own lines.</summary>
    private static PlanarProblem StepBlock()
    {
        var stack = SyntheticMmicChip.Stack;
        return new PlanarProblem(
            [new PlanarConductorLayer("M1", [R(0, 0, 40, 30)], 4.1e7, 2e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("M2", [R(20, 13, 70, 17), R(26, 11, 34, 19), R(0, 13, 10, 17)], 4.1e7, 3e-6,
                                      stack.TopZ)],
            GroundedSlab.GaAsStarter, 10e9, null, stack,
            [new PlanarVia(0, 1, [R(29, 14, 31, 16)], 4.1e7),
             new PlanarVia(PlanarVia.GroundTerminal, 0, [R(5, 5, 7, 7)], 4.1e7)]);
    }

    private static List<PlanarPort> GapPorts() =>
    [
        new(1, new EmPoint(60e-6, 15e-6), PlanarPortSide.MinX, 50, 1) { Kind = PlanarPortKind.InternalDeltaGap },
        new(2, new EmPoint(5e-6, 15e-6), PlanarPortSide.MinX, 50, 1) { Kind = PlanarPortKind.InternalDeltaGap },
    ];

    private static readonly PlanarMeshSettings Coarse =
        PlanarMeshSettings.Default with { MinCellsAcrossConductor = 2, EdgeMesh = false };

    private static PlanarMesh Mesh(PlanarMeshSettings s)
        => SurfaceMesher.Mesh(StepBlock(), s, PlanarEdgeReference.LocalConductorWidth, ports: GapPorts()).Mesh;

    /// <summary>One kernel set for the class, so its fits are taken once (PlanarKernelSet.For shares them).</summary>
    private static readonly Lazy<PlanarKernelSet> Kernels =
        new(() => new PlanarKernelSet(new LayeredSpectralGreens(StepBlock().EffectiveStack, 10e9)));

    private static Mat<Complex> FillAt(PlanarMesh mesh)
    {
        var cores = PlanarFill.BuildCores(mesh);
        return PlanarFill.FillMultiLevel(cores, Kernels.Value.For(cores), PlanarLevels.From(StepBlock()),
                                         2 * Math.PI * 10e9);
    }

    /// <summary>Gate 1 — with every block forced to 1 × 1 the local grid IS the tensor grid, so there is no support mesh
    /// and the multi-level matrix (vias and a ground attachment included) is today's entry for entry.</summary>
    [Fact]
    public void ForcedElementary_FillsAsTheTensorGrid()
    {
        var off = Mesh(Coarse);
        var forced = Mesh(Coarse with { LocalGrid = true, LocalGridForceElementary = true });
        Assert.False(forced.HasBlocks);
        Assert.Same(forced, PlanarLocalGridFill.SupportMesh(forced));

        var a = FillAt(off);
        var b = FillAt(forced);
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++)
                Assert.Equal(a[i, j], b[i, j]);
    }

    /// <summary>R-pms5-1 — the support mesh holds each distinct support rectangle once, and every basis names the two
    /// D-pms4-4 supports of its source basis, in the source's order: a step rooftop's coarse half is its strip.</summary>
    [Fact]
    public void TheSupportMesh_IsTheDistinctSupports()
    {
        var mesh = Mesh(Coarse with { LocalGrid = true });
        Assert.True(mesh.HasBlocks);
        var sup = PlanarLocalGridFill.SupportMesh(mesh);
        Assert.Same(mesh, sup.BlockSource);
        Assert.Same(sup, PlanarLocalGridFill.SupportMesh(mesh));
        Assert.Equal(mesh.Bases.Count, sup.Bases.Count);

        var rects = sup.Cells.Select(c => (c.LayerIndex, c.IX, c.IX1, c.IY, c.IY1)).ToList();
        Assert.Equal(rects.Count, rects.Distinct().Count());
        int strips = 0;
        for (int i = 0; i < mesh.Bases.Count; i++)
        {
            var b = mesh.Bases[i];
            var s = sup.Bases[i];
            Assert.Equal(b.Direction, s.Direction);
            Assert.False(s.IsSegment);
            if (b.Direction == PlanarBasisDirection.Z) continue;
            var (ha, hb) = PlanarLocalGrid.Supports(mesh, b);
            foreach (var (h, c) in new[] { (ha, sup.Cells[s.CellA]), (hb, sup.Cells[s.CellB]) })
            {
                Assert.Equal((h.XMin, h.YMin, h.XMax, h.YMax), (c.XMin, c.YMin, c.XMax, c.YMax));
                Assert.Equal(mesh.Cells[h.CellIndex].LayerIndex, c.LayerIndex);
                if (h.Area < mesh.Cells[h.CellIndex].Area) strips++;
            }
        }
        output.WriteLine($"{mesh.Cells.Count} cells, {sup.Cells.Count} supports, {strips} strip halves");
        Assert.True(strips > 0);
    }

    /// <summary>R-pms5-3 — a pair with a block or a strip in it is classed by its quantised edge positions, and the class
    /// table serves every band pair with the integral the per-pair reference takes on the pair itself; the core count is
    /// the class count of the support mesh.</summary>
    [Fact]
    public void TheClassTable_IsThePerPairIntegral()
    {
        var mesh = Mesh(Coarse with { LocalGrid = true });
        var cores = PlanarFill.BuildCores(mesh);
        var reference = PlanarFill.BuildCoresByPairs(mesh);
        Assert.Same(PlanarLocalGridFill.SupportMesh(mesh), cores.Mesh);
        Assert.True(cores.ClassCount < cores.BandPairs);
        Assert.Equal(cores.ClassCount, PlanarCoreFootprint.Count(mesh).Classes);

        double worst = 0;
        for (int a = 0; a < cores.CellCount; a++)
            for (int b = a; b < cores.CellCount; b++)
            {
                var x = cores.ScalarCoreOf(a, b);
                var y = reference.ScalarCoreOf(a, b);
                worst = Math.Max(worst, Math.Abs(x.Inverse - y.Inverse) / Math.Abs(y.Inverse));
                worst = Math.Max(worst, Math.Abs(x.Log - y.Log) / Math.Abs(y.Log));
            }
        foreach (var dir in new[] { PlanarBasisDirection.X, PlanarBasisDirection.Y })
        {
            int k = dir == PlanarBasisDirection.X ? cores.XBases.Length : cores.YBases.Length;
            for (int i = 0; i < k; i++)
                for (int j = i; j < k; j++)
                {
                    var x = cores.VectorCoreOf(dir, i, j);
                    var y = reference.VectorCoreOf(dir, i, j);
                    worst = Math.Max(worst, Math.Abs(x.Inverse - y.Inverse) / Math.Abs(y.Inverse));
                }
        }
        output.WriteLine($"{cores.ClassCount:N0} classes for {cores.BandPairs:N0} band pairs; worst relative {worst:E2}");
        Assert.True(worst < 1e-9, $"class table vs per pair {worst:E2}");
    }

    /// <summary>Gate 3 — Z is complex-symmetric to the bit, and every basis's divergence pulses integrate to zero over
    /// its supports (−1 for a ground attachment) — on the support mesh the fill actually reads.</summary>
    [Fact]
    public void Z_IsSymmetric_AndEveryBasisIsChargeNeutral()
    {
        var mesh = Mesh(Coarse with { LocalGrid = true });
        var sup = PlanarLocalGridFill.SupportMesh(mesh);
        foreach (var b in sup.Bases)
            Assert.Equal(b.AttachesToGround ? -1.0 : 0.0, PlanarBasisFunctions.NetCharge(sup, b));
        foreach (var b in mesh.Bases)
        {
            if (b.Direction == PlanarBasisDirection.Z && !b.AttachesToGround) continue;
            var (sa, sb) = PlanarLocalGrid.Supports(mesh, b);
            Assert.Equal(b.AttachesToGround ? -1.0 : 0.0, sa.Pulse * sa.Area + sb.Pulse * sb.Area, 1e-12);
        }

        var z = FillAt(mesh);
        Assert.Contains(sup.Bases, b => b.AttachesToGround);
        for (int i = 0; i < z.RowCount; i++)
            for (int j = 0; j < i; j++)
                Assert.Equal(z[i, j], z[j, i]);
    }

    /// <summary>
    /// Gate 4 / R-pms5-2 — the self and near rules hold the fill's 5e-6 on the worst strip aspect fixture S produces: the
    /// strip on itself, the strip against the block it lies in (an overlapping pair, cut at its edges), and the strip
    /// against its sibling across the block — pulse and ramp primitives, 1/R and ln r, against the independent
    /// correlation oracle.
    /// </summary>
    [Fact]
    public void OnTheWorstStrip_TheRulesHoldTheFillsGate()
    {
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600));
        var ports = chip.Ports();
        var problem = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        var mesh = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default with { LocalGrid = true },
                                      PlanarEdgeReference.LocalConductorWidth, ports: ports).Mesh;
        var sup = PlanarLocalGridFill.SupportMesh(mesh);

        // The worst strip: a support thinner than its block, at the largest aspect ratio.
        int worst = -1; double aspect = 0;
        foreach (var b in mesh.Bases)
        {
            if (!b.IsSegment) continue;
            foreach (var s in new[] { PlanarLocalGrid.Supports(mesh, b).A, PlanarLocalGrid.Supports(mesh, b).B })
            {
                double w = s.XMax - s.XMin, h = s.YMax - s.YMin;
                if (s.Area >= mesh.Cells[s.CellIndex].Area) continue;
                double r = Math.Max(w / h, h / w);
                if (r > aspect) { aspect = r; worst = SupportIndex(sup, s, mesh.Cells[s.CellIndex].LayerIndex); }
            }
        }
        Assert.True(worst >= 0);
        var strip = sup.Cells[worst];
        var block = sup.Cells.First(c => c.LayerIndex == strip.LayerIndex && !c.Equals(strip) &&
                                         c.XMin <= strip.XMin && c.XMax >= strip.XMax &&
                                         c.YMin <= strip.YMin && c.YMax >= strip.YMax);
        // A strip spans its block's full depth along the current and a segment across it; its sibling is the rest.
        bool alongX = strip.XMin == block.XMin && strip.XMax == block.XMax;
        var sibling = alongX
            ? (strip.YMin == block.YMin ? strip with { YMin = strip.YMax, YMax = block.YMax }
                                        : strip with { YMin = block.YMin, YMax = strip.YMin })
            : (strip.XMin == block.XMin ? strip with { XMin = strip.XMax, XMax = block.XMax }
                                        : strip with { XMin = block.XMin, XMax = strip.XMin });
        output.WriteLine($"worst strip {strip.Width * 1e6:F2} × {strip.Height * 1e6:F2} µm (aspect {aspect:F1}) in a " +
                         $"{block.Width * 1e6:F2} × {block.Height * 1e6:F2} µm block");

        var st = PlanarFillSettings.Default;
        double err = 0;
        foreach (var (a, c, what) in new[]
                 {
                     (strip, strip, "self"), (block, strip, "block ⊃ strip"), (strip, block, "strip ⊂ block"),
                     (strip, sibling, "sibling"),
                 })
        {
            var (nodes, panels) = PlanarFill.RuleForCells(a, c, st);
            var q = PlanarFill.CellPairCores(a, c, nodes, panels, wantRad: false, st.SelfPanels);
            foreach (var (kernel, f, pick) in new (string, Func<double, Complex>, Func<PlanarFill.CoreTriple, double>)[]
                     {
                         ("1/R", r => 1.0 / r, t => t.Inverse), ("ln r", r => Math.Log(r), t => t.Log),
                     })
            {
                double ex = Oracle(a, c, false, false, alongX, f);
                double e = Math.Abs(pick(q.Pulse) - ex) / Math.Abs(ex);
                // the rising ramp along the flow on both sides, and on each side alone
                var (q10, q01, q11) = alongX ? (q.X10, q.X01, q.X11) : (q.Y10, q.Y01, q.Y11);
                e = Math.Max(e, Rel(pick(q11), Oracle(a, c, true, true, alongX, f)));
                e = Math.Max(e, Rel(pick(q10), Oracle(a, c, true, false, alongX, f)));
                e = Math.Max(e, Rel(pick(q01), Oracle(a, c, false, true, alongX, f)));
                output.WriteLine($"  {what,-14} {kernel,-5} worst relative {e:E2}");
                err = Math.Max(err, e);
            }
        }
        Assert.True(err < 5e-6, $"strip quadrature {err:E2}");

        static double Rel(double x, double y) => Math.Abs(x - y) / Math.Abs(y);
    }

    private static int SupportIndex(PlanarMesh sup, PlanarStepSupport s, int layer)
    {
        for (int i = 0; i < sup.Cells.Count; i++)
        {
            var c = sup.Cells[i];
            if (c.LayerIndex == layer && c.XMin == s.XMin && c.XMax == s.XMax && c.YMin == s.YMin && c.YMax == s.YMax)
                return i;
        }
        return -1;
    }

    /// <summary>∫∫ w_a w_c f(ρ) over two whole rectangles, each weight 1/Area or the rising ramp (u − u_min)/Area.</summary>
    private static double Oracle(PlanarCell a, PlanarCell c, bool rampA, bool rampC, bool alongX, Func<double, Complex> f)
        => PlanarPairOracle.Pair(a, c, rampA, alongX ? a.XMin : a.YMin, rampC, alongX ? c.XMin : c.YMin, alongX, f).Real;

    /// <summary>
    /// R-pms5-5 — the Gram across a refinement step is ∫ f_m·f_n over the metal both supports cover: checked entry for
    /// entry against a direct integration over each block's elementary sub-cells (on which every weight is a polynomial,
    /// so a 2-point rule is exact), including the entries between two segments' strips of one coarse block that a
    /// per-cell Gram would miss.
    /// </summary>
    [Fact]
    public void TheGram_AcrossAStep_IsTheIntegralOverTheSupports()
    {
        var mesh = Mesh(Coarse with { LocalGrid = true });
        var gram = PlanarFill.BuildCores(mesh).Gram;
        int n = mesh.Bases.Count;
        var halves = new List<(int Basis, PlanarStepSupport S)>[mesh.Cells.Count];
        for (int c = 0; c < halves.Length; c++) halves[c] = [];
        for (int i = 0; i < n; i++)
        {
            if (mesh.Bases[i].Direction == PlanarBasisDirection.Z) continue;
            var (a, b) = PlanarLocalGrid.Supports(mesh, mesh.Bases[i]);
            halves[a.CellIndex].Add((i, a)); halves[b.CellIndex].Add((i, b));
        }

        var direct = new Dictionary<(int, int), double>();
        double[] gx = [-1 / Math.Sqrt(3), 1 / Math.Sqrt(3)];
        for (int c = 0; c < mesh.Cells.Count; c++)
        {
            var cell = mesh.Cells[c];
            foreach (var (bi, si) in halves[c])
                foreach (var (bj, sj) in halves[c])
                {
                    if (bi > bj || mesh.Bases[bi].Direction != mesh.Bases[bj].Direction) continue;
                    bool x = mesh.Bases[bi].Direction == PlanarBasisDirection.X;
                    double sum = 0;
                    for (int iy = cell.IY; iy < cell.IY1; iy++)
                        for (int ix = cell.IX; ix < cell.IX1; ix++)
                        {
                            double x0 = mesh.GridX[ix], x1 = mesh.GridX[ix + 1], y0 = mesh.GridY[iy], y1 = mesh.GridY[iy + 1];
                            foreach (double u in gx)
                                foreach (double v in gx)
                                {
                                    double px = 0.5 * (x0 + x1) + 0.5 * (x1 - x0) * u;
                                    double py = 0.5 * (y0 + y1) + 0.5 * (y1 - y0) * v;
                                    sum += 0.25 * (x1 - x0) * (y1 - y0) * W(si, px, py, x) * W(sj, px, py, x);
                                }
                        }
                    direct[(bi, bj)] = direct.GetValueOrDefault((bi, bj)) + sum;
                }
        }

        int stripPairs = 0;
        foreach (var ((i, j), v) in direct)
        {
            Assert.Equal(v, gram.At(i, j), Math.Max(Math.Abs(v), 1e-30) * 1e-12);
            if (i != j && v != 0 && (mesh.Bases[i].IsSegment || mesh.Bases[j].IsSegment)) stripPairs++;
        }
        output.WriteLine($"{direct.Count} pairs, {stripPairs} off-diagonal with a step rooftop; {gram.NonZeros} stored");
        Assert.True(stripPairs > 0);
        Assert.Equal(direct.Count(kv => kv.Value != 0), gram.NonZeros);

        static double W(PlanarStepSupport s, double px, double py, bool alongX)
        {
            if (px < s.XMin || px > s.XMax || py < s.YMin || py > s.YMax) return 0;
            double u = alongX ? px : py;
            double edge = s.Sign > 0 ? (alongX ? s.XMin : s.YMin) : (alongX ? s.XMax : s.YMax);
            return Math.Abs(u - edge) / s.Area;
        }
    }

    /// <summary>R-pms5-6 — the dense solve takes a block mesh with internal delta-gap ports, and lands on the tensor
    /// grid's answer; an edge port and the accelerated solve are still refused by name (brief 6).</summary>
    [Fact]
    public void ABlockMesh_Solves_AndEdgePortsAndAimStayRefused()
    {
        var p = StepBlock();
        var local = Mesh(Coarse with { LocalGrid = true });
        var run = PlanarSolve.Run(p, local, PlanarPorts.ResolveAll(local, GapPorts()), [10e9],
                                  PlanarSolveSettings.Default with { Deembed = false });
        var tensor = Mesh(Coarse);
        var reference = PlanarSolve.Run(p, tensor, PlanarPorts.ResolveAll(tensor, GapPorts()), [10e9],
                                        PlanarSolveSettings.Default with { Deembed = false });
        double d = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                d = Math.Max(d, (run.Points[0].S[i, j] - reference.Points[0].S[i, j]).Magnitude);
        output.WriteLine($"N {local.Bases.Count} local against {tensor.Bases.Count} tensor: max |ΔS| {d:E2}");
        Assert.True(d < 1e-3);

        var edge = new PlanarPort(3, new EmPoint(70e-6, 15e-6), PlanarPortSide.MaxX, 50, 1);
        var refused = Assert.Throws<PlanarLocalGridRefusedException>(() => PlanarPorts.ResolveAll(local, [edge]));
        Assert.Equal("an edge port", refused.Consumer);
        Assert.Throws<PlanarLocalGridRefusedException>(() => new PlanarSolveContext(
            local, [], PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default }, slabHeightM: 100e-6));
    }
}
