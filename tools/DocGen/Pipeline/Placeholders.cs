using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// Expands the typed placeholders a source page writes into the generated HTML.
///
/// <list type="table">
///   <item><term><c>{{ui: em-setup-editor}}</c></term><description>the light/dark figure pair, inline, framed, captioned</description></item>
///   <item><term><c>{{symbol: resistor}}</c></term><description>the generated component-symbol figure</description></item>
///   <item><term><c>{{toolbar: layout}}</c></term><description>the toolbar figure AND its generated per-button table</description></item>
///   <item><term><c>{{snapglyph: pin}}</c></term><description>one geometry-snap glyph, inline, for a table cell</description></item>
///   <item><term><c>{{table: components/Resistor}}</c></term><description>a parameter table read from the live registry</description></item>
///   <item><term><c>{{anchor: components#sdd}}</c></term><description>a checked cross-link; add <c>|Link text</c> to word it</description></item>
///   <item><term><c>{{toc: site}}</c></term><description>the complete table of contents, from the reading order</description></item>
///   <item><term><c>{{regions: workspace}}</c></term><description>the numbered legend of the workspace figure</description></item>
///   <item><term><c>{{search: hero}}</c></term><description>the landing page's full-width search box</description></item>
/// </list>
///
/// <para><b>An unknown placeholder is a generation error, never literal text.</b> A typo'd
/// <c>{{ui: em-setup}}</c> must not reach a shipped page as five visible braces — that is the
/// failure mode this whole pipeline exists to remove.</para>
///
/// <para><b>Figures are INLINED as <c>&lt;svg&gt;</c>, not referenced with <c>&lt;img&gt;</c>.</b>
/// An SVG loaded as an image cannot see the page's <c>@font-face</c> rules, so the figure would fall
/// back to whatever the reader has installed; data-URI fonts inside an SVG-as-image are unreliable
/// in Safari, which is the default browser <c>DocLauncher</c> opens on macOS. The pages are
/// generated anyway, so inlining costs nothing but bytes.</para>
/// </summary>
public sealed class Placeholders
{
    private static readonly Regex Rx = new(@"\{\{\s*(?<kind>[a-z]+)\s*:\s*(?<arg>[^}]+?)\s*\}\}",
                                           RegexOptions.Compiled);

    private readonly string _docsRoot;
    private readonly string _pageDir;
    private readonly Func<string, IReadOnlyList<ToolbarCatalog.Entry>> _toolbarManifest;
    private readonly Func<string, bool> _anchorExists;
    private readonly SiteNav? _nav;
    private readonly IReadOnlyDictionary<string, string>? _titles;
    private readonly string? _pageSlug;

    /// <summary>Families referenced by every figure inlined so far — drives which fonts get shipped.</summary>
    public HashSet<string> FontFamiliesUsed { get; } = new(StringComparer.Ordinal);

    public Placeholders(string docsRoot, string pageDir,
                        Func<string, IReadOnlyList<ToolbarCatalog.Entry>> toolbarManifest,
                        Func<string, bool> anchorExists,
                        SiteNav? nav = null,
                        IReadOnlyDictionary<string, string>? titles = null,
                        string? pageSlug = null)
    {
        _docsRoot = docsRoot;
        _pageDir = pageDir;
        _toolbarManifest = toolbarManifest;
        _anchorExists = anchorExists;
        _nav = nav;
        _titles = titles;
        _pageSlug = pageSlug;
    }

    /// <summary>Expand every placeholder in <paramref name="markdown"/>. Throws on anything unknown.</summary>
    public string Expand(string markdown, string sourcePath) => Rx.Replace(markdown, m =>
    {
        string kind = m.Groups["kind"].Value, arg = m.Groups["arg"].Value.Trim();
        try
        {
            return kind switch
            {
                "ui"      => UiFigure(arg),
                "symbol"  => SymbolFigure(arg),
                "toolbar"   => ToolbarFigure(arg),
                "snapglyph" => SnapGlyph(arg),
                "table"   => Table(arg),
                "anchor"  => Anchor(arg),
                "toc"     => Toc(arg),
                "regions" => Regions(arg),
                "search"  => Search(arg),
                _ => throw new InvalidOperationException(
                        $"unknown placeholder kind '{kind}'. Known kinds: ui, symbol, toolbar, snapglyph, "
                      + "table, anchor, toc, regions, search."),
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{sourcePath}: {m.Value} — {ex.Message}", ex);
        }
    });

    // ── {{ui: id}} ────────────────────────────────────────────────────────────

    private string UiFigure(string id)
    {
        var row = FigureCatalog.Catalog.FirstOrDefault(r => r.Id == id);
        if (row.Id is null)
            throw new InvalidOperationException(
                $"no figure with id '{id}' in FigureCatalog. Ids are a contract between a page and the " +
                $"catalog. Known ids: {string.Join(", ", FigureCatalog.Catalog.Select(r => r.Id))}.");

        return FigurePair(Path.Combine("assets", "figures"), id, row.Caption, "figure");
    }

    // ── {{symbol: stem}} ──────────────────────────────────────────────────────

    private string SymbolFigure(string stem)
    {
        var row = SymbolArtworkGenerator.Catalog.FirstOrDefault(r => r.File == stem);
        if (row.File is null)
            throw new InvalidOperationException(
                $"no symbol figure with stem '{stem}'. Add a row to SymbolArtworkGenerator.Catalog, " +
                "or fix the spelling.");

        string caption = ComponentTypeRegistry.DisplayName(row.Kind, row.Ports);
        return FigurePair(Path.Combine("assets", "symbols"), stem, caption, "symbol");
    }

    // ── {{toolbar: id}} ───────────────────────────────────────────────────────

    private string ToolbarFigure(string id)
    {
        var row = ToolbarCatalog.Catalog.FirstOrDefault(r => r.Id == id);
        if (row.Id is null)
            throw new InvalidOperationException(
                $"no toolbar '{id}'. Known: {string.Join(", ", ToolbarCatalog.Catalog.Select(r => r.Id))}.");

        var figure = FigurePair(Path.Combine("assets", "figures"), "toolbar-" + id + "-indexed",
                                row.Title + " toolbar", "figure");
        return figure + "\n" + DocTables.ToolbarButtons(_toolbarManifest(id), e => ButtonGlyph(id, e));
    }

    /// <summary>
    /// The captured button itself, inlined, for the per-button table's Button column.
    ///
    /// <para>Drawn LARGER than the toolbar draws it — the table has the room, and a 28-pixel icon in
    /// a body-text row is smaller than the surrounding letters (owner, 2026-08-20). The SVG carries a
    /// viewBox, so the width in the stylesheet is all it takes.</para>
    /// </summary>
    private string ButtonGlyph(string toolbarId, ToolbarCatalog.Entry e)
    {
        string stem = $"toolbar-{toolbarId}-btn-{e.Index}";
        if (!File.Exists(Path.Combine(_docsRoot, "assets", "figures", stem + ".svg")))
            return e.Id.Length == 0 ? "&mdash;" : "<code>" + WebUtility.HtmlEncode(e.Id) + "</code>";

        return InlinePair(Path.Combine("assets", "figures"), stem, "toolbar-glyph");
    }

    // ── {{snapglyph: id}} ─────────────────────────────────────────────────────

    private string SnapGlyph(string id)
    {
        if (!InlineGlyphArtwork.SnapGlyphs.Any(g => g.Id == id))
            throw new InvalidOperationException(
                $"no snap glyph '{id}'. Known: "
              + string.Join(", ", InlineGlyphArtwork.SnapGlyphs.Select(g => g.Id)) + ".");

        return InlinePair(Path.Combine("assets", "figures"),
                          InlineGlyphArtwork.SnapGlyphStem + id, "snap-glyph");
    }

    /// <summary>
    /// A light/dark pair inlined with no frame and no caption — for a mark that lives INSIDE running
    /// text or a table cell, where <see cref="FigurePair"/>'s block-level figure markup would break
    /// the row it is in.
    /// </summary>
    private string InlinePair(string relDir, string stem, string cssClass)
    {
        string light = ReadInline(Path.Combine(relDir, stem + ".svg"));
        string dark  = ReadInline(Path.Combine(relDir, stem + "-dark.svg"));
        return $"<span class=\"{cssClass} sym-light\">{light}</span>"
             + $"<span class=\"{cssClass} sym-dark\">{dark}</span>";
    }

    // ── {{table: …}} ──────────────────────────────────────────────────────────

    private string Table(string spec)
    {
        var parts = spec.Split('/', 2);
        return parts switch
        {
            ["components", var name] => ComponentTable(name),
            ["components"]           => DocTables.ComponentIndex(),
            _ => throw new InvalidOperationException(
                    $"unknown table '{spec}'. Supported: components, components/<SymbolKind>."),
        };
    }

    private static string ComponentTable(string kindName)
    {
        if (!Enum.TryParse<SymbolKind>(kindName, ignoreCase: true, out var kind))
            throw new InvalidOperationException($"'{kindName}' is not a SymbolKind.");

        var row = SymbolArtworkGenerator.Catalog.FirstOrDefault(r => r.Kind == kind);
        int ports = row.File is null ? 2 : row.Ports;
        return DocTables.ComponentParameters(kind, ports);
    }

    // ── {{anchor: page#id}} ───────────────────────────────────────────────────

    private string Anchor(string spec)
    {
        // Optional link text after a pipe: "{{anchor: dynamic-symbols#sdd|Dynamic symbols}}". Without
        // it the fragment is used, which reads badly in prose — the point of the placeholder is the
        // CHECK, not a particular wording.
        string? label = null;
        int bar = spec.IndexOf('|');
        if (bar >= 0) { label = spec[(bar + 1)..].Trim(); spec = spec[..bar].Trim(); }

        var parts = spec.Split('#', 2);
        string page = parts[0].EndsWith(".html", StringComparison.Ordinal) ? parts[0] : parts[0] + ".html";
        string frag = parts.Length > 1 ? parts[1] : "";

        // A page is named the way a reader would write it in this page's own directory — the same
        // spelling an ordinary Markdown link uses. The index is keyed from the docs root, so resolve
        // one against the other rather than making the author write the root-relative form twice.
        string rooted = Path.GetRelativePath(_docsRoot, Path.GetFullPath(Path.Combine(_pageDir, page)))
                            .Replace(Path.DirectorySeparatorChar, '/');
        string target = frag.Length == 0 ? rooted : rooted + "#" + frag;

        if (!_anchorExists(target))
            throw new InvalidOperationException(
                $"cross-link target '{target}' does not exist in the generated site. An unresolvable " +
                "link is a generation error here rather than a 404 a reader finds later.");

        return $"<a href=\"{WebUtility.HtmlEncode(Relative(rooted))}"
             + (frag.Length == 0 ? "" : "#" + WebUtility.HtmlEncode(frag)) + "\">"
             + WebUtility.HtmlEncode(label ?? (frag.Length == 0 ? page : frag)) + "</a>";
    }

    // ── {{toc: site}} ─────────────────────────────────────────────────────────

    /// <summary>
    /// The complete table of contents: every section, every page, in the one reading order, with the
    /// blurb the manifest gives it. Generated rather than authored because a hand-written contents
    /// page is the first thing to go stale, and a page missing from it is invisible.
    /// </summary>
    private string Toc(string arg)
    {
        if (_nav is null)
            throw new InvalidOperationException(
                "there is no reading order to build a table of contents from — docs/user/src/_nav.txt "
              + "is missing.");

        IReadOnlyList<SiteNav.Section> chosen;
        if (arg == "site")
        {
            chosen = _nav.Sections;
        }
        else if (arg.StartsWith("section:", StringComparison.Ordinal))
        {
            string want = arg["section:".Length..].Trim();
            chosen = _nav.Sections.Where(s => s.Title == want).ToList();
            if (chosen.Count == 0)
                throw new InvalidOperationException(
                    $"no section titled '{want}' in the reading order. Sections: "
                  + string.Join(", ", _nav.Sections.Select(s => s.Title)) + ".");
        }
        else
        {
            throw new InvalidOperationException(
                $"unknown table of contents '{arg}'. Supported: site, section:<Section title>.");
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<div class=\"site-toc\">");
        foreach (var section in chosen)
        {
            sb.AppendLine($"<h3>{WebUtility.HtmlEncode(section.Title)}</h3>");
            sb.AppendLine("<ul>");
            foreach (var e in section.Entries)
            {
                // Never link a page to itself. reference/index.html IS the Reference Guide contents
                // and is listed in the reading order like every other page, so its own "Core concepts"
                // section opened with a link back to the page the reader is already on (owner,
                // 2026-08-20). It stays in the reading order — Previous/Next still runs through it —
                // it simply does not appear inside its own list.
                if (_pageSlug is not null && e.Slug == _pageSlug) continue;

                string label = _titles is not null && _titles.TryGetValue(e.Slug, out var t)
                    ? t : Path.GetFileNameWithoutExtension(e.Slug);
                sb.Append($"<li><a href=\"{WebUtility.HtmlEncode(Relative(e.Slug))}\">"
                        + $"{WebUtility.HtmlEncode(label)}</a>");
                if (e.Blurb.Length > 0) sb.Append($" <span>{WebUtility.HtmlEncode(e.Blurb)}</span>");
                sb.AppendLine("</li>");
            }
            sb.AppendLine("</ul>");
        }
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    // ── {{regions: workspace}} ────────────────────────────────────────────────

    // ── {{search: hero}} ────────────────────────────────────────────────

    /// <summary>
    /// A prominent search box in the body of a page, for the landing page — where the reader has not
    /// yet chosen a guide and "search everything" is the fastest route in. It is the same control
    /// the header carries, from the same emitter, so the two cannot behave differently.
    /// </summary>
    private string Search(string arg)
    {
        if (arg != "hero")
            throw new InvalidOperationException(
                $"unknown search box '{arg}'. The only one is 'hero' \u2014 the landing page's full-width box. "
              + "Every page already carries the header box automatically.");

        string depth = _pageSlug is null
            ? ""
            : string.Concat(Enumerable.Repeat("../", _pageSlug.Count(c => c == '/')));

        return HtmlEmitter.SearchBox(depth, "search-hero", "Search the documentation",
                                     "Search the documentation\u2026");
    }

    /// <summary>The numbered legend of an indexed figure whose numbers are regions, not buttons.</summary>
    private string Regions(string arg) => arg switch
    {
        "workspace" => DocTables.WorkspaceRegionLegend(),
        _ => throw new InvalidOperationException(
                 $"unknown region legend '{arg}'. Supported: workspace."),
    };

    // ── Shared figure markup ──────────────────────────────────────────────────

    /// <summary>
    /// The light/dark pair, both inlined, one hidden by the stylesheet's <c>prefers-color-scheme</c>
    /// rule — the same <c>.sym-light</c>/<c>.sym-dark</c> convention the hand-written pages already
    /// use, so nothing about the look changes.
    /// </summary>
    private string FigurePair(string relDir, string stem, string caption, string figureClass)
    {
        string light = ReadInline(Path.Combine(relDir, stem + ".svg"));
        string dark  = ReadInline(Path.Combine(relDir, stem + "-dark.svg"));

        return $"""
                <figure class="{figureClass}"><span class="frame">
                <span class="sym-light">{light}</span>
                <span class="sym-dark">{dark}</span>
                </span><figcaption>{WebUtility.HtmlEncode(caption)}</figcaption></figure>
                """;
    }

    private string ReadInline(string relPath)
    {
        string abs = Path.Combine(_docsRoot, relPath);
        if (!File.Exists(abs))
            throw new InvalidOperationException(
                $"the figure file '{relPath}' has not been generated. Figures are produced before pages, " +
                "so this means the catalog row exists but its capture did not run.");

        string svg = File.ReadAllText(abs);

        foreach (Match m in Regex.Matches(svg, @"font-family=""(?<f>[^""]+)"""))
            foreach (var family in m.Groups["f"].Value.Split(',', StringSplitOptions.TrimEntries))
                FontFamiliesUsed.Add(family);

        // Drop the banner comment and the XML declaration: inline SVG is a fragment, not a document.
        svg = Regex.Replace(svg, @"<\?xml[^>]*\?>", "");
        svg = Regex.Replace(svg, @"<!--.*?-->", "", RegexOptions.Singleline);
        return svg.Trim();
    }

    private string Relative(string page)
    {
        var rel = Path.GetRelativePath(_pageDir, Path.Combine(_docsRoot, page));
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
