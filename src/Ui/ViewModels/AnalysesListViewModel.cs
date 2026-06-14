using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands.Analysis;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The single VM for the Analyses list — hosted both in the dock tool and in the
/// "Setup Analyses…" modal (one VM, two hosts).
///
/// Operates on the active schematic's <see cref="SchematicEditModel.Analyses"/> list via the
/// schematic's undo stack so mutations mark the document dirty and are undoable.
/// </summary>
public sealed partial class AnalysesListViewModel : ObservableObject
{
    private SchematicViewModel? _schematicVm;

    // ── List state ────────────────────────────────────────────────────────────

    public ObservableCollection<AnalysisRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private AnalysisRowViewModel? _selectedRow;

    // Full selection list for multi-select copy — maintained by code-behind SelectionChanged.
    private IReadOnlyList<AnalysisRowViewModel> _selectedRows = [];

    // Current workspace dir — set by WorkspaceViewModel via AnalysesTool.SetWorkspaceDir.
    private string? _workspaceDir;

    /// <summary>True when no schematic is active (neutral "open a schematic" state).</summary>
    [ObservableProperty] private bool _noActiveSchematic = true;

    /// <summary>Header text — filename of the active schematic, or "Analyses" when none/unsaved.</summary>
    [ObservableProperty] private string _headerLabel = "Analyses";

    /// <summary>True when a schematic is active but has no analyses (HIG empty state).</summary>
    public bool IsEmpty => !NoActiveSchematic && Rows.Count == 0;

    // ── Active-schematic binding ──────────────────────────────────────────────

    /// <summary>Called by the dock tool (and modal host) when the active schematic changes.</summary>
    public void SetActiveSchematic(SchematicViewModel? vm, string? schematicName = null)
    {
        if (_schematicVm is not null)
            _schematicVm.EditModel.Changed -= OnModelChanged;

        _schematicVm      = vm;
        NoActiveSchematic = vm is null;
        HeaderLabel       = string.IsNullOrEmpty(schematicName) ? "Analyses" : schematicName;

        if (vm is not null)
            vm.EditModel.Changed += OnModelChanged;

        RebuildRows();
    }

    private void OnModelChanged(object? sender, EventArgs e) => RebuildRows();

    private void RebuildRows()
    {
        SelectedRow = null;
        Rows.Clear();
        if (_schematicVm is null) return;

        foreach (var a in _schematicVm.EditModel.Analyses)
            Rows.Add(new AnalysisRowViewModel(a, _schematicVm));

        OnPropertyChanged(nameof(IsEmpty));
        RefreshCommandStates();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasActiveSchematic))]
    private async Task Add(Window? owner)
    {
        if (_schematicVm is null) return;
        var vm     = new AnalysisEditorViewModel(_schematicVm.EditModel);
        var result = await AnalysisEditorDialog.ShowAsync(owner, vm, isEdit: false);
        if (result is null) return;
        _schematicVm.Execute(new AddAnalysisCommand(_schematicVm.EditModel, result));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit(Window? owner)
    {
        if (SelectedRow is null || _schematicVm is null) return;
        var vm     = new AnalysisEditorViewModel(_schematicVm.EditModel, SelectedRow.Analysis);
        var result = await AnalysisEditorDialog.ShowAsync(owner, vm, isEdit: true);
        if (result is null) return;
        _schematicVm.Execute(new EditAnalysisCommand(_schematicVm.EditModel, SelectedRow.Analysis, result));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedRow is null || _schematicVm is null) return;
        _schematicVm.Execute(new RemoveAnalysisCommand(_schematicVm.EditModel, SelectedRow.Analysis));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        if (SelectedRow is null || _schematicVm is null) return;
        _schematicVm.Execute(new DuplicateAnalysisCommand(_schematicVm.EditModel, SelectedRow.Analysis));
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedRow is null || _schematicVm is null) return;
        _schematicVm.Execute(new MoveAnalysisCommand(_schematicVm.EditModel, SelectedRow.Analysis, moveUp: true));
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedRow is null || _schematicVm is null) return;
        _schematicVm.Execute(new MoveAnalysisCommand(_schematicVm.EditModel, SelectedRow.Analysis, moveUp: false));
    }

    // ── Copy / Paste (clipboard, §5.2) ───────────────────────────────────────

    /// <summary>Copies selected analyses to the clipboard via the step-2 shared serializer.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task Copy(Window? window)
    {
        if (_schematicVm is null) return;
        // Use all selected rows; fall back to SelectedRow for single-select.
        IReadOnlyList<Analysis> toCopy;
        if (_selectedRows.Count > 0)
            toCopy = _selectedRows.Select(r => r.Analysis).ToList();
        else if (SelectedRow is not null)
            toCopy = [SelectedRow.Analysis];
        else
            return;
        await CopyToClipboard(window, toCopy);
    }

    /// <summary>Copies all schematic analyses to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveSchematic))]
    private async Task CopyAll(Window? window)
    {
        if (_schematicVm is null) return;
        await CopyToClipboard(window, _schematicVm.EditModel.Analyses);
    }

    /// <summary>
    /// Pastes analyses from the clipboard, appending with name-collision resolution.
    /// Non-analysis clipboard content is a safe no-op.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasActiveSchematic))]
    private async Task Paste(Window? window)
    {
        if (_schematicVm is null) return;
        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null) return;

        string? json = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(json)) return;

        List<Analysis> toPaste;
        try
        {
            (toPaste, _) = AnalysisSerialization.Deserialize(json);
        }
        catch
        {
            return; // non-analysis clipboard — safe no-op
        }

        if (toPaste.Count == 0) return;
        _schematicVm.Execute(new PasteAnalysesCommand(_schematicVm.EditModel, toPaste));
    }

    // ── Templates (§5.3) ─────────────────────────────────────────────────────

    /// <summary>
    /// Saves selected analyses (or all when nothing is selected) as a named <c>.canl</c> template.
    /// Writes to the workspace templates dir when a workspace is open, otherwise to the user templates dir.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasActiveSchematic))]
    private async Task SaveAsTemplate(Window? window)
    {
        if (_schematicVm is null) return;

        IReadOnlyList<Analysis> toSave = _selectedRows.Count > 0
            ? _selectedRows.Select(r => r.Analysis).ToList()
            : _schematicVm.EditModel.Analyses;

        if (toSave.Count == 0) return;

        var result = await SaveAsTemplateDialog.ShowAsync(window, toSave);
        if (result is null) return;

        var targetDir = (_workspaceDir is not null
            ? TemplateManager.WorkspaceTemplatesDir(_workspaceDir)
            : null) ?? TemplateManager.UserTemplatesDir;

        // Collision guard
        if (window is not null && TemplateManager.TemplateExists(targetDir, result.Name))
        {
            var confirm = new SaveChangesDialog(
                $"A template named \"{result.Name}\" already exists. Overwrite it?",
                saveLabel:     "Overwrite",
                dontSaveLabel: null,
                cancelLabel:   "Cancel");
            var cr = await confirm.ShowDialog<SaveChangesResult>(window);
            if (cr != SaveChangesResult.Save) return;
        }

        try
        {
            var path = TemplateManager.SaveTemplate(targetDir, result.Name, result.Description, toSave, []);
            _schematicVm.MessageSink?.Success(path, path);
        }
        catch (Exception ex)
        {
            _schematicVm.MessageSink?.Error($"Failed to save template: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the Insert-from-Template picker; appending the selected bundle (collision-resolved)
    /// as one undoable action.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasActiveSchematic))]
    private async Task InsertFromTemplate(Window? window)
    {
        if (_schematicVm is null) return;
        var template = await InsertFromTemplateDialog.ShowAsync(window, _workspaceDir);
        if (template is null) return;
        _schematicVm.Execute(new PasteAnalysesCommand(_schematicVm.EditModel, template.Analyses));
    }

    // ── Multi-select support (updated by code-behind SelectionChanged) ────────

    /// <summary>Called by AnalysesListView code-behind when ListBox selection changes.</summary>
    internal void UpdateSelection(IReadOnlyList<AnalysisRowViewModel> rows)
    {
        _selectedRows = rows;
        CopyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called by WorkspaceViewModel (via AnalysesTool) when the open workspace changes.</summary>
    public void SetWorkspaceDir(string? workspaceDir) => _workspaceDir = workspaceDir;

    // ── CanExecute guards ─────────────────────────────────────────────────────

    private bool HasActiveSchematic() => _schematicVm is not null;
    private bool HasSelection()       => SelectedRow is not null && _schematicVm is not null;
    private bool CanCopy()            => _schematicVm is not null && (_selectedRows.Count > 0 || SelectedRow is not null);

    private bool CanMoveUp() =>
        SelectedRow is not null && _schematicVm is not null
        && _schematicVm.EditModel.Analyses.IndexOf(SelectedRow.Analysis) > 0;

    private bool CanMoveDown() =>
        SelectedRow is not null && _schematicVm is not null
        && _schematicVm.EditModel.Analyses.IndexOf(SelectedRow.Analysis)
            < _schematicVm.EditModel.Analyses.Count - 1;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshCommandStates()
    {
        AddCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CopyAllCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        SaveAsTemplateCommand.NotifyCanExecuteChanged();
        InsertFromTemplateCommand.NotifyCanExecuteChanged();
    }

    private static async Task CopyToClipboard(Window? window, IReadOnlyList<Analysis> analyses)
    {
        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null) return;
        string json = AnalysisSerialization.Serialize(analyses, []);
        await clipboard.SetTextAsync(json);
    }
}
