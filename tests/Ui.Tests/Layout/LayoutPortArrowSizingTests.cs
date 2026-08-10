// Owner report, 2026-08-09: "The rendering of the Port arrow head can sometimes extend beyond the
// metal shape that the port is connected to. This can happen for very short line lengths... Can the
// arrow never extend beyond the metal in the direction it's pointing to? Also, the size (width) of
// the arrow head seems to be a function of the port width. This also makes the arrow appear 'too big'
// for short but wide edge shapes."
//
// Both halves came from one omission: the arrow was sized purely from the port's WIDTH
// (reach = width × 0.66, barb = reach × 0.35) and knew nothing about how much metal lay ahead of it.
// On a pad wider than it is long that reach runs straight out the far end, with a head to match.
// PortHint now carries LengthDbu, and PortArrowGeometry clamps against it.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortArrowSizingTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static LayoutPortDirection.PortHint Hint(long widthUm, long lengthUm) =>
        new(LayoutRotation.R0, widthUm * Dbu, Inferred: false, 0, 0, lengthUm * Dbu);

    [Theory]
    [InlineData(2900, 20000)]  // §10.7's hero line: long and thin — the arrow is width-limited
    [InlineData(2900, 1000)]   // shorter than it is wide — the arrow must be LENGTH-limited
    [InlineData(5000, 500)]    // the owner's "short but wide edge shape", exaggerated
    [InlineData(100, 100)]     // square
    [InlineData(10000, 200)]   // pathological: 50x wider than long
    public void TheArrowNeverReachesTheFarEdgeOfTheMetal(long widthUm, long lengthUm)
    {
        var hint = Hint(widthUm, lengthUm);
        var (reach, barb) = LayoutRenderer.PortArrowGeometry(hint);

        Assert.True(reach < hint.LengthDbu,
            $"arrow reach {reach} runs to or past the conductor's far edge at {hint.LengthDbu}");

        // The barbs are drawn BACK from the tip, so they cannot overrun either — but they must not be
        // longer than the arrow they belong to.
        Assert.True(barb <= reach);
        Assert.True(barb > 0 || reach == 0);
    }

    [Fact]
    public void OnAShortWideConductor_TheWholeArrowShrinks_HeadIncluded()
    {
        // The second half of the report: the head must not stay huge while the shaft is clamped.
        var longLine  = Hint(widthUm: 2900, lengthUm: 20000);
        var shortWide = Hint(widthUm: 2900, lengthUm: 1000);

        var (longReach, longBarb)   = LayoutRenderer.PortArrowGeometry(longLine);
        var (shortReach, shortBarb) = LayoutRenderer.PortArrowGeometry(shortWide);

        Assert.True(shortReach < longReach, "the shaft must clamp on a short conductor");
        Assert.True(shortBarb  < longBarb,  "…and the head must clamp with it, not stay width-sized");

        // Tied to the reach, so the arrow keeps its proportions rather than becoming a stub with a
        // spearhead on it.
        Assert.Equal(shortReach * 0.35, shortBarb, 6);
    }

    [Fact]
    public void OnALongWideConductor_TheHeadIsStillBoundedByTheWidth()
    {
        // The reach-based rule alone grows without bound when the conductor is long AND wide, where
        // the head can start rivalling the reference-plane bar it sits against.
        var hint = Hint(widthUm: 20000, lengthUm: 100000);
        var (_, barb) = LayoutRenderer.PortArrowGeometry(hint);

        Assert.True(barb <= hint.WidthDbu * 0.22 + 1e-9,
            "a head that rivals the plane bar competes with the one mark that is load-bearing");
    }

    [Fact]
    public void WithNoConductorResolved_TheArrowFallsBackToItsPreferredSize()
    {
        // A port with a stated direction but no metal under it still draws — LengthDbu is then a
        // stand-in, not a measurement, and must not clamp the arrow to nothing.
        var hint = new LayoutPortDirection.PortHint(
            LayoutRotation.R0, 1_000_000, Inferred: false, 0, 0, LengthDbu: 0);

        var (reach, barb) = LayoutRenderer.PortArrowGeometry(hint);
        Assert.True(reach > 0);
        Assert.True(barb > 0);
    }

    // ── The port width is the CONDUCTOR's, never the label's text size ────────────────────────

    [Fact]
    public void ThePortWidth_IsTheConductorsWidth_EvenWhenTheLabelTextIsLarger()
    {
        // Owner report, 2026-08-09: "I made my port Text size 60 and placed it on the edge of a 42 mil
        // wide MLIN. Now the Port width is saying it's 60." Both Resolve branches floored the width at
        // label.Height — a legibility hack for a thin marker — and that floor leaked into the number
        // the Properties Inspector reports AND into the bar the marker draws. The same artwork would
        // then report two different excitation widths depending on how big someone typed the label.
        const long widthDbu = 42 * 25_400 * Dbu / 1000;   // 42 mil, in DBU
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 500 * Dbu, Y2 = widthDbu },
        };

        // A text size far LARGER than the conductor is exactly the reported case.
        var big = new LabelShape
        {
            Layer = TopCopper, X = 0, Y = widthDbu / 2, Text = "P1",
            Height = 60 * 25_400 * Dbu / 1000, IsPort = true, PortDirection = LayoutRotation.R0,
        };
        // …and one far smaller, so the test cannot pass by the floor simply never binding.
        var small = new LabelShape
        {
            Layer = TopCopper, X = 0, Y = widthDbu / 2, Text = "P1",
            Height = 5 * 25_400 * Dbu / 1000, IsPort = true, PortDirection = LayoutRotation.R0,
        };

        var hintBig   = Assert.NotNull(LayoutPortDirection.Resolve(shapes, big));
        var hintSmall = Assert.NotNull(LayoutPortDirection.Resolve(shapes, small));

        Assert.Equal(widthDbu, hintBig.WidthDbu);
        Assert.Equal(hintSmall.WidthDbu, hintBig.WidthDbu);   // the text size changes nothing at all
    }

    [Fact]
    public void TheInferredBranchIsGovernedByTheSameRule()
    {
        // The direction-inferred path (no stated PortDirection) had its own copy of the same floor.
        const long widthDbu = 42 * 25_400 * Dbu / 1000;
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 500 * Dbu, Y2 = widthDbu },
        };
        var label = new LabelShape
        {
            Layer = TopCopper, X = 0, Y = widthDbu / 2, Text = "P1",
            Height = 60 * 25_400 * Dbu / 1000, IsPort = true, PortDirection = null,
        };

        var hint = Assert.NotNull(LayoutPortDirection.Resolve(shapes, label));
        Assert.True(hint.Inferred);
        Assert.Equal(widthDbu, hint.WidthDbu);
    }

    /// <summary>The pixel oracle: nothing the port marker draws may appear past the metal's far edge.
    /// Rendered twice — with and without the port — so every differing pixel is marker-attributable by
    /// construction, which a colour probe cannot manage over the metal the marker sits on.</summary>
    [Fact]
    public void NothingOfTheMarkerIsPaintedBeyondTheConductorsFarEdge()
    {
        // Deliberately much wider than it is long: 4 mm wide, 0.4 mm long.
        const long w = 4_000 * Dbu, len = 400 * Dbu;

        var withPort = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        withPort.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = len, Y2 = w });
        withPort.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = 0, Y = w / 2, Text = "P1", Height = 200 * Dbu,
            IsPort = true, PortDirection = LayoutRotation.R0,
        });

        var bare = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        bare.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = len, Y2 = w });

        // Frame the pad so its 4 mm width fills the canvas vertically, leaving the whole right half
        // of the frame empty — an overrun along +x has plenty of room to show itself in.
        const double zoom = 400.0 / w;                       // 400 px across the pad's width
        var vp = new LayoutViewport(-len, (w / 2.0) - (300.0 / zoom), zoom, 600, 600);

        using var a = Render(withPort, vp);
        using var b = Render(bare, vp);

        int farEdgePx = (int)Math.Round(vp.WorldToScreenX(len));

        for (int x = farEdgePx + 2; x < 600; x++)
            for (int y = 0; y < 600; y++)
                Assert.True(a.GetPixel(x, y) == b.GetPixel(x, y),
                    $"the port marker painted at x={x}, past the conductor's far edge at {farEdgePx}");

        // Non-vacuity: the marker must actually be drawn SOMEWHERE, or this passes for free.
        bool drewSomething = false;
        for (int x = 0; x < farEdgePx && !drewSomething; x++)
            for (int y = 0; y < 600; y++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) { drewSomething = true; break; }
        Assert.True(drewSomething, "the port marker drew nothing at all — the test proves nothing");
    }

    private static SKBitmap Render(LayoutView view, LayoutViewport vp)
    {
        using var surface = SKSurface.Create(new SKImageInfo(600, 600));
        LayoutRenderer.Draw(surface.Canvas, view, null, vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = "" });
        return SKBitmap.FromImage(surface.Snapshot());
    }
}
