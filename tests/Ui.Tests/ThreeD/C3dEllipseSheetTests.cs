using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>brief-hzn-13 R-hzn13-2 — the ellipse sheet: its spelling, the polygon every consumer sees, its validation, its part in a
/// sheet boolean, and a radius that follows a variable.</summary>
public sealed class C3dEllipseSheetTests
{
    private const long Mm = 1_000_000;

    private static C3dSheet Ellipse(long ru, long rv, int? segments = null, string name = "pad") => new()
    {
        Name = name, Material = "pec", Plane = C3dPlane.XY, Offset = 0,
        Ellipse = new C3dEllipse { Centre = new C3dPoint2(10 * Mm, 20 * Mm), Radii = new C3dPoint2(ru, rv), Segments = segments },
    };

    [Fact]
    public void AnEllipseSheet_RoundTrips()
    {
        var doc = new C3dDocument { Objects = [Ellipse(2 * Mm, Mm, segments: 36)] };
        string text = C3dPersistence.Serialize(doc);
        Assert.Contains("\"Ellipse\"", text);
        Assert.DoesNotContain("\"Outline\"", text);
        var back = (C3dSheet)C3dPersistence.Deserialize(text).Objects[0];
        Assert.Equal((new C3dPoint2(10 * Mm, 20 * Mm), new C3dPoint2(2 * Mm, Mm), 36), (back.Ellipse!.Centre, back.Ellipse.Radii, back.Ellipse.Segments));
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
    }

    [Fact]
    public void ItsLoweredPolygon_HasSegmentsVerticesOnTheEllipse_FirstAtPlusU_CounterClockwise()
    {
        var lowered = C3dLowering.Lower(Ellipse(2 * Mm, Mm), C3dTransform.Identity, 1000)!;
        var ring = lowered.Sheet!.Outline;
        Assert.Equal(C3dEllipses.DefaultSegments, ring.Count);
        Assert.Equal((12e-3, 20e-3), (ring[0].X, ring[0].Y));
        foreach (var p in ring)
        {
            double x = (p.X - 10e-3) / 2e-3, y = (p.Y - 20e-3) / 1e-3;
            Assert.InRange(x * x + y * y, 1 - 2e-6, 1 + 2e-6);   // on the ellipse, to the DBU
        }
        double area = 0;
        for (int k = 0; k < ring.Count; k++)
        {
            var (a, b) = (ring[k], ring[(k + 1) % ring.Count]);
            area += a.X * b.Y - b.X * a.Y;
        }
        Assert.True(area > 0, "counter-clockwise");
        Assert.InRange(area / 2 / (Math.PI * 2e-3 * 1e-3), 0.998, 0.999);   // 0.13 % under the ellipse at 72 vertices
    }

    [Theory]
    [InlineData(0L, 1_000_000L, null, "radii are not both positive")]
    [InlineData(1_000_000L, 1_000_000L, 4, "Segments is a whole number from 8 to 1024")]
    public void Validation_RefusesABadEllipse(long ru, long rv, int? segments, string why)
    {
        var doc = new C3dDocument { Objects = [Ellipse(ru, rv, segments)] };
        Assert.Contains(C3dValidation.Validate(doc, _ => true), d => d.Render().Contains(why));
    }

    [Fact]
    public void Validation_RefusesASheetWithTwoShapes()
    {
        var s = Ellipse(Mm, Mm);
        s.Rect = new C3dRect { Size = new C3dPoint2(Mm, Mm) };
        var doc = new C3dDocument { Objects = [s] };
        Assert.Contains(C3dValidation.Validate(doc, _ => true), d => d.Render().Contains("has a Rect and an Ellipse; it must have one"));
    }

    [Fact]
    public void APlaneMinusACircle_IsAPlaneWithOneHole()
    {
        var plane = new C3dSheet { Material = "pec", Plane = C3dPlane.XY, Rect = new C3dRect { Size = new C3dPoint2(40 * Mm, 40 * Mm) } };
        var b = new C3dBoolean { Name = "ground", Op = C3dBooleanOp.Subtract, Blank = plane, Tools = [Ellipse(Mm, Mm, name: "antipad")] };
        var piece = Assert.Single(C3dSheetBooleans.Result(b).Pieces);
        var hole = Assert.Single(piece.Holes);
        Assert.Equal(C3dEllipses.DefaultSegments, hole.Count);
    }

    [Fact]
    public void ARadiusBoundToAVariable_FollowsIt()
    {
        var s = Ellipse(0, 0);
        var radii = C3dBindings.SpecOf(typeof(C3dEllipse), nameof(C3dEllipse.Radii))!;
        C3dBindings.SetExpr(s.Ellipse!, radii, 0, new C3dExpr("ra", "Mm"));
        C3dBindings.SetExpr(s.Ellipse!, radii, 1, new C3dExpr("ra", "Mm"));
        var doc = new C3dDocument { Objects = [s], Variables = [new C3dVariable { Name = "ra", Expression = "0.9mm" }] };
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        Assert.Equal(new C3dPoint2(900_000, 900_000), s.Ellipse!.Radii);
        doc.Variables[0].Expression = "1.5mm";
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        Assert.Equal(1_500_000, C3dEllipses.Ring(s.Ellipse).Max(p => p.U) - 10 * Mm);
    }
}
