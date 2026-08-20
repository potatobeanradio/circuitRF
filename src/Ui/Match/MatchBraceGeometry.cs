using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.Matching;

/// <summary>One step of a brace outline, in WORLD coordinates.</summary>
/// <param name="Kind">Move, Line, or a quadratic segment.</param>
/// <param name="CX">Quadratic control-point x — unused by Move/Line.</param>
/// <param name="CY">Quadratic control-point y — unused by Move/Line.</param>
/// <param name="X">End-point x.</param>
/// <param name="Y">End-point y.</param>
public readonly record struct MatchBraceStep(MatchBraceStepKind Kind, double CX, double CY, double X, double Y);

/// <summary>What one <see cref="MatchBraceStep"/> does.</summary>
public enum MatchBraceStepKind
{
    /// <summary>Start a new sub-path at (X, Y).</summary>
    Move,

    /// <summary>Straight to (X, Y).</summary>
    Line,

    /// <summary>Quadratic through control (CX, CY) to (X, Y).</summary>
    Quad,
}

/// <summary>
/// The shape of a transform brace (match.md §9.3), as pure geometry.
/// </summary>
/// <remarks>
/// <b>Owner, 2026-08-20:</b> <i>"The transform curly brace rendering in the Match Designer schematic
/// needs aesthetic improvement. It needs to have smooth 'curl' at its left and right edge locations.
/// It also needs a stem from the center of its horizontal line to its rendered text transform
/// name."</i> What it replaces was three straight lines — a bracket, not a brace.
///
/// <para>The outline is one continuous under-brace: a quarter-turn UP at each end (towards the
/// elements the brace is about), a straight run along the top, and a quarter-turn each side into a
/// centre tip pointing DOWN, from which the stem drops to the label. <b>Each turn's control point is
/// the corner itself</b>, which is what makes the curve tangent to both the vertical and the
/// horizontal — the whole difference between a brace and a bracket with rounded corners.</para>
///
/// <para><b>It lives here, in world units, rather than inside the canvas's draw operation</b>, for
/// one reason: a shape asked for on aesthetic grounds has to be inspectable without a running
/// application. The renderer maps each step through its own world-to-screen transform, so the brace
/// scales with the drawing exactly as the glyphs do.</para>
/// </remarks>
public static class MatchBraceGeometry
{
    /// <summary>
    /// The brace spanning <paramref name="x0"/>..<paramref name="x1"/> with its horizontal run at
    /// <paramref name="y"/>. Returns an empty list when the span is too small to draw one.
    /// </summary>
    /// <param name="curl">
    /// The quarter-turn radius. <b>Clamped to a quarter of the span</b>: a brace narrower than four
    /// curls has no straight run left, and shrinking the curl is what keeps a one-element span
    /// readable instead of letting the two halves cross.
    /// </param>
    public static IReadOnlyList<MatchBraceStep> Outline(double x0, double x1, double y, double curl)
    {
        double r = Math.Min(curl, (x1 - x0) / 4.0);
        if (!(r > 0)) return [];

        double xm = (x0 + x1) / 2.0;
        return
        [
            new(MatchBraceStepKind.Move, 0,  0,  x0,     y - r),
            new(MatchBraceStepKind.Quad, x0, y,  x0 + r, y),
            new(MatchBraceStepKind.Line, 0,  0,  xm - r, y),
            new(MatchBraceStepKind.Quad, xm, y,  xm,     y + r),
            new(MatchBraceStepKind.Quad, xm, y,  xm + r, y),
            new(MatchBraceStepKind.Line, 0,  0,  x1 - r, y),
            new(MatchBraceStepKind.Quad, x1, y,  x1,     y - r),
        ];
    }

    /// <summary>
    /// The stem, from the tip of the centre curl down to where the label's baseline goes. Null when
    /// <see cref="Outline"/> would draw nothing.
    /// </summary>
    public static (double X, double Y0, double Y1, double LabelBaselineY)? Stem(
        double x0, double x1, double y, double curl, double stem, double labelDrop)
    {
        double r = Math.Min(curl, (x1 - x0) / 4.0);
        if (!(r > 0)) return null;
        return ((x0 + x1) / 2.0, y + r, y + r + stem, y + r + stem + labelDrop);
    }
}
