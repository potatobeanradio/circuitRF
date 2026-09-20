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
}
