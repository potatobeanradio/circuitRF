// The board netlist a Gerber output set ships beside its artwork
// (docs/sonnet-briefs/brief-gi5-netlist-companion.md).
//
// THE PROBLEM THIS FILE EXISTS FOR: a Gerber import of a real multi-layer board prints three
// apologies in one run — that a via and a plated component hole are "indistinguishable from artwork
// alone", that a composited layer's "per-object net names are gone", and that "no layer span was
// declared" so every hole is assumed to go through the board. Every one of those is true about the
// ARTWORK. None of them is true about the FOLDER: a production output set routinely ships a board
// netlist beside its artwork, carrying a net name per pad, a plating flag per hole, the component
// reference and pin where there is one (whose ABSENCE is what identifies a via), and the layers each
// feature reaches. That file was classified "no Gerber or drill content in its head" and skipped.
//
// THE GOVERNING RULE, and the first thing to check in review (R-gi5-1): THE NETLIST IS EVIDENCE
// ABOUT THE ARTWORK. IT IS NEVER GEOMETRY. Nothing here creates a shape, moves a shape or deletes a
// shape. The Gerber and drill readers remain the sole source of every coordinate, every diameter and
// every outline; this file attaches FACTS to objects those readers already built, and reports
// anything it cannot attach. A netlist naming a pad the artwork does not have is a message with a
// count in it, never a pad.
//
// WRITTEN FROM PUBLIC DOCUMENTATION ONLY. IPC-D-356/356A is a standards-body interchange format in
// the same class as Gerber and Excellon; the root CLAUDE.md's rule against ingesting GPL sources
// applies here exactly as it does to those two, and nothing in this file names a tool, a product or
// a toolchain.
//
// THE ONE TRAP, AND IT IS THE USUAL CATASTROPHE (R-gi5-10): this format carries its own units AND
// its own RESOLUTION, in one header record, and the two resolutions in circulation for inches differ
// by a factor of ten. A netlist read at the wrong scale that matches nothing is at least loud; one
// read at a wrong scale that still lands inside the board MISLABELS EVERY PAD ON IT, silently. So
// the unit is read from the file, the inference is stated with its evidence, and the result is
// cross-checked against the artwork's own extent exactly as ExcellonReader.CrossCheckExtents already
// does for drill data. The cross-check is what separates the two failures.

using System.Globalization;

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>
/// The unit AND resolution pairs this format's own header record can name. One count of a coordinate
/// field is one of these, not one unit — which is the whole reason the enum exists rather than a
/// <see cref="GerberUnit"/>: the two inch resolutions differ by a factor of ten and choosing wrongly
/// between them puts every pad on a board a tenth of the way from where it is.
/// </summary>
public enum BoardNetlistUnits
{
    /// <summary>Inches, one count = 0.0001 in. What the format assumes when its header says nothing,
    /// so it is also this reader's default — stated as a default, never silently (R-gi5-10).</summary>
    InchTenThousandth,

    /// <summary>Millimetres, one count = 0.001 mm.</summary>
    MillimetreThousandth,

    /// <summary>Inches, one count = 0.00001 in.</summary>
    InchHundredThousandth,
}

/// <summary>How the units were settled — the same shape as <see cref="DrillFormatEvidence"/>, and for
/// the same reason: a guess and a declaration must never read the same (the GI series' standing rule
/// 2).</summary>
public enum BoardNetlistUnitsEvidence
{
    /// <summary>The file's own units record.</summary>
    Declared,

    /// <summary>Nothing in the file said, so the format's own default was taken.</summary>
    Defaulted,

    /// <summary>The declaration or the default disagreed with the artwork's extent, and this reading
    /// is the one that agrees (R-gi5-10's cross-check, used as evidence and not only printed).</summary>
    Artwork,
}

/// <summary>
/// One feature record: a pad, or a hole, at one coordinate, with the net at that coordinate and —
/// where the record has them — the component reference and pin it belongs to.
///
/// <para><b><see cref="Component"/> being null is the fact this phase turns on</b> (R-gi5-3): a
/// record carrying a component reference and a pin is a component hole; a record carrying a net and
/// no component reference is a via. That is the distinction <c>DrillViaPairing</c> declares
/// unavailable from artwork alone, and it is a field lookup here.</para>
/// </summary>
/// <param name="Code">The record's operation code, as written. Carried for the report and for
/// nothing else — this reader branches on WHICH FIELDS A RECORD HAS, never on its code, because a
/// code table is the part of a format most likely to have grown a value we have not heard of.</param>
/// <param name="Access">The access code, as written: 0 means the feature is reachable from both
/// outer surfaces (a through feature); n &gt; 0 names the one layer it is reachable from. Null when
/// the record did not state one. See <see cref="BoardNetlistEvidence.SpanFor"/> for what can and
/// cannot be made of it.</param>
public sealed record BoardNetlistRecord(
    int Code,
    string? Net,
    string? Component,
    string? Pin,
    bool Midpoint,
    long? DrillDbu,
    bool? Plated,
    int? Access,
    long X,
    long Y,
    int Line)
{
    /// <summary>Whether the record carried a drill field at all — a pad with no hole under it is a
    /// surface feature and can never be a via.</summary>
    public bool HasHole => DrillDbu is not null;

    /// <summary>R-gi5-3, read literally: a hole carrying a net and NO component reference.</summary>
    public bool IsVia => HasHole && Component is null;

    /// <summary>R-gi5-3, read literally: a hole carrying a component reference AND a pin.</summary>
    public bool IsComponentHole => HasHole && Component is not null && Pin is not null;

    /// <summary>A hole the rule above settles neither way — a reference with no pin on it. Rare, and
    /// deliberately NOT resolved by treating a bare reference as a component: a writer that puts a
    /// marker word in the reference field of a via would then have every via on the board counted as
    /// a component hole, silently. This bucket is counted and reported instead
    /// (<see cref="BoardNetlistEvidence"/>'s unclassified count), which is the only outcome a reader
    /// can check.</summary>
    public bool Unclassified => HasHole && !IsVia && !IsComponentHole;
}

/// <summary>Everything one netlist file turned out to be. <see cref="Refusal"/> non-null means
/// nothing was read and nothing may be used — the same contract <see cref="ExcellonReadResult"/>
/// states.</summary>
public sealed record BoardNetlist(
    string Path,
    string? Refusal,
    BoardNetlistUnits Units,
    BoardNetlistUnitsEvidence UnitsEvidence,
    string? Job,
    IReadOnlyList<BoardNetlistRecord> Records,
    int ConductorRecords,
    int OutlineRecords,
    int UnreadableRecords,
    DrillExtents Extents,
    IReadOnlyList<string> Diagnostics)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public IEnumerable<BoardNetlistRecord> Holes => Records.Where(r => r.HasHole);

    /// <summary>Every distinct net named anywhere in the file.</summary>
    public IReadOnlyCollection<string> Nets =>
        Records.Select(r => r.Net).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>R-gi5-10's sentence: the units, and what settled them.</summary>
    public string UnitsSummary => UnitsEvidence switch
    {
        BoardNetlistUnitsEvidence.Declared =>
            $"{BoardNetlistFile.Describe(Units)}, which the file declares",
        BoardNetlistUnitsEvidence.Artwork =>
            $"{BoardNetlistFile.Describe(Units)}, settled against the artwork's own extent",
        _ => $"{BoardNetlistFile.Describe(Units)} — the format's own default, because the file " +
             "states no units record",
    };
}

/// <summary>R-gi5-10's cross-check, and it is the same instrument
/// <see cref="ExcellonReader.CrossCheckExtents"/> is: a wrong resolution is a wrong SCALE, and a
/// wrong scale is visible against the artwork's bounding box even when every other source was
/// silent.</summary>
public sealed record BoardNetlistExtentsCheck(
    bool Agrees, int Outside, int Total, double WidthRatio, double HeightRatio, string Report);

public static class BoardNetlistFile
{
    /// <summary>How many feature records are needed before the kind is claimed on their own. Two, for
    /// the same reason <see cref="GerberDeclarationFile.MinimumKeywords"/> is two and
    /// <c>GerberFileClassifier.LooksLikeJobFile</c> demands a named key: one line that happens to
    /// begin with three digits and carry an X and a Y is a coincidence somebody's report can
    /// produce. <b>One record is enough when the file also carries a HEADER record</b> — a
    /// <c>P&#160;&#160;KEYWORD VALUE</c> line is this format's own and nothing else writes one, so
    /// the pair together is a stronger signature than two bare records.</summary>
    public const int MinimumRecords = 2;

    /// <summary>The record codes that carry CONDUCTOR geometry rather than a feature — a routed
    /// track and its continuation. Counted and reported, never read: R-gi5-1, and §7's rule that
    /// building a connectivity model from this data is a different piece of work.</summary>
    private static readonly int[] ConductorCodes = [378, 379];

    /// <summary>The record code for a board-outline segment. Counted and reported for the same
    /// reason.</summary>
    private const int OutlineCode = 389;

    public static string Describe(BoardNetlistUnits units) => units switch
    {
        BoardNetlistUnits.MillimetreThousandth => "millimetres in units of 0.001 mm",
        BoardNetlistUnits.InchHundredThousandth => "inches in units of 0.00001 in",
        _ => "inches in units of 0.0001 in",
    };

    // ── Recognition ───────────────────────────────────────────────────────────
    //
    // Runs AFTER the artwork and drill tests in GerberFileClassifier and before the declaration
    // test, which is the same ordering doctrine GI4 states: nothing here can take a file the drill
    // test already claimed, and the signature below is far more specific than the declaration file's
    // two-keyword minimum, so it goes first of the two.

    /// <summary>Whether <paramref name="head"/> is a board netlist, over text already in hand — the
    /// form <see cref="GerberFileClassifier.ClassifyContent"/> drives, so no fixture needs a
    /// temporary directory to assert what a byte stream is.</summary>
    public static bool Recognize(string head, out string why)
    {
        int records = 0, parameters = 0;
        foreach (string line in Lines(head))
        {
            if (IsParameterRecord(line, out _, out _)) { parameters++; continue; }
            if (ParseCode(line) is null) continue;
            if (ScanTail(line, out long _, out long _, out _, out _, out _, out _, out _)) records++;
        }

        if (records >= MinimumRecords || (records >= 1 && parameters >= 1))
        {
            why = $"a board netlist ({records} feature record(s)" +
                  (parameters > 0 ? $", {parameters} header record(s)" : "") + ")";
            return true;
        }

        why = "";
        return false;
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>Reads a netlist from text already in hand. <paramref name="unitsOverride"/> re-reads
    /// the same text at another resolution — what the artwork cross-check uses, and the only way the
    /// units are ever changed from what the file said.</summary>
    public static BoardNetlist Read(
        string path, string text, int dbuPerMicron,
        BoardNetlistUnits? unitsOverride = null,
        BoardNetlistUnitsEvidence? evidenceOverride = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        var diagnostics = new List<string>();

        var declared = DeclaredUnits(text, out string? job);
        var longNames = LongNetNames(text);
        var units = unitsOverride ?? declared ?? BoardNetlistUnits.InchTenThousandth;
        var evidence = evidenceOverride
                       ?? (unitsOverride is not null ? BoardNetlistUnitsEvidence.Artwork
                           : declared is not null ? BoardNetlistUnitsEvidence.Declared
                           : BoardNetlistUnitsEvidence.Defaulted);

        var records = new List<BoardNetlistRecord>();
        var extents = DrillExtents.Empty;
        int conductors = 0, outlines = 0, unreadable = 0, aliased = 0, line = 0;

        foreach (string raw in text.Split('\n'))
        {
            line++;
            string trimmed = raw.TrimEnd('\r');
            if (trimmed.Trim().Length == 0) continue;
            if (IsParameterRecord(trimmed, out _, out _)) continue;
            if (IsCommentRecord(trimmed)) continue;

            if (ParseCode(trimmed) is not { } code) continue;
            if (code == 999) break;                                  // the format's end-of-file record

            if (!ScanTail(trimmed, out long rawX, out long rawY, out long? rawDrill, out bool? plated,
                          out int? access, out bool midpoint, out bool sawCoordinates))
            {
                // A record whose code was read and whose coordinates were not is a record this reader
                // does not understand. Counted, never skipped in silence (R-L4f-8's discipline).
                if (sawCoordinates || code is >= 100 and <= 999) unreadable++;
                continue;
            }

            if (ConductorCodes.Contains(code)) { conductors++; continue; }
            if (code == OutlineCode) { outlines++; continue; }

            long x = ToDbu(rawX, units, dbuPerMicron);
            long y = ToDbu(rawY, units, dbuPerMicron);
            var (net, component, pin) = ParseHead(trimmed);

            // The net-name field is fourteen columns wide, so a longer name is written into the
            // header as an alias and the record carries the alias. Resolving it is not a nicety: a
            // net left as its alias is a net name that is WRONG rather than missing, and nothing
            // downstream would question it.
            if (net is not null && longNames.TryGetValue(net, out string? expanded)) { net = expanded; aliased++; }

            records.Add(new BoardNetlistRecord(
                code, net, component, pin, midpoint,
                rawDrill is { } d ? ToDbu(d, units, dbuPerMicron) : null,
                plated, access, x, y, line));
            extents = extents.Include(x, y);
        }

        if (records.Count == 0)
            return new BoardNetlist(
                full, NotThisFormat(text), units, evidence, job, [],
                conductors, outlines, unreadable, DrillExtents.Empty, diagnostics);

        if (conductors > 0)
            diagnostics.Add(
                $"{conductors:N0} conductor route record(s) were read as present and NOT used: they are " +
                "track geometry, and the artwork is the sole source of every coordinate in this import.");
        if (outlines > 0)
            diagnostics.Add(
                $"{outlines:N0} board-outline record(s) were read as present and not used, for the same " +
                "reason as the route records.");
        if (unreadable > 0)
            diagnostics.Add(
                $"{unreadable:N0} record(s) carried an operation code this reader understood but no " +
                "coordinate it could read, and were skipped.");
        if (aliased > 0)
            diagnostics.Add(
                $"{aliased:N0} record(s) name their net by an alias the header expands, because the " +
                "record's own net field is fourteen characters wide. The full names were used.");

        return new BoardNetlist(
            full, null, units, evidence, job, records, conductors, outlines, unreadable, extents,
            diagnostics);
    }

    /// <summary>
    /// The refusal for a file that held no feature record — which is nearly always a file that is
    /// not this format at all, rather than an empty one of it.
    /// </summary>
    /// <remarks>
    /// <b>It used to say only "holds no feature records that could be read"</b>, and a field report
    /// (2026-09-22) is what showed what that costs: a designer pointed the import at his own tool's
    /// part/net export, got that sentence, and had nothing to act on — it named neither what the
    /// file IS nor what railRF was hoping for. <see cref="PlacementFile"/> and <see cref="BomFile"/>
    /// have both run the import's own classifier over a file they could not read since they were
    /// written; this reader was the one of the three that did not, and there was no reason for it.
    ///
    /// <para><b>The format is named, and named as a STANDARD</b>. A board netlist here is IPC-D-356
    /// or its 356A revision — the file this reader's own header says it was written from public
    /// documentation of — and most CAD tools export it under a menu item of their own wording. A
    /// refusal that names the standard is one a user can search their own exporter for; naming a
    /// tool would be both wrong for every other reader and against the repo's own rule.</para>
    ///
    /// <para>Nothing about the artwork changes: a netlist railRF will not read leaves the import
    /// exactly where a board that shipped no netlist at all leaves it, which
    /// <see cref="ReadFile"/>'s own contract already states.</para>
    /// </remarks>
    private static string NotThisFormat(string text)
    {
        var kind = GerberFileClassifier.ClassifyContent("", text);

        // What it IS, where anything recognised it. A file the import can name is very often one
        // the user pointed at the wrong row of the dialog with, and saying so is the whole fix.
        string what = kind.Kind == GerberFileKind.Other
            ? "it is not any file kind circuitRF recognises"
            : $"it reads as {kind.Why}";

        return $"it holds no feature records, so {what}. railRF reads a board netlist in the "
             + "IPC-D-356/356A interchange format — the one that carries a net name per pad, a "
             + "plating flag per hole and the reference designator and pin where there is one. Most "
             + "CAD tools export it beside the Gerbers; it is commonly written .ipc, .d356, .356 or "
             + ".net. Naming no netlist at all is an ordinary state and imports fine.";
    }

    /// <summary>Reads a netlist from disk. A netlist that cannot be read is not a reason to fail an
    /// import of the artwork beside it — the same contract <see cref="GerberDeclarationFile.ReadFile"/>
    /// keeps.</summary>
    public static BoardNetlist? ReadFile(string path, int dbuPerMicron)
    {
        try
        {
            return Read(path, File.ReadAllText(path), dbuPerMicron);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The other resolutions this file could have been written at — what
    /// <see cref="CrossCheckExtents"/>'s caller tries when the declared or defaulted one disagrees
    /// with the artwork. Ordered by how far each is from the reading that failed, so the cheapest
    /// mistake is tried first.</summary>
    public static IEnumerable<BoardNetlistUnits> Alternatives(BoardNetlistUnits taken)
    {
        foreach (var candidate in new[]
                 {
                     BoardNetlistUnits.InchTenThousandth,
                     BoardNetlistUnits.MillimetreThousandth,
                     BoardNetlistUnits.InchHundredThousandth,
                 })
            if (candidate != taken) yield return candidate;
    }

    /// <summary>
    /// R-gi5-10. Compares the netlist's own coordinates against the artwork's bounding box, and
    /// reports the disagreement as a NUMBER — "the units look wrong" tells a user nothing they can
    /// act on, and "the netlist spans 25.4× the artwork" names the fix.
    ///
    /// <para>Deliberately the same instrument as <see cref="ExcellonReader.CrossCheckExtents"/>, with
    /// the same margin rule, because it is answering the same question about a different companion
    /// file. It is NOT the same method: the two report different things and share no counters, and a
    /// shared one would have to be told which noun to print.</para>
    /// </summary>
    public static BoardNetlistExtentsCheck CrossCheckExtents(BoardNetlist netlist, DrillExtents artwork)
    {
        if (!netlist.Extents.HasAny || !artwork.HasAny)
            return new BoardNetlistExtentsCheck(true, 0, 0, 1, 1,
                "No cross-check was possible: one of the two sets is empty.");

        long margin = Math.Max(artwork.Width, artwork.Height) / 100;
        int outside = 0, total = 0;
        foreach (var record in netlist.Records)
        {
            total++;
            if (!artwork.Contains(record.X, record.Y, margin)) outside++;
        }

        double wr = artwork.Width == 0 ? 1 : (double)netlist.Extents.Width / artwork.Width;
        double hr = artwork.Height == 0 ? 1 : (double)netlist.Extents.Height / artwork.Height;

        // A handful of records outside the artwork's own centreline extent is ordinary — a fiducial
        // or a tooling hole sits outside the copper. A wrong SCALE is not a handful; it is most of
        // them. So the test is a proportion, not a zero: 10 % is far above what a real board's
        // edge features produce and orders of magnitude below what any resolution error does.
        bool agrees = outside * 10 <= total;

        // A netlist whose records all sit at one point has no span, so a ratio against the artwork's
        // would be a zero that reads like a catastrophic scale error rather than like the absence of
        // a measurement. Said as an absence instead.
        bool measurable = netlist.Extents.Width > 0 && netlist.Extents.Height > 0;
        string spans = measurable
            ? $"the two spans differ by a factor of {wr:0.###} in X and {hr:0.###} in Y"
            : "the netlist's own records span no area, so there is nothing to compare it against";

        string report = agrees
            ? $"The netlist agrees with the artwork: {total - outside} of {total} record(s) fall inside " +
              $"the artwork extent, and {spans}."
            : $"The netlist DISAGREES with the artwork read as {Describe(netlist.Units)}: {outside} of " +
              $"{total} record(s) fall outside the artwork extent, and {spans}.";

        return new BoardNetlistExtentsCheck(agrees, outside, total, wr, hr, report);
    }

    // ── The units record ──────────────────────────────────────────────────────

    /// <summary>The header's units record, and the job name beside it. The three unit/resolution
    /// pairs are named by a customer code in the format's own header — <c>0</c> and <c>2</c> are the
    /// two inch resolutions, <c>1</c> is the metric one — and a header that spells the unit out in
    /// words instead is read for the unit and takes that unit's usual resolution.</summary>
    private static BoardNetlistUnits? DeclaredUnits(string text, out string? job)
    {
        job = null;
        BoardNetlistUnits? units = null;

        foreach (string line in Lines(text))
        {
            if (!IsParameterRecord(line, out string key, out string value)) continue;

            if (key.Equals("JOB", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                job ??= value;

            if (!key.Equals("UNITS", StringComparison.OrdinalIgnoreCase)) continue;
            if (units is not null) continue;

            string v = value.ToUpperInvariant();
            int code = -1;
            for (int i = 0; i < v.Length; i++)
                if (char.IsAsciiDigit(v[i])) { code = v[i] - '0'; break; }

            units = code switch
            {
                0 => BoardNetlistUnits.InchTenThousandth,
                1 => BoardNetlistUnits.MillimetreThousandth,
                2 => BoardNetlistUnits.InchHundredThousandth,
                _ => v.Contains("METRIC", StringComparison.Ordinal) || v.Contains("MM", StringComparison.Ordinal)
                    ? BoardNetlistUnits.MillimetreThousandth
                    : v.Contains("INCH", StringComparison.Ordinal) || v.Contains("ENGLISH", StringComparison.Ordinal)
                        ? BoardNetlistUnits.InchTenThousandth
                        : null,
            };
        }

        return units;
    }

    /// <summary>
    /// The header's long-net-name table. A net name longer than the record's fourteen-column field is
    /// written into the header and the record carries an alias, so a reader that ignores the table
    /// reports a WRONG net name rather than a missing one.
    ///
    /// <para>Both spellings in circulation are accepted — the alias as the header keyword itself, and
    /// the alias split into a keyword and an index — because the difference between them is
    /// whitespace and getting it wrong costs every long net name in the file.</para>
    /// </summary>
    private static Dictionary<string, string> LongNetNames(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string line in Lines(text))
        {
            if (!IsParameterRecord(line, out string key, out string value)) continue;
            if (!key.StartsWith("NNAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.Length == 0) continue;

            if (key.Length > 5) { map.TryAdd(key, value); continue; }

            int cut = value.IndexOf(' ', StringComparison.Ordinal);
            if (cut <= 0) continue;
            map.TryAdd(key + value[..cut], value[(cut + 1)..].Trim());
        }

        return map;
    }

    /// <summary>One count of a coordinate field, as DBU. Decimal throughout, never double — the same
    /// exactness rule <see cref="LayoutUnits"/>'s own header states, and 0.0001 in is not
    /// representable in binary floating point.</summary>
    private static long ToDbu(long counts, BoardNetlistUnits units, int dbuPerMicron) => units switch
    {
        BoardNetlistUnits.MillimetreThousandth =>
            LayoutUnits.ToDbu(counts * 0.001m, LayoutUnit.Mm, dbuPerMicron),
        BoardNetlistUnits.InchHundredThousandth =>
            LayoutUnits.ToDbu(counts * 0.00001m, LayoutUnit.Inch, dbuPerMicron),
        _ => LayoutUnits.ToDbu(counts * 0.0001m, LayoutUnit.Inch, dbuPerMicron),
    };

    // ── Record shapes ─────────────────────────────────────────────────────────

    private static IEnumerable<string> Lines(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length > 0) yield return line;
        }
    }

    /// <summary>A header record: <c>P</c> in the first column, then a keyword and its value.</summary>
    private static bool IsParameterRecord(string line, out string key, out string value)
    {
        key = value = "";
        if (line.Length < 3 || line[0] is not ('P' or 'p') || !char.IsWhiteSpace(line[1])) return false;

        string rest = line[1..].Trim();
        int cut = rest.IndexOf(' ', StringComparison.Ordinal);
        if (cut < 0) { key = rest; return key.Length > 0; }
        key = rest[..cut];
        value = rest[(cut + 1)..].Trim();
        return key.Length > 0;
    }

    private static bool IsCommentRecord(string line) =>
        line.Length > 0 &&
        (line[0] == ';' ||
         (line[0] is 'C' or 'c' && (line.Length == 1 || char.IsWhiteSpace(line[1]))));

    /// <summary>The three-digit operation code in the first three columns. Null for anything
    /// else — which is what keeps a line of prose out of the record stream.</summary>
    private static int? ParseCode(string line)
    {
        if (line.Length < 3) return null;
        if (!char.IsAsciiDigit(line[0]) || !char.IsAsciiDigit(line[1]) || !char.IsAsciiDigit(line[2]))
            return null;
        return ((line[0] - '0') * 100) + ((line[1] - '0') * 10) + (line[2] - '0');
    }

    /// <summary>Where a record's letter-tagged tail begins — column 31 in the format's own column
    /// table, which is where the fixed-width net-name and reference-designator fields end.</summary>
    private const int TailColumn = 30;

    /// <summary>
    /// The fixed-width head: the net name, and the reference designator and pin that decide
    /// R-gi5-3's whole question.
    ///
    /// <para>Read by COLUMN, because that is what this format is — but the reference designator and
    /// its pin are taken as one span split on the hyphen between them rather than as two exact
    /// column ranges, because the designator field's width is written both six and seven columns
    /// wide in the sets in circulation and a one-column error there silently truncates every
    /// reference on the board.</para>
    /// </summary>
    private static (string? Net, string? Component, string? Pin) ParseHead(string line)
    {
        string? net = Slice(line, 3, 14);

        string? component = null, pin = null;
        if (Slice(line, 19, TailColumn - 19) is { } refField)
        {
            int dash = refField.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                component = Blank(refField[..dash]);
                pin = Blank(refField[(dash + 1)..]);
            }
            else component = Blank(refField);
        }

        return (net, component, pin);
    }

    private static string? Slice(string line, int start, int length)
    {
        if (line.Length <= start) return null;
        return Blank(line.Substring(start, Math.Min(length, line.Length - start)));
    }

    private static string? Blank(string s) => s.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>
    /// The letter-tagged tail: an optional midpoint flag, the drill field and its plating letter, the
    /// access code, the coordinates, and the feature's own size and rotation.
    ///
    /// <para><b>Read as an ORDERED tag stream rather than by exact column</b>, which is the one place
    /// this reader deliberately departs from the column table. The tail's field ORDER is fixed by the
    /// format and the fields are self-identifying; their exact columns are not honoured by every
    /// writer, and a coordinate read one column short is a coordinate wrong by a factor of ten. The
    /// order is what disambiguates the two X/Y pairs: the first is the location, the second is the
    /// feature's own width and height, which this reader records nothing from — the artwork is the
    /// sole source of every dimension (R-gi5-1).</para>
    /// </summary>
    private static bool ScanTail(
        string line, out long x, out long y, out long? drill, out bool? plated, out int? access,
        out bool midpoint, out bool sawCoordinates)
    {
        x = y = 0;
        drill = null;
        plated = null;
        access = null;
        midpoint = false;
        sawCoordinates = false;
        bool sawX = false, sawY = false;

        for (int i = Math.Min(TailColumn, line.Length); i < line.Length; i++)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c)) continue;

            switch (c)
            {
                case 'M' or 'm':
                    midpoint = true;
                    break;

                case 'D' or 'd':
                {
                    long? value = ReadDigits(line, ref i);
                    if (value is null) break;
                    drill = value;
                    if (i + 1 < line.Length && line[i + 1] is 'P' or 'p') { plated = true; i++; }
                    else if (i + 1 < line.Length && line[i + 1] is 'U' or 'u') { plated = false; i++; }
                    break;
                }

                case 'A' or 'a':
                {
                    long? value = ReadDigits(line, ref i);
                    if (value is not null and <= int.MaxValue) access = (int)value.Value;
                    break;
                }

                case 'X' or 'x':
                {
                    long? value = ReadSigned(line, ref i);
                    if (value is null) break;
                    if (!sawX) { x = value.Value; sawX = true; }
                    break;                                   // the second X is the feature's width
                }

                case 'Y' or 'y':
                {
                    long? value = ReadSigned(line, ref i);
                    if (value is null) break;
                    if (!sawY) { y = value.Value; sawY = true; }
                    break;                                   // the second Y is the feature's height
                }

                default:
                    // R (rotation) and S (solder-mask) carry values this import has no use for; every
                    // other letter is something this reader does not know. Both are stepped over the
                    // same way, so an unrecognized tag cannot swallow the field after it.
                    ReadDigits(line, ref i);
                    break;
            }
        }

        sawCoordinates = sawX || sawY;
        return sawX && sawY;
    }

    /// <summary>Consumes the digits after a tag, leaving <paramref name="i"/> on the last one so the
    /// caller's own <c>i++</c> lands on the next tag.</summary>
    private static long? ReadDigits(string line, ref int i)
    {
        int start = i + 1;
        int end = start;
        while (end < line.Length && char.IsAsciiDigit(line[end])) end++;
        if (end == start) return null;
        i = end - 1;
        return long.TryParse(line.AsSpan(start, end - start), NumberStyles.None,
                             CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    private static long? ReadSigned(string line, ref int i)
    {
        int start = i + 1;
        int sign = 1;
        if (start < line.Length && line[start] is '+' or '-')
        {
            if (line[start] == '-') sign = -1;
            start++;
        }

        int end = start;
        while (end < line.Length && char.IsAsciiDigit(line[end])) end++;
        if (end == start) return null;
        i = end - 1;
        return long.TryParse(line.AsSpan(start, end - start), NumberStyles.None,
                             CultureInfo.InvariantCulture, out long value) ? sign * value : null;
    }
}

/// <summary>
/// What the netlist says about the hole at one coordinate, merged across every record that landed
/// there — a through-hole pad is written once per layer it is accessible from, so one hole is
/// several records and the facts have to be unioned before anything can be asked of them.
/// </summary>
public sealed record NetlistHoleFact(
    string? Net,
    string? Component,
    string? Pin,
    bool? Plated,
    long? DrillDbu,
    IReadOnlyList<int> AccessCodes,
    int RecordCount)
{
    public bool IsVia => Component is null;
    public bool IsComponentHole => Component is not null && Pin is not null;

    /// <summary>Neither — see <see cref="BoardNetlistRecord.Unclassified"/>.</summary>
    public bool Unclassified => !IsVia && !IsComponentHole;
}

/// <summary>
/// The netlist's hole records, indexed by coordinate so a drill hit can ask what the netlist said
/// about it. <b>Pure</b>, like <see cref="DrillViaPairing"/> itself: facts in, facts out, no file
/// system and no Messages — which is what lets the pairing take one without growing a dependency on
/// anything that reads a folder.
/// </summary>
public sealed class NetlistHoleIndex
{
    private readonly record struct Entry(long X, long Y, NetlistHoleFact Fact);

    private readonly Dictionary<(long X, long Y), List<Entry>> _byCell = new();
    private readonly long _tolerance;

    /// <summary>R-gi5-7's stated tolerance, in DBU.</summary>
    public long ToleranceDbu => _tolerance;

    public int Count { get; }

    internal NetlistHoleIndex(IEnumerable<(long X, long Y, NetlistHoleFact Fact)> facts, long tolerance)
    {
        _tolerance = Math.Max(1, tolerance);
        int n = 0;
        foreach (var (x, y, fact) in facts)
        {
            var key = (Cell(x), Cell(y));
            if (!_byCell.TryGetValue(key, out var list)) _byCell[key] = list = [];
            list.Add(new Entry(x, y, fact));
            n++;
        }
        Count = n;
    }

    /// <summary>What the netlist said about the hole at <paramref name="x"/>, <paramref name="y"/>, or
    /// null when it said nothing about that coordinate at all — which R-gi5-3 requires be COUNTED
    /// rather than filled in with a guess.</summary>
    public NetlistHoleFact? At(long x, long y)
    {
        NetlistHoleFact? best = null;
        long bestDistance = long.MaxValue;
        long cx = Cell(x), cy = Cell(y);

        for (long gx = cx - 1; gx <= cx + 1; gx++)
        for (long gy = cy - 1; gy <= cy + 1; gy++)
        {
            if (!_byCell.TryGetValue((gx, gy), out var list)) continue;
            foreach (var entry in list)
            {
                long dx = entry.X - x, dy = entry.Y - y;
                if (Math.Abs(dx) > _tolerance || Math.Abs(dy) > _tolerance) continue;
                long d2 = (dx * dx) + (dy * dy);
                if (d2 < bestDistance) { best = entry.Fact; bestDistance = d2; }
            }
        }

        return best;
    }

    private long Cell(long v) => (long)Math.Floor((double)v / _tolerance);
}

/// <summary>R-gi5-13's counts, and the one summary line they are printed as.</summary>
public sealed record NetlistAttachment(
    int Records,
    int WithNet,
    int Containment,
    int Near,
    int Unmatched,
    int MultiShape,
    int ShapesNamed,
    int Ambiguous,
    int Disagreed,
    int AlreadyNamed,
    long ToleranceDbu,
    IReadOnlyList<string> Messages);

/// <summary>
/// The netlist files of one import, and everything the rest of the import asks of them.
///
/// <para>Scoped exactly as <see cref="GerberCompanionFiles"/> is: to the files the caller handed
/// over, and no further. Nothing here opens a folder or reaches outside one.</para>
/// </summary>
public sealed class BoardNetlistEvidence
{
    private readonly List<BoardNetlist> _netlists = [];
    private readonly List<string> _messages = [];
    private readonly int _dbuPerMicron;
    private NetlistHoleIndex? _holes;

    private BoardNetlistEvidence(int dbuPerMicron) => _dbuPerMicron = dbuPerMicron;

    /// <summary>Every netlist that was recognised, usable or not.</summary>
    public IReadOnlyList<BoardNetlist> Netlists => _netlists;

    public IReadOnlyList<string> Messages => _messages;

    /// <summary>The netlists that may actually be used — a refused one, or one the artwork
    /// cross-check could not reconcile, is reported and then contributes nothing (R-gi5-10).</summary>
    public IReadOnlyList<BoardNetlist> Usable { get; private set; } = [];

    public bool Any => Usable.Count > 0;

    /// <summary>An EMPTY evidence set, for the overwhelmingly common case of a set with no netlist in
    /// it. R-gi5-2: everything this phase contributes is additive, and this is the object that makes
    /// "absent" cost nothing rather than being special-cased at every call site.</summary>
    public static BoardNetlistEvidence None(int dbuPerMicron) => new(dbuPerMicron);

    public static BoardNetlistEvidence Read(IEnumerable<GerberFileClass> files, int dbuPerMicron)
    {
        var set = new BoardNetlistEvidence(dbuPerMicron);
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var netlist = BoardNetlistFile.ReadFile(file.Path, dbuPerMicron);
            if (netlist is null)
            {
                set._messages.Add($"{file.FileName} is a board netlist but could not be read, so nothing was taken from it.");
                continue;
            }
            set._netlists.Add(netlist);
            if (netlist.Refusal is not null)
                set._messages.Add($"{netlist.FileName} {netlist.Refusal}");
        }

        set.Usable = [.. set._netlists.Where(n => n.Refusal is null)];
        return set;
    }

    /// <summary>
    /// R-gi5-10, and the reason it is a separate step from <see cref="Read"/>: the artwork's own
    /// extent is the strongest evidence available about the netlist's resolution, and it does not
    /// exist until the artwork has been read.
    ///
    /// <para>A netlist that disagrees is RE-READ at each of the other resolutions the format defines,
    /// and one that agrees is taken — the same ladder <c>GerberImport</c> already climbs for a drill
    /// file, and for the same reason. A netlist that agrees at NO resolution is reported and then
    /// used for nothing, because a netlist matched at the wrong scale mislabels every pad it
    /// touches.</para>
    /// </summary>
    public void CrossCheck(DrillExtents artwork)
    {
        if (_netlists.Count == 0) return;

        if (!artwork.HasAny)
        {
            // A drill-only set: there is no artwork extent to check against, and saying so is the
            // whole of what can honestly be said. The netlist is still used — the check is evidence,
            // not a precondition — and this sentence is what tells a reader the strongest source was
            // unavailable rather than silent.
            foreach (var netlist in Usable)
                _messages.Add(
                    $"{netlist.FileName}: read in {netlist.UnitsSummary}. No cross-check against the " +
                    "artwork was possible — this set has none — so nothing confirmed the resolution.");
            ReportDiagnostics();
            return;
        }

        var kept = new List<BoardNetlist>();
        foreach (var netlist in _netlists)
        {
            if (netlist.Refusal is not null) continue;

            var check = BoardNetlistFile.CrossCheckExtents(netlist, artwork);
            if (check.Agrees)
            {
                kept.Add(netlist);
                _messages.Add($"{netlist.FileName}: read in {netlist.UnitsSummary}. {check.Report}");
                continue;
            }

            BoardNetlist? settled = null;
            foreach (var candidate in BoardNetlistFile.Alternatives(netlist.Units))
            {
                var retry = ReadAt(netlist, candidate);
                if (retry is null || retry.Refusal is not null) continue;
                if (!BoardNetlistFile.CrossCheckExtents(retry, artwork).Agrees) continue;
                settled = retry;
                break;
            }

            if (settled is not null)
            {
                kept.Add(settled);
                _messages.Add(
                    $"{netlist.FileName}: {check.Report} It was settled against the artwork instead, as " +
                    $"{BoardNetlistFile.Describe(settled.Units)} — under which its records land on the board.");
                continue;
            }

            // NOT a refusal of the import, and not a repair either: the file is real and something
            // about it is wrong, so it is named and dropped. Matching it anyway is the one outcome
            // R-gi5-10 exists to prevent.
            _messages.Add(
                $"{netlist.FileName}: {check.Report} No resolution this format defines puts its records " +
                "on this board, so nothing was taken from it — it is most likely a netlist for a " +
                "different board or a different revision.");
        }

        Usable = kept;
        _holes = null;
        for (int i = 0; i < _netlists.Count; i++)
        {
            var replacement = kept.FirstOrDefault(k => string.Equals(k.Path, _netlists[i].Path, StringComparison.OrdinalIgnoreCase));
            if (replacement is not null) _netlists[i] = replacement;
        }

        ReportDiagnostics();
    }

    private void ReportDiagnostics()
    {
        foreach (var netlist in Usable)
            foreach (string diagnostic in netlist.Diagnostics)
                _messages.Add($"{netlist.FileName}: {diagnostic}");
    }

    private BoardNetlist? ReadAt(BoardNetlist netlist, BoardNetlistUnits units)
    {
        try
        {
            return BoardNetlistFile.Read(
                netlist.Path, File.ReadAllText(netlist.Path), _dbuPerMicron, units,
                BoardNetlistUnitsEvidence.Artwork);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// R-gi5-7's tolerance, stated rather than assumed: <b>one count of the netlist's own coordinate
    /// resolution</b>, floored at one micron.
    ///
    /// <para>It has to be derived from the resolution rather than fixed, because the coarsest
    /// resolution this format defines (0.0001 in = 2.54 µm) is COARSER than the one-micron snap
    /// <see cref="DrillViaPairing.SnapMicrons"/> uses between artwork and drill data — a netlist
    /// coordinate is up to half a count from the pad it names, so a fixed one-micron tolerance would
    /// have missed real matches on every inch-resolution set. It is still two orders of magnitude
    /// below the tightest pad pitch in circulation, which is the bound that matters.</para>
    /// </summary>
    public long ToleranceDbu
    {
        get
        {
            if (Usable.Count == 0) return 0;
            long counts = Usable.Max(n => OneCount(n.Units));
            long oneMicron = _dbuPerMicron;
            return Math.Max(counts, oneMicron);
        }
    }

    /// <summary>The tolerance in the words the import prints.</summary>
    public string ToleranceText =>
        $"{(double)ToleranceDbu / _dbuPerMicron:0.###} µm";

    private long OneCount(BoardNetlistUnits units) => units switch
    {
        BoardNetlistUnits.MillimetreThousandth => LayoutUnits.ToDbu(0.001m, LayoutUnit.Mm, _dbuPerMicron),
        BoardNetlistUnits.InchHundredThousandth => LayoutUnits.ToDbu(0.00001m, LayoutUnit.Inch, _dbuPerMicron),
        _ => LayoutUnits.ToDbu(0.0001m, LayoutUnit.Inch, _dbuPerMicron),
    };

    /// <summary>The hole facts, merged across files and across the several records one hole
    /// produces. Built once and reused — the pairing asks it once per drill hit.</summary>
    public NetlistHoleIndex Holes => _holes ??= BuildHoles();

    private NetlistHoleIndex BuildHoles()
    {
        long tolerance = Math.Max(1, ToleranceDbu);
        var merged = new Dictionary<(long, long), List<BoardNetlistRecord>>();

        foreach (var netlist in Usable)
            foreach (var record in netlist.Holes)
            {
                var key = ((long)Math.Floor((double)record.X / tolerance),
                           (long)Math.Floor((double)record.Y / tolerance));
                if (!merged.TryGetValue(key, out var list)) merged[key] = list = [];
                list.Add(record);
            }

        var facts = new List<(long, long, NetlistHoleFact)>();
        foreach (var group in merged.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
        {
            var records = group.Value;
            string? net = Single(records.Select(r => r.Net));
            string? component = records.Select(r => r.Component).OfType<string>().FirstOrDefault();
            string? pin = records.Where(r => r.Component is not null).Select(r => r.Pin).OfType<string>().FirstOrDefault();
            bool? plated = Single(records.Select(r => r.Plated));
            long? drill = records.FirstOrDefault(r => r.DrillDbu is not null)?.DrillDbu;
            var access = records.Select(r => r.Access).OfType<int>().Distinct().OrderBy(a => a).ToList();

            facts.Add((records[0].X, records[0].Y,
                       new NetlistHoleFact(net, component, pin, plated, drill, access, records.Count)));
        }

        return new NetlistHoleIndex(facts, tolerance);
    }

    private static T? Single<T>(IEnumerable<T> values)
    {
        var distinct = values.Where(v => v is not null).Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : default;
    }

    // ── R-gi5-4: plating ──────────────────────────────────────────────────────

    /// <summary>
    /// Settles the plating a drill file left unstated, from the netlist's own per-record flag.
    ///
    /// <para><b>Three ranks, and the middle one is R-gi5-4's whole point.</b> What the DRILL FILE
    /// itself declared always wins — a file is authoritative about itself, the same rule GI4's
    /// R-gi4-3 already states. Where the file said nothing and the TOOL LISTING did, the netlist is a
    /// second companion of equal standing: if the two agree the value stands, and <b>if they disagree
    /// NEITHER is applied</b> and both are named, because picking one would be exactly the silent
    /// guess this series exists to remove. Where neither companion spoke, the netlist settles
    /// it.</para>
    ///
    /// <para><paramref name="fileOwn"/> is the read as it was BEFORE any tool listing was applied,
    /// which is the only way to tell a value the drill file declared from a value a companion
    /// contributed.</para>
    /// </summary>
    public ExcellonReadResult ApplyPlating(
        ExcellonReadResult read, ExcellonReadResult fileOwn, out IReadOnlyList<string> notes)
    {
        var said = new List<string>();
        notes = said;
        if (!Any || read.Refusal is not null || read.Tools.Count == 0) return read;

        var own = fileOwn.Tools.ToDictionary(t => t.Number, t => t.Plated);
        var holes = Holes;
        var tools = new List<DrillTool>(read.Tools.Count);
        var takenFrom = new List<string>();
        var disagreed = new List<string>();
        int uncovered = 0;

        foreach (var tool in read.Tools)
        {
            bool? netlistSays = PlatingFromNetlist(read, tool.Number, holes, out int covered);
            if (covered == 0) uncovered++;

            bool? declaredByFile = own.TryGetValue(tool.Number, out bool? d) ? d : null;

            if (declaredByFile is { } fileSays)
            {
                if (netlistSays is { } n1 && n1 != fileSays)
                    said.Add($"the netlist states tool T{tool.Number} is {Plating(n1)}, which DISAGREES with " +
                             "the drill file's own statement. The drill file was preferred — a file is " +
                             "authoritative about itself.");
                tools.Add(tool);
                continue;
            }

            if (tool.Plated is { } fromListing)
            {
                if (netlistSays is { } n2 && n2 != fromListing)
                {
                    // R-gi5-4: neither companion outranks the other, so neither wins. The tool goes
                    // back to unstated, which downstream means "plated" — the safe reading, and the
                    // one it already had before either companion spoke.
                    disagreed.Add($"T{tool.Number} (listing: {Plating(fromListing)}, netlist: {Plating(n2)})");
                    tools.Add(tool with { Plated = null });
                    continue;
                }
                tools.Add(tool);
                continue;
            }

            if (netlistSays is { } settled)
            {
                takenFrom.Add($"T{tool.Number} {(settled ? "plated" : "NON-PLATED")}");
                tools.Add(tool with { Plated = settled });
                continue;
            }

            tools.Add(tool);
        }

        if (disagreed.Count > 0)
            said.Add(
                $"the tool listing and the netlist disagree about {disagreed.Count} tool(s) — " +
                string.Join(", ", disagreed) + " — so NEITHER was applied to them and they stay " +
                "unstated. One of the two files is from a different revision of this job; the drill " +
                "file itself states nothing either way.");

        if (takenFrom.Count == 0 && disagreed.Count == 0)
        {
            if (uncovered == read.Tools.Count && said.Count == 0)
                said.Add("the netlist covers none of this file's holes, so it settled no plating for it.");
            return read;
        }

        // Rebuilt from the DRILL FILE'S OWN hits rather than patched onto the listing's, because a
        // disagreement RETRACTS a value the listing had already written onto them. Patching only
        // fills nulls, so a retraction would have left the hole plated and the tool unstated — two
        // halves of one file disagreeing with each other.
        var platingByTool = tools.ToDictionary(t => t.Number, t => t.Plated);
        var hits = fileOwn.Hits
            .Select(h => h.Plated is null && platingByTool.TryGetValue(h.Tool, out bool? p) && p is not null
                ? h with { Plated = p } : h)
            .ToList();
        var slots = fileOwn.Slots
            .Select(s => s.Plated is null && platingByTool.TryGetValue(s.Tool, out bool? p) && p is not null
                ? s with { Plated = p } : s)
            .ToList();

        if (takenFrom.Count > 0)
            said.Add($"plating was read from the netlist: {string.Join(", ", takenFrom)}." +
                     (uncovered > 0 ? $" {uncovered} of this file's tool(s) are not covered by it and stay unstated." : ""));

        // The file-level answer, by exactly the rule ExcellonReader.ApplyToolListing uses: a drill
        // layer is one stackup entry, so it only has an answer when the tools that were actually used
        // agree. A mixed file keeps its unstated (i.e. plated) entry and says why — marking the whole
        // layer non-plated would delete every real via on it.
        bool? filePlated = fileOwn.Plated;
        if (filePlated is null)
        {
            var used = tools.Where(t => hits.Any(h => h.Tool == t.Number) || slots.Any(sl => sl.Tool == t.Number))
                            .ToList();
            if (used.Count == 0) used = tools;
            if (used.All(t => t.Plated == false)) filePlated = false;
            else if (used.All(t => t.Plated == true)) filePlated = true;
            else if (used.Any(t => t.Plated == false))
                said.Add("the netlist marks some of this file's tools plated and others not, so this drill " +
                         "layer has no single answer: its stackup entry stays PLATED and the non-plated " +
                         "holes are drawn exactly as they are. Split the non-plated tools into their own " +
                         "drill file if they need to be excluded from an EM run.");
        }

        return read with { Tools = tools, Hits = hits, Slots = slots, Plated = filePlated };
    }

    private static string Plating(bool plated) => plated ? "plated" : "non-plated";

    private static bool? PlatingFromNetlist(
        ExcellonReadResult read, int tool, NetlistHoleIndex holes, out int covered)
    {
        covered = 0;
        bool? answer = null;
        foreach (var hit in read.Hits)
        {
            if (hit.Tool != tool) continue;
            if (holes.At(hit.X, hit.Y) is not { Plated: { } plated }) continue;
            covered++;
            if (answer is null) answer = plated;
            else if (answer != plated) return null;         // one tool, two answers: no answer
        }
        return answer;
    }

    /// <summary>
    /// R-gi5-11's second half: how many holes the netlist names that no drill file in this set
    /// actually drilled.
    ///
    /// <para>Reported with a count and REPAIRED IN NO WAY. A hole in one file and not the other is
    /// information about the set — almost always that the two are from different revisions of the
    /// job — and it is invisible without a reader for the netlist, which is the point of counting it.
    /// Nothing is created for these: the drill file remains the sole source of every hole
    /// (R-gi5-1).</para>
    /// </summary>
    public int HolesNotDrilled(IEnumerable<ExcellonReadResult> drillReads)
    {
        if (!Any) return 0;
        long tolerance = Math.Max(1, ToleranceDbu);

        var drilled = new HashSet<(long, long)>();
        foreach (var read in drillReads)
            foreach (var hit in read.Hits)
                for (long gx = -1; gx <= 1; gx++)
                for (long gy = -1; gy <= 1; gy++)
                    drilled.Add(((long)Math.Floor((double)hit.X / tolerance) + gx,
                                 (long)Math.Floor((double)hit.Y / tolerance) + gy));

        int missing = 0;
        foreach (var netlist in Usable)
            foreach (var record in netlist.Holes)
                if (!drilled.Contains(((long)Math.Floor((double)record.X / tolerance),
                                       (long)Math.Floor((double)record.Y / tolerance))))
                    missing++;
        return missing;
    }

    // ── R-gi5-5: layer span ───────────────────────────────────────────────────

    /// <summary>
    /// The layer span the netlist states for one drill file's holes, or null when it cannot state
    /// one.
    ///
    /// <para><b>What this format actually carries is ACCESSIBILITY, not a span</b> — one access code
    /// per record, naming either "both outer surfaces" or the single layer a feature is reachable
    /// from. A span is therefore only recoverable where a hole's records name TWO different layers,
    /// which is what a writer emits for a blind or buried feature it can describe at all. A hole
    /// whose records all say "both surfaces" is a through hole and is reported as one; a hole whose
    /// records name exactly one inner layer is reported as what it is and used for nothing, because
    /// one layer is not a span and inventing the other end is inventing a stackup.</para>
    ///
    /// <para>R-gi5-5 also forbids synthesising a stackup entry per distinct span found. Nothing here
    /// creates one — the span is applied where <c>GerberImport</c> already mints a via entry, and the
    /// distinct spans found are reported.</para>
    /// </summary>
    public (DrillSpan? Span, IReadOnlyList<string> Spans, int Covered, int Uncovered) SpanFor(
        ExcellonReadResult read, int copperLayerCount)
    {
        var found = new List<(int From, int To)>();
        var partial = new List<int>();
        int covered = 0, uncovered = 0;

        if (!Any) return (null, [], 0, read.Hits.Count);

        var holes = Holes;
        foreach (var hit in read.Hits)
        {
            var fact = holes.At(hit.X, hit.Y);
            if (fact is null || fact.AccessCodes.Count == 0) { uncovered++; continue; }
            covered++;

            var layers = fact.AccessCodes.Where(a => a > 0).Distinct().OrderBy(a => a).ToList();
            if (fact.AccessCodes.Contains(0) || layers.Count == 0)
            {
                found.Add((1, Math.Max(1, copperLayerCount)));
                continue;
            }
            if (layers.Count == 1) { partial.Add(layers[0]); continue; }
            found.Add((layers[0], layers[^1]));
        }

        var distinct = found.Distinct().OrderBy(s => s.From).ThenBy(s => s.To).ToList();
        var names = distinct.Select(s => $"{s.From}-{s.To}").ToList();
        if (partial.Count > 0)
            names.Add($"{partial.Distinct().Count()} layer number(s) named without a second end " +
                      $"({string.Join(", ", partial.Distinct().OrderBy(p => p))})");

        if (distinct.Count != 1) return (null, names, covered, uncovered);

        var (from, to) = distinct[0];
        string kind =
            from == 1 && to == copperLayerCount ? "PTH"
            : from == 1 || to == copperLayerCount ? "Blind"
            : "Buried";
        return (new DrillSpan(from, to, kind, null), names, covered, uncovered);
    }

    // ── R-gi5-6/-8/-9: net names ──────────────────────────────────────────────

    /// <summary>
    /// Attaches net names to the shapes the readers already built — the one thing this phase gives
    /// back that is otherwise permanently lost.
    ///
    /// <para>A clear-polarity layer has had its per-object identities unioned away by compositing and
    /// no amount of re-reading the artwork recovers them. But a netlist coordinate still falls INSIDE
    /// the composited region, so the union that destroyed the pad's identity did not destroy its
    /// LOCATION, and the region that contains it can be named.</para>
    ///
    /// <para><b>R-gi5-9: one region holding pads of more than one net is a real state and is left
    /// null.</b> <see cref="LayoutShape.Net"/> is one nullable string; a pour genuinely containing
    /// two nets cannot honestly carry either of them, and writing whichever name arrived first would
    /// be a fabrication nothing downstream would question. Counted and said instead.</para>
    ///
    /// <para><b>Never geometry (R-gi5-1).</b> This walks a list it was handed and sets a string on
    /// shapes already in it. It adds nothing, removes nothing and moves nothing.</para>
    /// </summary>
    public NetlistAttachment AttachNets(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayerKey> copperTopToBottom)
    {
        var messages = new List<string>();
        long tolerance = Math.Max(1, ToleranceDbu);
        if (!Any || shapes.Count == 0)
            return new NetlistAttachment(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, tolerance, messages);

        // Flattening tolerance for the containment test below. A hundredth of the match tolerance,
        // which is two orders of magnitude finer than the netlist's own coordinate resolution and so
        // can never change a yes/no about a point — and NOT one DBU, which on a curved trace would
        // flatten a millimetre-scale arc into thousands of segments to answer a question that a
        // nanometre cannot decide.
        long flattenTolerance = Math.Max(1, tolerance / 100);

        var copper = new HashSet<LayerKey>(copperTopToBottom);
        var index = new LayoutSpatialIndex();
        var claims = new Dictionary<int, HashSet<string>>();
        var paths = new Dictionary<int, Paths64>();

        int records = 0, withNet = 0, containment = 0, near = 0, unmatched = 0, multi = 0;

        foreach (var netlist in Usable)
            foreach (var record in netlist.Records)
            {
                records++;
                if (record.Net is not { Length: > 0 } net) continue;
                withNet++;

                var allowed = LayersFor(record.Access, copperTopToBottom);
                var box = new Bbox(record.X - tolerance, record.Y - tolerance,
                                   record.X + tolerance, record.Y + tolerance);

                var inside = new List<int>();
                int nearest = -1;
                long nearestDistance = long.MaxValue;

                foreach (int i in index.QueryIntersecting(shapes, box))
                {
                    var shape = shapes[i];

                    // A VIA is copper on a layer that is not a copper layer: it sits on the DRILL
                    // layer and carries the copper layer it lands on separately. Excluding it here
                    // would report the pad it consumed as "matched nothing" — a coordinate whose pad
                    // became a via object is matched, not missing.
                    if (shape is ViaShape via)
                    {
                        if (allowed is not null && via.LandingLayer is { } landing && !allowed.Contains(landing))
                            continue;
                    }
                    else
                    {
                        if (!copper.Contains(shape.Layer)) continue;
                        if (allowed is not null && !allowed.Contains(shape.Layer)) continue;
                    }

                    if (Inside(shapes, i, paths, record.X, record.Y, flattenTolerance)) { inside.Add(i); continue; }

                    long distance = EdgeDistance(LayoutGeometry.BboxOf(shape), record.X, record.Y);
                    if (distance <= tolerance && distance < nearestDistance)
                    {
                        nearest = i;
                        nearestDistance = distance;
                    }
                }

                if (inside.Count > 0)
                {
                    containment++;
                    if (inside.Count > 1) multi++;
                    foreach (int i in inside) Claim(claims, i, net);
                }
                else if (nearest >= 0)
                {
                    near++;
                    Claim(claims, nearest, net);
                }
                else unmatched++;
            }

        int named = 0, ambiguous = 0, disagreed = 0, already = 0;
        foreach (var (i, nets) in claims.OrderBy(kv => kv.Key))
        {
            var shape = shapes[i];
            if (nets.Count > 1) { ambiguous++; continue; }
            string net = nets.First();

            if (shape.Net is { Length: > 0 } existing)
            {
                if (!string.Equals(existing, net, StringComparison.Ordinal)) disagreed++;
                else already++;
                continue;                              // the artwork declared it; a file speaks for itself
            }

            shape.Net = net;
            named++;
        }

        // R-gi5-8: a coordinate matching several shapes on one layer is the EXPECTED outcome on a
        // ground pour, not a fault, and the sentence has to read that way.
        if (multi > 0)
            messages.Add(
                $"{multi:N0} netlist coordinate(s) fall inside more than one copper shape. That is the " +
                "ordinary outcome on a pour — a whole net's worth of pads land in one composited " +
                "region — and every one of those shapes was offered the net name.");
        if (ambiguous > 0)
            messages.Add(
                $"{ambiguous:N0} composited region(s) contain pads of more than one net and were left " +
                "unnamed. A region is one shape and carries one net name, so naming it after whichever " +
                "net was read first would be a fabrication. Their copper and their holes are unaffected.");
        if (disagreed > 0)
            messages.Add(
                $"{disagreed:N0} shape(s) already carry a net name from the artwork's own attributes that " +
                "DISAGREES with the netlist's. The artwork's name was kept — a file is authoritative " +
                "about itself — and the disagreement is usually the two files coming from different " +
                "revisions of this job.");
        if (unmatched > 0)
            messages.Add(
                $"{unmatched:N0} netlist coordinate(s) matched no copper shape within {ToleranceText}. " +
                "Nothing was created for them (a netlist is evidence about the artwork, never " +
                "geometry) — they are usually pads on a layer that was not imported, or a netlist " +
                "written for a different revision of this board.");

        return new NetlistAttachment(
            records, withNet, containment, near, unmatched, multi, named, ambiguous, disagreed, already,
            tolerance, messages);
    }

    private static void Claim(Dictionary<int, HashSet<string>> claims, int shape, string net)
    {
        if (!claims.TryGetValue(shape, out var nets)) claims[shape] = nets = new HashSet<string>(StringComparer.Ordinal);
        nets.Add(net);
    }

    /// <summary>Which copper layers a record's access code lets it reach. Null means "any" — an
    /// absent code, or one naming a layer this import does not have, which must widen the search
    /// rather than exclude everything.</summary>
    private static HashSet<LayerKey>? LayersFor(int? access, IReadOnlyList<LayerKey> copperTopToBottom)
    {
        if (access is not { } code || code == 0 || copperTopToBottom.Count == 0) return null;
        if (code < 1 || code > copperTopToBottom.Count) return null;
        return [copperTopToBottom[code - 1]];
    }

    /// <summary>Whether a point is inside one shape's copper.
    ///
    /// <para>Analytic for the primitives a pad is actually written as, and Clipper's own
    /// even-odd test over the flattened rings for everything else — which is what makes a pour with
    /// holes in it answer correctly rather than claiming a coordinate that sits in one of its
    /// clearances. Flattened paths are cached per shape: a pour is flattened once per import, not
    /// once per netlist record.</para></summary>
    private static bool Inside(
        IReadOnlyList<LayoutShape> shapes, int i, Dictionary<int, Paths64> cache, long x, long y,
        long flattenTolerance)
    {
        var shape = shapes[i];
        switch (shape)
        {
            case CircleShape c:
            {
                long dx = c.Cx - x, dy = c.Cy - y;
                return (dx * dx) + (dy * dy) <= c.R * c.R;
            }
            case ViaShape v:
            {
                long half = v.PadSize / 2;
                return Math.Abs(v.X - x) <= half && Math.Abs(v.Y - y) <= half;
            }
            case RectShape r:
                return x >= r.X1 && x <= r.X2 && y >= r.Y1 && y <= r.Y2;
            case RoundedRectShape rr:
                // The bounding box, deliberately: the only error is a corner radius' worth at four
                // corners of a pad, and a netlist coordinate is the pad's CENTRE.
                return x >= rr.X1 && x <= rr.X2 && y >= rr.Y1 && y <= rr.Y2;
            case LabelShape or BitmapShape:
                return false;                                  // not copper, whatever layer it is on
            default:
            {
                if (!cache.TryGetValue(i, out var paths))
                    cache[i] = paths = LayoutClipper.ToClipperPaths(shape, flattenTolerance);

                bool inside = false;
                foreach (var path in paths)
                {
                    var where = Clipper.PointInPolygon(new Point64(x, y), path);
                    if (where == PointInPolygonResult.IsOn) return true;
                    if (where == PointInPolygonResult.IsInside) inside = !inside;
                }
                return inside;
            }
        }
    }

    /// <summary>Distance from a point to a box, zero inside it. The near rung's ruler (R-gi5-7): a
    /// coordinate that fell just outside a pad, not a search for the nearest pad on the board.</summary>
    private static long EdgeDistance(Bbox box, long x, long y)
    {
        if (box.IsEmpty) return long.MaxValue;
        long dx = Math.Max(Math.Max(box.MinX - x, x - box.MaxX), 0);
        long dy = Math.Max(Math.Max(box.MinY - y, y - box.MaxY), 0);
        return Math.Max(dx, dy);
    }
}
