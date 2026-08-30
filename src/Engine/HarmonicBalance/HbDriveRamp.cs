using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.Loadpull;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// The <c>DriveStepping</c> continuation (harmonic-balance.md §11): walk every tone source's drive
/// up to the requested level in fixed dB rungs, warm-starting each rung from the one below, so a
/// point that cannot be solved cold from a DC seed is reached along its own branch instead.
///
/// <para><b>Why it is still needed with the line search in.</b> HB-P3's backtracking line search
/// (see <see cref="HbNewton.Backtrack"/>) fixes the common failure — a full Newton step overshooting
/// from a DC seed — and it is what turned the shipped Hero-2 fixture's 16 and 20 dBm points from 100
/// iterations non-converged into six. It cannot help where the DC seed is not on the same branch as
/// the answer at all: the line search only shortens a step, it cannot choose a different starting
/// point. The ramp does exactly that, and is therefore the fallback rather than the first move.</para>
///
/// <para><b>The rungs.</b> A fixed <see cref="DefaultStepDb"/> ladder over
/// <see cref="DefaultSpanDb"/>, which is loadpull's own measured choice (2 dB gave zero holes across
/// its Γ grid; see <c>src/Engine/Loadpull/CLAUDE.md</c>). The offsets are RELATIVE to the requested
/// drive — the last rung is always exactly 0 dB, so the point that is finally reported is the point
/// that was asked for, not an accumulated sum near it.</para>
///
/// <para><b>What <see cref="DriveLadder"/> contributes, and what it cannot here.</b> A rung that does
/// not converge is re-walked by <see cref="DriveLadder.ContinueThroughJump{T}"/>, which subdivides
/// that ONE rung into 2ᵈ sub-steps for increasing d and keeps the first depth whose whole chain
/// solves — the same recovery the loadpull engine uses, written once. Its OTHER half, the physical
/// continuity criterion (<see cref="DriveLadder.IsDiscontinuous"/>), is deliberately inert on this
/// path: it is stated in output power, and a generic <c>type=hb</c> testbench has no designated load
/// node from which to compute one. The ramp therefore rejects a sub-step on non-convergence only,
/// and the margin is passed as 0 to say so rather than to disable a check that would otherwise
/// apply.</para>
/// </summary>
public static class HbDriveRamp
{
    /// <summary>Rung size in dB. Loadpull's measured uniform step; see the class remarks.</summary>
    public const double DefaultStepDb = 2.0;

    /// <summary>
    /// How far below the requested drive the ladder starts, in dB. 20 dB is deep enough that a PA
    /// fixture is unambiguously small-signal there — the regime whose DC seed is reliable, which is
    /// the whole point of starting low.
    /// </summary>
    public const double DefaultSpanDb = 20.0;

    /// <summary>
    /// Every source in the netlist whose RF drive the ramp can move. Empty means the drive is not
    /// expressed through a tone source this engine can reach (it might be, say, an expression the
    /// netlist resolves at elaboration), and the caller must then report the failure rather than
    /// pretend to ramp.
    /// </summary>
    public static IReadOnlyList<IDriveScalable> Collect(ElaboratedNetlist netlist)
    {
        var found = new List<IDriveScalable>();
        foreach (var ec in netlist.Components)
            if (ec.Model is IDriveScalable d) found.Add(d);
        return found;
    }

    /// <summary>Set every collected source to <paramref name="offsetDb"/> below its declared drive.</summary>
    public static void SetOffset(IReadOnlyList<IDriveScalable> sources, double offsetDb)
    {
        double scale = Math.Pow(10.0, offsetDb / 20.0);
        foreach (var d in sources) d.DriveScale = scale;
    }

    /// <summary>Restore every collected source to its declared drive. Always called in a finally.</summary>
    public static void Restore(IReadOnlyList<IDriveScalable> sources) => SetOffset(sources, 0.0);

    /// <summary>The rung offsets, in dB relative to the requested drive: −span … 0, last exactly 0.</summary>
    public static double[] Offsets(double spanDb = DefaultSpanDb, double stepDb = DefaultStepDb)
    {
        int n = Math.Max(1, (int)Math.Ceiling(spanDb / stepDb));
        var offsets = new double[n + 1];
        for (int i = 0; i <= n; i++) offsets[i] = i == n ? 0.0 : -spanDb + i * (spanDb / n);
        return offsets;
    }

    /// <summary>
    /// Walk the ladder and return the rung at 0 dB — the requested drive — or null when some rung is
    /// unreachable even subdivided, in which case the caller keeps whatever it already had.
    ///
    /// <para><paramref name="solveAt"/> is handed the rung's offset (already applied to every source
    /// by this method — it only has to re-extract and solve) and the previous rung's converged
    /// spectrum, and returns null for a rung that did not converge. It is also where the caller
    /// records the rung in its <see cref="HbConvergenceTrace"/>; nothing here does.</para>
    /// </summary>
    /// <param name="sources">From <see cref="Collect"/>; must be non-empty.</param>
    /// <param name="seedOf">The converged spectrum of a rung, for warm-starting the next.</param>
    public static TRung? Walk<TRung>(
        IReadOnlyList<IDriveScalable> sources,
        Func<double, Complex[,]?, TRung?> solveAt,
        Func<TRung, Complex[,]> seedOf,
        double spanDb = DefaultSpanDb,
        double stepDb = DefaultStepDb)
        where TRung : class
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(solveAt);
        ArgumentNullException.ThrowIfNull(seedOf);

        TRung? Probe(double offsetDb, Complex[,]? warm)
        {
            SetOffset(sources, offsetDb);
            return solveAt(offsetDb, warm);
        }

        var         offsets = Offsets(spanDb, stepDb);
        Complex[,]? warm    = null;
        TRung?      last    = null;
        double      prev    = offsets[0];

        for (int i = 0; i < offsets.Length; i++)
        {
            var rung = Probe(offsets[i], warm);

            // The bottom rung has no predecessor to subdivide toward, so a failure there is not a
            // step-size problem — it is the small-signal point itself failing, which the ramp has
            // nothing to say about.
            if (rung is null && i > 0)
                rung = DriveLadder.ContinueThroughJump(
                    prev, double.NaN, warm, offsets[i],
                    Probe, _ => double.NaN, seedOf, marginDb: 0.0);

            if (rung is null) return null;

            warm = seedOf(rung);
            last = rung;
            prev = offsets[i];
        }

        return last;
    }
}
