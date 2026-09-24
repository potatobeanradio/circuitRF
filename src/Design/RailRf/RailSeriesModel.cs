// The element the rail runs THROUGH, as an impedance over frequency — and the sentence a ferrite is
// not allowed to leave out (docs/sonnet-briefs/brief-railrf-25-series-element.md R-rail25-1b,
// R-rail25-1c).
//
// ── THIS FILE INTRODUCES NO SECOND IMPEDANCE TYPE, AND THAT IS THE REQUIREMENT ─────────────────
//
// R-rail25-1b: "A ferrite is R(f) + jωL(f), a resistor is R, and a published curve is a Touchstone
// file. RailSourceModel already models exactly this." So a series element's model IS a
// RailSourceModel — RailSourceBasis.Rl or Measured, ImpedanceAt returning a Complex, NaN outside a
// measured file's band rather than clamped — reached through the same RailMeasuredPart arithmetic
// the parts and the sources already use. What this record adds is the two things a SOURCE has no
// use for: the DC resistance the load current runs through, and the honest sentence below.
//
// ── THE SENTENCE IS THE SAME SHAPE AS RailSourceModel.OptimisticLine ───────────────────────────
//
// R-rail25-1c. A ferrite is the part in the whole document a lumped R-L most misrepresents: its
// impedance is strongly bias-dependent and its datasheet curve is measured at ZERO DC BIAS. At
// 350 mA the same part is optimistic by a large factor and the curve looks entirely ordinary —
// which is precisely the failure RailSourceBasis' own header describes for a converter near loop
// crossover, so it gets precisely the same treatment: A SENTENCE ON THE RESULT, NOT A LOG LINE.
// Where a measured curve is supplied the sentence goes away, because then it is a measurement.
//
// ── AND SINCE BRIEF 35, THE PART LIBRARY CAN DESCRIBE ONE ──────────────────────────────────────
//
// R-rail35-2. The same bead on four rails was four hand-typed rows: the library held a capacitor's
// model and nothing read a row classed Other as anything. Now each FIELD resolves on its own — the
// rail row's stated value, then an Other library row's — and the model says which won, because the
// parts table's model-source column has to (R-rail2-11's rule). The fields are exactly two: the DC
// resistance (the library row's ESR) and the impedance over frequency (the row's R-L or file, else
// the library row's Touchstone, read SERIES-thru). There is no third route and no guessed split of
// an impedance-at-one-frequency into R and L (R-rail35-2c).
//
// NUMBERS ARE BASE SI. Ohms, henries, hertz.

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Design.RailRf;

/// <summary>Where one of a series element's numbers came from (brief 35, R-rail35-2b).</summary>
public enum RailSeriesValueSource
{
    /// <summary>Nothing states it. <b>Not zero</b> — see the model's own sentences.</summary>
    Unstated,

    /// <summary>The rail's own part row — it wins wherever it states the field.</summary>
    Row,

    /// <summary>The part library's row for this part number, classed
    /// <see cref="PartLibraryRow.OtherClass"/> — inherited where the rail row states nothing.</summary>
    Library,
}

/// <summary>
/// One series element, resolved: what it is over frequency, what it costs at DC, and what has to be
/// said beside every number it produced.
/// </summary>
/// <param name="Row">The document's own part row — the subject, and what a refusal names.</param>
/// <param name="Impedance">Its impedance over frequency, in <see cref="RailSourceModel"/>'s shape
/// (R-rail25-1b). <see cref="RailSourceModel.Index"/> is 0 and <see cref="RailSourceModel.Name"/> is
/// the refdes: this is not a source and nothing reads it as one.</param>
public sealed record RailSeriesModel(RailPart Row, RailSourceModel Impedance)
{
    /// <summary>The element, as every report and every refusal names it.</summary>
    public string Refdes => Row.Refdes is { Length: > 0 } r ? r : "(unnamed series element)";

    /// <summary>
    /// The DC resistance the load current runs through, in OHMS. The row's own where it states one,
    /// else an <c>Other</c> library row's ESR (R-rail35-2a), else <b>0 Ω</b>;
    /// <see cref="DcResistanceFrom"/> says which.
    /// </summary>
    /// <remarks>
    /// <b>Nothing entered is 0 Ω</b> (owner, 2026-09-23). It was UNSTATED, stamped as zero anyway and
    /// raised as a "lower bound" finding (R-rail25-3b), which on a board of 0 Ω links and RF chokes
    /// put a finding on every rail for parts whose resistance really is negligible. The assumption is
    /// still said — as a note, by <see cref="AssumedDcResistanceLine"/> — so it is visible, not silent.
    /// </remarks>
    public double? DcResistanceOhms { get; init; } = Row.DcResistanceOhms ?? 0.0;

    /// <summary>Which of the row and the library stated <see cref="DcResistanceOhms"/>.</summary>
    public RailSeriesValueSource DcResistanceFrom { get; init; } =
        Row.DcResistanceOhms is null ? RailSeriesValueSource.Unstated : RailSeriesValueSource.Row;

    /// <summary>Which of the row and the library stated the impedance over frequency.</summary>
    public RailSeriesValueSource ImpedanceFrom { get; init; } =
        Row.TouchstoneRef is { Length: > 0 } || Row.IsRl
            ? RailSeriesValueSource.Row : RailSeriesValueSource.Unstated;

    /// <summary>The file the impedance was read from — the row's own reference, or the library
    /// row's resolved one — or null where it is an R-L.</summary>
    public string? TouchstonePath { get; init; } =
        Row.TouchstoneRef is { Length: > 0 } t ? t : null;

    /// <summary>The <c>Other</c> library row this element inherited from, or null.</summary>
    public PartLibraryRow? LibraryRow { get; init; }

    /// <summary>True where this element's impedance came out of a measured file.</summary>
    public bool IsMeasured => Impedance.Basis == RailSourceBasis.Measured;

    /// <summary>True where a file was named and could not be read — which is NOT a 0 Ω link, and
    /// the sweep refuses it rather than stamping one.</summary>
    public bool IsUnreadable => IsMeasured && Impedance.Measured is null;

    /// <summary>
    /// The parts table's model-source column for this row (R-rail35-1c): where the impedance came
    /// from and where the DCR came from, each named — the row, the library, or nothing.
    /// </summary>
    public string SourceText =>
        $"Z {Word(ImpedanceFrom, IsMeasured)} · DCR {Word(DcResistanceFrom, false)}";

    private static string Word(RailSeriesValueSource source, bool file) => source switch
    {
        RailSeriesValueSource.Row     => file ? "row file" : "row",
        RailSeriesValueSource.Library => file ? "library file" : "library",
        _                             => "none (0 Ω)",
    };

    /// <summary>
    /// What the sweep says where nothing states an impedance at all, or null. The element is
    /// stamped as a 0 Ω LINK — brief 25's reading of an R-L with nothing in it — and the answer
    /// SAYS so, because a link and a bead nobody described look identical on a curve.
    /// </summary>
    public string? UnstatedImpedanceLine => ImpedanceFrom != RailSeriesValueSource.Unstated
        ? null
        : $"Series element {Refdes} states no impedance — no R-L and no Touchstone file on its row, " +
          "and no part-library row classed Other with a file — so it is modelled as a 0 Ω LINK. " +
          "State its R-L on the row, or give its library row the part's own measured curve.";

    /// <summary>This element's impedance at one frequency, in OHMS.</summary>
    public Complex ImpedanceAt(double frequencyHz) => Impedance.ImpedanceAt(frequencyHz);

    /// <summary>
    /// <b>R-rail25-1c.</b> The statement that travels with every number this element produced, or
    /// null where there is nothing to state.
    /// </summary>
    /// <remarks>
    /// <b>Only for an R-L.</b> A supplied curve is a measurement of the part and needs no caveat —
    /// the sentence goes away, exactly as <see cref="RailSourceModel.OptimisticLine"/>'s does. An
    /// element with no impedance stated at all is not an R-L either: it is stamped as a link, and
    /// <see cref="UnstatedImpedanceLine"/> is the sentence it carries instead (brief 35). It is
    /// not conditional on the part being a FERRITE, because nothing in the document says which of a
    /// ferrite, a sense resistor and a FET this row is, and a flag for it would be a second place
    /// the same fact lives: the caveat is about a lumped R-L standing in for a part whose impedance
    /// depends on its bias, and the reader is the one who knows whether theirs does.
    /// </remarks>
    public string? BiasDependentLine => IsMeasured || ImpedanceFrom == RailSeriesValueSource.Unstated
        ? null
        : $"Series element {Refdes} is modelled as a lumped R-L. A FERRITE BEAD'S IMPEDANCE IS " +
          "STRONGLY BIAS-DEPENDENT and its datasheet curve is measured at ZERO DC bias: at a few " +
          "hundred milliamps the same part is optimistic by a large factor, and the curve it " +
          "produces looks entirely ordinary. Supply the part's own measured impedance as a " +
          "Touchstone file to replace this. A sense resistor or a link has no such dependence and " +
          "nothing here says which of them this is.";

    /// <summary>
    /// What the DC answer notes where neither the row nor the part library states this element's DC
    /// resistance and it was taken as 0 Ω, or null where one is stated.
    /// </summary>
    public string? AssumedDcResistanceLine => DcResistanceFrom != RailSeriesValueSource.Unstated
        ? null
        : $"Series element {Refdes} has no DC resistance on its row or in the part library, so it " +
          "is taken as 0 Ω. It carries the load current, so enter its datasheet DCR if it is not " +
          "negligible.";

    /// <summary>The row as a report prints it.</summary>
    public string Describe()
    {
        string z = IsMeasured
            ? $"its own measured impedance, {System.IO.Path.GetFileName(TouchstonePath ?? "")}" +
              (ImpedanceFrom == RailSeriesValueSource.Library ? " (from the part library)" : "")
            : $"R-L, {Row.SeriesResistanceOhms ?? 0:0.###} Ω + " +
              $"{(Row.SeriesInductanceHenries ?? 0) * 1e9:0.###} nH";

        string dcr = DcResistanceOhms is { } r
            ? $"DCR {r * 1e3:0.###} mΩ" +
              (DcResistanceFrom == RailSeriesValueSource.Library ? " (the part library's ESR)" : "")
            : "DCR 0 mΩ (none entered)";

        return $"{Refdes}: IN SERIES with the rail; {z}; {dcr}.";
    }

    /// <summary>
    /// The model one document row describes, or null where the row is not a series element.
    /// </summary>
    /// <param name="part">The document's own row.</param>
    /// <param name="measured">The file's sweep where <see cref="RailPart.TouchstoneRef"/> named one
    /// and it could be read — <b>through the resolver's own reader</b>, so a series element's file
    /// and a part's file go through one piece of Touchstone arithmetic rather than two.</param>
    public static RailSeriesModel? Of(RailPart part, RailMeasuredPart? measured = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Connection != RailPartConnection.Series) return null;

        return new RailSeriesModel(
            part,
            new RailSourceModel(
                0,
                part.Refdes is { Length: > 0 } r ? r : "(unnamed series element)",
                part.TouchstoneRef is { Length: > 0 } ? RailSourceBasis.Measured : RailSourceBasis.Rl,
                part.SeriesResistanceOhms,
                part.SeriesInductanceHenries,
                OpenCircuitVoltageV: null,
                measured));
    }

    /// <summary>
    /// <b>Brief 35.</b> The model one row describes, each field resolved on its own — the rail row's
    /// stated value, then the part library's row where that row is classed
    /// <see cref="PartLibraryRow.OtherClass"/> (R-rail35-2a, R-rail35-2b). Null where the row is not
    /// a series element.
    /// </summary>
    /// <remarks>
    /// <b>The one function the window's sweep, its parts table and the DC run all call</b>, so the
    /// number a row prints, the impedance stamped between two sections and the DCR in the breakdown
    /// cannot come to disagree about which source won. A library row that is a CAPACITOR is not read
    /// here at all: its ESR is a loss term at resonance, not a resistance the load current runs
    /// through, and a part marked series with a capacitor's part number keeps what its own row states.
    /// </remarks>
    /// <param name="part">The document's own row.</param>
    /// <param name="library">The part library, or null where there is none.</param>
    /// <param name="read">Reads one Touchstone file SERIES-thru, or returns null where it cannot —
    /// <see cref="RailPartResolver.ReadMeasured(string, PassiveExtraction, out string?)"/>. Null
    /// reads nothing, for a caller (the DC run) that only needs the DCR.</param>
    /// <param name="rowReference">Resolves the row's own <see cref="RailPart.TouchstoneRef"/>, which
    /// is relative to the <c>.crail</c>. Null takes it as written.</param>
    public static RailSeriesModel? Resolve(
        RailPart part, PartLibrary? library,
        Func<string, RailMeasuredPart?>? read = null,
        Func<string, string>? rowReference = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Connection != RailPartConnection.Series) return null;

        var libraryRow = part.PartNumber is { Length: > 0 } pn && library?.Part(pn) is { IsCapacitor: false } row
            ? row : null;

        // ── the DC resistance: the row's, then the library row's ESR ──────────────────────────
        var (dcr, dcrFrom) =
            part.DcResistanceOhms is { } own ? (own, RailSeriesValueSource.Row)
            : libraryRow?.EsrOhms is { } esr ? (esr, RailSeriesValueSource.Library)
            : ((double?)0.0, RailSeriesValueSource.Unstated);

        // ── the impedance over frequency: the row's R-L or file, then the library row's file ──
        string name = part.Refdes is { Length: > 0 } r ? r : "(unnamed series element)";
        RailSourceModel impedance;
        RailSeriesValueSource zFrom;
        string? path = null;

        if (part.TouchstoneRef is { Length: > 0 } own2)
        {
            path = rowReference?.Invoke(own2) ?? own2;
            impedance = new RailSourceModel(0, name, RailSourceBasis.Measured, null, null, null, read?.Invoke(path));
            zFrom = RailSeriesValueSource.Row;
        }
        else if (part.IsRl)
        {
            impedance = new RailSourceModel(0, name, RailSourceBasis.Rl,
                part.SeriesResistanceOhms, part.SeriesInductanceHenries, null);
            zFrom = RailSeriesValueSource.Row;
        }
        else if (libraryRow?.ModelRef is { Length: > 0 } model && PartLibrary.IsTouchstone(model))
        {
            path = library!.ResolveModel(libraryRow.PartNumber).FilePath ?? model;
            impedance = new RailSourceModel(0, name, RailSourceBasis.Measured, null, null, null, read?.Invoke(path));
            zFrom = RailSeriesValueSource.Library;
        }
        else
        {
            impedance = new RailSourceModel(0, name, RailSourceBasis.Rl, null, null, null);
            zFrom = RailSeriesValueSource.Unstated;
        }

        return new RailSeriesModel(part, impedance)
        {
            DcResistanceOhms = dcr,
            DcResistanceFrom = dcrFrom,
            ImpedanceFrom    = zFrom,
            TouchstonePath   = path,
            LibraryRow       = libraryRow,
        };
    }
}
