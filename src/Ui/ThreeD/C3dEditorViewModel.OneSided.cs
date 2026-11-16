// brief-hzn-18 R-hzn18-1 — a lossy sheet's One-sided flag: one surface impedance carrying the current of both faces (Hertzon's
// one-sided Finite Conductivity sheet), written on the sheet that carries the metal (a sheet boolean's Blank), one undo entry.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    public const string OneSidedTip =
        "Ticked, the sheet's metal is one surface impedance carrying the current of both its faces — about twice the conductor loss " +
        "of a two-sided sheet, which solves a current on each face. Hertzon's older default, kept so a design from it gives the same " +
        "answer. EMerge runs it; Palace and openEMS do not.";

    /// <summary>The sheet that carries <paramref name="index"/>'s metal when it is a lossy conductor sheet, else null.</summary>
    public C3dSheet? LossySheetAt(int index)
    {
        if (index < 0 || index >= Document.Objects.Count || C3dSheetBooleans.Carrier(Document.Objects[index]) is not { } sheet) return null;
        var m = Elaboration?.Technology?.FindMaterial(sheet.Material ?? "");
        return m?.Sigma20 is > 0 and var s && double.IsFinite(s) ? sheet : null;
    }

    /// <summary>Sets the One-sided flag of object <paramref name="index"/>'s carrying sheet, one undo entry.</summary>
    public void SetOneSided(int index, bool oneSided)
    {
        if (index < 0 || index >= Document.Objects.Count || IsOperandIndex(index)) return;
        string before = C3dPersistence.SerializeObject(Document.Objects[index]);
        var copy = C3dPersistence.DeserializeObject(before);
        if (C3dSheetBooleans.Carrier(copy) is not { } sheet) return;
        sheet.OneSided = oneSided;
        string after = C3dPersistence.SerializeObject(copy);
        if (after != before)
            Push(new C3dEdit($"{(oneSided ? "One-sided" : "Two-sided")} {Document.Objects[index].Name}", [new C3dEditSlot(false, index, before, after)], ApplySlots));
    }
}
