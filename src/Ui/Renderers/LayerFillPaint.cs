// The one place a layer's fill paint is built — solid or through a stipple.
//
// Three call sites want it (the per-layer draw, the placed-instance draw, and the drag/paste ghost)
// and before stipples existed all three built the same two-line SKPaint independently. That was
// harmless while a fill was a colour and an alpha; it stops being harmless the moment a fill can
// also be a repeating mask at a zoom-compensated scale, because three copies of THAT drift.

using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Builds the <see cref="SKPaint"/> a layer's interiors are filled with, and owns the bitmap cache
/// that makes a stipple cheap.
/// </summary>
internal static class LayerFillPaint
{
    /// <summary>
    /// How many device pixels one texel of a stipple occupies.
    ///
    /// <para><b>A stipple is a SCREEN-space texture, not a world-space one</b>, which is the whole
    /// reason it works as an identifier: it has to stay the same size and density however far the
    /// user is zoomed in, exactly as the hairline outline does. Scaled with the geometry it would be
    /// a moiré field at low zoom and a set of enormous stripes at high zoom, and would tell nobody
    /// anything at either end.</para>
    /// </summary>
    private const double TexelDevicePixels = 1.0;

    /// <summary>
    /// Coloured pattern bitmaps, keyed by the mask and the exact colour painted through it.
    ///
    /// <para>Keyed on colour as well as mask because a shader's texels ARE the painted pixels — one
    /// mask shared by twenty layers of different colours is twenty bitmaps. They are small (a 16×16
    /// mask is 1 KB) and the population is bounded by the technology's own layer count, so this is a
    /// cache that cannot grow without a new technology being loaded.</para>
    ///
    /// <para>Concurrent because rendering is not confined to one thread — see
    /// <c>LayoutRenderThreadSafetyTests</c>.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, uint), SKBitmap> Bitmaps = new();

    /// <summary>Beyond this the cache is cleared rather than grown. A technology's layer table is the
    /// natural bound and is far below it; passing it means something is generating masks, and a
    /// cleared cache costs a rebuild while an unbounded one costs the session.</summary>
    private const int MaxCachedBitmaps = 4096;

    /// <summary>
    /// The fill paint for <paramref name="def"/>, at <paramref name="scaleUm"/> device pixels per
    /// micron. The caller owns the returned paint.
    /// </summary>
    /// <param name="pattern">The stipple <paramref name="def"/> names, already resolved against the
    /// technology, or null for a solid fill. Resolved by the caller because the lookup is per
    /// technology and this is called per layer per frame.</param>
    /// <param name="counters">This frame's counters, if the caller has them. Counted PER FRAME
    /// rather than in a static of this type's own: a process-wide counter reads correctly in
    /// isolation and is meaningless under a parallel test run, where any other test rendering a
    /// layout perturbs it — the same reason the generated-cell writes are counted per workspace.</param>
    internal static SKPaint Create(LayerDef def, FillPattern? pattern, SKColor color, double scaleUm,
                                   LayoutFrameCounters? counters = null)
    {
        if (counters is not null) counters.FillPaintsBuilt++;

        byte alpha = (byte)System.Math.Clamp(System.Math.Round(def.FillOpacity * 255.0), 0, 255);
        var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(alpha) };

        // Nothing to paint through, or nothing to paint with: the plain solid fill, unchanged from
        // before stipples existed. A mask that states nothing usable lands here too and fills solid,
        // matching what a name that resolves to nothing does — visible and recoverable, rather than
        // a layer that silently disappears.
        if (pattern is null || alpha == 0 || pattern.Size <= 0) return paint;

        // A mask with NO set texel paints nothing, and is a process saying "outline only" as surely
        // as a hollow flag is. It must not fall through to the solid fill above — that would turn the
        // one instruction the mask carries into its exact opposite. No shader either: allocating a
        // fully transparent bitmap to draw nothing through is pure waste.
        if (pattern.IsBlank)
        {
            paint.Color = SKColors.Transparent;
            return paint;
        }

        var bitmap = BitmapFor(pattern, color.WithAlpha(alpha));
        if (bitmap is null) return paint;

        // Scale ALONE, anchored at the path-space origin: the pattern then keeps a fixed device size
        // through zoom (the point of the exercise) while still travelling with the geometry under a
        // pan, rather than swimming across it the way a device-space anchor would.
        float texel = (float)(TexelDevicePixels / System.Math.Max(scaleUm, 1e-12));
        paint.Shader = SKShader.CreateBitmap(
            bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateScale(texel, texel));

        // The shader supplies the colour; leaving the paint's own colour set would tint nothing but
        // would make the paint's state a lie to anything that reads it back.
        paint.Color = SKColors.White;
        return paint;
    }

    private static SKBitmap? BitmapFor(FillPattern pattern, SKColor color)
    {
        int size = pattern.Size;
        var key = (pattern.Name, size, (uint)color);

        if (Bitmaps.TryGetValue(key, out var cached)) return cached;
        if (Bitmaps.Count >= MaxCachedBitmaps) Clear();

        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bitmap.SetPixel(x, y, pattern.IsSet(y, x) ? color : SKColors.Transparent);

        // GetOrAdd rather than an unconditional store: two threads rendering the same layer would
        // otherwise both build it and the loser's bitmap would be dropped on the floor undisposed.
        var stored = Bitmaps.GetOrAdd(key, bitmap);
        if (!ReferenceEquals(stored, bitmap)) bitmap.Dispose();
        return stored;
    }

    /// <summary>Drops every cached bitmap. Called when the cache is full, and available to a test
    /// that needs to observe a build rather than a hit.</summary>
    internal static void Clear()
    {
        foreach (var key in Bitmaps.Keys)
            if (Bitmaps.TryRemove(key, out var b)) b.Dispose();
    }

    /// <summary>How many bitmaps are cached. Test instrumentation — a stipple that is rebuilt per
    /// frame is a performance defect that is invisible in a screenshot.</summary>
    internal static int CachedBitmapCount => Bitmaps.Count;

}
