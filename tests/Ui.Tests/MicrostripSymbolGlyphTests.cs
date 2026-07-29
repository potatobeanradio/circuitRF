using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>Owner-reported (round 1): give MLIN/MBend/MTee/MCross symbols thickness like TLIN (no
/// fill), and draw MBend as a real 90° bend with pin 2 pointing down.
/// Owner-reported (round 2): MBend/MTee/MCross must not draw overlapping/intersecting lines — each
/// body is now ONE unfilled outline polygon (the union of its arms), not multiple independently-
/// stroked RoundedRects sharing a corner region. MTee/MCross's filled center dot is removed. MTee's
/// port 3 now points down, not up.</summary>
public class MicrostripSymbolGlyphTests
{
    [Fact]
    public void Mlin_HasAtLeastOneUnfilledRoundedRectBody()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Mlin);
        var bodies = sym.Primitives.OfType<RoundedRectPrimitive>().ToList();
        Assert.NotEmpty(bodies);
        Assert.All(bodies, b => Assert.False(b.Filled));
    }

    [Theory]
    [InlineData(SymbolKind.MBend)]
    [InlineData(SymbolKind.MTee)]
    [InlineData(SymbolKind.MCross)]
    public void BentSymbols_HaveExactlyOneUnfilledPolygonBody_NoRoundedRects(SymbolKind kind)
    {
        // Two independently-stroked RoundedRects sharing a corner region draw crossing/overlapping
        // lines there — the union-outline polygon is the fix, so no RoundedRectPrimitive should
        // remain and exactly one (unfilled) PolygonPrimitive should carry the whole body.
        var sym = BuiltInSymbols.Primitives(kind);
        Assert.Empty(sym.Primitives.OfType<RoundedRectPrimitive>());
        var polys = sym.Primitives.OfType<PolygonPrimitive>().ToList();
        Assert.Single(polys);
        Assert.False(polys[0].Filled);
    }

    [Theory]
    [InlineData(SymbolKind.Mlin)]
    [InlineData(SymbolKind.MBend)]
    [InlineData(SymbolKind.MTee)]
    [InlineData(SymbolKind.MCross)]
    public void Symbol_HasNoFilledPolygonBody(SymbolKind kind)
    {
        // The original MLIN glyph had a filled Poly "trace body" — the fill bug this test guards.
        var sym = BuiltInSymbols.Primitives(kind);
        var filledPolys = sym.Primitives.OfType<PolygonPrimitive>().Where(p => p.Filled);
        Assert.Empty(filledPolys);
    }

    [Theory]
    [InlineData(SymbolKind.MTee)]
    [InlineData(SymbolKind.MCross)]
    public void MTeeAndMCross_HaveNoCircle_NoCenterDot(SymbolKind kind)
    {
        var sym = BuiltInSymbols.Primitives(kind);
        Assert.Empty(sym.Primitives.OfType<CirclePrimitive>());
    }

    [Fact]
    public void MBend_Pin2_PointsDown_NotRight()
    {
        var ports = SymbolPortDefs.For(SymbolKind.MBend);
        Assert.Equal(2, ports.Length);
        Assert.Equal(-200f, ports[0].LocalX);
        Assert.Equal(0f, ports[0].LocalY);
        Assert.Equal(0f, ports[1].LocalX);
        Assert.Equal(200f, ports[1].LocalY); // down, not (200,0)
    }

    [Fact]
    public void MTee_Port3_PointsDown_NotUp()
    {
        var ports = SymbolPortDefs.For(SymbolKind.MTee);
        Assert.Equal(3, ports.Length);
        Assert.Equal(0f, ports[2].LocalX);
        Assert.Equal(200f, ports[2].LocalY); // down, not (-200)
    }

    [Fact]
    public void MBend_PolygonOutline_SpansBothArms_ARealBend()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.MBend);
        var poly = sym.Primitives.OfType<PolygonPrimitive>().Single();
        double minX = poly.Points.Min(p => p[0]), maxX = poly.Points.Max(p => p[0]);
        double minY = poly.Points.Min(p => p[1]), maxY = poly.Points.Max(p => p[1]);
        // Horizontal arm reaches toward pin 1 (negative X); vertical arm reaches toward pin 2 (positive Y).
        Assert.True(minX < -100);
        Assert.True(maxY > 100);
    }

    // ── MKlopf: owner-reported — the right side of the body outline was not closed ────────────

    private static (double X, double Y) StartOf(SymbolPrimitive p) => p switch
    {
        LinePrimitive l      => (l.X1, l.Y1),
        QuadCurvePrimitive q => (q.P0X, q.P0Y),
        _ => throw new System.ArgumentException($"Unsupported primitive kind: {p.GetType().Name}"),
    };

    private static (double X, double Y) EndOf(SymbolPrimitive p) => p switch
    {
        LinePrimitive l      => (l.X2, l.Y2),
        QuadCurvePrimitive q => (q.P2X, q.P2Y),
        _ => throw new System.ArgumentException($"Unsupported primitive kind: {p.GetType().Name}"),
    };

    [Fact]
    public void Mklopf_BodyOutline_IsOneContinuousClosedLoop_BothSidesClosed()
    {
        // Regression for the owner-reported bug: the body outline's RIGHT side (where the top and
        // bottom quad curves meet, at x=+90) had no connecting segment — the left side (x=-90) did.
        // A general continuity check (each segment's end == the next segment's start, all the way
        // around, including the wraparound back to the first segment) catches a missing edge on
        // EITHER side, not just the one already found.
        var sym = BuiltInSymbols.Primitives(SymbolKind.Mklopf);

        // The two lead-in/lead-out stub lines (pin 1 -> body, body -> pin 2) are not part of the
        // closed body loop itself — only the primitives after them are.
        var leadCount = sym.Primitives.Count(p => p is LinePrimitive l && System.Math.Abs(l.Y1) < 1 && System.Math.Abs(l.Y2) < 1);
        var body = sym.Primitives.Skip(leadCount).ToList();

        Assert.True(body.Count >= 3, "Expected at least 3 primitives forming the MKlopf body outline.");

        for (int i = 0; i < body.Count; i++)
        {
            var end = EndOf(body[i]);
            var nextStart = StartOf(body[(i + 1) % body.Count]);
            Assert.True(
                System.Math.Abs(end.X - nextStart.X) < 0.01 && System.Math.Abs(end.Y - nextStart.Y) < 0.01,
                $"Body outline is not closed between primitive {i} (ends at {end}) and primitive {(i + 1) % body.Count} (starts at {nextStart}).");
        }
    }

    [Fact]
    public void Mklopf_BodyOutline_RightSide_HasAConnectingSegment_AtXEqualsNinety()
    {
        // Directly pins the fix: a segment whose two endpoints both sit at x=+90 (the right edge),
        // spanning the gap between the top curve's end (90,-20) and the bottom curve's start (90,20).
        var sym = BuiltInSymbols.Primitives(SymbolKind.Mklopf);
        bool hasRightClosingSegment = sym.Primitives.Any(p =>
        {
            var (sx, sy) = StartOf(p);
            var (ex, ey) = EndOf(p);
            return System.Math.Abs(sx - 90) < 0.01 && System.Math.Abs(ex - 90) < 0.01 && System.Math.Abs(sy - ey) > 1;
        });
        Assert.True(hasRightClosingSegment, "No segment closes the right side of the MKlopf body outline at x=90.");
    }
}
