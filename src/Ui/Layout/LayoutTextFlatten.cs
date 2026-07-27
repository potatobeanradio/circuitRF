// Text-to-polygon flattening (R-lbl-4/R-lbl-6, docs/sonnet-briefs/brief-layout-label-fix-and-text-
// flatten.md). Framework-free — takes glyph contours already extracted from SkiaSharp
// (CircuitRF.Ui.Renderers.LayoutTextOutline.BuildGlyphContours is the ONE place this feature touches
// Skia) as plain CurveShapes, flattens each via the shared LayoutFlattener (no second flattener, per
// the brief's explicit instruction), and resolves nesting — which contour is an outer boundary vs. a
// hole/counter ('O' has one hole, '8' has two, 'i' has two SEPARATE outer contours, not one nested
// inside the other) — via Clipper2's Union/PolyTree64, exactly like every other multi-contour boolean
// result in this codebase (LayoutClipper.FromClipperTree). NEVER infer nesting from contour winding by
// hand: glyph fill rules vary by font, and that is a classic source of filled-in letters (a hole
// silently vanishing because its winding didn't match the assumption).

using Clipper2Lib;

namespace CircuitRF.Ui.Layout;

public static class LayoutTextFlatten
{
    /// <summary>
    /// Flattens a label's already-extracted glyph contours into 0..N <see cref="PolygonShape"/>s, holes
    /// preserved (§3.1a) — one polygon per disjoint outer boundary: 'O' yields one polygon with one
    /// hole, '8' yields one with two holes, 'i' yields two separate polygons (dot + stem, never nested
    /// inside each other). Returns an empty list for no contours (blank text, or a port label the
    /// caller should never have passed in — see <c>LayoutEditorViewModel.FlattenLabelToPolygons</c>).
    /// </summary>
    public static IReadOnlyList<PolygonShape> FlattenContoursToPolygons(
        IReadOnlyList<CurveShape> glyphContours, long tolDbu, LayerKey layer, string? net)
    {
        if (glyphContours.Count == 0) return [];

        var rings = new List<long[]>(glyphContours.Count);
        foreach (var contour in glyphContours)
            rings.AddRange(LayoutFlattener.Flatten(contour, tolDbu));

        var paths = LayoutClipper.RingsToClipperPaths(rings);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, LayoutClipper.Rule);

        var solids = LayoutClipper.FromClipperTree(tree, layer, net);
        var result = new List<PolygonShape>(solids.Count);
        foreach (var s in solids)
            if (s is PolygonShape p) result.Add(p);
        return result;
    }
}
