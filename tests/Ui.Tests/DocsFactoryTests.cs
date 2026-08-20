using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for the User-Docs Factory (docs/sonnet-briefs/brief-docs-factory-infrastructure.md §10).
///
/// <para>These assert over the GENERATED ARTEFACTS in <c>docs/user/</c>, not over a live capture.
/// That is deliberate and it is not a weaker test: this project's <c>Ui.Tests</c> deliberately
/// touches no Avalonia runtime API, and standing up a drawing-capable headless platform inside a
/// 5,000-test suite to re-render every figure would trade a 27-second gate for a much longer and
/// much more fragile one. The generator itself already fails hard at capture time on an empty
/// figure, a dropped paint, an unopened popup, an unknown placeholder and an unresolvable
/// cross-link; what these tests add is that nobody can hand-edit or delete the result and have it
/// go unnoticed, and that the catalog and the anchor contract stay in step with the code.</para>
/// </summary>
public class DocsFactoryTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md walking up from this test file).");
        return dir!;
    }

    private static string DocsRoot() => Path.Combine(RepoRoot(), "docs", "user");
    private static string Figures()  => Path.Combine(DocsRoot(), "assets", "figures");
    private static string Symbols()  => Path.Combine(DocsRoot(), "assets", "symbols");

    private static IEnumerable<string> AllSvgs()
        => Directory.EnumerateFiles(DocsRoot(), "*.svg", SearchOption.AllDirectories);

    private static IEnumerable<string> AllPages()
        => Directory.EnumerateFiles(DocsRoot(), "*.html", SearchOption.AllDirectories);

    // ── §10.1 — every catalog figure renders, in both variants ────────────────

    [Fact]
    public void EveryCapturedFigureExistsInBothVariantsAndDrawsSomething()
    {
        foreach (var row in FigureCatalog.Catalog)
            foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
                AssertFigure(Path.Combine(Figures(), UiArtworkGenerator.FileStem(row.Id, variant) + ".svg"));
    }

    [Fact]
    public void EveryToolbarFigureExistsPlainAndIndexedInBothVariants()
    {
        foreach (var row in ToolbarCatalog.Catalog)
            foreach (var stem in new[] { "toolbar-" + row.Id, "toolbar-" + row.Id + "-indexed" })
                foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
                    AssertFigure(Path.Combine(Figures(), UiArtworkGenerator.FileStem(stem, variant) + ".svg"));
    }

    [Fact]
    public void EverySymbolFigureExistsInBothVariantsAndDrawsSomething()
    {
        foreach (var (_, file, _) in SymbolArtworkGenerator.Catalog)
            foreach (var suffix in new[] { ".svg", "-dark.svg" })
                AssertFigure(Path.Combine(Symbols(), file + suffix));
    }

    /// <summary>
    /// A blank box on a page nobody re-reads is the failure mode this exists to catch, so "the file
    /// is there" is not enough — it must contain something that puts ink on the page.
    /// </summary>
    private static void AssertFigure(string path)
    {
        Assert.True(File.Exists(path),
            $"{Path.GetFileName(path)} has not been generated. Run: dotnet run --project tools/DocGen -- --out docs/user");

        var svg = File.ReadAllText(path);
        Assert.True(svg.Length > 0, $"{Path.GetFileName(path)} is empty.");
        Assert.True(SvgLint.HasDrawingElements(svg),
            $"{Path.GetFileName(path)} contains no drawing elements. An empty capture is a bug, not an "
          + "empty figure — the usual cause is a XAML refactor that broke the headless capture.");
    }

    // ── §10.2 — the dropped-paint lint over everything emitted ────────────────

    [Fact]
    public void NoEmittedFigureHasADroppedPaint()
    {
        var failures = new List<string>();
        foreach (var path in AllSvgs())
        {
            var findings = SvgLint.DroppedPaint(File.ReadAllText(path));
            if (findings.Count > 0)
                failures.Add(SvgLint.Explain(Path.GetRelativePath(DocsRoot(), path), findings));
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void NoDocumentationImageIsABitmap()
    {
        var bitmaps = Directory.EnumerateFiles(DocsRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")
            .Select(f => Path.GetRelativePath(DocsRoot(), f))
            .ToList();

        Assert.True(bitmaps.Count == 0,
            "The user documentation is vector only. If a figure can only be produced as a raster, that is "
          + "a finding to report, not a PNG to ship. Found: " + string.Join(", ", bitmaps));
    }

    // ── §10.3 — the anchor contract ───────────────────────────────────────────

    [Fact]
    public void EveryDeepLinkDocLauncherCanEmitResolvesInTheGeneratedHtml()
    {
        var offered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in AllPages())
        {
            string rel = Path.GetRelativePath(DocsRoot(), page).Replace(Path.DirectorySeparatorChar, '/');
            offered.Add(rel);
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(File.ReadAllText(page), @"id=""(?<id>[^""]+)"""))
                offered.Add(rel + "#" + m.Groups["id"].Value);
        }

        var missing = DocAnchors.All()
            .Where(l => !offered.Contains(l.Anchor.Length == 0 ? l.Page : l.Page + "#" + l.Anchor))
            .Select(l => l.ToString())
            .ToList();

        Assert.True(missing.Count == 0,
            "These Help-button destinations do not exist in the generated documentation, so the button "
          + "would open the page at the top and say nothing:\n  " + string.Join("\n  ", missing));
    }

    // ── §10.4 — no unexpanded placeholder survives ────────────────────────────

    [Fact]
    public void NoEmittedPageContainsAnUnexpandedPlaceholder()
    {
        var offenders = new List<string>();
        foreach (var page in AllPages())
        {
            var m = Regex.Match(File.ReadAllText(page), @"\{\{[^}]*\}\}");
            if (m.Success)
                offenders.Add($"{Path.GetRelativePath(DocsRoot(), page)}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "A placeholder reached a shipped page as literal braces:\n  " + string.Join("\n  ", offenders));
    }

    // ── §10.5 — the toolbar manifest and its table agree ──────────────────────

    [Fact]
    public void EveryToolbarManifestIsGeneratedAndItsTableMatchesItRowForRow()
    {
        foreach (var row in ToolbarCatalog.Catalog)
        {
            string json = Path.Combine(Figures(), "toolbar-" + row.Id + ".json");
            Assert.True(File.Exists(json), $"No manifest for the '{row.Id}' toolbar. Regenerate the docs.");

            var entries = ReadManifest(json);
            Assert.NotEmpty(entries);

            var numbered = entries.Where(e => e.Index > 0).ToList();
            Assert.True(numbered.Count > 0, $"The '{row.Id}' toolbar manifest numbers nothing.");

            // Numbering must be 1..N with no gaps: the prose says "3 — Rotate", and a gap makes it wrong.
            Assert.Equal(Enumerable.Range(1, numbered.Count), numbered.Select(e => e.Index));

            var table = DocTables.ToolbarButtons(entries);
            var cells = Regex.Matches(table, @"<tr><td>(?<n>\d+)</td>").Select(m => int.Parse(m.Groups["n"].Value));
            Assert.Equal(numbered.Select(e => e.Index), cells);
        }
    }

    [Fact]
    public void EveryToolbarButtonHasATooltip()
    {
        var silent = new List<string>();
        foreach (var row in ToolbarCatalog.Catalog)
        {
            // NOT "if (!File.Exists) continue": a gate that passes because its input is missing is
            // worse than no gate. If the manifest is absent the docs are stale, and that is the
            // finding.
            string json = Path.Combine(Figures(), "toolbar-" + row.Id + ".json");
            Assert.True(File.Exists(json),
                $"No manifest for the '{row.Id}' toolbar, so this check cannot run. Regenerate the docs.");
            foreach (var e in ReadManifest(json))
                if (e.Index > 0 && e.Kind is "button" or "toggle" && e.Tooltip.Length == 0)
                    silent.Add($"{row.Id} item {e.Index} ({(e.Id.Length == 0 ? e.Icon : e.Id)})");
        }

        Assert.True(silent.Count == 0,
            "A toolbar button with no tooltip is a UI bug, not a blank table cell — the documentation "
          + "cannot say what it does because the application does not:\n  " + string.Join("\n  ", silent));
    }

    private static IReadOnlyList<ToolbarCatalog.Entry> ReadManifest(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("buttons").EnumerateArray()
            .Select(b => new ToolbarCatalog.Entry(
                b.GetProperty("Index").GetInt32(),
                b.GetProperty("Slot").GetInt32(),
                b.GetProperty("Id").GetString() ?? "",
                b.GetProperty("Tooltip").GetString() ?? "",
                b.GetProperty("Icon").GetString() ?? "",
                b.GetProperty("Command").ValueKind == JsonValueKind.Null ? null : b.GetProperty("Command").GetString(),
                b.GetProperty("Kind").GetString() ?? ""))
            .ToList();
    }

    // ── §10.6 — symbol catalog completeness ───────────────────────────────────

    [Fact]
    public void EveryUserPlaceableSymbolKindHasASymbolCatalogRow()
    {
        var documented = SymbolArtworkGenerator.Catalog.Select(r => r.Kind).ToHashSet();
        var missing = Enum.GetValues<SymbolKind>()
            .Where(k => !SymbolArtworkGenerator.NotUserPlaceable.Contains(k))
            .Where(k => !documented.Contains(k))
            .ToList();

        Assert.True(missing.Count == 0,
            "A new component type has no documentation figure. Add a row to "
          + "SymbolArtworkGenerator.Catalog, or add the kind to NotUserPlaceable if a user cannot place "
          + "it — this test exists so that decision is made rather than skipped:\n  "
          + string.Join("\n  ", missing));
    }

    [Fact]
    public void TheOnlyUndocumentedKindsAreTheTwoThatCannotBePlaced()
    {
        // Hard-coded on purpose: widening this list must be a deliberate edit, visible in a diff.
        Assert.Equal(new[] { SymbolKind.Generic, SymbolKind.Unknown },
                     SymbolArtworkGenerator.NotUserPlaceable.ToArray());
    }

    [Fact]
    public void EverySymbolFileStemIsUnique()
    {
        var dupes = SymbolArtworkGenerator.Catalog.GroupBy(r => r.File)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0,
            "Two catalog rows write the same file, so one silently overwrites the other: "
          + string.Join(", ", dupes));
    }

    [Fact]
    public void EveryFigureIdIsUnique()
    {
        var dupes = FigureCatalog.Catalog.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "Duplicate figure id(s): " + string.Join(", ", dupes));
    }

    // ── The stale hand-made FET artwork is gone ───────────────────────────────

    [Fact]
    public void TheHandMadeFetSymbolIsGoneAndNothingStillReferencesIt()
    {
        foreach (var stale in new[] { "fet.svg", "fet-dark.svg" })
            Assert.False(File.Exists(Path.Combine(Symbols(), stale)),
                $"{stale} was hand-made and produced by nothing. The five FET laws now generate their own "
              + "figures; delete it rather than letting a stale drawing outlive the code.");

        foreach (var page in AllPages())
        {
            var text = File.ReadAllText(page);
            Assert.DoesNotContain("symbols/fet.svg", text, StringComparison.Ordinal);
            Assert.DoesNotContain("symbols/fet-dark.svg", text, StringComparison.Ordinal);
        }
    }

    // ── Fonts ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NoEmittedFigureNamesAFontCircuitRfDoesNotShip()
    {
        // The gate this replaces accepted any name that STARTED with a shipped family, which is how
        // "IBM Plex Sans SemiBold" passed while being a family no @font-face declares — the browser
        // skipped it, fell back to the base family at the wrong weight, and every symbol caption
        // shipped Regular instead of SemiBold. An exact match is the only useful check.
        var offenders = new List<string>();
        foreach (var svg in AllSvgs())
        {
            if (Path.GetRelativePath(DocsRoot(), svg).Replace('\\', '/').StartsWith("assets/img/")) continue;

            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(File.ReadAllText(svg), @"font-family=""(?<f>[^""]+)"""))
            {
                string value = m.Groups["f"].Value;
                if (!SvgFontNormalizer.ShippedFamilies.Contains(value, StringComparer.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetRelativePath(DocsRoot(), svg)}: '{value}'");
            }
        }

        Assert.True(offenders.Count == 0,
            "A figure names a font circuitRF does not ship. Either Skia wrote a face name the browser "
          + "cannot resolve, or it substituted a platform font for a glyph our typefaces lack — both "
          + "render wrongly and neither reports itself:\n  " + string.Join("\n  ", offenders.Distinct()));
    }

    [Fact]
    public void EveryFontFamilyAndWeightAnInlinedFigureUsesIsDeclaredInTheStylesheet()
    {
        string fontsDir = Path.Combine(DocsRoot(), "assets", "fonts");
        Assert.True(Directory.Exists(fontsDir), "docs/user/assets/fonts does not exist. Regenerate the docs.");

        string css = File.ReadAllText(Path.Combine(DocsRoot(), "assets", "css", "circuitrf-docs.css"));
        var declared = Regex.Matches(css,
                @"@font-face\s*\{[^}]*font-family:\s*""(?<f>[^""]+)""[^}]*url\(""\.\./fonts/(?<file>[^""]+)""\)[^}]*font-weight:\s*(?<w>\d+)[^}]*font-style:\s*(?<s>\w+)")
            .Select(m => (Family: m.Groups["f"].Value,
                          File:   Uri.UnescapeDataString(m.Groups["file"].Value),
                          Weight: int.Parse(m.Groups["w"].Value),
                          Style:  m.Groups["s"].Value))
            .ToList();

        Assert.NotEmpty(declared);

        foreach (var d in declared)
            Assert.True(File.Exists(Path.Combine(fontsDir, d.File)),
                $"circuitrf-docs.css declares @font-face for '{d.File}' but that file was not extracted.");

        // Only pages matter: a figure is inlined into a page, and only then does the page's CSS have
        // to be able to resolve it.
        var missing = new List<string>();
        foreach (var page in AllPages())
        {
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(File.ReadAllText(page), @"<text[^>]*>"))
            {
                var tag = m.Value;
                var fam = Regex.Match(tag, @"font-family=""(?<f>[^""]+)""");
                if (!fam.Success) continue;

                string family = fam.Groups["f"].Value;
                var wm = Regex.Match(tag, @"font-weight=""(?<w>\d+)""");
                int weight = wm.Success ? int.Parse(wm.Groups["w"].Value) : 400;
                string style = Regex.IsMatch(tag, @"font-style=""italic""") ? "italic" : "normal";

                if (!declared.Any(d => d.Family.Equals(family, StringComparison.OrdinalIgnoreCase)
                                    && d.Weight == weight && d.Style == style))
                    missing.Add($"{Path.GetRelativePath(DocsRoot(), page)}: {family} {weight} {style}");
            }
        }

        Assert.True(missing.Count == 0,
            "An inlined figure asks for a face the stylesheet does not declare, so the browser picks "
          + "the nearest declared weight and the figure renders in a weight it was not drawn in:\n  "
          + string.Join("\n  ", missing.Distinct()));
    }

    [Fact]
    public void TheFontLicencesTravelWithTheFonts()
    {
        string fontsDir = Path.Combine(DocsRoot(), "assets", "fonts");
        foreach (var licence in new[] { "OFL.txt", "DejaVu Fonts License.txt" })
            Assert.True(File.Exists(Path.Combine(fontsDir, licence)),
                $"{licence} must ship beside the extracted typefaces.");
    }

    // ── Generated files say how to regenerate themselves ──────────────────────

    [Fact]
    public void EveryGeneratedFileCarriesARegenerationBanner()
    {
        foreach (var svg in AllSvgs())
        {
            // The brand artwork under assets/img is authored, not generated.
            if (Path.GetRelativePath(DocsRoot(), svg).Replace('\\', '/').StartsWith("assets/img/")) continue;

            var head = File.ReadAllText(svg);
            Assert.True(head.Contains("GENERATED FILE", StringComparison.Ordinal),
                $"{Path.GetRelativePath(DocsRoot(), svg)} carries no banner saying which command regenerates it.");
        }
    }
}
