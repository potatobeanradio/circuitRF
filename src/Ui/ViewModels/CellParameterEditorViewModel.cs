using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Cell;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// VM for the cell-parameter editor — edits the cell's declared parameter interface in
/// its .ccell (add / remove / rename rows + defaults), NOT instance values.
/// Owns its own UndoRedoStack (per the per-document-undo rule); the workspace routes
/// Undo/Redo to it while this document is active.
/// </summary>
public sealed partial class CellParameterEditorViewModel : ObservableObject
{
    private readonly CellParameterEditModel _editModel;

    /// <summary>Display name shown in the editor header.</summary>
    [ObservableProperty] private string _cellName = "";

    // ── Own undo/redo stack ───────────────────────────────────────────────────

    public UndoRedoStack UndoRedo { get; } = new();

    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }

    // ── Rows ──────────────────────────────────────────────────────────────────

    public ObservableCollection<CellParameterRowViewModel> Rows { get; } = [];

    public bool HasParameters   => Rows.Count > 0;
    public bool HasNoParameters => Rows.Count == 0;

    // ── Construction ──────────────────────────────────────────────────────────

    public CellParameterEditorViewModel(string cellName, CellParameterEditModel editModel)
    {
        CellName   = cellName;
        _editModel = editModel;

        UndoCommand = new RelayCommand(
            () => UndoRedo.Undo(),
            () => UndoRedo.CanUndo);

        RedoCommand = new RelayCommand(
            () => UndoRedo.Redo(),
            () => UndoRedo.CanRedo);

        UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
        };

        _editModel.Changed += (_, _) => RebuildRows();

        RebuildRows();
    }

    // ── Add Parameter command ─────────────────────────────────────────────────

    [RelayCommand]
    private void AddParameter()
    {
        var p = new CcellParameter
        {
            Name              = GenerateUniqueName("Param"),
            DefaultExpression = "0",
            ShowOnSchematic   = true,
        };
        UndoRedo.Execute(new AddCellParameterCommand(_editModel, p));
    }

    // ── Internal surface for CellParameterRowViewModel ────────────────────────

    internal CellParameterEditModel EditModel => _editModel;

    internal void Execute(IUiCommand cmd) => UndoRedo.Execute(cmd);

    internal void RemoveRow(CellParameterRowViewModel row)
    {
        var p = _editModel.MutableParameters.FirstOrDefault(x => ReferenceEquals(x, row.Parameter));
        if (p is null) return;
        UndoRedo.Execute(new RemoveCellParameterCommand(_editModel, p));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var p in _editModel.Parameters)
            Rows.Add(new CellParameterRowViewModel(p, this));

        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(HasNoParameters));
    }

    private string GenerateUniqueName(string prefix)
    {
        var existing = new HashSet<string>(_editModel.Parameters.Select(p => p.Name), StringComparer.Ordinal);
        for (int i = 1; i <= 1000; i++)
        {
            string candidate = $"{prefix}{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"{prefix}{Guid.NewGuid().ToString("N")[..4]}";
    }
}
