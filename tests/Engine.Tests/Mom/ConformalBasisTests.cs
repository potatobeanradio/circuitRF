// Conformal boundary cells — M2: the rooftop over a CUT PAIR.
//
// §3's M2: "The three properties are asserted as EQUALITIES today and must stay equalities … gate it
// on ∫∇·f dS = 0 and on the current across the shared edge being exactly 1 A."
//
// The one property that does NOT survive is pointwise continuity across the shared face, and that is
// MEASURED here rather than asserted away — see RooftopSupport's header for why it cannot survive a
// purely-x̂ basis and what the mismatch is (a zero-net-charge line dipole on an INTERNAL gridline,
// never on the metal's rim).

using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class ConformalBasisTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static PlanarProblem Disc(int points = 96, double radiusM = 1.45e-3, double fHz = 10e9)
    {
        var ring = new EmPoint[points];
        for (int i = 0; i < points; i++)
        {
            double a = 2.0 * Math.PI * i / points;
            ring[i] = new EmPoint(radiusM * Math.Cos(a), radiusM * Math.Sin(a));
        }
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, fHz);
    }

    private static PlanarProblem Taper(double w0 = 2.9e-3, double w1 = 1.0e-3, double len = 10e-3)
        => new([new PlanarConductorLayer("Metal",
                  [new PlanarPolygon([new EmPoint(0, -0.5 * w0), new EmPoint(len, -0.5 * w1),
                                      new EmPoint(len, 0.5 * w1), new EmPoint(0, 0.5 * w0)])],
                  5.8e7, 35e-6)],
                GroundedSlab.Fr4Starter, 10e9);

    private static PlanarMeshSettings Conformal =>
        new(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3,
            BoundaryCells: PlanarBoundaryCells.Conformal);

    public static TheoryData<string> Parts() => new() { "disc", "taper" };
    private static PlanarProblem PartNamed(string n) => n == "disc" ? Disc() : Taper();

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The three properties, as EQUALITIES
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(Parts))]
    public void B1_TheSupportTilesTheCellExactly(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), Conformal).Mesh;
        int cut = 0;
        foreach (var basis in mesh.Bases)
        {
            var (sa, sb) = PlanarBasisFunctions.Supports(mesh, basis);
            var ca = mesh.Cells[basis.CellA];
            var cb = mesh.Cells[basis.CellB];
            if (ca.IsCut || cb.IsCut) cut++;

            // The strips are what everything is integrated over, so if they do not tile the metal
            // exactly then ∇·f does not integrate to ±1 and nothing below it means anything.
            Assert.True(Math.Abs(sa.Area - ca.Area) <= 1e-12 * ca.Area,
                $"{part}: half A's strips cover {sa.Area:E17} against a cell area of {ca.Area:E17}");
            Assert.True(Math.Abs(sb.Area - cb.Area) <= 1e-12 * cb.Area,
                $"{part}: half B's strips cover {sb.Area:E17} against a cell area of {cb.Area:E17}");
        }
        _out.WriteLine($"{part}: {mesh.Bases.Count} bases, {cut} of them with a cut half");
        Assert.True(cut > 0, $"{part}: no basis has a cut half — the fixture proves nothing");
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void B2_TheDivergenceIsExactlyPlusOneMinusOne(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), Conformal).Mesh;
        foreach (var basis in mesh.Bases)
        {
            // ∫∇·f dS over each half is (±1/Area)·Area = ±1 — an identity in the AREA, which is what
            // makes it an equality rather than a quadrature. R-fil-1's own gate, on a cut pair.
            Assert.Equal(0.0, PlanarBasisFunctions.NetCharge(mesh, basis));

            var ca = mesh.Cells[basis.CellA];
            var cb = mesh.Cells[basis.CellB];
            Assert.Equal(+1.0 / ca.Area, PlanarBasisFunctions.Divergence(mesh, basis, ca.CentroidX, ca.CentroidY));
            Assert.Equal(-1.0 / cb.Area, PlanarBasisFunctions.Divergence(mesh, basis, cb.CentroidX, cb.CentroidY));
        }
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void B3_TheCurrentAcrossTheSharedEdgeIsExactlyOneAmp(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), Conformal).Mesh;
        double worst = 0;

        foreach (var basis in mesh.Bases)
        {
            var (sa, sb) = PlanarBasisFunctions.Supports(mesh, basis);
            var ca = mesh.Cells[basis.CellA];
            var cb = mesh.Cells[basis.CellB];

            // ∫ f·û dℓ over the shared face. For each strip the weight is affine in the transverse
            // coordinate, so a 2-point Gauss rule is EXACT — the number is a closed form, not a
            // convergence.
            worst = Math.Max(worst, Math.Abs(FaceCurrent(sa, ca, basis.Direction, true) - 1.0));
            worst = Math.Max(worst, Math.Abs(FaceCurrent(sb, cb, basis.Direction, false) - 1.0));
        }

        _out.WriteLine($"{part}: worst |∫f·û dℓ − 1| across a shared face = {worst:E3}");
        Assert.True(worst < 1e-12,
            $"{part}: a rooftop half carries {1 + worst:F12} A across its shared edge instead of 1 A — " +
            "the normalisation the whole port operator (L8d's D1) rests on");
    }

    /// <summary>The unit current across the support's shared face: Σ over strips of the affine
    /// weight's mean times the strip's face length, divided by the cell area.</summary>
    private static double FaceCurrent(RooftopSupport s, PlanarCell cell,
                                      PlanarBasisDirection dir, bool sharedIsHigh)
    {
        bool alongX = dir == PlanarBasisDirection.X;
        double face  = alongX ? (sharedIsHigh ? cell.XMax : cell.XMin)
                              : (sharedIsHigh ? cell.YMax : cell.YMin);
        double total = 0;

        foreach (var strip in s.Strips)
        {
            // The strip's own extent along the shared face — the two ring vertices that lie on it.
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var v in strip.Ring)
            {
                double onFace = alongX ? v.X : v.Y;
                if (Math.Abs(onFace - face) > 1e-9 * (Math.Abs(face) + 1e-12)) continue;
                double across = alongX ? v.Y : v.X;
                lo = Math.Min(lo, across); hi = Math.Max(hi, across);
            }
            if (double.IsInfinity(lo) || !(hi > lo)) continue;

            // ∫ w dℓ with w affine: the trapezoidal value is exact.
            double wLo = alongX ? strip.At(face, lo) : strip.At(lo, face);
            double wHi = alongX ? strip.At(face, hi) : strip.At(hi, face);
            total += 0.5 * (wLo + wHi) * (hi - lo);
        }
        return total / cell.Area;
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void B4_TheWeightIsZeroOnTheOuterBoundary_RIM_INCLUDED(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), Conformal).Mesh;
        double worst = 0;
        int checkedRims = 0;

        foreach (var basis in mesh.Bases)
        {
            var (sa, sb) = PlanarBasisFunctions.Supports(mesh, basis);
            foreach (var (s, cell, high) in new[] { (sa, mesh.Cells[basis.CellA], true),
                                                    (sb, mesh.Cells[basis.CellB], false) })
            {
                if (!cell.IsCut) continue;
                bool alongX = basis.Direction == PlanarBasisDirection.X;
                double face = alongX ? (high ? cell.XMax : cell.XMin)
                                     : (high ? cell.YMax : cell.YMin);
                double scale = Math.Max(cell.Width, cell.Height);

                foreach (var strip in s.Strips)
                    foreach (var v in strip.Ring)
                    {
                        // Every vertex NOT on the shared face is on the support's outer boundary —
                        // grid line or metal rim, the property does not distinguish them, and that is
                        // exactly the point. The rim is where charge must NOT land.
                        double onFace = alongX ? v.X : v.Y;
                        if (Math.Abs(onFace - face) <= 1e-9 * scale) continue;
                        checkedRims++;
                        worst = Math.Max(worst, Math.Abs(strip.At(v.X, v.Y)) / scale);
                    }
            }
        }

        _out.WriteLine($"{part}: {checkedRims} outer-boundary vertices, worst |w|/cell = {worst:E3}");
        Assert.True(checkedRims > 0, $"{part}: no cut cell's outer boundary was reached");
        Assert.True(worst < 1e-12,
            $"{part}: the rooftop is {worst:E3} of a cell high on the metal's own rim — that is a line " +
            "of charge on the edge of the conductor, which is the property §3 says to check hardest");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The property that does NOT survive, MEASURED
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(Parts))]
    public void B5_PointwiseContinuityAcrossTheSharedFace_IsMeasuredNotAsserted(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), Conformal).Mesh;
        double worstCut = 0, worstWhole = 0;

        foreach (var basis in mesh.Bases)
        {
            var ca = mesh.Cells[basis.CellA];
            var cb = mesh.Cells[basis.CellB];
            bool alongX = basis.Direction == PlanarBasisDirection.X;
            double face = alongX ? ca.XMax : ca.YMax;

            var (sa, sb) = PlanarBasisFunctions.Supports(mesh, basis);
            double jump = 0, mean = 0;
            int samples = 0;

            double lo = alongX ? Math.Max(ca.YMin, cb.YMin) : Math.Max(ca.XMin, cb.XMin);
            double hi = alongX ? Math.Min(ca.YMax, cb.YMax) : Math.Min(ca.XMax, cb.XMax);
            for (int k = 1; k <= 8; k++)
            {
                double t = lo + (hi - lo) * k / 9.0;
                double wa = Eval(sa, ca, alongX, face, t);
                double wb = Eval(sb, cb, alongX, face, t);
                if (wa == 0 && wb == 0) continue;
                jump = Math.Max(jump, Math.Abs(wa - wb));
                mean += 0.5 * (wa + wb);
                samples++;
            }
            if (samples == 0) continue;
            mean /= samples;
            double rel = mean > 0 ? jump / mean : 0;

            if (ca.IsCut || cb.IsCut) worstCut = Math.Max(worstCut, rel);
            else                      worstWhole = Math.Max(worstWhole, rel);
        }

        _out.WriteLine($"{part}: worst relative jump across a shared face — " +
                       $"whole-rectangle pairs {worstWhole:E3}, pairs with a cut half {worstCut:P2}");

        // A whole-rectangle pair is CONTINUOUS and stays so: this is R-cut-2's property seen from the
        // basis rather than from the mesh, and it is what says the cut pairs' jump is the cut's own.
        Assert.True(worstWhole < 1e-12,
            $"{part}: a pair of WHOLE rectangles is no longer continuous ({worstWhole:E3}) — the cut " +
            "path has leaked into the case it must not touch");
    }

    private static double Eval(RooftopSupport s, PlanarCell cell, bool alongX, double face, double t)
    {
        double x = alongX ? face : t;
        double y = alongX ? t : face;
        foreach (var strip in s.Strips)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var v in strip.Ring)
            {
                double onFace = alongX ? v.X : v.Y;
                if (Math.Abs(onFace - face) > 1e-9 * Math.Max(cell.Width, cell.Height)) continue;
                double across = alongX ? v.Y : v.X;
                lo = Math.Min(lo, across); hi = Math.Max(hi, across);
            }
            if (t >= lo && t <= hi) return Math.Max(strip.At(x, y), 0.0) / cell.Area;
        }
        return 0.0;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cut-4 — the faces that carry NO basis
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(Parts))]
    public void B6_EveryBasisTheMesherEmittedIsAnchoredOnBothSides(string part)
    {
        var report = SurfaceMesher.Mesh(PartNamed(part), Conformal);
        foreach (var basis in report.Mesh.Bases)
        {
            var (sa, sb) = PlanarBasisFunctions.Supports(report.Mesh, basis);
            Assert.True(sa.Anchored && sb.Anchored,
                $"{part}: the mesher emitted a basis across a face one of its halves is not swept by");
            Assert.True(sa.SharedFaceLength > 0 && sb.SharedFaceLength > 0,
                $"{part}: the mesher emitted a basis across a face that carries no metal");
        }
        foreach (var n in report.Notes.Where(n => n.Contains("NO basis function")))
            _out.WriteLine($"[{part}] {n}");
    }
}
