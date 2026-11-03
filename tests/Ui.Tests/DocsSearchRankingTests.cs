// ================================================================
//  DocsSearchRankingTests.cs — does the documentation search return the right page?
//
//  DocsFactoryTests already gates the search STRUCTURALLY: the index parses, every result points at
//  an anchor that exists, every page is in the index, every page loads both scripts. All of that was
//  green while the search was returning sections about CLICKS for a query of "CLI", because none of
//  it looks at RANKING. This file does, and only that.
//
//  ── It drives the SHIPPED javascript ──────────────────────────────────────────────────────────
//
//  `docs/user/assets/js/docs-search.js` is hand-written and ships as-is, so the only way to gate its
//  ranking is to run it. The alternative — reimplementing the scoring in C# — would gate a copy
//  agreeing with itself and would stay green through any change to the file that actually ships.
//  So the real script is evaluated over the real generated index by node, through the one hook
//  `window.CRF_DOCS_SEARCH_RANK` that exists for this purpose.
//
//  Where node is not installed the tests SKIP WITH A REASON rather than passing silently. A test
//  that quietly asserts nothing is worse than one that says it could not run.
//
//  ── What is asserted ──────────────────────────────────────────────────────────────────────────
//
//  Only that the right PAGE comes first, never a score or an ordering within a page. Scores are an
//  implementation detail and section order inside the right chapter is a matter of taste; which
//  chapter a reader is sent to is not.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>A <see cref="FactAttribute"/> that skips with a reason when node is not on PATH — the
/// repo's <c>FixtureFact</c> shape, applied to a missing tool rather than a missing file.</summary>
internal sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (DocsSearchRankingTests.NodePath() is null)
            Skip = "node is not on PATH; the documentation-search ranking gate needs it to run the "
                 + "shipped docs-search.js.";
    }
}

/// <inheritdoc cref="NodeFactAttribute"/>
internal sealed class NodeTheoryAttribute : TheoryAttribute
{
    public NodeTheoryAttribute()
    {
        if (DocsSearchRankingTests.NodePath() is null)
            Skip = "node is not on PATH; the documentation-search ranking gate needs it to run the "
                 + "shipped docs-search.js.";
    }
}

public class DocsSearchRankingTests(ITestOutputHelper output)
{
    // ── the three reported failures ────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>"command line" must return the chapter called "The Command Line".</b>
    ///
    /// <para>Owner-reported: it returned the Quick Start's "Headless / command line" subsection
    /// instead. Two causes, both in the scoring. The exact-name bonus was a string EQUALITY, so a
    /// leading article defeated it — "the command line" ≠ "command line". And a phrase found in a
    /// section HEADING scored 40 against 25 for the same phrase in a page TITLE, so a subsection of
    /// another chapter outranked the chapter that is named after the query.</para>
    /// </summary>
    [NodeFact]
    public void CommandLine_ReturnsTheCommandLineChapter()
        => AssertTopPage("command line", "reference/cli.html");

    /// <summary>
    /// <b>"CLI" must return the command-line chapter, and must NOT return sections about clicking.</b>
    ///
    /// <para>Owner-reported. <c>indexOf</c> makes every query a substring query, and "cli" is inside
    /// "click", "clicks", "client" and "clip" — words that appear on nearly every page of an
    /// interactive application's manual. The fix is that a term shorter than four characters scores
    /// only where it lands on a whole word or the start of one, and only against names, never
    /// prose.</para>
    /// </summary>
    [NodeFact]
    public void Cli_ReturnsTheCommandLineChapter_AndNothingAboutClicking()
    {
        var hits = Rank("CLI");
        AssertTopPage("CLI", "reference/cli.html");

        foreach (var h in hits)
        {
            string where = $"{h.Slug}#{h.Anchor} — \"{h.Heading}\"";
            Assert.False(h.Heading.Contains("click", StringComparison.OrdinalIgnoreCase),
                $"A search for CLI returned a section headed about clicking: {where}");
        }
    }

    /// <summary>
    /// <b>"CL" must reach the command-line chapter.</b>
    ///
    /// <para>Owner-reported. Two letters cannot be answered by substring matching at all — every
    /// page contains "cl" somewhere, and <c>.clay</c> is a real circuitRF extension that starts with
    /// it. What the reader means is the ACRONYM, so the index carries the initials of each page
    /// title and of each multi-word keyword phrase: "The Command Line" → "cl". That is also what
    /// makes "DD" find the Data Display, which is how people say it.</para>
    /// </summary>
    [NodeFact]
    public void Cl_ReachesTheCommandLineChapter()
        => AssertTopPage("CL", "reference/cli.html");

    // ── the vocabulary a reader actually types ─────────────────────────────────────────────────

    /// <summary>
    /// The point of the <c>keywords:</c> front-matter field, gated: each of these is the correct
    /// industry word for its subject and appears in no chapter TITLE, so before keywords existed
    /// every one of them was answered by whichever page happened to mention it most.
    /// </summary>
    [NodeTheory]
    [InlineData("touchstone",  "reference/npy-export.html")]
    [InlineData("s2p",         "reference/dynamic-symbols.html")]
    [InlineData("ESR",         "reference/derived-metrics.html")]
    [InlineData("SRF",         "reference/derived-metrics.html")]
    [InlineData("dielectric",  "reference/stackup.html")]
    [InlineData("bondwire",    "reference/wbond.html")]
    [InlineData("headless",    "reference/cli.html")]
    [InlineData("verilog-a",   "reference/veriloga.html")]
    [InlineData("PCell",       "reference/pcells.html")]
    // `serve` IS an MCP server, and "MCP" is what anyone looking for it will type; its own chapter
    // is titled "The MCP Server", so the acronym alone has to reach it through the keywords.
    [InlineData("MCP",                     "reference/mcp-server.html")]
    [InlineData("Model Context Protocol",  "reference/mcp-server.html")]
    public void AReadersOwnWordFindsTheChapterAboutIt(string query, string slug)
        => AssertTopPage(query, slug);

    /// <summary>
    /// Naming a chapter returns that chapter. These would all have worked before; they are here
    /// because the fix above CHANGED the weights, and the way to break a search is to tune it for
    /// three reported queries and quietly lose the ones that already worked.
    /// </summary>
    [NodeTheory]
    [InlineData("trace card",       "reference/data-display.html")]
    [InlineData("derived metrics",  "reference/derived-metrics.html")]
    [InlineData("plot types",       "reference/plot-types.html")]
    [InlineData("the workspace",    "reference/workspace.html")]
    [InlineData("symbol editor",    "reference/symbol-editor.html")]
    [InlineData("expressions",      "reference/expressions.html")]
    [InlineData("measurements",     "reference/measurements.html")]
    [InlineData("stackup",          "reference/stackup.html")]
    public void NamingAChapterReturnsThatChapter(string query, string slug)
        => AssertTopPage(query, slug);

    /// <summary>
    /// A short query still has to be ALLOWED to match a real short name. The rule that saved "CLI"
    /// from "clicks" is a rule about where a short term may match, not a ban on short terms — and a
    /// file extension is exactly the short thing people search for.
    /// </summary>
    [NodeTheory]
    [InlineData("cnl",  "reference/netlist.html")]
    [InlineData("clay", "reference/file-formats.html")]
    public void AShortNameIsStillFindable(string query, string slug)
        => AssertTopPage(query, slug);

    /// <summary>
    /// Nothing matches nothing. A query with a term that appears nowhere must return no results
    /// rather than degrading to an OR over the terms that do — which, on a forty-page manual, is
    /// the whole manual.
    /// </summary>
    [NodeFact]
    public void AQueryWithAnUnknownTermReturnsNothing()
        => Assert.Empty(Rank("stackup zzzznotaword"));

    /// <summary>
    /// A query that names a page — its title, or a phrase its keywords declare — opens the page at
    /// its TOP. Every section of a page shares the page's keywords and title, so the tie used to go
    /// to whichever subsection repeated the word most: "sensitivity" opened mid-chapter, at the
    /// section comparing it with Tuning, and "monte carlo" at the Yield chapter's list of modes.
    /// </summary>
    [NodeTheory]
    [InlineData("sense",                "reference/sensitivity.html")]
    [InlineData("Sensitivity",          "reference/sensitivity.html")]
    [InlineData("sensitivity analysis", "reference/sensitivity.html")]
    [InlineData("yield",                "reference/yield.html")]
    [InlineData("Monte Carlo",          "reference/yield.html")]
    [InlineData("montecarlo",           "reference/yield.html")]
    [InlineData("tuning",               "reference/tuning.html")]
    [InlineData("MCP server",           "reference/mcp-server.html")]
    // A heading that merely CONTAINS the word does not take the landing.
    [InlineData("dielectric",           "reference/stackup.html")]
    [InlineData("PCell",                "reference/pcells.html")]
    [InlineData("headless",             "reference/cli.html")]
    // A page that declares the query beats another page's section headed with it: the Simulations
    // chapter's two-line "Optimizer" section only points to this one.
    [InlineData("optimizer",            "reference/optimization.html")]
    public void NamingAPageOpensItsTop(string query, string slug)
    {
        AssertTopPage(query, slug);
        var top = Rank(query)[0];
        Assert.True(top.Anchor.Length == 0,
            $"A search for \"{query}\" should open {slug} at its top, but landed on #{top.Anchor} (\"{top.Heading}\").");
    }

    /// <summary>
    /// The page-top rule stops at a subsection the query names: "corners" is a keyword of the Yield
    /// chapter and the reader still wants its Corners section; "DOE" spells the initials of its
    /// "Design of experiments" heading, and the chapter's keywords declare both spellings; a
    /// heading's leading clause names it too ("Hierarchy: putting one schematic inside another").
    /// </summary>
    [NodeTheory]
    [InlineData("corners",   "reference/yield.html",            "corners")]
    [InlineData("DOE",       "reference/yield.html",            "doe")]
    [InlineData("hierarchy", "reference/schematic-editor.html", "hierarchy")]
    public void ASubsectionNamedByTheQueryStillWins(string query, string slug, string anchor)
    {
        var top = Rank(query)[0];
        Assert.Equal(slug, top.Slug);
        Assert.Equal(anchor, top.Anchor);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record Hit(string Slug, string Anchor, string Heading, string Title, double Score);

    private void AssertTopPage(string query, string expectedSlug)
    {
        var hits = Rank(query);
        Assert.NotEmpty(hits);

        foreach (var h in hits.Take(5))
            output.WriteLine($"  {h.Score,7:F1}  {h.Slug}#{h.Anchor}  {h.Heading}");

        Assert.True(hits[0].Slug == expectedSlug,
            $"A search for \"{query}\" should land on {expectedSlug}, but the top hit was "
          + $"{hits[0].Slug}#{hits[0].Anchor} (\"{hits[0].Heading}\").");
    }

    /// <summary>
    /// Runs the query through the SHIPPED <c>docs-search.js</c> against the SHIPPED
    /// <c>search-index.js</c>, in node, and returns what a reader would be offered.
    /// </summary>
    private static IReadOnlyList<Hit> Rank(string query)
    {
        string docs = Path.Combine(RepoRoot(), "docs", "user", "assets", "js");
        string index = Path.Combine(docs, "search-index.js");
        string ui    = Path.Combine(docs, "docs-search.js");

        Assert.True(File.Exists(index), $"{index} is missing — run tools/DocGen.");
        Assert.True(File.Exists(ui),    $"{ui} is missing; it is hand-written, not generated.");

        // The script is an IIFE that wires itself to DOM nodes, so it gets just enough of a window
        // and a document to reach its own end, where it publishes the ranking hook.
        string driver = $$"""
            global.window = {};
            global.document = { querySelectorAll: function () { return []; }, addEventListener: function () {} };
            var fs = require('fs');
            new Function(fs.readFileSync(process.argv[2], 'utf8'))();
            new Function(fs.readFileSync(process.argv[3], 'utf8'))();
            if (typeof global.window.CRF_DOCS_SEARCH_RANK !== 'function') {
              console.error('docs-search.js did not publish window.CRF_DOCS_SEARCH_RANK');
              process.exit(2);
            }
            process.stdout.write(JSON.stringify(global.window.CRF_DOCS_SEARCH_RANK(process.argv[4])));
            """;

        string script = Path.Combine(Path.GetTempPath(), $"crf-docs-rank-{Guid.NewGuid():N}.js");
        File.WriteAllText(script, driver);
        try
        {
            var psi = new ProcessStartInfo(NodePath()!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(index);
            psi.ArgumentList.Add(ui);
            psi.ArgumentList.Add(query);

            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            string stdout = outTask.GetAwaiter().GetResult();
            Assert.True(proc.ExitCode == 0,
                $"node failed running the shipped search: {errTask.GetAwaiter().GetResult()}");

            using var doc = JsonDocument.Parse(stdout);
            return [.. doc.RootElement.EnumerateArray().Select(e => new Hit(
                e.GetProperty("slug").GetString() ?? "",
                e.GetProperty("anchor").GetString() ?? "",
                e.GetProperty("heading").GetString() ?? "",
                e.GetProperty("title").GetString() ?? "",
                e.GetProperty("score").GetDouble()))];
        }
        finally { try { File.Delete(script); } catch { /* a temp file is not a failure */ } }
    }

    /// <summary>node's full path, or null when it is not installed.</summary>
    internal static string? NodePath()
    {
        string exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate)) return candidate;
        }
        // Homebrew on Apple silicon is not always on a test runner's inherited PATH.
        foreach (var known in new[] { "/opt/homebrew/bin/node", "/usr/local/bin/node", "/usr/bin/node" })
            if (File.Exists(known)) return known;
        return null;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
