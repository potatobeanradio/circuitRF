namespace CircuitRF.Design.Revision;

/// <summary>
/// <b>The longer note a person wrote about an entry</b> (<c>docs/design/revision-control.md</c> §5.12;
/// RC-12 R-rc12-1, R-rc12-2).
///
/// <para><b>A title is one line and some decisions need a paragraph.</b> "Output match retuned" is
/// what a designer scans for six weeks later; <i>why</i> it was retuned, what was tried first and what
/// the measurement said is the part that cannot be recovered from the files. Both dialogs that record
/// something therefore ask for both, and the second is behind an expander because most entries do not
/// need one.</para>
///
/// <para><b>It lives in the BODY of the message the entry already carries</b>, for
/// <see cref="CheckpointMessage"/>'s reason: the metadata has to survive everything the object
/// survives, and a side file is a second thing to keep in step. It sits directly under the subject,
/// where anybody reading the escape hatch of §4.1 with <c>git log</c> finds it as ordinary prose —
/// which is where a body belongs in any case.</para>
///
/// <para><b>Its extent is COUNTED, not inferred</b> (R-rc12-2). The alternative is to recognise
/// circuitRF's own generated sentences and treat whatever is left as the note, and that fails silently
/// the first time one of those sentences is reworded: every workspace recorded before the change reads
/// its own explanation back as part of somebody's note. One integer in a trailer is exact, survives
/// any rewording, and is ignored on the way in by every reader that predates it.</para>
///
/// <para><b>A message with no note is written exactly as it was before this existed</b> — no block, no
/// trailer, byte for byte. The ordinary entry is the one nobody wrote a paragraph for, and it must not
/// grow a line saying so.</para>
/// </summary>
public static class MessageNotes
{
    /// <summary>How many lines of the body are the note. Absent when there is none.</summary>
    public const string LinesKey = CheckpointMessage.KeyPrefix + "Note-Lines";

    /// <summary>
    /// Where a note starts: the subject is line 0 and line 1 is blank, so the body begins at line 2.
    /// <b>Both builders put the note first in the body</b> precisely so this is a constant rather than
    /// a search.
    /// </summary>
    public const int FirstLine = 2;

    /// <summary>
    /// The note as lines, ready to write — trailing whitespace off each, and no blank line at either
    /// end. <b>Blank lines INSIDE are kept</b>: a note is prose, and a paragraph break is something the
    /// person put there.
    /// </summary>
    public static IReadOnlyList<string> Lines(string? note)
    {
        if (note is not { Length: > 0 }) return [];

        List<string> lines = [.. note.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                                     .Select(l => l.TrimEnd())];

        while (lines.Count > 0 && lines[0].Length == 0)                 lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)                lines.RemoveAt(lines.Count - 1);

        return lines;
    }

    /// <summary>The note as one string again, or an empty one. What a panel and a dialog both read.</summary>
    public static string Text(string? note) => string.Join('\n', Lines(note));

    /// <summary>
    /// <b>Where the note is in a message that was read back</b> — the half-open line range, or an empty
    /// one.
    ///
    /// <para><b>The count is looked for in the FINAL run of trailer lines and nowhere else.</b> A note
    /// is free text a person typed, so it can perfectly well contain a line that looks like one of
    /// circuitRF's trailers; reading the count from anywhere in the message would let a note describe
    /// its own extent. The trailing run is git's own definition of a trailer block, and both builders
    /// put a generated sentence between the note and it, so the note can never be part of it.</para>
    /// </summary>
    public static (int Start, int End) Extent(IReadOnlyList<string> lines)
    {
        int count = CountedLines(lines);
        if (count <= 0) return (0, 0);

        int start = FirstLine;
        int end   = Math.Min(lines.Count, start + count);
        return start >= end ? (0, 0) : (start, end);
    }

    /// <summary>The note a message carries, or an empty string.</summary>
    public static string Read(string? message)
    {
        string[] lines = (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var (start, end) = Extent(lines);
        return start == end ? "" : string.Join('\n', lines[start..end]).Trim();
    }

    /// <summary>Whether this line of a message is part of the note, so a trailer scan can step over
    /// it. A note line shaped like a trailer is text somebody wrote, never a key.</summary>
    public static bool IsNoteLine(int index, (int Start, int End) extent)
        => index >= extent.Start && index < extent.End;

    /// <summary>
    /// <b>Puts a different note into a message that already exists, leaving everything else exactly as
    /// it was</b> — the same rule R-rc11-4 states for a subject, and for the same reason: rebuilding
    /// the message from <see cref="CommitMessage.Build"/> would be correct for a commit circuitRF wrote
    /// and a lie about anything else on the line of work, adding circuitRF's own sentences to a commit
    /// that never carried them.
    ///
    /// <para>An empty <paramref name="note"/> removes the block and its trailer, which is how somebody
    /// takes back a paragraph they did not mean to write.</para>
    /// </summary>
    public static string Replace(string? message, string? note)
    {
        List<string> lines = [.. (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];

        // Out with the old: the counted block, the blank line that separated it from what follows, and
        // the trailer that said how long it was.
        var extent = Extent(lines);
        if (extent.Start != extent.End)
        {
            lines.RemoveRange(extent.Start, extent.End - extent.Start);
            if (extent.Start < lines.Count && lines[extent.Start].Trim().Length == 0)
                lines.RemoveAt(extent.Start);
        }

        lines.RemoveAll(IsCountTrailer);

        var wanted = Lines(note);
        if (wanted.Count == 0) return string.Join('\n', lines);

        // FirstLine is a constant and not a search, so the subject has to be line 0 for it to be true.
        // A message that opens with a blank line is somebody's own, and it loses those blanks here
        // rather than acquiring a note this reader would then place one line off.
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);

        if (lines.Count == 0)                lines.Add("");
        if (lines.Count == 1)                lines.Add("");
        else if (lines[1].Trim().Length > 0) lines.Insert(1, "");

        lines.InsertRange(FirstLine, wanted);
        lines.Insert(FirstLine + wanted.Count, "");

        // Into the trailing block, where CountedLines looks. Appended rather than placed beside the
        // other keys: this may be rewriting a message whose trailers it did not write and has no
        // business reordering.
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add(LinesKey + ": "
                + wanted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return string.Join('\n', lines) + "\n";
    }

    private static bool IsCountTrailer(string line)
    {
        if (!line.StartsWith(CheckpointMessage.KeyPrefix, StringComparison.Ordinal)) return false;
        int colon = line.IndexOf(':');
        return colon > 0 && line[..colon].Trim() == LinesKey;
    }

    /// <summary>
    /// The count in the trailing trailer block, or 0. Scans backwards over blank lines and trailer
    /// lines and stops at the first line that is neither — which is the generated sentence both
    /// builders write.
    /// </summary>
    private static int CountedLines(IReadOnlyList<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;

            if (!line.StartsWith(CheckpointMessage.KeyPrefix, StringComparison.Ordinal)
             || !line.Contains(':')) return 0;

            int colon = line.IndexOf(':');
            if (line[..colon].Trim() == LinesKey
             && int.TryParse(line[(colon + 1)..].Trim(), out int count)) return count;
        }

        return 0;
    }
}
