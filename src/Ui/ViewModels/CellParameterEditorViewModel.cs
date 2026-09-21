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
    private const string NoneOption = "(none specified)";

    private readonly CellParameterEditModel _editModel;

    // Guards against re-entrant command execution when RebuildRows refreshes combo selections.
    private bool _suppressPrimaryChangeEvents;

    /// <summary>Display name shown in the editor header.</summary>
    [ObservableProperty] private string _cellName = "";

    // ── Own undo/redo stack ───────────────────────────────────────────────────

    /// <summary>Absolute path of the cell's own <c>.ccell</c> — what this editor edits, and what
    /// the document tab's "Reveal in …" item shows. Read-only passthrough to the edit model.</summary>
    public string CcellPath => _editModel.CcellPath;

    public UndoRedoStack UndoRedo { get; } = new();

    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }

    // ── Rows ──────────────────────────────────────────────────────────────────

    public ObservableCollection<CellParameterRowViewModel> Rows { get; } = [];

    public bool HasParameters   => Rows.Count > 0;
    public bool HasNoParameters => Rows.Count == 0;

    // ── Primary Schematic / Symbol combo data ─────────────────────────────────

    /// <summary>Available .csch filenames for the cell, prefixed by "(none specified)".</summary>
    [ObservableProperty] private IReadOnlyList<string> _availableSchematics = [NoneOption];

    /// <summary>Available .csym filenames for the cell, prefixed by "(none specified)".</summary>
    [ObservableProperty] private IReadOnlyList<string> _availableSymbols = [NoneOption];

    /// <summary>
    /// Selected primary schematic combo value. Setting fires an undoable command.
    /// "(none specified)" maps to null in .ccell.
    /// </summary>
    [ObservableProperty] private string _selectedPrimarySchematic = NoneOption;

    /// <summary>
    /// Selected primary symbol combo value. Setting fires an undoable command.
    /// "(none specified)" maps to null in .ccell.
    /// </summary>
    [ObservableProperty] private string _selectedPrimarySymbol = NoneOption;

    /// <summary>
    /// Number of ports this cell declares.  Editable; writes to .ccell via an undoable command.
    /// Clamped 0–64 in the callback.
    /// </summary>
    [ObservableProperty] private int _numPorts;

    // ── Terminals (brief-lvs-1-terminal-map.md R-lvs1-4c) ─────────────────────
    //
    // R-aut4-2's rule, applied: a rule that exists only in `check` is a rule the application does not
    // enforce, so a design would pass headlessly and be refused when someone opened it. Every finding
    // and every sentence here is `TerminalMap`'s — this panel computes none of its own.

    /// <summary>The resolved map, one row per terminal.</summary>
    public ObservableCollection<CellTerminalRowViewModel> TerminalRows { get; } = [];

    /// <summary><b>Always shown</b> (R-lvs1-2b): a derived map that does not say it was derived is
    /// indistinguishable from a declared one, and the two have very different failure modes.</summary>
    [ObservableProperty] private string _terminalOriginText = "";

    /// <summary>What the derivation had to say, and every <c>check.terminals.*</c> finding — the
    /// SAME ones <c>circuitrf check</c> prints, because they come from the same call.</summary>
    public ObservableCollection<string> TerminalNotes    { get; } = [];
    public ObservableCollection<string> TerminalFindings { get; } = [];

    /// <summary>R-lvs1-3d's two lists, shown SIDE BY SIDE, because that is a two-minute fix the user
    /// can only make if they can see both.</summary>
    public ObservableCollection<string> UnmatchedSymbolPins { get; } = [];
    public ObservableCollection<string> UnmatchedLayoutPins { get; } = [];

    public bool HasTerminals          => TerminalRows.Count > 0;
    public bool HasNoTerminals        => TerminalRows.Count == 0;
    public bool HasTerminalNotes      => TerminalNotes.Count > 0;
    public bool HasTerminalFindings   => TerminalFindings.Count > 0;
    public bool HasUnmatchedPins      => UnmatchedSymbolPins.Count > 0 || UnmatchedLayoutPins.Count > 0;

    /// <summary>True when the cell itself says — as opposed to the map having been derived.</summary>
    [ObservableProperty] private bool _terminalsAreDeclared;

    /// <summary>
    /// Writes the map currently shown into the cell's <c>.ccell</c>, turning a derivation into a
    /// declaration. <b>This is the gesture that fixes a <c>derived-by-order</c> warning</b> — the
    /// positional guess was right, and saying so is what stops it being a guess.
    /// </summary>
    [RelayCommand]
    private void DeclareTerminals() => CommitTerminals();

    /// <summary>Removes the block entirely, so the map derives again. Not the same as an empty
    /// list, which says "this cell has no terminals".</summary>
    [RelayCommand]
    private void ClearTerminals()
        => UndoRedo.Execute(new SetCellTerminalsCommand(_editModel, null));

    [RelayCommand]
    private void AddTerminal()
    {
        int next = TerminalRows.Count == 0 ? 1 : TerminalRows.Max(r => r.Port) + 1;
        TerminalRows.Add(new CellTerminalRowViewModel(next, "", [], isDerived: false, this));
        CommitTerminals();
    }

    internal void RemoveTerminalRow(CellTerminalRowViewModel row)
    {
        if (!TerminalRows.Remove(row)) return;
        CommitTerminals();
    }

    /// <summary>
    /// Commits every row as ONE block. Called by the view on LostFocus/Enter and by the commands
    /// above; a no-op when the rows already say exactly what the file says, so simply tabbing through
    /// the section adds nothing to the undo stack.
    /// </summary>
    public void CommitTerminals()
    {
        var rows = TerminalRows.Select(r => r.ToTerminal()).ToList();
        if (_editModel.Terminals is { } current && SameBlock(current, rows)) return;

        UndoRedo.Execute(new SetCellTerminalsCommand(_editModel, rows));
    }

    private static bool SameBlock(IReadOnlyList<CcellTerminal> a, IReadOnlyList<CcellTerminal> b)
        => a.Count == b.Count
           && a.Zip(b).All(p => p.First.Port == p.Second.Port
                                && string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)
                                && p.First.LayoutPin.SequenceEqual(p.Second.LayoutPin, StringComparer.Ordinal));

    /// <summary>
    /// Re-resolves the map and every finding from the cell on disk. Called from
    /// <see cref="RebuildRows"/>, so an undo, a redo and a primary-view change all land here.
    /// </summary>
    private void RebuildTerminals()
    {
        var map = TerminalMap.ResolveCell(_editModel.CellDir);
        bool declared = map.Origin == TerminalMapOrigin.Declared;

        TerminalRows.Clear();
        foreach (var t in map.Terminals)
            TerminalRows.Add(new CellTerminalRowViewModel(t.Port, t.Name, t.LayoutPins, !declared, this));

        TerminalNotes.Clear();
        foreach (var note in map.Notes) TerminalNotes.Add(note);

        TerminalFindings.Clear();
        foreach (var finding in TerminalMap.ValidateCell(_editModel.CellDir))
            TerminalFindings.Add(finding.Render());

        UnmatchedSymbolPins.Clear();
        foreach (var pin in map.UnmatchedSymbolPins) UnmatchedSymbolPins.Add(pin);
        UnmatchedLayoutPins.Clear();
        foreach (var pin in map.UnmatchedLayoutPins) UnmatchedLayoutPins.Add(pin);

        TerminalsAreDeclared = declared;
        TerminalOriginText   = OriginTextOf(map.Origin);

        OnPropertyChanged(nameof(HasTerminals));
        OnPropertyChanged(nameof(HasNoTerminals));
        OnPropertyChanged(nameof(HasTerminalNotes));
        OnPropertyChanged(nameof(HasTerminalFindings));
        OnPropertyChanged(nameof(HasUnmatchedPins));
    }

    private static string OriginTextOf(TerminalMapOrigin origin) => origin switch
    {
        TerminalMapOrigin.Declared    => "Declared by this cell.",
        TerminalMapOrigin.ImportTable => "Derived — this cell was imported, and its two views were numbered together.",
        TerminalMapOrigin.ByName      => "Derived — symbol pin names matched layout pin names.",
        TerminalMapOrigin.ByOrder     => "Derived BY ORDER — nothing is named on either side, so this is a guess. "
                                         + "Use This Map to confirm it, or name the pins.",
        _                             => "No map.",
    };

    // ── Partial callbacks ─────────────────────────────────────────────────────

    partial void OnSelectedPrimarySchematicChanged(string value)
    {
        if (_suppressPrimaryChangeEvents) return;
        if (IsControlResettingItself(value, _editModel.PrimarySchematic, AvailableSchematics)) return;
        var mapped = value == NoneOption ? null : value;
        if (mapped == _editModel.PrimarySchematic) return;
        UndoRedo.Execute(new SetCellPrimaryCommand(_editModel, isSymbol: false, mapped));
    }

    partial void OnSelectedPrimarySymbolChanged(string value)
    {
        if (_suppressPrimaryChangeEvents) return;
        if (IsControlResettingItself(value, _editModel.PrimarySymbol, AvailableSymbols)) return;
        var mapped = value == NoneOption ? null : value;
        if (mapped == _editModel.PrimarySymbol) return;
        UndoRedo.Execute(new SetCellPrimaryCommand(_editModel, isSymbol: true, mapped));
    }

    /// <summary>
    /// True when the incoming value is a ComboBox clearing its own SelectedItem rather than a user
    /// choosing something — which is what a reassigned ItemsSource makes it do.
    ///
    /// <para>The write-back is DEFERRED, so it lands outside
    /// <see cref="_suppressPrimaryChangeEvents"/> and used to be recorded as "set primary to
    /// nothing", wiping the saved primary (the persistence bug).  A null/empty selection is never
    /// something the user can pick — "(none specified)" is the item that means that — so it is
    /// always the control, and the model's own value is restored when the list can still show it.
    /// When it cannot, the combo is left blank rather than looping: re-asserting a value the list
    /// does not contain would only make the control clear itself again.</para>
    /// </summary>
    private bool IsControlResettingItself(string? value, string? modelValue, IReadOnlyList<string> available)
    {
        if (!string.IsNullOrEmpty(value)) return false;

        if (modelValue is not null && available.Contains(modelValue, StringComparer.OrdinalIgnoreCase))
            SyncPrimarySelectionsFromModel();

        return true;
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

        BuildAvailableFileLists();
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

        RebuildTerminals();
        SyncPrimarySelectionsFromModel();
    }

    /// <summary>
    /// Re-reads the cell folder so a view file created while this editor was open becomes
    /// selectable.  Called by the workspace whenever it writes a new <c>.csym</c> / <c>.csch</c>
    /// into this cell — New Symbol is the ordinary case, and before this the drop-down offered only
    /// "(none specified)" until circuitRF was restarted.
    ///
    /// <para>Safe to call at any time: the selections are re-synced from the model afterwards, and
    /// <see cref="IsControlResettingItself"/> absorbs the ComboBox's own deferred clear so a
    /// rebuilt ItemsSource can no longer wipe the saved primary.</para>
    /// </summary>
    public void RefreshAvailableFiles()
    {
        BuildAvailableFileLists();
        SyncPrimarySelectionsFromModel();
    }

    /// <summary>
    /// Builds the Primary Schematic / Symbol combo item lists from the cell folder.
    /// NOT called on parameter edits — only at construction and from
    /// <see cref="RefreshAvailableFiles"/>, because reassigning the ItemsSource makes the ComboBox
    /// transiently null its SelectedItem and that write-back is deferred past the suppression
    /// window.  <see cref="IsControlResettingItself"/> is what makes the reassignment survivable.
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
