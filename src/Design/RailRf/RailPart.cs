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
// require one row per capacitor. A row arrives one of three ways and `Origin` is what tells them
// apart, because a pre-filled number a user did not check is the one that will be wrong:
//
//   Typed     somebody typed it — the P1 lumped case, which §6 makes artwork-OPTIONAL
//   Artwork   `RailPartDiscovery` found it on the board and the user accepted the offer
//   Bom       the same, with its part number pre-filled from the bill of materials
//
// UNTIL brief 26 THIS COMMENT DESCRIBED BOM PRE-FILLING AS EXISTING BEHAVIOUR AND IT DID NOT EXIST:
// `RailPartOrigin.Bom` was assigned by nothing anywhere in `src/`, `new RailPart` appeared only in
// the reader, and the window had no add-part gesture at all — so every row in every document had
// been hand-written into the JSON. `RailPartDiscovery` is the producer this list never had.

namespace CircuitRF.Design.RailRf;

/// <summary>Where a part row came from. Shown on the row — see <see cref="RailPart.Origin"/>.</summary>
public enum RailPartOrigin
{
    /// <summary>Somebody typed it. The default, because nothing else can be assumed about a row —
    /// and what a <c>.crail</c> written before <see cref="Artwork"/> existed still reads as.</summary>
    Typed,

    /// <summary>Pre-filled from the bill of materials (brief 2's <c>BomFile</c>).</summary>
    Bom,

    /// <summary>
    /// Found ON THE BOARD by <see cref="RailPartDiscovery"/> — two pads, one on this rail's copper
    /// and one on its reference — and accepted by the user (R-rail26-3).
    /// </summary>
    /// <remarks>
    /// <b>It carries no part number and nothing was derived from its footprint.</b> An 0402 land
    /// pattern is a case size; it is not a capacitance, an ESR or a dielectric class, and a row
    /// invented from one would be exactly the defaulted number this tool marks and counts
    /// everywhere else. So the row is listed AS unresolved until a BOM or a user says what the part
    /// is — which is honest, and is the state the parts table already renders.
    ///
    /// <para>A user who edits its part number has ADOPTED it, which the existing origin display
    /// already handles (R-rail26-6).</para>
    /// </remarks>
    Artwork,
}

/// <summary>
/// How a part is wired to the rail (<c>brief-railrf-25-series-element.md</c> R-rail25-1a).
/// </summary>
/// <remarks>
/// <b><see cref="Shunt"/> is the default and every row written before this existed reads as one</b>,
/// so no <c>.crail</c> changed meaning when this arrived. The distinction is not cosmetic: a series
/// 1 Ω and a shunt 1 Ω do OPPOSITE things to a rail, and the two are the difference between a
/// decoupling network hanging off one node and a rail that has a before and an after.
/// </remarks>
public enum RailPartConnection
{
    /// <summary>Between the rail and its reference — the decoupling bank and the bulk. Every part
    /// railRF modelled before brief 25.</summary>
    Shunt,

    /// <summary>
    /// IN the rail: two rail-side terminals, everything upstream on one and everything downstream
    /// on the other. A ferrite bead, a protection FET, a sense resistor, a zero-ohm link.
    ///
    /// <para><b>Any number per rail</b> (brief 35, R-rail35-3) — each one cuts the rail, the pieces
    /// are SECTIONS, and the sections must form a tree rooted at the source's.
    /// <see cref="RailSeriesPartition"/> refuses a cycle, a bridged element and a section with no
    /// path to a source, each by name. Brief 25 allowed one.</para>
    /// </summary>
    Series,
}

/// <summary>The side of the board a part is soldered to (owner, 2026-09-24) — see
/// <see cref="RailPart.BoardSide"/>.</summary>
public enum RailBoardSide
{
    /// <summary>Its pads are on the stackup's first copper layer.</summary>
    Top,

    /// <summary>Its pads are on the stackup's last copper layer.</summary>
    Bottom,
}

/// <summary>
/// Which side of a rail's series elements something sits on (R-rail25-2a, R-rail25-2d), where
/// nothing measured it.
/// </summary>
/// <remarks>
/// <b>MEASURED off the artwork where there is artwork</b> — <see cref="RailSeriesPartition"/> cuts
/// the rail at every element's pads and walks, so no user types thirteen capacitors' sides and a
/// re-layout moves them by itself. <see cref="RailPart.Side"/> and <see cref="RailLoad.Side"/> are
/// what the artwork-less case (§6 makes P1 artwork-OPTIONAL) states instead.
///
/// <para><b>Two values for a rail that may have K+1 sections</b> (brief 35), and that is deliberate
/// rather than an omission: it is the file's own spelling from brief 25 and every <c>.crail</c>
/// written since reads through it. A row that needs to name a middle section of a chain names the
/// element it sits behind instead — <see cref="RailPart.Behind"/>, <see cref="RailLoad.Behind"/> —
/// and these two then mean the two ENDS: <see cref="Upstream"/> is the source's section and
/// <see cref="Downstream"/> is the last one in the chain.</para>
/// </remarks>
public enum RailSection
{
    /// <summary>Between the source and the first series element — the source's own section.</summary>
    Upstream,

    /// <summary>Beyond the series element — beyond the LAST one, on a typed chain — which is where
    /// decoupling goes. <b>The default.</b></summary>
    Downstream,
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

    /// <summary>
    /// Whether this part is FITTED. <b>True by default, and absent from the <c>.crail</c> means
    /// true</b>, so every document written before this existed reads exactly as it did
    /// (brief-railrf-23-mount-and-unmount.md R-rail23-1a).
    ///
    /// <para><b>False is not deletion and it is not a data problem.</b> Depopulating a board is the
    /// commonest what-if in power integrity, and until this flag existed it cost an edit to the
    /// ARTWORK — which is destructive, is not what the designer means, and throws away the mounting
    /// loop the geometry gave the part so it cannot be put back the way it was. An unmounted row
    /// keeps its refdes, its part number, its position and its computed mounting inductance; it is
    /// simply not in the frequency model, exactly as a deleted row would not be (R-rail23-1b). The
    /// <c>.clay</c> is never touched — railRF SHOWS the board, and depopulating is a statement about
    /// what is fitted to the geometry rather than a change to it (R-rail23-1c).</para>
    ///
    /// <para><b>It is a different state from unresolved</b> (R-rail23-1d). A row whose part number
    /// does not resolve is a data problem; an unmounted row resolves perfectly well and is
    /// deliberately absent. Two states, two spellings, and nothing may collapse them.</para>
    /// </summary>
    public bool Mounted { get; init; } = true;

    /// <summary>
    /// The side of the board the part is soldered to, or null to take it from the artwork — a
    /// footprint placed MIRRORED is on the bottom, which is what the board exports already assume.
    /// </summary>
    /// <remarks>
    /// Owner, 2026-09-24: a part on the bottom has its pads on BOTTOM copper, and railRF read every
    /// footprint's pads on the layer the footprint draws them on — the top — so a bottom-side
    /// capacitor was left out as "not on this rail's copper". <b>A fact about this board</b>, which is
    /// why it lives on the rail's row and never in a <c>.crlib</c>: the same part number is fitted top
    /// side on one board and bottom side on the next. See <see cref="RailPartSides"/>.
    /// </remarks>
    public RailBoardSide? BoardSide { get; init; }

    // ══ A SERIES ELEMENT (brief 25) ══════════════════════════════════════════════════════════

    /// <summary>
    /// Shunt — every row that existed before brief 25 — or <see cref="RailPartConnection.Series"/>.
    /// <b>Absent from the <c>.crail</c> reads as shunt</b> (R-rail25-1a).
    /// </summary>
    public RailPartConnection Connection { get; init; } = RailPartConnection.Shunt;

    /// <summary>True where this row is the thing the rail runs THROUGH rather than something hung
    /// off it.</summary>
    public bool IsSeries => Connection == RailPartConnection.Series;

    /// <summary>
    /// One rail-side terminal of a series element, and <see cref="TerminalB"/> is the other
    /// (R-rail25-1). Null on a shunt part, which has one rail-side terminal and needs no name for
    /// it.
    ///
    /// <para><b>Both are required wherever there is artwork</b>, because the partition is a cut at
    /// these two pads (R-rail25-2a) and a refdes alone resolves to EVERY pad of the part — which
    /// would merge the element's two ends into one node and model a short. With no artwork they are
    /// not read at all and <see cref="Side"/> states the partition instead (R-rail25-2d).</para>
    /// </summary>
    public RailPortAnchor? TerminalA { get; init; }

    /// <summary>The other end. See <see cref="TerminalA"/>.</summary>
    public RailPortAnchor? TerminalB { get; init; }

    /// <summary>
    /// The DC resistance this element carries the load current through, in OHMS — a ferrite's DCR,
    /// a FET's on-resistance, a link's own (R-rail25-3a).
    ///
    /// <para><b>Null is not zero</b> (R-rail25-3b). It is UNSTATED, and the drop answer then says
    /// the rail's DC total is a LOWER BOUND rather than printing a number that quietly omits the
    /// largest term after the source. <see cref="MountingInductanceHenries"/>' own null is the
    /// precedent.</para>
    /// </summary>
    public double? DcResistanceOhms { get; init; }

    /// <summary>
    /// The series resistance of this element's R-L model over frequency, in OHMS. Null where the
    /// element is modelled from its own measured curve (<see cref="TouchstoneRef"/>), or where
    /// nothing states one.
    ///
    /// <para><b>The same shape <see cref="RailSource"/> carries and the same arithmetic</b>
    /// (R-rail25-1b): a series element is an impedance over frequency, <see cref="RailSourceModel"/>
    /// already IS one, and a second impedance-over-frequency type is exactly what this reuse
    /// avoids. It is a different number from <see cref="DcResistanceOhms"/> — a ferrite's DCR is
    /// milliohms and its impedance at 100 MHz is hundreds of ohms.</para>
    /// </summary>
    public double? SeriesResistanceOhms { get; init; }

    /// <summary>The inductance of the same R-L model, in HENRIES.</summary>
    public double? SeriesInductanceHenries { get; init; }

    /// <summary>
    /// This element's own measured impedance, as a Touchstone file <b>relative to the
    /// <c>.crail</c></b> — the portability rule every other circuitRF document reference follows.
    ///
    /// <para>Where one is given it IS the model and the R-L fields are refused rather than ignored,
    /// which is <see cref="RailSource.Refusal"/>'s own rule arriving on a different row. It is also
    /// what makes R-rail25-1c's sentence go away: a published curve is a measurement of the part,
    /// and an R-L is not.</para>
    /// </summary>
    public string? TouchstoneRef { get; init; }

    /// <summary>
    /// Which side of the rail's series element this part sits on, <b>where nothing measured it</b>
    /// (R-rail25-2d).
    ///
    /// <para><b>Defaulted to <see cref="RailSection.Downstream"/>, which is where decoupling
    /// goes.</b> It is read only on a rail with no artwork: with artwork the partition comes off the
    /// board through <see cref="RailSeriesPartition"/>, because which parts are on which side is a
    /// fact about the copper and asking a user to type it for thirteen capacitors would be both
    /// tedious and wrong the first time somebody moved a part.</para>
    /// </summary>
    public RailSection Side { get; init; } = RailSection.Downstream;

    /// <summary>
    /// The refdes of the series element this row sits directly BEHIND, where nothing measured it
    /// (brief 35, R-rail35-3c) — or null, where <see cref="Side"/> says which end of the chain.
    /// </summary>
    /// <remarks>
    /// <b>Read only on a rail with no artwork</b>, exactly as <see cref="Side"/> is: with artwork the
    /// section is measured off the board. It exists because a rail with two elements in a chain has a
    /// MIDDLE section — between the bead and the switch — that the two-valued <see cref="Side"/>
    /// cannot name. Naming the element is the spelling a designer already has in their head ("C4 is
    /// after FB1"), and a section index would be a number that silently moved the day somebody
    /// inserted a third element.
    ///
    /// <para><b>The typed route is a CHAIN</b> — the series rows in row order, the first nearest the
    /// source — so this is not read on a series row: its position in the chain IS its row order,
    /// and a series row naming another element is refused rather than ignored. Naming an element
    /// and stating <see cref="RailSection.Upstream"/> at once is a contradiction, and is refused
    /// too.</para>
    /// </remarks>
    public string? Behind { get; init; }

    /// <summary>True where this row carries the R-L form rather than a Touchstone file.</summary>
    public bool IsRl => SeriesResistanceOhms is not null || SeriesInductanceHenries is not null;

    /// <summary>Null when this row is usable, or the sentence saying why not.</summary>
    public string? Refusal(string where)
    {
        if (string.IsNullOrWhiteSpace(Refdes))
            return $"{where} has a part row with no refdes. A part row is keyed by refdes, because " +
                   "that is what the BOM and the placement file both key on.";

        if (MountingInductanceHenries is { } l && (l < 0 || double.IsNaN(l) || double.IsInfinity(l)))
            return $"{where}'s part '{Refdes}' states a mounting inductance of {l} H. State it in " +
                   "henries, at or above zero — a mounted part is typically 0.3–1.5 nH.";

        if (Connection == RailPartConnection.Shunt &&
            (TerminalA is not null || TerminalB is not null))
            return $"{where}'s part '{Refdes}' is a SHUNT part and names series terminals. A shunt " +
                   "part has one rail-side terminal; mark it series, or remove the terminals.";

        if (TouchstoneRef is { Length: > 0 } && IsRl)
            return $"{where}'s part '{Refdes}' states both a series R-L and a Touchstone file " +
                   $"('{TouchstoneRef}'). An element has one model over frequency: keep the " +
                   "measured file, or remove it and keep the R-L.";

        if (SeriesResistanceOhms is { } sr && (sr < 0 || double.IsNaN(sr)))
            return $"{where}'s part '{Refdes}' states a series resistance of {sr} Ω.";

        if (SeriesInductanceHenries is { } sl && (sl < 0 || double.IsNaN(sl)))
            return $"{where}'s part '{Refdes}' states a series inductance of {sl} H.";

        if (DcResistanceOhms is { } dcr && (dcr < 0 || double.IsNaN(dcr) || double.IsInfinity(dcr)))
            return $"{where}'s part '{Refdes}' states a DC resistance of {dcr} Ω. State it in ohms, " +
                   "at or above zero — or leave it out, which says it is UNSTATED rather than zero.";

        if (Behind is { Length: > 0 } behind)
        {
            if (Connection == RailPartConnection.Series)
                return $"{where}'s series part '{Refdes}' says it sits behind '{behind}'. A series " +
                       "element's place in a typed chain is its ROW ORDER — the first series row is " +
                       "nearest the source — so remove the name, or move the row.";
            if (Side == RailSection.Upstream)
                return $"{where}'s part '{Refdes}' is stated both UPSTREAM and behind '{behind}'. " +
                       "Upstream is the source's own section, in front of every element; name the " +
                       "element, or state upstream, not both.";
        }

        if (TerminalA?.Refusal($"{where}'s series part '{Refdes}', first terminal,") is { } ta) return ta;
        if (TerminalB?.Refusal($"{where}'s series part '{Refdes}', second terminal,") is { } tb) return tb;

        return null;
    }
}
