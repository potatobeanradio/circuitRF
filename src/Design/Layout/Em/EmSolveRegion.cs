// The EM SOLVE REGION — "solve only what is inside this box" (round-7 designer feedback, 2026-09-24).
//
// A board imported from Gerber is the whole board, and the question asked of it is usually about one
// trace: is this 50 Ω line 50 Ω. Before this existed the answer was to Clip the layout and save the
// result as a second .clay — a copy of the artwork that stops following the original the moment
// either is edited. The region lives in the .cem instead, beside every other thing the solver is
// told, so the layout is untouched and each EM setup can look at its own part of the board.
//
// ONE PLACE, BOTH KERNELS. The clip is applied inside EmGeometry.Flatten's result, which is the one
// door the run, the preflight, the panel's Refresh, its mesh preview and `circuitrf explain` all pass
// through on the way to either extractor. A clip applied in only one of those would be a panel that
// previews one problem and a run that solves another.
//
// A RECTANGLE, stored in MICROMETRES. A rectangle because it is what a drag on a canvas makes and it
// is what the question needs; LayoutBooleans.Clip takes any stencil shape, so a polygon would cost
// only a persistence shape and a drawing gesture, not a second clipper. Micrometres rather than DBU
// because the .cem does not know the layout's DBU and a document that means a different box when
// the layout's resolution changes is a document that silently lies.

using System.Globalization;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// An axis-aligned rectangle in layout coordinates, micrometres, that bounds what an EM setup solves.
/// Always normalised (min ≤ max on both axes) by <see cref="FromCorners"/>.
/// </summary>
public sealed record EmSolveRegion(double XMinUm, double YMinUm, double XMaxUm, double YMaxUm)
{
    /// <summary>The rectangle two opposite corners span, in either order.</summary>
    public static EmSolveRegion FromCorners(double x1Um, double y1Um, double x2Um, double y2Um) =>
        new(Math.Min(x1Um, x2Um), Math.Min(y1Um, y2Um), Math.Max(x1Um, x2Um), Math.Max(y1Um, y2Um));

    /// <summary>The same rectangle from two DBU corners of a layout drawn at
    /// <paramref name="dbuPerMicron"/>.</summary>
    public static EmSolveRegion FromDbu(long x1, long y1, long x2, long y2, int dbuPerMicron)
    {
        double s = dbuPerMicron;
        return FromCorners(x1 / s, y1 / s, x2 / s, y2 / s);
    }

    /// <summary>True when the rectangle encloses no area — a click rather than a drag, or a
    /// hand-edited file with equal corners. Such a region would clip away everything.</summary>
    public bool IsDegenerate => !(XMaxUm > XMinUm) || !(YMaxUm > YMinUm);

    /// <summary>The rectangle in DBU at <paramref name="dbuPerMicron"/>, rounded outward so a
    /// region drawn to the edge of a feature never shaves a DBU off it.</summary>
    public (long XMin, long YMin, long XMax, long YMax) ToDbu(int dbuPerMicron) =>
        ((long)Math.Floor(XMinUm * dbuPerMicron), (long)Math.Floor(YMinUm * dbuPerMicron),
         (long)Math.Ceiling(XMaxUm * dbuPerMicron), (long)Math.Ceiling(YMaxUm * dbuPerMicron));

    /// <summary>Whether a DBU point lies inside the CLOSED rectangle — a port exactly on the edge is
    /// inside, because a line cut at the region's edge ends exactly there.</summary>
    public bool ContainsDbu(long x, long y, int dbuPerMicron)
    {
        var (x0, y0, x1, y1) = ToDbu(dbuPerMicron);
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    /// <summary>"(x0, y0) – (x1, y1) µm", the spelling every sentence about the region uses.</summary>
    public string Describe() =>
        $"({Num(XMinUm)}, {Num(YMinUm)}) – ({Num(XMaxUm)}, {Num(YMaxUm)}) µm, " +
        $"{Num(XMaxUm - XMinUm)} × {Num(YMaxUm - YMinUm)} µm";

    private static string Num(double um) => um.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>What <see cref="EmSolveRegionClip.Apply"/> did to a flattened shape list.</summary>
/// <param name="Shapes">The shapes the extractors see.</param>
/// <param name="Note">One sentence for the run's notes: the region and the kept/cut/dropped counts.
/// Null when no region is set.</param>
/// <param name="Refusal">Non-null when the region cannot be solved as drawn — a degenerate box, or a
/// port label outside it. Names the port.</param>
public sealed record EmSolveRegionClipResult(
    IReadOnlyList<LayoutShape> Shapes, string? Note, string? Refusal);

/// <summary>
/// Applies an <see cref="EmSolveRegion"/> to flattened geometry, through the SAME per-operand clip the
/// layout editor's Clip command runs (<see cref="LayoutBooleans.Clip"/>), so the EM problem is exactly
/// what Clip-and-save would have produced — without the second file.
/// </summary>
public static class EmSolveRegionClip
{
    /// <param name="shapes">Flattened, world-coordinate shapes.</param>
    /// <param name="portLabels">The layout's own TOP-LEVEL shapes, which is where port labels live
    /// and where <see cref="EmPortExtraction"/> reads them.</param>
    public static EmSolveRegionClipResult Apply(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutShape> portLabels,
        EmSolveRegion? region, int dbuPerMicron, Technology? tech)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        if (region is null) return new EmSolveRegionClipResult(shapes, null, null);

        if (region.IsDegenerate)
            return new EmSolveRegionClipResult(shapes, null,
                $"This EM setup's solve region {region.Describe()} encloses no area, so it would " +
                "clip away every shape. Draw the region again, or clear it to solve the whole layout.");

        // Ports first: a port outside the box is a question the user has to answer, and asking it
        // before clipping keeps the refusal about what they drew rather than about what was cut.
        foreach (var (number, label) in EmPortExtraction.NumberPorts(portLabels))
        {
            if (region.ContainsDbu(label.X, label.Y, dbuPerMicron)) continue;
            string name = label.Text is { Length: > 0 } t ? $" ('{t}')" : "";
            return new EmSolveRegionClipResult(shapes, null,
                $"Port {number}{name} at ({Um(label.X, dbuPerMicron)}, {Um(label.Y, dbuPerMicron)}) µm " +
                $"lies outside this EM setup's solve region {region.Describe()}. Only geometry inside " +
                "the region is solved, so this port would have no metal to excite. Move the port " +
                "inside the region, enlarge the region, or clear it to solve the whole layout.");
        }

        var (x0, y0, x1, y1) = region.ToDbu(dbuPerMicron);
        var stencil = new RectShape { X1 = x0, Y1 = y0, X2 = x1, Y2 = y1 };

        // Clip operands go through the editor's clip; anything else (a bitmap) is not geometry any
        // extractor reads and passes through untouched rather than being cropped.
        var operands = new List<LayoutShape>(shapes.Count);
        var passThrough = new List<LayoutShape>();
        foreach (var s in shapes)
            (LayoutBooleans.IsClipOperand(s) ? operands : passThrough).Add(s);

        var clipped = LayoutBooleans.Clip(operands, stencil, tech);

        var kept = new List<LayoutShape>(clipped.Shapes.Count + passThrough.Count);
        kept.AddRange(clipped.Shapes);
        kept.AddRange(passThrough);

        string note =
            $"Solve region {region.Describe()}: {clipped.OperandsUntouched + passThrough.Count} " +
            $"shape(s) kept whole, {clipped.OperandsChanged} cut at the region's edge, " +
            $"{clipped.OperandsRemoved} outside it and left out of the EM problem. A via is kept or " +
            "left out whole, by where its centre is.";

        return new EmSolveRegionClipResult(kept, note, null);
    }

    private static string Um(long dbu, int dbuPerMicron) =>
        (dbu / (double)dbuPerMicron).ToString("0.###", CultureInfo.InvariantCulture);
}
