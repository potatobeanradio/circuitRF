using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CircuitRF.Ui.Markdown;

/// <summary>
/// Turns <see cref="SmallMarkdown"/>'s parsed lines into Avalonia <see cref="Inline"/>s.
///
/// <para><b>Everything renders into ONE block</b>, not into a control per line. That is the Release
/// Notes dialog's own rule — a selection cannot cross two controls, and the owner's requirement there
/// is that a whole note drags and copies in one go — and a README document inherits it for the same
/// reason. It is also what decides the two oddities below: indentation is non-breaking spaces because
/// an inline has nowhere to put a margin, and a bullet is a glyph in the text rather than a marker
/// beside it.</para>
///
/// <para>The half of the Markdown path that touches Avalonia, kept apart from the parse for the
/// reason <see cref="SmallMarkdown"/> gives: <c>Ui.Tests</c> calls no Avalonia runtime API, so
/// anything decided in here is decided where no test can reach it. Nothing is decided in here —
/// it is a transcription.</para>
/// </summary>
public static class MarkdownInlines
{
    /// <summary>The Release Notes dialog's body size, matching the block's own <c>FontSize</c>.</summary>
    public const double ReleaseNotesBodyFontSize = 12;

    /// <summary>A document's body size: a point larger, because a README is read rather than skimmed
    /// once, and it gets a whole document tab rather than a dialog's fixed height.</summary>
    public const double DocumentBodyFontSize = 13;

    /// <summary>Indentation for one nesting level, in non-breaking spaces.</summary>
    private const int SpacesPerIndent = 4;

    /// <summary>
    /// The one character indentation is built from. An ordinary space is collapsible, and a run of
    /// them at the start of a wrapped line is exactly what a text layout is entitled to drop.
    /// </summary>
    private const char Nbsp = ' ';

    /// <summary>
    /// The face a fenced code block is drawn in, by preference order across the three platforms. A
    /// code block is the one place the shape of the text is content — a directory tree and an aligned
    /// column both stop meaning anything in a proportional face.
    /// </summary>
    private static readonly FontFamily CodeFace =
        FontFamily.Parse("Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");

    /// <summary>
    /// Section headings, by Markdown level: visibly larger than the body, and only TWO sizes rather
    /// than six — past <c>###</c> a document is not really nesting sections any more, and a ladder of
    /// near-identical sizes reads as inconsistency rather than as structure.
    /// </summary>
    public static double HeadingFontSize(int level, double bodyFontSize)
        => level <= 2 ? bodyFontSize + 3 : bodyFontSize + 2;

    /// <summary>
    /// Appends <paramref name="lines"/> to <paramref name="inlines"/>. Every line ends in a
    /// <see cref="LineBreak"/> rather than a newline character, because a <see cref="Run"/>'s own text
    /// is laid out as a single paragraph and a <c>\n</c> inside one is not a break.
    ///
    /// <para>No trailing break after the last line: it would add an empty row the scroll bar counts.</para>
    /// </summary>
    /// <param name="imageMaxWidth">The widest a picture is drawn, in device-independent pixels: the text
    /// block's own measure, so a picture is never wider than the column it sits in. A smaller picture is
    /// drawn at its own size.</param>
    /// <param name="owned">Receives each bitmap a picture line decoded, for the caller to dispose when it
    /// clears the block. Null when the lines carry no pictures.</param>
    public static void Append(InlineCollection? inlines, IReadOnlyList<MarkdownLine> lines,
                              double bodyFontSize, double imageMaxWidth = 0,
                              ICollection<IDisposable>? owned = null)
    {
        if (inlines is null) return;

        for (int i = 0; i < lines.Count; i++)
        {
            MarkdownLine line = lines[i];

            if (line.Image is { Path: { } picture } && imageMaxWidth > 0 && TryLoad(picture) is { } bitmap)
            {
                owned?.Add(bitmap);
                // Inside the one text block, as an inline, so a selection still runs through the whole
                // document; the alt text is the tooltip, since it is no longer on the page.
                var image = new Image
                {
                    Source   = bitmap,
                    Stretch  = Stretch.Uniform,
                    MaxWidth = Math.Min(imageMaxWidth, bitmap.Size.Width),
                };
                if (line.Image.Alt.Length > 0) ToolTip.SetTip(image, line.Image.Alt);
                inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom });
            }
            else if (line.IsCode)
            {
                // Its own leading spaces are content and an ordinary space at the start of a line is
                // exactly what a text layout drops, so they become the one character that survives.
                foreach (MarkdownRun run in line.Runs)
                    inlines.Add(new Run(Protected(run.Text))
                    {
                        FontFamily = CodeFace,
                        FontSize   = bodyFontSize - 1,   // a mono face reads larger at the same size
                    });
            }
            else if (!line.IsBlank)
            {
                double size = line.HeadingLevel > 0
                    ? HeadingFontSize(line.HeadingLevel, bodyFontSize)
                    : bodyFontSize;

                string lead = new(Nbsp, line.Indent * SpacesPerIndent);
                if (line.Bullet is not null) lead += line.Bullet + new string(Nbsp, 2);
                if (lead.Length > 0) inlines.Add(new Run(lead) { FontSize = size });

                foreach (MarkdownRun run in line.Runs)
                    inlines.Add(new Run(run.Text)
                    {
                        FontSize   = size,
                        FontWeight = run.Bold   ? FontWeight.Bold  : FontWeight.Normal,
                        FontStyle  = run.Italic ? FontStyle.Italic : FontStyle.Normal,
                    });
            }

            if (i < lines.Count - 1) inlines.Add(new LineBreak());
        }
    }

    /// <summary>The decoded picture, or null to fall back to its alt text: a file that changed or broke
    /// since the document resolved it is not an error in a README.</summary>
    private static Bitmap? TryLoad(string path)
    {
        try { return new Bitmap(path); }
        catch (Exception) { return null; }
    }

    /// <summary>The same text with its LEADING spaces made non-breaking, so the layout keeps them.</summary>
    private static string Protected(string text)
    {
        int i = 0;
        while (i < text.Length && text[i] == ' ') i++;
        return i == 0 ? text : new string(Nbsp, i) + text[i..];
    }
}
