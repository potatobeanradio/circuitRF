// The placement (pick-and-place) table a layout tool exports beside its artwork
// (docs/sonnet-briefs/brief-railrf-2-companion-readers.md §1, railrf.md §2.2).
//
// THE GOVERNING RULE, AND IT IS BoardNetlistFile's VERBATIM (R-rail2-1): THIS FILE IS EVIDENCE ABOUT
// THE ARTWORK. IT IS NEVER GEOMETRY. Nothing here creates a shape, moves a shape or deletes a shape.
// The Gerber, Excellon and board readers remain the sole source of every coordinate, every diameter
// and every outline; this attaches FACTS to objects those readers already built, and REPORTS
// anything it cannot attach. A placement row naming a refdes the artwork does not have is a message
// with a count in it, never a footprint.
//
// THE ONE TRAP, AND IT IS WORSE THAN THE EXCELLON ONE (R-rail2-2): THE COORDINATE ORIGIN IS A CHOICE
// MADE AT EXPORT — the footprint's symbol origin, the body centre, or pin 1 — and the exporting tool
// does not always record which. Three quarters of a millimetre on an 0402 is the difference between
// landing on the part's own pad and landing on its NEIGHBOUR's. This is the same shape as the
// Excellon coordinate format, which is already a refusal in `convert` because leading versus trailing
// suppression differ by four orders of magnitude on identical text — and here the error is SMALLER
// AND THEREFORE WORSE: a wrong Excellon read misses the board and is loud, a wrong placement origin
// lands on the neighbouring pad and is silent.
//
// SO AN UNSTATED ORIGIN IS A REFUSAL NAMING THE FLAG THAT ANSWERS IT, never a guess and never a
// house convention learned from one board. Q-14 closed it explicitly: railRF ASKS. In the window
// (brief 7) it is a three-way choice with NOTHING PRE-SELECTED, because a default here is the guess
// the refusal exists to prevent.
//
// AND A REFUSAL IS NOT THE ONLY FAILURE (R-rail2-3). Once an origin IS chosen, the rows are resolved
// against the artwork's own pads and THE COUNT THAT LANDED ON NO PAD AT ALL IS REPORTED — a chosen
// origin that puts 40 % of the parts on empty copper is almost certainly the wrong one of the three.
// It is a REPORTED NUMBER AND NEVER AN AUTOMATIC RE-CHOICE: a board where two of the three score
// similarly would then be chosen silently, which is the failure this whole file is built against.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>The three coordinate origins a placement export is written against. There is no fourth
/// and there is no default — see this file's header, and Q-14.</summary>
public enum PlacementOrigin
{
    /// <summary>The footprint's own origin as the library defines it.</summary>
    SymbolOrigin,

    /// <summary>The centre of the part body.</summary>
    BodyCentre,

    /// <summary>Pin 1.</summary>
    PinOne,
}

/// <summary>How the origin was settled. The same doctrine <see cref="BoardNetlistUnitsEvidence"/>
/// states: a declaration and a choice must never read the same, and neither may read like a
/// default — because there is no default (Q-14).</summary>
public enum PlacementOriginEvidence
{
    /// <summary>The file's own header record said so.</summary>
    Declared,

    /// <summary>The caller said so — the flag headless, the import dialog's three-way choice in the
    /// window. A statement about the RUN, exactly as <c>--drill-zeros</c> is.</summary>
    Chosen,

    /// <summary>Neither did, which is the refusal. <see cref="PlacementTable.Origin"/> is null.</summary>
    Unstated,
}

/// <summary>One placement row, in the ARTWORK's own DBU.</summary>
/// <param name="X">Where the chosen <see cref="PlacementOrigin"/> of this part sits. Not where its
/// body sits, and not where pin 1 sits, unless that is the origin the file was written against —
/// which is exactly why the origin is a refusal rather than a default.</param>
/// <param name="Mirror">Whether the row puts the part on the bottom side. <see cref="PlacementFile"/>
/// reports a mirrored row whose footprint has no bottom-side artwork; it never assumes one
/// (R-rail2-4).</param>
public sealed record PlacementRow(
    string Refdes,
    long X,
    long Y,
    double RotationDegrees,
    bool Mirror,
    string? Footprint,
    int Line);

/// <summary>Everything one placement file turned out to be. <see cref="Refusal"/> non-null means
/// NOTHING WAS READ AND NOTHING MAY BE USED — the contract <see cref="BoardNetlist"/> and
/// <see cref="ExcellonReadResult"/> both keep.</summary>
/// <param name="ParsedRowCount">How many rows the table held, filled even on a refusal. The one
/// number an import dialog needs before it can ask the origin question — "471 parts, which origin?"
/// — and the reason it is here rather than the rows themselves.</param>
public sealed record PlacementTable(
    string Path,
    string? Refusal,
    PlacementOrigin? Origin,
    PlacementOriginEvidence OriginEvidence,
    LayoutUnit Units,
    BoardNetlistUnitsEvidence UnitsEvidence,
    char Delimiter,
    IReadOnlyList<PlacementRow> Rows,
    int ParsedRowCount,
    int UnreadableRows,
    DrillExtents Extents,
    IReadOnlyList<string> Diagnostics)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The origin, and what settled it — the sentence a report prints.</summary>
    public string OriginSummary => OriginEvidence switch
    {
        PlacementOriginEvidence.Declared => $"{Describe(Origin!.Value)}, which the file declares",
        PlacementOriginEvidence.Chosen   => $"{Describe(Origin!.Value)}, as stated for this run",
        _ => "not stated, and not guessed",
    };

    /// <summary>The units, and what settled them. <see cref="BoardNetlist.UnitsSummary"/>'s shape.</summary>
    public string UnitsSummary => UnitsEvidence switch
    {
        BoardNetlistUnitsEvidence.Declared => $"{Units}, which the file declares",
        BoardNetlistUnitsEvidence.Artwork  => $"{Units}, settled against the artwork's own extent",
        _ => $"{Units} — this format's own usual unit, because the file states none",
    };

    public static string Describe(PlacementOrigin origin) => origin switch
    {
        PlacementOrigin.SymbolOrigin => "the footprint's symbol origin",
        PlacementOrigin.BodyCentre   => "the part body's centre",
        _ => "pin 1",
    };
}

/// <summary>
/// The little a landing check needs to know about one footprint, in the footprint's OWN unrotated
/// frame, as offsets from its symbol origin.
///
/// <para><b>Brief 3 builds these from the artwork; this file only consumes one.</b> It is the seam
/// rather than a footprint model: R-rail2-3's count cannot be computed without knowing where a
/// footprint's pads sit relative to each of the three origins, and inventing that here would be the
/// geometry R-rail2-1 forbids.</para>
/// </summary>
/// <param name="PadOffsets">Every pad centre, pin 1 FIRST.</param>
/// <param name="BodyCentreOffset">Where the body centre sits.</param>
/// <param name="HasBottomSideArtwork">Whether this footprint has artwork for the bottom side at all.
/// A mirrored row whose footprint has none is R-rail2-4's report.</param>
public sealed record FootprintGeometry(
    IReadOnlyList<(long X, long Y)> PadOffsets,
    (long X, long Y) BodyCentreOffset,
    bool HasBottomSideArtwork = true)
{
    /// <summary>Pin 1's offset — the first pad, because that is what "pin 1" means. (0, 0) for a
    /// footprint with no pads, which is a footprint nothing can land.</summary>
    public (long X, long Y) PinOneOffset => PadOffsets.Count > 0 ? PadOffsets[0] : (0, 0);

    internal (long X, long Y) OffsetOf(PlacementOrigin origin) => origin switch
    {
        PlacementOrigin.BodyCentre => BodyCentreOffset,
        PlacementOrigin.PinOne     => PinOneOffset,
        _ => (0, 0),
    };
}

/// <summary>
/// R-rail2-3's answer, as counts. <see cref="Landed"/> plus <see cref="Unlanded"/> plus
/// <see cref="Unknown"/> is every row.
///
/// <para><b>There is no verdict here and no re-choice.</b> The caller asks the question once per
/// origin and reads three numbers; railRF does not try the other two and pick the best, because a
/// board where two of the three score similarly would then be chosen silently.</para>
/// </summary>
/// <param name="Unlanded">Rows whose placed pads landed on NO artwork pad at all — the number that
/// says an origin is the wrong one of the three.</param>
/// <param name="Unknown">Rows whose footprint was not in the supplied geometry, so nothing could be
/// placed. Counted separately and never counted as landed: a missing footprint scoring as a success
/// is how a wrong origin wins.</param>
/// <param name="MirrorMismatches">R-rail2-4: refdes that set <c>mirror</c> and whose footprint has no
/// bottom-side artwork. Named, counted, and carried on from.</param>
public sealed record PlacementLanding(
    PlacementOrigin Origin,
    int Landed,
    int Unlanded,
    int Unknown,
    IReadOnlyList<string> UnlandedRefdes,
    IReadOnlyList<string> MirrorMismatches)
{
    public int Total => Landed + Unlanded + Unknown;

    public string Report =>
        $"Read at {PlacementTable.Describe(Origin)}: {Landed} of {Total} row(s) place a pad on the " +
        $"artwork, {Unlanded} land on no pad at all" +
        (Unknown > 0 ? $", and {Unknown} name a footprint whose geometry was not supplied" : "") + ".";
}

public static class PlacementFile
{
    /// <summary>How many data rows are needed before a header that matches is believed. Two, for
    /// <see cref="BoardNetlistFile.MinimumRecords"/>'s reason: one row under a plausible header is a
    /// coincidence a report can produce.</summary>
    public const int MinimumRows = 2;

    /// <summary>The flag that answers the origin question headless. Named in the refusal text, which
    /// is the whole point of the refusal.</summary>
    public const string OriginFlag = "--placement-origin";

    /// <summary>The two flags that separate a bill of materials from a placement table when a header
    /// matches both (R-rail2-12). One more refusal is cheaper than a board whose parts are all at
    /// (0, 0).</summary>
    public const string KindFlags = "--placement / --bom";

    /// <summary>
    /// The refusal for a file with no header row, <b>named so a caller can recognise it</b>.
    /// </summary>
    /// <remarks>
    /// <b>This is the one refusal in this reader that a CONTROL can answer</b> (field report,
    /// 2026-09-22), and that is why it is a constant rather than an interpolated sentence. Every
    /// other refusal here is about the file — an ambiguous header, a missing X column, something
    /// that is not a table at all — and the answer to those is a different file. This one is
    /// answerable in place, by somebody saying what the columns are; the GUI now asks
    /// (<c>RailPlacementColumnsDialog</c>) and <see cref="HasNoHeader"/> is how it tells this case
    /// apart without matching on prose.
    ///
    /// <para><b>AND IT NO LONGER NAMES A FLAG THAT DOES NOT EXIST.</b> The sentence used to read
    /// "Name them with --columns, or state --from to read it as something else". A designer met it
    /// in a WINDOW, where there is no command line at all — and the flag is not on one either:
    /// nothing in <c>src/Cli</c> parses <c>--columns</c>, and <c>netlist --placement</c> WRITES a
    /// placement table out of a layout rather than reading one in. There is exactly one headless
    /// reader of a placement file, <c>RailArtwork.ResolvePlacement</c>, and what it reads the
    /// mapping from is the <c>.crail</c>'s own <c>RailPlacementReading</c>. So that is what the
    /// sentence names. A refusal pointing at an inert knob is worse than one pointing at
    /// nothing: it costs the reader the time to go and look.</para>
    /// </remarks>
    public const string NoHeaderRefusal =
        "states no header row naming its columns, and column order is not a standard — a positional "
      + "reading would put the rotation in the Y column silently. Say what the columns are: railRF "
      + "asks when the file is imported, and a .crail records the answer under \"placement\" so "
      + "the same file reads the same way headlessly. Adding a header row to the file answers it "
      + "too.";

    /// <summary>Whether <paramref name="table"/> was refused for having no header row — the one
    /// refusal a caller can answer in place. See <see cref="NoHeaderRefusal"/>.</summary>
    public static bool HasNoHeader(PlacementTable? table) =>
        table?.Refusal is { Length: > 0 } r && r.Contains(NoHeaderRefusal, StringComparison.Ordinal);

    // The column names, in the normalized form DelimitedTables.NormalizeHeader produces. EXACT
    // equality, never a substring test: a `contains "x"` rule matches `footprint`, and the column it
    // then puts the coordinate in is the one nothing downstream questions.
    private static readonly string[] RefdesNames =
        ["refdes", "ref", "reference", "references", "designator", "designators", "part reference",
         "component", "comp", "ref des", "refdes id", "part"];

    private static readonly string[] XNames =
        ["x", "posx", "pos x", "x pos", "xpos", "centerx", "center x", "centrex", "centre x",
         "midx", "mid x", "x loc", "location x", "x coord", "x coordinate", "ref x", "center x mm"];

    private static readonly string[] YNames =
        ["y", "posy", "pos y", "y pos", "ypos", "centery", "center y", "centrey", "centre y",
         "midy", "mid y", "y loc", "location y", "y coord", "y coordinate", "ref y", "center y mm"];

    private static readonly string[] RotationNames =
        ["rot", "rotation", "angle", "theta", "rot deg", "rotate", "orientation"];

    private static readonly string[] SideNames =
        ["side", "layer", "tb", "top bottom", "board side", "placement side"];

    private static readonly string[] MirrorNames = ["mirror", "mirrored", "flip", "flipped"];

    private static readonly string[] FootprintNames =
        ["footprint", "package", "pattern", "land pattern", "fp", "decal", "padstack", "pkg",
         "footprint name", "geometry"];

    // ── Recognition ───────────────────────────────────────────────────────────
    //
    // Runs AFTER the artwork, drill and board-netlist tests in GerberFileClassifier, which is
    // GI4's own ordering doctrine and R-rail2-12 restates it: nothing here may take a file one of
    // those already claimed, and this signature — a header row naming columns — is much weaker than
    // a netlist's three-digit operation codes over letter-tagged coordinates.

    /// <summary>Whether <paramref name="head"/> is a placement table, over text already in hand — the
    /// form <see cref="GerberFileClassifier.ClassifyContent"/> drives, so no fixture needs a
    /// temporary directory to assert what a byte stream is.</summary>
    public static bool Recognize(string head, out string why)
    {
        why = "";
        var table = DelimitedTables.Parse(head);
        if (FindHeader(table) is not { } found) return false;
        if (table.Rows.Count - found.Index - 1 < MinimumRows) return false;
        if (BomFile.HeaderMatches(table.Rows[found.Index].Fields)) return false;   // ambiguous: not claimed

        why = $"a placement table ({table.Rows.Count - found.Index - 1} row(s), " +
              $"'{table.Delimiter}' separated)";
        return true;
    }

    /// <summary>Whether a header row names the columns this reader needs: a reference, an X and a Y.
    /// The "stated minimum" R-rail2-12 requires.</summary>
    public static bool HeaderMatches(IReadOnlyList<string> header) =>
        DelimitedTables.IndexOf(header, RefdesNames) >= 0 &&
        DelimitedTables.IndexOf(header, XNames) >= 0 &&
        DelimitedTables.IndexOf(header, YNames) >= 0;

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a placement table from text already in hand.
    /// </summary>
    /// <param name="origin">The origin, where the caller knows it — the flag, or the dialog's
    /// three-way choice. Null leaves it to the file, and a file that does not state one is a
    /// REFUSAL.</param>
    /// <param name="units">The units, where the caller states them. Null reads the file's own and
    /// falls back to this format's usual millimetre, stated as <c>Defaulted</c>.</param>
    /// <param name="columns">R-rail2-14 item 3: the column names in order, for a file with NO HEADER
    /// ROW AT ALL. Null and a missing header is a refusal naming the flag — never a positional
    /// guess, because column order is not a standard and a positional fallback puts the value column
    /// in the footprint column, silently.</param>
    public static PlacementTable Read(
        string path, string text, int dbuPerMicron,
        PlacementOrigin? origin = null,
        LayoutUnit? units = null,
        BoardNetlistUnitsEvidence? unitsEvidence = null,
        char? delimiter = null,
        IReadOnlyList<string>? columns = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        var diagnostics = new List<string>();
        var table = DelimitedTables.Parse(text, delimiter);

        PlacementTable Refuse(string sentence) => new(
            full, sentence, origin, origin is null ? PlacementOriginEvidence.Unstated
                                                   : PlacementOriginEvidence.Chosen,
            units ?? LayoutUnit.Mm, BoardNetlistUnitsEvidence.Defaulted, table.Delimiter,
            [], 0, 0, DrillExtents.Empty, diagnostics);

        if (table.Rows.Count == 0)
            return Refuse($"{System.IO.Path.GetFileName(full)} holds no rows that could be read, so " +
                          "nothing was taken from it.");

        // The header, or the caller's own column list. A file with neither is a refusal NAMING THE
        // FLAG — see the parameter's own note.
        IReadOnlyList<string> header;
        int firstDataRow;

        if (FindHeader(table) is { } found)
        {
            header = table.Rows[found.Index].Fields;
            firstDataRow = found.Index + 1;

            // R-rail2-12's genuinely ambiguous case. A comma-separated file that is a bill of
            // materials and one that is a placement table are not distinguishable on column COUNT,
            // so they are distinguished on column NAMES — and where both signatures match, this
            // refuses and names the two flags rather than choosing.
            if (BomFile.HeaderMatches(header))
                return Refuse(
                    $"{System.IO.Path.GetFileName(full)} has a header row that reads both as a " +
                    $"placement table and as a bill of materials, and the two are not distinguishable " +
                    $"from it. State which it is with {KindFlags}.");
        }
        else if (columns is { Count: > 0 })
        {
            header = columns;
            firstDataRow = 0;
            diagnostics.Add(
                $"{System.IO.Path.GetFileName(full)} states no header row; the {columns.Count} column " +
                "name(s) given for this run were used.");
        }
        else
        {
            return Refuse(
                $"{System.IO.Path.GetFileName(full)} {NoHeaderRefusal} ({Classify(text)})");
        }

        int cRefdes = DelimitedTables.IndexOf(header, RefdesNames);
        int cX = DelimitedTables.IndexOf(header, XNames);
        int cY = DelimitedTables.IndexOf(header, YNames);
        int cRot = DelimitedTables.IndexOf(header, RotationNames);
        int cSide = DelimitedTables.IndexOf(header, SideNames);
        int cMirror = DelimitedTables.IndexOf(header, MirrorNames);
        int cFootprint = DelimitedTables.IndexOf(header, FootprintNames);

        if (cRefdes < 0 || cX < 0 || cY < 0)
        {
            string missing = cRefdes < 0 ? "reference" : cX < 0 ? "X" : "Y";
            return Refuse(
                $"{System.IO.Path.GetFileName(full)} names no {missing} column, so it cannot be read " +
                $"as a placement table. {Classify(text)}");
        }

        // ── the origin, which is the whole point of this reader ──────────────
        var declaredOrigin = DeclaredOrigin(table.Preamble);
        var chosenOrigin = origin ?? declaredOrigin;
        var originEvidence = origin is not null ? PlacementOriginEvidence.Chosen
                           : declaredOrigin is not null ? PlacementOriginEvidence.Declared
                           : PlacementOriginEvidence.Unstated;

        // ── the units, cross-checkable and therefore defaultable; the origin is not ──
        var declaredUnits = DeclaredUnits(table.Preamble)
                            ?? DelimitedTables.UnitInHeader(header[cX])
                            ?? DelimitedTables.UnitInHeader(header[cY]);
        var readUnits = units ?? declaredUnits ?? LayoutUnit.Mm;
        var readUnitsEvidence = unitsEvidence
                                ?? (units is not null ? BoardNetlistUnitsEvidence.Artwork
                                    : declaredUnits is not null ? BoardNetlistUnitsEvidence.Declared
                                    : BoardNetlistUnitsEvidence.Defaulted);

        var rows = new List<PlacementRow>();
        var extents = DrillExtents.Empty;
        int unreadable = 0, grouped = 0;

        for (int i = firstDataRow; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            string? refdesCell = row.Field(cRefdes);
            if (refdesCell is null) continue;      // a blank or decorative row, not a part

            if (!TryCoordinate(row.Field(cX), readUnits, dbuPerMicron, out long x) ||
                !TryCoordinate(row.Field(cY), readUnits, dbuPerMicron, out long y))
            {
                // A row whose reference was read and whose coordinates were not is a row this reader
                // does not understand. Counted, never skipped in silence.
                unreadable++;
                continue;
            }

            // A placement row carries ONE reference by construction — it also carries one coordinate,
            // and a grouped cell there would mean several parts in one place, which is not a thing a
            // board does. So the cell is taken verbatim and a cell that looks grouped is REPORTED.
            if (RefdesCell.Parse(refdesCell).Expanded) grouped++;

            double rotation = 0;
            if (cRot >= 0 && row.Field(cRot) is { } rot &&
                double.TryParse(rot.TrimEnd('°'), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double parsedRot))
                rotation = parsedRot;

            bool mirror = ReadMirror(row, cSide, cMirror);

            rows.Add(new PlacementRow(
                refdesCell, x, y, rotation, mirror, cFootprint >= 0 ? row.Field(cFootprint) : null,
                row.Line));
            extents = extents.Include(x, y);
        }

        if (rows.Count == 0)
            return Refuse($"{System.IO.Path.GetFileName(full)} has a placement header and no row " +
                          "under it that could be read.");

        if (table.HadByteOrderMark)
            diagnostics.Add("The file begins with a UNICODE BYTE ORDER MARK (U+FEFF), which was " +
                            "removed before the header row was read.");
        if (unreadable > 0)
            diagnostics.Add($"{unreadable:N0} row(s) named a reference and no coordinate this reader " +
                            "could read, and were skipped.");
        if (grouped > 0)
            diagnostics.Add($"{grouped:N0} row(s) name more than one reference in one cell. A " +
                            "placement row carries one coordinate, so the cell was taken as written " +
                            "rather than expanded — check them.");

        // THE REFUSAL, and it comes LAST so everything above it is reported first: an import dialog
        // asking the origin question wants to say how many parts it is asking about.
        if (chosenOrigin is null)
            return new PlacementTable(
                full,
                $"{System.IO.Path.GetFileName(full)} does not state which coordinate origin it was " +
                $"exported against — the footprint's symbol origin, the part body's centre, or pin 1. " +
                $"Three quarters of a millimetre on an 0402 is the difference between landing on the " +
                $"part's own pad and landing on its neighbour's, so this is not guessed. State it " +
                $"with {OriginFlag} symbol|body|pin1.",
                null, PlacementOriginEvidence.Unstated, readUnits, readUnitsEvidence, table.Delimiter,
                [], rows.Count, unreadable, DrillExtents.Empty, diagnostics);

        return new PlacementTable(
            full, null, chosenOrigin, originEvidence, readUnits, readUnitsEvidence, table.Delimiter,
            rows, rows.Count, unreadable, extents, diagnostics);
    }

    /// <summary>Reads a placement table from disk. A companion file that cannot be read is not a
    /// reason to fail an import of the artwork beside it — <see cref="BoardNetlistFile.ReadFile"/>'s
    /// contract.</summary>
    public static PlacementTable? ReadFile(
        string path, int dbuPerMicron,
        PlacementOrigin? origin = null,
        LayoutUnit? units = null,
        char? delimiter = null,
        IReadOnlyList<string>? columns = null)
    {
        try
        {
            return Read(path, File.ReadAllText(path), dbuPerMicron, origin, units, null, delimiter, columns);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ── the cross-check ───────────────────────────────────────────────────────

    /// <summary>
    /// R-rail2-3's units half, and it is the same instrument
    /// <see cref="BoardNetlistFile.CrossCheckExtents"/> and <see cref="ExcellonReader.CrossCheckExtents"/>
    /// are, for the same reason: a wrong unit is a wrong SCALE, and a wrong scale is visible against
    /// the artwork's bounding box even when the file itself was silent. <b>A placement table read at
    /// the wrong scale that matches nothing is at least loud; one read at a wrong scale that still
    /// lands inside the board mislabels every part on it, silently.</b>
    /// </summary>
    public static BoardNetlistExtentsCheck CrossCheckExtents(PlacementTable table, DrillExtents artwork)
    {
        if (!table.Extents.HasAny || !artwork.HasAny)
            return new BoardNetlistExtentsCheck(true, 0, 0, 1, 1,
                "No cross-check was possible: one of the two sets is empty.");

        long margin = Math.Max(artwork.Width, artwork.Height) / 100;
        int outside = table.Rows.Count(r => !artwork.Contains(r.X, r.Y, margin));
        int total = table.Rows.Count;

        double wr = artwork.Width == 0 ? 1 : (double)table.Extents.Width / artwork.Width;
        double hr = artwork.Height == 0 ? 1 : (double)table.Extents.Height / artwork.Height;

        // A proportion, not a zero: a part outside the copper extent is ordinary on a board with a
        // connector on the edge. A wrong scale is not a handful; it is most of them.
        bool agrees = outside * 10 <= total;

        bool measurable = table.Extents.Width > 0 && table.Extents.Height > 0;
        string spans = measurable
            ? $"the two spans differ by a factor of {wr:0.###} in X and {hr:0.###} in Y"
            : "the placement rows span no area, so there is nothing to compare against";

        string report = agrees
            ? $"The placement table agrees with the artwork: {total - outside} of {total} row(s) fall " +
              $"inside the artwork extent, and {spans}."
            : $"The placement table DISAGREES with the artwork read in {table.Units}: {outside} of " +
              $"{total} row(s) fall outside the artwork extent, and {spans}.";

        return new BoardNetlistExtentsCheck(agrees, outside, total, wr, hr, report);
    }

    /// <summary>
    /// R-rail2-3's origin half, and R-rail2-4 with it. Places each row's footprint at the chosen
    /// origin and counts the rows whose pads landed on NO artwork pad.
    ///
    /// <para><b>It returns numbers and no verdict.</b> Ask it three times and read three counts; it
    /// does not try the other two and pick the best, because a board where two of the three score
    /// similarly would then be chosen silently.</para>
    /// </summary>
    /// <param name="pads">Artwork pad centres, in DBU — from the readers that built them, never from
    /// this file (R-rail2-1).</param>
    /// <param name="tolerance">How close a placed pad has to be to an artwork pad to have landed on
    /// it. A pad's own half-width is the number the caller has and this file does not.</param>
    /// <param name="footprints">What each footprint looks like, by name. A row whose footprint is not
    /// here is <see cref="PlacementLanding.Unknown"/> — never landed, because a missing footprint
    /// scoring as a success is how a wrong origin wins.</param>
    public static PlacementLanding CheckLanding(
        PlacementTable table,
        IReadOnlyList<(long X, long Y)> pads,
        long tolerance,
        IReadOnlyDictionary<string, FootprintGeometry> footprints)
    {
        var origin = table.Origin
            ?? throw new InvalidOperationException(
                "A landing check needs an origin, and this table has none — which is the refusal " +
                "itself. Read it again with one stated.");

        int landed = 0, unlanded = 0, unknown = 0;
        var unlandedRefdes = new List<string>();
        var mirrorMismatches = new List<string>();

        foreach (var row in table.Rows)
        {
            if (row.Footprint is null || !footprints.TryGetValue(row.Footprint, out var fp))
            {
                unknown++;
                continue;
            }

            if (row.Mirror && !fp.HasBottomSideArtwork) mirrorMismatches.Add(row.Refdes);

            // The stated point is where the CHOSEN ORIGIN sits, so the footprint's symbol origin is
            // that point less the rotated offset to it — and the pads follow from there.
            var (ox, oy) = Rotate(fp.OffsetOf(origin), row.RotationDegrees, row.Mirror);
            long sx = row.X - ox, sy = row.Y - oy;

            bool any = false;
            foreach (var pad in fp.PadOffsets)
            {
                var (px, py) = Rotate(pad, row.RotationDegrees, row.Mirror);
                if (Near(sx + px, sy + py, pads, tolerance)) { any = true; break; }
            }

            if (any) landed++;
            else { unlanded++; unlandedRefdes.Add(row.Refdes); }
        }

        return new PlacementLanding(origin, landed, unlanded, unknown, unlandedRefdes, mirrorMismatches);
    }

    private static bool Near(long x, long y, IReadOnlyList<(long X, long Y)> pads, long tolerance)
    {
        foreach (var pad in pads)
            if (Math.Abs(pad.X - x) <= tolerance && Math.Abs(pad.Y - y) <= tolerance) return true;
        return false;
    }

    /// <summary>A footprint-frame offset placed on the board. Mirroring is about the Y axis — the
    /// convention every placement export in circulation writes — and it is applied BEFORE the
    /// rotation, which is the order that makes a mirrored part's rotation read the same way round as
    /// the tool that wrote it.</summary>
    private static (long X, long Y) Rotate((long X, long Y) p, double degrees, bool mirror)
    {
        double x = mirror ? -p.X : p.X;
        double y = p.Y;
        double r = degrees * Math.PI / 180.0;
        double c = Math.Cos(r), s = Math.Sin(r);
        return ((long)Math.Round(x * c - y * s), (long)Math.Round(x * s + y * c));
    }

    // ── the header block ──────────────────────────────────────────────────────

    /// <summary>The header record's origin, where the file states one. Null where nothing did —
    /// which is the refusal, and the reason nothing here falls back to a value.</summary>
    private static PlacementOrigin? DeclaredOrigin(IReadOnlyList<string> preamble)
    {
        foreach (string line in preamble)
        {
            string? stated = Value(line, "origin")
                             ?? Value(line, "coordinate origin")
                             ?? Value(line, "placement origin");
            if (stated is null || stated.Length == 0) continue;

            string text = stated.ToLowerInvariant();

            if (text.Contains("pin", StringComparison.Ordinal)) return PlacementOrigin.PinOne;
            if (text.Contains("body", StringComparison.Ordinal) ||
                text.Contains("centroid", StringComparison.Ordinal) ||
                text.Contains("centre", StringComparison.Ordinal) ||
                text.Contains("center", StringComparison.Ordinal)) return PlacementOrigin.BodyCentre;
            if (text.Contains("symbol", StringComparison.Ordinal) ||
                text.Contains("library", StringComparison.Ordinal) ||
                text.Contains("insertion", StringComparison.Ordinal)) return PlacementOrigin.SymbolOrigin;
        }
        return null;
    }

    /// <summary>The header record's units, where the file states them.</summary>
    private static LayoutUnit? DeclaredUnits(IReadOnlyList<string> preamble)
    {
        foreach (string line in preamble)
        {
            string? stated = Value(line, "units") ?? Value(line, "unit");
            if (stated is null) continue;
            if (DelimitedTables.ParseUnit(stated) is { } unit) return unit;
        }
        return null;
    }

    /// <summary>A <c>KEY: value</c> or <c>KEY = value</c> header line's value, or null where the line
    /// is about something else.</summary>
    private static string? Value(string line, string key)
    {
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return null;

        string rest = trimmed[key.Length..].TrimStart();
        if (rest.Length == 0 || (rest[0] != ':' && rest[0] != '=')) return null;
        return rest[1..].Trim();
    }

    private static (int Index, IReadOnlyList<string> Fields)? FindHeader(DelimitedTable table)
    {
        // Only the first few rows: a header row is at the top of a table or it is not a header. A
        // scan of the whole file would find the word "reference" in somebody's description column.
        int limit = Math.Min(table.Rows.Count, 5);
        for (int i = 0; i < limit; i++)
            if (HeaderMatches(table.Rows[i].Fields)) return (i, table.Rows[i].Fields);
        return null;
    }

    private static bool TryCoordinate(string? cell, LayoutUnit unit, int dbuPerMicron, out long dbu)
    {
        dbu = 0;
        if (cell is null) return false;

        // A cell may carry its own unit — "12.7mm", "0.5in". LayoutUnits.TryParse reads that and
        // falls back to the table's unit where it does not, which is exactly the rule wanted here.
        return LayoutUnits.TryParse(cell, unit, dbuPerMicron, out dbu);
    }

    private static bool ReadMirror(DelimitedRow row, int cSide, int cMirror)
    {
        if (cMirror >= 0 && row.Field(cMirror) is { } m)
        {
            string v = m.ToLowerInvariant();
            if (v is "1" or "y" or "yes" or "true" or "t") return true;
            if (v is "0" or "n" or "no" or "false" or "f") return false;
        }

        if (cSide >= 0 && row.Field(cSide) is { } s)
        {
            string v = s.ToLowerInvariant();
            if (v.StartsWith("bot", StringComparison.Ordinal) || v is "b" or "back" or "2") return true;
        }

        return false;
    }

    /// <summary>
    /// R-rail2-12: what this file actually is, through the IMPORT'S OWN CLASSIFIER, so a Gerber or a
    /// drill file handed to this reader is NAMED rather than called unreadable.
    ///
    /// <para>The same doctrine <c>convert</c>, <c>check</c> and <c>explain</c> all follow — a second
    /// rule would be a second answer, and the one a user sees would depend on which door they came
    /// in by.</para>
    /// </summary>
    private static string Classify(string text)
    {
        var kind = GerberFileClassifier.ClassifyContent("", text);
        return kind.Kind == GerberFileKind.Other
            ? "It is not any file kind circuitRF recognises."
            : $"It reads as {kind.Why}.";
    }
}
