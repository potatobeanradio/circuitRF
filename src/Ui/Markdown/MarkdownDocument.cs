using System;
using System.Collections.Generic;
using System.IO;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Markdown;

/// <summary>
/// A Markdown file open as a <b>read-only</b> document tab — in practice a workspace's own
/// <c>README.md</c>, which is what an example workspace ships its explanation in.
///
/// <h3>Read-only is structural, not enforced</h3>
/// <para>There is no editing surface: the view is a selectable text block over
/// <see cref="Lines"/>, and the parsed lines are all this document holds — the source text is not
/// kept, so there is nothing an edit could be applied to and nothing a save could write. It is
/// therefore absent from <c>HasSaveRoute</c>/<c>HasSaveAsRoute</c> for the reason the Welcome stub
/// is: a menu item that does not apply is not a menu item that is temporarily unavailable. It is
/// never dirty, so it never joins a close prompt or a Save All.</para>
///
/// <para>circuitRF is not a Markdown editor and this is not the first step towards one. The file is
/// an ordinary file in the user's own workspace folder and their own editor is the right place to
/// change it; what circuitRF owes them is that opening the workspace SHOWS it.</para>
///
/// <h3>It is file-backed, so the tab menu's Reveal works</h3>
/// <para><see cref="IFileBackedDocument"/> is the whole reason — "show me where this actually is" is
/// exactly the question someone reading an example's README asks next.</para>
/// </summary>
public sealed class MarkdownDocument : Document, IFileBackedDocument
{
    /// <summary>The extension this document opens. Lower case; callers compare case-insensitively.</summary>
    public const string Extension = ".md";

    /// <summary>
    /// The file a workspace's own explanation lives in. Matched case-INSENSITIVELY against the
    /// workspace folder's entries rather than probed by name, because the three operating systems
    /// disagree about whether <c>Readme.md</c> is the same file, and an example that showed its README
    /// on two platforms and not the third would be a mystery nobody would look for here.
    /// </summary>
    public const string WorkspaceReadmeFileName = "README.md";

    /// <summary>Absolute path of the file. Never null: this document cannot exist without one.</summary>
    public string FilePath { get; }

    /// <summary>The parsed document, ready for <see cref="MarkdownInlines"/>.</summary>
    public IReadOnlyList<MarkdownLine> Lines { get; }

    private MarkdownDocument(string filePath, IReadOnlyList<MarkdownLine> lines)
    {
        FilePath = filePath;
        Lines    = lines;
        Id       = filePath;                       // the path, so two READMEs of the same name are two tabs
        Title    = Path.GetFileName(filePath);
    }

    /// <summary>
    /// Reads and parses <paramref name="absolutePath"/>.
    ///
    /// <para><b>Soft wraps are joined</b>, unlike the Release Notes dialog's parse — a README in a
    /// repository is hard-wrapped at its author's editor column, and honouring those breaks would put
    /// a ragged edge down the middle of every paragraph at a width that has nothing to do with the
    /// window. <see cref="SmallMarkdown.Parse"/>'s own parameter carries the argument.</para>
    /// <para><b>Pictures</b> are resolved here, against the file's own folder, because only the document
    /// knows where it is — see <see cref="ResolveImage"/>.</para>
    /// </summary>
    /// <exception cref="IOException">Unreadable; the caller reports it.</exception>
    public static MarkdownDocument Load(string absolutePath)
    {
        var parsed = SmallMarkdown.Parse(File.ReadAllText(absolutePath), joinSoftWraps: true, images: true);
        string? folder = Path.GetDirectoryName(absolutePath);
        var lines = new List<MarkdownLine>(parsed.Count);
        foreach (MarkdownLine line in parsed)
            lines.Add(line.Image is { } image
                ? line with { Image = image with { Path = ResolveImage(folder, image.Source) } }
                : line);
        return new(absolutePath, lines);
    }

    /// <summary>The picture formats drawn. Each is one Avalonia decodes; SVG is not among them.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    /// <summary>
    /// The local file a picture's target names, or null to show its alt text instead.
    ///
    /// <para><b>Local files only.</b> A target with a scheme (<c>https://</c>, <c>data:</c>) is not
    /// fetched: opening a workspace must not reach the network, and the same README on GitHub still shows
    /// the picture from the repository. A relative target resolves against the README's own folder, as
    /// GitHub resolves it, so one file reads the same in both places; <c>%20</c> is decoded, because that
    /// is how GitHub wants a space in a path written. A missing file or a format not in
    /// <see cref="ImageExtensions"/> is null as well — the alt text is the honest fallback.</para>
    /// </summary>
    public static string? ResolveImage(string? folder, string source)
    {
        if (string.IsNullOrWhiteSpace(source) || folder is null) return null;
        if (source.Contains("://", StringComparison.Ordinal)
            || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("//", StringComparison.Ordinal)) return null;

        string decoded;
        try { decoded = Uri.UnescapeDataString(source); }
        catch (UriFormatException) { return null; }

        string full;
        try { full = Path.GetFullPath(Path.Combine(folder, decoded.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        return ImageExtensions.Contains(Path.GetExtension(full)) && File.Exists(full) ? full : null;
    }

    /// <summary>Whether <paramref name="path"/> is a file this document can open.</summary>
    public static bool IsMarkdown(string? path)
        => path is { Length: > 0 }
        && string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The workspace's own README, or null when it has none. Case-insensitive by directory scan — see
    /// <see cref="WorkspaceReadmeFileName"/>.
    /// </summary>
    public static string? FindWorkspaceReadme(string? workspaceDir)
    {
        if (string.IsNullOrWhiteSpace(workspaceDir)) return null;

        try
        {
            foreach (string path in Directory.EnumerateFiles(workspaceDir, "*.md"))
                if (string.Equals(Path.GetFileName(path), WorkspaceReadmeFileName,
                                  StringComparison.OrdinalIgnoreCase))
                    return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A workspace that cannot be listed opens anyway; it simply shows no README.
        }
        return null;
    }
}
