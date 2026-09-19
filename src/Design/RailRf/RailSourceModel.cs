// The source as an element over frequency, and the sentence it is not allowed to leave out
// (docs/sonnet-briefs/brief-railrf-11-part-models.md R-rail11-9; railrf.md §2.2 "The sources", Q-5).
//
// ── Q-5 CLOSED THE SOURCE AS "ASSUME A BATTERY" ────────────────────────────────────────────────
//
// The tool is aimed at battery-powered designs and converter data frequently cannot be had — a
// supplier who fears reverse engineering does not publish an output-impedance curve. So:
//
//   * R-L IS THE V1 SOURCE MODEL, with R SWEPT ACROSS CELL LIFE — ohms to hundreds of ohms.
//   * A published output-impedance curve is accepted AS A TOUCHSTONE FILE LIKE ANY OTHER PART,
//     where one exists.
//   * Where the source is a converter modelled as R-L, RAILRF STATES THAT THE ANSWER NEAR THE LOOP
//     CROSSOVER IS OPTIMISTIC rather than quietly producing a monotonic curve that misses the peak.
//
// ── THE LAST CLAUSE IS THE SAME SHAPE AS THE "INDICATIVE" MARKING ──────────────────────────────
//
// It is A SENTENCE ON THE RESULT, NOT A LOG LINE: the honest statement has to TRAVEL WITH THE
// NUMBER, exactly as R-rail11-4's marking does. A series R-L is monotonic — |Z| rises with f and
// never peaks — whereas a real regulator's output impedance peaks at its loop crossover, typically
// somewhere in the tens of kilohertz, and that peak is often the largest single contribution to the
// rail's impedance in the band the bulk capacitors are there to cover. An R-L model does not
// approximate that peak badly; IT DOES NOT CONTAIN IT AT ALL, and the resulting curve looks
// entirely ordinary.
//
// NUMBERS ARE BASE SI. Ohms, henries, volts, hertz.

using System.Numerics;

namespace CircuitRF.Design.RailRf;

/// <summary>Which of Q-5's two forms a source is modelled in.</summary>
public enum RailSourceBasis
{
    /// <summary>A series R-L, typed. <b>The v1 model</b>, and the one whose answer near a
    /// converter's loop crossover is optimistic.</summary>
    Rl,

    /// <summary>The source's own published output-impedance curve, supplied as a Touchstone file —
    /// <b>a part like any other</b>, read through the same arithmetic
    /// (<see cref="RailMeasuredPart"/>).</summary>
    Measured,
}

/// <summary>
/// One branch feeding a rail, as an impedance over frequency.
/// </summary>
/// <param name="Index">Its index in the rail's own source list, so a result row matches a document
/// row.</param>
/// <param name="Name">The anchor's own spelling — <c>BT1.1</c>.</param>
/// <param name="Basis">R-L, or the source's own measured curve.</param>
/// <param name="SeriesResistanceOhms">R of the R-L form, or null.</param>
/// <param name="SeriesInductanceHenries">L of the R-L form, or null.</param>
/// <param name="OpenCircuitVoltageV">The level it holds the rail at, or null where it states none —
/// an impedance the frequency answer can use and the DC answer cannot.</param>
/// <param name="Measured">The published curve, where one was supplied.</param>
public sealed record RailSourceModel(
    int Index,
    string Name,
    RailSourceBasis Basis,
    double? SeriesResistanceOhms,
    double? SeriesInductanceHenries,
    double? OpenCircuitVoltageV,
    RailMeasuredPart? Measured = null)
{
    /// <summary>
    /// This branch's impedance at one frequency, in OHMS. <c>R + jωL</c> for the R-L form; the
    /// file's own value for a measured one, and <b>NaN outside the file's band</b> rather than
    /// clamped, on <see cref="RfCore.Data.PassiveMetrics"/>'s rule.
    /// </summary>
    public Complex ImpedanceAt(double frequencyHz)
    {
        if (Basis == RailSourceBasis.Measured)
        {
            if (Measured is not { } m || m.FrequenciesHz.Length == 0)
                return new Complex(double.NaN, double.NaN);
            if (frequencyHz < m.FrequenciesHz[0] || frequencyHz > m.FrequenciesHz[^1])
                return new Complex(double.NaN, double.NaN);

            for (int i = 1; i < m.FrequenciesHz.Length; i++)
            {
                if (frequencyHz > m.FrequenciesHz[i]) continue;
                double f0 = m.FrequenciesHz[i - 1], f1 = m.FrequenciesHz[i];
                if (f1 <= f0) return m.Impedance[i];
                double t = f0 > 0
                    ? (Math.Log(frequencyHz) - Math.Log(f0)) / (Math.Log(f1) - Math.Log(f0))
                    : (frequencyHz - f0) / (f1 - f0);
                return m.Impedance[i - 1] + t * (m.Impedance[i] - m.Impedance[i - 1]);
            }
            return m.Impedance[^1];
        }

        return new Complex(
            SeriesResistanceOhms ?? 0.0,
            2.0 * Math.PI * frequencyHz * (SeriesInductanceHenries ?? 0.0));
    }

    /// <summary>
    /// <b>R-rail11-9.</b> The statement that has to travel with every number this source produced,
    /// or null where there is nothing to state.
    ///
    /// <para>An R-L is a battery's honest model and a converter's optimistic one, and <b>nothing in
    /// the document says which this is</b> — the model has no converter flag and should not grow
    /// one, because the thing that makes a source a regulator is that its refdes is also a load on
    /// another rail (§2.2), which is a fact about the rail SET rather than about this row. So the
    /// statement is conditional on the caller: pass <paramref name="isConverter"/> true where
    /// <see cref="RailOrder"/> has identified this source as a regulator's output.</para>
    /// </summary>
    public string? OptimisticLine(bool isConverter) =>
        Basis != RailSourceBasis.Rl || !isConverter
            ? null
            : $"Source {Name} is a converter modelled as a series R-L, so the answer NEAR ITS LOOP " +
              "CROSSOVER is optimistic. A regulator's output impedance peaks there — typically in " +
              "the tens of kilohertz — and a series R-L does not approximate that peak, it does not " +
              "contain it: |Z| rises monotonically and the curve looks entirely ordinary. Supply " +
              "the converter's published output-impedance curve as a Touchstone file to replace this.";

    /// <summary>The row as a report prints it.</summary>
    public string Describe() => Basis == RailSourceBasis.Measured
        ? $"{Name}: its own measured output impedance, {System.IO.Path.GetFileName(Measured?.FilePath ?? "")}."
        : $"{Name}: R-L, {SeriesResistanceOhms ?? 0:0.###} Ω + " +
          $"{(SeriesInductanceHenries ?? 0) * 1e9:0.###} nH.";
}

/// <summary>
/// Q-5's cell-life sweep: <b>one source model per series resistance</b>.
/// </summary>
public static class RailSourceLife
{
    /// <summary>
    /// The default ladder, in OHMS — Q-5's own range, "ohms to hundreds of ohms".
    ///
    /// <para><b>Half-decade log spacing, because the answer moves by decades.</b> A cell's internal
    /// resistance rises by more than two orders of magnitude across its life, and a linear ladder
    /// over that range spends almost every point at the dead end of it — the same reason §2.2's
    /// frequency band is log spaced. Six points is what a plot can carry as six visibly distinct
    /// curves.</para>
    /// </summary>
    public static IReadOnlyList<double> DefaultOhms { get; } = [1.0, 3.0, 10.0, 30.0, 100.0, 300.0];

    /// <summary>
    /// R-rail11-9. One model per resistance, everything else held.
    ///
    /// <para>A MEASURED source is returned unchanged as a single point: a published output-impedance
    /// curve is one measurement of one cell in one state, and sweeping a life resistance around it
    /// would be inventing data — and, worse, would look like the sweep the R-L form genuinely
    /// supports.</para>
    /// </summary>
    public static IReadOnlyList<RailSourceModel> Sweep(
        RailSourceModel source, IReadOnlyList<double>? ohms = null)
    {
        if (source.Basis == RailSourceBasis.Measured) return [source];
        return [.. (ohms ?? DefaultOhms).Select(r => source with { SeriesResistanceOhms = r })];
    }

    /// <summary>
    /// The source model one document row describes (Q-5).
    ///
    /// <para>A row carrying a Touchstone reference is <see cref="RailSourceBasis.Measured"/> and the
    /// caller supplies the read sweep; a row carrying the R-L is
    /// <see cref="RailSourceBasis.Rl"/>. <see cref="RailSource.Refusal"/> already refuses a row that
    /// states both, so there is no preference to express here — which is the point: <i>a silently
    /// preferred one of two stated models</i> is the failure that refusal exists to prevent.</para>
    /// </summary>
    public static RailSourceModel Of(RailSource source, int index, RailMeasuredPart? measured = null) =>
        new(index,
            source.Anchor.Describe(),
            source.TouchstoneRef is { Length: > 0 } ? RailSourceBasis.Measured : RailSourceBasis.Rl,
            source.SeriesResistanceOhms,
            source.SeriesInductanceHenries,
            source.OpenCircuitVoltageV,
            measured);
}
