using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>§3.3 item 3 (brief-harmonicarf-r3b) — how many of <see cref="Solves"/> were a cold
    /// DC-seeded RETRY after the warm-started attempt at that same Pin level failed to converge. Zero
    /// on the ordinary path; a counter, not a hidden cost, so the retry's own price stays visible.</summary>
    public int Retries { get; init; }

    /// <summary>brief-harmonicarf-r4 §3.3 — how many extra probes <see cref="PinSearch.Run"/> spent
    /// bisecting a coarse bracket to find the TRUE first compression crossing (§3.2). Zero on the
    /// ordinary path (a narrow, monotone bracket needs no extra probe at all); a counter, never a
    /// hidden cost, exactly like <see cref="Retries"/>. Always 0 for <see cref="PinSearch.Sweep"/>,
    /// whose ladder has no bracket to refine.</summary>
    public int BracketRefineProbes { get; init; }

    /// <summary>Round 11 §2 — how many ladder rungs <see cref="PinSearch.Sweep"/>'s continuity guard
    /// re-walked by bisection continuation because the rung's Pout moved further than its own Pin step
    /// did (<c>HarmonicaSettings.LadderContinuityMarginDb</c>). Zero on the ordinary path; a counter
    /// rather than a hidden cost, exactly like <see cref="Retries"/>. Always 0 for
    /// <see cref="PinSearch.Run"/>, which walks no ladder.</summary>
    public int Continuations { get; init; }

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

/// <summary>Which stage of <see cref="PinSearch.Run"/> a probed solve belongs to — brief-harmonicarf-r3b
/// §3.1's own diagnostic vocabulary, so a hole's cause can be named instead of just counted.</summary>
public enum PinSearchStage { Tickle, PinStart, Bracket, Secant }

/// <summary>One solve attempt inside <see cref="PinSearch.Run"/>, reported to an optional observer —
/// purely additive instrumentation (§3.1); it changes nothing about the search itself.</summary>
public readonly record struct PinSearchProbe(PinSearchStage Stage, double PinDbm, bool Converged);

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

    /// <summary>brief-harmonicarf-r4 §3.3 — the cost budget's own cap: at most this many extra probes
    /// bisecting a coarse bracket to find the true first crossing, so a pathological gain curve cannot
    /// turn one Γ point's search into the ladder it was built to avoid.</summary>
    public const int MaxBracketRefineProbes = 4;

    /// <summary>Round 11 §2 — see <see cref="DriveLadder.MaxContinuationDepth"/>, which this ladder
    /// and <c>LoadpullEngine</c>'s share.</summary>
    public const int MaxContinuationDepth = DriveLadder.MaxContinuationDepth;

    /// <summary>
    /// Round 11 §2 — whether <paramref name="to"/> can be the same solution branch as
    /// <paramref name="from"/>, one drive step later. <b>A thin adapter over
    /// <see cref="DriveLadder.IsDiscontinuous"/></b>, which is where the physics is written down and
    /// which <c>LoadpullEngine</c>'s own ladder reads too — two drive-ups that disagreed about what
    /// "the same branch" means would be worse than either rule on its own.
    /// </summary>
    public static bool IsDiscontinuous(PinStep from, PinStep to, double marginDb)
        => DriveLadder.IsDiscontinuous(from.PavlDbm, PoutDbmOf(from),
                                       to.PavlDbm,   PoutDbmOf(to), marginDb);

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
    /// <param name="neighborSteps">
    /// §3.3 item 2 (brief-harmonicarf-r3b) — the VSWR-nearest neighbour's WHOLE converged ladder
    /// (every Pin level it solved, not just its most-compressed step). When supplied, EVERY solve in
    /// this search — including the very first — prefers whichever is closer in Pin: the neighbour's
    /// own step nearest the level being solved, or this point's own in-ladder predecessor. Measured to
    /// be exactly where the shipped default's holes come from: the bracket's first, hint-driven probe
    /// otherwise jumps from <c>PinStart</c> (a low-drive spectrum) straight to a neighbour's
    /// compression Pin — potentially 30+ dB in one step, seeded with the wrong end of that gap.
    /// </param>
    public static PinSearchResult Run(
        HarmonicaContext ctx, TerminationSet terminations, Complex[,]? warmStart = null,
        double? pinHintDbm = null, Action<PinSearchProbe>? onProbe = null,
        IReadOnlyList<PinStep>? neighborSteps = null)
    {
        var s = ctx.Model.Settings;
        var steps  = new List<PinStep>();
        int solves = 0;

        Complex[,]? seed = warmStart;
        double lastPinDbm = double.NaN;

        Complex[,]? SeedFor(double pavlDbm)
        {
            Complex[,]? best = seed;
            double bestD = double.IsNaN(lastPinDbm) ? double.MaxValue : Math.Abs(lastPinDbm - pavlDbm);
            if (neighborSteps is { Count: > 0 })
            {
                foreach (var st in neighborSteps)
                {
                    double d = Math.Abs(st.PavlDbm - pavlDbm);
                    if (d < bestD) { bestD = d; best = st.Point.V; }
                }
            }
            return best;
        }

        int retries = 0;
        int bracketRefineProbes = 0;

        PinStep? Solve(double pavlDbm, PinSearchStage stage)
        {
            solves++;
            var pt = ctx.Solve(terminations, pavlDbm, SeedFor(pavlDbm));
            onProbe?.Invoke(new PinSearchProbe(stage, pavlDbm, pt.Converged));

            if (!pt.Converged)
            {
                // §3.3 item 3 — one retry from the DC seed (ctx.Solve's own cold-seed path, a real
                // nonlinear operating point per §3.3 item 1) before declaring a hole. Costs one solve
                // on a path that was already failing; nothing on a path that converges normally.
                retries++;
                solves++;
                pt = ctx.Solve(terminations, pavlDbm, warmStart: null);
                onProbe?.Invoke(new PinSearchProbe(stage, pavlDbm, pt.Converged));
                if (!pt.Converged) return null;
            }

            seed = pt.V;
            lastPinDbm = pavlDbm;
            return Measure(ctx, pt, terminations);
        }

        // ── 1. the tickle (R-h9r2-18a — optional, and an ABSOLUTE level) ────────
        double? gss = null;
        double gMax = double.NegativeInfinity;
        if (s.TickleEnabled)
        {
            var tickle = Solve(s.TickleDbm, PinSearchStage.Tickle);
            if (tickle is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves) { Steps = steps, Retries = retries, BracketRefineProbes = bracketRefineProbes };
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

        var lo = Solve(s.PinStartDbm, PinSearchStage.PinStart);
        if (lo is null)
            return new PinSearchResult(PinStopReason.NonConvergence, solves)
                { Steps = steps, SmallSignalGainDb = gss, Retries = retries, BracketRefineProbes = bracketRefineProbes };
        if (!s.TickleEnabled) gMax = lo.GainDb;
        double cLo = CompressionAt(lo);
        steps.Add(lo with { Compression = cLo });

        if (cLo >= target - CompressionToleranceDb)
        {
            // Already at or past the target at the very first drive level. Nothing to search for.
            return new PinSearchResult(PinStopReason.Compression, solves)
            {
                Steps = steps, SmallSignalGainDb = gss, AtCompression = steps[^1], Retries = retries, BracketRefineProbes = bracketRefineProbes,
            };
        }

        // Bracket the target. Strides DOUBLE rather than staying uniform — a uniform 3 dB climb from
        // PinStart to PinMax is the batch engine's ladder wearing a different name, and it dominated
        // the solve count (12 of 14) before this was measured. A hint from a neighbouring Γ point,
        // when there is one, starts the climb beside the answer instead of at the bottom.
        //
        // brief-harmonicarf-r4 §3 — every probed (Pin, GainDb) pair is kept in `probed`, regardless of
        // the ORDER it was solved in. The doubling stride (or a hint's own big first jump) can land a
        // LATER probe at a much higher Pin than an EARLIER one; `gMax` above is a running maximum
        // accumulated in PROBE order, so deciding "is this the first crossing" from it directly is
        // wrong the moment probe order and Pin order diverge — exactly RESOLVED.md §3's own bracket-
        // stage trap. `FirstCrossing` below is a PURE function of the accumulated samples, sorted by
        // Pin, so the answer cannot depend on which order they happened to be solved in.
        double pinLo = lo.PavlDbm;
        PinStep? hi = null;

        // §3.3's own trigger, made precise: TRUE only when the DOUBLING STRIDE actually grew past its
        // first rung before `hi` was found — i.e. the coarse loop missed at least once and had to widen.
        // A hint's own first probe landing on/past the target (the ordinary, common case — most grid
        // points after the first warm-start from a close neighbour) leaves this FALSE even though the
        // literal span from PinStart to the hint can be tens of dB: that span was never SEARCHED by the
        // doubling stride at all, so there is no stride-granularity evidence to refine against, and
        // measured directly, bisecting into it anyway makes things WORSE (the serial/parallel grid
        // comparison's own worst-case PAE deviation went from 2.69 to 8.05 pts when this guard was
        // missing) rather than better — probes placed there cannot know what a hint deliberately
        // skipped, they just narrow around whatever they happen to find, which is not necessarily the
        // hint's OWN neighbour-informed answer.
        bool coarseDoubled = false;

        var probed = new List<PinStep> { lo with { Compression = cLo } };

        double stride = FirstStrideDb;
        double pin = pinHintDbm is { } hint
            ? Math.Clamp(hint, pinLo + 0.25, s.PinMaxDbm)
            : pinLo + stride;

        while (pin <= s.PinMaxDbm + 1e-9)
        {
            var step = Solve(pin, PinSearchStage.Bracket);
            if (step is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves)
                    { Steps = steps, SmallSignalGainDb = gss, Retries = retries, BracketRefineProbes = bracketRefineProbes };

            double c = CompressionAt(step);
            var withC = step with { Compression = c };
            steps.Add(withC);
            probed.Add(withC);

            if (c >= target) { hi = withC; coarseDoubled = stride > FirstStrideDb + 1e-9; break; }

            pinLo = pin; cLo = c;

            if (pin >= s.PinMaxDbm - 1e-9) break;      // PinMax itself has now been tried
            stride *= 2.0;
            pin = Math.Min(pin + stride, s.PinMaxDbm);
        }

        if (hi is null)
            // Never compressed before PinMax. R-hrf-8: this Γ point is a HOLE, thrown out of the grid
            // rather than extrapolated into.
            return new PinSearchResult(PinStopReason.PinMax, solves)
                { Steps = steps, SmallSignalGainDb = gss, Retries = retries, BracketRefineProbes = bracketRefineProbes };

        // §3.2 — the FIRST crossing, in ascending-Pin order, over EVERY sample probed so far. Exactly
        // Sweep()'s own running-gMax rule, applied to this search's (possibly sparse, non-uniform)
        // sample set instead of a dense uniform ladder. Guaranteed non-null: PinStart's own compression
        // is < target (the early-return above already excluded the opposite case) and `hi` — by
        // construction the highest-Pin sample probed so far — has compression >= target, so a
        // finite ascending walk from one to the other must cross somewhere.
        (PinStep Lo, double CLo, PinStep Hi, double CHi)? FirstCrossing()
        {
            var sorted = probed.OrderBy(p => p.PavlDbm).ToList();
            double runMax = s.TickleEnabled ? gss!.Value : double.NegativeInfinity;
            double prevC = double.NaN;
            PinStep? prevStep = null;
            foreach (var p in sorted)
            {
                if (p.GainDb > runMax) runMax = p.GainDb;
                double c = runMax - p.GainDb;
                if (prevStep is not null && prevC < target && c >= target)
                    return (prevStep, prevC, p, c);
                prevStep = p; prevC = c;
            }
            return null;
        }

        var crossing = FirstCrossing()
            ?? throw new InvalidOperationException("PinSearch.Run: no crossing found despite hi >= target — " +
                                                    "see FirstCrossing's own proof of non-nullity.");

        // §3.2's trap and §3.3's budget, together: a naive back-probe evaluated in PROBE order would
        // repeat the exact mistake this fix removes, so refinement always re-derives the crossing
        // through the SAME pure function above. Bounded to a small, COUNTED number of extra probes
        // (never silent, `Retries`' own precedent) and only spent when the sample history actually
        // gives a reason to: the doubling stride grew past its first rung, or a non-monotone gain
        // sequence (gain expansion — the exact shape that let a later, spurious crossing masquerade as
        // the first one before this fix).
        //
        // <b>Restricted to an UNHINTED search — measured, not assumed.</b> Extending refinement to a
        // HINTED search (every grid point after the first, warm-started from a converged neighbour)
        // regressed `ContourGridParallelTests`' own serial-vs-parallel gate from 2.69 to 3.75 pts PAE
        // worst-case, on a DIFFERENT set of points than the one this fix targets. Root cause: on this
        // fixture's gain-expansion device, the true peak gain can sit anywhere on the Γ plane, and a
        // hint from a DIFFERENT neighbour's own compression Pin can define a coarse bracket that never
        // samples that peak at all — refinement then converges CONFIDENTLY (stable across a wide range
        // of probe caps, not merely under-iterated) to a crossing measured against an
        // under-established `gMax`, and since serial and parallel grids hint from different neighbour
        // pools for the same Γ, they converge confidently to two DIFFERENT wrong answers instead of one
        // secant's shared, smoother approximation. That is a real, pre-existing limitation of `gMax`
        // establishment under a hint — not the doubling-stride sampling defect §3 scopes — and is
        // NOT fixed here; disabling refinement outright reproduces the original 2.69 pts baseline
        // almost exactly (2.687, measured), which is what pins the regression to refinement-under-a-hint
        // specifically. The unhinted path (the grid's own single deterministic "leader" point, and any
        // direct <see cref="Run"/> call with no hint) has no such neighbour to disagree with and is
        // exactly brief-harmonicarf-r4 §3's own named reproduction case (28.4 vs 27.2 dBm).
        while (pinHintDbm is null && bracketRefineProbes < MaxBracketRefineProbes)
        {
            var (a, _, b, _) = crossing;
            double width = b.PavlDbm - a.PavlDbm;

            bool nonMonotone = false;
            var sortedNow = probed.OrderBy(p => p.PavlDbm).ToList();
            for (int i = 1; i < sortedNow.Count; i++)
                if (sortedNow[i].GainDb > sortedNow[i - 1].GainDb + 1e-9) { nonMonotone = true; break; }

            if (!coarseDoubled && !nonMonotone) break;     // no reason to spend a probe — see above
            if (width <= FirstStrideDb + 1e-9 && !nonMonotone) break;

            double mid = 0.5 * (a.PavlDbm + b.PavlDbm);
            var midStep = Solve(mid, PinSearchStage.Bracket);
            bracketRefineProbes++;
            if (midStep is null) break;    // a failed refinement probe just stops refining — the
                                            // coarse bracket already found is still a valid answer.

            double c = CompressionAt(midStep);
            var withC = midStep with { Compression = c };
            steps.Add(withC);
            probed.Add(withC);

            var next = FirstCrossing();
            if (next is null) break;       // cannot happen per the proof above; never spin regardless.
            crossing = next.Value;
        }

        var (finalLo, finalCLo, finalHi, finalCHi) = crossing;
        pinLo = finalLo.PavlDbm;
        double pinHi = finalHi.PavlDbm;
        cLo = finalCLo;
        double cHi = finalCHi;

        // The secant below re-uses the closure's running `gMax` — re-seed it to what the PURE
        // evaluation actually used at the crossing's high end, so a probe-order artifact from a hint's
        // own big jump (or from the refinement probes above) cannot leak into the secant's own
        // CompressionAt calls. Every secant probe lands INSIDE [pinLo, pinHi], at or below `finalHi`'s
        // Pin, so this is the correct — and only — max the secant should be comparing against.
        gMax = s.TickleEnabled
            ? Math.Max(gss!.Value, probed.Where(p => p.PavlDbm <= pinHi + 1e-9).Max(p => p.GainDb))
            : probed.Where(p => p.PavlDbm <= pinHi + 1e-9).Max(p => p.GainDb);

        // ── 3. secant on (Pin, compression) toward the target ─────────────────
        PinStep best = finalHi with { Compression = cHi };
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

            var step = Solve(next, PinSearchStage.Secant);
            if (step is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves)
                    { Steps = steps, SmallSignalGainDb = gss, Retries = retries, BracketRefineProbes = bracketRefineProbes };

            double c = CompressionAt(step);
            var    withC = step with { Compression = c };
            steps.Add(withC);

            if (Math.Abs(c - target) < bestErr) { best = withC; bestErr = Math.Abs(c - target); }

            if (c < target) { pinLo = next; cLo = c; }
            else            { pinHi = next; cHi = c; }
        }

        return new PinSearchResult(PinStopReason.Compression, solves)
        {
            Steps = steps, SmallSignalGainDb = gss, AtCompression = best, Retries = retries, BracketRefineProbes = bracketRefineProbes,
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
    /// <para><b>Inclusive at both ends, UNLESS the ladder crosses compression</b> (R-h9r2-17, revised
    /// by brief-harmonicarf-r4 §1): a sweep that never reaches <c>CompressionDb</c> still solves every
    /// rung up to and including <paramref name="stopDbm"/>, so the user always sees the full range on a
    /// device that does not compress. A sweep that DOES cross stops once compression reaches
    /// <c>CompressionDb + HarmonicaSettings.SweepOverdriveDb</c> — R-h9r2-19's original "never stop
    /// early" is superseded for this case only; see <c>HarmonicaSettings.SweepOverdriveDb</c>'s own
    /// remarks for why (the rule was right at a 30 dBm <c>PinMaxDbm</c> ceiling and became visibly
    /// wrong once it moved to 50).</para>
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
        int continuations = 0;
        int retries = 0;

        Complex[,]? seed = warmStart;

        // One solve at an EXPLICIT warm start, leaving `seed` on its converged spectrum. The
        // continuation below needs this: a sub-step at 11 dBm has no prior-frame entry and must
        // continue from the rung below it, not from whatever `seed` happens to hold.
        PinStep? SolveFrom(double pavlDbm, Complex[,]? warm)
        {
            solves++;
            var pt = ctx.Solve(terminations, pavlDbm, warm);
            if (!pt.Converged) return null;
            seed = pt.V;
            return Measure(ctx, pt, terminations);
        }

        PinStep? Solve(double pavlDbm)
        {
            // A prior-level spectrum only WINS over the ladder's own running seed when this context
            // can actually use it. Round 11 §1: a prior frame's spectrum solved at a different
            // harmonic order has the same LEVEL keys and a different SHAPE, so the lookup hits, the
            // stale array is handed to ctx.Solve, ctx.Solve silently falls back to the cold DC seed —
            // and the ladder loses its rung-to-rung warm start for EVERY rung, not just the first.
            // Measured on the shipped default under Class F: that is a sweep truncating at 12 dBm on a
            // non-convergent rung where the warm-started identical circuit reaches 26 dBm.
            Complex[,]? levelSeed = priorLevelSpectra is not null &&
                                    priorLevelSpectra.TryGetValue(Math.Round(pavlDbm, 6), out var prior) &&
                                    ctx.AcceptsWarmStart(prior)
                ? prior : seed;
            return SolveFrom(pavlDbm, levelSeed);
        }

        // Round 11 §2 — re-walks ONE ladder step by bisection continuation. The walk itself is
        // DriveLadder.ContinueThroughJump, shared with LoadpullEngine's own ladder; all this adds is
        // this ladder's own types and its `seed`, which must end on whichever branch was accepted.
        PinStep? ContinueThroughJump(PinStep from, double toDbm)
        {
            var accepted = DriveLadder.ContinueThroughJump(
                from.PavlDbm, PoutDbmOf(from), from.Point.V, toDbm,
                SolveFrom, PoutDbmOf, step => step.Point.V, s.LadderContinuityMarginDb);

            if (accepted is not null) seed = accepted.Point.V;
            return accepted;
        }

        // ── 1. the tickle (R-h9r2-18a) ──────────────────────────────────────────
        double? gss = null;
        double gMax = double.NegativeInfinity;
        if (s.TickleEnabled)
        {
            var tickle = Solve(s.TickleDbm);
            if (tickle is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves)
                    { Steps = steps, Retries = retries, Continuations = continuations };
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
        // brief-harmonicarf-r4 §1 — the OVERDRIVE-inclusive stop target. Always >= target, so the
        // crossing above (which fires the moment c first reaches `target`) is guaranteed to have
        // already been recorded by the time this one can fire — "the early stop happens strictly
        // after the crossing pair has been recorded" holds by construction, not by ordering the checks
        // carefully. A margin of 0 makes the two targets identical, so the ladder stops on the very
        // rung that crosses.
        double overdriveTarget = target + Math.Max(0.0, s.SweepOverdriveDb);
        int cross = -1;

        // ── 2. the ladder, inclusive at both ends (R-h9r2-19, revised by brief-harmonicarf-r4 §1) ──
        // R-h9r2-19 originally forbade stopping early at all. That was right when PinMaxDbm was 30 dBm
        // (a few dB of overdrive past P3dB) and became wrong at 50 (~20 dB of overdrive, ~44 wasted
        // solves on the shipped default) — see CLAUDE.md's own dated note. A sweep that never crosses
        // the target is UNCHANGED: it still runs the full ladder, so the user sees the whole range and
        // can tell the device never compressed. Only a sweep that DOES cross stops early, and only
        // once it has gone at least as far PAST the crossing as SweepOverdriveDb asks for.
        foreach (double pin in Ladder(startDbm, stopDbm, stepDbm))
        {
            var step = Solve(pin);

            // Round 11 §2 — ONE guard for the ladder's two ways of taking too big a drive step, because
            // they are the same defect wearing two faces: the Newton either fails outright, or succeeds
            // onto a DIFFERENT root (a rung whose Pout moved further than its own Pin step did — see
            // HarmonicaSettings.LadderContinuityMarginDb for the measurement). Both are answered by
            // re-walking that one step as a continuation from the previous rung's converged spectrum
            // instead of taking it in a single leap.
            if (steps.Count > 0 && (step is null || IsDiscontinuous(steps[^1], step, s.LadderContinuityMarginDb)))
            {
                var refined = ContinueThroughJump(steps[^1], pin);
                if (refined is not null) { step = refined; continuations++; }
                else if (step is null)
                {
                    // Last resort, and the SAME one Run() already makes: the cold DC seed. A step this
                    // far from its predecessor may simply not be reachable by continuation at all.
                    step = SolveFrom(pin, null);
                    if (step is not null) retries++;
                }
            }

            if (step is null)
                return new PinSearchResult(PinStopReason.NonConvergence, solves)
                    { Steps = steps, SmallSignalGainDb = gss, Retries = retries, Continuations = continuations };

            // Whatever path produced the accepted answer, the ladder continues from ITS branch — never
            // from a rejected root, and never from a mid-continuation probe the refinement abandoned.
            seed = step.Point.V;

            if (!s.TickleEnabled && steps.Count == 0) gMax = step.GainDb;
            double c = CompressionAt(step);
            steps.Add(step with { Compression = c });

            if (cross < 0 && steps.Count > 1 && steps[^2].Compression < target && c >= target)
                cross = steps.Count - 2;

            bool alreadyAtTarget = cross >= 0 || (steps.Count == 1 && c >= target);
            if (alreadyAtTarget && c >= overdriveTarget)
                break;
        }

        if (steps.Count == 0)
            return new PinSearchResult(PinStopReason.PinMax, solves)
                { Steps = steps, SmallSignalGainDb = gss, Retries = retries, Continuations = continuations };

        CompressionReadout interpolated;
        if (cross < 0)
        {
            if (steps[0].Compression < target)
                // Never compressed before Stop. A NORMAL outcome (R-h9r2-17), not an error — the
                // panel's own "did not compress" note is what draws this.
                return new PinSearchResult(PinStopReason.PinMax, solves)
                    { Steps = steps, SmallSignalGainDb = gss, Retries = retries, Continuations = continuations };

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
            Retries = retries, Continuations = continuations,
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
