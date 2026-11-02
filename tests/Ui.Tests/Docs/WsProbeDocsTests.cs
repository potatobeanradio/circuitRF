using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;
using RxMatch = System.Text.RegularExpressions.Match;
using RxGroup = System.Text.RegularExpressions.Group;

namespace CircuitRF.Ui.Tests.Docs;

/// <summary>
/// The gates of <c>brief-wsprobe-7-user-docs-and-examples.md</c> §5 — the WSProbe chapter, its
/// citations, its figures and the designs they are drawn from.
///
/// <para>These assert over the AUTHORED source and the GENERATED artefacts, the way
/// <see cref="DocsFactoryTests"/> does, plus two that go further because the brief asks them to:
/// one reads real geometry out of a rendered figure, and one runs the example designs through the
/// engine and compares the answers against the numbers the page prints.</para>
/// </summary>
public class WsProbeDocsTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root walking up from this test file.");
        return dir!;
    }

    private static string DocsSrc()  => Path.Combine(RepoRoot(), "docs", "user", "src");
    private static string DocsOut()  => Path.Combine(RepoRoot(), "docs", "user");
    private static string PageSrc()  => Path.Combine(DocsSrc(), "reference", "wsprobe.md");
    private static string PageHtml() => Path.Combine(DocsOut(), "reference", "wsprobe.html");

    private const string Regenerate =
        "Run: dotnet run --project tools/DocGen -- --out docs/user";

    // ── The chapter exists, and the site knows about it ───────────────────────

    /// <summary>
    /// The page is authored, generated, listed in the ONE reading order, and therefore in the site
    /// contents and the Previous/Next chain — all four of which come off <c>_nav.txt</c>, which is
    /// why listing it there is the whole of the check.
    /// </summary>
    [Fact]
    public void TheWsProbeChapterIsAuthoredGeneratedAndInTheReadingOrder()
    {
        Assert.True(File.Exists(PageSrc()),  "docs/user/src/reference/wsprobe.md is missing.");
        Assert.True(File.Exists(PageHtml()), "reference/wsprobe.html has not been generated. " + Regenerate);

        var nav = File.ReadAllLines(Path.Combine(DocsSrc(), "_nav.txt"));
        int at = Array.FindIndex(nav, l => l.TrimStart().StartsWith("reference/wsprobe.html", StringComparison.Ordinal));
        Assert.True(at >= 0, "reference/wsprobe.html is not in _nav.txt, so it is in no section, no "
                           + "contents list and no Previous/Next chain — and the orphan check fails.");

        int derived = Array.FindIndex(nav, l => l.TrimStart().StartsWith("reference/derived-metrics.html", StringComparison.Ordinal));
        Assert.True(derived >= 0 && at == derived + 1,
            "The WSProbe chapter belongs directly after Derived Metrics: the two pages are the two "
          + "halves of the same subject and each points at the other.");

        // The generated contents page is built from that same file, so it must carry the row.
        var index = File.ReadAllText(Path.Combine(DocsOut(), "reference", "index.html"));
        Assert.Contains("wsprobe.html", index);
    }

    /// <summary>
    /// <b>Every link the chapter makes resolves.</b> An anchor into its own body, and an anchor into
    /// another chapter — a heading renamed on either side leaves a link that scrolls nowhere, and a
    /// reader has no way to tell that from a page that simply starts at the top.
    /// </summary>
    [Fact]
    public void EveryLinkTheWsProbeChapterMakesResolves()
    {
        string html = File.ReadAllText(PageHtml());
        var ids = new HashSet<string>(
            Regex.Matches(html, @"\sid=""([^""]+)""").Select(m => m.Groups[1].Value), StringComparer.Ordinal);

        var broken = new List<string>();
        foreach (RxMatch m in Regex.Matches(html, @"href=""([^""]+)"""))
        {
            string href = m.Groups[1].Value;
            if (href.StartsWith("http", StringComparison.Ordinal) || href.StartsWith("mailto:", StringComparison.Ordinal))
                continue;

            string page = href, frag = "";
            int hash = href.IndexOf('#');
            if (hash >= 0) { page = href[..hash]; frag = href[(hash + 1)..]; }

            if (page.Length == 0)
            {
                if (frag.Length > 0 && !ids.Contains(frag)) broken.Add(href);
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(DocsOut(), "reference", page));
            if (!File.Exists(target)) { broken.Add(href); continue; }
            if (frag.Length == 0) continue;

            string other = File.ReadAllText(target);
            if (!Regex.IsMatch(other, @"\sid=""" + Regex.Escape(frag) + @"""")) broken.Add(href);
        }

        Assert.True(broken.Count == 0,
            "These links on the WSProbe chapter resolve to nothing: " + string.Join(", ", broken)
          + ". " + Regenerate);
    }

    // ── Every equation on the page is one the register lists ──────────────────

    /// <summary>
    /// <b>The citation gate.</b> Every <c>Eq. n</c>, <c>M-Eq. n</c>, <c>E-Eq. n</c> and proxy display
    /// the chapter cites is compared against the equation register of
    /// <c>brief-wsprobe-0-overview.md</c> §4.
    ///
    /// <para>This is the way a formula gets onto a user-facing page that nobody checked: the register
    /// is the list of equations somebody has either re-derived or verified numerically, and a
    /// citation outside it is a claim with no provenance. It catches a mis-typed number as readily —
    /// <c>Eq. 84</c> for <c>Eq. 48</c> is one keystroke and points at a display the register marks
    /// as self-contradictory.</para>
    /// </summary>
    [Fact]
    public void EveryEquationTheChapterCitesIsInTheRegister()
    {
        var (bare, m, e, proxies) = Register();
        string page = File.ReadAllText(PageSrc());

        var unlisted = new List<string>();

        foreach (int n in CitedNumbers(page, @"(?<![ME]-)\bEq\.\s*([0-9/,\u2013\-\s]*[0-9])"))
            if (!bare.Contains(n)) unlisted.Add($"Eq. {n}");

        foreach (int n in CitedNumbers(page, @"\bM-Eq\.\s*([0-9/,\u2013\-\s]*[0-9])"))
            if (!m.Contains(n)) unlisted.Add($"M-Eq. {n}");

        foreach (int n in CitedNumbers(page, @"\bE-Eq\.\s*([0-9/,\u2013\-\s]*[0-9])"))
            if (!e.Contains(n)) unlisted.Add($"E-Eq. {n}");

        foreach (RxMatch match in Regex.Matches(page, @"\bM-(?:rY|iY|rH|iH)\b"))
            if (!proxies.Contains(match.Value)) unlisted.Add(match.Value);

        Assert.True(unlisted.Count == 0,
            "The WSProbe chapter cites equations that are NOT in the register of "
          + "docs/briefs/brief-wsprobe-0-overview.md §4, so nothing in this repository has "
          + "checked them: " + string.Join(", ", unlisted.Distinct()) + ".");
    }

    /// <summary>
    /// <b>The two printed equations the register marks as wrong are cited only in corrected form,
    /// and the page says so.</b> Both are invisible on a reciprocal network and order 1 on a
    /// non-reciprocal one, which is exactly the kind of error a reader cannot catch by inspection.
    /// </summary>
    [Fact]
    public void TheChapterSaysWhichPrintedEquationsItCorrects()
    {
        string page = File.ReadAllText(PageSrc());
        Assert.Contains("E-Eq. 11", page);
        Assert.Contains("M-Eq. 5", page);
        Assert.Contains("corrected form", page);
    }

    /// <summary>The register's three number sets and its four proxy display names.</summary>
    private static (HashSet<int> Bare, HashSet<int> M, HashSet<int> E, HashSet<string> Proxies) Register()
    {
        string overview = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs", "briefs", "brief-wsprobe-0-overview.md"));

        int start = overview.IndexOf("## 4. The equation register", StringComparison.Ordinal);
        Assert.True(start > 0, "brief-wsprobe-0-overview.md no longer has a §4 equation register.");
        int end = overview.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
        string table = end > 0 ? overview[start..end] : overview[start..];

        HashSet<int> bare = [], m = [], e = [];
        HashSet<string> proxies = new(StringComparer.Ordinal);

        foreach (string line in table.Split('\n'))
        {
            if (!line.StartsWith("|", StringComparison.Ordinal)) continue;
            string cell = line.Split('|', StringSplitOptions.None).ElementAtOrDefault(1)?.Trim() ?? "";
            if (cell.Length == 0 || cell.StartsWith("---", StringComparison.Ordinal)) continue;

            foreach (RxMatch p in Regex.Matches(cell, @"M-(?:rY|iY|rH|iH)")) proxies.Add(p.Value);

            var target = cell.StartsWith("M-Eq.", StringComparison.Ordinal) ? m
                       : cell.StartsWith("E-Eq.", StringComparison.Ordinal) ? e
                       : cell.StartsWith("E-App", StringComparison.Ordinal) ? null
                       : Regex.IsMatch(cell, @"^[0-9]") ? bare
                       : null;
            if (target is null) continue;

            foreach (int n in Expand(cell)) target.Add(n);
        }

        Assert.True(bare.Count > 100 && m.Count >= 10 && e.Count >= 12 && proxies.Count == 4,
            $"The register parsed as {bare.Count} bare, {m.Count} M-Eq., {e.Count} E-Eq. and "
          + $"{proxies.Count} proxies, which is too few — the table's shape has changed and this "
          + "gate would silently accept anything.");
        return (bare, m, e, proxies);
    }

    /// <summary>Every equation number a citation spec names — "44", "137–139", "44/48", "5, 8".</summary>
    private static IEnumerable<int> Expand(string spec)
    {
        foreach (RxMatch r in Regex.Matches(spec, @"([0-9]+)\s*[\u2013\-]\s*([0-9]+)"))
            for (int i = int.Parse(r.Groups[1].Value); i <= int.Parse(r.Groups[2].Value); i++) yield return i;

        string singles = Regex.Replace(spec, @"[0-9]+\s*[\u2013\-]\s*[0-9]+", " ");
        foreach (RxMatch s in Regex.Matches(singles, @"[0-9]+")) yield return int.Parse(s.Value);
    }

    private static IEnumerable<int> CitedNumbers(string text, string pattern)
        => Regex.Matches(text, pattern).SelectMany(x => Expand(x.Groups[1].Value)).Distinct();

    // ── The two greps the repository is under ─────────────────────────────────

    /// <summary>
    /// <b>No commercial simulator, kit or vendor name reaches the user documentation.</b> The
    /// reference document behind this chapter names one on nearly every page, which is what makes
    /// this worth a test rather than a habit.
    ///
    /// <para><b>The list is a set of DIGESTS, and that is not obfuscation for its own sake.</b>
    /// Repo-root <c>CLAUDE.md</c> §Commercial Vendor References forbids those names anywhere in this
    /// repository — <i>"not even as a glossary of names to filter out"</i> — so a gate written the
    /// obvious way would be the very thing it exists to prevent, and would be found by the same grep
    /// anyone runs before a commit. Each candidate word and word-pair of the prose is hashed and
    /// looked up instead; nothing readable is stored. A hit reports the FILE and LINE, which is all
    /// an author needs to see what they wrote.</para>
    ///
    /// <para>Adding one: hash the lowercase name with SHA-256 and keep the first 16 hex characters.
    /// Multi-word names are matched as adjacent word pairs, so a two-word product name works and a
    /// three-word one needs its distinctive pair.</para>
    /// </summary>
    [Fact]
    public void NoVendorNameReachesTheUserDocumentationSource()
    {
        var banned = BannedDigests;
        var hits = new List<string>();
        foreach (string f in Directory.EnumerateFiles(DocsSrc(), "*.md", SearchOption.AllDirectories))
            ScanForBanned(f, banned, hits);

        Assert.True(hits.Count == 0,
            "A commercial vendor, simulator or kit name reached docs/user/src at: "
          + string.Join(", ", hits.Distinct())
          + ". Repo-root CLAUDE.md forbids those names anywhere in this repository, which is also "
          + "why this gate cannot tell you which one it found.");
    }

    /// <summary>The digests <see cref="NoVendorNameReachesTheUserDocumentationSource"/> checks — shared with
    /// brief-em3d-53's generic material library, whose Source citations must name no manufacturer.</summary>
    internal static readonly HashSet<string> BannedDigests = new(StringComparer.Ordinal)
    {
            "f0752d059804986d", "03b548bd99277bf0", "abf36cd32bcc04eb", "fee6fd0cc1707a18",
            "df179ecdb694113f", "b0a29cba258364f6", "4f6b9e1c24d93d9a", "dddc2540655a9e86",
            "921bcc89bf6d54bd", "e214fdf90c1468b9", "e4472d0cd5e7c4ea", "0a1bab7945d5c6a3",
            "db87f26b104e7544", "699949a69f87ff18", "b5a28fbec19341f0", "0cdd2b0bb11909d9",
            "a2309cf1d14f05b8", "8e89759c83aa90a9", "446be308664e1964", "256b21fc60dc82ff",
            "85f697115c7a2a87", "d53ad95e70babd32", "f562e3c126d4ac7a", "ccc55dcbfdee7146",
            "872f53a234135a2c", "8c7ad921eb8dd369", "d597e69e0f4dccfc", "892fe341c689e4fe",
            "bd761ee8c7fc09c6",
    };

    private static void ScanForBanned(string f, HashSet<string> banned, List<string> hits)
    {
        var lines = File.ReadAllLines(f);
        for (int i = 0; i < lines.Length; i++)
        {
            var words = Regex.Matches(lines[i].ToLowerInvariant(), @"[a-z][a-z0-9]*")
                             .Select(w => w.Value).ToList();
            for (int w = 0; w < words.Count; w++)
            {
                if (banned.Contains(Digest(words[w])) ||
                    (w + 1 < words.Count && banned.Contains(Digest(words[w] + " " + words[w + 1]))))
                {
                    hits.Add($"{Path.GetFileName(f)}:{i + 1}");
                    break;
                }
            }
        }
    }

    internal static string Digest(string s)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
               System.Text.Encoding.UTF8.GetBytes(s)))[..16];

    /// <summary>
    /// <b>No path out of somebody's home directory is committed.</b> The documentation is public and
    /// the machine it was generated on is not.
    /// </summary>
    [Fact]
    public void NoDocumentationFileCarriesAPathOutOfAHomeDirectory()
    {
        // "/home/you" and "/Users/you" are the documentation's own anonymised example paths, which
        // is the SHAPE a reader needs and carries nobody's name. Anything else is a real one.
        var rx = new Regex(@"(?:/Users/|/home/|C:\\Users\\)(?!you\b|user\b|username\b|me\b)[A-Za-z0-9._-]+");
        var hits = new List<string>();

        foreach (string f in Directory.EnumerateFiles(DocsOut(), "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(f) is ".ttf" or ".woff" or ".woff2" or ".png" or ".pdf") continue;
            var m = rx.Match(File.ReadAllText(f));
            if (m.Success) hits.Add($"{Path.GetFileName(f)}: {m.Value}");
        }

        Assert.True(hits.Count == 0,
            "These generated documentation files carry an absolute path out of a home directory: "
          + string.Join(", ", hits.Take(8)));
    }

    // ── The figures ───────────────────────────────────────────────────────────

    /// <summary>Every figure the chapter cites is a catalog row, and every one it renders exists.</summary>
    [Fact]
    public void EveryFigureTheChapterCitesIsInTheCatalogAndHasBeenRendered()
    {
        var ids = new HashSet<string>(FigureCatalog.Catalog.Select(r => r.Id), StringComparer.Ordinal);
        string page = File.ReadAllText(PageSrc());

        foreach (RxMatch m in Regex.Matches(page, @"\{\{ui:\s*([a-z0-9-]+)\s*\}\}"))
        {
            string id = m.Groups[1].Value;
            Assert.True(ids.Contains(id),
                $"The WSProbe chapter cites the figure '{id}', which is in no FigureCatalog row.");
            foreach (string suffix in new[] { ".svg", "-dark.svg" })
                Assert.True(File.Exists(Path.Combine(DocsOut(), "assets", "figures", id + suffix)),
                    $"{id}{suffix} has not been generated. " + Regenerate + " --figure " + id);
        }
    }

    /// <summary>
    /// <b>The resonator figure shows the signature on ONE locus and not the other</b>, read off the
    /// rendered picture rather than off the data behind it.
    ///
    /// <para>The whole point of §4 is that a zero can mask the pole in <c>1/H0</c> or in <c>1/Y0</c>
    /// but never in both, and the figure is the evidence. A figure that drew the right numbers
    /// through a broken transform, or drew one trace twice, would pass every other check here and
    /// teach the reader the opposite of the truth.</para>
    ///
    /// <para><b>The picture carries its own coordinate system.</b> A polar plot holding a WSProbe
    /// trace draws small reference crosses at the origin and at +1 (<c>PlotRenderer</c>), which is
    /// exactly the two points needed to read the complex plane off the canvas: the origin, and the
    /// scale. So the assertion is geometric and not a pixel comparison — and it recovers the
    /// crossing's own value, <c>1/Y0 = ZG + ZL = R1 + RS = −10 Ω</c> at resonance, from the drawn
    /// path.</para>
    ///
    /// <para><b>A PLOT EACH since 2026-09-08, and the reading is per frame for that reason.</b> The
    /// two loci are reciprocal quantities — <c>1/H0</c> is an admittance, <c>1/Y0</c> an impedance —
    /// so on one shared radius the smaller of them is a dot on the origin (measured: ~0.05 S against
    /// 10–30 Ω, a ratio near 600). Each locus therefore has its own square, its own origin cross and
    /// its own scale, and the <c>+1</c> mark is simply outside the frame on the admittance plot,
    /// which is what an off-window reference point should do.</para>
    /// </summary>
    [Fact]
    public void TheResonatorFigureCrossesTheNegativeRealAxisOnceBetweenItsTwoLoci()
    {
        string svg = File.ReadAllText(
            Path.Combine(DocsOut(), "assets", "figures", "wsprobe-resonator-polar.svg"));

        // Skia's SVG writer hoists repeated geometry into <path id="…"> and re-draws it through
        // <use xlink:href="#…" transform="translate(…)">. Two plots carrying the same 8-unit cross
        // is exactly that case, so a reader of this file has to resolve the indirection or it sees
        // one mark where there are three.
        var defs = Regex.Matches(svg, @"<path id=""([^""]+)""[^>]*d=""([^""]+)""")
                        .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        // Every horizontal 8-unit stroke, as (frame, centre) — the frame is the plot it belongs to.
        var marks = new List<(double Fx, double Fy, double X, double Y)>();
        foreach (RxMatch m in Regex.Matches(svg, @"<(?:path|use)([^>]*)/>"))
        {
            string attrs = m.Groups[1].Value;
            // A <path id="…"> is the DEFINITION inside <defs> and is never itself drawn — counting it
            // adds a phantom frame at the origin.
            if (Regex.IsMatch(attrs, @"\bid=""")) continue;
            string? d = Regex.Match(attrs, @"\bd=""([^""]+)""") is { Success: true } dm
                ? dm.Groups[1].Value
                : Regex.Match(attrs, @"xlink:href=""#([^""]+)""") is { Success: true } hm
                    && defs.TryGetValue(hm.Groups[1].Value, out string? href) ? href : null;
            if (d is null) continue;

            var seg = Regex.Match(d, @"^M([-\d.]+) ([-\d.]+)L([-\d.]+) ([-\d.]+)$");
            if (!seg.Success) continue;
            double[] v = [.. seg.Groups.Cast<RxGroup>().Skip(1).Select(g => double.Parse(g.Value, CultureInfo.InvariantCulture))];
            if (Math.Abs(v[3] - v[1]) > 0.01 || Math.Abs(v[2] - v[0]) is <= 7 or >= 9) continue;

            var (fx, fy) = FrameOf(attrs);
            marks.Add((fx, fy, (v[0] + v[2]) / 2, v[1]));
        }

        // One frame per plot, and within a frame the ORIGIN is the leftmost mark: the other, when it
        // is there at all, is +1 and is to its right.
        var frames = marks.GroupBy(k => (k.Fx, k.Fy))
                          .ToDictionary(g => g.Key, g => g.OrderBy(k => k.X).ToList());

        Assert.True(frames.Count == 2,
            $"The resonator figure should be two polar plots, each with its own origin cross; "
          + $"{frames.Count} frame(s) carrying one were found.");

        // Every drawn locus: a path of at least 100 line segments, read in its own frame.
        var crossings = new List<(double Re, bool HasScale)>();
        int loci = 0;
        foreach (RxMatch m in Regex.Matches(svg, @"<path([^>]*)d=""(M[^""]{500,})"""))
        {
            string d = m.Groups[2].Value;
            if (d.Count(c => c == 'L') < 100) continue;
            loci++;

            var (fx, fy) = FrameOf(m.Groups[1].Value);
            Assert.True(frames.TryGetValue((fx, fy), out var frameMarks),
                $"A locus is drawn in a frame at ({fx}, {fy}) that carries no origin cross, so its "
              + "complex plane cannot be read off the picture.");

            var origin = frameMarks![0];
            double? scale = frameMarks.Count > 1 ? frameMarks[1].X - origin.X : null;

            var pts = Regex.Matches(d, @"[ML]([-\d.]+) ([-\d.]+)")
                           .Select(p => (X: double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture),
                                         Y: double.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture)))
                           .ToList();

            for (int i = 1; i < pts.Count; i++)
            {
                double ia = pts[i - 1].Y - origin.Y, ib = pts[i].Y - origin.Y;
                if (ia == 0 && ib == 0) continue;
                if (ia < 0 == ib < 0) continue;                       // no sign change: no crossing
                double f  = ia / (ia - ib);
                double xc = pts[i - 1].X + (pts[i].X - pts[i - 1].X) * f;
                if (xc < origin.X)
                    crossings.Add((scale is { } s ? (xc - origin.X) / s : double.NaN, scale is not null));
            }
        }

        Assert.True(loci == 2, $"The resonator figure should draw exactly two loci; it draws {loci}.");
        Assert.True(crossings.Count == 1,
            "Exactly one of the two loci may cross the negative real axis — that is the whole lesson "
          + "of the figure (a zero can mask the pole in one driving-point function but never in "
          + $"both). {crossings.Count} crossing(s) were drawn.");

        Assert.True(crossings[0].HasScale,
            "The locus that crosses is the impedance one, and its plot must carry the +1 mark as "
          + "well as the origin — without it the crossing has no scale to be read against.");
        Assert.True(Math.Abs(crossings[0].Re + 10.0) < 0.5,
            $"The crossing is drawn at Re = {crossings[0].Re:0.##}, and the closed form puts it at "
          + "1/Y0 = ZG + ZL = R1 + RS = -20 + 10 = -10 Ohm. The figure is drawing something else.");

        static (double X, double Y) FrameOf(string attrs)
        {
            var t = Regex.Match(attrs, @"translate\(([-\d.]+) ([-\d.]+)\)");
            return t.Success
                ? (double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture),
                   double.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture))
                : (0, 0);
        }
    }

    // ── The designs, run ──────────────────────────────────────────────────────

    /// <summary>Every example design the chapter's figures are taken from is where they read it.</summary>
    [Fact]
    public void EveryExampleDesignTheFiguresRunIsCommitted()
    {
        string[] designs =
        [
            "testdata/wsprobe/series_resonator.cnl",
            "testdata/wsprobe/parallel_resonator.cnl",
            "testdata/wsprobe/margin_split_resonator.cnl",
            "testdata/wsprobe/two_stage_terms.cnl",
            "testdata/wsprobe/hb_varactor_divider.cnl",
            "testdata/ndf/ohtomo_type_a_ndf.cnl",
            "testdata/ndf/hidden_pole_two_port.cnl",
        ];
        foreach (string d in designs)
            Assert.True(File.Exists(Path.Combine(RepoRoot(), d.Replace('/', Path.DirectorySeparatorChar))),
                $"The WSProbe chapter's figures read {d}, which is not committed. "
              + "DocWsProbeFixtures.Designs is the catalogue.");
    }

    /// <summary>
    /// <b>The numbers the chapter prints are the numbers the engine produces.</b> A documentation
    /// page that quotes a measurement is making a claim, and the claim goes stale silently: nothing
    /// else in this repository re-reads the prose.
    /// </summary>
    [Fact]
    public void TheMarginNumbersTheChapterPrintsAreTheOnesTheEngineProduces()
    {
        var ds = RunDesign("margin", Path.Combine("testdata", "wsprobe", "margin_split_resonator.cnl"));

        var smY0 = Cube(ds, "SM_Y0:P");
        var smH0 = Cube(ds, "SM_H0:P");
        var freqs = smY0.Axes[0].Values;
        var (yMin, yHz) = Minimum(smY0.RealValues, freqs);
        var (hMin, hHz) = Minimum(smH0.RealValues, freqs);

        string page = File.ReadAllText(PageSrc());

        AssertQuoted(page, "−18.06 dB at 1.5913 GHz", 20 * Math.Log10(yMin), -18.06, yHz / 1e9, 1.5913);
        AssertQuoted(page, "−19.85 dB at 1.7337 GHz", 20 * Math.Log10(hMin), -19.85, hHz / 1e9, 1.7337);

        Assert.Contains("1.5915 GHz", page);           // f0 of the split resonator, from L1 and C1
        Assert.True(Math.Abs(1.0 / (2 * Math.PI * Math.Sqrt(1e-9 * 10e-12)) / 1e9 - 1.5915) < 5e-4);
    }

    /// <summary>
    /// <b>"K is not enough" reads 2 right-half-plane poles.</b> The chapter's NDF section rests
    /// entirely on that number, and on the two-port metrics of the same circuit saying nothing.
    /// </summary>
    [Fact]
    public void TheKIsNotEnoughExampleReadsTwoRightHalfPlanePoles()
    {
        var ds = RunDesign("ndfk", Path.Combine("testdata", "ndf", "hidden_pole_two_port.cnl"));

        Assert.Equal(2, (int)Cube(ds, "NDF_poles").RealValues[0]);
        Assert.Contains("The NDF reads 2 right-half-plane poles", File.ReadAllText(PageSrc()));
    }

    private static void AssertQuoted(string page, string quoted,
                                     double db, double quotedDb, double ghz, double quotedGhz)
    {
        Assert.True(page.Contains(quoted, StringComparison.Ordinal),
            $"The chapter no longer prints \"{quoted}\", so this gate is checking nothing.");
        Assert.True(Math.Abs(db - quotedDb) < 0.01,
            $"The chapter prints {quotedDb:0.00} dB; the engine produced {db:0.00} dB.");
        Assert.True(Math.Abs(ghz - quotedGhz) < 5e-4,
            $"The chapter prints {quotedGhz:0.0000} GHz; the engine produced {ghz:0.0000} GHz.");
    }

    /// <summary>
    /// One cube by name, wherever the run put it. A run's cubes live in the ANALYSIS's own group
    /// (<c>SP1</c>), and the bare name resolves only in the default one — so a test that asked for
    /// the bare name would fail with "no cube named …" and read as a missing result rather than as
    /// a qualified name.
    /// </summary>
    private static DataCube Cube(DataSet ds, string name)
    {
        if (ds.Contains(name)) return ds[name];
        foreach (string g in ds.Groups)
            if (ds.Contains($"{g}.{name}")) return ds[$"{g}.{name}"];

        throw new Xunit.Sdk.XunitException(
            $"The run produced no cube called '{name}' in any group ({string.Join(", ", ds.Groups)}), "
          + "so the claim the WSProbe chapter makes about it has nothing behind it.");
    }

    private static (double Min, double Hz) Minimum(IReadOnlyList<double> v, IReadOnlyList<double> f)
    {
        int at = 0;
        for (int i = 1; i < v.Count; i++) if (v[i] < v[at]) at = i;
        return (v[at], f[at]);
    }

    /// <summary>Run one committed design through the same two calls the Run button makes.</summary>
    private static DataSet RunDesign(string key, string relative)
    {
        string dir = Path.Combine(Path.GetTempPath(), "circuitrf-wsp7-docs-" + key + "-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string cnl = Path.Combine(dir, "netlist.cnl");
        File.Copy(Path.Combine(RepoRoot(), relative), cnl, overwrite: true);

        var plan = SchematicRunService.Prepare(cnl, dir);
        Assert.True(plan.Status == RunStatus.Success, $"{relative} did not plan: {plan.StatusMessage}");

        var result = SchematicRunService.Execute(plan, new RunControl());
        Assert.True(result.Status == RunStatus.Success && result.GroupedResults is not null,
            $"{relative} did not run: {result.StatusMessage}");
        return result.GroupedResults!;
    }
}
