using System.Numerics;
using CircuitRF.Engine.Loadpull;

namespace CircuitRF.Harmonica;

/// <summary>Why a Pin search stopped. D5 turns everything but <see cref="Compression"/> into a hole.</summary>
public enum PinStopReason
{
    /// <summary>The compression target was reached — the only outcome that produces a grid point.</summary>
    Compression,
    /// <summary>PinMax was reached without compressing. R-hrf-8: this point is a HOLE.</summary>
    PinMax,
    /// <summary>The Newton loop failed. Also a hole, and reported separately from PinMax.</summary>
    NonConvergence,
}

/// <summary>One converged drive level and everything read off it.</summary>
/// <param name="PavlDbm">Available power.</param>
/// <param name="Compression">Gmax − G(Pin), dB.</param>
/// <param name="Point">The operating point itself, so a caller can warm-start from it.</param>
public sealed record PinStep(double PavlDbm, double Compression, OperatingPoint Point)
{
    public required LoadpullEngine.FomResult Foms { get; init; }
    public required double PdcW { get; init; }

    public double PoutW => Foms.PoutW;
    public double GainDb { get; init; }

    /// <summary>Drain efficiency, Pout / Pdc — the same definition <c>PinStepResult</c> uses.</summary>
    public double De  => PdcW > 1e-9 ? Foms.PoutW / PdcW : 0.0;

    /// <summary>PAE, (Pout − Pin_delivered) / Pdc — likewise.</summary>
    public double Pae => PdcW > 1e-9 ? (Foms.PoutW - Foms.PinDeliveredW) / PdcW : 0.0;
}

/// <summary>What a Pin search found at one Γ point.</summary>
/// <param name="Reason">Why it stopped. Anything but <see cref="PinStopReason.Compression"/> is a hole.</param>
/// <param name="Solves">HB solves used, tickle included — the number D4 exists to reduce.</param>
public sealed record PinSearchResult(PinStopReason Reason, int Solves)
{
    /// <summary>The step at the compression target. Null when the search did not reach it.</summary>
    public PinStep? AtCompression { get; init; }

    /// <summary>Every converged step, in the order they were solved. The power-sweep panel's data.</summary>
    public required IReadOnlyList<PinStep> Steps { get; init; }

    /// <summary>Small-signal gain from the tickle, dB. Termination-dependent, hence re-measured per Γ.</summary>
    public double SmallSignalGainDb { get; init; }

    public bool Compressed => Reason == PinStopReason.Compression && AtCompression is not null;
}

/// <summary>
/// R-hrf-7 / D4 — secant bisection on Pin toward the compression target, rather than the batch
/// engine's uniform 1 dB ladder.
///
/// <para><b>Why this replaces the ladder, and only here.</b> <c>LoadpullEngine</c> walks
/// <c>PinStart, PinStart+PinStep, …</c> until it is 0.1 dB past the target — about 30 solves per Γ
/// point, which is correct for a batch reference run and far too slow for an interactive one.
/// Compression <c>Gmax − G(Pin) = x</c> is monotone in Pin over the region of interest, so a secant
/// converges in a handful. <b><c>LoadpullEngine</c>'s own behaviour is untouched</b> and every Hero
/// 3/3B golden still walks the ladder.</para>
///
/// <para><b>The tickle is not optional and is re-taken at every Γ.</b> Gain is termination-dependent,
/// so the small-signal reference the compression is measured against has to be re-established at each
/// grid point — the existing engine's rule, kept.</para>
///
/// <para><b>No FOM is re-derived here.</b> <c>LoadpullEngine.ComputeFoms</c> is the one definition of
/// Pout / Pin_delivered / Gt / Gp, and <see cref="PinStep"/>'s DE and PAE are written exactly as
/// <c>PinStepResult</c> writes them.</para>
/// </summary>
public static class PinSearch
{
    /// <summary>How far below <c>PinStart</c> the tickle sits. Small-signal by a wide margin.</summary>
    public const double TickleBelowStartDb = 30.0;

    /// <summary>Compression tolerance, dB. The search stops when it is this close to the target.</summary>
    public const double CompressionToleranceDb = 0.01;

    /// <summary>Iteration cap, so a non-monotone region cannot spin.</summary>
    public const int MaxSecantSteps = 20;

    /// <summary>The first bracketing stride, dB. Subsequent strides DOUBLE.</summary>
    public const double FirstStrideDb = 3.0;

    /// <summary>
    /// Drives one Γ point to its compression target.
    ///
    /// <para><paramref name="warmStart"/> is the VSWR-nearest converged neighbour's solution, per the
    /// existing rule (<c>loadpull.md</c> §3.3); every step within the search then warm-starts from the
    /// previous one.</para>
    /// </summary>
    /// <param name="pinHintDbm">
    /// Where a neighbouring Γ point compressed, if one has. Bracketing from a hint is the single
    /// biggest saving in a grid: the FIRST point has to find the compression region from
    /// <c>PinStart</c>, and every point after it starts a few dB from the answer. Null bootstraps
    /// from <c>PinStart</c> with geometrically expanding strides.
    /// </param>
    public static PinSearchResult Run(
        HarmonicaContext ctx, TerminationSet terminations, Complex[,]? warmStart = null,
        double? pinHintDbm = null)
    {
        var s = ctx.Model.Settings;
        var steps  = new List<PinStep>();
        int solves = 0;

        Complex[,]? seed = warmStart;

        PinStep? Solve(double pavlDbm)
        {
            solves++;
            var pt = ctx.Solve(terminations, pavlDbm, seed);
            if (!pt.Converged) return null;
            seed = pt.V;
            return Measure(ctx, pt, terminations);
        }

        // ── 1. the tickle ─────────────────────────────────────────────────────
        var tickle = Solve(s.PinStartDbm - TickleBelowStartDb);
        if (tickle is null)
            return new PinSearchResult(PinStopReason.NonConvergence, solves) { Steps = steps };

        double gss = tickle.GainDb;

        // Compression as a function of Pin, against the small-signal reference. Gmax is tracked as a
        // running maximum for the same reason the batch engine tracks one: a real device's gain can
        // still creep up slightly above the tickle before it turns over, and measuring compression
        // from a value the gain later exceeds would report a negative.
        double gMax = gss;

        double CompressionAt(PinStep step)
        {
            if (step.GainDb > gMax) gMax = step.GainDb;
            return gMax - step.GainDb;
        }

        // ── 2. bracket, then secant ───────────────────────────────────────────
        double target = s.CompressionDb;

        var lo = Solve(s.PinStartDbm);
        if (lo is null)
            return new PinSearchResult(PinStopReason.NonConvergence, solves)
                { Steps = steps, SmallSignalGainDb = gss };
        double cLo = CompressionAt(lo);
        steps.Add(lo with { Compression = cLo });

        if (cLo >= target - CompressionToleranceDb)
        {
            // Already at or past the target at the very first drive level. Nothing to search for.
            return new PinSearchResult(PinStopReason.Compression, solves)
            {
                Steps = steps, SmallSignalGainDb = gss, AtCompression = steps[^1],
            };
        }

        // Bracket the target. Strides DOUBLE rather than staying uniform — a uniform 3 dB climb from
        // PinStart to PinMax is the batch engine's ladder wearing a different name, and it dominated
        // the solve count (12 of 14) before this was measured. A hint from a neighbouring Γ point,
        // when there is one, starts the climb beside the answer instead of at the bottom.
        double pinLo = lo.PavlDbm;
        PinStep? hi = null;
        double cHi = double.NaN, pinHi = double.NaN;

        double stride = FirstStrideDb;
        double pin = pinHintDbm is { } hint
            ? Math.Clamp(hint, pinLo + 0.25, s.PinMaxDbm)
            : pinLo + stride;

        while (pin <= s.PinMaxDbm + 1e-9)
        {
            var step = Solve(pin);
            if (step is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves)
                    { Steps = steps, SmallSignalGainDb = gss };

            double c = CompressionAt(step);
            steps.Add(step with { Compression = c });

            if (c >= target) { hi = step; cHi = c; pinHi = pin; break; }

            pinLo = pin; cLo = c;

            if (pin >= s.PinMaxDbm - 1e-9) break;      // PinMax itself has now been tried
            stride *= 2.0;
            pin = Math.Min(pin + stride, s.PinMaxDbm);
        }

        if (hi is null)
            // Never compressed before PinMax. R-hrf-8: this Γ point is a HOLE, thrown out of the grid
            // rather than extrapolated into.
            return new PinSearchResult(PinStopReason.PinMax, solves)
                { Steps = steps, SmallSignalGainDb = gss };

        // ── 3. secant on (Pin, compression) toward the target ─────────────────
        PinStep best = hi with { Compression = cHi };
        double  bestErr = Math.Abs(cHi - target);

        for (int it = 0; it < MaxSecantSteps && bestErr > CompressionToleranceDb; it++)
        {
            double denom = cHi - cLo;
            double next = Math.Abs(denom) < 1e-12
                ? 0.5 * (pinLo + pinHi)                                   // flat: fall back to bisection
                : pinLo + (target - cLo) * (pinHi - pinLo) / denom;

            // Keep the secant inside the bracket; an overshoot on a curve this shape is a bisection
            // step, not a reason to leave the interval where the answer is known to be.
            if (!(next > Math.Min(pinLo, pinHi) && next < Math.Max(pinLo, pinHi)))
                next = 0.5 * (pinLo + pinHi);

            var step = Solve(next);
            if (step is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves)
                    { Steps = steps, SmallSignalGainDb = gss };

            double c = CompressionAt(step);
            var    withC = step with { Compression = c };
            steps.Add(withC);

            if (Math.Abs(c - target) < bestErr) { best = withC; bestErr = Math.Abs(c - target); }

            if (c < target) { pinLo = next; cLo = c; }
            else            { pinHi = next; cHi = c; }
        }

        return new PinSearchResult(PinStopReason.Compression, solves)
        {
            Steps = steps, SmallSignalGainDb = gss, AtCompression = best,
        };
    }

    /// <summary>
    /// Reads the figures of merit off one converged point, through the engine's own definitions —
    /// <c>LoadpullEngine.ComputeFoms</c>, so there is exactly one definition of each and Tier 3's
    /// equivalence run compares two SOLVES rather than two formulas.
    /// </summary>
    public static PinStep Measure(HarmonicaContext ctx, OperatingPoint pt, TerminationSet terminations)
    {
        int k = ctx.Model.Settings.HarmonicCount;
        int gate  = ctx.InterfaceIndex(HarmonicaNetlist.GateTerminal);
        int drain = ctx.InterfaceIndex(HarmonicaNetlist.DrainTerminal);

        // With no package the device terminal IS the termination plane.
        if (gate  < 0) gate  = ctx.InterfaceIndex(HarmonicaNetlist.SourcePlane);
        if (drain < 0) drain = ctx.InterfaceIndex(HarmonicaNetlist.LoadPlane);

        // The TRUE delivered input current, at the extrinsic plane, per R-hrf-4 — not the device's
        // own gate current. This is the quantity Gp and Pin_delivered are defined against, and the
        // one the shipped loadpull Zin bug was about.
        var (_, planeI) = ctx.Interface.PlaneState(
            terminations, HarmonicaContext.DriveVolts(terminations, pt.PavlDbm),
            pt.INlTotal, ctx.Model.Settings.DcBlockFarads);

        var iin = new Complex[k + 1];
        for (int h = 0; h <= k; h++) iin[h] = planeI[(int)TerminationSide.Source, h];

        double pavlW = Math.Pow(10.0, (pt.PavlDbm - 30.0) / 10.0);
        var foms = LoadpullEngine.ComputeFoms(pt.V, iin, pt.INl, drain, gate, pavlW, k);

        // Pdc, exactly as PinStepResult defines it: Σ V_dc · I_supply over the bias nodes, with the
        // supply current read as the DC nonlinear current at that node.
        double pdc = 0;
        if (drain >= 0) pdc += pt.V[drain, 0].Real * pt.INl[drain, 0].Real;
        if (gate  >= 0) pdc += pt.V[gate,  0].Real * pt.INl[gate,  0].Real;

        return new PinStep(pt.PavlDbm, 0.0, pt)
        {
            Foms   = foms,
            PdcW   = pdc,
            GainDb = foms.GtDb,
        };
    }
}
