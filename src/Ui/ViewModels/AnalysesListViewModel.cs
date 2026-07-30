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
using CircuitRF.Ui.Commands.Schematic;
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

    // ── Results file override (R-res-2/3) ─────────────────────────────────────
    //
    // One file per RUN (not per analysis), so this is a single schematic-level setting shown once
    // near the analyses list — never a field on each analysis card, which would wrongly imply a
    // per-analysis file. Blank means the §1 default (<schematicKey>.npy).

    /// <summary>Staged text for the "Results file" field. Committed via CommitResultsFileName
    /// (view code-behind LostFocus/Enter), mirroring every other staged text field in this codebase.</summary>
    [ObservableProperty] private string _resultsFileNameText = "";

    /// <summary>Set by the view while the results-file TextBox has focus, so an unrelated model
    /// change (e.g. adding an analysis) never clobbers text the user is mid-typing.</summary>
    public bool ResultsFileNameFocused { get; set; }

    private void RefreshResultsFileNameText()
        => ResultsFileNameText = _schematicVm?.EditModel.ResultsFileName ?? "";

    /// <summary>Commits the results-file field: sanitizes (strips path separators and any other
    /// character the filesystem disallows in a plain file name — R-res-2's "reject or sanitize path
    /// separators"), appends ".npy" when absent so the text box always shows the EXACT file name a run
    /// writes (never just the stem the user happened to type), then pushes an undoable
    /// SetResultsFileNameCommand only when the resulting value actually differs from what's stored.
    /// Blank commits null (→ the default).</summary>
    public void CommitResultsFileName(string text)
    {
        if (_schematicVm is null) return;
        var sanitized = RunResultsWriter.SanitizeFileNameComponent(text);
        if (sanitized.Length > 0 && !sanitized.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
            sanitized += ".npy";
        var newValue  = sanitized.Length == 0 ? null : sanitized;
        var current   = _schematicVm.EditModel.ResultsFileName;
        if (!string.Equals(newValue, current, StringComparison.Ordinal))
            _schematicVm.Execute(new SetResultsFileNameCommand(_schematicVm.EditModel, newValue));
        ResultsFileNameText = sanitized;   // reflect the sanitized+extended form even on a same-value commit
    }

    // ── Run event (panel → WorkspaceViewModel) ────────────────────────────────

    /// <summary>Raised when the Run button is pressed; WorkspaceViewModel runs the retained schematic.</summary>
    public event Action? RunRequested;

    [RelayCommand(CanExecute = nameof(HasActiveSchematic))]
    private void Run() => RunRequested?.Invoke();

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

        RefreshResultsFileNameText();
        RebuildRows();
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        RebuildRows();
        // Never clobber text the user is mid-typing (mirrors the Layout editor's staged-field
        // focus guard) — an unrelated edit (add/remove analysis, undo elsewhere) still refreshes it.
        if (!ResultsFileNameFocused)
            RefreshResultsFileNameText();
    }

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
        var vm     = new AnalysisEditorViewModel(_schematicVm.EditModel,
                         workspaceRoot: _schematicVm.WorkspaceRoot);
        var result = await AnalysisEditorDialog.ShowAsync(owner, vm, isEdit: false);
        if (result is null) return;
        _schematicVm.Execute(new AddAnalysesCommand(_schematicVm.EditModel, result));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit(Window? owner)
    {
        if (SelectedRow is null || _schematicVm is null) return;
        var vm = new AnalysisEditorViewModel(_schematicVm.EditModel, SelectedRow.Analysis,
                     workspaceRoot: _schematicVm.WorkspaceRoot);

        // Collect the old chain before opening the dialog.
        var oldChainNames = vm.EditingChainNames;
        var oldChain = _schematicVm.EditModel.Analyses
            .Where(a => oldChainNames.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var result = await AnalysisEditorDialog.ShowAsync(owner, vm, isEdit: true);
        if (result is null) return;
        _schematicVm.Execute(new EditAnalysisChainCommand(_schematicVm.EditModel, oldChain, result));
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
    private void MoveUp() => MoveSelected(up: true);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => MoveSelected(up: false);

    // Moves the selected card (sweep → reorder within chain; base → move whole chain), then
    // re-selects it. Execute fires EditModel.Changed → RebuildRows, which nulls SelectedRow and
    // replaces every row instance, so we re-find the moved card by name to keep its highlight.
    private void MoveSelected(bool up)
    {
        if (SelectedRow is null || _schematicVm is null) return;
        var    moved     = SelectedRow.Analysis;
        string movedName = moved.Name;

        if (moved is ParametricSweepAnalysis psa)
            _schematicVm.Execute(new ReorderSweepInChainCommand(_schematicVm.EditModel, psa, moveInner: up));
        else
            _schematicVm.Execute(new MoveAnalysisChainCommand(_schematicVm.EditModel, moved, moveUp: up));

        SelectedRow = Rows.FirstOrDefault(r =>
            string.Equals(r.Analysis.Name, movedName, StringComparison.OrdinalIgnoreCase));
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
        await CopyToClipboard(window, ExpandSelectionToChains(toCopy));
    }

    /// <summary>Expands a selection so any selected base analysis also carries the parametric sweeps
    /// that (transitively) wrap it. Result is ordered by position in the model so chains stay contiguous
    /// (base first, then its sweeps inner→outer). Selected sweeps with no selected base come along alone.</summary>
    internal IReadOnlyList<Analysis> ExpandSelectionToChains(IEnumerable<Analysis> selected)
    {
        if (_schematicVm is null) return selected.ToList();
        var all  = _schematicVm.EditModel.Analyses;
        var keep = new HashSet<Analysis>(selected);

        // Map inner name → its wrapping sweep (follow InnerAnalysisName forward).
        var sweepsByInner = all.OfType<ParametricSweepAnalysis>()
            .ToLookup(p => p.InnerAnalysisName, StringComparer.OrdinalIgnoreCase);

        foreach (var a in keep.ToList())
            if (a is not ParametricSweepAnalysis)   // a base — pull its chain outward
            {
                var cursor = a.Name;
                while (sweepsByInner[cursor].FirstOrDefault() is { } sw)
                { keep.Add(sw); cursor = sw.Name; }
            }

        return all.Where(keep.Contains).ToList();   // model order → contiguous chains
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
        _schematicVm.Execute(new PasteAnalysesCommand(
            _schematicVm.EditModel, toPaste, retargetInner: SelectedRow?.Analysis.Name));
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
                cancelLabel:   "Cancel",
                title:         "Overwrite Template");
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

    private bool CanMoveUp()
    {
        if (SelectedRow is null || _schematicVm is null) return false;
        var list = _schematicVm.EditModel.Analyses;
        int idx = list.IndexOf(SelectedRow.Analysis);
        if (idx < 0) return false;

        if (SelectedRow.Analysis is ParametricSweepAnalysis)
        {
            // Not at the innermost slot ⇒ can move inward (the slot above it is also a sweep).
            return idx > 0 && list[idx - 1] is ParametricSweepAnalysis;
        }
        // Base: a chain block exists above.
        int b = idx;
        while (b > 0 && list[b] is ParametricSweepAnalysis) b--;
        return b > 0;
    }

    private bool CanMoveDown()
    {
        if (SelectedRow is null || _schematicVm is null) return false;
        var list = _schematicVm.EditModel.Analyses;
        int idx = list.IndexOf(SelectedRow.Analysis);
        if (idx < 0) return false;

        if (SelectedRow.Analysis is ParametricSweepAnalysis)
        {
            // Not at the outermost slot ⇒ can move outward (the slot below it is also a sweep).
            return idx + 1 < list.Count && list[idx + 1] is ParametricSweepAnalysis;
        }
        // Base: compute this chain's end; a block exists below.
        int b = idx;
        while (b > 0 && list[b] is ParametricSweepAnalysis) b--;
        int end = b;
        while (end + 1 < list.Count && list[end + 1] is ParametricSweepAnalysis) end++;
        return end + 1 < list.Count;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshCommandStates()
    {
        RunCommand.NotifyCanExecuteChanged();
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
