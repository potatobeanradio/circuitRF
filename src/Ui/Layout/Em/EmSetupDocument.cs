using System.IO;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>
/// Dock Document for an open <c>.cem</c> EM setup. Mirrors <see cref="TechDocument"/> exactly, for
/// the same reason: R-em-9 — a <c>.cem</c> is workspace-scoped and <b>never scratch</b>. It is a
/// setup for a layout that already exists on disk, so <see cref="FilePath"/> is set once at
/// construction and never becomes null; there is no materialize/offer-a-save-target path to build.
/// </summary>
public sealed class EmSetupDocument : Document, IUndoableDocument, IActivatableDocument, IFileBackedDocument
{
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;

    public EmSetupEditorViewModel ViewModel { get; }
    public UndoRedoStack          UndoRedo  => ViewModel.UndoRedo;

    /// <summary>Absolute on-disk path of the <c>.cem</c>. Never null — see class header.</summary>
    public string FilePath { get; private set; }

    private bool _isDirty;

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

    public EmSetupDocument(string title, EmSetupEditorViewModel viewModel, string filePath)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = viewModel;
        _isDirty   = false;

        ViewModel.EmSetupSavedAs += OnSavedAs;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EmSetupEditorViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };
    }

    /// <summary>Save As landed: follow the new file. Mirrors <c>LayoutDocument.OnSavedAs</c> — unlike
    /// <c>Materialize</c>, this may be called any number of times.</summary>
    private void OnSavedAs(string newPath)
    {
        FilePath   = newPath;
        _baseTitle = Path.GetFileName(newPath);
        Id         = _baseTitle;
        Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }
}
