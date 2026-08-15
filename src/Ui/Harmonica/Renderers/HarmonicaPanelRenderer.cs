using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Renderers;
using RfCore;
using SkiaSharp;

namespace CircuitRF.Ui.Harmonica.Renderers;

/// <summary>
/// The four §7.1 panels, drawn.
///
/// <para><b>These are not a second plot renderer.</b> §2's table lists the Smith/Rect plot, its axes
/// and ticks, the iso-line drawing, the grid points and the optima markers as things that already
/// exist and must not be reimplemented — so each panel builds an ordinary <see cref="Plot"/> and
/// hands it to <see cref="PlotRenderer.Draw"/>, with <see cref="HarmonicaRenderTheme.ToPlotTheme"/>
/// supplying harmonicaRF's own palette. What lives here is only the part the existing stack has no
/// concept of: harmonicaRF's markers and their per-band colours, the intrinsic glyphs beneath them,
/// the hollow hole dots, and §7.2's ranked alpha ramp.</para>
/// </summary>
public static class HarmonicaPanelRenderer
{
    // ── shared geometry ──────────────────────────────────────────────────────

    /// <summary>The Γ-plane transform for a Smith/Polar panel of this size — the SAME one
    /// <see cref="PlotRenderer.BuildTransforms"/> produces, so overlays land exactly on the chart the
    /// existing renderer drew rather than on a hand-derived approximation of it.</summary>
    private static TransformSet GammaTransform(Plot plot, (double W, double H) size)
        => PlotRenderer.BuildTransforms(plot, size);

    /// <summary>
    /// R-h9b-1's ROOT CAUSE, found while diagnosing the dead marker/grid-point drags: this used to set
    /// <c>CustomTitleOn</c>/<c>CustomTitle</c> from <see cref="SmithPanelData.Title"/>. A non-empty
    /// <c>Plot.Title</c> makes <c>PlotRenderer.ComputeViewport</c> reserve extra top margin for it
    /// (title-sized, via <c>topExtra</c>) — so the RENDERED chart sat shifted down from where
    /// <see cref="HitTestTransform"/>'s bare, always-untitled plot placed it. Every marker and grid
    /// point was therefore drawn in one place and hit-tested in another, offset by the reserved title
    /// band — which is exactly "click on a visible marker, nothing grabs". <c>Title</c>/<c>Subtitle</c>
    /// are drawn OURSELVES in <see cref="DrawSmithPanel"/> instead (R-h9b-4's two rows), reserved via
    /// <see cref="TitleBandHeight"/> and folded into the SAME transform the hit test uses — so render
    /// and hit-test can never disagree about where the chart's own viewport starts again.
    /// </summary>
    private static Plot NewSmithPlot() => new(PlotType.Smith, FreqUnit.GHz) { ShowWatermark = false };

    /// <summary>R-h9b-5 — the title font shrinks by this factor from the size §7.9's Data Display
    /// plots would otherwise use. Applied on harmonicaRF's side, never in <c>PlotRenderer</c> — every
    /// Data Display Smith plot reads that renderer's own title font and must not move.</summary>
    private const double TitleFontShrink = 0.8;

    /// <summary>Both title rows' base font size, as a fraction of the panel's shorter side, BEFORE
    /// R-h9b-5's 0.8× — the same panel-relative sizing convention every other glyph/marker size in
    /// this file uses.
    ///
    /// <para><b>R-h9r2-13 — row 2 used to be 0.82× row 1's fraction; the owner asked for the two rows
    /// to MATCH ("make the row 1 text size of the Smith Charts be the same as row 2"), so there is now
    /// only one fraction and one floor, shared by both rows and by <see cref="TitleBandHeight"/>.</b>
    /// The 0.8× shrink itself (R-h9b-5) is untouched — the owner asked for the rows to match each
    /// other, not for either to grow.</para>
    /// </summary>
    private const double TitleRowFontFraction = 0.052;

    /// <summary>
    /// R3C §4 — owner: "Make Smith chart title text size 85%." One more shrink factor on top of
    /// <see cref="TitleFontShrink"/>, named per the same rule R-h9r2-21 established ("make this text
    /// size a variable in the code") — this is the second such tweak, so the precedent already exists
    /// rather than being invented here.
    ///
    /// <para><b>The 7.0 pt floor in <see cref="TitleBandHeight"/> is deliberately NOT scaled by this
    /// factor.</b> It already reads as a readability minimum, not a proportional shrink target — a
    /// panel small enough to be clamped there is already at the smallest legible size, and multiplying
    /// the floor by 0.85 would only make an already-clamped title harder to read for no space
    /// saved (the clamp exists so nothing gets smaller than 7 pt, and shrinking the clamp itself
    /// defeats that).</b></para>
    /// </summary>
    private const double TitleSizeR3C = 0.85;

    /// <summary>
    /// R3C §4 — owner: "move the title down… so it renders closer to the Smith Chart. (I.e. the
    /// bottom of row 2 text should be above the Smith Chart with some padding.)" This is that
    /// padding, named and factored out of what used to be an inline <c>m * 0.01</c> magic fraction
    /// used for two jobs at once (part of <see cref="TitleBandHeight"/>'s own total, AND row 2's
    /// baseline offset in <see cref="DrawTitleRows"/>). Halved from the old 0.01 — row 2's baseline
    /// sits at <c>TitleBandHeight - TitleBottomPaddingFraction * m</c>, so a smaller value moves the
    /// text closer to the chart directly, on top of the closeness §4's 85% shrink already buys by
    /// making both rows shorter.
    /// </summary>
    private const double TitleBottomPaddingFraction = 0.005;

    /// <summary>
    /// R-h9b-4 — how much of the panel's height the two title rows reserve, in PIXELS, given the row
    /// font size above. Computed from the actual font metrics rather than a fixed fraction, so a very
    /// short or very tall panel does not waste — or run out of — title space.
    ///
    /// <para><b>R-h9r2-13 — the band itself is UNCHANGED in shape (still 1.3× line-height per row plus
    /// a hair of padding); only the two rows now share one font size instead of row 2 being smaller.
    /// The chart below therefore shifts down slightly (a taller row 2) — that is the equalisation's own
    /// footprint, not something hidden. The "too high" complaint is fixed separately, inside the band,
    /// by <see cref="DrawTitleRows"/>'s own baselines below — moving the text down within an unchanged
    /// band reduces the gap above the chart without costing any chart real estate.</b></para>
    ///
    /// <para><b>R3C §4 — now literally <c>rows + padding</c></b>, both named constants
    /// (<see cref="TitleSizeR3C"/>, <see cref="TitleBottomPaddingFraction"/>) rather than one fraction
    /// doing double duty. <see cref="DrawTitleRows"/> derives row 2's baseline from THIS SAME padding
    /// term, so the two can never disagree about where the gap above the chart actually is.</para>
    ///
    /// <para><b>brief-harmonicarf-r6b §4.1 — PUBLIC</b> so the fly-menu dispatch
    /// (<c>HarmonicaView.OnCanvasContextMenuOpening</c>) can resolve a title-band click against the
    /// SAME geometry this file draws into, rather than hand-deriving the band height a second time.</para>
    /// </summary>
    public static double TitleBandHeight((double W, double H) size)
    {
        double m = Math.Min(size.W, size.H);
        double row = Math.Max(7.0, m * TitleRowFontFraction * TitleFontShrink * TitleSizeR3C);
        double padding = m * TitleBottomPaddingFraction;
        // 1.3× line-height per row (ascender/descender headroom) plus the named bottom padding.
        return row * 1.3 + row * 1.3 + padding;
    }

    /// <summary>
    /// The Rect plots (Loadline/DCIV, Power Sweep, Time Domain) draw their own <c>CustomTitle</c>
    /// through the SHARED <c>AxesRenderer.DrawTitleAndAxisLabels</c>, whose Rect title formula is
    /// <c>Axes.FontSizeLabel * 1.4 * AxesRenderer.LineWidth(canvasSize)</c>, and
    /// <c>LineWidth = min(W,H) / 200</c> — so the rendered title height is
    /// <c>Axes.FontSizeLabel * 1.4 * min(W,H) / 200</c>, proportional to the panel's shorter side just
    /// like <see cref="TitleBandHeight"/>'s own row height above, only with a different constant.
    /// Setting <c>Axes.FontSizeLabel</c> to this value makes the two proportional-to-<c>min(W,H)</c>
    /// formulas equal at every panel size, not just one: solving
    /// <c>FontSizeLabel * 1.4 / 200 = TitleRowFontFraction * TitleFontShrink * TitleSizeR3C</c> for
    /// <c>FontSizeLabel</c>. <c>Axes.FontSizeLabel</c> is used for NOTHING ELSE on a Rect plot (the
    /// X/Y axis labels use <c>FontSizeTicks</c> instead), so this only ever touches the title.
    /// </summary>
    internal const double RectTitleFontSizeLabel =
        TitleRowFontFraction * TitleFontShrink * TitleSizeR3C * 200.0 / 1.4;

    /// <summary>
    /// R3C follow-up (2026-08-13) — "add slightly more margin around the Smith charts, ~20 more
    /// pixels." Expressed as a FRACTION of the panel's own shorter side, the same panel-relative
    /// convention every other constant in this file uses (<see cref="TitleRowFontFraction"/> etc.), and
    /// for the identical reason: <c>HarmonicaDragTests.
    /// Tier2_TheGrabRadiusIsTheSameNumberOfPixelsOnA300pxPanelAndA900pxOne</c> pins that a hit test in
    /// DEVICE PIXELS reads the same at every panel size, which only holds if every term in the
    /// Γ↔canvas pipeline is exactly proportional to panel size — a FLAT pixel constant (tried first)
    /// broke that invariant (14px on a 300px canvas, 15px on a 900px one) because a fixed cost is
    /// relatively larger on a small panel than a large one. 0.03 (3% of the shorter side) reads as
    /// ~18–20px at the panel sizes this file's own R3B history records as typical (600–650px shorter
    /// side), matching the owner's own estimate there without reintroducing a size-dependent wobble.
    /// <b>Distinct from <see cref="AnnulusHeadroom"/> on purpose</b> — that mechanism (currently 0)
    /// exists to guarantee an out-of-circle glyph is never clipped and is a fraction of the RIM radius;
    /// this one is plain cosmetic breathing room between the chart and the panel's own edges (and the
    /// title band above it), requested separately and for a different reason.
    /// </summary>
    private const double ChartMarginFraction = 0.03;

    /// <summary>
    /// The box the Smith chart actually draws and hit-tests into: <paramref name="size"/> minus the
    /// title band, minus <see cref="ChartMarginFraction"/> of the panel's shorter side on every side —
    /// plus the (x, y) canvas offset of that box's own top-left corner, so <see cref="DrawSmithPanel"/>,
    /// <see cref="GammaToCanvas"/> and <see cref="CanvasToGamma"/> can never disagree about where it
    /// starts. One shared computation rather than three independent ones, for the same "render and
    /// hit-test must never diverge" reason <see cref="TitleBandHeight"/> is itself already shared by
    /// all three.
    /// </summary>
    private static ((double W, double H) Box, double OffsetX, double OffsetY) ChartBox(
        (double W, double H) size, double bandH)
    {
        double margin = Math.Min(size.W, size.H) * ChartMarginFraction;
        (double W, double H) chartSize = (size.W, size.H - bandH);
        (double W, double H) box = (Math.Max(1.0, chartSize.W - 2 * margin),
                                     Math.Max(1.0, chartSize.H - 2 * margin));
        return (box, margin, bandH + margin);
    }

    // ── §7.2 — the Smith panels ──────────────────────────────────────────────

    /// <summary>
    /// Draws one Smith panel: contours (with §7.2's ranked alpha ramp), grid points including the
    /// hollow hole dots, MXP/MXE, then the intrinsic glyphs, then the termination markers on top.
    ///
    /// <para><b>Z-order is load-bearing, not cosmetic.</b> R-h45-4: glyphs are "always BENEATH the
    /// round termination markers". A glyph that covered its marker would hide the thing the user is
    /// dragging.</para>
    /// </summary>
    public static void DrawSmithPanel(SKCanvas canvas, (double W, double H) size,
                                      SmithPanelData d, HarmonicaRenderTheme theme, bool darkMode,
                                      bool showGridPoints = true, HarmonicaMarker? topmostMarker = null,
                                      HarmonicaBackdropCache? cache = null, double deviceScale = 1.0,
                                      bool showIsoLineLabels = false)
    {
        // R-h9b-4 — the two title rows are drawn OURSELVES, in the panel's own top strip, BEFORE the
        // chart transform below — never through PlotRenderer's CustomTitle (see NewSmithPlot's doc
        // comment for why: that path used to shift the render out of step with the hit test).
        double bandH = TitleBandHeight(size);
        DrawTitleRows(canvas, size, bandH, d, theme);

        // Everything below draws into the sub-rect BENEATH the title band, inset by ChartMargin on
        // every side. This is exactly the box GammaToCanvas/CanvasToGamma compute from the same
        // ChartBox helper, so a render position and a hit-tested position can never disagree about
        // where the drawn chart actually starts.
        var (chartSize, offsetX, offsetY) = ChartBox(size, bandH);

        canvas.Save();
        canvas.Translate((float)offsetX, (float)offsetY);

        // ── ANNULUS HEADROOM — owner-disabled, R3C follow-up, 2026-08-13 ────────
        //
        // This USED TO shrink the whole panel 20% so a compressed out-of-circle glyph (R-h45-4, §4.5
        // consequence 2, never clamped, never hidden) always had room and could never be clipped at
        // the panel edge — not cosmetic, found by a failing pixel oracle. It was ALSO the dominant
        // cause of a third owner-reported "title still too high above the chart": that 20% shrink
        // measured out to ~11% of the chart's own height as dead space above the visible circle, which
        // no amount of tuning the title band's own few-pixel padding could ever have closed. Presented
        // with the trade-off, the owner chose to remove the margin (AnnulusHeadroom is now 0 — see its
        // own doc comment for the full reasoning) and accept that a sufficiently far-out intrinsic
        // glyph can be clipped again. The mechanism itself is left in place, at k=1 (identity), rather
        // than unwound — see AnnulusHeadroom's own note on why that is the safer edit.
        float k = (float)(1.0 / (1.0 + AnnulusHeadroom));
        float cx = (float)(chartSize.W / 2), cy = (float)(chartSize.H / 2);

        var plot = NewSmithPlot();
        // tf depends only on chartSize (the Smith plot carries no traces to autoscale against — the
        // Γ-plane window is always the fixed unit circle) so it is cheap to (re)compute regardless of
        // whether Layer A's own chrome pixels came from cache or a fresh draw.
        var tf = GammaTransform(plot, chartSize);

        if (cache is null)
        {
            // ── the ORIGINAL, always-available uncached path — re-using the plot/tf built above
            // instead of rebuilding them. R8A §4.1 is the first thing to make this diverge from the
            // cached path's own draw call by more than that: both now also thread showIsoLineLabels.
            canvas.Save();
            canvas.Translate(cx, cy);
            canvas.Scale(k);
            canvas.Translate(-cx, -cy);

            PlotRenderer.Draw(canvas, chartSize, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                              watermarkOpacity: 0f);

            canvas.Save();
            canvas.ClipRect(PlotRenderer.ViewportClipRect(tf.Viewport, chartSize));
            DrawContours(canvas, d, tf, theme, chartSize, showIsoLineLabels);
            if (showGridPoints) DrawGridPoints(canvas, d, tf, theme, chartSize);
            // brief-harmonicarf-r6b §3 — the MXP/MXE optimum cross is no longer drawn here (deferred
            // to v2); d.Optimum stays populated for the readout columns (HarmonicaSolver.AddMxColumn),
            // this file just stopped rendering it.
            // brief-harmonicarf-r4 §4.4 — moved from BENEATH the chrome to here (still beneath the
            // glyphs/markers below, still clipped to the viewport) so this path and the cached one
            // below are provably pixel-identical for ANY scene, reachable region included, rather than
            // only for the common case where Reachable is null. See DrawSmithPanelCached's own note.
            DrawReachableRegion(canvas, d, tf, theme);
            canvas.Restore();

            DrawIntrinsicGlyphs(canvas, d, tf, theme, chartSize);
            DrawMarkers(canvas, d, tf, theme, chartSize, topmostMarker);

            canvas.Restore();
            canvas.Restore();
            return;
        }

        DrawSmithPanelCached(canvas, chartSize, cx, cy, k, plot, tf, d, theme, darkMode,
                             showGridPoints, topmostMarker, cache, deviceScale, showIsoLineLabels);
        canvas.Restore();
    }

    /// <summary>
    /// brief-harmonicarf-r4 §4 — the cached half of <see cref="DrawSmithPanel"/>: Layer A (Smith
    /// chrome + frozen contour polylines — the optimum cross is no longer part of it, see
    /// brief-harmonicarf-r6b §3) and Layer B (the grid-point dots) are
    /// each rendered once into an offscreen surface and blitted back — a drag frame's own marker
    /// glyph, the live termination markers and R-h6-12's reachable region are the only things drawn
    /// fresh every frame.
    ///
    /// <para><b>Z-order note.</b> The reachable region moved from BENEATH the chrome/contours to
    /// ABOVE Layer A/B (still beneath glyphs/markers) — see <see cref="DrawSmithPanel"/>'s own mirrored
    /// change. It is a light, ~22%-alpha wash (<see cref="DrawReachableRegion"/>), so the visual
    /// difference is minor, and moving it there is what lets a cached and an uncached frame be
    /// PROVABLY pixel-identical for any scene rather than only for the (common, but not universal)
    /// case where no reachable region is showing.</para>
    /// </summary>
    private static void DrawSmithPanelCached(
        SKCanvas canvas, (double W, double H) chartSize, float cx, float cy, float k,
        Plot plot, TransformSet tf, SmithPanelData d, HarmonicaRenderTheme theme, bool darkMode,
        bool showGridPoints, HarmonicaMarker? topmostMarker, HarmonicaBackdropCache cache,
        double deviceScale, bool showIsoLineLabels)
    {
        // §4's own correctness gate demands a BIT-EXACT match against the uncached vector draw. An
        // offscreen raster's own pixel grid is always phase-0 at ITS local origin, but the live canvas
        // places chart-local (0,0) at whatever FRACTIONAL device pixel its accumulated transform
        // (`canvas.TotalMatrix` — any outer HiDPI scale, this panel's own position, ChartBox's
        // margin/title-band offset, none of which is generally pixel-integral) happens to land on.
        // Rasterising Layer A/B at local-origin phase 0 and then blitting onto that fractional device
        // position forces Skia to RESAMPLE the whole image on every blit — reprocessing every
        // antialiased edge in the backdrop, which is what `HarmonicaBackdropCacheTests` actually caught
        // (~5% of pixels, up to 199 levels off — nothing like ordinary ±1 AA rounding). Fixed by baking
        // the SAME matrix into the offscreen render (so its AA phase matches exactly), shifted by only
        // an INTEGER number of device pixels (`floorX`/`floorY` — an integer translate does not change
        // AA phase), then blitting that integer shift back in raw device space (`SetMatrix(Identity)`,
        // bypassing whatever CTM was active) — an integer-aligned, same-size copy needs no resampling.
        var m = canvas.TotalMatrix;
        var origin = m.MapPoint(0, 0);
        float floorX = MathF.Floor(origin.X), floorY = MathF.Floor(origin.Y);
        var offscreenMatrix = SKMatrix.Concat(SKMatrix.CreateTranslation(-floorX, -floorY), m);

        // +1 padding in each dimension: baking a sub-pixel phase into the raster can push content up to
        // one extra device pixel past the plain Ceiling(chartSize * deviceScale) size.
        var pixelSize = new SKSizeI(
            (int)Math.Ceiling(Math.Max(1.0, chartSize.W) * deviceScale) + 1,
            (int)Math.Ceiling(Math.Max(1.0, chartSize.H) * deviceScale) + 1);

        void Blit(SKImage img)
        {
            canvas.Save();
            canvas.SetMatrix(SKMatrix.Identity);
            canvas.DrawImage(img, new SKRect(floorX, floorY, floorX + img.Width, floorY + img.Height));
            canvas.Restore();
        }

        var themeKey = BackdropThemeKey.From(theme, darkMode);
        // brief-harmonicarf-r6b §3 — Optimum dropped from the key: nothing in Layer A reads it any
        // more (the cross is not drawn), so keeping it here meant the whole cached layer was thrown
        // away every time the optimum moved during a drag, for a pixel difference that no longer exists.
        var layerAKey = new LayerAKey(chartSize, themeKey, d.Title, d.Subtitle, showIsoLineLabels,
                                      d.Contours, d.Levels, offscreenMatrix);

        var imgA = cache.GetOrRenderLayerA(layerAKey, pixelSize, offscreenMatrix, theme.Background, offscreen =>
        {
            // The SAME translate/scale/translate the uncached path applies live, baked into the
            // raster instead — a future non-zero AnnulusHeadroom stays correct without touching this.
            offscreen.Save();
            offscreen.Translate(cx, cy);
            offscreen.Scale(k);
            offscreen.Translate(-cx, -cy);

            PlotRenderer.Draw(offscreen, chartSize, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                              watermarkOpacity: 0f);
            offscreen.Save();
            offscreen.ClipRect(PlotRenderer.ViewportClipRect(tf.Viewport, chartSize));
            DrawContours(offscreen, d, tf, theme, chartSize, showIsoLineLabels);
            offscreen.Restore();
            offscreen.Restore();
        });
        if (showGridPoints)
        {
            // Fused, not blitted as its own translucent layer — see HarmonicaBackdropCache's own note
            // on why two separately-composited translucent layers cannot be bit-exact.
            var layerBKey = new LayerBKey(chartSize, themeKey, d.GridPoints, offscreenMatrix, pixelSize);
            var fused = cache.GetOrRenderFusedWithLayerB(layerBKey, imgA, offscreenMatrix, offscreen =>
            {
                offscreen.Save();
                offscreen.Translate(cx, cy);
                offscreen.Scale(k);
                offscreen.Translate(-cx, -cy);
                offscreen.Save();
                offscreen.ClipRect(PlotRenderer.ViewportClipRect(tf.Viewport, chartSize));
                DrawGridPoints(offscreen, d, tf, theme, chartSize);
                offscreen.Restore();
                offscreen.Restore();
            });
            Blit(fused);
        }
        else
        {
            Blit(imgA);
        }

        // Live from here on: the reachable region (its own gesture, changes independently of both
        // layers above) and every marker/glyph — same transform the cached layers were baked with,
        // applied to the live canvas so everything lines up.
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(k);
        canvas.Translate(-cx, -cy);

        canvas.Save();
        canvas.ClipRect(PlotRenderer.ViewportClipRect(tf.Viewport, chartSize));
        DrawReachableRegion(canvas, d, tf, theme);
        canvas.Restore();

        DrawIntrinsicGlyphs(canvas, d, tf, theme, chartSize);
        DrawMarkers(canvas, d, tf, theme, chartSize, topmostMarker);

        canvas.Restore();
    }

    /// <summary>brief-harmonicarf-r4 §4.4 — the theme/palette slice of the invalidation key, in ONE
    /// place, so a future field addition (a new role either layer starts reading) has exactly one
    /// site to update. Shared by both layers' keys rather than split per-layer: a layer that does not
    /// actually read a given field just invalidates a little more eagerly than strictly necessary on
    /// an unrelated theme change, which is cheap and rare — far cheaper than two field lists silently
    /// drifting apart.</summary>
    private readonly record struct BackdropThemeKey(
        SKColor Background, SKColor AxisLine, SKColor AxisText, SKColor GridLine, SKColor SmithGrid,
        SKColor Isoline, double IsoAlphaFloor, double IsoAlphaExponent,
        SKColor GridPoint, SKColor GridPointDropped, bool DarkMode)
    {
        public static BackdropThemeKey From(HarmonicaRenderTheme t, bool darkMode) => new(
            t.Background, t.AxisLine, t.AxisText, t.GridLine, t.SmithGrid,
            t.Isoline, t.IsoAlphaFloor, t.IsoAlphaExponent,
            t.GridPoint, t.GridPointDropped, darkMode);
    }

    /// <summary>Layer A's own key: everything <see cref="DrawContours"/> and the Smith chrome read.
    /// <c>Contours</c>/<c>Levels</c> compare by the record's own generated equality — reference
    /// equality for the two lists (R-h9r2-1's carry-forward keeps the SAME list instance across every
    /// grid-less/dragging frame, and a real rebuild always produces a new one).
    ///
    /// <para><b>brief-harmonicarf-r6b §3 — <c>Optimum</c> deliberately dropped.</b> Layer A no longer
    /// draws the optimum cross (see <see cref="DrawSmithPanelCached"/>'s own note), so keying on it
    /// only bought unnecessary cache invalidation — the whole layer re-rendered every time the argmax
    /// moved during a drag, for a pixel difference that no longer exists.</para>
    /// </summary>
    /// <summary><c>Matrix</c> is the exact offscreen-raster transform (§4.4's own exact-pixel-alignment
    /// note on <see cref="DrawSmithPanelCached"/>) — a panel reposition or a HiDPI scale change shifts
    /// this even when <c>ChartSize</c> itself does not, and either must invalidate the cache exactly
    /// like any other key field.</summary>
    private readonly record struct LayerAKey(
        (double W, double H) ChartSize, BackdropThemeKey Theme, string Title, string Subtitle,
        bool ShowIsoLineLabels, IReadOnlyList<RfCore.Loadpull.IsoPolyline> Contours,
        IReadOnlyList<double> Levels, SKMatrix Matrix);

    /// <summary>Layer B's own key: everything <see cref="DrawGridPoints"/> reads. <c>GridPoints</c>
    /// compares the same reference-or-value way <see cref="LayerAKey"/>'s own lists do. <c>Matrix</c>
    /// — see <see cref="LayerAKey"/>'s own note. <c>PixelSize</c> is explicit rather than inferred: a
    /// pure device-pixel-scale change can leave every OTHER field (including <c>Matrix</c>, if the
    /// live canvas's own transform happens not to encode it — as in a headless test render) unchanged
    /// while still demanding a higher- or lower-resolution raster, and that must count as Layer B's
    /// own change (it needs re-rasterising at the new size) rather than being silently absorbed into
    /// whatever Layer A recompose happens to be forced anyway.</summary>
    private readonly record struct LayerBKey(
        (double W, double H) ChartSize, BackdropThemeKey Theme,
        IReadOnlyList<HarmonicaGridPoint> GridPoints, SKMatrix Matrix, SKSizeI PixelSize);

    /// <summary>
    /// R-h9b-4/5 — the two title rows, centred with the CHART (not the raw panel): both rows share the
    /// chart's own horizontal centre, which for a Smith panel is <c>size.W / 2</c> regardless of the
    /// title band, since the band spans the panel's full width.
    ///
    /// <para><b>R-h9r2-13</b> — both rows now share one font size (<see cref="TitleRowFontFraction"/>,
    /// see that constant's own note). The owner's OTHER complaint ("title text is rendered too high
    /// above the Smith Chart plot") is fixed here, inside the unchanged band, by anchoring row 2's
    /// baseline to the BAND'S OWN BOTTOM EDGE (minus the same hair of outer padding
    /// <see cref="TitleBandHeight"/> reserves) rather than measuring forward from row 1 — that puts the
    /// title block as close to the chart as the band allows, with row 1 sitting exactly one line-height
    /// above row 2. Chosen over growing the band: growing it would buy the same closer-to-text look at
    /// the cost of chart area; this way the chart's own size is untouched (see
    /// <see cref="TitleBandHeight"/>'s own note on why the band's shape did not need to change).</para>
    /// </summary>
    private static void DrawTitleRows(SKCanvas canvas, (double W, double H) size, double bandH,
                                      SmithPanelData d, HarmonicaRenderTheme theme)
    {
        if (string.IsNullOrEmpty(d.Title) && string.IsNullOrEmpty(d.Subtitle)) return;

        double m = Math.Min(size.W, size.H);
        float rowSize = (float)Math.Max(7.0, m * TitleRowFontFraction * TitleFontShrink * TitleSizeR3C);
        float cx = (float)(size.W / 2);

        using var font1 = new SKFont(SkiaFonts.PlexBold,    rowSize);
        using var font2 = new SKFont(SkiaFonts.PlexRegular, rowSize);
        using var paint = new SKPaint { Color = theme.AxisText, IsAntialias = true };

        // R3C §4 — the SAME padding term TitleBandHeight reserves, so row 2's baseline and the band's
        // own bottom edge can never disagree about where the gap above the chart is.
        float y2 = (float)(bandH - m * TitleBottomPaddingFraction);
        float y1 = y2 - rowSize * 1.3f;

        if (!string.IsNullOrEmpty(d.Title))
            canvas.DrawText(d.Title, cx, y1, SKTextAlign.Center, font1, paint);
        if (!string.IsNullOrEmpty(d.Subtitle))
            canvas.DrawText(d.Subtitle, cx, y2, SKTextAlign.Center, font2, paint);
    }

    /// <summary>
    /// How much room beyond the Γ = 1 rim a Smith panel RESERVES BY SHRINKING ITSELF, as a fraction of
    /// the rim radius — <b>owner decision, R3C follow-up, 2026-08-13: 0.</b>
    ///
    /// <para><b>This used to equal <see cref="IntrinsicGlyphScale.DefaultMargin"/> (0.25)</b> — R-h45-4's
    /// original reasoning, still true and still worth keeping: shrinking the WHOLE panel by 20% so a
    /// compressed out-of-circle glyph (§4.5 consequence 2, never clamped, never hidden) always has room
    /// is what stops that glyph being clipped at the panel edge. But that 20% shrink was ALSO the
    /// dominant cause of a THIRD owner-reported "the title still renders too high above the chart"
    /// (measured: ~63px / ~11% of chart height gap on a representative panel, from this shrink alone —
    /// two prior fixes had been tuning <see cref="TitleBottomPaddingFraction"/>, a ~3px constant, which
    /// could never have closed a gap that size). Presented with the trade-off — a real but
    /// never-empirically-measured safety margin vs. a visibly tight chart — the owner chose to remove
    /// it and accept the risk: <b>a marker for a device whose intrinsic Γ is far enough outside the
    /// unit circle can now be clipped at the panel edge again</b>, the exact failure mode this constant
    /// used to prevent. <see cref="IntrinsicGlyphScale.DefaultMargin"/> itself is UNCHANGED (0.25) —
    /// it governs how a compressed glyph's POSITION is computed, a distinct question from whether the
    /// PANEL shrinks to make literal room for it, and the owner's request was about the panel's size,
    /// not the compression curve.</para>
    ///
    /// <para>Deliberately kept as a NAMED CONSTANT rather than deleted along with the scale/translate
    /// dance in <see cref="DrawSmithPanel"/> and the <c>k</c> factor in
    /// <see cref="GammaToCanvas"/>/<see cref="CanvasToGamma"/>: at 0, <c>k = 1</c> and every one of
    /// those becomes an identity operation, so hit-testing and rendering stay provably consistent with
    /// zero risk of the two drifting apart — changing ONE number here was strictly safer than unwinding
    /// the mechanism, and leaves the door open to a partial value later without more surgery.</para>
    /// </summary>
    public const double AnnulusHeadroom = 0.0;

    /// <summary>
    /// Where a Γ value lands on a Smith panel of this size, INCLUDING the annulus headroom scale AND
    /// R-h9b-4's reserved title band. Callers that need to hit-test or overlay on a harmonicaRF Smith
    /// panel must use this rather than <c>PlotRenderer.BuildTransforms</c> directly, or they will be
    /// off by the headroom factor and/or the title band — the exact bug R-h9b-1's diagnosis found.
    /// The headroom factor is currently an identity (<see cref="AnnulusHeadroom"/> = 0 — see its own
    /// doc comment) but the machinery stays general rather than special-cased for that.
    /// </summary>
    public static SKPoint GammaToCanvas(Complex gamma, (double W, double H) size)
    {
        double bandH = TitleBandHeight(size);
        var (chartSize, offsetX, offsetY) = ChartBox(size, bandH);

        var tf   = HitTestTransform(chartSize);
        var p    = tf.PrimaryToCanvas(gamma.Real, gamma.Imaginary);

        float k  = (float)(1.0 / (1.0 + AnnulusHeadroom));
        float cx = (float)(chartSize.W / 2), cy = (float)(chartSize.H / 2);
        return new SKPoint((float)offsetX + cx + (p.X - cx) * k, (float)offsetY + cy + (p.Y - cy) * k);
    }

    /// <summary>
    /// R-h6-1 — the exact inverse of <see cref="GammaToCanvas"/>, derived from the same
    /// <see cref="AnnulusHeadroom"/> factor <c>k</c> and the same title-band offset.
    ///
    /// <para><b>A hit-test that inverted <c>PlotRenderer</c>'s own transform would be off by that
    /// factor</b> — visibly, at the rim, which is exactly where markers sit. One transform pair, one
    /// place, so the two can never disagree about where Γ = 0.8 landed.</para>
    ///
    /// <para>The Smith viewport is square and uniformly scaled, so undoing the headroom scale about
    /// the panel centre and then inverting the linear window map is exact rather than iterative.</para>
    /// </summary>
    public static Complex CanvasToGamma(SKPoint canvas, (double W, double H) size)
    {
        double bandH = TitleBandHeight(size);
        var (chartSize, offsetX, offsetY) = ChartBox(size, bandH);

        float k  = (float)(1.0 / (1.0 + AnnulusHeadroom));
        float cx = (float)(chartSize.W / 2), cy = (float)(chartSize.H / 2);

        // Undo the ChartBox offset (title band + ChartMargin), then the headroom scale about the
        // chart's own centre — the same lines GammaToCanvas applies, run backwards.
        var local = new SKPoint((float)(canvas.X - offsetX), (float)(canvas.Y - offsetY));
        var p = new SKPoint(cx + (local.X - cx) / k, cy + (local.Y - cy) / k);

        // Invert the window map by measuring it, rather than reconstructing PlotRenderer's margin
        // arithmetic here: Γ = 0 and Γ = 1 pin the origin and the scale, and the map is affine.
        var tf = HitTestTransform(chartSize);
        var o  = tf.PrimaryToCanvas(0, 0);
        var xr = tf.PrimaryToCanvas(1, 0);
        var yi = tf.PrimaryToCanvas(0, 1);

        double sx = xr.X - o.X, sy = yi.Y - o.Y;
        if (Math.Abs(sx) < 1e-9 || Math.Abs(sy) < 1e-9) return Complex.Zero;

        return new Complex((p.X - o.X) / sx, (p.Y - o.Y) / sy);
    }

    /// <summary>The bare Smith transform both directions are built on, over the CHART sub-rect (panel
    /// minus the title band). Deliberately private: callers go through <see cref="GammaToCanvas"/> /
    /// <see cref="CanvasToGamma"/>, which carry the annulus headroom and the title band this one knows
    /// nothing about.</summary>
    private static TransformSet HitTestTransform((double W, double H) chartSize)
        => PlotRenderer.BuildTransforms(new Plot(PlotType.Smith, FreqUnit.GHz)
                                        { ShowWatermark = false }, chartSize);

    /// <summary>
    /// R-h6-12 — the reachable region, shaded during an intrinsic drag.
    ///
    /// <para><b>The glyph is drawn on the COMPRESSED radial scale, so the region has to be too.</b>
    /// A region drawn in raw Γ and a glyph drawn on <see cref="IntrinsicGlyphScale"/>'s scale would
    /// disagree about whether a target outside the unit circle is reachable — which is precisely the
    /// question the shading exists to answer, and precisely where the intrinsic plane spends most of
    /// its interesting values.</para>
    ///
    /// <para>Filled, not stroked, and that is not the contour-fill ruling: harmonicaRF's no-fill rule
    /// is about ISO-LINES (a fill there would claim a metric value everywhere inside a level). A
    /// reachable region is a region, and a boundary alone would leave "inside or outside?" to the
    /// reader on a shape that is not convex.</para>
    /// </summary>
    private static void DrawReachableRegion(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                            HarmonicaRenderTheme theme)
    {
        var r = d.Reachable;
        if (r is null || r.IsEmpty) return;

        using var path = new SKPath();
        for (int i = 0; i < r.Boundary.Count; i++)
        {
            var shown = IntrinsicGlyphScale.DisplayPosition(r.Boundary[i]);
            var p = tf.PrimaryToCanvas(shown.Real, shown.Imaginary);
            if (i == 0) path.MoveTo(p); else path.LineTo(p);
        }
        path.Close();

        using var fill = new SKPaint
        {
            Color = theme.ReachableRegion.WithAlpha((byte)(theme.ReachableRegion.Alpha * 0.22)),
            IsAntialias = true,
        };
        canvas.DrawPath(path, fill);

        using var edge = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true,
            Color = theme.ReachableRegion.WithAlpha((byte)(theme.ReachableRegion.Alpha * 0.75)),
        };
        canvas.DrawPath(path, edge);
    }

    /// <summary>harmonicaRF has no per-document label-spacing setting (Data Display's own
    /// trace-card label-spacing field has no counterpart here) — the Γ world is the unit disc on
    /// every panel, so one fixed value serves every document. Matches the Γ-plane default
    /// <c>PlotInspectorViewModel</c> now seeds for Data Display's own Smith/Polar contours (R8A §4.2):
    /// one label per ~1.1 rad of a rim-scale ring.</summary>
    private const double IsoLabelSpacingGamma = 0.35;

    /// <summary>R8A §4.1 — the label font's canvas-proportional scale, mirroring
    /// <c>ContourRenderer.DrawIsoLines</c>'s own <c>levelFontSize</c>/<c>BaseLw</c> convention (a
    /// 400×400 canvas at zoom 1) so a harmonicaRF label reads at the same relative size Data Display's
    /// own iso-line labels do.</summary>
    private const float IsoLabelFontSize = 9f;
    private const float IsoLabelBaseLw   = 2.0f;

    /// <summary>§7.2's ranked alpha ramp, one flat alpha per polyline — no shader, no per-vertex
    /// work, no geometry cache.
    ///
    /// <para><b>R8A §4.1 — iso-line labels.</b> harmonicaRF had the <paramref name="showIsoLineLabels"/>
    /// toggle wired end to end (the menu item, the .charm round trip, <c>LayerAKey</c>) except for the
    /// one thing it names: nothing ever drew a label. Fixed by reusing
    /// <see cref="ContourRenderer.DrawIsoLineLabel"/> — the SAME world-unit arc-walk placer Data
    /// Display's own iso-lines use, not a second hand-rolled one. Labels are part of Layer A (they
    /// depend on the contours and the chart size, not on marker positions, which is why
    /// <c>ShowIsoLineLabels</c> is already in <c>LayerAKey</c>), so they are drawn HERE, inside the
    /// cached layer. Colour comes from <c>theme.Isoline</c>/<c>theme.Background</c> — not Data
    /// Display's own per-trace label colours — so a user who recoloured iso-lines gets labels that match;
    /// and every label gets the SAME ramped alpha byte its own polyline got, so a faded low-rank
    /// contour never carries a fully-opaque label (with §1's new 0.01 floor a low-rank contour is
    /// nearly invisible, and an opaque label on it is exactly the artifact the fade exists to
    /// avoid).</para>
    /// </summary>
    private static void DrawContours(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                     HarmonicaRenderTheme theme, (double W, double H) size,
                                     bool showIsoLineLabels)
    {
        if (d.Contours.Count == 0) return;

        var levels = d.Levels;
        using var paint = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            IsAntialias = true,
        };

        float lw = AxesRenderer.LineWidth(size);
        using var labelFont  = showIsoLineLabels
            ? new SKFont(SkiaFonts.PlexRegular, IsoLabelFontSize * lw / IsoLabelBaseLw) : null;
        using var labelPaint = showIsoLineLabels
            ? new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill } : null;
        using var bgPaint    = showIsoLineLabels
            ? new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill } : null;
        using var bgStroke   = showIsoLineLabels
            ? new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f } : null;
        float labelPadX = 4f * lw / IsoLabelBaseLw;
        float labelPadY = 3f * lw / IsoLabelBaseLw;

        int ringIndex = 0;

        foreach (var poly in d.Contours)
        {
            if (poly.Points.Count < 2) { ringIndex++; continue; }

            int rank = IsoLineAlphaRamp.RankOf(poly.Level, levels);
            byte a   = IsoLineAlphaRamp.AlphaByte(rank, Math.Max(levels.Count, 1),
                                                  theme.IsoAlphaFloor, theme.IsoAlphaExponent);
            paint.Color = theme.Isoline.WithAlpha(ScaleAlpha(theme.Isoline.Alpha, a));

            using var path = new SKPath();
            var p0 = tf.PrimaryToCanvas(poly.Points[0].X, poly.Points[0].Y);
            path.MoveTo(p0);
            for (int i = 1; i < poly.Points.Count; i++)
            {
                var p = tf.PrimaryToCanvas(poly.Points[i].X, poly.Points[i].Y);
                path.LineTo(p);
            }
            if (poly.Closed) path.Close();
            canvas.DrawPath(path, paint);

            if (showIsoLineLabels)
            {
                labelPaint!.Color = theme.Isoline.WithAlpha(ScaleAlpha(theme.Isoline.Alpha, a));
                bgPaint!.Color    = theme.Background.WithAlpha(ScaleAlpha(theme.Background.Alpha, a));
                bgStroke!.Color   = new SKColor(0, 0, 0, ScaleAlpha(120, a));

                ContourRenderer.DrawIsoLineLabel(
                    canvas, poly.Points, tf.PrimaryToCanvas,
                    poly.Level, IsoLabelSpacingGamma, ringIndex,
                    labelFont!, labelPaint, bgPaint, bgStroke, labelPadX, labelPadY);
            }

            ringIndex++;
        }
    }

    /// <summary>The role's own alpha and the ramp's compose — a role a user made translucent stays
    /// translucent, and the ramp still fades within whatever opacity that leaves.</summary>
    private static byte ScaleAlpha(byte roleAlpha, byte rampAlpha)
        => (byte)Math.Clamp((int)Math.Round(roleAlpha * (rampAlpha / 255.0)), 0, 255);

    /// <summary>
    /// R-h45-5 — thrown-out Γ points render as small HOLLOW dots, so a hole reads as measured rather
    /// than as a rendering gap. Converged points are filled.
    /// </summary>
    private static void DrawGridPoints(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                       HarmonicaRenderTheme theme, (double W, double H) size)
    {
        if (d.GridPoints.Count == 0) return;

        float r = (float)(Math.Min(size.W, size.H) * 0.0055);
        r = Math.Max(1.6f, r);

        using var fill = new SKPaint { Color = theme.GridPoint, IsAntialias = true };
        using var hollow = new SKPaint
        {
            Color       = theme.GridPointDropped,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, r * 0.45f),
            IsAntialias = true,
        };

        foreach (var gp in d.GridPoints)
        {
            var p = tf.PrimaryToCanvas(gp.Gamma.Real, gp.Gamma.Imaginary);
            if (gp.IsHole) canvas.DrawCircle(p, r * 1.15f, hollow);
            else           canvas.DrawCircle(p, r, fill);
        }
    }

    /// <summary>
    /// brief-harmonicarf-r6b §1.3 — the live "VSWR: &lt;val&gt;" readout shown near the pointer while
    /// dragging the circle (or on a plain click, before any move). Mirrors Data Display's own
    /// unclipped, drawn-last VSWR readout (<c>PlotRenderer.cs</c>'s <c>vswrReadout</c> block) —
    /// <b>the caller draws this OUTSIDE any panel's own clip rect</b> (<c>HarmonicaCanvas</c>'s draw
    /// operation, after <c>HarmonicaCanvasRenderer.DrawAll</c>, never from inside
    /// <see cref="DrawSmithPanel"/>), so it is never cut off at a panel edge. <paramref name="text"/>
    /// and <paramref name="pointerCanvas"/> are already in full-canvas space; <paramref name="panelSize"/>
    /// is the Smith panel the drag started on, ONLY for font sizing (the same panel-relative
    /// convention every other glyph size in this file uses) — the text itself is not clipped to it.
    /// </summary>
    public static void DrawVswrReadout(SKCanvas canvas, string text, SKPoint pointerCanvas,
                                       (double W, double H) panelSize, HarmonicaRenderTheme theme)
    {
        if (string.IsNullOrEmpty(text)) return;

        float size = (float)(Math.Min(panelSize.W, panelSize.H) * 0.0224);
        if (size <= 0) return;

        using var font  = new SKFont(SkiaFonts.PlexRegular, size);
        using var paint = new SKPaint { Color = theme.ReadoutText, IsAntialias = true };
        canvas.DrawText(text, pointerCanvas.X + 10f, pointerCanvas.Y - 10f, SKTextAlign.Left, font, paint);
    }

    // brief-harmonicarf-r6b §3 — DrawOptima (the MXP/MXE cross) removed; the glyph is deferred to v2.
    // SmithPanelData.Optimum stays populated for HarmonicaSolver.AddMxColumn's readout columns — this
    // file simply stopped rendering it (see LayerAKey's own note on why Optimum left its cache key too).

    /// <summary>
    /// R-h45-4 — the intrinsic glyphs: subtle TRIANGULAR markers, always beneath the round
    /// termination markers, in the same per-band colour at reduced saturation. Values come from the
    /// <c>Gamma_intr</c> cube; nothing here recomputes them (§0.3 item 1).
    /// </summary>
    /// <summary>R8C §4.1 — the intrinsic glyph is 0.9× the termination marker's rendered radius:
    /// clearly secondary, but no longer half the size (0.012/0.020 = 0.6, the ratio before this
    /// change). DERIVED from the marker's own constants so the two cannot drift apart.</summary>
    internal const double IntrinsicGlyphScaleOfMarker = 0.9;

    private static void DrawIntrinsicGlyphs(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                            HarmonicaRenderTheme theme, (double W, double H) size)
    {
        float s = (float)(MarkerRadius(size) * IntrinsicGlyphScaleOfMarker);

        foreach (var m in d.Markers)
        {
            // R-h8-3 — an intrinsic plane nobody has located arrives as NaN and draws NOTHING. Left
            // to Skia a non-finite coordinate is silently dropped on most paths and lands at the
            // origin on some; a glyph sitting at Γ = 0 is a plausible-looking wrong answer.
            if (double.IsNaN(m.GammaIntrinsic.Real) || double.IsNaN(m.GammaIntrinsic.Imaginary))
                continue;

            var shown = IntrinsicGlyphScale.DisplayPosition(m.GammaIntrinsic);
            var p     = tf.PrimaryToCanvas(shown.Real, shown.Imaginary);

            var band = theme.MarkerBand(m.Band);
            // "reduced saturation" — pulled toward the background so a glyph reads as secondary to
            // its marker without losing which band it belongs to.
            var c = Desaturate(band, theme.Background, 0.45);

            // R8C §4.2 — fully opaque; only the desaturation toward the background (above) marks the
            // glyph as secondary now, not a reduced alpha.
            using var fill = new SKPaint { Color = c, IsAntialias = true };
            using var path = new SKPath();
            path.MoveTo(p.X, p.Y - s);
            // The 0.9f/0.75f below are the triangle's own SHAPE proportions (vertex spread), unrelated
            // to IntrinsicGlyphScaleOfMarker's 0.9 (the glyph's overall SIZE relative to a marker) —
            // an unfortunate collision of literals, not the same knob.
            path.LineTo(p.X + s * 0.9f, p.Y + s * 0.75f);
            path.LineTo(p.X - s * 0.9f, p.Y + s * 0.75f);
            path.Close();
            canvas.DrawPath(path, fill);

            // A glyph in the compressed annulus is never silent about it: the outline says the
            // radial scale is not the chart's own out here.
            if (IntrinsicGlyphScale.IsCompressed(m.GammaIntrinsic))
            {
                using var edge = new SKPaint
                {
                    Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true,
                    Color = c.WithAlpha(255),
                    PathEffect = SKPathEffect.CreateDash([2.5f, 2.0f], 0),
                };
                canvas.DrawPath(path, edge);
            }
        }
    }

    private static SKColor Desaturate(SKColor c, SKColor toward, double t)
    {
        byte L(byte a, byte b) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
        return new SKColor(L(c.Red, toward.Red), L(c.Green, toward.Green), L(c.Blue, toward.Blue), c.Alpha);
    }

    /// <summary>R8C §4.1 — the round termination marker's own rendered radius, hoisted out of
    /// <see cref="DrawMarkers"/> so <see cref="DrawIntrinsicGlyphs"/> can size itself as a DERIVED
    /// fraction of it rather than an independent magic number the two could drift apart from.</summary>
    internal const double MarkerRadiusFraction = 0.020;
    internal const float  MarkerRadiusFloorPx  = 6f;

    internal static float MarkerRadius((double W, double H) size)
        => Math.Max(MarkerRadiusFloorPx, (float)(Math.Min(size.W, size.H) * MarkerRadiusFraction));

    /// <summary>
    /// §4.2 — a marker is a filled circle with a thin outline and its name inside, in its BAND's
    /// colour from the five-colour cycle.
    ///
    /// <para><b>R-h9r2-5 — draw order is the z-order, lowest rank first</b>, so the highest-ranked
    /// marker (L1 by default, or whichever one the session has promoted) is painted LAST and ends up
    /// visually on top of every other marker it overlaps — the same rank
    /// <see cref="HarmonicaHitTest.Resolve"/> uses to decide which marker a click actually grabs.</para>
    /// </summary>
    private static void DrawMarkers(SKCanvas canvas, SmithPanelData d, TransformSet tf,
                                    HarmonicaRenderTheme theme, (double W, double H) size,
                                    HarmonicaMarker? topmostMarker = null)
    {
        float r  = MarkerRadius(size);
        float ts = r * 1.15f;

        using var font   = new SKFont(SkiaFonts.PlexBold, ts);
        using var edge   = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f,
                                         Color = SKColors.Black, IsAntialias = true };
        using var label  = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        foreach (var m in HarmonicaMarkerZOrder.DrawOrder(d.Markers, topmostMarker))
        {
            // R-h9r2-8 — drawn BENEATH the marker itself, so the round glyph and its name stay
            // readable sitting on top of the circle rather than the circle competing with them.
            DrawVswrLocus(canvas, m, tf, theme, d.Z0);

            // R8B §2 — an ACTIVE termination is drawn where it TRULY is, on the plain chart map,
            // rather than clamped to the rim (R-h6-10) or composed with the intrinsic glyph's own
            // compressed scale (that composition was never this marker's — see IntrinsicGlyphScale's
            // own remarks, which are about the intrinsic glyph only). A marker can now leave the panel
            // at |Γ| > ~1.3; DrawMarkers carries no ClipRect, so it still paints.
            var p = tf.PrimaryToCanvas(m.Gamma.Real, m.Gamma.Imaginary);
            using var fill = new SKPaint { Color = theme.MarkerBand(m.Band), IsAntialias = true };
            canvas.DrawCircle(p, r, fill);

            if (m.ExtrinsicIsOutsideUnitCircle) DrawHatchedOutline(canvas, p, r, theme);
            else                                canvas.DrawCircle(p, r, edge);

            float tw = font.MeasureText(m.Name);
            canvas.DrawText(m.Name, p.X - tw / 2f, p.Y + ts * 0.36f, SKTextAlign.Left, font, label);
        }
    }

    /// <summary>
    /// R-h9r2-8 — the constant-VSWR locus around a marker's termination, reusing Data Display's
    /// <c>RfCore.Loadpull.LoadpullSurface.VswrLocus</c> geometry (never its TYPES — harmonicaRF has
    /// its own <see cref="HarmonicaMarker"/>, not Data Display's <c>Marker</c>).
    ///
    /// <para><b>Drawn through <paramref name="tf"/>, not the public <see cref="GammaToCanvas"/>.</b>
    /// Within <see cref="DrawMarkers"/> the canvas already carries the title-band translate and the
    /// annulus-headroom scale <see cref="DrawSmithPanel"/> pushed before calling it — exactly the
    /// transforms <see cref="GammaToCanvas"/> itself applies analytically for a caller with no canvas
    /// state of its own. Calling it again here would apply both a second time. <c>tf.PrimaryToCanvas</c>
    /// is what the marker circle right below already uses for the identical reason.</para>
    ///
    /// <para><b>brief-harmonicarf-r6b §1.1 — no gripper glyph any more.</b> The circle used to carry a
    /// small square drag handle at its own θ = 0 sample; the whole circumference is grabbable now
    /// (<see cref="HarmonicaHitTest.Resolve"/>'s Pass 2.5), so a glyph that implied only ONE point was
    /// draggable would be actively misleading. Only the dashed stroke remains.</para>
    /// </summary>
    private static void DrawVswrLocus(SKCanvas canvas, HarmonicaMarker m, TransformSet tf,
                                      HarmonicaRenderTheme theme, double z0)
    {
        if (!m.VswrEnabled) return;

        var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(
            m.Gamma, m.VswrValue, RfCore.Loadpull.SurfacePlane.Gamma, new Complex(z0, 0));
        if (pts is null || pts.Length < 2) return;

        using var paint = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1.3f,
            IsAntialias = true,
            Color       = theme.MarkerBand(m.Band).WithAlpha(190),
            PathEffect  = SKPathEffect.CreateDash([4f, 3f], 0),
        };

        using var path = new SKPath();
        var p0 = tf.PrimaryToCanvas(pts[0].Real, pts[0].Imaginary);
        path.MoveTo(p0);
        for (int i = 1; i < pts.Length; i++)
            path.LineTo(tf.PrimaryToCanvas(pts[i].Real, pts[i].Imaginary));
        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// R-h6-10's flag: a marker whose extrinsic Γ is outside the unit circle wears a HATCHED outline
    /// — a heavier dashed ring plus radial ticks, so it is distinguishable at a glance from the
    /// ordinary thin outline and from the intrinsic glyph's own dashed edge.
    ///
    /// <para>It is a flag, never a clamp. "An active source termination is a legitimate thing to
    /// discover, and hiding it would mislead."</para>
    /// </summary>
    private static void DrawHatchedOutline(SKCanvas canvas, SKPoint p, float r,
                                           HarmonicaRenderTheme theme)
    {
        using var ring = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 2.0f, IsAntialias = true,
            Color = theme.AxisLine,
            PathEffect = SKPathEffect.CreateDash([3f, 2.5f], 0),
        };
        canvas.DrawCircle(p, r + 1.5f, ring);

        using var tick = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true,
            Color = theme.AxisLine,
        };
        for (int i = 0; i < 8; i++)
        {
            double a = Math.PI * 2 * i / 8;
            float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
            canvas.DrawLine(p.X + ca * (r + 1.5f), p.Y + sa * (r + 1.5f),
                            p.X + ca * (r + 5.0f), p.Y + sa * (r + 5.0f), tick);
        }
    }

    // ── §7.3 — the loadline panel ────────────────────────────────────────────

    /// <summary>
    /// The DCIV family with the time-domain loadline over it, plus §7.3's <b>persistent</b> plane
    /// indicator — "it is never absent", because a loadline shown in the wrong plane is a plausible
    /// picture of the wrong thing.
    /// </summary>
    public static void DrawLoadlinePanel(SKCanvas canvas, (double W, double H) size,
                                         LoadlinePanelData d, HarmonicaRenderTheme theme, bool darkMode,
                                         HarmonicaSettings? settings = null)
    {
        var plot = BuildLoadlinePlot(d, theme, DcivLimits(settings ?? new HarmonicaSettings()));
        PlotRenderer.Draw(canvas, size, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                          watermarkOpacity: 0f);
        DrawPlaneIndicator(canvas, size, d, theme);
    }

    internal static Plot BuildLoadlinePlot(LoadlinePanelData d, HarmonicaRenderTheme theme,
                                           StoredAxisWindow limits = default)
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz)
        {
            ShowWatermark = false,
            // brief-harmonicarf-r6d §3.
            CustomTitleOn = true, CustomTitle = "Loadline", CustomTitleBold = true,
            CustomXLabelOn = true, CustomXLabel = "Vds (V)",
            CustomYLabelOn = true, CustomYLabel = "Ids (A)",
        };
        // Matches the Smith panels' own row-1 title height — see RectTitleFontSizeLabel's own remarks.
        plot.Axes.FontSizeLabel = RectTitleFontSizeLabel;

        foreach (var c in d.Dciv)
        {
            var t = NewRectTrace(c.Vds, c.Ids, theme.DcivFamily, width: 0.8);
            plot.Traces.Add(t);
        }

        if (d.LoadlineVds.Length > 1)
            plot.Traces.Add(NewRectTrace(d.LoadlineVds, d.LoadlineIds, theme.Loadline, width: 1.8));

        AutoScale(plot);
        // brief-harmonicarf-r6e §2.4 — applied last: an explicit stored limit overrides AutoScale's
        // own fit, and autoscale ON leaves it untouched so CaptureAxisWindows can read it back.
        ApplyStoredWindow(plot, limits, hasSecondary: false);
        // R-h9b-11 — the loadline draws no secondary trace, so Plot.SetAxesViewport() (fired
        // automatically when the traces above were added) reserved a NARROWER right margin than the
        // power-sweep panel's own secondary-axis margin. Pinned to the same shape either draws.
        plot.Axes.Viewport = PowerSweepShapedViewport();
        return plot;
    }

    /// <summary>
    /// R-h9b-11's fix: the viewport fraction a plot shaped exactly like the power-sweep panel (one
    /// left-axis trace, one right-axis trace) computes, through <c>Plot</c>'s own
    /// <c>SetAxesViewport()</c> algorithm on a scratch probe — not a copy of its formula, so the two
    /// panels cannot drift apart if that algorithm ever changes. Assigned to BOTH the loadline and the
    /// power-sweep plots so their DATA rectangles are identical at every window size, not by
    /// coincidence of two independently-derived margins happening to agree.
    /// </summary>
    private static Avalonia.Rect PowerSweepShapedViewport()
    {
        var probe = new Plot(PlotType.Rect, FreqUnit.GHz);
        probe.Traces.Add(NewRectTrace([0, 1], [0, 1], SKColors.Black, width: 1));
        probe.Traces.Add(NewRectTrace([0, 1], [0, 1], SKColors.Black, width: 1, secondary: true));
        return probe.Axes.Viewport;
    }

    private static void DrawPlaneIndicator(SKCanvas canvas, (double W, double H) size,
                                           LoadlinePanelData d, HarmonicaRenderTheme theme)
    {
        float ts = (float)Math.Max(9.0, Math.Min(size.W, size.H) * 0.032);
        using var font  = new SKFont(SkiaFonts.PlexRegular, ts);
        using var paint = new SKPaint { Color = theme.AxisText.WithAlpha(190), IsAntialias = true };
        string text = d.PlaneLabel;
        float tw = font.MeasureText(text);
        canvas.DrawText(text, (float)size.W - tw - 6f, (float)(ts + 4f), SKTextAlign.Left, font, paint);
    }

    // ── §7.4 — the power-sweep panel ─────────────────────────────────────────

    /// <summary>Gain on the left axis, efficiency on the right, against the currently selected
    /// X unit (§7.4's click-to-cycle), with the operating-point cursor.</summary>
    public static void DrawPowerSweepPanel(SKCanvas canvas, (double W, double H) size,
                                           PowerSweepPanelData d, HarmonicaRenderTheme theme, bool darkMode,
                                           HarmonicaSettings? settings = null)
    {
        var plot = BuildPowerSweepPlot(d, theme, PowerSweepLimits(settings ?? new HarmonicaSettings()));
        DrawWithSuppressedSecondaryChrome(canvas, size, plot, theme, darkMode);
        DrawSecondaryAxisOverlay(canvas, size, plot, theme, theme.EfficiencyTrace);
        // R9A §5 — owner ruling: the dashed vertical operating-point cursor is removed from this plot
        // entirely (DrawOperatingCursor deleted below). PowerSweepPanelData.CursorIndex still drives
        // which step the glyphs/loadline/readouts read (HarmonicaViewModel.OperatingPointDbm) and is
        // still readable in the strip — a USER-PLACED cursor (Display ▸ Cursor Snap to Compression
        // off) simply no longer has a mark on the curve itself. The owner chose that knowingly.
        if (!d.ReachedCompression) DrawDidNotCompressNote(canvas, size, theme);
    }

    /// <summary>
    /// brief-harmonicarf-r6d §1 — the fix for the double-rendered right axis: draw the SHARED plot
    /// with its secondary chrome (border edges, ticks, tick numbers, label — all of
    /// <c>AxesRenderer</c>'s <c>axes.ShowSecondary</c> branches) suppressed, so
    /// <see cref="DrawSecondaryAxisOverlay"/> below is drawing the axis for the FIRST time rather than
    /// covering a stroke that is still there underneath it.
    ///
    /// <para><b>Why a copy, not a flag flip-then-back.</b> <c>Axes</c> already has a deep-copy
    /// constructor (<c>Axes.cs:161</c>) that carries <c>Window</c>/<c>WindowSecondary</c>/<c>Viewport</c>
    /// verbatim — so the copy's trace geometry is byte-identical to the original's; only
    /// <c>ShowSecondary</c> differs. <c>Plot.Axes</c> is temporarily swapped to the copy for the
    /// <see cref="PlotRenderer.Draw"/> call and restored in a <c>finally</c>, so the ORIGINAL
    /// <c>plot</c> — with <c>ShowSecondary</c> still true — is what <see cref="DrawSecondaryAxisOverlay"/>
    /// (and every caller after it: the operating cursor, the compression note) sees. Confirmed safe by
    /// reading, not assumed: trace drawing branches on <c>trace.UseSecondaryAxis</c>/
    /// <c>TransformSet.SecondaryToCanvas</c>, never on <c>Axes.ShowSecondary</c> — nothing in
    /// <c>PlotRenderer</c>/<c>TraceRenderer</c> reads that flag — and <c>BuildPowerSweepPlot</c> pins
    /// <c>plot.Axes.Viewport</c> explicitly (R-h9b-11), so the copy's identical <c>Viewport</c> value
    /// keeps the data rectangle exactly where it was; <c>PlotRenderer.ComputeViewport</c> only
    /// re-derives a viewport from <c>ShowSecondary</c> for a Rect plot with NO pinned viewport, which
    /// this one is not.</para>
    /// </summary>
    private static void DrawWithSuppressedSecondaryChrome(SKCanvas canvas, (double W, double H) size,
                                                           Plot plot, HarmonicaRenderTheme theme,
                                                           bool darkMode)
    {
        var original = plot.Axes;
        plot.Axes = new Axes(original) { ShowSecondary = false };
        try
        {
            PlotRenderer.Draw(canvas, size, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                              watermarkOpacity: 0f);
        }
        finally
        {
            plot.Axes = original;
        }
    }

    /// <summary>
    /// R-h9b-9 — the right axis's line, tick marks and tick NUMBERS, drawn in <paramref name="color"/>.
    ///
    /// <para><b>Not a shared-renderer change.</b> <c>AxesRenderer</c> colours the primary and secondary
    /// axes identically and has no per-axis colour capability — adding one there for a single
    /// harmonicaRF panel is exactly what the <c>AnnulusHeadroom</c> precedent (§9) says not to do. This
    /// duplicates the handful of lines of <c>AxesRenderer</c>'s own secondary-axis geometry rather than
    /// widening it, using the SAME public <see cref="Axes.Ticks"/> data and the SAME
    /// <see cref="TransformSet.SecondaryToCanvas"/>/<see cref="TransformSet.PrimaryToCanvas"/> calls.</para>
    ///
    /// <para><b>brief-harmonicarf-r6d §1 — no longer a cover.</b> R3C §5 had this matching
    /// <c>AxesRenderer.DrawBorder</c>'s antialiased, <c>Square</c>-capped stroke shape so a two-pass
    /// draw wouldn't leave a fringe of the covered colour showing. That matching is no longer load
    /// -bearing for correctness (<see cref="DrawWithSuppressedSecondaryChrome"/> means there is nothing
    /// underneath to fringe) but is kept anyway, since it is also just the right way to draw a border
    /// stroke.</para>
    ///
    /// <para><b>brief-harmonicarf-r6d §5 — <paramref name="color"/> is a parameter, not a fork.</b> The
    /// time-domain view redraws this same axis in <c>Harmonica.Loadline</c> instead of
    /// <c>Harmonica.EfficiencyTrace</c>; the geometry is identical either way.</para>
    /// </summary>
    private static void DrawSecondaryAxisOverlay(SKCanvas canvas, (double W, double H) size,
                                                  Plot plot, HarmonicaRenderTheme theme, SKColor color)
    {
        var axes = plot.Axes;
        if (!axes.ShowSecondary) return;

        var tf = PlotRenderer.BuildTransforms(plot, size);
        float lw = AxesRenderer.LineWidth(size);

        using var linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Square,
            Color = color, StrokeWidth = 2f * (float)axes.GridThicknessFactor * lw,
        };
        // The right border edge — positioned in the PRIMARY window, exactly as AxesRenderer.DrawBorder
        // draws it (the border box itself does not depend on the secondary VALUE scale).
        var br = tf.PrimaryToCanvas(axes.Window.Right, axes.Window.Top);
        var tr = tf.PrimaryToCanvas(axes.Window.Right, axes.Window.Bottom);
        canvas.DrawLine(tr, br, linePaint);

        using var tickPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Square,
            Color = color, StrokeWidth = (float)axes.TickThicknessFactor * lw,
        };
        using var font  = new SKFont(SkiaFonts.PlexRegular, (float)(axes.FontSizeTicks * lw));
        using var textPaint = new SKPaint { Color = color, IsAntialias = true };

        foreach (var (yPrimary, ySecondary) in axes.Ticks(minorTicks: false).MajorY)
        {
            if (!double.IsFinite(yPrimary)) continue;

            // The tick MARK — on the shared grid line (AxesRenderer's default, SecondaryShareGrid),
            // else on the secondary window's own position. Matches AxesRenderer's own branch exactly.
            if (axes.SecondaryShareGrid)
            {
                var t0 = tf.PrimaryToCanvas(axes.Window.Right - axes.TickLengthX, yPrimary);
                var t1 = tf.PrimaryToCanvas(axes.Window.Right,                    yPrimary);
                canvas.DrawLine(t0, t1, tickPaint);
            }
            else if (double.IsFinite(ySecondary))
            {
                var t0 = tf.SecondaryToCanvas(axes.WindowSecondary.Right - axes.TickLengthX, ySecondary);
                var t1 = tf.SecondaryToCanvas(axes.WindowSecondary.Right,                    ySecondary);
                canvas.DrawLine(t0, t1, tickPaint);
            }

            // The tick NUMBER — always through the SECONDARY window, regardless of ShareGrid.
            if (!double.IsFinite(ySecondary)) continue;
            double v = Math.Abs(ySecondary) < 1e-12 ? 0 : ySecondary;
            string label = v.ToString($"G{axes.NumDigitsRightY}");
            var rPt = tf.SecondaryToCanvas(axes.WindowSecondary.Right, ySecondary);
            canvas.DrawText(label, rPt.X + lw * 4f, rPt.Y + font.Size * 0.35f,
                            SKTextAlign.Left, font, textPaint);
        }

        DrawSecondaryAxisLabel(canvas, size, plot, color);
    }

    /// <summary>
    /// R-h9r2-23 — the "Efficiency (%)" / "Ids (A)" text label, drawn in <paramref name="color"/>.
    /// R-h9b-9 already redrew the axis's line, ticks and tick NUMBERS; it did not redraw the axis LABEL
    /// itself, which <c>AxesRenderer.DrawTitleAndAxisLabels</c> still draws in the shared theme's
    /// ordinary text colour.
    ///
    /// <para><b>Lands EXACTLY on top of the original</b>, using
    /// <see cref="AxesRenderer.ComputeLabelHitRects"/>'s <c>Y2Label</c> rect — a standalone,
    /// non-drawing geometry accessor R-h9b-10 already uses for the X-label context menu — rather than
    /// a hand-derived guess at the label's position.</para>
    /// </summary>
    private static void DrawSecondaryAxisLabel(SKCanvas canvas, (double W, double H) size,
                                                Plot plot, SKColor color)
    {
        string label = plot.Y2Label;
        if (string.IsNullOrEmpty(label) || !plot.Axes.ShowSecondary) return;

        var rects = AxesRenderer.ComputeLabelHitRects(plot, size);
        var r = rects.Y2Label;
        if (r.Width <= 0 && r.Height <= 0) return;   // no secondary label drawn — nothing to overlay

        float lw = AxesRenderer.LineWidth(size);
        using var font  = new SKFont(SkiaFonts.PlexRegular, (float)(plot.Axes.FontSizeTicks * 0.9f * lw));
        using var paint = new SKPaint { Color = color, IsAntialias = true };

        float cx = r.MidX;
        float cy = r.MidY;
        float tw = font.MeasureText(label);

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.RotateDegrees(90f);
        canvas.DrawText(label, -tw / 2f, font.Size * 0.35f, SKTextAlign.Left, font, paint);
        canvas.Restore();
    }

    internal static Plot BuildPowerSweepPlot(PowerSweepPanelData d, HarmonicaRenderTheme theme,
                                             StoredAxisWindow limits = default)
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz)
        {
            ShowWatermark = false,
            // brief-harmonicarf-r6d §3 — also the §4 fly menu's hit target (the title band read
            // through AxesRenderer.ComputeLabelHitRects, never hand-derived).
            CustomTitleOn = true, CustomTitle = "Power Sweep", CustomTitleBold = true,
            CustomXLabelOn = true, CustomXLabel = d.XUnit.Label(),
            CustomYLabelOn = true, CustomYLabel = "Gain (dB)",
            // R-h9b-8 — without this the right axis falls back to its trace's own auto-derived label
            // ("real(S(1,1))" for the placeholder SNP-backed Trace this panel builds traces on).
            CustomY2LabelOn = true,
            CustomY2Label = d.EfficiencyMetric == GridMetric.Pae ? "PAE (%)" : "Efficiency (%)",
        };
        // Matches the Smith panels' own row-1 title height — see RectTitleFontSizeLabel's own remarks.
        plot.Axes.FontSizeLabel = RectTitleFontSizeLabel;

        double[] x = d.XUnit.Values(d);
        if (x.Length > 1)
        {
            plot.Traces.Add(NewRectTrace(x, d.GainDb,        theme.GainTrace,       width: 1.6));
            plot.Traces.Add(NewRectTrace(x, d.EfficiencyPct, theme.EfficiencyTrace, width: 1.6,
                                         secondary: true));
        }

        AutoScale(plot);
        PinAxisPin(plot, d);
        AddXHeadroom(plot);
        // brief-harmonicarf-r6e §2.4 — LAST in the ordering: AutoScale, then the Pin-domain pin, then
        // the right-edge headroom, then (only here) an explicit stored limit, which overrides all
        // three. Autoscale ON leaves this frame's computed window as the other three left it, so
        // CaptureAxisWindows can read it back.
        ApplyStoredWindow(plot, limits, hasSecondary: true);
        // R-h9b-11 — pinned explicitly rather than left to the automatic computation, so a future
        // change to Plot.SetAxesViewport()'s formula cannot silently re-open the mismatch with the
        // loadline panel, which derives its own viewport from this SAME probe shape.
        plot.Axes.Viewport = PowerSweepShapedViewport();
        return plot;
    }

    /// <summary>
    /// brief-harmonicarf-r6d §2 — without this the curve ends exactly on the right border (neither
    /// <see cref="AutoScale"/>'s <c>Pad</c> nor <see cref="PinAxisPin"/> add any X margin, only Y), so
    /// the compression cursor — drawn at the LAST swept X — sits under the axis line and cannot be
    /// read. Extends <see cref="Axes.Window"/>'s right edge by a fraction of the span, AFTER both
    /// AutoScale and the Pin-domain pin.
    ///
    /// <para><b>The identical extension is applied to <see cref="Axes.WindowSecondary"/>.</b>
    /// <see cref="PinAxisPin"/>'s own note says why: the two windows must keep the same X mapping or
    /// the gain and efficiency curves separate horizontally. <see cref="AutoScale"/> already gives
    /// them the same X range (one <c>minX</c>/<c>maxX</c> accumulator across both primary and
    /// secondary traces) and <see cref="PinAxisPin"/> — when it fires — sets both to the identical
    /// <c>[lo, hi]</c>, so re-using ONE <c>extra</c> value computed from the primary window (rather
    /// than independently deriving a second fraction from the secondary one) is what guarantees the
    /// two stay exactly equal rather than merely equal up to floating-point noise.</para>
    /// </summary>
    internal const double XHeadroomFraction = 0.05;

    private static void AddXHeadroom(Plot plot)
    {
        var w = plot.Axes.Window;
        if (w.Width <= 0) return;
        double extra = w.Width * XHeadroomFraction;
        plot.Axes.Window = new Avalonia.Rect(w.X, w.Y, w.Width + extra, w.Height);

        var w2 = plot.Axes.WindowSecondary;
        if (w2.Width > 0)
            plot.Axes.WindowSecondary = new Avalonia.Rect(w2.X, w2.Y, w2.Width + extra, w2.Height);
    }

    /// <summary>
    /// brief-harmonicarf-r4 §1.2 — with the sweep's own early stop, the last solved Pin (and hence
    /// AutoScale's own data-fit X range) moves with the termination, which would make the axis visibly
    /// breathe frame to frame during a drag. When the X unit is Pin-domain, override the X extent
    /// AutoScale computed with the sweep's full CONFIGURED range instead — the axis then stays fixed
    /// regardless of where any one termination's ladder actually stopped, and the curve simply ends
    /// short of the right edge. Only the X component is touched; Y (gain/efficiency) stays whatever
    /// AutoScale fit to the data, exactly as before. Pout-domain X units have no fixed range to pin to
    /// (Pout at compression is not a control setting) and are left to AutoScale, unchanged.
    /// </summary>
    private static void PinAxisPin(Plot plot, PowerSweepPanelData d)
    {
        if (d.XUnit is not (PowerSweepXUnit.PinAvailDbm or PowerSweepXUnit.PinAvailW)) return;
        if (plot.Axes.Window.Width <= 0) return;

        double lo = d.PinStartDbm, hi = d.PinMaxDbm;
        if (d.XUnit == PowerSweepXUnit.PinAvailW) { lo = DbmToWatts(lo); hi = DbmToWatts(hi); }
        if (!(hi > lo)) return;

        var w = plot.Axes.Window;
        plot.Axes.Window = new Avalonia.Rect(lo, w.Y, hi - lo, w.Height);

        var w2 = plot.Axes.WindowSecondary;
        if (w2.Width > 0)
            plot.Axes.WindowSecondary = new Avalonia.Rect(lo, w2.Y, hi - lo, w2.Height);
    }

    private static double DbmToWatts(double dbm) => Math.Pow(10.0, (dbm - 30.0) / 10.0);

    /// <summary>§6.3 — "the power-sweep panel still shows the full drive-up at the current L1
    /// position, annotated 'did not reach P-x dB'."</summary>
    private static void DrawDidNotCompressNote(SKCanvas canvas, (double W, double H) size,
                                               HarmonicaRenderTheme theme)
    {
        float ts = (float)Math.Max(9.0, Math.Min(size.W, size.H) * 0.032);
        using var font  = new SKFont(SkiaFonts.PlexRegular, ts);
        using var paint = new SKPaint { Color = theme.GridPointDropped, IsAntialias = true };
        canvas.DrawText("did not reach compression", 6f, (float)(ts + 4f),
                        SKTextAlign.Left, font, paint);
    }

    // ── §7.4 (r6d §5) — the power-sweep panel, repurposed as a Time Domain view ─

    /// <summary>
    /// brief-harmonicarf-r6d §4/§5 — the power-sweep panel's title fly menu can switch it to this
    /// view instead: Vds(t) on the left axis, Ids(t) on the right, over ONE RF cycle.
    ///
    /// <para><b>Reads the SAME arrays the loadline panel plots</b> — <paramref name="d"/> is
    /// <c>HarmonicaFrame.Loadline</c>, exactly what <see cref="DrawLoadlinePanel"/> draws — so the two
    /// panels can never disagree about the loadline's own shape. Nothing here re-evaluates
    /// <c>Vds_intr_t</c>/<c>Ids_intr_t</c> (§0.3 item 1).</para>
    ///
    /// <para><b>The empty case is stated, never zeros.</b> When the intrinsic plane has not been
    /// located, <see cref="LoadlinePanelData.LoadlineVds"/>/<see cref="LoadlinePanelData.LoadlineIds"/>
    /// are published empty (R-h8-3's refusal) — drawing an all-zero flat line there would be a
    /// plausible-looking wrong answer, so this draws a stated note instead, the same shape
    /// <see cref="DrawPickedTracePanel"/>'s "no trace" note already uses.</para>
    /// </summary>
    public static void DrawTimeDomainPanel(SKCanvas canvas, (double W, double H) size,
                                           LoadlinePanelData d, HarmonicaRenderTheme theme, bool darkMode,
                                           HarmonicaSettings? settings = null)
    {
        if (d.LoadlineVds.Length < 2 || d.LoadlineIds.Length < 2)
        {
            DrawTimeDomainEmptyNote(canvas, size, theme);
            return;
        }

        var plot = BuildTimeDomainPlot(d, theme, TimeDomainLimits(settings ?? new HarmonicaSettings()));
        DrawWithSuppressedSecondaryChrome(canvas, size, plot, theme, darkMode);
        // §1's fix applies here too — the right axis is drawn ONCE, in Harmonica.Loadline rather than
        // Harmonica.EfficiencyTrace, through the SAME colour-parametrized overlay.
        DrawSecondaryAxisOverlay(canvas, size, plot, theme, theme.Loadline);
    }

    /// <summary>Owner: show 2 RF cycles on the Time Domain view rather than 1, so a period's shape
    /// reads as periodic rather than as a single, possibly-ambiguous-looking arc.</summary>
    internal const int TimeDomainCycles = 2;

    /// <summary>
    /// The time axis is <c>i / N × (1/f₀)</c>, <c>N = LoadlineVds.Length − 1</c> (the array is closed
    /// over one cycle — <c>HarmonicaSolver.BuildLoadline</c> repeats the first sample as the last — so
    /// <c>N</c> is exactly <c>Settings.LoadlineSamples</c>), extended to
    /// <see cref="TimeDomainCycles"/> cycles by wrapping the source index (see the loop below),
    /// labelled in <b>nanoseconds</b>: at the shipped f₀ = 2 GHz one period is 0.5 ns, which reads as
    /// a plain number in ns (<c>NumDigitsXAxis</c>'s default 5 significant figures) without the extra
    /// zeros picoseconds would add for a period this size.
    /// </summary>
    internal static Plot BuildTimeDomainPlot(LoadlinePanelData d, HarmonicaRenderTheme theme,
                                             StoredAxisWindow limits = default)
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz)
        {
            ShowWatermark = false,
            CustomTitleOn = true, CustomTitle = "Time Domain", CustomTitleBold = true,
            CustomXLabelOn = true, CustomXLabel = "Time (ns)",
            CustomYLabelOn = true, CustomYLabel = "Vds (V)",
            CustomY2LabelOn = true, CustomY2Label = "Ids (A)",
        };
        // Matches the Smith panels' own row-1 title height — see RectTitleFontSizeLabel's own remarks.
        plot.Axes.FontSizeLabel = RectTitleFontSizeLabel;

        int n = d.LoadlineVds.Length;
        if (n > 1 && d.FrequencyHz > 0)
        {
            double periodNs = 1e9 / d.FrequencyHz;
            int cycleSamples = n - 1;
            // Owner: show 2 RF cycles, not 1. LoadlineVds/LoadlineIds is one CLOSED cycle (its last
            // sample already repeats its first — HarmonicaSolver.BuildLoadline's own doc comment), so
            // wrapping the source index modulo cycleSamples repeats that exact closed waveform a
            // second time rather than re-deriving it — the seam at t=periodNs lands on the identical
            // value either way, since d.LoadlineVds[cycleSamples] == d.LoadlineVds[0] already.
            int total = cycleSamples * TimeDomainCycles + 1;
            double[] t   = new double[total];
            double[] vds = new double[total];
            double[] ids = new double[total];
            for (int i = 0; i < total; i++)
            {
                t[i] = (double)i / cycleSamples * periodNs;
                int src = i % cycleSamples;
                vds[i] = d.LoadlineVds[src];
                ids[i] = d.LoadlineIds[src];
            }

            plot.Traces.Add(NewRectTrace(t, vds, theme.GainTrace, width: 1.6));
            plot.Traces.Add(NewRectTrace(t, ids, theme.Loadline, width: 1.6, secondary: true));
        }

        AutoScale(plot);
        // brief-harmonicarf-r6e §2.4/§4 — the Time Domain view's OWN stored window, never the
        // power-sweep one, even though the two share this panel slot (§4's own instruction).
        ApplyStoredWindow(plot, limits, hasSecondary: true);
        // R-h9b-11 — the same pinned shape every §7.4-family plot uses, so this panel's data
        // rectangle lines up with the power-sweep view it replaces (they occupy the same layout slot).
        plot.Axes.Viewport = PowerSweepShapedViewport();
        return plot;
    }

    private static void DrawTimeDomainEmptyNote(SKCanvas canvas, (double W, double H) size,
                                                 HarmonicaRenderTheme theme)
    {
        float ts = (float)Math.Max(9.0, Math.Min(size.W, size.H) * 0.05);
        using var font  = new SKFont(SkiaFonts.PlexRegular, ts);
        using var paint = new SKPaint { Color = theme.GridPointDropped, IsAntialias = true };
        canvas.DrawText("intrinsic plane not located", 6f, (float)(ts + 4f), SKTextAlign.Left, font, paint);
    }

    // ── §7.7 — a picked trace's own panel ────────────────────────────────────

    /// <summary>
    /// Draws one picked trace (R-h7-5) into its panel. It is an ordinary <see cref="Plot"/> through
    /// <see cref="PlotRenderer.Draw"/> with harmonicaRF's palette — the same route the power-sweep
    /// panel takes, and deliberately not a second renderer.
    /// </summary>
    public static void DrawPickedTracePanel(SKCanvas canvas, (double W, double H) size,
                                            Plot? plot, string? error,
                                            HarmonicaRenderTheme theme, bool darkMode)
    {
        if (plot is not null)
        {
            PlotRenderer.Draw(canvas, size, plot, PlotDetail.Full, theme.ToPlotTheme(darkMode),
                              watermarkOpacity: 0f);
            return;
        }

        // A spec that no longer resolves must SAY so on the panel. An empty rectangle where a trace
        // used to be reads as a failed solve, which is a different and much more alarming thing.
        float ts = (float)Math.Max(9.0, Math.Min(size.W, size.H) * 0.05);
        using var font  = new SKFont(SkiaFonts.PlexRegular, ts);
        using var paint = new SKPaint { Color = theme.GridPointDropped, IsAntialias = true };
        canvas.DrawText(error ?? "no trace", 6f, (float)(ts + 4f), SKTextAlign.Left, font, paint);
    }

    // ── trace construction ───────────────────────────────────────────────────

    private static Trace NewRectTrace(double[] x, double[] y, SKColor colour,
                                      double width, bool secondary = false)
    {
        var t = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real,
                          secondaryAxis: secondary);
        t.Properties.LineColorStorage =
            Avalonia.Media.Color.FromArgb(colour.Alpha, colour.Red, colour.Green, colour.Blue);
        t.Properties.LineWidth   = width;
        t.Properties.LineEnabled = true;
        t.SetCubeData(x, null, y, "x", null, PlotType.Rect, FreqUnit.GHz);
        return t;
    }

    /// <summary>Fits the window to the traces, primary and secondary axes separately. A panel drawn
    /// into an unfitted window would be blank, which reads as "the solve failed".</summary>
    internal static void AutoScale(Plot plot)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minY2 = double.MaxValue, maxY2 = double.MinValue;

        foreach (var t in plot.Traces)
        {
            var r = t.PathBoundingRect();
            if (r.Width <= 0 && r.Height <= 0) continue;
            minX = Math.Min(minX, r.X); maxX = Math.Max(maxX, r.X + r.Width);
            if (t.UseSecondaryAxis) { minY2 = Math.Min(minY2, r.Y); maxY2 = Math.Max(maxY2, r.Y + r.Height); }
            else                    { minY  = Math.Min(minY,  r.Y); maxY  = Math.Max(maxY,  r.Y + r.Height); }
        }

        if (minX < maxX && minY < maxY)
            plot.Axes.Window = Pad(minX, minY, maxX, maxY);
        if (minX < maxX && minY2 < maxY2)
            plot.Axes.WindowSecondary = Pad(minX, minY2, maxX, maxY2);
    }

    private static Avalonia.Rect Pad(double x0, double y0, double x1, double y1)
    {
        double w = x1 - x0, h = y1 - y0;
        if (w <= 0) w = 1; if (h <= 0) h = 1;
        return new Avalonia.Rect(x0, y0 - h * 0.05, w, h * 1.10);
    }

    // ── brief-harmonicarf-r6e §2 — persisted axis limits + autoscale, one mechanism, three plots ──

    /// <summary>
    /// One plot's worth of <c>HarmonicaSettings</c>' stored axis fields, resolved to a single value
    /// so <see cref="ApplyStoredWindow"/> does not need to know which plot it is applying to.
    /// <c>Y2Min</c>/<c>Y2Max</c> are unused (left null) for the DCIV/loadline plot, which has no
    /// secondary axis.
    /// </summary>
    internal readonly record struct StoredAxisWindow(
        double? XMin, double? XMax, double? YMin, double? YMax,
        double? Y2Min, double? Y2Max, bool Autoscale);

    internal static StoredAxisWindow DcivLimits(HarmonicaSettings s) => new(
        s.DcivXMin, s.DcivXMax, s.DcivYMin, s.DcivYMax, null, null, s.DcivAutoscale);

    internal static StoredAxisWindow PowerSweepLimits(HarmonicaSettings s) => new(
        s.PowerSweepXMin, s.PowerSweepXMax, s.PowerSweepYMin, s.PowerSweepYMax,
        s.PowerSweepY2Min, s.PowerSweepY2Max, s.PowerSweepAutoscale);

    internal static StoredAxisWindow TimeDomainLimits(HarmonicaSettings s) => new(
        s.TimeDomainXMin, s.TimeDomainXMax, s.TimeDomainYMin, s.TimeDomainYMax,
        s.TimeDomainY2Min, s.TimeDomainY2Max, s.TimeDomainAutoscale);

    /// <summary>
    /// brief-harmonicarf-r6e §2.3/§2.4 — applied AFTER <see cref="AutoScale"/> (and, for the power
    /// sweep, after <c>PinAxisPin</c> and the right-edge headroom) — <b>an explicit user limit is the
    /// user's, and nothing may silently correct it, so this always wins when it applies.</b>
    ///
    /// <para><b>Autoscale ON leaves the just-computed window untouched</b> — that IS "autoscale":
    /// whatever <see cref="AutoScale"/>/<c>PinAxisPin</c>/the headroom fraction computed is what
    /// gets captured back into <c>HarmonicaSettings</c> by
    /// <see cref="Harmonica.HarmonicaViewModel.CaptureAxisWindows"/> on the next published frame, so
    /// turning autoscale back off freezes exactly what is on screen (§2.3).</para>
    ///
    /// <para><b>Autoscale OFF with no stored limit (§2.2)</b> — <c>XMin</c>/<c>YMin</c> null — is
    /// ALSO a no-op here: the computed window is left exactly as <see cref="AutoScale"/> fit it, which
    /// is what a document that has never had its axes touched looks like today. It is
    /// <see cref="Harmonica.HarmonicaViewModel.CaptureAxisWindows"/>'s job to notice the absence and
    /// store this same window once, so the NEXT frame holds it — this method never writes anywhere,
    /// it only reads.</para>
    /// </summary>
    private static void ApplyStoredWindow(Plot plot, StoredAxisWindow limits, bool hasSecondary)
    {
        if (limits.Autoscale) return;

        if (limits.XMin is { } xMin && limits.XMax is { } xMax && xMax > xMin)
        {
            var w = plot.Axes.Window;
            plot.Axes.Window = new Avalonia.Rect(xMin, w.Y, xMax - xMin, w.Height);
            if (hasSecondary)
            {
                var w2 = plot.Axes.WindowSecondary;
                plot.Axes.WindowSecondary = new Avalonia.Rect(xMin, w2.Y, xMax - xMin, w2.Height);
            }
        }

        if (limits.YMin is { } yMin && limits.YMax is { } yMax && yMax > yMin)
        {
            var w = plot.Axes.Window;
            plot.Axes.Window = new Avalonia.Rect(w.X, yMin, w.Width, yMax - yMin);
        }

        if (hasSecondary && limits.Y2Min is { } y2Min && limits.Y2Max is { } y2Max && y2Max > y2Min)
        {
            var w2 = plot.Axes.WindowSecondary;
            plot.Axes.WindowSecondary = new Avalonia.Rect(w2.X, y2Min, w2.Width, y2Max - y2Min);
        }
    }
}
