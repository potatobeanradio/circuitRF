// The other half of PlacementFile — brief-authored-board-3-companion-writers.md R-ab3-1.
//
// ── THE ORIGIN ROW IS WHY THIS FILE IS WORTH WRITING AT ALL (R-ab3-1c) ─────────────────────────
//
// PlacementFile's own header: the coordinate origin is a CHOICE MADE AT EXPORT, the exporting tool
// does not always record which, and three quarters of a millimetre on an 0402 is the difference
// between landing on the part's own pad and landing on its neighbour's. An unstated origin is
// therefore a REFUSAL on the way in — the import dialog asks, with nothing pre-selected.
//
// A FILE circuitRF WROTE MUST NEVER PROVOKE THAT QUESTION. So this writer declares the origin, and
// PlacementOrigin.SymbolOrigin is the honest answer rather than a convenient one: a placement row
// here IS an instance's own origin, which is the footprint cell's own frame origin and nothing
// else. Saying "body centre" because the shipped land patterns happen to be centred on their bodies
// would be true of those cells and false of the first imported one.
//
// ── IT RE-DERIVES NOTHING (R-ab3-1a) ───────────────────────────────────────────────────────────
//
// The rows are the ROOT's own placements, verbatim — the same list LayoutDesignFlatten walks for
// designators (R-fp4b-4b) and PlacedPins walks for pads. A land pattern nested three cells deep
// inside a module is that module's internal business until somebody places the module, and a
// placement table that descended into one would name parts no assembly machine is asked to place.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What one write states about itself.</summary>
/// <param name="Board">The board's own name, for the comment line. Null omits it.</param>
/// <param name="Units">The unit every coordinate is written in. Millimetres by default, which is
/// what <see cref="PlacementFile"/> takes when a file states none — so a file this writer produced
/// and a file that stated nothing read the same, and only the EVIDENCE differs. That is the point.</param>
/// <param name="Delimiter">The separator. A comma unless a value would have to be quoted.</param>
public sealed record PlacementWriteOptions(
    string? Board = null,
    LayoutUnit Units = LayoutUnit.Mm,
    char Delimiter = ',',
    string? Provenance = null);

/// <summary>One row's worth of what a placement table says, taken off one <see cref="LayoutInstance"/>.</summary>
/// <param name="Refdes">The placement's own designator — <see cref="LayoutInstance.DisplayRefDes"/>,
/// which is <c>SchematicId</c> before <c>RefDes</c> and is the one spelling the board draws.</param>
/// <param name="Footprint">What the instance is an instance OF, as a name. Null where the reference
/// resolves to nothing nameable.</param>
public sealed record PlacementEntry(
    string Refdes, long X, long Y, double RotationDegrees, bool Mirror, string? Footprint);

public static class PlacementWriter
{
    /// <summary>The column names, in the spelling <see cref="PlacementFile"/>'s own alias lists
    /// recognise — and deliberately NOT a set that <see cref="BomFile.HeaderMatches"/> also claims,
    /// because a header matching both is a refusal on the way back in (R-rail2-12).</summary>
    private static readonly string[] Columns = ["Refdes", "X", "Y", "Rotation", "Side", "Footprint"];

    public const string DefaultProvenance =
        "Placement projected by circuitRF from the artwork's own placements.";

    /// <summary>
    /// Every placement on <paramref name="view"/>, as the table an assembly house reads.
    /// </summary>
    /// <param name="view">The board. Its INSTANCES are the rows; its own shapes are not parts.</param>
    /// <param name="nameOfFootprint">What to call the cell an instance references — the same
    /// question <c>DisplayRefDes</c> answers for the designator, asked of the cell. Null writes the
    /// reference's own last segment, which is the cell folder's name.</param>
    public static string Write(
        LayoutView view, int dbuPerMicron,
        Func<LayoutInstance, string?>? nameOfFootprint = null,
        PlacementWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        return Write(EntriesOf(view, nameOfFootprint), dbuPerMicron, options);
    }

    /// <summary>The rows a board's placements are, in the root's own order. <b>Public because the
    /// bill of materials is the same walk</b> — one projection, and two tables that cannot name
    /// different parts (R-ab3-2c).</summary>
    public static IReadOnlyList<PlacementEntry> EntriesOf(
        LayoutView view, Func<LayoutInstance, string?>? nameOfFootprint = null)
    {
        ArgumentNullException.ThrowIfNull(view);

        var rows = new List<PlacementEntry>();
        foreach (var inst in view.Instances)
        {
            // R-ab1-1b's rule, one table along: null means THIS PLACEMENT HAS NO IDENTITY, and a
            // row keyed on a fabricated designator is worse than no row — an assembly machine would
            // place a part nothing on the board is called.
            if (inst.DisplayRefDes is not { Length: > 0 } refdes) continue;

            // An ARRAY placement is ONE placement carrying one designator (R-ab1-1d), and it is one
            // row here for that reason: the row states where the placement is, and a machine asked
            // to put N parts down under one designator has been told something false.
            rows.Add(new PlacementEntry(
                refdes, inst.X, inst.Y, inst.RotationDegrees, inst.MirrorX,
                nameOfFootprint is not null ? nameOfFootprint(inst) : CellName(inst.CellRef)));
        }

        return rows;
    }

    /// <summary>The table for rows already in hand.</summary>
    public static string Write(
        IReadOnlyList<PlacementEntry> rows, int dbuPerMicron, PlacementWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var o = options ?? new PlacementWriteOptions();

        var sb = new StringBuilder();
        sb.Append("# ").Append(o.Provenance ?? DefaultProvenance).Append('\n');
        if (o.Board is { Length: > 0 } board) sb.Append("# Board: ").Append(Clean(board)).Append('\n');

        // THE TWO DECLARATIONS, and they are the whole reason a file circuitRF wrote imports without
        // a question (R-ab3-1c). The reader's DeclaredUnits/DeclaredOrigin read a `KEY: value` line
        // out of the preamble, so these spellings are load-bearing and the round-trip gate is what
        // holds them.
        sb.Append("# Units: ").Append(LayoutUnits.AsciiSuffix(o.Units)).Append('\n');
        sb.Append("# Origin: ").Append(OriginWord).Append('\n');

        sb.Append(string.Join(o.Delimiter, Columns)).Append('\n');

        foreach (var row in rows)
        {
            sb.Append(Cell(row.Refdes, o.Delimiter)).Append(o.Delimiter);
            sb.Append(Coordinate(row.X, o.Units, dbuPerMicron)).Append(o.Delimiter);
            sb.Append(Coordinate(row.Y, o.Units, dbuPerMicron)).Append(o.Delimiter);
            sb.Append(Rotation(row.RotationDegrees)).Append(o.Delimiter);
            sb.Append(row.Mirror ? "bottom" : "top").Append(o.Delimiter);
            sb.Append(Cell(row.Footprint ?? "", o.Delimiter));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The word the reader's <c>DeclaredOrigin</c> turns back into
    /// <see cref="PlacementOrigin.SymbolOrigin"/>. Spelled out rather than abbreviated, because this
    /// line is read by people as well as by the reader.</summary>
    private const string OriginWord = "symbol origin";

    /// <summary>One coordinate, exact. Decimal throughout — <see cref="LayoutUnits"/>' own rule, and
    /// the round trip is measured in DBU, so a coordinate printed through a double would come back
    /// a count or two out on a board whose parts are not on a round grid.</summary>
    private static string Coordinate(long dbu, LayoutUnit unit, int dbuPerMicron)
    {
        decimal v = LayoutUnits.FromDbu(dbu, unit, dbuPerMicron);
        return v.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static string Rotation(double degrees) =>
        degrees.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The cell folder an instance references, by name. The reference is a PATH relative to
    /// the layout folder; its last segment is what the project tree calls the cell.</summary>
    internal static string? CellName(string? cellRef)
    {
        if (cellRef is not { Length: > 0 }) return null;
        string name = cellRef.Replace('\\', '/').TrimEnd('/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".clay", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        return name.Length > 0 ? name : null;
    }

    /// <summary>A cell, quoted only where it has to be. <see cref="DelimitedTables"/> reads a quoted
    /// field, and quoting everything would make a file nobody wants to read in a text editor.</summary>
    internal static string Cell(string value, char delimiter)
    {
        string v = value.Replace('\r', ' ').Replace('\n', ' ');
        bool needs = v.Contains(delimiter) || v.Contains('"') ||
                     v.StartsWith(' ') || v.EndsWith(' ');
        return needs ? '"' + v.Replace("\"", "\"\"") + '"' : v;
    }

    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
