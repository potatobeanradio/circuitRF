// brief-hzn-18 R-hzn18-1 — the Inspector's One-sided row: shown for one selected lossy conductor sheet, a click is one undo entry.

using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    [ObservableProperty] private bool _hasOneSided;
    [ObservableProperty] private bool _oneSidedChecked;
    public string OneSidedTip => C3dEditorViewModel.OneSidedTip;
    private int _oneSidedIndex = -1;

    private void ClearOneSided()
    {
        HasOneSided = false;
        OneSidedChecked = false;
        _oneSidedIndex = -1;
    }

    private void LoadOneSided(int index)
    {
        if (editor.LossySheetAt(index) is not { } sheet) return;
        _oneSidedIndex = index;
        OneSidedChecked = sheet.OneSided;
        HasOneSided = true;
    }

    partial void OnOneSidedCheckedChanged(bool value)
    {
        if (_loading || !HasOneSided || _oneSidedIndex < 0) return;
        editor.SetOneSided(_oneSidedIndex, value);
    }
}
