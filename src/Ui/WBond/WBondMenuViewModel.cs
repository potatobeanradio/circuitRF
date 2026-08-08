using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The standalone binary's menu bar, as commands (wbond.md §11 / brief-wbond-wbe M2).
///
/// <para><b>Hooks, not implementations</b> — the same shape <c>HarmonicaMenuViewModel</c> uses, and
/// for the same reason: every one of these actions needs a <c>Window</c> (a file picker, a modal) or
/// the view's own state, neither of which a view model may reach without dragging Avalonia across
/// the seam. The shell wires them once.</para>
///
/// <para><b>Every item here already exists as a command somewhere in circuitRF.</b> M2 is wiring, not
/// new behaviour: Open/Save route through the same <see cref="WBondDocument"/> methods the docked
/// tab uses, and the DXF pair and the wirebond-table import are the toolbar buttons the editor view
/// already carries. Nothing is re-implemented for the standalone.</para>
/// </summary>
public sealed partial class WBondMenuViewModel : ObservableObject
{
    public Action? NewDocumentHook    { get; set; }
    public Action? OpenDocumentHook   { get; set; }
    public Action? SaveDocumentHook   { get; set; }
    public Action? SaveDocumentAsHook { get; set; }
    public Action? CloseWindowHook    { get; set; }

    public Action? ImportWireTableHook { get; set; }
    public Action? ImportWiresDxfHook  { get; set; }
    public Action? ExportDxfHook       { get; set; }
    public Action? ExportTouchstoneHook{ get; set; }

    public Action? UndoHook            { get; set; }
    public Action? RedoHook            { get; set; }
    public Action? CopyHook            { get; set; }
    public Action? CopyGraphicHook     { get; set; }
    public Action? PasteHook           { get; set; }
    public Action? PreferencesHook     { get; set; }

    public Action? SelectAllWiresHook  { get; set; }
    public Action? CheckDesignRulesHook{ get; set; }
    public Action? HelpHook            { get; set; }

    [RelayCommand] private void NewDocument()    => NewDocumentHook?.Invoke();
    [RelayCommand] private void OpenDocument()   => OpenDocumentHook?.Invoke();
    [RelayCommand] private void SaveDocument()   => SaveDocumentHook?.Invoke();
    [RelayCommand] private void SaveDocumentAs() => SaveDocumentAsHook?.Invoke();
    [RelayCommand] private void CloseWindow()    => CloseWindowHook?.Invoke();

    [RelayCommand] private void ImportWireTable()  => ImportWireTableHook?.Invoke();
    [RelayCommand] private void ImportWiresDxf()   => ImportWiresDxfHook?.Invoke();
    [RelayCommand] private void ExportDxf()        => ExportDxfHook?.Invoke();
    [RelayCommand] private void ExportTouchstone() => ExportTouchstoneHook?.Invoke();

    [RelayCommand] private void Undo()        => UndoHook?.Invoke();
    [RelayCommand] private void Redo()        => RedoHook?.Invoke();
    [RelayCommand] private void Copy()        => CopyHook?.Invoke();
    [RelayCommand] private void CopyGraphic() => CopyGraphicHook?.Invoke();
    [RelayCommand] private void Paste()       => PasteHook?.Invoke();
    [RelayCommand] private void Preferences() => PreferencesHook?.Invoke();

    [RelayCommand] private void SelectAllWires()   => SelectAllWiresHook?.Invoke();
    [RelayCommand] private void CheckDesignRules() => CheckDesignRulesHook?.Invoke();
    [RelayCommand] private void Help()             => HelpHook?.Invoke();
}
