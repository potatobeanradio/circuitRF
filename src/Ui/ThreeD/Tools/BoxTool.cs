// brief-em3d-45 R-em3d45-3a / -3b — Box (three clicks: corner, opposite corner, height) and Sheet (the first two).
//
// THE PLANE IS TAKEN AT THE FIRST CLICK and kept for the gesture, so a plane change mid-gesture cannot bend it.
// Corner B is fixed on the plane; the box then rises along the plane's normal to where the cursor's ray passes
// closest to the normal line through B — or, near a feature, to that feature's own height (IC3dDrawHost.Along),
// which is how a box rises to exactly the top of a neighbouring pad. Negative heights are allowed.
//
// STORED AS A BOX WHATEVER THE PLANE: Min plus a positive Size (brief 41), each typed width, depth or height in ONE
// Size component — which is what makes the same three clicks on XY, YZ and XZ permutations of one box (gate 2).

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

/// <summary>The rectangle both tools start with: corner A, then corner B, on the plane taken at the first click.</summary>
public abstract class C3dRectangleTool(IC3dDrawHost host) : C3dDrawTool(host)
{
    protected DrawingPlane Plane;
    protected C3dPoint2 A, B;

    private static readonly string[] Corner = [];
    private static readonly string[] WidthDepth = ["width", "depth"];

    public override IReadOnlyList<string> Dimensions => Step == 1 ? WidthDepth : Corner;

    public override bool TakesPlaneAtFirstClick => true;

    protected C3dPoint2? Cursor(in C3dDrawInput input, out string? refusal)
    {
        if (Host.PlanePoint(input, out refusal) is not { } p) return null;
        return (Step == 0 ? Host.Plane : Plane).ToUv(p);
    }

    public override long?[] Current(in C3dDrawInput input)
        => Step == 1 && Cursor(input, out _) is { } c ? [c.U - A.U, c.V - A.V] : [null, null];

    /// <summary>Steps 0 and 1: fix A, then B. Null when the step was a rectangle step and is done.</summary>
    protected C3dToolStep? RectangleClick(in C3dDrawInput input)
    {
        if (Step == 0)
        {
            if (Host.PlanePoint(input, out var why) is not { } p) return C3dToolStep.Refuse(why!);
            Plane = Host.Plane;
            A = Plane.ToUv(p);
            Step = 1;
            return C3dToolStep.Next;
        }
        if (Step == 1)
        {
            if (Cursor(input, out var why) is not { } c) return C3dToolStep.Refuse(why!);
            return SetB(c);
        }
        return null;
    }

    protected C3dToolStep SetB(C3dPoint2 b)
    {
        if (b.U == A.U || b.V == A.V) return C3dToolStep.Refuse($"A {Name.ToLowerInvariant()} needs a width and a depth: the second corner is in line with the first.");
        B = b;
        Step = 2;
        return C3dToolStep.Next;
    }

    protected C3dToolStep TypedRectangle(long?[] values, in C3dDrawInput input)
    {
        var cur = Current(input);
        return SetB(new C3dPoint2(A.U + Or(values[0], cur[0]), A.V + Or(values.Length > 1 ? values[1] : null, cur[1])));
    }

    protected void PreviewRectangle(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints, long?[]? typed = null)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(Plane.FromUv(A)));
        if (Step != 1) return;
        var cur = Current(input);
        long? du = typed is { Length: > 0 } && typed[0] is { } t0 ? t0 : cur[0];
        long? dv = typed is { Length: > 1 } && typed[1] is { } t1 ? t1 : cur[1];
        if (du is { } u && dv is { } v) DrawGeometry.Chain(DrawGeometry.Rectangle(Plane, A, new C3dPoint2(A.U + u, A.V + v)), true, Host.DbuPerMicron, rubber);
    }

    public override bool FieldIsMagnitude(int step, int dim) => true;
}

public sealed class BoxTool(IC3dDrawHost host) : C3dRectangleTool(host)
{
    private static readonly string[] HeightOnly = ["height"];

    public override C3dToolKind Kind => C3dToolKind.Box;

    public override string Prompt => Step switch
    {
        0 => "Box: click the first corner on the drawing plane.",
        1 => "Box: click the opposite corner (or type the width, Tab, the depth).",
        _ => "Box: move to set the height and click (or type it). Esc cancels.",
    };

    public override IReadOnlyList<string> Dimensions => Step == 2 ? HeightOnly : base.Dimensions;

    private long? Height(in C3dDrawInput input) => Host.Along(Plane.FromUv(B), Plane.Normal, input) is { } w ? w - Plane.OffsetDbu : null;

    public override long?[] Current(in C3dDrawInput input) => Step == 2 ? [Height(input)] : base.Current(input);

    /// <summary>Width and depth are the Size components along the plane's u and v; the height is the one along its normal.</summary>
    public override string? FieldFor(int step, int dim)
    {
        var (u, v, n) = AxesOf(Plane.Plane);
        return step switch { 1 => $"Size[{(dim == 0 ? u : v)}]", 2 => $"Size[{n}]", _ => null };
    }

    public override void PreviewTyped(long?[] values, in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step != 2) { PreviewRectangle(input, rubber, fixedPoints, values); return; }
        PreviewRectangle(input, rubber, fixedPoints);
        fixedPoints.Add(M(Plane.FromUv(B)));
        DrawGeometry.Box(Plane, A, B, (values.Length > 0 ? values[0] : null) ?? Height(input) ?? 0, Host.DbuPerMicron, rubber);
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (RectangleClick(input) is { } step) return step;
        if (Height(input) is not { } h) return C3dToolStep.Refuse("Box: the cursor is looking straight along the height; type the height instead.");
        return Finish(h);
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Step == 1) return TypedRectangle(values, input);
        if (Step == 2)
        {
            long? h = values.Length > 0 ? values[0] : null;
            h ??= Height(input);
            return h is { } hh ? Finish(hh) : C3dToolStep.Refuse("Box: type the height.");
        }
        return new(false);
    }

    private C3dToolStep Finish(long height)
    {
        if (height == 0) return C3dToolStep.Refuse("A box needs a height: move off the drawing plane, or type one.");
        var p = Plane.FromUvw(A.U, A.V, Plane.OffsetDbu);
        var q = Plane.FromUvw(B.U, B.V, Plane.OffsetDbu + height);
        var box = new C3dBox
        {
            Name = Host.NextName("box"),
            Material = Host.CurrentMaterial,
            Min = new C3dPoint3(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), Math.Min(p.Z, q.Z)),
            Size = new C3dPoint3(Math.Abs(q.X - p.X), Math.Abs(q.Y - p.Y), Math.Abs(q.Z - p.Z)),
        };
        Step = 0;
        return C3dToolStep.Done(box);
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        PreviewRectangle(input, rubber, fixedPoints);
        if (Step != 2) return;
        fixedPoints.Add(M(Plane.FromUv(B)));
        DrawGeometry.Box(Plane, A, B, Height(input) ?? 0, Host.DbuPerMicron, rubber);
    }
}

public sealed class SheetTool(IC3dDrawHost host) : C3dRectangleTool(host)
{
    public override C3dToolKind Kind => C3dToolKind.Sheet;

    public override string Prompt => Step == 0
        ? "Sheet: click the first corner on the drawing plane."
        : "Sheet: click the opposite corner (or type the width, Tab, the depth). Esc cancels.";

    public override C3dToolStep Click(in C3dDrawInput input) => Finish(RectangleClick(input)!.Value);

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
        => Step == 1 ? Finish(TypedRectangle(values, input)) : new(false);

    public override string? FieldFor(int step, int dim) => step == 1 ? $"Rect.Size[{dim}]" : null;

    public override void PreviewTyped(long?[] values, in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => PreviewRectangle(input, rubber, fixedPoints, values);

    /// <summary>The rectangle's second corner makes the sheet: a Rect (corner + size) on the plane, at the material's
    /// thickness when the technology states one (else none, and the elaboration says so).</summary>
    private C3dToolStep Finish(C3dToolStep step)
    {
        if (!step.Advanced || Step != 2) return step;
        Step = 0;
        string? material = Host.CurrentMaterial;
        return C3dToolStep.Done(new C3dSheet
        {
            Name = Host.NextName("sheet"),
            Material = material,
            Plane = Plane.Plane,
            Offset = Plane.OffsetDbu,
            Rect = new C3dRect
            {
                Min = new C3dPoint2(Math.Min(A.U, B.U), Math.Min(A.V, B.V)),
                Size = new C3dPoint2(Math.Abs(B.U - A.U), Math.Abs(B.V - A.V)),
            },
            ThicknessUm = Host.ThicknessUmFor(material),
        });
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => PreviewRectangle(input, rubber, fixedPoints);
}
