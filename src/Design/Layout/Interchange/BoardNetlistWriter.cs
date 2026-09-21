// The other half of BoardNetlistFile — brief-authored-board-3-companion-writers.md R-ab3-1.
//
// WRITTEN FROM PUBLIC DOCUMENTATION ONLY, and this file is the half of that statement with teeth
// (R-ab3-1b). BoardNetlistFile's own header says it of the reader; a writer is where the claim can
// actually be checked, because a writer is what would have to have been copied from somebody's
// output. Every column position, every record code and every header keyword below is the one the
// READER beside this file already documents, and the round trip is the gate.
//
// ── IT RE-DERIVES NOTHING (R-ab3-1a) ───────────────────────────────────────────────────────────
//
// It takes briefs 1 and 2's projection — PlacedPins' pads, already carrying their refdes, their
// pin and their net — plus the root's own vias, and serialises them. A second projection is the
// drift this whole series exists to prevent: the netlist a user exports and the pads railRF analyses
// would then be two readings of one board that nothing compares.
//
// ── AN ABSENT FIELD IS WRITTEN AS ABSENT (R-ab3-1d) ────────────────────────────────────────────
//
// BoardNetlistRecord models Access, Plated and DrillDbu as NULLABLE precisely so "the file did not
// say" and "the file said" are different readings. A writer that fills in a plausible `A01` makes a
// through feature look like a surface one, and nothing downstream would question it — so a pad,
// which states no span, gets no access code at all, and a via whose span the stackup cannot resolve
// gets none either.

using System.Globalization;
using System.Text;

using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What one write states about itself. Every field here lands in the file's own header, so
/// the reader comes back <see cref="BoardNetlistUnitsEvidence.Declared"/> (R-ab3-1c).</summary>
/// <param name="Job">The job name — the board's own cell name, ordinarily.</param>
/// <param name="Units">The unit AND resolution every coordinate is counted in. Metric thousandths
/// by default: it is the one of the three that cannot be confused with the other, and the two inch
/// resolutions in circulation differ by a factor of ten.</param>
/// <param name="Provenance">The comment line at the top. Null writes the default sentence.</param>
public sealed record BoardNetlistWriteOptions(
    string? Job = null,
    BoardNetlistUnits Units = BoardNetlistUnits.MillimetreThousandth,
    string? Provenance = null);

public static class BoardNetlistWriter
{
    /// <summary>The record code every feature this writer emits carries — a plated/unplated feature
    /// with a net on it. One code, because this writer branches on WHICH FIELDS IT HAS rather than
    /// on a code table, exactly as the reader does.</summary>
    private const int FeatureCode = 317;

    /// <summary>The net-name field's width, from the reader's own column table
    /// (<c>Slice(line, 3, 14)</c>). A longer name goes into the header as an alias.</summary>
    private const int NetFieldWidth = 14;

    /// <summary>The reference-and-pin span's width — the reader takes columns 19 to 30 as one span
    /// and splits it on the hyphen.</summary>
    private const int RefFieldWidth = 11;

    /// <summary>
    /// The netlist for one board: one record per pad, one per via that carries a net.
    /// </summary>
    /// <param name="pads">Briefs 1 and 2's projection, verbatim. A pad with no refdes or no pin
    /// names no feature this format can express and is skipped.</param>
    /// <param name="vias">The ROOT's own vias — <c>LayoutView.Shapes</c>, not the flatten's. A via
    /// inside a land pattern is that pattern's internal business, which is
    /// <c>PlacedPins.NetPointsOf</c>' rule and is right here for its reason.</param>
    /// <param name="netOfVia">The net at a via, where the pad projection's own partition knows one —
    /// <c>CopperPieces.NameAt</c>, passed in rather than rebuilt (R-ab3-1a). Null asks nothing
    /// and takes the via's own stamp alone.</param>
    /// <param name="tech">The stackup, which is the only thing that can say what a via SPANS.</param>
    public static string Write(
        IReadOnlyList<PlacedPin> pads,
        IReadOnlyList<ViaShape> vias,
        int dbuPerMicron,
        Technology? tech = null,
        Func<ViaShape, string?>? netOfVia = null,
        BoardNetlistWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pads);
        ArgumentNullException.ThrowIfNull(vias);

        var o = options ?? new BoardNetlistWriteOptions();

        // The alias table, built FIRST: a record carries the alias, so the header has to know every
        // long name before a single record is written. Ordinal and insertion-ordered, so two runs
        // over one board produce the same file.
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pad in pads) Alias(aliases, pad.Net);
        foreach (var via in vias) Alias(aliases, NetOf(via, netOfVia));

        var sb = new StringBuilder();
        sb.Append("C  ").Append(o.Provenance ?? DefaultProvenance).Append('\n');
        if (o.Job is { Length: > 0 } job) sb.Append("P  JOB ").Append(Clean(job)).Append('\n');
        sb.Append("P  UNITS CUST ").Append(UnitsCode(o.Units)).Append('\n');
        sb.Append("P  DIM ").Append(DimWord(o.Units)).Append('\n');

        foreach (var (alias, full) in aliases)
            sb.Append("P  ").Append(alias).Append(' ').Append(full).Append('\n');

        foreach (var pad in pads)
        {
            if (pad.Refdes is not { Length: > 0 } refdes || pad.Pin is not { Length: > 0 } pin)
                continue;

            // A PAD states no span. R-ab3-1d: the field is omitted rather than defaulted, because a
            // plausible `1` is exactly what makes a through feature read as a surface one.
            sb.Append(Record(
                NameIn(aliases, pad.Net), $"{refdes}-{pin}",
                pad.X, pad.Y, o.Units, dbuPerMicron, drill: null, plated: null, access: null));
            sb.Append('\n');
        }

        // One HOLE is one feature, and a barrel drawn as two shapes — a pad on the top surface and
        // one on the bottom, at the same coordinate on the same drill layer — is one hole said
        // twice. The reader merges identical records anyway (NetlistHoleIndex unions the facts at a
        // coordinate), so the duplicate is noise rather than information; what it is NOT is a
        // second barrel, and a count of vias taken off this file must not read it as one.
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (var via in vias)
        {
            // R-gi5-3, from the writing side: a hole carrying a net and NO component reference IS
            // the via. So a via record's reference span is BLANK, and that absence is the fact the
            // reader turns back into `IsVia`.
            string? net = NetOf(via, netOfVia);
            if (net is not { Length: > 0 }) continue;

            string record = Record(
                NameIn(aliases, net), "",
                via.X, via.Y, o.Units, dbuPerMicron,
                drill: via.DrillSize > 0 ? via.DrillSize : null,
                plated: null,                                  // a ViaShape states none — R-ab3-1d
                access: AccessOf(via, tech));

            if (!written.Add(record)) continue;
            sb.Append(record).Append('\n');
        }

        sb.Append("999\n");
        return sb.ToString();
    }

    /// <summary>The sentence a file circuitRF wrote carries at the top of it. A constant, so two
    /// writes of one board are the same bytes — <c>netlist</c>'s own provenance rule.</summary>
    public const string DefaultProvenance =
        "Board netlist projected by circuitRF from the artwork's own lands and vias.";

    // ── one record ────────────────────────────────────────────────────────────

    /// <summary>
    /// One feature record, laid out by COLUMN down to the tail and by ordered tag after it — which
    /// is the reader's own division and is not a stylistic choice: the head's fields are fixed-width
    /// and the tail's are self-identifying.
    /// </summary>
    private static string Record(
        string? net, string reference, long x, long y,
        BoardNetlistUnits units, int dbuPerMicron,
        long? drill, bool? plated, int? access)
    {
        var sb = new StringBuilder();
        sb.Append(FeatureCode.ToString("000", CultureInfo.InvariantCulture));
        sb.Append((net ?? "").PadRight(NetFieldWidth)[..NetFieldWidth]);
        sb.Append("  ");                                       // columns 17-18, unused by the reader
        sb.Append(reference.PadRight(RefFieldWidth)[..RefFieldWidth]);

        // The tail, in the format's own field ORDER: the drill and its plating letter, the access
        // code, then the location. The second X/Y pair is the feature's own size, which this writer
        // does not emit — the artwork is the sole source of every dimension (R-gi5-1), and a size
        // written here would be a second statement of it that nothing compares.
        if (drill is { } d)
        {
            sb.Append('D').Append(Counts(d, units, dbuPerMicron).ToString("000000", CultureInfo.InvariantCulture));
            if (plated is { } p) sb.Append(p ? 'P' : 'U');
        }
        if (access is { } a) sb.Append('A').Append(a.ToString("00", CultureInfo.InvariantCulture));

        sb.Append(Signed('X', Counts(x, units, dbuPerMicron)));
        sb.Append(Signed('Y', Counts(y, units, dbuPerMicron)));
        return sb.ToString();
    }

    /// <summary>A coordinate as a tagged, seven-digit, sign-prefixed field. The reader takes a sign
    /// immediately after the tag and digits after it, so a negative coordinate is expressible —
    /// which a board whose origin is not its corner needs.</summary>
    private static string Signed(char tag, long counts)
    {
        string digits = Math.Abs(counts).ToString(CultureInfo.InvariantCulture).PadLeft(7, '0');
        return counts < 0 ? $"{tag}-{digits}" : $"{tag}{digits}";
    }

    /// <summary>One coordinate, in this file's own counts. Decimal throughout, never double — the
    /// exactness rule <see cref="LayoutUnits"/>' header states, and 0.0001 in is not representable
    /// in binary floating point. This is <see cref="BoardNetlistFile"/>'s <c>ToDbu</c> inverted, and
    /// the round-trip gate is what holds the two together.</summary>
    private static long Counts(long dbu, BoardNetlistUnits units, int dbuPerMicron) => units switch
    {
        BoardNetlistUnits.MillimetreThousandth =>
            (long)Math.Round(LayoutUnits.FromDbu(dbu, LayoutUnit.Mm, dbuPerMicron) * 1000m,
                             MidpointRounding.AwayFromZero),
        BoardNetlistUnits.InchHundredThousandth =>
            (long)Math.Round(LayoutUnits.FromDbu(dbu, LayoutUnit.Inch, dbuPerMicron) * 100000m,
                             MidpointRounding.AwayFromZero),
        _ => (long)Math.Round(LayoutUnits.FromDbu(dbu, LayoutUnit.Inch, dbuPerMicron) * 10000m,
                              MidpointRounding.AwayFromZero),
    };

    private static int UnitsCode(BoardNetlistUnits units) => units switch
    {
        BoardNetlistUnits.MillimetreThousandth => 1,
        BoardNetlistUnits.InchHundredThousandth => 2,
        _ => 0,
    };

    private static string DimWord(BoardNetlistUnits units) =>
        units == BoardNetlistUnits.MillimetreThousandth ? "MM" : "INCH";

    // ── the long-name table ───────────────────────────────────────────────────

    /// <summary>
    /// R-gi5-10's sibling trap, from the writing side: a net name longer than the record's fourteen
    /// columns cannot be written into one, and TRUNCATING it produces a net name that is WRONG
    /// rather than missing. So a long name goes into the header and the record carries the alias —
    /// the same table the reader already expands, in the spelling where the index is part of the
    /// keyword.
    /// </summary>
    private static void Alias(Dictionary<string, string> aliases, string? net)
    {
        if (net is not { Length: > NetFieldWidth }) return;
        if (aliases.ContainsValue(net)) return;
        aliases[$"NNAME{aliases.Count}"] = Clean(net);
    }

    private static string? NameIn(Dictionary<string, string> aliases, string? net)
    {
        if (net is not { Length: > NetFieldWidth }) return net;
        foreach (var (alias, full) in aliases) if (full == Clean(net)) return alias;
        return net[..NetFieldWidth];
    }

    private static string? NetOf(ViaShape via, Func<ViaShape, string?>? netOfVia) =>
        via.Net is { Length: > 0 } own ? own : netOfVia?.Invoke(via);

    /// <summary>
    /// R-ab3-1d's access code, off the STACKUP's own via span and nothing else.
    ///
    /// <para><c>0</c> is a through feature; <c>n &gt; 0</c> names the one layer it is reachable
    /// from. A BURIED via is reachable from neither surface and there is no code that says so, so it
    /// gets none — and neither does a via whose barrel layer belongs to no via entry, because then
    /// the artwork has not said what it spans.</para>
    /// </summary>
    private static int? AccessOf(ViaShape via, Technology? tech)
    {
        if (tech is null) return null;
        if (ViaSpanResolver.Resolve(via.Layer, tech) is not { } span) return null;
        if (ViaSpanResolver.IsThrough(span, tech)) return 0;

        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.Count == 0) return null;

        if (ReferenceEquals(span.Top, conductors[0])) return 1;
        if (ReferenceEquals(span.Bottom, conductors[^1])) return conductors.Count;
        return null;
    }

    /// <summary>A header value with nothing in it that would end the line or fake a field. Newlines
    /// are the only characters that could, and a name carrying one is a name somebody pasted.</summary>
    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
