// IPC-2581 features to pieces: the dictionaries, the primitives, lines, polygons and how a Set or a pad stack
// places them. Each section's numbers are read at that section's own unit, so a dictionary in microns under a
// CadHeader in inches is ordinary.

using System.Xml;
using System.Xml.Linq;
using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class Ipc2581Reader
{
    private sealed class DictionaryEntry(XElement element, double scale)
    {
        public XElement Element { get; } = element;
        public double Scale { get; } = scale;
        public IReadOnlyList<Ipc2581Piece>? Pieces { get; set; }
    }

    private sealed record LineDescription(double Width, PathEndStyle End);

    /// <summary>A feature whose attributes cannot be read draws nothing and is counted by what was wrong.</summary>
    private sealed class FeatureError(string what) : Exception(what);

    private readonly Dictionary<string, DictionaryEntry> _standard = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DictionaryEntry> _user = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DictionaryEntry> _lineDescs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DictionaryEntry> _fillDescs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanding = new(StringComparer.Ordinal);

    private void ReadDictionary(XmlReader xml, string section, Dictionary<string, DictionaryEntry> target)
    {
        string? units = xml.GetAttribute("units");
        double? scale = ScaleOf(units);
        Children(xml, name =>
        {
            if (!name.StartsWith("Entry", StringComparison.Ordinal)) { Unknown(name); xml.Skip(); return; }
            var entry = Tree(xml);
            if (scale is null)
                throw new Refusal(units is { Length: > 0 }
                    ? $"{section} states the units \"{units}\", which is not MILLIMETER, MICRON or INCH. Guessing would differ by a factor of 25.4 or 1,000. Nothing was imported."
                    : $"{section} states no units and has entries. Guessing would differ by a factor of 25.4 or 1,000. Nothing was imported.");
            if (Attr(entry, "id") is { Length: > 0 } id) target.TryAdd(id, new DictionaryEntry(entry, scale.Value));
        });
    }

    // ── Placement: a Set, a pad stack, and the elements inside them ─────────────────────────────

    private void ReadSet(XElement set, string layer, StepInfo step)
    {
        bool negative = string.Equals(Attr(set, "polarity"), "NEGATIVE", StringComparison.OrdinalIgnoreCase);
        string? net = NetName(Attr(set, "net"), step.Name);
        double scale = EcadScale();

        var drawn = new List<LayoutShape>();
        foreach (var child in set.Elements())
        {
            string name = child.Name.LocalName;
            switch (name)
            {
                case "Features" or "Pad" or "BadBoardMark" or "GlobalFiducial" or "GoodPanelMark" or "LocalFiducial":
                {
                    int first = drawn.Count;
                    PlaceElement(child, Ipc2581Transform.Identity, scale, drawn);
                    if (name == "Pad") StatePart(child, null, drawn, first, step);
                    if (!negative && net is not null) NoteThermal(child, layer, net, scale);
                    break;
                }
                case "Fiducial":
                    foreach (var mark in child.Elements()) PlaceElement(mark, Ipc2581Transform.Identity, scale, drawn);
                    break;
                case "Hole":
                    ReadHole(child, layer, net, scale);
                    break;
                case "SlotCavity":
                    ReadSlot(child, layer, scale);
                    break;
                case "ColorRef" or "Color" or "NonstandardAttribute" or "SpecRef" or "TextAttribute":
                    break;
                default:
                    PlaceFeature(child, Ipc2581Transform.Identity, scale, drawn);
                    break;
            }
        }
        AddOps(layer, drawn, net, negative);
    }

    /// <summary>The second form pads and holes take: at step level, each pad stating its own layer.</summary>
    private void ReadPadStack(XElement padStack, StepInfo step)
    {
        string? net = NetName(Attr(padStack, "net"), step.Name);
        double scale = EcadScale();
        var stackPin = Child(padStack, "PinRef");
        foreach (var child in padStack.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "LayerPad":
                {
                    string layer = Attr(child, "layerRef") ?? "";
                    if (layer.Length == 0) { Skipped("pad of a pad stack that names no layer"); break; }
                    var drawn = new List<LayoutShape>();
                    PlaceElement(child, Ipc2581Transform.Identity, scale, drawn);
                    StatePart(child, stackPin, drawn, 0, step);
                    AddOps(layer, drawn, net, erase: false);
                    break;
                }
                case "LayerHole":
                    ReadHole(child, null, net, scale);
                    break;
                case "NonstandardAttribute" or "SpecRef" or "TextAttribute" or "PinRef":
                    break;
                default:
                    Unknown(child.Name.LocalName);
                    break;
            }
        }
    }

    /// <summary>An element that places what it holds: its Xform and Location apply to every feature child.</summary>
    private void PlaceElement(XElement element, Ipc2581Transform parent, double scale, List<LayoutShape> sink)
    {
        Ipc2581Transform xf;
        try { xf = parent.After(LocalTransform(element, scale)); }
        catch (FeatureError e)
        {
            Skipped($"{element.Name.LocalName} with {e.Message}");
            return;
        }

        bool anyFeature = false;
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Xform" or "Location" or "PinRef" or "NonstandardAttribute" or "TextAttribute" or "ColorRef" or "Color"
                    or "SpecRef":
                    break;
                default:
                    anyFeature = true;
                    PlaceFeature(child, xf, scale, sink);
                    break;
            }
        }

        // The artwork is the geometry: a pad that names only its padstack definition states no copper of its own.
        if (!anyFeature) Skipped($"{element.Name.LocalName} with no shape of its own");
    }

    private void PlaceFeature(XElement feature, Ipc2581Transform xf, double scale, List<LayoutShape> sink)
    {
        if (feature.Name.LocalName is "Features") { PlaceElement(feature, xf, scale, sink); return; }

        IReadOnlyList<Ipc2581Piece> pieces;
        try { pieces = PiecesOf(feature, scale); }
        catch (FeatureError e)
        {
            CountEntity();
            Skipped($"{feature.Name.LocalName} with {e.Message}");
            return;
        }

        foreach (var piece in pieces)
        {
            CountEntity();
            if (piece.Erase) EraseWithin(sink, piece, xf);
            else if (piece is Ipc2581Region { Holes.Count: > 1 } region)
            {
                var islands = new List<LayoutShape>();
                sink.AddRange(Ipc2581Shapes.Place(WithoutNestedCutouts(region, xf, islands), xf, _flattenTolDbu));
                sink.AddRange(islands);
                _cutoutIslands.UnionWith(islands);
            }
            else sink.AddRange(Ipc2581Shapes.Place(piece, xf, _flattenTolDbu));
        }
    }

    private Ipc2581Transform LocalTransform(XElement element, double scale)
    {
        double x = 0, y = 0, xOffset = 0, yOffset = 0, rotation = 0, factor = 1;
        bool mirror = false;
        if (Child(element, "Location") is { } location)
        {
            x = Optional(location, "x", 0) * scale;
            y = Optional(location, "y", 0) * scale;
        }
        if (Child(element, "Xform") is { } xform)
        {
            xOffset = Optional(xform, "xOffset", 0) * scale;
            yOffset = Optional(xform, "yOffset", 0) * scale;
            rotation = Optional(xform, "rotation", 0);
            factor = Optional(xform, "scale", 1);
            mirror = Bool(Attr(xform, "mirror"));
        }
        return Ipc2581Transform.Of(xOffset, yOffset, rotation, mirror, factor, x, y);
    }

    // ── Features to pieces ─────────────────────────────────────────────────────────────────────

    private IReadOnlyList<Ipc2581Piece> PiecesOf(XElement element, double scale)
    {
        string name = element.Name.LocalName;
        switch (name)
        {
            case "StandardPrimitiveRef": return Resolved(element, _standard, "standard primitive");
            case "UserPrimitiveRef": return Resolved(element, _user, "user primitive");
            case "UserSpecial": return UserSpecialPieces(element, scale);

            case "Line":
            {
                var line = RequiredLine(element, scale);
                var contour = new Ipc2581Contour(
                    [Point(element, "startX", "startY", scale), Point(element, "endX", "endY", scale)], null);
                return [new Ipc2581Stroke(contour, line.Width, line.End, Closed: false)];
            }
            case "Arc":
            {
                var line = RequiredLine(element, scale);
                var start = Point(element, "startX", "startY", scale);
                var points = new List<Ipc2581Point> { start };
                var bulges = new List<double>();
                Ipc2581Shapes.AppendArc(points, bulges, start, Point(element, "endX", "endY", scale),
                    Point(element, "centerX", "centerY", scale), Bool(Attr(element, "clockwise")));
                return [new Ipc2581Stroke(new Ipc2581Contour([.. points], [.. bulges]), line.Width, line.End, Closed: false)];
            }
            case "Polyline":
            {
                var line = RequiredLine(element, scale);
                var (points, bulges) = Vertices(element, scale);
                if (points.Count < 2) throw new FeatureError("fewer than two points");
                return [new Ipc2581Stroke(new Ipc2581Contour([.. points], [.. bulges]), line.Width, line.End, Closed: false)];
            }
            case "Outline":
            {
                var line = RequiredLine(element, scale);
                var polygon = Child(element, "Polygon") ?? throw new FeatureError("no Polygon");
                return [new Ipc2581Stroke(Ring(polygon, scale), line.Width, line.End, Closed: true)];
            }
            case "Polygon":
                return WithFill([new Ipc2581Region(Ring(element, scale), [])], element, scale);
            case "Contour":
                return WithFill([ContourRegion(element, scale)], element, scale);

            case "Text":
                Skipped("drawn text (Text)");
                return [];
        }

        var primitive = PrimitivePieces(element, name, scale);
        if (primitive is null)
        {
            Unknown(name);
            return [];
        }
        return WithFill(primitive, element, scale);
    }

    private IReadOnlyList<Ipc2581Piece> Resolved(XElement reference, Dictionary<string, DictionaryEntry> dictionary, string kind)
    {
        string id = Attr(reference, "id") ?? "";
        if (!dictionary.TryGetValue(id, out var entry))
        {
            Skipped($"reference to a {kind} the dictionary does not define");
            return [];
        }
        if (entry.Pieces is { } done) return done;

        string guard = $"{kind}:{id}";
        if (!_expanding.Add(guard))
            throw new Refusal($"The {kind} '{id}' refers to itself through its own features, so it cannot be drawn. Nothing was imported.");

        try
        {
            var pieces = new List<Ipc2581Piece>();
            foreach (var child in entry.Element.Elements())
                pieces.AddRange(PiecesOf(child, entry.Scale));
            entry.Pieces = pieces;
            return pieces;
        }
        finally
        {
            _expanding.Remove(guard);
        }
    }

    /// <summary>A UserSpecial's features, in order, at the scale of the section that states them.</summary>
    private IReadOnlyList<Ipc2581Piece> UserSpecialPieces(XElement special, double scale)
    {
        var pieces = new List<Ipc2581Piece>();
        foreach (var child in special.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "NonstandardAttribute" or "TextAttribute" or "ColorRef" or "Color":
                    break;
                default:
                    try { pieces.AddRange(PiecesOf(child, scale)); }
                    catch (FeatureError e) { Skipped($"{child.Name.LocalName} with {e.Message}"); }
                    break;
            }
        }
        return pieces;
    }

    private Ipc2581Region ContourRegion(XElement contour, double scale)
    {
        var polygon = Child(contour, "Polygon") ?? throw new FeatureError("no Polygon");
        var holes = contour.Elements().Where(e => e.Name.LocalName == "Cutout").Select(c => Ring(c, scale)).ToList();
        return new Ipc2581Region(Ring(polygon, scale), holes);
    }

    private static (List<Ipc2581Point> Points, List<double> Bulges) Vertices(XElement element, double scale)
    {
        var points = new List<Ipc2581Point>();
        var bulges = new List<double>();
        foreach (var step in element.Elements())
        {
            switch (step.Name.LocalName)
            {
                case "PolyBegin":
                    points.Add(Point(step, "x", "y", scale));
                    break;
                case "PolyStepSegment":
                    if (points.Count == 0) throw new FeatureError("a step before its PolyBegin");
                    bulges.Add(0);
                    points.Add(Point(step, "x", "y", scale));
                    break;
                case "PolyStepCurve":
                    if (points.Count == 0) throw new FeatureError("a step before its PolyBegin");
                    Ipc2581Shapes.AppendArc(points, bulges, points[^1], Point(step, "x", "y", scale),
                        Point(step, "centerX", "centerY", scale), Bool(Attr(step, "clockwise")));
                    break;
            }
        }
        return (points, bulges);
    }

    /// <summary>A closed ring: a last step back onto the first point closes it, and otherwise a straight edge does.</summary>
    private static Ipc2581Contour Ring(XElement element, double scale)
    {
        var (points, bulges) = Vertices(element, scale);
        if (points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
        else if (points.Count > 1) bulges.Add(0);
        if (points.Count < 3 && !(points.Count == 2 && bulges.Exists(b => b != 0)))
            throw new FeatureError("too few points to enclose an area");
        return new Ipc2581Contour([.. points], [.. bulges]);
    }

    // ── Line and fill descriptions ─────────────────────────────────────────────────────────────

    private LineDescription RequiredLine(XElement element, double scale) =>
        LineOf(element, scale) ?? throw new FeatureError("no line description");

    private LineDescription? LineOf(XElement element, double scale)
    {
        if (Child(element, "LineDesc") is { } inline) return ReadLine(inline, scale);
        if (Child(element, "LineDescRef") is not { } reference) return null;

        string id = Attr(reference, "id") ?? "";
        if (!_lineDescs.TryGetValue(id, out var entry))
            throw new FeatureError("a line description the dictionary does not define");
        var description = Child(entry.Element, "LineDesc") ?? throw new FeatureError("an empty line description");
        return ReadLine(description, entry.Scale);
    }

    private LineDescription ReadLine(XElement description, double scale)
    {
        if (Attr(description, "lineProperty") is { Length: > 0 } property
            && !property.Equals("SOLID", StringComparison.OrdinalIgnoreCase))
            Degraded("dashed or dotted line drawn solid");

        var end = (Attr(description, "lineEnd") ?? "ROUND").ToUpperInvariant() switch
        {
            "SQUARE" => PathEndStyle.Square,
            "NONE" => PathEndStyle.Flush,
            _ => PathEndStyle.Round,
        };
        return new LineDescription(Required(description, "lineWidth") * scale, end);
    }

    private string? FillOf(XElement element)
    {
        if (Child(element, "FillDesc") is { } inline) return Attr(inline, "fillProperty")?.ToUpperInvariant();
        if (Child(element, "FillDescRef") is not { } reference) return null;

        string id = Attr(reference, "id") ?? "";
        if (!_fillDescs.TryGetValue(id, out var entry))
            throw new FeatureError("a fill description the dictionary does not define");
        return (Child(entry.Element, "FillDesc") is { } description ? Attr(description, "fillProperty") : null)?.ToUpperInvariant();
    }

    private IReadOnlyList<Ipc2581Piece> WithFill(IReadOnlyList<Ipc2581Piece> pieces, XElement element, double scale)
    {
        string? fill = FillOf(element);
        var line = LineOf(element, scale);
        switch (fill)
        {
            case "VOID":
                return [.. pieces.Select(p => p with { Erase = true })];
            case "HOLLOW":
                if (line is null or { Width: <= 0 })
                {
                    Degraded("hollow shape with no line width, which draws nothing");
                    return [];
                }
                return [.. pieces.SelectMany(p => Strokes(p, line))];
            case "HATCH" or "MESH":
                Degraded("hatched or meshed fill drawn solid");
                return pieces;
            default:
                if (line is { Width: > 0 }) Degraded("filled shape drawn at its stated outline, without its line width");
                return pieces;
        }
    }

    private static IEnumerable<Ipc2581Piece> Strokes(Ipc2581Piece piece, LineDescription line)
    {
        switch (piece)
        {
            case Ipc2581Disc disc:
                yield return new Ipc2581Stroke(Ipc2581Contour.Circle(disc.Cx, disc.Cy, disc.R), line.Width, line.End, Closed: true);
                break;
            case Ipc2581Region region:
                yield return new Ipc2581Stroke(region.Outer, line.Width, line.End, Closed: true);
                foreach (var hole in region.Holes)
                    yield return new Ipc2581Stroke(hole, line.Width, line.End, Closed: true);
                break;
        }
    }

    // ── Primitives ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The pieces of a standard primitive centred on its origin as the format defines it, or null for an
    /// element that is not one.</summary>
    private IReadOnlyList<Ipc2581Piece>? PrimitivePieces(XElement e, string name, double scale)
    {
        double Len(string attr) => Required(e, attr) * scale;

        switch (name)
        {
            case "Circle":
                return [new Ipc2581Disc(0, 0, Len("diameter") / 2)];
            case "RectCenter":
            {
                double w = Len("width") / 2, h = Len("height") / 2;
                return [Solid(Ipc2581Contour.Box(-w, -h, w, h))];
            }
            case "RectCorner":
                return [Solid(Ipc2581Contour.Box(Len("lowerLeftX"), Len("lowerLeftY"), Len("upperRightX"), Len("upperRightY")))];
            case "RectRound":
                return [Solid(CorneredBox(e, Len("width"), Len("height"), Len("radius"), rounded: true))];
            case "RectCham":
                return [Solid(CorneredBox(e, Len("width"), Len("height"), Len("chamfer"), rounded: false))];
            case "Oval":
                return [OvalPiece(Len("width"), Len("height"))];
            case "Octagon":
                return [Solid(RegularRing(Len("length"), 8))];
            case "Hexagon":
                return [Solid(RegularRing(Len("length"), 6))];
            case "Diamond":
            {
                double w = Len("width") / 2, h = Len("height") / 2;
                return [Solid(new Ipc2581Contour([new(w, 0), new(0, h), new(-w, 0), new(0, -h)], null))];
            }
            case "Triangle":
            {
                double b = Len("base") / 2, h = Len("height") / 2;
                return [Solid(new Ipc2581Contour([new(-b, -h), new(b, -h), new(0, h)], null))];
            }
            case "Ellipse":
                return [Solid(EllipseRing(Len("width") / 2, Len("height") / 2))];
            case "Donut":
                return [DonutPiece(ShapeWord(e), Len("outerDiameter"), Len("innerDiameter"))];
            case "Thermal":
                return ThermalPieces(e, Len("outerDiameter"), Len("innerDiameter"), scale);
            case "Butterfly":
                return ButterflyPieces(e, scale);
            case "Moire":
                return MoirePieces(e, scale);
            default:
                return null;
        }
    }

    private static Ipc2581Region Solid(Ipc2581Contour outer) => new(outer, []);

    private static string ShapeWord(XElement e) => (Attr(e, "shape") ?? "ROUND").ToUpperInvariant();

    private static Ipc2581Piece OvalPiece(double width, double height)
    {
        if (width == height) return new Ipc2581Disc(0, 0, width / 2);
        double hw = width / 2, hh = height / 2;
        return width > height
            ? Solid(new Ipc2581Contour(
                [new(-hw + hh, -hh), new(hw - hh, -hh), new(hw - hh, hh), new(-hw + hh, hh)], [0, 1, 0, 1]))
            : Solid(new Ipc2581Contour(
                [new(hw, -hh + hw), new(hw, hh - hw), new(-hw, hh - hw), new(-hw, -hh + hw)], [0, 1, 0, 1]));
    }

    /// <summary>A rectangle with some corners rounded or chamfered. A file that names no corner means all four.</summary>
    private static Ipc2581Contour CorneredBox(XElement e, double width, double height, double cut, bool rounded)
    {
        // Files write a plain rectangle this way too, with a zero radius, and an arc of no length is not an edge.
        if (cut <= 0) return Ipc2581Contour.Box(-width / 2, -height / 2, width / 2, height / 2);

        bool anyStated = Attr(e, "upperRight") is not null || Attr(e, "upperLeft") is not null
                         || Attr(e, "lowerLeft") is not null || Attr(e, "lowerRight") is not null;
        bool Corner(string attr) => !anyStated || Bool(Attr(e, attr));

        double hw = width / 2, hh = height / 2;
        double bulge = rounded ? LayoutArc.ToBulge(Math.PI / 2) : 0;
        var points = new List<Ipc2581Point>();
        var bulges = new List<double>();

        void Add(bool cutHere, Ipc2581Point corner, Ipc2581Point entry, Ipc2581Point exit)
        {
            if (!cutHere)
            {
                points.Add(corner);
                bulges.Add(0);
                return;
            }
            points.Add(entry);
            bulges.Add(bulge);
            points.Add(exit);
            bulges.Add(0);
        }

        // Counter-clockwise from the lower-right corner, so a positive bulge rounds each corner outward.
        Add(Corner("lowerRight"), new(hw, -hh), new(hw - cut, -hh), new(hw, -hh + cut));
        Add(Corner("upperRight"), new(hw, hh), new(hw, hh - cut), new(hw - cut, hh));
        Add(Corner("upperLeft"), new(-hw, hh), new(-hw + cut, hh), new(-hw, hh - cut));
        Add(Corner("lowerLeft"), new(-hw, -hh), new(-hw, -hh + cut), new(-hw + cut, -hh));

        return new Ipc2581Contour([.. points], rounded ? [.. bulges] : null);
    }

    /// <summary>A regular polygon with a point facing north, measured across its points, as the format defines both
    /// the hexagon and the octagon.</summary>
    private static Ipc2581Contour RegularRing(double acrossPoints, int sides)
    {
        var points = new Ipc2581Point[sides];
        double r = acrossPoints / 2;
        for (int i = 0; i < sides; i++)
        {
            double a = Math.PI / 2 + 2 * Math.PI * i / sides;
            points[i] = new Ipc2581Point(r * Math.Cos(a), r * Math.Sin(a));
        }
        return new Ipc2581Contour(points, null);
    }

    private Ipc2581Contour EllipseRing(double rx, double ry)
    {
        int n = GerberPrimitives.CircleSegments(Math.Max(rx, ry), _flattenTolDbu);
        var points = new Ipc2581Point[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            points[i] = new Ipc2581Point(rx * Math.Cos(a), ry * Math.Sin(a));
        }
        return new Ipc2581Contour(points, null);
    }

    private static Ipc2581Contour NamedRing(string shape, double diameter) => shape switch
    {
        "SQUARE" => Ipc2581Contour.Box(-diameter / 2, -diameter / 2, diameter / 2, diameter / 2),
        "HEXAGON" => RegularRing(diameter, 6),
        "OCTAGON" => RegularRing(diameter, 8),
        _ => Ipc2581Contour.Circle(0, 0, diameter / 2),
    };

    private static Ipc2581Piece DonutPiece(string shape, double outer, double inner) =>
        new Ipc2581Region(NamedRing(shape, outer), inner > 0 ? [NamedRing(shape, inner)] : []);

    /// <summary>
    /// The ring between two outlines with its spokes cut out of it. What this draws is what the primitive covers, so
    /// on a negative plane it is a clearance and the cut spokes are exactly what stays copper. A spoke runs from the
    /// centre outward, which is why three spokes on a hexagon are three cuts and not three diameters.
    /// </summary>
    private IReadOnlyList<Ipc2581Piece> ThermalPieces(XElement e, double outer, double inner, double scale)
    {
        string shape = ShapeWord(e);
        int spokes = (int)Optional(e, "spokeCount", shape == "HEXAGON" ? 3 : 4);
        if (spokes <= 0) return [DonutPiece(shape, outer, inner)];

        // Older files call the same width a gap, and with neither stated it is the ring's own thickness.
        double spokeWidth = Attr(e, "spokeWidth") is not null ? Required(e, "spokeWidth") * scale
            : Attr(e, "gap") is not null ? Required(e, "gap") * scale
            : outer - inner;
        double start = Optional(e, "spokeStartAngle", 45);

        var outerPath = ThermalOutline(shape, outer);
        var innerPath = ThermalOutline(shape, inner);
        var ring = Clipper.Difference([outerPath], [innerPath], LayoutClipper.Rule);

        var cuts = new Paths64();
        double reach = outer;    // past every corner of the outer outline
        for (int i = 0; i < spokes; i++)
        {
            double a = (start + 360.0 * i / spokes) * Math.PI / 180.0;
            double ux = Math.Cos(a), uy = Math.Sin(a);
            double nx = -uy * spokeWidth / 2, ny = ux * spokeWidth / 2;
            cuts.Add(
            [
                GerberPrimitives.Point(nx, ny),
                GerberPrimitives.Point(ux * reach + nx, uy * reach + ny),
                GerberPrimitives.Point(ux * reach - nx, uy * reach - ny),
                GerberPrimitives.Point(-nx, -ny),
            ]);
        }

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Difference, ring, cuts, tree, LayoutClipper.Rule);
        return RegionsOf(tree);
    }

    private Path64 ThermalOutline(string shape, double diameter) => shape switch
    {
        "SQUARE" => GerberPrimitives.Rect(0, 0, diameter, diameter),
        "HEXAGON" => GerberPrimitives.RegularPolygon(0, 0, diameter, 6, 90),
        "OCTAGON" => GerberPrimitives.RegularPolygon(0, 0, diameter, 8, 90),
        _ => GerberPrimitives.Circle(0, 0, diameter / 2, _flattenTolDbu),
    };

    private static List<Ipc2581Piece> RegionsOf(PolyTree64 tree)
    {
        var pieces = new List<Ipc2581Piece>();
        foreach (var shape in LayoutClipper.FromClipperTree(tree, default, null).OfType<PolygonShape>())
            pieces.Add(new Ipc2581Region(FromFlat(shape.Xy),
                shape.Holes?.Select(FromFlat).ToList() ?? (IReadOnlyList<Ipc2581Contour>)[]));
        return pieces;
    }

    private static Ipc2581Contour FromFlat(long[] xy)
    {
        var points = new Ipc2581Point[xy.Length / 2];
        for (int i = 0; i < points.Length; i++) points[i] = new Ipc2581Point(xy[2 * i], xy[2 * i + 1]);
        return new Ipc2581Contour(points, null);
    }

    /// <summary>The first and third quadrants are the ones removed, leaving the second and fourth.</summary>
    private static IReadOnlyList<Ipc2581Piece> ButterflyPieces(XElement e, double scale)
    {
        string shape = ShapeWord(e);
        if (shape == "SQUARE")
        {
            double s = Required(e, "side") * scale / 2;
            return [Solid(Ipc2581Contour.Box(-s, 0, 0, s)), Solid(Ipc2581Contour.Box(0, -s, s, 0))];
        }

        double r = Required(e, "diameter") * scale / 2;
        double quarter = LayoutArc.ToBulge(Math.PI / 2);
        return
        [
            Solid(new Ipc2581Contour([new(0, 0), new(0, r), new(-r, 0)], [0, quarter, 0])),
            Solid(new Ipc2581Contour([new(0, 0), new(0, -r), new(r, 0)], [0, quarter, 0])),
        ];
    }

    private static IReadOnlyList<Ipc2581Piece> MoirePieces(XElement e, double scale)
    {
        double diameter = Required(e, "diameter") * scale;
        double ringWidth = Required(e, "ringWidth") * scale;
        double ringGap = Required(e, "ringGap") * scale;
        int rings = (int)Required(e, "ringNumber");

        var pieces = new List<Ipc2581Piece>();
        for (int i = 0; i < rings; i++)
        {
            double center = diameter / 2 - i * ringGap;
            double outer = center + ringWidth / 2, inner = center - ringWidth / 2;
            if (outer <= 0) break;
            pieces.Add(new Ipc2581Region(Ipc2581Contour.Circle(0, 0, outer),
                inner > 0 ? [Ipc2581Contour.Circle(0, 0, inner)] : []));
        }

        double lineWidth = Optional(e, "lineWidth", 0) * scale;
        double lineLength = Optional(e, "lineLength", 0) * scale;
        if (lineWidth > 0 && lineLength > 0)
        {
            double a = Optional(e, "lineAngle", 0) * Math.PI / 180.0;
            foreach (double angle in new[] { a, a + Math.PI / 2 })
            {
                double ux = Math.Cos(angle) * lineLength / 2, uy = Math.Sin(angle) * lineLength / 2;
                pieces.Add(new Ipc2581Stroke(new Ipc2581Contour([new(-ux, -uy), new(ux, uy)], null), lineWidth, PathEndStyle.Flush, Closed: false));
            }
        }
        return pieces;
    }

    // ── Attribute helpers ──────────────────────────────────────────────────────────────────────

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    private static double Required(XElement element, string attribute) =>
        Number(Attr(element, attribute)) ?? throw new FeatureError($"a missing or unreadable {attribute}");

    private static double Optional(XElement element, string attribute, double fallback)
    {
        string? text = Attr(element, attribute);
        if (text is null) return fallback;
        return Number(text) ?? throw new FeatureError($"an unreadable {attribute}");
    }

    private static Ipc2581Point Point(XElement element, string xAttr, string yAttr, double scale) =>
        new(Required(element, xAttr) * scale, Required(element, yAttr) * scale);
}
