// ================================================================
//  HarmonicaPanelTests.cs  —  M3/M4 of brief-harmonicarf-h4-h5
//
//  TIER 0  the §7.2 alpha ramp against the formula, computed independently: α of the top level is
//          EXACTLY 1.0, α is monotone in RANK, and a deliberately uneven level set does not crush
//          the ramp.
//  TIER 7  |Γ_intr| > 1 renders OUTSIDE the boundary rather than clamped or hidden — pixel oracle.
//  R-h45-1 the locked §7.1 layout is DATA, and it round-trips through the .charm.
//  R-h45-5 a hole draws a HOLLOW dot, not a gap.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using RfCore;
using RfCore.Loadpull;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class HarmonicaPanelTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public HarmonicaPanelTests(ITestOutputHelper output)
    {
        _out = output;
        SkiaFonts.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose() => SkiaFonts.TestOverrideTypeface = null;

    // ══ TIER 0 — the alpha ramp ══════════════════════════════════════════════

    /// <summary>The formula from §7.2, written out INDEPENDENTLY of the implementation. A test that
    /// called IsoLineAlphaRamp to compute its own expectation would pass whatever the code did.</summary>
    private static double ExpectedAlpha(int i, int n, double floor, double p)
        => n <= 1 ? 1.0 : floor + (1.0 - floor) * Math.Pow((double)i / (n - 1), p);

    [Theory]
    [InlineData(10, 0.25, 1.5)]
    [InlineData(10, 0.00, 1.0)]
    [InlineData(3,  0.60, 2.5)]
    [InlineData(25, 0.10, 0.5)]
    public void Tier0_RampMatchesTheFormula_AndTheTopLevelIsExactlyOne(int n, double floor, double p)
    {
        var a = IsoLineAlphaRamp.ForLevels(n, floor, p);
        Assert.Equal(n, a.Length);

        for (int i = 0; i < n - 1; i++)
            Assert.Equal(ExpectedAlpha(i, n, floor, p), a[i], precision: 12);

        // "α_{n−1} = 1 exactly" — the top contour IS the answer, and that has to hold at the
        // boundary rather than merely approach it.
        Assert.Equal(1.0, a[^1]);
        Assert.Equal(255, IsoLineAlphaRamp.AlphaByte(n - 1, n, floor, p));
    }

    [Theory]
    [InlineData(10, 0.25, 1.5)]
    [InlineData(4,  0.05, 3.0)]
    [InlineData(50, 0.40, 0.7)]
    public void Tier0_RampIsMonotoneInRank(int n, double floor, double p)
    {
        var a = IsoLineAlphaRamp.ForLevels(n, floor, p);
        for (int i = 1; i < n; i++)
            Assert.True(a[i] >= a[i - 1],
                $"α must not decrease with rank: α[{i - 1}]={a[i - 1]:F6}, α[{i}]={a[i]:F6}");
        Assert.Equal(floor, a[0], precision: 12);
    }

    [Fact]
    public void Tier0_AnUnevenLevelSetDoesNotCrushTheRamp()
    {
        // The whole reason §7.2 specifies RANKED rather than value-proportional. This level set has
        // a long low tail: nine levels bunched at the bottom and one far above.
        double[] levels = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 100.0];

        var ranked = IsoLineAlphaRamp.ForLevels(levels.Length, 0.25, 1.5);

        // What a value-proportional ramp WOULD have produced, written out here so the comparison is
        // against a real alternative rather than an assertion about nothing.
        double lo = levels[0], hi = levels[^1];
        var proportional = levels.Select(v => 0.25 + 0.75 * Math.Pow((v - lo) / (hi - lo), 1.5)).ToArray();

        // Ranked: every contour is meaningfully visible.
        Assert.All(ranked, a => Assert.True(a >= 0.25));
        Assert.True(ranked[4] > 0.35,
            $"the middle contour must not be crushed — ranked α[4] = {ranked[4]:F3}");

        // Value-proportional: every level below the top is pinned within a hair of the floor, which
        // is the failure mode — nine contours rendered indistinguishably faint.
        int crushed = proportional.Take(levels.Length - 1).Count(a => a - 0.25 < 0.01);
        Assert.Equal(levels.Length - 1, crushed);

        // The two forms genuinely differ where it matters: the ninth contour reads clearly under the
        // ranked ramp and is invisible under the proportional one.
        Assert.True(ranked[8] > 0.70,
            $"ranked α[8] = {ranked[8]:F3} — the second-highest contour must read clearly");
        Assert.True(proportional[8] < 0.26,
            $"proportional α[8] = {proportional[8]:F3} — if this is no longer crushed, the fixture " +
            "has stopped demonstrating the difference the ranked form exists for");

        _out.WriteLine($"ranked:       [{string.Join(", ", ranked.Select(a => a.ToString("F3")))}]");
        _out.WriteLine($"proportional: [{string.Join(", ", proportional.Select(a => a.ToString("F3")))}]");
    }

    [Fact]
    public void Tier0_ALoneContourIsFullyOpaque_AndTheFloorCanFlattenTheRamp()
    {
        Assert.Equal(1.0, IsoLineAlphaRamp.Alpha(0, 1, 0.25, 1.5));

        // §7.9.4: "a user who dislikes the fade can flatten it (α_floor = 1) without a code change."
        var flat = IsoLineAlphaRamp.ForLevels(12, 1.0, 1.5);
        Assert.All(flat, a => Assert.Equal(1.0, a));
    }

    [Fact]
    public void Tier0_RankIsFoundByNearestLevel_NotByExactEquality()
    {
        // An iso-polyline carries the level it was EXTRACTED at; exact float equality against the
        // generating table is not something to rely on.
        double[] levels = [10, 20, 30, 40];
        Assert.Equal(0, IsoLineAlphaRamp.RankOf(10.0000000001, levels));
        Assert.Equal(2, IsoLineAlphaRamp.RankOf(29.9999999998, levels));
        Assert.Equal(3, IsoLineAlphaRamp.RankOf(1e9, levels));
    }

    // ══ TIER 7 — |Γ_intr| > 1 lands OUTSIDE the boundary ═════════════════════

    [Fact]
    public void Tier7_TheCompressedRadialScale_IsExactInside_MonotoneOutside_AndBounded()
    {
        // Inside the disc the chart is exact — the compression exists only for the region that has
        // nowhere else to go.
        foreach (double m in new[] { 0.0, 0.25, 0.5, 0.9, 1.0 })
            Assert.Equal(m, IntrinsicGlyphScale.DisplayRadius(m), precision: 12);

        // Outside: strictly greater than 1, strictly increasing, and bounded by 1 + margin so no
        // finite value can be pushed off-panel (which would be "hidden" by another route).
        double prev = 1.0;
        foreach (double m in new[] { 1.0001, 1.05, 1.3, 2.0, 5.0, 50.0, 1e6 })
        {
            double r = IntrinsicGlyphScale.DisplayRadius(m);
            Assert.True(r > prev, $"|Γ|={m} must map beyond |Γ|={prev}");
            Assert.True(r < 1.0 + IntrinsicGlyphScale.DefaultMargin + 1e-12,
                $"|Γ|={m} mapped to {r}, past the 1+margin bound");
            prev = r;
        }

        // The angle is NEVER touched — which band's glyph points where is real information.
        var g = Complex.FromPolarCoordinates(3.7, 1.234);
        var shown = IntrinsicGlyphScale.DisplayPosition(g);
        Assert.Equal(g.Phase, shown.Phase, precision: 12);
        Assert.True(shown.Magnitude > 1.0);
        Assert.True(IntrinsicGlyphScale.IsCompressed(g));
        Assert.False(IntrinsicGlyphScale.IsCompressed(new Complex(0.5, 0.2)));
    }

    [Fact]
    public void Tier7_AGlyphOutsideTheUnitCircle_PaintsAPixelBeyondTheRim_NotClampedAndNotHidden()
    {
        const int W = 420, H = 420;
        var theme = HarmonicaRenderTheme.Dark;

        // One marker whose termination is well inside the disc and whose INTRINSIC value is outside
        // it — §4.5's ordinary case with conduction-only current, not an error.
        var marker = new HarmonicaMarker(TerminationSideKind.Load, 1)
        {
            Gamma          = new Complex(0.30, 0.10),
            GammaIntrinsic = new Complex(1.80, 0.00),      // |Γ_intr| = 1.8, on the +real axis
        };

        var data = new SmithPanelData { Title = "", Markers = [marker] };

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(canvas, (W, H), data, theme, darkMode: true);

        using var img  = surface.Snapshot();
        using var bmp  = SKBitmap.FromImage(img);

        // The chart's own geometry, taken from the SAME headroom-aware transform the renderer used
        // — never a hand-derived guess at where the rim is, and never PlotRenderer's raw transform,
        // which does not know about the annulus headroom.
        var centre = HarmonicaPanelRenderer.GammaToCanvas(Complex.Zero, (W, H));
        var rimPt  = HarmonicaPanelRenderer.GammaToCanvas(Complex.One,  (W, H));
        float rimR = Math.Abs(rimPt.X - centre.X);

        // The probe is SPECIFIC to the glyph, not merely "not background". R-h9a-6 (brief-
        // harmonicarf-r1a, 2026-08-12) moved MarkerBand1 to a pure (0,255,0) — it no longer carries
        // any red at all, so the red-channel probe this test used before that change stopped working
        // (found nothing, everywhere). BLUE is what still separates the glyph from every other
        // Smith-panel role near the rim: grid (0,90,30) and axes (0,255,65) both carry real blue,
        // while the glyph's own desaturated marker colour (blended 45% toward the near-black
        // background, which is itself low-blue) stays near zero blue. Combined with the glyph's own
        // green being far brighter than the background's, this is unique to the glyph and nothing
        // else drawn near the rim. The first version of this test probed for "not background" and
        // its own negative control caught it picking up chart chrome instead; that is why the
        // control is here.
        var bg = theme.Background;
        bool IsGlyphPixel(SKColor c) => c.Green >= bg.Green + 50 && c.Blue <= bg.Blue + 12;

        int litBeyondRim = -1;
        for (int dx = 2; dx < (int)(rimR * 0.30) && litBeyondRim < 0; dx++)
        {
            int x = (int)Math.Round(rimPt.X) + dx;
            if (x < 0 || x >= W) break;
            for (int dy = -16; dy <= 16; dy++)
            {
                int y = (int)Math.Round(rimPt.Y) + dy;
                if (y < 0 || y >= H) continue;
                if (IsGlyphPixel(bmp.GetPixel(x, y))) { litBeyondRim = dx; break; }
            }
        }

        Assert.True(litBeyondRim > 0,
            "no glyph pixel beyond the unit circle — a glyph with |Γ_intr| > 1 was clamped or hidden, " +
            "which §4.5 consequence 2 forbids.");
        _out.WriteLine($"first glyph pixel {litBeyondRim} px beyond the rim (rim radius {rimR:F1} px)");

        // NEGATIVE CONTROL. The same probe against a marker whose intrinsic value sits INSIDE the
        // disc must find nothing out there — otherwise the positive case above is measuring chart
        // chrome and proves nothing about the glyph.
        var inside = new HarmonicaMarker(TerminationSideKind.Load, 1)
        {
            Gamma = new Complex(0.30, 0.10), GammaIntrinsic = new Complex(0.40, 0.0),
        };
        using var s2 = SKSurface.Create(new SKImageInfo(W, H));
        s2.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(s2.Canvas, (W, H),
            new SmithPanelData { Markers = [inside] }, theme, darkMode: true);
        using var img2 = s2.Snapshot();
        using var bmp2 = SKBitmap.FromImage(img2);

        bool anyOut = false;
        for (int dx = 2; dx < (int)(rimR * 0.30) && !anyOut; dx++)
        for (int dy = -16; dy <= 16; dy++)
        {
            int x = (int)Math.Round(rimPt.X) + dx, y = (int)Math.Round(rimPt.Y) + dy;
            if (x < 0 || x >= W || y < 0 || y >= H) continue;
            if (IsGlyphPixel(bmp2.GetPixel(x, y))) { anyOut = true; break; }
        }
        Assert.False(anyOut,
            "an INSIDE-the-disc glyph painted beyond the rim — the probe is picking up chart chrome " +
            "rather than the glyph, so the positive case above proves nothing.");
    }

    private static bool NearlySame(SKColor a, SKColor b)
        => Math.Abs(a.Red - b.Red) <= 6 && Math.Abs(a.Green - b.Green) <= 6 && Math.Abs(a.Blue - b.Blue) <= 6;

    // ══ R-h45-5 — the hollow hole dot ════════════════════════════════════════

    [Fact]
    public void HoleDots_AreHollow_AndConvergedPointsAreFilled()
    {
        const int W = 420, H = 420;
        var theme = HarmonicaRenderTheme.Dark;

        // Two points far apart on the real axis: one converged, one a hole.
        var data = new SmithPanelData
        {
            GridPoints =
            [
                new HarmonicaGridPoint(new Complex(-0.45, 0.0), IsHole: false),
                new HarmonicaGridPoint(new Complex( 0.45, 0.0), IsHole: true),
            ],
        };

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), data, theme, darkMode: true);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        var solid = HarmonicaPanelRenderer.GammaToCanvas(new Complex(-0.45, 0.0), (W, H));
        var hole  = HarmonicaPanelRenderer.GammaToCanvas(new Complex( 0.45, 0.0), (W, H));

        // The converged point's CENTRE is painted in the grid-point colour.
        var solidC = bmp.GetPixel((int)Math.Round(solid.X), (int)Math.Round(solid.Y));
        Assert.False(NearlySame(solidC, theme.Background),
            "a converged Γ point must render as a FILLED dot");

        // The hole's centre is NOT painted in the dropped colour — the dot is a ring, so the middle
        // shows whatever is behind it. That is what makes the hole read as measured rather than as a
        // rendering gap.
        var holeC = bmp.GetPixel((int)Math.Round(hole.X), (int)Math.Round(hole.Y));
        Assert.True(NearlySame(holeC, theme.GridPointDropped) is false,
            "the hole dot must be HOLLOW — its centre is filled with the dropped colour");

        // …but its RIM is painted, so it is drawn, not omitted.
        bool rimLit = false;
        for (int dx = -8; dx <= 8 && !rimLit; dx++)
        for (int dy = -8; dy <= 8; dy++)
        {
            if (dx * dx + dy * dy < 4) continue;                       // skip the hollow middle
            int x = (int)Math.Round(hole.X) + dx, y = (int)Math.Round(hole.Y) + dy;
            if (x < 0 || x >= W || y < 0 || y >= H) continue;
            if (!NearlySame(bmp.GetPixel(x, y), theme.Background)) { rimLit = true; break; }
        }
        Assert.True(rimLit, "the hole dot must be DRAWN — a hole is measured data, not a gap");
    }

    // ══ TIER 8 — nothing is drawn inside a hole ══════════════════════════════

    [Fact]
    public void Tier8_AGridWithAHole_DrawsNoContourAndNoFillInsideTheExcludedDisc()
    {
        // Tier 8 is Tier 7 of the PREVIOUS brief moved one layer out: that one proved no POLYLINE
        // enters a hole; this proves no PIXEL does, which is a different claim and the one a user
        // sees. §6.3: "an invented efficiency ridge in a hole is exactly the kind of artifact this
        // tool must never produce."
        //
        // The grid is built by ContourGrid itself — its own support mask (convex hull minus a disc
        // around each thrown-out point) is the thing under test, so synthesising a mask here would
        // test the fixture instead.
        const int W = 520, H = 520;
        var theme = HarmonicaRenderTheme.Dark;

        var grid = BuildGridWithADeliberateHole(out var holeGamma, out double holeRadius);
        Assert.True(grid.HoleCount >= 1, "the fixture produced no hole — nothing to test");
        _out.WriteLine($"grid: {grid.Points.Count} Γ points, {grid.HoleCount} hole(s), " +
                       $"hole at {holeGamma}, mask radius {holeRadius:F3}");

        var raster = grid.Raster(GridMetric.PoutDbm, 256);
        var levels = ContourExtractor.LevelsBetween(raster, 10);
        var polys  = ContourExtractor.Extract(raster, levels);
        Assert.NotEmpty(polys);

        // The oracle is a DIFFERENTIAL render, not a colour probe. A first attempt keyed on the
        // green channel and could not separate an iso-line from the Smith chart's own constant-R/X
        // arcs — those legitimately cross a hole (chart chrome, not data), and where two arcs
        // overlap the composited green rivals a faintly-ramped iso-line. Rendering the SAME panel
        // twice, once with contours and once without, makes every differing pixel contour-
        // attributable by construction, with nothing left to discriminate by colour.
        var withContours = new SmithPanelData
        {
            Contours   = polys,
            Levels     = [.. levels.Levels.OrderBy(v => v)],
            GridPoints = [.. grid.Points.Select(p => new HarmonicaGridPoint(p.Gamma, p.IsHole))],
        };
        var withoutContours = withContours with { Contours = [], Levels = [] };

        using var bmpWith    = RenderSmith(withContours,    theme, W, H);
        using var bmpWithout = RenderSmith(withoutContours, theme, W, H);

        var centrePx = HarmonicaPanelRenderer.GammaToCanvas(holeGamma, (W, H));
        var edgePx   = HarmonicaPanelRenderer.GammaToCanvas(
                           holeGamma + new Complex(holeRadius, 0), (W, H));
        float discR  = Math.Abs(edgePx.X - centrePx.X);
        Assert.True(discR > 8, $"the hole's disc is only {discR:F1} px across — too small to probe");

        // Probe out to 80% of the disc: far enough in that the mask's own boundary (and its
        // antialiasing) is excluded, deep enough that a contour genuinely crossing the hole cannot
        // hide. The hollow hole dot is drawn from the contour-free frame too, so it cancels in the
        // difference and needs no exclusion.
        int lit = 0;
        var offenders = new List<(int X, int Y, double R)>();
        for (int y = (int)(centrePx.Y - discR); y <= (int)(centrePx.Y + discR); y++)
        for (int x = (int)(centrePx.X - discR); x <= (int)(centrePx.X + discR); x++)
        {
            if (x < 0 || x >= W || y < 0 || y >= H) continue;
            double dx = x - centrePx.X, dy = y - centrePx.Y;
            double r  = Math.Sqrt(dx * dx + dy * dy);
            if (r > discR * 0.80) continue;

            if (!NearlySame(bmpWith.GetPixel(x, y), bmpWithout.GetPixel(x, y)))
            {
                lit++;
                offenders.Add((x, y, r));
            }
        }

        if (lit > 0)
            _out.WriteLine("contour pixels INSIDE the hole: " + string.Join(", ",
                offenders.Take(6).Select(o => $"({o.X},{o.Y}) r={o.R:F1}/{discR:F1}")));

        Assert.Equal(0, lit);

        // NON-VACUITY: the same difference taken over the WHOLE panel must find plenty of contour
        // pixels — otherwise this would pass against a panel that drew no contours at all.
        int litSomewhere = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            if (!NearlySame(bmpWith.GetPixel(x, y), bmpWithout.GetPixel(x, y))) litSomewhere++;

        Assert.True(litSomewhere > 200,
            $"only {litSomewhere} contour pixels anywhere — the panel drew essentially nothing, so " +
            "the in-hole assertion above proves nothing.");
        _out.WriteLine($"{litSomewhere} contour pixels on the panel, 0 inside the hole " +
                       $"(disc {discR:F1} px, probed to {discR * 0.80:F1} px)");
    }

    private static SKBitmap RenderSmith(SmithPanelData data, HarmonicaRenderTheme theme, int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (w, h), data, theme, darkMode: true);
        return SKBitmap.FromImage(surface.Snapshot());
    }

    /// <summary>A PinMax most Γ points reach and the extreme-|Γ| ones do not — chosen by measurement
    /// on this fixture, not guessed.</summary>
    private const double PinMaxForAFewHoles = 34.0;

    /// <summary>
    /// A real <see cref="ContourGrid"/> with a deliberate hole: the device is given a PinMax it can
    /// reach almost everywhere, and one Γ point is placed where it cannot — the same mechanism §6.3
    /// describes, rather than a NaN written in by hand.
    /// </summary>
    private static ContourGrid BuildGridWithADeliberateHole(out Complex holeGamma, out double holeRadius)
    {
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "_v1/50",
                    ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
                },
            },
            Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
            Settings = new HarmonicaSettings
            {
                HarmonicCount = 3, FrequencyHz = 2e9,
                BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
                // A PinMax most Γ points reach and a few do not — which is how a real hole appears.
                CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = PinMaxForAFewHoles,
            },
        };

        var ctx = HarmonicaContext.Create(model, new CircuitRF.Engine.AnalysisSettings
        {
            InductanceRegularization  = CircuitRF.Engine.RegularizationMode.Always,
            ConductanceRegularization = CircuitRF.Engine.RegularizationMode.Never,
        });

        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));

        var grid = new ContourGrid();
        grid.Build(ctx, terms, ContourGrid.RingGrid(rings: 3, spokes: 10, maxGamma: 0.85));

        // The fixture must be a MOSTLY-converged grid with A FEW holes. An all-holes grid has no
        // surface to draw and would pass the in-hole assertion vacuously; a no-holes grid has nothing
        // to assert about. Guarding here means a future engine change that degenerates the fixture
        // fails loudly instead of quietly making the test meaningless.
        Assert.InRange(grid.HoleCount, 1, grid.Points.Count - 8);

        var hole = grid.Points.First(p => p.IsHole);
        holeGamma  = hole.Gamma;
        holeRadius = grid.HoleRadius;
        return grid;
    }

    // ══ harmonicaRF NEVER FILLS ══════════════════════════════════════════════

    [Fact]
    public void SmithPanel_NeverFills_AndHasNoPathToTheFillRenderers()
    {
        // Owner ruling, 2026-08-06: iso-lines only. No fill, no fill setting, and no plan for one.
        // §7.2's "contours are unfilled" is the whole behaviour, not a default a preference flips.
        //
        // Enforced structurally: HarmonicaPanelRenderer never constructs a ContourData, so
        // DrawTopoMapFill / DrawHeatMapFill are unreachable from harmonicaRF. This test reads the
        // renderer's own source because that is the property being claimed — "there is no fill path"
        // is a statement about the code, and a behavioural test could only ever show that one
        // particular fixture did not happen to fill.
        string src = ReadRendererSource();
        Assert.DoesNotContain("ContourData",       src, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawTopoMapFill",   src, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawHeatMapFill",   src, StringComparison.Ordinal);
        Assert.DoesNotContain("ContourFillType",   src, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowFill",          src, StringComparison.Ordinal);

        // And SmithPanelData offers nowhere to ask for one.
        Assert.DoesNotContain(typeof(SmithPanelData).GetProperties(),
            p => p.Name.Contains("Fill", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SmithPanel_DrawsContoursAsSTROKES_LeavingTheInteriorUnpainted()
    {
        // The behavioural companion: a single closed contour must paint its OUTLINE and leave the
        // region it bounds alone. A filled render would paint the interior too — this is what the
        // ruling actually means on screen.
        const int W = 460, H = 460;
        var theme = HarmonicaRenderTheme.Dark;

        var d = new SmithPanelData
        {
            Levels   = [1.0],
            Contours = [new IsoPolyline(1.0, Circle(0.60, 160), Closed: true)],
        };

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), d, theme, darkMode: true);
        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        // On the ring itself: painted.
        double onRing = BestGreenDistance(bmp, (W, H), 0.60, theme);
        Assert.True(onRing > 40, $"the contour did not stroke (green distance {onRing:F1})");

        // Well inside it, away from the Smith grid's own arcs: the centre of the disc. The chart's
        // real axis passes through it, so probe a point OFF that axis but still deep inside the ring.
        var probe = HarmonicaPanelRenderer.GammaToCanvas(new Complex(0.10, 0.28), (W, H));
        var c = bmp.GetPixel((int)Math.Round(probe.X), (int)Math.Round(probe.Y));
        Assert.True(Math.Abs(c.Green - theme.Background.Green) < 25,
            $"the interior of a closed contour was painted (green {c.Green} vs background " +
            $"{theme.Background.Green}) — harmonicaRF must never fill.");
    }

    private static string ReadRendererSource()
    {
        // Walk up from the test binary to the repo root, the same way this repo's other source-scan
        // tests locate a file they need to assert about.
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine(dir!.FullName,
            "src", "Ui", "Harmonica", "Renderers", "HarmonicaPanelRenderer.cs");
        Assert.True(System.IO.File.Exists(path), $"renderer source not found at {path}");
        return System.IO.File.ReadAllText(path);
    }

    // ══ R-h45-1 — the locked layout is DATA ══════════════════════════════════

    [Fact]
    public void Layout_DefaultIsSection71_LockedAndCoveringTheWholeArea()
    {
        var l = CharmLayout.Default;
        Assert.True(l.Locked);
        Assert.Equal(5, l.Panels.Count);

        var power  = l.PlacementOf(HarmonicaPanelId.SmithPower);
        var eff    = l.PlacementOf(HarmonicaPanelId.SmithEfficiency);
        var strip  = l.PlacementOf(HarmonicaPanelId.ReadoutStrip);
        var load   = l.PlacementOf(HarmonicaPanelId.Loadline);
        var sweep  = l.PlacementOf(HarmonicaPanelId.PowerSweep);

        // §7.1: "The two Smith charts sit side by side — power on the left, efficiency on the right."
        Assert.Equal(0.0, power.X);
        Assert.Equal(power.X + power.W, eff.X, precision: 12);
        Assert.Equal(power.Y, eff.Y);
        Assert.Equal(power.W, eff.W, precision: 12);

        // "…with the dense settings/readout strip spanning beneath BOTH."
        Assert.Equal(0.0, strip.X);
        Assert.Equal(power.Y + power.H, strip.Y, precision: 12);
        Assert.Equal(power.W + eff.W, strip.W, precision: 12);

        // "The right column holds the loadline plot ABOVE the power-sweep plot, full height."
        Assert.Equal(load.X, sweep.X, precision: 12);
        Assert.Equal(load.W, sweep.W, precision: 12);
        Assert.Equal(load.Y + load.H, sweep.Y, precision: 12);
        Assert.Equal(1.0, sweep.Y + sweep.H, precision: 12);
        Assert.Equal(1.0, load.X + load.W, precision: 12);

        // The two regions tile the area with no gap and no overlap.
        Assert.Equal(load.X, power.W + eff.W, precision: 12);
        Assert.Equal(1.0, strip.Y + strip.H, precision: 12);
    }

    [Fact]
    public void Layout_RoundTripsThroughTheCharm_AndAnUntouchedOneWritesNoBlock()
    {
        var model = MinimalModel();

        // Untouched ⇒ no block at all, so an existing file does not churn.
        string plain = CharmIo.Write(model, new TerminationSet(3), null, CharmLayout.Default);
        Assert.DoesNotContain("\"Layout\"", plain, StringComparison.Ordinal);
        Assert.True(CharmIo.ReadAll(plain, null).Layout.IsDefault);

        // Moved and unlocked ⇒ persisted exactly.
        var moved = new CharmLayout
        {
            Locked = false,
            Panels =
            [
                new CharmPanelPlacement(HarmonicaPanelId.SmithPower, 0.05, 0.05, 0.40, 0.50),
                new CharmPanelPlacement(HarmonicaPanelId.PowerSweep, 0.50, 0.10, 0.45, 0.80),
            ],
        };
        string json = CharmIo.Write(model, new TerminationSet(3), null, moved);
        var back = CharmIo.ReadAll(json, null).Layout;

        Assert.False(back.Locked);
        Assert.Equal(2, back.Panels.Count);
        Assert.Equal(moved.Panels[0], back.Panels[0]);
        Assert.Equal(moved.Panels[1], back.Panels[1]);

        // A panel the file did not mention still positions sensibly rather than at (0,0,0,0).
        var missing = back.PlacementOf(HarmonicaPanelId.Loadline);
        Assert.True(missing.W > 0 && missing.H > 0);
    }

    [Fact]
    public void Layout_ADegeneratePlacementIsDropped_NotHonoured()
    {
        // A panel positioned at zero width is invisible with nothing on screen to say why — worse
        // than falling back to §7.1's own default for that one panel.
        var bad = new CharmLayout
        {
            Locked = false,
            Panels =
            [
                new CharmPanelPlacement(HarmonicaPanelId.SmithPower, 0.0, 0.0, 0.0, 0.5),
                new CharmPanelPlacement(HarmonicaPanelId.Loadline,   0.5, 0.0, 0.4, 0.9),
            ],
        };
        string json = CharmIo.Write(MinimalModel(), new TerminationSet(3), null, bad);
        var back = CharmIo.ReadAll(json, null).Layout;

        Assert.Single(back.Panels);
        Assert.Equal(HarmonicaPanelId.Loadline, back.Panels[0].PanelId);
        var fallback = back.PlacementOf(HarmonicaPanelId.SmithPower);
        Assert.True(fallback.W > 0 && fallback.H > 0);
    }

    // ══ the panels draw ══════════════════════════════════════════════════════

    [Fact]
    public void LoadlinePanel_DrawsTheDcivFamilyAndTheLoadline_AndStatesItsPlane()
    {
        const int W = 480, H = 380;
        var theme = HarmonicaRenderTheme.Dark;

        var curves = new List<LoadlinePanelData.Curve>();
        for (int k = 0; k < 7; k++)
        {
            double vgs = -5 + k * 0.5;
            var vds = Enumerable.Range(0, 120).Select(i => 60.0 * i / 119).ToArray();
            var ids = vds.Select(v => Math.Max(0, vgs + 4) * Math.Max(0, vgs + 4) * 0.06 * Math.Tanh(v * 0.35)).ToArray();
            curves.Add(new LoadlinePanelData.Curve(vgs, vds, ids));
        }
        var th = Enumerable.Range(0, 33).Select(i => 2 * Math.PI * i / 32).ToArray();

        var d = new LoadlinePanelData
        {
            Dciv        = curves,
            LoadlineVds = th.Select(a => 30 + 24 * Math.Cos(a)).ToArray(),
            LoadlineIds = th.Select(a => 0.9 - 0.85 * Math.Cos(a)).ToArray(),
            Intrinsic   = true,
        };
        Assert.Equal("intrinsic", d.PlaneLabel);
        Assert.Equal("extrinsic", (d with { Intrinsic = false }).PlaneLabel);

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawLoadlinePanel(surface.Canvas, (W, H), d, theme, darkMode: true);

        using var bmp = SKBitmap.FromImage(surface.Snapshot());
        Assert.True(CountNonBackground(bmp, theme.Background) > 500,
            "the loadline panel drew essentially nothing");

        // The loadline's RESERVED RED must actually appear — the panel is the one place red is spent.
        Assert.True(HasColourNear(bmp, theme.Loadline, tolerance: 40),
            "the loadline must render in the reserved red (§7.9.2)");
    }

    [Fact]
    public void PowerSweepPanel_DrawsGainAndEfficiency_AndCyclesItsXUnit()
    {
        const int W = 480, H = 380;
        var theme = HarmonicaRenderTheme.Dark;

        int n = 33;
        var pin  = Enumerable.Range(0, n).Select(i => -10.0 + i * 1.25).ToArray();
        var gain = pin.Select(p => 14.5 - 4.0 * Math.Log(1 + Math.Exp((p - 18) * 0.45))).ToArray();
        var d = new PowerSweepPanelData
        {
            PinAvailDbm   = pin,
            PoutDbm       = pin.Zip(gain, (p, g) => p + g).ToArray(),
            GainDb        = gain,
            EfficiencyPct = pin.Select(p => 72.0 / (1 + Math.Exp(-(p - 14) * 0.30))).ToArray(),
            CursorIndex   = 20,
        };

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawPowerSweepPanel(surface.Canvas, (W, H), d, theme, darkMode: true);

        using var bmp = SKBitmap.FromImage(surface.Snapshot());
        Assert.True(CountNonBackground(bmp, theme.Background) > 500);
        Assert.True(HasColourNear(bmp, theme.EfficiencyTrace, tolerance: 40),
            "the efficiency trace must render in the reserved red (§7.9.2)");

        // §7.4 — the X unit cycles Pout (dBm) → Pout (W) → Pin available (dBm) → Pin available (W).
        var u = PowerSweepXUnit.PoutDbm;
        Assert.Equal("Pout (dBm)",           u.Label());
        Assert.Equal(PowerSweepXUnit.PoutW,       u = u.Next());
        Assert.Equal("Pout (W)",                  u.Label());
        Assert.Equal(PowerSweepXUnit.PinAvailDbm, u = u.Next());
        Assert.Equal(PowerSweepXUnit.PinAvailW,   u = u.Next());
        Assert.Equal(PowerSweepXUnit.PoutDbm,     u.Next());          // wraps

        // dBm → W is a real conversion, not a relabel: 30 dBm is 1 W.
        var watts = PowerSweepXUnit.PoutW.Values(d with { PoutDbm = [30.0, 0.0] });
        Assert.Equal(1.0,   watts[0], precision: 9);
        Assert.Equal(1e-3,  watts[1], precision: 12);
    }

    [Fact]
    public void SmithPanel_DrawsContoursWithTheRankedRamp_TopLevelOpaqueLowestFaded()
    {
        const int W = 460, H = 460;
        var theme = HarmonicaRenderTheme.Dark;

        // Two concentric rings at the extreme ranks of a 10-level set, far enough apart to sample.
        double[] levels = Enumerable.Range(0, 10).Select(i => (double)i).ToArray();
        var d = new SmithPanelData
        {
            Levels   = levels,
            Contours =
            [
                new IsoPolyline(levels[0], Circle(0.75, 128), Closed: true),   // rank 0 — most faded
                new IsoPolyline(levels[^1], Circle(0.30, 128), Closed: true),  // rank 9 — fully opaque
            ],
        };

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), d, theme, darkMode: true);
        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        double outerDist = BestGreenDistance(bmp, (W, H), 0.75, theme);
        double innerDist = BestGreenDistance(bmp, (W, H), 0.30, theme);

        _out.WriteLine($"rank 0 (|Γ|=0.75) distance-from-background {outerDist:F1}; " +
                       $"rank 9 (|Γ|=0.30) {innerDist:F1}");

        Assert.True(innerDist > 0, "the top-level contour did not render at all");
        Assert.True(outerDist > 0, "the lowest-level contour was faded to invisibility — the ramp " +
                                   "has a floor precisely so this cannot happen");
        Assert.True(innerDist > outerDist * 1.3,
            $"the top level must read as markedly more opaque than the lowest " +
            $"({innerDist:F1} vs {outerDist:F1})");
    }

    private static IReadOnlyList<(double X, double Y)> Circle(double r, int n)
    {
        var pts = new List<(double, double)>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts.Add((r * Math.Cos(a), r * Math.Sin(a)));
        }
        return pts;
    }

    /// <summary>The strongest departure from the background found anywhere on a ring of radius
    /// <paramref name="r"/> — the alpha the ramp actually painted, read off the pixels.</summary>
    private static double BestGreenDistance(SKBitmap bmp, (double W, double H) size,
                                            double r, HarmonicaRenderTheme theme)
    {
        double best = 0;
        for (int i = 0; i < 720; i++)
        {
            double a = 2 * Math.PI * i / 720;
            var p = HarmonicaPanelRenderer.GammaToCanvas(
                        new Complex(r * Math.Cos(a), r * Math.Sin(a)), size);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int x = (int)Math.Round(p.X) + dx, y = (int)Math.Round(p.Y) + dy;
                if (x < 0 || x >= bmp.Width || y < 0 || y >= bmp.Height) continue;
                var c = bmp.GetPixel(x, y);
                // Green channel only: the iso-line colour is phosphor green and the background is
                // near-black, so the green departure IS the painted alpha.
                double dist = c.Green - theme.Background.Green;
                if (dist > best) best = dist;
            }
        }
        return best;
    }

    private static int CountNonBackground(SKBitmap bmp, SKColor bg)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y += 2)
        for (int x = 0; x < bmp.Width;  x += 2)
            if (!NearlySame(bmp.GetPixel(x, y), bg)) n++;
        return n;
    }

    private static bool HasColourNear(SKBitmap bmp, SKColor target, int tolerance)
    {
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width;  x++)
        {
            var c = bmp.GetPixel(x, y);
            if (Math.Abs(c.Red - target.Red) <= tolerance &&
                Math.Abs(c.Green - target.Green) <= tolerance &&
                Math.Abs(c.Blue - target.Blue) <= tolerance)
                return true;
        }
        return false;
    }

    private static CircuitModel MinimalModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "0.1*tanh(_v2)",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.0, Vds = 28 },
        Settings = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = 2e9 },
    };
}
