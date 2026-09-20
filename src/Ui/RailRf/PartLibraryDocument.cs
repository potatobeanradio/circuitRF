using System;
using System.IO;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// Dock Document for an open <c>.crlib</c> part library
/// (docs/sonnet-briefs/brief-railrf-24-part-library-editor.md R-rail24-1a).
///
/// <para><b>Mirrors <c>EmSetupDocument</c> exactly, and that is the whole point of the brief.</b> A
/// part library is a file that exists on disk before it is a document — there is no scratch state to
/// build — so routing it through the workspace's own open-or-activate path gives it one session per
/// path, a dirty mark, Save, Save As, undo/redo and revision control BY CONSTRUCTION, rather than
/// four partial reimplementations of them.</para>
/// </summary>
public sealed class PartLibraryDocument : Document, IUndoableDocument, IActivatableDocument, IFileBackedDocument
{
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;

    public PartLibraryEditorViewModel ViewModel { get; }
    public UndoRedoStack              UndoRedo  => ViewModel.UndoRedo;

    /// <summary>Absolute on-disk path of the <c>.crlib</c>. Never null — see class header.</summary>
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

    public PartLibraryDocument(string title, PartLibraryEditorViewModel viewModel, string filePath)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = viewModel;
        _isDirty   = false;

        ViewModel.PartLibrarySavedAs += OnSavedAs;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PartLibraryEditorViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };
    }

    /// <summary>Save As landed: follow the new file. Mirrors <c>EmSetupDocument.OnSavedAs</c> — unlike
    /// a materialize, this may be called any number of times.</summary>
    private void OnSavedAs(string newPath)
    {
        FilePath   = newPath;
        _baseTitle = Path.GetFileName(newPath);
        Id         = _baseTitle;
        Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }
}
