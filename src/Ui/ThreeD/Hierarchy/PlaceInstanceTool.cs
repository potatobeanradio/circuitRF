// brief-em3d-48 R-em3d48-1b — placing a cell's 3D or layout view: its elaborated bounds follow the cursor as an outline
// (never the full child — that is drawn once it is placed), and one click places it.
//
// THE HANDLE is the child's ORIGIN — (0, 0, 0) of its .c3d, or for a layout its origin at its stackup's bottom (overview
// §1i) — or, with Ctrl/Cmd held, its bounding box's BOTTOM-CENTRE, which is how a die is dropped onto an attach: snap
// to the attach's top face centre and the die's underside lands on it, centred. The handle point snaps (brief 44); the
// status line says which handle is in use.
//
// Like every tool it never touches the document: it finishes, and the editor inserts the instance as ONE undo entry.

using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;

namespace CircuitRF.Ui.ThreeD.Hierarchy;

public sealed class PlaceInstanceTool(IC3dDrawHost host, C3dInstance template, string cellName, C3dPoint3 min, C3dPoint3 max)
    : C3dDrawTool(host)
{
    /// <summary>The instance to place: its name, cell reference and view; the placement is the click's.</summary>
    public C3dInstance Template { get; } = template;
    public string CellName { get; } = cellName;

    /// <summary>The child's elaborated bounds relative to its origin, in THIS document's DBU.</summary>
    public C3dPoint3 Min { get; } = min;
    public C3dPoint3 Max { get; } = max;

    /// <summary>The instance the click placed, once it has.</summary>
    public C3dInstance? Placed { get; private set; }

    /// <summary>Whether the last cursor held Ctrl/Cmd — what the status line reports.</summary>
    public bool BottomCentre { get; private set; }

    public override C3dToolKind Kind => C3dToolKind.Place;
    public override string Name => "Place Instance";

    public override string Prompt =>
        $"Click to place {Template.Name} ({CellName}, {ViewNoun(Template.View)}) — handle: " +
        (BottomCentre ? "bottom-centre (release Ctrl/Cmd for the origin)" : "origin (hold Ctrl/Cmd for the bottom-centre)") + ". Esc cancels.";

    /// <summary>What the placed thing is called in the prompt and the status line.</summary>
    public static string ViewNoun(C3dInstanceView view) => view switch
    {
        C3dInstanceView.Layout => "layout",
        C3dInstanceView.Hertzon => "Hertzon design",
        _ => "3D view",
    };

    public override IReadOnlyList<string> Dimensions => [];
    public override long?[] Current(in C3dDrawInput input) => [];

    /// <summary>The handle's offset from the child's origin: none, or the bottom-centre of its bounds.</summary>
    public C3dPoint3 Handle(bool bottomCentre)
        => bottomCentre ? new C3dPoint3(Mid(Min.X, Max.X), Mid(Min.Y, Max.Y), Min.Z) : default;

    private static long Mid(long a, long b) => (long)Math.Floor((a + (double)b) / 2);

    /// <summary>Where the child's origin lands for the cursor, or null with the reason.</summary>
    public C3dPoint3? OriginFor(in C3dDrawInput input, out string? refusal)
    {
        BottomCentre = input.Command;
        if (Host.FreePoint(input, out refusal) is not { } p) return null;
        return p - Handle(input.Command);
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (OriginFor(input, out var why) is not { } origin) return C3dToolStep.Refuse(why!);
        var placed = C3dPersistence.DeserializeInstance(C3dPersistence.SerializeInstance(Template));
        placed.Placement = placed.Placement.Translated(origin - placed.Placement.Origin);
        Placed = placed;
        return C3dToolStep.Finish;
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input) => Click(input);

    /// <summary>The child's bounds as a box outline where the click would put it, and the handle as a point.</summary>
    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<CircuitRF.Engine.Em3d.Point3> fixedPoints)
    {
        if (OriginFor(input, out _) is not { } o) return;
        var lo = o + Min;
        var hi = o + Max;
        C3dPoint3 C(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
        for (int k = 0; k < 8; k++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((k & bit) == 0) rubber.Add(new DrawSegment(M(C(k)), M(C(k | bit))));
        fixedPoints.Add(M(o + Handle(input.Command)));
    }
}
