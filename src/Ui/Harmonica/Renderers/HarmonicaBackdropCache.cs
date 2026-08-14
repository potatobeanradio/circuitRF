using System;
using SkiaSharp;

namespace CircuitRF.Ui.Harmonica.Renderers;

/// <summary>
/// brief-harmonicarf-r4 §4 — the two cached raster layers behind ONE Smith panel (chrome + frozen
/// contours + the optimum cross, and separately the grid-point dots), each an offscreen
/// <see cref="SKSurface"/> snapshotted to an <see cref="SKImage"/> and blitted rather than re-issued
/// as draw calls every frame.
///
/// <para><b>Rasterised, not <c>SKPicture</c> — deliberately (§4.2).</b> RESOLVED.md's own §1.4
/// measurement found the two Smith panels scaling ~2.9× from 1x to 2x device pixel density against
/// ~4× the pixel count — draw-call COUNT is scale-invariant, so that scaling means the cost is
/// rasterisation-bound, not draw-call-bound. Replaying an <c>SKPicture</c> re-executes the same draw
/// commands and saves the geometry/layout work, not the antialiased path rasterisation, which is the
/// expensive part. A real offscreen render, snapshotted once and blitted, is what actually amortises
/// rasterisation across frames.</para>
///
/// <para><b>Owned per-panel, per-document — never static.</b> <c>HarmonicaCanvas</c> holds one
/// instance for the Power panel and one for the Efficiency panel, matching
/// <c>src/Harmonica/CLAUDE.md</c>'s "no static mutable state" rule (a rule written for the
/// framework-free side, but the reasoning — two documents, two workers, must never share render
/// state — applies here too). A caller that has no cache (export, Copy Plot, a one-off render) simply
/// never constructs one; <see cref="HarmonicaPanelRenderer.DrawSmithPanel"/> falls back to its
/// original uncached draw when none is supplied.</para>
///
/// <para><b>Layer B is not blitted as its own translucent image — it is FUSED onto a copy of Layer A's
/// (opaque) pixels in one compositing pass, via <see cref="GetOrRenderFusedWithLayerB"/>.</b> The
/// grid-point dots are sparse, so their own raster can never be pre-filled with an opaque background
/// the way Layer A's clear color makes Layer A exact (see that method's own doc comment) — two
/// separately-blitted translucent layers means every dot's antialiased edge is 8-bit-rounded once when
/// IT is rasterised and rounded AGAIN when it is composited over Layer A, and that second rounding is
/// real: caught at up to ±2 levels/channel on ~1% of the backdrop by <c>HarmonicaBackdropCacheTests</c>
/// even after the matrix-phase and opaque-background fixes closed the far larger mismatches. Drawing
/// the dots directly onto a copy of Layer A's own already-opaque pixels makes it exactly ONE
/// compositing pass per pixel — the same as the uncached path, which draws dots directly over the
/// live, already-rendered chrome.</para>
/// </summary>
public sealed class HarmonicaBackdropCache : IDisposable
{
    private SKImage? _layerA;
    private object?  _layerAKey;

    private SKImage? _fused;         // Layer A's pixels with Layer B's dots drawn on top, one pass
    private SKImage? _fusedFromLayerA; // which Layer A IMAGE (by reference) the fused raster reflects
    private object?  _fusedFromLayerBKey;

    /// <summary>How many times Layer A (chrome + frozen contours + optimum) was actually
    /// re-rendered — a counter, not a stopwatch, this repo's own convention
    /// (<c>Retries</c>/<c>BracketRefineProbes</c>' precedent) for making a cache's own hit rate
    /// visible rather than inferred.</summary>
    public int LayerARebuilds { get; private set; }

    /// <summary>How many times Layer B's OWN data (the grid-point set) actually changed and its dots
    /// were redrawn. Does NOT increment when the fused raster is merely recomposed because Layer A
    /// changed underneath it — see <see cref="GetOrRenderFusedWithLayerB"/>'s own note.</summary>
    public int LayerBRebuilds { get; private set; }

    /// <summary>
    /// Returns Layer A's cached image, re-rendering it first if <paramref name="key"/> does not equal
    /// the key it was last built from (<see cref="object.Equals(object?)"/> — the caller's key type,
    /// almost always a <c>record</c>, gets value equality for free). <paramref name="pixelSize"/> is
    /// the OFFSCREEN surface's size in real pixels. <paramref name="matrix"/> is baked into the
    /// offscreen canvas via <c>SetMatrix</c> — not a bare device-scale <c>Scale()</c> — so that
    /// <paramref name="draw"/>'s antialiased output lands on the EXACT SAME sub-pixel grid the live
    /// canvas would have used; see <see cref="HarmonicaPanelRenderer.DrawSmithPanelCached"/>'s own note
    /// for why a plain scale is not enough to make a cached and an uncached frame pixel-identical.
    ///
    /// <para><b><paramref name="clearColor"/> must be OPAQUE</b> — the panel's real background, since
    /// Layer A fully covers the chart rect — so every antialiased edge is blended against it exactly
    /// ONCE, the same as the uncached draw would. Clearing to a transparent surface instead means every
    /// AA edge gets blended a SECOND time when the layer is later composited, and 8-bit premultiplied
    /// rounding at that extra step is real, not merely theoretical — caught directly by
    /// <c>HarmonicaBackdropCacheTests</c>.</para>
    /// </summary>
    public SKImage GetOrRenderLayerA(object key, SKSizeI pixelSize, SKMatrix matrix, SKColor clearColor,
                                     Action<SKCanvas> draw)
    {
        if (_layerA is not null && key.Equals(_layerAKey) &&
            _layerA.Width == pixelSize.Width && _layerA.Height == pixelSize.Height)
            return _layerA;

        _layerA?.Dispose();
        _layerA = RenderOffscreen(pixelSize, matrix, clearColor, draw);
        _layerAKey = key;
        LayerARebuilds++;
        return _layerA;
    }

    /// <summary>
    /// Returns Layer A fused with the grid-point dots, rebuilding the fused raster whenever EITHER
    /// Layer A's own image changed (by reference — <paramref name="layerA"/> is whatever
    /// <see cref="GetOrRenderLayerA"/> just returned this frame) OR <paramref name="key"/> (the
    /// grid-point set) differs from what the fused raster currently reflects.
    ///
    /// <para><b><see cref="LayerBRebuilds"/> counts only the SECOND case.</b> A contour edit forces a
    /// recompose here too (the dots have to be redrawn on top of A's new pixels — there is no way to
    /// patch just the changed region), but that recompose is driven entirely by Layer A's own change,
    /// not by anything Layer B's caller is responsible for, so it must not be attributed to Layer B —
    /// <c>HarmonicaBackdropCacheTests.ChangingContours_RebuildsLayerA_NotLayerB</c> pins exactly this
    /// distinction.</para>
    /// </summary>
    public SKImage GetOrRenderFusedWithLayerB(object key, SKImage layerA, SKMatrix matrix,
                                              Action<SKCanvas> drawDots)
    {
        bool bKeyChanged = _fusedFromLayerBKey is null || !key.Equals(_fusedFromLayerBKey);
        bool aImageChanged = !ReferenceEquals(layerA, _fusedFromLayerA);
        bool sizeChanged = _fused is null ||
                           _fused.Width != layerA.Width || _fused.Height != layerA.Height;

        if (_fused is not null && !bKeyChanged && !aImageChanged && !sizeChanged)
            return _fused;

        _fused?.Dispose();
        var info = new SKImageInfo(layerA.Width, layerA.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var surface = SKSurface.Create(info))
        {
            // An exact, opaque-source copy — no blend, no rounding — then the dots composite onto it
            // in the SAME single pass the uncached path draws them in, over the already-opaque chrome.
            surface.Canvas.DrawImage(layerA, 0, 0);
            surface.Canvas.Save();
            surface.Canvas.SetMatrix(matrix);
            drawDots(surface.Canvas);
            surface.Canvas.Restore();
            _fused = surface.Snapshot();
        }
        _fusedFromLayerA = layerA;
        _fusedFromLayerBKey = key;
        if (bKeyChanged) LayerBRebuilds++;
        return _fused;
    }

    private static SKImage RenderOffscreen(SKSizeI pixelSize, SKMatrix matrix, SKColor clearColor,
                                           Action<SKCanvas> draw)
    {
        var info = new SKImageInfo(Math.Max(1, pixelSize.Width), Math.Max(1, pixelSize.Height),
                                   SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(clearColor);
        surface.Canvas.Save();
        surface.Canvas.SetMatrix(matrix);
        draw(surface.Canvas);
        surface.Canvas.Restore();
        return surface.Snapshot();
    }

    public void Dispose()
    {
        _layerA?.Dispose();
        _fused?.Dispose();
        _layerA = null; _fused = null; _fusedFromLayerA = null;
        _layerAKey = null; _fusedFromLayerBKey = null;
    }
}
