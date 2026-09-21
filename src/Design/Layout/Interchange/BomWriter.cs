// The other half of BomFile — brief-authored-board-3-companion-writers.md R-ab3-1.
//
// ── IT WRITES NO PART NUMBER IT WAS NOT GIVEN (R-ab3-1e) ───────────────────────────────────────
//
// A bill of materials' part number is a PURCHASING decision, and circuitRF does not hold one. There
// is no value it could put there that would be right: a refdes is not a part number, a value is not
// a part number, and a footprint is certainly not one. So BomRow.PartNumber comes back null, the
// column is not written at all, and brief 4's coverage count is what tells a user which rows their
// part library could not answer. A plausible identifier here is the shape that gets the wrong part
// ordered, and nothing on any report would question it.
//
// ── THE NAMING HAZARD, AND IT IS BomFile's VERBATIM (R-rail2-14 item 4) ────────────────────────
//
// "BOM" here is a BILL OF MATERIALS and never a byte order mark. This writer emits no byte order
// mark at all — DelimitedTables strips one on the way in and reports it, and a file circuitRF wrote
// must never produce that report.
//
// ── THE THIN CASE IS FIRST-CLASS (R-ab3-2d) ────────────────────────────────────────────────────
//
// A board that resolves no schematic still gets a bill of materials: the refdes and the footprint
// each placement already carries, and nothing else. Thin, and honest about being thin — which is a
// different thing from absent, and is what the export dialog says before it writes.

using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One part, as much as the design actually states about it.</summary>
/// <param name="Refdes">The placement's own designator.</param>
/// <param name="Value">What the schematic draws as this component's value, where a schematic
/// resolves. Null on a board with none behind it.</param>
/// <param name="Footprint">The land pattern — the <c>Footprint</c> parameter where the schematic
/// states one, else the cell the placement references.</param>
/// <param name="Description">Free text, where the design carries any. <b>Never synthesised</b>: a
/// description parsed back out by <see cref="BomFile.ParseDescription"/> drives the ESR default and
/// the derating, so a sentence this writer invented would become a number nobody typed.</param>
public sealed record BomEntry(
    string Refdes, string? Value, string? Footprint, string? Description);

/// <param name="Board">The board's own name, for the comment line.</param>
public sealed record BomWriteOptions(
    string? Board = null, char Delimiter = ',', string? Provenance = null);

public static class BomWriter
{
    /// <summary>
    /// The column names, in the spelling <see cref="BomFile"/>'s alias lists recognise.
    ///
    /// <para><b>There is no part-number column and that is R-ab3-1e</b> — an empty column with that
    /// heading reads as "this design has no part numbers", which is a claim, where an absent column
    /// reads as "this file does not carry them", which is the fact. <b>Description is what makes
    /// the header a bill of materials at all</b>: <see cref="BomFile.HeaderMatches"/> wants a
    /// reference plus one of part number, quantity or description, and a value column alone would
    /// leave this file unrecognisable as either kind.</para>
    /// </summary>
    private static readonly string[] Columns = ["Refdes", "Value", "Footprint", "Description"];

    public const string DefaultProvenance =
        "Bill of materials projected by circuitRF from the design's own components.";

    public static string Write(
        IReadOnlyList<BomEntry> rows, BomWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var o = options ?? new BomWriteOptions();

        var sb = new StringBuilder();
        sb.Append("# ").Append(o.Provenance ?? DefaultProvenance).Append('\n');
        if (o.Board is { Length: > 0 } board)
            sb.Append("# Board: ").Append(board.Replace('\r', ' ').Replace('\n', ' ').Trim()).Append('\n');

        sb.Append(string.Join(o.Delimiter, Columns)).Append('\n');

        foreach (var row in rows)
        {
            sb.Append(PlacementWriter.Cell(row.Refdes, o.Delimiter)).Append(o.Delimiter);
            sb.Append(PlacementWriter.Cell(row.Value ?? "", o.Delimiter)).Append(o.Delimiter);
            sb.Append(PlacementWriter.Cell(row.Footprint ?? "", o.Delimiter)).Append(o.Delimiter);
            sb.Append(PlacementWriter.Cell(row.Description ?? "", o.Delimiter));
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
