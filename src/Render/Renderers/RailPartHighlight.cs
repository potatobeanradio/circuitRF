// The part the user picked in the parts table, as the board draws it.
//
// ── WHY THIS IS A DRAW ARGUMENT AND NOT PART OF RailMapScene ───────────────────────────────────
//
// R-rail8-13: the scene is a PURE FUNCTION OF THE RESULT. A selection is not a result — it is the
// state of a list box, it changes on every click, and a solve neither produces it nor is invalidated
// by it. Folding it into the scene would make one result produce a different scene per click, which
// is the same mistake the hidden-layer set was kept out of the scene to avoid, and it would throw
// away RailMapScene's cached build on every arrow-key press down the table.
//
// So this travels the way `hiddenLayers` does: alongside the scene, applied at the draw.
//
// ── IT CARRIES GEOMETRY, NOT A REFDES ──────────────────────────────────────────────────────────
//
// Nothing below the firewall may look a part up. Which pads belong to C7 is a question about the
// board netlist, and answering it here would put a second copy of PdnAttachments' resolution in the
// renderer — which is exactly the divergence that rule exists against. The caller resolves; this
// says where to draw.

using CircuitRF.Design.Layout;
using System.Linq;

namespace CircuitRF.Render;

/// <summary>
/// One part, marked on the board because it is selected in the parts table.
/// </summary>
/// <param name="Label">What is written beside the mark — the refdes, as the table spells it.</param>
/// <param name="Pads">Each of the part's pads, DBU. <b>Every one, not a centroid</b>: a decoupling
/// capacitor's two pads are what the mounting loop is measured between, and a single dot in the
/// middle of the part is the one place on it that no answer is about.</param>
/// <param name="Body">The part body's box where a placement states one, or null. Drawn as the
/// outline; with none, the pads' own extent serves — see <see cref="Outline"/>.</param>
public sealed record RailPartHighlight(
    string Label,
    IReadOnlyList<(long X, long Y)> Pads,
    Bbox? Body = null)
{
    /// <summary>How far past the pads the fallback outline reaches, DBU per micron scaled by the
    /// caller. A pad is a coordinate here, not a shape, so the box needs some width of its own.</summary>
    public const long PadReachDbu = 300_000;   // 0.3 mm at the default 1000 DBU/µm

    /// <summary>
    /// <b>Value equality, over the pads as well.</b>
    /// </summary>
    /// <remarks>
    /// A record's generated <c>Equals</c> compares <see cref="Pads"/> by REFERENCE, and every caller
    /// here builds a fresh list each time it resolves a selection — so two highlights of the same
    /// part were never equal. That is not only a test's problem: <c>RailLayoutOverlay</c> uses this
    /// comparison to decide whether a selection change is worth a repaint, and with reference
    /// equality it repaints on every property notification whether or not anything moved.
    /// </remarks>
    public bool Equals(RailPartHighlight? other) =>
        other is not null &&
        Label == other.Label &&
        Body == other.Body &&
        Pads.Count == other.Pads.Count &&
        Pads.SequenceEqual(other.Pads);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Label);
        hash.Add(Body);
        foreach (var pad in Pads) hash.Add(pad);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The box to outline: the body where one was given, otherwise the pads' extent grown by
    /// <see cref="PadReachDbu"/>. Empty when there is nothing to draw at all, which is a part the
    /// board does not place — the caller is what decides whether to mark it, and it is told by this
    /// coming back empty rather than by a mark appearing at the origin.
    /// </summary>
    public Bbox Outline
    {
        get
        {
            if (Body is { } body && !body.IsEmpty) return body;
            if (Pads.Count == 0) return Bbox.Empty;

            var box = Bbox.Empty;
            foreach (var (x, y) in Pads)
                box = box.Union(new Bbox(x - PadReachDbu, y - PadReachDbu,
                                         x + PadReachDbu, y + PadReachDbu));
            return box;
        }
    }
}
