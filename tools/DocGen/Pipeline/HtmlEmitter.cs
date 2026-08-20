using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Markdig;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// Wraps a page's body in circuitRF's existing documentation chrome.
///
/// <para><b>This changes HOW pages are produced, not what they look like.</b> The header lock-up,
/// the brand gradient rule, the breadcrumb, the reading column, the footer and the
/// <c>prefers-color-scheme</c> dark handling are all today's, byte for byte from
/// <c>circuitrf-docs.css</c> — an un-ported page copied straight through and a generated page must
/// sit side by side without a reader being able to tell which is which.</para>
/// </summary>
public static class HtmlEmitter
{
    private static readonly MarkdownPipeline Markdown =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()   // tables, footnotes, auto-ids on headings, definition lists
            .UseAutoIdentifiers()
            .Build();

    /// <summary>Render <paramref name="page"/> (placeholders already expanded) to a complete HTML file.</summary>
    public static string Render(DocPage page, string expandedBody)
    {
        string depth = string.Concat(Enumerable.Repeat("../", page.Slug.Count(c => c == '/')));
        string bodyHtml = Markdig.Markdown.ToHtml(expandedBody, Markdown);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>circuitRF — {E(page.Title)}</title>");
        sb.AppendLine($"<link rel=\"icon\" href=\"{depth}assets/img/favicon.svg\" type=\"image/svg+xml\">");
        sb.AppendLine($"<link rel=\"stylesheet\" href=\"{depth}assets/css/circuitrf-docs.css\">");
        sb.AppendLine("<!--");
        sb.AppendLine("  GENERATED FILE - do not edit. Edit the Markdown source named below and re-run:");
        sb.AppendLine("      dotnet run [project tools/DocGen] [flag out] docs/user");
        sb.AppendLine("  (XML comments cannot contain a double hyphen; the flags are ordinary ones.)");
        sb.AppendLine($"  Source: {Comment(SourceLabel(page.SourcePath))}");
        sb.AppendLine("-->");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine();
        sb.AppendLine("<header class=\"doc-header\">");
        sb.AppendLine($"  <a class=\"brand\" href=\"{depth}index.html\">"
                    + $"<img class=\"logo\" src=\"{depth}assets/img/favicon.svg\" alt=\"circuitRF\">"
                    + "<span class=\"wordmark\">circuitRF</span></a>");
        if (page.DocKind.Length > 0)
            sb.AppendLine($"  <span class=\"doc-kind\">{E(page.DocKind)}</span>");
        sb.AppendLine("</header>");
        sb.AppendLine("<hr class=\"doc-headrule\">");
        sb.AppendLine();
        sb.AppendLine("<main class=\"page\">");
        if (page.Breadcrumb.Count > 0)
            sb.AppendLine($"  <p class=\"breadcrumb\">{Breadcrumb(page, depth)}</p>");
        sb.AppendLine();
        sb.AppendLine($"  <h1>{E(page.Title)}</h1>");
        if (page.Lede.Length > 0)
            sb.AppendLine($"  <p class=\"lede\">{E(page.Lede)}</p>");
        sb.AppendLine();
        sb.AppendLine(bodyHtml);
        sb.AppendLine("</main>");
        sb.AppendLine();
        sb.AppendLine("<footer class=\"doc-footer\">");
        sb.AppendLine($"  <img src=\"{depth}assets/img/circuitRF-mark.svg\" alt=\"\">");
        sb.AppendLine("  <span>circuitRF — RF circuit simulator</span>");
        sb.AppendLine("  <span class=\"spacer\"></span>");
        sb.AppendLine($"  <a href=\"{depth}index.html\">Documentation home</a>");
        sb.AppendLine("</footer>");
        sb.AppendLine();
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// "Docs &gt; Reference &gt; Components", where every segment but the last is a link back up the
    /// tree. Written as "Docs > Reference > Components" in the front-matter; the generator derives
    /// the hrefs, so a moved page cannot leave a stale one behind.
    /// </summary>
    private static string Breadcrumb(DocPage page, string depth)
    {
        var parts = page.Breadcrumb;
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(" › ");
            if (i == parts.Count - 1) { sb.Append(E(parts[i])); continue; }

            // "Docs" is the site root; anything between is a section index.
            string href = i == 0
                ? depth + "index.html"
                : Normalise(depth + string.Join('/', parts.Skip(1).Take(i).Select(Slugify)) + "/index.html",
                            page.Slug);
            sb.Append($"<a href=\"{href}\">{E(parts[i])}</a>");
        }
        return sb.ToString();
    }

    private static string Slugify(string s) => s.ToLowerInvariant().Replace(' ', '-');

    /// <summary>Collapse "../reference/index.html" to "index.html" when the page is already there.</summary>
    private static string Normalise(string href, string slug)
    {
        string pageDir = Path.GetDirectoryName(slug)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        string target  = Path.GetFullPath(Path.Combine("/site", pageDir, href));
        string rel = Path.GetRelativePath(Path.Combine("/site", pageDir), target);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>A path that is safe inside an XML comment (no double hyphen).</summary>
    private static string Comment(string s) => s.Replace("--", "-‑");

    /// <summary>
    /// The source path as a reader can act on it: relative to the working directory, with forward
    /// slashes. An absolute build-machine path in a shipped page tells nobody anything.
    /// </summary>
    private static string SourceLabel(string path)
    {
        var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return (rel.StartsWith("..", StringComparison.Ordinal) ? path : rel)
               .Replace(Path.DirectorySeparatorChar, '/');
    }
}
