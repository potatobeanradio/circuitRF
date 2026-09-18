using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// SKColor token bundle for railRF's board maps — on <see cref="LayoutRenderTheme"/>'s exact pattern,
/// and for its reason: a renderer's colours are a PROJECTION of the active theme, never hardcoded
/// (docs/design/color-themes.md, L2).
///
/// <h3>Every colour here is opaque, and that is a design constraint</h3>
/// <para>railrf.md §11.7 point 2 decides it here "rather than discovered in an export". For every
/// other copy in this application the page background is a <i>backdrop</i>, so honouring transparency
/// is a matter of not painting it. The stackup copy is the counter-example: its renderer uses the
/// background colour as <b>paint</b>, to cut a drill hole, and simply not painting the bore did not
/// make it transparent — it showed the dielectric behind it, and a hole had to be cut in what was
/// behind.</para>
///
/// <para><b>A railRF map must not acquire that shape.</b> The shading is opaque paint laid over the
/// copper and the legend carries its own scale, so nothing in the picture depends on the page's
/// background being any particular colour. It is free to honour while this is being written and
/// expensive to retrofit after one background-dependent effect has been used.</para>
/// </summary>
public sealed class RailMapTheme
{
    /// <summary>The cold end of the drop ramp — the highest voltage on the rail.</summary>
    public SKColor MapCold { get; init; }

    /// <summary>The ramp's midpoint (see <see cref="ColorRole.RailMapMid"/> for why it is a stop of
    /// its own rather than a blend).</summary>
    public SKColor MapMid { get; init; }

    /// <summary>The hot end — the lowest voltage, the far end of the drop.</summary>
    public SKColor MapHot { get; init; }

    /// <summary>The legend plate. Opaque, per this class's header.</summary>
    public SKColor LegendBackground { get; init; }

    /// <summary>Legend text, its frame, and every callout label.</summary>
    public SKColor LegendInk { get; init; }

    public SKColor Source { get; init; }
    public SKColor Load { get; init; }

    /// <summary>A flagged via transition's callout (brief 6).</summary>
    public SKColor ViaFlag { get; init; }

    public SKColor ClassTrace { get; init; }
    public SKColor ClassSpreading { get; init; }

    /// <summary>The accent on a region the user FORCED — §2.9 rule 2 is about being able to see what
    /// you overrode.</summary>
    public SKColor ClassForced { get; init; }

    /// <summary>The rail's own copper on the <c>copper</c> tab, which carries no map.</summary>
    public SKColor CopperHighlight { get; init; }

    public static RailMapTheme FromTheme(ColorTheme theme, ColorVariant variant)
    {
        ArgumentNullException.ThrowIfNull(theme);

        SKColor SK(string role)
        {
            var c = theme.Resolve(role, variant);
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return new RailMapTheme
        {
            MapCold          = SK(ColorRole.RailMapCold),
            MapMid           = SK(ColorRole.RailMapMid),
            MapHot           = SK(ColorRole.RailMapHot),
            LegendBackground = SK(ColorRole.RailLegendBackground),
            LegendInk        = SK(ColorRole.RailLegendInk),
            Source           = SK(ColorRole.RailSource),
            Load             = SK(ColorRole.RailLoad),
            ViaFlag          = SK(ColorRole.RailViaFlag),
            ClassTrace       = SK(ColorRole.RailClassTrace),
            ClassSpreading   = SK(ColorRole.RailClassSpreading),
            ClassForced      = SK(ColorRole.RailClassForced),
            CopperHighlight  = SK(ColorRole.RailCopperHighlight),
        };
    }

    /// <summary>
    /// The colour for a normalised position on the ramp, 0 (cold) … 1 (hot).
    /// </summary>
    /// <remarks>
    /// <b>Here rather than in the renderer</b>, because the legend and the map have to agree: a
    /// legend drawn from one ramp beside a map painted from another is worse than no legend, and the
    /// two are drawn by different methods. Linear through the three stops, with the result forced
    /// fully opaque — see this class's header.
    /// </remarks>
    public SKColor Ramp(double t)
    {
        t = double.IsNaN(t) ? 0 : Math.Clamp(t, 0, 1);

        var (a, b, f) = t <= 0.5
            ? (MapCold, MapMid, t * 2)
            : (MapMid, MapHot, (t - 0.5) * 2);

        byte Mix(byte lo, byte hi) => (byte)Math.Round(lo + (hi - lo) * f);
        return new SKColor(Mix(a.Red, b.Red), Mix(a.Green, b.Green), Mix(a.Blue, b.Blue), 255);
    }

    /// <summary>A sensible default so a canvas can draw before a theme is wired up — the BUILT-IN
    /// light palette, not a private copy of it, so "the fallback" and "the shipped theme" cannot
    /// drift apart the way <c>WBondRenderTheme</c>'s had.</summary>
    public static RailMapTheme Fallback { get; } = FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);

    public static RailMapTheme Light { get; } = FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
    public static RailMapTheme Dark  { get; } = FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);
}
