// brief-dfr34-3 R-dfr34-3-2 — Section Through…: pick two points, and the picture a report draws by hand is made in one gesture —
// a field plot on the vertical plane through them (C3dFieldPlot.Through), the clip plane on the same plane keeping the side away
// from the camera, and the camera turned to look straight at it. ONE undo entry takes all three back: the plot is a record of
// the document, the clip plane and the camera are the view's, and SectionViewEdit carries the view's half beside the plot's.
//
// WHICH WAY THE LINE RUNS is decided here, not by the order of the clicks: the camera ends up on the line's right, so the
// distance along the line grows left to right on screen — the same way it grows across `render --field`'s picture of the plot.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The Field Plots menu's and the 3D view's item.</summary>
    public const string SectionThroughHeader = "Section Through…";

    public const string SectionThroughTip =
        "Pick two points: a field plot on the vertical plane through both, the clip plane cut there and the view turned to face it. One undo takes all three back.";

    /// <summary>Arms the two-point pick.</summary>
    [RelayCommand]
    public void SectionThrough() => SetTool(new SectionThroughTool(this));

    private Viewer3DMenuItem SectionThroughItem() => new(SectionThroughHeader, SectionThrough, Tip: SectionThroughTip);

    /// <summary>The 3D view's menu offers it while a run's fields are there to cut.</summary>
    private IEnumerable<Viewer3DMenuItem> SectionMenuItems()
    {
        if (Viewer.FieldsAvailable) yield return SectionThroughItem();
    }

    /// <summary>
    /// The finished pick: the plot, the clip plane and the camera, as one undo entry. <paramref name="a"/> and
    /// <paramref name="b"/> are DBU; only their plan positions are read. Null, or the refusal.
    /// </summary>
    public string? CommitSectionThrough(C3dPoint3 a, C3dPoint3 b)
    {
        if (a.X == b.X && a.Y == b.Y) return SectionThroughTool.SamePlanPoint;
        var scene = Viewer.Scene;
        double perDbu = C3dLowering.Metres(1, Document.DbuPerMicron);
        var from = new C3dPoint2(a.X, a.Y);
        var to = new C3dPoint2(b.X, b.Y);
        // the camera on the line's right (−normal): swap the ends when it is on the left
        var line = new FieldSectionLine(from.U * perDbu, from.V * perDbu, to.U * perDbu, to.V * perDbu);
        var (ex, ey, _) = scene.ToWorld(Viewer.View.Camera.Eye);
        if (line.Across(ex, ey) > 0)
        {
            (from, to) = (to, from);
            line = new FieldSectionLine(line.X1, line.Y1, line.X0, line.Y0);
        }

        var clipBefore = Viewer.CurrentClip;
        var cameraBefore = Viewer.CurrentCamera;
        string name = C3dFieldPlot.NextName(Document.FieldPlots, "Section");
        BeginGroup($"Section through {name}");
        AddFieldPlot(null, p =>
        {
            p.Name = name;
            p.On = C3dFieldPlotOn.ClipPlane;
            p.Through = new C3dFieldPlotThrough { From = from, To = to };
        });
        // the clip plane on the plot's own plane, keeping the side away from the camera (+normal); the camera on −normal
        var plane = line.ScenePlane(scene.Origin);
        var (nx, ny) = line.Normal;
        var normal = new Vector3((float)nx, (float)ny, 0);
        Viewer.SetSectionClip(normal, plane.Offset, flip: true);
        Viewer.LookAlong(-normal);
        Push(new SectionViewEdit($"Section through {name}", Viewer, clipBefore, cameraBefore, Viewer.CurrentClip, Viewer.CurrentCamera));
        EndGroup();
        StatusMessage = $"{name}: the vertical plane through {PlanText(from)} and {PlanText(to)}. Undo takes the plot, the clip plane and the view back.";
        return null;
    }

    private string PlanText(C3dPoint2 p)
        => $"({LayoutUnits.Format(p.U, Document.DisplayUnit, Document.DbuPerMicron)}, " +
           $"{LayoutUnits.Format(p.V, Document.DisplayUnit, Document.DbuPerMicron)}) {LayoutUnits.Suffix(Document.DisplayUnit)}";

    /// <summary>The section's view half: the clip plane and the camera, after and before. Applied when pushed (the gesture already
    /// set them), so its first Execute inside the group is the state it already holds.</summary>
    private sealed class SectionViewEdit(string description, Viewer3DViewModel viewer, Viewer3DViewModel.ClipState clipBefore, Camera3D cameraBefore,
                                         Viewer3DViewModel.ClipState clipAfter, Camera3D cameraAfter) : IUiCommand
    {
        public string Description { get; } = description;

        public void Execute()
        {
            viewer.RestoreClip(clipAfter);
            viewer.RestoreCamera(cameraAfter);
        }

        public void Undo()
        {
            viewer.RestoreClip(clipBefore);
            viewer.RestoreCamera(cameraBefore);
        }
    }

    /// <summary>A two-point plot's plane in the view: the vertical rectangle through its line, across the air box (the scene's
    /// bounds without one) and its full height.</summary>
    private (CircuitRF.Engine.Em3d.Point3[] Corners, string Label) SectionPlane(C3dFieldPlot p, FieldSectionLine line)
    {
        var scene = Viewer.Scene;
        var (lo, hi) = scene.Problem?.Boundary is { } box
            ? ((box.Min.X, box.Min.Y, box.Min.Z), (box.Max.X, box.Max.Y, box.Max.Z))
            : (scene.ToWorld(scene.BoundsMin), scene.ToWorld(scene.BoundsMax));
        // where the line runs over the box in plan
        var (dx, dy) = line.Direction;
        double s0 = double.NegativeInfinity, s1 = double.PositiveInfinity;
        void Slab(double p0, double d, double a, double b)
        {
            if (Math.Abs(d) < 1e-15) return;
            double t0 = (a - p0) / d, t1 = (b - p0) / d;
            if (t0 > t1) (t0, t1) = (t1, t0);
            s0 = Math.Max(s0, t0); s1 = Math.Min(s1, t1);
        }
        Slab(line.X0, dx, lo.Item1, hi.Item1);
        Slab(line.Y0, dy, lo.Item2, hi.Item2);
        if (!(s1 > s0)) (s0, s1) = (0, line.Length);
        CircuitRF.Engine.Em3d.Point3 P(double s, double z) { var (x, y, zz) = line.At(s, z); return new(x, y, zz); }
        return ([P(s0, lo.Item3), P(s1, lo.Item3), P(s1, hi.Item3), P(s0, hi.Item3)], $"{p.Name}: section");
    }
}
