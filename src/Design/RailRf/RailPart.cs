// One part ON the rail, as the document carries it (railrf.md §2.2 "The parts";
// docs/sonnet-briefs/brief-railrf-11-part-models.md R-rail11-8).
//
// ── WHY THE DOCUMENT CARRIES A PART ROW AT ALL ─────────────────────────────────────────────────
//
// The BOM already says which internal part number sits at which refdes, and the part library says
// what that part number IS. Neither of them can say the one thing §2.2 makes per-instance: the
// MOUNTING INDUCTANCE — "the loop from the pad through its via to the plane pair and back", which
// is a property of where the part was placed rather than of what was bought. §6 makes P1 artwork-
// OPTIONAL, so in P1 that number is TYPED; brief 13 computes it from the actual via geometry, and
// §2.2 lets a user override a computed one anyway. Both of those need somewhere on the document to
// live, and it is here.
//
// The consequence worth stating: this list is NOT a second bill of materials and railRF does not
// require one row per capacitor. A rail with an imported BOM carries rows pre-filled from it
// (Origin = Bom, exactly as RailAggressor does); a rail with no artwork and no BOM — the P1 lumped
// case — carries rows somebody typed. The same list serves both, and Origin is what tells them
// apart on the row, because a pre-filled number a user did not check is the one that will be wrong.

namespace CircuitRF.Design.RailRf;

/// <summary>Where a part row came from. Shown on the row — see <see cref="RailPart.Origin"/>.</summary>
public enum RailPartOrigin
{
    /// <summary>Somebody typed it. The default, because nothing else can be assumed about a row.</summary>
    Typed,

    /// <summary>Pre-filled from the bill of materials (brief 2's <c>BomFile</c>).</summary>
    Bom,
}

/// <summary>
/// One part sitting on a rail: which part number it is, where it sits, and what its mounting loop
/// costs.
///
/// <para><b>Nothing here models anything.</b> The model is <see cref="RailPartModel"/> and it is
/// computed by <see cref="RailPartResolver"/> from this row plus the part library. This is the
/// document's half — the identity and the one number only the board knows.</para>
/// </summary>
public sealed record RailPart
{
    /// <summary>The instance on the board — <c>C7</c>. The key a BOM row and a placement row are
    /// both keyed by, and what the parts table's first column reads.</summary>
    public string Refdes { get; init; } = "";

    /// <summary>The internal part number, which is what the model attaches to (§2.2, R-rail2-7).
    /// Empty where the BOM named no part for this refdes, which the parts table lists as unresolved
    /// rather than filling in.</summary>
    public string PartNumber { get; init; } = "";

    /// <summary>
    /// The mounting loop, in HENRIES — typed in P1 (R-rail11-8), computed from the via geometry in
    /// P2a, and overridable either way.
    ///
    /// <para><b>Typically 0.3–1.5 nH, dominating above roughly 50 MHz.</b> Null where nobody typed
    /// one and nothing computed one, which is honest rather than zero: zero mounting inductance puts
    /// the part's resonance where no mounted part has ever resonated.</para></summary>
    public double? MountingInductanceHenries { get; init; }

    /// <summary>Where this row came from — recognised from the BOM, or typed.</summary>
    public RailPartOrigin Origin { get; init; } = RailPartOrigin.Typed;

    /// <summary>Null when this row is usable, or the sentence saying why not.</summary>
    public string? Refusal(string where)
    {
        if (string.IsNullOrWhiteSpace(Refdes))
            return $"{where} has a part row with no refdes. A part row is keyed by refdes, because " +
                   "that is what the BOM and the placement file both key on.";

        if (MountingInductanceHenries is { } l && (l < 0 || double.IsNaN(l) || double.IsInfinity(l)))
            return $"{where}'s part '{Refdes}' states a mounting inductance of {l} H. State it in " +
                   "henries, at or above zero — a mounted part is typically 0.3–1.5 nH.";

        return null;
    }
}
