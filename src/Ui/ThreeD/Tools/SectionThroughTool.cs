// brief-dfr34-3 R-dfr34-3-2 — Section Through…: two points picked with the snapped point picking every tool uses (an object's
// centre, a vertex, an edge — so a via's axis and a pad's centre are one click each), and nothing made by the tool: the editor
// turns them into a field plot, a clip plane and a camera, as one undo entry.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class SectionThroughTool(IC3dDrawHost host) : C3dDrawTool(host)
{
    private C3dPoint3 _first;
    private static readonly string[] None = [];

    public override C3dToolKind Kind => C3dToolKind.SectionThrough;
    public override string Name => "Section through";

    public override string Prompt => Step == 0
        ? "Section Through: click the first point (a snap on an object's centre, a vertex or an edge is exact)."
        : "Section Through: click the second point: the section is the vertical plane through both. Esc cancels.";

    public override IReadOnlyList<string> Dimensions => None;

    public override long?[] Current(in C3dDrawInput input) => [];

    /// <summary>The two points of the finished gesture, handed over once.</summary>
    public (C3dPoint3 From, C3dPoint3 To)? Taken { get; private set; }

    public (C3dPoint3 From, C3dPoint3 To)? Take() { var t = Taken; Taken = null; return t; }

    /// <summary>The refusal for a second point directly above or below the first: a vertical plane needs two places in plan.</summary>
    public const string SamePlanPoint =
        "Pick a second point apart from the first in plan: two points one above the other hold no single vertical plane.";

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Host.FreePoint(input, out var why) is not { } p) return C3dToolStep.Refuse(why!);
        if (Step == 0) { _first = p; Step = 1; return C3dToolStep.Next; }
        if (p.X == _first.X && p.Y == _first.Y) return C3dToolStep.Refuse(SamePlanPoint);
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
