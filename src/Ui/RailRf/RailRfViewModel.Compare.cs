// The A/B comparison, from the window (§2.5, brief 16; owner, 2026-09-19).
//
// ── WHY A SECOND VIEW MODEL IS THE REFERENCE SIDE ───────────────────────────────────────────────
//
// §2.5's question is "the reference passes; does yours?", so a comparison needs a second design
// RESOLVED AND SOLVED — its artwork, its stackup, its netlist, its placement, its part library, its
// DC answer and its frequency curve. Every one of those is something this view model already does,
// and it is framework-free by construction (that is why brief 7's own tests drive it with no
// application host).
//
// So the reference side is a `RailRfViewModel` over the reference document. The alternative was a
// second resolve-and-solve path written against `src/Design` directly, which is the same code
// arranged differently and would answer differently the first time either was touched — the
// divergence this window's whole CLI chapter is written against. The cost is one object that is
// never shown; the benefit is that the two sides of the comparison are computed by the same code by
// construction, which is the one property a comparison cannot be allowed to lack.
//
// ── AND THE REFERENCE'S REFERENCE LAYER IS NOT CONFIRMED BY A CLICK ────────────────────────────
//
// Q-8's "proposed, never assumed" is about a document a USER is editing: the click is how railRF
// refuses to pick a reference conductor on someone's behalf. A `.crail` picked as the reference of
// a comparison has already stated its layer, in its own file, and there is nobody to ask — asking
// would be asking the user to re-confirm a decision the file records. So `SolveOnce` runs the
// document as written and the report's own banner says which extent each side ran at
// (R-rail16-8), which is where that information belongs.

using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// Solves this document ONCE, in place, on the calling thread, and hands back the side of a
    /// comparison it makes up.
    /// </summary>
    /// <remarks>
    /// <b>Not the Fast edit loop and deliberately not on it.</b> <see cref="QueueResolve"/> exists
    /// so the numbers follow the typing and cancels itself on the next keystroke; this is a single
    /// blocking run of a document nobody is editing, which is what a comparison's reference side
    /// is. It touches none of <see cref="Current"/>, <see cref="ByModel"/> or the refusal strip,
    /// because this view model may not be the one on screen.
    /// </remarks>
    /// <param name="kind">Which model to run. The comparison runs both sides at the SAME kind —
    /// a fast reading held against a meshed one measures the two extractors, not the two boards.
    /// </param>
    /// <param name="railName">Which rail, on this document.</param>
    /// <returns>The side, or null where this document has no board or no such rail.</returns>
    public RailComparisonSide? SolveOnce(PdnModelKind kind, string railName)
    {
        if (Board is not { } board) return null;
        if (_document.Rail(railName) is not { } rail) return null;

        string? was = SelectedRailName;
        try
        {
            SelectedRailName = rail.Name;
            if (SelectedRail is not { } selected) return null;

            var run = SolveFunc(BuildRequest(board, kind), System.Threading.CancellationToken.None);
            if (run.Refusal is not null || run.RefusalFor(rail.Name) is not null) return null;

            var dc = run.Rail(rail.Name);
            var sweepRequest = BuildSweepRequest(kind);
            var sweep = sweepRequest is null ? null : SweepFunc(sweepRequest);

            return new RailComparisonSide
            {
                Dc     = dc is null ? null : RailComparisonDc.Of(dc),
                Sweep  = sweep is { Refusal: null } s ? s : null,
                ComputedMountingHenries = ComputedMountingFor(selected),

                // Modes is LEFT EMPTY on purpose. RailComparisonSide wants PdnModeSolver's
                // `PdnMode`; what the window's Find button produces is `PdnPlaneMode`, a different
                // record off a different solve. Converting one to the other here would be this file
                // deciding what a mode IS, which is not its business — so the report's plane-mode
                // section simply says nothing rather than saying something derived.
            };
        }
        finally
        {
            // The selector is user-visible state on the design being judged, so it goes back
            // exactly as it was — a comparison must not move the rail the user was looking at.
            if (was is not null && !string.Equals(was, SelectedRailName, StringComparison.Ordinal))
                SelectedRailName = was;
        }
    }

    /// <summary>The side of the comparison this window IS, without re-solving: what is already on
    /// screen. Null with nothing solved.</summary>
    public RailComparisonSide? CurrentSide()
    {
        if (Current is not { } current || SelectedRail is not { } rail) return null;

        var dc = current.Result.Rail(rail.Name);

        return new RailComparisonSide
        {
            Dc     = dc is null ? null : RailComparisonDc.Of(dc),
            Sweep  = Sweep,
            ComputedMountingHenries = ComputedMountingFor(rail),
        };
    }

    /// <summary>The computed mounting loops for one rail — <see cref="ComputedMounting"/>, which is
    /// private and is the same map the sweep request is built with.</summary>
    private IReadOnlyDictionary<string, double>? ComputedMountingFor(RailSpec rail) =>
        ComputedMounting(rail);
}
