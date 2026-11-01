// brief-em3d-102 R-em3d102-5 — Sphere: the centre on the plane, then the radius (a click or typed). Centred ON the drawing
// plane (owner decision D2): the click is the centre, as the 2D circle's click and the cylinder's base are. Stored as a
// C3dSphere — Centre the click, Radius the one field a typed radius lands in.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class SphereTool(IC3dDrawHost host) : C3dDrawTool(host)
{
    private DrawingPlane _plane;
    private C3dPoint2 _centre;

    private static readonly string[] None = [];
    private static readonly string[] Radius = ["radius"];

    public override C3dToolKind Kind => C3dToolKind.Sphere;

    public override bool TakesPlaneAtFirstClick => true;

    public override string Prompt => Step switch
    {
        0 => "Sphere: click the centre on the drawing plane.",
        _ => "Sphere: click to set the radius (or type it). Esc cancels.",
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

    /// <summary>The radius is <c>Radius</c>, a magnitude.</summary>
    public override string? FieldFor(int step, int dim) => step == 1 ? "Radius" : null;

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
        return CursorRadius(input) is { } r ? Finish(r) : C3dToolStep.Refuse("Sphere: the drawing plane is edge-on; type the radius.");
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Step != 1) return new(false);
        long? v = values.Length > 0 ? values[0] : null;
        return (v ?? CursorRadius(input)) is { } r ? Finish(r) : C3dToolStep.Refuse("Sphere: type the radius.");
    }

    private C3dToolStep Finish(long r)
    {
        if (r <= 0) return C3dToolStep.Refuse("A sphere needs a radius: click away from the centre, or type one.");
        Step = 0;
        return C3dToolStep.Done(new C3dSphere
        {
            Name = Host.NextName("sphere"),
            Material = Host.CurrentMaterial,
            Centre = _plane.FromUv(_centre),
            Radius = r,
        });
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_plane.FromUv(_centre)));
        if (CursorRadius(input) is { } r && r > 0) DrawGeometry.Sphere(_plane, _centre, r, Host.DbuPerMicron, rubber);
    }
}
