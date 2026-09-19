// The optional swept band (docs/design/smith-chart.md §3.6; brief-smith-9-q-and-sweep.md R-smith9-4).
//
// ONE Evaluate PER POINT and nothing else. The band is what makes bandwidth visible on a tool whose
// premise is that bandwidth is not the question, and it is off by default for exactly that reason.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CircuitRF.Design.Smith;

/// <summary>
/// The band, walked.
/// </summary>
/// <param name="Gamma">Γ of the load at each of <see cref="SmithSweep.Points"/> frequencies across
/// <paramref name="StartHz"/>…<paramref name="StopHz"/>. Empty when the band is off, or when the
/// cascade cannot be walked at all — in which case the strip's refusal half is already saying
/// why.</param>
/// <param name="StartHz">The band actually walked, AFTER any clamp.</param>
/// <param name="StopHz">Likewise.</param>
/// <param name="Clamped">True when the document asked for a wider band than the generator table can
/// answer for. <b>A clamp, never a refusal</b> — the one caller allowed to (<c>R-smith2-5</c>).</param>
public readonly record struct SmithBandResult(
    IReadOnlyList<Complex> Gamma, double StartHz, double StopHz, bool Clamped);

/// <summary>
/// §3.6's third kind of frequency: a start/stop/npts band drawn as a thin continuous locus through
/// the load points.
/// </summary>
/// <remarks>
/// <b>It is CLAMPED to the generator table's span, with a stated note, rather than refused.</b> That
/// is the one place in this tool where an out-of-span frequency is not a refusal, and the reason is
/// a distinction rather than a convenience: <i>a band is a viewing choice, where a design frequency
/// is a design input</i>. Extrapolating the generator impedance past the table would be inventing
/// data; narrowing the view to what the table can answer for costs the user nothing but the part of
/// the picture that was never there.
///
/// <para><b>A single-row table is not clamped</b>, on <see cref="SmithCascade.GeneratorImpedance"/>'s
/// own exception: one row is one impedance, flat, and every frequency is legal against it.</para>
/// </remarks>
public static class SmithBand
{
    /// <summary>Walks <paramref name="design"/>'s band, or returns an empty one.</summary>
    /// <param name="documentDirectory">What an S1P/S2P element's relative <c>FileRef</c> resolves
    /// against — the document's own folder.</param>
    public static SmithBandResult Evaluate(SmithDesign design, string? documentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(design);

        var sweep = design.Sweep;
        if (!sweep.Enabled || sweep.Points < 2 || !(sweep.StartHz < sweep.StopHz))
            return new SmithBandResult([], sweep.StartHz, sweep.StopHz, Clamped: false);

        var (start, stop, clamped) = ClampToTable(design, sweep.StartHz, sweep.StopHz);

        double z0 = design.Chart.Z0Ohm;
        var gamma = new List<Complex>(sweep.Points);

        try
        {
            for (int i = 0; i < sweep.Points; i++)
            {
                double f = start + (stop - start) * i / (sweep.Points - 1);

                // CLAMP, not Refuse — and it is belt and braces: the span clamp above has already
                // brought every f inside the table, and this says so at the one call that is allowed
                // to. A rounding step at either end of the band is not a reason to draw nothing.
                var nodes = SmithCascade.Evaluate(design, f, documentDirectory, SmithOutOfBand.Clamp);
                gamma.Add(SmithCascade.Gamma(nodes[^1].Z, z0));
            }
        }
        catch (Exception)
        {
            // A cascade that cannot be walked at one frequency cannot be walked at any of them — the
            // failures are a missing file or a malformed element, not a frequency. Drawing the part
            // that worked would invite the reader to trust it.
            return new SmithBandResult([], start, stop, clamped);
        }

        return new SmithBandResult(gamma, start, stop, clamped);
    }

    /// <summary>
    /// The band the table can answer for.
    /// </summary>
    /// <remarks>
    /// <b>The ENDS are clamped and the points are then spread across what is left</b>, rather than
    /// each sample being clamped on its own. Clamping per sample piles half the band up on one
    /// frequency and draws a locus that stops moving without saying so; clamping the ends narrows
    /// the view, which is what the note reports.
    /// </remarks>
    private static (double StartHz, double StopHz, bool Clamped) ClampToTable(
        SmithDesign design, double startHz, double stopHz)
    {
        if (design.Generator.Rows.Count < 2 || design.Generator.Span is not { } span)
            return (startHz, stopHz, false);

        double start = Math.Clamp(startHz, span.StartHz, span.StopHz);
        double stop  = Math.Clamp(stopHz,  span.StartHz, span.StopHz);

        return (start, stop, start != startHz || stop != stopHz);
    }
}
