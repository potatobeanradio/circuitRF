// ANT-9 / M1 — FINDING A RESONANCE THAT FALLS BETWEEN THE POINTS THAT WERE ASKED FOR.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHY THIS EXISTS, AND WHAT IT IS ALLOWED TO BREAK
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// L9e gave `PlanarAdaptiveSweep` one property above all others: IT NEVER ADDS A FREQUENCY THE USER
// DID NOT ASK FOR. That is what makes every published point the user's own, and it is enforced
// structurally rather than by a check — the refinement bisects INDICES of the requested grid, so a
// frequency off the grid is not merely rejected, it is unrepresentable.
//
// The measurement that produced this phase (ANT-9 §1) is where that property costs something. On an
// imported patch board, 1.50-2.00 GHz in 51 points, the sampler solved 44 of 51 — 86 % of the grid,
// none of the intended saving — and STILL stopped at |ΔS| = 0.02 against a tolerance of 1e-3. The
// resonance is narrower than the 10 MHz step. No amount of refinement on that grid can see it,
// because every point refinement is allowed to reach is already solved. The answer the user wants —
// "where IS it" — is not expressible in the grid they happened to type.
//
// So this file is the OPT-IN under which the never-add property is narrowed, and nothing else about
// the sweep changes:
//
//   * `PlanarAdaptiveSettings.Search` is NULL BY DEFAULT. With it null, not one line here runs and
//     the sweep is bit-identical to L9e's — the same frequencies, the same matrices, the same notes.
//     That is R-adf-1's rule applied one level down: the general capability is built ALONGSIDE the
//     shipped one and gated against it, never on top of it.
//   * With it set, added frequencies are PUBLISHED ALONGSIDE the requested grid and every one of
//     them is FLAGGED (`PlanarFrequencyPoint.AddedBySearch`). A found point that a user cannot
//     tell apart from one they asked for would be the never-add property broken quietly, which is
//     worse than not having the mode at all.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// THE CRITERION IS UNTOUCHED. THIS FILE ONLY DECIDES WHERE TO LOOK
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// `PlanarAdaptiveSweep`'s header is emphatic and this phase does not weaken it by a word: the
// stopping test is an ERROR — a freshly SOLVED point against what the interpolant PREDICTED there —
// never a fit residual. This repository has measured why twice (L7b-b's `ModeCouplingResidual` is
// anti-correlated with the terminal error; L8a's `FitResidual` picks one of the worst
// configurations), and a search that quietly moved the criterion onto "how well does the model fit
// its own nodes" would be that mistake a third time.
//
// What is added is SEEDING. A resonance is where Im(Z_in) crosses zero, which costs NO SOLVE to
// look for: the interpolant the sampler already built is evaluated densely and its sign changes are
// read off. The crossing decides where to probe. The criterion still decides when to stop.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// Q IS ONE FORMULA, NOT TWO, AND THE SIGN IS THE LABEL
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// At a crossing Z is real, Z = R. Textbook Q is written differently for the two resonance types:
//
//     series    Z = R + jX          Q = ω₀ (dX/dω) / 2R          X rises through zero
//     parallel  Y = G + jB          Q = ω₀ (dB/dω) / 2G          B rises, so X FALLS through zero
//
// They are the same expression. At the crossing Y = 1/R exactly, so G = 1/R, and differentiating
// B = −X/(R²+X²) at X = 0 gives dB/dω = −(1/R²)·dX/dω. Substituting turns the parallel form into
// −ω₀(dX/dω)/2R — the series form with the sign the falling slope supplies. So:
//
//     **Q = ω₀ |dX/dω| / 2R = f₀ |dX/df| / 2R**   in BOTH cases,
//
// and the SIGN of dX/df is not part of the magnitude at all — it is the LABEL, series or parallel.
// One formula, no branch, and the branch that would have existed is exactly the thing most likely
// to be written backwards.
//
// For a one-port this Q is the resonator's OWN Q — radiation plus loss, everything inside Z_in —
// and NOT the loaded Q of the matched system, because a source impedance is not in Z_in. An antenna
// user wants precisely this number; it is the one that sets the achievable bandwidth before any
// matching network is designed. It is stated in those words in the note rather than left to be
// guessed from the word "Q".
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHY THE ANSWER IS READ OFF SOLVED POINTS AND NEVER OFF THE INTERPOLANT
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// The interpolant proposes; solved points dispose. f₀ is the root between the two SOLVED bracket
// endpoints, and dX/df is that bracket's own secant. The interpolant is used to pick the next probe
// and to detect candidates — nowhere does a reported number come from it. A reported f₀ that was
// really a spline's opinion about a region it had no samples in is the failure mode this whole
// phase exists to avoid, and it would be invisible: a plausible number, to four figures, wrong.
//
// This is also what makes `LocatedToHz` meaningful. It is the width of the final SOLVED bracket —
// an interval the root provably lies inside because its ends have opposite signs — not an estimate
// of convergence. A caller can compare it against the resonance's own bandwidth and see at once
// whether the answer is sharp enough to be worth quoting.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// TERMINATION, WHICH IS THE PART A SEARCH GETS WRONG
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// Three independent bounds, because a solve here costs 48-72 s and a mode that can run away is a
// mode nobody dares turn on:
//
//   1. `MaxAddedPoints` — a hard ceiling on added solves, reported WHENEVER IT BINDS. Reaching it
//      is not a failure, it is a budget; the note says which resonances were located before it
//      bound so the partial answer is usable.
//   2. The bracket shrinks geometrically. The probe is the interpolant's own root estimate, which
//      converges in two or three steps on anything smooth — but a regula-falsi probe can stagnate
//      against one endpoint forever, so ANY step that fails to halve the bracket forces the NEXT
//      probe to be the plain midpoint. Worst case is therefore bisection at twice the step count,
//      never an unbounded run.
//   3. A candidate the solved points refuse to confirm is DROPPED after one probe, and counted. A
//      matched port has Im(Z_in) ≈ 0 everywhere and the interpolant will wander across zero in it
//      repeatedly; every one of those is a crossing and none of them is a resonance. `MinQ` is what
//      separates them — a crossing that shallow implies Q ≈ 0 — and the count of what was discarded
//      is reported rather than hidden, because "I looked and found nothing" and "I found nine
//      things and threw them all away" are different answers.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>Which way Im(Z_in) went through zero — the label the Q magnitude does not carry.</summary>
public enum PlanarResonanceKind
{
    /// <summary>Reactance RISING through zero: Z is small and real at f₀, current is maximal.</summary>
    Series,

    /// <summary>Reactance FALLING through zero: Z peaks real at f₀. An edge-fed patch is this one.
    /// </summary>
    Parallel,
}

/// <param name="MaxAddedPoints">
/// <b>The hard ceiling on frequencies this search may add</b>, and the reason the mode is safe to
/// turn on. A de-embedded full-wave point is 48 s on one level and 71.9 s on two (L8d/L9d), so 24 is
/// already 20-30 minutes of added solving; it is a budget the caller sets, not a target to reach.
/// It is reported whenever it binds.
/// </param>
/// <param name="FrequencyTolerance">
/// How tightly f₀ is bracketed, RELATIVE to f₀ — so it means the same thing at 2 GHz and 40 GHz,
/// which an absolute figure would not. Each halving is one solve and the default 1e-4 is ~6 of them
/// from a 10 MHz starting bracket at 1.8 GHz.
/// </param>
/// <param name="MinQ">
/// <b>The guard that separates a resonance from a matched port.</b> Im(Z_in) of a well-matched line
/// hovers at zero and the interpolant crosses it wherever numerical wander takes it; every such
/// crossing is real and none is a resonance. A crossing that shallow implies Q ≈ 0, so one number
/// rejects them all. Crossings discarded this way are COUNTED and reported.
/// </param>
/// <param name="PortNumber">
/// Which port's input impedance the search reads, 1-based so it means what the s-parameter matrix
/// means. One port, because Im(Z_in) of two different ports crosses zero in two different places
/// and a search that merged them would report the union as if it were a mode list.
/// </param>
/// <param name="MatchCriterionDb">
/// The return-loss level the reported matched bandwidth is measured at. −10 dB is the antenna
/// convention (VSWR 2:1 is −9.54 dB). It is a SETTING because a resonator that is deliberately
/// undercoupled has no −10 dB band at all and the useful question there is a different level.
/// </param>
public sealed record PlanarResonanceSettings(
    int    MaxAddedPoints     = 24,
    double FrequencyTolerance = 1e-4,
    double MinQ               = 1.0,
    int    PortNumber         = 1,
    double MatchCriterionDb   = -10.0)
{
    public static readonly PlanarResonanceSettings Default = new();
}

/// <summary>
/// One located resonance. <b>Every field here was read off SOLVED points</b> — see this file's
/// header for why none of them may come from the interpolant.
/// </summary>
/// <param name="FrequencyHz">f₀ — the root of Im(Z_in) between the final bracket's solved ends.</param>
/// <param name="LocatedToHz">
/// The width of that final bracket. The root provably lies inside it, so this is an INTERVAL rather
/// than an estimate of one. Compare it against <paramref name="HalfPowerBandwidthHz"/> to see
/// whether f₀ is quoted to a useful precision.
/// </param>
/// <param name="Q">f₀·|dX/df| / 2R — the resonator's own Q at this port, radiation and loss
/// included, NOT the loaded Q of a matched system.</param>
/// <param name="ResistanceOhm">Re(Z_in) at f₀, which at a crossing is the whole of Z_in.</param>
/// <param name="HalfPowerBandwidthHz">f₀/Q — what the slope implies, not a measured −3 dB width.</param>
/// <param name="ReturnLossDb">20·log₁₀|S_pp| at f₀. This is what says whether the resonance is
/// MATCHED; a patch can resonate hard at 200 Ω and never reach −10 dB into 50.</param>
/// <param name="MatchedBandwidthHz">
/// The measured width over which |S_pp| stays below the criterion, or NaN with
/// <paramref name="MatchedBandwidthRefusal"/> saying why there is none.
/// </param>
/// <param name="SolvesSpent">Added solves this resonance cost, so a bound cap can be apportioned.</param>
public sealed record PlanarResonance(
    double              FrequencyHz,
    double              LocatedToHz,
    PlanarResonanceKind Kind,
    double              Q,
    double              ResistanceOhm,
    double              ReactanceSlopeOhmPerHz,
    double              HalfPowerBandwidthHz,
    double              ReturnLossDb,
    double              MatchedBandwidthHz,
    double              MatchedBandwidthLoHz,
    double              MatchedBandwidthHiHz,
    string?             MatchedBandwidthRefusal,
    int                 SolvesSpent);

/// <summary>The solved set as it stands: every frequency now known, ascending, with its matrix.</summary>
public sealed record PlanarResonanceNodes(
    IReadOnlyList<double> Frequencies, IReadOnlyList<Mat<Complex>> Values);

/// <summary>
/// Solve at one frequency and hand back the WHOLE node set as it now stands.
///
/// <para><b>This delegate is the reason the search is testable in milliseconds.</b> In
/// <c>PlanarSolve.Run</c> it is a full fill-factor-excite-de-embed cycle costing a minute; in a test
/// it is an analytic resonator evaluated in nanoseconds. The search cannot tell the difference, and
/// that is exactly the property ANT-9 §5 asks for — the SEARCH is gated on a response whose f₀ and Q
/// are known in closed form, not on a solved structure whose true answer nobody has.</para>
/// </summary>
public delegate PlanarResonanceNodes PlanarResonanceProbe(double frequencyHz);

/// <summary>
/// The pure half of ANT-9: where to look for a resonance, how to bracket it, and what to report.
/// No solve, no kernel, no calibrator — the same rule <see cref="PlanarAdaptiveSweep"/> follows.
/// </summary>
public static class PlanarResonanceSearch
{
    /// <summary>
    /// Z_in at one port with every other port terminated in its own reference impedance — which is
    /// what S_pp already describes, so this is a change of variable and not a new claim.
    /// </summary>
    public static Complex InputImpedance(Mat<Complex> s, Complex z0, int portIndex)
    {
        // `Mat<Complex>` is a struct — there is nothing to null-check, which is why the guard other
        // entry points here carry is absent rather than forgotten.
        var spp = s[portIndex, portIndex];
        var den = Complex.One - spp;
        // |S_pp| = 1 with zero phase is an open circuit: infinite, not a number. Report it as a huge
        // positive reactance rather than throwing, so one degenerate point cannot fail a sweep.
        if (den.Magnitude < 1e-300) return new Complex(double.PositiveInfinity, double.PositiveInfinity);
        return z0 * (Complex.One + spp) / den;
    }

    /// <summary>
    /// <b>The seeding step, and it costs no solve (§3).</b> Sample the interpolant the sampler
    /// already built and read off every sign change of Im(Z_in) inside the span.
    ///
    /// <para><paramref name="perInterval"/> samples per node interval. A strict sign test —
    /// <c>&gt; 0</c> against <c>&lt; 0</c> — so an identically-zero reactance (a perfectly matched
    /// port) produces no candidate at all rather than one per sample.</para>
    /// </summary>
    public static double[] CandidateCrossings(
        PlanarResonanceNodes nodes, double spanLoHz, double spanHiHz, Complex z0, int portIndex,
        PlanarInterpolant interpolant, int perInterval = 8)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Frequencies.Count < 2) return [];

        var series = EntrySeries(nodes, portIndex);
        var f = nodes.Frequencies;

        int n = Math.Max(2, (f.Count - 1) * Math.Max(1, perInterval) + 1);
        var found = new List<double>();

        double prevF = double.NaN, prevX = double.NaN;
        for (int k = 0; k < n; k++)
        {
            double at = spanLoHz + (spanHiHz - spanLoHz) * k / (n - 1.0);
            double x  = ReactanceAt(f, series, z0, at, interpolant);
            if (k > 0 && Straddles(prevX, x))
                found.Add(LinearRoot(prevF, prevX, at, x));
            prevF = at;
            prevX = x;
        }
        return [.. found];
    }

    /// <summary>
    /// <b>The whole search.</b> Locate every resonance in the span, then spend what is left of the
    /// budget resolving the curve around them on the sampler's OWN |ΔS| criterion.
    ///
    /// <para>The two stages are deliberately in that order and not interleaved. Locating is what the
    /// mode exists for — "where is it" must come back as a number — so it takes the budget first and
    /// resolving takes the remainder. A cap that bound during locating leaves a partial list of
    /// resonances; a cap that bound during resolving leaves the full list with a coarser curve
    /// between the points. The second is a far better place to run out, and this ordering is what
    /// guarantees it is the one that happens.</para>
    /// </summary>
    public static PlanarResonanceOutcome Search(
        PlanarResonanceNodes start, double spanLoHz, double spanHiHz, Complex z0,
        PlanarInterpolant interpolant, double sTolerance,
        PlanarResonanceSettings st, PlanarResonanceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(st);
        ArgumentNullException.ThrowIfNull(probe);

        int port = Math.Max(0, st.PortNumber - 1);
        if (start.Frequencies.Count < 2 || start.Values[0].RowCount <= port)
            return new PlanarResonanceOutcome([], [], 0, false, false,
                "The resonance search needs at least two solved points and a port to read them at.");

        var nodes   = start;
        var added   = new List<double>();
        var results = new List<PlanarResonance>();
        int budget  = Math.Max(0, st.MaxAddedPoints);
        int dropped = 0;
        bool capBound = false;

        // ── Stage A: locate. One pass over the candidates the FIRST interpolant offers, refreshed
        //    after each located resonance so a point added for one helps find the next.
        var done = new List<double>();
        while (true)
        {
            double? next = null;
            foreach (double c in CandidateCrossings(nodes, spanLoHz, spanHiHz, z0, port, interpolant))
            {
                // Already accounted for? A located resonance and a dropped candidate both count —
                // "near" is one located-to width, or the grid's own finest spacing if that is wider.
                if (done.Any(d => Math.Abs(d - c) <= Separation(nodes.Frequencies, c))) continue;
                next = c;
                break;
            }
            if (next is null) break;
            if (added.Count >= budget) { capBound = true; break; }

            var located = Locate(nodes, next.Value, z0, port, interpolant, st, budget - added.Count, probe);
            nodes = located.Nodes;
            added.AddRange(located.Added);
            done.Add(located.At);
            if (located.CapBound) capBound = true;

            if (located.Resonance is { } r && r.Q >= st.MinQ) results.Add(r);
            else dropped++;

            if (located.CapBound) break;
        }

        results.Sort((a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));

        // ── Stage B: resolve. The sampler's own criterion, on the intervals around each resonance.
        foreach (var r in results)
        {
            if (added.Count >= budget) { capBound = true; break; }
            double half = double.IsFinite(r.HalfPowerBandwidthHz) && r.HalfPowerBandwidthHz > 0
                ? r.HalfPowerBandwidthHz : (spanHiHz - spanLoHz) / 100;
            var (n2, a2, bound) = Resolve(
                nodes, Math.Max(spanLoHz, r.FrequencyHz - 1.5 * half),
                Math.Min(spanHiHz, r.FrequencyHz + 1.5 * half),
                interpolant, sTolerance, budget - added.Count,
                // THE FLOOR, and without it this stage is unbounded. Phase 1's refinement stops
                // because it runs out of GRID — there is always a point it cannot bisect past. Here
                // there is no grid, so subdivision continues until the cap takes it, every time, on
                // every sharp resonance, and the cap then reads as "binding" on a run that had in
                // fact converged.
                //
                // The floor is a quarter of the resonance's OWN half-power bandwidth rather than a
                // frequency tolerance, because what this stage is for is the SHAPE of the curve: a
                // resonance drawn with four points across its own bandwidth is drawn, and one drawn
                // with four hundred is the same curve at a hundred times the price. Scaling it to f₀
                // instead would spend the whole budget on a high-Q feature and almost nothing on a
                // broad one, which is backwards.
                //
                // A half-bandwidth floor over a +/-1.5 bandwidth window is at most SIX intervals, so
                // this stage cannot swallow the cap and leave "cap bound" meaning nothing. It can
                // afford to be that thrifty because stage A has already paid for dense coverage of
                // the core: bisection leaves every probe it took inside the original bracket,
                // clustering geometrically on f0. What stage B adds is the FLANKS.
                Math.Max(half / 2, r.FrequencyHz * st.FrequencyTolerance),
                probe);
            nodes = n2;
            added.AddRange(a2);
            if (bound) { capBound = true; break; }
        }

        added.Sort();
        return new PlanarResonanceOutcome(results, added, dropped, capBound, true,
                                          Describe(results, added.Count, dropped, capBound, st,
                                                   spanLoHz, spanHiHz));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Stage A — bracket one candidate and characterise it.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private sealed record Located(
        PlanarResonanceNodes Nodes, IReadOnlyList<double> Added, double At,
        PlanarResonance? Resonance, bool CapBound);

    private static Located Locate(
        PlanarResonanceNodes nodes, double candidate, Complex z0, int port,
        PlanarInterpolant interpolant, PlanarResonanceSettings st, int budget,
        PlanarResonanceProbe probe)
    {
        var added = new List<double>();

        // The two solved nodes the candidate sits between. If their reactances already straddle
        // zero the bracket is free; if they do not, the interpolant claimed a crossing its own
        // samples do not support, and ONE probe at the candidate is what settles that.
        var (lo, hi) = Surround(nodes.Frequencies, candidate);
        if (lo < 0) return new Located(nodes, added, candidate, null, false);

        double xLo = Reactance(nodes.Values[lo], z0, port);
        double xHi = Reactance(nodes.Values[hi], z0, port);
        double fLo = nodes.Frequencies[lo], fHi = nodes.Frequencies[hi];

        if (!Straddles(xLo, xHi))
        {
            if (budget <= 0) return new Located(nodes, added, candidate, null, true);
            nodes = probe(candidate);
            added.Add(candidate);
            double xc = ReactanceOf(nodes, candidate, z0, port);

            if (Straddles(xLo, xc))      { fHi = candidate; xHi = xc; }
            else if (Straddles(xc, xHi)) { fLo = candidate; xLo = xc; }
            else
                // Confirmed artefact: three solved points, no sign change between any adjacent pair.
                return new Located(nodes, added, candidate, null, false);
        }

        // ── Bisect. PLAIN MIDPOINT, and the choice is measured rather than stylistic.
        //
        // Probing at the interpolant's own root estimate — regula falsi, which is what §3's "bisect
        // toward the crossing" invites and what this was written as first — is WRONG HERE, for a
        // reason specific to a resonance. X(f) through a resonance is very nearly linear, so the
        // first regula-falsi probe lands on the root immediately and to many digits. That sounds
        // ideal and is not: it moves ONE bracket end onto the root and leaves the other where it
        // started, so THE BRACKET NEVER SHRINKS. Measured on the analytic Q = 214 case, that cost
        // two probes per halving (the safeguard midpoint had to do all the actual work) and burned
        // the entire 24-point cap — and, worse, left the final bracket 60 MHz wide, whose secant is
        // not dX/df at f₀ at all: it reported **Q = 281 against a true 214, 31 % high**, while f₀
        // itself was exact. A wrong Q beside a right f₀ is the most credible-looking wrong answer
        // this phase could produce.
        //
        // A midpoint halves the bracket every single time. The cost is exactly
        // ceil(log2(width / tolerance)) probes — 6 from a 10 MHz grid at the default 1e-4 — which is
        // both predictable and cheaper than the safeguarded version it replaces, and it ends on a
        // bracket tight enough that its secant IS the local derivative and its endpoints ARE a
        // proven interval around the root. The interpolant still chooses which interval to open in;
        // it just no longer chooses where inside it to land.
        double want = st.FrequencyTolerance * Math.Max(1.0, Math.Abs(candidate));
        while (fHi - fLo > want)
        {
            if (added.Count >= budget)
                return new Located(nodes, added, 0.5 * (fLo + fHi),
                                   Characterise(nodes, fLo, xLo, fHi, xHi, z0, port, interpolant, st,
                                                added.Count), true);

            double at = 0.5 * (fLo + fHi);
            if (at <= fLo || at >= fHi) break;      // the bracket is at the floor of double

            nodes = probe(at);
            added.Add(at);
            double x = ReactanceOf(nodes, at, z0, port);

            // Exactly zero is measure-zero but must not collapse the bracket to nothing, which
            // would lose the resonance entirely on the one input that pins it perfectly.
            if (x == 0) { fHi = at; xHi = 0; break; }
            if (Straddles(xLo, x)) { fHi = at; xHi = x; }
            else                   { fLo = at; xLo = x; }
        }

        double f0 = fHi > fLo ? LinearRoot(fLo, xLo, fHi, xHi) : fLo;
        return new Located(nodes, added, f0,
                           Characterise(nodes, fLo, xLo, fHi, xHi, z0, port, interpolant, st,
                                        added.Count), false);
    }

    /// <summary>
    /// Everything reported about one resonance, read off the two SOLVED bracket ends: f₀ is their
    /// root, dX/df is their secant, R is Re(Z) interpolated linearly between them at f₀.
    /// </summary>
    private static PlanarResonance? Characterise(
        PlanarResonanceNodes nodes, double fLo, double xLo, double fHi, double xHi,
        Complex z0, int port, PlanarInterpolant interpolant, PlanarResonanceSettings st, int spent)
    {
        if (fHi <= fLo) return null;

        double f0    = LinearRoot(fLo, xLo, fHi, xHi);
        double slope = (xHi - xLo) / (fHi - fLo);
        if (!double.IsFinite(slope) || slope == 0) return null;

        int iLo = IndexOf(nodes.Frequencies, fLo), iHi = IndexOf(nodes.Frequencies, fHi);
        if (iLo < 0 || iHi < 0) return null;

        double rLo = InputImpedance(nodes.Values[iLo], z0, port).Real;
        double rHi = InputImpedance(nodes.Values[iHi], z0, port).Real;
        double t   = (f0 - fLo) / (fHi - fLo);
        double r   = rLo + t * (rHi - rLo);
        if (!double.IsFinite(r) || r <= 0) return null;

        // The one formula, both kinds — see the header for why the sign is the label and not part
        // of the magnitude.
        double q = f0 * Math.Abs(slope) / (2 * r);
        var kind = slope > 0 ? PlanarResonanceKind.Series : PlanarResonanceKind.Parallel;

        var series  = EntrySeries(nodes, port);
        var s0      = PlanarAdaptiveSweep.PredictSeriesAt(nodes.Frequencies, series, f0, interpolant);
        double rlDb = 20 * Math.Log10(Math.Max(s0.Magnitude, 1e-30));
        double bwHp = q > 0 ? f0 / q : double.NaN;

        var (bw, bwLo, bwHi, refusal) =
            MatchedBandwidth(nodes, series, f0, bwHp, interpolant, st.MatchCriterionDb, rlDb);

        return new PlanarResonance(f0, fHi - fLo, kind, q, r, slope, bwHp, rlDb,
                                   bw, bwLo, bwHi, refusal, spent);
    }

    /// <summary>
    /// The measured width over which |S_pp| stays under the criterion, walked outward from f₀ on
    /// the interpolant. <b>A resonance that never meets the criterion is REFUSED by name</b> rather
    /// than reported as a zero or an omission — an edge-fed patch resonating at 200 Ω into 50 Ω is
    /// the ordinary case, not an error, and "no −10 dB band" is the answer.
    /// </summary>
    private static (double Bw, double Lo, double Hi, string? Refusal) MatchedBandwidth(
        PlanarResonanceNodes nodes, Complex[] series, double f0, double bwHalfPower,
        PlanarInterpolant interpolant, double criterionDb, double atF0Db)
    {
        if (atF0Db > criterionDb)
            return (double.NaN, double.NaN, double.NaN,
                    $"|S| at resonance is {atF0Db:F1} dB, which never reaches {criterionDb:F0} dB, " +
                    $"so there is no {criterionDb:F0} dB bandwidth to measure. The resonance is real; " +
                    $"it is the MATCH that is not there.");

        var f = nodes.Frequencies;
        double fLoLimit = f[0], fHiLimit = f[^1];
        double reach = double.IsFinite(bwHalfPower) && bwHalfPower > 0
            ? 5 * bwHalfPower : (fHiLimit - fLoLimit);

        double Db(double at) =>
            20 * Math.Log10(Math.Max(
                PlanarAdaptiveSweep.PredictSeriesAt(f, series, at, interpolant).Magnitude, 1e-30));

        // 200 steps each way is plenty against an interpolant: this is a bracket-and-refine on a
        // curve that already exists, not a search that costs anything.
        bool clipped = false;
        double Walk(int dir)
        {
            double step = reach / 200;
            double prev = f0, prevDb = atF0Db;
            for (int k = 1; k <= 200; k++)
            {
                double at = f0 + dir * k * step;
                if (at < fLoLimit || at > fHiLimit) { clipped = true; return dir < 0 ? fLoLimit : fHiLimit; }
                double d = Db(at);
                if (d > criterionDb)
                {
                    // Refine the crossing on the same curve — 40 bisections is exact to double.
                    double a = prev, b = at, da = prevDb;
                    for (int it = 0; it < 40; it++)
                    {
                        double m = 0.5 * (a + b), dm = Db(m);
                        if ((da <= criterionDb) == (dm <= criterionDb)) { a = m; da = dm; }
                        else b = m;
                    }
                    return 0.5 * (a + b);
                }
                prev = at; prevDb = d;
            }
            clipped = true;
            return dir < 0 ? f0 - reach : f0 + reach;
        }

        double lo = Walk(-1), hi = Walk(+1);
        return (hi - lo, lo, hi,
                clipped
                    ? $"the {criterionDb:F0} dB band runs past the end of the swept span, so the " +
                      "width reported is the part inside it and the true band is wider"
                    : null);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Stage B — the sampler's OWN criterion, on the intervals around a located resonance.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bisect the solved intervals inside <paramref name="lo"/>..<paramref name="hi"/> until a
    /// freshly solved midpoint agrees with the interpolant's prediction there to
    /// <paramref name="sTolerance"/>.
    ///
    /// <para><b>This is `PlanarSolve`'s refinement loop with the grid taken away</b> — the same
    /// <c>PredictAt</c>, the same <c>WorstAbsDiff</c>, the same absolute |ΔS| threshold. What is
    /// different is only that the midpoint is a FREQUENCY rather than a grid index, which is the
    /// entire content of the opt-in.</para>
    /// </summary>
    private static (PlanarResonanceNodes Nodes, IReadOnlyList<double> Added, bool CapBound) Resolve(
        PlanarResonanceNodes nodes, double lo, double hi, PlanarInterpolant interpolant,
        double sTolerance, int budget, double minWidth, PlanarResonanceProbe probe)
    {
        var added = new List<double>();
        if (hi <= lo || budget <= 0) return (nodes, added, budget <= 0);

        var work = new List<(double Lo, double Hi)>();
        for (int i = 0; i + 1 < nodes.Frequencies.Count; i++)
        {
            double a = nodes.Frequencies[i], b = nodes.Frequencies[i + 1];
            if (b > lo && a < hi) work.Add((a, b));
        }

        while (work.Count > 0)
        {
            var next = new List<(double, double)>();
            foreach (var (a, b) in work)
            {
                if (added.Count >= budget) return (nodes, added, true);
                if (b - a <= minWidth) continue;
                // Only what actually overlaps the window. Without this, ONE grid interval straddling
                // the window edge subdivides its far half all the way to the floor as well, and the
                // split-both-halves recursion turns that into 2^k intervals — the budget is gone
                // before the resonance itself is resolved at all.
                if (b <= lo || a >= hi) continue;
                double mid = 0.5 * (a + b);
                if (mid <= a || mid >= b) continue;

                var predicted = PlanarAdaptiveSweep.PredictAt(
                    nodes.Frequencies, nodes.Values, mid, interpolant);
                nodes = probe(mid);
                added.Add(mid);

                int at = IndexOf(nodes.Frequencies, mid);
                if (at < 0) continue;
                if (PlanarAdaptiveSweep.WorstAbsDiff(nodes.Values[at], predicted) > sTolerance)
                {
                    next.Add((a, mid));
                    next.Add((mid, b));
                }
            }
            work = next;
        }
        return (nodes, added, false);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // The report
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private static string Describe(
        IReadOnlyList<PlanarResonance> found, int addedCount, int dropped, bool capBound,
        PlanarResonanceSettings st, double spanLo, double spanHi)
    {
        var sb = new System.Text.StringBuilder();

        if (found.Count == 0)
        {
            sb.Append("Resonance search: NO RESONANCE was found between ")
              .Append(Ghz(spanLo)).Append(" and ").Append(Ghz(spanHi)).Append(". ");
            if (dropped > 0)
                sb.Append(dropped).Append(" zero crossing(s) of Im(Z_in) were probed and none implied ")
                  .Append("a Q above ").Append(st.MinQ.ToString("G3"))
                  .Append(" — a crossing that shallow is a matched port, not a resonance. ");
            else
                sb.Append("Im(Z_in) does not change sign at any point that was solved, so there is " +
                          "nothing to bracket. THIS IS NOT PROOF THAT THERE IS NONE: the search can " +
                          "resolve a resonance the solved points straddle in SIGN, and a resonance " +
                          "whose whole reactance excursion falls between two neighbouring solved " +
                          "points leaves no sign for it to straddle. If one is expected here, the " +
                          "remedy is the same one a non-converged sweep has — a finer requested " +
                          "grid, which is what gives the search something to bracket. ");
            sb.Append(addedCount).Append(" frequency(ies) were added and solved to establish that");
            sb.Append(capBound
                ? $", and the search stopped at its cap of {st.MaxAddedPoints} added point(s) before " +
                  "it had finished looking — raise the cap, or narrow the span, if a resonance is expected."
                : ".");
            return sb.ToString();
        }

        sb.Append("Resonance search: ").Append(found.Count)
          .Append(found.Count == 1 ? " resonance was" : " resonances were")
          .Append(" FOUND on port ").Append(st.PortNumber).Append(", costing ")
          .Append(addedCount).Append(" added frequency(ies). ")
          .Append("Every added frequency is flagged in the result and is NOT one you asked for; " +
                  "every point of your own grid is still published exactly as it was. ");

        foreach (var r in found)
        {
            sb.Append("  f0 = ").Append(Ghz(r.FrequencyHz))
              .Append(" (bracketed to ").Append(Mhz(r.LocatedToHz)).Append("), ")
              .Append(r.Kind == PlanarResonanceKind.Series ? "series" : "parallel")
              .Append(", Q = ").Append(r.Q.ToString("G4"))
              .Append(" at R = ").Append(r.ResistanceOhm.ToString("G4")).Append(" ohm")
              .Append(", so a half-power bandwidth of ").Append(Mhz(r.HalfPowerBandwidthHz))
              .Append(". |S| there is ").Append(r.ReturnLossDb.ToString("F1")).Append(" dB");

            if (double.IsFinite(r.MatchedBandwidthHz))
                sb.Append(" and the ").Append(st.MatchCriterionDb.ToString("F0"))
                  .Append(" dB bandwidth is ").Append(Mhz(r.MatchedBandwidthHz))
                  .Append(" (").Append(Ghz(r.MatchedBandwidthLoHz)).Append(" to ")
                  .Append(Ghz(r.MatchedBandwidthHiHz)).Append(')');
            if (r.MatchedBandwidthRefusal is { } why) sb.Append(" — ").Append(why.TrimEnd('.'));
            sb.Append(". ");
        }

        sb.Append("Q here is the resonator's OWN Q at that port — radiation and loss, everything " +
                  "inside Z_in — and not the loaded Q of a matched system. ");
        if (dropped > 0)
            sb.Append(dropped).Append(" further zero crossing(s) were probed and discarded as too " +
                                      "shallow to be a resonance (Q below ")
              .Append(st.MinQ.ToString("G3")).Append("). ");
        if (capBound)
            sb.Append("The search stopped at its cap of ").Append(st.MaxAddedPoints)
              .Append(" added point(s), so the curve around these resonances may still be coarse and " +
                      "a further resonance may be unfound. Raise the cap or narrow the span.");
        return sb.ToString().TrimEnd();
    }

    private static string Ghz(double hz) =>
        double.IsFinite(hz) ? (hz / 1e9).ToString("G6") + " GHz" : "n/a";

    private static string Mhz(double hz) =>
        double.IsFinite(hz) ? (hz / 1e6).ToString("G4") + " MHz" : "n/a";

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Small shared arithmetic
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private static bool Straddles(double a, double b) => (a > 0 && b < 0) || (a < 0 && b > 0);

    private static double LinearRoot(double fa, double xa, double fb, double xb)
    {
        double d = xa - xb;
        if (d == 0 || !double.IsFinite(d)) return 0.5 * (fa + fb);
        return fa + (fb - fa) * xa / d;
    }

    private static double Reactance(Mat<Complex> s, Complex z0, int port)
        => InputImpedance(s, z0, port).Imaginary;

    private static double ReactanceOf(PlanarResonanceNodes nodes, double at, Complex z0, int port)
    {
        int i = IndexOf(nodes.Frequencies, at);
        return i >= 0 ? Reactance(nodes.Values[i], z0, port) : double.NaN;
    }

    private static double ReactanceAt(
        IReadOnlyList<double> f, Complex[] series, Complex z0, double at, PlanarInterpolant interp)
    {
        var spp = PlanarAdaptiveSweep.PredictSeriesAt(f, series, at, interp);
        var den = Complex.One - spp;
        if (den.Magnitude < 1e-300) return double.PositiveInfinity;
        return (z0 * (Complex.One + spp) / den).Imaginary;
    }

    private static Complex[] EntrySeries(PlanarResonanceNodes nodes, int port)
    {
        var series = new Complex[nodes.Frequencies.Count];
        for (int k = 0; k < series.Length; k++) series[k] = nodes.Values[k][port, port];
        return series;
    }

    /// <summary>Exact <c>==</c>, for the reason <see cref="PlanarAdaptiveSweep"/>'s own node lookup
    /// gives: every frequency here came from the same <c>double</c> the probe was handed.</summary>
    private static int IndexOf(IReadOnlyList<double> f, double at)
    {
        for (int i = 0; i < f.Count; i++) if (f[i] == at) return i;
        return -1;
    }

    private static (int Lo, int Hi) Surround(IReadOnlyList<double> f, double at)
    {
        for (int i = 0; i + 1 < f.Count; i++)
            if (f[i] <= at && at <= f[i + 1]) return (i, i + 1);
        return (-1, -1);
    }

    /// <summary>How close two candidates must be to count as the same one: the local node spacing,
    /// which is the finest structure the current node set can distinguish.</summary>
    private static double Separation(IReadOnlyList<double> f, double at)
    {
        var (lo, hi) = Surround(f, at);
        return lo < 0 ? 0 : (f[hi] - f[lo]);
    }
}

/// <param name="Resonances">Every resonance found in the span, ascending. Empty is an ANSWER.</param>
/// <param name="AddedFrequencies">
/// <b>Exactly the frequencies this search added</b>, ascending — none of which the user asked for,
/// which is why they are enumerated here as well as flagged point by point.
/// </param>
/// <param name="DiscardedCrossings">Zero crossings probed and rejected as too shallow (ANT-9 §3).</param>
/// <param name="CapBound">Whether <c>MaxAddedPoints</c> stopped the search, which is REPORTED.</param>
/// <param name="Ran">False when the search declined outright — too few nodes, or no such port.</param>
public sealed record PlanarResonanceOutcome(
    IReadOnlyList<PlanarResonance> Resonances,
    IReadOnlyList<double>          AddedFrequencies,
    int                            DiscardedCrossings,
    bool                           CapBound,
    bool                           Ran,
    string                         Note);
