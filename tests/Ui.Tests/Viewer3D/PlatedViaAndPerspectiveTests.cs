using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  Two 3D-view pictures that did not show what the design is: a plated via drew as a solid copper rod under an
//  unbroken pad (its bore is a higher-order air cylinder the solver subtracts and a surface renderer cannot), and
//  Copy as Vector drew a perspective view orthographically.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class PlatedViaAndPerspectiveTests
{
    private const double Mm = 1e-3;

    /// <summary>A pad, a plated barrel through it and the barrel's air bore, in an air box.</summary>
    private static Em3dProblem Via(bool bore)
    {
        var pec = Em3dBoundaryKind.Pec;
        var lo = new Point3(-3 * Mm, -3 * Mm, 0); var hi = new Point3(3 * Mm, 3 * Mm, 2 * Mm);
        Point2[] pad = [new(-1 * Mm, -1 * Mm), new(1 * Mm, -1 * Mm), new(1 * Mm, 1 * Mm), new(-1 * Mm, 1 * Mm)];
        var solids = new List<Em3dSolid>
        {
            new("pad", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(pad, [], 0.5 * Mm, 0.535 * Mm), 1),
            new("via", "cu", Em3dRole.Conductor, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.535 * Mm), 0.15 * Mm), 2),
        };
        if (bore) solids.Add(new("via/fill", "air", Em3dRole.Air, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.535 * Mm), 0.125 * Mm), 3));
        return new Em3dProblem(solids, [], [new Em3dMaterial("air", 1, null, 0, 1, 0), new Em3dMaterial("cu", 1, null, 0, 1, 5.8e7)], [],
                               new Em3dAirBox(lo, hi, new Em3dFaces(pec, pec, pec, pec, pec, pec)),
                               new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    /// <summary>The barrel is drawn as a tube and the pad with a hole the barrel's size; a problem with no bore costs nothing.</summary>
    [Fact]
    public void APlatedBarrel_IsDrawnAsATube_ThroughAHoleInItsPad()
    {
        Assert.Null(Scene3DBores.Of(Via(bore: false)));

        var problem = Via(bore: true);
        var bores = Scene3DBores.Of(problem)!;
        var pad = bores.Carved(problem.Solids[0])!.Value.Make();
        var tube = bores.Carved(problem.Solids[1])!.Value.Make();

        // Nothing of the pad's top covers the bore's centre; its own faces keep their numbers, the bore wall is no face.
        Assert.DoesNotContain(pad.Triangles.Where(t => t.Face == 1), t => Covers(pad, t, 0, 0));
        Assert.Contains(pad.Triangles, t => t.Face == -1);
        // The hole is the BARREL'S outside (0.15 mm), which the tube fills: at the bore's 0.125 mm the pad's hole wall lay on the
        // tube's inner wall and its top on the tube's end ring, and the two speckled at an angle (owner report, 2026-10-25).
        Assert.Contains(pad.Vertices, v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 0.15 * Mm) < 1e-12);
        Assert.DoesNotContain(pad.Vertices, v => Math.Sqrt(v.X * v.X + v.Y * v.Y) < 0.149 * Mm);
        // A pad too narrow for the barrel's outside keeps the bore's hole rather than being drawn over it.
        Point2[] narrow = [new(-0.14 * Mm, -0.14 * Mm), new(0.14 * Mm, -0.14 * Mm), new(0.14 * Mm, 0.14 * Mm), new(-0.14 * Mm, 0.14 * Mm)];
        var small = problem.Solids[0] with { Primitive = new Em3dExtrudedPolygon(narrow, [], 0.5 * Mm, 0.535 * Mm) };
        var smallPad = Scene3DBores.Of(problem with { Solids = [small, .. problem.Solids.Skip(1)] })!.Carved(small)!.Value.Make();
        Assert.Contains(smallPad.Vertices, v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 0.125 * Mm) < 1e-12);
        // A via on the board's edge: the pad's edge runs through the bore's centre, so no hole fits and the barrel's circle is
        // cut out of the pad as a notch — its top no longer lies on the tube's end ring (owner report, 2026-10-25).
        Point2[] edge = [new(-1 * Mm, -1 * Mm), new(1 * Mm, -1 * Mm), new(1 * Mm, 0), new(-1 * Mm, 0)];
        var half = problem.Solids[0] with { Primitive = new Em3dExtrudedPolygon(edge, [], 0.5 * Mm, 0.535 * Mm) };
        var notched = Scene3DBores.Of(problem with { Solids = [half, .. problem.Solids.Skip(1)] })!.Carved(half)!.Value.Make();
        Assert.DoesNotContain(notched.Triangles.Where(t => t.Face == 1), t => Covers(notched, t, 0, -0.14 * Mm));
        Assert.Contains(notched.Triangles.Where(t => t.Face == 1), t => Covers(notched, t, 0, -0.16 * Mm));
        Assert.Contains(notched.Vertices, v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 0.15 * Mm) < 1e-12);
        // Its own four edges keep their faces (2…5), the notch's arc is no face.
        Assert.Equal([-1, 0, 1, 2, 3, 4, 5], notched.Triangles.Select(t => t.Face).Distinct().Order());
        // The barrel has vertices on the bore's radius, and its top is an annulus that does not reach the axis.
        Assert.Contains(tube.Vertices, v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 0.125 * Mm) < 1e-12);
        Assert.DoesNotContain(tube.Vertices, v => Math.Sqrt(v.X * v.X + v.Y * v.Y) < 0.1 * Mm);
    }

    /// <summary>A FILLED via and a conductor box carve the pad they pass right through at their own outline, so the via's cap and
    /// the box's top are the only faces on the pad's plane there: drawn whole, the pad's top lay under both and speckled
    /// (owner report, 2026-11-01). Only a via named as one carves; a problem with none costs nothing.</summary>
    [Fact]
    public void AFilledViaAndAConductorBox_CarveThePadTheyPassThrough()
    {
        var problem = Via(bore: false);
        var strap = new Em3dSolid("strap", "cu", Em3dRole.Conductor, new Em3dBox(new(0.5 * Mm, -0.3 * Mm, 0), new(1.5 * Mm, 0.3 * Mm, 0.535 * Mm)), 3);
        problem = problem with { Solids = [.. problem.Solids, strap] };
        var bores = Scene3DBores.Of(problem, name => name == "via")!;
        var pad = bores.Carved(problem.Solids[0])!.Value.Make();
        var top = pad.Triangles.Where(t => t.Face == 1).ToList();
        Assert.DoesNotContain(top, t => Covers(pad, t, 0, 0));
        Assert.DoesNotContain(top, t => Covers(pad, t, 0.8 * Mm, 0));
        Assert.Contains(top, t => Covers(pad, t, -0.5 * Mm, 0));
        Assert.Null(bores.Carved(problem.Solids[1]));
    }

    /// <summary>A non-plated hole is an air cylinder with no barrel: the substrate it passes through is drawn with the hole,
    /// and a box-shaped slab keeps the box's own face numbers (z max is 5), the hole's wall no face. A plated via carves it
    /// too, at its barrel's outside.</summary>
    [Fact]
    public void ANonPlatedHole_IsDrawnAsAHoleInTheSubstrate_AndABoxSlabKeepsItsFaces()
    {
        var pec = Em3dBoundaryKind.Pec;
        var slab = new Em3dSolid("sub", "fr4", Em3dRole.Dielectric, new Em3dBox(new(-2 * Mm, -2 * Mm, 0), new(2 * Mm, 2 * Mm, 0.5 * Mm)), 1);
        var hole = new Em3dSolid("hole/1", "air", Em3dRole.Air, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.5 * Mm), 0.4 * Mm), 2);
        var problem = new Em3dProblem([slab, hole], [], [new Em3dMaterial("air", 1, null, 0, 1, 0), new Em3dMaterial("fr4", 4.4, null, 0.02, 1, 0)], [],
                                      new Em3dAirBox(new(-3 * Mm, -3 * Mm, -1 * Mm), new(3 * Mm, 3 * Mm, 1 * Mm), new Em3dFaces(pec, pec, pec, pec, pec, pec)),
                                      new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);

        var drawn = Scene3DBores.Of(problem)!.Carved(slab)!.Value.Make();
        Assert.DoesNotContain(drawn.Triangles.Where(t => t.Face == 5), t => Covers(drawn, t, 0, 0));
        Assert.Contains(drawn.Triangles.Where(t => t.Face == 5), t => Covers(drawn, t, 1.5 * Mm, 1.5 * Mm));
        Assert.Contains(drawn.Triangles, t => t.Face == -1);
        Assert.Equal([-1, 0, 1, 2, 3, 4, 5], drawn.Triangles.Select(t => t.Face).Distinct().Order());
        Assert.Equal(Em3dTessellation.Of(slab).Triangles.Select(t => t.Face).Distinct().Order(),
                     drawn.Triangles.Select(t => t.Face).Where(f => f >= 0).Distinct().Order());

        // A plated barrel around the same bore carves the substrate too, at the BARREL'S outside (0.5 mm), which the tube
        // fills — so looking down the bore shows the plating, not the slab's top face across it (owner report, 2026-10-04).
        // At the bore's 0.4 mm the slab's hole wall would be the tube's inner wall drawn a second time.
        var barrel = new Em3dSolid("via/1", "fr4", Em3dRole.Conductor, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.5 * Mm), 0.5 * Mm), 3);
        var plated = problem with { Solids = [slab, hole, barrel] };
        var carved = Scene3DBores.Of(plated)!.Carved(slab)!.Value.Make();
        Assert.DoesNotContain(carved.Triangles.Where(t => t.Face == 5), t => Covers(carved, t, 0.45 * Mm, 0));
        Assert.Contains(carved.Vertices, v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 0.5 * Mm) < 1e-12);
        Assert.DoesNotContain(carved.Vertices, v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 0.4 * Mm) < 1e-12);
    }

    private static bool Covers(Em3dTriangleMesh m, Em3dTriangle t, double x, double y)
    {
        var (a, b, c) = (m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C]);
        double d1 = (x - b.X) * (a.Y - b.Y) - (a.X - b.X) * (y - b.Y);
        double d2 = (x - c.X) * (b.Y - c.Y) - (b.X - c.X) * (y - c.Y);
        double d3 = (x - a.X) * (c.Y - a.Y) - (c.X - a.X) * (y - a.Y);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    /// <summary>
    /// A box seen from the front, from an eye 50 mm away and 20 mm to its right: the near face lies on the focal plane and
    /// lands where the orthographic view puts it (x = ±5), and the right face is seen converging toward the eye's axis —
    /// its far edge at 20 + (5 − 20)·50/70 = 9.286 mm, where the orthographic view shows no right face at all.
    /// </summary>
    [Fact]
    public void APerspectiveCamera_IsDrawnInPerspective_AtTheOrthographicScaleOnItsFocalPlane()
    {
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(
            [new Em3dSolid("b", "Copper", Em3dRole.Conductor, new Em3dBox(new(-5 * Mm, 0, -5 * Mm), new(5 * Mm, 20 * Mm, 5 * Mm)), 0)],
            [], [], [], new Em3dAirBox(new(-50 * Mm, -50 * Mm, -50 * Mm), new(50 * Mm, 50 * Mm, 50 * Mm), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        var front = Em3dProjection.Standard(Em3dStandardView.Front);
        double MaxU(Em3dProjection p) => Em3dSectionScene.Outline(problem, p, new(), out _).Lines.Max(l => Math.Max(l.A.U, l.B.U)) / Mm;
        double MinU(Em3dProjection p) => Em3dSectionScene.Outline(problem, p, new(), out _).Lines.Min(l => Math.Min(l.A.U, l.B.U)) / Mm;

        var perspective = front.WithEye(new Point3(20 * Mm, -50 * Mm, 0), 50 * Mm);
        Assert.Equal(5, MaxU(front), 9);
        Assert.Equal(20 + (5 - 20) * 50.0 / 70, MaxU(perspective), 6);
        Assert.Equal(-5, MinU(perspective), 6);
    }
}
