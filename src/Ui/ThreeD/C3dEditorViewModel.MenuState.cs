// 3D menu cleanup (2026-09-27) — whether each 3D ▸ Modify / Boolean item can act on the selection NOW, so the menu bar
// enables exactly what the canvas context menu offers enabled. Every answer is read off the predicate that menu's own
// builder uses (Targets, GroupRefusal, BooleanRefusal, EdgeOpRefusal, FaceSelection, VertexSelection, ExtrudeSource …), so
// the two cannot drift. MenuStateChanged is what tells the shell to re-ask: on macOS a key equivalent is validated
// against its item's enabled state, so a stale answer would block the shortcut, not just grey a row.

using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>Raised when what the 3D menu can act on may have changed: the selection, the select mode, the tree's row,
    /// or an adopted edit.</summary>
    public event Action? MenuStateChanged;

    private void RaiseMenuStateChanged()
    {
        RaiseSelectionVisibilityChanged();          // brief-em3d-91 — the toolbar's eye button reads the same moments
        MenuStateChanged?.Invoke();
    }

    /// <summary>Anything a Modify item could act on: a selection in the view, or a polyline or wire selected in the tree.</summary>
    public bool HasModifySelection
        => !IsViewOnly && (Viewer.Selection.Count > 0 || ExtrudeSource() is not null || SelectedWires().Count > 0);

    /// <summary>3D ▸ Modify ▸ Extrude: one sheet selected, or a polyline selected in the tree, that can be extruded.</summary>
    public bool CanExtrude => !IsViewOnly && ExtrudeSource() is { } s && ExtrudeTool.CannotExtrude(s.Obj) is null;

    /// <summary>
    /// Whether <see cref="RunModify"/>(<paramref name="which"/>) would act on the selection now — false where it would only
    /// put a refusal on the status line. The reason itself stays where it always was: the context menu's tooltip.
    /// </summary>
    public bool CanRunModify(string? which)
    {
        if (IsViewOnly || string.IsNullOrEmpty(which)) return false;
        var mode = Viewer.SelectMode;
        switch (which)
        {
            case "Move":
                return mode switch
                {
                    // brief-dfr34-1 R-dfr34-1-1 — a selected port moves too (StartMove hands ports to StartPortMove)
                    Scene3DSelectMode.Object => Targets().Count > 0 || SelectedPorts().Count > 0,
                    Scene3DSelectMode.Face   => CanRunModify("FaceMove"),
                    Scene3DSelectMode.Vertex => CanRunModify("VertexMove"),
                    _                        => false,
                };
            case "MoveX" or "MoveY" or "MoveZ" or "Rotate" or "Duplicate" or "Array":
                return Targets().Count > 0;
            case "Front" or "Forward" or "Backward" or "Back":
                return Targets().Any(t => !t.Instance);
            case "Group":
                return _entered is null && mode == Scene3DSelectMode.Object && GroupRefusal(SelectedUnits()) is null;
            case "Ungroup":
                return _entered is null && mode == Scene3DSelectMode.Object && SelectedUnits().Any(u => u.IsGroup);
            case "ReseatWires":
                return SelectedWires().Count > 0;
            case "StepReload":     // brief-em3d-129 — 3D ▸ STEP
                return Targets() is [{ Instance: false } st] && !IsOperandIndex(st.Index) && ReloadRefusal(StepAt(st.Index)) is null;
            case "StepSplit":
                return SplitSelectionRefusal() is null;
            case "StepPrism":      // brief-em3d-130
                return ConvertSelectionRefusal(StepConvertKind.Prism) is null;
            case "StepBox":
                return ConvertSelectionRefusal(StepConvertKind.Box) is null;
            case "StepPolyhedron":
                return ConvertSelectionRefusal(StepConvertKind.Polyhedron) is null;
            case "BooleanSubtract" or "BooleanUnite" or "BooleanIntersect":
                return BooleanRefusal() is null;
            case "BooleanDissolve":
                return KernelMissing("Dissolve Boolean") is null && SelectedBoolean() >= 0;
            case "BooleanEnter":
                return KernelMissing("Edit Operands") is null && SelectedBoolean() >= 0;
            case "Fillet":
                return EdgeOpRefusal(C3dEdgeOp.Fillet) is null;
            case "Chamfer":
                return EdgeOpRefusal(C3dEdgeOp.Chamfer) is null;
            case "TangentChain":
                return mode == Scene3DSelectMode.Edge && Viewer.Selection.Any(i => i.IsEdge);
            case "PushPull":
                return EditableFace() is { } fp && fp.Obj is not (C3dSphere or C3dEllipsoid);
            case "FaceMove":
                return EditableFace() is { } fm && fm.Obj is not (C3dCylinder or C3dSphere or C3dEllipsoid);
            case "ExtrudeFace" or "AlignFace":
                return EditableFace() is { } fe && fe.Obj is not (C3dSphere or C3dEllipsoid) && !(fe.Obj is C3dCylinder && fe.Face == "side");
            case "CopySheet":
                return EditableFace() is { } fc && C3dFaceCommands.Polygon(fc.Obj, fc.Face, out _) is (_, not null);
            case "MeasureFace":
                return FaceSelection() is not null;
            case "VertexMove":
                return VertexSelection() is { Vertex: >= 0 };
            case "MeasureFrom":
                return mode == Scene3DSelectMode.Vertex && Viewer.Selection.Count == 1;
            case "ConvertPoly":
                return ConvertibleSelection() is not null;
        }
        if (which.StartsWith("ConvertPoly", StringComparison.Ordinal)) return ConvertibleSelection() is C3dCylinder;
        if (which.StartsWith("Mirror", StringComparison.Ordinal) || which.StartsWith("Rot", StringComparison.Ordinal))
            return Targets().Count > 0;
        if (which.StartsWith("Align", StringComparison.Ordinal)) return Targets().Count >= 2;
        return false;
    }

    /// <summary>The one selected face when a face edit may change it (not a kernel-made solid's, D13).</summary>
    private (int Index, C3dObject Obj, string Face, Scene3DObject Scene, int SceneFace)? EditableFace()
        => FaceSelection() is { } f && ResultEditRefusal(f.Obj) is null ? f : null;

    /// <summary>Object mode's one selected box, prism or cylinder of this document — what Convert to Polyhedron takes.</summary>
    private C3dObject? ConvertibleSelection()
        => Viewer.SelectMode == Scene3DSelectMode.Object && Viewer.SelectedObjects() is [var o] && EditableIndex(o) is >= 0 and var i
           && ObjectAt(i) is (C3dBox or C3dPrism or C3dCylinder) and var selected
            ? selected
            : null;
}
