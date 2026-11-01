// EMB-4 (brief-emb-4-honest-estimates.md) — the engine half of its gates. One test per claim; no timing.
//
//   R-emb4-6  the core count's distinct band keys are bit-identical with the per-worker merge.
//   R-emb4-5  the near-field estimate counts the operator's own near set, its symbolic factor predicts the LU's fill,
//             and the factor reports its columns.
//   R-emb4-2  an accelerated multi-level mesh judged on the dense ceiling says both figures.
//   R-emb4-3  a ceiling of 0 is said in words, never as a count.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class HonestEstimatesTests(ITestOutputHelper output)
{
    private static PlanarProblem TwoLevelWithVia()
    {
        var stack = LayerStacks.MmicTwoLevel;
        return new PlanarProblem(
            [new PlanarConductorLayer("M1", [PlanarLineFixtures.Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 2e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("M2", [PlanarLineFixtures.Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 3e-6, stack.TopZ)],
            GroundedSlab.GaAsStarter, 10e9, null, stack,
            [new PlanarVia(0, 1, [PlanarLineFixtures.Rect(180e-6, 30e-6, 220e-6, 70e-6)], 4.1e7)]);
    }

    private static PlanarProblem Chamfer() => new([new PlanarConductorLayer("Metal",
        [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(3.0e-3, 0), new EmPoint(3.0e-3, 1.6e-3),
                            new EmPoint(1.6e-3, 2.6e-3), new EmPoint(0, 2.6e-3)])], 5.8e7, 35e-6)],
        GroundedSlab.Fr4Starter, 10e9);

    // ── R-emb4-6 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The per-worker merge returns exactly the keys a plain serial scan of every classifiable band pair does,
    /// ascending — on a single-level line, a conformally cut mesh and a two-level mesh with a via.</summary>
    [Fact]
    public void DistinctBandKeys_PerWorkerMerge_IsBitIdenticalToASerialScan()
    {
        (string, PlanarProblem, PlanarMeshSettings)[] meshes =
        [
            ("GaAs line 2 mm", PlanarLineFixtures.GaAsLine(2e-3, 20e9), PlanarLineFixtures.Shipping),
            ("chamfer, conformal", Chamfer(), new PlanarMeshSettings(Auto: false, CellsPerWavelength: 4, EdgeMesh: false,
                                                                      BoundaryCells: PlanarBoundaryCells.Conformal)),
            ("two levels + via", TwoLevelWithVia(), PlanarLineFixtures.Shipping),
        ];
        foreach (var (label, problem, settings) in meshes)
        {
            var mesh = SurfaceMesher.Mesh(problem, settings).Mesh;
            var st = PlanarFillSettings.Default;
            var topo = RampTopology.Build(mesh);
            var classifier = PairClassifier.Build(mesh);

            var serial = new HashSet<long>();
            int m = mesh.Cells.Count;
            for (int a = 0; a < m; a++)
            {
                if (!classifier.Classifiable[a]) continue;
                for (int c = topo.MinInner[a]; c < m; c++)
                    if (classifier.Classifiable[c]) serial.Add(classifier.Key(mesh.Cells[a], mesh.Cells[c], st, out _));
            }
            var expected = serial.ToArray();
            Array.Sort(expected);

            var keys = PlanarFill.DistinctBandKeys(mesh, topo, classifier, st);
            output.WriteLine($"{label}: {m:N0} cells, {keys.Length:N0} classes");
            Assert.Equal(expected, keys);
        }
    }

    // ── R-emb4-5 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The estimate's near set is the operator's: the count equals the CSR <see cref="PlanarAimGeometry"/>
    /// builds on the same mesh at the same slab height, and the radius is the geometry's.</summary>
    [Fact]
    public void NearFieldEstimate_CountsTheOperatorsOwnNearSet()
    {
        var problem = PlanarLineFixtures.GaAsLine(2e-3, 20e9);
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping, accelerated: true).Mesh;
        var geometry = PlanarAimGeometry.Build(PlanarFill.BuildGeometryOnlyCores(mesh), problem.Slab.HeightM);

        var estimate = PlanarNearFieldEstimate.For(mesh, problem.Slab.HeightM);
        output.WriteLine(estimate.Describe());

        Assert.Equal(geometry.ColIdx.LongLength, estimate.NearEntries);
        Assert.Equal(geometry.NearRadiusM, estimate.NearRadiusM);
        Assert.Equal(geometry.N, estimate.HorizontalUnknowns);
        Assert.NotNull(estimate.FactorNonZeros);
    }

    /// <summary>A symmetric near pattern on a grid of rooftops, with a dominant diagonal so the LU's pivots stay on it.</summary>
    private static (int N, int[] RowPtr, int[] ColIdx, Complex[] Values) NearPattern(int side, double radius)
    {
        var c = new List<(double X, double Y)>();
        for (int i = 0; i < side - 1; i++) for (int j = 0; j < side; j++) c.Add((i + 1, j + 0.5));
        for (int i = 0; i < side; i++) for (int j = 0; j < side - 1; j++) c.Add((i + 0.5, j + 1));
        int n = c.Count;
        var rowPtr = new int[n + 1];
        var cols = new List<int>();
        var vals = new List<Complex>();
        for (int a = 0; a < n; a++)
        {
            for (int b = 0; b < n; b++)
            {
                double dx = c[a].X - c[b].X, dy = c[a].Y - c[b].Y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > radius) continue;
                cols.Add(b);
                vals.Add(a == b ? new Complex(4 * radius * radius, 1) : new Complex(1 / (d + 0.3), 0.1 / (d + 1)));
            }
            rowPtr[a + 1] = cols.Count;
        }
        return (n, rowPtr, [.. cols], [.. vals]);
    }

    /// <summary>The symbolic analysis on the factor's own AMD ordering predicts SparseLU's fill exactly (pivots on the
    /// diagonal), and the factor reports its progress in columns, ending on the last one.</summary>
    [Fact]
    public void NearFactor_FillIsPredictedExactly_AndProgressIsReportedInColumns()
    {
        var (n, rowPtr, colIdx, values) = NearPattern(side: 24, radius: 3);
        var (predicted, work) = PlanarNearFieldEstimate.SymbolicFactor(n, rowPtr, colIdx);

        var reports = new List<(long Done, long Total)>();
        var (lu, nnz, factorNnz) = PlanarNearFactor.Factor(n, rowPtr, colIdx, values, (d, t) => reports.Add((d, t)));
        output.WriteLine($"n = {n}, near {nnz:N0}, factor {factorNnz:N0} (predicted {predicted:N0}), work {work:E3}, " +
                         $"{reports.Count} progress reports");

        Assert.NotNull(lu);
        Assert.Equal(predicted, factorNnz);
        Assert.True(reports.Count > 2);
        Assert.All(reports, r => Assert.Equal(n, r.Total));
        Assert.Equal(reports.Select(r => r.Done).Order(), reports.Select(r => r.Done));
        Assert.Equal((n, (long)n), reports[^1]);
    }

    // ── R-emb4-2 / R-emb4-3 ─────────────────────────────────────────────────────────────────────

    /// <summary>With the accelerated solve on, a mesh with a via is judged on the DENSE ceiling (unchanged), and the
    /// budget and the past-the-limit warning say what the accelerated operator holds beside the dense figure.</summary>
    [Fact]
    public void AcceleratedViaMesh_JudgedOnTheDenseCeiling_SaysTheAcceleratedFigure()
    {
        var problem = TwoLevelWithVia();
        var limited = PlanarLineFixtures.Shipping with { MemoryLimitBytes = 2_000_000 };
        var report = SurfaceMesher.Mesh(problem, limited,
                                        accelerated: SurfaceMesher.UsesAcceleratedCeiling(true, problem.RequiresGeneralKernel),
                                        aimOn: true);
        output.WriteLine(report.Budget);
        foreach (var f in report.Notes) output.WriteLine(f);

        Assert.False(report.AcceleratedCeiling);
        Assert.NotNull(report.AcceleratedEstimate);
        Assert.Contains("judged on the dense ceiling (", report.Budget, StringComparison.Ordinal);
        Assert.Contains("the accelerated solve this run uses holds about", report.Budget, StringComparison.Ordinal);
        Assert.True(report.PastMachineLimit);
        Assert.Contains(report.Notes, n => n.StartsWith("This mesh has", StringComparison.Ordinal)
                                           && n.Contains("the accelerated solve this run uses holds about", StringComparison.Ordinal));

        // Without the accelerated solve nothing of the kind is said.
        var dense = SurfaceMesher.Mesh(problem, limited);
        Assert.DoesNotContain("accelerated solve this run uses", dense.Budget, StringComparison.Ordinal);
    }

    /// <summary>A limit this mesh's cores alone exceed makes the ceiling 0; the budget says what is over and by how
    /// much, never "allows 0 unknowns" or an infinite ratio.</summary>
    [Fact]
    public void CeilingOfZero_IsSaidAsTheCoresAlone()
    {
        var report = SurfaceMesher.Mesh(PlanarLineFixtures.GaAsLine(2e-3, 20e9),
                                        PlanarLineFixtures.Shipping with { MemoryLimitBytes = 1_000 });
        output.WriteLine(report.Budget);

        Assert.Equal(0, report.CeilingUnknowns);
        Assert.Contains("this mesh's cores alone need", report.Budget, StringComparison.Ordinal);
        Assert.Contains("so no unknown count fits beside them — OVER.", report.Budget, StringComparison.Ordinal);
        Assert.DoesNotContain("allows 0", report.Budget, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", report.Budget, StringComparison.Ordinal);
    }
}
