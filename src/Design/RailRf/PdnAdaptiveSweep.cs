// §4.4's adaptive frequency sampling, through the mechanism that already exists
// (docs/sonnet-briefs/brief-railrf-14-cavity.md R-rail14-4; railrf.md §4.4).
//
// ── WHY THIS IS NOT OPTIONAL, AND IT IS NOT AN OPTIMISATION ────────────────────────────────────
//
// §4.4: "Adaptive frequency sampling is not optional once the cavity band is in scope: PLANE
// RESONANCES ARE NARROW AND A LOG GRID STEPS STRAIGHT OVER ONE. The existing adaptive sweep from the
// EM engine is the mechanism."
//
// A log grid that steps over a resonance produces a curve that looks smooth and is missing its own
// worst point. NOTHING REPORTS THAT. The mask passes, the anti-resonance table has no row for it,
// and the coincidence check — the sentence §2.4 says this tool exists to produce — is computed over
// a curve the peak is not in. That is the failure this file exists against, and it is why the gate
// in PdnCavityTests is on the peak being FOUND rather than on a speed-up.
//
// ── WHAT IS REUSED, AND THE ONE THING THAT IS DELIBERATELY NOT ─────────────────────────────────
//
// Everything that DECIDES is the EM engine's: PlanarResonanceSearch locates the crossings and
// brackets them, seeded from PlanarAdaptiveSweep's own interpolant, judged on PlanarAdaptiveSweep's
// own |ΔS| criterion. Not one of those is re-expressed here. A second criterion, a second
// interpolant or a second bracketing rule is exactly the drift the brief's "do not write a second
// refiner" is about, and this file contains none of them: it supplies the PROBE and keeps the store.
//
// WHAT IS NOT REUSED IS THE GRID-BISECTION HALF, AND THAT IS A DECISION RATHER THAN AN OMISSION.
// PlanarSolve's refinement solves a SUBSET of the requested grid and models the rest, because a
// de-embedded full-wave point costs 48-72 s there. Here a point is one sparse complex solve —
// milliseconds — so the saving is worth nothing, and it would cost two things that are worth a
// great deal:
//
//   1. §2.4's mask verdict, anti-resonance table and coincidence rows are read off the curve AT the
//      requested points. A modelled point in a mask verdict is an interpolation reported as a
//      measurement, which is the one thing a verdict may not be.
//   2. The recorded EM finding (ANT-9) is that refinement CANNOT find a narrow resonance anyway:
//      it bisects INDICES of the requested grid, so every frequency it is allowed to look at is
//      already solved — on a high-Q response it can solve 86 % of a 51-point grid, miss its
//      tolerance by twenty times, and never once look where the resonance is.
//
// So the whole requested grid is solved and the search ADDS what the grid stepped over. That is
// PlanarAdaptiveSettings' own "InitialPoints = the whole grid, Search on" configuration, arrived at
// for this cost model rather than copied.
//
// ── THE ADDED POINTS ARE PUBLISHED, AND THAT IS ANT-9's NARROWED PROPERTY ──────────────────────
//
// The EM sweep's rule is "refinement never adds a frequency you did not ask for; the resonance
// search, when you switch it on, may — and says which." Same here: with Search null the result is
// the requested grid, point for point, and with it on every added frequency is in AddedHz and
// counted in the note. A curve whose x axis silently grew is a curve nobody can compare against a
// measurement taken on the grid they asked for.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// How a rail's band is sampled. <b>Null <see cref="Search"/> is the requested grid and nothing
/// else</b>, bit for bit — the same "off is off" property the EM sampler's own settings carry.
/// </summary>
/// <param name="Search">The EM engine's resonance search, or null to sample the grid alone.
/// <see cref="PlanarResonanceSettings.MaxAddedPoints"/> is the budget, and it binds: a PDN point is
/// cheap but it is not free, and a cap that never binds is a cap nobody set.</param>
/// <param name="Interpolant">Which model of the curve the search seeds its candidates from. The EM
/// sampler's own setting, passed through — this file owns no interpolant.</param>
/// <param name="Tolerance">The |ΔS| the search's second stage resolves each resonance's flanks to.
/// The EM sampler's own criterion, passed through.</param>
public sealed record PdnSamplingSettings(
    PlanarResonanceSettings? Search = null,
    PlanarInterpolant Interpolant = PlanarInterpolant.CubicSpline,
    double Tolerance = 1e-3)
{
    /// <summary>The search on, at the EM engine's own defaults.</summary>
    public static readonly PdnSamplingSettings Default = new(PlanarResonanceSettings.Default);
}

/// <summary>What one sampled sweep produced.</summary>
/// <param name="FrequenciesHz">Every frequency SOLVED, ascending — the requested grid plus whatever
/// the search added. Nothing here is modelled.</param>
/// <param name="S">The s-parameter matrix at each, in the same order.</param>
/// <param name="AddedHz">The frequencies the search added, which were not asked for.</param>
/// <param name="Resonances">What it found, with each one's f₀, Q and located-to width.</param>
/// <param name="Notes">The search's own sentence, plus what a reader has to know about the grid
/// they asked for having grown.</param>
public sealed record PdnSampledSweep(
    double[] FrequenciesHz,
    Mat<Complex>[] S,
    IReadOnlyList<double> AddedHz,
    IReadOnlyList<PlanarResonance> Resonances,
    IReadOnlyList<string> Notes);

/// <summary>§4.4's sampling, driven by the EM engine's own sampler.</summary>
public static class PdnAdaptiveSweep
{
    /// <summary>
    /// Solves <paramref name="grid"/>, then lets <see cref="PlanarResonanceSearch"/> add the
    /// frequencies the grid stepped over.
    /// </summary>
    /// <param name="grid">The requested frequencies. Solved in full — see this file's header.</param>
    /// <param name="z0">The port reference the search reads Z_in against. A REFERENCE and not a
    /// termination: Im(Z_in) crosses zero at the same frequencies whatever it is.</param>
    /// <param name="settings">Null, or a null <see cref="PdnSamplingSettings.Search"/>, samples the
    /// grid and nothing else.</param>
    /// <param name="solveAt">One assembly and one solve at one frequency.</param>
    /// <param name="stopped">Asked before every ADDED probe, so a user can stop a search that is
    /// still spending its budget. The requested grid is never abandoned part way: a partial curve on
    /// the axis somebody asked for is worse than a complete one with no search on it.</param>
    public static PdnSampledSweep Run(
        double[] grid, Complex z0, PdnSamplingSettings? settings,
        Func<double, Mat<Complex>> solveAt, Func<bool>? stopped = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(solveAt);

        // Ascending and deduplicated, because both the interpolant and the search read the node set
        // as a curve. A duplicated frequency is a zero-width interval and a division by zero in
        // every spline built over it.
        var solved = new SortedDictionary<double, Mat<Complex>>();
        foreach (double f in grid) solved.TryAdd(f, default);
        foreach (double f in solved.Keys.ToArray()) solved[f] = solveAt(f);

        var notes = new List<string>();

        if (settings?.Search is not { } search || solved.Count < 2)
            return new PdnSampledSweep([.. solved.Keys], [.. solved.Values], [], [], notes);

        PlanarResonanceNodes Nodes() => new([.. solved.Keys], [.. solved.Values]);

        int ports = solved.Values.First().RowCount;
        if (search.PortNumber > ports)
        {
            notes.Add(
                $"The resonance search was asked to read port {search.PortNumber} and this rail has " +
                $"{ports}. No search was run — a search on a port that is not there would report " +
                "having looked and found nothing.");
            return new PdnSampledSweep([.. solved.Keys], [.. solved.Values], [], [], notes);
        }

        var outcome = PlanarResonanceSearch.Search(
            Nodes(), solved.Keys.First(), solved.Keys.Last(), z0, settings.Interpolant,
            settings.Tolerance, search,
            probe: f =>
            {
                if (!solved.ContainsKey(f)) solved[f] = solveAt(f);
                return Nodes();
            },
            stopped: stopped);

        if (outcome.Note is { Length: > 0 } note) notes.Add(note);

        if (outcome.AddedFrequencies.Count > 0)
            notes.Add(
                $"{outcome.AddedFrequencies.Count} frequency point(s) were added to the grid you asked for, at " +
                $"the resonances the search located. A log grid steps straight over a narrow plane " +
                "resonance and the curve it draws looks smooth; these points are the ones that " +
                (ports > 1
                    ? $"would otherwise be missing. They were located on port {search.PortNumber}."
                    : "would otherwise be missing."));

        return new PdnSampledSweep(
            [.. solved.Keys], [.. solved.Values], outcome.AddedFrequencies, outcome.Resonances, notes);
    }
}
