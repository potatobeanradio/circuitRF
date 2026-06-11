using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open schematic in a Content tab.
/// A scratch document has <see cref="FilePath"/> == null: it is in-memory only,
/// dirty from creation, and invisible to the project tree.
/// A materialized document has a real on-disk path (set at save time, step 2+).
/// </summary>
public sealed class SchematicDocument : Document, IUndoableDocument
{
    private readonly string _baseTitle;

    public SchematicViewModel ViewModel { get; }
    public UndoRedoStack      UndoRedo  => ViewModel.UndoRedo;

    /// <summary>Message sink for posting save/error messages; null if no sink was provided at construction.</summary>
    public IMessageSink? Messages { get; init; }

    /// <summary>Current render snapshot (convenience alias for canvas binding).</summary>
    public SchematicModel? Model => ViewModel.RenderModel;

    // ── Scratch / dirty identity ───────────────────────────────────────────────

    /// <summary>
    /// Absolute on-disk path of the .csch file, or null for a scratch document.
    /// Set once at materialization (step 2); null = scratch.
    /// </summary>
    public string? FilePath { get; private set; }

    /// <summary>True when this document has no on-disk path yet (scratch mode).</summary>
    public bool IsScratch => FilePath is null;

    private bool _isDirty;

    /// <summary>
    /// True when the document has unsaved content.
    /// Scratch documents start dirty and remain dirty in step 1 (no save path yet).
    /// On-disk documents become dirty on the first undoable edit.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            // Reflect dirty state in the tab title with a leading bullet.
            Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="cellName">Display name / base title for the tab.</param>
    /// <param name="viewModel">The schematic view model.</param>
    /// <param name="filePath">
    /// Absolute path of the .csch file on disk, or null for a scratch (in-memory) document.
    /// </param>
    public SchematicDocument(string cellName, SchematicViewModel viewModel, string? filePath = null)
    {
        _baseTitle = cellName;
        Id         = cellName;
        FilePath   = filePath;
        ViewModel  = viewModel;

        // Both scratch and on-disk documents start clean; first undoable edit makes them dirty.
        _isDirty = false;
        Title    = _baseTitle;

        // Any edit on a non-scratch doc makes it dirty (first undo-able action recorded).
        ViewModel.UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo) && ViewModel.UndoRedo.CanUndo)
                IsDirty = true;
        };

        // Keep the Model property change notification alive so bindings update.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SchematicViewModel.RenderModel))
                OnPropertyChanged(nameof(Model));
        };
    }

    // ── Materialization ───────────────────────────────────────────────────────

    /// <summary>
    /// Transitions this scratch document to materialized: sets its on-disk path and
    /// clears the dirty flag. Called by the plan executor (L2) after the .csch is written.
    /// After this call IsScratch is false and the tab title loses its bullet.
    /// Must only be called once per document (from scratch to materialized is one-way).
    /// </summary>
    internal void Materialize(string filePath)
    {
        FilePath = filePath;
        IsDirty  = false;
    }
}
