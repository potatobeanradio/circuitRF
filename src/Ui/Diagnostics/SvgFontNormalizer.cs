using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Makes the <c>font-family</c> and <c>font-weight</c> Skia writes into something a BROWSER resolves
/// to the face the figure was actually laid out with.
///
/// <para><b>The defect, measured on the shipped output.</b> For a face loaded from its own file,
/// Skia's SVG device writes the font's FULL name first and its family second, and a weight that does
/// not match either:</para>
/// <code>
///   font-family="IBM Plex Sans SemiBold, IBM Plex Sans" font-weight="500"   (the face is weight 600)
/// </code>
/// <para>Neither half survives the browser. <c>IBM Plex Sans SemiBold</c> is not a declared
/// <c>@font-face</c> family, so it is skipped; the fallback <c>IBM Plex Sans</c> IS declared, at
/// 400/600/700 — and CSS font matching for a requested 500 tries 500, then descends (400) before it
/// ascends, so it lands on <b>Regular</b>. Every one of the 76 symbol captions was authored SemiBold
/// and shipped looking Regular. Skia gets <c>Light</c> right (300) and gets a weight set as a
/// property right (600); it is specifically the "distinct SemiBold file" case that comes out as
/// 500.</para>
///
/// <para><b>The fix</b> is to normalise to the base family and state the weight the face really has,
/// which is exactly what the full-name suffix says. An unrecognised suffix is a generation error, not
/// a guess — a new face must not be able to ship silently mis-weighted.</para>
///
/// <para><b>Glyph fallback is handled here too.</b> A character neither UI font covers makes Skia
/// substitute a PLATFORM font and bake its name into the figure — <c>Lucida Grande</c> on macOS,
/// something else elsewhere. That is two problems at once: the figure stops being reproducible across
/// machines (so a regenerate-and-diff check fails on the wrong OS), and the reader is sent to a font
/// the documentation does not ship. Such a family is rewritten to <see cref="GlyphFallbackFamily"/>,
/// which circuitRF does ship and which covers the geometric shapes the interface uses, and every
/// substitution is reported rather than quietly corrected.</para>
///
/// <para>Framework-free (string and regex only) so it runs in the test suite with no UI platform.</para>
/// </summary>
public static class SvgFontNormalizer
{
    /// <summary>The typeface families circuitRF renders with and the documentation ships.</summary>
    public static readonly IReadOnlyList<string> ShippedFamilies = ["Inter", "IBM Plex Sans", "DejaVu Sans"];

    /// <summary>
    /// Where a glyph none of the UI fonts covers is sent. DejaVu Sans is the widest-coverage face
    /// circuitRF ships, which is why the plot renderers already use it.
    /// </summary>
    public const string GlyphFallbackFamily = "DejaVu Sans";

    /// <summary>Full-name suffix → CSS weight. The nine standard names; anything else fails.</summary>
    private static readonly Dictionary<string, int> SuffixWeight = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Thin"] = 100, ["ExtraLight"] = 200, ["Light"] = 300, ["Regular"] = 400,
        ["Medium"] = 500, ["SemiBold"] = 600, ["Bold"] = 700, ["ExtraBold"] = 800, ["Black"] = 900,
    };

    /// <summary>Suffix words that describe slant rather than weight.</summary>
    private static readonly string[] ItalicWords = ["Italic", "Oblique"];

    /// <summary>One platform-font substitution found in a figure.</summary>
    /// <param name="Family">The platform family Skia baked in.</param>
    /// <param name="Text">The text it was used for — normally a single uncovered glyph.</param>
    public readonly record struct Substitution(string Family, string Text)
    {
        public override string ToString() => $"'{Family}' for {Describe(Text)}";

        private static string Describe(string text)
        {
            var t = text.Trim();
            if (t.Length == 0) return "(empty)";
            var codes = string.Join(" ", t.Take(4).Select(c => "U+" + ((int)c).ToString("X4")));
            return $"\"{t}\" ({codes})";
        }
    }

    private static readonly Regex TextTagRx =
        new(@"<text(?<attrs>[^>]*)>(?<body>.*?)</text>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Rewrite every <c>&lt;text&gt;</c>'s font attributes. Returns the corrected document; reports
    /// any platform-font substitution it had to redirect.
    /// </summary>
    public static string Normalize(string svg, out IReadOnlyList<Substitution> substitutions)
    {
        var found = new List<Substitution>();

        string result = TextTagRx.Replace(svg, m =>
        {
            string attrs = TrimCoordinateLists(m.Groups["attrs"].Value);
            string family = Attr(attrs, "font-family");
            if (family.Length == 0) return $"<text{attrs}>{m.Groups["body"].Value}</text>";

            var (newFamily, weight, style, substituted) = Resolve(family);
            if (substituted is not null) found.Add(new Substitution(substituted, m.Groups["body"].Value));

            // A null weight or style means "nothing to restate", NOT "clear it". Skia gets the weight
            // right whenever it wrote a single family name — 238 correctly-600 runs were being wiped
            // to unweighted by an earlier version of this that removed instead of leaving alone.
            attrs = SetAttr(attrs, "font-family", newFamily);
            if (weight is not null) attrs = SetAttr(attrs, "font-weight", weight.Value.ToString(CultureInfo.InvariantCulture));
            if (style  is not null) attrs = SetAttr(attrs, "font-style", style);

            return $"<text{attrs}>{m.Groups["body"].Value}</text>";
        });

        substitutions = found;
        return result;
    }

    /// <summary>
    /// Repairs every <c>&lt;text&gt;</c>'s per-glyph position lists in a whole SVG document, and does
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is the entry point for SVG that LEAVES THE APPLICATION</b> — a Data Display plot
    /// exported to <c>.svg</c>, a schematic, symbol, layout or wire-bond assembly copied to the
    /// clipboard as SVG. They carry the same Skia defect the documentation figures did (see
    /// <see cref="TrimCoordinateLists"/>): correct in Illustrator, Inkscape, Chrome and Safari,
    /// mangled in Firefox.
    ///
    /// <para><b>Deliberately NOT <see cref="Normalize"/>.</b> That one also rewrites the font family
    /// and weight, and it <i>throws</i> on a face-name word it cannot weigh — which is right for a
    /// documentation build, where a silently mis-weighted caption must not be allowed to ship, and
    /// wrong here: a copy-to-clipboard must not be able to fail because of a font's name. Rewriting
    /// the family is also less obviously desirable for a file the user opens in a vector editor,
    /// where Skia's full face name is what resolves to the exact face. This fixes only what is
    /// invalid, and cannot throw.</para>
    /// </remarks>
    public static string RepairPositionLists(string svg)
        => TextTagRx.Replace(svg, m =>
        {
            string attrs = TrimCoordinateLists(m.Groups["attrs"].Value);
            return ReferenceEquals(attrs, m.Groups["attrs"].Value) || attrs == m.Groups["attrs"].Value
                ? m.Value
                : $"<text{attrs}>{m.Groups["body"].Value}</text>";
        });

    /// <summary>
    /// Removes the trailing separator Skia leaves on a <c>&lt;text&gt;</c>'s per-glyph <c>x</c> and
    /// <c>y</c> lists, and drops either attribute entirely when nothing is left.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-21: the figures' text was "missing or else really small" in Firefox
    /// on Ubuntu, while macOS and Windows were perfect.</b> Reproduced in a Linux Firefox 140 ESR
    /// container and proven by A/B render: the SAME figure, on the same page, in the same browser,
    /// renders correctly the moment these trailing commas are stripped from the DOM.
    ///
    /// <para>Skia's SVG device writes a per-glyph position list with a separator after the LAST
    /// entry:</para>
    /// <code>
    ///   &lt;text ... x="0, 8.11, 15.52, ..., 87.37, " y="12.11, "&gt;Setup Analyses&lt;/text&gt;
    /// </code>
    /// <para>An SVG <c>&lt;list-of-coordinates&gt;</c> may not end in a separator. Gecko applies SVG's
    /// strict error handling and treats the whole attribute as unspecified, so <c>x</c> and <c>y</c>
    /// both fall back to 0: every run is drawn at the element's origin instead of on its baseline —
    /// one line too high — where the enclosing control's clip removes all but a sliver of each glyph.
    /// Measured on the first run of <c>analyses-setup.svg</c>: <c>getBBox().y</c> is <b>-12.00</b> as
    /// shipped and <b>+0.11</b> once trimmed. Blink and WebKit accept the trailing comma, which is the
    /// whole of why this looked like a Linux problem — Edge and Safari are the defaults there, and
    /// Firefox is the default on Ubuntu. <b>It reproduces in Firefox on every platform.</b></para>
    ///
    /// <para>Only <c>x</c> and <c>y</c> are affected; a scan of all 170 generated figures and symbols
    /// found the trailing separator on those two attributes and no others.</para>
    /// </remarks>
    private static string TrimCoordinateLists(string attrs)
    {
        foreach (string name in CoordinateListAttributes)
        {
            var m = Regex.Match(attrs, $@"(?:^|\s){name}\s*=\s*""(?<v>[^""]*)""");
            if (!m.Success) continue;

            string trimmed = m.Groups["v"].Value.TrimEnd(' ', '\t', '\r', '\n', ',');

            // An empty list is as invalid as a trailing separator, and means the same thing as having
            // no attribute at all — so it is removed rather than written back empty. Checked BEFORE
            // "did the trim change anything": an already-empty x="" changes nothing and still has to go.
            if (trimmed.Length == 0) { attrs = RemoveAttr(attrs, name); continue; }
            if (trimmed == m.Groups["v"].Value) continue;

            attrs = SetAttr(attrs, name, trimmed);
        }
        return attrs;
    }

    /// <summary>The per-glyph position lists Skia writes with a trailing separator.</summary>
    private static readonly string[] CoordinateListAttributes = ["x", "y"];

    /// <summary>
    /// Turn Skia's family list into (family, weight, style). A null weight or style means "Skia's own
    /// attribute is correct, leave it alone" — only the full-name case needs the weight restated.
    /// Public so the tests can pin the exact strings that were measured coming out of the SVG device.
    /// </summary>
    public static (string Family, int? Weight, string? Style, string? Substituted) Resolve(string fontFamilyAttr)
    {
        var names = fontFamilyAttr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return (GlyphFallbackFamily, null, null, fontFamilyAttr);

        // The plain case: Skia wrote just the family, because the face IS the family's regular.
        if (names.Length == 1)
        {
            return Shipped(names[0]) is { } exact
                ? (exact, null, null, null)
                : (GlyphFallbackFamily, null, null, names[0]);
        }

        // The two-name case: "<Full Name>, <Family>". The suffix is what the family lacks.
        string family = names[^1];
        if (Shipped(family) is not { } baseFamily)
            return (GlyphFallbackFamily, null, null, family);

        string full = names[0];
        if (!full.StartsWith(baseFamily + " ", StringComparison.OrdinalIgnoreCase))
            // Not the shape Skia produces. Keep the family; do not invent a weight for it.
            return (baseFamily, null, null, null);

        string suffix = full[(baseFamily.Length + 1)..].Trim();
        int? weight = null;
        string? style = null;

        foreach (var word in suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SuffixWeight.TryGetValue(word, out var w)) { weight = w; continue; }
            if (ItalicWords.Contains(word, StringComparer.OrdinalIgnoreCase)) { style = "italic"; continue; }

            throw new InvalidOperationException(
                $"Cannot tell what weight '{full}' is: '{word}' is not a recognised face-name word. "
              + "Skia writes the font's full name into the SVG and the browser cannot resolve it, so "
              + "the docs must restate the weight — add the word to SvgFontNormalizer.SuffixWeight "
              + "rather than letting the face ship silently mis-weighted.");
        }

        return (baseFamily, weight, style, null);
    }

    /// <summary>The shipped family this name is, matched case-insensitively; null if it is not one.</summary>
    private static string? Shipped(string name)
        => ShippedFamilies.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ── Attribute editing ─────────────────────────────────────────────────────

    private static string Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"(?:^|\s){Regex.Escape(name)}\s*=\s*""(?<v>[^""]*)""");
        return m.Success ? m.Groups["v"].Value : "";
    }

    private static string SetAttr(string attrs, string name, string value)
    {
        var rx = new Regex($@"(?<lead>^|\s){Regex.Escape(name)}\s*=\s*""[^""]*""");
        return rx.IsMatch(attrs)
            ? rx.Replace(attrs, m => m.Groups["lead"].Value + name + "=\"" + value + "\"", 1)
            : attrs + $" {name}=\"{value}\"";
    }

    /// <summary>
    /// Deletes one attribute, matching only at an attribute BOUNDARY.
    /// </summary>
    /// <remarks>
    /// The leading <c>\s</c> is not cosmetic and not optional: without it, removing <c>y</c> matches
    /// the tail of <c>font-famil|y="Inter"</c> and eats the font off the run. Caught by the
    /// empty-list test, which is the only caller that removes anything.
    /// </remarks>
    private static string RemoveAttr(string attrs, string name)
    {
        var rx = new Regex($@"(?<=^|\s)\s*{Regex.Escape(name)}\s*=\s*""[^""]*""");
        return rx.Replace(attrs, "", 1);
    }

}
