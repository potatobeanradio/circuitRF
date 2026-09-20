// The reading the status strip states, and the one `circuitrf smith` prints
// (docs/design/smith-chart.md §3.4, §5.3; brief-smith-4-document-window.md R-smith4-8,
// brief-smith-10-cli-verb.md R-smith10-1).
//
// IT IS HERE BECAUSE THERE ARE TWO READERS AND THERE MAY ONLY BE ONE ANSWER. The window's strip and
// the headless verb report the same five numbers about the same document, and a VSWR or a mismatch
// computed once in a view model and again in a verb is two chances for a sign, a conjugate or a
// square to be wrong — in a quantity whose wrong value looks entirely ordinary. So the arithmetic
// is stated once, below the firewall, and both surfaces FORMAT it.
//
// NOTHING HERE VALIDATES THE DOCUMENT. It evaluates the cascade as it stands, exactly as
// SmithCascade does, and lets that call's own refusals out.

using System;
using System.Numerics;

namespace CircuitRF.Design.Smith;

/// <summary>
/// What the cascade lands on at one frequency.
/// </summary>
/// <param name="FrequencyHz">Where it was read, in hertz.</param>
/// <param name="GeneratorZ">Node 0 — the generator impedance §3.6 interpolated at this frequency.</param>
/// <param name="LoadZ">The LAST node of the walk, which with no elements placed is the generator
/// itself. <b>There is no load element and nothing terminates the cascade</b> — the load is where
/// you read.</param>
/// <param name="Z0Ohm">What Γ is against: the chart's own reference impedance, not the
/// generator's.</param>
/// <param name="Gamma"><see cref="SmithCascade.Gamma"/> of <paramref name="LoadZ"/>.</param>
/// <param name="Vswr">(1+|Γ|)/(1−|Γ|), and <see cref="double.PositiveInfinity"/> at or outside the
/// unit circle. <b>A |Γ| ≥ 1 is not clamped</b>: an active S2P or a Z1P with negative R legitimately
/// puts the load there, and reporting a finite VSWR for it would be a lie about a stability
/// result.</param>
/// <param name="MismatchDb">The power the mismatch costs, −10·log₁₀(1−|Γ|²) against
/// <paramref name="Z0Ohm"/> — the same Γ as <paramref name="Gamma"/> and therefore the same question
/// <paramref name="Vswr"/> answers, said in decibels. Zero exactly at Γ = 0, and
/// <see cref="double.PositiveInfinity"/> at or outside the unit circle, where VSWR is infinite
/// too.</param>
public readonly record struct SmithReading(
    double  FrequencyHz,
    Complex GeneratorZ,
    Complex LoadZ,
    double  Z0Ohm,
    Complex Gamma,
    double  Vswr,
    double  MismatchDb);

/// <summary>The five numbers §5.3's strip states, computed once.</summary>
public static class SmithReadings
{
    /// <summary>
    /// Reads <paramref name="design"/> at <paramref name="fHz"/>.
    /// </summary>
    /// <param name="design">The document. Not validated as a whole — <see cref="SmithCascade"/>'s
    /// own remarks say why, and <see cref="SmithDesign.Refusal"/> is the caller's to ask.</param>
    /// <param name="fHz">The frequency, HERTZ.</param>
    /// <param name="documentDirectory">What an element's relative <c>FileRef</c> resolves
    /// against — the folder the <c>.csmith</c> lives in.</param>
    /// <param name="outOfBand">What to do with a frequency the generator table does not span.
    /// <see cref="SmithOutOfBand.Refuse"/> unless the caller is a swept band.</param>
    /// <exception cref="InvalidDataException">Whatever the cascade refused with, verbatim.</exception>
    public static SmithReading At(
        SmithDesign    design,
        double         fHz,
        string?        documentDirectory = null,
        SmithOutOfBand outOfBand         = SmithOutOfBand.Refuse)
    {
        ArgumentNullException.ThrowIfNull(design);

        var nodes = SmithCascade.Evaluate(design, fHz, documentDirectory, outOfBand);
        return Of(design.Chart.Z0Ohm, fHz, nodes[0].Z, nodes[^1].Z);
    }

    /// <summary>
    /// The same reading from a walk already performed — <b>the form a caller with nodes in hand
    /// uses</b>, so a chart that has just evaluated does not evaluate a second time to put numbers
    /// under itself.
    /// </summary>
    public static SmithReading Of(double z0Ohm, double fHz, Complex generatorZ, Complex loadZ)
    {
        var    gamma = SmithCascade.Gamma(loadZ, z0Ohm);
        double mag   = gamma.Magnitude;
        double vswr  = mag < 1.0 ? (1.0 + mag) / (1.0 - mag) : double.PositiveInfinity;

        // THE MISMATCH IS AGAINST THE CHART'S OWN Z₀ AND NOT AGAINST conj(Z_gen) (Q-17, owner
        // decision 2026-09-19). It is |Γ|² put through −10·log₁₀(1−|Γ|²) — the power the mismatch
        // costs rather than the reflection itself, which is what a strip saying "dB" has to mean.
        //
        // WHAT IT USED TO BE, AND WHY THAT WAS WRONG IN THE HAND. §3.4 draws the conjugate-match
        // target glyphs at Γ(conj(Z_gen)) and this reported the mismatch against THEM, which is
        // faithful to the note and answers a different question from the VSWR sitting beside it.
        // Driving the shipped example showed what that reads like: a two-element match taking
        // 8 − j12 Ω to 50 Ω lands at VSWR 1.002 and the strip said 3.411 dB — and the number got
        // SMALLER at the band edges, where the match is worse (2.068 dB at 2.3 GHz). A number
        // labelled as a mismatch in decibels, in a column beside VSWR, reads as match quality; one
        // that moves the other way is worse than no number at all. The target glyphs are unchanged
        // and landing a load point on one is still the conjugate match — it simply has no column.
        double mismatchSq = mag * mag;
        double mismatchDb = mismatchSq < 1.0 ? -10.0 * Math.Log10(1.0 - mismatchSq)
                                             : double.PositiveInfinity;

        return new SmithReading(fHz, generatorZ, loadZ, z0Ohm, gamma, vswr, mismatchDb);
    }
}
