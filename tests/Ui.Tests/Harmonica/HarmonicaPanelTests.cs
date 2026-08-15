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
    public void Tier7_TheRadialScale_IsTheIdentity_UpToTheWritableCeiling()
    {
        // ROUND 10 (owner) turned the compression OFF: an intrinsic glyph is drawn at its TRUE Γ, on
        // the same raw radial scale a marker is, so that for a DUT whose two planes coincide the
        // glyph and its marker land in the same place. This test used to assert the opposite (exact
        // inside, compressed-and-bounded outside) and is re-pointed rather than deleted, because
        // "which radius does a |Γ_intr| land at" is still the question that matters.
        foreach (double m in new[] { 0.0, 0.25, 0.5, 0.9, 1.0, 1.0001, 1.3, 2.0, 5.0 })
            Assert.Equal(m, IntrinsicGlyphScale.DisplayRadius(m), precision: 12);

        // The one bound left is MaxTrueMagnitude — the largest |Γ| a pointer may ever WRITE (R7A §1),
        // shared with TrueRadius so the two stay each other's exact inverse over the whole writable
        // range instead of only part of it.
        Assert.Equal(IntrinsicGlyphScale.MaxTrueMagnitude, IntrinsicGlyphScale.DisplayRadius(1e6), precision: 12);

        // The angle is NEVER touched — which band's glyph points where is real information.
        var g = Complex.FromPolarCoordinates(3.7, 1.234);
        var shown = IntrinsicGlyphScale.DisplayPosition(g);
        Assert.Equal(g.Phase, shown.Phase, precision: 12);
        Assert.Equal(3.7, shown.Magnitude, precision: 12);

        // Nothing is "compressed" any more, so the renderer's own compressed-glyph decoration is off
        // for every value rather than for values inside the disc only.
        Assert.False(IntrinsicGlyphScale.IsCompressed(g));
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
            // |Γ_intr| = 1.10, on the +real axis. This was 1.80 while IntrinsicGlyphScale compressed
            // everything past the rim into a bounded annulus, which kept ANY magnitude on-panel.
            // Round 10 turned that off (see IntrinsicGlyphScale.Compress), so a large overshoot is
            // now genuinely clipped at the panel edge — the trade the owner accepted. The claim this
            // test exists for is unchanged and is what 1.10 still exercises: a glyph outside the unit
            // circle is drawn OUTSIDE it, never clamped onto the rim and never dropped.
            GammaIntrinsic = new Complex(1.10, 0.00),
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

    // ══ TIER 8 — R8A §6: a hole is now SPANNED, at the pixel level ═══════════

    [Fact]
    public void Tier8_AGridWithAHole_NowDrawsContourPixelsInsideTheExcludedDisc()
    {
        // R8A §6 REVERSES the doctrine Tier 8 originally pinned: that one proved no PIXEL entered a
        // hole; this proves the opposite, on the same fixture and the same differential-render
        // oracle, because the owner ruled the surface model still covers the hole and an iso-line
        // should render there — see ContourGrid's own class doc comment for the reasoning and why it
        // depends on the hollow hole dot (asserted separately, above) staying drawn.
        //
        // The grid is built by ContourGrid itself — its own support mask (now just the convex hull;
        // the per-hole disc is opt-in) is the thing under test, so synthesising a mask here would
        // test the fixture instead.
        const int W = 520, H = 520;
        var theme = HarmonicaRenderTheme.Dark;

        var grid = BuildGridWithADeliberateHole(out var holeGamma, out double holeRadius);
        Assert.True(grid.HoleCount >= 1, "the fixture produced no hole — nothing to test");
        _out.WriteLine($"grid: {grid.Points.Count} Γ points, {grid.HoleCount} hole(s), " +
                       $"hole at {holeGamma}, mask radius {holeRadius:F3}");

        // Default excludeHoleDiscs: false — the SPANNING raster, exactly what HarmonicaSolver draws.
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
        var hits = new List<(int X, int Y, double R)>();
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
                hits.Add((x, y, r));
            }
        }

        _out.WriteLine($"{lit} contour pixels inside the (now-unexcluded) hole disc " +
                       $"(disc {discR:F1} px, probed to {discR * 0.80:F1} px)" +
                       (lit > 0 ? ": " + string.Join(", ",
                           hits.Take(6).Select(o => $"({o.X},{o.Y}) r={o.R:F1}/{discR:F1}")) : ""));

        Assert.True(lit > 0,
            "R8A §6 spans a hole with the fitted surface — a contour genuinely crossing it must leave " +
            "at least one differing pixel inside the disc, or the reversal isn't wired to the renderer");

        // NON-VACUITY: the same difference taken over the WHOLE panel must find plenty of contour
        // pixels — otherwise this would pass against a panel that drew no contours at all.
        int litSomewhere = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            if (!NearlySame(bmpWith.GetPixel(x, y), bmpWithout.GetPixel(x, y))) litSomewhere++;

        Assert.True(litSomewhere > 200,
            $"only {litSomewhere} contour pixels anywhere — the panel drew essentially nothing, so " +
            "the in-hole assertion above proves nothing.");
        _out.WriteLine($"{litSomewhere} contour pixels on the panel, {lit} of them inside the hole");
    }

    private static SKBitmap RenderSmith(SmithPanelData data, HarmonicaRenderTheme theme, int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (w, h), data, theme, darkMode: true);
        return SKBitmap.FromImage(surface.Snapshot());
    }

    /// <summary>A PinMax comfortably reached everywhere on this fixture's own grid — R9C made the
    /// ladder-based search robust enough that this SDD device now converges at every point up to
    /// maxGamma≈0.99 at this PinMax (measured; the pre-R9C fixture's own 2/31-holes-at-34-dBm figure
    /// relied on <c>PinSearch.Run</c>'s bracket being fragile near the compression boundary, which the
    /// ladder no longer is). See <see cref="BuildGridWithADeliberateHole"/>'s own remarks for how this
    /// fixture now gets its hole instead.</summary>
    private const double PinMaxForAFewHoles = 34.0;

    /// <summary>
    /// A real, converged <see cref="ContourGrid"/> with ONE point's own result overwritten to a hole.
    ///
    /// <para><b>R9C changed how this fixture gets its hole.</b> It used to rely on
    /// <c>PinSearch.Run</c>'s bracket search being fragile enough near a tuned PinMax boundary that a
    /// couple of Γ points would fail while the rest converged — measured at the time to be 2/31 at
    /// maxGamma 0.90. The ladder-based search R9C replaced <c>Run</c> with for grid points is
    /// deliberately more robust (that robustness is the whole point of R9C §3), so this fixture now
    /// converges EVERYWHERE up to maxGamma≈0.99 at the same PinMax — scanned directly, not assumed —
    /// which starved the old "tune PinMax until a few holes appear" approach: the transition from 0 to
    /// "most points" holing is a near-vertical cliff (this SDD's whole grid tends to cross the
    /// compression target within the SAME ladder rung), not the smooth few-holes gradient the old
    /// approach exploited. So the grid is now solved to full convergence first (a genuine "mostly
    /// converged" precondition, checked below), and exactly ONE already-converged point's own
    /// <see cref="GridPoint"/> is overwritten with a hand-built <see cref="PinStopReason.PinMax"/>
    /// result — the same mechanism §6.3 describes (a point that did not reach compression before
    /// PinMax), just placed rather than hunted for. This is a strictly BETTER fixture than the one it
    /// replaces: it no longer depends on a specific SDD's convergence boundary lining up with a hand-
    /// tuned PinMax, so it cannot be starved again by a future search-robustness improvement.
    /// </para>
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
                // brief-harmonicarf-r6a §5.2 — pinned explicitly rather than left on the default: this
                // fixture's hole count (2/31 at maxGamma 0.90, see the comment below) was measured
                // against 50 Ω, and Z0 changes which impedances a given Γ grid actually sweeps.
                Z0 = 50.0,
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
        grid.Build(ctx, terms, ContourGrid.RingGrid(rings: 3, spokes: 10, maxGamma: 0.90));

        // R9C — the fixture's OWN precondition is now "fully converged", not "a few holes": the hole
        // is injected below, deliberately, rather than hunted for via PinMax tuning (see this method's
        // own remarks for why). A future engine change degenerating THIS precondition still fails
        // loudly rather than quietly making the test meaningless.
        Assert.Equal(0, grid.HoleCount);

        // Overwrite one INTERIOR point (not Γ=0, and not an outer-ring point the convex hull would clip
        // anyway) with a hand-built PinMax hole — §6.3's own mechanism (a point that did not reach
        // compression before PinMax), just placed at a known location instead of hunted for.
        var points = (List<GridPoint>)typeof(ContourGrid)
            .GetField("_points", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(grid)!;
        int holeIndex = points.FindIndex(p => p.Gamma != Complex.Zero);
        Assert.True(holeIndex >= 0, "no non-origin point to hole");
        var target = points[holeIndex];
        points[holeIndex] = target with
        {
            Result = new PinSearchResult(PinStopReason.PinMax, target.Result.Solves) { Steps = [] },
        };

        Assert.Equal(1, grid.HoleCount);

        holeGamma  = points[holeIndex].Gamma;
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

    // ══ brief-harmonicarf-r6d §3 — panel titles ═════════════════════════════════════════════════════

    [Fact]
    public void LoadlineAndPowerSweepPanels_AreTitled_AndTheirViewportsStillLineUp()
    {
        var theme = HarmonicaRenderTheme.Dark;

        var loadlinePlot   = HarmonicaPanelRenderer.BuildLoadlinePlot(new LoadlinePanelData(), theme);
        var powerSweepPlot = HarmonicaPanelRenderer.BuildPowerSweepPlot(new PowerSweepPanelData(), theme);

        Assert.Equal("Loadline",    loadlinePlot.Title);
        Assert.Equal("Power Sweep", powerSweepPlot.Title);

        // The two panels deliberately share ONE pinned viewport shape (PowerSweepShapedViewport,
        // R-h9b-11) so their data rectangles line up; a title must not move that — it is
        // unconditionally pinned, never re-derived from ComputeViewport's own title-aware top-margin
        // formula (which only applies to Smith/Polar plots in the first place).
        Assert.Equal(loadlinePlot.Axes.Viewport, powerSweepPlot.Axes.Viewport);

        // The reserved top margin (10% of the panel, R-h9b-11's own shape) must still leave the bulk
        // of the panel to the chart — a squeezed plot would show as this collapsing toward zero.
        Assert.True(loadlinePlot.Axes.Viewport.Height > 0.7,
            $"the title band must not squeeze the plot area (Viewport.Height={loadlinePlot.Axes.Viewport.Height})");
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

    // ══ R9A §5 — owner ruling: the dashed operating-point cursor is removed from the power-sweep
    //    plot entirely. A pixel probe at one column cannot separate the cursor from a grid line (the
    //    same trap H4–H5 recorded for iso-lines vs Smith chrome), so the honest oracle is DIFFERENTIAL:
    //    the same panel drawn at CursorIndex = -1 (no cursor) and at a valid index must now be
    //    pixel-identical, because nothing reads CursorIndex to draw a mark any more.

    [Fact]
    public void PowerSweepPanel_RendersIdentically_RegardlessOfCursorIndex()
    {
        const int W = 480, H = 380;
        var theme = HarmonicaRenderTheme.Dark;

        int n = 33;
        var pin  = Enumerable.Range(0, n).Select(i => -10.0 + i * 1.25).ToArray();
        var gain = pin.Select(p => 14.5 - 4.0 * Math.Log(1 + Math.Exp((p - 18) * 0.45))).ToArray();
        PowerSweepPanelData Data(int cursorIndex) => new()
        {
            PinAvailDbm   = pin,
            PoutDbm       = pin.Zip(gain, (p, g) => p + g).ToArray(),
            GainDb        = gain,
            EfficiencyPct = pin.Select(p => 72.0 / (1 + Math.Exp(-(p - 14) * 0.30))).ToArray(),
            CursorIndex   = cursorIndex,
        };

        byte[] Render(int cursorIndex)
        {
            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            surface.Canvas.Clear(theme.Background);
            HarmonicaPanelRenderer.DrawPowerSweepPanel(surface.Canvas, (W, H), Data(cursorIndex), theme, darkMode: true);
            using var bmp = SKBitmap.FromImage(surface.Snapshot());
            return bmp.Bytes;
        }

        Assert.Equal(Render(-1), Render(20));
    }

    // ══ brief-harmonicarf-r6d §2 — right-side headroom past the sweep stop ═════════════════════════

    [Theory]
    [InlineData(PowerSweepXUnit.PoutDbm)]
    [InlineData(PowerSweepXUnit.PoutW)]
    [InlineData(PowerSweepXUnit.PinAvailDbm)]
    [InlineData(PowerSweepXUnit.PinAvailW)]
    public void PowerSweepPlot_XAxis_HasHeadroomPastTheSweepStop_ForEveryXUnit(PowerSweepXUnit unit)
    {
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
            XUnit         = unit,
            // The sweep's own configured range matches the last solved point exactly, so the
            // PRE-fix window right edge lands exactly at max(x) — the failure mode being fixed —
            // for the Pin-domain units too (PinAxisPin overrides the AutoScale window with this
            // range), not just Pout-domain (where AutoScale's own Pad adds no X margin at all).
            PinStartDbm   = pin[0],
            PinMaxDbm     = pin[^1],
        };

        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(d, theme);
        double maxX = unit.Values(d).Max();

        Assert.True(plot.Axes.Window.Right > maxX,
            $"expected the window's right edge ({plot.Axes.Window.Right}) to clear the data's own " +
            $"max X ({maxX}) — the curve used to end exactly on the border");

        Assert.Equal(plot.Axes.Window.Left,  plot.Axes.WindowSecondary.Left,  precision: 9);
        Assert.Equal(plot.Axes.Window.Width, plot.Axes.WindowSecondary.Width, precision: 9);
    }

    // ══ R-h9r2-23 — the "Efficiency (%)" / "PAE (%)" LABEL itself, not just the axis line/ticks ═════

    [Theory]
    [InlineData(GridMetric.DrainEfficiency)]
    [InlineData(GridMetric.Pae)]
    public void PowerSweepPanel_TheY2Label_RendersInEfficiencyTrace_ExactlyOverTheOriginal(GridMetric metric)
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
            EfficiencyMetric = metric,
        };

        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(d, theme);
        var rects = CircuitRF.Ui.DataDisplay.AxesRenderer.ComputeLabelHitRects(plot, (W, H));
        Assert.True(rects.Y2Label.Width > 0 || rects.Y2Label.Height > 0, "expected a Y2 label rect");

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawPowerSweepPanel(surface.Canvas, (W, H), d, theme, darkMode: true);
        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        // Sample WITHIN the label's own hit rect (inclusive, clamped to the bitmap) — this is
        // specifically the LABEL region, not the axis line/ticks (which sit outside the viewport,
        // to the label's own left, and were already covered by the pre-existing "efficiency trace
        // renders somewhere" test).
        int x0 = Math.Max(0, (int)Math.Floor(rects.Y2Label.Left));
        int x1 = Math.Min(W - 1, (int)Math.Ceiling(rects.Y2Label.Right));
        int y0 = Math.Max(0, (int)Math.Floor(rects.Y2Label.Top));
        int y1 = Math.Min(H - 1, (int)Math.Ceiling(rects.Y2Label.Bottom));

        bool found = false;
        for (int y = y0; y <= y1 && !found; y++)
        for (int x = x0; x <= x1 && !found; x++)
            if (NearlySame(bmp.GetPixel(x, y), theme.EfficiencyTrace)) found = true;

        Assert.True(found,
            $"no {theme.EfficiencyTrace} pixel found within the Y2 label's own hit rect " +
            $"({x0},{y0})-({x1},{y1}) — the label text must be drawn exactly there.");
    }

    // ══ brief-harmonicarf-r6d §1 — the right axis is drawn ONCE, not covered ═══════════════════════

    [Fact]
    public void PowerSweepPanel_RightAxis_NoOrdinaryAxisColourSurvivesUnderneathTheOverlay()
    {
        const int W = 480, H = 380;
        var theme = HarmonicaRenderTheme.Dark;

        int n = 33;
        var pin  = Enumerable.Range(0, n).Select(i => -10.0 + i * 1.25).ToArray();
        var gain = pin.Select(p => 14.5 - 4.0 * Math.Log(1 + Math.Exp((p - 18) * 0.45))).ToArray();
        var d = new PowerSweepPanelData
        {
            PinAvailDbm     = pin,
            PoutDbm         = pin.Zip(gain, (p, g) => p + g).ToArray(),
            GainDb          = gain,
            EfficiencyPct   = pin.Select(p => 72.0 / (1 + Math.Exp(-(p - 14) * 0.30))).ToArray(),
            CursorIndex     = 20,
            ReachedCompression = true,
        };

        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(d, theme);
        var tf    = CircuitRF.Ui.DataDisplay.PlotRenderer.BuildTransforms(plot, (W, H));
        var rects = CircuitRF.Ui.DataDisplay.AxesRenderer.ComputeLabelHitRects(plot, (W, H));

        var topRight = tf.PrimaryToCanvas(plot.Axes.Window.Right, plot.Axes.Window.Top);
        var botRight = tf.PrimaryToCanvas(plot.Axes.Window.Right, plot.Axes.Window.Bottom);

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawPowerSweepPanel(surface.Canvas, (W, H), d, theme, darkMode: true);
        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        // The right-axis line itself (a few px either side of the border x), the tick-number band to
        // its right, and the rotated Y2-label rect — the three places the two prior attempts (R3C §5,
        // R-h9r2-23) redrew a cover over. Clear of the BOTTOM border's own legitimate AxisLine-coloured
        // corner pixel (that border still spans the full width — see DrawBorder — it is a different
        // stroke, not the bug), and of the X-axis tick labels below it.
        int xLo = Math.Max(0, (int)topRight.X - 4);
        int xHi = Math.Min(W - 1, (int)Math.Ceiling(rects.Y2Label.Right) + 2);
        int yLo = Math.Max(0, (int)topRight.Y + 2);
        int yHi = Math.Min(H - 1, (int)botRight.Y - 6);

        bool axisColourSurvives = false;
        int hitX = -1, hitY = -1;
        for (int y = yLo; y <= yHi && !axisColourSurvives; y++)
        for (int x = xLo; x <= xHi; x++)
            if (NearlySame(bmp.GetPixel(x, y), theme.AxisLine))
            {
                axisColourSurvives = true; hitX = x; hitY = y; break;
            }

        Assert.False(axisColourSurvives,
            $"the right axis must be drawn ONCE, in Harmonica.EfficiencyTrace — found the ordinary " +
            $"AxisLine colour at ({hitX},{hitY}), the exact fringe the previous two 'match the cover " +
            $"to the covered' fixes could not eliminate because the covered stroke was still there.");

        Assert.True(HasColourNear(bmp, theme.EfficiencyTrace, tolerance: 40),
            "the efficiency axis must still render — just once, not zero times");
    }

    // ══ brief-harmonicarf-r6d §5 — the Time Domain view ═════════════════════════════════════════

    [Fact]
    public void TimeDomainPanel_DrawsVdsInGainTrace_AndIdsInLoadline_WithTheRightAxisPaintedOnce()
    {
        const int W = 480, H = 380;
        var theme = HarmonicaRenderTheme.Dark;

        var d = new LoadlinePanelData
        {
            LoadlineVds = [0.0, 10.0, 20.0, 30.0, 20.0, 10.0, 0.0],
            LoadlineIds = [0.9, 0.6, 0.3, 0.05, 0.3, 0.6, 0.9],
            FrequencyHz = 2e9,
        };

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawTimeDomainPanel(surface.Canvas, (W, H), d, theme, darkMode: true);
        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        Assert.True(HasColourNear(bmp, theme.GainTrace, tolerance: 40),
            "Vds(t) must render in Harmonica.GainTrace, the SAME colour as the power-sweep gain trace");
        Assert.True(HasColourNear(bmp, theme.Loadline, tolerance: 40),
            "Ids(t) must render in Harmonica.Loadline, the reserved red the loadline panel uses");

        // §1's fix applies here too: the right axis must never carry the ordinary AxisLine colour —
        // it is drawn ONCE, in Harmonica.Loadline, through the SAME colour-parametrized overlay.
        var plot  = HarmonicaPanelRenderer.BuildTimeDomainPlot(d, theme);
        var tf    = CircuitRF.Ui.DataDisplay.PlotRenderer.BuildTransforms(plot, (W, H));
        var rects = CircuitRF.Ui.DataDisplay.AxesRenderer.ComputeLabelHitRects(plot, (W, H));
        var topRight = tf.PrimaryToCanvas(plot.Axes.Window.Right, plot.Axes.Window.Top);
        var botRight = tf.PrimaryToCanvas(plot.Axes.Window.Right, plot.Axes.Window.Bottom);

        int xLo = Math.Max(0, (int)topRight.X - 4);
        int xHi = Math.Min(W - 1, (int)Math.Ceiling(rects.Y2Label.Right) + 2);
        int yLo = Math.Max(0, (int)topRight.Y + 2);
        int yHi = Math.Min(H - 1, (int)botRight.Y - 6);

        bool axisColourSurvives = false;
        for (int y = yLo; y <= yHi && !axisColourSurvives; y++)
        for (int x = xLo; x <= xHi; x++)
            if (NearlySame(bmp.GetPixel(x, y), theme.AxisLine)) { axisColourSurvives = true; break; }

        Assert.False(axisColourSurvives,
            "the time-domain panel's right axis must be drawn ONCE, in Harmonica.Loadline");
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

    // ══ R-h9r2-13 — both title rows share one font size, and the chart still renders exactly where
    //    MarkerToCanvas says it does, at more than one panel size ══════════════════════════════════

    [Theory]
    [InlineData(300, 300)]
    [InlineData(460, 460)]
    [InlineData(600, 380)]
    public void TitledSmithPanel_MarkerStillPaintsExactlyAtMarkerToCanvas(int W, int H)
    {
        var theme = HarmonicaRenderTheme.Dark;
        var marker = new HarmonicaMarker(TerminationSideKind.Load, 1) { Gamma = new Complex(0.35, -0.20) };
        var data = new SmithPanelData
        {
            Title = "Power (dBm)", Subtitle = "Efficiency (%)", Markers = [marker],
        };

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), data, theme, darkMode: true);
        using var bmp = SKBitmap.FromImage(surface.Snapshot());

        var at = HarmonicaPanelRenderer.GammaToCanvas(marker.Gamma, (W, H));
        int cx = (int)Math.Round(at.X), cy = (int)Math.Round(at.Y);

        var expected = theme.MarkerBand(marker.Band);
        bool found = false;
        for (int dx = -1; dx <= 1 && !found; dx++)
        for (int dy = -1; dy <= 1 && !found; dy++)
            if (NearlySame(bmp.GetPixel(cx + dx, cy + dy), expected)) found = true;

        Assert.True(found,
            $"expected marker fill {expected} within 1px of GammaToCanvas ({cx},{cy}) at {W}x{H} " +
            "with both title rows drawn — render and the published transform must agree.");
    }

    [Fact]
    public void TitleRows_BothUseTheSameFontSize_PerTheOwnersMatchRequest()
    {
        string src = ReadSourceFile("src", "Ui", "Harmonica", "Renderers", "HarmonicaPanelRenderer.cs");

        // R-h9r2-13: "make the row 1 text size of the Smith Charts be the same as row 2" — the two
        // separate fractions (row 1 at 0.052, row 2 at 0.82x that) are gone in favour of one.
        Assert.DoesNotContain("TitleRow1FontFraction", src, StringComparison.Ordinal);
        Assert.DoesNotContain("TitleRow2FontFraction", src, StringComparison.Ordinal);
        Assert.Contains("TitleRowFontFraction", src, StringComparison.Ordinal);

        // DrawTitleRows must build both fonts from the SAME rowSize variable.
        int m = src.IndexOf("private static void DrawTitleRows(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    }", m, StringComparison.Ordinal);
        string body = src[m..mEnd];
        Assert.Contains("new SKFont(SkiaFonts.PlexBold,    rowSize)", body, StringComparison.Ordinal);
        Assert.Contains("new SKFont(SkiaFonts.PlexRegular, rowSize)", body, StringComparison.Ordinal);
    }

    private static string ReadSourceFile(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.File.ReadAllText(System.IO.Path.Combine([dir!.FullName, .. parts]));
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
