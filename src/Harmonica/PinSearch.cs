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
    /// <summary>
    /// The step whose SPECTRUM the caller should read for anything beyond the scalar FOMs — the
    /// glyphs, Zin, AM/PM, the loadline, the published cubes. Null when the search did not reach the
    /// compression target. For <see cref="Run"/> this IS the compression point, exactly (the secant
    /// lands there). For <see cref="Sweep"/> it is the NEAREST solved ladder point unless
    /// <c>ExactCompressionSolve</c> is on, in which case it is the one real extra solve taken there —
    /// see <see cref="SweepCompression"/>, which is where the SCALAR figures at compression belong.
    /// </summary>
    public PinStep? AtCompression { get; init; }

    /// <summary>
    /// R-h9r2-17a — populated by <see cref="Sweep"/> only (null for <see cref="Run"/>, whose own
    /// <see cref="AtCompression"/> already sits exactly on target — nothing to interpolate). Carries
    /// the scalar figures AT the compression target: interpolated from the first bracketing pair of
    /// ladder points by default, or read straight off <c>ExactCompressionSolve</c>'s one extra solve
    /// when that option is on. <b>A caller reading Pin/Pout/Gain/DE/PAE/Pdc "at compression" for a
    /// sweep result must read THIS, never <c>AtCompression</c>'s own step</b> — with a 1 dB ladder,
    /// the step's own numbers are rounded to the nearest whole dB, which is precisely the error this
    /// exists to remove.
    /// </summary>
    public CompressionReadout? SweepCompression { get; init; }

    /// <summary>Every converged step, in the order they were solved. The power-sweep panel's data.</summary>
    public required IReadOnlyList<PinStep> Steps { get; init; }

    /// <summary>
    /// Small-signal gain from the tickle, dB. Termination-dependent, hence re-measured per Γ. Null
    /// when R-h9r2-18a's tickle is OFF — never a fabricated value; <c>gMax</c> then seeds from the
    /// first solved point instead (see <see cref="Run"/>/<see cref="Sweep"/>'s own remarks).
    /// </summary>
    public double? SmallSignalGainDb { get; init; }

    public bool Compressed => Reason == PinStopReason.Compression && AtCompression is not null;
}

/// <summary>
/// R-h9r2-17a — the compression point's scalar figures, named per-quantity domain so two
/// implementations cannot drift apart in the fourth digit: Pin/Gain in dB/dBm, Pout in dBm (the
/// curve the power-sweep panel actually plots), DE/PAE as ratios (0…1, matching <see cref="PinStep.
/// De"/>/<see cref="PinStep.Pae"/>), Pdc in watts.
/// </summary>
/// <param name="Spectrum">
/// The step whose CONVERGED spectrum backs this reading — the nearest solved ladder point by default,
/// or the one real extra solve when <c>ExactCompressionSolve</c> is on. Never a fabricated
/// <see cref="PinStep"/>: a linear blend of two converged HB solutions is not itself a solution (it
/// satisfies the harmonic-balance residual at neither Pin level), so nothing here interpolates a
/// spectrum — only the scalar figures above are ever interpolated.
/// </param>
/// <param name="WasInterpolated">False when <c>ExactCompressionSolve</c> produced this reading from one
/// real solve at the target; true when it was linearly interpolated from the bracketing ladder pair.</param>
public sealed record CompressionReadout(
    double PinDbm, double PoutDbm, double GainDb, double De, double Pae, double PdcW,
    PinStep Spectrum, bool WasInterpolated);

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

        // ── 1. the tickle (R-h9r2-18a — optional, and an ABSOLUTE level) ────────
        double? gss = null;
        double gMax = double.NegativeInfinity;
        if (s.TickleEnabled)
        {
            var tickle = Solve(s.TickleDbm);
            if (tickle is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves) { Steps = steps };
            gss  = tickle.GainDb;
            gMax = gss.Value;
        }

        // Compression as a function of Pin, against the small-signal reference. Gmax is tracked as a
        // running maximum for the same reason the batch engine tracks one: a real device's gain can
        // still creep up slightly above the tickle before it turns over, and measuring compression
        // from a value the gain later exceeds would report a negative. With the tickle OFF, gMax seeds
        // from the first solved point below instead (R-h9r2-18a: "compression is then referenced to
        // the gain at Start").
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
        if (!s.TickleEnabled) gMax = lo.GainDb;
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
    /// R-h9r2-17/17a/18/18a/19 — the EXPLICIT uniform Pin sweep tier A drives: every point
    /// <c>Start, Start+Step, …</c> up to and INCLUDING <c>Stop</c>, each a real HB solve, nothing
    /// resampled or decimated. This is a completely different shape from <see cref="Run"/>'s
    /// bracket-and-secant — deliberately: <see cref="Run"/> stays what <c>ContourGrid</c> calls once
    /// per Γ point (§5.1's own guardrail — a 61-point grid × a 61-point uniform sweep would be
    /// ~13× the solves and make the grid unaffordable), while this is tier A's alone, called once per
    /// frame at the marker's own terminations.
    ///
    /// <para><b>Inclusive at both ends</b> (R-h9r2-17): <paramref name="stopDbm"/> is always solved,
    /// even when the range is not an integer multiple of <paramref name="stepDbm"/> — the final
    /// interval is then short rather than <paramref name="stopDbm"/> being dropped.</para>
    ///
    /// <para><b>Compression is INTERPOLATED from the ladder</b> (R-h9r2-17a), never from an extra
    /// solve by default — <see cref="PinSearchResult.SweepCompression"/> carries it.
    /// <paramref name="exactCompressionSolve"/> (default false, <c>HarmonicaSettings.
    /// ExactCompressionSolve</c>) trades the interpolation for one real HB solve at the interpolated
    /// Pin, after which every figure — scalar AND spectrum — comes from that one solved state.</para>
    ///
    /// <para><paramref name="warmStart"/> seeds the FIRST point solved (the tickle, when on, else
    /// <paramref name="startDbm"/>); every point after that warm-starts from its own predecessor in
    /// the ladder, exactly like <see cref="Run"/>. <paramref name="priorLevelSpectra"/> is R-h9r2-19's
    /// lever 1 — the PREVIOUS FRAME's converged spectrum at each Pin LEVEL (keyed by the level itself,
    /// rounded), which is a far better seed than the ladder's own neighbour when only the termination
    /// moved slightly between frames. When supplied, it is tried FIRST at every level, falling back to
    /// the in-ladder seed only for a level with no prior-frame entry.</para>
    /// </summary>
    public static PinSearchResult Sweep(
        HarmonicaContext ctx, TerminationSet terminations,
        double startDbm, double stopDbm, double stepDbm,
        Complex[,]? warmStart = null,
        IReadOnlyDictionary<double, Complex[,]>? priorLevelSpectra = null)
    {
        var s = ctx.Model.Settings;
        var steps  = new List<PinStep>();
        int solves = 0;

        Complex[,]? seed = warmStart;

        PinStep? Solve(double pavlDbm)
        {
            solves++;
            Complex[,]? levelSeed = priorLevelSpectra is not null &&
                                    priorLevelSpectra.TryGetValue(Math.Round(pavlDbm, 6), out var prior)
                ? prior : seed;
            var pt = ctx.Solve(terminations, pavlDbm, levelSeed);
            if (!pt.Converged) return null;
            seed = pt.V;
            return Measure(ctx, pt, terminations);
        }

        // ── 1. the tickle (R-h9r2-18a) ──────────────────────────────────────────
        double? gss = null;
        double gMax = double.NegativeInfinity;
        if (s.TickleEnabled)
        {
            var tickle = Solve(s.TickleDbm);
            if (tickle is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves) { Steps = steps };
            gss  = tickle.GainDb;
            gMax = gss.Value;
        }

        // gMax is the RUNNING maximum, exactly Run()'s own CompressionAt rule — updated as the ladder
        // is walked, in ORDER, never recomputed globally after the fact. This is what "first crossing,
        // lowest Pin" (R-h9r2-17a) actually means: a device with a non-monotone gain curve must not be
        // read against a peak the ladder has not reached YET at the point being measured, or the very
        // first honest crossing gets skipped in favour of a later, spurious one measured against a
        // peak from further up the ladder — measured directly: doing it the other way moved this
        // fixture's own compression point from single digits of dBm to 27 dBm. With the tickle off, it
        // seeds from the FIRST solved ladder point (R-h9r2-18a).
        double CompressionAt(PinStep step)
        {
            if (step.GainDb > gMax) gMax = step.GainDb;
            return gMax - step.GainDb;
        }

        double target = s.CompressionDb;
        int cross = -1;

        // ── 2. the ladder, every point a real solve, inclusive at both ends (R-h9r2-19) ────────
        // Every point is solved regardless of where compression crosses — R-h9r2-19 forbids stopping
        // early, unlike Run()'s own bracket-and-secant. The FIRST crossing is still recorded AS the
        // ladder is walked (R-h9r2-17a's own "first one"), from the incremental gMax above.
        foreach (double pin in Ladder(startDbm, stopDbm, stepDbm))
        {
            var step = Solve(pin);
            if (step is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves)
                    { Steps = steps, SmallSignalGainDb = gss };

            if (!s.TickleEnabled && steps.Count == 0) gMax = step.GainDb;
            double c = CompressionAt(step);
            steps.Add(step with { Compression = c });

            if (cross < 0 && steps.Count > 1 && steps[^2].Compression < target && c >= target)
                cross = steps.Count - 2;
        }

        if (steps.Count == 0)
            return new PinSearchResult(PinStopReason.PinMax, solves) { Steps = steps, SmallSignalGainDb = gss };

        CompressionReadout interpolated;
        if (cross < 0)
        {
            if (steps[0].Compression < target)
                // Never compressed before Stop. A NORMAL outcome (R-h9r2-17), not an error — the
                // panel's own "did not compress" note is what draws this.
                return new PinSearchResult(PinStopReason.PinMax, solves) { Steps = steps, SmallSignalGainDb = gss };

            // Compressed at (or past) the very first ladder point — nothing to bracket; that point IS
            // the reading, by construction (WasInterpolated: false — it is not a blend of two points).
            var only = steps[0];
            interpolated = new CompressionReadout(only.PavlDbm, PoutDbmOf(only), only.GainDb, only.De,
                                                   only.Pae, only.PdcW, only, WasInterpolated: false);
        }
        else
        {
            var lo = steps[cross];
            var hi = steps[cross + 1];
            double denom = hi.Compression - lo.Compression;
            double f = Math.Abs(denom) < 1e-12 ? 0.5 : (target - lo.Compression) / denom;
            f = Math.Clamp(f, 0.0, 1.0);

            double poutLoDbm = PoutDbmOf(lo), poutHiDbm = PoutDbmOf(hi);
            double poutAtComp = double.IsNaN(poutLoDbm) || double.IsNaN(poutHiDbm)
                ? double.NaN : poutLoDbm + f * (poutHiDbm - poutLoDbm);

            var nearest = f <= 0.5 ? lo : hi;
            interpolated = new CompressionReadout(
                lo.PavlDbm + f * (hi.PavlDbm - lo.PavlDbm),
                poutAtComp,
                lo.GainDb  + f * (hi.GainDb  - lo.GainDb),
                lo.De      + f * (hi.De      - lo.De),
                lo.Pae     + f * (hi.Pae     - lo.Pae),
                lo.PdcW    + f * (hi.PdcW    - lo.PdcW),
                nearest, WasInterpolated: true);
        }

        // ── 4. ExactCompressionSolve — an OPTIONAL, single extra solve (R-h9r2-17a) ─────────────
        var (atStep, reading, extraSolves) = MaybeSolveExactly(ctx, terminations, s, interpolated, ref seed);
        solves += extraSolves;

        return new PinSearchResult(PinStopReason.Compression, solves)
        {
            Steps = steps, SmallSignalGainDb = gss, AtCompression = atStep, SweepCompression = reading,
        };
    }

    /// <summary>Pout in dBm — the domain the power-sweep panel actually plots, and R-h9r2-17a's own
    /// naming rule for which domain each interpolated quantity lives in.</summary>
    private static double PoutDbmOf(PinStep step) => step.PoutW > 0 ? 10 * Math.Log10(step.PoutW) + 30 : double.NaN;

    /// <summary>
    /// R-h9r2-17a's <c>ExactCompressionSolve</c> option. OFF (the default): the interpolated reading
    /// passes straight through, its own <see cref="CompressionReadout.Spectrum"/> — the nearest solved
    /// ladder point — is what the loadline and the intrinsic glyphs read. ON: one real HB solve runs
    /// at the interpolated Pin, and EVERYTHING — scalars and spectrum alike — comes from that one
    /// converged state, R-h9b-16's own principle applied here. A failed extra solve refuses back to
    /// the interpolated/nearest reading rather than losing the compression point entirely; it still
    /// counts as the one extra solve this option costs.
    /// </summary>
    private static (PinStep AtStep, CompressionReadout Reading, int ExtraSolves) MaybeSolveExactly(
        HarmonicaContext ctx, TerminationSet terminations, HarmonicaSettings s,
        CompressionReadout interpolated, ref Complex[,]? seed)
    {
        if (!s.ExactCompressionSolve)
            return (interpolated.Spectrum, interpolated, 0);

        var pt = ctx.Solve(terminations, interpolated.PinDbm, seed);
        if (!pt.Converged)
            return (interpolated.Spectrum, interpolated, 1);

        seed = pt.V;
        var step = Measure(ctx, pt, terminations);
        var exact = new CompressionReadout(step.PavlDbm, PoutDbmOf(step), step.GainDb, step.De, step.Pae,
                                           step.PdcW, step, WasInterpolated: false);
        return (step, exact, 1);
    }

    /// <summary>
    /// R-h9r2-17's exact ladder: <paramref name="start"/>, <paramref name="start"/>+<paramref
    /// name="step"/>, … up to and INCLUDING <paramref name="stop"/>. The final regular rung is
    /// excluded once it comes within 1e-9 of <paramref name="stop"/> and <paramref name="stop"/> is
    /// yielded explicitly instead — so a range that divides evenly is never double-counted, and a
    /// range that does not still reaches <paramref name="stop"/> exactly, with a short final interval.
    /// </summary>
    private static IEnumerable<double> Ladder(double start, double stop, double step)
    {
        if (step <= 0 || !double.IsFinite(start) || !double.IsFinite(stop) || start > stop) yield break;

        double pin = start;
        while (pin < stop - 1e-9)
        {
            yield return pin;
            pin += step;
        }
        yield return stop;
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
