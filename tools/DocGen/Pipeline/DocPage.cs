using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// One authored source page: YAML front-matter plus Markdown body.
///
/// <para><b>Prose lives here, not in C#.</b> The generator owns layout, chrome, figures, tables and
/// cross-links — everything a human cannot keep from drifting. It does not own the words. A
/// generator that owned prose would mean editing a C# string literal to fix a sentence, which is
/// worse than the hand-written HTML this replaces, and would lock the docs to whoever can build the
/// solution.</para>
///
/// <para>The front-matter parser is deliberately tiny — five known keys, one list — rather than a
/// YAML library. Anything it does not recognise is an error, so a typo'd key cannot be silently
/// ignored and leave a page with the wrong breadcrumb.</para>
/// </summary>
public sealed class DocPage
{
    /// <summary>Page title: the browser tab and the H1 if the body has none.</summary>
    public required string Title { get; init; }

    /// <summary>"page" (HTML) or "slides" (a landscape PDF deck).</summary>
    public string Kind { get; init; } = "page";

    /// <summary>
    /// A <c>kind: slides</c> page's deck id — what <c>--deck &lt;id&gt;</c> selects on the command
    /// line (<c>overview</c>, <c>new-user</c>, <c>quick-start</c>, <c>reference</c>).
    ///
    /// <para>Deliberately NOT derived from the file name. The file name is the PDF's name and a
    /// reader sees it; the id is what a build script types, and the two want to change independently
    /// (<c>circuitrf-new-user.pdf</c> is the right file for <c>--deck new-user</c>). An empty id on a
    /// slides page is an error, raised where the deck is selected rather than here, so a non-slides
    /// page is not made to carry one.</para>
    /// </summary>
    public string Deck { get; init; } = "";

    /// <summary>The breadcrumb trail, as "Docs &gt; Reference &gt; Components" segments.</summary>
    public IReadOnlyList<string> Breadcrumb { get; init; } = [];

    /// <summary>Output path relative to the docs root, e.g. <c>reference/components.html</c>.</summary>
    public required string Slug { get; init; }

    /// <summary>The guide badge in the header ("Reference Guide", "Quick Start", …).</summary>
    public string DocKind { get; init; } = "";

    /// <summary>Optional one-line lede rendered under the H1.</summary>
    public string Lede { get; init; } = "";

    /// <summary>The Markdown body, front-matter removed.</summary>
    public required string Body { get; init; }

    /// <summary>Where it was read from — named in every error message.</summary>
    public required string SourcePath { get; init; }

    private static readonly string[] KnownKeys =
        ["title", "kind", "deck", "breadcrumb", "slug", "doc-kind", "lede"];

    /// <summary>Read and parse one <c>.md</c> source page.</summary>
    public static DocPage Load(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{path}: every source page must open with a '---' front-matter block declaring at " +
                "least 'title' and 'slug'. Without a slug the generator does not know where the page goes.");

        int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"{path}: the front-matter block is never closed with '---'.");

        var head = text[4..end];
        var body = text[(end + 4)..].TrimStart('\n');

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in head.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
                throw new InvalidOperationException($"{path}: front-matter line is not 'key: value' — '{line}'.");
            var key = line[..colon].Trim();
            if (!KnownKeys.Contains(key))
                throw new InvalidOperationException(
                    $"{path}: unknown front-matter key '{key}'. Known keys: {string.Join(", ", KnownKeys)}. " +
                    "A typo here would otherwise be silently ignored and the page would ship with the wrong chrome.");
            map[key] = line[(colon + 1)..].Trim();
        }

        string Get(string k, string fallback = "") => map.TryGetValue(k, out var v) ? v : fallback;

        if (!map.ContainsKey("title")) throw new InvalidOperationException($"{path}: front-matter has no 'title'.");
        if (!map.ContainsKey("slug"))  throw new InvalidOperationException($"{path}: front-matter has no 'slug'.");

        return new DocPage
        {
            Title      = Get("title"),
            Kind       = Get("kind", "page"),
            Deck       = Get("deck"),
            Slug       = Get("slug"),
            DocKind    = Get("doc-kind"),
            Lede       = Get("lede"),
            Breadcrumb = Get("breadcrumb").Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Body       = body,
            SourcePath = path,
        };
    }
}
