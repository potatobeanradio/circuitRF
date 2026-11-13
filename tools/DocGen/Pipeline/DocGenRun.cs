using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Diagnostics.Fixtures;
using CircuitRF.Render;
using CircuitRF.Ui.Theming;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// One docs regeneration, in the order the steps depend on each other:
/// symbols and figures first (pages inline them) WHEN <see cref="Capture"/> asks for them,
/// then the toolbars and their manifests, then the pages, then the fonts (which are chosen from what the figures turned out to reference), then the
/// stylesheet's generated font block, and finally the report.
/// </summary>
public sealed class DocGenRun
{
    private readonly string _docsRoot;
    private readonly Dictionary<string, IReadOnlyList<ToolbarCatalog.Entry>> _manifests = [];
    private readonly List<string> _untooltipped = [];
    private readonly List<string> _written = [];

    private long _bytesBefore, _bytesAfter;
    private int _searchSections;
    private long _searchBytes;
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

    /// <summary>Rebuild the figures <see cref="CircuitRF.Ui.Diagnostics.FigureCatalog.Row.Static"/>
    /// marks as committed. Off for an ordinary run — see that flag for why they are committed.</summary>
    public bool RebuildStatic { get => _rebuildStatic; set => _rebuildStatic = value; }
    private bool _rebuildStatic;

    /// <summary>
    /// Regenerate ONLY these pages — each a source path (<c>docs/user/src/reference/wbond.md</c>,
    /// absolute, or relative to the working directory or to <c>docs/user/src</c>) or an output slug
    /// (<c>reference/wbond.html</c>). Null for an ordinary full run.
    ///
    /// <para>A page run captures NOTHING: symbols, figures, toolbars and fonts are left exactly as
    /// committed, and the page inlines the figures already on disk. That is what makes it seconds
    /// instead of minutes, and it is also its limit — a change that moves a figure needs the full run.
    /// Every page is still EXPANDED, because the search index and the cross-link check are built from
    /// all of them; only the named pages and the search index are written. Any other page whose output
    /// would now differ (a retitled page renames its neighbours' Previous/Next links and the contents
    /// page) is named in the report rather than written, so the run cannot quietly leave the site
    /// inconsistent.</para>
    /// </summary>
    public IReadOnlyList<string>? OnlyPages { get; set; }

    private readonly List<string> _staleOthers = [];

    /// <summary>Which figures a run recaptures. See <see cref="FigureMode"/>.</summary>
    public FigureMode Capture { get; set; } = FigureMode.None;

    /// <summary>Recapture these too, whatever <see cref="Capture"/> says — FigureCatalog ids, or a
    /// toolbar as <c>toolbar-&lt;id&gt;</c>. Null for none.</summary>
    public IReadOnlySet<string>? OnlyFigures { get; set; }

    /// <summary>Capture nothing, but record every page's source as current — for after a full capture
    /// made some other way, so the next <see cref="FigureMode.ChangedPages"/> run starts from it.</summary>
    public bool MarkFiguresCurrent { get; set; }

    private readonly List<string> _captured = [];
    private readonly List<string> _changedPages = [];
    private bool _noStamps;

    /// <summary>
    /// Where each page's source hash was recorded the last time its figures were captured. Beside
    /// <c>_nav.txt</c>, under <c>src/</c>, and excluded from the application bundle like the sources.
    /// </summary>
    private string StampsPath => Path.Combine(_docsRoot, "src", "_figure-stamps.txt");

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

        if (OnlyPages is not null)
        {
            Pages(slidesOnly: false, slidesOut: null);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Pages regenerated in {clock.Elapsed.TotalSeconds:F1} s "
                        + "(figures, symbols, toolbars and fonts left as committed):");
            foreach (var f in _written.Distinct()) sb.AppendLine("  " + f);
            if (_staleOthers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{_staleOthers.Count} OTHER page(s) would also change and were NOT written. Add them");
                sb.AppendLine("with --page, or run the full regeneration:");
                foreach (var p in _staleOthers) sb.AppendLine("  " + p);
            }
            Report = sb.ToString();
            return;
        }

        if (!slidesOnly)
        {
            ValidateOnlyFigures();
            var sel = SelectFigures(figures, symbols);
            if (!sel.IsEmpty)
            {
                Symbols(symbols, sel);
                InlineGlyphs(figures, sel);
                Figures(figures, sel);
                Toolbars(figures, sel);
            }
        }

        var families = Pages(slidesOnly, slidesOut);

        if (!slidesOnly)
        {
            long fontBytes = Fonts(families);

            // LAST, once every page and figure is written: a run that failed part-way must leave the
            // old stamps, or the pages it did not reach would be recorded as captured.
            if (Capture != FigureMode.None || MarkFiguresCurrent) WriteStamps();
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

    private void Symbols(string outDir, FigureSelection sel)
    {
        if (sel.Symbols.Count == 0 && !sel.All) return;
        foreach (var f in SymbolArtworkGenerator.GenerateAll(outDir, sel.All ? null : sel.Symbols.Contains))
        {
            _written.Add(f);
            _captured.Add(f);
        }
    }

    // ── Inline glyphs (table cells, not figures) ──────────────────────────────

    private void InlineGlyphs(string outDir, FigureSelection sel)
    {
        if (sel.Glyphs.Count == 0 && !sel.All) return;
        foreach (var f in InlineGlyphArtwork.GenerateSnapGlyphs(outDir, sel.All ? null : sel.Glyphs.Contains))
        {
            _written.Add(f);
            _captured.Add(f);
        }
    }

    // ── Captured UI figures ───────────────────────────────────────────────────

    private void Figures(string outDir, FigureSelection sel)
    {
        foreach (var row in FigureCatalog.Catalog)
        {
            if (!sel.All && !sel.Ui.Contains(row.Id)) continue;
            foreach (var variant in (ColorVariant[])[ColorVariant.Light, ColorVariant.Dark])
            {
                string path = Path.Combine(outDir, UiArtworkGenerator.FileStem(row.Id, variant) + ".svg");

                // A STATIC figure is committed and left alone — see FigureCatalog.Row.Static. It is
                // CHECKED rather than skipped: the page inlines it either way, so a missing one is a
                // blank box on a chapter, which is the failure this whole factory exists to refuse.
                if (row.Static && !_rebuildStatic)
                {
                    if (!File.Exists(path))
                        throw new InvalidOperationException(
                            $"'{Path.GetFileName(path)}' is a STATIC figure and it is not committed. It "
                          + "is not rebuilt by an ordinary regeneration, so there is nothing here to "
                          + "recover it from: restore it, or rebuild it with --rebuild-static (which "
                          + "will tell you what that figure needs).");
                    Account(path);
                    continue;
                }

                using var scene = row.Build();
                UiArtworkGenerator.RenderScene(scene, row.Width, row.Height, variant, path,
                                               row.Chrome, row.MustContainPopup);
                Account(path);
                _captured.Add(path);
            }
        }
    }

    /// <summary>An unknown <c>--figure</c> id is an error naming the real ones — never a run that
    /// reports success having captured nothing. A Static figure asked for by name without
    /// <c>--rebuild-static</c> is refused for the same reason: it would only be checked, not rebuilt.</summary>
    private void ValidateOnlyFigures()
    {
        if (OnlyFigures is null) return;
        var figures  = FigureCatalog.Catalog.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var toolbars = ToolbarCatalog.Catalog.Select(r => "toolbar-" + r.Id).ToHashSet(StringComparer.Ordinal);

        var unknown = OnlyFigures.Where(id => !figures.ContainsKey(id) && !toolbars.Contains(id)
                                            && !SymbolStems.Contains(id)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"--figure: no figure named {string.Join(", ", unknown.Select(u => "'" + u + "'"))}. "
              + $"Figures: {string.Join(", ", figures.Keys)}. Toolbars: {string.Join(", ", toolbars)}. "
              + $"Symbols: {string.Join(", ", SymbolStems.Order(StringComparer.Ordinal))}.");

        var staticAsked = OnlyFigures.Where(id => figures.TryGetValue(id, out var r) && r.Static && !_rebuildStatic).ToList();
        if (staticAsked.Count > 0)
            throw new InvalidOperationException(
                $"--figure: {string.Join(", ", staticAsked)} {(staticAsked.Count == 1 ? "is a" : "are")} STATIC "
              + "figure(s), which a capture leaves as committed. Add --rebuild-static to rebuild them.");
    }

    // ── Which figures to capture ──────────────────────────────────────────────

    /// <summary>The component symbols <c>--figure</c> accepts by their file stem (<c>atten</c>,
    /// <c>vccs</c>) — the stem a <c>{{symbol: …}}</c> placeholder names, so the id the missing-figure
    /// message prints is one this flag takes. A UI figure or toolbar of the same name wins.</summary>
    private static readonly HashSet<string> SymbolStems =
        SymbolArtworkGenerator.Catalog.Select(r => r.File).ToHashSet(StringComparer.Ordinal);

    /// <summary>What one run recaptures: everything, or these ids of each kind.</summary>
    private sealed class FigureSelection
    {
        public bool All;
        public readonly HashSet<string> Ui = new(StringComparer.Ordinal);
        public readonly HashSet<string> Toolbars = new(StringComparer.Ordinal);
        public readonly HashSet<string> Symbols = new(StringComparer.Ordinal);
        public readonly HashSet<string> Glyphs = new(StringComparer.Ordinal);
        public bool IsEmpty => !All && Ui.Count + Toolbars.Count + Symbols.Count + Glyphs.Count == 0;
    }

    /// <summary>The placeholders that inline a captured file. Read from the SOURCE, not the
    /// expansion, so a page's citations are known before anything is captured.</summary>
    private static readonly Regex Cite = new(
        @"\{\{\s*(?<kind>ui|toolbar|symbol|snapglyph)\s*:\s*(?<arg>[^}|]+?)\s*(\|[^}]*)?\}\}", RegexOptions.Compiled);

    /// <summary>
    /// The figures this run recaptures. <see cref="FigureMode.All"/> is everything. Otherwise:
    /// <list type="bullet">
    /// <item>with <see cref="FigureMode.ChangedPages"/>, every figure cited by a page whose source
    /// differs from the hash recorded at the last capture — a figure cited only by unchanged pages
    /// is left exactly as committed;</item>
    /// <item>with any capture at all, every cited figure that is MISSING on disk, since a page cannot
    /// be built without it;</item>
    /// <item>and every <see cref="OnlyFigures"/> id, named explicitly.</item>
    /// </list>
    /// </summary>
    private FigureSelection SelectFigures(string figures, string symbols)
    {
        var sel = new FigureSelection();
        if (Capture == FigureMode.All) { sel.All = true; return sel; }

        foreach (var id in OnlyFigures ?? (IEnumerable<string>)[])
        {
            if (id.StartsWith("toolbar-", StringComparison.Ordinal) && !FigureCatalog.Catalog.Any(r => r.Id == id))
                sel.Toolbars.Add(id["toolbar-".Length..]);
            else if (SymbolStems.Contains(id) && !FigureCatalog.Catalog.Any(r => r.Id == id))
                sel.Symbols.Add(id);
            else sel.Ui.Add(id);
        }
        if (Capture == FigureMode.None) return sel;

        var stamps = ReadStamps();
        _noStamps = stamps is null;
        foreach (var (rel, hash, text) in Sources())
        {
            bool changed = stamps is null || !stamps.TryGetValue(rel, out var old) || old != hash;
            if (changed) _changedPages.Add(rel);

            foreach (Match m in Cite.Matches(text))
            {
                string kind = m.Groups["kind"].Value, arg = m.Groups["arg"].Value.Trim();
                var (set, dir, stem) = kind switch
                {
                    "ui"      => (sel.Ui,       figures, arg),
                    "toolbar" => (sel.Toolbars, figures, "toolbar-" + arg + "-indexed"),
                    "symbol"  => (sel.Symbols,  symbols, arg),
                    _         => (sel.Glyphs,   figures, InlineGlyphArtwork.SnapGlyphStem + arg),
                };
                bool missing = !File.Exists(Path.Combine(dir, stem + ".svg"))
                            || !File.Exists(Path.Combine(dir, stem + "-dark.svg"));
                if (changed || missing) set.Add(arg);
            }
        }
        return sel;
    }

    /// <summary>Every Markdown source: its path relative to <c>src/</c>, the hash of its text with
    /// line endings normalised (a Windows checkout must not read as a change), and the text.</summary>
    private IEnumerable<(string Rel, string Hash, string Text)> Sources()
    {
        string src = Path.Combine(_docsRoot, "src");
        if (!Directory.Exists(src)) yield break;
        foreach (var path in Directory.EnumerateFiles(src, "*.md", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(path);
            string rel  = Path.GetRelativePath(src, path).Replace(Path.DirectorySeparatorChar, '/');
            string hash = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));
            yield return (rel, hash, text);
        }
    }

    private Dictionary<string, string>? ReadStamps()
    {
        if (!File.Exists(StampsPath)) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(StampsPath))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf("  ", StringComparison.Ordinal);
            if (sp > 0) map[line[(sp + 2)..]] = line[..sp];
        }
        return map;
    }

    /// <summary>Record every page as captured. Only reached by a run that captured every figure a
    /// changed page cites (or was told to mark them current), so the record is true.</summary>
    private void WriteStamps()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# Written by tools/DocGen. One line per page: the hash of its Markdown source when its\n");
        sb.Append("# figures were last captured. `--figures` recaptures only what changed pages cite.\n");
        foreach (var (rel, hash, _) in Sources()) sb.Append(hash).Append("  ").Append(rel).Append('\n');
        string text = sb.ToString();
        if (!File.Exists(StampsPath) || File.ReadAllText(StampsPath) != text) File.WriteAllText(StampsPath, text);
        _written.Add(StampsPath);
    }

    // ── Toolbars: figure, indexed figure, manifest ────────────────────────────

    private void Toolbars(string outDir, FigureSelection sel)
    {
        foreach (var row in ToolbarCatalog.Catalog)
        {
            if (!sel.All && !sel.Toolbars.Contains(row.Id)) continue;
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
                _captured.Add(p1);

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

        var search = new SearchIndex();

        var pages = Directory.EnumerateFiles(srcRoot, "*.md", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.Ordinal)
                             .Select(DocPage.Load)
                             .ToList();
        // Each shipped example workspace's README, as a page of its own under examples/ (ExamplePages).
        pages.AddRange(ExamplePages.Load(_docsRoot));

        // Every anchor the generated site will offer, known BEFORE any page is written, so a
        // forward cross-link resolves as readily as a backward one.
        var offered = AnchorIndex(pages);

        var selected = OnlyPages is null ? null : SelectPages(pages, srcRoot, OnlyPages);

        // The reading order, checked against the page set BEFORE anything is written: a run that
        // would produce an unreachable page fails instead of producing it.
        var nav = SiteNav.Load(_docsRoot);
        var titles = pages.Where(p => p.Kind != "slides")
                          .ToDictionary(p => p.Slug, p => p.Title, StringComparer.Ordinal);
        if (nav is not null && !slidesOnly)
        {
            // A RETIRED page is deliberately not in the reading order: it is a slug that must not 404
            // for somebody with a bookmark, not a chapter, and a reader following Next should never
            // arrive at one. Excluded from `emitted` so the orphan check does not refuse it.
            // An example's README page is reached from the Example Workspaces page, not by Previous/Next: 39 more pages in
            // the chain would bury the chapters, so it is outside the reading order as a retired page is (but searched).
            var emitted = pages.Where(p => p.Kind is not "slides" and not "retired" and not ExamplePages.Kind).Select(p => p.Slug)
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
                                            ToolbarManifest, offered.Contains, nav, titles, page.Slug);
            string expanded = expander.Expand(page.Body, page.SourcePath);
            foreach (var f in expander.FontFamiliesUsed) families.Add(f);

            // Rank = position in the reading order, which is what breaks a scoring tie at query
            // time. A page the nav does not list sorts last rather than first: SiteNav.Validate has
            // already refused that case, so this only decides the shape of a run with no _nav.txt.
            int rank = nav is null ? int.MaxValue
                     : nav.Order.ToList().IndexOf(page.Slug) is var r && r >= 0 ? r : int.MaxValue;

            // A retired slug is a signpost and not an answer: indexing it would put a page saying
            // "this moved" above the chapter it moved to, for the words they both contain.
            if (page.Kind != "retired") search.Add(page, HtmlEmitter.BodyHtml(expanded), rank);

            string html = HtmlEmitter.Render(page, expanded, nav, titles);

            var leftover = Regex.Match(html, @"\{\{[^}]*\}\}");
            if (leftover.Success)
                throw new InvalidOperationException(
                    $"{page.SourcePath}: an unexpanded placeholder survived into the output: " +
                    $"'{leftover.Value}'. A placeholder that reaches a shipped page as literal braces is " +
                    "exactly the failure this pipeline exists to prevent.");

            if (selected is not null && !selected.Contains(page))
            {
                if (!File.Exists(outPath) || File.ReadAllText(outPath) != html) _staleOthers.Add(page.Slug);
                continue;
            }

            File.WriteAllText(outPath, html);
            _written.Add(outPath);
            Account(outPath, htmlOnly: true);
            if (page.Kind == ExamplePages.Kind)
                foreach (string copied in ExamplePages.CopyPictures(page, Path.GetDirectoryName(outPath)!)) _written.Add(copied);
        }

        // The search index is written LAST, from what the pages turned out to contain — so it cannot
        // describe a page this run did not write.
        //
        // A SLIDES-ONLY RUN MUST NOT REACH THE WRITE. This comment used to assert that it never did,
        // and it did: the loop above `continue`s past each page's HTML emission on `slidesOnly` but
        // still falls through to here, with `search` holding nothing — so
        // `dotnet run --project tools/DocGen -- --slides docs/slides --deck overview` OVERWROTE the
        // committed site index with `{"v":1,"p":[],"s":[]}`. Nothing failed and nothing was printed;
        // the symptom is that search on every page of the published documentation returns nothing,
        // which is not visible in a diff anybody reads. `docs/slides/` is git-ignored, so a deck run
        // touches no tracked file OTHER than this one — which is exactly why it went unnoticed.
        if (slidesOnly) return families;

        string js = Path.Combine(_docsRoot, "assets", "js", "search-index.js");
        Directory.CreateDirectory(Path.GetDirectoryName(js)!);
        File.WriteAllText(js, search.ToJs());
        _written.Add(js);
        _searchSections = search.SectionCount;
        _searchBytes = new FileInfo(js).Length;

        // Un-ported pages stay exactly as they are (migration is incremental, never big-bang), and
        // their font usage is whatever their <img>-referenced symbol files already used.
        return families;
    }

    /// <summary>
    /// A toolbar's button manifest: the one the full run captured, or — on a page run, which captures
    /// no toolbars — read from a fresh fixture, exactly as <see cref="Toolbars"/> reads it.
    /// </summary>
    private IReadOnlyList<ToolbarCatalog.Entry> ToolbarManifest(string id)
    {
        if (!_manifests.TryGetValue(id, out var m))
            _manifests[id] = m = ToolbarCatalog.Manifest(DocFixtures.Toolbar(id).Panel);
        return m;
    }

    /// <summary>Resolve <see cref="OnlyPages"/> against the sources. An unknown name is an error,
    /// never a run that reports success having written nothing.</summary>
    private static HashSet<DocPage> SelectPages(IReadOnlyList<DocPage> pages, string srcRoot,
                                                IReadOnlyList<string> requested)
    {
        var chosen = new HashSet<DocPage>();
        foreach (var r in requested)
        {
            string slug = r.Replace('\\', '/');
            if (slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) slug = slug[..^3] + ".html";

            var hit = pages.FirstOrDefault(p =>
                   SamePath(p.SourcePath, Path.GetFullPath(r))
                || SamePath(p.SourcePath, Path.GetFullPath(Path.Combine(srcRoot, r)))
                || p.Slug == slug);

            if (hit is null)
                throw new InvalidOperationException(
                    $"--page '{r}' names no source page. Give the Markdown source "
                  + "(docs/user/src/reference/wbond.md) or the output slug (reference/wbond.html).");
            if (hit.Kind == "slides")
                throw new InvalidOperationException(
                    $"--page '{r}' is a slide deck. Build it with --slides <out-dir> --deck {hit.Deck}.");
            chosen.Add(hit);
        }
        return chosen;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), b,
                         OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

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
        if (Capture == FigureMode.ChangedPages)
            sb.AppendLine($"Pages changed since capture  {_changedPages.Count}"
                        + (_noStamps ? " (no stamp file yet, so every page counts as changed)" : "")
                        + (_changedPages.Count is > 0 and <= 12 ? ": " + string.Join(", ", _changedPages) : ""));
        sb.AppendLine(_captured.Count == 0
            ? "Figures captured ........... 0 (every figure left as committed)"
            : $"Figures captured ........... {_captured.Count} files"
              + (Capture == FigureMode.All ? " (--all-figures: everything)" : ""));
        if (MarkFiguresCurrent)
            sb.AppendLine("Figure stamps .............. every page marked current; nothing captured");
        sb.AppendLine($"Total emitted bytes ........ {total:N0} (of which fonts {fontBytes:N0})");
        sb.AppendLine($"SVG before post-pass ....... {_bytesBefore:N0}");
        sb.AppendLine($"SVG after  post-pass ....... {_bytesAfter:N0}"
                    + (_bytesAfter > 0 ? $"  ({(double)_bytesBefore / _bytesAfter:F2}x smaller)" : ""));
        sb.AppendLine($"  no-op clips dropped ...... {_clipsDropped}");
        sb.AppendLine($"  repeated paths hoisted ... {_pathsDeduped}");
        sb.AppendLine($"Black-alpha brushes remapped {DocsApp.RemapReport.Count}");
        sb.AppendLine($"Search index ............... {_searchSections} sections, {_searchBytes:N0} bytes");
        sb.AppendLine($"Wall clock ................. {elapsed.TotalSeconds:F1} s");
        if (elapsed.TotalSeconds > 60)
            sb.AppendLine("NOTE: generation took over 60 s. That is slower than this is meant to be; "
                        + "say so rather than letting it become normal.");
        if (_fontSubstitutions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{_fontSubstitutions.Count} text run(s) used a character NO circuitRF font");
            sb.AppendLine("covers, so Skia substituted a PLATFORM font and baked its name in. Each has been");
            sb.AppendLine($"redirected to {CircuitRF.Render.SvgFontNormalizer.GlyphFallbackFamily}, which is shipped — but the interface is drawing a");
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

/// <summary>Which figures a docs run recaptures from the live application.</summary>
public enum FigureMode
{
    /// <summary>None: every page inlines the SVG already on disk. The default, and seconds.</summary>
    None,
    /// <summary><c>--figures</c>: only the figures cited by pages whose Markdown changed since the last
    /// capture, plus any cited figure missing on disk.</summary>
    ChangedPages,
    /// <summary><c>--all-figures</c>: every figure, symbol, glyph and toolbar — the CI check's mode, and
    /// the only one that catches a UI change moving a figure whose page did not change.</summary>
    All,
}
