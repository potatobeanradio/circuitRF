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
    /// True when a component of this kind may carry a <c>Footprint</c> at all — false for every
    /// transmission-line and microstrip kind (TLIN, CPWG, SLIN, MLIN, MBEND, MCURVE, MTEE, MCROSS,
    /// MTAPER, MKLOPF, the vias). A line's artwork IS its geometry — drawn by its generator from W and
    /// L, or by the line itself — so a land pattern chosen beside it is a second, contradictory answer
    /// to "what does this look like on the board". Asked by the CATEGORY the palette already files the
    /// part under, so a line kind added later is excluded without anyone remembering a list.
    /// </summary>
    public static bool IsOffered(SymbolKind symbol)
        => ComponentTypeRegistry.Get(symbol).Category
            is not (ComponentCategory.Microstrip or ComponentCategory.TransmissionLine);

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

    /// <summary>
    /// The <c>Footprint</c> a vendor library part takes from the case size its header states in BOTH
    /// systems (<c>1005M(0402)</c> → metric <c>1005</c>, imperial <c>0402</c>), or <c>null</c> for
    /// <b>None</b> — brief vendorlib-2 R-vl2-3d. The palette's placement and <c>circuitrf find
    /// --parts</c> both ask this, so the two cannot disagree.
    ///
    /// <para><b>Both codes must match one row</b>: its <see cref="SmtCase.Code"/> is the imperial code
    /// and its <see cref="SmtCase.MetricTwin"/> the metric one. Either code alone is ambiguous —
    /// imperial <c>0201</c> and metric <c>0201</c> are two different parts, 2.4x apart — and the
    /// header states the pair precisely so that nobody has to guess which was meant.</para>
    ///
    /// <para>No pair, a pair the table lacks, or a technology that is not a board gives None. There is
    /// no nearest size and no reading of a part number: the user picks from the ordinary picker.</para>
    ///
    /// <para><b>No technology at all is NOT a reason for None.</b> The case is a property of the part, and a
    /// schematic drawn before its workspace has a technology would otherwise need every part's footprint set by hand
    /// once one is chosen. A technology is needed only to DRAW a land pattern — Update Layout from Schematic, or
    /// placing into a <c>.clay</c> — and the stored reference waits for it. A technology that IS named and is not a
    /// board (an MMIC process) still gives None: there is no land pattern there to draw.</para>
    /// </summary>
    public static string? ForCaseCodes(string? metric, string? imperial, Technology? technology)
    {
        if (string.IsNullOrWhiteSpace(metric) || string.IsNullOrWhiteSpace(imperial)) return null;
        if (technology is not null && !LandPatternLayers.IsBoardTechnology(technology)) return null;

        foreach (var c in SmtCaseTable.All)
            if (string.Equals(c.Code, imperial.Trim(), StringComparison.Ordinal) &&
                string.Equals(c.MetricTwin, metric.Trim(), StringComparison.Ordinal))
                return FootprintRef.For(c).ToString();
        return null;
    }
}
