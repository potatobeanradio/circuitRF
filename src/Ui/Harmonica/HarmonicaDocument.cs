using System;
using System.IO;
using CircuitRF.Ui.Commands;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.Harmonica;

/// <summary>Document-shell view model for one harmonicaRF tab. Wraps the real
/// <see cref="HarmonicaViewModel"/> rather than merging with it, exactly as
/// <c>DataDisplayDocumentViewModel</c> wraps <c>DisplayWindowViewModel</c>.</summary>
public sealed partial class HarmonicaDocumentViewModel : ObservableObject
{
    [ObservableProperty] private bool _isDirty;

    public HarmonicaViewModel Harmonica { get; }

    public HarmonicaDocumentViewModel(HarmonicaViewModel? vm = null)
    {
        Harmonica = vm ?? new HarmonicaViewModel();
        Harmonica.DirtyChanged += () => IsDirty = true;
    }

    /// <summary>Clears the dirty flag after a successful save.</summary>
    public void MarkSaved() => IsDirty = false;
}

/// <summary>
/// Dock Document for an open harmonicaRF instrument.
///
/// <para>Mirrors <c>DataDisplayDocument</c>'s shape exactly — scratch vs. materialized keyed on
/// <see cref="FilePath"/>, dirty mirrored FROM the VM (the VM is the source of truth; the document
/// reflects it, never the reverse), a bullet in the tab title while dirty, and
/// <see cref="IActivatableDocument"/> so the view takes keyboard focus on tab activation without a
/// click.</para>
/// </summary>
public sealed class HarmonicaDocument : Document, IActivatableDocument
{
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;
    private bool   _isDirty;

    public HarmonicaDocumentViewModel ViewModel { get; }

    /// <summary>Absolute path of the <c>.charm</c>, or null for a scratch document.</summary>
    public string? FilePath { get; private set; }

    public bool IsScratch => FilePath is null;

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

    public HarmonicaDocument(string title, HarmonicaDocumentViewModel vm, string? filePath = null)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = vm;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HarmonicaDocumentViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };
    }

    /// <summary>Scratch → materialized, or a Save-As onto a new path. Safe to call repeatedly.</summary>
    internal void OnSavedToPath(string path)
    {
        FilePath   = path;
        _baseTitle = Path.GetFileNameWithoutExtension(path);
        Id         = path;
        ViewModel.MarkSaved();
        Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }
}
