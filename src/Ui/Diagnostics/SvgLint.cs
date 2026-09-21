using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// THE DROPPED-PAINT LINT — a blocking check over every SVG the docs factory emits.
///
/// <para>Skia's SVG device omits <c>fill</c> when the paint colour is pure black, and drops
/// <c>fill-opacity</c> along with it (see <see cref="DocsPaintRemap"/> for the measurement). The
/// result is a shape with NO paint attributes, which a browser renders as opaque black — a figure
/// that is visibly wrong but throws nothing. This is precisely the class of defect that gets noticed
/// six months later in a picture nobody re-read, so generation fails on it.</para>
///
/// <para>Deliberately framework-free (string and regex only, no Avalonia, no Skia) so the test
/// suite can run it over the committed figures without a UI platform.</para>
/// </summary>
public static class SvgLint
{
    /// <summary>Elements that actually put ink on the page.</summary>
    private static readonly string[] DrawingElements =
        ["path", "rect", "ellipse", "circle", "line", "polygon", "polyline", "text", "image", "use"];

    /// <summary>Shapes the lint inspects for a dropped paint. <c>text</c> defaults to black legitimately.</summary>
    private static readonly string[] PaintedShapes = ["path", "rect", "ellipse", "circle", "polygon"];

    /// <summary>
    /// Containers whose children describe GEOMETRY, not ink — a <c>&lt;rect&gt;</c> inside a
    /// <c>&lt;clipPath&gt;</c> has no paint because it is a clip, and flagging it is a false
    /// positive. Skia emits one clip per Avalonia control, so without this exclusion the lint is
    /// nothing but false positives.
    /// </summary>
    private static readonly string[] GeometryOnly = ["clipPath", "mask", "defs", "pattern", "marker"];

    private static readonly Regex TagRx =
        new(@"<(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*?)(?<selfclose>/?)>",
            RegexOptions.Compiled);

    /// <summary>True when <paramref name="svg"/> contains at least one element that draws something.</summary>
    public static bool HasDrawingElements(string svg)
    {
        foreach (Match m in TagRx.Matches(svg))
            if (m.Groups["close"].Length == 0 && DrawingElements.Contains(m.Groups["name"].Value))
                return true;
        return false;
    }

    /// <summary>One suspected dropped paint.</summary>
    /// <param name="Element">The element name, e.g. <c>rect</c>.</param>
    /// <param name="Line">1-based line number in the file.</param>
    /// <param name="Snippet">The offending tag, trimmed for the error message.</param>
    public readonly record struct Finding(string Element, int Line, string Snippet)
    {
        public override string ToString() => $"line {Line}: <{Element}> {Snippet}";
    }

    /// <summary>
    /// Every <c>path</c>/<c>rect</c>/<c>ellipse</c>/<c>circle</c>/<c>polygon</c> that carries neither
    /// a <c>fill</c> nor a <c>stroke</c> — inherited or not. A shape inside a group that supplies the
    /// paint is NOT a finding, so the enclosing <c>&lt;g&gt;</c>'s attributes count too.
    /// </summary>
    public static IReadOnlyList<Finding> DroppedPaint(string svg)
    {
        var findings = new List<Finding>();
        var groupStack = new Stack<bool>();      // true = this group supplies a fill or a stroke
        groupStack.Push(false);
        int geometryDepth = 0;                   // inside clipPath/mask/defs: shapes are not ink

        foreach (Match m in TagRx.Matches(svg))
        {
            string name  = m.Groups["name"].Value;
            string attrs = m.Groups["attrs"].Value;
            bool closing     = m.Groups["close"].Length      != 0;
            bool selfClosing = m.Groups["selfclose"].Length  != 0;

            if (GeometryOnly.Contains(name))
            {
                if (closing) { if (geometryDepth > 0) geometryDepth--; }
                else if (!selfClosing) geometryDepth++;
                continue;
            }

            if (name == "g")
            {
                if (closing) { if (groupStack.Count > 1) groupStack.Pop(); }
                else if (!selfClosing) groupStack.Push(groupStack.Peek() || Paints(attrs));
                continue;
            }

            if (closing || geometryDepth > 0 || !PaintedShapes.Contains(name)) continue;

            if (groupStack.Peek() || Paints(attrs)) continue;

            findings.Add(new Finding(name, LineOf(svg, m.Index), Trim(m.Value)));
        }

        return findings;
    }

    /// <summary>True when the attribute text supplies a visible fill or stroke.</summary>
    private static bool Paints(string attrs)
    {
        foreach (var key in (string[])["fill", "stroke"])
        {
            var v = Attr(attrs, key);
            if (v is null) continue;
            if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }
        // A style="" attribute can carry the same thing.
        var style = Attr(attrs, "style");
        return style is not null
            && (style.Contains("fill:", StringComparison.OrdinalIgnoreCase)
             || style.Contains("stroke:", StringComparison.OrdinalIgnoreCase));
    }

    internal static string? Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"(?:^|\s){Regex.Escape(name)}\s*=\s*""(?<v>[^""]*)""");
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static int LineOf(string s, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < s.Length; i++)
            if (s[i] == '\n') line++;
        return line;
    }

    private static string Trim(string tag) => tag.Length <= 160 ? tag : tag[..157] + "...";

    // ── The measurement lint ──────────────────────────────────────────────────

    /// <summary>
    /// Text a figure must never carry: a number that measures THIS MACHINE ON THIS RUN.
    ///
    /// <para>A solve count, a frame rate or an elapsed time is not a property of the thing being
    /// documented — it is a property of the box the generator happened to run on. Committed, it
    /// makes the figure differ on every regeneration with nothing to show for it: the harmonicaRF
    /// instrument read <c>40 HB solves</c> on one run and <c>813 HB solves</c> on the next, and the
    /// railRF status strip <c>414.9 ms</c> then <c>400.3 ms</c>. Eight figures and two pages churned
    /// on every run for months because the only symptom is a diff nobody can attribute.</para>
    ///
    /// <para><b>Measured against the whole committed set before it was turned on</b>: these four
    /// patterns match the eight offending figures and NOTHING else in 766 files, so there is no
    /// allow-list and no false positive to argue about. A unit that is a SETTING rather than a
    /// measurement (a rise time, a sweep step) is spelt in the document's own units and does not
    /// reach these; if one ever does, fix the pattern rather than muting the lint.</para>
    /// </summary>
    private static readonly (string What, Regex Rx)[] MeasurementPatterns =
    [
        ("a solve count", new Regex(@"\d+\s*(?:HB\s+)?solves\b", RegexOptions.Compiled)),
        ("a frame rate",  new Regex(@"\d+(?:\.\d+)?\s*fps\b",     RegexOptions.Compiled)),
        ("an elapsed time", new Regex(@"\b\d+(?:\.\d+)?\s*ms\b", RegexOptions.Compiled)),
        ("an elapsed time", new Regex(@"\b\d+(?:\.\d+)?\s*[\u00B5u]s\b", RegexOptions.Compiled)),
    ];

    private static readonly Regex TextNodeRx =
        new(@"<text\b[^>]*>(?<body>.*?)</text>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Every run of text in <paramref name="svg"/> that states a measurement of the machine.
    /// <see cref="Finding.Element"/> carries what kind it is; the snippet is the matched text.
    /// </summary>
    public static IReadOnlyList<Finding> Measurements(string svg)
    {
        var found = new List<Finding>();
        foreach (Match t in TextNodeRx.Matches(svg))
        {
            // Skia splits one run across lines and escapes it; join and unescape before matching,
            // or "400.3 ms" broken over two lines reads as two harmless fragments.
            string body = Regex.Replace(t.Groups["body"].Value, @"\s+", " ").Trim();
            body = body.Replace("&#181;", "\u00B5").Replace("&amp;", "&");
            foreach (var (what, rx) in MeasurementPatterns)
                foreach (Match m in rx.Matches(body))
                    found.Add(new Finding(what, LineOf(svg, t.Index), Trim(m.Value)));
        }
        return found;
    }

    /// <summary>The blocking-failure message for <see cref="Measurements"/>.</summary>
    public static string ExplainMeasurements(string file, IReadOnlyList<Finding> findings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{file}: {findings.Count} MEASUREMENT(s) drawn into a committed figure.");
        sb.AppendLine("A solve count, a frame rate or an elapsed time measures the machine this run");
        sb.AppendLine("happened on, so the figure differs on every regeneration and its diff can");
        sb.AppendLine("never be attributed. Fix: suppress the number while");
        sb.AppendLine("UiArtworkGenerator.HeadlessCapture is set, the way the railRF status strip and");
        sb.AppendLine("the harmonicaRF message line do. Text:");
        foreach (var f in findings) sb.AppendLine($"  line {f.Line}: {f.Element} — \"{f.Snippet}\"");
        return sb.ToString();
    }

    /// <summary>The blocking-failure message: name the file, and name every element.</summary>
    public static string Explain(string file, IReadOnlyList<Finding> findings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{file}: {findings.Count} suspected DROPPED PAINT(s).");
        sb.AppendLine("Skia's SVG device omits `fill` when the colour is pure black and drops");
        sb.AppendLine("`fill-opacity` with it, so these shapes will render as OPAQUE BLACK slabs.");
        sb.AppendLine("Fix: add the offending theme brush to the docs paint remap (DocsPaintRemap),");
        sb.AppendLine("or give the fixture a non-black brush. Elements:");
        foreach (var f in findings) sb.AppendLine("  " + f);
        return sb.ToString();
    }
}
