// ================================================================
//  HarmonicaMarkerZOrder.cs  —  R2A §3 (R-h9r2-5)
//
//  ONE comparer/rank function on (Side, Band), used identically by the renderer (draws lowest rank
//  first, so the highest-ranked marker ends up drawn LAST — on top) and by HarmonicaHitTest.Resolve's
//  marker pass (prefers the topmost-rank candidate among everything within grab radius, falling back
//  to nearest only to break a tie at equal rank). Before this, HarmonicaHitTest picked "nearest within
//  radius" while the renderer drew d.Markers in plain list order — the two could disagree about what
//  was visually on top, which is exactly the bug this file closes.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Harmonica;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// The marker z-order: default rendering order, highest (rendered last / on top) first, is
/// <c>L1 &gt; L2 &gt; L3 &gt; S1 &gt; S2</c>. A marker the user has interacted with — grabbed, either
/// by its own round marker or by its intrinsic glyph — is promoted to the top for the SESSION
/// (<see cref="HarmonicaViewModel.TopmostMarker"/>), which this class reads but never writes and
/// never persists.
/// </summary>
public static class HarmonicaMarkerZOrder
{
    /// <summary>The default order, LOWEST first (drawn first / grabbed last).</summary>
    private static readonly (TerminationSideKind Side, int Band)[] DefaultOrderLowestFirst =
    [
        (TerminationSideKind.Source, 2),
        (TerminationSideKind.Source, 1),
        (TerminationSideKind.Load,   3),
        (TerminationSideKind.Load,   2),
        (TerminationSideKind.Load,   1),
    ];

    /// <summary>
    /// This marker's rank. Higher is drawn LATER (on top) and preferred FIRST by the hit test. A
    /// marker matching <paramref name="topmost"/> by reference always ranks above every other marker,
    /// regardless of the default (Side, Band) order — that is the whole of what "click to promote"
    /// means. A band not named in the default table (there is none today; future-proofing only) sorts
    /// beneath every named one rather than colliding with them all at rank zero.
    /// </summary>
    public static int RankOf(HarmonicaMarker marker, HarmonicaMarker? topmost)
    {
        if (topmost is not null && ReferenceEquals(marker, topmost))
            return int.MaxValue;

        for (int i = 0; i < DefaultOrderLowestFirst.Length; i++)
            if (DefaultOrderLowestFirst[i].Side == marker.Side && DefaultOrderLowestFirst[i].Band == marker.Band)
                return i + 1;

        return 0;
    }

    /// <summary>
    /// <paramref name="markers"/> sorted lowest rank first — the RENDER order, so that
    /// <c>foreach</c>ing and drawing each one leaves the highest-ranked marker painted last, on top of
    /// every other. Stable on ties (there are none among distinct (Side, Band) pairs, and
    /// <see cref="ReferenceEquals"/> promotion is exclusive to one marker), so this never reorders two
    /// markers the rank function considers equal.
    /// </summary>
    public static IReadOnlyList<HarmonicaMarker> DrawOrder(
        IReadOnlyList<HarmonicaMarker> markers, HarmonicaMarker? topmost)
        => [.. markers.OrderBy(m => RankOf(m, topmost))];
}
