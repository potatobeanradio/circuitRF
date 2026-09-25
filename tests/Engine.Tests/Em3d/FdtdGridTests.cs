using System.Numerics;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-em3d-8 §7 — the FDTD grid generator. No openEMS anywhere: every property is a property of
// three arrays of numbers, and every test runs in milliseconds.

public sealed class FdtdGridTests(ITestOutputHelper output)
{
    private const double Um = 1e-6;
    private const double C0 = 299_792_458.0;
    private static readonly OpenEmsGridSettings Defaults = OpenEmsGridSettings.Default;
    private const long Plenty = long.MaxValue;

    // ── 1. Microstrip: the thirds rule, and lines exactly where they must be ────────────────────

    [Fact]
    public void Gate1_Microstrip_ThirdsRule_SubstrateAndGround_PortsOnLines()
    {
        var p = Microstrip();
        const double w = 600 * Um, hSub = 254 * Um;

        // The rule's statement: the local cell is the target size at the edge — λ at the top frequency
        // in the densest material there, over CellsPerWavelength — no wider than 3/5 of the metal.
        double h = Math.Min(C0 / (20e9 * Math.Sqrt(9.8) * 20), 0.6 * w);
        var on = FdtdGrid.Build(p, Defaults, Plenty);
        var x = on.X.Lines;
        AssertLine(x, +w / 2 - h / 3);          // a third inside the right edge
        AssertLine(x, +w / 2 + 2 * h / 3);      // two thirds outside it
        AssertLine(x, -w / 2 + h / 3);
        AssertLine(x, -w / 2 - 2 * h / 3);
        Assert.Contains(on.X.Required, l => l.Sources.Any(s => s.Feature == "strip" && s.Kind == FdtdLineKind.ThirdsInside));

        // Ports sit ON lines, on every axis (their extents), as do the substrate top and the ground.
        foreach (var port in p.Ports)
        {
            AssertLine(on.X.Lines, port.Min.X); AssertLine(on.X.Lines, port.Max.X);
            AssertLine(on.Y.Lines, port.Min.Y);
            AssertLine(on.Z.Lines, port.Min.Z); AssertLine(on.Z.Lines, port.Max.Z);
        }
        AssertLine(on.Z.Lines, 0);
        AssertLine(on.Z.Lines, hSub);
        Assert.Contains(0.0, on.Z.Lines);       // exactly: the PEC floor is a face, and faces never move
        Assert.Contains(hSub, on.Z.Lines);      // exactly: the sheet's plane never moves

        // Rule off: the edges carry their own lines and the thirds pair is gone.
        var off = FdtdGrid.Build(p, Defaults with { ThirdsRule = false }, Plenty);
        Assert.Contains(+w / 2, off.X.Lines);
        Assert.Contains(-w / 2, off.X.Lines);
        Assert.DoesNotContain(off.X.Lines, v => Math.Abs(v - (w / 2 - h / 3)) < 1e-9);
        Assert.DoesNotContain(off.X.Required, l => l.Sources.Any(s => s.Kind is FdtdLineKind.ThirdsInside or FdtdLineKind.ThirdsOutside));
    }

    /// <summary>
    /// The width the thirds rule reads is the metal's ACROSS the edge. A strip whose end steps in to a
    /// narrower tab has the step's vertex 200 µm inside its long edge's line but beyond that edge's end;
    /// read as the width, it halved the local cell (on the generated case B, where a line is fused to its
    /// via pad, a pad vertex made a 3.48 µm cell). The long edge's pair sits where a plain 600 µm
    /// strip's does.
    /// </summary>
    [Fact]
    public void Gate1b_TheThirdsWidthIsTheMetalAcrossTheEdge_NotAVertexBeyondItsEnd()
    {
        var box = Box(-1500, 1500, -2000, 2000, 0, 1254, pecFloor: true);
        IReadOnlyList<Point2> tee =
        [
            new(-300 * Um, -2000 * Um), new(300 * Um, -2000 * Um), new(300 * Um, 1500 * Um), new(100 * Um, 1500 * Um),
            new(100 * Um, 1700 * Um), new(-300 * Um, 1700 * Um),
        ];
        var p = new Em3dProblem([Substrate(-1500, 1500, -2000, 2000, 254), AirSolid(box)],
                                [new Em3dSheet("strip", "Copper", tee, [], 254 * Um, 5 * Um, 10)], Materials,
                                [PortY(1, "strip", -300 * Um, 300 * Um, -2000 * Um, 254 * Um)], box, Band, 20);

        double h = Math.Min(C0 / (20e9 * Math.Sqrt(9.8) * 20), 0.6 * 600 * Um);
        var x = FdtdGrid.Build(p, Defaults, Plenty).X.Lines;
        AssertLine(x, 300 * Um - h / 3);
        AssertLine(x, 300 * Um + 2 * h / 3);
    }

    /// <summary>
    /// A coplanar waveguide: a 400 µm strip in a 100 µm slot cut in a same-layer ground pour. The pour's
    /// box CONTAINS the strip's, which the gap clamp used to read as "the same conductor" — so neither
    /// side of the slot was clamped, and the strip's outside line landed 160 µm out, inside the pour.
    /// Clamped at 3/7 of the slot, the local cell is too small for a pair, and both slot edges carry
    /// their own lines.
    /// </summary>
    [Fact]
    public void Gate1c_ACoplanarSlot_ClampsBothEdgesThirds_ToTheGap()
    {
        var box = Box(-1500, 1500, -2000, 2000, 0, 1254, pecFloor: true);
        var p = new Em3dProblem(
            [Substrate(-1500, 1500, -2000, 2000, 254), AirSolid(box)],
            [
                Sheet("strip", -200 * Um, 200 * Um, -1900 * Um, 1900 * Um, 254 * Um),
                new Em3dSheet("pour", "Copper", Rect(-1500, 1500, -2000, 2000), [Rect(-300, 300, -1900, 1900)],
                              254 * Um, 5 * Um, 11),
            ],
            Materials, [PortY(1, "strip", -200 * Um, 200 * Um, -1900 * Um, 254 * Um)], box, Band, 20);

        var x = FdtdGrid.Build(p, Defaults, Plenty).X.Lines;
        AssertLine(x, 200 * Um);
        AssertLine(x, 300 * Um);
        Assert.DoesNotContain(x, v => v > 200 * Um + 1e-9 && v < 300 * Um - 1e-9 && Math.Abs(v - 250 * Um) > 30 * Um);
    }

    // ── 2. Grading everywhere ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_GradingHolds_OnTheMicrostrip_AndOnFiftySeededManhattanLayouts()
    {
        AssertGraded(FdtdGrid.Build(Microstrip(), Defaults, Plenty), Defaults.GradingRatio, "microstrip");

        var rng = new Random(20260925);
        for (int i = 0; i < 50; i++)
        {
            var p = RandomManhattan(rng);
            var settings = Defaults with { GradingRatio = 1.15 + 0.35 * rng.NextDouble(), ThirdsRule = rng.Next(4) != 0 };
            var g = FdtdGrid.Build(p, settings, Plenty);
            AssertGraded(g, settings.GradingRatio, $"layout {i}");
            AssertMaxCell(p, g, settings.CellsPerWavelength, $"layout {i}");
        }
    }

    // ── 3. Maximum cell, per slab, from the densest material in it ──────────────────────────────

    [Fact]
    public void Gate3_MaximumCellHolds_TheSubstrateSlabGetsSmallerCellsThanTheAirAboveIt()
    {
        var p = Microstrip();
        var g = FdtdGrid.Build(p, Defaults, Plenty);
        AssertMaxCell(p, g, Defaults.CellsPerWavelength, "microstrip");

        var z = g.Z.Lines;
        double inSub = 0, inAir = 0;
        for (int i = 1; i < z.Count; i++)
        {
            double c = z[i] - z[i - 1];
            if (z[i] <= 254 * Um) inSub = Math.Max(inSub, c);
            else if (z[i - 1] >= 254 * Um && z[i] <= p.Boundary.Max.Z) inAir = Math.Max(inAir, c);
        }
        Assert.True(inSub <= C0 / (20e9 * Math.Sqrt(9.8) * 20) * (1 + 1e-12), $"substrate cell {inSub}");
        Assert.True(inSub < inAir, $"substrate {inSub} vs air {inAir}");
    }

    // ── 4. Merges are reported; fixed lines are never moved ─────────────────────────────────────

    [Fact]
    public void Gate4_TwoEdgesTenNanometresApart_AreMergedAndReported_TwoPortsAreKeptWithAWarning()
    {
        const double gap = 10e-9;
        var box = Box(-2000, 2000, -2000, 2000, 0, 1000, pecFloor: true);
        var p = new Em3dProblem(
            [Substrate(-1500, 1500, -1500, 1500, 254), AirSolid(box)],
            [
                Sheet("left", -500 * Um, 0, -1000 * Um, 1000 * Um, 254 * Um),
                Sheet("right", gap, 500 * Um, -1000 * Um, 1000 * Um, 254 * Um),
            ],
            Materials, [], box, Band, 20);

        var g = FdtdGrid.Build(p, Defaults, Plenty);
        var merge = Assert.Single(g.Merges, m => m.Axis == FdtdAxis.X && Math.Abs(m.DistanceM - gap) < 1e-12);
        Assert.Contains(merge.First.Concat(merge.Second), s => s.Feature == "left");
        Assert.Contains(merge.First.Concat(merge.Second), s => s.Feature == "right");
        Assert.Contains("'left'", merge.Sentence);
        Assert.Contains("'right'", merge.Sentence);
        Assert.Single(g.X.Lines, v => v >= 0 && v <= gap);                // one line, not two
        Assert.Empty(g.Warnings);

        // Two ports whose extents are 10 nm apart: neither moves, both stay, and a warning names both.
        var withPorts = p with
        {
            Ports =
            [
                Port(1, "left", -500 * Um, 0, -1000 * Um, 254 * Um),
                Port(2, "right", gap, 500 * Um, -1000 * Um, 254 * Um),
            ],
        };
        var gp = FdtdGrid.Build(withPorts, Defaults, Plenty);
        Assert.Contains(0.0, gp.X.Lines);
        Assert.Contains(gap, gp.X.Lines);
        var warning = Assert.Single(gp.Warnings);
        Assert.Contains("'port/1'", warning);
        Assert.Contains("'port/2'", warning);
        Assert.Contains("10 nm", warning);
    }

    // ── 5. Locality ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AddingAFarAwayPad_LeavesTheLinesOverTheOriginalStripUnchanged()
    {
        var before = Microstrip(xHalfUm: 4000);
        var after = before with
        {
            Sheets = [.. before.Sheets, Sheet("pad", 2500 * Um, 3500 * Um, -500 * Um, 500 * Um, 254 * Um)],
        };
        var g0 = FdtdGrid.Build(before, Defaults, Plenty);
        var g1 = FdtdGrid.Build(after, Defaults, Plenty);

        // Everything up to the strip's own outermost line on the pad's side: the intervals the pad does
        // not reach, filled from their own two ends alone (R-em3d8-4d). The interval from that line to
        // the pad is the one the edit touches, and it may change.
        double reach = g0.X.Required.Where(l => l.Sources.Any(s => s.Feature == "strip")).Max(l => l.PositionM);
        var over0 = g0.X.Lines.Where(v => v <= reach).ToList();
        var over1 = g1.X.Lines.Where(v => v <= reach).ToList();
        Assert.Equal(over0.Count, over1.Count);
        Assert.True(over0.Zip(over1).All(t => BitConverter.DoubleToInt64Bits(t.First) == BitConverter.DoubleToInt64Bits(t.Second)),
                    "an x-line over the strip moved when a pad 2 mm away was added");
        // The pad is in the strip's own layer, so the z-lines are the same lines.
        Assert.Equal(g0.Z.Lines, g1.Z.Lines);
        Assert.NotEqual(g0.X.Lines.Count, g1.X.Lines.Count);    // and the pad did add lines of its own
    }

    // ── 6. PML ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_AnAbsorbingFaceGetsPmlCellsUniformCells_AndTheBoxGrowsOutwardByExactlyThatSpan()
    {
        var p = Microstrip();
        var g = FdtdGrid.Build(p, Defaults, Plenty);
        int n = Defaults.PmlCells;

        void Uniform(IReadOnlyList<double> cells, string where)
        {
            Assert.Equal(n, cells.Count);
            Assert.All(cells, c => Assert.True(Math.Abs(c / cells[0] - 1) < 1e-9, $"{where}: {c} vs {cells[0]}"));
        }

        var x = g.X.Lines;
        Assert.Equal(p.Boundary.Min.X, x[n]);                  // the face is still a line, exactly
        Assert.Equal(p.Boundary.Max.X, x[^(n + 1)]);
        var low = Enumerable.Range(1, n).Select(i => x[i] - x[i - 1]).ToList();
        var high = Enumerable.Range(x.Count - n, n).Select(i => x[i] - x[i - 1]).ToList();
        Uniform(low, "xmin PML");
        Uniform(high, "xmax PML");
        Assert.Equal(n * low[0], g.X.PmlLowM, 12);             // grown outward by exactly the PML's span
        Assert.Equal(p.Boundary.Min.X - x[0], g.X.PmlLowM);
        Assert.Equal(x[^1] - p.Boundary.Max.X, g.X.PmlHighM);

        // The PEC floor is not absorbing: no PML, the grid starts on the face.
        Assert.Equal(p.Boundary.Min.Z, g.Z.Lines[0]);
        Assert.Equal(0, g.Z.PmlLowM);
        Assert.True(g.Z.PmlHighM > 0);
    }

    // ── 7. Courant ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_TheCourantEstimateMatchesTheFormula_OnAHandBuiltThreeCellGrid()
    {
        double[] x = [0, 10 * Um, 30 * Um, 40 * Um];        // cells 10, 20, 10 µm
        double[] y = [0, 25 * Um, 50 * Um, 60 * Um];        // 25, 25, 10
        double[] z = [0, 5 * Um, 20 * Um, 50 * Um];         // 5, 15, 30
        double expected = 1 / (C0 * Math.Sqrt(1 / Math.Pow(10 * Um, 2) + 1 / Math.Pow(10 * Um, 2) + 1 / Math.Pow(5 * Um, 2)));
        Assert.Equal(expected, FdtdGrid.CourantTimeStep(x, y, z), 15);

        // And a built grid reports exactly that estimate on its own lines.
        var g = FdtdGrid.Build(Microstrip(), Defaults, Plenty);
        Assert.Equal(FdtdGrid.CourantTimeStep(g.X.Lines, g.Y.Lines, g.Z.Lines), g.TimeStepEstimateS);
        Assert.Equal((long)g.X.Lines.Count * g.Y.Lines.Count * g.Z.Lines.Count, g.Cells);
        Assert.Equal((long)Math.Ceiling(g.ExcitationS * (1 + FdtdGrid.RingDownPulses) / g.TimeStepEstimateS), g.Steps);
    }

    // ── 8. Refusal ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_AGridThatWouldNotFit_IsRefused_NamingTheFeatureAndTheSetting()
    {
        var p = Microstrip();
        var g = FdtdGrid.Build(p, Defaults, availableMemoryBytes: 1_000_000);
        Assert.NotNull(g.Refusal);
        var smallest = g.Smallest;
        Assert.Contains($"'{smallest.SmallestCellFeatures[0].Feature}'", g.Refusal);
        Assert.Contains("MinCellUm", g.Refusal);
        Assert.Null(FdtdGrid.Build(p, Defaults, Plenty).Refusal);
    }

    // ── 9. Determinism ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_TwoBuildsAreBitwiseEqual()
    {
        var p = CaseB();
        var a = FdtdGrid.Build(p, Defaults, Plenty);
        var b = FdtdGrid.Build(p, Defaults, Plenty);
        foreach (var axis in new[] { FdtdAxis.X, FdtdAxis.Y, FdtdAxis.Z })
            Assert.Equal(a.Axis(axis).Lines.Select(BitConverter.DoubleToInt64Bits),
                         b.Axis(axis).Lines.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.Merges.Select(m => m.Sentence), b.Merges.Select(m => m.Sentence));
        Assert.Equal(a.Warnings, b.Warnings);
        Assert.Equal((a.Cells, a.Steps, a.TimeStepEstimateS), (b.Cells, b.Steps, b.TimeStepEstimateS));
    }

    // ── 10. Case B ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_CaseB_Builds_AndItsSmallestCellIsSetByTheVia()
    {
        var p = CaseB();
        Assert.Empty(p.Validate());
        var g = FdtdGrid.Build(p, Defaults, Plenty);
        Assert.Null(g.Refusal);
        AssertGraded(g, Defaults.GradingRatio, "case B");
        AssertMaxCell(p, g, Defaults.CellsPerWavelength, "case B");

        var s = g.Smallest;
        output.WriteLine($"case B: {g.X.Lines.Count} x {g.Y.Lines.Count} x {g.Z.Lines.Count} = {g.Cells:N0} cells; " +
                         $"smallest {FdtdGrid.FormatLength(s.SmallestCellM)} on {FdtdGrid.AxisName(s.Axis)}, set by " +
                         string.Join("; ", s.SmallestCellFeatures.Select(f => f.Describe(s.Axis))) +
                         $"; dt {g.TimeStepEstimateS:G4} s, {g.Steps:N0} steps, {g.Merges.Count} merges");
        foreach (var m in g.Merges) output.WriteLine(m.Sentence);

        // A real feature of the via — its barrel, pads or antipad — not a sliver between two rules.
        Assert.Contains(s.SmallestCellFeatures, f => f.Feature.StartsWith("via/1/", StringComparison.Ordinal));
        Assert.True(s.SmallestCellM > 10 * Um, $"smallest cell {s.SmallestCellM}");
        Assert.Empty(g.Warnings);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static readonly Em3dFrequency Band = new(1e9, 20e9, 20, Em3dSweepKind.Linear);

    private static readonly IReadOnlyList<Em3dMaterial> Materials =
    [
        new("Air", 1, null, 0, 1, 0),
        new("Alumina", 9.8, null, 0, 1, 0),
        new("Copper", 1, null, 0, 1, 5.8e7),
    ];

    /// <summary>A 600 µm strip along y on 254 µm of εr 9.8 over a PEC floor, ports at both ends.</summary>
    private static Em3dProblem Microstrip(double xHalfUm = 1500)
    {
        var box = Box(-xHalfUm, xHalfUm, -2000, 2000, 0, 1254, pecFloor: true);
        return new Em3dProblem(
            [Substrate(-xHalfUm, xHalfUm, -2000, 2000, 254), AirSolid(box)],
            [Sheet("strip", -300 * Um, 300 * Um, -2000 * Um, 2000 * Um, 254 * Um)],
            Materials,
            [
                PortY(1, "strip", -300 * Um, 300 * Um, -2000 * Um, 254 * Um),
                PortY(2, "strip", -300 * Um, 300 * Um, +2000 * Um, 254 * Um),
            ],
            box, Band, 20);
    }

    /// <summary>
    /// F0's case B (testdata/em3d/f0/README.md), stated as brief 3's generator states it: copper
    /// lines, pads and ground as solids, the barrel a cylinder, the antipad a hole in the plane with a
    /// dielectric fill, the board and its air.
    /// </summary>
    private static Em3dProblem CaseB()
    {
        var box = Box(-6500, 6500, -4000, 4000, -1500, 2005, pecFloor: false);
        var materials = new List<Em3dMaterial>
        {
            new("Air", 1, null, 0, 1, 0), new("Core", 3.66, null, 0.004, 1, 0), new("Copper", 1, null, 0, 1, 5.8e7),
        };
        Em3dSolid Slab(string name, double z0, double z1, int order) => new(name, "Core", Em3dRole.Dielectric,
            new Em3dBox(new Point3(-5000 * Um, -2500 * Um, z0 * Um), new Point3(5000 * Um, 2500 * Um, z1 * Um)), order);
        Em3dSolid Plate(string name, IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes,
                        double z0, double z1, int order)
            => new(name, "Copper", Em3dRole.Conductor, new Em3dExtrudedPolygon(outline, holes, z0 * Um, z1 * Um), order);

        return new Em3dProblem(
            [
                AirSolid(box) with { Order = 1 },
                Slab("core/lower", 35, 235, 2),
                Slab("core/upper", 270, 470, 3),
                Plate("gnd", Rect(-5000, 5000, -2500, 2500), [Circle(450)], 235, 270, 4),
                new Em3dSolid("via/1/antipad", "Core", Em3dRole.Dielectric,
                    new Em3dCylinder(new Point3(0, 0, 235 * Um), new Point3(0, 0, 270 * Um), 450 * Um), 5),
                Plate("line/top", Rect(-5000, 0, -200, 200), [], 470, 505, 6),
                Plate("line/bottom", Rect(0, 5000, -200, 200), [], 0, 35, 7),
                Plate("via/1/pad/top", Circle(300), [], 470, 505, 8),
                Plate("via/1/pad/bottom", Circle(300), [], 0, 35, 9),
                new Em3dSolid("via/1/barrel", "Copper", Em3dRole.Conductor,
                    new Em3dCylinder(new Point3(0, 0, 0), new Point3(0, 0, 505 * Um), 150 * Um), 10),
            ],
            [],
            materials,
            [
                new Em3dPort(1, "port/1", "line/top", "gnd", new Point3(-5000 * Um, -200 * Um, 270 * Um),
                             new Point3(-5000 * Um, 200 * Um, 470 * Um), new Point3(0, 0, 1), new Complex(50, 0),
                             new Em3dReferencePlane(new Point3(-5000 * Um, 0, 370 * Um), new Point3(1, 0, 0), 0)),
                new Em3dPort(2, "port/2", "line/bottom", "gnd", new Point3(5000 * Um, -200 * Um, 35 * Um),
                             new Point3(5000 * Um, 200 * Um, 235 * Um), new Point3(0, 0, -1), new Complex(50, 0),
                             new Em3dReferencePlane(new Point3(5000 * Um, 0, 135 * Um), new Point3(-1, 0, 0), 0)),
            ],
            box, new Em3dFrequency(0.1e9, 20e9, 200, Em3dSweepKind.Linear), 20);
    }

    /// <summary>A board with one to six random rectangles of metal, snapped to a micrometre grid.</summary>
    private static Em3dProblem RandomManhattan(Random rng)
    {
        double bx = rng.Next(1500, 5000), by = rng.Next(1500, 5000), t = rng.Next(80, 800);
        double er = 2 + 8 * rng.NextDouble();
        var box = Box(-bx - 1000, bx + 1000, -by - 1000, by + 1000, 0, t + 1500, pecFloor: rng.Next(2) == 0);
        var mats = new List<Em3dMaterial> { new("Air", 1, null, 0, 1, 0), new("Sub", er, null, 0, 1, 0), new("Copper", 1, null, 0, 1, 5.8e7) };
        var solids = new List<Em3dSolid>
        {
            AirSolid(box),
            new("sub", "Sub", Em3dRole.Dielectric, new Em3dBox(new Point3(-bx * Um, -by * Um, 0), new Point3(bx * Um, by * Um, t * Um)), 2),
        };
        var sheets = new List<Em3dSheet>();
        int shapes = rng.Next(1, 7);
        for (int k = 0; k < shapes; k++)
        {
            double x0 = rng.Next((int)-bx, (int)bx - 50), y0 = rng.Next((int)-by, (int)by - 50);
            double x1 = Math.Min(bx, x0 + rng.Next(20, 2000)), y1 = Math.Min(by, y0 + rng.Next(20, 2000));
            if (rng.Next(2) == 0)
                sheets.Add(Sheet($"m{k}", x0 * Um, x1 * Um, y0 * Um, y1 * Um, t * Um));
            else
                solids.Add(new Em3dSolid($"m{k}", "Copper", Em3dRole.Conductor,
                    new Em3dExtrudedPolygon(Rect(x0, x1, y0, y1), [], t * Um, (t + 35) * Um), 3 + k));
        }
        return new Em3dProblem(solids, sheets, mats, [], box, Band, 20);
    }

    private static Em3dAirBox Box(double x0, double x1, double y0, double y1, double z0, double z1, bool pecFloor)
        => new(new Point3(x0 * Um, y0 * Um, z0 * Um), new Point3(x1 * Um, y1 * Um, z1 * Um),
               new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                             Em3dBoundaryKind.Absorbing, pecFloor ? Em3dBoundaryKind.Pec : Em3dBoundaryKind.Absorbing,
                             Em3dBoundaryKind.Absorbing));

    private static Em3dSolid AirSolid(Em3dAirBox box)
        => new("air", "Air", Em3dRole.Air, new Em3dBox(box.Min, box.Max), 1);

    private static Em3dSolid Substrate(double x0, double x1, double y0, double y1, double t)
        => new("substrate", "Alumina", Em3dRole.Dielectric,
               new Em3dBox(new Point3(x0 * Um, y0 * Um, 0), new Point3(x1 * Um, y1 * Um, t * Um)), 2);

    private static Em3dSheet Sheet(string name, double x0, double x1, double y0, double y1, double z)
        => new(name, "Copper", [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], [], z, 5 * Um, 10);

    private static IReadOnlyList<Point2> Rect(double x0, double x1, double y0, double y1)
        => [new(x0 * Um, y0 * Um), new(x1 * Um, y0 * Um), new(x1 * Um, y1 * Um), new(x0 * Um, y1 * Um)];

    private static IReadOnlyList<Point2> Circle(double rUm, int n = 32)
        => Enumerable.Range(0, n).Select(k => new Point2(rUm * Um * Math.Cos(2 * Math.PI * k / n),
                                                          rUm * Um * Math.Sin(2 * Math.PI * k / n))).ToList();

    /// <summary>A port sheet across a y-directed strip at y, from the PEC floor up to the strip.</summary>
    private static Em3dPort PortY(int n, string strip, double x0, double x1, double y, double zTop)
        => new(n, $"port/{n}", strip, Em3dAirBox.FaceName("zmin"), new Point3(x0, y, 0), new Point3(x1, y, zTop),
               new Point3(0, 0, 1), new Complex(50, 0), new Em3dReferencePlane(new Point3((x0 + x1) / 2, y, zTop / 2), new Point3(0, 1, 0), 0));

    private static Em3dPort Port(int n, string strip, double x0, double x1, double y, double zTop)
        => PortY(n, strip, x0, x1, y, zTop);

    // ── assertions ──────────────────────────────────────────────────────────────────────────────

    private static void AssertLine(IReadOnlyList<double> lines, double at)
        => Assert.True(lines.Any(v => Math.Abs(v - at) <= 1e-12), $"no line at {at:R}");

    private static void AssertGraded(FdtdGridResult g, double ratio, string what)
    {
        foreach (var axis in new[] { g.X, g.Y, g.Z })
        {
            var l = axis.Lines;
            for (int i = 1; i < l.Count; i++) Assert.True(l[i] > l[i - 1], $"{what} {axis.Axis}: lines not increasing at {i}");
            for (int i = 2; i < l.Count; i++)
            {
                double a = l[i - 1] - l[i - 2], b = l[i] - l[i - 1];
                double q = Math.Max(a / b, b / a);
                Assert.True(q <= ratio + 1e-12, $"{what} {axis.Axis}: cells {a:R} and {b:R} at {l[i - 1]:R} differ by {q:R}");
            }
        }
    }

    /// <summary>Every cell is at most λ/N in the densest non-conductor overlapping it — computed here
    /// from the problem, not from the generator.</summary>
    private static void AssertMaxCell(Em3dProblem p, FdtdGridResult g, double cellsPerWavelength, string what)
    {
        var index = p.Materials.ToDictionary(m => m.Name, m => Math.Sqrt(m.Epsr * m.Mur));
        foreach (var axis in new[] { g.X, g.Y, g.Z })
        {
            var l = axis.Lines;
            for (int i = 1; i < l.Count; i++)
            {
                double n = 1;
                foreach (var s in p.Solids.Where(s => s.Role != Em3dRole.Conductor))
                {
                    var b = Em3dProblem.Bounds(s.Primitive);
                    var (lo, hi) = axis.Axis switch { FdtdAxis.X => (b.X0, b.X1), FdtdAxis.Y => (b.Y0, b.Y1), _ => (b.Z0, b.Z1) };
                    if (lo < l[i] && hi > l[i - 1]) n = Math.Max(n, index[s.Material]);
                }
                double max = C0 / (p.Frequency.StopHz * n * cellsPerWavelength);
                Assert.True(l[i] - l[i - 1] <= max * (1 + 1e-9), $"{what} {axis.Axis}: cell {l[i] - l[i - 1]:R} at {l[i - 1]:R} above {max:R}");
            }
        }
    }
}
