// The swept band (docs/design/smith-chart.md §3.6; brief-smith-9-q-and-sweep.md R-smith9-4).
//
// ONE Evaluate PER POINT and nothing else. The band is what makes bandwidth visible on a tool whose
// premise is that bandwidth is not the question.
//
// IT IS THE GENERATOR TABLE'S OWN SPAN, ALWAYS (owner instruction, 2026-09-19). It used to be a
// start/stop/npts block with a checkbox, which is three numbers the user had to keep in step with
// the table and one more thing for a document to be refused over — and every honest value of those
// three was already written down one card higher up. The table states the frequencies this design is
// about; the band is the locus through them.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CircuitRF.Design.Smith;

/// <summary>
/// The band, walked.
/// </summary>
/// <param name="FrequencyHz">Where each sample was taken, hertz — <b>the grid itself and not the
/// two ends it was built from</b>. It is carried rather than left to be recomputed because a caller
/// that has to write the band out (<c>circuitrf smith -o out.s1p</c>) would otherwise hold a second
/// copy of the spacing rule, and two spacings that agree until one of them is changed is exactly
/// the kind of pair this tool refuses to have anywhere else. Same length as
/// <paramref name="Gamma"/>, and empty with it.</param>
/// <param name="Gamma">Γ of the load at each of <paramref name="FrequencyHz"/>. Empty when the
/// generator table states a single frequency — a band needs two ends — or when the cascade cannot
/// be walked at all, in which case the strip's refusal half is already saying why.</param>
/// <param name="StartHz">The band walked, which is the table's first row.</param>
/// <param name="StopHz">Likewise, its last.</param>
public readonly record struct SmithBandResult(
    IReadOnlyList<double>  FrequencyHz,
    IReadOnlyList<Complex> Gamma,
    double StartHz, double StopHz);

/// <summary>
/// §3.6's second kind of frequency: the locus the load traces across the generator table's span,
/// drawn as a thin continuous line through the load points.
/// </summary>
/// <remarks>
/// <b>There is nothing to set and nothing to get wrong.</b> The ends are the table's first and last
/// rows, so the band is exactly the part of the picture the generator can be asked about — no
/// clamp, no note about one, and no way to state a band the document then refuses.
///
/// <para><b>A single-row table draws no band.</b> One row is one impedance, flat: the locus is one
/// point, which the load points already draw.</para>
/// </remarks>
public static class SmithBand
{
    /// <summary>
    /// How many frequencies the band is walked at.
    /// </summary>
    /// <remarks>
    /// <b>A constant rather than a setting, and the reason is the DRAG rather than the sweep.</b>
    /// The band is one full <see cref="SmithCascade.Evaluate"/> per point and it is re-walked inside
    /// every rebuild of the chart — which is every pointer move of a gripper drag, twenty times a
    /// second. Fifty-one samples is already finer than the pixels a locus a few hundred wide is
    /// drawn on, so a larger number buys a picture nobody can tell apart at a cost the hand holding
    /// the gripper can feel.
    /// </remarks>
    public const int Points = 51;

    /// <summary>Walks <paramref name="design"/>'s band, or returns an empty one.</summary>
    /// <param name="documentDirectory">What an S1P/S2P element's relative <c>FileRef</c> resolves
    /// against — the document's own folder.</param>
    public static SmithBandResult Evaluate(SmithDesign design, string? documentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(design);

        if (design.Generator.Rows.Count < 2 || design.Generator.Span is not { } span)
            return new SmithBandResult([], [], 0.0, 0.0);

        var (start, stop) = span;
        if (!(start < stop)) return new SmithBandResult([], [], start, stop);

        double z0 = design.Chart.Z0Ohm;
        var freqs = new List<double>(Points);
        var gamma = new List<Complex>(Points);

        try
        {
            for (int i = 0; i < Points; i++)
            {
                double f = start + (stop - start) * i / (Points - 1);

                // CLAMP, not Refuse, and it is belt and braces: every f above is a convex
                // combination of the table's own two ends and is inside it already. A rounding step
                // at either end of the band is not a reason to draw nothing.
                var nodes = SmithCascade.Evaluate(design, f, documentDirectory, SmithOutOfBand.Clamp);

                freqs.Add(f);
                gamma.Add(SmithCascade.Gamma(nodes[^1].Z, z0));
            }
        }
        catch (Exception)
        {
            // A cascade that cannot be walked at one frequency cannot be walked at any of them — the
            // failures are a missing file or a malformed element, not a frequency. Drawing the part
            // that worked would invite the reader to trust it.
            return new SmithBandResult([], [], start, stop);
        }

        return new SmithBandResult(freqs, gamma, start, stop);
    }
}
