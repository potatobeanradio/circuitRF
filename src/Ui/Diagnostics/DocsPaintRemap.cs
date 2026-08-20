using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// THE DOCS-ONLY BLACK-ALPHA REMAP — the fix for a defect that is otherwise invisible until someone
/// looks at a shipped figure six months later.
///
/// <para><b>The finding (measured against Skia 3.119.4, not inferred).</b> Skia's SVG device omits
/// the <c>fill</c> attribute when the paint colour is pure black, because black is the SVG default —
/// and it drops <c>fill-opacity</c> along with it. A 20 %-opaque black brush therefore serialises as
/// a bare shape with no paint attributes at all, which every browser renders as an OPAQUE BLACK
/// slab:</para>
/// <code>
///   fill #33000000  ->  &lt;path d=… /&gt;                                      (wrong: opaque black)
///   fill #33010101  ->  &lt;path fill="#010101" fill-opacity="0.2" d=… /&gt;     (right)
/// </code>
/// <para>Fluent's light-theme <c>ButtonBackground</c> IS <c>#33000000</c>, so a naive capture draws
/// every Button as a black slab with its label invisible on top. Verified directly in this repo: a
/// probe capture of one Button emitted exactly one paintless element, and it was the button body.</para>
///
/// <para><b>Strokes are NOT affected</b> — measured in the same probe. Skia writes
/// <c>stroke="black" stroke-opacity="0.6"</c> for a <c>#99000000</c> pen, correctly. Only fills lose
/// their paint, which is why <see cref="SvgLint"/> treats a shape with neither attribute, rather
/// than "any black", as the defect.</para>
///
/// <para><b>Why it is discovered rather than listed.</b> A hand-written list of Fluent brush keys
/// goes stale the first time Avalonia re-tints a control. <see cref="Build"/> collects every
/// resource key reachable from the live application's style tree, resolves each one PER THEME
/// VARIANT through the ordinary resource lookup (the theme dictionaries store deferred items, so
/// enumerating their raw values finds nothing — the lookup is what materialises a brush), and
/// re-points the pure-black ones. <see cref="SvgLint"/> is the backstop: a shape that still reaches
/// the SVG with no paint fails generation and names itself.</para>
///
/// <para>Merged by <c>tools/DocGen</c>'s <c>DocsApp</c> only. The shipping application never sees
/// it.</para>
/// </summary>
public static class DocsPaintRemap
{
    /// <summary>The one bit of red that keeps Skia from treating the colour as "default black".</summary>
    public const byte OffBlack = 0x01;

    /// <summary>True when <paramref name="c"/> is pure black with any alpha — the losing case.</summary>
    public static bool IsPureBlack(Color c) => c is { R: 0, G: 0, B: 0 };

    /// <summary>Pure black nudged one bit off, alpha preserved. Visually identical.</summary>
    public static Color Nudge(Color c) => Color.FromArgb(c.A, OffBlack, OffBlack, OffBlack);

    /// <summary>The variants a docs figure is ever rendered in.</summary>
    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    /// <summary>
    /// Scan <paramref name="app"/>'s whole style/resource tree and return a dictionary that
    /// re-points every pure-black brush it can resolve, keyed per theme variant. Merge the result
    /// LAST so it wins over the theme it is correcting.
    /// </summary>
    public static ResourceDictionary Build(Application app) => Build(app, out _);

    /// <inheritdoc cref="Build(Application)"/>
    /// <param name="app">The live application.</param>
    /// <param name="remapped">The keys re-pointed, per variant — reported by the generator.</param>
    public static ResourceDictionary Build(Application app, out IReadOnlyList<string> remapped)
    {
        var keys = CollectKeys(app);
        var report = new List<string>();
        var result = new ResourceDictionary();

        foreach (var variant in Variants)
        {
            var vd = new ResourceDictionary();
            foreach (var key in keys)
            {
                if (!app.TryGetResource(key, variant, out var value)) continue;
                switch (value)
                {
                    // OPAQUE pure black is remapped TOO, even though dropping its `fill` renders
                    // correctly (black IS the SVG default). Measured reason, not tidiness: a light
                    // theme's icon foreground is opaque black, so a Material icon serialises with no
                    // paint at all — which is byte-for-byte what a DROPPED paint looks like. Leaving
                    // opaque black alone left the lint unable to tell a correct icon from a black
                    // slab, and a lint with dozens of benign findings is a lint nobody reads. Making
                    // every black explicit costs about twenty bytes per glyph run and buys a lint
                    // whose every finding is real.
                    case ISolidColorBrush b when IsPureBlack(b.Color) && b.Color.A != 0:
                        vd[key] = new SolidColorBrush(Nudge(b.Color));
                        report.Add($"{Describe(key)} [{variant}] {b.Color}");
                        break;

                    // A fully transparent brush is ALSO pure black (#00000000), and Skia drops its
                    // paint the same way — turning an invisible border into a solid black one. It
                    // cannot be nudged to a visible colour, so it is remapped to an explicitly
                    // transparent NON-black brush instead.
                    case ISolidColorBrush { Color.A: 0 }:
                        vd[key] = new SolidColorBrush(Color.FromArgb(0, OffBlack, OffBlack, OffBlack));
                        report.Add($"{Describe(key)} [{variant}] transparent");
                        break;

                    // A resource can be a COLOR rather than a brush, and a Color assigned to a brush
                    // property is converted on the way in — so it reaches Skia as a pure-black paint
                    // just the same. This is not hypothetical: CircuitRfStyles.axaml softens toolbar
                    // icons with Foreground="{DynamicResource SystemBaseMediumColor}", which is
                    // #99000000, and every toolbar icon in the Schematic and Data Display editors
                    // serialised with no paint until this case existed. Re-point it as a COLOR, or
                    // the override does not type-match the consumer and is silently ignored.
                    case Color c when IsPureBlack(c) && c.A != 0:
                        vd[key] = Nudge(c);
                        report.Add($"{Describe(key)} [{variant}] {c} (Color)");
                        break;
                }
            }
            if (vd.Count > 0) result.ThemeDictionaries[variant] = vd;
        }

        remapped = report;
        return result;
    }

    /// <summary>Convenience for the generator: build against <see cref="Application.Current"/>.</summary>
    public static ResourceDictionary Dictionary()
        => Application.Current is { } app ? Build(app) : new ResourceDictionary();

    private static string Describe(object key) => key as string ?? key.ToString() ?? "?";

    // ── Collecting every reachable resource key ───────────────────────────────

    /// <summary>
    /// Every key declared anywhere in the application's resources or in any style's resources,
    /// including per-variant ThemeDictionaries and merged dictionaries. Keys only — the VALUES in a
    /// theme dictionary are deferred items, so they must be resolved through
    /// <see cref="IResourceHost.TryGetResource"/> rather than read directly.
    /// </summary>
    internal static IReadOnlyCollection<object> CollectKeys(Application app)
    {
        var keys = new HashSet<object>();
        Collect(app.Resources, keys);
        WalkStyles(app.Styles, keys);
        return keys;
    }

    private static void WalkStyles(IEnumerable<IStyle> styles, HashSet<object> keys)
    {
        foreach (var s in styles)
        {
            switch (s)
            {
                case StyleInclude si when si.Loaded is { } loaded:
                    WalkStyles([loaded], keys);
                    break;
                case Styles group:
                    Collect(group.Resources, keys);
                    WalkStyles(group, keys);
                    break;
                case Style style:
                    Collect(style.Resources, keys);
                    break;
            }
        }
    }

    private static void Collect(IResourceDictionary? dict, HashSet<object> keys)
    {
        if (dict is not ResourceDictionary rd) return;
        foreach (var key in rd.Keys) keys.Add(key);
        foreach (var (_, sub) in rd.ThemeDictionaries) Collect(sub as IResourceDictionary, keys);
        foreach (var merged in rd.MergedDictionaries) Collect(merged as IResourceDictionary, keys);
    }
}
