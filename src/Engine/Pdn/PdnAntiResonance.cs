// Peak finding, and NAMING THE TWO CONTRIBUTORS
// (docs/design/railrf.md §2.4 "The anti-resonance table", §2.5;
//  docs/sonnet-briefs/brief-railrf-12-impedance.md R-rail12-5).
//
// ── THE LABEL IS THE OUTPUT; THE PEAK ON ITS OWN IS NOT ────────────────────────────────────────
//
// §2.4: "Frequency, peak |Z|, margin against the mask, and THE TWO CONTRIBUTORS, NAMED:
// 'L(mount, C3-C9 bank) against C(bulk bank)'. That label is the actionable output; the peak on
// its own is not." A user who is told there is a 9 dB peak at 7.1 MHz still has to work out which
// two things are ringing against each other before they can change anything — and that is the
// step this file exists to take.
//
// ── ATTRIBUTION, NOT PROXIMITY ─────────────────────────────────────────────────────────────────
//
// An anti-resonance is a PARALLEL resonance between the inductive branch of one group of parts and
// the capacitive branch of another. So the method is measurement, not pattern matching: at the peak
// frequency, rank every branch by its contribution to the port admittance, group the inductive
// contributors and the capacitive contributors, and name the top group on each side by the part
// range it spans. A branch that is NEITHER is not named.
//
// DO NOT ATTRIBUTE BY PROXIMITY, BY VALUE OR BY PART TYPE. §2.5 states the same rule for the A/B
// matcher and it holds here: railRF does not pair things by guessing. The two failures that rule
// forbids are both plausible and both wrong — the nearest part to the peak in frequency is often a
// bystander, and "the big one against the small ones" is exactly backwards on a board whose bulk
// bank is paralleled enough to have moved.
//
// ── NO DOMAIN TYPES ────────────────────────────────────────────────────────────────────────────
//
// src/Engine cannot see src/Design (overview §1a): a branch here is a group key, a member name and
// a complex admittance. What makes two parts one bank is the caller's, because it is the caller
// that holds the parts.

using System.Numerics;

namespace CircuitRF.Engine.Pdn;

/// <summary>
/// One branch's contribution at one frequency.
/// </summary>
/// <param name="Group">What makes this branch one row with others — the caller's own bank key.
/// Branches sharing it are summed before either side is ranked.</param>
/// <param name="Member">The instance — <c>C7</c>. What the group's own label is built from.</param>
/// <param name="Admittance">Its admittance at the peak frequency, in siemens. <b>An inductive
/// branch has a NEGATIVE imaginary part</b> — <c>Y = 1/(R + jX)</c>, so <c>Im Y</c> carries the
/// opposite sign to the reactance — which is the one sign in this file worth stating rather than
/// rediscovering.</param>
public readonly record struct PdnBranchAdmittance(string Group, string Member, Complex Admittance);

/// <summary>
/// One side of an anti-resonance: the bank that is ringing, and how much of that side it is.
/// </summary>
/// <param name="Group">The caller's bank key.</param>
/// <param name="Members">Its instances, in the order they were supplied.</param>
/// <param name="Admittance">The group's own summed admittance at the peak.</param>
/// <param name="Share">Its fraction of the susceptance on ITS side of the peak, 0…1. <b>Reported
/// because a top group at 0.34 is a different finding from one at 0.95</b>: the first says three
/// banks are involved and changing one will barely move the peak.</param>
public sealed record PdnContributorGroup(
    string Group,
    IReadOnlyList<string> Members,
    Complex Admittance,
    double Share)
{
    /// <summary>
    /// What the label calls this bank — <b>the part range it spans</b>, which is §2.4's own
    /// spelling (<c>C3–C9 bank</c>). One member reads as itself; a group with no members at all
    /// falls back to its key.
    /// </summary>
    public string Label => Members.Count switch
    {
        0 => Group,
        1 => Members[0],
        _ => $"{Members[0]}–{Members[^1]}",
    };
}

/// <summary>
/// One peak of |Z|, with the two banks that made it.
/// </summary>
/// <param name="Index">Which sample of the sweep it sits on.</param>
/// <param name="FrequencyHz">Where it is.</param>
/// <param name="PeakOhms">|Z| there.</param>
/// <param name="Inductive">The bank supplying the inductive side, or null where nothing did.</param>
/// <param name="Capacitive">The bank supplying the capacitive side, or null.</param>
/// <param name="LimitOhms">What the mask allowed there, or null where it said nothing.</param>
/// <param name="MarginDb">The margin against that limit, or null.</param>
/// <param name="Indicative">True where a class-default ESR is behind this peak's height (§9) —
/// <b>on the row</b>, per R-rail12-7, because a peak height and its margin are exactly the numbers
/// that read as authoritative whether or not they are.</param>
public sealed record PdnAntiResonancePeak(
    int Index,
    double FrequencyHz,
    double PeakOhms,
    PdnContributorGroup? Inductive,
    PdnContributorGroup? Capacitive,
    double? LimitOhms,
    double? MarginDb,
    bool Indicative)
{
    /// <summary>
    /// §2.4's own label — <c>L(C3–C9) against C(C1)</c> — or the honest refusal to name where one
    /// side had no contributor. <b>Never a half-attribution</b>: "L(C3–C9) against nothing" would
    /// read as a finding about C3–C9 when it is a finding about this method's limits.
    /// </summary>
    public string Contributors =>
        Inductive is { } l && Capacitive is { } c
            ? $"L({l.Label}) against C({c.Label})"
            : "not attributed — no two branches on opposite sides of this peak";

    /// <summary>True where both sides were named, which is the case §2.4 calls actionable.</summary>
    public bool IsAttributed => Inductive is not null && Capacitive is not null;

    /// <summary>The sentence the anti-resonance table's own row reads.</summary>
    public string Describe() =>
        $"{PdnMask.Hertz(FrequencyHz)}: {PdnMask.Ohms(PeakOhms)}" +
        (MarginDb is { } m
            ? m < 0 ? $", {-m:0.#} dB over its mask" : $", {m:0.#} dB under its mask"
            : ", no mask here") +
        (Indicative ? " (indicative)" : "") +
        $" — {Contributors}.";
}

/// <summary>§2.4's anti-resonance table, as arithmetic over a solved sweep.</summary>
public static class PdnAntiResonance
{
    /// <summary>
    /// How far above its neighbouring minima a local maximum has to stand to be an anti-resonance
    /// rather than a wobble, in DECIBELS.
    ///
    /// <para><b>1 dB, and it is a prominence rather than a height.</b> Every sampled curve has
    /// local maxima that are numerical or are the shoulder of a real peak, and a table that listed
    /// them would bury the two rows that matter. Prominence is the right measure because it is
    /// scale-free — a 1 dB bump on a 10 mΩ floor and one on a 2 Ω peak are the same shape — where
    /// an absolute threshold in ohms would find every peak on one board and none on the next.</para>
    /// </summary>
    public const double DefaultProminenceDb = 1.0;

    /// <summary>
    /// A branch counts as being on a side of the peak when its susceptance is at least this
    /// fraction of the largest single contribution there.
    ///
    /// <para><b>This is what "a branch that is neither is not named" means numerically</b>
    /// (R-rail12-5). A source's own series resistance, a part far off resonance and a branch the
    /// sweep could not model all sit within rounding of zero susceptance, and naming one of them as
    /// a contributor because its sign happened to fall one way would be attribution by
    /// noise.</para>
    /// </summary>
    public const double ContributorFloor = 1e-3;

    /// <summary>
    /// Every local maximum of <paramref name="magnitudeOhms"/> standing at least
    /// <paramref name="prominenceDb"/> above the lower of its two flanking minima.
    ///
    /// <para><b>The endpoints are never peaks.</b> A curve rising into the top of the band has its
    /// maximum at the last sample and that is a statement about where the sweep stopped, not about
    /// the board — and reporting it as an anti-resonance would put a row in the table that moves
    /// when somebody changes the band.</para>
    /// </summary>
    public static IReadOnlyList<int> FindPeaks(
        double[] magnitudeOhms, double prominenceDb = DefaultProminenceDb)
    {
        ArgumentNullException.ThrowIfNull(magnitudeOhms);

        var peaks = new List<int>();
        int n = magnitudeOhms.Length;
        if (n < 3) return peaks;

        double ratio = Math.Pow(10.0, prominenceDb / 20.0);

        for (int i = 1; i < n - 1; i++)
        {
            double z = magnitudeOhms[i];
            if (!(z > 0) || !double.IsFinite(z)) continue;

            // STRICTLY above the sample before it and at least equal to the one after: that is one
            // test doing two jobs. It is the local-maximum condition, and it picks the FIRST sample
            // of a flat top rather than every sample of it — a plateau would otherwise put the same
            // peak in the table several times.
            if (!(z > magnitudeOhms[i - 1]) || !(z >= magnitudeOhms[i + 1])) continue;

            if (z < Math.Max(Flank(i, -1), Flank(i, +1)) * ratio) continue;

            peaks.Add(i);
        }

        return peaks;

        // The flanking minimum on one side: walk outward while the curve keeps falling and stop
        // where it turns back up. Walking off the end is an ordinary answer — a peak near the edge
        // of the band has its flank AT the edge — and the edge samples themselves were excluded
        // from being peaks by the loop above.
        double Flank(int from, int step)
        {
            double min = magnitudeOhms[from];
            for (int i = from + step; i >= 0 && i < n; i += step)
            {
                double z = magnitudeOhms[i];
                if (!double.IsFinite(z)) break;
                if (z > min) break;
                min = z;
            }
            return min;
        }
    }

    /// <summary>
    /// R-rail12-5. Names the two banks behind one peak.
    /// </summary>
    /// <param name="index">Which sample the peak sits on.</param>
    /// <param name="frequencyHz">Its frequency.</param>
    /// <param name="peakOhms">|Z| there.</param>
    /// <param name="branches">Every branch's admittance AT THAT FREQUENCY.</param>
    /// <param name="limitOhms">The mask's limit there, or null.</param>
    /// <param name="indicative">Whether a class-default ESR is behind the height.</param>
    public static PdnAntiResonancePeak Attribute(
        int index,
        double frequencyHz,
        double peakOhms,
        IEnumerable<PdnBranchAdmittance> branches,
        double? limitOhms,
        bool indicative)
    {
        ArgumentNullException.ThrowIfNull(branches);

        // Summed per group first, because what rings is a BANK: nine 100 nF parts each contribute a
        // ninth of the susceptance and would each be ranked below a single bulk part that is not
        // the contributor at all.
        var order = new List<string>();
        var summed = new Dictionary<string, (List<string> Members, Complex Y)>(StringComparer.Ordinal);

        foreach (var b in branches)
        {
            if (!double.IsFinite(b.Admittance.Real) || !double.IsFinite(b.Admittance.Imaginary))
                continue;

            if (!summed.TryGetValue(b.Group, out var g))
            {
                summed[b.Group] = g = ([], Complex.Zero);
                order.Add(b.Group);
            }

            g.Members.Add(b.Member);
            summed[b.Group] = (g.Members, g.Y + b.Admittance);
        }

        double largest = 0;
        foreach (var g in summed.Values) largest = Math.Max(largest, Math.Abs(g.Y.Imaginary));
        double floor = largest * ContributorFloor;

        var inductive = Side(negative: true);
        var capacitive = Side(negative: false);

        return new PdnAntiResonancePeak(
            index, frequencyHz, peakOhms, inductive, capacitive,
            limitOhms,
            limitOhms is { } lim ? PdnMask.MarginDb(lim, peakOhms) : null,
            indicative);

        PdnContributorGroup? Side(bool negative)
        {
            string? best = null;
            double bestMagnitude = 0, total = 0;

            foreach (string key in order)
            {
                double b = summed[key].Y.Imaginary;
                if (negative ? b >= 0 : b <= 0) continue;

                double magnitude = Math.Abs(b);
                if (magnitude <= floor) continue;

                total += magnitude;
                if (magnitude <= bestMagnitude) continue;
                bestMagnitude = magnitude;
                best = key;
            }

            if (best is null) return null;

            var (members, y) = summed[best];
            return new PdnContributorGroup(best, members, y, total > 0 ? bestMagnitude / total : 1.0);
        }
    }
}
