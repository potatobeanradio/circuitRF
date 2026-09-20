// The two |Z| this application computes, named once
// (docs/sonnet-briefs/brief-railrf-21-two-numbers-one-name.md R-rail21-1a).
//
// ── WHY A FILE FOR TWO STRINGS ─────────────────────────────────────────────────────────────────
//
// railRF prints two impedances of one board at one frequency from one port name, and they differ by
// four orders of magnitude. The board map is the PLANE PAIR's — its copper, its shape, its stackup,
// driven from one observation port, with nothing hanging on it. The plot's curve is the DECOUPLED
// RAIL's — the capacitors, their ESR, their mounting loops and the source's own R and L. At 50 MHz
// a bare plane pair between two points is hundreds of ohms and the same rail with its decoupling is
// tens of milliohms; 465 Ω and 23 mΩ are both right.
//
// Neither surface said which it was. A first-time reader saw two impedances, one board, one
// frequency, one port name and a factor of twenty thousand, and concluded the tool was broken
// (2026-09-20). The correct sentence existed — in PdnPlaneAnswer.Notes, a list on a different tab
// nobody reads while looking at a picture.
//
// So the distinction is spelled ONCE and every surface that says anything says it from here: the
// map caption, the hover readout, the |Z| tab's tooltip, the map panel's own note, and every
// exported picture that carries the caption. Two spellings of one distinction is how they come to
// disagree — which is the defect this file exists to make unrepresentable, not a style rule.
//
// ── IT IS A NAMING CHANGE AND NOTHING ELSE ─────────────────────────────────────────────────────
//
// R-rail21-1e, explicit because the temptation is real: nobody is to "fix" the map by adding the
// parts to it. The map is of the plane pair on purpose, it is the only view of the cavity there is,
// and adding lumped parts to a distributed cavity solve would produce a third quantity with no name
// at all.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>What each of railRF's two impedances is called, wherever one of them is printed.</summary>
public static class PdnImpedanceNames
{
    /// <summary>The board map's quantity: the plane pair on its own, with nothing on it.</summary>
    public const string PlanePair = "plane pair alone";

    /// <summary>The |Z| curve's quantity: the rail as built, decoupling and source included.</summary>
    public const string Rail = "this rail";

    /// <summary>
    /// The sentence that says what the map is — <b>on the picture, not only in the notes</b>
    /// (R-rail21-1b).
    /// </summary>
    /// <remarks>
    /// <see cref="PdnPlaneModes.Of"/> puts this on every answer's <c>Notes</c> and the window puts
    /// the same string on the map panel. It is the single most important thing a reader of that
    /// picture needs to know and it used to be the hardest thing in the window to find.
    /// </remarks>
    public const string PlanePairNote =
        "These modes and this map are the PLANE PAIR's own — its copper, its shape and its " +
        "stackup. The decoupling parts, the sources and the loads hanging on it are not in " +
        "either of them; their answer is the |Z| curve.";

    /// <summary>
    /// The rail's own name, qualified with what makes it the other number — <c>this rail (the
    /// parts, at 50 MHz)</c>.
    /// </summary>
    /// <param name="frequency">The frequency, already spelled (<c>PdnMask.Hertz</c>).</param>
    public static string RailAt(string frequency) => $"{Rail} (the parts, at {frequency})";
}
