namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The ONE rule that produces what a placement stores about a cell: the reference itself, and the
/// interface hash recorded beside it (SL3 R-sl3-6).
///
/// <para><b>Why this exists rather than a second call to <c>CellInterfaceHash</c> at each drop
/// site.</b> <see cref="ExternalCellRef.MakeCellRef"/> is already the single producing rule for a
/// cell reference, adopted precisely because call sites that each did their own thing drifted. The
/// recorded hash has the same property and must not acquire a second producing site: a placement
/// path that writes the reference and forgets the hash produces an instance that reads as
/// <i>never recorded</i> forever, and nothing would ever report it.</para>
///
/// <para>It cannot live beside <c>MakeCellRef</c> itself: that is in <c>src/Design</c>, and the
/// interface is half <c>Symbol</c>, which is a <c>src/Ui</c> type. So the reference rule stays where
/// it is and this wraps it — one call, both halves, and the wrapper is what placement sites use.</para>
/// </summary>
public static class PlacedCellRef
{
    /// <summary>What a placement of <paramref name="cellAbsDir"/> into a document in
    /// <paramref name="baseDir"/> stores: the reference, and the hash of the interface it is being
    /// placed against (null when the cell does not resolve to a usable symbol — see
    /// <see cref="CellInterfaceHash.For"/>).</summary>
    public static (string CellRef, string? InterfaceHash) For(
        string baseDir, string cellAbsDir, string? workspaceRoot = null)
    {
        string cellRef = ExternalCellRef.MakeCellRef(baseDir, cellAbsDir);
        return (cellRef, CellInterfaceHash.For(cellRef, baseDir, workspaceRoot));
    }

    /// <summary>
    /// The hash to record for a reference that was produced some other way — a <c>pdk://</c> kit
    /// part, a <c>wbond://</c>, a reference typed into the Properties panel, or one rewritten by a
    /// cross-workspace copy. Same rule, same moment, entered by the door those paths already use.
    /// </summary>
    public static string? HashFor(string? cellRef, string? baseDir, string? workspaceRoot = null)
        => CellInterfaceHash.For(cellRef, baseDir, workspaceRoot);
}
