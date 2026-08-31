using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// Builds the client-side search index the documentation's search boxes query.
///
/// <para><b>It is emitted as a SCRIPT, not as JSON.</b> The docs are read three ways — over the
/// loopback server <c>DocLauncher</c> starts, from a web host, and by opening a page straight off
/// disk — and only a classic <c>&lt;script src&gt;</c> works in all three. A <c>fetch()</c> of a
/// sibling <c>.json</c> is blocked by every browser's <c>file://</c> origin rules, so a search built
/// that way would work everywhere except the offline case the stylesheet's own header promises.</para>
///
/// <para><b>A section, not a page, is the unit.</b> The Reference Guide's pages are long; "which
/// page mentions push-in" is a much worse answer than "The Schematic Editor › Hierarchy". Each
/// <c>h2</c>/<c>h3</c> with an id becomes one record carrying its own text, so a result can deep-link
/// to the anchor the reader actually wants.</para>
///
/// <para>The extraction runs over the RENDERED body rather than the Markdown, which is what makes
/// generated content searchable: a component's parameter table, a toolbar's per-button table and a
/// figure caption are all produced by a placeholder and exist nowhere in the source page.</para>
/// </summary>
public sealed class SearchIndex
{
    /// <summary>Section text is truncated here. See <see cref="Truncate"/> for why there is a cap at all.</summary>
    private const int MaxSectionChars = 4000;

    private sealed record Entry(int Rank, string Slug, string Title, string DocKind, string Lede,
                                List<(string Anchor, string Heading, string Text)> Sections);

    private readonly List<Entry> _entries = [];

    /// <summary>Sections indexed so far — reported, because this file's size is worth watching.</summary>
    public int SectionCount => _entries.Sum(e => e.Sections.Count);

    /// <summary>
    /// Index one page. <paramref name="bodyHtml"/> is the rendered body — placeholders expanded,
    /// Markdown converted — exactly as <see cref="HtmlEmitter"/> writes it into the page.
    ///
    /// <para><paramref name="rank"/> is the page's position in the reading order
    /// (<c>src/_nav.txt</c>), and it is what breaks a scoring TIE at query time. Two sections can
    /// legitimately score the same — "Hierarchy" heads a section in both editors — and the order the
    /// documentation itself puts them in is a real answer where the order the file system happened to
    /// enumerate them in is not. It puts the Getting-started guides above the Reference, and the
    /// schematic above the layout, which is the order the pages are meant to be read in.</para>
    /// </summary>
    public void Add(DocPage page, string bodyHtml, int rank)
    {
        var sections = new List<(string, string, string)>();

        foreach (var (anchor, heading, body) in Sections(Strippable(bodyHtml)))
        {
            // The lead section carries the lede too: it is the page's own one-line summary and the
            // best thing a query for the page's subject can match against.
            string full = anchor.Length == 0 && page.Lede.Length > 0 ? page.Lede + " " + body : body;
            if (heading.Length == 0 && full.Length == 0) continue;
            sections.Add((anchor, heading, Truncate(full)));
        }

        _entries.Add(new Entry(rank, page.Slug, page.Title, page.DocKind, page.Lede, sections));
    }

    /// <summary>The generated <c>assets/js/search-index.js</c>.</summary>
    public string ToJs()
    {
        // Reading order, with the slug as the tie-break so a page the nav does not rank (there
        // should be none — SiteNav.Validate refuses one) still lands somewhere deterministic.
        var ordered = _entries.OrderBy(e => e.Rank).ThenBy(e => e.Slug, StringComparer.Ordinal).ToList();

        var pages = new List<string[]>();
        var sections = new List<object[]>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            pages.Add([e.Slug, e.Title, e.DocKind, e.Lede]);
            foreach (var (anchor, heading, text) in e.Sections)
                sections.Add([i, anchor, heading, text]);
        }

        var payload = new Dictionary<string, object>
        {
            ["v"] = 1,
            ["p"] = pages,
            ["s"] = sections,
        };

        // Not indented: this file is read by a machine, and pretty-printing it would add a third of
        // its bytes to every clone for nobody's benefit. Unicode is not escaped, so "Ω" stays one
        // character and the file stays diffable.
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        return "// GENERATED FILE - do not edit. Rebuilt by tools/DocGen from docs/user/src/**.md.\n"
             + "// The search UI that reads it is the hand-written assets/js/docs-search.js.\n"
             + "window.CRF_DOCS_SEARCH = " + json + ";\n";
    }

    // ── Extraction ────────────────────────────────────────────────────────────

    /// <summary>
    /// The body with everything that is not this page's own prose removed:
    ///
    /// <list type="bullet">
    ///   <item>the inlined figures — hundreds of kilobytes of path data each, and not one readable
    ///         word;</item>
    ///   <item>the on-page contents card, which only repeats the headings this index already carries
    ///         and would make every page match its own section names twice;</item>
    ///   <item><b>the generated site contents</b> (<c>{{toc: site}}</c>), which is every other page's
    ///         one-line blurb. Left in, the two contents pages match almost any query — a search for
    ///         "bondwire" answered with the table of contents instead of the wBond page — and the
    ///         reading-order prior, which ranks the landing page first, makes that worse rather than
    ///         better.</item>
    /// </list>
    /// </summary>
    private static string Strippable(string html)
    {
        html = Regex.Replace(html, "<!--.*?-->", " ", RegexOptions.Singleline);
        html = StripElement(html, "svg");
        html = StripElement(html, "script");
        html = StripElement(html, "style");
        html = StripElement(html, "nav");
        html = StripElement(html, "div", @"<div\s[^>]*class=""site-toc""[^>]*>");
        return html;
    }

    /// <summary>
    /// Remove every <c>&lt;tag&gt;…&lt;/tag&gt;</c> region, counting nesting. Pass
    /// <paramref name="startPattern"/> to remove only the regions that begin with a particular
    /// opening tag — a <c>div</c> of one class, say — while still counting every <c>div</c> inside
    /// it towards the depth.
    ///
    /// <para>A regex alone cannot do this: an inlined figure is an <c>&lt;svg&gt;</c> that may
    /// contain another, and a non-greedy match would stop at the inner close tag and leave the outer
    /// half of a figure — tens of thousands of path coordinates — in the index as "text".</para>
    /// </summary>
    private static string StripElement(string html, string tag, string? startPattern = null)
    {
        var open  = new Regex("<" + tag + @"(\s[^>]*)?>", RegexOptions.IgnoreCase);
        var close = new Regex("</" + tag + @"\s*>", RegexOptions.IgnoreCase);
        var begin = startPattern is null ? open : new Regex(startPattern, RegexOptions.IgnoreCase);

        var sb = new StringBuilder(html.Length);
        int pos = 0;
        while (true)
        {
            var start = begin.Match(html, pos);
            if (!start.Success) { sb.Append(html, pos, html.Length - pos); break; }

            sb.Append(html, pos, start.Index - pos).Append(' ');

            int depth = 1, scan = start.Index + start.Length;
            while (depth > 0)
            {
                var next  = open.Match(html, scan);
                var shut  = close.Match(html, scan);
                if (!shut.Success) { scan = html.Length; break; }   // unbalanced: drop the rest
                if (next.Success && next.Index < shut.Index) { depth++; scan = next.Index + next.Length; }
                else { depth--; scan = shut.Index + shut.Length; }
            }
            pos = scan;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Split a stripped body into (anchor, heading, text) at every <c>h2</c>/<c>h3</c> that carries
    /// an id. Text before the first heading becomes the lead section, whose anchor is the empty
    /// string — a link to the page itself.
    /// </summary>
    private static IEnumerable<(string Anchor, string Heading, string Text)> Sections(string html)
    {
        var heading = new Regex(@"<h(?<lvl>[23])\b[^>]*\bid=""(?<id>[^""]*)""[^>]*>(?<t>.*?)</h\k<lvl>>",
                                RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var hits = heading.Matches(html).Cast<Match>().ToList();

        string lead = Plain(hits.Count > 0 ? html[..hits[0].Index] : html);
        if (lead.Length > 0) yield return ("", "", lead);

        for (int i = 0; i < hits.Count; i++)
        {
            int from = hits[i].Index + hits[i].Length;
            int to   = i + 1 < hits.Count ? hits[i + 1].Index : html.Length;
            yield return (hits[i].Groups["id"].Value,
                          Plain(hits[i].Groups["t"].Value),
                          Plain(html[from..to]));
        }
    }

    /// <summary>Tags out, entities decoded, whitespace collapsed — the text a reader would see.</summary>
    private static string Plain(string html)
    {
        string s = Regex.Replace(html, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    /// <summary>
    /// Cap a section's indexed text.
    ///
    /// <para>The cap exists because this file is committed and downloaded by every reader, not
    /// because the tail of a long section is worthless. It is set well above the length of an
    /// ordinary section, so it bites only on the handful of very long ones — where a query that
    /// matches nothing but the last paragraph is already better served by the browser's own
    /// find-on-page once the reader is there.</para>
    /// </summary>
    private static string Truncate(string s)
    {
        if (s.Length <= MaxSectionChars) return s;
        int cut = s.LastIndexOf(' ', MaxSectionChars);
        return s[..(cut > MaxSectionChars / 2 ? cut : MaxSectionChars)];
    }
}
