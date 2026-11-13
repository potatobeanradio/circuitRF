// brief-em3d-45 R-em3d45-5 — Extrude: a closed polyline, a polygon sheet or a rectangle sheet becomes a Prism on the
// source's own plane, its Height the distance; an open polyline becomes a ribbon sheet.
//
// THE DISTANCE is the cursor's along the source's normal — the closest point of that line to the cursor's ray, or a
// feature's height when the cursor is near one (IC3dDrawHost.Along, the height step every tool shares; brief 47's
// move-normal gesture will reuse it) — or typed. A source whose placement rotates its normal off every world axis
// takes a typed distance only, and says so.
//
// CONSUME BY DEFAULT (K keeps the source): push/pull consumes, and a sheet left inside its own prism would overlap
// it. A consumed source is REPLACED in its slot — one undo entry of one shape (C3dEdit), with the prism keeping a
// sheet's name, material and role, since it is the same thing pushed; a kept one is joined by an insertion.
//
// AN OPEN POLYLINE makes a ribbon: a sheet in the plane containing the polyline and the normal. That is one of the
// three drawing planes only when the polyline is a straight line along an axis of its own plane; a bent one would be
// quads in more than one plane, a slanted one a tilted plane — both refused, with the reason (a later brief).

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class ExtrudeTool : C3dDrawTool
{
    private static readonly string[] Distance = ["distance"];
    private readonly C3dObject _source;
    private readonly C3dPlane _plane;
    private readonly long _offset;
    private readonly List<C3dPoint2> _outline;
    private readonly List<List<C3dPoint2>> _holes;
    private readonly bool _open;
    private readonly C3dTransform _t;
    private readonly C3dAxis? _worldAxis;
    private readonly int _worldSign;
    private readonly C3dPoint3 _through;

    /// <summary>The source's index in the document, which a consuming extrude replaces.</summary>
    public int SourceIndex { get; }

    /// <summary>Keep the source (K toggles); consumed by default.</summary>
    public bool Keep { get; set; }

    /// <summary>Why this object cannot be extruded at all, or null. Checked before a tool is made.</summary>
    public static string? CannotExtrude(C3dObject o) => o switch
    {
        C3dPolyline { Points3: not null } l => $"'{l.Name}' leaves its plane; only a polyline on one plane extrudes.",
        C3dPolyline { Closed: true } l when l.Points.Count < 3 => $"'{l.Name}' needs at least three vertices to extrude as a closed outline.",
        C3dPolyline { Closed: false } l => RibbonRefusal(l),
        C3dPolyline => null,
        C3dSheet { Rect: not null } => null,
        C3dSheet s when s.Outline.Count >= 3 => null,
        C3dSheet s => $"'{s.Name}' has no outline to extrude.",
        _ => "Extrude takes a closed polyline, a polygon sheet or a rectangle sheet.",
    };

    private static string? RibbonRefusal(C3dPolyline l)
    {
        if (l.Points.Count < 2) return $"'{l.Name}' needs two vertices to extrude.";
        bool alongU = l.Points.All(p => p.V == l.Points[0].V), alongV = l.Points.All(p => p.U == l.Points[0].U);
        if (alongU || alongV) return null;
        return $"'{l.Name}' is open and not a straight line along an axis of its plane: its ribbon would be quads in more than one " +
               "plane, or in a tilted one, which this build does not draw. Close it to extrude a prism.";
    }

    public ExtrudeTool(IC3dDrawHost host, C3dObject source, int index) : base(host)
    {
        _source = source;
        SourceIndex = index;
        switch (source)
        {
            case C3dSheet s:
                _plane = s.Plane; _offset = s.Offset;
                // brief-hzn-13 — a rectangle, an outline or an ellipse's polygon (an extruded ellipse is the prism of its polygon)
                var (ring, rings) = C3dEllipses.Polygon(s);
                _outline = [.. ring];
                _holes = [.. rings.Select(h => h.ToList())];
                break;
            case C3dPolyline l:
                _plane = l.Plane; _offset = l.Offset;
                _outline = [.. l.Points];
                _holes = [];
                _open = !l.Closed;
                break;
            default: throw new ArgumentException("Not extrudable.", nameof(source));
        }
        _t = source.Placement.ToTransform();
        var local = new DrawingPlane(_plane, _offset);
        var n = local.FromUvw(0, 0, 1) - local.FromUvw(0, 0, 0);
        var (a0x, a0y, a0z) = _t.Apply(default);
        var (a1x, a1y, a1z) = _t.Apply(n);
        double dx = a1x - a0x, dy = a1y - a0y, dz = a1z - a0z;
        (C3dAxis Axis, double V)[] parts = [(C3dAxis.X, dx), (C3dAxis.Y, dy), (C3dAxis.Z, dz)];
        foreach (var (axis, v) in parts)
            if (Math.Abs(Math.Abs(v) - 1) < 1e-12 && parts.All(q => q.Axis == axis || Math.Abs(q.V) < 1e-12))
            {
                _worldAxis = axis;
                _worldSign = Math.Sign(v);
            }
        var (tx, ty, tz) = _t.Apply(local.FromUv(_outline[0]));
        _through = new C3dPoint3((long)Math.Round(tx), (long)Math.Round(ty), (long)Math.Round(tz));
        Step = 1;
    }

    public override C3dToolKind Kind => C3dToolKind.Extrude;

    public override string Prompt
        => $"Extrude '{_source.Name}': move to set the distance and click (or type it). The source is " +
           (Keep ? "kept" : "consumed") + " — K toggles. Esc cancels." +
           (_worldAxis is null ? " Its placement turns its normal off every axis: type the distance." : "");

    public override IReadOnlyList<string> Dimensions => Distance;

    /// <summary>brief-em3d-51 — the distance is the prism's signed <c>Height</c> (a result of another kind has no such field,
    /// and the expression is then evaluated once).</summary>
    public override string? FieldFor(int step, int dim) => "Height";

    private long? CursorDistance(in C3dDrawInput input)
    {
        if (_worldAxis is not { } axis) return null;
        if (Host.Along(_through, axis, input) is not { } w) return null;
        return (w - DrawingPlane.Get(_through, axis)) * _worldSign;
    }

    public override long?[] Current(in C3dDrawInput input) => [CursorDistance(input)];

    public override C3dToolStep Click(in C3dDrawInput input)
        => CursorDistance(input) is { } d ? Finish(d) : C3dToolStep.Refuse("Extrude: type the distance.");

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
        => ((values.Length > 0 ? values[0] : null) ?? CursorDistance(input)) is { } d ? Finish(d) : C3dToolStep.Refuse("Extrude: type the distance.");

    private C3dToolStep Finish(long distance)
    {
        if (distance == 0) return C3dToolStep.Refuse("Extrude: the distance is zero.");
        var placement = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(_source)).Placement;
        C3dObject result;
        if (_open)
        {
            // The ribbon: the straight line's span along its axis, and the distance along the normal.
            var local = new DrawingPlane(_plane, _offset);
            var ends = _outline.Select(local.FromUv).ToList();
            var (au, av) = DrawingPlane.AxesOf(_plane);
            var along = _outline.All(p => p.V == _outline[0].V) ? au : av;
            var ribbon = DrawingPlane.Spanning(along, local.Normal);
            var other = (C3dAxis)(3 - (int)along - (int)local.Normal);
            var rp = new DrawingPlane(ribbon, DrawingPlane.Get(ends[0], other));
            long lo = ends.Min(p => DrawingPlane.Get(p, along)), hi = ends.Max(p => DrawingPlane.Get(p, along));
            var p0 = rp.ToUv(DrawingPlane.With(DrawingPlane.With(ends[0], along, lo), local.Normal, _offset));
            var p1 = rp.ToUv(DrawingPlane.With(DrawingPlane.With(ends[0], along, hi), local.Normal, _offset + distance));
            string? material = Host.CurrentMaterial;
            result = new C3dSheet
            {
                Name = Host.NextName("ribbon"),
                Material = material,
                Plane = ribbon,
                Offset = rp.OffsetDbu,
                Rect = new C3dRect
                {
                    Min = new C3dPoint2(Math.Min(p0.U, p1.U), Math.Min(p0.V, p1.V)),
                    Size = new C3dPoint2(Math.Abs(p1.U - p0.U), Math.Abs(p1.V - p0.V)),
                },
                ThicknessUm = Host.ThicknessUmFor(material),
                Placement = placement,
            };
        }
        else
        {
            bool sheet = _source is C3dSheet;
            result = new C3dPrism
            {
                Name = sheet && !Keep ? _source.Name : Host.NextName("prism"),
                Material = sheet ? _source.Material : Host.CurrentMaterial,
                Role = sheet ? _source.Role : null,
                Plane = _plane,
                Offset = _offset,
                Outline = [.. _outline],
                Holes = [.. _holes.Select(h => h.ToList())],
                Height = distance,
                Placement = placement,
            };
        }
        Step = 0;
        return C3dToolStep.Done(result);
    }

    /// <summary>Extrude starts on the chosen outline — its only earlier stage — so Esc ends it.</summary>
    public override bool StepBack() => false;

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        long d = CursorDistance(input) ?? 0;
        var local = new DrawingPlane(_plane, _offset);
        C3dPoint3 W(C3dPoint2 uv, long lift)
        {
            var (x, y, z) = _t.Apply(local.FromUvw(uv.U, uv.V, _offset + lift));
            return new C3dPoint3((long)Math.Round(x), (long)Math.Round(y), (long)Math.Round(z));
        }
        var lo = _outline.Select(p => W(p, 0)).ToList();
        var hi = _outline.Select(p => W(p, d)).ToList();
        DrawGeometry.Chain(lo, !_open, Host.DbuPerMicron, rubber);
        DrawGeometry.Chain(hi, !_open, Host.DbuPerMicron, rubber);
        for (int k = 0; k < lo.Count; k++) rubber.Add(new DrawSegment(M(lo[k]), M(hi[k])));
    }
}
