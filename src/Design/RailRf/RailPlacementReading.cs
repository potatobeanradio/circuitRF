using System.Collections.Generic;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// The three answers a placement file cannot be read without and does not always carry: where its
/// coordinates are measured from, what unit they are in, and what its columns are called.
/// </summary>
/// <remarks>
/// <b>Every one of these is an answer a HUMAN gave and nothing wrote down</b> (field report,
/// 2026-09-22). The import dialog has refused an unstated origin since R-rail7-7 — correctly: three
/// quarters of a millimetre on an 0402 is the difference between landing on the part's own pad and
/// on its neighbour's. It just never saved the answer, so the same document reopened asked again,
/// and the columns a user named for a headerless file were gone entirely.
///
/// <para><b>Null means "let the file say", never a default.</b> That keeps a document written before
/// this existed reading exactly as it did — and it keeps the distinction the readers' own evidence
/// enums are built on: a value here is <c>Chosen</c>, and its absence leaves the reader to report
/// <c>Declared</c> or <c>Defaulted</c> as it always has.</para>
/// </remarks>
public sealed class RailPlacementReading
{
    /// <summary>Where the file's coordinates are measured from, where a user stated it. Null leaves
    /// it to the file, and a file that states none is a refusal — never a guess.</summary>
    public PlacementOrigin? Origin { get; set; }

    /// <summary>The unit the coordinates are in, where a user stated it. Null reads the file's own
    /// and falls back to this format's usual millimetre, reported as defaulted.</summary>
    public LayoutUnit? Units { get; set; }

    /// <summary>
    /// The column names in order, for a file with NO HEADER ROW — what
    /// <c>PlacementFile.Read</c>'s <c>columns</c> parameter takes, and what
    /// <c>PlacementColumns.ToColumns</c> builds from a user's assignment.
    /// </summary>
    /// <remarks>
    /// An empty string is a column that is not read, which is <c>PlacementColumns.HeaderFor</c>'s
    /// own spelling for it. Null is a file that had a header of its own.
    /// </remarks>
    public List<string>? Columns { get; set; }

    /// <summary>True while nothing here was stated, which is the ordinary case and what a document
    /// written before this existed reads as.</summary>
    public bool IsEmpty => Origin is null && Units is null && Columns is not { Count: > 0 };
}
