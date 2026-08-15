// ================================================================
//  DriveLadder.cs — the continuity criterion and the bisection continuation, written ONCE.
//
//  Two Pin drive-up ladders exist in this repo and neither may own this: `LoadpullEngine.
//  RunOneTermination` (the batch grid's inner sweep, here) and `CircuitRF.Harmonica.PinSearch.Sweep`
//  (harmonicaRF's live tier-A ladder and its contour grid). src/Harmonica references src/Engine and
//  not the other way round, so the shared half lives on this side of that edge.
// ================================================================

using System.Numerics;

namespace CircuitRF.Engine.Loadpull;

/// <summary>
/// The rule a Pin drive-up ladder uses to decide that it took a step it could not take, and the
/// recovery when it did.
///
/// <para><b>Why this exists at all.</b> A converged harmonic-balance solve is not automatically the
/// RIGHT root. Measured on harmonicaRF's shipped default under the Class F preset: a 2 dB drive step
/// took the Newton, at ‖F‖ ≈ 2e-9, from a sane rung (Pout 19.2 dBm, Pdc 3.49 W) to a root of the same
/// residual drawing 353 kW from a 48 V supply — Pout 89.5 dBm, DE 251%. It reported convergence and
/// was consumed as ordinary data. Walking the identical termination at 1 dB, 0.5 dB and 0.25 dB all
/// give the same, entirely ordinary answer, so the step size was the whole of the difference.</para>
/// </summary>
public static class DriveLadder
{
    /// <summary>
    /// The default slack, in dB, around <see cref="IsDiscontinuous"/>'s physical statement. 0 or
    /// negative disables the guard.
    /// </summary>
    public const double DefaultContinuityMarginDb = 3.0;

    /// <summary>
    /// How far <see cref="ContinueThroughJump{T}"/> may subdivide ONE ladder step before giving up:
    /// depth d walks 2ᵈ sub-steps, so 4 bottoms out at a sixteenth of the step. A bound rather than a
    /// budget — the guard fires on the rare rung, 2+4+8+16 = 30 extra solves is its absolute worst
    /// case there, and the measured cost is depth 1 (2 solves), because one bisection of a 2 dB step
    /// is already the 1 dB ladder that walks the same termination cleanly.
    /// </summary>
    public const int MaxContinuationDepth = 4;

    /// <summary>
    /// Whether <paramref name="poutHiDbm"/> can be the same solution branch as
    /// <paramref name="poutLoDbm"/>, one drive step later.
    ///
    /// <para><b>The criterion is physical, not tuned.</b> Along the branch a real amplifier follows,
    /// output power tracks input power at 1:1 below compression and MORE SLOWLY above it. So a rung
    /// whose Pout moved further than its own Pin step did — in EITHER direction; a collapse is as
    /// impossible as an expansion — did not get there along that branch. The margin is slack around
    /// that statement rather than the statement itself.</para>
    ///
    /// <para>False (never discontinuous) when the margin is disabled, when the two rungs sit at the
    /// same drive, or when either Pout is not a finite dB figure — a zero-output rung is a story for
    /// the convergence flag to tell, not for this guard.</para>
    /// </summary>
    public static bool IsDiscontinuous(double pinLoDbm, double poutLoDbm,
                                       double pinHiDbm, double poutHiDbm, double marginDb)
    {
        if (!(marginDb > 0)) return false;

        double dPin = Math.Abs(pinHiDbm - pinLoDbm);
        if (dPin <= 0) return false;

        if (!double.IsFinite(poutLoDbm) || !double.IsFinite(poutHiDbm)) return false;

        return Math.Abs(poutHiDbm - poutLoDbm) > dPin + marginDb;
    }

    /// <summary>Pout in dBm, or NaN for a non-positive output power — the one spelling of this
    /// conversion both ladders read <see cref="IsDiscontinuous"/> through.</summary>
    public static double PoutDbm(double poutW) => poutW > 0 ? 10 * Math.Log10(poutW) + 30 : double.NaN;

    /// <summary>
    /// Re-walks ONE ladder step as a bisection continuation. Subdivides
    /// <c>[fromPinDbm, toPinDbm]</c> into 2ᵈ equal sub-steps for d = 1 … <paramref name="maxDepth"/>,
    /// each warm-started from its own predecessor; the FIRST depth whose whole chain is continuous
    /// wins.
    ///
    /// <para><b>Returns null when no depth produces a continuous chain, and the caller must then KEEP
    /// whatever it had.</b> A jump that survives a sixteenth-step walk is a property of the circuit,
    /// not of the step size, and hiding a real bifurcation is worse than showing it. This is a
    /// statement about which root the ladder CONVERGES TO — never a plausibility filter on the
    /// answer.</para>
    ///
    /// <para><paramref name="solveAt"/> returns null for a sub-step that did not converge, which
    /// abandons that depth and tries the next one. It is also where the caller counts its own solves;
    /// nothing here does.</para>
    /// </summary>
    /// <typeparam name="T">The caller's own converged-step type.</typeparam>
    /// <param name="fromPoutDbm">Pout at <paramref name="fromPinDbm"/>, dBm — the rung being continued FROM.</param>
    /// <param name="fromSeed">That rung's converged spectrum, the warm start every depth restarts from.</param>
    /// <param name="solveAt">(Pin dBm, warm start) → a converged step, or null.</param>
    /// <param name="poutDbmOf">Pout of a step, dBm.</param>
    /// <param name="seedOf">The spectrum of a step, for warm-starting the next sub-step.</param>
    public static T? ContinueThroughJump<T>(
        double fromPinDbm, double fromPoutDbm, Complex[,]? fromSeed, double toPinDbm,
        Func<double, Complex[,]?, T?> solveAt,
        Func<T, double> poutDbmOf,
        Func<T, Complex[,]?> seedOf,
        double marginDb, int maxDepth = MaxContinuationDepth)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(solveAt);
        ArgumentNullException.ThrowIfNull(poutDbmOf);
        ArgumentNullException.ThrowIfNull(seedOf);

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            int    n   = 1 << depth;
            double sub = (toPinDbm - fromPinDbm) / n;

            double prevPin  = fromPinDbm;
            double prevPout = fromPoutDbm;
            var    warm     = fromSeed;
            T?     last     = null;
            bool   ok       = true;

            for (int i = 1; i <= n; i++)
            {
                // The final sub-step lands on toPinDbm EXACTLY rather than on an accumulated sum —
                // a rung reported at 12.000000000000002 dBm would miss every level-keyed lookup a
                // caller makes against it.
                double pin   = i == n ? toPinDbm : fromPinDbm + i * sub;
                var    probe = solveAt(pin, warm);

                if (probe is null ||
                    IsDiscontinuous(prevPin, prevPout, pin, poutDbmOf(probe), marginDb))
                { ok = false; break; }

                prevPin  = pin;
                prevPout = poutDbmOf(probe);
                warm     = seedOf(probe);
                last     = probe;
            }

            if (ok && last is not null) return last;
        }

        return null;
    }
}
