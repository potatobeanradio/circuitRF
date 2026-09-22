namespace CircuitRF.Ui.RailRf;

/// <summary>
/// What told railRF where a part is — the provenance behind the parts table's <b>where</b> column.
/// </summary>
/// <remarks>
/// <b>It exists because the column used to have exactly one source and behaved as though that were
/// the only one there could be</b> (owner report, 2026-09-22). A part placed in the <c>.clay</c>
/// read "not placed", with a tooltip blaming the absence of a placement file, while the column
/// immediately to its left was naming that same part's land pattern off that same instance.
///
/// <para><b>The two are not interchangeable and the row must not let them read the same.</b> A
/// placement file states the manufacturing CENTROID under a declared origin convention — the thing
/// <c>RailImportOptions.PlacementOrigin</c> refuses to guess, because three quarters of a millimetre
/// on an 0402 is the difference between a part's own pad and its neighbour's. An instance origin is
/// where the land-pattern cell's own origin was dropped. On a sanely drawn footprint they agree; in
/// general they do not. So the file wins where it has a row, and the tooltip says which spoke.</para>
/// </remarks>
public enum RailPartPositionSource
{
    /// <summary>Nothing places this part. The column reads "not placed".</summary>
    Nothing,

    /// <summary>A placement file names this designator. The more specific statement, and it wins.</summary>
    PlacementFile,

    /// <summary>An instance in the artwork carries this designator.</summary>
    Artwork,
}
