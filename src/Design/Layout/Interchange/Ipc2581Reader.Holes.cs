// IPC-2581 holes, vias and slots. A hole's platingStatus says what it is, a Span says how far it runs, and a round
// copper pad at the same point is what turns a via's hole into a via. The pairing is DrillViaPairing's, reached through
// the adapter below rather than written a second time: it is the same question a drill file asks of Gerber artwork.
//
// A drill layer's name is never read for a span, although names like Hole1-4 look like one. Only a Span states one.

using System.Xml.Linq;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class Ipc2581Reader
{
    private const string DrillFunction = "DRILL";

    /// <summary>Where a pad stack's holes land when the file declares no drill layer to put them on.</summary>
    internal const string SyntheticDrillLayer = "DRILL";

    /// <param name="Layer">The drill layer a Set placed it on. Null for a pad stack's hole, which states none.</param>
    private sealed record HoleRead(
        long X, long Y, long Diameter, string Status, string? Net, string? Layer, string? From, string? To);

    private readonly List<HoleRead> _holes = [];
    private readonly List<(PathShape Shape, string Layer)> _slots = [];

    private static long Dbu(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private void ReadHole(XElement hole, string? layer, string? net, double scale)
    {
        CountEntity();
        if (Number(Attr(hole, "x")) is not { } x || Number(Attr(hole, "y")) is not { } y
            || Number(Attr(hole, "diameter")) is not { } diameter || diameter <= 0)
        {
            Skipped("hole with no readable position or diameter");
            return;
        }

        var span = Child(hole, "Span");
        _holes.Add(new HoleRead(
            Dbu(x * scale), Dbu(y * scale), Dbu(diameter * scale),
            (Attr(hole, "platingStatus") ?? "").ToUpperInvariant(),
            net, layer,
            span is null ? null : Attr(span, "fromLayer"),
            span is null ? null : Attr(span, "toLayer")));
    }

    /// <summary>A slot is one opening, a path when its shape is one. A cavity cut to a depth does not go through what
    /// it crosses, so it is not an opening on a drill layer at all.</summary>
    private void ReadSlot(XElement slot, string layer, double scale)
    {
        CountEntity();
        const string NotAPath = "slot or cavity whose shape is not a path";

        if (Child(slot, "MaterialCut") is not null || Child(slot, "MaterialLeft") is not null)
        {
            Skipped("cavity cut to a depth");
            return;
        }

        try
        {
            var xf = LocalTransform(slot, scale);
            var feature = slot.Elements().FirstOrDefault(e => e.Name.LocalName is not (
                "Xform" or "Location" or "Fill" or "NonstandardAttribute" or "TextAttribute" or "SpecRef"));
            if (feature is null) { Skipped(NotAPath); return; }

            double featureScale = scale;
            var drawn = feature;
            if (feature.Name.LocalName == "StandardPrimitiveRef"
                && Attr(feature, "id") is { } id && _standard.TryGetValue(id, out var entry)
                && entry.Element.Elements().FirstOrDefault() is { } primitive)
            {
                drawn = primitive;
                featureScale = entry.Scale;
            }

            if (drawn.Name.LocalName == "Oval")
            {
                double width = Required(drawn, "width") * featureScale, height = Required(drawn, "height") * featureScale;
                double run = Math.Abs(width - height) / 2;
                var a = xf.Apply(width >= height ? new Ipc2581Point(-run, 0) : new Ipc2581Point(0, -run));
                var b = xf.Apply(width >= height ? new Ipc2581Point(run, 0) : new Ipc2581Point(0, run));
                _slots.Add((new PathShape
                {
                    Xy = [Dbu(a.X), Dbu(a.Y), Dbu(b.X), Dbu(b.Y)],
                    Width = Dbu(Math.Min(width, height) * xf.Scale),
                    End = PathEndStyle.Round,
                }, layer));
                return;
            }

            if (drawn.Name.LocalName is "Line" or "Polyline")
            {
                var shapes = new List<LayoutShape>();
                PlaceFeature(drawn, xf, featureScale, shapes);
                if (shapes is [PathShape path]) { _slots.Add((path, layer)); return; }
            }

            Skipped(NotAPath);
        }
        catch (FeatureError)
        {
            Skipped(NotAPath);
        }
    }

    /// <summary>Holes, vias and slots become shapes, once the copper they pair with is known and the stackup has
    /// said which copper layer is where.</summary>
    private void ResolveHoles()
    {
        var order = CopperTopToBottom();
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) rank[order[i]] = i;

        var vias = new List<(HoleRead Hole, string Layer, string? From, string? To)>();
        int plainHoles = 0, platedThrough = 0, viaByShortSpan = 0;

        foreach (var hole in _holes)
        {
            string layer = hole.Layer ?? DefaultDrillLayer();
            EnsureDrillLayerDeclared(layer);
            var (from, to) = SpanOf(hole, layer, order, rank);
            bool throughBoard = from is not null && rank[from] == 0 && rank[to!] == order.Count - 1;

            // A lead cannot enter a hole that stops short of a surface, so a span short of the board is not the
            // component hole PLATED otherwise means. Files that write only PLATED have no other word for a via.
            bool shortPlated = hole.Status == "PLATED" && from is not null && !throughBoard;
            if (hole.Status is "VIA" or "VIA_CAPPED" || shortPlated)
            {
                if (shortPlated) viaByShortSpan++;
                vias.Add((hole, layer, from, to));
                continue;
            }

            if (hole.Status == "PLATED") platedThrough++;
            else if (hole.Status != "NONPLATED") Degraded("hole stating no plating status, kept as a hole");
            plainHoles++;
            AddHoleCircle(hole, layer);
        }

        int paired = PairVias(vias, order);

        foreach (var (shape, layer) in _slots)
        {
            EnsureDrillLayerDeclared(layer);
            _board.Shapes.Add(new PcbImportedShape(shape, layer));
        }

        if (_holes.Count > 0)
            _board.Diagnostics.Add(
                $"Holes: {paired} via(s) rebuilt from a hole and its pad, and {plainHoles} plain hole(s)." +
                (viaByShortSpan > 0
                    ? $" {viaByShortSpan} hole(s) this file calls PLATED were read as vias, because their span stops short of the board."
                    : "") +
                (platedThrough > 0
                    ? $" {platedThrough} PLATED through hole(s) stay holes with their pads as copper, so they join no layers. The " +
                      "file does not say whether any of them is really a via."
                    : ""));

        if (_slots.Count > 0)
            _board.Diagnostics.Add($"{_slots.Count} slot(s) were imported as single openings on their layer.");
    }

    /// <summary>The adapter onto <see cref="DrillViaPairing"/>: one call per copper layer, in the order a via's pad is
    /// wanted, so a pad and the layer the via says it lands on cannot disagree. Pairing consumes the pad, and the pads on
    /// the via's other layers stay as the copper they are.</summary>
    /// <returns>How many vias were built.</returns>
    private int PairVias(List<(HoleRead Hole, string Layer, string? From, string? To)> vias, List<string> order)
    {
        if (vias.Count == 0) return 0;

        var copper = new HashSet<string>(order, StringComparer.Ordinal);
        var padsOn = new Dictionary<string, List<GerberImportedShape>>(StringComparer.Ordinal);
        foreach (var imported in _board.Shapes)
        {
            if (imported.Shape is not CircleShape || !copper.Contains(imported.LayerName)) continue;
            if (!padsOn.TryGetValue(imported.LayerName, out var list)) padsOn[imported.LayerName] = list = [];
            list.Add(new GerberImportedShape(imported.Shape, null, null, null));
        }

        var format = ExcellonFormat.Resolve(new DrillFormatDeclarations(), null);
        var consumed = new HashSet<LayoutShape>(ReferenceEqualityComparer.Instance);
        int built = 0;

        foreach (var group in vias.GroupBy(v => (v.Layer, v.From, v.To)))
        {
            var waiting = group.Select(v => v.Hole).ToList();
            foreach (string padLayer in PadLayersOf(group.Key.From, group.Key.To, order))
            {
                if (waiting.Count == 0) break;
                if (!padsOn.TryGetValue(padLayer, out var pads) || pads.Count == 0) continue;

                var result = DrillViaPairing.Pair(
                    pads,
                    new ExcellonReadResult
                    {
                        Hits = [.. waiting.Select(h => new DrillHit(h.X, h.Y, 0, h.Diameter, true, "ViaDrill"))],
                        Format = format,
                    },
                    default(LayerKey),
                    dbuPerMicron: _dbuPerMicron);

                var at = waiting.GroupBy(h => (h.X, h.Y)).ToDictionary(g => g.Key, g => new Queue<HoleRead>(g));
                foreach (var via in result.Vias)
                {
                    var hole = at[(via.X, via.Y)].Dequeue();
                    via.Net = hole.Net ?? via.Net;
                    _board.Shapes.Add(new PcbImportedShape(
                        via, group.Key.Layer, padLayer, group.Key.From, group.Key.To));
                    built++;
                }

                var kept = new HashSet<LayoutShape>(result.RemainingArtwork.Select(a => a.Shape), ReferenceEqualityComparer.Instance);
                foreach (var pad in pads)
                    if (!kept.Contains(pad.Shape)) consumed.Add(pad.Shape);
                padsOn[padLayer] = [.. result.RemainingArtwork];
                waiting = [.. at.Values.SelectMany(q => q)];
            }

            foreach (var hole in waiting)
            {
                Degraded("via with no round copper pad at its point, kept as a hole beside its copper");
                AddHoleCircle(hole, group.Key.Layer);
            }
        }

        // Its copper now belongs to the via, and drawing it here as well would draw it twice.
        if (consumed.Count > 0) _board.Shapes.RemoveAll(s => consumed.Contains(s.Shape));
        return built;
    }

    /// <summary>Where a via's pad is looked for: the top of its span, then the bottom, then what lies between.</summary>
    private static IEnumerable<string> PadLayersOf(string? from, string? to, List<string> order)
    {
        if (from is null || to is null) return order;
        int a = order.IndexOf(from), b = order.IndexOf(to);
        return new[] { from, to }.Concat(order.Skip(a + 1).Take(b - a - 1));
    }

    private void AddHoleCircle(HoleRead hole, string layer) =>
        _board.Shapes.Add(new PcbImportedShape(new CircleShape
        {
            Cx = hole.X,
            Cy = hole.Y,
            R = hole.Diameter / 2,
            Net = hole.Status == "NONPLATED" ? null : hole.Net,
        }, layer));

    /// <summary>A hole's own span, then its drill layer's, and with neither it runs through the board. Top first, by
    /// the copper's order. Null when a span names something that is not two different copper layers.</summary>
    private (string? From, string? To) SpanOf(
        HoleRead hole, string layer, List<string> order, Dictionary<string, int> rank)
    {
        string? from = hole.From, to = hole.To;
        if (from is null && to is null
            && _layers.FirstOrDefault(l => l.Name == layer) is { } declared
            && Child(declared.Element, "Span") is { } span)
        {
            from = Attr(span, "fromLayer");
            to = Attr(span, "toLayer");
        }

        if (from is null && to is null)
            return order.Count >= 2 ? (order[0], order[^1]) : (null, null);

        if (from is null || to is null || !rank.TryGetValue(from, out int a) || !rank.TryGetValue(to, out int b) || a == b)
        {
            Degraded("hole whose span does not name two copper layers, imported with no span");
            return (null, null);
        }
        return a < b ? (from, to) : (to, from);
    }

    private string DefaultDrillLayer() =>
        _layers.FirstOrDefault(l => l.Function == DrillFunction)?.Name ?? SyntheticDrillLayer;

    private void EnsureDrillLayerDeclared(string layer)
    {
        if (_layers.Exists(l => l.Name == layer)) return;
        _layers.Add(new LayerDeclaration(layer, DrillFunction, "", false, new XElement("Layer")));
    }
}
