using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Shrinks an emitted figure without changing what it draws.
///
/// <para><b>Why this is not optional.</b> <c>src/Ui/CircuitRF.Ui.csproj</c> copies
/// <c>docs/user/**</c> into the application output, so <b>doc size is app size</b>. Skia's SVG
/// device is verbose in three specific ways, all measured on a real capture: it emits one
/// <c>clipPath</c> per Avalonia control (a 320x200 four-control panel produced thirteen, most of
/// them the full canvas), it writes full double precision (<c>8.30361</c> where <c>8.3</c> is a
/// tenth of a pixel), and it repeats identical path data verbatim (three identical window-frame
/// dots, three copies of the geometry).</para>
///
/// <para>Three passes, in order, none of which touches geometry it cannot prove is redundant:</para>
/// <list type="number">
///   <item>drop a clip that is a no-op, i.e. one whose rectangle already contains the clip in
///         force — and unwrap the <c>&lt;g&gt;</c> that existed only to carry it;</item>
///   <item>round every coordinate to two decimal places;</item>
///   <item>hoist repeated path data into <c>&lt;defs&gt;</c> and reference it with <c>&lt;use&gt;</c>.</item>
/// </list>
///
/// <para>Two further passes are not about size at all, and are here for the same reason the font
/// normaliser is — this is the one place every emitted figure goes through:</para>
/// <list type="number">
///   <item><b>Scope every element id to the file</b> (<see cref="ScopeIds"/>). The docs pages INLINE
///         several figures into one HTML document, where ids share a single namespace; a
///         <c>&lt;use href="#d0"&gt;</c> then resolves to whichever figure came first on the page.</item>
///   <item><b>Add a <c>viewBox</c></b>. Skia writes <c>width</c>/<c>height</c> and nothing else, and
///         an inline <c>&lt;svg&gt;</c> with no <c>viewBox</c> does not scale: the CSS box shrinks to
///         the column and the drawing is CLIPPED rather than resized.</item>
/// </list>
///
/// <para>It also runs <see cref="SvgFontNormalizer"/> first. That is not a size pass and does not
/// pretend to be — it is here because this is the one place every emitted figure passes through,
/// whether it came from the symbol generator or from a window capture.</para>
///
/// <para>Framework-free (System.Xml.Linq only) so the test suite can run it without a UI platform.</para>
/// </summary>
public static class SvgPostPass
{
    private static readonly XNamespace Svg   = "http://www.w3.org/2000/svg";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    /// <summary>Decimal places kept. Two is a hundredth of a pixel — far below anything visible.</summary>
    public const int Decimals = 2;

    /// <summary>What one run of the post-pass did, for the generator's size report.</summary>
    public readonly record struct Report(
        int BytesBefore, int BytesAfter, int ClipsDropped, int PathsDeduped,
        IReadOnlyList<SvgFontNormalizer.Substitution> FontSubstitutions)
    {
        public double Ratio => BytesAfter == 0 ? 0 : (double)BytesBefore / BytesAfter;
    }

    /// <summary>Attributes whose value is a plain number or a list of numbers.</summary>
    private static readonly string[] NumericAttrs =
    [
        "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
        "width", "height", "stroke-width", "stroke-opacity", "fill-opacity",
        "opacity", "font-size", "stroke-miterlimit", "stroke-dashoffset", "points",
    ];

    /// <summary>Run every pass over <paramref name="svg"/> and return the rewritten document.</summary>
    /// <param name="svg">The document Skia's SVG device just wrote.</param>
    /// <param name="idScope">
    /// A token unique to this FILE, prefixed onto every element id. Pass the emitted file's stem.
    /// Empty means "do not scope", which is only ever right for a document that will never be
    /// inlined beside another — see <see cref="ScopeIds"/> for why that is nearly never true here.
    /// </param>
    /// <param name="report">What the size passes did, for the generator's run summary.</param>
    public static string Run(string svg, string idScope, out Report report)
    {
        int before = Encoding.UTF8.GetByteCount(svg);

        // Font attributes first, on the raw text: Skia's family list and weight do not survive a
        // browser as written (see SvgFontNormalizer), and correcting them is not a size optimisation
        // — it is the difference between a caption rendering in the weight it was drawn in and not.
        svg = SvgFontNormalizer.Normalize(svg, out var substitutions);

        var doc = XDocument.Parse(svg);
        var root = doc.Root ?? throw new InvalidOperationException("SVG has no root element.");

        int clipsDropped = DropNoOpClips(root);
        RoundNumbers(root);
        int deduped = DedupePaths(root);
        ScopeIds(root, idScope);
        AddViewBox(root);

        // Writing without an XML declaration and with no indentation is worth another few percent
        // and costs nothing: an SVG served inline in HTML has no use for either.
        var sb = new StringBuilder();
        using (var w = System.Xml.XmlWriter.Create(sb, new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = System.Xml.NewLineHandling.None,
        }))
            doc.Save(w);

        string outSvg = sb.ToString();
        report = new Report(before, Encoding.UTF8.GetByteCount(outSvg), clipsDropped, deduped, substitutions);
        return outSvg;
    }

    // ── Pass 1: no-op clips ───────────────────────────────────────────────────

    private static int DropNoOpClips(XElement root)
    {
        var clipRects = new Dictionary<string, (double X, double Y, double W, double H)>();
        foreach (var cp in root.Descendants(Svg + "clipPath").ToList())
        {
            string? id = (string?)cp.Attribute("id");
            if (id is null) continue;
            var kids = cp.Elements().ToList();
            if (kids.Count != 1) continue;
            var kid = kids[0];
            if (kid.Name != Svg + "rect") continue;
            if (kid.Attribute("rx") is not null || kid.Attribute("ry") is not null) continue;
            if (kid.Attribute("transform") is not null) continue;
            clipRects[id] = (Num(kid, "x"), Num(kid, "y"), Num(kid, "width"), Num(kid, "height"));
        }

        int dropped = 0;
        var viewport = (X: 0.0, Y: 0.0, W: Num(root, "width"), H: Num(root, "height"));
        Visit(root, viewport, clean: true);

        void Visit(XElement el, (double X, double Y, double W, double H)? active, bool clean)
        {
            foreach (var child in el.Elements().ToList())
            {
                if (child.Name != Svg + "g") { continue; }

                var childClean = clean && child.Attribute("transform") is null;
                var next = active;

                var attr = child.Attribute("clip-path");
                string? id = attr is null ? null : ClipId(attr.Value);

                if (id is not null && clipRects.TryGetValue(id, out var rect))
                {
                    if (childClean && active is { } a && Contains(rect, a))
                    {
                        attr!.Remove();
                        dropped++;
                    }
                    else
                    {
                        next = childClean && active is { } b ? Intersect(rect, b) : rect;
                    }
                }
                else if (attr is not null)
                {
                    next = null;    // an unknown / complex clip: nothing below can be proven a no-op
                }

                Visit(child, next, childClean);

                // A <g> that carried nothing but the clip we just dropped is pure overhead.
                if (!child.HasAttributes && child.Name == Svg + "g")
                {
                    var parent = child.Parent!;
                    child.ReplaceWith(child.Nodes().ToList());
                    _ = parent;
                }
            }
        }

        // Any clipPath definition nobody references any more.
        var referenced = root.Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => ClipId(a.Value))
            .Where(s => s is not null)
            .ToHashSet()!;
        foreach (var cp in root.Descendants(Svg + "clipPath").ToList())
            if ((string?)cp.Attribute("id") is { } id && !referenced.Contains(id))
                cp.Remove();

        return dropped;
    }

    private static string? ClipId(string value)
    {
        var m = Regex.Match(value, @"^url\(#(?<id>[^)]+)\)$");
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static bool Contains((double X, double Y, double W, double H) outer,
                                 (double X, double Y, double W, double H) inner)
        => outer.X <= inner.X + 1e-6 && outer.Y <= inner.Y + 1e-6
        && outer.X + outer.W >= inner.X + inner.W - 1e-6
        && outer.Y + outer.H >= inner.Y + inner.H - 1e-6;

    private static (double X, double Y, double W, double H) Intersect(
        (double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b)
    {
        double x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        double x1 = Math.Min(a.X + a.W, b.X + b.W), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
        return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static double Num(XElement el, string name)
        => double.TryParse((string?)el.Attribute(name), NumberStyles.Float,
                           CultureInfo.InvariantCulture, out var v) ? v : 0.0;

    // ── Pass 2: coordinate precision ──────────────────────────────────────────

    private static readonly Regex NumberRx =
        new(@"-?\d+\.\d+(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    private static void RoundNumbers(XElement root)
    {
        foreach (var el in root.DescendantsAndSelf())
        {
            foreach (var a in el.Attributes().ToList())
            {
                string n = a.Name.LocalName;
                if (n is "d" or "transform" || NumericAttrs.Contains(n))
                    a.Value = RoundAll(a.Value);
            }
        }
    }

    /// <summary>
    /// Significant figures kept for a magnitude BELOW one. Two decimal places is a hundredth of a
    /// pixel on a coordinate and a 67% error on a matrix scale, which is not a rounding subtlety —
    /// it is a differently-shaped drawing.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not theorised.</b> Every ComboBox in the docs showed HALF a drop-down chevron:
    /// the left stroke drawn, the right one simply absent. The chevron is a 2010-unit-wide geometry
    /// scaled into a 12 px icon box — <c>matrix(0.00597 0 0 0.00597 …)</c> — and rounding that scale
    /// to two decimals made it <c>0.01</c>, blowing the glyph up by 67% inside a clip that was still
    /// 12 px wide. The clip did the rest. Anything whose value lives below one is cheap to keep
    /// precise (few digits either way) and expensive to round, so it is kept to four significant
    /// figures instead of two decimal places.
    /// </remarks>
    public const int SignificantFiguresBelowOne = 4;

    internal static string RoundAll(string s) => NumberRx.Replace(s, m =>
    {
        if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return m.Value;
        if (Math.Abs(v) >= 1)
            return Math.Round(v, Decimals, MidpointRounding.AwayFromZero)
                       .ToString("0.##", CultureInfo.InvariantCulture);
        return RoundSignificant(v, SignificantFiguresBelowOne)
                   .ToString("0.##########", CultureInfo.InvariantCulture);
    });

    /// <summary>Round <paramref name="v"/> to <paramref name="figures"/> significant figures.</summary>
    internal static double RoundSignificant(double v, int figures)
    {
        if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
        int magnitude = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        int decimals = Math.Min(15, Math.Max(0, figures - 1 - magnitude));
        return Math.Round(v, decimals, MidpointRounding.AwayFromZero);
    }

    // ── Pass 3: repeated path data ────────────────────────────────────────────

    private static int DedupePaths(XElement root)
    {
        var byData = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
        foreach (var p in root.Descendants(Svg + "path"))
        {
            // A path inside a clipPath is geometry, not ink; leave it alone.
            if (p.Ancestors(Svg + "clipPath").Any()) continue;
            if ((string?)p.Attribute("d") is not { Length: > 0 } d) continue;
            if (!byData.TryGetValue(d, out var list)) byData[d] = list = [];
            list.Add(p);
        }

        var repeated = byData.Where(kv => kv.Value.Count > 1).ToList();
        if (repeated.Count == 0) return 0;

        var defs = new XElement(Svg + "defs");
        int n = 0, saved = 0;
        foreach (var (d, uses) in repeated)
        {
            string id = "d" + n++;
            defs.Add(new XElement(Svg + "path", new XAttribute("id", id), new XAttribute("d", d)));
            foreach (var p in uses)
            {
                var use = new XElement(Svg + "use", new XAttribute(Xlink + "href", "#" + id));
                foreach (var a in p.Attributes())
                    if (a.Name.LocalName != "d") use.Add(new XAttribute(a.Name, a.Value));
                p.ReplaceWith(use);
                saved++;
            }
        }
        root.AddFirst(defs);
        return saved;
    }

    // ── Pass 4: id scoping ────────────────────────────────────────────────────

    /// <summary>
    /// Prefix every element id with <paramref name="scope"/>, and rewrite every reference to it.
    ///
    /// <para><b>Why a figure cannot keep its own id namespace.</b> The documentation pages inline
    /// their figures as <c>&lt;svg&gt;</c> rather than referencing them with <c>&lt;img&gt;</c> (so
    /// the page's <c>@font-face</c> rules reach them), and inline SVG shares the HTML document's
    /// single id namespace. <see cref="DedupePaths"/> numbers its hoisted paths <c>d0, d1, …</c> from
    /// zero in every file and Skia numbers its own <c>img_0</c>, <c>img_1</c> the same way, so two
    /// figures on one page BOTH define <c>d0</c> — and every <c>&lt;use href="#d0"&gt;</c> in the
    /// second one silently resolves to the first one's geometry.</para>
    ///
    /// <para><b>Every symptom of this looked like a different bug.</b> The Data Display toolbar
    /// "glitched out"; the snap-glyph strip drew its glyphs over corrupted metal while being perfect
    /// as a standalone file; and on the harmonicaRF page the DARK figure rendered the LIGHT
    /// contour raster, because both variants define <c>img_0</c> and the light one is first in the
    /// document. One cause, four reports.</para>
    ///
    /// <para>The scope is the file stem rather than a hash: it is provably unique across the run
    /// (two figures cannot share a file), and it is readable in the emitted document, which a
    /// six-character hash is not.</para>
    /// </summary>
    private static void ScopeIds(XElement root, string scope)
    {
        if (scope.Length == 0) return;

        string prefix = Regex.Replace(scope, @"[^A-Za-z0-9_-]", "-") + "_";

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var el in root.DescendantsAndSelf())
            if (el.Attribute("id") is { } id && !id.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                string canonical = Canonicalise(id.Value, counters);
                renamed[id.Value] = prefix + canonical;
                id.Value = prefix + canonical;
            }

        if (renamed.Count == 0) return;

        foreach (var el in root.DescendantsAndSelf())
            foreach (var a in el.Attributes())
            {
                if (a.Name.LocalName == "id") continue;

                // The two spellings a reference takes: a bare fragment (href / xlink:href) and a
                // functional one (clip-path, mask, fill, stroke, filter, marker-*).
                if (a.Value.StartsWith('#') && renamed.TryGetValue(a.Value[1..], out var direct))
                    a.Value = "#" + direct;
                else if (a.Value.Contains("url(#", StringComparison.Ordinal))
                    a.Value = Regex.Replace(a.Value, @"url\(#(?<id>[^)]+)\)",
                        m => renamed.TryGetValue(m.Groups["id"].Value, out var mapped)
                             ? $"url(#{mapped})" : m.Value);
            }
    }

    /// <summary>
    /// <b>Skia's own generated ids are a PROCESS-WIDE counter, and left alone they make every figure
    /// churn whenever any other figure changes.</b>
    ///
    /// <para>Its SVG device names each generated element <c>&lt;kind&gt;_&lt;hex&gt;</c> —
    /// <c>cl_1965</c> for a clip — where the hex is a counter shared by everything the process has
    /// emitted so far. One DocGen run writes every figure, so ADDING ONE FIGURE shifts that counter
    /// for every figure rendered after it: 277 files came back modified, with nothing in any of them
    /// different but the digits in their clip ids. That is a diff nobody can review and a repository
    /// that grows by a megabyte per docs edit (owner report, 2026-08-25).</para>
    ///
    /// <para>So an id of that shape is renumbered <b>per file, in document order</b>: the first clip
    /// is <c>cl_0</c> whatever the process did before it. Ids of any other shape are left exactly as
    /// they are — in particular the <c>d0</c>, <c>d1</c>… this file's own dedupe pass mints, which
    /// are already deterministic and are referenced by the <c>&lt;use&gt;</c> elements it wrote.</para>
    ///
    /// <para>Renaming is safe because it happens BEFORE the reference rewrite below and through the
    /// same map, so <c>url(#…)</c> and <c>href="#…"</c> follow.</para>
    /// </summary>
    private static string Canonicalise(string id, Dictionary<string, int> counters)
    {
        var m = GeneratedId.Match(id);
        if (!m.Success) return id;

        string kind = m.Groups["kind"].Value;
        counters.TryGetValue(kind, out int n);
        counters[kind] = n + 1;
        return $"{kind}_{n}";
    }

    /// <summary>Skia's <c>&lt;kind&gt;_&lt;hex counter&gt;</c> shape, and nothing else.</summary>
    private static readonly Regex GeneratedId =
        new(@"^(?<kind>[A-Za-z]+)_[0-9a-fA-F]+$", RegexOptions.Compiled);

    // ── Pass 5: the viewBox ───────────────────────────────────────────────────

    /// <summary>
    /// Give the root a <c>viewBox</c> matching the size Skia wrote, so the figure SCALES.
    ///
    /// <para>Skia emits <c>width</c> and <c>height</c> and nothing else. An inline
    /// <c>&lt;svg&gt;</c> with no <c>viewBox</c> has no intrinsic aspect ratio to preserve and no
    /// user-coordinate mapping to rescale, so the stylesheet's <c>max-width: 100%</c> narrows the
    /// element's BOX and the drawing inside it is clipped at full size rather than resized. The
    /// symbol figures never showed it because they are smaller than the column they sit in.</para>
    ///
    /// <para><c>width</c>/<c>height</c> stay: they are the figure's natural size, which is what a
    /// page with room for it should use.</para>
    /// </summary>
    private static void AddViewBox(XElement root)
    {
        if (root.Attribute("viewBox") is not null) return;
        double w = Num(root, "width"), h = Num(root, "height");
        if (w <= 0 || h <= 0) return;
        root.SetAttributeValue("viewBox",
            string.Create(CultureInfo.InvariantCulture, $"0 0 {w:0.##} {h:0.##}"));
    }
}
