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
        var extraction = PdnMeshExtractor.Extract(
            RailDcRun.RequestFor(request.Board, rail, request.FrequencyHz));

        if (extraction.Refusal is { } why)
            return RailPlaneResult.Refused($"Rail '{rail.Name}' was not extracted. {why}");

        var pdn = extraction.Netlist!;
        var answer = PdnPlaneModes.Of(pdn, request.ModeCount, request.MapPortIndex, request.Modes);

        return answer.Refusal is { } modeRefusal
            ? new RailPlaneResult(modeRefusal, null, pdn.Provenance, extraction.Diagnostics)
            : new RailPlaneResult(null, answer, pdn.Provenance, extraction.Diagnostics);
    }
}
