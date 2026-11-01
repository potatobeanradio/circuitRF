// brief-em3d-45 R-em3d45-3c / -3d — Polygon (a closed sheet) and Polyline (construction geometry), click per vertex.
//
// A POLYGON closes on a click on its first vertex, Enter, or a double-click, and is stored as a Sheet with that
// outline. A self-intersecting outline is refused AT CLOSE, exactly (DrawGeometry.FirstCrossing), with the two
// crossing edges handed back to be highlighted; the document is not touched and the gesture stays open, so
// Backspace can take the offending vertex back.
//
// A POLYLINE ends on Enter or a double-click and closes on a click on its first vertex. Its vertices snap to
// features anywhere in 3D, so one whose points leave its plane is stored with Points3 (the format keeps Plane + 2D
// points otherwise, and a planar polyline's spelling is unchanged).
//
// TYPED (R-em3d45-4): the next vertex's offset from the last, Δu and Δv on the plane — or its segment length along
// the cursor's direction, when only the length is typed.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

/// <summary>The vertex-per-click tools: the points so far (3D DBU), on the plane taken at the first click.</summary>
public abstract class C3dChainTool(IC3dDrawHost host) : C3dDrawTool(host)
{
    protected DrawingPlane Plane;
    protected readonly List<C3dPoint3> Points = [];

    private static readonly string[] Next = ["length", "Δu", "Δv"];
    private static readonly string[] None = [];

    public override IReadOnlyList<string> Dimensions => Step > 0 ? Next : None;

    public override bool TakesPlaneAtFirstClick => true;

    /// <summary>A polygon's vertices lie on its plane; a polyline's may be anywhere.</summary>
    protected abstract bool OnPlaneOnly { get; }

    /// <summary>The fewest points that make the finished object.</summary>
    protected abstract int MinimumPoints { get; }

    protected C3dPoint3? Cursor(in C3dDrawInput input, out string? refusal)
    {
        if (Step == 0) return OnPlaneOnly ? Host.PlanePoint(input, out refusal) : Host.FreePoint(input, out refusal);
        var p = OnPlaneOnly ? Host.PlanePoint(input, out refusal) : Host.FreePoint(input, out refusal);
        return p is { } q && OnPlaneOnly ? Plane.Project(q) : p;
    }

    public override long?[] Current(in C3dDrawInput input)
    {
        if (Step == 0 || Cursor(input, out _) is not { } c) return [null, null, null];
        var last = Points[^1];
        var d = c - last;
        long len = (long)Math.Round(Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z));
        var uv = Plane.ToUv(c);
        var luv = Plane.ToUv(last);
        return [len, uv.U - luv.U, uv.V - luv.V];
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Cursor(input, out var why) is not { } p) return C3dToolStep.Refuse(why!);
        return Add(p);
    }

    public override C3dToolStep DoubleClick(in C3dDrawInput input) => Step > 0 ? Finish(closed: OnPlaneOnly) : new(false);

    public override C3dToolStep Enter(in C3dDrawInput input) => Step > 0 ? Finish(closed: OnPlaneOnly) : new(false);

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Step == 0) return new(false);
        var last = Points[^1];
        long? du = values.Length > 1 ? values[1] : null, dv = values.Length > 2 ? values[2] : null;
        if (du is not null || dv is not null)
        {
            var cur = Current(input);
            var luv = Plane.ToUv(last);
            return Add(Plane.FromUvw(luv.U + Or(du, cur[1]), luv.V + Or(dv, cur[2]), Plane.W(last)));
        }
        if (values.Length > 0 && values[0] is { } len)
        {
            if (Cursor(input, out var why) is not { } c) return C3dToolStep.Refuse(why!);
            var d = c - last;
            double n = Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z);
            if (!(n > 0)) return C3dToolStep.Refuse("Point the cursor the way the segment should go, then type its length.");
            long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
            return Add(last + new C3dPoint3(R(d.X / n * len), R(d.Y / n * len), R(d.Z / n * len)));
        }
        return new(false);
    }

    private C3dToolStep Add(C3dPoint3 p)
    {
        if (Step == 0)
        {
            Plane = Host.Plane;
            Points.Clear();
            Points.Add(p);
            Step = 1;
            return C3dToolStep.Next;
        }
        // A click on the first vertex closes the outline.
        if (Points.Count >= 3 && p == Points[0]) return Finish(closed: true);
        if (p == Points[^1]) return new(false);            // the same point again: nothing to add
        Points.Add(p);
        Step = Points.Count;
        return C3dToolStep.Next;
    }

    public override bool Backspace()
    {
        if (Points.Count == 0) return false;
        Points.RemoveAt(Points.Count - 1);
        Step = Points.Count;
        return true;
    }

    public override void Reset()
    {
        base.Reset();
        Points.Clear();
    }

    /// <summary>A chain's stages are its vertices: Esc takes back the last one, as Backspace does.</summary>
    public override bool StepBack() => Backspace();

    protected abstract C3dToolStep Finish(bool closed);

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        foreach (var p in Points) fixedPoints.Add(M(p));
        DrawGeometry.Chain(Points, false, Host.DbuPerMicron, rubber);
        if (Cursor(input, out _) is { } c)
        {
            rubber.Add(new DrawSegment(M(Points[^1]), M(c)));
            if (OnPlaneOnly && Points.Count >= 2) rubber.Add(new DrawSegment(M(c), M(Points[0])));
        }
    }
}

public sealed class PolygonTool(IC3dDrawHost host) : C3dChainTool(host)
{
    public override C3dToolKind Kind => C3dToolKind.Polygon;
    protected override bool OnPlaneOnly => true;
    protected override int MinimumPoints => 3;

    public override string Prompt => Step == 0
        ? "Polygon: click the first vertex on the drawing plane."
        : "Polygon: click each vertex; click the first one, press Enter or double-click to close. Backspace takes one back.";

    protected override C3dToolStep Finish(bool closed)
    {
        if (Points.Count < MinimumPoints) return C3dToolStep.Refuse($"A polygon needs at least {MinimumPoints} vertices; it has {Points.Count}.");
        var outline = Points.Select(Plane.ToUv).ToList();
        if (DrawGeometry.FirstCrossing(outline) is { } x)
            return new C3dToolStep(false, Refusal: $"The outline crosses itself (edges {x.EdgeA + 1} and {x.EdgeB + 1}): move or take back a vertex.",
                                   Crossing: x);
        if (DrawGeometry.TwiceArea(outline) == 0) return C3dToolStep.Refuse("The outline encloses no area.");
        Step = 0;
        string? material = Host.CurrentMaterial;
        var sheet = new C3dSheet
        {
            Name = Host.NextName("polygon"),
            Material = material,
            Plane = Plane.Plane,
            Offset = Plane.OffsetDbu,
            Outline = outline,
            ThicknessUm = Host.ThicknessUmFor(material),
        };
        Points.Clear();
        return C3dToolStep.Done(sheet);
    }

    /// <summary>The outline's edges as the overlay draws them, for a refused close's crossing.</summary>
    public (DrawSegment A, DrawSegment B)? CrossingSegments((int EdgeA, int EdgeB) x)
    {
        if (Points.Count < 2) return null;
        DrawSegment E(int k) => new(M(Points[k % Points.Count]), M(Points[(k + 1) % Points.Count]));
        return (E(x.EdgeA), E(x.EdgeB));
    }
}

public sealed class PolylineTool(IC3dDrawHost host) : C3dChainTool(host)
{
    public override C3dToolKind Kind => C3dToolKind.Polyline;
    protected override bool OnPlaneOnly => false;
    protected override int MinimumPoints => 2;

    public override string Prompt => Step == 0
        ? "Polyline: click the first vertex."
        : "Polyline: click each vertex; Enter or double-click ends it, a click on the first vertex closes it.";

    protected override C3dToolStep Finish(bool closed)
    {
        if (Points.Count < MinimumPoints) return C3dToolStep.Refuse($"A polyline needs at least {MinimumPoints} vertices.");
        if (closed && Points.Count < 3) closed = false;
        Step = 0;
        bool planar = Points.All(Plane.Contains);
        var line = new C3dPolyline
        {
            Name = Host.NextName("polyline"),
            Plane = Plane.Plane,
            Offset = Plane.OffsetDbu,
            Closed = closed,
        };
        if (planar) line.Points = [.. Points.Select(Plane.ToUv)];
        else line.Points3 = [.. Points];
        Points.Clear();
        return C3dToolStep.Done(line);
    }
}
