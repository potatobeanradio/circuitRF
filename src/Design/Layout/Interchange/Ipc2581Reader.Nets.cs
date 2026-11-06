// What IPC-2581 says about the copper without drawing any of it: which net, which part, which pin. Series rule S2:
// nothing here creates a shape. A logical net gives a net's parts and pins, a physical net gives points on layers,
// and a component gives a part's reference, side and position.
//
// A negative plane needs this most. Its features are clearances, so the copper left behind has no set stating its
// net, and a clearance's own net describes the pin passing through the plane and not the plane. A point the file
// states on that layer, or a thermal that joins a pin to the plane, is the evidence that names it.

using System.Xml;
using System.Xml.Linq;
using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class Ipc2581Reader
{
    private readonly record struct PlanePoint(long X, long Y, string Net);

    private readonly record struct PartPin(string Component, string Pin);

    private sealed record ComponentRead(string Reference, string? LayerRef, long X, long Y, double Rotation);

    private readonly Dictionary<string, List<PartPin>> _pinsByNet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PlanePoint>> _planeEvidence = new(StringComparer.Ordinal);
    private readonly HashSet<string> _statedNets = new(StringComparer.Ordinal);
    private readonly List<ComponentRead> _components = [];
    private readonly List<LayoutShape> _padsWithHalfAPart = [];
    private int _padsWithPart;

    /// <summary>Spellings files use to say a feature is on no net. Such a name is not a net.</summary>
    private static readonly HashSet<string> NoNetNames =
        new(["No Net", "NoNet", "NO_NET", "N/C", "$NONE$"], StringComparer.OrdinalIgnoreCase);

    private static string? NetName(string? raw, string step)
    {
        string? name = StripStepPrefix(raw, step);
        return string.IsNullOrWhiteSpace(name) || NoNetNames.Contains(name.Trim()) ? null : Unescaped(name);
    }

    /// <summary>Some files escape a name twice, so after the XML reader's own pass a bus bit still reads D&amp;lt;3&amp;gt;. Those
    /// spellings are never a real net's name.</summary>
    private static string Unescaped(string name) => !name.Contains('&', StringComparison.Ordinal) ? name : name
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&apos;", "'", StringComparison.Ordinal)
        .Replace("&amp;", "&", StringComparison.Ordinal);

    private void ReleaseNetsAndHoles()
    {
        _pinsByNet.Clear();
        _planeEvidence.Clear();
        _statedNets.Clear();
        _components.Clear();
        _padsWithHalfAPart.Clear();
        _padsWithPart = 0;
        _holes.Clear();
        _slots.Clear();
        _zones.Clear();
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────────

    private void ReadLogicalNet(XElement logical, StepInfo step)
    {
        if (NetName(Attr(logical, "name"), step.Name) is not { } net) return;
        _statedNets.Add(net);
        foreach (var child in logical.Elements().Where(e => e.Name.LocalName is "LogicalNetPin" or "PinRef"))
        {
            string? component = StripStepPrefix(Attr(child, "componentRef"), step.Name);
            string? pin = Attr(child, "pin");
            if (component is null || pin is not { Length: > 0 }) continue;
            if (!_pinsByNet.TryGetValue(net, out var pins)) _pinsByNet[net] = pins = [];
            pins.Add(new PartPin(component, pin));
        }
    }

    /// <summary>One physical net at a time, since this group can hold a point for every pad on a board.</summary>
    private void ReadPhyNetGroup(XmlReader xml, StepInfo step)
    {
        Children(xml, name =>
        {
            if (name != "PhyNet") { Unknown(name); xml.Skip(); return; }
            var phy = Tree(xml);
            if (NetName(Attr(phy, "name"), step.Name) is not { } net) return;
            _statedNets.Add(net);

            double scale = EcadScale();
            foreach (var point in phy.Elements().Where(e => e.Name.LocalName == "PhyNetPoint"))
            {
                if (Attr(point, "layerRef") is not { Length: > 0 } layer) continue;
                if (Number(Attr(point, "x")) is not { } x || Number(Attr(point, "y")) is not { } y) continue;
                AddPlaneEvidence(layer, Dbu(x * scale), Dbu(y * scale), net);
            }
        });
    }

    private void ReadComponent(XElement component, StepInfo step)
    {
        if (StripStepPrefix(Attr(component, "refDes"), step.Name) is not { } reference)
        {
            Skipped("component with no reference designator");
            return;
        }

        double scale = EcadScale();
        var location = Child(component, "Location");
        double x = Number(location is null ? null : Attr(location, "x")) ?? 0;
        double y = Number(location is null ? null : Attr(location, "y")) ?? 0;
        var xform = Child(component, "Xform");
        double rotation = Number(xform is null ? null : Attr(xform, "rotation")) ?? 0;
        _components.Add(new ComponentRead(reference, Attr(component, "layerRef"), Dbu(x * scale), Dbu(y * scale), rotation));
    }

    private void AddPlaneEvidence(string layer, long x, long y, string net)
    {
        if (!_planeEvidence.TryGetValue(layer, out var points)) _planeEvidence[layer] = points = [];
        points.Add(new PlanePoint(x, y, net));
    }

    /// <summary>A thermal joins its pin to the plane, so unlike a plain clearance its net is the plane's. Its centre
    /// stays copper through the subtraction, joined by the spokes, so it is a point on the piece it names.</summary>
    private void NoteThermal(XElement placed, string layer, string net, double scale)
    {
        if (!HoldsThermal(placed)) return;
        try
        {
            var centre = LocalTransform(placed, scale).Apply(new Ipc2581Point(0, 0));
            AddPlaneEvidence(layer, Dbu(centre.X), Dbu(centre.Y), net);
        }
        catch (FeatureError)
        {
            // PlaceElement has already counted this element's unreadable placement.
        }
    }

    private bool HoldsThermal(XElement placed)
    {
        foreach (var child in placed.Elements())
        {
            if (child.Name.LocalName == "Thermal") return true;
            if (child.Name.LocalName == "StandardPrimitiveRef"
                && Attr(child, "id") is { } id
                && _standard.TryGetValue(id, out var entry)
                && entry.Element.Elements().Any(e => e.Name.LocalName == "Thermal"))
                return true;
        }
        return false;
    }

    // ── Pads know their part ───────────────────────────────────────────────────────────────────

    /// <summary>Sets the part and pin a pad states, from a PinRef or the pad's own attributes. A pad stating only one
    /// of the two waits for <see cref="ApplyPadParts"/>, because a logical net can be anywhere in the step. No location
    /// is ever matched: where nothing states the part, both stay null.</summary>
    private void StatePart(XElement pad, XElement? stackPin, List<LayoutShape> shapes, int first, StepInfo step)
    {
        var pinRef = Child(pad, "PinRef") ?? stackPin;
        string? component = StripStepPrefix(
            (pinRef is null ? null : Attr(pinRef, "componentRef")) ?? Attr(pad, "componentRef"), step.Name);
        string? pin = (pinRef is null ? null : Attr(pinRef, "pin")) ?? Attr(pad, "pin");
        if (pin is { Length: 0 }) pin = null;
        if (component is null && pin is null) return;

        for (int i = first; i < shapes.Count; i++)
        {
            shapes[i].Component = component;
            shapes[i].Pin = pin;
            if (component is null || pin is null) _padsWithHalfAPart.Add(shapes[i]);
            else _padsWithPart++;
        }
    }

    /// <summary>A pin attribute may hold both halves, as <c>J1.1</c>, which counts only when the file names that part.
    /// Otherwise a logical net settles the half a pad left out, when exactly one of that net's pins fits.</summary>
    private void ApplyPadParts()
    {
        var references = _components.Select(c => c.Reference).ToHashSet(StringComparer.Ordinal);
        foreach (var shape in _padsWithHalfAPart)
        {
            if (SplitCombinedPin(shape, references)) { _padsWithPart++; continue; }

            var fits = shape.Net is { } net && _pinsByNet.TryGetValue(net, out var pins)
                ? pins.Where(p => (shape.Component is null || p.Component == shape.Component)
                                  && (shape.Pin is null || p.Pin == shape.Pin)).Distinct().ToList()
                : [];
            if (fits.Count == 1)
            {
                shape.Component = fits[0].Component;
                shape.Pin = fits[0].Pin;
                _padsWithPart++;
            }
            else
            {
                Degraded("pad stating only its part or only its pin, which no logical net settled");
            }
        }
    }

    private bool SplitCombinedPin(LayoutShape shape, HashSet<string> references)
    {
        if (shape.Component is not null || shape.Pin is not { } combined) return false;
        for (int dot = combined.IndexOf('.'); dot > 0 && dot < combined.Length - 1; dot = combined.IndexOf('.', dot + 1))
        {
            string? part = StripStepPrefix(combined[..dot], _chosen?.Name ?? "");
            if (part is null || !references.Contains(part)) continue;
            shape.Component = part;
            shape.Pin = combined[(dot + 1)..];
            return true;
        }
        return false;
    }

    // ── A negative plane's net ─────────────────────────────────────────────────────────────────

    /// <summary>Evidence for one net names a piece; none leaves it null; a conflict leaves it null and is reported. A
    /// layer's name is never evidence, including a layer called GND.</summary>
    private void NamePlanePieces(string layer, List<LayoutShape> pieces)
    {
        var unnamed = pieces.Where(p => p.Net is null).ToList();
        if (unnamed.Count == 0) return;

        var boxes = unnamed.Select(LayoutGeometry.BboxOf).ToList();
        var paths = new Paths64?[unnamed.Count];
        var found = new HashSet<string>?[unnamed.Count];

        if (_planeEvidence.TryGetValue(layer, out var points))
        {
            foreach (var point in points)
            {
                for (int i = 0; i < unnamed.Count; i++)
                {
                    var box = boxes[i];
                    if (point.X < box.MinX || point.X > box.MaxX || point.Y < box.MinY || point.Y > box.MaxY) continue;
                    paths[i] ??= LayoutClipper.ToClipperPaths(unnamed[i], LayoutFlattener.ResolveTolDbu(unnamed[i], null));
                    if (Inside(paths[i]!, point.X, point.Y)) (found[i] ??= new(StringComparer.Ordinal)).Add(point.Net);
                }
            }
        }

        int noEvidence = 0;
        for (int i = 0; i < unnamed.Count; i++)
        {
            switch (found[i]?.Count ?? 0)
            {
                case 0:
                    noEvidence++;
                    break;
                case 1:
                    unnamed[i].Net = found[i]!.First();
                    break;
                default:
                    _board.Diagnostics.Add(
                        $"A piece of copper on negative layer '{layer}' has evidence for more than one net " +
                        $"({string.Join(", ", found[i]!.Order(StringComparer.Ordinal))}), so it was left with no net.");
                    break;
            }
        }

        if (noEvidence > 0)
            Bump(_board.DegradedCounts, $"piece of copper on negative layer '{layer}' left with no net, since nothing in the file locates one on it", noEvidence);
    }

    /// <summary>An outline and its holes: inside an odd number of them is inside the copper.</summary>
    private static bool Inside(Paths64 paths, long x, long y)
    {
        var point = new Point64(x, y);
        int crossings = 0;
        foreach (var path in paths)
            if (Clipper.PointInPolygon(point, path) != PointInPolygonResult.IsOutside) crossings++;
        return crossings % 2 == 1;
    }

    // ── Reporting ──────────────────────────────────────────────────────────────────────────────

    private void ReportNetsAndComponents()
    {
        var carried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var imported in _board.Shapes)
            if (imported.Shape.Net is { } net) carried.Add(net);

        int empty = _statedNets.Count(n => !carried.Contains(n));
        if (empty > 0)
            _board.Diagnostics.Add(
                $"{empty} net(s) this file states are carried by no copper, so they are not nets on the board.");

        if (_components.Count == 0) return;

        var sides = _layers.GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Side, StringComparer.Ordinal);
        foreach (var c in _components)
        {
            string? side = c.LayerRef is { } layer && sides.TryGetValue(layer, out var stated) && stated is "TOP" or "BOTTOM"
                ? stated
                : null;
            _board.Components.Add(new PcbStatedComponent(c.Reference, side, c.X, c.Y, c.Rotation));
        }

        _board.Diagnostics.Add(
            $"{_components.Count} component(s) were read and not placed as instances: the board imports as one flat cell, " +
            "because its artwork already holds every pad. " +
            (_padsWithPart > 0
                ? $"{_padsWithPart} pad(s) carry their part and pin."
                : "No pad on a layer of this file names its part, so pads carry no part or pin."));
    }
}
