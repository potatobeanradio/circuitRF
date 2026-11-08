// brief-sar-3 R-sar3-1 — Ellipsoid: drawn as a sphere is (the centre on the plane, then the radius, a click or typed), and
// stored as a C3dEllipsoid whose three semi-axes are that radius. Each semi-axis is then its own field in the Inspector: a
// round start is the one shape two clicks can say without guessing an aspect.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class EllipsoidTool(IC3dDrawHost host) : C3dDrawTool(host)
{
    private DrawingPlane _plane;
    private C3dPoint2 _centre;

    private static readonly string[] None = [];
    private static readonly string[] Radius = ["semi-axis"];

    public override C3dToolKind Kind => C3dToolKind.Ellipsoid;

    public override bool TakesPlaneAtFirstClick => true;

    /// <summary>While only the centre is placed: the radius is measured on the plane, so a plane chosen now is taken.</summary>
    public override C3dPoint3? PlaneAnchor => Step == 1 ? _plane.FromUv(_centre) : null;

    public override void RetakePlane(DrawingPlane plane)
    {
        var c = _plane.FromUv(_centre);
        _plane = plane;
        _centre = plane.ToUv(c);
    }

    public override string Prompt => Step switch
    {
        0 => "Ellipsoid: click the centre on the drawing plane.",
        _ => "Ellipsoid: click to set the semi-axes (or type one); each is edited apart in the Inspector. Esc cancels.",
    };

    public override IReadOnlyList<string> Dimensions => Step == 1 ? Radius : None;

    private long? CursorRadius(in C3dDrawInput input)
    {
        if (Host.PlanePoint(input, out _) is not { } p) return null;
        var uv = _plane.ToUv(p);
        double du = uv.U - _centre.U, dv = uv.V - _centre.V;
        return (long)Math.Round(Math.Sqrt(du * du + dv * dv), MidpointRounding.AwayFromZero);
    }

    public override long?[] Current(in C3dDrawInput input) => Step == 1 ? [CursorRadius(input)] : [];

    public override bool FieldIsMagnitude(int step, int dim) => step == 1;

    public override void PreviewTyped(long?[] values, in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 1 && values.Length > 0 && values[0] is { } r && r > 0)
        {
            fixedPoints.Add(M(_plane.FromUv(_centre)));
            DrawGeometry.Sphere(_plane, _centre, r, Host.DbuPerMicron, rubber);
            return;
        }
        Preview(input, rubber, fixedPoints);
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Step == 0)
        {
            if (Host.PlanePoint(input, out var why) is not { } p) return C3dToolStep.Refuse(why!);
            _plane = Host.Plane;
            _centre = _plane.ToUv(p);
            Step = 1;
            return C3dToolStep.Next;
        }
        return CursorRadius(input) is { } r ? Finish(r) : C3dToolStep.Refuse("Ellipsoid: the drawing plane is edge-on; type the semi-axis.");
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Step != 1) return new(false);
        long? v = values.Length > 0 ? values[0] : null;
        return (v ?? CursorRadius(input)) is { } r ? Finish(r) : C3dToolStep.Refuse("Ellipsoid: type the semi-axis.");
    }

    private C3dToolStep Finish(long r)
    {
        if (r <= 0) return C3dToolStep.Refuse("An ellipsoid needs its semi-axes: click away from the centre, or type one.");
        Step = 0;
        return C3dToolStep.Done(new C3dEllipsoid
        {
            Name = Host.NextName("ellipsoid"),
            Material = Host.CurrentMaterial,
            Centre = _plane.FromUv(_centre),
            SemiAxes = new C3dPoint3(r, r, r),
        });
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_plane.FromUv(_centre)));
        if (CursorRadius(input) is { } r && r > 0) DrawGeometry.Sphere(_plane, _centre, r, Host.DbuPerMicron, rubber);
    }
}
