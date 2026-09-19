// Every anti-resonance within a stated fraction of an aggressor line
// (docs/design/railrf.md §2.2 "The band, and the aggressors", §2.4;
//  docs/sonnet-briefs/brief-railrf-12-impedance.md R-rail12-4).
//
// ── THIS IS THE SENTENCE THE TOOL EXISTS TO PRODUCE ────────────────────────────────────────────
//
// §2.4: "A short list: every anti-resonance within a stated fraction of an aggressor line, worst
// first. THIS IS THE SENTENCE THE TOOL EXISTS TO PRODUCE — your bulk-to-ceramic anti-resonance at
// 7.1 MHz is the converter's own fundamental at its top setting."
//
// So the output is NOT a boolean and NOT a count. It is a row per coincidence, naming the
// anti-resonance, naming the aggressor and its harmonic number, and giving the separation. The
// reason that shape matters is §2.2's own: "a 9 dB peak nothing excites is not a problem and a
// 3 dB peak sitting on the converter's fifth harmonic is." A tool that reported only the peak
// heights would rank those two exactly backwards.
//
// ── NO DOMAIN TYPES ────────────────────────────────────────────────────────────────────────────
//
// src/Engine cannot see src/Design (overview §1a), so an aggressor here is a name, a fundamental
// and a harmonic count — never a RailAggressor. Where the row came from (typed, or recognised from
// the BOM) stays on the document side, because it changes what a user should check and not what
// the arithmetic does.

namespace CircuitRF.Engine.Pdn;

/// <summary>
/// One thing on the board that excites the PDN, reduced to what the check needs.
/// </summary>
/// <param name="Name">What it is, as the row reads — <c>converter</c>.</param>
/// <param name="FundamentalHz">Its fundamental. Base SI.</param>
/// <param name="Harmonics">How many harmonics to check. 1 is the fundamental alone.</param>
public readonly record struct PdnAggressorLine(string Name, double FundamentalHz, int Harmonics);

/// <summary>
/// One anti-resonance sitting on one aggressor line.
/// </summary>
/// <param name="Peak">The anti-resonance, with its own attribution and margin.</param>
/// <param name="AggressorName">Which aggressor.</param>
/// <param name="Harmonic">Which harmonic of it — 1 is the fundamental.</param>
/// <param name="AggressorHz">That harmonic's frequency.</param>
/// <param name="SeparationFraction">How far apart they are, as a fraction of the aggressor's own
/// frequency. <b>A fraction rather than a difference</b>, because 200 kHz is a miss at 32 kHz and
/// a direct hit at 50 MHz.</param>
public sealed record PdnCoincidenceRow(
    PdnAntiResonancePeak Peak,
    string AggressorName,
    int Harmonic,
    double AggressorHz,
    double SeparationFraction)
{
    /// <summary>True where a class-default ESR is behind this peak's height (§9).</summary>
    public bool Indicative => Peak.Indicative;

    /// <summary>
    /// The sentence §2.4 quotes, with the aggressor named and the harmonic said out loud.
    /// </summary>
    public string Describe() =>
        $"{Peak.Contributors} at {PdnMask.Hertz(Peak.FrequencyHz)}, {PdnMask.Ohms(Peak.PeakOhms)}" +
        (Peak.MarginDb is { } m && m < 0 ? $" ({-m:0.#} dB over its mask)" : "") +
        (Indicative ? " (indicative)" : "") +
        $" — {Harmonic switch { 1 => $"the {AggressorName}'s own fundamental",
                               2 => $"the {AggressorName}'s second harmonic",
                               3 => $"the {AggressorName}'s third harmonic",
                               _ => $"the {AggressorName}'s {Harmonic}th harmonic" }} " +
        $"at {PdnMask.Hertz(AggressorHz)}, {SeparationFraction:P1} away.";
}

/// <summary>§2.4's coincidence check.</summary>
public static class PdnCoincidence
{
    /// <summary>
    /// How close an anti-resonance has to sit to an aggressor line to be reported — as a FRACTION
    /// of the line's own frequency.
    ///
    /// <para><b>10 %, and the reasoning is the capacitor tolerance rather than a round number.</b>
    /// A part's resonance moves as <c>1/√C</c>, so the ±20 % tolerance an ordinary ceramic is
    /// bought to moves f₀ by about ∓10 % on its own — before the bias derating of Q-12, before the
    /// mounting loop varies with placement, and before the converter's own switching frequency
    /// moves with load and temperature. A peak drawn 8 % away from a line on THIS model therefore
    /// lands on it for some fraction of the parts actually fitted, which is precisely the failure
    /// the check exists to catch. A tighter window would report only the coincidences that had
    /// already happened.</para>
    /// </summary>
    public const double DefaultFraction = 0.10;

    /// <summary>
    /// Every peak within <paramref name="fraction"/> of every harmonic of every aggressor,
    /// <b>worst first</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>Worst is the MASK MARGIN where there is one, and the peak height where there is
    /// not</b> — the separation only breaks ties. A near-miss on a peak that violates its target by
    /// 6 dB is a bigger problem than a direct hit on one that passes by 10, and ordering by how
    /// close the two frequencies happen to be would put them the other way round.</para>
    ///
    /// <para>A peak may appear on more than one row, and that is not a duplicate: a peak sitting on
    /// both a converter harmonic and a crystal fundamental is excited by both, and collapsing the
    /// two would drop the name of one of them — which is the whole content of the row.</para>
    /// </remarks>
    /// <param name="peaks">The anti-resonance table for one port.</param>
    /// <param name="aggressors">The rail's declared excitation set.</param>
    /// <param name="fraction">The window, as a fraction. See <see cref="DefaultFraction"/>.</param>
    public static IReadOnlyList<PdnCoincidenceRow> Find(
        IEnumerable<PdnAntiResonancePeak> peaks,
        IEnumerable<PdnAggressorLine> aggressors,
        double fraction = DefaultFraction)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        ArgumentNullException.ThrowIfNull(aggressors);

        var list = peaks.ToList();
        var rows = new List<PdnCoincidenceRow>();

        foreach (var a in aggressors)
        {
            if (!(a.FundamentalHz > 0) || a.Harmonics < 1) continue;

            for (int h = 1; h <= a.Harmonics; h++)
            {
                double line = a.FundamentalHz * h;

                foreach (var peak in list)
                {
                    double separation = Math.Abs(peak.FrequencyHz - line) / line;
                    if (separation > fraction) continue;
                    rows.Add(new PdnCoincidenceRow(peak, a.Name, h, line, separation));
                }
            }
        }

        rows.Sort(static (p, q) =>
        {
            // A violated mask outranks an unjudged peak of any height: the first is a stated target
            // that is not met and the second is a number with nothing to compare it against.
            int byMargin = Rank(p).CompareTo(Rank(q));
            if (byMargin != 0) return byMargin;

            int byPeak = q.Peak.PeakOhms.CompareTo(p.Peak.PeakOhms);
            if (byPeak != 0) return byPeak;

            int bySeparation = p.SeparationFraction.CompareTo(q.SeparationFraction);
            return bySeparation != 0
                ? bySeparation
                : string.CompareOrdinal(p.AggressorName, q.AggressorName);
        });

        return rows;

        static double Rank(PdnCoincidenceRow r) => r.Peak.MarginDb ?? double.PositiveInfinity;
    }
}
