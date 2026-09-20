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
// NUMBERS ARE BASE SI. Ohms, henries, hertz.

using System.Numerics;

namespace CircuitRF.Design.RailRf;

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
    /// The DC resistance the load current runs through, in OHMS — <b>null is UNSTATED, never
    /// zero</b> (R-rail25-3b).
    /// </summary>
    public double? DcResistanceOhms => Row.DcResistanceOhms;

    /// <summary>True where this element's impedance came out of its own measured file.</summary>
    public bool IsMeasured => Impedance.Basis == RailSourceBasis.Measured;

    /// <summary>This element's impedance at one frequency, in OHMS.</summary>
    public Complex ImpedanceAt(double frequencyHz) => Impedance.ImpedanceAt(frequencyHz);

    /// <summary>
    /// <b>R-rail25-1c.</b> The statement that travels with every number this element produced, or
    /// null where there is nothing to state.
    /// </summary>
    /// <remarks>
    /// <b>Only for an R-L.</b> A supplied curve is a measurement of the part and needs no caveat —
    /// the sentence goes away, exactly as <see cref="RailSourceModel.OptimisticLine"/>'s does. It is
    /// not conditional on the part being a FERRITE, because nothing in the document says which of a
    /// ferrite, a sense resistor and a FET this row is, and a flag for it would be a second place
    /// the same fact lives: the caveat is about a lumped R-L standing in for a part whose impedance
    /// depends on its bias, and the reader is the one who knows whether theirs does.
    /// </remarks>
    public string? BiasDependentLine => IsMeasured
        ? null
        : $"Series element {Refdes} is modelled as a lumped R-L. A FERRITE BEAD'S IMPEDANCE IS " +
          "STRONGLY BIAS-DEPENDENT and its datasheet curve is measured at ZERO DC bias: at a few " +
          "hundred milliamps the same part is optimistic by a large factor, and the curve it " +
          "produces looks entirely ordinary. Supply the part's own measured impedance as a " +
          "Touchstone file to replace this. A sense resistor or a link has no such dependence and " +
          "nothing here says which of them this is.";

    /// <summary>
    /// <b>R-rail25-3b.</b> What the DC answer says where this element states no DC resistance, or
    /// null where it states one.
    /// </summary>
    public string? UnstatedDcResistanceLine => DcResistanceOhms is not null
        ? null
        : $"Series element {Refdes} states NO DC resistance, so this rail's DC total is a LOWER " +
          "BOUND rather than the drop. It is not zero — it is unstated, and a series element " +
          "carries the whole load current, so its DCR is the largest term after the source on a " +
          "typical rail. State it to get the drop.";

    /// <summary>The row as a report prints it.</summary>
    public string Describe()
    {
        string z = IsMeasured
            ? $"its own measured impedance, {System.IO.Path.GetFileName(Row.TouchstoneRef ?? "")}"
            : $"R-L, {Row.SeriesResistanceOhms ?? 0:0.###} Ω + " +
              $"{(Row.SeriesInductanceHenries ?? 0) * 1e9:0.###} nH";

        string dcr = DcResistanceOhms is { } r
            ? $"DCR {r * 1e3:0.###} mΩ"
            : "DCR unstated — the DC total is a lower bound";

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
}
