using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// The mesher's own contract: edge grading (R-mom-8), interface exclusion (R-mom-9), truncation
/// (R-mom-10) and the contents of <see cref="EmMeshReport"/> — which exists so the UI has nothing
/// to recompute.
/// </summary>
public class MeshingTests
{
    private static EmProblem Hero() => EmProblemBuilders.Fr4Microstrip(2.9e-3);

    // ── R-mom-9 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The strip sits ON the substrate: its bottom face carries free charge and the interface
    /// beneath it does not exist. Getting this wrong puts two unknowns on the same physical surface
    /// and the matrix goes singular — a good failure, but the specific one to test for.
    /// </summary>
    [Fact]
    public void R_mom_9_NoInterfaceSegmentLiesInsideOrOnAConductor()
    {
        var p = Hero();
        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);
        var outline = Polygon2D.AsCcw(p.Conductors[0].Outline);

        int checkedCount = 0;
        foreach (var s in report.Mesh.Segments)
        {
            if (s.Kind != EmSegmentKind.DielectricInterface) continue;
            checkedCount++;
            Assert.False(Polygon2D.ContainsOrOn(outline, s.Mid, 1e-12),
                $"interface segment at x = {s.Mid.X:G6} lies on the strip footprint");
        }
        Assert.True(checkedCount > 0, "the hero must produce interface segments at all");
    }

    [Fact]
    public void R_mom_9_TheStripFootprintIsTheOnlyGapInTheInterface()
    {
        var p = Hero();
        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);

        var xs = new List<(double A, double B)>();
        foreach (var s in report.Mesh.Segments)
            if (s.Kind == EmSegmentKind.DielectricInterface)
                xs.Add((Math.Min(s.A.X, s.B.X), Math.Max(s.A.X, s.B.X)));
        xs.Sort((u, v) => u.A.CompareTo(v.A));

        var gaps = new List<(double, double)>();
        for (int i = 1; i < xs.Count; i++)
            if (xs[i].A - xs[i - 1].B > 1e-12)
                gaps.Add((xs[i - 1].B, xs[i].A));

        var gap = Assert.Single(gaps);
        Assert.Equal(-1.45e-3, gap.Item1, 1e-9);
        Assert.Equal(+1.45e-3, gap.Item2, 1e-9);
    }

    /// <summary>The mesh must not be singular — the concrete symptom of an R-mom-9 violation.</summary>
    [Fact]
    public void TheAssembledSystemIsNonSingular()
    {
        var report = BoundaryMesher.Mesh(Hero(), EmMeshSettings.Default);
        var c = ChargeSolver.MaxwellCapacitance(report.Mesh);
        Assert.True(double.IsFinite(c[0, 0].Real) && c[0, 0].Real > 0, $"C = {c[0, 0]}");
    }

    // ── R-mom-8 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R_mom_8_ConductorCellsAreGradedTowardEveryVertex()
    {
        var p = Hero();
        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);

        // For the strip's bottom face, the cell nearest each end must be far smaller than the one
        // in the middle — and the progression must be monotone outward-to-inward.
        var face = new List<EmSegment>();
        foreach (var s in report.Mesh.Segments)
            if (s.Kind == EmSegmentKind.Conductor && Math.Abs(s.Mid.Y - 1.6e-3) < 1e-9)
                face.Add(s);
        face.Sort((u, v) => u.Mid.X.CompareTo(v.Mid.X));

        Assert.True(face.Count >= 6, $"only {face.Count} cells across the strip width (want ≥ 6)");
        Assert.True(face[0].Length < 0.2 * face[face.Count / 2].Length,
            $"end cell {face[0].Length:E3} is not much smaller than the middle cell {face[face.Count / 2].Length:E3}");
        // Grading is applied from both ends. It is not bit-symmetric: the partition walk is
        // directional and the final cell is rescaled so the last breakpoint lands exactly on the
        // edge end, which leaves a few percent between the two end cells. That is cosmetic — the
        // clockwise-winding test below pins that the answer does not depend on it.
        Assert.Equal(face[0].Length, face[^1].Length, face[0].Length * 0.1);

        for (int i = 1; i < face.Count / 2; i++)
            Assert.True(face[i].Length >= face[i - 1].Length * 0.999,
                $"cell {i} ({face[i].Length:E3}) is smaller than cell {i - 1} ({face[i - 1].Length:E3}) going inward");
    }

    /// <summary>
    /// The half of R-mom-8 that is easy to skip: the bound charge in the interface directly beside
    /// a conductor edge has the same crowding as the free charge on the metal, so the interface is
    /// graded toward the conductor edge too.
    /// </summary>
    [Fact]
    public void R_mom_8_InterfaceCellsAreGradedTowardTheConductorEdge()
    {
        var report = BoundaryMesher.Mesh(Hero(), EmMeshSettings.Default);

        var right = new List<EmSegment>();
        foreach (var s in report.Mesh.Segments)
            if (s.Kind == EmSegmentKind.DielectricInterface && s.Mid.X > 1.45e-3)
                right.Add(s);
        right.Sort((u, v) => u.Mid.X.CompareTo(v.Mid.X));

        Assert.True(right.Count >= 8, $"only {right.Count} interface cells to the right of the strip");
        Assert.True(right[0].Length < 0.05 * right[^1].Length,
            $"the cell against the strip edge ({right[0].Length:E3}) is not graded relative to the far tail ({right[^1].Length:E3})");
        for (int i = 1; i < right.Count; i++)
            Assert.True(right[i].Length >= right[i - 1].Length * 0.9,
                $"interface cell {i} shrank going outward ({right[i - 1].Length:E3} → {right[i].Length:E3})");
    }

    [Fact]
    public void GradingSurvivesAClockwiseWoundOutline()
    {
        var p = Hero();
        var reversed = new List<EmPoint>(p.Conductors[0].Outline);
        reversed.Reverse();
        var q = p with { Conductors = [p.Conductors[0] with { Outline = reversed }] };

        var a = RlgcExtractor.Extract(p, BoundaryMesher.Mesh(p, EmMeshSettings.Default));
        var b = RlgcExtractor.Extract(q, BoundaryMesher.Mesh(q, EmMeshSettings.Default));
        Assert.Equal(a.CPerM, b.CPerM, a.CPerM * 1e-9);
        Assert.Equal(a.Eeff, b.Eeff, a.Eeff * 1e-9);
    }

    // ── R-mom-10 and the report ───────────────────────────────────────────────────────────────

    [Fact]
    public void R_mom_10_TruncationExtentIsReportedAndScalesWithTheSetting()
    {
        var p = Hero();
        foreach (double heights in new[] { 5.0, 20.0, 80.0 })
        {
            var r = BoundaryMesher.Mesh(p, EmMeshSettings.Default with { TruncationHeights = heights });
            Assert.Equal(heights * 1.6e-3, r.TruncationHalfExtent, 1e-12);

            double maxX = 0;
            foreach (var s in r.Mesh.Segments)
                if (s.Kind == EmSegmentKind.DielectricInterface) maxX = Math.Max(maxX, s.B.X);
            Assert.Equal(1.45e-3 + heights * 1.6e-3, maxX, 1e-9);
        }
    }

    [Fact]
    public void TheReportCarriesEverythingTheMeshViewerNeeds()
    {
        var report = BoundaryMesher.Mesh(Hero(), EmMeshSettings.Default);

        Assert.Equal(report.Mesh.Segments.Count, report.UnknownCount);

        int total = 0;
        foreach (int n in report.SegmentsPerConductor) total += n;
        foreach (int n in report.SegmentsPerInterface) total += n;
        Assert.Equal(report.UnknownCount, total);

        Assert.Single(report.SegmentsPerConductor);
        Assert.Single(report.SegmentsPerInterface);
        Assert.Equal([1.6e-3], report.InterfaceYs);

        double min = double.MaxValue, max = 0;
        foreach (var s in report.Mesh.Segments) { min = Math.Min(min, s.Length); max = Math.Max(max, s.Length); }
        Assert.Equal(min, report.MinCellLength, min * 1e-12);
        Assert.Equal(max, report.MaxCellLength, max * 1e-12);

        Assert.NotEmpty(report.Template.EdgeFractions);
        Assert.All(report.Template.EdgeFractions[0], f => Assert.Equal(1.0, f[^1], 1e-12));
    }

    /// <summary>R-mom-13: the crossover is surfaced so a user sweeping below it is told.</summary>
    [Fact]
    public void R_mom_13_TheWheelerCrossoverFrequencyIsReported()
    {
        var report = BoundaryMesher.Mesh(Hero(), EmMeshSettings.Default);
        double f = Assert.Single(report.WheelerValidAboveHz);

        // δ = t/2 at f = 4/(π·t²·µ₀·σ): 35 µm copper ⇒ ~14 MHz.
        double want = 4.0 / (Math.PI * 35e-6 * 35e-6 * EmConstants.Mu0 * EmProblemBuilders.CopperSigma);
        Assert.Equal(want, f, want * 1e-9);
        Assert.InRange(f, 1e7, 2e7);
        Assert.Contains(report.Notes, s => s.Contains("skin depth", StringComparison.Ordinal));
    }

    /// <summary>R-mom-7: an interface coincident with the ground plane is not an interface.</summary>
    [Fact]
    public void R_mom_7_AnInterfaceOnTheGroundPlaneIsDropped()
    {
        var strip = EmProblemBuilders.Rect(-1e-3, 1.6e-3, 1e-3, 1.635e-3);
        var p = new EmProblem(
            [new EmConductor("strip", strip, EmProblemBuilders.CopperSigma)],
            [
                // A distinct material below the ground plane — the boundary at y = 0 would be an
                // interface if the plane were not there.
                new EmDielectricRegion(double.NegativeInfinity, 0, new EmMaterial(9.8)),
                new EmDielectricRegion(0, 1.6e-3, new EmMaterial(4.4)),
                new EmDielectricRegion(1.6e-3, double.PositiveInfinity, EmMaterial.Air),
            ],
            new EmGroundPlane(0, EmProblemBuilders.CopperSigma),
            [new EmPort(1, "strip", null, 50), new EmPort(2, "strip", null, 50)],
            0.02);

        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);
        Assert.Equal([1.6e-3], report.InterfaceYs);
        Assert.Contains(report.Notes, s => s.Contains("coincides with the ground plane", StringComparison.Ordinal));
    }

    /// <summary>
    /// A conductor edge crossing a region boundary is split there, so no single segment straddles
    /// two dielectrics with an ambiguous outward permittivity.
    /// </summary>
    [Fact]
    public void AConductorEdgeCrossingAnInterfaceIsSplitAtIt()
    {
        // A strip half-buried: y from h−t/2 to h+t/2, interface at h.
        const double h = 1.6e-3, t = 70e-6;
        var strip = EmProblemBuilders.Rect(-1e-3, h - 0.5 * t, 1e-3, h + 0.5 * t);
        var p = new EmProblem(
            [new EmConductor("strip", strip, EmProblemBuilders.CopperSigma)],
            [
                new EmDielectricRegion(double.NegativeInfinity, h, new EmMaterial(4.4)),
                new EmDielectricRegion(h, double.PositiveInfinity, EmMaterial.Air),
            ],
            new EmGroundPlane(0, EmProblemBuilders.CopperSigma),
            [new EmPort(1, "strip", null, 50), new EmPort(2, "strip", null, 50)],
            0.02);

        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);
        foreach (var s in report.Mesh.Segments)
        {
            if (s.Kind != EmSegmentKind.Conductor) continue;
            bool straddles = (s.A.Y - h) * (s.B.Y - h) < 0;
            Assert.False(straddles, $"segment ({s.A.X:G4},{s.A.Y:G4})→({s.B.X:G4},{s.B.Y:G4}) straddles the interface");
        }

        // And the two sides genuinely carry different permittivities.
        var seen = new HashSet<double>();
        foreach (var s in report.Mesh.Segments)
            if (s.Kind == EmSegmentKind.Conductor) seen.Add(Math.Round(s.EpsOutside.Real, 6));
        Assert.Equal([1.0, 4.4], [.. new SortedSet<double>(seen)]);
    }

    [Fact]
    public void TheTemplateRemeshesAPerturbedOutlineIdentically()
    {
        var p = Hero();
        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);
        var outline = Polygon2D.AsCcw(p.Conductors[0].Outline);
        var receded = Polygon2D.OffsetInward(outline, 1e-7);

        var again = BoundaryMesher.ConductorsOnly([receded], ["strip"], report.Template, p.Ground);
        Assert.Equal(report.SegmentsPerConductor[0], again.Segments.Count);
    }

    [Fact]
    public void PartitionFractionsAlwaysEndsAtExactlyOne()
    {
        foreach (double len in new[] { 1e-9, 1e-3, 1.0, 1e6 })
        foreach (int minCells in new[] { 1, 3, 20 })
        {
            var fr = BoundaryMesher.PartitionFractions(len, x => 0.01 * len + 0.7 * x, minCells);
            Assert.True(fr.Length >= minCells);
            Assert.Equal(1.0, fr[^1], 0.0);
            for (int i = 1; i < fr.Length; i++) Assert.True(fr[i] > fr[i - 1]);
        }
    }

    [Fact]
    public void GeometricSlopeForReproducesTheRequestedCellCount()
    {
        const double first = 0.267e-3, length = 28.8e-3;
        foreach (int n in new[] { 6, 12, 40 })
        {
            double g = BoundaryMesher.GeometricSlopeFor(first, length, n);
            double r = 1 + g, sum = first * (Math.Pow(r, n) - 1) / (r - 1);
            Assert.Equal(length, sum, length * 1e-6);
        }
        // Already enough cells at constant size ⇒ effectively uniform.
        Assert.Equal(1e-6, BoundaryMesher.GeometricSlopeFor(1.0, 5.0, 12), 1e-12);
    }
}
