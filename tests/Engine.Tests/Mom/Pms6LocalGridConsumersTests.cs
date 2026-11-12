// PMS-6 — every reader of the mesh on a locally refined grid (brief-pms-6-local-grid-consumers.md).
//
// One test per claim; the five pure readers are routine, the two solves (~16–21 s in Debug) are Category=Benchmark. The
// de-embedded accuracy on A1 and A2, the
// accelerator's pitch measurement, the far-field gate and the end-to-end run through EmRunService were taken with a
// scratch harness in Release and are in src/Engine/Mom/RESOLVED.md §PMS-6.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class Pms6LocalGridConsumersTests(ITestOutputHelper output)
{
    private static PlanarPolygon R(double x0, double y0, double x1, double y1)
        => PlanarLineFixtures.Rect(x0 * 1e-6, y0 * 1e-6, x1 * 1e-6, y1 * 1e-6);

    /// <summary>A meshed chip as the product's run meshes it: ground paths and feed leads grown, ports handed to the
    /// mesher, ports resolved.</summary>
    private static (PlanarProblem Problem, PlanarMesh Mesh, IReadOnlyList<PlanarPortResolution> Ports,
                    IReadOnlyList<PlanarFeedLead> Leads) Meshed(SyntheticMmicChip chip, PlanarMeshSettings s)
    {
        var ports = chip.Ports();
        var grounded = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        var feed = PlanarFeedExtension.ExtendDetailed(grounded, ports, PlanarSolveSettings.Default.Calibration, null);
        var mesh = SurfaceMesher.Mesh(feed.Problem, s, PlanarEdgeReference.LocalConductorWidth, ports: ports,
                                      leads: feed.Leads).Mesh;
        return (feed.Problem, mesh, PlanarPorts.ResolveAll(mesh, ports), feed.Leads);
    }

    private static readonly PlanarMeshSettings TwoAcross = PlanarMeshSettings.Default with { MinCellsAcrossConductor = 2 };

    /// <summary>Deterministic currents, so the pure readers can be compared without a solve.</summary>
    private static Vec<Complex> Currents(int n)
    {
        var rng = new Random(11);
        var v = new Vec<Complex>(n);
        for (int i = 0; i < n; i++) v[i] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        return v;
    }

    /// <summary>Gate 1 — with every block forced 1 × 1 there is no block, and every converted reader returns the
    /// tensor grid's answer bit for bit: the ports, the current-density map, the rooftop transform and the DC point.</summary>
    [Fact]
    public void OneByOneBlocks_AreTheTensorGrid_ForEveryReader()
    {
        var chip = SyntheticMmicChip.AccuracyBlock(regionVia: true);
        var off = Meshed(chip, TwoAcross);
        var forced = Meshed(chip, TwoAcross with { LocalGrid = true, LocalGridForceElementary = true });
        Assert.False(forced.Mesh.HasBlocks);

        for (int p = 0; p < off.Ports.Count; p++)
        {
            Assert.Equal(off.Ports[p].BasisIndices, forced.Ports[p].BasisIndices);
            Assert.Equal(off.Ports[p].TransverseLines, forced.Ports[p].TransverseLines);
            Assert.Equal(off.Ports[p].LongitudinalRunM, forced.Ports[p].LongitudinalRunM);
        }

        var i = Currents(off.Mesh.Bases.Count);
        var a = PlanarCurrentDensity.Compute(off.Mesh, i, 1, 10e9);
        var b = PlanarCurrentDensity.Compute(forced.Mesh, i, 1, 10e9);
        Assert.Null(b.Pieces);
        Assert.Equal(a.Magnitude, b.Magnitude);
        Assert.Equal(a.Jx, b.Jx);

        foreach (var (pa, pb) in off.Mesh.Bases.Zip(forced.Mesh.Bases))
            if (pa.Direction != PlanarBasisDirection.Z)
                Assert.Equal(RooftopSpectrum.Of(off.Mesh, pa, 3e4, -2e4), RooftopSpectrum.Of(forced.Mesh, pb, 3e4, -2e4));

        var da = PlanarDcSolve.Solve(off.Problem, off.Mesh, off.Ports, off.Leads).Y;
        var db = PlanarDcSolve.Solve(forced.Problem, forced.Mesh, forced.Ports, forced.Leads).Y;
        for (int r = 0; r < da.RowCount; r++)
            for (int c = 0; c < da.ColCount; c++) Assert.Equal(da[r, c], db[r, c]);
    }

    /// <summary>
    /// R-pms6-1 / gate 3 — a port's feed window carries only the lines active at the port: on fixture S at 0.6 mm every
    /// port's cross-section is narrower than the tensor grid's, its end run and reference plane are the tensor grid's,
    /// and its calibration standard is built from that cross-section (SameCrossSection against the standard's own port).
    /// </summary>
    [Fact]
    public void ThePortWindow_CarriesOnlyTheFeedsOwnLines()
    {
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600));
        var tensor = Meshed(chip, PlanarMeshSettings.Default);
        var local = Meshed(chip, PlanarMeshSettings.Default with { LocalGrid = true });
        Assert.True(local.Mesh.HasBlocks);

        for (int p = 0; p < tensor.Ports.Count; p++)
        {
            var (t, l) = (tensor.Ports[p], local.Ports[p]);
            var std = PlanarCalibration.BuildLine(l, 300e-6, 3);
            output.WriteLine($"port {l.Number}: {t.BasisCount} across on the tensor grid, {l.BasisCount} on the local; " +
                             $"standard N {PlanarCalibration.BuildLine(t, 300e-6, 3).Mesh.Bases.Count:N0} → {std.Mesh.Bases.Count:N0}");
            Assert.True(l.BasisCount < t.BasisCount);
            Assert.Equal(t.ReferencePlaneM, l.ReferencePlaneM);
            Assert.Equal(t.LongitudinalRunM.Take(3), l.LongitudinalRunM.Take(3));
            Assert.Equal(t.WidthM, l.WidthM, 1e-12 * t.WidthM);
            Assert.True(PlanarPortCalibrator.SameCrossSection(l, std.Port1, 3));
        }
    }

    /// <summary>
    /// R-pms6-2 — on a block mesh the map is published per support piece: the pieces tile each cell, the cell's own
    /// values are their area-weighted means, and per cell Σ J_x·(piece area) / (cell width) is the mean of the currents
    /// crossing its two x-faces — the tensor identity (src/Engine/Mom/CLAUDE.md §3.5), held per strip.
    /// </summary>
    [Fact]
    public void TheCurrentMap_OnBlocks_IsPerPiece_AndHoldsTheEdgeIdentity()
    {
        var m = Meshed(SyntheticMmicChip.AccuracyBlock(regionVia: true), TwoAcross with { LocalGrid = true }).Mesh;
        var i = Currents(m.Bases.Count);
        var map = PlanarCurrentDensity.Compute(m, i, 1, 10e9);
        Assert.NotNull(map.Pieces);

        var faceIn = new Complex[m.Cells.Count];
        var faceOut = new Complex[m.Cells.Count];
        for (int b = 0; b < m.Bases.Count; b++)
        {
            var bs = m.Bases[b];
            if (bs.Direction != PlanarBasisDirection.X) continue;
            faceOut[bs.CellA] += i[b];
            faceIn[bs.CellB] += i[b];
        }

        int strips = 0;
        foreach (var g in map.Pieces!.GroupBy(p => p.Cell))
        {
            var cell = m.Cells[g.Key];
            double area = g.Sum(p => p.Area);
            Assert.Equal(cell.Area, area, cell.Area * 1e-12);
            Complex jx = 0;
            foreach (var p in g) jx += p.Jx * p.Area;
            Assert.Equal(map.Jx[g.Key].Real, (jx / area).Real, 1e-9 * Math.Max(1, map.Jx[g.Key].Magnitude));

            var column = jx / cell.Width;
            var mean = 0.5 * (faceIn[g.Key] + faceOut[g.Key]);
            Assert.True((column - mean).Magnitude <= 1e-12 * Math.Max(1, mean.Magnitude), $"cell {g.Key}");
            if (g.Count() > 1) strips++;
        }
        output.WriteLine($"{map.Pieces!.Count:N0} pieces over {m.Cells.Count:N0} cells; {strips} cells cut into strips");
        Assert.True(strips > 0);
    }

    /// <summary>R-pms6-3 — a rooftop across a refinement step transforms as the triangle × rectangle over its two
    /// strips: equal to the same basis on the support mesh, where the strips are its cells, and at k = 0 its dipole
    /// moment (w_A + w_B)/2.</summary>
    [Fact]
    public void AStepRooftop_TransformsAsItsStrips()
    {
        var m = Meshed(SyntheticMmicChip.AccuracyBlock(regionVia: true), TwoAcross with { LocalGrid = true }).Mesh;
        var sup = PlanarLocalGridFill.SupportMesh(m);
        int steps = 0;
        for (int b = 0; b < m.Bases.Count; b++)
        {
            var bs = m.Bases[b];
            if (!bs.IsSegment) continue;
            steps++;
            Assert.Equal(RooftopSpectrum.Of(sup, sup.Bases[b], 4e4, 1.5e4), RooftopSpectrum.Of(m, bs, 4e4, 1.5e4));
            var (sa, sb) = PlanarLocalGrid.Supports(m, bs);
            bool x = bs.Direction == PlanarBasisDirection.X;
            double wa = x ? sa.XMax - sa.XMin : sa.YMax - sa.YMin, wb = x ? sb.XMax - sb.XMin : sb.YMax - sb.YMin;
            Assert.Equal(0.5 * (wa + wb), RooftopSpectrum.Of(m, bs, 0, 0).Real, 1e-12 * (wa + wb));
        }
        Assert.True(steps > 0);
    }

    /// <summary>Gate 4 — the DC point on a block mesh is the tensor grid's conduction network, rebuilt from the blocks,
    /// so A1's port-to-port resistance is the tensor grid's (the coarse two-point network was 1.3–1.7 % low and did not
    /// converge to it).</summary>
    [Fact]
    public void TheDcPoint_OnBlocks_IsTheTensorNetwork()
    {
        var chip = SyntheticMmicChip.AccuracyBlock(regionVia: true);
        var t = Meshed(chip, TwoAcross);
        var l = Meshed(chip, TwoAcross with { LocalGrid = true });
        Assert.True(l.Mesh.HasBlocks);
        double rt = -1 / PlanarDcSolve.Solve(t.Problem, t.Mesh, t.Ports, t.Leads).Y[0, 1].Real;
        double rl = -1 / PlanarDcSolve.Solve(l.Problem, l.Mesh, l.Ports, l.Leads).Y[0, 1].Real;
        output.WriteLine($"R12 {rt * 1e3:F4} mΩ tensor, {rl * 1e3:F4} mΩ local");
        Assert.Equal(rt, rl, rt * 1e-9);
    }

    // ── The run, end to end: a 20 µm GaAs line through a 160 µm pad, which the local grid coarsens ────────────────────

    private static PlanarProblem LineThroughPad()
    {
        var slab = GroundedSlab.GaAsStarter;
        return new PlanarProblem(
            [new PlanarConductorLayer("M", [R(0, 70, 600, 90), R(220, 0, 380, 160)], 4.1e7, 3e-6, slab.HeightM)],
            slab, 10e9);
    }

    private static readonly List<PlanarPort> LinePorts =
    [
        new(1, new EmPoint(0, 80e-6), PlanarPortSide.MinX, 50),
        new(2, new EmPoint(600e-6, 80e-6), PlanarPortSide.MaxX, 50),
    ];

    private static PlanarKernelResult Run(bool local, bool accelerated)
    {
        var st = PlanarSolveSettings.Default;
        if (accelerated) st = st with { Fill = (st.Fill ?? PlanarFillSettings.Default) with { Aim = PlanarAimSettings.Default } };
        return new PlanarKernel().Solve(LineThroughPad(), PlanarMeshSettings.Default with { LocalGrid = local }, LinePorts,
                                        [10e9], st);
    }

    private static readonly Lazy<PlanarKernelResult> LocalDense = new(() => Run(local: true, accelerated: false));

    private static double MaxDiff(Mat<Complex> a, Mat<Complex> b)
    {
        double d = 0;
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++) d = Math.Max(d, (a[i, j] - b[i, j]).Magnitude);
        return d;
    }

    /// <summary>R-pms6-6 — the product's run solves a block mesh end to end, edge ports de-embedded, and lands on the
    /// tensor grid's answer. Benchmark tier: ~21 s in Debug (two de-embedded solves).</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ALocalGridMesh_SolvesEndToEnd_DeEmbedded()
    {
        var local = LocalDense.Value;
        var tensor = Run(local: false, accelerated: false);
        Assert.True(local.MeshReport.Mesh.HasBlocks);
        double d = MaxDiff(local.Solve.Points[0].S, tensor.Solve.Points[0].S);
        output.WriteLine($"N {local.MeshReport.UnknownCount} local, {tensor.MeshReport.UnknownCount} tensor: max |ΔS| {d:E2}");
        Assert.True(d < 1e-4);
    }

    /// <summary>R-pms6-5 — the accelerated solve reads a block mesh and agrees with the dense local solve. Benchmark
    /// tier: ~16 s in Debug.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheAccelerator_ReadsABlockMesh()
    {
        var aim = Run(local: true, accelerated: true);
        double d = MaxDiff(aim.Solve.Points[0].S, LocalDense.Value.Solve.Points[0].S);
        output.WriteLine($"accelerated against dense, local grid: max |ΔS| {d:E2}");
        Assert.True(d < 1e-3);
    }
}
