using System;
using System.IO;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// Dock Document representing an open Data Display.
/// </summary>
public sealed class DataDisplayDocument : Document, IActivatableDocument, IFileBackedDocument
{
    // ── Activation focus — view grabs keyboard focus on tab-switch (Select All etc. without a click) ──
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;

    public DataDisplayDocumentViewModel ViewModel { get; }

    /// <summary>
    /// Absolute on-disk path of the .cdd file, or null for a scratch document.
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

        // Update tab title and identity when the display is saved to a path.
        vm.Window.ConfigPathSaved += OnSavedToPath;
    }

    /// <summary>
    /// Called when DisplayWindowViewModel.SaveAllAsync completes successfully.
    /// Updates FilePath, _baseTitle, Id, and Title so the dock tab reflects the real file name.
    /// Also handles the scratch→materialized transition (clears dirty via the VM).
    /// </summary>
    internal void OnSavedToPath(string path)
    {
        FilePath   = path;
        _baseTitle = Path.GetFileNameWithoutExtension(path);
        Id         = path;
        Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }

    /// <summary>
    /// Transitions this document to materialized.
    /// Clears the dirty flag; tab title is already updated via ConfigPathSaved / OnSavedToPath.
    /// </summary>
    internal void Materialize(string filePath)
    {
        FilePath          = filePath;
        ViewModel.IsDirty = false;
        // IsDirty on the document clears via the PropertyChanged subscription above.
    }
}
