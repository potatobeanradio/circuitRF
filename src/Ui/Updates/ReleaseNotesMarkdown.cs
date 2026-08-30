using System;
using System.Collections.Generic;
using System.Text;

namespace CircuitRF.Ui.Updates;

/// <summary>One stretch of text with a single weight/slant, inside a <see cref="ReleaseNoteLine"/>.</summary>
/// <param name="Text">The literal characters, with every markup delimiter already removed.</param>
/// <param name="Bold">Rendered <b>bold</b>.</param>
/// <param name="Italic">Rendered <i>italic</i>.</param>
public sealed record ReleaseNoteRun(string Text, bool Bold, bool Italic);

/// <summary>
/// One rendered line. A blank line is an entry with no runs, which is what puts a gap between
/// paragraphs without the renderer having to know about paragraphs.
/// </summary>
/// <param name="Indent">Nesting depth, in levels rather than in spaces or pixels.</param>
/// <param name="Bullet">The bullet glyph for a list item, or null for an ordinary line.</param>
/// <param name="Runs">The line's content, in order.</param>
/// <param name="HeadingLevel">
/// 1-6 for a Markdown heading, 0 for an ordinary line. Carried rather than folded into
/// <see cref="ReleaseNoteRun.Bold"/> because a section heading has to be VISIBLY larger than the body
/// it introduces — bold alone reads the same as a bold lead-in mid-paragraph, which release bodies are
/// full of (owner, 2026-08-29). The size itself is the renderer's, not this parser's.
/// </param>
public sealed record ReleaseNoteLine(int Indent, string? Bullet, IReadOnlyList<ReleaseNoteRun> Runs,
                                     int HeadingLevel = 0)
{
    /// <summary>True for the blank line between two paragraphs.</summary>
    public bool IsBlank => Runs.Count == 0 && Bullet is null;
}

/// <summary>
/// The deliberately small Markdown reader behind the Release Notes dialog: <b>bold, italic, bullets
/// and indentation, and nothing else</b>.
///
/// <para><b>Why not Markdig</b>, which the User-Docs factory already depends on. What arrives here is
/// a release body typed into GitHub's web form — untrusted text from the network, rendered inside the
/// application on launch. A full CommonMark implementation would faithfully carry tables, raw HTML,
/// images and reference links into a control that cannot show any of them, and each of those is a
/// shape someone has to decide what to do with. Four constructs is a surface small enough to state,
/// to test exhaustively, and to be sure of.</para>
///
/// <para><b>Everything it does not understand degrades to plain text</b> rather than to an error or
/// to raw delimiters on screen: headings become a bold line (the release-notes idiom is a heading per
/// section, and bold is the vocabulary this parser has), inline code loses its backticks, and a link
/// keeps its text and drops its target — a URL nothing in this dialog can follow is noise around the
/// sentence the user is reading.</para>
///
/// <para>Pure, and framework-free on purpose: <c>Ui.Tests</c> calls no Avalonia runtime API, so the
/// only way this is testable at all is for the parse to produce data and the dialog to turn that data
/// into inlines.</para>
/// </summary>
public static class ReleaseNotesMarkdown
{
    /// <summary>Spaces of source indentation that make up one nesting level.</summary>
    private const int SpacesPerLevel = 2;

    /// <summary>How deep indentation is honoured. Past this the line is simply as deep as it gets.</summary>
    public const int MaxIndent = 6;

    /// <summary>The one bullet glyph. Nesting is shown by indentation, not by a different mark.</summary>
    public const string Bullet = "•";

    /// <summary>
    /// Parses a release body into lines of runs. Never throws and never returns null; unparseable
    /// input comes back as its own plain text, which is the honest failure for a document whose
    /// author cannot be asked what they meant.
    /// </summary>
    public static IReadOnlyList<ReleaseNoteLine> Parse(string? markdown)
    {
        var lines = new List<ReleaseNoteLine>();
        if (string.IsNullOrWhiteSpace(markdown)) return lines;

        // One line terminator, so the rest of this file never has to think about \r.
        string[] source = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        bool previousWasBlank = true;   // true at the start, so leading blank lines are dropped

        foreach (string raw in source)
        {
            string text = Untab(raw);
            int spaces  = CountLeadingSpaces(text);
            string body = text[spaces..].TrimEnd();

            if (body.Length == 0)
            {
                // Runs of blank lines collapse to one: a release body typed in a web form is full of
                // double spacing, and reproducing it faithfully would scroll a short note off screen.
                if (!previousWasBlank) lines.Add(new ReleaseNoteLine(0, null, []));
                previousWasBlank = true;
                continue;
            }

            bool priorWasBlank = previousWasBlank;
            previousWasBlank   = false;

            // A horizontal rule separates sections in most release bodies. It has no glyph in this
            // vocabulary, so it becomes the gap it was drawing attention to — and only when there is
            // not already one there, since the idiom is a blank line either side of the rule.
            if (IsThematicBreak(body))
            {
                if (lines.Count > 0 && !priorWasBlank) lines.Add(new ReleaseNoteLine(0, null, []));
                previousWasBlank = true;
                continue;
            }

            if (TryTakeHeading(body, out string? heading, out int level))
            {
                // A heading is a whole bold line, delimiters and all: emphasis inside one adds
                // nothing when the entire line is already the strongest weight available. The LEVEL
                // goes with it, so the renderer can also make it bigger.
                lines.Add(new ReleaseNoteLine(0, null, [new ReleaseNoteRun(StripInline(heading!), true, false)],
                                              level));
                continue;
            }

            int indent = Math.Min(spaces / SpacesPerLevel, MaxIndent);

            if (TryTakeBullet(body, out string? item))
            {
                lines.Add(new ReleaseNoteLine(indent, Bullet, ParseInline(item!)));
                continue;
            }

            lines.Add(new ReleaseNoteLine(indent, null, ParseInline(body)));
        }

        // A body ending in blank lines would otherwise open the dialog scrolled against empty space.
        while (lines.Count > 0 && lines[^1].IsBlank) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // ── line shapes ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A tab is four spaces here, so one indent measure serves both spellings.</summary>
    private static string Untab(string s) => s.Contains('\t', StringComparison.Ordinal)
        ? s.Replace("\t", "    ", StringComparison.Ordinal)
        : s;

    private static int CountLeadingSpaces(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        return i;
    }

    /// <summary><c>---</c>, <c>***</c> or <c>___</c>, three or more, nothing else on the line.</summary>
    private static bool IsThematicBreak(string body)
    {
        if (body.Length < 3) return false;
        char c = body[0];
        if (c != '-' && c != '*' && c != '_') return false;
        foreach (char ch in body)
            if (ch != c && ch != ' ') return false;
        return true;
    }

    /// <summary><c>#</c> to <c>######</c> followed by a space. Reports the level as well as the text.</summary>
    private static bool TryTakeHeading(string body, out string? text, out int level)
    {
        text  = null;
        level = 0;

        int hashes = 0;
        while (hashes < body.Length && body[hashes] == '#') hashes++;
        if (hashes is 0 or > 6) return false;
        if (hashes >= body.Length || body[hashes] != ' ') return false;

        // Closing hashes ("## Fixed ##") are decoration, not content.
        text  = body[(hashes + 1)..].Trim().TrimEnd('#').TrimEnd();
        level = hashes;
        return text.Length > 0;
    }

    /// <summary>
    /// <c>-</c>, <c>*</c> or <c>+</c> followed by a space — and an ordered item's <c>1.</c>, which is
    /// rendered with the same bullet. Numbering is not in this vocabulary, and a list whose numbers
    /// were dropped silently reads worse than one drawn as bullets.
    /// </summary>
    private static bool TryTakeBullet(string body, out string? item)
    {
        item = null;

        if (body.Length >= 2 && body[1] == ' ' && body[0] is '-' or '*' or '+')
        {
            item = body[2..].TrimStart();
            return true;
        }

        int digits = 0;
        while (digits < body.Length && char.IsAsciiDigit(body[digits])) digits++;
        if (digits > 0 && digits + 1 < body.Length
            && (body[digits] == '.' || body[digits] == ')') && body[digits + 1] == ' ')
        {
            item = body[(digits + 2)..].TrimStart();
            return true;
        }

        return false;
    }

    // ── inline emphasis ─────────────────────────────────────────────────────────────────────────

    /// <summary>The inline pass with every run collapsed back to one string — what a heading needs.</summary>
    private static string StripInline(string text)
    {
        var sb = new StringBuilder();
        foreach (ReleaseNoteRun r in ParseInline(text)) sb.Append(r.Text);
        return sb.ToString();
    }

    /// <summary>
    /// Splits one line into runs. <c>***both***</c>, <c>**bold**</c>, <c>*italic*</c> and their
    /// underscore spellings; a backslash escapes any of them.
    ///
    /// <para><b>An unmatched delimiter is printed, not obeyed.</b> A lone <c>*</c> — a footnote mark,
    /// a wildcard in a file name — would otherwise italicise everything after it to the end of the
    /// line, which is a far more visible failure than showing the asterisk the author typed.</para>
    /// </summary>
    public static IReadOnlyList<ReleaseNoteRun> ParseInline(string? text)
    {
        var runs = new List<ReleaseNoteRun>();
        if (string.IsNullOrEmpty(text)) return runs;

        var  buffer = new StringBuilder();
        bool bold   = false;
        bool italic = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            runs.Add(new ReleaseNoteRun(buffer.ToString(), bold, italic));
            buffer.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    // The delimiters go; the text stays, in whatever weight surrounds it. There is no
                    // monospace face in this vocabulary and inventing one is a bigger change than the
                    // dialog is worth.
                    buffer.Append(text.AsSpan(i + 1, close - i - 1));
                    i = close + 1;
                    continue;
                }
            }

            // An image's leading '!' is consumed with the link, so a screenshot reduces to its alt
            // text rather than to "!" followed by its alt text.
            int linkAt = c == '!' && i + 1 < text.Length && text[i + 1] == '[' ? i + 1 : i;
            if (text[linkAt] == '[' && TryTakeLink(text, linkAt, out string? label, out int after))
            {
                buffer.Append(label);
                i = after;
                continue;
            }

            if ((c == '*' || c == '_') && IsDelimiter(text, i, c, bold || italic, out int width))
            {
                Flush();
                if (width >= 3)      { bold = !bold; italic = !italic; }
                else if (width == 2) { bold = !bold; }
                else                 { italic = !italic; }
                i += width;
                continue;
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return runs;
    }

    private static bool IsEscapable(char c)
        => c is '*' or '_' or '`' or '\\' or '[' or ']' or '#' or '-' or '(' or ')';

    /// <summary>
    /// Whether the delimiter run starting at <paramref name="i"/> is markup rather than text, and how
    /// long it is (capped at three, since nothing past bold-italic has a meaning here).
    ///
    /// <para>Two rules, and both earn their place against real release bodies. <b>A run with no
    /// partner later in the line is text</b> — that is the unmatched-asterisk case above — unless
    /// <paramref name="emphasisOpen"/>, since the LAST delimiter of <c>*italic*</c> has nothing after
    /// it by construction and must still close what it opened. <b>An underscore between two
    /// alphanumerics is text</b>, because <c>snake_case_names</c> appear in release notes constantly
    /// and every one of them would otherwise start an italic span. Asterisks get no such exemption:
    /// <c>*</c> inside a word is vanishingly rare.</para>
    /// </summary>
    private static bool IsDelimiter(string text, int i, char c, bool emphasisOpen, out int width)
    {
        width = 0;
        int n = 0;
        while (i + n < text.Length && text[i + n] == c) n++;

        if (c == '_')
        {
            char before = i > 0 ? text[i - 1] : ' ';
            char after  = i + n < text.Length ? text[i + n] : ' ';
            if (char.IsLetterOrDigit(before) && char.IsLetterOrDigit(after)) return false;
        }

        // Somewhere to close, or something to close. Not a full pairing pass — just enough that a
        // stray mark stays a mark.
        if (!emphasisOpen && text.IndexOf(c, i + n) < 0) return false;

        width = Math.Min(n, 3);
        return true;
    }

    /// <summary>
    /// <c>[label](target)</c>, reduced to its label. An image (<c>![alt](src)</c>) reduces to its alt
    /// text by the same path, the <c>!</c> having already been buffered as ordinary punctuation —
    /// which is why the alt text of a screenshot reads as a stray word rather than as a broken image.
    /// </summary>
    private static bool TryTakeLink(string text, int i, out string? label, out int after)
    {
        label = null;
        after = i;

        int close = text.IndexOf(']', i + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;

        int end = text.IndexOf(')', close + 2);
        if (end < 0) return false;

        label = text[(i + 1)..close];
        after = end + 1;
        return true;
    }
}
