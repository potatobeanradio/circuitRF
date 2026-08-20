using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// The ONE reading order through the whole documentation set, authored in
/// <c>docs/user/src/_nav.txt</c>.
///
/// <para>The owner's requirement is that a reader can browse every page in a browser without ever
/// going back to the index. Three things follow from that, and all three are derived from this one
/// file rather than hand-maintained: the complete table of contents on <c>index.html</c>, the
/// Previous/Next chain at the foot of every page, and the guarantee that <b>no page is an
/// orphan</b>.</para>
///
/// <para><b>The orphan check is the reason this is a manifest and not an inferred order.</b> An
/// order inferred from directory names would silently absorb a new page — which is exactly the
/// failure being guarded against, because a page nobody linked to is indistinguishable from a page
/// nobody wrote. Here, adding a source page and forgetting to place it in the reading order fails
/// generation with the file name in the message.</para>
///
/// <para><c>index.html</c> is the head of the chain implicitly and must not be listed: it IS the
/// table of contents, so listing it inside itself would be circular.</para>
/// </summary>
public sealed class SiteNav
{
    /// <summary>The site root — first in the linear order, never listed in the manifest.</summary>
    public const string Root = "index.html";

    /// <summary>One page in the reading order.</summary>
    /// <param name="Slug">Path relative to the docs root, e.g. <c>reference/units.html</c>.</param>
    /// <param name="Blurb">The one-line description shown beside it in the table of contents.</param>
    public readonly record struct Entry(string Slug, string Blurb);

    /// <summary>One heading in the table of contents, with the pages under it in reading order.</summary>
    public sealed record Section(string Title, IReadOnlyList<Entry> Entries);

    public IReadOnlyList<Section> Sections { get; }

    /// <summary>Every slug in reading order, <see cref="Root"/> first.</summary>
    public IReadOnlyList<string> Order { get; }

    private SiteNav(IReadOnlyList<Section> sections)
    {
        Sections = sections;
        Order = new[] { Root }.Concat(sections.SelectMany(s => s.Entries).Select(e => e.Slug)).ToList();
    }

    /// <summary>Where the manifest lives, given the docs root.</summary>
    public static string PathIn(string docsRoot) => Path.Combine(docsRoot, "src", "_nav.txt");

    /// <summary>
    /// Read the manifest. Absent is not an error — a docs tree that predates the reading order still
    /// generates, it simply gets no navigation — but a malformed one is, since a dropped line would
    /// otherwise present as a page quietly vanishing from the table of contents.
    /// </summary>
    public static SiteNav? Load(string docsRoot)
    {
        string path = PathIn(docsRoot);
        if (!File.Exists(path)) return null;

        var sections = new List<Section>();
        List<Entry>? current = null;
        string title = "";
        int lineNo = 0;

        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("==", StringComparison.Ordinal))
            {
                if (current is not null) sections.Add(new Section(title, current));
                title = line[2..].Trim();
                current = [];
                continue;
            }

            if (current is null)
                throw new InvalidOperationException(
                    $"{path}({lineNo}): '{line}' appears before any '== Section' heading. Every page in "
                  + "the reading order belongs to a section, because the table of contents is built "
                  + "from the sections.");

            var parts = line.Split('|', 2);
            string slug = parts[0].Trim();
            if (slug == Root)
                throw new InvalidOperationException(
                    $"{path}({lineNo}): '{Root}' is the table of contents itself and is the head of the "
                  + "reading order implicitly. Listing it would put the index inside its own contents.");
            current.Add(new Entry(slug, parts.Length > 1 ? parts[1].Trim() : ""));
        }

        if (current is not null) sections.Add(new Section(title, current));
        if (sections.Count == 0)
            throw new InvalidOperationException($"{path}: the reading order is empty.");

        var dupes = sections.SelectMany(s => s.Entries).GroupBy(e => e.Slug)
                            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
            throw new InvalidOperationException(
                $"{path}: these pages appear more than once in the reading order, so Previous/Next "
              + "would loop: " + string.Join(", ", dupes));

        return new SiteNav(sections);
    }

    /// <summary>
    /// Fail if the reading order and the page set disagree in either direction.
    /// <paramref name="emitted"/> is every page the run will write or copy through.
    /// </summary>
    public void Validate(IReadOnlySet<string> emitted)
    {
        var ordered = Order.ToHashSet(StringComparer.Ordinal);

        var orphans = emitted.Where(p => !ordered.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (orphans.Count > 0)
            throw new InvalidOperationException(
                "These pages are generated but are in no section of docs/user/src/_nav.txt, so nothing "
              + "links to them and no Previous/Next reaches them — a reader browsing the site would "
              + "never see them:\n  " + string.Join("\n  ", orphans));

        var missing = ordered.Where(p => !emitted.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "docs/user/src/_nav.txt places these pages in the reading order but nothing produces "
              + "them, so the table of contents and every Previous/Next through them would 404:\n  "
              + string.Join("\n  ", missing));
    }

    /// <summary>The page before <paramref name="slug"/> in the reading order, or null at the head.</summary>
    public string? Previous(string slug)
    {
        int i = Order.ToList().IndexOf(slug);
        return i > 0 ? Order[i - 1] : null;
    }

    /// <summary>The page after <paramref name="slug"/> in the reading order, or null at the tail.</summary>
    public string? Next(string slug)
    {
        var list = Order.ToList();
        int i = list.IndexOf(slug);
        return i >= 0 && i < list.Count - 1 ? Order[i + 1] : null;
    }
}
