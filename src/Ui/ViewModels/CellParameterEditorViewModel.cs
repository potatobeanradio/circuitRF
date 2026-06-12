using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
/// Also exposes Primary Schematic / Primary Symbol combo data and a read-only port count.
/// Owns its own UndoRedoStack (per the per-document-undo rule); the workspace routes
/// Undo/Redo to it while this document is active.
/// </summary>
public sealed partial class CellParameterEditorViewModel : ObservableObject
{
    private const string NoneOption = "(none)";

    private readonly CellParameterEditModel _editModel;

    // Guards against re-entrant command execution when RebuildRows refreshes combo selections.
    private bool _suppressPrimaryChangeEvents;

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

    // ── Primary Schematic / Symbol combo data ─────────────────────────────────

    /// <summary>Available .csch filenames for the cell, prefixed by "(none)".</summary>
    [ObservableProperty] private IReadOnlyList<string> _availableSchematics = [NoneOption];

    /// <summary>Available .csym filenames for the cell, prefixed by "(none)".</summary>
    [ObservableProperty] private IReadOnlyList<string> _availableSymbols = [NoneOption];

    /// <summary>
    /// Selected primary schematic combo value. Setting fires an undoable command.
    /// "(none)" maps to null in .ccell.
    /// </summary>
    [ObservableProperty] private string _selectedPrimarySchematic = NoneOption;

    /// <summary>
    /// Selected primary symbol combo value. Setting fires an undoable command.
    /// "(none)" maps to null in .ccell.
    /// </summary>
    [ObservableProperty] private string _selectedPrimarySymbol = NoneOption;

    /// <summary>
    /// Number of ports this cell declares.  Editable; writes to .ccell via an undoable command.
    /// Clamped 0–64 in the callback.
    /// </summary>
    [ObservableProperty] private int _numPorts;

    // ── Partial callbacks ─────────────────────────────────────────────────────

    partial void OnSelectedPrimarySchematicChanged(string value)
    {
        if (_suppressPrimaryChangeEvents) return;
        var mapped = value == NoneOption ? null : value;
        if (mapped == _editModel.PrimarySchematic) return;
        UndoRedo.Execute(new SetCellPrimaryCommand(_editModel, isSymbol: false, mapped));
    }

    partial void OnSelectedPrimarySymbolChanged(string value)
    {
        if (_suppressPrimaryChangeEvents) return;
        var mapped = value == NoneOption ? null : value;
        if (mapped == _editModel.PrimarySymbol) return;
        UndoRedo.Execute(new SetCellPrimaryCommand(_editModel, isSymbol: true, mapped));
    }

    partial void OnNumPortsChanged(int value)
    {
        if (_suppressPrimaryChangeEvents) return;
        int clamped = Math.Clamp(value, 0, 64);
        if (clamped != value) { NumPorts = clamped; return; }
        if (clamped == _editModel.NumPorts) return;
        UndoRedo.Execute(new SetCellPortCountCommand(_editModel, clamped));
    }

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

        BuildAvailableFileLists();   // file lists are stable for the editor's lifetime — build ONCE
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

        SyncPrimarySelectionsFromModel();
    }

    /// <summary>
    /// Builds the Primary Schematic / Symbol combo item lists from the cell folder.
    /// The available files are stable for the editor's lifetime, so this is called ONCE at
    /// construction — NEVER on parameter edits.  Reassigning the ItemsSource on every model
    /// change made the ComboBox transiently null its SelectedItem, whose write-back escaped
    /// the suppression window and fired a spurious "set primary to (none)" command that wiped
    /// the saved primary (the persistence bug).
    /// </summary>
    private void BuildAvailableFileLists()
    {
        var cellDir = _editModel.CellDir;
        AvailableSchematics = BuildFileList(cellDir, ViewType.Schematic);
        AvailableSymbols    = BuildFileList(cellDir, ViewType.Symbol);
    }

    /// <summary>
    /// Syncs the combo SELECTIONS and the port count from the model (e.g. to reflect undo/redo
    /// of a primary change).  Does NOT touch the ItemsSource — see <see cref="BuildAvailableFileLists"/>.
    /// Wrapped in the suppression guard so programmatic selection changes don't re-fire the command.
    /// </summary>
    private void SyncPrimarySelectionsFromModel()
    {
        _suppressPrimaryChangeEvents = true;
        try
        {
            SelectedPrimarySchematic = _editModel.PrimarySchematic ?? NoneOption;
            SelectedPrimarySymbol    = _editModel.PrimarySymbol    ?? NoneOption;
            NumPorts                 = _editModel.NumPorts;
        }
        finally
        {
            _suppressPrimaryChangeEvents = false;
        }
    }

    private static IReadOnlyList<string> BuildFileList(string cellDir, ViewType viewType)
    {
        var subDir = CellFolder.SubFolderPath(cellDir, viewType);
        var ext    = CellFolder.ViewExtension(viewType);
        var list   = new List<string> { NoneOption };
        if (Directory.Exists(subDir))
        {
            list.AddRange(
                Directory.GetFiles(subDir, $"*{ext}")
                         .Select(Path.GetFileName)
                         .Where(f => f is not null)
                         .Cast<string>()
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        return list;
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
