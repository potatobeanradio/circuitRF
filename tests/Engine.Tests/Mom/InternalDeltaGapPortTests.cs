// The INTERNAL DELTA-GAP PORT — the second port type this kernel builds.
//
// Everything about an internal port that is worth a test is EXACT, and that is not a coincidence:
// the port is the same object an edge port is (a delta gap across the shared edge of two adjacent
// cells, driving the rooftop row that spans it — L8d's D1), cut somewhere else. So the tests here
// are about WHERE the cut lands, WHICH rooftops it drives, and the one thing that genuinely differs
// — that there is no feed outside the cut, therefore no calibration, no error box and no Z_c.
//
// The tolerant assertions are the two solves, and each says why it cannot be an equality.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class InternalDeltaGapPortTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>The same 6 × 3 algebra fixture <see cref="PlanarPortTests"/> uses: unit-ish cells,
    /// gridlines at exact multiples of 1 mm along x, so every coordinate below is checkable by
    /// inspection rather than against another run of the code under test.</summary>
    private static PlanarMesh Slab6x3()
    {
        var gx = new double[7];
        var gy = new double[4];
        for (int i = 0; i < 7; i++) gx[i] = i * 1e-3;
        for (int i = 0; i < 4; i++) gy[i] = i * 0.5e-3;
        return PlanarFillTests.Grid(gx, gy);
    }

    private static PlanarPort Gap(double xM, PlanarPortSide side = PlanarPortSide.MinX, int number = 1)
        => new(number, new EmPoint(xM, 0.75e-3), side, 50.0,
               Kind: PlanarPortKind.InternalDeltaGap);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Where the cut lands
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AGapOnAGridline_CutsExactlyThere_AndDrivesTheWholeTransverseRow()
    {
        var mesh = Slab6x3();
        var p    = PlanarPorts.Resolve(mesh, Gap(3e-3));

        _out.WriteLine(p.Describe());

        Assert.Equal(PlanarPortKind.InternalDeltaGap, p.Kind);
        Assert.False(p.IsDeembeddable);

        // The cut is the gridline that was asked for, to the bit, and nothing moved.
        Assert.Equal(3e-3, p.ReferencePlaneM, 15);
        Assert.Equal(0.0,  p.GapOffsetM,      15);

        // Three cells across the width, so three rooftops — the same row an edge port drives, and
        // the full conductor width, because a gap cuts the whole conductor.
        Assert.Equal(3, p.BasisCount);
        Assert.Equal(1.5e-3, p.WidthM, 15);

        // Every basis pairs column 2 with column 3, i.e. straddles x = 3 mm.
        foreach (int b in p.BasisIndices)
        {
            var bs = mesh.Bases[b];
            Assert.Equal(PlanarBasisDirection.X, bs.Direction);
            Assert.Equal(2, mesh.Cells[bs.CellA].IX);
            Assert.Equal(3, mesh.Cells[bs.CellB].IX);
        }
    }

    [Fact]
    public void AGapBetweenGridlines_SnapsToTheNEARERone_AndSAYSHowFarItMoved()
    {
        var mesh = Slab6x3();

        // 3.4 mm is nearer 3 mm than 4 mm; 3.6 mm is nearer 4 mm. The point of the pair is that the
        // snap is to the nearest gridline rather than, say, always the lower one — an "always
        // rounds down" bug is invisible against a single sample.
        var lo = PlanarPorts.Resolve(mesh, Gap(3.4e-3));
        var hi = PlanarPorts.Resolve(mesh, Gap(3.6e-3));

        Assert.Equal(3e-3, lo.ReferencePlaneM, 15);
        Assert.Equal(4e-3, hi.ReferencePlaneM, 15);

        // And the displacement is REPORTED rather than absorbed. It is bounded by half a cell, and
        // half a cell is a quantity the user sets, so it has to be visible.
        Assert.Equal(0.4e-3, lo.GapOffsetM, 15);
        Assert.Equal(0.4e-3, hi.GapOffsetM, 15);
        Assert.Contains("from where it was placed", lo.Describe());
    }

    [Fact]
    public void TheDirectionSetsTheSIGN_NotWhichEndItIsOn()
    {
        var mesh = Slab6x3();
        var pos  = PlanarPorts.Resolve(mesh, Gap(3e-3, PlanarPortSide.MinX));
        var neg  = PlanarPorts.Resolve(mesh, Gap(3e-3, PlanarPortSide.MaxX));

        // Same cut, same rooftops — only the sign of the port current differs. For an edge port the
        // side names WHICH END; here there is no end, and the side is only the polarity.
        Assert.Equal(pos.ReferencePlaneM, neg.ReferencePlaneM, 15);
        Assert.Equal(pos.BasisIndices, neg.BasisIndices);
        Assert.Equal(+1.0, pos.IncidenceSign);
        Assert.Equal(-1.0, neg.IncidenceSign);
    }

    [Fact]
    public void TheReportSaysItIsNotDeembedded_AndWhyNot()
    {
        string text = PlanarPorts.Resolve(Slab6x3(), Gap(3e-3)).Describe();
        _out.WriteLine(text);

        Assert.Contains("internal delta gap", text);
        Assert.Contains("NOT de-embedded", text);
        Assert.Contains("no feed", text);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The refusals — each one names what is actually wrong, not "unsupported"
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(6e-3, 6e-3)]      // inside the bounding box, in the L's own empty corner
    [InlineData(6e-3, 40e-3)]     // outside the meshed region entirely
    public void AGapOffTheMetal_IsRefusedByName_AndSaysWhyThisDiffersFromAnEdgePort(double x, double y)
    {
        // An L, so that "off the metal" has two genuinely different spellings and both are refused.
        // The second case is the one the index lookup's CLAMP would otherwise swallow: it returns
        // the nearest cell rather than a miss, so a point far outside the artwork lands on the
        // outermost row, finds metal, and cuts a gap nobody asked for.
        //
        // An EDGE port's label may sit just off the end face it names; an internal one cuts metal,
        // so it has to be on the metal, and the refusal says which of the two rules applies.
        var problem = PlanarLineFixtures.Problem(
            GroundedSlab.Fr4Starter, 2e9,
            PlanarLineFixtures.Rect(0, 0, 8e-3, 2.9e-3),
            PlanarLineFixtures.Rect(0, 2.9e-3, 2.9e-3, 8e-3));
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;

        var port = new PlanarPort(1, new EmPoint(x, y), PlanarPortSide.MinX, 50.0,
                                  Kind: PlanarPortKind.InternalDeltaGap);

        Assert.False(PlanarPorts.TryResolve(mesh, port, out _, out string? why));
        _out.WriteLine(why);
        Assert.Contains("internal delta-gap port", why!);
        Assert.Contains("on the metal", why!);
    }

    [Fact]
    public void AConductorWithNoInteriorCut_IsRefused_PointingAtAnEdgePort()
    {
        // One cell along x: there is metal, but no pair of adjacent cells to gap between. That is a
        // conductor END, which is an edge port — and the refusal has to say so rather than reporting
        // a generic failure, because "use the other port type" is the actual remedy.
        var gx = new[] { 0.0, 1e-3 };
        var gy = new[] { 0.0, 0.5e-3, 1e-3 };
        var mesh = PlanarFillTests.Grid(gx, gy);

        Assert.False(PlanarPorts.TryResolve(
            mesh, new PlanarPort(1, new EmPoint(0.5e-3, 0.5e-3), PlanarPortSide.MinX, 50.0,
                                 Kind: PlanarPortKind.InternalDeltaGap),
            out _, out string? why));

        _out.WriteLine(why);
        Assert.Contains("metal on BOTH sides", why!);
        Assert.Contains("edge port", why!);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // What an internal port does NOT get: a feed, a clearance warning, a calibration
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NoUniformFeedIsGrownForAnInternalPort_EvenOnATaperThatWouldGrowOneForAnEdgePort()
    {
        // A taper is exactly the shape R-fed-1 exists for: an EDGE port on either end grows a lead.
        // An internal gap must grow nothing, and the check is Assert.Same rather than a vertex count
        // — "the problem reaches the mesher by reference" is the property that keeps every recorded
        // number reproducible, and a vertex count would drift.
        var problem = PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 1.0e-3, 12e-3, 2e9);
        var (x0, _, x1, _) = problem.Bounds();

        var edge = PlanarLineFixtures.EndPorts(problem);
        Assert.NotSame(problem, PlanarFeedExtension.Extend(problem, edge).Problem);   // it does grow one

        PlanarPort[] gaps =
        [
            new(1, new EmPoint(0.5 * (x0 + x1), 0), PlanarPortSide.MinX, 50.0,
                Kind: PlanarPortKind.InternalDeltaGap),
        ];
        var grown = PlanarFeedExtension.Extend(problem, gaps);
        Assert.Same(problem, grown.Problem);
        Assert.Empty(grown.Leads);
    }

    [Fact]
    public void TheFeedClearanceWarning_SaysNothingAboutAnInternalPort()
    {
        // The warning is about metal inside the length the calibration standard replaces. Nothing is
        // replaced here, so there is no length for it to be about and no action a user could take.
        var mesh = Slab6x3();
        var gap  = PlanarPorts.Resolve(mesh, Gap(3e-3));
        Assert.Null(PlanarPorts.CheckFeedClearance(mesh, gap, requiredM: 1.0));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DeembeddingIsATrueNoOpForAnInternalPort()
    {
        // The claim behind PlanarSolve.IdentityBox: with de-embedding ON, an internal port's answer
        // is the raw answer. Driving the same problem both ways is the direct test of it.
        //
        // The comparison is tolerant rather than an equality because the ON path still passes through
        // PlanarDeembed.Apply's LU — of the identity matrix, so the residual is round-off and nothing
        // else. Asserting bit-identity would be asserting a property of NumFlat's LU rather than of
        // this code.
        var problem = PlanarLineFixtures.Fr4Line(8e-3, 2e9);
        var (x0, _, x1, _) = problem.Bounds();

        PlanarPort[] ports =
        [
            new(1, new EmPoint(0.5 * (x0 + x1), 0), PlanarPortSide.MinX, 50.0,
                Kind: PlanarPortKind.InternalDeltaGap),
        ];

        var report   = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);

        var on  = PlanarSolve.Run(problem, report.Mesh, resolved, [2e9]);
        var off = PlanarSolve.Run(problem, report.Mesh, resolved, [2e9],
                                  PlanarSolveSettings.Default with { Deembed = false });

        var a = on.Points[0].S[0, 0];
        var b = off.Points[0].S[0, 0];
        _out.WriteLine($"de-embedding on: {a}   off: {b}   |Δ| = {(a - b).Magnitude:E3}");
        Assert.True((a - b).Magnitude < 1e-12, $"de-embedding moved an internal port by {(a - b).Magnitude:E3}");

        // And it says so, rather than leaving the user to notice that no calibration was reported.
        Assert.Contains(on.Notes, n => n.Contains("internal delta gaps and are NOT"));
    }

    [Fact]
    public void AGapAtTheCENTREOfASymmetricLine_IsANTIsymmetricAboutItsOwnCut()
    {
        // The structural gate on the whole path: mesh, cut, incidence matrix and solve together.
        //
        // A gap at the exact middle of a uniform line is mirror-symmetric about its own cut, so the
        // two end ports must see the same thing: S₁₁ = S₂₂. The coupling to the gap is the
        // interesting half, and the sign is the part worth stating precisely.
        //
        // **A DELTA GAP IS A SERIES SOURCE, SO S₁₃ = −S₂₃, NOT +.** The excitation pushes current in
        // one direction along the conductor — into the line on one side of the cut and out of it on
        // the other — so the two halves are driven in ANTIPHASE. A shunt port (a current injected
        // against the ground plane) would be symmetric; this one is not, and the difference is a
        // hard π. Asserting the symmetric identity here would have been asserting the wrong port
        // model, and the measurement is what says which: the two numbers came back equal and
        // opposite to sixteen digits.
        //
        // Neither identity survives the gap landing one cell off centre, driving one rooftop row
        // rather than the other, or reading the current back with a side-dependent sign — the class
        // of bug that otherwise produces a complete and plausible s-matrix.
        //
        // Tolerant because the two halves are meshed independently and the answer passes through an
        // LU; the residual is discretisation and round-off, not a modelling difference.
        var problem = PlanarLineFixtures.Fr4Line(8e-3, 2e9);
        var (x0, _, x1, _) = problem.Bounds();
        double xc = 0.5 * (x0 + x1);

        PlanarPort[] ports =
        [
            new(1, new EmPoint(x0, 0), PlanarPortSide.MinX, 50.0),
            new(2, new EmPoint(x1, 0), PlanarPortSide.MaxX, 50.0),
            new(3, new EmPoint(xc, 0), PlanarPortSide.MinX, 50.0, Kind: PlanarPortKind.InternalDeltaGap),
        ];

        var report   = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);
        foreach (var p in resolved) _out.WriteLine(p.Describe());

        // The gap really did land in the middle, to within the half cell the snap can move it.
        Assert.True(resolved[2].GapOffsetM <= 0.5 * resolved[2].BulkCellM + 1e-15);

        var s = PlanarSolve.Run(problem, report.Mesh, resolved, [2e9]).Points[0].S;
        _out.WriteLine($"S13 = {s[0, 2]}   S23 = {s[1, 2]}");
        _out.WriteLine($"S11 = {s[0, 0]}   S22 = {s[1, 1]}");

        double scale = Math.Max(s[0, 2].Magnitude, 1e-12);
        Assert.True((s[0, 2] + s[1, 2]).Magnitude / scale < 1e-6,
                    $"a centred series gap is not antisymmetric: S13 = {s[0, 2]}, S23 = {s[1, 2]}");
        Assert.True((s[0, 0] - s[1, 1]).Magnitude / Math.Max(s[0, 0].Magnitude, 1e-12) < 1e-6,
                    $"a symmetric structure reported S11 != S22: {s[0, 0]} vs {s[1, 1]}");

        // Reciprocity is structural in Y and survives to S through the LU — the same claim
        // PlanarExcitation's header makes, asked of a matrix with a port of each kind in it.
        Assert.True((s[0, 2] - s[2, 0]).Magnitude / scale < 1e-6, "S13 != S31");
    }
}
