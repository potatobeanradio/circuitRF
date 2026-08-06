using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using Clipper2Lib;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// DRC v2's operand half (docs/design/layout-view.md §9A.5): a rule measures a layer EXPRESSION,
/// not a bare layer. These cover the text syntax and the region algebra behind it — the checks
/// themselves are in <see cref="DrcTwoRegionCheckTests"/>.
/// </summary>
public class DrcLayerExprParserTests
{
    [Theory]
    [InlineData("1/0")]
    [InlineData("and(1/0, 2/0)")]
    [InlineData("not(1/0, 2/0)")]
    [InlineData("or(1/0, 2/0)")]
    [InlineData("xor(1/0, 2/0)")]
    [InlineData("sized(1/0, 100)")]
    [InlineData("sized(1/0, -50)")]
    [InlineData("holes(1/0)")]
    [InlineData("merged(1/0)")]
    [InlineData("interacting(1/0, 2/0)")]
    [InlineData("not_interacting(1/0, 2/0)")]
    [InlineData("inside(1/0, 2/0)")]
    [InlineData("outside(1/0, 2/0)")]
    [InlineData("covering(1/0, 2/0)")]
    [InlineData("not_covering(1/0, 2/0)")]
    [InlineData("and(1/0, not(2/0, sized(3/5, 25)))")]
    public void EveryFormOfTheSyntax_RoundTripsExactly(string text)
    {
        Assert.True(DrcLayerExprParser.TryParse(text, out var expr, out string? err), err);
        Assert.NotNull(expr);
        Assert.Equal(text, DrcLayerExprParser.Format(expr!));
    }

    /// <summary>
    /// Whitespace is insignificant, but the CANONICAL form is what gets written back to the
    /// `.ctech` — so a hand-edited file re-serializes to one stable spelling rather than preserving
    /// whatever spacing the author happened to type.
    /// </summary>
    [Fact]
    public void Whitespace_IsAccepted_AndNormalisedOnFormat()
    {
        Assert.True(DrcLayerExprParser.TryParse("  and( 1/0 ,   not( 2/0,3/0 ) )  ", out var expr, out _));
        Assert.Equal("and(1/0, not(2/0, 3/0))", DrcLayerExprParser.Format(expr!));
    }

    /// <summary>
    /// The one genuinely ambiguous token in the grammar. `sized(1/0, 100)` has a layer operand
    /// followed by an integer; `and(1/0, 2/0)` has two layer operands. Both start their second
    /// argument with digits, and only the `/` distinguishes them — so the scanner has to look past
    /// the whole digit run before deciding.
    /// </summary>
    [Fact]
    public void AnIntegerArgument_IsNotConfusedWithALayerLeaf()
    {
        var sized = DrcLayerExprParser.Parse("sized(1/0, 100)");
        Assert.Equal(100, Assert.IsType<DrcLayerExpr.Sized>(sized).ByDbu);

        var and = DrcLayerExprParser.Parse("and(1/0, 100/0)");
        var leaf = Assert.IsType<DrcLayerExpr.Layer>(Assert.IsType<DrcLayerExpr.And>(and).B);
        Assert.Equal(new LayerKey(100, 0), leaf.Key);
    }

    /// <summary>
    /// A malformed expression must degrade to "this rule is unusable, here is why" — never to a
    /// thrown exception that takes the whole technology load with it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("and(1/0)")]                 // arity
    [InlineData("and(1/0, 2/0, 3/0)")]       // arity
    [InlineData("not(1/0)")]                 // difference needs two operands
    [InlineData("sized(1/0)")]               // missing distance
    [InlineData("frobnicate(1/0, 2/0)")]     // unknown operation
    [InlineData("and(1/0, 2/0")]             // unterminated
    [InlineData("1/0 and 2/0")]              // infix is not the grammar
    [InlineData("1/")]                       // truncated leaf
    [InlineData("and(1/0, 2/0) extra")]      // trailing junk
    public void MalformedInput_ReturnsFalseWithAReason_NeverThrows(string text)
    {
        Assert.False(DrcLayerExprParser.TryParse(text, out var expr, out string? error));
        Assert.Null(expr);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// `not` is a DIFFERENCE, not a complement — the error says so, because "not" reads as unary to
    /// anyone who has not read the grammar and the complement of a region is unbounded.
    /// </summary>
    [Fact]
    public void NotWithOneOperand_ExplainsThatItIsADifference()
    {
        Assert.False(DrcLayerExprParser.TryParse("not(1/0)", out _, out string? error));
        Assert.Contains("difference", error!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferencedLayers_ReportsEveryLeaf_Deduplicated()
    {
        var expr = DrcLayerExprParser.Parse("and(1/0, not(2/0, and(1/0, 3/7)))");
        var keys = expr.ReferencedLayers().OrderBy(k => k.Layer).ThenBy(k => k.Datatype).ToList();

        Assert.Equal([new LayerKey(1, 0), new LayerKey(2, 0), new LayerKey(3, 7)], keys);
    }
}

/// <summary>The region algebra itself, driven directly against hand-built geometry.</summary>
public class DrcRegionEvalTests
{
    private static readonly LayerKey A = new(1, 0);
    private static readonly LayerKey B = new(2, 0);

    private static Paths64 Rect(long x1, long y1, long x2, long y2) =>
        [[new Point64(x1, y1), new Point64(x2, y1), new Point64(x2, y2), new Point64(x1, y2)]];

    private static Paths64 Union(params Paths64[] parts)
    {
        var all = new Paths64();
        foreach (var p in parts) all.AddRange(p);
        return DrcRegions.Union(all);
    }

    private static DrcRegionEval Eval(Paths64? a = null, Paths64? b = null)
    {
        var map = new Dictionary<LayerKey, Paths64>();
        if (a is not null) map[A] = a;
        if (b is not null) map[B] = b;
        return new DrcRegionEval(map);
    }

    private static double Area(Paths64 p)
    {
        double sum = 0;
        foreach (var path in p) sum += Clipper.Area(path);
        return System.Math.Abs(sum);
    }

    [Fact]
    public void And_IsTheIntersection()
    {
        var eval = Eval(Rect(0, 0, 100, 100), Rect(50, 0, 150, 100));
        Assert.Equal(50.0 * 100.0, Area(eval.Evaluate(DrcLayerExprParser.Parse("and(1/0, 2/0)"))), 1);
    }

    [Fact]
    public void Not_IsADifference_InTheStatedDirection()
    {
        var eval = Eval(Rect(0, 0, 100, 100), Rect(50, 0, 150, 100));

        // A minus B keeps the left half; the reverse keeps the right half. Getting the direction
        // backwards produces an equal-area result here, so the test asserts WHERE, not how much.
        var ab = eval.Evaluate(DrcLayerExprParser.Parse("not(1/0, 2/0)"));
        Assert.Equal(50.0 * 100.0, Area(ab), 1);
        Assert.Equal(0, DrcRegions.BoundsOf(ab).MinX);
        Assert.Equal(50, DrcRegions.BoundsOf(ab).MaxX);

        var ba = eval.Evaluate(DrcLayerExprParser.Parse("not(2/0, 1/0)"));
        Assert.Equal(100, DrcRegions.BoundsOf(ba).MinX);
        Assert.Equal(150, DrcRegions.BoundsOf(ba).MaxX);
    }

    [Fact]
    public void Sized_GrowsAndShrinks_AndAShrinkThatConsumesTheRegionYieldsNothing()
    {
        var eval = Eval(Rect(0, 0, 100, 100));

        Assert.Equal(120.0 * 120.0, Area(eval.Evaluate(DrcLayerExprParser.Parse("sized(1/0, 10)"))), 1);
        Assert.Equal(80.0 * 80.0, Area(eval.Evaluate(DrcLayerExprParser.Parse("sized(1/0, -10)"))), 1);
        Assert.Empty(eval.Evaluate(DrcLayerExprParser.Parse("sized(1/0, -60)")));
    }

    /// <summary>
    /// The distinction that makes `interacting` worth having at all: it keeps WHOLE polygons, where
    /// `and` keeps only the overlapping area. A implementation that returned partial area would
    /// look plausible on screen and measure differently.
    /// </summary>
    [Fact]
    public void Interacting_KeepsWholePolygons_WhereAndKeepsOnlyTheOverlap()
    {
        var a = Union(Rect(0, 0, 100, 100), Rect(500, 0, 600, 100));   // two separate squares
        var b = Rect(50, 50, 550, 60);                                  // a bar crossing only the first
        var eval = Eval(a, b);

        var interacting = eval.Evaluate(DrcLayerExprParser.Parse("interacting(1/0, 2/0)"));
        var anded = eval.Evaluate(DrcLayerExprParser.Parse("and(1/0, 2/0)"));

        // The bar reaches x=550, so it crosses BOTH squares — interacting keeps both whole.
        Assert.Equal(2 * 100.0 * 100.0, Area(interacting), 1);
        Assert.True(Area(anded) < Area(interacting) / 10, "and() must keep only the overlapping sliver");
    }

    [Fact]
    public void NotInteracting_IsTheComplementWithinA()
    {
        var a = Union(Rect(0, 0, 100, 100), Rect(500, 0, 600, 100));
        var b = Rect(50, 50, 60, 60);                                   // touches only the first square
        var eval = Eval(a, b);

        var kept = eval.Evaluate(DrcLayerExprParser.Parse("not_interacting(1/0, 2/0)"));
        Assert.Equal(100.0 * 100.0, Area(kept), 1);
        Assert.Equal(500, DrcRegions.BoundsOf(kept).MinX);
    }

    /// <summary>
    /// Two regions sharing only an EDGE overlap in zero area, so a strict area test reports them as
    /// not interacting — the opposite of what a deck means. The one-DBU dilation is what makes edge
    /// contact register, and this is the case that would silently regress if it were removed.
    /// </summary>
    [Fact]
    public void Interacting_CountsEdgeContact_NotJustOverlap()
    {
        var eval = Eval(Rect(0, 0, 100, 100), Rect(100, 0, 200, 100));  // abutting, zero overlap
        Assert.NotEmpty(eval.Evaluate(DrcLayerExprParser.Parse("interacting(1/0, 2/0)")));
    }

    [Fact]
    public void Inside_KeepsOnlyFullyContainedPolygons()
    {
        var a = Union(Rect(10, 10, 20, 20), Rect(500, 10, 520, 20));    // one inside B, one far away
        var b = Rect(0, 0, 100, 100);
        var eval = Eval(a, b);

        var kept = eval.Evaluate(DrcLayerExprParser.Parse("inside(1/0, 2/0)"));
        Assert.Equal(10.0 * 10.0, Area(kept), 1);
        Assert.Equal(10, DrcRegions.BoundsOf(kept).MinX);
    }

    [Fact]
    public void Covering_KeepsPolygonsOfAThatFullyContainSomePolygonOfB()
    {
        var a = Union(Rect(0, 0, 100, 100), Rect(500, 0, 600, 100));
        var b = Rect(20, 20, 30, 30);                                   // sits inside the first only
        var eval = Eval(a, b);

        var kept = eval.Evaluate(DrcLayerExprParser.Parse("covering(1/0, 2/0)"));
        Assert.Equal(100.0 * 100.0, Area(kept), 1);
        Assert.Equal(0, DrcRegions.BoundsOf(kept).MinX);
    }

    [Fact]
    public void Holes_ReturnsTheHolesAsSolids()
    {
        var withHole = Clipper.BooleanOp(
            ClipType.Difference, Rect(0, 0, 100, 100), Rect(40, 40, 60, 60), LayoutClipper.Rule);

        var eval = Eval(withHole);
        Assert.Equal(20.0 * 20.0, Area(eval.Evaluate(DrcLayerExprParser.Parse("holes(1/0)"))), 1);
    }

    /// <summary>
    /// A layer the technology does not define contributes nothing rather than failing the rule — a
    /// deck written for a full process names layers a simpler technology omits, and refusing those
    /// rules outright would hide the ones that ARE evaluable. Recorded so the run can say so.
    /// </summary>
    [Fact]
    public void AnUndefinedLayer_ContributesNothing_AndIsReported()
    {
        var eval = Eval(Rect(0, 0, 100, 100));
        var result = eval.Evaluate(DrcLayerExprParser.Parse("and(1/0, 99/3)"));

        Assert.Empty(result);
        Assert.Contains(new LayerKey(99, 3), eval.MissingLayers);
    }

    /// <summary>
    /// A deck measures many rules against the same derived layer. Re-deriving it per rule is the
    /// difference between a check that runs once and one that runs once per rule, so identical
    /// sub-expressions must share one evaluation.
    /// </summary>
    [Fact]
    public void IdenticalSubExpressions_AreEvaluatedOnce()
    {
        var eval = Eval(Rect(0, 0, 100, 100), Rect(50, 0, 150, 100));

        var expr = DrcLayerExprParser.Parse("and(1/0, 2/0)");
        eval.Evaluate(expr);
        int afterFirst = eval.EvaluatedNodeCount;

        // A structurally identical expression parsed independently must hit the cache — the key is
        // the record tree's own equality, not reference identity.
        eval.Evaluate(DrcLayerExprParser.Parse("and(1/0, 2/0)"));
        Assert.Equal(afterFirst, eval.EvaluatedNodeCount);
    }
}
