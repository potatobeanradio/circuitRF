using System;
using System.IO;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Dock Document representing an open Layout Editor session.
/// A scratch document has <see cref="FilePath"/> == null: it is in-memory only,
/// starts clean, and is invisible to the project tree until saved.
/// A materialized document has a real on-disk .clay path (set at save time).
/// Clones <c>SymbolEditorDocument</c>'s shape — including undo as of L1b
/// (<see cref="UndoRedo"/> delegates to <see cref="LayoutEditorViewModel.UndoRedo"/>, so the
/// window-level Undo/Redo routing (<c>WorkspaceViewModel.SetActiveUndoTarget</c>) picks this
/// document up automatically via <see cref="IUndoableDocument"/> — no new routing code needed).
/// </summary>
public sealed class LayoutDocument : Document, IUndoableDocument, IActivatableDocument
{
    public UndoRedoStack UndoRedo => ViewModel.UndoRedo;

    // ── Activation focus — view grabs keyboard focus on tab-switch ────────────
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;

    public LayoutEditorViewModel ViewModel { get; }

    // ── Scratch / dirty identity ─────────────────────────────────────────────

    /// <summary>Absolute on-disk path of the .clay file, or null for a scratch document.</summary>
    public string? FilePath { get; private set; }

    /// <summary>True when this document has no on-disk path yet (scratch mode).</summary>
    public bool IsScratch => FilePath is null;

    private bool _isDirty;

    /// <summary>
    /// True when the document has unsaved content.
    /// The VM is the source of truth for dirty state; the document reflects it.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
        }
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <param name="title">Display name / base title for the tab.</param>
    /// <param name="viewModel">The layout editor view model.</param>
    /// <param name="filePath">Absolute path of the .clay file on disk, or null for a scratch document.</param>
    public LayoutDocument(string title, LayoutEditorViewModel viewModel, string? filePath = null)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = viewModel;
        _isDirty   = false;

        // VM is the source of truth for IsDirty; document reflects it so the tab
        // bullet and VM dirty state stay in lock-step without double-tracking.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LayoutEditorViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
            else if (e.PropertyName is nameof(LayoutEditorViewModel.CurrentLayoutPath))
                SyncTitleToPath();
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SyncTitleToPath()
    {
        if (ViewModel.CurrentLayoutPath is not { } path) return;
        FilePath   = path;
        _baseTitle = Path.GetFileName(path);
        Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }

    // ── Materialization ───────────────────────────────────────────────────────

    /// <summary>
    /// Transitions this scratch document to materialized: sets its on-disk path and clears the
    /// dirty flag (on both the document and the VM). Must only be called once per document
    /// (scratch → materialized is one-way).
    /// </summary>
    internal void Materialize(string filePath)
    {
        FilePath                    = filePath;
        ViewModel.CurrentLayoutPath = filePath;
        ViewModel.MarkSaved();   // clean baseline (undo stack + pref edits) -> IsDirty clears via the subscription above
    }

    /// <summary>
    /// Updates path and title after a "Save As" on an already-materialized document.
    /// Unlike <see cref="Materialize"/>, this may be called repeatedly.
    /// </summary>
    internal void OnSavedAs(string filePath, string cellName)
    {
        FilePath                    = filePath;
        ViewModel.CurrentLayoutPath = filePath;
        _baseTitle                  = cellName;
        Id                          = cellName;
        ViewModel.MarkSaved();
        Title                       = _baseTitle; // explicit refresh even if IsDirty didn't change
    }
}
