// brief-hzn-21 R-hzn21-3 — Split at Plane: one solid wrapped in a Split through its own centre, keeping the positive side, for the user to
// adjust in Properties. One undo entry; the Split itself is made by C3dSplits.Make, which anything headless calls too.

using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>Wraps the top-level object at <paramref name="index"/> in a Split at <paramref name="plane"/> through its centre;
    /// null on success, else why not.</summary>
    public string? SplitAtPlane(int index, C3dSplitPlane plane)
    {
        if (index < 0 || index >= Document.Objects.Count || IsOperandIndex(index)) return "Select one object to split.";
        var o = Document.Objects[index];
        if (KernelMissing("Split") is { } missing) return missing;
        if (C3dSplits.Refusal(o, Document.DbuPerMicron) is { } why) return why;
        long at = C3dSplits.Centre(o, plane, Document.DbuPerMicron) ?? 0;
        var split = C3dSplits.Make(o, plane, at, C3dSplitKeep.Positive);
        string before = C3dPersistence.SerializeObject(o), after = C3dPersistence.SerializeObject(split);
        Push(new C3dEdit($"Split {o.Name} at the {plane} plane", [new C3dEditSlot(false, index, before, after)], ApplySlots));
        return null;
    }

    /// <summary>The Split at Plane submenu for the one selected object.</summary>
    private Viewer3DMenuItem? SplitMenuItem()
    {
        if (Targets() is not [{ Instance: false } t] || IsOperandIndex(t.Index)) return null;
        var o = Document.Objects[t.Index];
        string? why = KernelMissing("Split") ?? C3dSplits.Refusal(o, Document.DbuPerMicron);
        const string tip = "Cuts it at a plane through its centre and keeps the positive side: one half of a symmetric structure. " +
                           "Set the plane, where it lies and the side kept in Properties; mark the air box face on the plane Symmetry.";
        return new Viewer3DMenuItem("Split at Plane", Enabled: why is null, Tip: why ?? tip, Children:
        [
            .. new[] { C3dSplitPlane.YZ, C3dSplitPlane.XZ, C3dSplitPlane.XY }.Select(p =>
                new Viewer3DMenuItem($"{p} (normal to {"xyz"[p switch { C3dSplitPlane.YZ => 0, C3dSplitPlane.XZ => 1, _ => 2 }]})",
                                     () => Report(SplitAtPlane(t.Index, p)), Tip: tip)),
        ]);
    }
}
