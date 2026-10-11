// The seam a schematic picture's words are read through — brief-img-9-text-and-values.md R-im9-1 (overview D1).
//
// The one reader that ships is the stroke-glyph matcher AS-10 reads silkscreen with (StrokeTextReader). D1 keeps it
// in-house; an operating system's own text recognition is deferred, not rejected, and this is where it would plug in:
// it receives a word's picture region and returns what the word says, and the grammar, the association and the report
// downstream do not change.

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>Reads one word region of a schematic picture.</summary>
public interface IImageTextReader
{
    /// <summary>
    /// What <paramref name="region"/> says: one word, or several where it is a line of words with spaces between
    /// them that read only apart ("C1 10pF"). Every word comes back — an unread one with its box and no reading. The
    /// <see cref="ImageWord.Id"/>s are left 0; <see cref="ImageText.Read"/> numbers them.
    /// </summary>
    /// <param name="strokeWidth">The picture's stroke width w, pixels.</param>
    IReadOnlyList<ImageWord> Read(TextRegion region, double strokeWidth);
}
