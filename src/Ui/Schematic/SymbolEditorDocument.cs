using System;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open Symbol Editor session.
/// A scratch document has <see cref="FilePath"/> == null: it is in-memory only,
/// dirty from creation, and invisible to the project tree.
/// A materialized document has a real on-disk path (set at save time).
/// </summary>
public sealed class SymbolEditorDocument : Document, IUndoableDocument, IActivatableDocument
{
    // ── Activation focus — view grabs keyboard focus on tab-switch (Select All etc. without a click) ──
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    // ── Zoom To Fit request — see SchematicDocument.ZoomToFitRequested for the pattern this mirrors.
    public event Action? ZoomToFitRequested;
    public void RequestZoomToFit() => ZoomToFitRequested?.Invoke();

    // ── Canvas interaction — see LayoutDocument.CanvasInteracted, which this mirrors exactly ────────
    // Clicking the project tree can change what the Properties panel shows WITHOUT this document ever
    // leaving the DocumentDock's ActiveDockable slot (the tree is a different dock region), and
    // PropertiesTool.SetActiveFileInfo unconditionally clears the symbol context on its way past. So a
    // click back onto THIS canvas re-fires nothing, the symbol inspector stays detached from its VM,
    // and selecting a pin or a primitive appears to do nothing at all (owner, 2026-08-17: "sometimes
    // the Property Inspector does not update when I click on a Pin"). Raised by the view on canvas
    // GotFocus — a click into the canvas always re-focuses it, because the tree click moved focus away.
    public event Action? CanvasInteracted;
    public void NotifyCanvasInteracted() => CanvasInteracted?.Invoke();

    private string _baseTitle;

    public SymbolEditorViewModel ViewModel { get; }
    public UndoRedoStack         UndoRedo  => ViewModel.UndoRedo;

    // ── Scratch / dirty identity ─────────────────────────────────────────────

    /// <summary>
    /// Absolute on-disk path of the .csym file, or null for a scratch document.
    /// Set once at materialization; null = scratch.
    /// </summary>
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
    /// <param name="viewModel">The symbol editor view model.</param>
    /// <param name="filePath">
    /// Absolute path of the .csym file on disk, or null for a scratch (in-memory) document.
    /// </param>
    public SymbolEditorDocument(string title, SymbolEditorViewModel viewModel, string? filePath = null)
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
            if (e.PropertyName is nameof(SymbolEditorViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
            else if (e.PropertyName is nameof(SymbolEditorViewModel.CurrentSymbolPath))
                SyncTitleToPath();
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SyncTitleToPath()
    {
        if (ViewModel.CurrentSymbolPath is not { } path) return;
        _baseTitle = System.IO.Path.GetFileName(path);
        Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }

    // ── Materialization ──────────────────────────────────────────────────────

    /// <summary>
    /// Transitions this scratch document to materialized: sets its on-disk path and
    /// clears the dirty flag (on both the document and the VM).
    /// Called after the .csym is written to disk.
    /// Must only be called once per document (scratch → materialized is one-way).
    /// </summary>
    internal void Materialize(string filePath)
    {
        FilePath                    = filePath;
        ViewModel.CurrentSymbolPath = filePath;
        ViewModel.UndoRedo.MarkSaved();   // clean baseline → ViewModel.IsModified/IsDirty false
        // IsDirty on the document updates via the PropertyChanged subscription above.
    }

    /// <summary>
    /// Transitions this ALREADY-materialized document's on-disk path to a new location — the
    /// "Save Symbol As…" case on an existing symbol, as opposed to <see cref="Materialize"/>'s
    /// one-way scratch-to-materialized transition. May be called repeatedly. Mirrors
    /// <c>LayoutDocument.OnSavedAs</c>'s exact shape.
    /// </summary>
    internal void OnSavedAs(string filePath, string cellName)
    {
        FilePath                    = filePath;
        ViewModel.CurrentSymbolPath = filePath;   // fires SyncTitleToPath via the ctor subscription
        _baseTitle                  = cellName;   // overrides SyncTitleToPath's file-name guess
        Id                          = cellName;
        ViewModel.UndoRedo.MarkSaved();           // clean baseline → ViewModel.IsModified/IsDirty false
        Title                       = _isDirty ? $"• {_baseTitle}" : _baseTitle; // explicit refresh
    }
}
