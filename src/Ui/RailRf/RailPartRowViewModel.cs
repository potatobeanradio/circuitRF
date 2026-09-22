using System;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One row of the parts table (§2.3 step 3, R-rail7-9).
/// </summary>
/// <remarks>
/// <b>Anything railRF could not resolve is listed AS unresolved rather than defaulted</b> — the
/// brief's own sentence, and the whole reason this row carries an <see cref="IsUnresolved"/> flag
/// instead of simply leaving a cell blank. A blank cell and a resolved zero look alike at a glance,
/// and §9's finding is that partial population must be visible.
///
/// <para><b>Three of its columns carry PROVENANCE rather than data</b>, and all three exist because
/// §9 says their absence is silent:</para>
/// <list type="bullet">
/// <item><see cref="ModelSourceText"/> — library row / attached file, and WHICH WON (R-rail2-11).</item>
/// <item><see cref="DeratedText"/> beside <see cref="MarkedText"/>, and which was used (Q-12).</item>
/// <item><see cref="EsrBasisText"/> — measured / stated / class default, with a class default marked
///   <b>indicative</b> (Q-15) — beside <see cref="EsrText"/>, which is the OHMS.</item>
/// </list>
///
/// <para><b>The electrical columns are the SOLVE's own numbers, not the library row's</b>
/// (R-rail11-6: <i>"the individual models are what the parts table's rows read"</i>). The row is
/// handed the <see cref="RailPartModel"/> <c>RailPartResolver</c> produced, so the capacitance is
/// the one after Q-12's bias correction, the ESR is evaluated at the part's own mounted resonance,
/// and the inductance is the one that sets that resonance. Until 2026-09-19 this row read the
/// LIBRARY ROW instead and could therefore only name a provenance — the ESR column said
/// <i>stated</i> beside a library that states 32 mΩ, and the derated column said <i>unresolved</i>
/// for every part in a document with bias curves (owner, 2026-09-19).</para>
///
/// <para><b>Read-only, deliberately.</b> A part's model is the part library's (a model is entered once
/// and used twenty-two times, which is why it is a file of its own); a row that could be edited here
/// would be a second place the same number lives.</para>
/// </remarks>
public sealed class RailPartRowViewModel
{
    /// <summary>What an unresolvable cell reads. One spelling, in one place, so the table cannot say
    /// it three ways.</summary>
    public const string UnresolvedText = "unresolved";

    /// <param name="part">The RAIL's own row — <b>the subject</b> (R-rail18-5a). The table lists the
    /// selected rail's parts; the BOM, the placement and the library each fill a column and none of
    /// them decides whether a row exists.</param>
    /// <param name="bom">The BOM row, or null where the BOM names no such reference.</param>
    /// <param name="model">What the part library resolved, or null where there is no library.</param>
    /// <param name="mountingInductanceHenries">The mounting loop — the document's own where it
    /// states one, or the computed one — or null where neither exists.</param>
    /// <param name="position">Where the placement put it, already formatted, or null.</param>
    /// <param name="resolved">What <c>RailPartResolver</c> worked this part out to be — the element
    /// the sweep carries, with every number's provenance beside it (R-rail11-6). Null where the row
    /// was built with no library at all.</param>
    /// <param name="boardFootprint">The land-pattern cell this part is PLACED on, where the artwork
    /// states one — R-rail27-3b. Read only where the BOM and the library are both silent.</param>
    public RailPartRowViewModel(
        RailPart part,
        BomRow? bom,
        PartModelResolution? model,
        double? mountingInductanceHenries,
        string? position,
        RailPartModel? resolved = null,
        string? boardFootprint = null,
        RailPartPositionSource positionFrom = RailPartPositionSource.Nothing)
    {
        ArgumentNullException.ThrowIfNull(part);

        _part = part;
        _bom = bom;
        _model = model;
        _resolved = resolved;
        _boardFootprint = boardFootprint;
        Refdes = part.Refdes;
        MountingInductanceHenries = mountingInductanceHenries;
        Position = position;
        PositionFrom = positionFrom;
    }

    private readonly RailPart _part;
    private readonly BomRow? _bom;
    private readonly PartModelResolution? _model;
    private readonly RailPartModel? _resolved;
    private readonly string? _boardFootprint;

    /// <summary>The resolved model, or null where it did not resolve — so every numeric column below
    /// is <see cref="UnresolvedText"/> in one place rather than thirteen.</summary>
    private RailPartModel? Model => _resolved is { IsResolved: true } m ? m : null;

    /// <summary>The reference.</summary>
    public string Refdes { get; }

    /// <summary>
    /// The internal part number, or <see cref="UnresolvedText"/>.
    /// </summary>
    /// <remarks>
    /// <b>The rail's own first, the BOM's where the rail states none</b> (R-rail18-5a). A refdes the
    /// rail names and the BOM does not is a row with an unresolved part number, which is the state
    /// this word already exists to say — not a missing row.
    /// </remarks>
    public string PartNumber =>
        _part.PartNumber is { Length: > 0 } own ? own
        : _bom?.PartNumber is { Length: > 0 } p ? p
        : UnresolvedText;

    /// <summary>Where this row came from — typed, or pre-filled from the bill of materials. Shown on
    /// the row, because a pre-filled number nobody checked is the one that will be wrong.</summary>
    public RailPartOrigin Origin => _part.Origin;

    /// <summary>The same, as the row reads it.</summary>
    public string OriginText => _part.Origin == RailPartOrigin.Bom
        ? "pre-filled from the bill of materials"
        : "typed on the rail";

    /// <summary>
    /// The marked value — <b>as the BOM states it, and the library's own marked capacitance where
    /// the BOM states none</b>.
    /// </summary>
    /// <remarks>
    /// The BOM's spelling wins because it is the board's own statement of what is fitted, and it is
    /// the column R-rail18-5a gives the BOM. The fallback is what makes this column useful in the
    /// P1 case §6 makes ordinary: the <c>Power Rail</c> example has no BOM at all, so every one of
    /// its thirteen rows read <i>unresolved</i> beside a library that states 100 nF.
    /// </remarks>
    public string MarkedText =>
        _bom?.Value is { Length: > 0 } v ? v
        : Model?.Capacitance.MarkedFarads is { } marked && double.IsFinite(marked)
            ? Farads(marked)
        : UnresolvedText;

    /// <summary>
    /// The derated value, where a bias curve produced one — <b>beside the marked one, never instead
    /// of it</b> (Q-12). Where no curve exists this reads <see cref="UnresolvedText"/>, and the count
    /// of such parts is a headline number on the status strip.
    /// </summary>
    /// <remarks>
    /// <b>The solve's own number</b> (R-rail11-6). This was a settable <c>init</c> property nothing
    /// ever set, so the column read <i>unresolved</i> for every part of every document — including
    /// the shipped example, whose four library rows each carry a five-point bias curve.
    /// </remarks>
    public string DeratedText =>
        Model?.Capacitance.DeratedFarads is { } derated ? Farads(derated) : UnresolvedText;

    /// <summary>Which of the two <see cref="MarkedText"/>/<see cref="DeratedText"/> the solve used.
    /// Stated, because a table showing both and saying neither is worse than showing one.</summary>
    public string ValueUsedText => Model?.Capacitance.Basis switch
    {
        RailCapacitanceBasis.Derated  => "derated",
        RailCapacitanceBasis.Measured => "measured",
        _                             => "marked",
    };

    /// <summary>
    /// The capacitance column: <b>marked and derated in ONE cell, with an arrow between them</b> —
    /// "100 nF → 74 nF".
    /// </summary>
    /// <remarks>
    /// <b>Both, which is Q-12's requirement</b> (<i>beside the marked one, never instead of it</i>)
    /// — in one cell because the parts pane is the board column and there is not room for two
    /// number columns plus the two this change adds. Where no bias curve exists the arrow and the
    /// second number are absent, which is the honest rendering of "nothing derated this": a table
    /// that printed <c>100 nF → 100 nF</c> would say a curve had been applied.
    /// </remarks>
    public string CapacitanceText
    {
        get
        {
            // ── brief 25, R-rail25-4a: A SERIES 1 Ω AND A SHUNT 1 Ω DO OPPOSITE THINGS ────────
            //
            // So this column does not print a capacitance for a series element — it has none, and
            // a blank would read as a part railRF failed on. It prints the element's own model
            // over frequency, which is the quantity in the same place on the row.
            if (_part.IsSeries) return SeriesImpedanceText;

            if (Model is not { } m) return MarkedText;
            if (m.Capacitance.Basis == RailCapacitanceBasis.Measured) return Farads(m.Capacitance.UsedFarads);

            string marked = MarkedText;
            return m.Capacitance.DeratedFarads is { } derated
                ? $"{marked} → {Farads(derated)}"
                : marked;
        }
    }

    /// <summary>Q-12's triple, spelled out — what the row's capacitance column means, on its own
    /// tooltip. <c>RailDeratedCapacitance.Describe</c> owns the sentence.</summary>
    public string CapacitanceTooltip =>
        Model?.Capacitance.Describe()
        ?? "No capacitance resolved for this part, so nothing here is derated and nothing is "
         + "defaulted.";

    // ══ THE FOOTPRINT COLUMN (brief-footprint-4 R-fp4-3) ══════════════════════════════════════
    //
    // BomFile has recognised a footprint column since brief-railrf-2 and PartLibraryRow.Footprint
    // has existed beside it; neither fed anything. They feed this — and this REPORTS, it never
    // assigns (R-fp4-3d). A BOM column that silently set artwork would be the same class of error
    // as the imperial/metric ambiguity it is trying to avoid.

    /// <summary>The footprint token this part carries: the bill of materials' own column first,
    /// then what the description parse recognised, then the part library's. Null where none of the
    /// three states one.</summary>
    /// <summary>
    /// What the footprint column is ABOUT — the BOM's, then the library's, then <b>the board's own
    /// land pattern</b> (R-rail27-3b).
    /// </summary>
    /// <remarks>
    /// <b>The artwork is LAST and only speaks where the other two are silent.</b> The BOM is the
    /// board's own statement of what is fitted and stays the statement of record; the library row is
    /// what the part number resolves to. The artwork is circuitRF's own reading of the placement, and
    /// it is here because a board with neither of the first two — a Gerber set with hand-placed
    /// footprints, which is the whole scenario brief 27 is measured against — showed nothing at all
    /// in the one column that lets a designer group the rows they are about to assign a part number
    /// to. Nothing is derived from it (R-rail26-3 stands: an 0402 land is a case size, not a
    /// capacitance) and no document field is added — the artwork is still there on the next open.
    /// </remarks>
    private string? FootprintToken =>
        _bom?.Footprint is { Length: > 0 } f ? f
        : _bom?.Parsed.CaseCode is { Length: > 0 } c ? c
        : (Model?.Row ?? _model?.Row)?.Footprint is { Length: > 0 } lib ? lib
        : _boardFootprint is { Length: > 0 } art ? art
        : null;

    private FootprintTokenMatch? _footprintMatch;
    private FootprintTokenMatch Footprint => _footprintMatch ??= FootprintTokens.Match(FootprintToken);

    /// <summary>
    /// What the footprint column reads: the matched case code, the token itself where nothing
    /// matched, or the token marked ambiguous.
    /// </summary>
    /// <remarks>
    /// <b>An unmatched token is shown AS WRITTEN and is never turned into the nearest code</b>
    /// (R-fp4-3b). The whole point of the column is that it tells you something; a fuzzy match tells
    /// you what the matcher believed. <b>A bare four-digit token that names a real case in both
    /// schemes is marked ambiguous rather than read</b> (R-fp4-3c) — <c>0201</c> imperial and
    /// <c>0201</c> metric differ by 2.4x, and the tooltip names both readings.
    /// </remarks>
    public string FootprintText => FootprintToken is null
        ? UnresolvedText
        : Footprint.Outcome switch
        {
            FootprintTokenOutcome.Matched   => Footprint.Case!.Code,
            FootprintTokenOutcome.Ambiguous => $"{Footprint.Token} — ambiguous",
            _                               => $"{Footprint.Token} — unmatched",
        };

    /// <summary>Both readings for an ambiguous token, the full §1e spelling for a matched one, and
    /// the case list for an unmatched one — <c>FootprintTokens</c> owns every sentence.</summary>
    public string FootprintTooltip => FootprintToken is null
        ? "Neither the bill of materials nor the part library states a footprint for this part, and "
          + "the board places no land pattern for it."
        : Footprint.Report
          + (IsFootprintFromTheBoard
                ? "\n\nRead off the board: this part is placed on the land-pattern cell of this name. "
                + "Neither the bill of materials nor the part library states a footprint for it."
                : "");

    /// <summary>True where the column is reading the ARTWORK rather than a stated footprint —
    /// R-rail27-3b. The tooltip says so, because a token nobody typed is worth marking.</summary>
    public bool IsFootprintFromTheBoard =>
        _bom?.Footprint is not { Length: > 0 }
        && _bom?.Parsed.CaseCode is not { Length: > 0 }
        && (Model?.Row ?? _model?.Row)?.Footprint is not { Length: > 0 }
        && _boardFootprint is { Length: > 0 };

    /// <summary>
    /// Library row / attached file, and which won.
    /// </summary>
    public string ModelSourceText => _model switch
    {
        null                                                 => UnresolvedText,
        { Row: null }                                        => UnresolvedText,
        { Source: PartModelSource.AttachedFile, FilePath: { } f }
            => $"file — {System.IO.Path.GetFileName(f)}",
        _                                                    => "library row",
    };

    /// <summary>
    /// <b>The ESR in OHMS</b>, at this part's own mounted resonance.
    /// </summary>
    /// <remarks>
    /// <b>The number, because the basis alone is not an answer</b> (owner, 2026-09-19: the column
    /// said <i>stated</i> beside a library row stating 32 mΩ). The basis is not lost: it is on
    /// <see cref="EsrTooltip"/>, and an <see cref="IsEsrIndicative">indicative</see> one is drawn
    /// italic, which is Q-15's marking with no column spent on it.
    ///
    /// <para><b>At the resonance, and that is <c>RailPartModel.EsrOhms</c>' own rule</b>: a
    /// class-default ESR is <c>DF/(2π·f·C)</c> and therefore has no single value, so the frequency
    /// it is quoted at has to be the one where it matters — where it sets the depth of the minimum
    /// and the height of the anti-resonance the part takes part in.</para>
    /// </remarks>
    public string EsrText =>
        // A series element's number in this place is its DCR — the resistance the LOAD CURRENT
        // runs through, which is what it costs the rail, and the analogue of the loss term a
        // capacitor's ESR is. Unstated is unstated, never zero (R-rail25-3b).
        _part.IsSeries
            ? (_part.DcResistanceOhms is { } dcr
                  ? RailValueFormat.FormatWithUnit(dcr, RailQuantity.Resistance, 3)
                  : UnstatedText)
        : Model?.EsrOhms is { } r && double.IsFinite(r)
            ? RailValueFormat.FormatWithUnit(r, RailQuantity.Resistance, 3)
            : UnresolvedText;

    /// <summary>
    /// Measured / stated / class default — with a class default marked <b>indicative</b>.
    /// </summary>
    /// <remarks>
    /// The class default is the NORMAL case rather than the degraded one (Q-15), and the word matters
    /// because a mask margin in dB computed from an indicative peak looks exactly as authoritative as
    /// one computed from a measurement.
    /// </remarks>
    public string EsrBasisText => (Model?.EsrBasis ?? _model?.EsrBasis) switch
    {
        EsrProvenance.Measured     => "measured",
        EsrProvenance.Stated       => "stated",
        EsrProvenance.ClassDefault => "class default — " + RailEsrDefaults.Marking,
        _                          => UnresolvedText,
    };

    /// <summary>The sentence behind those two columns — where the number came from, and at what
    /// frequency it was evaluated.</summary>
    public string EsrTooltip => Model switch
    {
        null => "No ESR: this part did not resolve, so nothing here is defaulted.",
        { EsrBasis: EsrProvenance.Measured, Measured: { } file } =>
            $"Re Z read from {System.IO.Path.GetFileName(file.FilePath)} at this part's own "
            + "resonance. The only route to a real ESR.",
        { EsrBasis: EsrProvenance.Stated } m =>
            $"The part library states {RailValueFormat.FormatWithUnit(m.StatedEsrOhms ?? 0, RailQuantity.Resistance, 3)}, "
            + "at every frequency — that is all the row says.",
        { EsrBasis: EsrProvenance.ClassDefault } m =>
            $"No ESR is stated and no file is attached, so railRF used the {m.DissipationFactor:0.###} "
            + $"dissipation factor for a {m.DielectricClass} dielectric: ESR = DF/(2π·f·C), "
            + "evaluated at this part's own resonance. A quoted dissipation factor is a MAXIMUM for a "
            + "class of part, so every peak height and every mask margin computed from it is "
            + RailEsrDefaults.Marking + ". A part's own Touchstone file is the only route to a real ESR.",
        _ => "No ESR: no attached file, no stated value and no recognised dielectric class. Nothing "
           + "here is defaulted — this part contributes no loss.",
    };

    /// <summary>True where the ESR is a class default, so the row can be marked in the table.</summary>
    public bool IsEsrIndicative => (Model?.EsrBasis ?? _model?.EsrBasis) == EsrProvenance.ClassDefault;

    /// <summary>
    /// The part's OWN series inductance — the package, not the mounting loop.
    /// </summary>
    /// <remarks>
    /// <b>The other half of the branch.</b> The table showed only the mounting loop, and a reader
    /// looking at <c>L mount</c> alone cannot tell what sets the resonance: it is the SUM, and on a
    /// bulk part the package is the larger term. Derived from C and f₀ by R-rail2-8's arithmetic,
    /// which is exact rather than a fit.
    /// </remarks>
    public string PartInductanceText =>
        RailValueFormat.FormatWithUnit(Model?.InductanceHenries, RailQuantity.Inductance, UnresolvedText, 3);

    /// <summary>Where that inductance came from, and what the row's two L columns add up to.</summary>
    public string PartInductanceTooltip => Model switch
    {
        null => "No inductance: this part did not resolve.",
        { InductanceHenries: null } => "The library row carries neither a self-resonant frequency to "
            + "derive an inductance from nor a stated one, so railRF has none for this part.",
        { } m =>
            (m.InductanceBasis switch
            {
                RailInductanceBasis.DerivedFromResonance =>
                    "L = 1/((2·π·f₀)²·C) from the row's own capacitance and self-resonant frequency.",
                RailInductanceBasis.Measured => "Read from the part's own file.",
                _ => "Stated on the library row — the row carries no self-resonant frequency to "
                   + "derive one from.",
            })
            + (m.TotalInductanceHenries is { } total
                ? $" The branch carries {RailValueFormat.FormatWithUnit(total, RailQuantity.Inductance, 3)} "
                  + "in all, package plus mounting loop, and that is the L that sets the resonance."
                : ""),
    };

    /// <summary>The sentence behind the L column — where each term came from, and what they add
    /// up to.</summary>
    public string InductanceTooltip => $"{PartInductanceTooltip} {MountingInductanceTooltip}";

    /// <summary>
    /// Where this part stops being a capacitor — <c>1/(2π·√(L_total·C))</c> over the numbers this
    /// row actually carries.
    /// </summary>
    /// <remarks>
    /// <b>The MOUNTED resonance, not the library row's f₀</b>, and <c>RailPartModel</c>'s own note
    /// says why: the row's figure is the unmounted part at its marked capacitance, while this one
    /// includes the mounting loop and Q-12's bias correction, both of which move it — derating
    /// RAISES it by √(marked/derated). <see cref="SelfResonanceTooltip"/> prints the row's own
    /// figure beside it when the two differ.
    /// </remarks>
    public string SelfResonanceText =>
        RailValueFormat.FormatWithUnit(Model?.SelfResonanceHz, RailQuantity.Frequency, UnresolvedText, 3);

    /// <summary>The stated f₀ beside the mounted one, because the difference is the point.</summary>
    public string SelfResonanceTooltip => Model switch
    {
        null => "No resonance: this part did not resolve.",
        { SelfResonanceHz: null } => "railRF has no capacitance, no inductance, or neither, so there "
            + "is no resonance to compute.",
        { } m =>
            "Where this part stops being a capacitor: 1/(2π·√(L·C)) over the capacitance and the "
            + "inductance this row carries — the MOUNTED part, so the mounting loop and the bias "
            + "derating are both in it."
            + (m.StatedSelfResonanceHz is { } stated
                ? $" The library row states {RailValueFormat.FormatWithUnit(stated, RailQuantity.Frequency, 3)} "
                  + "for the part on its own."
                : ""),
    };

    /// <summary>The whole row as one sentence, with every provenance said out loud — the row's own
    /// tooltip. <c>RailPartModel.Describe</c> owns it; nothing here writes a second version.</summary>
    public string RowTooltip => _part.IsSeries
        ? SeriesTooltip
        : _resolved?.Describe() ?? OriginText;

    /// <summary>What the library row says about this part that is not electrical — description,
    /// footprint, dielectric class and voltage rating, for the part-number column's tooltip.</summary>
    public string PartNumberTooltip
    {
        get
        {
            if ((Model?.Row ?? _model?.Row) is not { } row)
                return PartNumber == UnresolvedText
                    ? "No part number: neither this rail's row nor the bill of materials names one."
                    : $"'{PartNumber}' is not in the part library, so railRF has no C, no f₀ and no "
                    + "dielectric class for it. Nothing about it is defaulted.";

            var parts = new System.Collections.Generic.List<string>(4);
            if (row.Description is { Length: > 0 } d) parts.Add(d);
            if (row.Footprint is { Length: > 0 } f) parts.Add($"footprint {f}");
            if (row.DielectricClass is { Length: > 0 } c) parts.Add(c);
            if (row.VoltageRatingV is { } v)
                parts.Add($"rated {RailValueFormat.FormatWithUnit(v, RailQuantity.Voltage, 3)}");
            parts.Add(row.BiasCurve.Count > 0
                ? $"{row.BiasCurve.Count}-point bias curve"
                : "no bias curve");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The inductance column: <b>the package and the mounting loop as two terms of one sum</b> —
    /// "0.5 + 0.85 nH".
    /// </summary>
    /// <remarks>
    /// <b>The sum is what sets the resonance, and the split is what a reader changes.</b> §2.2 on
    /// the mounting loop: <i>"it is the thing your form factor change actually altered"</i>, so
    /// collapsing the two into one total would hide the term being tuned — and showing the mounting
    /// loop alone, which is what this column did, leaves a reader unable to tell what the branch
    /// actually carries. Both terms are printed in the TOTAL's own unit, so they add up on the face
    /// of the row rather than needing two rungs reconciled.
    /// </remarks>
    public string InductanceText
    {
        get
        {
            if (Model is not { } m) return MountingInductanceText;

            double? part = m.InductanceHenries, mount = m.MountingInductanceHenries;
            if (m.TotalInductanceHenries is not { } total) return UnresolvedText;

            string unit = RailValueFormat.AutoUnitFor(total, RailQuantity.Inductance);
            double scale = RailValueFormat.Scale(unit);
            string Term(double h) => RailValueFormat.Significant(h / scale, 3);

            return part is { } p && mount is { } t ? $"{Term(p)} + {Term(t)} {unit}"
                 : $"{Term(total)} {unit}";
        }
    }

    /// <summary>The capacitance ladder, through the one formatter this window uses.</summary>
    private static string Farads(double farads) =>
        double.IsFinite(farads)
            ? RailValueFormat.FormatWithUnit(farads, RailQuantity.Capacitance, 3)
            : UnresolvedText;

    /// <summary>The mounting inductance this row carries, base SI, or null.</summary>
    public double? MountingInductanceHenries { get; }

    /// <summary>The same, as the column reads it.</summary>
    public string MountingInductanceText =>
        RailValueFormat.FormatWithUnit(MountingInductanceHenries, RailQuantity.Inductance, UnresolvedText, 3);

    /// <summary>Typed on the rail, or computed from the via geometry — §2.2's own precedence,
    /// because a computed number silently replacing a typed one is the same defect as a defaulted
    /// plating thickness reported as a stated one. Printed after
    /// <see cref="PartInductanceTooltip"/> on the one L column's tooltip.</summary>
    public string MountingInductanceTooltip => _resolved?.MountingBasis switch
    {
        RailMountingBasis.Typed => "The loop from the pad through its via to the plane pair and back, "
            + "as this rail's own row types it. Typically 0.3–1.5 nH, and it dominates above roughly "
            + "50 MHz.",
        RailMountingBasis.ComputedFromGeometry => "Computed from this part's actual via positions and "
            + "the plane separation. Nothing typed one, so railRF worked it out — type one on the "
            + "rail to override it.",
        _ => "Nothing states a mounting loop for this part and there is no artwork to compute one "
           + "from, so the branch carries the package inductance alone.",
    };

    /// <summary>Where the placement put it, or null where it did not land.</summary>
    public string? Position { get; }

    /// <summary>
    /// The same, as the column reads it — <b>"not placed", never "unresolved"</b>.
    /// </summary>
    /// <remarks>
    /// The two are different states and only one of them is a problem (owner, 2026-09-19, who asked
    /// the reasonable question: the Parts table lists C1…C13 and the board shows no capacitor
    /// anywhere). <i>Unresolved</i> means railRF could not model this part. <i>Not placed</i> means
    /// nothing has said where it is — no placement file names its refdes — so it is a shunt branch on
    /// the rail with its own stated mounting inductance, which is a perfectly ordinary way to ask this
    /// question and is what the shipped example does. Printing the alarm word for the ordinary case is
    /// what made a user go looking for a defect that was not there.
    /// </remarks>
    public string PositionText => Position is { Length: > 0 } p ? p : NotPlacedText;

    /// <summary>What an unplaced part's location column says.</summary>
    public const string NotPlacedText = "not placed";

    /// <summary>
    /// Which of the two things that can say where a part is, said it.
    /// </summary>
    /// <remarks>
    /// <b>Carried because the two are not the same number</b> — see
    /// <c>PlacedPins.OriginsOf</c> for the argument. The column shows one coordinate either way;
    /// the TOOLTIP is where the reader finds out which, and a coordinate whose source is invisible
    /// is the defaulted-number failure in another column.
    /// </remarks>
    public RailPartPositionSource PositionFrom { get; }

    /// <summary>The sentence behind that column, for the row's own tooltip.</summary>
    public string PositionTooltip => PositionFrom switch
    {
        RailPartPositionSource.PlacementFile =>
            "Where the PLACEMENT FILE puts this part — the manufacturing centroid, under the origin "
          + "convention the import stated. The board panel's own coordinates read in the same unit.",

        // Owner report, 2026-09-22: this case read "not placed" and the tooltip blamed the absence
        // of a placement file, while railRF was naming the part's land pattern one column to the
        // left off the very instance it claimed not to know about.
        RailPartPositionSource.Artwork =>
            "Where the ARTWORK places this part — the origin of the land-pattern instance carrying "
          + "this reference designator in the layout. That is not quite a placement file's number: a "
          + "placement file states the manufacturing centroid under a declared origin convention, "
          + "and a placement file's row would be shown here instead if one named this part.",

        _ =>
            "Nothing says where this part is: no placement file names this refdes and no instance in "
          + "the artwork carries it, so railRF has no coordinate for it and draws nothing for it on "
          + "the board. It is still in the answer — a shunt branch on this rail, with the mounting "
          + "inductance this row states. Place it in the layout, or load a placement file.",
    };

    /// <summary>
    /// True when this row could not be resolved at all — no part number, or no library row for it.
    /// </summary>
    /// <remarks>
    /// <b>The window renders such a row as unresolved and populates NO numeric column for it</b>
    /// (R-rail7-9's own test). A half-filled row is the shape a defaulted one takes, and the whole
    /// point of listing it is that it is not one.
    /// </remarks>
    /// <remarks>
    /// <b>And it is NOT <see cref="IsUnmounted"/></b> (R-rail23-1d). An unmounted row resolves
    /// perfectly well and is deliberately absent; an unresolved one is a data problem. Two states,
    /// two spellings, and the table must not collapse them — a row can be either, both or neither.
    /// </remarks>
    /// <remarks>
    /// <b>A SERIES element is never this</b> (brief 25). Its model is its own row's R-L or its own
    /// measured file, not the library's capacitor arithmetic, so a ferrite the library has no row
    /// for is completely resolved — dimming it would report a data problem the document does not
    /// have.
    /// </remarks>
    public bool IsUnresolved =>
        !_part.IsSeries &&
        (PartNumber == UnresolvedText || _model is null || _model.Row is null);

    /// <summary>What the dielectric class was parsed as, or empty — shown beside the description it
    /// came from, for correction (brief 2's R-rail2-5).</summary>
    public string DielectricClassText => _bom?.Parsed.DielectricClass ?? "";

    // ══ IN THE RAIL, NOT ACROSS IT (brief 25) ════════════════════════════════════════════════

    /// <summary>What an UNSTATED number reads — and it is not <see cref="UnresolvedText"/>.</summary>
    /// <remarks>
    /// Two states, two spellings, on the rule R-rail23-1d already set for mounted-versus-unresolved.
    /// <i>Unresolved</i> is a data problem: railRF looked and could not work the number out.
    /// <i>Unstated</i> is the document saying nothing, which is honest and is the reason the drop
    /// answer reports a LOWER BOUND rather than a total (R-rail25-3b).
    /// </remarks>
    public const string UnstatedText = "unstated";

    /// <summary>
    /// True where this row is the element the rail runs THROUGH rather than something hung off it
    /// (R-rail25-4a).
    /// </summary>
    /// <remarks>
    /// <b>The table must mark it</b>: a series 1 Ω and a shunt 1 Ω do opposite things to a rail,
    /// and a table that spells them the same is a table that will be misread. It is marked three
    /// ways — the row carries the <c>series</c> style, the capacitance column reads the element's
    /// impedance instead of a capacitance it does not have, and the ESR column reads its DCR.
    /// </remarks>
    public bool IsSeries => _part.IsSeries;

    /// <summary>The element's model over frequency, as the capacitance column reads it on a series
    /// row — its own measured file, or the R-L.</summary>
    public string SeriesImpedanceText
    {
        get
        {
            if (_part.TouchstoneRef is { Length: > 0 } file)
                return $"file — {System.IO.Path.GetFileName(file)}";

            if (!_part.IsRl) return UnstatedText;

            string r = RailValueFormat.FormatWithUnit(
                _part.SeriesResistanceOhms ?? 0, RailQuantity.Resistance, 3);
            string l = RailValueFormat.FormatWithUnit(
                _part.SeriesInductanceHenries ?? 0, RailQuantity.Inductance, 3);
            return $"{r} + {l}";
        }
    }

    /// <summary>The sentence behind a series row — what it is, what it costs, and the one caveat a
    /// lumped R-L standing in for a ferrite cannot leave out (R-rail25-1c).</summary>
    public string SeriesTooltip =>
        RailSeriesModel.Of(_part) is { } m
            ? m.Describe() + " " +
              (m.BiasDependentLine ?? "Its impedance is its own measured curve, so nothing here is " +
                                      "a lumped stand-in.") +
              " Everything upstream of it sees one impedance and everything downstream sees another."
            : RowTooltip;

    /// <summary>Which side of the rail's series element this row is on, where the ROW states it —
    /// read only on a rail with no artwork (R-rail25-2d).</summary>
    public string SideText => _part.Side == RailSection.Upstream ? "upstream" : "downstream";

    // ══ MOUNTED, AND IT IS NOT "UNRESOLVED" (brief 23) ════════════════════════════════════════

    /// <summary>
    /// Whether this part is FITTED — the row's own checkbox (R-rail23-2a).
    /// </summary>
    /// <remarks>
    /// <b>Read-only here on purpose.</b> Every other column of this row is read-only because the
    /// number belongs to the part library and a second place to type it would be a second place it
    /// could be wrong; this one is not a number at all, it is a statement about the board, so the
    /// checkbox writes it — through <c>RailRfViewModel.SetPartsMounted</c>, which is what makes an
    /// unmount an ordinary committed edit and re-solves once for however many rows moved.
    /// </remarks>
    public bool IsMounted => _part.Mounted;

    /// <summary>True where this row is in the table but not in the answer — what greys it.</summary>
    public bool IsUnmounted => !_part.Mounted;

    /// <summary>
    /// The sentence behind the checkbox. <b>It says what SURVIVES</b>, because that is the whole
    /// difference between unmounting a part and deleting it.
    /// </summary>
    public string MountTooltip =>
        _part.IsSeries && _part.Mounted ? SeriesMountTooltip
        : _part.Mounted
        ? "Fitted. Clear this to depopulate it: the row stays, with its part number, its position "
        + "and its computed mounting loop, and the rail re-solves without it. The layout is not "
        + "touched — this says what is fitted to the board, not what the board is."
        : "NOT FITTED. This part is in the table and not in the answer: its part number, its "
        + "position and its mounting loop are all kept, so ticking this again gives the answer it "
        + "gave before. This is a different state from UNRESOLVED — unresolved is a data "
        + "problem, and this is a design question.";

    /// <summary>
    /// <b>R-rail25-3c.</b> Clearing a SERIES element's checkbox is not a depopulated board — it
    /// OPENS the rail, and the run refuses with that reason rather than answering for half a rail
    /// fed by nothing. Said on the checkbox so the refusal is not the first anyone hears of it.
    /// </summary>
    public string SeriesMountTooltip =>
        "Fitted. This element is IN the rail and carries the whole load current, so clearing it "
        + "does not take a branch off the rail — it OPENS the rail, and everything downstream is "
        + "then fed by nothing. railRF refuses that with the reason rather than answering for an "
        + "open circuit. Delete the row to ask about a board that never had it.";
}
