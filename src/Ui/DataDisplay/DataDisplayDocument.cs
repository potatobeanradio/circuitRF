using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// Dock Document representing an open Data Display.
/// In 7.1a the body is a placeholder canvas; plot model, persistence, and undo
/// arrive in 7.1b–7.1e.
/// </summary>
public sealed class DataDisplayDocument : Document
{
    private string _baseTitle;

    public DataDisplayDocumentViewModel ViewModel { get; }

    /// <summary>
    /// Absolute on-disk path of the .cdd file, or null for a scratch document.
    /// Set once at materialization (7.1e); null = scratch.
    /// </summary>
    public string? FilePath { get; private set; }

    /// <summary>True when this document has no on-disk path yet.</summary>
    public bool IsScratch => FilePath is null;

    private bool _isDirty;

    /// <summary>
    /// True when the document has unsaved content.
    /// The VM is the source of truth; the document reflects it.
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

    public DataDisplayDocument(string title, DataDisplayDocumentViewModel vm, string? filePath = null)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = vm;

        // VM is the source of truth for IsDirty.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DataDisplayDocumentViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };
    }

    /// <summary>
    /// Transitions this scratch document to materialized (stub — wired for real in 7.1e).
    /// </summary>
    internal void Materialize(string filePath)
    {
        FilePath       = filePath;
        ViewModel.IsDirty = false;
        // IsDirty on the document clears via the PropertyChanged subscription above.
    }
}
