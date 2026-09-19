// The plane-pair run: one extraction at the frequency the answer is of, then §4.5's modes and
// §2.4's map (docs/sonnet-briefs/brief-railrf-15-modes-and-maps.md; railrf.md §4.5, §2.4, §2.9).
//
// ── WHY IT IS A RUN OF ITS OWN AND NOT A FIELD ON THE DC ANSWER ────────────────────────────────
//
// Two reasons, and the second is the one that matters.
//
// FIRST, IT CANNOT BE. §4.1's shunt branch vanishes at ω = 0 by construction, so the DC run's
// netlist carries no plane capacitance at all and no inductance either — there is literally no
// cavity in it to find modes in. The cavity needs its own extraction at its own frequency, meshed
// to λ/20 THERE, which is a different mesh from the DC one on every board where the two rules
// disagree (PdnMeshExtractor.WavelengthCellSizeMetres carries that argument).
//
// SECOND, IT IS EXPENSIVE AND §2.9's RULE IS THAT AN EXPENSIVE READING IS NEVER ENTERED
// AUTOMATICALLY. The mode solve is dense and cubic in the cell count — 36 s on the design note's
// own 90 x 70 mm board at 1.4 mm cells (src/Engine/RESOLVED.md). Folding that into the Fast edit
// loop, which re-solves on every committed row edit, would make a window that types at four
// characters a minute. So this is its own call, with its own button, exactly as Accuracy is.
//
// ── IT ADDS NO READING OF THE GEOMETRY ─────────────────────────────────────────────────────────
//
// The extraction below is RailDcRun.RequestFor's, with one field changed: the frequency. Every
// other term — the reference extent, the class overrides, the copper temperature, the pads, the
// mesh settings — is the same request the drop map was built from, because a mode list of a board
// the drop map is not of would be worse than no mode list.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Engine.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>Everything one plane-pair run reads.</summary>
public sealed class RailPlaneRequest
{
    /// <summary>The board, exactly as the DC run reads it. <b>The same request object</b> — see
    /// this file's header.</summary>
    public required RailDcRequest Board { get; init; }

    /// <summary>Which rail. One rail per run, as the extractor itself works (R-rail3-13).</summary>
    public required string RailName { get; init; }

    /// <summary>
    /// The frequency the cavity is read at, in hertz.
    /// </summary>
    /// <remarks>
    /// <b>It sets three things at once and they are not separable</b>: the mesh (λ/20 there), the
    /// copper's own skin-effect resistance, and the dielectric's <c>G = ωC·tan δ</c>. That is why
    /// the |Z| map is AT this frequency and not at one passed separately — a map at another
    /// frequency would use this one's losses.
    ///
    /// <para>The modes themselves do not depend on it except through the mesh, which is the
    /// ordinary discretisation question: a mesh too coarse for a mode cannot carry it.</para>
    /// </remarks>
    public required double FrequencyHz { get; init; }

    /// <summary>How many modes to report. Six is §7's own acceptance count.</summary>
    public int ModeCount { get; init; } = 6;

    /// <summary>Which observation port the |Z| map is driven from — the rail's own load index.</summary>
    public int MapPortIndex { get; init; }

    /// <summary>The solver's ceiling, or null for its own default. See
    /// <see cref="PdnModeOptions.MaxCells"/>, which is a measured number.</summary>
    public PdnModeOptions? Modes { get; init; }

    /// <summary>
    /// Called as each stage of the run begins, on the RUN's thread. Null to report nothing.
    /// </summary>
    /// <remarks>
    /// <b>Stages and not a percentage, because a percentage here would be invented</b> (owner
    /// asked for a progress bar, 2026-09-19). The dominant cost is one dense LAPACK call inside
    /// <c>PdnModeSolver</c> — cubic in the cell count, no callbacks, no subdivision — so nothing
    /// in this run can honestly say it is 40 % done. What it CAN say is which of four things it
    /// is doing and how big the problem turned out to be, which is the part a waiting user
    /// actually wants: an auto-fitted re-mesh and a 2,544-cell dense solve explain a thirty-second
    /// wait, where a bar creeping at an invented rate explains nothing and is a lie besides.
    /// </remarks>
    public Action<string>? Progress { get; init; }
}

/// <summary>
/// Everything one plane-pair run produced. <see cref="Refusal"/> non-null means NOTHING was
/// computed.
/// </summary>
/// <param name="Refusal">Why nothing was computed, or null.</param>
/// <param name="Answer">§4.5's modes and §2.4's map. Null exactly when
/// <paramref name="Refusal"/> is not.</param>
/// <param name="Provenance">The extraction the answer is of — <b>its own</b>, not the DC run's:
/// the cell size, the cell count and the dielectric are all different in the cavity band and a
/// reader comparing the two needs to see which.</param>
/// <param name="Diagnostics">Everything worth saying that did not stop the run.</param>
public sealed record RailPlaneResult(
    string? Refusal,
    PdnPlaneAnswer? Answer,
    PdnProvenance? Provenance,
    IReadOnlyList<string> Diagnostics)
{
    internal static RailPlaneResult Refused(string why) => new(why, null, null, []);
}

/// <summary>§4.5's modes and §2.4's impedance map, for one rail of one board.</summary>
public static class RailPlaneRun
{
    /// <summary>Runs one rail's plane pair, or refuses and says why.</summary>
    public static RailPlaneResult Run(RailPlaneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!(request.FrequencyHz > 0))
            return RailPlaneResult.Refused(
                "A plane-pair run needs a frequency above zero. At ω = 0 §4.1's shunt branch and " +
                "its inductance both vanish and there is no cavity — that is the DC answer, and it " +
                "is the one the drop map is of.");

        var doc = request.Board.Document;
        if (doc.Rail(request.RailName) is not { } rail)
            return RailPlaneResult.Refused(
                $"This document has no rail called '{request.RailName}'.");

        // ── ALWAYS THE MESH, whatever the window's model kind is ──────────────────────────────
        //
        // §2.9's Fast reading is a graph: traces as closed forms, pours as a coarse mesh, and — by
        // R-rail4-4's own ceiling — it REFUSES above a tenth of the plane pair's first cavity
        // resonance. The cavity band is by definition above that. So there is one reading of a
        // plane pair and it is the mesh; asking Fast for it would get a refusal, and answering it
        // with a coarse pour mesh would be a mode list of a board nobody drew.
        request.Progress?.Invoke($"extracting the plane pair at {PdnMask.Hertz(request.FrequencyHz)}…");

        var extraction = PdnMeshExtractor.Extract(
            RailDcRun.RequestFor(request.Board, rail, request.FrequencyHz));

        if (extraction.Refusal is { } why)
            return RailPlaneResult.Refused($"Rail '{rail.Name}' was not extracted. {why}");

        var pdn = extraction.Netlist!;
        var diagnostics = new List<string>(extraction.Diagnostics);
        int ceiling = Math.Max(1, (request.Modes ?? new PdnModeOptions()).MaxCells);

        var fit = Fit(request, rail, pdn, ceiling);
        if (fit.Refusal is { } fitRefusal)
            return new RailPlaneResult(fitRefusal, null, pdn.Provenance, diagnostics);

        if (fit.Netlist is { } refitted)
        {
            pdn = refitted;
            diagnostics.AddRange(fit.Extraction!.Diagnostics);
        }

        request.Progress?.Invoke(
            $"solving {PdnPlaneModes.CavityCellCount(pdn):N0} cavity cells — the dense step, and " +
            "the slow one.");

        var answer = PdnPlaneModes.Of(
            pdn, request.ModeCount, request.MapPortIndex, request.Modes, request.Progress);

        if (answer.Refusal is { } modeRefusal)
            return new RailPlaneResult(modeRefusal, null, pdn.Provenance, diagnostics);

        // The mesh note goes on the ANSWER's notes and not only into the diagnostics, because it is
        // about the picture: a |Z| map drawn at a cell size the rest of the window is not at is a
        // map somebody will compare against the drop map square for square.
        if (fit.Note is { Length: > 0 } note)
        {
            answer = answer with { Notes = [.. answer.Notes, note] };
            diagnostics.Add(note);
        }

        return new RailPlaneResult(null, answer, pdn.Provenance, diagnostics);
    }

    // ── THE CAVITY SIZES ITS OWN MESH, because the DC mesh is sized for a different question ───
    //
    // The extraction above inherits the DC run's cell size, and with nothing stated that size is
    // R-rail3-14's FEATURE rule: enough cells across the narrowest current-carrying conductor that
    // a thin trace's RESISTANCE comes out right. On a real board that is a fraction of a
    // millimetre — the shipped Power Rail example meshes at 0.066 mm — and it has nothing to do
    // with a cavity. A plane pair's modes are a property of the plane pair; a 0.2 mm trace hanging
    // off it does not move them, and paying 61,129 cavity cells to resolve one buys nothing this
    // answer contains.
    //
    // The mode solve is dense and cubic, so PdnModeSolver stops at PdnModeOptions.MaxCells. Left
    // alone the two rules collide on every board with a trace on it and THE |Z| MAP IS SIMPLY NOT
    // REACHABLE — which is exactly what the shipped example did: the run refused at every
    // frequency, and the refusal named two knobs that do not bind here. (λ/20 at 100 MHz in εr 4.3
    // is 72 mm, wider than the board; halving the frequency changed the cell size by nothing at
    // all, so "find the modes against a lower band top" was advice that could not work.)
    //
    // So with no cell size stated the cavity picks its own: the finest mesh that fits the solver,
    // MEASURED rather than guessed. n cells of side Δ cover about n·Δ² of plane, so the Δ that
    // lands on a target count follows from the extraction already in hand — no second reading of
    // the geometry, which is this file's own standing rule. The loop is there to absorb the error
    // in "about": a plane pair's cell count is not exactly area/Δ² once its edges and its
    // galvanic splits are counted.
    //
    // A STATED cell size is never overridden. It is the knob the convergence sweeps turn, and a
    // run that silently re-meshed it would make those sweeps measure nothing — so that case is a
    // refusal, and the refusal names the size that would have fitted.

    /// <summary>
    /// How many cavity cells an auto-fitted mesh aims at — <b>not the solver's ceiling</b>.
    /// </summary>
    /// <remarks>
    /// <b>The ceiling is a refusal threshold and would be the wrong target.</b> The solve is cubic,
    /// and <see cref="PdnModeOptions.MaxCells"/>'s own measurements say what that costs: 800 cells
    /// is 0.7 s, 1,575 is 4.4 s, 3,200 is <b>36 s</b>, and 4,000 is about 70 s. An auto-fit aimed
    /// at the ceiling would make every board that needs one pay a minute for a picture somebody is
    /// clicking to look around in — and the extra cells buy resolution nobody asked for, since six
    /// modes of a plane pair do not need a 63 × 63 grid to sit on.
    ///
    /// <para>1,500 is a few seconds and about a 39 × 39 grid. A reader who wants the finer answer
    /// states a cell size, which is the knob the note names — and which the ceiling then guards in
    /// the ordinary way.</para>
    /// </remarks>
    private const int AutoFitCells = 1_500;

    /// <summary>How many times the fit re-extracts before giving up and saying so.</summary>
    private const int FitAttempts = 4;

    /// <summary>What <see cref="Fit"/> decided. All three null means the mesh in hand already
    /// fits and nothing was done.</summary>
    private readonly record struct MeshFit(
        string? Refusal, PdnNetlist? Netlist, PdnExtraction? Extraction, string? Note);

    /// <summary>Re-meshes the plane pair to fit the mode solver, or leaves it alone.</summary>
    private static MeshFit Fit(
        RailPlaneRequest request, RailSpec rail, PdnNetlist pdn, int ceiling)
    {
        int natural = PdnPlaneModes.CavityCellCount(pdn);
        if (natural <= ceiling) return default;

        double naturalDelta = pdn.Provenance.CellSizeMetres;

        if (request.Board.Mesh.CellSizeMetres is { } stated && stated > 0)
            return new MeshFit(
                $"This plane pair meshes to {natural:N0} cavity cells at the " +
                $"{stated * 1e3:0.###} mm cell size this run states, and the mode solve is dense — " +
                $"cubic in the cell count — so it stops at {ceiling:N0}. Clear the cell size and " +
                $"the cavity will size its own mesh, or state about " +
                $"{Coarser(naturalDelta, natural, Math.Min(AutoFitCells, ceiling)) * 1e3:0.###} mm.",
                null, null, null);

        int target = Math.Min(AutoFitCells, ceiling);
        int cells = natural;
        double delta = naturalDelta;
        PdnExtraction? extraction = null;

        // Coarsen until the mesh is AT OR UNDER the ceiling, aiming each step at the target. The
        // loop condition is the ceiling and not the target: overshooting the target is a slightly
        // coarser picture, and iterating to hit it exactly would buy re-extractions nobody wants.
        for (int attempt = 0; attempt < FitAttempts && cells > ceiling; attempt++)
        {
            delta = Coarser(delta, cells, target);

            request.Progress?.Invoke(
                $"re-meshing at {delta * 1e3:0.###} mm — {cells:N0} cavity cells is over the " +
                $"{ceiling:N0} the dense solve stops at…");

            var next = PdnMeshExtractor.Extract(
                RailDcRun.RequestFor(
                    request.Board, rail, request.FrequencyHz,
                    new PdnMeshSettings
                    {
                        CellSizeMetres            = delta,
                        CellsAcrossMinimumFeature = request.Board.Mesh.CellsAcrossMinimumFeature,
                        PortRefinementRatio       = request.Board.Mesh.PortRefinementRatio,
                        IncludeIsolatedRegions    = request.Board.Mesh.IncludeIsolatedRegions,
                    }));

            if (next.Refusal is { } why)
                return new MeshFit(
                    $"Rail '{rail.Name}' did not re-extract at the {delta * 1e3:0.###} mm cell the " +
                    $"cavity sized itself to. {why}", null, null, null);

            extraction = next;
            cells = PdnPlaneModes.CavityCellCount(next.Netlist!);
        }

        if (cells > ceiling || extraction is null)
            return new MeshFit(
                $"This plane pair still meshes to {cells:N0} cavity cells at " +
                $"{delta * 1e3:0.###} mm, over the {ceiling:N0} the dense mode solve stops at, " +
                $"after {FitAttempts} attempts at sizing the mesh to it. State a cell size for " +
                "this run.", null, null, null);

        return new MeshFit(
            null, extraction.Netlist, extraction,
            $"The cavity meshed itself at {delta * 1e3:0.###} mm — {cells:N0} cells — rather than " +
            $"the {naturalDelta * 1e3:0.###} mm the DC rules give, which would have been " +
            $"{natural:N0} cells against the {ceiling:N0} this solve stops at. The DC cell size is " +
            "set by the narrowest TRACE on the rail so that its resistance comes out right, and a " +
            "plane pair's modes do not depend on it. State a cell size for a finer answer — the " +
            "solve is cubic in the cell count, so half the cell is about eight times the wait.");
    }

    /// <summary>The cell size that would land <paramref name="cells"/> cells on
    /// <paramref name="ceiling"/>, from the one already measured.</summary>
    /// <remarks>n cells of side Δ cover n·Δ², so the Δ that covers the same plane in the target
    /// count scales as the square root of the ratio.</remarks>
    private static double Coarser(double delta, int cells, int target) =>
        delta * Math.Sqrt((double)cells / Math.Max(1, target));
}
