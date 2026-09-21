// The net highlighted in railRF's pick list, as the board draws it
// (docs/sonnet-briefs/brief-railrf-19-unreachable-states.md R-rail19-2).
//
// ── WHY THIS IS A DRAW ARGUMENT AND NOT PART OF RailMapScene ───────────────────────────────────
//
// RailPartHighlight's own header, verbatim in its reasoning: R-rail8-13 makes the scene a PURE
// FUNCTION OF THE RESULT, and this is not a result — it is the state of a list box, it changes on
// every arrow key, and no solve produces it or is invalidated by it. It travels the way
// `hiddenLayers` and the part highlight do: alongside the scene, applied at the draw.
//
// ── IT IS A PREVIEW AND IT LOOKS LIKE ONE ──────────────────────────────────────────────────────
//
// R-rail19-2b. The committed rail's copper is a solid outline in Rail.CopperHighlight on the copper
// tab; this is a DASHED outline in Rail.NetPreview, which is a different colour on both variants.
// A preview that looks identical to a committed rail is a preview that makes a user think they
// already pressed the button.
//
// ── IT CARRIES GEOMETRY, NOT A NET NAME ────────────────────────────────────────────────────────
//
// Nothing below the firewall may walk a board's connectivity. Which copper is on `+3V3` is a
// question for Regions, joined to the board netlist; the caller walks, this says where to
// draw. Same rule, same reason, as RailPartHighlight's.

using CircuitRF.Design.Layout;
using Clipper2Lib;

namespace CircuitRF.Render;

/// <summary>
/// One net's copper, outlined on the board because it is highlighted in the pick list and has not
/// been made a rail.
/// </summary>
/// <param name="Label">The net's name, as the list spells it — written beside the outline.</param>
/// <param name="Copper">Its geometry, per drawing layer, DBU. Empty means the net names no copper,
/// which is drawn as nothing at all rather than as a box at the origin.</param>
/// <param name="Bounds">The extent of <paramref name="Copper"/>, DBU.</param>
/// <remarks>
/// <b>Reference equality, deliberately.</b> <see cref="Copper"/> is a list of Clipper path lists and
/// comparing it structurally on every property notification would cost more than the repaint it is
/// meant to save. The one producer — the view model's per-net cache — hands back the SAME instance
/// for the same net, so reference equality is exactly "is this the same preview", which is what
/// <c>RailLayoutOverlay</c> asks.
/// </remarks>
public sealed record RailNetPreview(
    string Label,
    IReadOnlyList<(LayerKey Layer, Paths64 Paths)> Copper,
    Bbox Bounds)
{
    /// <summary>Value equality is reference equality here — see this type's own remarks.</summary>
    public bool Equals(RailNetPreview? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>True where there is nothing to draw — a net the artwork gives no copper.</summary>
    public bool IsEmpty => Copper.Count == 0 || Bounds.IsEmpty;
}
