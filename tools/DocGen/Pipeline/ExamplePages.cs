using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// The shipped example workspaces' READMEs as documentation pages: <c>examples/&lt;folder&gt;/index.html</c>, one per row of
/// <c>examples/examples.json</c>, beside the Example Workspaces page at <c>examples/index.html</c> that lists them and links
/// each. The README is the source, so the page says what the workspace says when it opens; nothing is copied into
/// <c>docs/user/src</c> to drift from it.
///
/// <para>Each page's pictures (a README's <c>![alt](pictures/x.png)</c>) are copied beside it at the same relative path, so
/// the README's own links work unchanged. The pages are searched, but stay out of the reading order: they are reached from
/// the Example Workspaces page, and 39 more pages in Previous/Next would bury the chapters.</para>
/// </summary>
public static class ExamplePages
{
    /// <summary>The page kind: emitted and searched, but not part of the reading order or its orphan check.</summary>
    public const string Kind = "example";

    private static readonly Regex Picture = new(@"!\[[^\]]*\]\((?<src>[^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>The folder's page slug: <c>examples/loop-sensor/index.html</c> for <c>Loop Sensor</c>.</summary>
    public static string Slug(string folder) => "examples/" + folder.ToLowerInvariant().Replace(' ', '-') + "/index.html";

    /// <summary>One page per example in the index whose README exists; none when the repository's examples are not beside the
    /// docs root (<c>docs/user</c> → <c>../../examples</c>).</summary>
    public static IReadOnlyList<DocPage> Load(string docsRoot)
    {
        string root = Path.GetFullPath(Path.Combine(docsRoot, "..", "..", "examples"));
        string index = Path.Combine(root, "examples.json");
        if (!File.Exists(index)) return [];
        using var json = JsonDocument.Parse(File.ReadAllText(index));
        var pages = new List<DocPage>();
        foreach (var e in json.RootElement.GetProperty("Examples").EnumerateArray())
        {
            string folder = e.GetProperty("Folder").GetString()!;
            string menu = e.TryGetProperty("Title", out var t) ? t.GetString() ?? folder : folder;
            string summary = e.TryGetProperty("Summary", out var s) ? s.GetString() ?? "" : "";
            string readme = Path.Combine(root, folder, "README.md");
            if (!File.Exists(readme)) continue;
            string text = File.ReadAllText(readme).Replace("\r\n", "\n");
            // The README's own title is the page's; its first-level heading is then not repeated in the body.
            var h1 = Regex.Match(text, @"\A\s*#\s+(?<t>[^\n]+)\n");
            string title = h1.Success ? h1.Groups["t"].Value.Trim() : menu;
            string body = Pictures(h1.Success ? text[h1.Length..] : text, Path.Combine(root, folder));
            string open = "<div class=\"callout note\">\n<span class=\"label\">Open this example</span>\n" +
                          $"<p><strong>File ▸ New ▸ Examples ▸ {Html(menu)}</strong> copies this workspace to a folder you choose and " +
                          "opens it on this README. Every example is listed in <a href=\"../index.html\">Example Workspaces</a>.</p>\n</div>\n\n";
            pages.Add(new DocPage
            {
                Title = title,
                Kind = Kind,
                Slug = Slug(folder),
                DocKind = "Examples",
                Lede = summary,
                Keywords = "example, example workspace",
                Breadcrumb = ["Docs", "Examples", title],
                Body = open + body,
                SourcePath = readme,
            });
        }
        return pages;
    }

    /// <summary>Copies the pictures <paramref name="page"/>'s README shows into <paramref name="outDir"/>, at the same relative
    /// paths; returns what it wrote. A picture that is missing, absolute or outside the workspace folder is left out (the
    /// page then shows its alt text, as the application's README view does).</summary>
    public static IEnumerable<string> CopyPictures(DocPage page, string outDir)
    {
        string from = Path.GetDirectoryName(Path.GetFullPath(page.SourcePath))!;
        foreach (var src in Picture.Matches(page.Body).Select(m => Uri.UnescapeDataString(m.Groups["src"].Value)).Distinct())
        {
            if (src.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(src)) continue;
            string source = Path.GetFullPath(Path.Combine(from, src));
            if (!source.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(source)) continue;
            string target = Path.Combine(outDir, src.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                File.Copy(source, target, overwrite: true);
            yield return target;
        }
    }

    /// <summary>
    /// A picture alone on its line, written as an <c>&lt;img&gt;</c> drawn at half its pixel width with a frame
    /// (<c>img.readme-picture</c>). Example pictures are made at twice the size they are meant to be seen at; drawn at one
    /// CSS pixel per image pixel they are scaled up on a high-density screen and look soft, while at half width every pixel
    /// of the file is used. A picture whose width cannot be read (not a PNG) stays as written.
    /// </summary>
    private static string Pictures(string body, string workspace)
        => Regex.Replace(body, @"(?m)^!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)\)[ \t]*$", m =>
        {
            string src = m.Groups["src"].Value;
            if (src.Contains("://", StringComparison.Ordinal) || PngWidth(Path.Combine(workspace, Uri.UnescapeDataString(src))) is not { } w)
                return m.Value;
            return $"<p><img class=\"readme-picture\" src=\"{Html(src)}\" alt=\"{Html(m.Groups["alt"].Value).Replace("\"", "&quot;")}\" " +
                   $"width=\"{(w + 1) / 2}\"></p>";
        });

    /// <summary>A PNG's pixel width, from its header; null when the file is not a PNG.</summary>
    private static int? PngWidth(string path)
    {
        if (!File.Exists(path)) return null;
        Span<byte> h = stackalloc byte[24];
        using var f = File.OpenRead(path);
        if (f.Read(h) < 24 || h[0] != 0x89 || h[1] != (byte)'P' || h[2] != (byte)'N' || h[3] != (byte)'G') return null;
        return (h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19];
    }

    private static string Html(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
