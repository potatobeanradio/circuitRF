// R-rail22-3b — what an import says it read.

using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The one line an import leaves on the window: what the companion tables actually gave up.
/// </summary>
/// <remarks>
/// <b>A BOM that read nine parts out of thirteen should be visible AT IMPORT</b>, not inferred from
/// an odd answer later. <see cref="BomFile"/> has counted all three numbers since brief 2 and
/// nothing showed any of them — the grouped-cell expansion in particular, which is where a bill of
/// materials silently loses members: a cell reading <c>C3, C5, C7, C10, C30</c> is five references,
/// and a fragment that looks like a range and could not be expanded is taken as written and matches
/// nothing.
///
/// <para><b>Not a refusal.</b> It goes beside the status strip rather than through
/// <c>PendingImportRefusal</c>, which gates the Run button — a board whose BOM has two unexpandable
/// cells is a board that still solves, and blocking the run over a report would be worse than the
/// silence it replaces.</para>
/// </remarks>
public static class RailImportReport
{
    /// <summary>
    /// The three counts, or "" where there was no bill of materials to read.
    /// </summary>
    /// <remarks>
    /// A refusal from the reader is said INSTEAD of the counts, because there are none: a BOM that
    /// states no header row produced no rows at all, and reporting "0 rows" over the reader's own
    /// sentence would drop the part that says what to do about it.
    /// </remarks>
    public static string BomSummary(BomTable? bom)
    {
        if (bom is null) return "";
        if (bom.Refusal is { Length: > 0 } refusal) return $"{bom.FileName}: {refusal}";

        string line = $"{bom.FileName}: {bom.SourceRowCount:N0} row(s) read, expanded to "
                    + $"{bom.Rows.Count:N0} reference(s)";

        if (bom.UnexpandedCells.Count > 0)
            line += $"; {bom.UnexpandedCells.Count:N0} reference cell(s) could NOT be expanded and "
                  + "were taken as written, so they match nothing on the board";

        if (bom.UnreadableRows > 0)
            line += $"; {bom.UnreadableRows:N0} row(s) named no reference and were skipped";

        return line + ".";
    }

    /// <summary>
    /// What the board netlist gave up, or "" where there was none to read.
    /// </summary>
    /// <remarks>
    /// <b>Because a netlist railRF could not read was thrown away in silence</b> (reported from the
    /// field, 2026-09-21). A folder of files exported by a schematic tool gives no clue which of them
    /// this row wants; picking one produced a board on which nothing had a net name and a message
    /// saying no board netlist had named any — which is true, and says nothing at all about the file
    /// that was chosen. <c>BoardNetlistFile.Read</c> had the sentence the whole time
    /// (<c>Refusal</c>); <c>ApplyImport</c> classified the PLACEMENT's refusal and dropped this one on
    /// the floor.
    ///
    /// <para><b>Still not a gate</b>, for <see cref="BomSummary"/>'s reason: a board with no netlist
    /// is the ordinary assisted-Gerber path and solves perfectly well by picking the pour. What must
    /// not happen is that a file was NAMED and nothing anywhere says what became of it.</para>
    /// </remarks>
    public static string NetlistSummary(BoardNetlist? netlist)
    {
        if (netlist is null) return "";

        // The refusal INSTEAD of the counts, because there are none — and it carries the one thing
        // the READER cannot know: which of two files with the same word in their name this row
        // wants. The reader names the format and says what the file it was given actually is
        // (BoardNetlistFile.NotThisFormat), and that sentence is the same on every surface; what is
        // added here is the distinction the dialog's own row is about. The set a board is
        // fabricated from carries a board netlist; a schematic tool's own netlist export is a
        // different file for a different purpose and is not it.
        if (netlist.Refusal is { Length: > 0 } refusal)
            return $"{netlist.FileName} {RefusalTail(refusal)}";

        string line = $"{netlist.FileName}: {netlist.Records.Count:N0} feature record(s), "
                    + $"{netlist.Nets.Count:N0} net(s), {netlist.UnitsSummary}";

        if (netlist.UnreadableRecords > 0)
            line += $"; {netlist.UnreadableRecords:N0} record(s) could not be read and were skipped";

        return line + ".";
    }

    /// <summary>
    /// A board-netlist refusal with the clause only this ROW can supply on the end of it.
    /// </summary>
    /// <remarks>
    /// <b>Separate so the import and the OPEN say the same thing</b> (field report, 2026-09-22).
    /// The reader's own sentence names the format and says what the file it was handed actually is;
    /// what it cannot know is which of two files with the same word in their name the user meant to
    /// point at, and that is a fact about this dialog row. Until now the tail existed only on the
    /// import path, so a document that was imported, saved and re-opened reported the same file with
    /// a shorter and less useful sentence on the second surface than on the first.
    /// </remarks>
    public static string RefusalTail(string refusal) =>
        $"{refusal} That row takes the BOARD netlist that ships beside the artwork — one record "
      + "per pad with its net — and not a netlist exported from the schematic.";

    /// <summary>Every companion's own sentence, joined — what the window shows after one import.</summary>
    public static string Summary(BomTable? bom, BoardNetlist? netlist)
    {
        string a = BomSummary(bom), b = NetlistSummary(netlist);
        return a.Length == 0 ? b : b.Length == 0 ? a : a + "  " + b;
    }
}
