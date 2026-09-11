using System.Text;

namespace CircuitRF.Design.Revision;

/// <summary>
/// <b>A restore point's own metadata, carried in the message of the object that holds it</b>
/// (<c>docs/design/revision-control.md</c> §5.2b, §5.5; R-rc5-1e, R-rc5-9a).
///
/// <para><b>Why the message and not a side file.</b> The metadata has to survive everything the object
/// survives — an archive that copies the directory, a machine that crashed between two writes, a
/// reference somebody deleted by hand. A side file is a second thing to keep in step, and the one
/// state it cannot represent is the one that matters: an entry whose metadata went missing is
/// indistinguishable from an entry that never carried any.</para>
///
/// <para><b>Trailers, in git's own trailer shape</b> — <c>Key: value</c> lines in a final block — so
/// the escape hatch of §4.1 can read them with <c>git log</c> and nothing here needs a parser
/// anybody has to learn. Unrecognised keys are ignored on the way in, so a later stage may add one
/// without this reader changing.</para>
///
/// <para><b>No git vocabulary reaches this text</b> (R-rc0-6, R-rc5-2). What a designer might read in
/// an escape hatch is the same wording the restore-point list shows them.</para>
/// </summary>
public static class CheckpointMessage
{
    /// <summary>The trailer key prefix. Namespaced so a designer's own trailers cannot collide.</summary>
    public const string KeyPrefix = "CircuitRF-";

    public const string SequenceKey = KeyPrefix + "Sequence";
    public const string OriginKey   = KeyPrefix + "Origin";
    public const string IntentKey   = KeyPrefix + "Intent";
    public const string KeptKey     = KeyPrefix + "Kept";

    /// <summary>Repeatable: one line per workspace-relative path left out under R-rc5-15a.</summary>
    public const string LeftOutKey  = KeyPrefix + "Left-Out";

    /// <summary>§5.12's longer note, when somebody wrote one. See <see cref="MessageNotes"/>.</summary>
    public const string NoteLinesKey = MessageNotes.LinesKey;

    /// <summary>
    /// What the list shows when the user asked for a save-point and typed nothing (R-rc5-5a).
    /// <b>Never a bare time</b>: a time is what every entry already has, so an entry labelled only
    /// with one says nothing at all.
    /// </summary>
    public const string UnnamedSavePoint = "save-point";

    /// <summary>What an agent's batch is labelled when it declared no intent (R-rc5-6b).</summary>
    public const string UnnamedBatch = "an unnamed change";

    /// <summary>The subject line for each origin, given whatever label the caller has.</summary>
    public static string SubjectFor(CheckpointOrigin origin, string? label)
    {
        string trimmed = label?.Trim().ReplaceLineEndings(" ").Trim() ?? "";

        return origin switch
        {
            CheckpointOrigin.SavePoint       => trimmed.Length > 0 ? trimmed : UnnamedSavePoint,
            CheckpointOrigin.WorkspaceClosed => "closed",
            CheckpointOrigin.BeforeBatch     => "before: " + (trimmed.Length > 0 ? trimmed : UnnamedBatch),
            // Owner, 2026-09-08. These labels are no longer what identifies a row — the entry's own
            // short identity is, and it is on the row beside the time. So the label went back to naming
            // the KIND of moment in as few words as carry it, which is all a label can honestly do when
            // a workspace holds forty of them.
            //
            // "before going back" was tried once before and withdrawn (2026-09-07) because the menu
            // built from it read "Go back to 'before going back'". That is fixed at its source:
            // HistoryRowItem.GoBackText quotes only words a PERSON wrote, so a generated label reaches
            // no menu item now.
            CheckpointOrigin.BeforeRestore   => "before going back",
            CheckpointOrigin.RecordingOff    => "recording turned off",
            CheckpointOrigin.RecordingOn     => "recording turned back on",
            _                                => UnnamedSavePoint,
        };
    }

    /// <summary>The wire spelling of an origin. A file format — do not rename one; add one.</summary>
    public static string Spell(CheckpointOrigin origin) => origin switch
    {
        CheckpointOrigin.SavePoint       => "save-point",
        CheckpointOrigin.WorkspaceClosed => "workspace-closed",
        CheckpointOrigin.BeforeBatch     => "before-batch",
        CheckpointOrigin.BeforeRestore   => "before-restore",
        CheckpointOrigin.RecordingOff    => "recording-off",
        CheckpointOrigin.RecordingOn     => "recording-on",
        _                                => "save-point",
    };

    private static CheckpointOrigin Parse(string? spelled) => spelled?.Trim() switch
    {
        "workspace-closed" => CheckpointOrigin.WorkspaceClosed,
        "before-batch"     => CheckpointOrigin.BeforeBatch,
        "before-restore"   => CheckpointOrigin.BeforeRestore,
        "recording-off"    => CheckpointOrigin.RecordingOff,
        "recording-on"     => CheckpointOrigin.RecordingOn,
        _                  => CheckpointOrigin.SavePoint,
    };

    /// <summary>
    /// Builds the whole message: a subject, a plain sentence saying how it came about, then the
    /// trailer block.
    /// </summary>
    /// <param name="sequence">R-rc5-8's monotonic sequence. The wall clock supplies the label a human
    /// reads; it never decides what is oldest.</param>
    /// <param name="kept">Whether retention may never thin this one (R-rc5-1f).</param>
    /// <param name="leftOut">Workspace-relative paths left out at an unattended boundary
    /// (R-rc5-15a). Recorded here because the entry has to be able to say it is incomplete.</param>
    /// <param name="subject">
    /// The line the list shows, when the caller has one that <see cref="SubjectFor"/> would not produce
    /// (RC-11 R-rc11-3, R-rc11-4). Null takes the origin's own wording, which is every path but a
    /// rename.
    ///
    /// <para><b>Four of the six origins ignore <paramref name="label"/> entirely</b> — a
    /// workspace-close entry is "workspace closed" whatever anybody types — so a rename that went
    /// through the label alone would silently do nothing on exactly the entries a designer is most
    /// likely to want to name, which are the automatic ones they are trying to find again. The origin,
    /// the sequence, the kept mark and the left-out record are unaffected either way: the label is the
    /// only thing a rename moves.</para>
    /// </param>
    /// <param name="note">
    /// §5.12's longer note — what the label has no room for. Null or blank writes <b>nothing at
    /// all</b>, so an entry nobody wrote a paragraph for is byte-identical to the one this produced
    /// before notes existed. It is carried through every rebuild of the message for
    /// <paramref name="subject"/>'s reason: a mark-keep or a rename that dropped it would delete
    /// somebody's paragraph as a side effect of tidying a label.
    /// </param>
    public static string Build(
        CheckpointOrigin       origin,
        string?                label,
        long                   sequence,
        bool                   kept        = false,
        IReadOnlyList<string>? leftOut     = null,
        string?                subject     = null,
        string?                note        = null)
    {
        string line = subject?.ReplaceLineEndings(" ").Trim() is { Length: > 0 } given
                    ? given
                    : SubjectFor(origin, label);

        var noteLines = MessageNotes.Lines(note);

        var text = new StringBuilder();
        text.Append(line).Append('\n').Append('\n');

        // MessageNotes.FirstLine: directly under the subject, and always with a generated sentence
        // between it and the trailer block.
        foreach (string body in noteLines) text.Append(body).Append('\n');
        if (noteLines.Count > 0) text.Append('\n');

        text.Append(Explanation(origin)).Append('\n').Append('\n');

        text.Append(SequenceKey).Append(": ").Append(sequence).Append('\n');
        text.Append(OriginKey).Append(": ").Append(Spell(origin)).Append('\n');

        if (noteLines.Count > 0)
            text.Append(NoteLinesKey).Append(": ").Append(noteLines.Count).Append('\n');

        if (label?.Trim() is { Length: > 0 } intent)
            text.Append(IntentKey).Append(": ").Append(intent.ReplaceLineEndings(" ").Trim()).Append('\n');

        if (kept) text.Append(KeptKey).Append(": yes").Append('\n');

        foreach (string path in leftOut ?? [])
            if (path.Trim() is { Length: > 0 } p)
                text.Append(LeftOutKey).Append(": ").Append(p.ReplaceLineEndings(" ").Trim()).Append('\n');

        return text.ToString();
    }

    /// <summary>The one-line explanation under the subject — §5.5's "a line recording how it came
    /// about", in circuitRF's words rather than git's.
    ///
    /// <para><b>Public rather than private since RC-10</b>: §5.10's expander spells the origin out,
    /// and the sentence a designer reads there must be the same one the entry itself carries. Two
    /// wordings of one fact is how a panel and a record come to disagree about what happened.</para>
    /// </summary>
    public static string Explanation(CheckpointOrigin origin) => origin switch
    {
        CheckpointOrigin.SavePoint       => "You asked circuitRF to keep this state.",
        CheckpointOrigin.WorkspaceClosed => "circuitRF kept this state because the workspace was closed.",
        CheckpointOrigin.BeforeBatch     => "circuitRF kept this state before an assistant changed anything.",
        CheckpointOrigin.BeforeRestore   => "circuitRF kept this state before replacing it with an earlier one.",
        CheckpointOrigin.RecordingOff    => "This is the last state circuitRF kept before recording was switched off.",
        CheckpointOrigin.RecordingOn     => "This is the first state circuitRF kept after recording was switched back on.",
        _                                => "circuitRF kept this state.",
    };

    /// <summary>
    /// Reads back what <see cref="Build"/> wrote. <b>Never throws and never refuses</b>: a message
    /// this cannot understand still yields an entry, because a restore point whose metadata is
    /// unreadable is still a restore point and hiding it would be the silent loss §1.4 forbids.
    /// </summary>
    public static CheckpointMetadata Read(string message)
    {
        string   subject  = "";
        long?    sequence = null;
        string?  origin   = null;
        string?  intent   = null;
        bool     kept     = false;
        List<string> leftOut = [];

        var lines = (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // §5.12, and the same reason CommitMessage.Read steps over it: a note is free text, so one of
        // its lines may look like a trailer, and its first line would be read as the subject on an
        // entry whose own subject line was blank.
        var note = MessageNotes.Extent(lines);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (MessageNotes.IsNoteLine(i, note)) continue;

            if (subject.Length == 0 && line.Trim().Length > 0 && !IsTrailer(line))
                subject = line.Trim();

            if (!IsTrailer(line)) continue;

            int    colon = line.IndexOf(':');
            string key   = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case SequenceKey when long.TryParse(value, out long parsed): sequence = parsed; break;
                case OriginKey:  origin = value; break;
                case IntentKey:  intent = value; break;
                case KeptKey:    kept   = value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                       || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case LeftOutKey when value.Length > 0: leftOut.Add(value); break;
            }
        }

        var parsedOrigin = Parse(origin);

        // Three origins carry the mark by construction (§5.6 rule 6, R-rc6-5a) — a save-point, and the
        // pair that brackets an off period. Derived rather than only read, so an entry written before
        // the trailer existed is still marked, and so a hand-edited message cannot un-keep the pair
        // that gives a gap its ends.
        if (parsedOrigin is CheckpointOrigin.SavePoint
                         or CheckpointOrigin.RecordingOff
                         or CheckpointOrigin.RecordingOn) kept = true;

        return new CheckpointMetadata(subject, sequence, parsedOrigin, intent, kept, leftOut,
                                      note.Start == note.End
                                          ? ""
                                          : string.Join('\n', lines[note.Start..note.End]).Trim());
    }

    private static bool IsTrailer(string line)
        => line.StartsWith(KeyPrefix, StringComparison.Ordinal) && line.Contains(':');
}

/// <summary>What <see cref="CheckpointMessage.Read"/> found.</summary>
/// <param name="Subject">The line a designer reads — already free of git vocabulary.</param>
/// <param name="Sequence">R-rc5-8's ordering, or null on an entry that carried none.</param>
/// <param name="Origin">How it came about (§5.5).</param>
/// <param name="Intent">The label the user or the agent supplied, when there was one.</param>
/// <param name="Kept">Whether retention may thin it (R-rc5-1f).</param>
/// <param name="LeftOut">Paths left out at an unattended boundary (R-rc5-15a).</param>
/// <param name="Note">§5.12's longer note, or an empty string.</param>
public sealed record CheckpointMetadata(
    string                Subject,
    long?                 Sequence,
    CheckpointOrigin      Origin,
    string?               Intent,
    bool                  Kept,
    IReadOnlyList<string> LeftOut,
    string                Note = "");
