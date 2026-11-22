// brief-em3d-75 R-em3d75-1a — drawing the thermal places: a heat source, a probe, a mesh region. Each makes a RECORD beside the
// ports (brief 73 R-em3d73-4), never an object: the editor inserts it as one undo entry, exactly as it inserts a port.
//
//   * Heat Source — a rectangle on the drawing plane with the Port tool's own picking and snapping (both are
//     C3dRectangleTool: the same two snapped clicks, the same typed width and depth), or a polygon with the Polygon tool's
//     (C3dChainTool). The power is asked afterwards (the editor's), defaulting to the document's.
//   * Probe — a point (one click on a face), a spot (its centre on a face, then its diameter: a second click or typed), or a
//     line (two clicks, anywhere a snap reaches). A Point click on a bond wire makes a wire probe instead. A face or solid
//     probe is a context-menu command on what is selected.
//   * Mesh Region — a box drawn as the Box tool draws one (two corners, then the height); its element size is asked after.
//   * Temperature Along — two points, as a line probe takes them, that make NOTHING: the editor plots T between them
//     (R-em3d75-4d), the owner's by-hand Rth.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

/// <summary>A tool that makes a record rather than an object: the editor takes it once, and inserts it.</summary>
public interface IC3dRecordTool
{
    /// <summary>What the last finished gesture made (a <see cref="C3dHeatSource"/>, <see cref="C3dProbe"/> or
    /// <see cref="C3dMeshRegion"/>), handed over once.</summary>
    object? TakeMade();
}

/// <summary>R-em3d75-1a — a rectangular heat source, drawn exactly as a port's sheet is.</summary>
public sealed class HeatSourceTool(IC3dDrawHost host) : C3dRectangleTool(host), IC3dRecordTool
{
    private object? _made;

    public override C3dToolKind Kind => C3dToolKind.HeatSource;
    public override string Name => "Heat source";

    public override string Prompt => Step == 0
        ? "Heat Source: click the first corner on the drawing plane (on a face: Ctrl/Cmd-click the face first to put the plane there)."
        : "Heat Source: click the opposite corner (or type the width, Tab, the depth). Esc cancels.";

    public object? TakeMade() { var m = _made; _made = null; return m; }

    public override C3dToolStep Click(in C3dDrawInput input) => Finish(RectangleClick(input)!.Value);

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
        => Step == 1 ? Finish(TypedRectangle(values, input)) : new(false);

    public override string? FieldFor(int step, int dim) => step == 1 ? $"Sheet.Rect.Size[{dim}]" : null;

    public override void PreviewTyped(long?[] values, in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => PreviewRectangle(input, rubber, fixedPoints, values);

    private C3dToolStep Finish(C3dToolStep step)
    {
        if (!step.Advanced || Step != 2) return step;
        Step = 0;
        _made = new C3dHeatSource
        {
            Name = Host.NextName("source"),
            Sheet = new C3dHeatSheet
            {
                Plane = Plane.Plane,
                Offset = Plane.OffsetDbu,
                Rect = new C3dRect
                {
                    Min = new C3dPoint2(Math.Min(A.U, B.U), Math.Min(A.V, B.V)),
                    Size = new C3dPoint2(Math.Abs(B.U - A.U), Math.Abs(B.V - A.V)),
                },
            },
        };
        return C3dToolStep.Finish;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => PreviewRectangle(input, rubber, fixedPoints);
}

/// <summary>R-em3d75-1a — a polygonal heat source, drawn exactly as the Polygon tool draws a sheet's outline.</summary>
public sealed class HeatSourcePolygonTool(IC3dDrawHost host) : C3dChainTool(host), IC3dRecordTool
{
    private object? _made;

    public override C3dToolKind Kind => C3dToolKind.HeatSourcePolygon;
    public override string Name => "Heat source";
    protected override bool OnPlaneOnly => true;
    protected override int MinimumPoints => 3;

    public override string Prompt => Step == 0
        ? "Heat Source (polygon): click the first vertex on the drawing plane."
        : "Heat Source (polygon): click each vertex; click the first one, press Enter or double-click to close.";

    public object? TakeMade() { var m = _made; _made = null; return m; }

    protected override C3dToolStep Finish(bool closed)
    {
        if (Points.Count < MinimumPoints) return C3dToolStep.Refuse($"A heat source's outline needs at least {MinimumPoints} vertices; it has {Points.Count}.");
        var outline = Points.Select(Plane.ToUv).ToList();
        if (DrawGeometry.FirstCrossing(outline) is { } x)
            return new C3dToolStep(false, Refusal: $"The outline crosses itself (edges {x.EdgeA + 1} and {x.EdgeB + 1}): move or take back a vertex.",
                                   Crossing: x);
        if (DrawGeometry.TwiceArea(outline) == 0) return C3dToolStep.Refuse("The outline encloses no area.");
        Step = 0;
        Points.Clear();
        _made = new C3dHeatSource
        {
            Name = Host.NextName("source"),
            Sheet = new C3dHeatSheet { Plane = Plane.Plane, Offset = Plane.OffsetDbu, Outline = outline },
        };
        return C3dToolStep.Finish;
    }
}

/// <summary>Which probe the Probe tool draws.</summary>
public enum C3dProbeShape { Point, Spot, Line }

/// <summary>R-em3d75-1a — a point, spot or line probe, clicked where it reads.</summary>
public sealed class ProbeTool(IC3dDrawHost host, C3dProbeShape shape) : C3dDrawTool(host), IC3dRecordTool
{
    private object? _made;
    private C3dPoint3 _first;
    private string _face = "";

    private static readonly string[] None = [];
    private static readonly string[] DiameterOnly = ["diameter"];

    public C3dProbeShape Shape { get; } = shape;

    public override C3dToolKind Kind => Shape switch
    {
        C3dProbeShape.Spot => C3dToolKind.ProbeSpot,
        C3dProbeShape.Line => C3dToolKind.ProbeLine,
        _ => C3dToolKind.ProbePoint,
    };

    public override string Name => "Probe";

    public override string Prompt => (Shape, Step) switch
    {
        (C3dProbeShape.Point, _) => "Probe: click the point on a face where the temperature is read, or a bond wire to read the wire.",
        (C3dProbeShape.Spot, 0) => "Spot probe: click the spot's centre on a face.",
        (C3dProbeShape.Spot, _) => "Spot probe: click at the spot's edge (or type its diameter). Esc cancels.",
        (_, 0) => "Line probe: click the line's first end.",
        _ => "Line probe: click the other end. Esc cancels.",
    };

    public override IReadOnlyList<string> Dimensions => Shape == C3dProbeShape.Spot && Step == 1 ? DiameterOnly : None;

    public object? TakeMade() { var m = _made; _made = null; return m; }

    private long? Diameter(in C3dDrawInput input)
    {
        if (Host.FreePoint(input, out _) is not { } p) return null;
        var d = p - _first;
        return 2 * (long)Math.Round(Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z));
    }

    public override long?[] Current(in C3dDrawInput input) => Shape == C3dProbeShape.Spot && Step == 1 ? [Diameter(input)] : [];

    public override string? FieldFor(int step, int dim) => Shape == C3dProbeShape.Spot && step == 1 ? "Spot.Diameter" : null;

    public override bool FieldIsMagnitude(int step, int dim) => true;

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Step == 0)
        {
            C3dPoint3? p;
            string? why;
            // A Point probe clicked on a bond wire reads the wire (T along it, and its maximum): the context menu's
            // Add Probe ▸ Wire, from the one toolbar button.
            if (Shape == C3dProbeShape.Point && Host.WireUnderCursor(input) is { } wire)
                return Done(new C3dProbe { Name = Host.NextName("probe"), Wire = wire, Stat = C3dProbeStat.Max });
            if (Shape == C3dProbeShape.Line) p = Host.FreePoint(input, out why);
            else
            {
                p = Host.SurfacePoint(input, out string? face, out why);
                _face = face ?? "";
            }
            if (p is not { } q) return C3dToolStep.Refuse(why ?? "Click on a face.");
            if (Shape == C3dProbeShape.Point) return Done(new C3dProbe { Name = Host.NextName("probe"), Point = q });
            if (Shape == C3dProbeShape.Spot && _face.Length == 0)
                return C3dToolStep.Refuse("A spot probe lies on a face: click on the face of a solid.");
            _first = q;
            Step = 1;
            return C3dToolStep.Next;
        }
        if (Shape == C3dProbeShape.Spot)
            return Diameter(input) is { } d ? SpotDone(d) : C3dToolStep.Refuse("Spot probe: point at the spot's edge, or type its diameter.");
        if (Host.FreePoint(input, out var w) is not { } end) return C3dToolStep.Refuse(w!);
        if (end == _first) return C3dToolStep.Refuse("A line probe needs two different points.");
        return Done(new C3dProbe { Name = Host.NextName("probe"), Line = new C3dProbeLine { From = _first, To = end } });
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Shape != C3dProbeShape.Spot || Step != 1) return new(false);
        long? d = values.Length > 0 ? values[0] : null;
        d ??= Diameter(input);
        return d is { } dd ? SpotDone(Math.Abs(dd)) : C3dToolStep.Refuse("Spot probe: type the diameter.");
    }

    private C3dToolStep SpotDone(long diameter)
    {
        if (diameter <= 0) return C3dToolStep.Refuse("A spot needs a diameter: move off its centre, or type one.");
        return Done(new C3dProbe { Name = Host.NextName("probe"), Spot = new C3dProbeSpot { Face = _face, Center = _first, Diameter = diameter } });
    }

    private C3dToolStep Done(C3dProbe probe)
    {
        Step = 0;
        _made = probe;
        return C3dToolStep.Finish;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_first));
        if (Shape == C3dProbeShape.Line && Host.FreePoint(input, out _) is { } e) rubber.Add(new DrawSegment(M(_first), M(e)));
        if (Shape == C3dProbeShape.Spot && Host.FreePoint(input, out _) is { } r) rubber.Add(new DrawSegment(M(_first), M(r)));
    }
}

/// <summary>R-em3d75-1a — a mesh region: a box drawn as the Box tool draws one. brief-em3d-76 R-em3d76-2a — with
/// <paramref name="block"/>, an effective block over a via field, drawn the same way and made DISABLED.</summary>
public sealed class MeshRegionTool(IC3dDrawHost host, bool block = false) : C3dRectangleTool(host), IC3dRecordTool
{
    private static readonly string[] HeightOnly = ["height"];
    private object? _made;

    public override C3dToolKind Kind => block ? C3dToolKind.EffectiveBlock : C3dToolKind.MeshRegion;
    public override string Name => block ? "Effective block" : "Mesh region";

    private string Title => block ? "Effective Block" : "Mesh Region";

    public override string Prompt => Step switch
    {
        0 => $"{Title}: click the first corner on the drawing plane.",
        1 => $"{Title}: click the opposite corner (or type the width, Tab, the depth).",
        _ => $"{Title}: move to set the height and click (or type it). Esc cancels.",
    };

    public object? TakeMade() { var m = _made; _made = null; return m; }

    public override IReadOnlyList<string> Dimensions => Step == 2 ? HeightOnly : base.Dimensions;

    private long? Height(in C3dDrawInput input) => Host.Along(Plane.FromUv(B), Plane.Normal, input) is { } w ? w - Plane.OffsetDbu : null;

    public override long?[] Current(in C3dDrawInput input) => Step == 2 ? [Height(input)] : base.Current(input);

    public override string? FieldFor(int step, int dim)
    {
        var (u, v, n) = AxesOf(Plane.Plane);
        return step switch { 1 => $"Size[{(dim == 0 ? u : v)}]", 2 => $"Size[{n}]", _ => null };
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (RectangleClick(input) is { } step) return step;
        if (Height(input) is not { } h) return C3dToolStep.Refuse($"{Title}: the cursor is looking straight along the height; type the height instead.");
        return Finish(h);
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Step == 1) return TypedRectangle(values, input);
        if (Step != 2) return new(false);
        long? h = (values.Length > 0 ? values[0] : null) ?? Height(input);
        return h is { } hh ? Finish(hh) : C3dToolStep.Refuse($"{Title}: type the height.");
    }

    private C3dToolStep Finish(long height)
    {
        if (height == 0) return C3dToolStep.Refuse($"A{(block ? "n effective block" : " mesh region")} needs a height: move off the drawing plane, or type one.");
        var p = Plane.FromUvw(A.U, A.V, Plane.OffsetDbu);
        var q = Plane.FromUvw(B.U, B.V, Plane.OffsetDbu + height);
        var min = new C3dPoint3(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), Math.Min(p.Z, q.Z));
        var size = new C3dPoint3(Math.Abs(q.X - p.X), Math.Abs(q.Y - p.Y), Math.Abs(q.Z - p.Z));
        // A starting element size: a tenth of the box's smallest side, in µm; the editor asks for the real one.
        double um = Math.Max(1e-3, Math.Round(Math.Min(size.X, Math.Min(size.Y, size.Z)) / 10.0 / Host.DbuPerMicron, 3));
        Step = 0;
        _made = block ? new C3dEffectiveBlock { Name = Host.NextName("block"), Min = min, Size = size }
                      : new C3dMeshRegion { Name = Host.NextName("region"), Min = min, Size = size, SizeUm = um };
        return C3dToolStep.Finish;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        PreviewRectangle(input, rubber, fixedPoints);
        if (Step != 2) return;
        fixedPoints.Add(M(Plane.FromUv(B)));
        DrawGeometry.Box(Plane, A, B, Height(input) ?? 0, Host.DbuPerMicron, rubber);
    }
}

/// <summary>R-em3d75-4d — Temperature Along…: two points, and nothing made; the editor plots T between them.</summary>
public sealed class TemperatureAlongTool(IC3dDrawHost host) : C3dDrawTool(host)
{
    private C3dPoint3 _first;
    private static readonly string[] None = [];

    public override C3dToolKind Kind => C3dToolKind.TemperatureAlong;
    public override string Name => "Temperature along";

    public override string Prompt => Step == 0
        ? "Temperature Along: click the first point (a snap on a vertex, an edge or a face centre is exact)."
        : "Temperature Along: click the second point. Esc cancels.";

    public override IReadOnlyList<string> Dimensions => None;

    public override long?[] Current(in C3dDrawInput input) => [];

    /// <summary>The two points of the last finished gesture, handed over once.</summary>
    public (C3dPoint3 From, C3dPoint3 To)? Taken { get; private set; }

    public (C3dPoint3 From, C3dPoint3 To)? Take() { var t = Taken; Taken = null; return t; }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Host.FreePoint(input, out var why) is not { } p) return C3dToolStep.Refuse(why!);
        if (Step == 0) { _first = p; Step = 1; return C3dToolStep.Next; }
        if (p == _first) return C3dToolStep.Refuse("Pick a second point apart from the first.");
        Step = 0;
        Taken = (_first, p);
        return C3dToolStep.Finish;
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input) => new(false);

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_first));
        if (Host.FreePoint(input, out _) is { } e) rubber.Add(new DrawSegment(M(_first), M(e)));
    }
}
