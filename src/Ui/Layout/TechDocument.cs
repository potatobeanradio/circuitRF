using System;
using System.IO;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Dock Document representing an open .ctech editor session. Unlike <see cref="LayoutDocument"/>
/// or <c>SymbolEditorDocument</c>, a .ctech is <b>never scratch</b> — it is workspace-scoped
/// configuration, backed by a file from the moment it exists (§5 of the L0d brief), so
/// <see cref="FilePath"/> is set once at construction and never becomes null. Implements
/// <see cref="IUndoableDocument"/> (mirrors <c>SymbolEditorDocument</c>) so Ctrl/Cmd+Z routes to
/// this document's own <see cref="TechEditorViewModel.UndoRedo"/> while it is active.
/// </summary>
public sealed class TechDocument : Document, IUndoableDocument, IActivatableDocument, IFileBackedDocument
{
    // ── Activation focus — view grabs keyboard focus on tab-switch ────────────
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;

    public TechEditorViewModel ViewModel { get; }
    public UndoRedoStack       UndoRedo  => ViewModel.UndoRedo;

    /// <summary>Absolute on-disk path of the .ctech file. Never null — see class header.</summary>
    public string FilePath { get; }

    private bool _isDirty;

    /// <summary>True when the document has unsaved content. The VM is the source of truth;
    /// the document reflects it via <see cref="TechEditorViewModel.IsDirty"/>.</summary>
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

    public TechDocument(string title, TechEditorViewModel viewModel, string filePath)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = viewModel;
        _isDirty   = false;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TechEditorViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };
    }
}
