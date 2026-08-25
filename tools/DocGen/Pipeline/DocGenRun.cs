using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Diagnostics.Fixtures;
using CircuitRF.Ui.Theming;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// One full docs regeneration, in the order the steps depend on each other:
/// symbols and figures first (pages inline them), then the toolbars and their manifests, then the
/// pages, then the fonts (which are chosen from what the figures turned out to reference), then the
/// stylesheet's generated font block, and finally the report.
/// </summary>
public sealed class DocGenRun
{
    private readonly string _docsRoot;
    private readonly Dictionary<string, IReadOnlyList<ToolbarCatalog.Entry>> _manifests = [];
    private readonly List<string> _untooltipped = [];
    private readonly List<string> _written = [];

    private long _bytesBefore, _bytesAfter;
    private int _clipsDropped, _pathsDeduped;
    private readonly List<string> _fontSubstitutions = [];

    public DocGenRun(string docsRoot) => _docsRoot = Path.GetFullPath(docsRoot);

    /// <summary>The human-readable summary printed at the end and pasted into RESOLVED.md.</summary>
    public string Report { get; private set; } = "";

    /// <summary>Which decks to build, or null for every deck the sources declare.</summary>
    private IReadOnlySet<string>? _decks;

    /// <summary>Which colour variants the decks are rendered in. Both, unless narrowed.</summary>
    private IReadOnlyList<ColorVariant> _variants = [ColorVariant.Light, ColorVariant.Dark];

    /// <summary>Deck ids the sources actually offered, so an unknown --deck can name the real ones.</summary>
    private readonly List<string> _decksOffered = [];

    public void Run(bool slidesOnly = false, string? slidesOut = null,
                    IReadOnlySet<string>? decks = null,
                    IReadOnlyList<ColorVariant>? variants = null)
    {
        var clock = Stopwatch.StartNew();
        _decks = decks;
        if (variants is { Count: > 0 }) _variants = variants;

        string figures = Path.Combine(_docsRoot, "assets", "figures");
        string symbols = Path.Combine(_docsRoot, "assets", "symbols");
        Directory.CreateDirectory(figures);

        if (!slidesOnly)
        {
            Symbols(symbols);
            InlineGlyphs(figures);
            Figures(figures);
            Toolbars(figures);
        }

        var families = Pages(slidesOnly, slidesOut);

        if (!slidesOnly)
        {
            long fontBytes = Fonts(families);
            Summarise(clock.Elapsed, fontBytes);
        }
        else
        {
            if (_decks is not null)
            {
                var unknown = _decks.Where(d => !_decksOffered.Contains(d)).ToList();
                if (unknown.Count > 0)
                    throw new InvalidOperationException(
                        $"No deck named {string.Join(", ", unknown.Select(u => "'" + u + "'"))}. "
                      + $"The sources under docs/user/src/slides/ offer: {string.Join(", ", _decksOffered)}. "
                      + "A misspelt deck id would otherwise report success having written nothing.");
            }

            Report = _written.Count > 0
                ? $"Slides regenerated in {clock.Elapsed.TotalSeconds:F1} s "
                  + $"({string.Join(" + ", _variants)}):\n  "
                  + string.Join("\n  ", _written)
                : "No slide decks were produced. A deck comes from a source page under docs/user/src/ "
                + "whose front-matter says 'kind: slides'; there is none, or the docs root is wrong.";
        }
    }

    // ── Symbols ───────────────────────────────────────────────────────────────

    private void Symbols(string outDir)
    {
        foreach (var f in SymbolArtworkGenerator.GenerateAll(outDir))
            _written.Add(f);
    }

    // ── Inline glyphs (table cells, not figures) ──────────────────────────────

    private void InlineGlyphs(string outDir)
    {
        foreach (var f in InlineGlyphArtwork.GenerateSnapGlyphs(outDir))
            _written.Add(f);
    }

    // ── Captured UI figures ───────────────────────────────────────────────────

    private void Figures(string outDir)
    {
        foreach (var row in FigureCatalog.Catalog)
            foreach (var variant in (ColorVariant[])[ColorVariant.Light, ColorVariant.Dark])
            {
                string path = Path.Combine(outDir, UiArtworkGenerator.FileStem(row.Id, variant) + ".svg");
                using var scene = row.Build();
                UiArtworkGenerator.RenderScene(scene, row.Width, row.Height, variant, path,
                                               row.Chrome, row.MustContainPopup);
                Account(path);
            }
    }

    // ── Toolbars: figure, indexed figure, manifest ────────────────────────────

    private void Toolbars(string outDir)
    {
        foreach (var row in ToolbarCatalog.Catalog)
        {
            IReadOnlyList<ToolbarCatalog.Entry>? manifest = null;

            foreach (var variant in (ColorVariant[])[ColorVariant.Light, ColorVariant.Dark])
            {
                // A fresh fixture per capture: an Avalonia control cannot be hosted by two windows,
                // and this panel is captured four times.
                var plain = DocFixtures.Toolbar(row.Id);
                manifest ??= ToolbarCatalog.Manifest(plain.Panel);

                string p1 = Path.Combine(outDir, UiArtworkGenerator.FileStem("toolbar-" + row.Id, variant) + ".svg");
                UiArtworkGenerator.RenderScene(new FigureScene(plain.Panel), row.Width, row.Height, variant, p1);
                Account(p1);

                var indexed = DocFixtures.Toolbar(row.Id);
                var callouts = ToolbarCatalog.WithCallouts(
                    indexed.Panel, ToolbarCatalog.Manifest(indexed.Panel), variant, row.Height);
                string p2 = Path.Combine(outDir,
                    UiArtworkGenerator.FileStem("toolbar-" + row.Id + "-indexed", variant) + ".svg");
                UiArtworkGenerator.RenderScene(new FigureScene(callouts), row.Width, row.Height + 26, variant, p2);
                Account(p2);

                foreach (var p3 in ToolbarButtons(row.Id, variant, outDir)) Account(p3);
            }

            _manifests[row.Id] = manifest!;
            string json = Path.Combine(outDir, "toolbar-" + row.Id + ".json");
            File.WriteAllText(json, ToolbarCatalog.ToJson(row.Id, row.Title, manifest!));
            _written.Add(json);

            foreach (var e in manifest!.Where(x => x.Index > 0 && x.Kind is "button" or "toggle"
                                                                && x.Tooltip.Length == 0))
                _untooltipped.Add($"{row.Id}: item {e.Index} ({(e.Id.Length == 0 ? e.Icon : e.Id)})");
        }
    }

    /// <summary>
    /// Capture each of a toolbar's buttons ON ITS OWN, so the per-button table can show the button
    /// instead of naming its icon.
    ///
    /// <para>The owner asked for the picture and for the redundant Icon column to go (2026-08-20):
    /// a reader looking up "what is button 7" wants to recognise it on the toolbar, and
    /// <c>ZoomOutIcon</c> in a text column does not help them do that. The button is DETACHED from
    /// the panel and captured at its own arranged size — the same reasoning as lifting the whole
    /// toolbar out of its editor rather than cropping a screenshot.</para>
    /// </summary>
    private IEnumerable<string> ToolbarButtons(string id, ColorVariant variant, string outDir)
    {
        var fixture = DocFixtures.Toolbar(id);
        var manifest = ToolbarCatalog.Manifest(fixture.Panel);
        var written = new List<string>();

        // Snapshot and detach first: a control cannot be hosted by a second window while it is still
        // a child of the panel, and removing children shifts every later index.
        var children = fixture.Panel.Children.ToList();
        var context  = fixture.Panel.DataContext;
        fixture.Panel.Children.Clear();

        foreach (var e in manifest)
        {
            if (e.Index == 0 || e.Kind is not ("button" or "toggle")) continue;
            if (e.Slot >= children.Count) continue;

            var button = children[e.Slot];
            var size = button.Bounds;
            if (size.Width < 1 || size.Height < 1) continue;

            button.DataContext = context;      // detaching cost it the inherited one
            string path = Path.Combine(outDir,
                UiArtworkGenerator.FileStem($"toolbar-{id}-btn-{e.Index}", variant) + ".svg");
            UiArtworkGenerator.RenderScene(new FigureScene(button),
                (int)Math.Round(size.Width), (int)Math.Round(size.Height), variant, path);
            written.Add(path);
        }

        return written;
    }

    // ── Pages ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Convert every <c>docs/user/src/**.md</c>, copy every not-yet-ported HTML page through
    /// untouched, and return the font families the inlined figures turned out to use.
    /// </summary>
    private IReadOnlySet<string> Pages(bool slidesOnly, string? slidesOut)
    {
        string srcRoot = Path.Combine(_docsRoot, "src");
        var families = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(srcRoot))
        {
            // Nothing has been ported to Markdown yet, which is a legitimate state during migration —
            // the hand-written pages are simply copied through. But a slides-only run with no sources
            // has nothing to do, and must say so rather than reporting success.
            if (slidesOnly)
                throw new InvalidOperationException(
                    $"No Markdown sources under {srcRoot}, so there is nothing to build a deck from.");
            return families;
        }

        var pages = Directory.EnumerateFiles(srcRoot, "*.md", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.Ordinal)
                             .Select(DocPage.Load)
                             .ToList();

        // Every anchor the generated site will offer, known BEFORE any page is written, so a
        // forward cross-link resolves as readily as a backward one.
        var offered = AnchorIndex(pages);

        // The reading order, checked against the page set BEFORE anything is written: a run that
        // would produce an unreachable page fails instead of producing it.
        var nav = SiteNav.Load(_docsRoot);
        var titles = pages.Where(p => p.Kind != "slides")
                          .ToDictionary(p => p.Slug, p => p.Title, StringComparer.Ordinal);
        if (nav is not null && !slidesOnly)
        {
            var emitted = pages.Where(p => p.Kind != "slides").Select(p => p.Slug)
                               .Concat(CopiedThroughPages(pages))
                               .ToHashSet(StringComparer.Ordinal);
            nav.Validate(emitted);
            foreach (var slug in nav.Order.Where(s => !titles.ContainsKey(s)))
                titles[slug] = TitleOfHtml(Path.Combine(_docsRoot, slug));
        }

        foreach (var page in pages)
        {
            if (page.Kind == "slides")
            {
                // Decks are only produced when an output directory was asked for. They must NOT
                // default to somewhere under docs/user: everything there is copied into the
                // application bundle, and a PDF deck is not a runtime asset.
                if (slidesOut is null) continue;

                if (page.Deck.Length == 0)
                    throw new InvalidOperationException(
                        $"{page.SourcePath}: a 'kind: slides' page must declare 'deck: <id>' in its "
                      + "front-matter. The id is what `--deck <id>` selects; without one the deck can only "
                      + "ever be built as part of 'all', which is the state this option exists to fix.");

                _decksOffered.Add(page.Deck);
                if (_decks is not null && !_decks.Contains(page.Deck)) continue;

                string stem = Path.GetFileNameWithoutExtension(page.Slug);
                foreach (var variant in _variants)
                {
                    string deck = Path.Combine(slidesOut,
                        UiArtworkGenerator.FileStem(stem, variant) + ".pdf");
                    SlideEmitter.Render(page, Path.Combine(_docsRoot, "assets", "figures"), deck, variant);
                    _written.Add(deck);
                }
                continue;
            }

            if (slidesOnly) continue;

            string outPath = Path.Combine(_docsRoot, page.Slug.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            var expander = new Placeholders(_docsRoot, Path.GetDirectoryName(outPath)!,
                                            id => _manifests[id], offered.Contains, nav, titles, page.Slug);
            string expanded = expander.Expand(page.Body, page.SourcePath);
            foreach (var f in expander.FontFamiliesUsed) families.Add(f);

            string html = HtmlEmitter.Render(page, expanded, nav, titles);

            var leftover = Regex.Match(html, @"\{\{[^}]*\}\}");
            if (leftover.Success)
                throw new InvalidOperationException(
                    $"{page.SourcePath}: an unexpanded placeholder survived into the output: " +
                    $"'{leftover.Value}'. A placeholder that reaches a shipped page as literal braces is " +
                    "exactly the failure this pipeline exists to prevent.");

            File.WriteAllText(outPath, html);
            _written.Add(outPath);
            Account(outPath, htmlOnly: true);
        }

        // Un-ported pages stay exactly as they are (migration is incremental, never big-bang), and
        // their font usage is whatever their <img>-referenced symbol files already used.
        return families;
    }

    /// <summary>
    /// The hand-written HTML pages that survive this run untouched — everything under the docs root
    /// that no Markdown source is about to overwrite. They are part of the site, so they are part of
    /// the reading order and of the orphan check.
    /// </summary>
    private IEnumerable<string> CopiedThroughPages(IReadOnlyList<DocPage> pages)
    {
        var generated = pages.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
        foreach (var html in Directory.EnumerateFiles(_docsRoot, "*.html", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_docsRoot, html).Replace(Path.DirectorySeparatorChar, '/');
            if (!generated.Contains(rel)) yield return rel;
        }
    }

    /// <summary>The <c>&lt;h1&gt;</c> of a page this run is not generating, for its nav label.</summary>
    private static string TitleOfHtml(string path)
    {
        if (!File.Exists(path)) return Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(File.ReadAllText(path), @"<h1[^>]*>(?<t>.*?)</h1>", RegexOptions.Singleline);
        return m.Success ? Regex.Replace(m.Groups["t"].Value, "<[^>]+>", "").Trim()
                         : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Every <c>page</c> and <c>page#anchor</c> the generated site will contain — from the Markdown
    /// sources being written now, plus every hand-written HTML page still being copied through.
    /// </summary>
    private HashSet<string> AnchorIndex(IReadOnlyList<DocPage> pages)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var page in pages.Where(p => p.Kind != "slides"))
        {
            set.Add(page.Slug);
            foreach (var id in HeadingIds(page.Body)) set.Add(page.Slug + "#" + id);
        }

        foreach (var html in Directory.EnumerateFiles(_docsRoot, "*.html", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_docsRoot, html).Replace(Path.DirectorySeparatorChar, '/');
            if (pages.Any(p => p.Slug == rel)) continue;    // about to be overwritten by a generated one
            set.Add(rel);
            foreach (Match m in Regex.Matches(File.ReadAllText(html), @"id=""(?<id>[^""]+)"""))
                set.Add(rel + "#" + m.Groups["id"].Value);
        }

        return set;
    }

    /// <summary>
    /// Heading ids as Markdig's auto-identifier extension will produce them, plus any explicit
    /// <c>{#id}</c> attribute — the anchor contract depends on the explicit form, since a
    /// <c>SymbolKind</c>'s anchor is its lowercased name and not a slug of the heading text.
    /// </summary>
    internal static IEnumerable<string> HeadingIds(string markdown)
    {
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!line.StartsWith('#')) continue;

            var explicitId = Regex.Match(line, @"\{#(?<id>[^}\s]+)\}\s*$");
            if (explicitId.Success) { yield return explicitId.Groups["id"].Value; continue; }

            var text = line.TrimStart('#').Trim();
            if (text.Length == 0) continue;
            yield return Slug(text);
        }

        // Explicit ids can also be written as raw HTML anchors in the body.
        foreach (Match m in Regex.Matches(markdown, @"id=""(?<id>[^""]+)"""))
            yield return m.Groups["id"].Value;
    }

    /// <summary>Markdig's AutoIdentifier slug: lowercase, non-alphanumerics to hyphens, collapsed.</summary>
    private static string Slug(string text)
    {
        var cleaned = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return cleaned;
    }

    // ── Fonts ─────────────────────────────────────────────────────────────────

    private long Fonts(IReadOnlySet<string> familiesUsed)
    {
        string fontsDir = Path.Combine(_docsRoot, "assets", "fonts");
        long bytes = FontExtractor.Extract(fontsDir, familiesUsed, out var missing);
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "These font assets could not be read through the asset loader, so the docs would ship " +
                "with a substituted typeface and nothing would say so:\n  " + string.Join("\n  ", missing));

        string css = Path.Combine(_docsRoot, "assets", "css", "circuitrf-docs.css");
        DocsCss.WriteFontBlock(css, FontExtractor.FontFaceCss(familiesUsed));
        _written.Add(css);
        return bytes;
    }

    // ── Bookkeeping ───────────────────────────────────────────────────────────

    private void Account(string path, bool htmlOnly = false)
    {
        _written.Add(path);
        if (htmlOnly) return;
        var r = UiArtworkGenerator.LastReport;
        _bytesBefore  += r.BytesBefore;
        _bytesAfter   += r.BytesAfter;
        _clipsDropped += r.ClipsDropped;
        _pathsDeduped += r.PathsDeduped;
        foreach (var sub in r.FontSubstitutions ?? [])
            _fontSubstitutions.Add($"{Path.GetFileName(path)}: {sub}");
    }

    private void Summarise(TimeSpan elapsed, long fontBytes)
    {
        long total = _written.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Files written .............. {_written.Distinct().Count()}");
        sb.AppendLine($"Total emitted bytes ........ {total:N0} (of which fonts {fontBytes:N0})");
        sb.AppendLine($"SVG before post-pass ....... {_bytesBefore:N0}");
        sb.AppendLine($"SVG after  post-pass ....... {_bytesAfter:N0}"
                    + (_bytesAfter > 0 ? $"  ({(double)_bytesBefore / _bytesAfter:F2}x smaller)" : ""));
        sb.AppendLine($"  no-op clips dropped ...... {_clipsDropped}");
        sb.AppendLine($"  repeated paths hoisted ... {_pathsDeduped}");
        sb.AppendLine($"Black-alpha brushes remapped {DocsApp.RemapReport.Count}");
        sb.AppendLine($"Wall clock ................. {elapsed.TotalSeconds:F1} s");
        if (elapsed.TotalSeconds > 60)
            sb.AppendLine("NOTE: generation took over 60 s. That is slower than this is meant to be; "
                        + "say so rather than letting it become normal.");
        if (_fontSubstitutions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{_fontSubstitutions.Count} text run(s) used a character NO circuitRF font");
            sb.AppendLine("covers, so Skia substituted a PLATFORM font and baked its name in. Each has been");
            sb.AppendLine($"redirected to {CircuitRF.Ui.Diagnostics.SvgFontNormalizer.GlyphFallbackFamily}, which is shipped — but the interface is drawing a");
            sb.AppendLine("glyph its own typefaces do not have, which is worth fixing at the source:");
            foreach (var f in _fontSubstitutions.Distinct()) sb.AppendLine("  " + f);
        }
        if (_untooltipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{_untooltipped.Count} toolbar button(s) have NO TOOLTIP. That is a UI bug, not a");
            sb.AppendLine("blank table cell — record it in src/Ui/RESOLVED.md:");
            foreach (var t in _untooltipped) sb.AppendLine("  " + t);
        }
        Report = sb.ToString();
    }
}
