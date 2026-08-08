namespace CircuitRF.WBond.Tests;

/// <summary>
/// Tier 6 of brief-wbond-wbc §3 — selection resolution and marquee semantics (R-wbc-1).
/// </summary>
public class SelectionResolverTests
{
    /// <summary>Three arrays of three wires, laid out so a marquee can catch them selectively.</summary>
    private static WireMesh Mesh()
    {
        var design = new WBondDesign();
        for (int a = 0; a < 3; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < 3; w++)
            {
                double y = a * 100 + w * 6;
                array.Wires.Add(new Wire
                {
                    Points =
                    {
                        Point3.Mils(0, y, 4),
                        Point3.Mils(50, y, 24),
                        Point3.Mils(100, y, 1),
                    },
                });
            }
            design.Arrays.Add(array);
        }
        return WireMesh.Build(design);
    }

    [Fact]
    public void Element_SelectsOnlyTheThingHit()
    {
        var selection = SelectionResolver.Resolve(Mesh(), wire: 4, point: 1, isSegment: false, SelectionScope.Element);

        Assert.Empty(selection.Wires);
        Assert.Equal([new PointRef(4, 1)], selection.Points);
        Assert.Empty(selection.Segments);
    }

    /// <summary>Double-click, or `w` held, promotes to the whole wire.</summary>
    [Fact]
    public void WireScope_PromotesToTheWholeWire()
    {
        var selection = SelectionResolver.Resolve(Mesh(), wire: 4, point: 1, isSegment: false, SelectionScope.Wire);

        Assert.Equal([4], selection.Wires);
        Assert.Empty(selection.Points);
    }

    /// <summary>Triple-click, or `g` held, promotes to every wire in that wire's ARRAY.</summary>
    [Fact]
    public void ArrayScope_PromotesToEveryWireInTheArray()
    {
        var mesh = Mesh();
        var selection = SelectionResolver.Resolve(mesh, wire: 4, point: 1, isSegment: false, SelectionScope.Array);

        // Wire 4 is in array 1 (wires 3, 4, 5).
        Assert.Equal([3, 4, 5], selection.Wires.OrderBy(w => w));

        foreach (int w in selection.Wires)
            Assert.Equal(mesh.ArrayOfWire[4], mesh.ArrayOfWire[w]);
    }

    // ---------------------------------------------------------------- the marquee asymmetry

    /// <summary>
    /// TIER 6 — <b>right → left catches the WHOLE wire for any wire with a point inside</b>, while
    /// left → right does not.
    ///
    /// <para>That asymmetry is the entire behavioural difference between the two directions and it is
    /// the part that is easy to get subtly wrong: an implementation returning the enclosed points for
    /// both passes any test that only counts how many things were selected. So this asserts the
    /// KIND of what came back, not just the quantity.</para>
    /// </summary>
    [Fact]
    public void Tier6_RightToLeftPromotesToWholeWires_LeftToRightDoesNot()
    {
        var mesh = Mesh();

        // A box covering only the LEFT part of array 0's wires — their first point, not their last.
        long minX = WBondUnits.ToNm(-10, WBondUnit.Mil), maxX = WBondUnits.ToNm(60, WBondUnit.Mil);
        long minY = WBondUnits.ToNm(-10, WBondUnit.Mil), maxY = WBondUnits.ToNm(20, WBondUnit.Mil);

        var crossing = SelectionResolver.ResolveMarquee(
            mesh, minX, minY, maxX, maxY, MarqueeDirection.RightToLeft);

        var enclose = SelectionResolver.ResolveMarquee(
            mesh, minX, minY, maxX, maxY, MarqueeDirection.LeftToRight);

        // Crossing: whole wires, no loose points.
        Assert.Equal([0, 1, 2], crossing.Wires.OrderBy(w => w));
        Assert.Empty(crossing.Points);

        // Enclose: the wires are only partly inside, so it must NOT promote them.
        Assert.Empty(enclose.Wires);
        Assert.NotEmpty(enclose.Points);
        Assert.All(enclose.Points, p => Assert.InRange(p.Wire, 0, 2));
    }

    /// <summary>A wire lying entirely inside is caught whole by BOTH directions.</summary>
    [Fact]
    public void Tier6_AFullyEnclosedWire_IsCaughtWholeByEitherDirection()
    {
        var mesh = Mesh();

        long minX = WBondUnits.ToNm(-10, WBondUnit.Mil), maxX = WBondUnits.ToNm(110, WBondUnit.Mil);
        long minY = WBondUnits.ToNm(-10, WBondUnit.Mil), maxY = WBondUnits.ToNm(20, WBondUnit.Mil);

        foreach (var direction in new[] { MarqueeDirection.LeftToRight, MarqueeDirection.RightToLeft })
        {
            var selection = SelectionResolver.ResolveMarquee(mesh, minX, minY, maxX, maxY, direction);
            Assert.Equal([0, 1, 2], selection.Wires.OrderBy(w => w));
            Assert.Empty(selection.Points);
        }
    }

    /// <summary>An empty box selects nothing, whichever way it was dragged.</summary>
    [Fact]
    public void Tier6_AnEmptyBox_SelectsNothing()
    {
        var mesh = Mesh();
        long far = WBondUnits.ToNm(100_000, WBondUnit.Mil);

        foreach (var direction in new[] { MarqueeDirection.LeftToRight, MarqueeDirection.RightToLeft })
            Assert.True(SelectionResolver.ResolveMarquee(mesh, far, far, far * 2, far * 2, direction).IsEmpty);
    }

    /// <summary>The box is normalised, so a marquee dragged in any corner order works.</summary>
    [Fact]
    public void Tier6_TheBoxIsNormalised()
    {
        var mesh = Mesh();
        long lo = WBondUnits.ToNm(-10, WBondUnit.Mil), hi = WBondUnits.ToNm(110, WBondUnit.Mil);
        long yLo = WBondUnits.ToNm(-10, WBondUnit.Mil), yHi = WBondUnits.ToNm(20, WBondUnit.Mil);

        var forward = SelectionResolver.ResolveMarquee(mesh, lo, yLo, hi, yHi, MarqueeDirection.RightToLeft);
        var reversed = SelectionResolver.ResolveMarquee(mesh, hi, yHi, lo, yLo, MarqueeDirection.RightToLeft);

        Assert.Equal(forward.Wires.OrderBy(w => w), reversed.Wires.OrderBy(w => w));
    }

    /// <summary>The profile view marquee tests span and z, not x and y.</summary>
    [Fact]
    public void Tier6_TheProfileViewMarquee_TestsZNotY()
    {
        var mesh = Mesh();

        // A band covering only the high part of every wire — its apex, at z = 24 mil.
        long minX = WBondUnits.ToNm(-10, WBondUnit.Mil), maxX = WBondUnits.ToNm(110, WBondUnit.Mil);
        long minZ = WBondUnits.ToNm(20, WBondUnit.Mil), maxZ = WBondUnits.ToNm(30, WBondUnit.Mil);

        var selection = SelectionResolver.ResolveMarquee(
            mesh, minX, minZ, maxX, maxZ, MarqueeDirection.LeftToRight, EditorView.Profile);

        // Every wire's apex is index 1, and only that point is in the z band.
        Assert.Empty(selection.Wires);
        Assert.All(selection.Points, p => Assert.Equal(1, p.Point));
        Assert.Equal(mesh.WireCount, selection.Points.Count);
    }

    // ---------------------------------------------------------------- union and moving points

    /// <summary>Shift-click adds, and a whole-wire selection subsumes its own loose points.</summary>
    [Fact]
    public void Union_AWholeWireSubsumesItsOwnPointsAndSegments()
    {
        var points = new WireSelection { Points = { new PointRef(2, 0) }, Segments = { new SegmentRef(2, 1) } };
        var whole = new WireSelection { Wires = { 2 } };

        var merged = SelectionResolver.Union(points, whole);

        Assert.Equal([2], merged.Wires);
        Assert.Empty(merged.Points);
        Assert.Empty(merged.Segments);
    }

    /// <summary>Union leaves the inputs alone — a selection gesture must not mutate the previous one.</summary>
    [Fact]
    public void Union_DoesNotMutateItsInputs()
    {
        var a = new WireSelection { Wires = { 1 } };
        var b = new WireSelection { Wires = { 2 } };

        var merged = SelectionResolver.Union(a, b);

        Assert.Equal([1], a.Wires);
        Assert.Equal([2], b.Wires);
        Assert.Equal(2, merged.Wires.Count);
    }

    /// <summary>
    /// A selected SEGMENT contributes both its endpoints to a move — which is what keeps a dragged
    /// segment attached at both ends by construction rather than by a constraint (§6.3).
    /// </summary>
    [Fact]
    public void MovingPoints_ASegmentCarriesBothItsEndpoints()
    {
        var selection = new WireSelection { Segments = { new SegmentRef(0, 1) } };
        var moving = selection.MovingPoints(0, pointCount: 3);

        Assert.Equal([1, 2], moving.OrderBy(i => i));
    }

    /// <summary>A wholly-selected wire moves every point.</summary>
    [Fact]
    public void MovingPoints_AWholeWireMovesEveryPoint()
    {
        var selection = new WireSelection { Wires = { 0 } };
        Assert.Equal([0, 1, 2, 3], selection.MovingPoints(0, pointCount: 4).OrderBy(i => i));
    }

    /// <summary>The last segment of a wire does not invent a point past the end.</summary>
    [Fact]
    public void MovingPoints_TheLastSegmentDoesNotRunPastTheEnd()
    {
        var selection = new WireSelection { Segments = { new SegmentRef(0, 2) } };
        Assert.Equal([2], selection.MovingPoints(0, pointCount: 3).OrderBy(i => i));
    }

    /// <summary>TouchedWires spans wires reached wholly, by point and by segment.</summary>
    [Fact]
    public void TouchedWires_SpansEveryWayAWireCanBeReached()
    {
        var selection = new WireSelection
        {
            Wires = { 0 },
            Points = { new PointRef(3, 1) },
            Segments = { new SegmentRef(7, 0) },
        };

        Assert.Equal([0, 3, 7], selection.TouchedWires().OrderBy(w => w));
    }
}
