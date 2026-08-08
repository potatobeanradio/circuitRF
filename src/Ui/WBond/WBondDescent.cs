using System;
using System.Collections.Generic;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Places the wire overlay into a sub-cell's coordinate frame when the layout editor is pushed in
/// (wbond.md WB27).
///
/// <h3>Why this exists at all</h3>
/// <para>The owner's motivating case is jumping into a cell to nudge a bond pad <i>while watching the
/// wires that land on it</i>. That only works if the wires are drawn in the sub-cell's own frame —
/// the pad moved by ten microns, and the wire foot has to appear to stay put relative to it. Drawing
/// the wires in world coordinates over a canvas showing sub-cell coordinates would put them
/// somewhere arbitrary, which is worse than not drawing them.</para>
///
/// <h3>The wires are a LOCKED reference at depth</h3>
/// <para>They are dimmed and not selectable (<see cref="DimmedAlpha"/>). Editing a wire from inside a
/// sub-cell it does not belong to is ambiguous about which <i>instance</i> of that cell is being
/// edited — an array of five placements would offer five equally valid answers. The user ascends to
/// edit them.</para>
///
/// <h3>Stated limitation</h3>
/// <para>The transform composes in database units, so it is exact only while every level of the
/// descent shares one resolution. <see cref="CanPlace"/> checks the two ends and refuses otherwise
/// rather than composing across a scale change and drawing at a silently wrong offset.</para>
/// </summary>
public static class WBondDescent
{
    /// <summary>How far the locked reference wires are knocked back at depth.</summary>
    public const byte DimmedAlpha = 90;

    /// <summary>
    /// True when the wire overlay can be placed into <paramref name="leaf"/>'s frame: the chain is
    /// complete (every pushed frame recorded its instance) and the resolutions agree.
    /// </summary>
    public static bool CanPlace(LayoutDocument document, LayoutView? root, LayoutView? leaf)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.NavDepth == 0) return true;                 // at the base — nothing to transform
        if (!document.DescentChainIsComplete) return false;
        if (root is null || leaf is null) return false;

        return root.DbuPerMicron == leaf.DbuPerMicron;
    }

    /// <summary>
    /// Maps a world-space wire point (nanometres, the base cell's frame) into the frame reached by
    /// <paramref name="chain"/>.
    ///
    /// <para>Applies each level's INVERSE transform in descent order — outermost first — which is what
    /// "descend" means. <c>LayoutInstanceTransform.InverseTransformPoint</c> is the same routine the
    /// layout editor's own hit-test and snap query use to push a cursor down a hierarchy (R-snp-13),
    /// so a wire foot lands exactly where a click at that point would.</para>
    /// </summary>
    public static (double XNm, double YNm) ToFrame(
        long xNm, long yNm, IReadOnlyList<(LayoutInstance Instance, int Row, int Col)> chain, int dbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(chain);

        double x = WBondSnap.ToDbu(xNm, dbuPerMicron);
        double y = WBondSnap.ToDbu(yNm, dbuPerMicron);

        foreach (var (instance, row, col) in chain)
            (x, y) = LayoutInstanceTransform.InverseTransformPoint(x, y, instance, row, col);

        double scale = dbuPerMicron <= 0 ? 1.0 : 1000.0 / dbuPerMicron;
        return (x * scale, y * scale);
    }

    /// <summary>
    /// The transform to hand the renderer, or null at the base level (where world coordinates already
    /// are the frame and the renderer should do no work at all).
    /// </summary>
    public static Func<long, long, (double X, double Y)>? FrameTransform(
        IReadOnlyList<(LayoutInstance Instance, int Row, int Col)> chain, int dbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0) return null;

        return (x, y) => ToFrame(x, y, chain, dbuPerMicron);
    }
}
