// Which of §2.9's two readings produced a result, and how both are kept at once
// (docs/sonnet-briefs/brief-railrf-4-fast-extractor.md R-rail4-2 / R-rail4-5, railrf.md §2.9).
//
// ── RULE 1 OF §2.9, AND IT IS A TYPE RATHER THAN A CONVENTION ──────────────────────────────────
//
// "A pass in Fast mode is reported as A FAST-MODEL PASS, never as a pass." That sentence is only
// enforceable if a result cannot be built without saying which model made it, so
// PdnProvenance.ModelKind is `required` and this enum has no default member. A result object that
// can be constructed without a model kind is a result that can reach a user without one, and the
// whole of §9's "the fast model's classification" risk is invisible the moment that happens.
//
// ── RULE 4: THE TWO COEXIST, THEY DO NOT REPLACE EACH OTHER ────────────────────────────────────
//
// §2.9: "Running Accuracy KEEPS THE FAST CURVE ON THE PLOT BESIDE THE ACCURATE ONE, so the error is
// measured on this design rather than promised in a document." A window that overwrote one result
// with the other would make that impossible, and the error would go back to being a number in a
// document. PdnResultsByModel is the smallest thing that makes it possible: a result is FILED under
// its own model kind, so an Accuracy run never displaces the Fast one that is already there.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// Which of §2.9's two readings of the geometry produced a result.
///
/// <para><b>Not a quality setting and not a mode.</b> Both readings produce the same kind of netlist
/// (§4.6) — the solver, the result model, the tables, the plots and the exports are identical and
/// only the extractor differs. That is the reason the fast path is safe to have at all: it is not a
/// second simulator, it is a second reading of the geometry.</para>
/// </summary>
public enum PdnModelKind
{
    /// <summary>
    /// The graph reading (<see cref="PdnGraphExtractor"/>): each trace section between junctions one
    /// resistance <c>R = ρ·L/(W·T)</c>, each via its barrel, each pad a node, and only copper that is
    /// not trace-shaped meshed — coarsely. A few hundred elements, so it re-solves on a keystroke.
    /// </summary>
    Fast,

    /// <summary>
    /// The mesh reading (<see cref="PdnMeshExtractor"/>): §4.1's unit cells over the real copper, at
    /// the density §4.1 requires. Thousands of elements, and on a button.
    /// </summary>
    Accurate,
}

/// <summary>
/// One extraction per model kind, kept side by side.
///
/// <para><b>§2.9's fourth rule in one class.</b> Running Accuracy must not displace the Fast result,
/// because the whole point of the fourth rule is that the two are compared <i>on the user's own
/// board</i> rather than trusted from a document. Brief 12 draws the second series; what brief 4 owes
/// it is that the first one is still there to draw.</para>
/// </summary>
public sealed class PdnResultsByModel
{
    private readonly Dictionary<PdnModelKind, PdnExtraction> _byKind = [];

    /// <summary>Files <paramref name="extraction"/> under the model kind its own provenance names —
    /// never under one the caller supplies, because a result filed under the wrong kind is exactly
    /// the mislabelling rule 1 exists against.</summary>
    /// <exception cref="ArgumentException">The extraction refused, so it names no model.</exception>
    public void Add(PdnExtraction extraction)
    {
        if (extraction.Netlist is not { } netlist)
            // An INTERNAL invariant, not a message for a reader: a refusal is reported as a refusal
            // by whoever asked for the extraction, and never reaches here.
            throw new ArgumentException(
                "A refused extraction names no model kind and is not filed as a result.",
                nameof(extraction));

        _byKind[netlist.Provenance.ModelKind] = extraction;
    }

    /// <summary>The extraction of that kind, or null where none has been run.</summary>
    public PdnExtraction? this[PdnModelKind kind] =>
        _byKind.TryGetValue(kind, out var e) ? e : null;

    /// <summary>True once both readings of this board are in hand — which is when §2.9's fourth rule
    /// can actually be shown: the fast curve beside the accurate one, on this design.</summary>
    public bool HasBoth => _byKind.ContainsKey(PdnModelKind.Fast)
                        && _byKind.ContainsKey(PdnModelKind.Accurate);

    /// <summary>Every kind held, in enum order, so a legend is built in the same order twice.</summary>
    public IEnumerable<PdnModelKind> Kinds =>
        Enum.GetValues<PdnModelKind>().Where(_byKind.ContainsKey);

    /// <summary>Forgets every result — what a window does when the artwork or the rail changes
    /// underneath them, because a fast curve beside an accurate one from a different board is worse
    /// than no comparison at all.</summary>
    public void Clear() => _byKind.Clear();
}
