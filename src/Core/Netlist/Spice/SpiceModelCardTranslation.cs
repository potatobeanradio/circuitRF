namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// One parameter of a translated card, in circuitRF's own spelling.
/// </summary>
/// <param name="Name">The circuitRF parameter name — a factory key, exactly as the engine reads it.</param>
/// <param name="Expression">
/// The value, already in circuitRF's grammar and in <b>base SI units</b>. The reader has normalised
/// the dialect's own suffixes away (<c>1p</c> is <c>1e-12</c> by the time it gets here), and a
/// <c>.model</c> card states everything unscaled, so whatever writes this into a design must give
/// the row a base-unit token — never the schematic default's convenience unit.
/// </param>
/// <param name="Source">Which spelling on the card it came from, so a report can say so.</param>
public sealed record ModelCardParameter(string Name, string Expression, string Source);

/// <summary>What a card becomes: one circuitRF component, its parameters, and what was left over.</summary>
/// <param name="EngineReference">
/// The component-model factory key — "Diode", "BJT_NPN", "FET_Statz", "R", … Never a symbol or a
/// palette name: this layer has no UI in it, and the caller maps the reference onto whatever it
/// draws.
/// </param>
/// <param name="Parameters">Every card parameter circuitRF has a home for, in a stable order.</param>
/// <param name="Unmapped">
/// Card parameters this translation does NOT carry, by their own spelling. <b>Never empty because
/// nothing was dropped — empty because nothing was.</b> A card's noise coefficients, a substrate
/// junction, a level number: each is real, each is silently absent from the built cell, and a user
/// who is not told has no way to find out.
/// </param>
/// <param name="Notes">
/// Decisions the translation made that a user could reasonably want to see — which of two published
/// laws a MESFET card was read as, why a charge model was selected. Not warnings.
/// </param>
public sealed record ModelCardBinding(
    string                              EngineReference,
    IReadOnlyList<ModelCardParameter>   Parameters,
    IReadOnlyList<string>               Unmapped,
    IReadOnlyList<string>               Notes);

/// <summary>A card and what circuitRF can make of it — one or the other, never both.</summary>
/// <param name="Card">The card as read.</param>
/// <param name="Binding">What it becomes, or null when it becomes nothing.</param>
/// <param name="Refusal">
/// Why it becomes nothing, as a sentence naming the type. Null when <paramref name="Binding"/> is
/// set. A refusal is a complete answer and is shown verbatim.
/// </param>
public sealed record ModelCardTranslation(
    SpiceModelCard  Card,
    ModelCardBinding? Binding,
    string?         Refusal)
{
    /// <summary>True when this card can be built.</summary>
    public bool IsSupported => Binding is not null;
}

/// <summary>
/// Turns a <c>.model</c> card into the circuitRF component that implements it.
///
/// <para><b>Why this is not part of <see cref="SpicePassiveModelBinding"/>.</b> That pass answers a
/// different question — "an instance in this netlist names a card, what does that instance become?"
/// — and it deliberately touches only <c>R</c> and <c>C</c>, leaving every semiconductor card alone
/// for whatever supplies that device. This one answers "here is a card and nothing else; what
/// device IS it?", which is the import gesture: there is no instance, no geometry and no netlist
/// around it.</para>
///
/// <para><b>A type circuitRF has no model for is REFUSED BY NAME, never approximated.</b> The
/// nearest-native temptation is real — a JFET's square law looks like the Curtice quadratic with the
/// <c>tanh</c> ignored, a ferrite bead looks like a parallel RLC — and every one of those produces a
/// cell that simulates and is quantitatively wrong, with nothing anywhere reporting it. The user
/// asked for a model card to be imported; being told "circuitRF has no VDMOS model" costs them a
/// minute, and a plausible wrong transistor costs them the measurement they built around it.</para>
///
/// <para><b>Nothing is invented to fill a gap either.</b> A resistor card that states only a sheet
/// resistance has no resistance without a geometry to apply it to, and is refused for exactly the
/// reason <see cref="SpicePassiveModelBinding"/> refuses the same card on an instance: a value of
/// zero simulates perfectly.</para>
/// </summary>
public static class SpiceModelCardTranslation
{
    // ─────────────────────────────────────────────────────────────────────────
    //  The maps
    //
    //  Each entry is  circuitRF name  ←  the card spellings that mean it, in PREFERENCE order.
    //
    //  Written target-first rather than source-first because several published spellings denote one
    //  quantity (a diode's zero-bias junction capacitance is CJO, CJ0 or CJ depending on the
    //  dialect) and a source-keyed map makes that collision unrepresentable — three entries would
    //  each claim the same target and the last one read would win, silently and by file order.
    //  Target-first also makes "which of these did the card actually state" a lookup rather than a
    //  search, which is what the report needs.
    //
    //  An ALIAS IS ONLY LISTED WHERE IT IS THE SAME QUANTITY.  Old SPICE's C2 and C4 are NOT listed
    //  against Ise and Isc: they are MULTIPLIERS of IS, not currents, and reading one as the other
    //  is off by fourteen orders of magnitude on a card that looks entirely ordinary.
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly (string Target, string[] Sources)[] DiodeMap =
    [
        ("Is",   ["IS"]),
        ("Rs",   ["RS"]),
        ("N",    ["N"]),
        ("Tt",   ["TT"]),
        ("Cj0",  ["CJO", "CJ0", "CJ"]),
        ("Vj",   ["VJ", "PB"]),
        ("M",    ["M", "MJ"]),
        ("Fc",   ["FC"]),
        ("Bv",   ["BV"]),
        ("Ibv",  ["IBV"]),
        ("Isr",  ["ISR"]),
        ("Nr",   ["NR"]),
        ("Nbv",  ["NBV"]),
        ("Area", ["AREA"]),
        ("Xti",  ["XTI"]),
        ("Eg",   ["EG"]),
        ("Temp", ["TEMP"]),
        ("Tnom", ["TNOM"]),
    ];

    private static readonly (string Target, string[] Sources)[] BjtMap =
    [
        ("Is",   ["IS"]),
        ("Bf",   ["BF"]),
        ("Nf",   ["NF"]),
        ("Vaf",  ["VAF", "VA"]),
        ("Ikf",  ["IKF", "IK"]),
        ("Ise",  ["ISE"]),
        ("Ne",   ["NE"]),
        ("Br",   ["BR"]),
        ("Nr",   ["NR"]),
        ("Var",  ["VAR", "VB"]),
        ("Ikr",  ["IKR"]),
        ("Isc",  ["ISC"]),
        ("Nc",   ["NC"]),
        ("Rb",   ["RB"]),
        ("Irb",  ["IRB"]),
        ("Rbm",  ["RBM"]),
        ("Re",   ["RE"]),
        ("Rc",   ["RC"]),
        ("Cje",  ["CJE"]),
        ("Vje",  ["VJE", "PE"]),
        ("Mje",  ["MJE", "ME"]),
        ("Cjc",  ["CJC"]),
        ("Vjc",  ["VJC", "PC"]),
        ("Mjc",  ["MJC", "MC"]),
        ("Xcjc", ["XCJC"]),
        ("Fc",   ["FC"]),
        ("Tf",   ["TF"]),
        ("Xtf",  ["XTF"]),
        ("Vtf",  ["VTF"]),
        ("Itf",  ["ITF"]),
        ("Tr",   ["TR"]),
        ("Area", ["AREA"]),
        ("Xti",  ["XTI"]),
        ("Xtb",  ["XTB"]),
        ("Eg",   ["EG"]),
        ("Temp", ["TEMP"]),
        ("Tnom", ["TNOM"]),
    ];

    /// <summary>
    /// Shared by both MESFET laws. <c>B</c> belongs to Statz alone and is added there.
    ///
    /// <para><c>RD</c> and <c>RS</c> are deliberately absent: circuitRF's FET family has no drain or
    /// source parasitic resistance of its own, so they cannot be carried as parameters. They are not
    /// dropped — <see cref="MesfetLeadResistance"/> reports them so a caller can place them as the
    /// real series resistors they are, which is what the built cell does.</para>
    /// </summary>
    private static readonly (string Target, string[] Sources)[] MesfetMap =
    [
        ("Vto",     ["VTO", "VT0"]),
        ("Beta",    ["BETA"]),
        ("Alpha",   ["ALPHA"]),
        ("Lambda",  ["LAMBDA"]),
        ("Cgs",     ["CGS"]),
        ("Cgd",     ["CGD"]),
        ("Vbi",     ["VBI", "PB"]),
        ("Mj",      ["M", "MJ"]),
        ("Fc",      ["FC"]),
        ("Is",      ["IS"]),
        ("N",       ["N"]),
        ("Xti",     ["XTI"]),
        ("Eg",      ["EG"]),
        ("Betatc",  ["BETATCE", "BETATC"]),
        ("Alphatc", ["ALPHATC"]),
        ("Vtotc",   ["VTOTC"]),
        ("Temp",    ["TEMP"]),
        ("Tnom",    ["TNOM"]),
    ];

    /// <summary>
    /// The ferrite-bead card.
    ///
    /// <para><b>Spellings vary more here than anywhere else in this file</b>, because a bead card is
    /// a recent and un-standardised thing: the four elements go by several names across dialects. So
    /// each target lists what it has been seen as, and anything not matched lands in
    /// <c>Unmapped</c> and is reported by name — which is the honest handling for a format with no
    /// settled vocabulary.</para>
    ///
    /// <para><b>The parallel resistance is NOT aliased onto the series one.</b> <c>R</c> and
    /// <c>Rdc</c> are the two most collision-prone spellings on such a card and they mean opposite
    /// things — one caps the impedance at resonance, the other sets the DC drop — so <c>R</c> is
    /// read as the PARALLEL loss, which is what a bead card means by it, and only the explicitly
    /// DC-flavoured spellings feed <c>Rdc</c>.</para>
    /// </summary>
    private static readonly (string Target, string[] Sources)[] BeadMap =
    [
        ("Rdc", ["RDC", "RSER", "RS"]),
        ("L",   ["L", "LSER", "LS"]),
        ("Rp",  ["RP", "RPAR", "R"]),
        ("Cp",  ["CP", "CPAR", "C"]),
    ];

    /// <summary>
    /// The vertical power MOSFET card.
    ///
    /// <para>Carries the body diode's parameters as its own — <c>IS</c>/<c>N</c>/<c>BV</c>/
    /// <c>IBV</c>/<c>TT</c>/<c>CJO</c>/<c>VJ</c>/<c>MJ</c> — because on this card those describe
    /// the intrinsic diode, not a substrate junction. They are the same SPELLINGS a plain diode
    /// card uses and they mean the same physics, which is why they map onto the same names.</para>
    ///
    /// <para><c>RG</c> IS carried, which no other family here has: the gate resistance of a power
    /// MOSFET sits in the drive path in series with a very large capacitance, so it is a parameter
    /// of the device rather than something to place beside it.</para>
    /// </summary>
    private static readonly (string Target, string[] Sources)[] VdmosMap =
    [
        ("Vto",    ["VTO", "VT0", "VTH"]),
        ("Kp",     ["KP"]),
        ("Lambda", ["LAMBDA"]),
        ("Rds",    ["RDS"]),
        ("Is",     ["IS"]),
        ("N",      ["N"]),
        ("Bv",     ["BV"]),
        ("Ibv",    ["IBV"]),
        ("Nbv",    ["NBV"]),
        ("Tt",     ["TT"]),
        ("Cjo",    ["CJO", "CJ0"]),
        ("Vj",     ["VJ", "PB"]),
        ("Mj",     ["M", "MJ"]),
        ("Fc",     ["FC"]),
        ("Cgs",    ["CGS"]),
        ("Cgdmax", ["CGDMAX"]),
        ("Cgdmin", ["CGDMIN"]),
        ("Rg",     ["RG"]),
        ("Rd",     ["RD"]),
        ("Rs",     ["RS"]),
        ("Vtotc",  ["VTOTC", "TCVTH"]),
        ("Xti",    ["XTI"]),
        ("Eg",     ["EG"]),
        ("Temp",   ["TEMP"]),
        ("Tnom",   ["TNOM"]),
    ];

    /// <summary>
    /// The junction-FET card, shared by both channel types — one set of equations with one sign
    /// changed, so nothing here branches on polarity and <c>VTO</c> is carried exactly as stated
    /// (negative for n-channel, positive for p-channel).
    ///
    /// <para><c>RD</c> and <c>RS</c> ARE carried here, unlike on the MESFET card above, and the
    /// difference is not an inconsistency: circuitRF's JFET has ohmic drain and source resistances
    /// as model parameters, on internal nodes the elaborator mints, and its MESFET family does not.
    /// A card's <c>RD</c> therefore belongs in the device here and in the schematic there.</para>
    ///
    /// <para><c>B</c>, <c>ALPHA</c> and <c>VK</c> are deliberately absent. They belong to higher
    /// published JFET levels — a doping-profile tail and a channel-length-modulation pair — and
    /// there is no square-law parameter that means the same thing, so they land in <c>Unmapped</c>
    /// and are reported rather than being folded into <c>Lambda</c>, which is a different
    /// quantity.</para>
    /// </summary>
    private static readonly (string Target, string[] Sources)[] JfetMap =
    [
        ("Vto",     ["VTO", "VT0"]),
        ("Beta",    ["BETA"]),
        ("Lambda",  ["LAMBDA"]),
        ("Is",      ["IS"]),
        ("N",       ["N"]),
        ("Isr",     ["ISR"]),
        ("Nr",      ["NR"]),
        ("Cgs",     ["CGS"]),
        ("Cgd",     ["CGD"]),
        ("Pb",      ["PB", "VJ"]),
        ("M",       ["M", "MJ"]),
        ("Fc",      ["FC"]),
        ("Rd",      ["RD"]),
        ("Rs",      ["RS"]),
        ("Area",    ["AREA"]),
        ("Xti",     ["XTI"]),
        ("Eg",      ["EG"]),
        ("Vtotc",   ["VTOTC"]),
        ("Betatce", ["BETATCE", "BETATC"]),
        ("Temp",    ["TEMP"]),
        ("Tnom",    ["TNOM"]),
    ];

    /// <summary>
    /// The MOS level-1 card. Shared by both channel types, which are one set of equations with one
    /// sign changed — the card's own <c>VTO</c> is negative for a p-channel part and circuitRF reads
    /// it exactly as stated, so nothing here branches on polarity.
    ///
    /// <para><b>Both the DEVICE and the PROCESS spelling of a quantity are carried where the card
    /// may state either</b> — <c>KP</c> or <c>UO</c>, <c>GAMMA</c>/<c>PHI</c> or <c>NSUB</c>,
    /// <c>RD</c>/<c>RS</c> or <c>RSH</c> with <c>NRD</c>/<c>NRS</c>, <c>CBD</c>/<c>CBS</c> or
    /// <c>CJ</c>/<c>CJSW</c> with the junction areas. They are NOT aliases of one another and are
    /// not listed as such: they are different quantities, and the model derives the device one from
    /// the process one only where the device one is absent. Aliasing a mobility onto a
    /// transconductance parameter would be off by the oxide capacitance, which is four orders of
    /// magnitude.</para>
    ///
    /// <para><c>LEVEL</c> is deliberately absent, and lands in <c>Unmapped</c> so nobody concludes
    /// it was honoured — the same rule the MESFET card follows, for the same reason. Which level a
    /// card is read as is decided by <see cref="Mosfet"/> from the parameters it states.</para>
    /// </summary>
    private static readonly (string Target, string[] Sources)[] MosfetMap =
    [
        ("Vto",    ["VTO", "VT0"]),
        ("Kp",     ["KP"]),
        ("Gamma",  ["GAMMA"]),
        ("Phi",    ["PHI"]),
        ("Lambda", ["LAMBDA"]),
        ("W",      ["W"]),
        ("L",      ["L"]),
        ("Ld",     ["LD"]),
        ("Tox",    ["TOX"]),
        ("Uo",     ["UO", "U0"]),
        ("Nsub",   ["NSUB"]),
        // Level 3's own six. They are collected for BOTH levels — a level-1 binding simply leaves
        // them unconsumed, and they then appear in Unmapped and are reported, which is exactly what
        // should happen to a short-channel parameter a square law cannot use.
        ("Eta",    ["ETA"]),
        ("Theta",  ["THETA"]),
        ("Kappa",  ["KAPPA"]),
        ("Vmax",   ["VMAX"]),
        ("Delta",  ["DELTA"]),
        ("Xj",     ["XJ"]),
        ("Cgso",   ["CGSO"]),
        ("Cgdo",   ["CGDO"]),
        ("Cgbo",   ["CGBO"]),
        ("Is",     ["IS"]),
        ("Js",     ["JS"]),
        ("N",      ["N"]),
        ("Cbd",    ["CBD"]),
        ("Cbs",    ["CBS"]),
        ("Cj",     ["CJ"]),
        ("Cjsw",   ["CJSW"]),
        ("Ad",     ["AD"]),
        ("As",     ["AS"]),
        ("Pd",     ["PD"]),
        ("Ps",     ["PS"]),
        ("Pb",     ["PB"]),
        ("Mj",     ["MJ"]),
        ("Mjsw",   ["MJSW"]),
        ("Fc",     ["FC"]),
        ("Rd",     ["RD"]),
        ("Rs",     ["RS"]),
        ("Rsh",    ["RSH"]),
        ("Nrd",    ["NRD"]),
        ("Nrs",    ["NRS"]),
        ("Xti",    ["XTI"]),
        ("Eg",     ["EG"]),
        ("Temp",   ["TEMP"]),
        ("Tnom",   ["TNOM"]),
    ];

    private static readonly (string Target, string[] Sources)[] ResistorMap =
    [
        ("R",    ["R"]),
        ("TC1",  ["TC1", "TC"]),
        ("TC2",  ["TC2"]),
        ("Temp", ["TEMP"]),
        ("Tnom", ["TNOM"]),
    ];

    private static readonly (string Target, string[] Sources)[] CapacitorMap =
    [
        ("C", ["C"]),
    ];

    private static readonly (string Target, string[] Sources)[] InductorMap =
    [
        ("L", ["L"]),
    ];

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry points
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Translates every card, preserving order. Supported and refused are both returned.</summary>
    public static IReadOnlyList<ModelCardTranslation> TranslateAll(IEnumerable<SpiceModelCard> cards)
        => [.. (cards ?? []).Select(Translate)];

    /// <summary>Translates one card into the circuitRF component that implements it, or refuses it.</summary>
    public static ModelCardTranslation Translate(SpiceModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        string type = card.ModelType.Trim().ToUpperInvariant();

        return type switch
        {
            "D"                 => Bind(card, "Diode",   DiodeMap),
            "NPN"               => Bind(card, "BJT_NPN", BjtMap),
            "PNP"               => Bind(card, "BJT_PNP", BjtMap),
            "NMF"               => Mesfet(card, pChannel: false),
            "PMF"               => Mesfet(card, pChannel: true),
            "VDMOS"             => Vdmos(card),
            "BEAD"              => Bead(card),
            "NJF"               => Jfet(card, "JFET_N"),
            "PJF"               => Jfet(card, "JFET_P"),
            "NMOS"              => Mosfet(card, nChannel: true),
            "PMOS"              => Mosfet(card, nChannel: false),
            "RES" or "R"        => Resistor(card),
            "CAP" or "C"        => Passive(card, "C", CapacitorMap, "capacitance"),
            "IND" or "L"        => Passive(card, "L", InductorMap, "inductance"),
            _                   => new ModelCardTranslation(card, null, RefusalFor(card, type)),
        };
    }

    /// <summary>
    /// Whether a type is one circuitRF could ever build, ignoring whether THIS card completes.
    /// Used to decide whether a file has anything worth offering at all.
    /// </summary>
    public static bool IsSupportedType(string modelType) =>
        modelType?.Trim().ToUpperInvariant() is
            "D" or "NPN" or "PNP" or "NMF" or "PMF" or "NJF" or "PJF"
            or "NMOS" or "PMOS" or "VDMOS" or "BEAD"
            or "RES" or "R" or "CAP" or "C" or "IND" or "L";

    /// <summary>
    /// The drain and source lead resistances a MESFET card states, which circuitRF's FET has no
    /// parameter for. Null entries where the card is silent. A caller that draws a schematic places
    /// them as ordinary series resistors; one that cannot is told by <c>Unmapped</c> instead.
    /// </summary>
    public static (string? Rd, string? Rs) MesfetLeadResistance(SpiceModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return (Find(card, ["RD"]).Value, Find(card, ["RS"]).Value);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Per-family binding
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Which published MESFET law a card states is decided from its PARAMETERS, not its
    /// LEVEL.</b> The level numbering is not portable — the same integer selects a different law in
    /// different dialects — so reading it would make the choice depend on which simulator the file
    /// was written for, a fact the file does not record. <c>B</c> (the doping-profile tail) appears
    /// in the Statz law and in no other, so its presence is the file's own unambiguous statement.
    /// The choice is reported either way, and a stated LEVEL is listed as unmapped so nobody
    /// concludes it was honoured.
    /// </summary>
    private static ModelCardTranslation Mesfet(SpiceModelCard card, bool pChannel)
    {
        bool statz = card.Parameters.ContainsKey("B");

        var map = statz ? [.. MesfetMap, ("B", new[] { "B" })] : MesfetMap;
        // Both laws a MESFET card can be read as have a p-channel form, which is why a PMF card is
        // no longer refused: the two are anchored to a threshold and even in it, so mirroring is
        // exactly "flip every voltage and current". The other three laws in the family are
        // n-channel only and a card is never read as one of them.
        string reference = (pChannel ? "PFET_" : "FET_") + (statz ? "Statz" : "Curtice");

        var (parameters, unmapped) = Collect(card, map);
        var notes = new List<string>
        {
            statz
                ? "Read as the Statz law (it states B, the doping-profile parameter, which no other "
                + "published MESFET law carries)."
                : "Read as the Curtice quadratic law (it states no B, the parameter that would make "
                + "it Statz).",
        };

        if (pChannel)
            notes.Add(
                "Read as a p-channel device: every voltage, current and charge is mirrored, and VTO "
                + "is carried exactly as the card states it (positive for a p-channel depletion "
                + "MESFET) because the model applies the channel sign itself.");

        // The gate charge SPICE's MESFET carries is bias-dependent depletion, not the constant
        // Cgs/Cgd circuitRF defaults to. Selecting it here is what makes an imported card's
        // capacitance mean what the card meant; leaving the default would quietly turn a junction
        // into a fixed capacitor of the same zero-bias value.
        if (parameters.Any(p => p.Name is "Cgs" or "Cgd"))
        {
            parameters.Add(new ModelCardParameter("CapModel", "2", "(circuitRF)"));
            notes.Add(
                "CapModel set to 2 (bias-dependent junction charge) because the card states a gate "
                + "capacitance, which is what CGS/CGD mean on a MESFET card. circuitRF's own default "
                + "is a constant capacitance of the same zero-bias value.");
        }

        // RD/RS have no parameter to go to and are reported here as well as in Unmapped, because a
        // caller that CAN place them still wants to know it did.
        var (rd, rs) = MesfetLeadResistance(card);
        if (rd is not null || rs is not null)
            notes.Add(
                "The card states a lead resistance (" +
                string.Join(", ", new[] { rd is null ? null : $"RD={rd}", rs is null ? null : $"RS={rs}" }
                    .Where(x => x is not null)) +
                "). circuitRF's FET has no parameter for it, so it belongs in the schematic as a "
                + "series resistor rather than in the device.");

        // Statz's own charge formulation is not circuitRF's — say so where it could matter, rather
        // than letting a Statz card look fully carried when its charge model is an approximation.
        if (statz && parameters.Any(p => p.Name is "Cgs" or "Cgd"))
            notes.Add(
                "circuitRF implements the standard depletion charge, not the Statz/TOM charge "
                + "formulation (which works on a smoothed effective voltage). Below pinch-off the "
                + "two agree closely; near it they do not.");

        return new ModelCardTranslation(
            card, new ModelCardBinding(reference, parameters, unmapped, notes), null);
    }

    /// <summary>
    /// A ferrite-bead card. <b>Refused when it states nothing but a DC resistance</b>, for the same
    /// reason a resistor card stating only a sheet resistance is: what would be built is a resistor
    /// of a few milliohms, which simulates perfectly and is not a bead.
    ///
    /// <para>A card that states an inductance but no parallel loss is BUILT, with a note: that is a
    /// bead whose impedance rises for ever and never peaks, which is a real (if optimistic) reading
    /// of an under-specified card rather than a wrong one. The difference from the refusal above is
    /// that something was actually said about the ferrite.</para>
    /// </summary>
    private static ModelCardTranslation Bead(SpiceModelCard card)
    {
        var (parameters, unmapped) = Collect(card, BeadMap);

        bool hasTank = parameters.Any(p => p.Name is "L" or "Rp" or "Cp");
        if (!hasTank)
            return new ModelCardTranslation(card, null,
                $"'{card.Name}' is a BEAD card that states no inductance, no parallel loss and no "
                + "parallel capacitance, so there is nothing in it that describes a ferrite. What "
                + "could be built from it is a resistor of a few milliohms, which is not a bead and "
                + "would simulate perfectly.");

        var notes = new List<string>
        {
            "Read as the four-element bead equivalent: Rdc in series with a parallel L, Rp and Cp. "
            + "Rp is the core loss and is what CAPS the impedance — at the parallel resonance the "
            + "reactive branches cancel and |Z| is Rdc + Rp, which is the peak a data sheet plots.",
        };

        if (!parameters.Any(p => p.Name == "Rp"))
            notes.Add(
                "The card states no parallel loss, so the impedance rises without a peak and the "
                + "bead never becomes resistive. That is what the card says; if you have the data "
                + "sheet's impedance curve, its maximum is Rdc + Rp and is worth entering.");

        if (!parameters.Any(p => p.Name == "Cp"))
            notes.Add(
                "The card states no parallel capacitance, so the impedance goes on rising above "
                + "resonance instead of falling. A real bead stops working above its own resonance, "
                + "and this parameter is what says where.");

        notes.Add(
            "SATURATION is not modelled and cannot be: a bead's inductance falls with DC bias "
            + "current, sometimes by most of it, and this is a linear element. The parameters "
            + "describe the part at whatever current they were measured at.");

        return new ModelCardTranslation(
            card, new ModelCardBinding("Bead", parameters, unmapped, notes), null);
    }

    /// <summary>
    /// A vertical power MOSFET card.
    ///
    /// <para><b>The channel comes from a BARE KEYWORD on the card, not from the type name</b>, which
    /// is unlike every other family here: the type is <c>VDMOS</c> either way, and a lone
    /// <c>pchan</c> is what makes it p-channel. That is why <see cref="SpiceModelCard.Flags"/>
    /// exists — a reader that keeps only <c>key=value</c> pairs cannot tell the two apart at all,
    /// and would import a p-channel part as an n-channel one silently.</para>
    ///
    /// <para><b>A card with no such keyword is read as n-channel, and a negative threshold on one is
    /// REPORTED rather than acted on.</b> A negative <c>VTO</c> is what a p-channel part looks like,
    /// but it is also what a (rare, real) n-channel depletion part looks like, and guessing between
    /// them is exactly what this layer refuses to do. Saying so lets the user place the p-channel
    /// tile instead, which takes a second; guessing wrong costs them the measurement.</para>
    /// </summary>
    private static ModelCardTranslation Vdmos(SpiceModelCard card)
    {
        bool pChannel = card.HasFlag("PCHAN") || card.HasFlag("PCHANNEL") || card.HasFlag("PMOS");
        var (parameters, unmapped) = Collect(card, VdmosMap);

        var notes = new List<string>
        {
            pChannel
                ? "Read as a p-channel device: the card states the channel as a bare keyword, which "
                + "is how this card type states it. Every voltage, current and charge is mirrored, "
                + "and VTO is carried exactly as stated."
                : "Read as an n-channel device: the card states no channel keyword, and n-channel is "
                + "what that means for this card type.",
            "circuitRF's vertical MOSFET has THREE terminals — the source-to-body short is inside "
            + "the silicon, which is what turns the substrate junction into the body diode between "
            + "source and drain. The card's IS/N/BV/TT/CJO describe that diode, and they are carried "
            + "onto it; its current is reported on its own branch.",
        };

        if (!pChannel
            && Find(card, ["VTO", "VT0", "VTH"]).Value is { } vto
            && double.TryParse(vto, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v)
            && v < 0)
            notes.Add(
                $"The card states a NEGATIVE threshold (VTO={vto}) and no channel keyword. That is "
                + "what a p-channel part looks like — but it is also what a depletion-mode n-channel "
                + "part looks like, and nothing on the card separates the two. It has been read as "
                + "n-channel, which is what the absence of a keyword means. If it is a p-channel "
                + "part, build it against the p-channel component instead.");

        // Quasi-saturation is the one mechanism a card may describe that this model has no place for
        // at all, so it is named rather than left to the unmapped list to explain itself.
        if (new[] { "MTRIODE", "SUBTHRES", "KSUBTHRES", "RQ", "VQ" }.Any(k => Find(card, [k]).Value is not null))
            notes.Add(
                "The card states parameters for triode-region shaping or subthreshold conduction. "
                + "circuitRF's vertical MOSFET has neither: its channel is the square law, so it "
                + "goes to exactly zero at threshold and its knee is the square law's own. Those "
                + "parameters are listed below as not carried.");

        return new ModelCardTranslation(
            card, new ModelCardBinding(pChannel ? "VDMOS_P" : "VDMOS_N", parameters, unmapped, notes), null);
    }

    /// <summary>
    /// A junction-FET card. One law, so there is no level to decide — but a card stating parameters
    /// that only a higher published level uses is still read as the square law, with a note naming
    /// what that costs, rather than refused. Same rule as <see cref="Mosfet"/>, same reason: every
    /// parameter the levels share means the same thing in all of them.
    /// </summary>
    private static ModelCardTranslation Jfet(SpiceModelCard card, string reference)
    {
        var (parameters, unmapped) = Collect(card, JfetMap);
        var notes = new List<string>
        {
            "Read as the Shichman-Hodges square law, which is the JFET law circuitRF implements. "
            + "Its gate is modelled as TWO junctions, one to each end of the channel, so a card's "
            + "CGS and CGD are bias-dependent depletion charge rather than fixed capacitors.",
        };

        // The higher published JFET levels add a doping-profile tail and their own channel-length
        // modulation. Named individually rather than summarised, because "some parameters were
        // dropped" is not something a user can act on.
        var higher = new[] { "B", "ALPHA", "VK" }
            .Where(k => Find(card, [k]).Value is not null)
            .ToList();
        if (higher.Count > 0)
            notes.Add(
                "The card states " + string.Join(", ", higher) + ", which belong to a higher "
                + "published JFET level than the square law. There is no square-law parameter that "
                + "means the same thing, so they are not carried — folding them into Lambda would "
                + "be putting one quantity where another belongs. The device is read as the square "
                + "law and will be optimistic where those terms matter.");

        // RD/RS are model parameters HERE and placed resistors on a MESFET card. Worth saying,
        // because the two cards spell them identically and the difference is invisible afterwards.
        if (parameters.Any(p => p.Name is "Rd" or "Rs"))
            notes.Add(
                "RD/RS are carried as MODEL parameters: circuitRF's JFET puts them on internal "
                + "nodes of its own, so the schematic shows one device rather than a transistor "
                + "with two resistors beside it.");

        return new ModelCardTranslation(
            card, new ModelCardBinding(reference, parameters, unmapped, notes), null);
    }

    /// <summary>
    /// A MOS card. <b>Which LEVEL circuitRF reads it as is decided from its PARAMETERS, never from
    /// its <c>LEVEL</c> number</b> — the same rule, and the same reason, as the MESFET card above:
    /// the numbering is not portable between dialects, so honouring it would make the choice depend
    /// on which simulator the file was written for, which is a fact the file does not record.
    ///
    /// <para>circuitRF implements one level, so today that decision has one answer and the card's
    /// own <c>LEVEL</c> is reported as unmapped. <b>A card stating a level circuitRF does not have
    /// is still read as the level it does</b>, with a note saying so, rather than refused: every
    /// parameter the two levels share means the same thing in both, and a device that is right at
    /// low field and optimistic at high field is a far better answer than no device at all — as long
    /// as the user is told, which is what the note is for. The short-channel parameters that only
    /// the higher levels use land in <c>Unmapped</c> and are reported individually, so what was lost
    /// is named rather than summarised.</para>
    /// </summary>
    private static ModelCardTranslation Mosfet(SpiceModelCard card, bool nChannel)
    {
        // The stated LEVEL, if any. It is READ — unlike the MESFET card's, which is not — and the
        // difference is not an inconsistency: a MESFET card's level selects between laws that
        // different dialects number differently, while a MOS card's numbering for the classical
        // levels is the one thing about it that IS portable. 1, 2 and 3 mean the same three
        // published models everywhere they appear.
        string? levelText = Find(card, ["LEVEL"]).Value;
        double level = levelText is not null
                    && double.TryParse(levelText, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out double lv)
            ? lv : 1.0;

        // Level 4 and above are the compact-model families, and their parameters are a DIFFERENT
        // vocabulary describing different quantities — a threshold is not VTO there, an oxide
        // thickness is not TOX. Almost nothing on such a card would be carried, and what came out
        // would be a device wearing this file's defaults under the card's name. That is the one
        // outcome worth refusing over.
        if (level >= 3.5)
            return new ModelCardTranslation(card, null,
                $"'{card.Name}' is a {card.ModelType.Trim().ToUpperInvariant()} card stating "
                + $"LEVEL={levelText}. circuitRF implements the classical levels 1 and 3; level 4 and "
                + "above are the compact-model families, whose parameters name different quantities "
                + "under different spellings, so almost nothing on this card could be carried and "
                + "what was built would be this transistor wearing default numbers. Run the model "
                + "through the VerilogA component instead, which takes its parameters from the model "
                + "file rather than from a card.");

        bool level3 = level >= 2.5;
        string reference = level3
            ? (nChannel ? "MOS3_N" : "MOS3_P")
            : (nChannel ? "MOS1_N" : "MOS1_P");

        var (parameters, unmapped) = Collect(card, MosfetMap);

        // Each level owns parameters the OTHER one has no home for, and the rule runs BOTH ways.
        // Carrying one onto a level that never reads it is worse than reporting it dropped: it lands
        // on the built cell as an ordinary row, looks honoured, and is found much later by wondering
        // why changing it does nothing.
        //
        // Level 1 has no home for the six short-channel parameters. Level 3 has no LAMBDA — it
        // computes the output slope from a real shortening of the channel rather than fitting it,
        // which is the whole difference between the two laws, and is why MosfetLevel3Model has no
        // such constructor parameter and why the level-3 palette tile leaves the row off as well.
        var notes = new List<string>();
        DropOntoUnmapped(parameters, unmapped, level3
            ? ["Lambda"]
            : ["Eta", "Theta", "Kappa", "Vmax", "Delta", "Xj"]);

        notes.Add(level3
            ? "Read as the level-3 semi-empirical short-channel law, which the card states."
            : levelText is null
                ? "Read as the level-1 (Shichman-Hodges) square law, which is what a MOS card that "
                + "states no LEVEL means."
                : $"Read as the level-1 (Shichman-Hodges) square law. The card states LEVEL={levelText}, "
                + "which circuitRF has no model for; every parameter the classical levels share means "
                + "the same thing in all of them, so the card is read as level 1 rather than refused. "
                + "Anything only the stated level uses is listed below as not carried.");

        // A card with no TOX has no oxide capacitance and therefore no intrinsic gate charge. That
        // is the published rule and there is nothing to guess a thickness from, but it is worth
        // saying out loud: the imported device will have almost no gate capacitance, which is the
        // kind of thing found much later by wondering where the gain went.
        if (!parameters.Any(p => p.Name == "Tox"))
            notes.Add(
                "The card states no TOX, so there is no oxide capacitance and the intrinsic gate "
                + "charge is absent — only the CGSO/CGDO/CGBO overlaps remain. Nothing is invented "
                + "to fill it: an oxide thickness cannot be derived from the rest of the card.");

        // Level 3's channel-length modulation and its short-channel charge sharing are both built
        // from the depletion width, which only NSUB supplies. Worth naming, because the two
        // parameters that drive them can be stated and still do nothing.
        if (level3 && !parameters.Any(p => p.Name == "Nsub")
                   && parameters.Any(p => p.Name is "Kappa" or "Xj"))
            notes.Add(
                "The card states KAPPA or XJ but no NSUB. Both are built from the substrate "
                + "depletion width, which nothing but the doping supplies, so channel-length "
                + "modulation and short-channel charge sharing are both absent — the parameters are "
                + "carried and are inert.");

        // The bulk is a real pin on circuitRF's MOS symbol, which is not what every user expects
        // from a three-terminal part on a schematic. Said here because the cell this becomes has
        // four pins, and a user who wires three of them gets a floating substrate.
        notes.Add(
            "circuitRF's MOS transistor has FOUR terminals — drain, gate, source and bulk. The bulk "
            + "is a real pin rather than a tie to the source, because tying it would delete the body "
            + "effect this card's GAMMA and PHI describe. Wire it.");

        return new ModelCardTranslation(
            card, new ModelCardBinding(reference, parameters, unmapped, notes), null);
    }

    /// <summary>
    /// A resistor card. <b>Refused when it states no resistance</b> — a card carrying only a sheet
    /// resistance describes a resistor per unit square, and there is no geometry here to apply it
    /// to. That is the same refusal <see cref="SpicePassiveModelBinding"/> gives on an instance, for
    /// the same reason: the alternative is a resistance of zero, which simulates.
    /// </summary>
    private static ModelCardTranslation Resistor(SpiceModelCard card)
    {
        if (Find(card, ["R"]).Value is null)
        {
            string extra = card.Parameters.ContainsKey("RSH")
                ? " It states a sheet resistance (RSH), which is a resistance per square and needs a "
                + "width and a length to become a value — neither of which a model card carries."
                : "";
            return new ModelCardTranslation(card, null,
                $"'{card.Name}' is a resistor card that states no resistance, so there is nothing to "
                + $"build a resistor from.{extra}");
        }

        var (parameters, unmapped) = Collect(card, ResistorMap);
        return new ModelCardTranslation(
            card, new ModelCardBinding("R", parameters, unmapped, []), null);
    }

    /// <summary>
    /// A capacitor or inductor card. <b>Refused when it states no value</b>, for the reason above.
    ///
    /// <para>Both are the plain circuitRF primitive, which carries a value and nothing else — so a
    /// card's temperature coefficients land in <c>Unmapped</c> and are reported. That is a real loss
    /// of fidelity and is exactly why it is reported rather than absorbed: circuitRF's resistor has
    /// TC1/TC2 and its capacitor and inductor do not.</para>
    /// </summary>
    private static ModelCardTranslation Passive(
        SpiceModelCard card, string reference, (string, string[])[] map, string quantity)
    {
        string target = map[0].Item1;
        if (Find(card, map[0].Item2).Value is null)
            return new ModelCardTranslation(card, null,
                $"'{card.Name}' is a {card.ModelType.Trim().ToUpperInvariant()} card that states no "
                + $"{quantity} ({target}=…), so there is nothing to build from it.");

        var (parameters, unmapped) = Collect(card, map);
        return new ModelCardTranslation(
            card, new ModelCardBinding(reference, parameters, unmapped, []), null);
    }

    private static ModelCardTranslation Bind(
        SpiceModelCard card, string reference, (string, string[])[] map)
    {
        var (parameters, unmapped) = Collect(card, map);
        return new ModelCardTranslation(
            card, new ModelCardBinding(reference, parameters, unmapped, []), null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Collection
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the map in order, taking the first spelling each target is actually stated under, and
    /// returns everything the map never consumed.
    /// </summary>
    private static (List<ModelCardParameter> Parameters, List<string> Unmapped) Collect(
        SpiceModelCard card, (string Target, string[] Sources)[] map)
    {
        var parameters = new List<ModelCardParameter>();
        var consumed   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (target, sources) in map)
        {
            var (source, value) = Find(card, sources);
            if (source is null || value is null) continue;
            parameters.Add(new ModelCardParameter(target, value, source));
            consumed.Add(source);
        }

        var unmapped = card.Parameters.Keys
            .Where(k => !consumed.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (parameters, unmapped);
    }

    /// <summary>
    /// Moves every parameter whose circuitRF name is in <paramref name="targets"/> out of the
    /// binding and into the unmapped list, under the CARD's own spelling — so a report names what
    /// was not carried the way the user's file spells it rather than the way this table does.
    /// </summary>
    private static void DropOntoUnmapped(
        List<ModelCardParameter> parameters, List<string> unmapped, string[] targets)
    {
        var dropped = parameters.Where(p => targets.Contains(p.Name)).ToList();
        if (dropped.Count == 0) return;

        foreach (var d in dropped) { parameters.Remove(d); unmapped.Add(d.Source); }
        unmapped.Sort(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The first of <paramref name="sources"/> the card states, with its value.</summary>
    private static (string? Source, string? Value) Find(SpiceModelCard card, string[] sources)
    {
        foreach (string s in sources)
            if (card.Parameters.TryGetValue(s, out var v) && !string.IsNullOrWhiteSpace(v))
                // The card's OWN spelling, not the one searched for: the two differ in case on
                // nearly every real file, and a report that echoes the search term is telling the
                // user about this table rather than about their file.
                return (card.Parameters.Keys.FirstOrDefault(
                            k => k.Equals(s, StringComparison.OrdinalIgnoreCase)) ?? s,
                        v);
        return (null, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Refusals
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Why a type cannot be built. <b>Each names the type and says what is missing</b>, because
    /// "unsupported" sends a user to look for a setting that does not exist. Where a nearby device
    /// exists but is not the same law, the refusal says that too — it is the question the user is
    /// about to ask.
    /// </summary>
    private static string RefusalFor(SpiceModelCard card, string type) => type switch
    {
        // circuitRF HAS an IGBT — and this card still cannot be imported into it, which is a
        // different refusal from "no model exists" and has to say so. The card's parameters belong
        // to the published ambipolar-transport model and describe the SILICON (base width, doping,
        // carrier lifetime, mobility); circuitRF's is an equivalent-circuit model parameterised by
        // what a data sheet gives (threshold, transconductance, current gain, transit time). There
        // is no mapping between the two sets — deriving one from the other is a device-modelling
        // extraction, not a rename — so carrying the card's numbers across would produce a
        // transistor that simulates and bears no relation to the part.
        "NIGBT" or "PIGBT" =>
            $"'{card.Name}' is a {type} card (an insulated-gate bipolar transistor). circuitRF has "
            + "an IGBT, but not this one: its parameters are the ambipolar transport model's, which "
            + "describe the silicon (base width, doping, carrier lifetime), and circuitRF's IGBT is "
            + "an equivalent-circuit model parameterised by what a data sheet gives (Vto, Kp, the "
            + "bipolar gain Bf, and the transit time Tau that sets the current tail). Neither set "
            + "can be derived from the other by renaming, so nothing on this card is carried across. "
            + "Place the IGBT component and enter the data sheet's numbers, or run the card's own "
            + "model through the VerilogA component, which takes its parameters from the model file "
            + "rather than from a card.",

        "SW" or "CSW" or "VSWITCH" or "ISWITCH" =>
            $"'{card.Name}' is a {type} card (a controlled switch). circuitRF's Switch component is "
            + "an ideal RF block set by its own parameters, not a card-driven behavioural switch.",

        "" =>
            $"'{card.Name}' names no model type, so there is nothing to identify it by.",

        _ =>
            $"'{card.Name}' is a '{card.ModelType.Trim()}' card. circuitRF has no built-in device of "
            + "that type. A compiled model can still be run through the VerilogA component, which "
            + "takes its parameters from the model file itself rather than from a card.",
    };
}
