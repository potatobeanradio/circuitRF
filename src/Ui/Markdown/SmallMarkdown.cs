using System;
using System.Collections.Generic;
using System.Text;

namespace CircuitRF.Ui.Markdown;

/// <summary>One stretch of text with a single weight/slant, inside a <see cref="MarkdownLine"/>.</summary>
/// <param name="Text">The literal characters, with every markup delimiter already removed.</param>
/// <param name="Bold">Rendered <b>bold</b>.</param>
/// <param name="Italic">Rendered <i>italic</i>.</param>
public sealed record MarkdownRun(string Text, bool Bold, bool Italic);

/// <summary>
/// One rendered line. A blank line is an entry with no runs, which is what puts a gap between
/// paragraphs without the renderer having to know about paragraphs.
/// </summary>
/// <param name="Indent">Nesting depth, in levels rather than in spaces or pixels.</param>
/// <param name="Bullet">The bullet glyph for a list item, or null for an ordinary line.</param>
/// <param name="Runs">The line's content, in order.</param>
/// <param name="HeadingLevel">
/// 1-6 for a Markdown heading, 0 for an ordinary line. Carried rather than folded into
/// <see cref="MarkdownRun.Bold"/> because a section heading has to be VISIBLY larger than the body
/// it introduces — bold alone reads the same as a bold lead-in mid-paragraph, which release bodies are
/// full of (owner, 2026-08-29). The size itself is the renderer's, not this parser's.
/// </param>
/// <param name="IsCode">
/// One line of a fenced code block: rendered verbatim in a monospace face, with its own leading
/// spaces kept. Carried on the LINE rather than on a run because it is not emphasis — nothing inside
/// it was parsed, so there is no run structure to attach it to, and its indentation is content.
/// </param>
/// <param name="Image">
/// A line that is a picture and nothing else (<c>![alt](file.png)</c> on its own line), or null. Its
/// <see cref="Runs"/> carry the alt text, so a picture that cannot be drawn still says what it was.
/// </param>
public sealed record MarkdownLine(int Indent, string? Bullet, IReadOnlyList<MarkdownRun> Runs,
                                     int HeadingLevel = 0, bool IsCode = false, MarkdownImage? Image = null)
{
    /// <summary>True for the blank line between two paragraphs.</summary>
    public bool IsBlank => Runs.Count == 0 && Bullet is null;
}

/// <summary>A picture a document line shows.</summary>
/// <param name="Alt">The alt text: what is drawn instead when the picture cannot be.</param>
/// <param name="Source">The target as written, angle brackets and title removed.</param>
/// <param name="Path">
/// The local file it resolved to, or null — set by the document that knows its own folder
/// (<see cref="MarkdownDocument"/>), never by the parse, which has no folder.
/// </param>
public sealed record MarkdownImage(string Alt, string Source, string? Path = null);

/// <summary>
/// The deliberately small Markdown reader: <b>bold, italic, bullets, indentation, headings and
/// two-dimensional tables flattened into lines — and nothing else</b>.
///
/// <para><b>Two callers, and they are why it is not named after either of them.</b> The Release Notes
/// dialog renders a release body fetched from GitHub, and a workspace's own <c>README.md</c> opens as
/// a read-only document (<c>MarkdownDocument</c>). The second arrived after the first and needed no
/// new vocabulary, which is the evidence that the vocabulary is the right size.</para>
///
/// <para><b>Why not Markdig</b>, which the User-Docs factory already depends on. One of the two inputs
/// is untrusted text from the network, rendered inside the application on launch. A full CommonMark
/// implementation would faithfully carry raw HTML, images and reference links into a control that
/// cannot show any of them, and each of those is a shape someone has to decide what to do with. This
/// surface is small enough to state, to test exhaustively, and to be sure of.</para>
///
/// <para><b>Everything it does not understand degrades to plain text</b> rather than to an error or
/// to raw delimiters on screen: inline code loses its backticks, and a link keeps its text and drops
/// its target — a URL nothing here can follow is noise around the sentence the user is reading.</para>
///
/// <para><b>Pictures, for a README only.</b> With <c>images</c> set, a line that is one
/// <c>![alt](file)</c> and nothing else is a picture line (<see cref="MarkdownLine.Image"/>); the
/// README document resolves the file against its own folder and draws it. Off, which is what the
/// Release Notes dialog asks for, a picture is its alt text as it always was: a release body is
/// fetched from the network, and its pictures are URLs this reader does not fetch. A picture inside a
/// sentence is its alt text either way.</para>
///
/// <para>Pure, and framework-free on purpose: <c>Ui.Tests</c> calls no Avalonia runtime API, so the
/// only way this is testable at all is for the parse to produce data and
/// <see cref="MarkdownInlines"/> to turn that data into inlines.</para>
/// </summary>
public static class SmallMarkdown
{
    /// <summary>Spaces of source indentation that make up one nesting level.</summary>
    private const int SpacesPerLevel = 2;

    /// <summary>How deep indentation is honoured. Past this the line is simply as deep as it gets.</summary>
    public const int MaxIndent = 6;

    /// <summary>The one bullet glyph. Nesting is shown by indentation, not by a different mark.</summary>
    public const string Bullet = "•";

    /// <summary>
    /// Parses Markdown into lines of runs. Never throws and never returns null; unparseable input
    /// comes back as its own plain text, which is the honest failure for a document whose author
    /// cannot be asked what they meant.
    /// </summary>
    /// <param name="markdown">The source.</param>
    /// <param name="joinSoftWraps">
    /// Whether a paragraph broken across several source lines is ONE line here.
    ///
    /// <para><b>The two callers genuinely want opposite answers, which is why this is a parameter and
    /// not a decision.</b> A release body is typed into GitHub's web form, where a newline is a line
    /// the author put there and GitHub renders it as one — joining would run their notes together. A
    /// <c>README.md</c> in a repository is hard-wrapped by its author at whatever column their editor
    /// uses, and honouring THOSE breaks puts a ragged edge down the middle of a paragraph at a width
    /// that has nothing to do with the window: the visible symptom is a sentence that stops at 100
    /// characters and resumes on the next row, twice per paragraph.</para>
    ///
    /// <para>A continuation joins the line above it, bullet included — a wrapped list item stays one
    /// item. Headings, table rows and blank lines end a paragraph and are never joined into.</para>
    /// </param>
    /// <param name="images">Whether a line that is one picture and nothing else becomes a picture line
    /// (see the class remarks). Off by default: the network-fed caller keeps the alt text.</param>
    public static IReadOnlyList<MarkdownLine> Parse(string? markdown, bool joinSoftWraps = false,
                                                    bool images = false)
    {
        var lines = new List<MarkdownLine>();
        if (string.IsNullOrWhiteSpace(markdown)) return lines;

        // One line terminator, so the rest of this file never has to think about \r.
        string[] source = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        bool previousWasBlank = true;   // true at the start, so leading blank lines are dropped
        bool inTable         = false;  // past a table's |---|---| rule, so its rows are its body
        bool canContinue     = false;  // the last emitted line is a paragraph a wrap can join
        bool inFence         = false;  // inside ``` … ```, where nothing is markup

        foreach (string raw in source)
        {
            string text = Untab(raw);
            int spaces  = CountLeadingSpaces(text);
            string body = text[spaces..].TrimEnd();

            // A fence is checked BEFORE the blank-line rule and before every line shape: inside one,
            // a blank line is content, a leading `#` is a comment in whatever language this is, and a
            // `-` is a flag. Nothing in a code block is markup, including the things that look most
            // like it.
            if (IsFence(body))
            {
                inFence          = !inFence;
                previousWasBlank = false;
                canContinue      = false;
                inTable          = false;
                continue;
            }

            if (inFence)
            {
                // Verbatim, leading spaces included — a directory tree or an indented command loses
                // its meaning without them. TrimEnd only, which no code depends on.
                lines.Add(new MarkdownLine(0, null, [new MarkdownRun(text.TrimEnd(), false, false)],
                                           0, IsCode: true));
                previousWasBlank = false;
                canContinue      = false;
                continue;
            }

            if (body.Length == 0)
            {
                // Runs of blank lines collapse to one: a release body typed in a web form is full of
                // double spacing, and reproducing it faithfully would scroll a short note off screen.
                if (!previousWasBlank) lines.Add(new MarkdownLine(0, null, []));
                previousWasBlank = true;
                inTable          = false;   // a blank line is the end of a table, as it is of a list
                canContinue      = false;
                continue;
            }

            bool priorWasBlank = previousWasBlank;
            previousWasBlank   = false;

            // A horizontal rule separates sections in most release bodies. It has no glyph in this
            // vocabulary, so it becomes the gap it was drawing attention to — and only when there is
            // not already one there, since the idiom is a blank line either side of the rule.
            if (IsThematicBreak(body))
            {
                if (lines.Count > 0 && !priorWasBlank) lines.Add(new MarkdownLine(0, null, []));
                previousWasBlank = true;
                canContinue      = false;
                continue;
            }

            if (TryTakeCells(body, out IReadOnlyList<string>? cells))
            {
                // The rule row is not content. It is also the only thing that identifies the row
                // ABOVE it as a header, which is why that one is rewritten here rather than guessed
                // at when it was read: a row of pipes with no rule under it is not a table at all.
                if (IsTableRule(cells!))
                {
                    if (lines.Count > 0 && lines[^1] is { Bullet: null, Runs.Count: > 0 } header)
                        lines[^1] = header with { Runs = Bolded(header.Runs) };
                    inTable = true;
                    continue;
                }

                lines.Add(inTable ? TableBodyLine(cells!) : TableHeaderLine(cells!));
                canContinue = false;
                continue;
            }

            inTable = false;

            if (TryTakeHeading(body, out string? heading, out int level))
            {
                // A heading is a whole bold line, delimiters and all: emphasis inside one adds
                // nothing when the entire line is already the strongest weight available. The LEVEL
                // goes with it, so the renderer can also make it bigger.
                lines.Add(new MarkdownLine(0, null, [new MarkdownRun(StripInline(heading!), true, false)],
                                              level));
                canContinue = false;
                continue;
            }

            int indent = Math.Min(spaces / SpacesPerLevel, MaxIndent);

            // Checked before the soft-wrap join: a picture is its own block and never the end of the
            // sentence above it, nor something the next line could continue.
            if (images && TryTakeImageLine(body, out MarkdownImage? image))
            {
                lines.Add(new MarkdownLine(0, null, [new MarkdownRun(image!.Alt, false, true)], Image: image));
                canContinue = false;
                continue;
            }

            if (TryTakeBullet(body, out string? item))
            {
                lines.Add(new MarkdownLine(indent, Bullet, ParseInline(item!)));
                canContinue = true;
                continue;
            }

            if (joinSoftWraps && canContinue && lines.Count > 0)
            {
                // The author's own wrap column, folded back out. The continuation's indentation goes
                // with it: it describes where the editor broke the line, not a nesting level.
                MarkdownLine open = lines[^1];
                var joined = new List<MarkdownRun>(open.Runs) { new(" ", false, false) };
                joined.AddRange(ParseInline(body));
                lines[^1] = open with { Runs = joined };
                continue;
            }

            lines.Add(new MarkdownLine(indent, null, ParseInline(body)));
            canContinue = true;
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

    /// <summary>
    /// The <c>```</c> or <c>~~~</c> that opens or closes a code block. An info string after it
    /// (<c>```python</c>) is a language tag; there is no syntax highlighting here, so it is dropped
    /// with the fence rather than printed above the code.
    /// </summary>
    private static bool IsFence(string body)
        => body.StartsWith("```", StringComparison.Ordinal)
        || body.StartsWith("~~~", StringComparison.Ordinal);

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

    /// <summary>
    /// A line that is exactly one <c>![alt](target)</c>. The target may be written in angle brackets
    /// (<c>![a](&lt;pictures/my pad.png&gt;)</c>, the CommonMark spelling of a path with a space in it)
    /// and may carry a quoted title, which is dropped. Anything after the closing parenthesis makes it
    /// a sentence, and a sentence is not a picture line.
    /// </summary>
    private static bool TryTakeImageLine(string body, out MarkdownImage? image)
    {
        image = null;
        if (!body.StartsWith("![", StringComparison.Ordinal)) return false;

        int close = body.IndexOf("](", 2, StringComparison.Ordinal);
        if (close < 0 || !body.EndsWith(')')) return false;

        string alt    = StripInline(body[2..close]);
        string target = body[(close + 2)..^1].Trim();

        if (target.StartsWith('<'))
        {
            int end = target.IndexOf('>');
            if (end < 0) return false;
            target = target[1..end];
        }
        else
        {
            // A title follows the target after whitespace; the target itself has none unencoded.
            int space = target.IndexOfAny([' ', '\t']);
            if (space >= 0) target = target[..space];
            // A bare target holds no parenthesis, so one here means the line went on past the picture.
            if (target.Contains(')', StringComparison.Ordinal)) return false;
        }

        if (target.Length == 0) return false;
        image = new MarkdownImage(alt, target);
        return true;
    }

    // ── tables, flattened ───────────────────────────────────────────────────────────────────────
    //
    // A table is TWO-DIMENSIONAL and this renderer has one dimension: everything ends up as inlines in
    // a single selectable block, where a column cannot be held to a width (the face is proportional,
    // and non-breaking spaces do not align in it). So a table becomes the list it was already saying —
    // header row bold, each body row a bullet whose FIRST cell leads in bold and whose remaining cells
    // follow it. That is a real loss for a wide table and none at all for the two-column
    // "name / what it does" table, which is the shape every table in the shipped example READMEs and in
    // a release body actually has.
    //
    // The alternative was to leave a table alone and let its pipes and its |---|---| rule reach the
    // screen verbatim, which is what happened before this and is not a smaller decision — it is the
    // same decision, made worse.

    /// <summary>The separator between a flattened row's cells. An en dash with spaces, so it cannot be
    /// mistaken for a hyphen inside one of the cells.</summary>
    private const string CellSeparator = " – ";

    /// <summary>
    /// Splits a <c>| a | b |</c> row into its cells, or answers false for a line that is not one.
    ///
    /// <para>A leading pipe is required. GFM allows a row without one, but so does ordinary prose that
    /// happens to contain a vertical bar — and reading "a | b" as a two-column table would silently
    /// restructure a sentence.</para>
    /// </summary>
    private static bool TryTakeCells(string body, out IReadOnlyList<string>? cells)
    {
        cells = null;
        if (body.Length < 2 || body[0] != '|') return false;

        var found  = new List<string>();
        var buffer = new StringBuilder();

        for (int i = 1; i < body.Length; i++)
        {
            char c = body[i];

            // An escaped pipe is content. Nothing else is unescaped here — the cell text goes through
            // the ordinary inline pass, which owns every other backslash.
            if (c == '\\' && i + 1 < body.Length && body[i + 1] == '|') { buffer.Append('|'); i++; continue; }

            if (c == '|') { found.Add(buffer.ToString().Trim()); buffer.Clear(); continue; }

            buffer.Append(c);
        }

        // Whatever follows the last pipe. Empty for the usual trailing-pipe spelling; a real cell for
        // a row written without one.
        if (buffer.ToString().Trim() is { Length: > 0 } tail) found.Add(tail);

        if (found.Count == 0) return false;
        cells = found;
        return true;
    }

    /// <summary>The <c>|---|:--:|</c> row under a header: every cell dashes, with optional alignment
    /// colons. Alignment is not in this vocabulary, so the colons are read and discarded.</summary>
    private static bool IsTableRule(IReadOnlyList<string> cells)
    {
        foreach (string cell in cells)
        {
            string c = cell.Trim(':');
            if (c.Length == 0) return false;
            foreach (char ch in c) if (ch != '-') return false;
        }
        return cells.Count > 0;
    }

    /// <summary>The header row: the whole line bold, cells separated.</summary>
    private static MarkdownLine TableHeaderLine(IReadOnlyList<string> cells)
        => new(0, null, Bolded(JoinCells(cells, boldFirst: false)));

    /// <summary>A body row: a bullet, its first cell bold, the rest after it.</summary>
    private static MarkdownLine TableBodyLine(IReadOnlyList<string> cells)
        => new(0, Bullet, JoinCells(cells, boldFirst: true));

    /// <summary>
    /// One row's cells as a single line's runs, each cell through the ordinary inline pass so its own
    /// emphasis and code spans survive the flattening.
    /// </summary>
    private static IReadOnlyList<MarkdownRun> JoinCells(IReadOnlyList<string> cells, bool boldFirst)
    {
        var runs = new List<MarkdownRun>();
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].Length == 0) continue;   // an empty cell contributes nothing but its separator
            if (runs.Count > 0) runs.Add(new MarkdownRun(CellSeparator, false, false));

            IReadOnlyList<MarkdownRun> cell = ParseInline(cells[i]);
            runs.AddRange(i == 0 && boldFirst ? Bolded(cell) : cell);
        }
        return runs;
    }

    /// <summary>The same runs, every one of them bold.</summary>
    private static IReadOnlyList<MarkdownRun> Bolded(IReadOnlyList<MarkdownRun> runs)
    {
        var bold = new List<MarkdownRun>(runs.Count);
        foreach (MarkdownRun r in runs) bold.Add(r with { Bold = true });
        return bold;
    }

    // ── inline emphasis ─────────────────────────────────────────────────────────────────────────

    /// <summary>The inline pass with every run collapsed back to one string — what a heading needs.</summary>
    private static string StripInline(string text)
    {
        var sb = new StringBuilder();
        foreach (MarkdownRun r in ParseInline(text)) sb.Append(r.Text);
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
    public static IReadOnlyList<MarkdownRun> ParseInline(string? text)
    {
        var runs = new List<MarkdownRun>();
        if (string.IsNullOrEmpty(text)) return runs;

        var  buffer = new StringBuilder();
        bool bold   = false;
        bool italic = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            runs.Add(new MarkdownRun(buffer.ToString(), bold, italic));
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
