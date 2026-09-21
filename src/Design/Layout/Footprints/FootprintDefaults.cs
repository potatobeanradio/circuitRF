// The DEFAULT footprint a freshly placed component gets — brief-footprint-2 R-fp2-3.
//
// ONE function, with its own test, and not a literal in a placement path. There are several
// placement paths (the palette drop, the P-key commit, a type change typed over an existing part),
// and a literal in one of them is a default that depends on how the part got there.

using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// Answers "what footprint, if any, does a component of this kind get when it is first placed on
/// this technology?".
/// </summary>
public static class FootprintDefaults
{
    /// <summary>
    /// The default case size — <c>0201</c>, the smallest size a person still places by hand.
    /// <see cref="SmtCaseTable.DefaultCode"/> is the same decision stated once.
    /// </summary>
    public static string DefaultCaseCode => SmtCaseTable.DefaultCode;

    /// <summary>
    /// The discrete RLC family: the parts whose physical realisation actually IS a chip component
    /// (R-fp2-3a). <c>SRLC</c>/<c>PRLC</c> are deliberately absent — a three-element branch is a
    /// network someone builds, not a part someone buys, so guessing a chip land for one would be
    /// putting artwork on a model.
    /// </summary>
    private static readonly HashSet<SymbolKind> _discreteRlc =
    [
        SymbolKind.Resistor, SymbolKind.Capacitor, SymbolKind.Inductor,
        SymbolKind.Srl, SymbolKind.Src, SymbolKind.Slc,
        SymbolKind.Prl, SymbolKind.Prc, SymbolKind.Plc,
    ];

    /// <summary>True when this kind's physical realisation is a two-terminal chip component.</summary>
    public static bool IsDiscreteRlc(SymbolKind symbol) => _discreteRlc.Contains(symbol);

    /// <summary>
    /// The <c>Footprint</c> value a freshly placed <paramref name="symbol"/> takes on
    /// <paramref name="technology"/>, or <c>null</c> for <b>None</b> — which is the answer
    /// everywhere but the discrete RLC family on a board (R-fp2-3c).
    ///
    /// <para><b>Everything else falls out of the allow-list rather than being listed twice.</b> A
    /// component with a <c>CellRef</c> — a placed cell or a kit part — carries the placeholder kind
    /// <see cref="SymbolKind.Generic"/>, and every microstrip PCell, every <c>SnP</c>, every source,
    /// port, wBond, Match and system block is its own kind; none of the ten is in
    /// <see cref="_discreteRlc"/>, so none of them is offered a default. Stating the exclusions a
    /// second time here would be a second list to keep in step with the first.</para>
    ///
    /// <para><b>Applied at placement only</b> (R-fp2-3d). Nothing re-derives this: the value is then
    /// an ordinary stored parameter the user owns, and a design whose technology later changes keeps
    /// what it was authored with. A default that follows the technology around is a design that
    /// changes when you open it somewhere else.</para>
    /// </summary>
    public static string? For(SymbolKind symbol, Technology? technology)
    {
        if (!IsDiscreteRlc(symbol)) return null;
        if (!LandPatternLayers.IsBoardTechnology(technology)) return null;

        var found = SmtCaseTable.Find(DefaultCaseCode);
        return found is null ? null : FootprintRef.For(found).ToString();
    }
}
