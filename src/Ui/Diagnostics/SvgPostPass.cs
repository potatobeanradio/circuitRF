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
    public static string Run(string svg, out Report report)
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

    internal static string RoundAll(string s) => NumberRx.Replace(s, m =>
    {
        if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return m.Value;
        double r = Math.Round(v, Decimals, MidpointRounding.AwayFromZero);
        return r.ToString("0.##", CultureInfo.InvariantCulture);
    });

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
}
