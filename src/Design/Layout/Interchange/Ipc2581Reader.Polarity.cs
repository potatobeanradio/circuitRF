// What is copper. Each layer's features are held in document order with what they do, and turned into shapes
// once the whole step has been read, because a layer's own polarity and the step's profile can be stated
// anywhere in the file. A layer nothing erases on costs no boolean at all.
//
// Erasing keeps each shape's net: every surviving piece carries the net of the shape it came from, which is
// what a composite of the whole layer would lose. Only a shape an eraser's box touches is clipped, found through
// the layout spatial index, so a few hundred antipads do not rewrite a layer. Consecutive erasers go in as one
// batch, so a plane with thousands of antipads is one Clipper call and not thousands.

using System.Xml.Linq;
using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class Ipc2581Reader
{
    private sealed class LayerOps
    {
        public List<LayoutShape> Shapes { get; } = [];
        public List<bool> Erases { get; } = [];
    }

    /// <summary>Stands in for a shape an eraser removed whole, so no other shape's index moves. Far outside any
    /// board, so no query finds it.</summary>
    private static readonly RectShape Removed = new()
    {
        X1 = 1L << 50, Y1 = 1L << 50, X2 = (1L << 50) + 1, Y2 = (1L << 50) + 1,
    };

    private void AddOps(string layer, List<LayoutShape> shapes, string? net, bool erase)
    {
        if (shapes.Count == 0) return;
        if (!_ops.TryGetValue(layer, out var ops)) _ops[layer] = ops = new LayerOps();
        foreach (var shape in shapes)
        {
            shape.Net = net;
            ops.Shapes.Add(shape);
            ops.Erases.Add(erase);
        }
    }

    private void ResolveLayers()
    {
        var declared = new HashSet<string>(_layers.Select(l => l.Name), StringComparer.Ordinal);
        foreach (var name in _ops.Keys.Where(n => !declared.Contains(n)).ToList())
        {
            // Its artwork is still imported, on a layer of that name that can be nothing but artwork.
            _board.Diagnostics.Add($"Layer '{name}' has features but no Layer declaration, so it was imported as non-copper artwork.");
            _layers.Add(new LayerDeclaration(name, "", "", false, new XElement("Layer")));
        }

        foreach (var layer in _layers.DistinctBy(l => l.Name))
        {
            if (layer.Function is "RESISTIVE" or "CAPACITIVE")
                Degraded($"{layer.Function} layer '{layer.Name}' imported as non-copper artwork");

            bool copper = IsCopperFunction(layer.Function);
            if (layer.Negative && !copper && _ops.ContainsKey(layer.Name))
                Degraded($"non-copper layer '{layer.Name}' states negative polarity and was drawn as its features");

            _ops.TryGetValue(layer.Name, out var ops);
            if (layer.Negative && copper)
            {
                var outline = NegativeLayerOutline(layer);
                if (outline.Count == 0)
                {
                    _board.Diagnostics.Add(
                        $"Layer '{layer.Name}' is a negative plane with no usable profile, on the layer or its step, so what is " +
                        "copper on it cannot be known. That layer was not imported.");
                    continue;
                }
                var plane = Resolve(outline, ops, invert: true);
                NamePlanePieces(layer.Name, plane);
                AddResolved(layer.Name, plane);
            }
            else if (ops is not null)
            {
                AddResolved(layer.Name, ops.Erases.Contains(true) ? Resolve([], ops, invert: false) : ops.Shapes);
            }
        }

        AddProfileAsOutline();
        _ops.Clear();
    }

    private void AddResolved(string layer, IReadOnlyList<LayoutShape> shapes)
    {
        foreach (var shape in shapes)
            _board.Shapes.Add(new PcbImportedShape(shape, layer));
    }

    /// <summary>The copper a negative layer starts from: its own profile when it states one, otherwise the step's.
    /// Copper is this minus the layer's features.</summary>
    private List<LayoutShape> NegativeLayerOutline(LayerDeclaration layer)
    {
        var profiles = layer.Element.Elements().Where(e => e.Name.LocalName == "Profile").ToList();
        if (profiles.Count == 0 && _stepProfile is not null) profiles = [_stepProfile];

        var shapes = new List<LayoutShape>();
        foreach (var profile in profiles)
            foreach (var region in ProfileRegions(profile))
                shapes.AddRange(Ipc2581Shapes.Place(region, Ipc2581Transform.Identity, _flattenTolDbu));
        return shapes;
    }

    private List<Ipc2581Region> ProfileRegions(XElement profile)
    {
        try { return [ContourRegion(profile, EcadScale())]; }
        catch (FeatureError e)
        {
            Skipped($"Profile with {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Plays a layer's features in document order onto an image. <paramref name="invert"/> is a negative layer, where
    /// a feature clears and a NEGATIVE set, being the opposite of that, puts copper back.
    /// </summary>
    private List<LayoutShape> Resolve(List<LayoutShape> start, LayerOps? ops, bool invert)
    {
        var image = new List<LayoutShape>(start);
        if (ops is null) return image;

        LayoutSpatialIndex? index = null;
        int i = 0;
        while (i < ops.Shapes.Count)
        {
            bool erase = ops.Erases[i] != invert;
            int j = i;
            while (j < ops.Shapes.Count && (ops.Erases[j] != invert) == erase) j++;

            if (erase)
            {
                EraseBatch(image, ref index, ops.Shapes.GetRange(i, j - i));
            }
            else
            {
                int first = image.Count;
                for (int k = i; k < j; k++) image.Add(ops.Shapes[k]);
                index?.Apply(image, LayoutChangeInfo.Appended(first, j - i));
            }
            i = j;
        }

        image.RemoveAll(s => ReferenceEquals(s, Removed));
        return image;
    }

    private void EraseBatch(List<LayoutShape> image, ref LayoutSpatialIndex? index, List<LayoutShape> erasers)
    {
        if (image.Count == 0) return;
        index ??= new LayoutSpatialIndex();

        // Which erasers each shape's box meets. A sorted map, so the pieces come out in the file's order.
        var touching = new SortedDictionary<int, Paths64>();
        foreach (var eraser in erasers)
        {
            if (!LayoutBooleans.IsClipperOperand(eraser)) continue;
            var paths = LayoutClipper.ToClipperPaths(eraser, LayoutFlattener.ResolveTolDbu(eraser, null));
            if (paths.Count == 0) continue;

            var bounds = Clipper.GetBounds(paths);
            var box = new Bbox(bounds.left, bounds.top, bounds.right, bounds.bottom);
            foreach (int target in index.QueryIntersecting(image, box))
            {
                if (ReferenceEquals(image[target], Removed)) continue;
                if (!touching.TryGetValue(target, out var clip)) touching[target] = clip = new Paths64();
                clip.AddRange(paths);
            }
        }

        var replaced = new List<int>();
        var added = new List<LayoutShape>();
        foreach (var (target, clip) in touching)
        {
            var pieces = Clipped(image[target], clip);
            if (pieces is null) continue;     // the boxes met and the shapes do not: left as it was drawn
            replaced.Add(target);
            image[target] = pieces.Count > 0 ? pieces[0] : Removed;
            for (int p = 1; p < pieces.Count; p++) added.Add(pieces[p]);
        }

        if (replaced.Count > 0) index.Apply(image, LayoutChangeInfo.Updated(replaced));
        if (added.Count > 0)
        {
            int first = image.Count;
            image.AddRange(added);
            index.Apply(image, LayoutChangeInfo.Appended(first, added.Count));
        }
    }

    /// <summary>A VOID fill: it clears what the same set has already drawn, and nothing else on the layer.</summary>
    private void EraseWithin(List<LayoutShape> drawn, Ipc2581Piece piece, Ipc2581Transform xf)
    {
        var clip = new Paths64();
        foreach (var shape in Ipc2581Shapes.Place(piece, xf, _flattenTolDbu))
            clip.AddRange(LayoutClipper.ToClipperPaths(shape, LayoutFlattener.ResolveTolDbu(shape, null)));
        if (clip.Count == 0) return;

        var kept = new List<LayoutShape>(drawn.Count);
        foreach (var shape in drawn)
        {
            var pieces = Clipped(shape, clip);
            if (pieces is null) kept.Add(shape);
            else kept.AddRange(pieces);
        }
        drawn.Clear();
        drawn.AddRange(kept);
    }

    /// <summary>What is left of a shape, each piece keeping the shape's net and part; null when the clip does not
    /// reach it, so a circle a box only came near stays a circle.</summary>
    private static List<LayoutShape>? Clipped(LayoutShape shape, Paths64 clip)
    {
        if (!LayoutBooleans.IsClipperOperand(shape)) return null;

        var subject = LayoutClipper.ToClipperPaths(shape, LayoutFlattener.ResolveTolDbu(shape, null));
        if (Clipper.Intersect(subject, clip, LayoutClipper.Rule).Count == 0) return null;

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Difference, subject, clip, tree, LayoutClipper.Rule);
        var pieces = new List<LayoutShape>();
        foreach (var piece in LayoutClipper.FromClipperTree(tree, shape.Layer, shape.Net))
        {
            piece.Component = shape.Component;
            piece.Pin = shape.Pin;
            pieces.Add(piece);
        }
        return pieces;
    }

    /// <summary>
    /// The step's profile, drawn as a line on the file's outline layer when that layer has no features of its own. A
    /// file very often states its board's shape only here, and an imported board with no outline layer has no outline
    /// for anything that looks for one. The line's width means nothing: it only has to be visible.
    /// </summary>
    private void AddProfileAsOutline()
    {
        if (_stepProfile is null) return;

        var outlineLayer = _layers.FirstOrDefault(l => IsOutlineFunction(l.Function));
        if (outlineLayer is not null && _ops.ContainsKey(outlineLayer.Name)) return;

        var regions = ProfileRegions(_stepProfile);
        if (regions.Count == 0) return;

        string name = outlineLayer?.Name ?? SyntheticOutlineLayer;
        if (outlineLayer is null)
            _layers.Add(new LayerDeclaration(name, "BOARD_OUTLINE", "NONE", false, new XElement("Layer")));

        double width = _dbuPerMicron * OutlineLineWidthMicrons;
        foreach (var region in regions)
            foreach (var ring in new[] { region.Outer }.Concat(region.Holes))
                AddResolved(name, [.. Ipc2581Shapes.Place(
                    new Ipc2581Stroke(ring, width, PathEndStyle.Round, Closed: true), Ipc2581Transform.Identity, _flattenTolDbu)]);
    }

    private const double OutlineLineWidthMicrons = 100;
}
