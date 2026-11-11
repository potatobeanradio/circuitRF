// PMS-1 — a medium MMIC reaches its verdict without stalling (brief-pms-1-preflight-stall.md).
//
// The synthetic chip (Support/SyntheticMmicChip) reproduces the reported stall: the mesh returns in about a second,
// and pricing the calibration standards then counted each standard's cores exactly and never finished. These tests
// hold the four fixes on it: pricing is skipped when the verdict is decided; a count stops at a class cap at the same
// point every time; the pricing stage stops on cancellation; the via scan is bit-identical to a whole-grid scan; and
// the accelerated estimate stops counting once it knows. No assertion here is on time.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class Pms1PreflightStallTests(ITestOutputHelper output)
{
    private const long WslMemory = 7_600_000_000L;

    private static PlanarMeshSettings Limited => PlanarMeshSettings.Default with
    {
        MemoryLimitBytes = WslMemory, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
    };

    /// <summary>R-pms1-2 — on fixture S the dense matrix alone is past the limit, the report says so, and the standards
    /// are not priced: a note says why and nothing is added to the figure.</summary>
    [Fact]
    public void FixtureS_MatrixAlonePastTheLimit_TheStandardsAreNotPriced()
    {
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.S);
        var ports = chip.Ports();
        var meshed = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        var report = SurfaceMesher.Mesh(meshed, Limited, PlanarEdgeReference.LocalConductorWidth, ports: ports);
        output.WriteLine($"N = {report.UnknownCount:N0}, grid {report.GridLinesX} x {report.GridLinesY}");

        Assert.True(report.CanSolve);
        Assert.True(report.PastMachineLimit);
        Assert.True(report.MatrixAlonePastLimit);

        var skipped = PlanarRunMemory.NotPriced(report);
        Assert.Equal(0, skipped.StandardResidentBytes);
        Assert.Contains(skipped.AllFindings, f => f.Text == PlanarRunMemory.NotPricedNote);
        Assert.True(skipped.PastMachineLimit);
    }

    /// <summary>R-pms1-2 — a mesh that fits is not marked decided, so it is priced as before.</summary>
    [Fact]
    public void AMeshThatFits_IsNotMarkedDecided()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 5e9);
        var report = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse with
        {
            MemoryLimitBytes = WslMemory, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
        });
        Assert.False(report.MatrixAlonePastLimit);
        Assert.False(SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).MatrixAlonePastLimit);
    }

    /// <summary>R-pms1-5 — the bounded via scan emits exactly the vertical bases a whole-grid scan of every polygon
    /// does, in the same order, on fixture S (posts, the large region and octagonal backside vias). Fixture L was
    /// checked the same way when this was written (7 s, mostly the whole-grid reference) and is left out of the
    /// routine gate for its cost.</summary>
    [Fact]
    public void ViaScan_IsBitIdenticalToAWholeGridScan()
    {
        const string preset = "S";
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.S);
        var problem = chip.ToProblem();
        var mesh = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth).Mesh;

        var expected = WholeGridViaBases(problem, mesh);
        int firstVia = mesh.Bases.Count - expected.Count;
        Assert.True(expected.Count > 0);
        Assert.All(mesh.Bases.Take(firstVia), b => Assert.NotEqual(PlanarBasisDirection.Z, b.Direction));
        Assert.Equal(expected, mesh.Bases.Skip(firstVia).ToList());
        output.WriteLine($"{preset}: {expected.Count:N0} vertical bases of {mesh.Bases.Count:N0}");
    }

    /// <summary>R-pms1-3 — a count that passes the class cap stops, says it is an estimate, and lands on the same figure
    /// every time; under the cap it is the exact count it always was.</summary>
    [Fact]
    public void ACountPastTheClassCap_StopsDeterministically_AndUnderItIsExact()
    {
        // A calibration standard of fixture S: built on the chip's own lines, so its pairs barely share a class.
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.S);
        var ports = chip.Ports();
        var meshed = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        var report = SurfaceMesher.Mesh(meshed, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth,
                                        ports: ports);
        var port = PlanarPorts.ResolveAll(report.Mesh, ports)[1];
        var standard = PlanarCalibration.BuildSet(port, meshed.Slab, 10e9, 10e9, PlanarSolveSettings.Default.Calibration,
                                                  meshed.Layers[port.LayerIndex].Name)[0].Mesh;

        const long cap = 200_000;
        var first = PlanarFill.CountCores(standard, null, default, cap);
        var again = PlanarFill.CountCores(standard, null, default, cap);
        output.WriteLine($"standard: {standard.Cells.Count:N0} cells, band {first.BandPairs:N0}, classes {first.Classes:N0} (estimated)");
        Assert.False(first.Counted);
        Assert.Equal(first, again);
        Assert.InRange(first.Classes, cap, first.BandPairs);

        // Under the cap: the count is DistinctBandKeys' own, and counted.
        var small = SurfaceMesher.Mesh(PlanarLineFixtures.GaAsLine(2e-3, 20e9), PlanarLineFixtures.Shipping).Mesh;
        var exact = PlanarFill.CountCores(small, null);
        var keys = PlanarFill.DistinctBandKeys(small, RampTopology.Build(small), PairClassifier.Build(small),
                                               PlanarFillSettings.Default);
        Assert.True(exact.Counted);
        Assert.Equal(keys.Length, exact.Classes);
    }

    /// <summary>R-pms1-4 — the pricing opens its own stage, and a cancellation taken when it opens stops it.</summary>
    [Fact]
    public void ThePricingStage_StopsOnCancellation()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 5e9);
        var ports = PlanarLineFixtures.EndPorts(problem);
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var resolved = PlanarPorts.ResolveAll(mesh, ports);

        var stages = new List<string>();
        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token = cts.Token,
            Progress = new SyncProgress(p =>
            {
                stages.Add(p.Stage);
                if (p.Stage.StartsWith(PlanarRunMemory.PricingStage, StringComparison.Ordinal)) cts.Cancel();
            }),
        };
        Assert.ThrowsAny<OperationCanceledException>(
            () => PlanarRunMemory.StandardBytes(problem, resolved, [5e9], new PlanarSolveSettings(), mesh, control));
        Assert.Contains(PlanarRunMemory.PricingStage, stages);

        // And uncancelled, the same call prices as it always did.
        Assert.Equal(PlanarRunMemory.StandardBytes(problem, resolved, [5e9], new PlanarSolveSettings(), mesh),
                     PlanarRunMemory.StandardBytes(problem, resolved, [5e9], new PlanarSolveSettings(), mesh,
                                                   new RunControl()));
    }

    /// <summary>R-pms1-6 — on fixture S the accelerated estimate stops past its cap and says the figure is a lower bound.</summary>
    [Fact]
    public void TheAcceleratedEstimate_StopsPastItsCap_AndSaysSo()
    {
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.S);
        var mesh = SurfaceMesher.Mesh(chip.ToProblem(), PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth).Mesh;
        var estimate = PlanarNearFieldEstimate.For(mesh, 100e-6);
        output.WriteLine(estimate.Describe());

        Assert.True(estimate.CountStopped);
        Assert.True(estimate.Large);
        Assert.True(estimate.NearEntries > PlanarNearFieldEstimate.AnalysedNearEntriesCap);
        Assert.Null(estimate.FactorNonZeros);
        Assert.Contains("more than", estimate.Describe(), StringComparison.Ordinal);
        Assert.Equal(estimate, PlanarNearFieldEstimate.For(mesh, 100e-6));
    }

    /// <summary>The scan the bounded one replaced, written out: every cell of the grid against every polygon of every
    /// via group, in (via, IY, IX) order.</summary>
    private static List<PlanarBasis> WholeGridViaBases(PlanarProblem problem, PlanarMesh mesh)
    {
        var gx = mesh.GridX; var gy = mesh.GridY;
        int nx = gx.Count - 1, ny = gy.Count - 1;
        var cellAt = new int[problem.Layers.Count][];
        for (int l = 0; l < cellAt.Length; l++) { cellAt[l] = new int[nx * ny]; Array.Fill(cellAt[l], -1); }
        for (int c = 0; c < mesh.Cells.Count; c++)
            cellAt[mesh.Cells[c].LayerIndex][mesh.Cells[c].IY * nx + mesh.Cells[c].IX] = c;

        var bases = new List<PlanarBasis>();
        foreach (var via in problem.ViaList)
            for (int iy = 0; iy < ny; iy++)
            {
                double yc = 0.5 * (gy[iy] + gy[iy + 1]);
                for (int ix = 0; ix < nx; ix++)
                {
                    double xc = 0.5 * (gx[ix] + gx[ix + 1]);
                    if (!via.Polygons.Any(p => p.Contains(xc, yc))) continue;
                    int b = cellAt[via.UpperLayerIndex][iy * nx + ix];
                    if (via.ToGround)
                    {
                        if (b >= 0) bases.Add(new PlanarBasis(via.UpperLayerIndex, b, b, PlanarBasisDirection.Z, AttachesToGround: true));
                        continue;
                    }
                    int a = cellAt[via.LowerLayerIndex][iy * nx + ix];
                    if (a >= 0 && b >= 0) bases.Add(new PlanarBasis(via.LowerLayerIndex, a, b, PlanarBasisDirection.Z));
                }
            }
        return bases;
    }

    private sealed class SyncProgress(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
