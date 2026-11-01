// brief-em3d-45 R-em3d45-3e — Cylinder (owner decision D6): the centre on the plane, the radius (a click or typed),
// then the height as a box's. Stored as a C3dCylinder whose axis is the plane's normal: Base at the centre on the
// plane, Length the signed height, Radius — the one field a typed radius lands in.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class CylinderTool(IC3dDrawHost host) : C3dDrawTool(host)
{
    private DrawingPlane _plane;
    private C3dPoint2 _centre;
    private long _radius;

    private static readonly string[] None = [];
    private static readonly string[] Radius = ["radius"];
    private static readonly string[] Height = ["height"];

    public override C3dToolKind Kind => C3dToolKind.Cylinder;

    public override bool TakesPlaneAtFirstClick => true;

    public override string Prompt => Step switch
    {
        0 => "Cylinder: click the centre on the drawing plane.",
        1 => "Cylinder: click to set the radius (or type it).",
        _ => "Cylinder: move to set the height and click (or type it). Esc cancels.",
    };

    public override IReadOnlyList<string> Dimensions => Step switch { 1 => Radius, 2 => Height, _ => None };

    private long? CursorRadius(in C3dDrawInput input)
    {
        if (Host.PlanePoint(input, out _) is not { } p) return null;
        var uv = _plane.ToUv(p);
        double du = uv.U - _centre.U, dv = uv.V - _centre.V;
        return (long)Math.Round(Math.Sqrt(du * du + dv * dv), MidpointRounding.AwayFromZero);
    }

    private long? CursorHeight(in C3dDrawInput input)
        => Host.Along(_plane.FromUv(_centre), _plane.Normal, input) is { } w ? w - _plane.OffsetDbu : null;

    public override long?[] Current(in C3dDrawInput input) => Step switch
    {
        1 => [CursorRadius(input)],
        2 => [CursorHeight(input)],
        _ => [],
    };

    /// <summary>The radius is <c>Radius</c> (a magnitude); the height is the signed <c>Length</c>.</summary>
    public override string? FieldFor(int step, int dim) => step switch { 1 => "Radius", 2 => "Length", _ => null };

    public override bool FieldIsMagnitude(int step, int dim) => step == 1;

    public override void PreviewTyped(long?[] values, in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        long? typed = values.Length > 0 ? values[0] : null;
        if (Step == 1 && typed is { } r && r > 0)
        {
            fixedPoints.Add(M(_plane.FromUv(_centre)));
            DrawGeometry.Circle(_plane, _centre, r, 0, Host.DbuPerMicron, rubber);
            return;
        }
        if (Step == 2 && typed is { } h)
        {
            fixedPoints.Add(M(_plane.FromUv(_centre)));
            DrawGeometry.Cylinder(_plane, _centre, _radius, h, Host.DbuPerMicron, rubber);
            return;
        }
        Preview(input, rubber, fixedPoints);
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        switch (Step)
        {
            case 0:
                if (Host.PlanePoint(input, out var why) is not { } p) return C3dToolStep.Refuse(why!);
                _plane = Host.Plane;
                _centre = _plane.ToUv(p);
                Step = 1;
                return C3dToolStep.Next;
            case 1:
                if (CursorRadius(input) is not { } r) return C3dToolStep.Refuse("Cylinder: the drawing plane is edge-on; type the radius.");
                return SetRadius(r);
            default:
                if (CursorHeight(input) is not { } h) return C3dToolStep.Refuse("Cylinder: the cursor is looking straight along the height; type the height instead.");
                return Finish(h);
        }
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        long? v = values.Length > 0 ? values[0] : null;
        if (Step == 1) return (v ?? CursorRadius(input)) is { } r ? SetRadius(r) : C3dToolStep.Refuse("Cylinder: type the radius.");
        if (Step == 2) return (v ?? CursorHeight(input)) is { } h ? Finish(h) : C3dToolStep.Refuse("Cylinder: type the height.");
        return new(false);
    }

    private C3dToolStep SetRadius(long r)
    {
        if (r <= 0) return C3dToolStep.Refuse("A cylinder needs a radius: click away from the centre, or type one.");
        _radius = r;
        Step = 2;
        return C3dToolStep.Next;
    }

    private C3dToolStep Finish(long height)
    {
        if (height == 0) return C3dToolStep.Refuse("A cylinder needs a height: move off the drawing plane, or type one.");
        Step = 0;
        return C3dToolStep.Done(new C3dCylinder
        {
            Name = Host.NextName("cylinder"),
            Material = Host.CurrentMaterial,
            Base = _plane.FromUv(_centre),
            Axis = _plane.Normal,
            Length = height,
            Radius = _radius,
        });
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_plane.FromUv(_centre)));
        if (Step == 1)
        {
            if (CursorRadius(input) is { } r && r > 0) DrawGeometry.Circle(_plane, _centre, r, 0, Host.DbuPerMicron, rubber);
            return;
        }
        DrawGeometry.Cylinder(_plane, _centre, _radius, CursorHeight(input) ?? 0, Host.DbuPerMicron, rubber);
    }
}
