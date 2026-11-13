// brief-cxp-2 R-cxp2-2 — Coaxial Port: the centre on the drawing plane, then the inner radius, then the outer (each a click or
// typed), made a PORT record whose sheet is that ring. As the rectangle Port tool, the polarity is not the tool's to decide —
// the editor resolves the ring by what its two circles touch on every move (C3dPorts) and names the + and − ends, or the
// refusal, beside it.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class CoaxialPortTool(IC3dDrawHost host, Func<C3dPort> template) : C3dDrawTool(host)
{
    private DrawingPlane _plane;
    private C3dPoint2 _centre;
    private long _inner;

    private static readonly string[] None = [];
    private static readonly string[] Inner = ["inner radius"];
    private static readonly string[] Outer = ["outer radius"];

    public override C3dToolKind Kind => C3dToolKind.CoaxialPort;

    public override string Name => "Coaxial Port";

    public override bool TakesPlaneAtFirstClick => true;

    /// <summary>While only the centre is placed: both radii are measured on the plane, so a plane chosen now is taken.</summary>
    public override C3dPoint3? PlaneAnchor => Step == 1 ? _plane.FromUv(_centre) : null;

    public override void RetakePlane(DrawingPlane plane)
    {
        var c = _plane.FromUv(_centre);
        _plane = plane;
        _centre = plane.ToUv(c);
    }

    public override string Prompt => Step switch
    {
        0 => "Coaxial Port: click the centre of the coax on the drawing plane (the pin's axis).",
        1 => "Coaxial Port: click on the pin's surface to set the inner radius (or type it).",
        _ => "Coaxial Port: click on the shell's inner surface to set the outer radius (or type it). Esc cancels.",
    };

    public override IReadOnlyList<string> Dimensions => Step switch { 1 => Inner, 2 => Outer, _ => None };

    /// <summary>The port the last click made — the editor inserts it as one undo entry.</summary>
    public C3dPort? Made { get; private set; }

    private long? CursorRadius(in C3dDrawInput input)
    {
        if (Host.PlanePoint(input, out _) is not { } p) return null;
        var uv = _plane.ToUv(p);
        double du = uv.U - _centre.U, dv = uv.V - _centre.V;
        return (long)Math.Round(Math.Sqrt(du * du + dv * dv), MidpointRounding.AwayFromZero);
    }

    public override long?[] Current(in C3dDrawInput input) => Step is 1 or 2 ? [CursorRadius(input)] : [];

    public override bool FieldIsMagnitude(int step, int dim) => step is 1 or 2;

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
            default:
                if (CursorRadius(input) is not { } r) return C3dToolStep.Refuse("Coaxial Port: the drawing plane is edge-on; type the radius.");
                return Step == 1 ? SetInner(r) : Finish(r);
        }
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        long? v = values.Length > 0 ? values[0] : null;
        if (Step is not (1 or 2)) return new(false);
        if ((v is { } typed ? Math.Abs(typed) : CursorRadius(input)) is not { } r) return C3dToolStep.Refuse("Coaxial Port: type the radius.");
        return Step == 1 ? SetInner(r) : Finish(r);
    }

    private C3dToolStep SetInner(long r)
    {
        if (r <= 0) return C3dToolStep.Refuse("A coaxial port's inner radius is the pin's: click on the pin's surface, away from the centre, or type it.");
        _inner = r;
        Step = 2;
        return C3dToolStep.Next;
    }

    private C3dToolStep Finish(long outer)
    {
        if (outer <= _inner)
            return C3dToolStep.Refuse("A coaxial port's outer radius is the shell's, beyond the inner one: click farther from the centre, or type it.");
        Step = 0;
        Made = Port(_inner, outer);
        return C3dToolStep.Finish;
    }

    /// <summary>The port the ring would make with its outer circle at the cursor; null before the inner radius is set.</summary>
    public C3dPort? Preview(in C3dDrawInput input)
        => Step == 2 && CursorRadius(input) is { } r && r > _inner ? Port(_inner, r) : null;

    private C3dPort Port(long inner, long outer)
    {
        var p = template();
        p.Plane = _plane.Plane;
        p.Offset = _plane.OffsetDbu;
        p.Annulus = new C3dPortAnnulus { Centre = _centre, Inner = inner, Outer = outer };
        return p;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_plane.FromUv(_centre)));
        if (Step == 2) DrawGeometry.Circle(_plane, _centre, _inner, 0, Host.DbuPerMicron, rubber);
        if (CursorRadius(input) is { } r && r > 0) DrawGeometry.Circle(_plane, _centre, r, 0, Host.DbuPerMicron, rubber);
    }

    public override void PreviewTyped(long?[] values, in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (values.Length > 0 && values[0] is { } typed && typed != 0 && Step is 1 or 2)
        {
            fixedPoints.Add(M(_plane.FromUv(_centre)));
            if (Step == 2) DrawGeometry.Circle(_plane, _centre, _inner, 0, Host.DbuPerMicron, rubber);
            DrawGeometry.Circle(_plane, _centre, Math.Abs(typed), 0, Host.DbuPerMicron, rubber);
            return;
        }
        Preview(input, rubber, fixedPoints);
    }
}
